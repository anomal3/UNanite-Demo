using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace UNanite
{
    // Mesh registry (M2, incremental since M8): every registered mesh owns a range of the global
    // group, node and page tables and a block of the pool's root region. Meshes are added and
    // released at the start of a frame without touching the others (their streamed pages stay
    // resident); a full rebuild (RebuildMeshTables) only happens when a table or the root region
    // runs out of room, or when the streaming settings change.
    public sealed unsafe partial class VgWorld
    {
        sealed class MeshRecord
        {
            public VirtualGeometryMesh asset;
            public VgMeshReader reader;  // tables and root pages
            public byte[] fullBlob;      // embedded pages (null: page file)
            public string pageFile;      // external pages: absolute path (null: embedded or missing)
            public int index;
            public int refCount;
            public VgMeshGpu gpu;
            public int treeDepth;
            public bool streamed;        // M7: pages beyond the root pages are streamed into slots
            public uint rootAddress;     // M7: pool byte address of the mesh's resident region
            public int rootBytes;        // M8: size of that region (16-aligned)
            public bool registered;      // M8: tables, root pages and page range live on the GPU
        }

        readonly List<MeshRecord> m_Meshes = new List<MeshRecord>();      // index = mesh index; null = free slot
        readonly Stack<int> m_FreeMeshSlots = new Stack<int>();
        readonly Dictionary<VirtualGeometryMesh, MeshRecord> m_MeshLookup = new Dictionary<VirtualGeometryMesh, MeshRecord>();
        readonly List<MeshRecord> m_PendingMeshes = new List<MeshRecord>();
        bool m_MeshTablesDirty;   // full rebuild at the next frame
        bool m_MeshReleaseCheck;  // some mesh lost its last instance
        int m_MaxTreeDepth = 1;
        GraphicsBuffer m_MeshBuffer, m_GroupBuffer, m_NodeBuffer, m_PageOffsetBuffer, m_PagePool;
        readonly VgRangeAllocator m_GroupAlloc = new VgRangeAllocator(0);
        readonly VgRangeAllocator m_NodeAlloc = new VgRangeAllocator(0);
        readonly VgRangeAllocator m_PageAlloc = new VgRangeAllocator(0);
        readonly VgRangeAllocator m_RootAlloc = new VgRangeAllocator(0); // 16-byte units
        readonly List<int> m_Released = new List<int>();

        /// <summary>Full rebuilds of the mesh tables since creation (tests: replacing a mesh must not cause one).</summary>
        public int FullRebuildCount { get; private set; }

        public int MeshCount => m_Meshes.Count - m_FreeMeshSlots.Count;

        MeshRecord AcquireMesh(VirtualGeometryMesh asset)
        {
            if (m_MeshLookup.TryGetValue(asset, out var rec))
            {
                rec.refCount++;
                return rec;
            }

            var reader = asset.ResidentReader;
            var bounds = reader.LocalBounds;
            int index = m_FreeMeshSlots.Count > 0 ? m_FreeMeshSlots.Pop() : m_Meshes.Count;
            rec = new MeshRecord { asset = asset, reader = reader, index = index, refCount = 1 };
            if (asset.HasExternalPages)
            {
                rec.pageFile = asset.ResolvePageFile();
                if (rec.pageFile == null)
                    Debug.LogError($"UNanite: page file of '{asset.name}' not found; only its root pages (coarse LOD) can render.", asset);
            }
            else
                rec.fullBlob = asset.Blob;
            var uvFromXZ = asset.UvFromPositionXZ;
            rec.gpu = new VgMeshGpu
            {
                rootNodeCount = reader.Header.rootNodeCount,
                positionPrecision = reader.Header.positionPrecision,
                positionPrecisionXZ = reader.Header.PrecisionXZ,
                uvPrecision = reader.Header.uvPrecision,
                normalBits = reader.Header.normalBits,
                // terrain tiles: hardware raster only (their small clusters of long border triangles were
                // 0.1 ms slower in the software raster, M8 follow-up)
                meshFlags = reader.Header.meshFlags | (uvFromXZ != Vector2.zero ? VgMeshGpu.FlagUvFromXZ | VgMeshGpu.FlagHardwareRaster : 0u),
                boundsCenter = bounds.center,
                boundsRadius = bounds.extents.magnitude,
                materialCount = reader.Header.materialCount,
                tangentAngleBits = reader.Header.tangentAngleBits,
                uvScaleX = uvFromXZ.x,
                uvScaleZ = uvFromXZ.y,
            };
            rec.treeDepth = ComputeTreeDepth(reader);
            if (index == m_Meshes.Count)
                m_Meshes.Add(rec);
            else
                m_Meshes[index] = rec;
            m_MeshLookup.Add(asset, rec);
            m_PendingMeshes.Add(rec);
            return rec;
        }

        void ReleaseMeshRef(MeshRecord rec)
        {
            rec.refCount--;
            if (rec.refCount <= 0)
                m_MeshReleaseCheck = true;
        }

        static int ComputeTreeDepth(VgMeshReader reader)
        {
            var nodes = reader.Nodes;
            int maxDepth = 1;
            var stack = new Stack<(int node, int depth)>();
            for (int r = 0; r < reader.Header.rootNodeCount; ++r)
                stack.Push((r, 1));
            while (stack.Count > 0)
            {
                var (n, d) = stack.Pop();
                maxDepth = Mathf.Max(maxDepth, d);
                var node = nodes[n];
                if (node.group < 0)
                    for (uint c = 0; c < node.childCount; ++c)
                        stack.Push(((int)(node.childOffset + c), d + 1));
            }
            return maxDepth;
        }

        static void Realloc(ref GraphicsBuffer buffer, GraphicsBuffer.Target target, int count, int stride)
        {
            if (buffer != null && buffer.count >= count && buffer.stride == stride)
                return;
            buffer?.Dispose();
            buffer = new GraphicsBuffer(target, count, stride);
        }

        // Where a mesh's resident pages end: the root pages when streamed, else the whole blob.
        uint ResidentEnd(MeshRecord m)
        {
            var h = m.reader.Header;
            bool canLoadAll = m.fullBlob != null || m.pageFile != null;
            // streaming off still streams (root pages only) a mesh whose page file is missing
            m.streamed = h.streamDataOffset < h.blobSize && (m_StreamingSetting || !canLoadAll);
            return m.streamed ? h.streamDataOffset : h.blobSize;
        }

        int RootBytesOf(MeshRecord m) => (int)(((long)(ResidentEnd(m) - m.reader.Header.pageDataOffset) + 15) & ~15L);

        /// <summary>Frame start: releases unused meshes and registers new ones, incrementally when possible.</summary>
        void ApplyMeshChanges()
        {
            if (m_MeshTablesDirty)
            {
                RebuildMeshTables();
                return;
            }
            bool changed = false;
            if (m_MeshReleaseCheck)
            {
                m_MeshReleaseCheck = false;
                for (int i = 0; i < m_Meshes.Count; ++i)
                {
                    var m = m_Meshes[i];
                    if (m == null || m.refCount > 0)
                        continue;
                    if (m.registered)
                        UnregisterMesh(m);
                    m_PendingMeshes.Remove(m);
                    m_MeshLookup.Remove(m.asset);
                    m_Meshes[i] = null;
                    m_FreeMeshSlots.Push(i);
                    changed = true;
                }
            }
            if (m_PendingMeshes.Count > 0)
            {
                if (m_PagePool == null || m_Meshes.Count > m_MeshBuffer.count)
                {
                    RebuildMeshTables();
                    return;
                }
                foreach (var m in m_PendingMeshes)
                {
                    if (!TryRegisterMesh(m))
                    {
                        RebuildMeshTables();
                        return;
                    }
                }
                m_PendingMeshes.Clear();
                changed = true;
            }
            if (changed)
            {
                UploadMeshTable();
                if (m_PageTableDirty)
                {
                    m_PageTableDirty = false;
                    m_PageOffsetBuffer.SetData(m_PageTable);
                }
                RefreshStreamingTotals();
            }
        }

        bool TryRegisterMesh(MeshRecord m)
        {
            var h = m.reader.Header;
            int rootBytes = RootBytesOf(m);
            int groups = m_GroupAlloc.Allocate((int)h.groupCount);
            int nodes = groups < 0 ? -1 : m_NodeAlloc.Allocate((int)h.nodeCount);
            int pages = nodes < 0 ? -1 : m_PageAlloc.Allocate((int)h.pageCount);
            int root = pages < 0 ? -1 : m_RootAlloc.Allocate(rootBytes / 16);
            if (root < 0)
            {
                if (groups >= 0) m_GroupAlloc.Free(groups, (int)h.groupCount);
                if (nodes >= 0) m_NodeAlloc.Free(nodes, (int)h.nodeCount);
                if (pages >= 0) m_PageAlloc.Free(pages, (int)h.pageCount);
                return false;
            }
            m.gpu.groupBase = (uint)groups;
            m.gpu.nodeBase = (uint)nodes;
            m.gpu.pageBase = (uint)pages;
            m.rootAddress = (uint)root * 16;
            m.rootBytes = rootBytes;
            UploadMesh(m);
            m_MaxTreeDepth = Mathf.Max(m_MaxTreeDepth, m.treeDepth);
            return true;
        }

        // Tables, resident pages and page-table entries of one mesh at its allocated ranges.
        void UploadMesh(MeshRecord m)
        {
            var r = m.reader;
            var h = r.Header;
            if (h.groupCount > 0)
                m_GroupBuffer.SetData(r.Groups, 0, (int)m.gpu.groupBase, (int)h.groupCount);
            if (h.nodeCount > 0)
                m_NodeBuffer.SetData(r.Nodes, 0, (int)m.gpu.nodeBase, (int)h.nodeCount);

            uint residentEnd = ResidentEnd(m);
            byte[] source = m.streamed || m.fullBlob != null ? (m.fullBlob ?? r.Blob) : m.asset.Reader.Blob;
            int bytes = (int)(residentEnd - h.pageDataOffset);
            using (var pageData = new NativeArray<byte>((bytes + 3) & ~3, Allocator.Temp))
            {
                NativeArray<byte>.Copy(source, (int)h.pageDataOffset, pageData, 0, bytes);
                m_PagePool.SetData(pageData.Reinterpret<uint>(1), 0, (int)(m.rootAddress / 4), pageData.Length / 4);
            }

            int first = (int)m.gpu.pageBase;
            var deps = new int[h.pageCount][];
            var pinned = new bool[h.pageCount];
            for (int p = 0; p < r.Pages.Length; ++p)
            {
                int g = first + p;
                bool root = !m.streamed || p < h.rootPageCount;
                pinned[p] = root;
                m_PageTable[g] = root ? m.rootAddress + r.Pages[p].dataOffset : VgFormat.Invalid;
                m_PageMesh[g] = m.index;
                m_PageSize[g] = (int)r.Pages[p].dataSize;
                var d = new int[r.Pages[p].depsCount];
                for (uint k = 0; k < r.Pages[p].depsCount; ++k)
                    d[k] = (int)(m.gpu.pageBase + r.PageDependencies[r.Pages[p].depsOffset + k]);
                deps[p] = d;
            }
            m_Residency.AddPages(first, deps, pinned);
            m_PageTableDirty = true;
            m.registered = true;
        }

        void UnregisterMesh(MeshRecord m)
        {
            var h = m.reader.Header;
            int first = (int)m.gpu.pageBase, count = (int)h.pageCount;

            // loads in flight of its pages are dropped (their staging buffers come back)
            for (int j = m_Loads.Count - 1; j >= 0; --j)
            {
                var load = m_Loads[j];
                if (load.page < first || load.page >= first + count)
                    continue;
                if (load.reading && load.handle.IsValid())
                {
                    load.handle.JobHandle.Complete();
                    load.handle.Dispose();
                }
                m_FreeStaging.Push(load.staging);
                m_Loads.RemoveAt(j);
            }
            m_Released.Clear();
            m_Residency.RemovePages(first, count, m_Released);
            for (int p = first; p < first + count; ++p)
            {
                m_PageTable[p] = VgFormat.Invalid;
                m_PageMesh[p] = -1;
                m_PageSize[p] = 0;
            }
            m_PageTableDirty = true;

            m_GroupAlloc.Free((int)m.gpu.groupBase, (int)h.groupCount);
            m_NodeAlloc.Free((int)m.gpu.nodeBase, (int)h.nodeCount);
            m_PageAlloc.Free(first, count);
            m_RootAlloc.Free((int)(m.rootAddress / 16), m.rootBytes / 16);
            m.registered = false;

            m_MaxTreeDepth = 1;
            foreach (var other in m_Meshes)
                if (other != null && other.registered)
                    m_MaxTreeDepth = Mathf.Max(m_MaxTreeDepth, other.treeDepth);
        }

        void UploadMeshTable()
        {
            var meshData = new VgMeshGpu[m_MeshBuffer.count];
            foreach (var m in m_Meshes)
                if (m != null && m.registered)
                    meshData[m.index] = m.gpu;
            m_MeshBuffer.SetData(meshData);
        }

        static int WithSlack(long used, int minimum) => (int)Math.Min(int.MaxValue, Math.Max(minimum, used + used / 2));

        /// <summary>
        /// Rebuilds the mesh tables and the page pool from scratch: every live mesh's tables at fresh
        /// ranges (with room to grow), root pages packed at the start of the pool, then the streaming
        /// slots. Streaming state starts over. With streaming off every page is packed into the root
        /// region (the M2-M6 behaviour).
        /// </summary>
        void RebuildMeshTables()
        {
            m_MeshTablesDirty = false;
            m_MeshReleaseCheck = false;
            FullRebuildCount++;
            AbandonLoads();
            m_FeedbackReadbacks.Clear();
            m_StreamingSetting = m_Settings.streaming;
            m_StreamingPoolSetting = m_Settings.streamingPoolMB;

            // drop meshes nobody uses any more
            for (int i = 0; i < m_Meshes.Count; ++i)
            {
                var m = m_Meshes[i];
                if (m == null || m.refCount > 0)
                    continue;
                m_MeshLookup.Remove(m.asset);
                m_Meshes[i] = null;
                m_FreeMeshSlots.Push(i);
            }
            m_PendingMeshes.Clear();

            long groups = 0, nodes = 0, pages = 0, rootBytes = 0;
            int maxPageSize = 16, streamablePages = 0;
            foreach (var m in m_Meshes)
            {
                if (m == null)
                    continue;
                var h = m.reader.Header;
                groups += h.groupCount;
                nodes += h.nodeCount;
                pages += h.pageCount;
                rootBytes += RootBytesOf(m);
                maxPageSize = Mathf.Max(maxPageSize, (int)h.pageSize);
                if (m.streamed)
                    streamablePages += (int)(h.pageCount - h.rootPageCount);
            }

            // room to register / replace meshes later without a rebuild
            int groupCap = WithSlack(groups, 1024), nodeCap = WithSlack(nodes, 1024), pageCap = WithSlack(pages, 256);
            long rootCap = (rootBytes + Math.Max(2L << 20, rootBytes / 8) + 15) & ~15L;

            // streaming slots: whatever the budget leaves after the root region, never more than needed
            m_SlotBytes = (maxPageSize + 15) & ~15;
            long budget = Math.Min(2000L, Math.Max(16, m_StreamingPoolSetting)) << 20;
            int slots = streamablePages == 0 ? 0 : (int)Math.Max(4, Math.Min(streamablePages + streamablePages / 4 + 16, (budget - rootCap) / m_SlotBytes));
            if (s_DebugSlotLimit > 0 && slots > 0)
                slots = Math.Min(slots, s_DebugSlotLimit);
            else if (s_DebugSlotLimit < 0)
                slots = 0; // debug: root pages only
            if (rootCap + (long)slots * m_SlotBytes > int.MaxValue)
                throw new InvalidOperationException($"UNanite: root pages ({rootBytes >> 20} MB) exceed the 2 GB pool limit; enable streaming or register fewer meshes.");
            m_SlotBase = (uint)rootCap;
            long poolBytes = Math.Max(16, rootCap + (long)slots * m_SlotBytes);

            Realloc(ref m_MeshBuffer, GraphicsBuffer.Target.Structured, Mathf.Max(16, m_Meshes.Count + m_Meshes.Count / 2), VgMeshGpu.Stride);
            Realloc(ref m_GroupBuffer, GraphicsBuffer.Target.Structured, groupCap, VgFormat.GroupSize);
            Realloc(ref m_NodeBuffer, GraphicsBuffer.Target.Structured, nodeCap, VgFormat.NodeSize);
            Realloc(ref m_PageOffsetBuffer, GraphicsBuffer.Target.Structured, pageCap, 4);
            // exact size: the pool is the memory budget (Realloc would keep a larger old pool)
            if (m_PagePool == null || m_PagePool.count != (int)(poolBytes / 4))
            {
                m_PagePool?.Dispose();
                m_PagePool = new GraphicsBuffer(GraphicsBuffer.Target.Raw, (int)(poolBytes / 4), 4);
            }
            Realloc(ref m_Feedback, GraphicsBuffer.Target.Structured, m_FeedbackCapacity + 1 + pageCap, 4);
            m_Feedback.SetData(new uint[m_Feedback.count]);

            m_GroupAlloc.Reset(m_GroupBuffer.count);
            m_NodeAlloc.Reset(m_NodeBuffer.count);
            m_PageAlloc.Reset(m_PageOffsetBuffer.count);
            m_RootAlloc.Reset((int)(rootCap / 16));
            m_TotalPageCapacity = m_PageOffsetBuffer.count;
            m_FeedbackIdleStreak = 0;
            m_PageTable = new uint[m_TotalPageCapacity];
            for (int p = 0; p < m_PageTable.Length; ++p)
                m_PageTable[p] = VgFormat.Invalid;
            m_PageMesh = new int[m_TotalPageCapacity];
            m_PageSize = new int[m_TotalPageCapacity];
            m_Residency.Reset(m_TotalPageCapacity, slots);

            m_MaxTreeDepth = 1;
            foreach (var m in m_Meshes)
            {
                if (m == null)
                    continue;
                m.registered = false;
                if (!TryRegisterMesh(m))
                    throw new InvalidOperationException("UNanite: internal error, mesh tables sized too small");
            }
            UploadMeshTable();
            m_PageOffsetBuffer.SetData(m_PageTable);
            m_PageTableDirty = false;

            m_StreamingActive = slots > 0;
            EnsureStaging(m_StreamingActive ? Mathf.Clamp(m_Settings.streamingLoadsInFlight, 4, 256) : 0, m_SlotBytes);

            m_StreamStats = new VgStreamingStats
            {
                enabled = m_StreamingActive,
                slots = slots,
                poolBytes = poolBytes,
            };
            RefreshStreamingTotals();
        }

        void RefreshStreamingTotals()
        {
            int total = 0, rootPages = 0;
            long rootBytes = 0, streamable = 0;
            foreach (var m in m_Meshes)
            {
                if (m == null || !m.registered)
                    continue;
                var h = m.reader.Header;
                total += (int)h.pageCount;
                rootPages += m.streamed ? (int)h.rootPageCount : (int)h.pageCount;
                rootBytes += m.rootBytes;
                if (m.streamed)
                    streamable += h.blobSize - h.streamDataOffset;
            }
            m_StreamStats.totalPages = total;
            m_StreamStats.rootPages = rootPages;
            m_StreamStats.rootBytes = rootBytes;
            m_StreamStats.streamableBytes = streamable;
        }
    }
}
