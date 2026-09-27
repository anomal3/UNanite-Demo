using System.Collections.Generic;

namespace UNanite
{
    /// <summary>
    /// M12: CPU residency of runtime-virtual-texture tiles (no GPU; unit-tested). Atlas slots hold tiles
    /// of any terrain; feedback reports the tiles the resolve wanted; <see cref="Update"/> returns the
    /// tiles to bake this frame (coarse mips first, bounded per frame) with their slot, evicting the
    /// least recently used tiles that were not wanted for <see cref="MinAge"/> frames. Pinned tiles
    /// (every terrain's top mips) are never evicted. Page-table writes (index, value) are collected in
    /// <see cref="PageWrites"/> for the GPU scatter.
    /// </summary>
    public sealed class VgRvtResidency
    {
        /// <summary>Tile key: terrain &lt;&lt; 25 | mip &lt;&lt; 20 | ty &lt;&lt; 10 | tx (VgRvt.compute).</summary>
        public static uint Key(int terrain, int mip, int tx, int ty) => (uint)terrain << 25 | (uint)mip << 20 | (uint)ty << 10 | (uint)tx;
        public static int KeyTerrain(uint key) => (int)(key >> 25);
        public static int KeyMip(uint key) => (int)((key >> 20) & 31u);
        public static int KeyX(uint key) => (int)(key & 1023u);
        public static int KeyY(uint key) => (int)((key >> 10) & 1023u);

        /// <summary>First page-table entry of mip `mip` for a terrain with 2^L tiles per side at mip 0 (VgRvt.hlsl VgRvtMipOffset).</summary>
        public static int MipOffset(int L, int mip)
        {
            long all = 1L << (2 * (L + 1)), rest = 1L << (2 * (L + 1 - mip));
            return (int)((all - rest) / 3);
        }

        /// <summary>Page-table entries of a terrain with 2^L tiles per side at mip 0 (all mips).</summary>
        public static int PageCount(int L) => MipOffset(L, L + 1);

        public struct Terrain
        {
            public bool live;
            public int L;          // log2(tiles per side) at mip 0 = top mip
            public int pageBase;   // first page-table entry
        }

        public struct Bake
        {
            public uint key;
            public int slot;
        }

        readonly uint[] m_SlotKey;
        readonly int[] m_SlotUsed;       // frame the tile was last wanted
        readonly bool[] m_SlotPinned;
        readonly bool[] m_SlotLive;
        readonly Stack<int> m_Free = new Stack<int>();
        readonly Dictionary<uint, int> m_Resident = new Dictionary<uint, int>();
        readonly Dictionary<uint, int> m_Requests = new Dictionary<uint, int>(); // key -> frame of the last report
        readonly HashSet<uint> m_Pinned = new HashSet<uint>();
        readonly List<Terrain> m_Terrains = new List<Terrain>();
        readonly List<uint> m_Sorted = new List<uint>();
        readonly List<Bake> m_Bakes = new List<Bake>();

        /// <summary>Frames a tile must go unreported before its slot may be reused.</summary>
        public int MinAge = 30;
        /// <summary>Page-table writes (entry index, atlas slot + 1 or 0) since the last <see cref="Update"/>.</summary>
        public readonly List<(int index, uint value)> PageWrites = new List<(int, uint)>();

        public int Capacity => m_SlotKey.Length;
        public int ResidentCount => m_Resident.Count;
        public int PendingCount => m_Requests.Count;
        public int EvictionsLastUpdate { get; private set; }

        public VgRvtResidency(int slots)
        {
            m_SlotKey = new uint[slots];
            m_SlotUsed = new int[slots];
            m_SlotPinned = new bool[slots];
            m_SlotLive = new bool[slots];
            for (int s = slots - 1; s >= 0; --s)
                m_Free.Push(s);
        }

        public IReadOnlyList<Terrain> Terrains => m_Terrains;

        /// <summary>Registers a terrain at page-table base `pageBase` with 2^L tiles per side at mip 0; its top `pinnedMips` mips are requested and pinned.</summary>
        public int AddTerrain(int L, int pageBase, int pinnedMips)
        {
            int index = m_Terrains.FindIndex(t => !t.live);
            var terrain = new Terrain { live = true, L = L, pageBase = pageBase };
            if (index < 0)
            {
                index = m_Terrains.Count;
                m_Terrains.Add(terrain);
            }
            else
                m_Terrains[index] = terrain;
            for (int mip = L; mip >= 0 && mip > L - pinnedMips; --mip)
            {
                int side = 1 << (L - mip);
                for (int y = 0; y < side; ++y)
                    for (int x = 0; x < side; ++x)
                    {
                        uint key = Key(index, mip, x, y);
                        m_Pinned.Add(key);
                        m_Requests[key] = int.MaxValue; // before everything else
                    }
            }
            return index;
        }

        /// <summary>Drops a terrain: its slots are freed and its page-table entries cleared.</summary>
        public void RemoveTerrain(int terrain)
        {
            if (terrain < 0 || terrain >= m_Terrains.Count || !m_Terrains[terrain].live)
                return;
            for (int s = 0; s < m_SlotKey.Length; ++s)
                if (m_SlotLive[s] && KeyTerrain(m_SlotKey[s]) == terrain)
                    FreeSlot(s, true);
            m_Requests.Keys.Where(k => KeyTerrain(k) == terrain, m_Sorted);
            foreach (var k in m_Sorted)
                m_Requests.Remove(k);
            m_Pinned.RemoveWhere(k => KeyTerrain(k) == terrain);
            var t = m_Terrains[terrain];
            t.live = false;
            m_Terrains[terrain] = t;
        }

