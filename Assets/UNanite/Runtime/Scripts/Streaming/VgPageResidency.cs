using System;
using System.Collections.Generic;

namespace UNanite
{
    /// <summary>
    /// CPU residency bookkeeping of streamable pages (M7), independent of the GPU so it can be unit
    /// tested. Pages are global indices over every registered mesh; dependencies point to the pages
    /// holding the coarser parents (they must be resident first, see DataFormat.md).
    ///
    /// Invariants kept by every operation:
    ///   * a page is Resident only if all its dependencies are Resident or Pinned (upward closure,
    ///     so every LOD cut stays crack-free);
    ///   * a page is Loading only if all its dependencies are Resident, Pinned or Loading, and it is
    ///     committed only once they are Resident or Pinned;
    ///   * a page with Resident or Loading dependents is never evicted;
    ///   * Pinned (root) pages are never evicted and never occupy a slot.
    /// Feedback marks resident pages as used (LRU) and turns missing ones into requests, prioritised by
    /// their projected error; parents inherit the priority of their children.
    ///
    /// M8: page ranges can be added and removed while everything else stays resident (meshes
    /// registered or replaced at runtime); pages outside live ranges ignore feedback.
    /// </summary>
    public sealed class VgPageResidency
    {
        public enum State : byte { Absent, Loading, Resident, Pinned }

        static readonly int[] s_NoDeps = Array.Empty<int>();

        State[] m_State = Array.Empty<State>();
        bool[] m_Live = Array.Empty<bool>();
        int[] m_Slot = Array.Empty<int>();
        int[][] m_Deps = Array.Empty<int[]>();
        int[] m_Dependents = Array.Empty<int>(); // resident + loading pages depending on this one
        uint[] m_LastUsed = Array.Empty<uint>();
        float[] m_LastPriority = Array.Empty<float>();
        int[] m_SlotPage = Array.Empty<int>();
        readonly Stack<int> m_FreeSlots = new Stack<int>();

        readonly Dictionary<int, float> m_Requests = new Dictionary<int, float>();
        readonly List<KeyValuePair<int, float>> m_Candidates = new List<KeyValuePair<int, float>>();
        readonly List<int> m_Victims = new List<int>();
        readonly Stack<int> m_Walk = new Stack<int>();

        public int PageCount => m_State.Length;
        public int SlotCount => m_SlotPage.Length;
        public int FreeSlots => m_FreeSlots.Count;
        public int ResidentCount { get; private set; }
        public int LoadingCount { get; private set; }
        /// <summary>The last Schedule call could not find a slot for a wanted page.</summary>
        public bool Saturated { get; private set; }
        /// <summary>Missing pages requested by the feedback since the last Schedule call.</summary>
        public int PendingRequests => m_Requests.Count;

        /// <summary>A page must be unused for this many frames before plain LRU eviction may take it.</summary>
        public uint MinIdleFrames = 2;
        /// <summary>A used page gives way to a request only if the request's priority is at least this many times its own.</summary>
        public float ReplaceRatio = 2f;

        public State StateOf(int page) => m_State[page];
        public bool IsLive(int page) => m_Live[page];
        public int SlotOf(int page) => m_Slot[page];
        public int DependentsOf(int page) => m_Dependents[page];
        public uint LastUsed(int page) => m_LastUsed[page];

        /// <summary>
        /// Starts over: every page live and Absent except the pinned ones. `deps` holds the dependency
        /// lists (global page indices) at [depsStart[p], depsStart[p] + depsCount[p]).
        /// </summary>
        public void Reset(int pageCount, int slotCount, int[] depsStart, int[] depsCount, int[] deps, bool[] pinned)
        {
            Reset(pageCount, slotCount);
            var lists = new int[pageCount][];
            for (int p = 0; p < pageCount; ++p)
            {
                lists[p] = depsCount[p] == 0 ? s_NoDeps : new int[depsCount[p]];
                Array.Copy(deps, depsStart[p], lists[p], 0, depsCount[p]);
            }
            AddPages(0, lists, pinned);
        }

