using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace UNanite
{
    /// <summary>Page streaming state (M7), updated every rendered frame.</summary>
    public struct VgStreamingStats
    {
        public bool enabled;
        public int totalPages;          // pages of all registered meshes
        public int rootPages;           // always resident
        public int residentPages;       // streamed pages currently resident
        public int loadingPages;
        public int slots;               // streaming slots in the pool
        public long poolBytes;          // root region + slots
        public long rootBytes;
        public long streamableBytes;    // total size of all streamable pages
        public long residentBytes;      // bytes of the resident streamed pages
        public int requestsLastFeedback;
        public int feedbackEntries;     // distinct pages reported by the last processed feedback
        public bool feedbackOverflow;
        public bool saturated;          // a wanted page found no slot
        public int loadsStarted;        // this frame
        public int pagesUploaded;       // this frame
        public long bytesUploaded;      // this frame
        public int evictions;           // this frame
        public long totalBytesUploaded; // since the pool was (re)built
        public int totalEvictions;
    }

    // M7 streaming: page pool = packed root pages of every registered mesh + fixed slots of
    // `slotBytes`; VG_PageOffsets holds each page's pool address or ~0 (not resident). CullClusters
    // reports every group item's page with its projected error (VG_PageFeedback + VG_FeedbackList);
    // at the end of each context render GatherFeedback compacts that for an AsyncGPUReadback. The
    // CPU side (VgPageResidency) turns it into loads and evictions; loads read the page file with
    // AsyncReadManager (or copy from an embedded blob) into staging buffers and are committed with a
    // bounded upload per frame, before any culling of that frame.
    public sealed unsafe partial class VgWorld
    {
        static class StreamIds
        {
            public static readonly int Feedback = Shader.PropertyToID("VG_Feedback");
            public static readonly int FeedbackOut = Shader.PropertyToID("VG_FeedbackOut");
            public static readonly int GroupItemsIn = Shader.PropertyToID("VG_GroupItemsIn");
            public static readonly int StreamingConfig = Shader.PropertyToID("_StreamingConfig");
        }

        struct PageLoad
        {
            public int page;
            public int staging;
            public ReadHandle handle;
            public bool reading; // AsyncReadManager read in flight
            public bool ready;   // data in the staging buffer
            public int size;
        }

        readonly VgPageResidency m_Residency = new VgPageResidency();
        uint[] m_PageTable = Array.Empty<uint>();     // CPU mirror of VG_PageOffsets
        bool m_PageTableDirty;
        int[] m_PageMesh = Array.Empty<int>();        // global page -> mesh index
        int[] m_PageSize = Array.Empty<int>();
        uint m_SlotBase;                              // pool byte address of slot 0
        int m_SlotBytes;
        int m_TotalPageCapacity;
        bool m_StreamingActive;                       // streamable pages exist and streaming is on
        bool m_StreamingSetting;
        int m_StreamingPoolSetting;

        GraphicsBuffer m_Feedback, m_FeedbackOut; // [count, list[capacity], word per page], gathered pairs
        int m_FeedbackCapacity;
        int m_FeedbackWindow;
        int m_FeedbackIdleStreak, m_FeedbackTick;
        bool m_FeedbackConsumed; // a readback was processed this frame
        readonly Queue<AsyncGPUReadbackRequest> m_FeedbackReadbacks = new Queue<AsyncGPUReadbackRequest>();
        int k_GatherFeedback = -1, k_ResetFeedback = -1;

        readonly List<PageLoad> m_Loads = new List<PageLoad>();
        NativeArray<byte>[] m_Staging = Array.Empty<NativeArray<byte>>();
        NativeArray<ReadCommand>[] m_ReadCommands = Array.Empty<NativeArray<ReadCommand>>();
        readonly Stack<int> m_FreeStaging = new Stack<int>();
        readonly List<int> m_NewLoads = new List<int>();
        readonly List<int> m_Evicted = new List<int>();
        readonly List<int> m_Cancelled = new List<int>();
        uint m_StreamFrame;
        VgStreamingStats m_StreamStats;

        public VgStreamingStats StreamingStats => m_StreamStats;

        /// <summary>GPU/CPU sampler around the feedback gather ("VG.StreamingFeedback").</summary>
        public static readonly CustomSampler FeedbackSampler = CustomSampler.Create("VG.StreamingFeedback", true);
        /// <summary>Debug: skip the feedback readback (the gather still runs), to measure its cost.</summary>
        public static bool DebugSkipFeedbackReadback;

        void InitStreaming()
        {
            if (m_Cull.HasKernel("GatherFeedback") && m_Cull.HasKernel("ResetFeedback"))
            {
                k_GatherFeedback = m_Cull.FindKernel("GatherFeedback");
                k_ResetFeedback = m_Cull.FindKernel("ResetFeedback");
            }
            m_FeedbackCapacity = Mathf.Max(256, m_Settings.streamingFeedbackCapacity);
            m_FeedbackOut = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2 * m_FeedbackCapacity + 1, 4);
            m_Feedback = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_FeedbackCapacity + 2, 4);
            m_Feedback.SetData(new uint[m_Feedback.count]);
        }

        void DisposeStreaming()
        {
            AbandonLoads();
            foreach (var s in m_Staging)
                if (s.IsCreated)
                    s.Dispose();
            foreach (var c in m_ReadCommands)
                if (c.IsCreated)
                    c.Dispose();
            m_Staging = Array.Empty<NativeArray<byte>>();
            m_ReadCommands = Array.Empty<NativeArray<ReadCommand>>();
            m_FreeStaging.Clear();
            m_Feedback?.Dispose();
            m_FeedbackOut?.Dispose();
            m_Feedback = m_FeedbackOut = null;
            m_FeedbackReadbacks.Clear();
        }

        // Waits for reads in flight (their staging buffers must stay alive) and drops every load.
        void AbandonLoads()
        {
            foreach (var load in m_Loads)
            {
                if (load.reading && load.handle.IsValid())
                {
                    load.handle.JobHandle.Complete();
                    load.handle.Dispose();
                }
            }
            m_Loads.Clear();
            m_FreeStaging.Clear();
            for (int i = m_Staging.Length - 1; i >= 0; --i)
                m_FreeStaging.Push(i);
        }

        void EnsureStaging(int count, int bytes)
        {
            if (m_Staging.Length == count && (count == 0 || m_Staging[0].Length == bytes))
                return;
            AbandonLoads();
            foreach (var s in m_Staging)
                if (s.IsCreated)
                    s.Dispose();
            foreach (var c in m_ReadCommands)
                if (c.IsCreated)
                    c.Dispose();
            m_Staging = new NativeArray<byte>[count];
            m_ReadCommands = new NativeArray<ReadCommand>[count];
            m_FreeStaging.Clear();
            for (int i = count - 1; i >= 0; --i)
            {
                m_Staging[i] = new NativeArray<byte>(bytes, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                m_ReadCommands[i] = new NativeArray<ReadCommand>(1, Allocator.Persistent);
                m_FreeStaging.Push(i);
            }
        }

        /// <summary>
        /// Per-frame streaming work, before any culling of the frame: consume GPU feedback, schedule
        /// loads and evictions, start reads, commit finished loads within the upload budget and
        /// upload the page table.
        /// </summary>
        void UpdateStreaming()
        {
            if (m_StreamingSetting != m_Settings.streaming || m_StreamingPoolSetting != m_Settings.streamingPoolMB)
            {
                RebuildMeshTables();
            }
            if (!m_StreamingActive)
                return;

            m_StreamFrame++;
            m_StreamStats.loadsStarted = 0;
            m_StreamStats.pagesUploaded = 0;
            m_StreamStats.bytesUploaded = 0;
            m_StreamStats.evictions = 0;

            ConsumeFeedback();

            // finished reads first: committing them frees staging buffers for this frame's new loads
            PollReads();
            CommitLoads();

            // new loads (as many as free staging buffers), with the evictions they need
            m_NewLoads.Clear();
            m_Evicted.Clear();
            m_StreamStats.requestsLastFeedback = m_Residency.PendingRequests;
            if (m_FeedbackConsumed)
                m_FeedbackIdleStreak = m_Residency.PendingRequests == 0 && m_Residency.LoadingCount == 0 && m_Loads.Count == 0 ? m_FeedbackIdleStreak + 1 : 0;
            m_Residency.Schedule(m_StreamFrame, m_FreeStaging.Count, m_NewLoads, m_Evicted);
            m_StreamStats.saturated = m_Residency.Saturated;
            foreach (int page in m_Evicted)
            {
                m_PageTable[page] = VgFormat.Invalid;
                m_PageTableDirty = true;
            }
            m_StreamStats.evictions = m_Evicted.Count;
            m_StreamStats.totalEvictions += m_Evicted.Count;
            foreach (int page in m_NewLoads)
                StartLoad(page);
            m_StreamStats.loadsStarted = m_NewLoads.Count;

            // embedded pages are ready at once (within what is left of the upload budget)
            CommitLoads();

            if (m_PageTableDirty)
            {
                m_PageTableDirty = false;
                m_PageOffsetBuffer.SetData(m_PageTable);
            }

            m_StreamStats.residentPages = m_Residency.ResidentCount;
            m_StreamStats.loadingPages = m_Residency.LoadingCount;
            long resident = 0;
            for (int p = 0; p < m_TotalPageCapacity; ++p)
                if (m_Residency.StateOf(p) == VgPageResidency.State.Resident)
                    resident += m_PageSize[p];
            m_StreamStats.residentBytes = resident;
        }

        void ConsumeFeedback()
        {
            m_FeedbackConsumed = false;
            while (m_FeedbackReadbacks.Count > 0 && m_FeedbackReadbacks.Peek().done)
            {
                var req = m_FeedbackReadbacks.Dequeue();
                if (req.hasError)
                    continue;
                var data = req.GetData<uint>();
                m_FeedbackConsumed = true;
                uint header = data[0];
                int total = (int)Math.Min(header & 0x7FFFFFFFu, (uint)m_FeedbackCapacity);
                int count = Math.Min(total, (data.Length - 1) / 2);
                m_FeedbackWindow = total;
                m_StreamStats.feedbackEntries = total;
                m_StreamStats.feedbackOverflow = (header & 0x80000000u) != 0 || count < total;
                for (int i = 0; i < count; ++i)
                {
                    int page = (int)data[1 + 2 * i];
                    float priority = math_asfloat(data[2 + 2 * i]);
                    m_Residency.AddFeedback(page, priority, m_StreamFrame);
                }
            }
        }

        static float math_asfloat(uint v) => *(float*)&v;

        void StartLoad(int page)
        {
            var m = m_Meshes[m_PageMesh[page]];
            int local = page - (int)m.gpu.pageBase;
            var h = m.reader.Header;
            var info = m.reader.Pages[local];
            if (m.fullBlob == null && m.pageFile == null)
            {
                m_Residency.CancelLoad(page); // page file missing (reported at registration)
                return;
            }
            int staging = m_FreeStaging.Pop();
            var load = new PageLoad { page = page, staging = staging, size = (int)info.dataSize };
            uint blobOffset = h.pageDataOffset + info.dataOffset;
            if (m.fullBlob != null)
            {
                NativeArray<byte>.Copy(m.fullBlob, (int)blobOffset, m_Staging[staging], 0, load.size);
                load.ready = true;
            }
            else
            {
                var cmds = m_ReadCommands[staging];
                cmds[0] = new ReadCommand
                {
                    Buffer = m_Staging[staging].GetUnsafePtr(),
                    Offset = blobOffset - h.streamDataOffset,
                    Size = load.size,
                };
                load.handle = AsyncReadManager.Read(m.pageFile, (ReadCommand*)cmds.GetUnsafePtr(), 1);
                load.reading = true;
            }
            m_Loads.Add(load);
        }

        void PollReads()
        {
            for (int i = 0; i < m_Loads.Count; ++i)
            {
                var load = m_Loads[i];
                if (!load.reading)
                    continue;
                var status = load.handle.Status;
                if (status == ReadStatus.InProgress)
                    continue;
                load.handle.Dispose();
                load.reading = false;
                if (status == ReadStatus.Complete)
                {
                    load.ready = true;
                    m_Loads[i] = load;
                }
                else
                {
                    Debug.LogWarning($"UNanite: page read failed ({status}) for page {load.page}");
                    m_Cancelled.Clear();
                    m_Residency.CancelLoad(load.page, m_Cancelled);
                    // loads of pages depending on it are dropped with it
                    for (int j = m_Loads.Count - 1; j >= 0; --j)
                    {
                        if (!m_Cancelled.Contains(m_Loads[j].page))
                            continue;
                        var dropped = m_Loads[j];
                        if (dropped.reading && dropped.handle.IsValid())
                        {
                            dropped.handle.JobHandle.Complete();
                            dropped.handle.Dispose();
                        }
                        m_FreeStaging.Push(dropped.staging);
                        m_Loads.RemoveAt(j);
                        if (j <= i)
                            i--;
                    }
                }
            }
        }

        // Commits ready loads whose dependencies are resident, parents first, within the upload budget.
        void CommitLoads()
        {
            long budget = (long)(Mathf.Max(0.5f, m_Settings.streamingUploadMBPerFrame) * 1048576f);
            m_Loads.Sort((a, b) => a.page.CompareTo(b.page));
            bool progress = true;
            while (progress)
            {
                progress = false;
                for (int i = 0; i < m_Loads.Count; ++i)
                {
                    var load = m_Loads[i];
                    if (!load.ready || !m_Residency.CanCommit(load.page))
                        continue;
                    if (m_StreamStats.bytesUploaded > 0 && m_StreamStats.bytesUploaded + load.size > budget)
                        return;
                    uint address = m_SlotBase + (uint)(m_Residency.SlotOf(load.page) * m_SlotBytes);
                    m_PagePool.SetData(m_Staging[load.staging].Reinterpret<uint>(1), 0, (int)(address / 4), (load.size + 3) / 4);
                    m_Residency.Commit(load.page);
                    m_PageTable[load.page] = address;
                    m_PageTableDirty = true;
                    m_FreeStaging.Push(load.staging);
                    m_Loads.RemoveAt(i--);
                    m_StreamStats.pagesUploaded++;
                    m_StreamStats.bytesUploaded += load.size;
                    m_StreamStats.totalBytesUploaded += load.size;
                    progress = true;
                }
            }
        }

        /// <summary>After every view of a context: compact this frame's page feedback and read it back.</summary>
        void RequestStreamingFeedback()
        {
            if (!m_StreamingActive || k_GatherFeedback < 0 || m_ViewSlotsUsed == 0 || !SystemInfo.supportsAsyncGPUReadback)
                return;
            // steady state (the last feedbacks requested nothing, nothing loading): gather and read
            // back every 4th frame only; the GPU keeps accumulating reports (max priority) meanwhile
            if (m_FeedbackIdleStreak >= 2 && (m_FeedbackTick++ & 3) != 0)
                return;
            m_Cmd.Clear();
            m_Cmd.BeginSample(FeedbackSampler);
            m_Cmd.SetComputeIntParams(m_Cull, StreamIds.StreamingConfig, 1, m_FeedbackCapacity, 0, 0);
            foreach (int k in new[] { k_GatherFeedback, k_ResetFeedback })
            {
                m_Cmd.SetComputeBufferParam(m_Cull, k, StreamIds.Feedback, m_Feedback);
                m_Cmd.SetComputeBufferParam(m_Cull, k, StreamIds.FeedbackOut, m_FeedbackOut);
            }
            m_Cmd.DispatchCompute(m_Cull, k_GatherFeedback, (m_FeedbackCapacity + 63) / 64, 1, 1);
            m_Cmd.DispatchCompute(m_Cull, k_ResetFeedback, 1, 1, 1);
            m_Cmd.EndSample(FeedbackSampler);
            Graphics.ExecuteCommandBuffer(m_Cmd);
            m_Cmd.Clear();
            // read back only about twice as many entries as the last feedback had (at least 256); a
            // longer list loses the tail for one frame (those pages report again) and grows the window
            if (m_FeedbackReadbacks.Count < 8 && !DebugSkipFeedbackReadback)
            {
                int entries = Mathf.Min(m_FeedbackCapacity, Mathf.Max(256, Mathf.NextPowerOfTwo(2 * m_FeedbackWindow)));
                m_FeedbackReadbacks.Enqueue(AsyncGPUReadback.Request(m_FeedbackOut, (1 + 2 * entries) * 4, 0));
            }
        }

        void BindStreaming(CommandBuffer cmd, int kernel)
        {
            cmd.SetComputeBufferParam(m_Cull, kernel, StreamIds.Feedback, m_Feedback);
            cmd.SetComputeBufferParam(m_Cull, kernel, StreamIds.GroupItemsIn, m_GroupItems);
        }

        void SetStreamingConstants(CommandBuffer cmd)
        {
            cmd.SetComputeIntParams(m_Cull, StreamIds.StreamingConfig, m_StreamingActive ? 1 : 0, m_FeedbackCapacity, 0, 0);
        }

        /// <summary>Tests / tools: waits for feedback readbacks and page reads in flight.</summary>
        public void WaitForStreaming()
        {
            foreach (var req in m_FeedbackReadbacks)
                req.WaitForCompletion();
            foreach (var load in m_Loads)
                if (load.reading && load.handle.IsValid())
                    load.handle.JobHandle.Complete();
        }

        /// <summary>Debug: synchronous readback of the last gathered feedback ([0] count, then page / priority bits pairs).</summary>
        public uint[] DebugReadFeedback()
        {
            var data = new uint[m_FeedbackOut.count];
            m_FeedbackOut.GetData(data);
            return data;
        }

        static int s_DebugSlotLimit;

        /// <summary>Tests / tools: caps the number of streaming slots (0 = budget only, negative = none: root pages only); rebuilds the pool.</summary>
        public static int DebugSlotLimit
        {
            get => s_DebugSlotLimit;
            set
            {
                s_DebugSlotLimit = value;
                if (s_Instance != null)
                    s_Instance.m_MeshTablesDirty = true;
            }
        }

        /// <summary>Tests / tools: drops every streamed page (roots stay), as after startup.</summary>
        public void ResetStreaming() => m_MeshTablesDirty = true;

        /// <summary>Debug: residency of one page of a registered mesh (Absent/Loading/Resident/Pinned).</summary>
        public VgPageResidency.State DebugPageState(VirtualGeometryMesh mesh, int page)
        {
            if (!m_MeshLookup.TryGetValue(mesh, out var rec) || m_MeshTablesDirty)
                return VgPageResidency.State.Absent;
            return m_Residency.StateOf((int)rec.gpu.pageBase + page);
        }
    }
}