        /// <summary>Re-bakes the resident tiles of `terrain` overlapping the UV rectangle (after edits of its layers or heights).</summary>
        public void Invalidate(int terrain, float uMin, float vMin, float uMax, float vMax)
        {
            for (int s = 0; s < m_SlotKey.Length; ++s)
            {
                if (!m_SlotLive[s] || KeyTerrain(m_SlotKey[s]) != terrain)
                    continue;
                uint key = m_SlotKey[s];
                int mip = KeyMip(key), L = m_Terrains[terrain].L;
                float size = 1f / (1 << (L - mip));
                float u0 = KeyX(key) * size, v0 = KeyY(key) * size;
                if (u0 > uMax || v0 > vMax || u0 + size < uMin || v0 + size < vMin)
                    continue;
                bool pinned = m_SlotPinned[s];
                FreeSlot(s, true);
                m_Requests[key] = pinned ? int.MaxValue : m_SlotUsed[s];
            }
        }

        /// <summary>Feedback of one frame: resident tiles are refreshed, missing ones requested.</summary>
        public void Report(uint key, int frame)
        {
            int terrain = KeyTerrain(key);
            if (terrain >= m_Terrains.Count || !m_Terrains[terrain].live || KeyMip(key) > m_Terrains[terrain].L)
                return; // stale (terrain removed / replaced since the frame was rendered)
            if (m_Resident.TryGetValue(key, out int slot))
                m_SlotUsed[slot] = frame;
            else if (!m_Requests.TryGetValue(key, out int last) || last < frame)
                m_Requests[key] = frame;
        }

        /// <summary>Chooses up to `budget` tiles to bake this frame (pinned ones regardless of the budget).</summary>
        public List<Bake> Update(int frame, int budget)
        {
            m_Bakes.Clear();
            EvictionsLastUpdate = 0;
            if (m_Requests.Count == 0)
                return m_Bakes;
            // requests older than MinAge were not confirmed by later feedback: dropped
            m_Sorted.Clear();
            foreach (var kv in m_Requests)
                if (kv.Value != int.MaxValue && frame - kv.Value > MinAge)
                    m_Sorted.Add(kv.Key);
            foreach (var k in m_Sorted)
                m_Requests.Remove(k);

            m_Sorted.Clear();
            m_Sorted.AddRange(m_Requests.Keys);
            // pinned first, then coarse mips first (a coarse tile serves a large area), then the most recent
            m_Sorted.Sort((a, b) =>
            {
                bool pa = m_Pinned.Contains(a), pb = m_Pinned.Contains(b);
                if (pa != pb)
                    return pa ? -1 : 1;
                int c = KeyMip(b).CompareTo(KeyMip(a));
                return c != 0 ? c : m_Requests[b].CompareTo(m_Requests[a]);
            });
            foreach (var key in m_Sorted)
            {
                bool pinned = m_Pinned.Contains(key);
                if (!pinned && m_Bakes.Count >= budget)
                    break;
                int slot = AllocateSlot(frame);
                if (slot < 0)
                    break; // atlas full of recently used tiles
                m_Requests.Remove(key);
                m_SlotKey[slot] = key;
                m_SlotUsed[slot] = frame;
                m_SlotPinned[slot] = pinned;
                m_SlotLive[slot] = true;
                m_Resident[key] = slot;
                m_Bakes.Add(new Bake { key = key, slot = slot });
                PageWrites.Add((PageIndex(key), (uint)slot + 1u));
            }
            return m_Bakes;
        }

        /// <summary>Page-table index of a tile key.</summary>
        public int PageIndex(uint key)
        {
            var t = m_Terrains[KeyTerrain(key)];
            int mip = KeyMip(key);
            return t.pageBase + MipOffset(t.L, mip) + KeyY(key) * (1 << (t.L - mip)) + KeyX(key);
        }

        public bool IsResident(uint key) => m_Resident.ContainsKey(key);

        int AllocateSlot(int frame)
        {
            if (m_Free.Count > 0)
                return m_Free.Pop();
            int best = -1, bestUsed = int.MaxValue;
            for (int s = 0; s < m_SlotKey.Length; ++s)
                if (m_SlotLive[s] && !m_SlotPinned[s] && m_SlotUsed[s] < bestUsed)
                {
                    best = s;
                    bestUsed = m_SlotUsed[s];
                }
            if (best < 0 || frame - bestUsed <= MinAge)
                return -1;
            FreeSlot(best, true);
            EvictionsLastUpdate++;
            return m_Free.Pop();
        }

        void FreeSlot(int slot, bool clearPage)
        {
            uint key = m_SlotKey[slot];
            m_Resident.Remove(key);
            if (clearPage)
                PageWrites.Add((PageIndex(key), 0u));
            m_SlotLive[slot] = false;
            m_SlotPinned[slot] = false;
            m_Free.Push(slot);
        }
    }

    static class VgRvtCollectionExtensions
    {
        public static void Where(this Dictionary<uint, int>.KeyCollection keys, System.Func<uint, bool> predicate, List<uint> into)
        {
            into.Clear();
            foreach (var k in keys)
                if (predicate(k))
                    into.Add(k);
        }
    }
}