        /// <summary>Starts over with `pageCapacity` page indices (none live) and `slotCount` free slots.</summary>
        public void Reset(int pageCapacity, int slotCount)
        {
            m_State = new State[pageCapacity];
            m_Live = new bool[pageCapacity];
            m_Slot = new int[pageCapacity];
            m_Deps = new int[pageCapacity][];
            m_Dependents = new int[pageCapacity];
            m_LastUsed = new uint[pageCapacity];
            m_LastPriority = new float[pageCapacity];
            for (int p = 0; p < pageCapacity; ++p)
            {
                m_Slot[p] = -1;
                m_Deps[p] = s_NoDeps;
            }
            m_SlotPage = new int[Math.Max(0, slotCount)];
            m_FreeSlots.Clear();
            for (int s = m_SlotPage.Length - 1; s >= 0; --s)
            {
                m_SlotPage[s] = -1;
                m_FreeSlots.Push(s);
            }
            m_Requests.Clear();
            ResidentCount = 0;
            LoadingCount = 0;
            Saturated = false;
        }

        /// <summary>
        /// Makes pages [first, first + deps.Length) live: Absent, or Pinned where `pinned` says so.
        /// Dependencies must point inside live ranges (normally the same mesh).
        /// </summary>
        public void AddPages(int first, int[][] deps, bool[] pinned)
        {
            for (int i = 0; i < deps.Length; ++i)
            {
                int p = first + i;
                if (m_Live[p])
                    throw new InvalidOperationException($"page {p} is already live");
                m_Live[p] = true;
                m_State[p] = pinned != null && pinned[i] ? State.Pinned : State.Absent;
                m_Slot[p] = -1;
                m_Deps[p] = deps[i] ?? s_NoDeps;
                m_Dependents[p] = 0;
                m_LastUsed[p] = 0;
                m_LastPriority[p] = 0;
            }
        }

        /// <summary>
        /// Retires pages [first, first + count) (a mesh unregistered): loading and resident ones give
        /// their slots back and are appended to `released`. Dependencies must not cross the range.
        /// </summary>
        public void RemovePages(int first, int count, List<int> released)
        {
            for (int p = first; p < first + count; ++p)
            {
                m_Requests.Remove(p);
                var s = m_State[p];
                if (s == State.Resident || s == State.Loading)
                {
                    int slot = m_Slot[p];
                    m_SlotPage[slot] = -1;
                    m_FreeSlots.Push(slot);
                    if (s == State.Resident) ResidentCount--; else LoadingCount--;
                    released?.Add(p);
                }
            }
            for (int p = first; p < first + count; ++p)
            {
                m_State[p] = State.Absent;
                m_Live[p] = false;
                m_Slot[p] = -1;
                m_Deps[p] = s_NoDeps;
                m_Dependents[p] = 0;
            }
        }

        /// <summary>GPU feedback for one page: a use of a resident page or a request for a missing one.</summary>
        public void AddFeedback(int page, float priority, uint frame)
        {
            if ((uint)page >= (uint)m_State.Length || !m_Live[page])
                return;
            switch (m_State[page])
            {
                case State.Resident:
                case State.Loading:
                    m_LastUsed[page] = frame;
                    m_LastPriority[page] = priority;
                    break;
                case State.Absent:
                    m_Requests.TryGetValue(page, out float p);
                    m_Requests[page] = Math.Max(p, priority);
                    break;
            }
        }

        bool DepsAtLeast(int page, bool allowLoading)
        {
            foreach (int dep in m_Deps[page])
            {
                var s = m_State[dep];
                if (s == State.Absent || (s == State.Loading && !allowLoading))
                    return false;
            }
            return true;
        }

        /// <summary>True when a loading page's dependencies are all resident, so it may be committed.</summary>
        public bool CanCommit(int page) => m_State[page] == State.Loading && DepsAtLeast(page, false);

