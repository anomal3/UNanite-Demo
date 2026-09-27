using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
#if UNANITE_HDRP
using UnityEngine.Rendering.HighDefinition;
#endif

namespace UNanite
{
    // M13: virtual shadow maps - cached shadow pages of VG casters delivered into HDRP's shadow atlas
    // (VgVsm.hlsl, VgVsm.compute, VgVsmRaster.shader, VgVsmComposite.shader, Milestones.md M13).
    //
    //   culling callback       receiver-culled shadow splits of the shadow raster become cache
    //                          entries (light, split, owner camera): an extra view slot for the
    //                          cached raster; BRG gets a composite draw per entry (args 0 until the
    //                          custom pass fills them)
    //   AfterOpaqueDepthAndNormal (RenderDeferredShadows, after the receiver pyramids of a batch)
    //                          HDRP's request of the split (matrices, resolution, slope bias, z-clip)
    //                          -> entry window / anchor / reset; Release, Invalidate, Mark, Finalize,
    //                          ClearPages, dirty pyramid; cached culling (only shadow-raster bins, only
    //                          over dirty pages, texel LOD); per-frame culling of the other bins (skip
    //                          shadow-raster bins); raster of the dirty pages into the page pool
    //   HDRP shadow pass       composite draw: needed pages -> atlas depth
    public sealed partial class VgWorld
    {
        /// <summary>GPU/CPU sampler around the virtual-shadow-map page work ("VG.Vsm").</summary>
        public static readonly CustomSampler VsmSampler = CustomSampler.Create("VG.Vsm", true);
        /// <summary>GPU/CPU sampler around the raster of dirty shadow pages ("VG.VsmRaster").</summary>
        public static readonly CustomSampler VsmRasterSampler = CustomSampler.Create("VG.VsmRaster", true);

        const int k_VsmMaxEntries = 128;
        const int k_VsmEntryFloats = 48;       // VgVsmEntry (VgVsm.hlsl), 192 bytes
        const int k_VsmPage = 128;
        const int k_VsmPoolRow = 64;           // VSM_POOL_ROW
        const uint k_ViewFlagOnlyRaster = 4u;  // VgCull.compute VIEW_FLAG_ONLY_RASTER
        const uint k_ViewFlagSkipRaster = 8u;  // VgCull.compute VIEW_FLAG_SKIP_RASTER
        const int k_VsmMaxSpheres = 1 << 16;

        sealed class VsmEntry
        {
            public int index;
            public int lightId, split;
            public Camera camera;
            public int lastCulled;         // frame of the last culling reference
            public bool resolved;          // matched to an HDRP request this frame
            public int res, W, tableBase = -1, tableW;   // W wanted this frame, tableW = layout of the allocated table
            public bool ortho, zClip, hasAnchor, reset;
            public Matrix4x4 renderVP, anchorVP, cullVP, anchorCullVP;
            public Vector2Int window;
            public Vector2 frac;           // cascades: sub-texel offset of this frame's grid from the anchor grid
            public float slopeBias, depthA = 1f, depthB;
            public Vector3 eye;            // perspective: light position (texel-LOD origin)
            public float projScale;        // |row 0| of the render matrix: NDC per world unit (ortho) / at distance 1 (perspective)
            public int rasterSlot = -1;    // view slot of the last page raster (diagnostics)
            public int pyramidIndex = -1;  // receiver batch index of the last page raster (diagnostics)
        }

        ComputeShader m_VsmCs;
        int k_VsmBegin, k_VsmRelease, k_VsmInvalidate, k_VsmMark, k_VsmFinalize, k_VsmClear, k_VsmDirty;
        readonly Material[] m_VsmRasterMaterials = new Material[4]; // (single, double-sided) x (z-clip on, off)
        Material m_VsmComposite;
        BatchMaterialID m_VsmCompositeId;
        RenderTexture m_VsmPool, m_VsmDummy;
        GraphicsBuffer m_VsmEntries, m_VsmPages, m_VsmFree, m_VsmCounters, m_VsmDirty, m_VsmList, m_VsmArgs, m_VsmSpheres, m_VsmHzb, m_VsmBatch;
        readonly uint[] m_VsmBatchData = new uint[4 * k_VsmMaxEntries];
        VgRangeAllocator m_VsmTables;
        readonly VsmEntry[] m_VsmSlots = new VsmEntry[k_VsmMaxEntries];
        readonly Dictionary<(int, int, Camera), VsmEntry> m_VsmLookup = new Dictionary<(int, int, Camera), VsmEntry>();
        readonly float[] m_VsmEntryData = new float[k_VsmMaxEntries * k_VsmEntryFloats];
        readonly HashSet<int> m_VsmTouched = new HashSet<int>();
        Vector4[] m_VsmCasterSpheres = new Vector4[0];
        readonly List<Vector4> m_VsmSphereList = new List<Vector4>();
        int m_VsmSphereCount, m_VsmFrame, m_VsmPoolPages;
        uint m_VsmEpoch = 1;
        bool m_VsmSupported, m_VsmReadbackPending, m_VsmResetAll;
        MaterialPropertyBlock m_VsmProps;
        static readonly uint[] s_Zero4 = new uint[4];

        /// <summary>M13 statistics (last read back): pages needed / re-rendered, pool use, overflow.</summary>
        public struct VsmStats
        {
            public int entries, pagesNeeded, pagesRendered, overflow, poolPages, poolFree;
            public int recordsRastered;   // cluster records of the dirty-page raster (all cached splits, last frame)
            public long pagesRenderedTotal;
        }

        VsmStats m_VsmStats;
        public VsmStats VirtualShadowMapStats => m_VsmStats;

        static class VsmIds
        {
            public static readonly int Entries = Shader.PropertyToID("VG_VsmEntries");
            public static readonly int Pages = Shader.PropertyToID("VG_VsmPages");
            public static readonly int Free = Shader.PropertyToID("VG_VsmFree");
            public static readonly int Counters = Shader.PropertyToID("VG_VsmCounters");
            public static readonly int Dirty = Shader.PropertyToID("VG_VsmDirty");
            public static readonly int List = Shader.PropertyToID("VG_VsmList");
            public static readonly int Args = Shader.PropertyToID("VG_VsmArgs");
            public static readonly int Spheres = Shader.PropertyToID("VG_VsmSpheres");
            public static readonly int Receivers = Shader.PropertyToID("VG_Receivers");
            public static readonly int HzbOut = Shader.PropertyToID("VG_VsmHzbOut");
            public static readonly int Pool = Shader.PropertyToID("_VgVsmPool");
            public static readonly int Params = Shader.PropertyToID("_VsmParams");
            public static readonly int Pyramid = Shader.PropertyToID("_VsmPyramid");
            public static readonly int Batch = Shader.PropertyToID("VG_VsmBatch");
            public static readonly int Draw = Shader.PropertyToID("_VgVsmDraw");
            public static readonly int Entry = Shader.PropertyToID("_VgVsmEntry");
            public static readonly int ZClip = Shader.PropertyToID("_VgZClip");
            public static readonly int CopySrc = Shader.PropertyToID("VG_VsmCopySrc");
            public static readonly int CopyDst = Shader.PropertyToID("VG_VsmCopyDst");
        }

