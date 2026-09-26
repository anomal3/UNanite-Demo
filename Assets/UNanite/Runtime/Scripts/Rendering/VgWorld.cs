using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace UNanite
{
    /// <summary>Per-view statistics read back from the GPU (one frame late).</summary>
    public struct VgViewStats
    {
        public string name;
        public int visibleInstances;
        public int groupItems;
        public int visibleClusters;
        public int triangles;
        public uint overflowMask; // bit0 node queue, bit1 group items, bit2 visible clusters, bit3 vertices, bit4 indices
        public int doubleSidedClusters;
        public bool visibilityBuffer; // M3 path (raster + resolve) instead of expansion
        public bool shadowRaster;     // M6: shadow split drawn with the vertex-pulled shadow raster
        // M4 two-phase occlusion (visibility-buffer cameras)
        public int deferredInstances;  // occluded by the previous-frame HZB in phase 1
        public int deferredNodes;
        public int deferredClusters;
        public int phase2Clusters;     // deferred work found visible again in phase 2
        public int phase2Triangles;
        // M5 software raster (visibility-buffer cameras)
        public int softwareClusters;       // phase 1
        public int softwareClustersPhase2;
        // M6 shadows
        public bool receiverCulling;       // split culled in the custom pass against the camera's receivers
        public int receiverCulled;         // instances + nodes + clusters rejected by receiver culling
    }

    /// <summary>
    /// Owns all GPU state of the virtual geometry system: mesh tables, page pool, instances,
    /// material bins, the per-frame expansion arena and the BatchRendererGroup that submits the
    /// GPU-driven draws into the active SRP. There is one world per domain.
    ///
    /// Frame flow:
    ///   beginContextRendering → upload dirty data, reset the arena
    ///   BRG culling callback (per camera / shadow split) → record the view's cull + expand
    ///       dispatches (VgCull.compute), execute them, return one indirect draw per material bin
    ///   SRP renders → the draws read the arena ranges written by the view's dispatches
    /// Visibility-buffer cameras (M3, VgWorld.Visibility.cs) skip the expansion of resolve-capable
    /// bins; a BeforeRendering custom pass rasterises them and classifies tiles, and the bins are
    /// drawn as procedural resolve draws inside HDRP's GBuffer pass. Shadow splits (M6,
    /// VgWorld.Shadows.cs) draw shadow-capable bins with a vertex-pulled ShadowCaster pass and are
    /// culled against the camera's receivers inside the AfterOpaqueDepthAndNormal pass when possible.
    /// CPU cost per frame is O(views × bins) and independent of the instance count.
    /// </summary>
    public sealed unsafe partial class VgWorld : IDisposable
    {
        static VgWorld s_Instance;

        public static bool IsSupported =>
            SystemInfo.supportsComputeShaders &&
            SystemInfo.supportsIndirectArgumentsBuffer &&
            SystemInfo.graphicsDeviceType is GraphicsDeviceType.Direct3D12 or GraphicsDeviceType.Vulkan or GraphicsDeviceType.Direct3D11 or GraphicsDeviceType.Metal;

        public static VgWorld Instance => s_Instance;

        static float s_RetryAfter;

        /// <summary>
        /// Returns the world, creating it on first use. Creation is exception-safe: a failure (for
        /// example a compute shader whose import is still pending after a script reload) releases
        /// everything created so far and is retried after a short delay, never every frame.
        /// </summary>
        public static VgWorld GetOrCreate()
        {
            if (s_Instance != null || !IsSupported || Time.realtimeSinceStartup < s_RetryAfter)
                return s_Instance;

            var world = new VgWorld();
            try
            {
                world.Initialize();
            }
            catch (Exception e)
            {
                Debug.LogError($"UNanite: could not create the virtual geometry world ({e.Message}); retrying in 5 s.");
                world.m_Valid = false;
            }
            if (!world.m_Valid)
            {
                world.Dispose();
                s_RetryAfter = Time.realtimeSinceStartup + 5f;
                return null;
            }
            s_Instance = world;
            return s_Instance;
        }

        /// <summary>FindKernel that fails with a clear message instead of an ArgumentException mid-initialisation.</summary>
        static int RequireKernel(ComputeShader cs, string name)
        {
            if (!cs.HasKernel(name))
                throw new InvalidOperationException($"kernel '{name}' missing in {cs.name} (shader import pending or failed)");
            return cs.FindKernel(name);
        }

        static bool HasKernels(ComputeShader cs, params string[] names)
        {
            if (cs == null)
                return false;
            foreach (var n in names)
                if (!cs.HasKernel(n))
                    return false;
            return true;
        }

        // ---------------------------------------------------------------------------------------
        // Meshes

        // ---------------------------------------------------------------------------------------
        // Instances

        VgInstanceGpu[] m_Instances = new VgInstanceGpu[256];
        int[] m_InstanceMaterialCount = new int[256];
        int m_InstanceHighWater;
        int m_LiveInstances;
        readonly Stack<int> m_FreeInstances = new Stack<int>();
        int m_DirtyMin = int.MaxValue, m_DirtyMax = -1;
        GraphicsBuffer m_InstanceBuffer;

        readonly List<uint> m_InstanceBins = new List<uint>();
        readonly Dictionary<int, Stack<int>> m_FreeBinRanges = new Dictionary<int, Stack<int>>();
        bool m_InstanceBinsDirty;
        GraphicsBuffer m_InstanceBinBuffer;

        // ---------------------------------------------------------------------------------------
        // Material bins

        readonly List<Material> m_BinMaterials = new List<Material>();
        readonly List<BatchMaterialID> m_BinMaterialIds = new List<BatchMaterialID>();
        readonly List<int> m_BinRefCount = new List<int>();
        // key: material, lightmapped, owner instance (M10: sorted transparent instances get their own bin; -1 = shared),
        // SpeedTree wind slot (M11, -1 = none)
        readonly Dictionary<(Material, bool, int, int), int> m_BinLookup = new Dictionary<(Material, bool, int, int), int>();
        readonly List<int> m_BinOwner = new List<int>();
        int m_OwnedBins;
        readonly List<uint> m_BinFlags = new List<uint>();                 // VG_BIN_* (VgFormat.hlsl)
        readonly List<Material> m_BinResolveMaterials = new List<Material>(); // runtime twins (null = expansion only)
        readonly List<BatchMaterialID> m_BinResolveIds = new List<BatchMaterialID>();
        readonly List<bool> m_BinResolveOverride = new List<bool>(); // M8: resolve material owned by someone else
        bool m_BinFlagsDirty;
        GraphicsBuffer m_BinFlagsBuffer;
        int m_BinCapacity;

        // ---------------------------------------------------------------------------------------
        // BRG + arena

        BatchRendererGroup m_BRG;
        BatchID m_BatchID;
        BatchMeshID m_ArenaMeshID;
        GraphicsBuffer m_BatchData;
        GraphicsBuffer m_VisibleInstances;
        bool m_RawBatchBuffers;
        Mesh m_Arena;
        GraphicsBuffer m_ArenaVertices, m_ArenaIndices;

        // per-view working buffers (reused sequentially by every view on the GPU timeline)
        GraphicsBuffer m_Counters, m_ArenaCounters, m_NodeQueue0, m_NodeQueue1, m_GroupItems, m_Visible;
        GraphicsBuffer m_BinTriangles, m_BinCursor, m_DrawArgs, m_Stats;

        ComputeShader m_Cull;
        int k_ResetFrame, k_ResetView, k_CullInstances, k_PrepareNodes, k_TraverseNodes, k_PrepareClusters, k_CullClusters, k_AllocateBins, k_PrepareExpand, k_Expand, k_PrepareRaster,
            k_BeginPhase2, k_CullDeferredInstances, k_SeedDeferredNodes, k_CullDeferredClusters, k_FinishPhase2;
        readonly CommandBuffer m_Cmd = new CommandBuffer { name = "UNanite" };
        readonly VirtualGeometrySettings m_Settings;
        bool m_Valid;

        int m_ViewSlotsUsed;
        readonly List<string> m_ViewNames = new List<string>();
        readonly List<VgViewStats> m_LastStats = new List<VgViewStats>();
        bool m_StatsPending;
        bool m_WarnedViews;

        public IReadOnlyList<VgViewStats> LastFrameStats => m_LastStats;

        /// <summary>GPU/CPU sampler around every view's cull + expand work ("VG.CullView" in the profiler).</summary>
        public static readonly CustomSampler CullSampler = CustomSampler.Create("VG.CullView", true);

        /// <summary>Debug: synchronous readback of the working counters of the last culled view.</summary>
        public uint[] DebugReadCounters()
        {
            var data = new uint[k_CounterCount];
            m_Counters.GetData(data);
            return data;
        }

        /// <summary>Debug: arena usage, the first `n` indices, vertex positions and the draw args of view slot 0.</summary>
        public string DebugReadArena(int n)
        {
            var counters = new uint[4];
            m_ArenaCounters.GetData(counters);
            var ib = new uint[Mathf.Min(n, (int)counters[1])];
            if (ib.Length > 0)
                m_ArenaIndices.GetData(ib, 0, 0, ib.Length);
            uint maxIndex = 0;
            foreach (var i in ib) maxIndex = System.Math.Max(maxIndex, i);
            var vb = new float[12 * 4];
            m_ArenaVertices.GetData(vb, 0, 0, vb.Length);
            var args = new uint[m_BinCapacity * 5];
            m_DrawArgs.GetData(args, 0, 0, args.Length);
            return $"arena vertices {counters[0]}/{m_ArenaVertices.count} indices {counters[1]}/{m_ArenaIndices.count}; first indices max {maxIndex}: [{string.Join(",", ib.Take(12))}]; " +
                   $"v0 pos ({vb[0]:F2},{vb[1]:F2},{vb[2]:F2}) n ({vb[3]:F2},{vb[4]:F2},{vb[5]:F2}); v1 pos ({vb[12]:F2},{vb[13]:F2},{vb[14]:F2}); slot0 args: [{string.Join(",", args.Take(Mathf.Min(args.Length, 15)))}]";
        }

        /// <summary>Debug: synchronous readback of the mesh table.</summary>
        public uint[] DebugReadMeshTable()
        {
            var data = new uint[m_MeshBuffer.count * VgMeshGpu.Stride / 4];
            m_MeshBuffer.GetData(data);
            return data;
        }
        public int InstanceCount => m_LiveInstances;
        public long PagePoolBytes => m_PagePool != null ? (long)m_PagePool.count * 4 : 0;

        static class Ids
        {
            public static readonly int Meshes = Shader.PropertyToID("VG_Meshes");
            public static readonly int Groups = Shader.PropertyToID("VG_Groups");
            public static readonly int Nodes = Shader.PropertyToID("VG_Nodes");
            public static readonly int PageOffsets = Shader.PropertyToID("VG_PageOffsets");
            public static readonly int PagePool = Shader.PropertyToID("VG_PagePool");
            public static readonly int Instances = Shader.PropertyToID("VG_Instances");
            public static readonly int InstanceBins = Shader.PropertyToID("VG_InstanceBins");
            public static readonly int Counters = Shader.PropertyToID("VG_Counters");
            public static readonly int ArenaCounters = Shader.PropertyToID("VG_ArenaCounters");
            public static readonly int NodeQueue0 = Shader.PropertyToID("VG_NodeQueue0");
            public static readonly int NodeQueue1 = Shader.PropertyToID("VG_NodeQueue1");
            public static readonly int GroupItems = Shader.PropertyToID("VG_GroupItems");
            public static readonly int Visible = Shader.PropertyToID("VG_Visible");
            public static readonly int BinTriangles = Shader.PropertyToID("VG_BinTriangles");
            public static readonly int BinCursor = Shader.PropertyToID("VG_BinCursor");
            public static readonly int DrawArgs = Shader.PropertyToID("VG_DrawArgs");
            public static readonly int Stats = Shader.PropertyToID("VG_Stats");
            public static readonly int ArenaVertices = Shader.PropertyToID("VG_ArenaVertices");
            public static readonly int ArenaIndices = Shader.PropertyToID("VG_ArenaIndices");
            public static readonly int Planes = Shader.PropertyToID("_Planes");
            public static readonly int ViewPosAndLodA = Shader.PropertyToID("_ViewPosAndLodA");
            public static readonly int LodCamera = Shader.PropertyToID("_LodCamera");
            public static readonly int DensityLod = Shader.PropertyToID("VG_DensityLod");             // M11
            public static readonly int DensityLodOrigin = Shader.PropertyToID("VG_DensityLodOrigin"); // M11
            public static readonly int Batch = Shader.PropertyToID("_Batch");
            public static readonly int SplitViews = Shader.PropertyToID("VG_SplitViews");
            public static readonly int LodParams = Shader.PropertyToID("_LodParams");
            public static readonly int LodMorph = Shader.PropertyToID("VG_LodMorph");
            public static readonly int LodFade = Shader.PropertyToID("_LodFade");
            public static readonly int RasterMotion = Shader.PropertyToID("VG_RasterMotion");
            public static readonly int Counts = Shader.PropertyToID("_Counts");
            public static readonly int Capacities = Shader.PropertyToID("_Capacities");
            public static readonly int Iteration = Shader.PropertyToID("_Iteration");
            public static readonly int ViewConfig = Shader.PropertyToID("_ViewConfig");
            public static readonly int BinFlags = Shader.PropertyToID("VG_BinFlags");
            public static readonly int RasterArgs = Shader.PropertyToID("VG_RasterArgs");
            public static readonly int Hzb = Shader.PropertyToID("VG_Hzb");
            public static readonly int PhaseState = Shader.PropertyToID("VG_PhaseState");
            public static readonly int OccludedInstances = Shader.PropertyToID("VG_OccludedInstances");
            public static readonly int OccludedNodes = Shader.PropertyToID("VG_OccludedNodes");
            public static readonly int OccludedClusters = Shader.PropertyToID("VG_OccludedClusters");
            public static readonly int OccViewProj = Shader.PropertyToID("_OccViewProj");
            public static readonly int OccScreen = Shader.PropertyToID("_OccScreen");
            public static readonly int OccConfig = Shader.PropertyToID("_OccConfig");
            public static readonly int OccHzb = Shader.PropertyToID("_OccHzb");
        }

        const int k_CounterCount = 40;
        const int k_MaxBatchSplits = 8;       // split index in 3 bits of the work items (VgCull.compute)
        const int k_SplitViewStride = 384;    // VgSplitView (VgCull.compute)
        const int k_DispatchBatchOffset = 36 * 4;
        const int k_StatsPerView = 16;
        const int k_DispatchNodesOffset = 4 * 4;
        const int k_DispatchClustersOffset = 8 * 4;
        const int k_DispatchExpandOffset = 12 * 4;
        const uint k_ViewFlagShadow = 1, k_ViewFlagNoCone = 2;

        VgWorld()
        {
            m_Settings = VirtualGeometrySettings.Active;
        }

        // Everything that allocates GPU resources; may throw (GetOrCreate disposes the partial state).
        void Initialize()
        {
            m_Cull = Resources.Load<ComputeShader>("UNanite/VgCull");
            if (m_Cull == null)
            {
                Debug.LogError("UNanite: VgCull.compute not found in Resources/UNanite");
                return;
            }

            k_ResetFrame = RequireKernel(m_Cull, "ResetFrame");
            k_ResetView = RequireKernel(m_Cull, "ResetView");
            k_CullInstances = RequireKernel(m_Cull, "CullInstances");
            k_PrepareNodes = RequireKernel(m_Cull, "PrepareNodes");
            k_TraverseNodes = RequireKernel(m_Cull, "TraverseNodes");
            k_PrepareClusters = RequireKernel(m_Cull, "PrepareClusters");
            k_CullClusters = RequireKernel(m_Cull, "CullClusters");
            k_AllocateBins = RequireKernel(m_Cull, "AllocateBins");
            k_PrepareExpand = RequireKernel(m_Cull, "PrepareExpand");
            k_Expand = RequireKernel(m_Cull, "Expand");
            k_PrepareRaster = RequireKernel(m_Cull, "PrepareRaster");
            k_BeginPhase2 = RequireKernel(m_Cull, "BeginPhase2");
            k_CullDeferredInstances = RequireKernel(m_Cull, "CullDeferredInstances");
            k_SeedDeferredNodes = RequireKernel(m_Cull, "SeedDeferredNodes");
            k_CullDeferredClusters = RequireKernel(m_Cull, "CullDeferredClusters");
            k_FinishPhase2 = RequireKernel(m_Cull, "FinishPhase2");
            k_UpdateLodFade = RequireKernel(m_Cull, "UpdateLodFade");

            CreateArena();
            CreateBRG();
            CreateViewBuffers();
            InitStreaming();
            EnsureBinCapacity(16);
            EnsureInstanceCapacity(256);
            InitVisibility();
            InitSoftwareRaster();
            InitShadows();
            InitPulled();
            UploadLodSwitches();

            RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
            RenderPipelineManager.endContextRendering += OnEndContextRendering;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
#if UNITY_EDITOR
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += DisposeInstance;
#endif
            Application.quitting += DisposeInstance;
            m_Valid = true;
        }

        static void DisposeInstance()
        {
            s_Instance?.Dispose();
            s_Instance = null;
        }

        public void Dispose()
        {
            RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
            RenderPipelineManager.endContextRendering -= OnEndContextRendering;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
#if UNITY_EDITOR
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= DisposeInstance;
#endif
            Application.quitting -= DisposeInstance;

            DisposeVisibility();
            DisposeSoftwareRaster();
            DisposeShadows();
            DisposeStreaming();
            DisposeLightmaps();
            DisposePulled();
            if (m_BRG != null)
            {
                m_BRG.Dispose();
                m_BRG = null;
            }
            foreach (var b in new[] { m_SplitViews, m_MeshBuffer, m_GroupBuffer, m_NodeBuffer, m_PageOffsetBuffer, m_PagePool, m_InstanceBuffer, m_InstanceBinBuffer,
                         m_BatchData, m_VisibleInstances, m_ArenaVertices, m_ArenaIndices, m_Counters, m_ArenaCounters, m_NodeQueue0, m_NodeQueue1,
                         m_GroupItems, m_Visible, m_BinTriangles, m_BinCursor, m_DrawArgs, m_Stats, m_BinFlagsBuffer, m_RasterArgs, m_LodSwitchBuffer, m_LodFadeState })
                b?.Dispose();
            if (m_Arena != null)
                CoreUtils.Destroy(m_Arena);
            if (m_DebugMaterial != null)
                CoreUtils.Destroy(m_DebugMaterial);
            for (int b = 0; b < m_BinResolveMaterials.Count; ++b)
                if (m_BinResolveMaterials[b] != null && !m_BinResolveOverride[b])
                    CoreUtils.Destroy(m_BinResolveMaterials[b]);
            m_Cmd.Release();
            m_CameraCmd.Release();
            if (s_Instance == this)
                s_Instance = null;
        }

        // ---------------------------------------------------------------------------------------
        // Setup

        // M11 follow-up: debug views expand every visible triangle into the arena; in heavy scenes (the
        // Terrain Sample with Unity-dense grass: 7.7 M camera triangles in the triangles view) it
        // overflowed and dropped other clusters every frame (flicker). An overflow reported by the stats
        // readback grows the overflowing part x2 on the next frame, up to 4x the setting.
        int m_ArenaVertexGrowth = 1, m_ArenaIndexGrowth = 1;
        uint m_ArenaOverflow; // 8: vertices, 16: indices (VgCull.compute SetOverflow)

        void GrowArenaIfNeeded()
        {
            uint overflow = m_ArenaOverflow;
            m_ArenaOverflow = 0;
            bool vertices = (overflow & 8u) != 0 && m_ArenaVertexGrowth < 4, indices = (overflow & 16u) != 0 && m_ArenaIndexGrowth < 4;
            if (!vertices && !indices)
                return;
            if (vertices)
                m_ArenaVertexGrowth *= 2;
            if (indices)
                m_ArenaIndexGrowth *= 2;
            m_BRG.UnregisterMesh(m_ArenaMeshID);
            m_ArenaVertices?.Dispose();
            m_ArenaIndices?.Dispose();
            CoreUtils.Destroy(m_Arena);
            CreateArena();
            m_ArenaMeshID = m_BRG.RegisterMesh(m_Arena);
            Debug.Log($"UNanite: expansion arena grown to {m_ArenaVertices.count} vertices, {m_ArenaIndices.count} indices (overflow in a debug view or a heavy expansion view)");
        }

        void CreateArena()
        {
            int vcap = Mathf.Max(1024, (int)Mathf.Min(int.MaxValue / 64, (long)m_Settings.arenaVertexCapacity * m_ArenaVertexGrowth));
            int icap = Mathf.Max(3072, (int)Mathf.Min(int.MaxValue / 8, (long)m_Settings.arenaIndexCapacity * m_ArenaIndexGrowth) / 3 * 3);

            m_Arena = new Mesh { name = "UNanite Arena", hideFlags = HideFlags.HideAndDontSave };
            m_Arena.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
            m_Arena.indexBufferTarget |= GraphicsBuffer.Target.Raw;
            // Unity orders attributes of a stream by VertexAttribute value; VgCull.compute
            // (StoreVertex) writes exactly this 48-byte layout.
            m_Arena.SetVertexBufferParams(vcap,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float16, 4),
                new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float16, 2));
            m_Arena.SetIndexBufferParams(icap, IndexFormat.UInt32);
            var flags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;
            m_Arena.subMeshCount = 1;
            m_Arena.SetSubMesh(0, new SubMeshDescriptor(0, icap, MeshTopology.Triangles), flags);
            m_Arena.bounds = new Bounds(Vector3.zero, Vector3.one * 1e7f);
            m_Arena.UploadMeshData(true); // drop the CPU copy; contents are GPU-written every frame

            m_ArenaVertices = m_Arena.GetVertexBuffer(0);
            m_ArenaIndices = m_Arena.GetIndexBuffer();
        }

        void CreateBRG()
        {
            m_BRG = new BatchRendererGroup(OnPerformCulling, IntPtr.Zero);
            m_BRG.SetGlobalBounds(new Bounds(Vector3.zero, Vector3.one * 1e7f));
            m_ArenaMeshID = m_BRG.RegisterMesh(m_Arena);

            // One DOTS instance with identity matrices: arena vertices are already in world space.
            // Layout: 64 zero bytes (default for unset properties), then 4 packed float3x4.
            const int zeroBytes = 64, matrixBytes = 48;
            int totalBytes = zeroBytes + 4 * matrixBytes;
            bool raw = m_RawBatchBuffers = BatchRendererGroup.BufferTarget == BatchBufferTarget.RawBuffer;
            m_BatchData = new GraphicsBuffer(raw ? GraphicsBuffer.Target.Raw : GraphicsBuffer.Target.Constant, totalBytes / 4, 4);
            var data = new float[totalBytes / 4];
            var identity = new PackedMatrix(Matrix4x4.identity);
            var tmp = new PackedMatrix[] { identity };
            for (int m = 0; m < 4; ++m)
            {
                int o = (zeroBytes + m * matrixBytes) / 4;
                fixed (PackedMatrix* p = tmp)
                {
                    float* f = (float*)p;
                    for (int k = 0; k < 12; ++k)
                        data[o + k] = f[k];
                }
            }
            m_BatchData.SetData(data);

            var metadata = new NativeArray<MetadataValue>(4, Allocator.Temp);
            metadata[0] = new MetadataValue { NameID = Shader.PropertyToID("unity_ObjectToWorld"), Value = 0x80000000u | (uint)(zeroBytes + 0 * matrixBytes) };
            metadata[1] = new MetadataValue { NameID = Shader.PropertyToID("unity_WorldToObject"), Value = 0x80000000u | (uint)(zeroBytes + 1 * matrixBytes) };
            metadata[2] = new MetadataValue { NameID = Shader.PropertyToID("unity_MatrixPreviousM"), Value = 0x80000000u | (uint)(zeroBytes + 2 * matrixBytes) };
            metadata[3] = new MetadataValue { NameID = Shader.PropertyToID("unity_MatrixPreviousMI"), Value = 0x80000000u | (uint)(zeroBytes + 3 * matrixBytes) };
            m_BatchID = m_BRG.AddBatch(metadata, m_BatchData.bufferHandle);
            metadata.Dispose();

        }

        void CreateViewBuffers()
        {
            var s = m_Settings;
            // one block of counters per split of a batch (block 0: single views and the batch's shared traversal)
            m_Counters = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.IndirectArguments, k_CounterCount * k_MaxBatchSplits, 4);
            m_SplitViews = new GraphicsBuffer(GraphicsBuffer.Target.Structured, k_MaxBatchSplits, k_SplitViewStride);
            Shader.SetGlobalBuffer(Ids.SplitViews, m_SplitViews);
            m_ArenaCounters = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4, 4);
            m_NodeQueue0 = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1024, s.nodeQueueCapacity), 8);
            m_NodeQueue1 = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1024, s.nodeQueueCapacity), 8);
            m_GroupItems = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1024, s.groupItemCapacity), 8);
            // region 0: shadow / expansion views (reused sequentially); regions 1..N: visibility-buffer
            // cameras, whose lists must survive until their raster pass (all cameras cull first)
            m_VisibleCapacity = Mathf.Max(1024, s.visibleClusterCapacity);
            m_Visible = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_VisibleCapacity * (1 + MaxVisibilityCameras), 16);
            m_RasterArgs = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.IndirectArguments, Mathf.Max(1, s.maxViewsPerFrame) * k_RasterArgsStride, 4);
            m_Stats = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, s.maxViewsPerFrame) * k_StatsPerView, 4);
        }

        void EnsureBinCapacity(int bins)
        {
            if (bins <= m_BinCapacity)
                return;
            m_BinCapacity = Mathf.NextPowerOfTwo(Mathf.Max(bins, 16));
            m_BinTriangles?.Dispose();
            m_BinCursor?.Dispose();
            m_DrawArgs?.Dispose();
            m_BinTriangles = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_BinCapacity * k_MaxBatchSplits, 4);
            m_BinCursor = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_BinCapacity * k_MaxBatchSplits, 4);
            m_DrawArgs = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.IndirectArguments,
                Mathf.Max(1, m_Settings.maxViewsPerFrame) * m_BinCapacity * 5, 4);
            // Indirect draws read "visible instance" visibleOffset + SV_InstanceID from this buffer.
            // Every entry is instance 0 (identity matrices); resolve draws use visibleOffset = bin so
            // their vertex shader can recover the bin (VgResolve.hlsl), expansion draws use 0.
            m_VisibleInstances?.Dispose();
            // M11: raster-twin draws use visibleOffset = their args entry (view slot * bins + bin)
            int visibleEntries = Mathf.Max(1, m_Settings.maxViewsPerFrame) * m_BinCapacity;
            m_VisibleInstances = new GraphicsBuffer(GraphicsBuffer.Target.Raw, visibleEntries, 4);
            m_VisibleInstances.SetData(new uint[visibleEntries]);
            m_BinFlagsBuffer?.Dispose();
            m_BinFlagsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_BinCapacity, 4);
            m_BinFlagsDirty = true;
            EnsureClassifyCapacity(m_BinCapacity);
        }

        void EnsureInstanceCapacity(int count)
        {
            if (m_InstanceBuffer != null && m_InstanceBuffer.count >= count)
                return;
            int capacity = Mathf.NextPowerOfTwo(Mathf.Max(count, 256));
            if (m_Instances.Length < capacity)
            {
                Array.Resize(ref m_Instances, capacity);
                Array.Resize(ref m_InstanceMaterialCount, capacity);
            }
            EnsureLightmapCapacity(capacity);
            m_InstanceBuffer?.Dispose();
            m_InstanceBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, VgInstanceGpu.Stride);
            m_DirtyMin = 0;
            m_DirtyMax = m_InstanceHighWater - 1;
        }

        // ---------------------------------------------------------------------------------------
        // Mesh registry

        // ---------------------------------------------------------------------------------------
        // Materials

        int AcquireBin(Material material, bool lightmapped = false, int owner = -1, int wind = -1)
        {
            if (material == null)
                material = GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.defaultMaterial : null;
            if (material == null)
                return -1;
            lightmapped = lightmapped && CanLightmap(material);
            if (!IsSpeedTreeMaterial(material))
                wind = -1; // M11: only SpeedTree materials read the wind

            // M10: transparent materials: one bin per instance (up to a cap) so HDRP sorts them back to front
            if (owner >= 0 && (!IsTransparent(material) || m_OwnedBins >= m_Settings.maxSortedTransparentInstances) && !m_BinLookup.ContainsKey((material, lightmapped, owner, wind)))
                owner = -1;
            if (!m_BinLookup.TryGetValue((material, lightmapped, owner, wind), out int bin))
            {
                bin = m_BinMaterials.Count;
                m_BinMaterials.Add(material);
                m_BinMaterialIds.Add(m_BRG.RegisterMaterial(material));
                m_BinRefCount.Add(0);
                m_BinLookup.Add((material, lightmapped, owner, wind), bin);
                m_BinOwner.Add(owner);
                m_BinWind.Add(wind);
                if (owner >= 0)
                    m_OwnedBins++;
                OnBinCreated(bin, lightmapped);
                m_BinFlags.Add(0);
                m_BinResolveMaterials.Add(null);
                m_BinResolveIds.Add(BatchMaterialID.Null);
                m_BinResolveOverride.Add(false);
                OnBinCreatedPulled();
                RefreshBin(bin);
                EnsureBinCapacity(m_BinMaterials.Count);

                var kw = material.shader.keywordSpace.FindKeyword("DOTS_INSTANCING_ON");
                if (!kw.isValid)
                    Debug.LogWarning($"UNanite: material '{material.name}' ({material.shader.name}) has no DOTS_INSTANCING_ON variant; it cannot render virtual geometry through BatchRendererGroup.", material);
            }
            m_BinRefCount[bin]++;
            return bin;
        }

        void ReleaseBin(int bin)
        {
            if (bin >= 0 && bin < m_BinRefCount.Count)
                m_BinRefCount[bin]--;
        }

        // ---------------------------------------------------------------------------------------
        // Instances

        /// <summary>Adds an instance; returns a handle for later updates.</summary>
        public int AddInstance(VirtualGeometryMesh mesh, IReadOnlyList<Material> materials, in Matrix4x4 localToWorld, bool castShadows) =>
            AddInstance(mesh, materials, localToWorld, castShadows, -1, Vector4.zero);

        /// <summary>
        /// Adds an instance lit by a baked lightmap: `lightmapIndex` into LightmapSettings.lightmaps and the
        /// scale/offset of its lightmap UVs (MeshRenderer.lightmapIndex / lightmapScaleOffset); -1 = none.
        /// M11: `speedTreeWind` = a slot of <see cref="AcquireSpeedTreeWind"/> (its SpeedTree materials sway), -1 = none.
        /// </summary>
        public int AddInstance(VirtualGeometryMesh mesh, IReadOnlyList<Material> materials, in Matrix4x4 localToWorld, bool castShadows,
                               int lightmapIndex, Vector4 lightmapScaleOffset, int speedTreeWind = -1)
        {
            if (mesh == null || !mesh.IsValid)
                return -1;

            var rec = AcquireMesh(mesh);
            int handle = m_FreeInstances.Count > 0 ? m_FreeInstances.Pop() : m_InstanceHighWater++;
            EnsureInstanceCapacity(m_InstanceHighWater);

            int slots = Mathf.Max(1, (int)rec.gpu.materialCount);
            int binBase = AllocateBinRange(slots);
            for (int s = 0; s < slots; ++s)
            {
                // like MeshRenderer: submeshes beyond the material list are not drawn (VgCull skips
                // clusters of an invalid bin)
                if (materials != null && materials.Count > 0 && s >= materials.Count)
                {
                    m_InstanceBins[binBase + s] = VgFormat.Invalid;
                    continue;
                }
                Material mat = materials != null && materials.Count > 0 ? materials[s] : null;
                int bin = AcquireBin(mat, IsLightmapped(lightmapIndex), handle, speedTreeWind);
                m_InstanceBins[binBase + s] = bin < 0 ? 0u : (uint)bin;
            }
            m_InstanceBinsDirty = true;

            var inst = new VgInstanceGpu
            {
                meshIndex = (uint)rec.index,
                materialBase = (uint)binBase,
                flags = VgInstanceGpu.FlagEnabled | (castShadows ? VgInstanceGpu.FlagShadows : 0u),
                lodSelf = VgFormat.Invalid,
                lodParent = VgFormat.Invalid,
                errorScale = 1f,
            };
            if (AllBinsResolve(binBase, slots))
                inst.flags |= VgInstanceGpu.FlagOccludable;
            inst.SetTransform(localToWorld, rec.gpu);
            inst.ResetPrevious(); // M9: a new instance has no motion
            m_Instances[handle] = inst;
            SetInstanceLightmap(handle, lightmapIndex, lightmapScaleOffset);
            m_InstanceMaterialCount[handle] = slots;
            m_LiveInstances++;
            MarkDirty(handle);
            return handle;
        }

        public void UpdateTransform(int handle, in Matrix4x4 localToWorld)
        {
            if (handle < 0 || handle >= m_InstanceHighWater)
                return;
            var rec = m_Meshes[(int)m_Instances[handle].meshIndex];
            MotionOnTransform(handle, ref m_Instances[handle]); // M9: previous transform, moving flag
            m_Instances[handle].SetTransform(localToWorld, rec.gpu);
            MarkDirty(handle);
        }

        // M9: transform fields computed by VgTransformTracker's job
        internal void ApplyTransform(int handle, in VgTransformData d)
        {
            if (handle < 0 || handle >= m_InstanceHighWater)
                return;
            ref var inst = ref m_Instances[handle];
            MotionOnTransform(handle, ref inst);
            inst.localToWorld0 = d.l0;
            inst.localToWorld1 = d.l1;
            inst.localToWorld2 = d.l2;
            inst.worldToLocal0 = d.w0;
            inst.worldToLocal1 = d.w1;
            inst.worldToLocal2 = d.w2;
            inst.maxScale = d.maxScale;
            inst.worldSphere = d.sphere;
            if (d.mirrored != 0)
                inst.flags |= VgInstanceGpu.FlagMirrored;
            else
                inst.flags &= ~VgInstanceGpu.FlagMirrored;
            MarkDirty(handle);
        }

        /// <summary>
        /// M11: the instance's LOD is chosen at `pixelError` times the view's pixel error (terrain tiles
        /// follow their terrain's pixel error like Unity's heightmapPixelError).
        /// </summary>
        public void SetInstancePixelError(int handle, float pixelError)
        {
            if (handle < 0 || handle >= m_InstanceHighWater)
                return;
            m_Instances[handle].errorScale = 1f / Mathf.Max(pixelError, 1e-3f);
            MarkDirty(handle);
        }

        /// <summary>
        /// M11 density LOD: the instance is thinned out with the distance from the camera (settings
        /// densityLod*): terrain grass and other small, numerous instances.
        /// </summary>
        public void SetInstanceDensityLod(int handle, bool enabled, float start = 0f)
        {
            if (handle < 0 || handle >= m_InstanceHighWater)
                return;
            // M11: flags bits 16-31 = the instance's own thinning start in metres (0: VirtualGeometrySettings.densityLodStart)
            uint flags = m_Instances[handle].flags & 0xFFFFu;
            if (enabled)
                flags |= VgInstanceGpu.FlagDensityLod | ((uint)Mathf.Clamp(Mathf.RoundToInt(start), 0, 65535) << 16);
            else
                flags &= ~VgInstanceGpu.FlagDensityLod;
            m_Instances[handle].flags = flags;
            MarkDirty(handle);
        }

        public void SetInstanceEnabled(int handle, bool enabled)
        {
            if (handle < 0 || handle >= m_InstanceHighWater)
                return;
            if (enabled)
                m_Instances[handle].flags |= VgInstanceGpu.FlagEnabled;
            else
                m_Instances[handle].flags &= ~VgInstanceGpu.FlagEnabled;
            MarkDirty(handle);
        }

        public void RemoveInstance(int handle)
        {
            if (handle < 0 || handle >= m_InstanceHighWater || (m_Instances[handle].flags & VgInstanceGpu.FlagEnabled) == 0 && m_InstanceMaterialCount[handle] == 0)
                return;

            ref var inst = ref m_Instances[handle];
            int slots = m_InstanceMaterialCount[handle];
            for (int s = 0; s < slots; ++s)
                ReleaseBin((int)m_InstanceBins[(int)inst.materialBase + s]);
            FreeBinRange((int)inst.materialBase, slots);

            ReleaseMeshRef(m_Meshes[(int)inst.meshIndex]); // released at the next frame start if unused (M8)
            MotionOnRemove(handle);

            inst = default;
            m_InstanceMaterialCount[handle] = 0;
            m_FreeInstances.Push(handle);
            m_LiveInstances--;
            MarkDirty(handle);

            if (m_LiveInstances == 0)
                DisposeInstance(); // release GPU memory when nothing uses virtual geometry
        }

        int AllocateBinRange(int count)
        {
            if (m_FreeBinRanges.TryGetValue(count, out var stack) && stack.Count > 0)
                return stack.Pop();
            int at = m_InstanceBins.Count;
            for (int i = 0; i < count; ++i)
                m_InstanceBins.Add(0);
            return at;
        }

        void FreeBinRange(int at, int count)
        {
            if (!m_FreeBinRanges.TryGetValue(count, out var stack))
                m_FreeBinRanges[count] = stack = new Stack<int>();
            stack.Push(at);
        }

        void MarkDirty(int handle)
        {
            m_DirtyMin = Mathf.Min(m_DirtyMin, handle);
            m_DirtyMax = Mathf.Max(m_DirtyMax, handle);
        }

        // ---------------------------------------------------------------------------------------
        // Frame

        void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            BeforeFrame?.Invoke(); // M9: trackers push transform changes before this frame's upload
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            LastTrackMs = (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            BeginMotionFrame();
            try { BeginContextRenderingBody(); }
            finally { LastBeginFrameMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }
        }

        /// <summary>M9: CPU milliseconds of the last frame start (trackers, motion, uploads) and of the trackers alone.</summary>
        public double LastBeginFrameMs { get; private set; }
        public double LastTrackMs { get; private set; }

        void BeginContextRenderingBody()
        {
            GrowArenaIfNeeded();
            ApplyMeshChanges();
            UpdateStreaming();
            UploadLodSwitches();

            UpdateLightmaps();
#if UNITY_EDITOR
            // materials are edited live in the editor: keep capability flags and twins in sync
            for (int b = 0; b < m_BinMaterials.Count; ++b)
                if (m_BinRefCount[b] > 0)
                    RefreshBin(b);
#endif
            if (m_BinFlagsDirty)
            {
                m_BinFlagsDirty = false;
                var flags = new uint[m_BinCapacity];
                for (int b = 0; b < m_BinFlags.Count; ++b)
                    flags[b] = m_BinFlags[b];
                m_BinFlagsBuffer.SetData(flags);
                RefreshOccludableFlags();
            }

            if (m_DirtyMax >= m_DirtyMin && m_InstanceHighWater > 0)
            {
                int start = Mathf.Max(0, m_DirtyMin);
                int count = Mathf.Min(m_DirtyMax, m_InstanceHighWater - 1) - start + 1;
                if (count > 0)
                {
                    m_InstanceBuffer.SetData(m_Instances, start, start, count);
                    if (m_PulledBatchData != null && m_PulledCapacity == m_InstanceBuffer.count)
                        WritePulledMatrices(start, count);
                }
            }
            EnsurePulledBatch(); // after a capacity change: new batch, every matrix written
            UpdateSpeedTreeWind(); // M11: after EnsurePulledBatch (its buffer holds the wind)
            m_DirtyMin = int.MaxValue;
            m_DirtyMax = -1;
            UploadLightmapST();

            if (m_InstanceBinsDirty)
            {
                m_InstanceBinsDirty = false;
                Realloc(ref m_InstanceBinBuffer, GraphicsBuffer.Target.Structured, Mathf.Max(1, m_InstanceBins.Count), 4);
                if (m_InstanceBins.Count > 0)
                    m_InstanceBinBuffer.SetData(m_InstanceBins);
            }

            BeginVisibilityFrame();
            BeginPulledFrame();
            BeginShadowFrame();
            if (!m_Settings.freezeCulling && m_Frozen.Count > 0)
                m_Frozen.Clear();

            m_Cmd.Clear();
            m_Cmd.SetComputeBufferParam(m_Cull, k_ResetFrame, Ids.ArenaCounters, m_ArenaCounters);
            m_Cmd.DispatchCompute(m_Cull, k_ResetFrame, 1, 1, 1);
            Graphics.ExecuteCommandBuffer(m_Cmd);
            m_Cmd.Clear();

            m_ViewSlotsUsed = 0;
            m_ViewNames.Clear();
        }

        void OnEndContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            RequestStreamingFeedback();
            if (m_ViewSlotsUsed == 0 || m_StatsPending || !SystemInfo.supportsAsyncGPUReadback)
                return;
            var names = m_ViewNames.ToArray();
            int views = m_ViewSlotsUsed;
            m_StatsPending = true;
            AsyncGPUReadback.Request(m_Stats, views * k_StatsPerView * 4, 0, req =>
            {
                m_StatsPending = false;
                if (req.hasError)
                    return;
                var data = req.GetData<uint>();
                m_LastStats.Clear();
                for (int v = 0; v < views && v < names.Length; ++v)
                {
                    int o = v * k_StatsPerView;
                    m_ArenaOverflow |= data[o + 4] & (8u | 16u);
                    m_LastStats.Add(new VgViewStats
                    {
                        name = names[v],
                        visibleInstances = (int)data[o + 0],
                        groupItems = (int)data[o + 1],
                        visibleClusters = (int)data[o + 2],
                        triangles = (int)data[o + 3],
                        overflowMask = data[o + 4],
                        doubleSidedClusters = (int)data[o + 5],
                        visibilityBuffer = (data[o + 6] & BinFlagResolve) != 0,
                        shadowRaster = (data[o + 6] & BinFlagShadow) != 0,
                        receiverCulled = (int)data[o + 14],
                        receiverCulling = data[o + 15] != 0,
                        deferredInstances = (int)data[o + 7],
                        deferredNodes = (int)data[o + 8],
                        deferredClusters = (int)data[o + 9],
                        phase2Clusters = (int)data[o + 10],
                        phase2Triangles = (int)data[o + 11],
                        softwareClusters = (int)data[o + 12],
                        softwareClustersPhase2 = (int)data[o + 13],
                    });
                }
            });
        }

        struct SubView
        {
            public Vector4[] planes;
            public int planeCount;
            public Vector3 position;
            public float lodA, lodB, near;
            public Vector4 camLod;     // shadow views: owner camera position (xyz) and perspective LOD term (w, 0 = off)
            public uint flags;
            public ushort splitMask;
            public string name;
            public int visRegion; // > 0: visibility-buffer camera region in VG_Visible, 0: expansion
            public Camera camera;
            public bool occlusion;        // M4: two-phase occlusion for this camera
            public bool occPhase1;        // a valid previous-frame HZB exists: phase 1 tests + defers
            public OcclusionHistory occHistory;
            public bool swEnabled;               // M5: small clusters go to the software raster
            public float swPixelsPerUnit, swPixelsPerUnitOrtho;
            public bool frozen;                  // debug: culling view frozen (freezeCulling)
            public bool shadowRaster;            // M6: shadow bins drawn by the vertex-pulled shadow raster
            public bool receiverCulling;         // M6: culled later, in the camera's custom pass
            public Matrix4x4 lightViewProj;      // split culling matrix (world -> clip)
            public Vector4 splitSphere;          // split culling sphere (radius <= 0: none)
            public int receiverBatchIndex;       // its receiver pyramid inside the current batch
            public int lodFadeSlot;              // M11 animated LOD crossfade state of the camera (-1: none)
        }

        readonly Vector4[] m_PlaneScratch = new Vector4[16];

        JobHandle OnPerformCulling(BatchRendererGroup rendererGroup, BatchCullingContext ctx, BatchCullingOutput output, IntPtr userContext)
        {
            if (!m_Valid || m_LiveInstances == 0 || m_MeshTablesDirty || m_PagePool == null || m_InstanceBinBuffer == null)
                return default;
            if (ctx.viewType != BatchCullingViewType.Camera && ctx.viewType != BatchCullingViewType.Light)
                return default;

            var subViews = BuildSubViews(ctx);
            if (subViews.Count == 0)
                return default;

            int maxViews = Mathf.Max(1, m_Settings.maxViewsPerFrame);
            if (m_ViewSlotsUsed + subViews.Count > maxViews)
            {
                if (!m_WarnedViews)
                    Debug.LogWarning($"UNanite: more than {maxViews} views in one frame; raise VirtualGeometrySettings.maxViewsPerFrame.");
                m_WarnedViews = true;
                return default;
            }

            int firstSlot = m_ViewSlotsUsed;
            m_Cmd.Clear();
            for (int i = 0; i < subViews.Count; ++i)
            {
                m_ViewNames.Add(subViews[i].name);
                if (subViews[i].receiverCulling)
                    RecordShadowPlaceholder(m_Cmd, subViews[i], firstSlot + i); // culled in the custom pass
                else
                    RecordView(m_Cmd, subViews[i], firstSlot + i);
                RegisterVisibilityCamera(subViews[i], firstSlot + i);
            }
            m_ViewSlotsUsed += subViews.Count;
            Graphics.ExecuteCommandBuffer(m_Cmd);
            m_Cmd.Clear();

            if (m_Settings.debugStopAfterStage == 0)
                EmitDrawCommands(output, subViews, firstSlot);
            return default;
        }

        List<SubView> BuildSubViews(in BatchCullingContext ctx)
        {
            var result = new List<SubView>(ctx.cullingSplits.Length > 0 ? ctx.cullingSplits.Length : 1);
            var s = m_Settings;

            if (ctx.viewType == BatchCullingViewType.Camera)
            {
                float height = 1080f;
#pragma warning disable 618
                var cam = Resources.InstanceIDToObject(ctx.viewID.GetInstanceID()) as Camera;
#pragma warning restore 618
                if (cam != null)
                    height = Mathf.Max(1, cam.pixelHeight);
                m_CullOwner = cam; // the camera's lights are culled right after it (same Cull call)

                var lod = ctx.lodParameters;
                var sv = new SubView
                {
                    position = lod.cameraPosition,
                    near = cam != null ? Mathf.Max(cam.nearClipPlane, 1e-3f) : 0.01f,
                    flags = s.disableConeCulling ? k_ViewFlagNoCone : 0u,
                    splitMask = 0xFF,
                    name = cam != null ? cam.name : "Camera",
                };
                if (lod.isOrthographic)
                    sv.lodA = height / (2f * Mathf.Max(lod.orthoSize, 1e-4f)) / s.pixelError;
                else
                    sv.lodB = 1f / Mathf.Tan(lod.fieldOfView * 0.5f * Mathf.Deg2Rad) * 0.5f * height / s.pixelError;
                m_CullOwnerLod = new Vector4(sv.position.x, sv.position.y, sv.position.z, sv.lodB); // its lights' splits follow
                int offset = ctx.cullingSplits.Length > 0 ? ctx.cullingSplits[0].cullingPlaneOffset : 0;
                int count = ctx.cullingSplits.Length > 0 ? ctx.cullingSplits[0].cullingPlaneCount : ctx.cullingPlanes.Length;
                sv.planes = CopyPlanes(ctx.cullingPlanes, offset, count, out sv.planeCount);
                sv.camera = cam;
                sv.lodFadeSlot = LodFadeSlot(cam);
                sv.visRegion = AssignVisibilityRegion(cam);
                ConfigureOcclusion(ref sv);
                ConfigureSoftwareRaster(ref sv, height, lod);
                ApplyFreeze(ref sv);
                result.Add(sv);
                return result;
            }

            // light: one sub-view per split (cascade / cube face)
            for (int i = 0; i < ctx.cullingSplits.Length && i < 8; ++i)
            {
                var split = ctx.cullingSplits[i];
                var m = split.cullingMatrix;
                bool ortho = Mathf.Abs(m.m30) < 1e-6f && Mathf.Abs(m.m31) < 1e-6f && Mathf.Abs(m.m32) < 1e-6f;
                float res = Mathf.Max(64, s.shadowResolution);
                var sv = new SubView
                {
                    position = ctx.lodParameters.cameraPosition,
                    near = Mathf.Max(split.nearPlane, 1e-3f),
                    flags = k_ViewFlagShadow | k_ViewFlagNoCone,
                    splitMask = (ushort)(1 << i),
                    name = $"Shadow split {i}",
                    lodFadeSlot = -1,
                };
                if (ortho)
                {
                    float unitsToNdc = new Vector3(m.m00, m.m01, m.m02).magnitude;
                    sv.lodA = 0.5f * res * unitsToNdc / s.shadowTexelError;
                }
                else
                {
                    float fov = ctx.lodParameters.fieldOfView > 0 ? ctx.lodParameters.fieldOfView : 90f;
                    sv.lodB = 1f / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * 0.5f * res / s.shadowTexelError;
                }
                sv.planes = CopyPlanes(ctx.cullingPlanes, split.cullingPlaneOffset, split.cullingPlaneCount, out sv.planeCount);
                ConfigureShadowView(ref sv, ctx, split);
                result.Add(sv);
            }
            return result;
        }

        static Vector4[] CopyPlanes(NativeArray<Plane> planes, int offset, int count, out int copied)
        {
            var result = new Vector4[16];
            copied = 0;
            for (int i = 0; i < count && copied < 16; ++i)
            {
                if (offset + i >= planes.Length)
                    break;
                var p = planes[offset + i];
                result[copied++] = new Vector4(p.normal.x, p.normal.y, p.normal.z, p.distance);
            }
            return result;
        }

        // Debug "freeze culling": a camera keeps culling (frustum, LOD, cone, occlusion history) from
        // where it was when the setting was switched on, while it renders from wherever it moves,
        // so what was culled becomes visible. Phase 2 and the HZB history are skipped while frozen.
        readonly Dictionary<Camera, SubView> m_Frozen = new Dictionary<Camera, SubView>();

        void ApplyFreeze(ref SubView sv)
        {
            if (!m_Settings.freezeCulling || sv.camera == null)
            {
                if (m_Frozen.Count > 0 && !m_Settings.freezeCulling)
                    m_Frozen.Clear();
                return;
            }
            if (!m_Frozen.TryGetValue(sv.camera, out var f))
            {
                if (sv.occlusion && !sv.occPhase1)
                    return; // wait for an occlusion history so the frozen view includes occlusion
                m_Frozen[sv.camera] = f = sv;
            }
            sv.position = f.position;
            sv.lodA = f.lodA;
            sv.lodB = f.lodB;
            sv.near = f.near;
            sv.planes = f.planes;
            sv.planeCount = f.planeCount;
            sv.swPixelsPerUnit = f.swPixelsPerUnit;
            sv.swPixelsPerUnitOrtho = f.swPixelsPerUnitOrtho;
            sv.occHistory = sv.occlusion ? f.occHistory : null;
            sv.occPhase1 = sv.occHistory != null && sv.occHistory.valid;
            sv.frozen = true;
        }

        void BindAll(CommandBuffer cmd, int kernel)
        {
            cmd.SetComputeBufferParam(m_Cull, kernel, Ids.Meshes, m_MeshBuffer);
            cmd.SetComputeBufferParam(m_Cull, kernel, Ids.Groups, m_GroupBuffer);
            cmd.SetComputeBufferParam(m_Cull, kernel, Ids.Nodes, m_NodeBuffer);
            cmd.SetComputeBufferParam(m_Cull, kernel, Ids.PageOffsets, m_PageOffsetBuffer);
            cmd.SetComputeBufferParam(m_Cull, kernel, Ids.PagePool, m_PagePool);
            cmd.SetComputeBufferParam(m_Cull, kernel, Ids.Instances, m_InstanceBuffer);
            cmd.SetComputeBufferParam(m_Cull, kernel, Ids.InstanceBins, m_InstanceBinBuffer);
            cmd.SetComputeBufferParam(m_Cull, kernel, Ids.Counters, m_Counters);
            cmd.SetComputeBufferParam(m_Cull, kernel, Ids.ArenaCounters, m_ArenaCounters);
            cmd.SetComputeBufferParam(m_Cull, kernel, Ids.NodeQueue0, m_NodeQueue0);
            cmd.SetComputeBufferParam(m_Cull, kernel, Ids.NodeQueue1, m_NodeQueue1);
            cmd.SetComputeBufferParam(m_Cull, kernel, Ids.GroupItems, m_GroupItems);
            cmd.SetComputeBufferParam(m_Cull, kernel, Ids.Visible, m_Visible);
            cmd.SetComputeBufferParam(m_Cull, kernel, Ids.BinTriangles, m_BinTriangles);
            cmd.SetComputeBufferParam(m_Cull, kernel, Ids.BinCursor, m_BinCursor);
            cmd.SetComputeBufferParam(m_Cull, kernel, Ids.DrawArgs, m_DrawArgs);
            cmd.SetComputeBufferParam(m_Cull, kernel, PulledIds.TriangleIds, m_TriangleIds);
            cmd.SetComputeBufferParam(m_Cull, kernel, PulledIds.VisibleIn, m_Visible);
            cmd.SetComputeBufferParam(m_Cull, kernel, PulledIds.RasterListsIn, m_RasterLists);
            cmd.SetComputeBufferParam(m_Cull, kernel, Ids.Stats, m_Stats);
            cmd.SetComputeBufferParam(m_Cull, kernel, Ids.ArenaVertices, m_ArenaVertices);
            cmd.SetComputeBufferParam(m_Cull, kernel, Ids.ArenaIndices, m_ArenaIndices);
            cmd.SetComputeBufferParam(m_Cull, kernel, Ids.BinFlags, m_BinFlagsBuffer);
            cmd.SetComputeBufferParam(m_Cull, kernel, Ids.RasterArgs, m_RasterArgs);
            BindOcclusion(cmd, kernel);
            cmd.SetComputeBufferParam(m_Cull, kernel, SwIds.RasterLists, m_RasterLists);
            cmd.SetComputeBufferParam(m_Cull, kernel, ShadowIds.ShadowRecords, m_ShadowRecords);
            cmd.SetComputeBufferParam(m_Cull, kernel, ShadowIds.ShadowViews, m_ShadowViews);
            BindStreaming(cmd, kernel);
            BindHlod(cmd, kernel);
        }

        int DebugModeOf(in SubView v) => (v.flags & k_ViewFlagShadow) == 0 ? (int)m_Settings.debugView : 0;

        // M11 density LOD constants (VgFormat.hlsl VgDensityLodScale)
        Vector4 DensityLodParams()
        {
            var s = m_Settings;
            return new Vector4(Mathf.Max(0f, s.densityLodStart), Mathf.Max(0.1f, s.densityLodExponent), Mathf.Max(1f, s.densityLodMaxScale), Mathf.Clamp(s.densityLodFade, 0.01f, 1f));
        }

        // the camera's passes (raster, resolve, pulled shadows) thin density-LOD instances for this camera
        void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (!m_Valid || camera == null)
                return;
            m_CameraCmd.Clear();
            m_CameraCmd.SetGlobalVector(Ids.DensityLod, DensityLodParams());
            m_CameraCmd.SetGlobalVector(Ids.DensityLodOrigin, camera.transform.position);
            // M11 smooth LOD: the camera's LOD factor like its culling (VgAttributes.hlsl VgLodMorphCrossfade)
            float lodB = camera.orthographic ? 0f : 1f / Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 0.5f * Mathf.Max(1, camera.pixelHeight) / m_Settings.pixelError;
            var p = camera.transform.position;
            m_CameraCmd.SetGlobalVector(Ids.LodMorph, new Vector4(p.x, p.y, p.z, lodB > 0f ? 1f / lodB : 0f));
            float motion = Mathf.Max(0f, m_Settings.rasterMotionDistance);
            m_CameraCmd.SetGlobalVector(Ids.RasterMotion, new Vector4(motion * motion, 0f, 0f, 0f));
            // M11 LOD crossfade: the camera's animated-fade states (VgAttributes.hlsl VgLodDither)
            if (m_LodFadeState != null)
            {
                m_CameraCmd.SetGlobalBuffer(s_LodFadeStateId, m_LodFadeState);
                m_CameraCmd.SetGlobalVector(s_LodFadeParamsId, LodFadeConstants(m_LodFadeSlots.TryGetValue(camera, out int slot) ? slot : -1));
            }
            context.ExecuteCommandBuffer(m_CameraCmd);
        }

        readonly CommandBuffer m_CameraCmd = new CommandBuffer { name = "UNanite.Camera" };

        void SetViewConstants(CommandBuffer cmd, in SubView v, int slot, int debugMode)
        {
            cmd.SetComputeVectorArrayParam(m_Cull, Ids.Planes, v.planes);
            cmd.SetComputeVectorParam(m_Cull, Ids.ViewPosAndLodA, new Vector4(v.position.x, v.position.y, v.position.z, v.lodA));
            cmd.SetComputeVectorParam(m_Cull, Ids.LodParams, new Vector4(v.lodB, v.near, v.flags, v.planeCount));
            cmd.SetComputeVectorParam(m_Cull, Ids.LodCamera, v.camLod);
            var fade = LodFadeConstants(v.frozen ? -1 : v.lodFadeSlot);
            cmd.SetComputeIntParams(m_Cull, Ids.LodFade, (int)fade.x, (int)fade.y, System.BitConverter.SingleToInt32Bits(fade.z), 0);
            // M11 density LOD: thinned for the view's camera (shadow splits: their owner camera)
            cmd.SetComputeVectorParam(m_Cull, Ids.DensityLod, DensityLodParams());
            cmd.SetComputeVectorParam(m_Cull, Ids.DensityLodOrigin, v.camera != null ? v.camera.transform.position : v.position);
            cmd.SetComputeIntParams(m_Cull, Ids.Batch, 0, 0, 0, 0); // single view
            cmd.SetComputeIntParams(m_Cull, Ids.Counts, m_InstanceHighWater, m_BinCapacity, slot * m_BinCapacity, m_NodeQueue0.count);
            cmd.SetComputeIntParams(m_Cull, Ids.Capacities, m_GroupItems.count, m_VisibleCapacity, m_ArenaVertices.count, m_ArenaIndices.count);
            cmd.SetComputeIntParams(m_Cull, Ids.Iteration, 0, slot, debugMode, 0);
            // y: bins rasterised by this view instead of expanded (VgCull.compute)
            uint rasterBins = v.visRegion > 0 ? BinFlagResolve : (v.shadowRaster ? BinFlagShadow : 0u);
            cmd.SetComputeIntParams(m_Cull, Ids.ViewConfig, v.visRegion * m_VisibleCapacity, (int)rasterBins, slot * k_RasterArgsStride, m_ShadowRecords.count);
            SetSoftwareConstants(cmd, v);
            SetStreamingConstants(cmd);
        }

        void DispatchTraversal(CommandBuffer cmd, int slot, int debugMode)
        {
            for (int level = 0; level < m_MaxTreeDepth; ++level)
            {
                cmd.SetComputeIntParams(m_Cull, Ids.Iteration, level, slot, debugMode, 0);
                cmd.DispatchCompute(m_Cull, k_PrepareNodes, 1, 1, 1);
                cmd.DispatchCompute(m_Cull, k_TraverseNodes, m_Counters, k_DispatchNodesOffset);
            }
            cmd.SetComputeIntParams(m_Cull, Ids.Iteration, 0, slot, debugMode, 0);
        }

        /// <summary>M11: GPU samplers of the camera views' culling stages (inside "VG.CullView").</summary>
        public static readonly CustomSampler CullInstancesSampler = CustomSampler.Create("VG.Cull.Instances", true);
        public static readonly CustomSampler CullTraverseSampler = CustomSampler.Create("VG.Cull.Traverse", true);
        public static readonly CustomSampler CullClustersSampler = CustomSampler.Create("VG.Cull.Clusters", true);
        public static readonly CustomSampler CullExpandSampler = CustomSampler.Create("VG.Cull.Expand", true);

        void RecordView(CommandBuffer cmd, in SubView v, int slot)
        {
            var s = m_Settings;
            // debug colours only for camera views; shadow views keep real vertex colours
            int debugMode = DebugModeOf(v);
            cmd.BeginSample(CullSampler);
            SetViewConstants(cmd, v, slot, debugMode);
            if (v.receiverCulling)
                SetReceiverConstants(cmd, v);
            else
                SetOcclusionConstants(cmd, v, v.occPhase1 ? 1 : 0, v.occHistory);

            foreach (int k in new[] { k_ResetView, k_CullInstances, k_PrepareNodes, k_TraverseNodes, k_PrepareClusters, k_CullClusters, k_AllocateBins, k_PrepareExpand, k_Expand, k_PrepareRaster, k_PrepareShadowRaster, k_CompactShadow })
                BindAll(cmd, k);

            int stop = s.debugStopAfterStage;
            // camera views: stage samplers (M11 profiling)
            bool stages = v.visRegion > 0;
            if (stages) cmd.BeginSample(CullInstancesSampler);
            if (v.lodFadeSlot >= 0 && !v.frozen)
            {
                // M11 animated LOD crossfade: the camera's switches of this frame, before its instances
                BindAll(cmd, k_UpdateLodFade);
                cmd.DispatchCompute(m_Cull, k_UpdateLodFade, (m_LodSwitches.Count + 63) / 64, 1, 1);
            }
            cmd.DispatchCompute(m_Cull, k_ResetView, 1, 1, 1);
            cmd.DispatchCompute(m_Cull, k_CullInstances, (m_InstanceHighWater + 63) / 64, 1, 1);
            if (stages) cmd.EndSample(CullInstancesSampler);

            if (stages) cmd.BeginSample(CullTraverseSampler);
            if (stop == 0 || stop >= 2)
                DispatchTraversal(cmd, slot, debugMode);
            if (stages) cmd.EndSample(CullTraverseSampler);

            if (stages) cmd.BeginSample(CullClustersSampler);
            if (stop == 0 || stop >= 3)
            {
                cmd.DispatchCompute(m_Cull, k_PrepareClusters, 1, 1, 1);
                cmd.DispatchCompute(m_Cull, k_CullClusters, m_Counters, k_DispatchClustersOffset);
            }
            cmd.DispatchCompute(m_Cull, k_AllocateBins, 1, 1, 1);
            if (stages) cmd.EndSample(CullClustersSampler);
            if (stages) cmd.BeginSample(CullExpandSampler);
            if (stop == 0 || stop >= 4)
            {
                cmd.DispatchCompute(m_Cull, k_PrepareExpand, 1, 1, 1);
                cmd.DispatchCompute(m_Cull, k_Expand, m_Counters, k_DispatchExpandOffset);
            }
            if (stages) cmd.EndSample(CullExpandSampler);
            if (v.visRegion > 0)
                cmd.DispatchCompute(m_Cull, k_PrepareRaster, 1, 1, 1);
            else if (v.shadowRaster)
                RecordShadowCompaction(cmd, slot);

            cmd.EndSample(CullSampler);
        }

        Material m_DebugMaterial;
        BatchMaterialID m_DebugMaterialId;

        BatchMaterialID? GetDebugMaterial()
        {
            var mode = m_Settings.debugView;
            if (mode == VgDebugView.None)
                return null;
            if (m_DebugMaterial == null)
            {
                var shader = Shader.Find("Hidden/UNanite/DebugBRG");
                if (shader == null)
                    return null;
                m_DebugMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, name = "UNanite Debug View" };
                m_DebugMaterialId = m_BRG.RegisterMaterial(m_DebugMaterial);
            }
            int levels = 1;
            foreach (var m in m_Meshes)
                if (m != null)
                    levels = Mathf.Max(levels, (int)m.reader.Header.levelCount);
            m_DebugMaterial.SetInt("_Mode", (int)mode);
            m_DebugMaterial.SetInt("_LevelCount", levels);
            return m_DebugMaterialId;
        }

        void EmitDrawCommands(BatchCullingOutput output, List<SubView> subViews, int firstSlot)
        {
            BatchMaterialID? debugMaterial = GetDebugMaterial();

            // expansion bins -> indexed indirect draws of the arena; resolve bins of visibility-buffer
            // cameras -> procedural indirect tile draws (args written by VgClassify.compute)
            // shadow-raster splits: two procedural draws of their visible records replace the expansion of shadow bins (M6)
            int indirectCount = 0, proceduralCount = 0;
            for (int v = 0; v < subViews.Count; ++v)
            {
                proceduralCount += ShadowRasterDrawCount(subViews[v]);
                for (int b = 0; b < m_BinMaterials.Count; ++b)
                {
                    if (m_BinRefCount[b] <= 0 || IsShadowRasterBin(subViews[v], b))
                        continue;
                    if (IsResolveDraw(subViews[v], b))
                    {
                        proceduralCount++;
                        if (IsProgrammableBin(b))
                            indirectCount++; // M11: + the raster twin's VgVisBuffer draw
                        if (IsRasterMotionBin(b))
                            proceduralCount++; // M11: + the copy of its raster motion
                    }
                    else
                        indirectCount++;
                }
            }
            if (indirectCount + proceduralCount == 0)
                return;

            var draws = (BatchCullingOutputDrawCommands*)output.drawCommands.GetUnsafePtr();
            var indirect = indirectCount == 0 ? null : (BatchDrawCommandIndirect*)UnsafeUtility.Malloc(
                UnsafeUtility.SizeOf<BatchDrawCommandIndirect>() * indirectCount, UnsafeUtility.AlignOf<BatchDrawCommandIndirect>(), Allocator.TempJob);
            var procedural = proceduralCount == 0 ? null : (BatchDrawCommandProceduralIndirect*)UnsafeUtility.Malloc(
                UnsafeUtility.SizeOf<BatchDrawCommandProceduralIndirect>() * proceduralCount, UnsafeUtility.AlignOf<BatchDrawCommandProceduralIndirect>(), Allocator.TempJob);
            // M10: transparent bins sort by their owner instance's centre (3 floats per indirect command)
            float* sortingPositions = indirectCount == 0 ? null : (float*)UnsafeUtility.Malloc(sizeof(float) * 3 * indirectCount, 4, Allocator.TempJob);
            int rangeCount = (indirectCount > 0 ? 1 : 0) + (proceduralCount > 0 ? 2 : 0); // M9: + a motion range
            var ranges = (BatchDrawRange*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<BatchDrawRange>() * rangeCount, UnsafeUtility.AlignOf<BatchDrawRange>(), Allocator.TempJob);
            // raw-buffer mode binds the whole buffer; constant-buffer mode needs a window
            uint windowSize = m_RawBatchBuffers ? 0u : (uint)(m_VisibleInstances.count * 4);

            int ci = 0, cp = 0;
            for (int v = 0; v < subViews.Count; ++v)
            {
                int slot = firstSlot + v;
                if (subViews[v].shadowRaster)
                    cp = EmitShadowRasterDraws(procedural, cp, subViews[v], slot, windowSize);
                for (int b = 0; b < m_BinMaterials.Count; ++b)
                {
                    if (m_BinRefCount[b] <= 0 || IsShadowRasterBin(subViews[v], b))
                        continue;
                    if (IsResolveDraw(subViews[v], b))
                    {
                        procedural[cp++] = new BatchDrawCommandProceduralIndirect
                        {
                            // M9: bins with a moving instance also draw the MotionVectors pass
                            flags = BinHasMotion(b) ? BatchDrawCommandFlags.HasMotion : BatchDrawCommandFlags.None,
                            batchID = IsShaderGraphResolveBin(b) ? PulledBatchFor(b) : m_BatchID, // M10: per-instance matrices (M11: + wind)
                            materialID = m_BinResolveIds[b],
                            topology = MeshTopology.Triangles,
                            splitVisibilityMask = subViews[v].splitMask,
                            lightmapIndex = 0xFFFF,
                            sortingPosition = 0,
                            visibleOffset = (uint)b, // -> GetDOTSIndirectVisibleIndex() in VgResolve.hlsl
                            visibleInstancesBufferHandle = m_VisibleInstances.bufferHandle,
                            visibleInstancesBufferWindowOffset = 0,
                            visibleInstancesBufferWindowSizeBytes = windowSize,
                            indirectArgsBufferHandle = m_ResolveArgs.bufferHandle,
                            indirectArgsBufferOffset = (uint)(b * GraphicsBuffer.IndirectDrawArgs.size),
                        };
                        if (IsRasterMotionBin(b))
                        {
                            // M11: motion vectors of the moved vertices, every frame (object motion range)
                            procedural[cp] = procedural[cp - 1];
                            procedural[cp].flags = BatchDrawCommandFlags.HasMotion;
                            procedural[cp].batchID = m_BatchID;
                            procedural[cp].materialID = RasterMotionMaterialId();
                            cp++;
                        }
                        if (IsProgrammableBin(b))
                        {
                            // M11 programmable raster: the arena indices Expand wrote for this view, drawn
                            // by the custom pass's VgVisBuffer renderer list (no HDRP pass is enabled)
                            sortingPositions[3 * ci + 0] = sortingPositions[3 * ci + 1] = sortingPositions[3 * ci + 2] = 0f;
                            indirect[ci++] = new BatchDrawCommandIndirect
                            {
                                flags = BatchDrawCommandFlags.None,
                                batchID = PulledBatchFor(b),
                                materialID = m_BinRasterIds[b],
                                meshID = m_ArenaMeshID,
                                topology = MeshTopology.Triangles,
                                splitVisibilityMask = subViews[v].splitMask,
                                lightmapIndex = 0xFFFF,
                                visibleOffset = (uint)(slot * m_BinCapacity + b), // -> VG_DrawArgs entry (VgVisRaster.hlsl)
                                visibleInstancesBufferHandle = m_VisibleInstances.bufferHandle,
                                visibleInstancesBufferWindowOffset = 0,
                                visibleInstancesBufferWindowSizeBytes = windowSize,
                                indirectArgsBufferHandle = m_DrawArgs.bufferHandle,
                                indirectArgsBufferOffset = (uint)((slot * m_BinCapacity + b) * GraphicsBuffer.IndirectDrawIndexedArgs.size),
                            };
                        }
                        continue;
                    }
                    bool debug = debugMaterial.HasValue && (subViews[v].flags & k_ViewFlagShadow) == 0;
                    bool pulled = IsPulledBin(b) && !debug; // M10: the arena's indices are pulled-vertex IDs
                    bool sorted = IsTransparent(m_BinMaterials[b]);
                    var center = m_BinOwner[b] >= 0 ? (Vector3)m_Instances[m_BinOwner[b]].worldSphere : Vector3.zero;
                    sortingPositions[3 * ci + 0] = center.x;
                    sortingPositions[3 * ci + 1] = center.y;
                    sortingPositions[3 * ci + 2] = center.z;
                    indirect[ci] = new BatchDrawCommandIndirect
                    {
                        flags = sorted ? BatchDrawCommandFlags.HasSortingPosition : BatchDrawCommandFlags.None,
                        sortingPosition = 3 * ci,
                        batchID = pulled ? PulledBatchFor(b) : m_BatchID,
                        materialID = pulled ? m_BinPulledIds[b] : debug ? debugMaterial.Value : m_BinMaterialIds[b],
                        meshID = m_ArenaMeshID,
                        topology = MeshTopology.Triangles,
                        splitVisibilityMask = subViews[v].splitMask,
                        lightmapIndex = 0xFFFF,
                        visibleOffset = 0,
                        visibleInstancesBufferHandle = m_VisibleInstances.bufferHandle,
                        visibleInstancesBufferWindowOffset = 0,
                        visibleInstancesBufferWindowSizeBytes = windowSize,
                        indirectArgsBufferHandle = m_DrawArgs.bufferHandle,
                        indirectArgsBufferOffset = (uint)((slot * m_BinCapacity + b) * GraphicsBuffer.IndirectDrawIndexedArgs.size),
                    };
                    ci++;
                }
            }

            var filter = new BatchFilterSettings
            {
                renderingLayerMask = 0xFFFFFFFF,
                layer = 0,
                motionMode = MotionVectorGenerationMode.Camera,
                shadowCastingMode = ShadowCastingMode.On,
                receiveShadows = true,
                staticShadowCaster = false,
                allDepthSorted = false,
            };
            // M9: resolve draws with motion last, in their own range (MotionVectorGenerationMode.Object)
            int motionCount = 0;
            for (int i = 0; i < proceduralCount; ++i)
                if ((procedural[i].flags & BatchDrawCommandFlags.HasMotion) != 0)
                    motionCount++;
            if (motionCount > 0 && motionCount < proceduralCount)
            {
                var sorted = (BatchDrawCommandProceduralIndirect*)UnsafeUtility.Malloc(
                    UnsafeUtility.SizeOf<BatchDrawCommandProceduralIndirect>() * proceduralCount, UnsafeUtility.AlignOf<BatchDrawCommandProceduralIndirect>(), Allocator.Temp);
                int s0 = 0, s1 = proceduralCount - motionCount;
                for (int i = 0; i < proceduralCount; ++i)
                    sorted[(procedural[i].flags & BatchDrawCommandFlags.HasMotion) != 0 ? s1++ : s0++] = procedural[i];
                UnsafeUtility.MemCpy(procedural, sorted, UnsafeUtility.SizeOf<BatchDrawCommandProceduralIndirect>() * proceduralCount);
                UnsafeUtility.Free(sorted, Allocator.Temp);
            }
            int staticProcedural = proceduralCount - motionCount;
            var motionFilter = filter;
            motionFilter.motionMode = MotionVectorGenerationMode.Object;
            int r = 0;
            if (indirectCount > 0)
                ranges[r++] = new BatchDrawRange { drawCommandsType = BatchDrawCommandType.Indirect, drawCommandsBegin = 0, drawCommandsCount = (uint)indirectCount, filterSettings = filter };
            if (staticProcedural > 0)
                ranges[r++] = new BatchDrawRange { drawCommandsType = BatchDrawCommandType.ProceduralIndirect, drawCommandsBegin = 0, drawCommandsCount = (uint)staticProcedural, filterSettings = filter };
            if (motionCount > 0)
                ranges[r++] = new BatchDrawRange { drawCommandsType = BatchDrawCommandType.ProceduralIndirect, drawCommandsBegin = (uint)staticProcedural, drawCommandsCount = (uint)motionCount, filterSettings = motionFilter };

            draws->indirectDrawCommands = indirect;
            draws->indirectDrawCommandCount = indirectCount;
            draws->proceduralIndirectDrawCommands = procedural;
            draws->proceduralIndirectDrawCommandCount = proceduralCount;
            draws->drawRanges = ranges;
            draws->drawRangeCount = r;
            draws->drawCommands = null;
            draws->drawCommandCount = 0;
            draws->visibleInstances = null;
            draws->visibleInstanceCount = 0;
            draws->instanceSortingPositions = sortingPositions;
            draws->instanceSortingPositionFloatCount = sortingPositions == null ? 0 : 3 * indirectCount;
        }

        static bool IsTransparent(Material m) => m != null && m.renderQueue > (int)RenderQueue.GeometryLast;

        bool IsRasterMotionBin(int bin) => (m_BinFlags[bin] & BinFlagRasterMotion) != 0 && IsProgrammableBin(bin);

        bool IsResolveDraw(in SubView v, int bin) => v.visRegion > 0 && (m_BinFlags[bin] & BinFlagResolve) != 0 && m_BinResolveMaterials[bin] != null;
    }
}