        /// <summary>
        /// Turns the collected requests into at most `maxLoads` new loads (appended to `loads`, each
        /// with its slot) and the evictions they need (appended to `evicted`). Missing dependencies are
        /// requested with their children's priority, so parents always load first.
        /// </summary>
        public void Schedule(uint frame, int maxLoads, List<int> loads, List<int> evicted)
        {
            Saturated = false;
            if (m_Requests.Count == 0)
                return;

            // missing ancestors inherit the priority of the pages that need them
            m_Candidates.Clear();
            foreach (var kv in m_Requests)
                m_Candidates.Add(kv);
            foreach (var kv in m_Candidates)
            {
                m_Walk.Push(kv.Key);
                while (m_Walk.Count > 0)
                {
                    int page = m_Walk.Pop();
                    foreach (int dep in m_Deps[page])
                    {
                        if (m_State[dep] != State.Absent)
                            continue;
                        if (!m_Requests.TryGetValue(dep, out float p) || p < kv.Value)
                        {
                            m_Requests[dep] = kv.Value;
                            m_Walk.Push(dep);
                        }
                    }
                }
            }
            m_Candidates.Clear();
            foreach (var kv in m_Requests)
                m_Candidates.Add(kv);
            m_Requests.Clear();
            // highest priority first; parents (lower page index inside a mesh) before children on ties
            m_Candidates.Sort((a, b) => a.Value != b.Value ? b.Value.CompareTo(a.Value) : a.Key.CompareTo(b.Key));

            BuildVictimList(frame);
            int victimCursor = 0;
            int started = 0;
            foreach (var kv in m_Candidates)
            {
                if (started >= maxLoads)
                    break;
                int page = kv.Key;
                if (!m_Live[page] || m_State[page] != State.Absent || !DepsAtLeast(page, true))
                    continue;

                int slot = m_FreeSlots.Count > 0 ? m_FreeSlots.Pop() : TakeVictim(page, frame, kv.Value, ref victimCursor, evicted);
                if (slot < 0)
                {
                    Saturated = true;
                    break;
                }

                m_State[page] = State.Loading;
                m_Slot[page] = slot;
                m_SlotPage[slot] = page;
                m_LastUsed[page] = frame;
                m_LastPriority[page] = kv.Value;
                LoadingCount++;
                AddDependents(page, +1);
                loads.Add(page);
                started++;
            }
        }

        // Evictable resident pages (no resident/loading dependents), least recently used first,
        // lower priority first among equally old pages.
        void BuildVictimList(uint frame)
        {
            m_Victims.Clear();
            for (int s = 0; s < m_SlotPage.Length; ++s)
            {
                int page = m_SlotPage[s];
                if (page >= 0 && m_State[page] == State.Resident && m_Dependents[page] == 0)
                    m_Victims.Add(page);
            }
            m_Victims.Sort((a, b) => m_LastUsed[a] != m_LastUsed[b] ? m_LastUsed[a].CompareTo(m_LastUsed[b]) : m_LastPriority[a].CompareTo(m_LastPriority[b]));
        }

        bool IsDependencyOf(int dep, int page) => Array.IndexOf(m_Deps[page], dep) >= 0;

        // A slot for `forPage`: never one of its own dependencies (they must stay resident).
        int TakeVictim(int forPage, uint frame, float priority, ref int cursor, List<int> evicted)
        {
            // plain LRU: pages unused for MinIdleFrames
            while (cursor < m_Victims.Count)
            {
                int page = m_Victims[cursor];
                if (m_State[page] != State.Resident || m_Dependents[page] != 0 || IsDependencyOf(page, forPage))
                {
                    cursor++;
                    continue;
                }
                if (frame - m_LastUsed[page] < MinIdleFrames)
                    break;
                cursor++;
                return Evict(page, evicted);
            }
            // pool full of pages in use: the least important one gives way to a much more important request
            int best = -1;
            for (int i = cursor; i < m_Victims.Count; ++i)
            {
                int page = m_Victims[i];
                if (m_State[page] != State.Resident || m_Dependents[page] != 0 || IsDependencyOf(page, forPage))
                    continue;
                if (m_LastPriority[page] * ReplaceRatio <= priority && (best < 0 || m_LastPriority[page] < m_LastPriority[best]))
                    best = page;
            }
            return best >= 0 ? Evict(best, evicted) : -1;
        }