        void InitVsm()
        {
            var cs = Resources.Load<ComputeShader>("UNanite/VgVsm");
            var raster = Shader.Find("Hidden/UNanite/VsmRaster");
            var composite = Shader.Find("Hidden/UNanite/VsmComposite");
            if (!HasKernels(cs, "Begin", "Release", "Invalidate", "Mark", "Finalize", "ClearPages", "DirtyPyramid", "CopySlots") ||
                raster == null || !raster.isSupported || composite == null || !composite.isSupported || !SystemInfo.supportsComputeShaders)
                return;
            m_VsmCs = cs;
            k_VsmBegin = cs.FindKernel("Begin");
            k_VsmRelease = cs.FindKernel("Release");
            k_VsmInvalidate = cs.FindKernel("Invalidate");
            k_VsmMark = cs.FindKernel("Mark");
            k_VsmFinalize = cs.FindKernel("Finalize");
            k_VsmClear = cs.FindKernel("ClearPages");
            k_VsmDirty = cs.FindKernel("DirtyPyramid");
            InitSunClipmap(cs); // M13b
            for (int i = 0; i < 4; ++i)
            {
                int list = i & 1;
                bool zClip = i < 2;
                var m = new Material(raster) { hideFlags = HideFlags.HideAndDontSave, name = $"UNanite VSM Raster ({(list == 0 ? "single" : "double")}-sided, z clip {(zClip ? "on" : "off")})" };
                m.SetFloat(ShadowIds.CullMode, list == 0 ? (float)CullMode.Back : (float)CullMode.Off);
                m.SetFloat(VsmIds.ZClip, zClip ? 1f : 0f);
                m_VsmRasterMaterials[i] = m;
            }
            m_VsmComposite = new Material(composite) { hideFlags = HideFlags.HideAndDontSave, name = "UNanite VSM Composite" };
            m_VsmCompositeId = m_BRG.RegisterMaterial(m_VsmComposite);
            m_VsmProps = new MaterialPropertyBlock();
            m_VsmSupported = true;
        }

        // GPU memory on the first cached split (pool = vsmPoolPages x 64 KB)
        bool EnsureVsmResources()
        {
            if (m_VsmPool != null)
                return true;
            m_VsmPoolPages = Mathf.Clamp(m_Settings.vsmPoolPages / k_VsmPoolRow * k_VsmPoolRow, k_VsmPoolRow, 128 * k_VsmPoolRow);
            int rows = m_VsmPoolPages / k_VsmPoolRow;
            m_VsmPool = new RenderTexture(new RenderTextureDescriptor(k_VsmPoolRow * k_VsmPage, rows * k_VsmPage, GraphicsFormat.R32_UInt, GraphicsFormat.None)
            {
                enableRandomWrite = true, msaaSamples = 1, useMipMap = false,
            })
            { name = "UNanite VSM page pool", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
            if (!m_VsmPool.Create())
            {
                Debug.LogWarning("UNanite: virtual shadow map page pool could not be created; virtual shadow maps are off.");
                CoreUtils.Destroy(m_VsmPool);
                m_VsmPool = null;
                m_VsmSupported = false;
                return false;
            }
            m_VsmEntries = new GraphicsBuffer(GraphicsBuffer.Target.Structured, k_VsmMaxEntries, k_VsmEntryFloats * 4);
            m_VsmTables = new VgRangeAllocator(1 << 16); // 1 MB of slots: the M13b clipmap alone takes 12 x 65^2
            m_VsmPages = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_VsmTables.Capacity, 16);
            m_VsmList = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_VsmTables.Capacity, 4);
            m_VsmFree = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_VsmPoolPages + 1, 4);
            m_VsmCounters = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 16 + k_VsmMaxEntries, 4);
            m_VsmDirty = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_VsmPoolPages, 4);
            m_VsmArgs = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.IndirectArguments, 4 + 4 * k_VsmMaxEntries, 4);
            m_VsmArgs.SetData(new uint[m_VsmArgs.count]);
            m_VsmSpheres = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1024, 16);
            m_VsmHzb = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_ReceiverTexels * k_ReceiverBatch, 4);
            m_VsmBatch = new GraphicsBuffer(GraphicsBuffer.Target.Structured, k_VsmMaxEntries, 16);
            ResetVsmPool();
            // bounds of every caster so far: the first move of each one also clears where it was
            m_VsmCasterSpheres = new Vector4[Mathf.Max(256, m_InstanceHighWater)];
            for (int h = 0; h < m_InstanceHighWater; ++h)
            {
                var inst = m_Instances[h];
                if ((inst.flags & VgInstanceGpu.FlagEnabled) != 0 && (inst.flags & VgInstanceGpu.FlagShadows) != 0)
                    m_VsmCasterSpheres[h] = inst.worldSphere;
            }
            m_VsmTouched.Clear();
            BindVsmGlobals();
            return true;
        }

        // every page free, every table slot empty (also after an overflow or a device reset)
        void ResetVsmPool()
        {
            var free = new uint[m_VsmPoolPages + 1];
            free[0] = (uint)m_VsmPoolPages;
            for (int i = 0; i < m_VsmPoolPages; ++i)
                free[1 + i] = (uint)(m_VsmPoolPages - 1 - i);
            m_VsmFree.SetData(free);
            m_VsmPages.SetData(EmptyVsmSlots(m_VsmPages.count));
            foreach (var e in m_VsmSlots)
                if (e != null)
                    e.hasAnchor = false;
            m_VsmEpoch++;
        }

        static uint[] EmptyVsmSlots(int count)
        {
            var data = new uint[count * 4];
            for (int i = 0; i < count; ++i)
            {
                data[4 * i + 0] = 0xFFFFu;
                data[4 * i + 1] = 0xFFFFFFFFu;
            }
            return data;
        }

        void DisposeVsm()
        {
            VgSunGlobals.BindDummies(); // M13b: never leave released buffers bound for the patched HDRP
            ReleaseRetiredVsm();
            foreach (var b in new[] { m_VsmEntries, m_VsmPages, m_VsmFree, m_VsmCounters, m_VsmDirty, m_VsmList, m_VsmArgs, m_VsmSpheres, m_VsmHzb, m_VsmBatch })
                b?.Dispose();
            m_VsmEntries = m_VsmPages = m_VsmFree = m_VsmCounters = m_VsmDirty = m_VsmList = m_VsmArgs = m_VsmSpheres = m_VsmHzb = m_VsmBatch = null;
            foreach (var rt in new[] { m_VsmPool, m_VsmDummy })
                if (rt != null)
                {
                    rt.Release();
                    CoreUtils.Destroy(rt);
                }
            m_VsmPool = m_VsmDummy = null;
            for (int i = 0; i < 4; ++i)
                if (m_VsmRasterMaterials[i] != null)
                    CoreUtils.Destroy(m_VsmRasterMaterials[i]);
            if (m_VsmComposite != null)
                CoreUtils.Destroy(m_VsmComposite);
            System.Array.Clear(m_VsmSlots, 0, m_VsmSlots.Length);
            m_VsmLookup.Clear();
            m_VsmSupported = false;
        }

        bool VsmEnabled => m_VsmSupported && m_Settings.virtualShadowMaps
#if UNANITE_HDRP
                           && VgHdrpShadowRequests.Available
#endif
                           ;

        // ---------------------------------------------------------------------------------------
        // Invalidation: casters that moved, appeared or disappeared

        // MarkDirty hook: every instance change of the frame
        void VsmTouch(int handle)
        {
            if (m_VsmPool != null)
                m_VsmTouched.Add(handle);
        }

        // Frame start (after the trackers and the instance upload): spheres of the casters whose
        // bounds changed, old and new, for the Invalidate kernel.
        readonly List<GraphicsBuffer> m_VsmRetired = new List<GraphicsBuffer>();
        readonly List<RenderTexture> m_VsmRetiredTextures = new List<RenderTexture>();

        void ReleaseRetiredVsm()
        {
            foreach (var b in m_VsmRetired)
                b.Dispose();
            m_VsmRetired.Clear();
            foreach (var rt in m_VsmRetiredTextures)
            {
                rt.Release();
                CoreUtils.Destroy(rt);
            }
            m_VsmRetiredTextures.Clear();
        }

        void BeginVsmFrame()
        {
            m_VsmFrame++;
            ReleaseRetiredVsm();
            m_VsmSphereList.Clear();
            if (m_VsmPool == null)
            {
                m_VsmTouched.Clear();
                return;
            }
            if (m_VsmCasterSpheres.Length < m_InstanceHighWater)
                System.Array.Resize(ref m_VsmCasterSpheres, Mathf.Max(m_InstanceHighWater, m_VsmCasterSpheres.Length * 2));
            foreach (int h in m_VsmTouched)
            {
                if (h < 0 || h >= m_VsmCasterSpheres.Length)
                    continue;
                var old = m_VsmCasterSpheres[h];
                var cur = Vector4.zero;
                if (h < m_InstanceHighWater)
                {
                    var inst = m_Instances[h];
                    if ((inst.flags & VgInstanceGpu.FlagEnabled) != 0 && (inst.flags & VgInstanceGpu.FlagShadows) != 0)
                        cur = inst.worldSphere;
                }
                if (old == cur)
                    continue;
                if (old.w > 0f)
                    m_VsmSphereList.Add(old);
                if (cur.w > 0f)
                    m_VsmSphereList.Add(cur);
                m_VsmCasterSpheres[h] = cur;
            }
            m_VsmTouched.Clear();
            if (m_VsmStreamedMeshes.Count > 0)
            {
                for (int h = 0; h < m_InstanceHighWater; ++h)
                {
                    var inst = m_Instances[h];
                    if ((inst.flags & VgInstanceGpu.FlagEnabled) != 0 && (inst.flags & VgInstanceGpu.FlagShadows) != 0 &&
                        m_VsmStreamedMeshes.Contains((int)inst.meshIndex))
                        m_VsmSphereList.Add(inst.worldSphere);
                }
                m_VsmStreamedMeshes.Clear();
            }
            if (m_VsmSphereList.Count > k_VsmMaxSpheres)
            {
                m_VsmSphereList.Clear();
                m_VsmResetAll = true; // too many at once: start over
            }
            m_VsmSphereCount = m_VsmSphereList.Count;
            if (m_VsmSphereCount > 0)
            {
                if (m_VsmSpheres.count < m_VsmSphereCount)
                {
                    m_VsmSpheres.Dispose();
                    m_VsmSpheres = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.NextPowerOfTwo(m_VsmSphereCount), 16);
                }
                m_VsmSpheres.SetData(m_VsmSphereList);
            }
            if (m_VsmResetAll)
            {
                m_VsmResetAll = false;
                ResetVsmPool();
            }
            else if (m_VsmSphereCount > 0 && m_VsmCs != null)
            {
                // every entry with a table, with the matrices it was last rendered with (still in the buffer)
                m_Cmd.Clear();
                m_Cmd.SetComputeBufferParam(m_VsmCs, k_VsmInvalidate, VsmIds.Entries, m_VsmEntries);
                m_Cmd.SetComputeBufferParam(m_VsmCs, k_VsmInvalidate, VsmIds.Pages, m_VsmPages);
                m_Cmd.SetComputeBufferParam(m_VsmCs, k_VsmInvalidate, VsmIds.Spheres, m_VsmSpheres);
                int n = 0;
                for (int i = 0; i < k_VsmMaxEntries; ++i)
                    if (m_VsmSlots[i] != null && m_VsmSlots[i].tableBase >= 0)
                        PutVsmBatch(n++, i, 0);
                if (n > 0)
                {
                    SetVsmBatch(m_Cmd, n, k_VsmInvalidate);
                    m_Cmd.SetComputeIntParams(m_VsmCs, VsmIds.Params, n, m_VsmFrame, m_VsmSphereCount, m_VsmPoolPages);
                    m_Cmd.DispatchCompute(m_VsmCs, k_VsmInvalidate, (m_VsmSphereCount + 63) / 64, n, 1);
                }
                Graphics.ExecuteCommandBuffer(m_Cmd);
                m_Cmd.Clear();
            }
            BindVsmGlobals();
            ExpireVsmEntries();
        }

        // read by the page raster and the composite draws; also bound on the first frame, whose
        // resources are created during culling (after the frame start)
        void BindVsmGlobals()
        {
            Shader.SetGlobalBuffer(VsmIds.Entries, m_VsmEntries);
            Shader.SetGlobalBuffer(VsmIds.Pages, m_VsmPages);
            Shader.SetGlobalBuffer(VsmIds.List, m_VsmList);
            Shader.SetGlobalTexture(VsmIds.Pool, m_VsmPool);
            VgSunGlobals.Bind(m_VsmEntries, m_VsmPages, m_VsmPool); // M13b: the HDRP patch binds them in every lighting draw
        }

        /// <summary>M13: drops every cached shadow page (after edits the invalidation does not see, e.g. material changes).</summary>
        public void InvalidateVirtualShadowMaps() => m_VsmResetAll = true;

        // streaming commits change LODs of cached casters: the content of every page is stale
        // a streamed page committed: finer LODs of its mesh's casters; their pages are invalidated at the
        // next frame start (the bounds of every shadow-casting instance of the mesh)
        readonly HashSet<int> m_VsmStreamedMeshes = new HashSet<int>();

        void VsmOnPageCommitted(int page)
        {
            if (m_VsmPool != null && page >= 0 && page < m_PageMesh.Length && m_PageMesh[page] >= 0)
                m_VsmStreamedMeshes.Add(m_PageMesh[page]);
        }

        // entries unused for a while give their pages and table back
        void ExpireVsmEntries()
        {
            int maxAge = Mathf.Max(8, m_Settings.vsmPageMaxAge);
            for (int i = 0; i < k_VsmMaxEntries; ++i)
            {
                var e = m_VsmSlots[i];
                if (e == null || m_VsmFrame - e.lastCulled <= maxAge)
                    continue;
                if (e.tableBase >= 0)
                {
                    // release its pages on the GPU now (entry data: reset flag)
                    e.reset = true;
                    WriteVsmEntry(e);
                    m_Cmd.Clear();
                    m_Cmd.SetBufferData(m_VsmEntries, m_VsmEntryData, e.index * k_VsmEntryFloats, e.index * k_VsmEntryFloats, k_VsmEntryFloats);
                    DispatchVsmRelease(m_Cmd, e);
                    Graphics.ExecuteCommandBuffer(m_Cmd);
                    m_Cmd.Clear();
                    m_VsmTables.Free(e.tableBase, e.tableW * e.tableW);
                }
                m_VsmLookup.Remove((e.lightId, e.split, e.camera));
                m_VsmSlots[i] = null;
            }
        }

        // ---------------------------------------------------------------------------------------
        // Culling time

        // A receiver-culled shadow-raster split of a light becomes (or keeps) a cache entry.
        int VsmEntryFor(in SubView sv, in BatchCullingContext ctx, int splitIndex)
        {
            if (!VsmEnabled || !sv.receiverCulling || !sv.shadowRaster || sv.camera == null || !EnsureVsmResources())
                return -1;
            var key = (ctx.viewID.GetInstanceID(), splitIndex, sv.camera);
            if (!m_VsmLookup.TryGetValue(key, out var e))
            {
                int free = System.Array.IndexOf(m_VsmSlots, null);
                if (free < 0)
                    return -1;
                e = new VsmEntry { index = free, lightId = key.Item1, split = splitIndex, camera = sv.camera };
                m_VsmSlots[free] = e;
                m_VsmLookup[key] = e;
            }
            e.lastCulled = m_VsmFrame;
            e.resolved = false;
            e.cullVP = sv.lightViewProj;
            return e.index;
        }

        void RecordVsmPlaceholder(CommandBuffer cmd, in SubView v)
        {
            // composite draw empty and cached slot empty unless the custom pass runs
            cmd.SetBufferData(m_VsmArgs, s_Zero4, 0, 4 + 4 * v.VsmEntry, 4);
            var cached = v;
            cached.flags |= k_ViewFlagOnlyRaster;
            SetViewConstants(cmd, cached, v.vsmSlot, 0);
            BindAll(cmd, k_ClearShadowView);
            cmd.DispatchCompute(m_Cull, k_ClearShadowView, 1, 1, 1);
        }

        int VsmCompositeDrawCount(in SubView v) => v.VsmEntry >= 0 ? 1 : 0;

        unsafe int EmitVsmCompositeDraw(BatchDrawCommandProceduralIndirect* procedural, int cp, in SubView v, uint windowSize)
        {
            procedural[cp++] = new BatchDrawCommandProceduralIndirect
            {
                flags = BatchDrawCommandFlags.None,
                batchID = m_BatchID,
                materialID = m_VsmCompositeId,
                topology = MeshTopology.Triangles,
                splitVisibilityMask = v.splitMask,
                lightmapIndex = 0xFFFF,
                sortingPosition = 0,
                visibleOffset = (uint)v.VsmEntry, // -> the cache entry (VgVsmComposite.shader)
                visibleInstancesBufferHandle = m_VisibleInstances.bufferHandle,
                visibleInstancesBufferWindowOffset = 0,
                visibleInstancesBufferWindowSizeBytes = windowSize,
                indirectArgsBufferHandle = m_VsmArgs.bufferHandle,
                indirectArgsBufferOffset = (uint)((4 + 4 * v.VsmEntry) * 4),
            };
            return cp;
        }