        int Evict(int page, List<int> evicted)
        {
            int slot = m_Slot[page];
            m_State[page] = State.Absent;
            m_Slot[page] = -1;
            m_SlotPage[slot] = -1;
            ResidentCount--;
            AddDependents(page, -1);
            evicted?.Add(page);
            return slot;
        }

        void AddDependents(int page, int delta)
        {
            foreach (int dep in m_Deps[page])
                m_Dependents[dep] += delta;
        }

        /// <summary>Marks a loading page resident (its data is in its slot). Call only when CanCommit.</summary>
        public void Commit(int page)
        {
            if (!CanCommit(page))
                throw new InvalidOperationException($"page {page} cannot be committed before its dependencies");
            m_State[page] = State.Resident;
            LoadingCount--;
            ResidentCount++;
        }

        /// <summary>
        /// Abandons a load (read error): the page becomes Absent again and its slot is freed, and so
        /// does every loading page that depends on it (they could never be committed). Cancelled
        /// pages are appended to `cancelled`.
        /// </summary>
        public void CancelLoad(int page, List<int> cancelled = null)
        {
            if (m_State[page] != State.Loading)
                return;
            int slot = m_Slot[page];
            m_State[page] = State.Absent;
            m_Slot[page] = -1;
            m_SlotPage[slot] = -1;
            m_FreeSlots.Push(slot);
            LoadingCount--;
            AddDependents(page, -1);
            cancelled?.Add(page);
            if (m_Dependents[page] == 0)
                return;
            // rare (read failures): find the loading dependents by scanning the slots
            for (int s = 0; s < m_SlotPage.Length; ++s)
            {
                int other = m_SlotPage[s];
                if (other >= 0 && m_State[other] == State.Loading && IsDependencyOf(page, other))
                    CancelLoad(other, cancelled);
            }
        }

        /// <summary>Checks every invariant; returns null when consistent, else a description (tests).</summary>
        public string Validate()
        {
            var dependents = new int[m_State.Length];
            int resident = 0, loading = 0;
            var slotSeen = new bool[m_SlotPage.Length];
            for (int p = 0; p < m_State.Length; ++p)
            {
                var s = m_State[p];
                if (!m_Live[p] && (s != State.Absent || m_Slot[p] != -1 || m_Deps[p].Length != 0))
                    return $"retired page {p} is {s} (slot {m_Slot[p]})";
                if (s == State.Resident || s == State.Loading)
                {
                    if (s == State.Resident) resident++; else loading++;
                    int slot = m_Slot[p];
                    if (slot < 0 || slot >= m_SlotPage.Length || m_SlotPage[slot] != p || slotSeen[slot])
                        return $"page {p}: bad slot {slot}";
                    slotSeen[slot] = true;
                    foreach (int dep in m_Deps[p])
                    {
                        var ds = m_State[dep];
                        if (ds == State.Absent || (s == State.Resident && ds == State.Loading))
                            return $"page {p} ({s}) depends on {dep} ({ds})";
                        dependents[dep]++;
                    }
                }
                else if (m_Slot[p] != -1)
                    return $"page {p} ({s}) holds slot {m_Slot[p]}";
            }
            for (int p = 0; p < m_State.Length; ++p)
                if (dependents[p] != m_Dependents[p])
                    return $"page {p}: dependents {m_Dependents[p]} != {dependents[p]}";
            if (resident != ResidentCount || loading != LoadingCount)
                return $"counts {ResidentCount}/{LoadingCount} != {resident}/{loading}";
            int used = 0;
            foreach (int page in m_SlotPage)
                if (page >= 0)
                    used++;
            if (used + m_FreeSlots.Count != m_SlotPage.Length)
                return $"slots: {used} used + {m_FreeSlots.Count} free != {m_SlotPage.Length}";
            return null;
        }
    }
}