#if UNANITE_HDRP
        // ---------------------------------------------------------------------------------------
        // Custom pass: resolve the entries of a batch against HDRP's requests

        readonly List<VgHdrpShadowRequests.Request> m_HdrpRequests = new List<VgHdrpShadowRequests.Request>();
        int m_HdrpRequestsFrame = -1;
        Camera m_HdrpRequestsCamera;

        // Fills the entry's frame data from HDRP's request of its split; false = not found (the split
        // then renders like before: per-frame culling of every bin, no composite).
        bool ResolveVsmEntry(VsmEntry e, Camera camera)
        {
            if (m_HdrpRequestsFrame != m_VsmFrame || m_HdrpRequestsCamera != camera)
            {
                VgHdrpShadowRequests.Read(m_HdrpRequests);
                m_HdrpRequestsFrame = m_VsmFrame;
                m_HdrpRequestsCamera = camera;
            }
            var camPos = camera.transform.position;
            var toRelative = Matrix4x4.Translate(-camPos);
            float best = 1e-3f;
            int found = -1;
            Matrix4x4 bestView = default;
            for (int i = 0; i < m_HdrpRequests.Count; ++i)
            {
                var r = m_HdrpRequests[i];
                if (!r.valid || r.splitIndex != e.split || r.viewportSize.x < 256f)
                    continue;
                var unflip = r.deviceProjectionYFlip;
                unflip.SetRow(1, -unflip.GetRow(1));
                for (int v = 0; v < 2; ++v)
                {
                    var view = v == 0 ? r.view * toRelative : r.view; // camera-relative views hold T(camera)
                    float err = Mathf.Min(MatrixError(r.projection * view, e.cullVP), MatrixError(unflip * view, e.cullVP));
                    if (err < best)
                    {
                        best = err;
                        found = i;
                        bestView = view;
                    }
                }
            }
            if (found < 0)
            {
                LastVsmDecision = $"split {e.split} of light {e.lightId}: no matching HDRP shadow request ({m_HdrpRequests.Count} read)";
                return false;
            }
            var req = m_HdrpRequests[found];
            int res = Mathf.RoundToInt(req.viewportSize.x);
            if (res != Mathf.RoundToInt(req.viewportSize.y) || res % k_VsmPage != 0)
            {
                LastVsmDecision = $"split {e.split} of light {e.lightId}: resolution {req.viewportSize} not a multiple of {k_VsmPage}";
                return false;
            }
            var vp = req.deviceProjectionYFlip * bestView;
            bool ortho = Mathf.Abs(vp.m30) < 1e-7f && Mathf.Abs(vp.m31) < 1e-7f && Mathf.Abs(vp.m32) < 1e-7f;
            int W = res / k_VsmPage + (ortho ? 1 : 0);

            // reuse, scroll or reset
            string why = !e.hasAnchor ? "new" : e.res != res ? "resolution" : e.ortho != ortho ? "projection" : e.zClip != req.zClip ? "z clip" :
                         !Mathf.Approximately(e.slopeBias, req.slopeBias) ? "slope bias" : null;
            var window = Vector2Int.zero;
            var frac = Vector2.zero;
            float a = 1f, b = 0f;
            if (why == null && ortho)
                why = CascadeWindow(e.anchorVP, vp, res, out window, out frac, out a, out b);
            else if (why == null && MatrixError(e.anchorCullVP, e.cullVP) > 1e-6f)
                why = "light moved"; // the light (or its cone) moved
            bool reset = why != null;
            if (reset)
                CountVsmReset(why);
            if (reset)
            {
                e.anchorCullVP = e.cullVP;
                e.anchorVP = vp;
                e.hasAnchor = true;
                window = Vector2Int.zero;
                frac = Vector2.zero;
                a = 1f;
                b = 0f;
            }
            e.reset = reset && e.tableBase >= 0;
            e.res = res;
            e.ortho = ortho;
            e.zClip = req.zClip;
            e.slopeBias = req.slopeBias;
            // HDRP does not snap cascades to texels: the pages are rendered on the anchor's texel grid
            // (the render matrix moved by the sub-texel offset; the composite samples them with it)
            if (frac != Vector2.zero)
            {
                vp.SetRow(0, vp.GetRow(0) + new Vector4(0f, 0f, 0f, 2f * frac.x / res));
                vp.SetRow(1, vp.GetRow(1) - new Vector4(0f, 0f, 0f, 2f * frac.y / res));
            }
            e.renderVP = vp;
            e.window = window;
            e.frac = frac;
            e.depthA = a;
            e.depthB = b;
            e.projScale = new Vector3(vp.m00, vp.m01, vp.m02).magnitude;
            if (!ortho)
            {
                var eye = vp.inverse * new Vector4(0f, 0f, 1f, 0f); // the eye maps to clip (0, 0, z, 0)
                e.eye = Mathf.Abs(eye.w) > 1e-12f ? (Vector3)eye / eye.w : Vector3.zero;
            }
            if (e.tableBase >= 0 && e.tableW != W)
                e.reset = true; // a new table: the old pages are released first, with the old layout
            e.W = W;
            e.resolved = true;
            LastVsmDecision = $"split {e.split} of light {e.lightId}: {(ortho ? "cascade" : "perspective")} {res}, window {e.window}{(reset ? ", reset: " + why : "")}";
            return true;
        }

        static float MatrixError(in Matrix4x4 a, in Matrix4x4 b)
        {
            float err = 0f, scale = 1e-6f;
            for (int i = 0; i < 16; ++i)
            {
                err = Mathf.Max(err, Mathf.Abs(a[i] - b[i]));
                scale = Mathf.Max(scale, Mathf.Abs(b[i]));
            }
            return err / scale;
        }

        // Directional cascades follow the camera in texel steps: the same rotation and texel size keep
        // the anchor's global texel grid (window = integer texel offset) and its depth up to an affine map.
        // null = the pages can be reused (window = scroll), else why the entry starts over
        static string CascadeWindow(in Matrix4x4 anchor, in Matrix4x4 current, int res, out Vector2Int window, out Vector2 frac, out float a, out float b)
        {
            window = Vector2Int.zero;
            frac = Vector2.zero;
            a = 1f;
            b = 0f;
            // a row difference d moves the map's edge (half a map from its centre) by |d| / |row| * res / 2
            // texels: up to 0.1 texel is float noise of HDRP's camera-relative matrices far from the origin
            float tolerance = 0.2f / Mathf.Max(1, res);
            for (int r = 0; r < 2; ++r)
            {
                Vector3 ra = anchor.GetRow(r), rc = current.GetRow(r);
                if ((ra - rc).magnitude > tolerance * ra.magnitude)
                {
                    if (r == 0 && Mathf.Abs(ra.magnitude - rc.magnitude) > tolerance * ra.magnitude)
                    {
                        float ratio = rc.magnitude / ra.magnitude;
                        s_SizeRatioMin = Mathf.Min(s_SizeRatioMin, ratio);
                        s_SizeRatioMax = Mathf.Max(s_SizeRatioMax, ratio);
                        return "cascade size";
                    }
                    return "light rotated";
                }
            }
            Vector3 za = anchor.GetRow(2), zc = current.GetRow(2);
            float s = Vector3.Dot(zc, za) / Mathf.Max(za.sqrMagnitude, 1e-20f);
            if ((zc - s * za).magnitude > tolerance * zc.magnitude)
            {
                s_ZDevMax = Mathf.Max(s_ZDevMax, (zc - s * za).magnitude / zc.magnitude);
                s_ZDevMin = Mathf.Min(s_ZDevMin, (zc - s * za).magnitude / zc.magnitude);
                return "depth axis";
            }
            a = s;
            b = current.m23 - s * anchor.m23;
            // texel offset of one world point between the two frames
            var p = current.inverse.MultiplyPoint(new Vector3(0f, 0f, 0.5f));
            var ta = LocalTexel(anchor, p, res);
            var tc = LocalTexel(current, p, res);
            // anchor texel = current texel + d = current texel + frac + window
            var d = ta - tc;
            if (Mathf.Abs(d.x) > 1 << 20 || Mathf.Abs(d.y) > 1 << 20)
                return "far scroll";
            var floor = new Vector2(Mathf.Floor(d.x), Mathf.Floor(d.y));
            frac = d - floor;
            if (frac.x < 1e-3f || frac.x > 1f - 1e-3f)
                frac.x = 0f;
            if (frac.y < 1e-3f || frac.y > 1f - 1e-3f)
                frac.y = 0f;
            window = new Vector2Int(Mathf.RoundToInt(d.x - frac.x), Mathf.RoundToInt(d.y - frac.y));
            // the global page keys hold +-32768 pages
            return Mathf.Abs(window.x) / k_VsmPage < 30000 && Mathf.Abs(window.y) / k_VsmPage < 30000 ? null : "far scroll";
        }

        readonly Dictionary<string, int> m_VsmResetReasons = new Dictionary<string, int>();
        static float s_SizeRatioMin = float.MaxValue, s_SizeRatioMax, s_ZDevMin = float.MaxValue, s_ZDevMax;

        void CountVsmReset(string why)
        {
            m_VsmResetReasons.TryGetValue(why, out int n);
            m_VsmResetReasons[why] = n + 1;
        }

        /// <summary>M13 diagnostics: how often cache entries started over, per reason, since the world was created.</summary>
        public string VsmResetReasons
        {
            get
            {
                var sb = new System.Text.StringBuilder();
                if (s_SizeRatioMax > 0f)
                    sb.Append($"cascade size ratio {s_SizeRatioMin:F5}..{s_SizeRatioMax:F5}; ");
                if (s_ZDevMax > 0f)
                    sb.Append($"depth axis deviation {s_ZDevMin:E2}..{s_ZDevMax:E2}; ");
                foreach (var kv in m_VsmResetReasons)
                    sb.Append(kv.Key).Append(' ').Append(kv.Value).Append("; ");
                return sb.ToString();
            }
        }

        static Vector2 LocalTexel(in Matrix4x4 vp, Vector3 p, int res)
        {
            var c = vp * new Vector4(p.x, p.y, p.z, 1f);
            return new Vector2((c.x / c.w * 0.5f + 0.5f) * res, (0.5f - c.y / c.w * 0.5f) * res);
        }

        // cull NDC <-> local texel affine maps (the receiver / dirty pyramids use the culling matrix)
        static void CullLocalMaps(in Matrix4x4 cull, in Matrix4x4 render, int res, out Vector4 c2l0, out Vector4 c2l1, out Vector4 l2c0, out Vector4 l2c1)
        {
            var inv = cull.inverse;
            var vp = render;
            Vector2 T(float x, float y)
            {
                var w = inv * new Vector4(x, y, 0.5f, 1f);
                return LocalTexel(vp, (Vector3)w / w.w, res);
            }
            Vector2 o = T(0f, 0f), dx = T(1f, 0f) - o, dy = T(0f, 1f) - o;
            c2l0 = new Vector4(dx.x, dy.x, o.x, 0f);
            c2l1 = new Vector4(dx.y, dy.y, o.y, 0f);
            // inverse of [dx dy] (2x2) and offset
            float det = dx.x * dy.y - dy.x * dx.y;
            if (Mathf.Abs(det) < 1e-20f)
                det = 1e-20f;
            float i00 = dy.y / det, i01 = -dy.x / det, i10 = -dx.y / det, i11 = dx.x / det;
            l2c0 = new Vector4(i00, i01, -(i00 * o.x + i01 * o.y), 0f);
            l2c1 = new Vector4(i10, i11, -(i10 * o.x + i11 * o.y), 0f);
        }

        void WriteVsmEntry(VsmEntry e)
        {
            int o = e.index * k_VsmEntryFloats;
            var d = m_VsmEntryData;
            for (int r = 0; r < 4; ++r)
                Put4(d, o + 4 * r, e.renderVP.GetRow(r));
            var anchorRow = e.ortho ? e.anchorVP.GetRow(2) : Vector4.zero;
            Put4(d, o + 16, anchorRow);
            int flags = (e.ortho ? 1 : 0) | (e.zClip ? 2 : 0) | (e.reset ? 4 : 0);
            PutInt4(d, o + 20, e.window.x, e.window.y, e.res, flags);
            PutInt4(d, o + 24, e.tableBase, e.tableW, (int)m_VsmEpoch, Mathf.Max(8, m_Settings.vsmPageMaxAge));
            Put4(d, o + 28, new Vector4(e.depthA, e.depthB, e.slopeBias, m_VsmFrame));
            CullLocalMaps(e.cullVP, e.renderVP, Mathf.Max(1, e.res), out var c0, out var c1, out var l0, out var l1);
            c0.w = e.frac.x; // composite: cached texel = floor(atlas texel + frac) + window
            c1.w = e.frac.y;
            Put4(d, o + 32, c0);
            Put4(d, o + 36, c1);
            Put4(d, o + 40, l0);
            Put4(d, o + 44, l1);
        }

        static void Put4(float[] d, int o, Vector4 v)
        {
            d[o] = v.x;
            d[o + 1] = v.y;
            d[o + 2] = v.z;
            d[o + 3] = v.w;
        }

        static void PutInt4(float[] d, int o, int x, int y, int z, int w)
        {
            d[o] = System.BitConverter.Int32BitsToSingle(x);
            d[o + 1] = System.BitConverter.Int32BitsToSingle(y);
            d[o + 2] = System.BitConverter.Int32BitsToSingle(z);
            d[o + 3] = System.BitConverter.Int32BitsToSingle(w);
        }

        // one entry's pages (reset, expiry)
        void DispatchVsmRelease(CommandBuffer cmd, VsmEntry e)
        {
            PutVsmBatch(0, e.index, 0);
            SetVsmBatch(cmd, 1, k_VsmRelease);
            DispatchVsmRelease(cmd, 1, e.tableW);
        }

        // the entries of VG_VsmBatch (`count`, tables of at most maxW x maxW slots)
        void DispatchVsmRelease(CommandBuffer cmd, int count, int maxW)
        {
            cmd.SetComputeIntParams(m_VsmCs, VsmIds.Params, count, m_VsmFrame, 0, m_VsmPoolPages);
            cmd.SetComputeBufferParam(m_VsmCs, k_VsmRelease, VsmIds.Entries, m_VsmEntries);
            cmd.SetComputeBufferParam(m_VsmCs, k_VsmRelease, VsmIds.Pages, m_VsmPages);
            cmd.SetComputeBufferParam(m_VsmCs, k_VsmRelease, VsmIds.Free, m_VsmFree);
            cmd.DispatchCompute(m_VsmCs, k_VsmRelease, (maxW * maxW + 63) / 64, 1, count);
        }

        void PutVsmBatch(int i, int entry, int pyramid)
        {
            m_VsmBatchData[4 * i + 0] = (uint)entry;
            m_VsmBatchData[4 * i + 1] = (uint)pyramid;
        }

        // uploads the first `count` batch rows (copied when recorded: later uploads do not change it)
        void SetVsmBatch(CommandBuffer cmd, int count, params int[] kernels)
        {
            cmd.SetBufferData(m_VsmBatch, m_VsmBatchData, 0, 0, 4 * count);
            foreach (int k in kernels)
                cmd.SetComputeBufferParam(m_VsmCs, k, VsmIds.Batch, m_VsmBatch);
        }

        /// <summary>
        /// Custom pass, after the receiver pyramids of a batch of deferred splits: resolves the batch's
        /// cache entries, updates their pages, culls and rasterises the dirty ones. Views whose entry is
        /// active get VIEW_FLAG_SKIP_RASTER for their per-frame culling (the caller's RecordBatch).
        /// </summary>
        void RenderVsmBatch(CommandBuffer cmd, Camera camera, List<(SubView view, int slot)> pending, int first, int count)
        {
            var active = new List<(SubView view, int slot, VsmEntry entry)>();
            for (int i = 0; i < count; ++i)
            {
                var (view, slot) = pending[first + i];
                if (view.VsmEntry < 0)
                    continue;
                var e = m_VsmSlots[view.VsmEntry];
                if (e == null || !ResolveVsmEntry(e, camera))
                    continue;
                active.Add((view, slot, e));
            }
            if (active.Count == 0)
                return;

            cmd.BeginSample(VsmSampler);
            // tables: release pages of reset entries with the old layout, then (re)allocate
            foreach (var (_, _, e) in active)
            {
                if (e.reset && e.tableBase >= 0)
                {
                    WriteVsmEntry(e);
                    cmd.SetBufferData(m_VsmEntries, m_VsmEntryData, e.index * k_VsmEntryFloats, e.index * k_VsmEntryFloats, k_VsmEntryFloats);
                    DispatchVsmRelease(cmd, e);
                    if (e.tableW != e.W)
                    {
                        m_VsmTables.Free(e.tableBase, e.tableW * e.tableW);
                        e.tableBase = -1;
                    }
                }
                e.reset = false;
                if (e.tableBase < 0)
                {
                    e.tableW = e.W;
                    int size = e.W * e.W;
                    e.tableBase = m_VsmTables.Allocate(size);
                    if (e.tableBase < 0)
                    {
                        GrowVsmTables(cmd, m_VsmTables.Capacity + size);
                        e.tableBase = m_VsmTables.Allocate(size);
                    }
                    cmd.SetBufferData(m_VsmPages, EmptyVsmSlots(size), 0, e.tableBase * 4, size * 4);
                }
                WriteVsmEntry(e);
                cmd.SetBufferData(m_VsmEntries, m_VsmEntryData, e.index * k_VsmEntryFloats, e.index * k_VsmEntryFloats, k_VsmEntryFloats);
            }

            // counters, release, mark: one dispatch each for the whole batch (entry = dispatch z)
            int n = active.Count, maxW = 0;
            for (int i = 0; i < n; ++i)
            {
                PutVsmBatch(i, active[i].entry.index, active[i].view.receiverBatchIndex);
                maxW = Mathf.Max(maxW, active[i].entry.W);
            }
            SetVsmBatch(cmd, n, k_VsmRelease, k_VsmMark, k_VsmFinalize, k_VsmDirty);
            cmd.SetComputeBufferParam(m_VsmCs, k_VsmBegin, VsmIds.Counters, m_VsmCounters);
            cmd.DispatchCompute(m_VsmCs, k_VsmBegin, (16 + k_VsmMaxEntries + 63) / 64, 1, 1);
            DispatchVsmRelease(cmd, n, maxW);
            var r = m_Receivers;
            cmd.SetComputeIntParams(m_VsmCs, VsmIds.Params, n, m_VsmFrame, 0, m_VsmPoolPages);
            cmd.SetComputeIntParams(m_VsmCs, VsmIds.Pyramid, m_ReceiverTexels, 0, r.width0, r.levels);
            foreach (var b in new[] { (VsmIds.Entries, m_VsmEntries), (VsmIds.Pages, m_VsmPages), (VsmIds.Free, m_VsmFree), (VsmIds.Counters, m_VsmCounters),
                                      (VsmIds.Dirty, m_VsmDirty), (VsmIds.List, m_VsmList), (VsmIds.Receivers, r.hzb) })
                cmd.SetComputeBufferParam(m_VsmCs, k_VsmMark, b.Item1, b.Item2);
            cmd.DispatchCompute(m_VsmCs, k_VsmMark, (maxW + 7) / 8, (maxW + 7) / 8, n);
            cmd.SetComputeBufferParam(m_VsmCs, k_VsmFinalize, VsmIds.Counters, m_VsmCounters);
            cmd.SetComputeBufferParam(m_VsmCs, k_VsmFinalize, VsmIds.Args, m_VsmArgs);
            cmd.DispatchCompute(m_VsmCs, k_VsmFinalize, 1, 1, 1);
            // clear the dirty pages
            cmd.SetComputeBufferParam(m_VsmCs, k_VsmClear, VsmIds.Dirty, m_VsmDirty);
            cmd.SetComputeTextureParam(m_VsmCs, k_VsmClear, VsmIds.Pool, m_VsmPool);
            cmd.DispatchCompute(m_VsmCs, k_VsmClear, m_VsmArgs, 0);

            // dirty pyramids (receiver layout) -> cached culling keeps casters over dirty pages only
            cmd.SetComputeBufferParam(m_VsmCs, k_VsmDirty, VsmIds.Entries, m_VsmEntries);
            cmd.SetComputeBufferParam(m_VsmCs, k_VsmDirty, VsmIds.Pages, m_VsmPages);
            cmd.SetComputeBufferParam(m_VsmCs, k_VsmDirty, VsmIds.HzbOut, m_VsmHzb);
            cmd.DispatchCompute(m_VsmCs, k_VsmDirty, (r.width0 + 7) / 8, (r.width0 + 7) / 8, n);
            BuildPyramidLevels(cmd, m_VsmHzb, count);
            cmd.EndSample(VsmSampler);

            // cached culling: shadow-raster bins only, over dirty pages, texel LOD (no camera LOD or
            // caster frustum: the pages serve any later receiver)
            var cached = new List<(SubView view, int slot)>();
            foreach (var (view, _, e) in active)
            {
                var v = view;
                v.flags |= k_ViewFlagOnlyRaster;
                v.planeCount = v.basePlaneCount;
                v.camLod = Vector4.zero;
                if (!e.ortho)
                {
                    v.position = e.eye;
                    v.lodA = 0f;
                    v.lodB = 0.5f * Mathf.Max(64, m_Settings.shadowResolution) * e.projScale / m_Settings.shadowTexelError;
                }
                cached.Add((v, view.vsmSlot));
            }
            m_HzbOverride = m_VsmHzb;
            if (m_Settings.batchShadowCulling && cached.Count > 1)
            {
                for (int s = 0; s < cached.Count; s += k_MaxBatchSplits)
                    RecordBatch(cmd, cached, s, Mathf.Min(k_MaxBatchSplits, cached.Count - s));
            }
            else
                foreach (var (v, slot) in cached)
                    RecordView(cmd, v, slot);
            m_HzbOverride = null;

            // the per-frame culling of these splits skips the shadow-raster bins
            for (int i = 0; i < count; ++i)
            {
                var (view, slot) = pending[first + i];
                if (view.VsmEntry < 0 || !m_VsmSlots[view.VsmEntry].resolved)
                    continue;
                view.flags |= k_ViewFlagSkipRaster;
                pending[first + i] = (view, slot);
            }

            // raster of the dirty pages (records of the cached slots); one target for the largest split
            int maxRes = 0;
            foreach (var (_, _, e) in active)
                maxRes = Mathf.Max(maxRes, e.res);
            EnsureVsmDummy(maxRes);
            cmd.BeginSample(VsmRasterSampler);
            cmd.SetRenderTarget(m_VsmDummy);
            cmd.SetRandomWriteTarget(1, m_VsmPool);
            foreach (var (view, _, e) in active)
            {
                e.rasterSlot = view.vsmSlot;
                e.pyramidIndex = view.receiverBatchIndex;
                cmd.SetViewport(new Rect(0, 0, e.res, e.res));
                for (int list = 0; list < 2; ++list)
                {
                    if (!m_AnyShadowBin[list])
                        continue;
                    m_VsmProps.Clear();
                    m_VsmProps.SetInteger(VsmIds.Draw, view.vsmSlot * 2 + list);
                    m_VsmProps.SetInteger(VsmIds.Entry, e.index);
                    var material = m_VsmRasterMaterials[(e.zClip ? 0 : 2) + list];
                    cmd.DrawProceduralIndirect(Matrix4x4.identity, material, 0, MeshTopology.Triangles, m_RasterArgs,
                        (view.vsmSlot * k_RasterArgsStride + list * 8) * 4, m_VsmProps);
                }
            }
            cmd.ClearRandomWriteTargets();
            cmd.EndSample(VsmRasterSampler);
            foreach (var (view, _, _) in active)
                m_VsmRasterSlots.Add(view.vsmSlot);
            RequestVsmStats(cmd);
        }

        void GrowVsmTables(CommandBuffer cmd, int capacity)
        {
            capacity = Mathf.NextPowerOfTwo(capacity);
            // keep the live slots: copy on the GPU through a temporary buffer
            var pages = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 16);
            var list = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 4);
            pages.SetData(EmptyVsmSlots(capacity));
            // CommandBuffer.CopyBuffer needs equal sizes: copy the old slots with a kernel
            int k = m_VsmCs.FindKernel("CopySlots");
            cmd.SetComputeIntParams(m_VsmCs, VsmIds.Params, m_VsmPages.count, 0, 0, 0);
            cmd.SetComputeBufferParam(m_VsmCs, k, VsmIds.CopySrc, m_VsmPages);
            cmd.SetComputeBufferParam(m_VsmCs, k, VsmIds.CopyDst, pages);
            cmd.DispatchCompute(m_VsmCs, k, (m_VsmPages.count + 63) / 64, 1, 1);
            // the command buffer still reads the old ones: released at the next frame start
            m_VsmRetired.Add(m_VsmPages);
            m_VsmRetired.Add(m_VsmList);
            m_VsmPages = pages;
            m_VsmList = list;
            m_VsmTables.Grow(capacity);
            Shader.SetGlobalBuffer(VsmIds.Pages, m_VsmPages);
            Shader.SetGlobalBuffer(VsmIds.List, m_VsmList);
            VgSunGlobals.Bind(m_VsmEntries, m_VsmPages, m_VsmPool); // the old pages buffer is released next frame
        }

        void EnsureVsmDummy(int res)
        {
            if (m_VsmDummy != null && m_VsmDummy.width >= res)
                return;
            if (m_VsmDummy != null)
                m_VsmRetiredTextures.Add(m_VsmDummy); // may be bound by recorded commands
            m_VsmDummy = new RenderTexture(res, res, 0, GraphicsFormat.R8_UNorm) { name = "UNanite VSM raster target", hideFlags = HideFlags.HideAndDontSave };
            m_VsmDummy.Create();
        }

        // levels 1.. of `count` pyramids whose level 0 is in `hzb` (VgShadowReceivers.compute Pyramid)
        void BuildPyramidLevels(CommandBuffer cmd, GraphicsBuffer hzb, int count)
        {
            var r = m_Receivers;
            cmd.SetComputeBufferParam(m_RcvCs, k_RcvPyramid, OccIds.HzbOut, hzb);
            cmd.SetComputeIntParams(m_RcvCs, ShadowIds.Level, 0, 0, 0, 0);
            cmd.DispatchCompute(m_RcvCs, k_RcvPyramid, r.width0 / k_ReceiverTile, r.height0 / k_ReceiverTile, count);
            cmd.SetComputeIntParams(m_RcvCs, ShadowIds.Level, 4, 0, 0, 0);
            cmd.DispatchCompute(m_RcvCs, k_RcvPyramid, 1, 1, count);
            cmd.SetComputeBufferParam(m_RcvCs, k_RcvPyramid, OccIds.HzbOut, r.hzb);
        }

        readonly Queue<AsyncGPUReadbackRequest> m_VsmReadbacks = new Queue<AsyncGPUReadbackRequest>();

        readonly List<int> m_VsmRasterSlots = new List<int>();

        void RequestVsmStats(CommandBuffer cmd)
        {
            if (m_VsmReadbackPending || !SystemInfo.supportsAsyncGPUReadback)
            {
                m_VsmRasterSlots.Clear();
                return;
            }
            m_VsmReadbackPending = true;
            var slots = m_VsmRasterSlots.ToArray();
            m_VsmRasterSlots.Clear();
            cmd.RequestAsyncReadback(m_ShadowViews, req =>
            {
                if (req.hasError)
                    return;
                var views = req.GetData<uint>();
                int records = 0;
                foreach (int slot in slots)
                    if (4 * slot + 2 < views.Length)
                        records += (int)(views[4 * slot + 1] + views[4 * slot + 2]);
                m_VsmStats.recordsRastered = records;
            });
            var counters = m_VsmCounters;
            var free = m_VsmFree;
            cmd.RequestAsyncReadback(counters, 3 * 4, 0, req =>
            {
                m_VsmReadbackPending = false;
                if (req.hasError || counters != m_VsmCounters)
                    return;
                var d = req.GetData<uint>();
                m_VsmStats.pagesRendered = (int)d[0];
                m_VsmStats.overflow = (int)d[1];
                m_VsmStats.pagesNeeded = (int)d[2];
                m_VsmStats.pagesRenderedTotal += d[0];
                m_VsmStats.poolPages = m_VsmPoolPages;
                int entries = 0;
                foreach (var e in m_VsmSlots)
                    if (e != null)
                        entries++;
                m_VsmStats.entries = entries;
                if (d[1] > 0)
                {
                    if (!m_VsmWarnedOverflow)
                        Debug.LogWarning($"UNanite: the virtual shadow map pool ({m_VsmPoolPages} pages) is full; cached shadows start over. Raise VirtualGeometrySettings.vsmPoolPages.");
                    m_VsmWarnedOverflow = true;
                    m_VsmResetAll = true;
                }
            });
            cmd.RequestAsyncReadback(free, 4, 0, req =>
            {
                if (!req.hasError && free == m_VsmFree)
                    m_VsmStats.poolFree = (int)req.GetData<uint>()[0];
            });
        }

        bool m_VsmWarnedOverflow;

        /// <summary>M13 diagnostics (synchronous GPU reads, tests only): per entry its pages and composite draw, and the pool content of its pages.</summary>
        public string DebugVsmDump()
        {
            if (m_VsmPool == null)
                return "VSM: no resources";
            var sb = new System.Text.StringBuilder();
            var pages = new uint[m_VsmPages.count * 4];
            m_VsmPages.GetData(pages);
            var args = new uint[m_VsmArgs.count];
            m_VsmArgs.GetData(args);
            var counters = new uint[m_VsmCounters.count];
            m_VsmCounters.GetData(counters);
            var list = new uint[m_VsmList.count];
            m_VsmList.GetData(list);
            var pool = AsyncGPUReadback.Request(m_VsmPool, 0);
            pool.WaitForCompletion();
            var texels = pool.hasError ? default : pool.GetData<uint>();
            int poolWidth = m_VsmPool.width;
            sb.Append($"VSM counters dirty {counters[0]} overflow {counters[1]} marked {counters[2]}; clear args {args[0]} {args[1]} {args[2]}\n");
            foreach (var e in m_VsmSlots)
            {
                if (e == null)
                    continue;
                int a = 4 + 4 * e.index;
                sb.Append($" entry {e.index} split {e.split} res {e.res} W {e.W}/{e.tableW} base {e.tableBase} ortho {e.ortho} zClip {e.zClip} window {e.window} depth a {e.depthA} b {e.depthB} slope {e.slopeBias} resolved {e.resolved}; composite args {args[a]} {args[a + 1]}; list count {counters[16 + e.index]}\n");
                if (e.rasterSlot >= 0)
                {
                    var rargs = new uint[k_RasterArgsStride];
                    m_RasterArgs.GetData(rargs, 0, e.rasterSlot * k_RasterArgsStride, k_RasterArgsStride);
                    var sview = new uint[4];
                    m_ShadowViews.GetData(sview, 0, e.rasterSlot * 4, 4);
                    sb.Append($"   raster slot {e.rasterSlot}: list 0 args {rargs[0]} {rargs[1]} {rargs[2]} {rargs[3]}, list 1 args {rargs[8]} {rargs[9]}; shadow view {sview[0]} {sview[1]} {sview[2]} {sview[3]}\n");
                }
                if (e.pyramidIndex >= 0)
                {
                    var hzb = new float[m_ReceiverTexels];
                    m_VsmHzb.GetData(hzb, 0, e.pyramidIndex * m_ReceiverTexels, m_ReceiverTexels);
                    var rcv = new float[m_ReceiverTexels];
                    m_Receivers.hzb.GetData(rcv, 0, e.pyramidIndex * m_ReceiverTexels, m_ReceiverTexels);
                    int w0 = m_Receivers.width0, dirty0 = 0, dirtyAll = 0, rcv0 = 0;
                    for (int t = 0; t < w0 * w0; ++t)
                    {
                        if (hzb[t] > -1e38f) dirty0++;
                        if (rcv[t] > -1e38f) rcv0++;
                    }
                    for (int t = 0; t < m_ReceiverTexels; ++t)
                        if (hzb[t] > -1e38f) dirtyAll++;
                    sb.Append($"   pyramid {e.pyramidIndex}: dirty texels level 0 {dirty0} of {w0 * w0}, all levels {dirtyAll} of {m_ReceiverTexels}; receiver texels level 0 {rcv0}\n");
                }
                if (e.tableBase < 0)
                    continue;
                int valid = 0, dirty = 0, used = 0;
                for (int s = 0; s < e.tableW * e.tableW; ++s)
                {
                    int i = (e.tableBase + s) * 4;
                    uint phys = pages[i] & 0xFFFFu;
                    if (phys == 0xFFFFu)
                        continue;
                    used++;
                    if ((pages[i] & (1u << 16)) != 0)
                        valid++;
                    if ((pages[i] & (1u << 17)) != 0)
                        dirty++;
                    if (texels.IsCreated && used <= 6)
                    {
                        int ox = (int)(phys % k_VsmPoolRow) * k_VsmPage, oy = (int)(phys / k_VsmPoolRow) * k_VsmPage;
                        int nonZero = 0;
                        uint max = 0, min = uint.MaxValue;
                        for (int y = 0; y < k_VsmPage; ++y)
                            for (int x = 0; x < k_VsmPage; ++x)
                            {
                                uint k = texels[(oy + y) * poolWidth + ox + x];
                                if (k == 0)
                                    continue;
                                nonZero++;
                                max = System.Math.Max(max, k);
                                min = System.Math.Min(min, k);
                            }
                        int kx = (int)(pages[i + 1] & 0xFFFFu) - 32768, ky = (int)(pages[i + 1] >> 16) - 32768;
                        sb.Append($"   slot {s} page ({kx},{ky}) phys {phys} flags {pages[i] >> 16:X} frame {pages[i + 2]} epoch {pages[i + 3]}: {nonZero} texels with casters, keys {min:X8}..{max:X8}\n");
                    }
                }
                sb.Append($"   {used} slots used, {valid} valid, {dirty} dirty\n");
            }
            return sb.ToString();
        }

        /// <summary>M13 diagnostics: how the last cached shadow split was resolved.</summary>
        public string LastVsmDecision { get; private set; }
#endif
    }
}
