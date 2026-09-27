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
    // M12: runtime virtual texture of VG terrains (VgRvt.hlsl, VgRvt.compute, VgRvtResidency.cs).
    //
    //   frame start (CPU)        feedback read back from earlier frames -> VgRvtResidency.Report;
    //                            Update picks this frame's bakes (+ page-table writes)
    //   visibility custom pass   first camera of a context render: bakes (the terrain resolve
    //                            material's VgRvtBake pass into the atlas tiles), page-table scatter,
    //                            feedback reset; every camera: VgRvt.compute Feedback over its
    //                            visibility buffer
    //   end of context render    AsyncGPUReadback of the request list
    //   HDRP GBuffer pass        terrain resolve with VG_TERRAIN_RVT samples the atlas
    public sealed partial class VgWorld
    {
        /// <summary>GPU/CPU sampler around the virtual-texture bakes, page updates and feedback ("VG.TerrainRvt").</summary>
        public static readonly CustomSampler RvtSampler = CustomSampler.Create("VG.TerrainRvt", true);

        const int k_RvtRequestCapacity = 8192;
        const int k_RvtUpdateCapacity = 4096;
        const int k_RvtMaxTerrains = 127;
        const int k_RvtPinnedMips = 2;

        sealed class RvtTerrain
        {
            public Material fallback;   // the bin's source material (key of the terrain's bins)
            public Material resolve;    // TerrainLitResolve material (bakes, resolve keyword)
            public Vector2 origin, size;
            public int k;               // log2 virtual size
            public int pageBase, pageCount;
            public int residencyIndex = -1; // GPU / residency terrain index while active, -1 = inactive
        }

        ComputeShader m_RvtCs;
        int k_RvtReset = -1, k_RvtFeedback = -1, k_RvtScatter = -1;
        RenderTexture m_RvtAlbedo, m_RvtNormal;
        readonly RenderTargetIdentifier[] m_RvtTargets = new RenderTargetIdentifier[2];
        int m_RvtAtlasTiles, m_RvtAtlasSize;
        GraphicsBuffer m_RvtPageTable, m_RvtBits, m_RvtRequests, m_RvtUpdates, m_RvtTerrainBuffer, m_BinRvtBuffer;
        uint[] m_RvtPageMirror = new uint[0];
        VgRangeAllocator m_RvtPages;
        VgRvtResidency m_RvtResidency;
        readonly Dictionary<int, RvtTerrain> m_RvtTerrains = new Dictionary<int, RvtTerrain>(); // residency index -> active terrain
        readonly Dictionary<int, RvtTerrain> m_RvtHandles = new Dictionary<int, RvtTerrain>();  // handle -> registered terrain
        int m_RvtNextHandle;
        readonly List<VgRvtResidency.Bake> m_RvtBakes = new List<VgRvtResidency.Bake>();
        readonly List<Vector2Int> m_RvtWrites = new List<Vector2Int>();
        readonly Queue<(int frame, AsyncGPUReadbackRequest request)> m_RvtReadbacks = new Queue<(int, AsyncGPUReadbackRequest)>();
        uint[] m_BinRvt = new uint[0];
        bool m_RvtTerrainsDirty, m_RvtFeedbackThisContext, m_RvtUploadsDone;
        int m_RvtFrame;

        /// <summary>M12 statistics of the last frame (diagnostics / measurements).</summary>
        public struct RvtStats
        {
            public int terrains, residentTiles, capacity, pendingRequests, bakesLastFrame, evictionsLastFrame, reportsLastFrame;
            public long bakedTotal;
        }

        RvtStats m_RvtStats;
        public RvtStats TerrainRvtStats => m_RvtStats;

        static class RvtIds
        {
            public static readonly int Albedo = Shader.PropertyToID("_VgRvtAlbedo");
            public static readonly int Normal = Shader.PropertyToID("_VgRvtNormal");
            public static readonly int Atlas = Shader.PropertyToID("_VgRvtAtlas");
            public static readonly int PageTable = Shader.PropertyToID("VG_RvtPageTable");
            public static readonly int PageTableOut = Shader.PropertyToID("VG_RvtPageTableOut");
            public static readonly int Bits = Shader.PropertyToID("VG_RvtBits");
            public static readonly int Requests = Shader.PropertyToID("VG_RvtRequests");
            public static readonly int Updates = Shader.PropertyToID("VG_RvtUpdates");
            public static readonly int Terrains = Shader.PropertyToID("VG_RvtTerrains");
            public static readonly int BinRvt = Shader.PropertyToID("VG_BinRvt");
            public static readonly int InvViewProj = Shader.PropertyToID("_RvtInvViewProj");
            public static readonly int Screen = Shader.PropertyToID("_RvtScreen");
            public static readonly int Params = Shader.PropertyToID("_RvtParams");
            public static readonly int BakeTile = Shader.PropertyToID("_VgRvtBakeTile");
            public static readonly int BakeUV = Shader.PropertyToID("_VgRvtBakeUV");
            public static readonly int TerrainParams = Shader.PropertyToID("_VgRvtParams");
        }

        bool m_RvtActive; // settings.terrainVirtualTexture as applied to the terrains' materials

        void InitRvt()
        {
            var cs = Resources.Load<ComputeShader>("UNanite/VgRvt");
            if (!HasKernels(cs, "ResetFeedback", "Feedback", "ScatterPages"))
                return;
            m_RvtCs = cs;
            k_RvtReset = cs.FindKernel("ResetFeedback");
            k_RvtFeedback = cs.FindKernel("Feedback");
            k_RvtScatter = cs.FindKernel("ScatterPages");
        }

        // GPU resources on the first registered terrain (85 MB of atlas by default)
        void EnsureRvtResources()
        {
            if (m_RvtAlbedo != null)
                return;
            m_RvtAtlasTiles = Mathf.Clamp(m_Settings.rvtAtlasTiles, 4, 60);
            m_RvtAtlasSize = m_RvtAtlasTiles * (VgRvtTile + 2 * VgRvtBorder);
            m_RvtAlbedo = CreateRvtAtlas(GraphicsFormat.R8G8B8A8_SRGB, "UNanite RVT albedo");
            m_RvtNormal = CreateRvtAtlas(GraphicsFormat.R8G8B8A8_UNorm, "UNanite RVT normal");
            m_RvtTargets[0] = m_RvtAlbedo;
            m_RvtTargets[1] = m_RvtNormal;
            m_RvtResidency = new VgRvtResidency(m_RvtAtlasTiles * m_RvtAtlasTiles);
            m_RvtPages = new VgRangeAllocator(0);
            m_RvtRequests = new GraphicsBuffer(GraphicsBuffer.Target.Structured, k_RvtRequestCapacity + 1, 4);
            m_RvtRequests.SetData(new uint[1]);
            m_RvtUpdates = new GraphicsBuffer(GraphicsBuffer.Target.Structured, k_RvtUpdateCapacity, 8);
            m_RvtTerrainBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2 * (k_RvtMaxTerrains + 1), 16);
            EnsureRvtPageCapacity(1 << 16);
        }

        const int VgRvtTile = 128, VgRvtBorder = 4; // VgRvt.hlsl VG_RVT_TILE / VG_RVT_BORDER

        RenderTexture CreateRvtAtlas(GraphicsFormat format, string name)
        {
            var rt = new RenderTexture(new RenderTextureDescriptor(m_RvtAtlasSize, m_RvtAtlasSize, format, GraphicsFormat.None) { msaaSamples = 1, useMipMap = false })
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 4, // taps along the footprint's major axis (VgRvtLod clamps it to 4:1)
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
            return rt;
        }

        void DisposeRvt()
        {
            foreach (var b in new[] { m_RvtPageTable, m_RvtBits, m_RvtRequests, m_RvtUpdates, m_RvtTerrainBuffer, m_BinRvtBuffer })
                b?.Dispose();
            m_RvtPageTable = m_RvtBits = m_RvtRequests = m_RvtUpdates = m_RvtTerrainBuffer = m_BinRvtBuffer = null;
            foreach (var rt in new[] { m_RvtAlbedo, m_RvtNormal })
                if (rt != null)
                {
                    rt.Release();
                    CoreUtils.Destroy(rt);
                }
            m_RvtAlbedo = m_RvtNormal = null;
            // the terrains keep their handles; their materials fall back to the direct blend
            foreach (var t in m_RvtHandles.Values)
                SetRvtKeyword(t.resolve, false, Vector4.zero);
            m_RvtTerrains.Clear();
            m_RvtHandles.Clear();
            m_RvtBakes.Clear();
            m_RvtReadbacks.Clear();
            m_RvtWrites.Clear();
            m_RvtCs = null;
            m_RvtActive = false;
        }

        void EnsureRvtPageCapacity(int entries)
        {
            if (m_RvtPageTable != null && m_RvtPageTable.count >= entries)
                return;
            int capacity = Mathf.Max(entries, m_RvtPageTable != null ? m_RvtPageTable.count * 2 : 0);
            System.Array.Resize(ref m_RvtPageMirror, capacity);
            m_RvtPageTable?.Dispose();
            m_RvtBits?.Dispose();
            m_RvtPageTable = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 4);
            m_RvtPageTable.SetData(m_RvtPageMirror);
            m_RvtBits = new GraphicsBuffer(GraphicsBuffer.Target.Structured, (capacity + 31) / 32, 4);
            m_RvtBits.SetData(new uint[m_RvtBits.count]);
            m_RvtPages.Grow(capacity); // live ranges keep their place
        }

        /// <summary>
        /// M12: registers a VG terrain for the runtime virtual texture (bins of `fallback` are the
        /// terrain's; `resolve` is its TerrainLitResolve material, which gets VG_TERRAIN_RVT while the
        /// setting is on). GPU memory is only allocated while the setting is on. Returns a handle, or -1.
        /// </summary>
        public int RegisterTerrainRvt(Material fallback, Material resolve, Vector3 origin, Vector3 size)
        {
            if (m_RvtCs == null || fallback == null || resolve == null || resolve.FindPass("VgRvtBake") < 0)
                return -1;
            float extent = Mathf.Max(size.x, size.z);
            int k = Mathf.Clamp(Mathf.CeilToInt(Mathf.Log(Mathf.Max(1f, extent * m_Settings.rvtTexelsPerMeter), 2f)), VgRvtTileLog2 + k_RvtPinnedMips, 17);
            var t = new RvtTerrain
            {
                fallback = fallback, resolve = resolve, origin = new Vector2(origin.x, origin.z), size = new Vector2(size.x, size.z), k = k,
            };
            int handle = m_RvtNextHandle++;
            m_RvtHandles[handle] = t;
            if (m_RvtActive)
                ActivateRvtTerrain(t);
            return handle;
        }

        const int VgRvtTileLog2 = 7;

        // page-table range + residency entry + keyword (setting on)
        void ActivateRvtTerrain(RvtTerrain t)
        {
            if (t.residencyIndex >= 0 || m_RvtTerrains.Count >= k_RvtMaxTerrains)
                return;
            int L = t.k - VgRvtTileLog2;
            int count = VgRvtResidency.PageCount(L);
            int pageBase = m_RvtPages.Allocate(count);
            if (pageBase < 0)
            {
                EnsureRvtPageCapacity(m_RvtPages.Capacity + count);
                pageBase = m_RvtPages.Allocate(count);
                if (pageBase < 0)
                    return;
            }
            for (int i = 0; i < count; ++i)
                m_RvtPageMirror[pageBase + i] = 0;
            m_RvtPageTable.SetData(m_RvtPageMirror, pageBase, pageBase, count);
            t.pageBase = pageBase;
            t.pageCount = count;
            t.residencyIndex = m_RvtResidency.AddTerrain(L, pageBase, k_RvtPinnedMips);
            m_RvtTerrains[t.residencyIndex] = t;
            m_RvtTerrainsDirty = true;
            ApplyRvtKeyword(t, true);
        }

        void DeactivateRvtTerrain(RvtTerrain t)
        {
            int index = t.residencyIndex;
            if (index < 0)
                return;
            m_RvtResidency.RemoveTerrain(index);
            m_RvtBakes.RemoveAll(b => VgRvtResidency.KeyTerrain(b.key) == index);
            ApplyRvtWrites(); // the freed slots' entries (the range is released below anyway)
            m_RvtPages.Free(t.pageBase, t.pageCount);
            m_RvtTerrains.Remove(index);
            m_RvtTerrainsDirty = true;
            t.residencyIndex = -1;
            ApplyRvtKeyword(t, false);
        }

        public void UnregisterTerrainRvt(int handle)
        {
            if (!m_RvtHandles.TryGetValue(handle, out var t))
                return;
            DeactivateRvtTerrain(t);
            m_RvtHandles.Remove(handle);
        }

        /// <summary>M12: re-bakes a terrain's tiles over a UV rectangle (after layer, splat or height edits).</summary>
        public void InvalidateTerrainRvt(int handle, Rect uv)
        {
            if (m_RvtHandles.TryGetValue(handle, out var t) && t.residencyIndex >= 0)
                m_RvtResidency.Invalidate(t.residencyIndex, uv.xMin, uv.yMin, uv.xMax, uv.yMax);
        }

        static void ApplyRvtKeyword(RvtTerrain t, bool on) =>
            SetRvtKeyword(t.resolve, on, on ? new Vector4(t.pageBase, t.k, t.k - VgRvtTileLog2, 1) : Vector4.zero);

        static void SetRvtKeyword(Material m, bool on, Vector4 parameters)
        {
            if (m == null)
                return;
            m.SetVector(RvtIds.TerrainParams, parameters);
            if (on)
                m.EnableKeyword("VG_TERRAIN_RVT");
            else
                m.DisableKeyword("VG_TERRAIN_RVT");
        }

        // Frame start: feedback -> residency, this frame's bakes and page-table writes, per-bin map.
        void BeginRvtFrame()
        {
            m_RvtFrame++;
            m_RvtFeedbackThisContext = false;
            m_RvtUploadsDone = false;
            if (m_RvtCs == null || m_RvtHandles.Count == 0)
                return;
            // the setting is live: terrains switch between the cache and the direct blend (GPU memory
            // is allocated the first time it is switched on)
            if (m_RvtActive != m_Settings.terrainVirtualTexture)
            {
                m_RvtActive = m_Settings.terrainVirtualTexture;
                if (m_RvtActive)
                    EnsureRvtResources();
                foreach (var t in m_RvtHandles.Values)
                {
                    if (m_RvtActive)
                        ActivateRvtTerrain(t);
                    else
                        DeactivateRvtTerrain(t);
                }
            }
            if (!m_RvtActive)
            {
                m_RvtReadbacks.Clear();
                return;
            }
            int reports = 0;
            while (m_RvtReadbacks.Count > 0 && m_RvtReadbacks.Peek().request.done)
            {
                var (frame, request) = m_RvtReadbacks.Dequeue();
                if (request.hasError)
                    continue;
                var data = request.GetData<uint>();
                int count = (int)System.Math.Min(data[0], (uint)k_RvtRequestCapacity);
                for (int i = 0; i < count; ++i)
                    m_RvtResidency.Report(data[1 + i], frame);
                reports += count;
            }
            // atlas contents lost (device reset, pipeline switch): every tile is baked again
            if (!m_RvtAlbedo.IsCreated() || !m_RvtNormal.IsCreated())
            {
                m_RvtAlbedo.Create();
                m_RvtNormal.Create();
                m_RvtBakes.Clear();
                foreach (var index in m_RvtTerrains.Keys)
                    m_RvtResidency.Invalidate(index, 0f, 0f, 1f, 1f);
            }
            // new bakes once the previous ones ran (no visibility-buffer camera rendered: they wait)
            int baked = 0;
            if (m_RvtBakes.Count == 0)
            {
                m_RvtBakes.AddRange(m_RvtResidency.Update(m_RvtFrame, Mathf.Max(1, m_Settings.rvtBakesPerFrame)));
                baked = m_RvtBakes.Count;
            }
            m_RvtStats.terrains = m_RvtTerrains.Count;
            m_RvtStats.residentTiles = m_RvtResidency.ResidentCount;
            m_RvtStats.capacity = m_RvtResidency.Capacity;
            m_RvtStats.pendingRequests = m_RvtResidency.PendingCount;
            m_RvtStats.bakesLastFrame = baked;
            m_RvtStats.evictionsLastFrame = baked > 0 ? m_RvtResidency.EvictionsLastUpdate : 0;
            m_RvtStats.reportsLastFrame = reports;
            m_RvtStats.bakedTotal += baked;

            if (m_RvtTerrainsDirty)
            {
                m_RvtTerrainsDirty = false;
                var data = new Vector4[2 * (k_RvtMaxTerrains + 1)];
                foreach (var kv in m_RvtTerrains)
                {
                    var t = kv.Value;
                    data[2 * kv.Key] = new Vector4(t.origin.x, t.origin.y, 1f / Mathf.Max(t.size.x, 1e-3f), 1f / Mathf.Max(t.size.y, 1e-3f));
                    data[2 * kv.Key + 1] = new Vector4(t.k, t.k - VgRvtTileLog2, t.pageBase, 0);
                }
                m_RvtTerrainBuffer.SetData(data);
            }
            UpdateBinRvt();

            Shader.SetGlobalTexture(RvtIds.Albedo, m_RvtAlbedo);
            Shader.SetGlobalTexture(RvtIds.Normal, m_RvtNormal);
            Shader.SetGlobalBuffer(RvtIds.PageTable, m_RvtPageTable);
            Shader.SetGlobalVector(RvtIds.Atlas, new Vector4(m_RvtAtlasTiles, 1f / m_RvtAtlasSize, m_RvtAtlasSize, 0));
        }

        void UpdateBinRvt()
        {
            int bins = Mathf.Max(1, m_BinCapacity);
            if (m_BinRvt.Length != bins)
                m_BinRvt = new uint[bins];
            bool changed = m_BinRvtBuffer == null || m_BinRvtBuffer.count != bins;
            for (int b = 0; b < bins; ++b)
            {
                uint value = 0;
                if (b < m_BinMaterials.Count && m_BinRefCount[b] > 0)
                    foreach (var kv in m_RvtTerrains)
                        if (kv.Value.fallback == m_BinMaterials[b])
                        {
                            value = (uint)kv.Key + 1u;
                            break;
                        }
                changed |= m_BinRvt[b] != value;
                m_BinRvt[b] = value;
            }
            if (!changed)
                return;
            if (m_BinRvtBuffer == null || m_BinRvtBuffer.count != bins)
            {
                m_BinRvtBuffer?.Dispose();
                m_BinRvtBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bins, 4);
            }
            m_BinRvtBuffer.SetData(m_BinRvt);
        }

        // page-table writes of the residency -> mirror + GPU update list
        void ApplyRvtWrites()
        {
            foreach (var (index, value) in m_RvtResidency.PageWrites)
            {
                if (index < 0 || index >= m_RvtPageMirror.Length)
                    continue;
                m_RvtPageMirror[index] = value;
                m_RvtWrites.Add(new Vector2Int(index, (int)value));
            }
            m_RvtResidency.PageWrites.Clear();
        }

#if UNANITE_HDRP
        // Visibility custom pass, after the classification: bakes + page updates + feedback reset once
        // per context render, then this camera's feedback.
        void RenderRvt(CustomPassContext ctx, int width, int height)
        {
            if (m_RvtCs == null || !m_RvtActive || m_RvtTerrains.Count == 0 || m_BinRvtBuffer == null)
                return;
            var cmd = ctx.cmd;
            cmd.BeginSample(RvtSampler);
            if (!m_RvtUploadsDone)
            {
                m_RvtUploadsDone = true;
                if (m_RvtBakes.Count > 0)
                {
                    cmd.SetRenderTarget(m_RvtTargets, m_RvtAlbedo.depthBuffer);
                    cmd.SetViewport(new Rect(0, 0, m_RvtAtlasSize, m_RvtAtlasSize));
                    int stride = VgRvtTile + 2 * VgRvtBorder;
                    foreach (var bake in m_RvtBakes)
                    {
                        int terrain = VgRvtResidency.KeyTerrain(bake.key);
                        if (!m_RvtTerrains.TryGetValue(terrain, out var t) || t.resolve == null)
                            continue;
                        int mip = VgRvtResidency.KeyMip(bake.key);
                        float texelUV = (float)(1 << mip) / (1 << t.k); // terrain UV per texel of this mip
                        var tile = new Vector2(VgRvtResidency.KeyX(bake.key), VgRvtResidency.KeyY(bake.key));
                        cmd.SetGlobalVector(RvtIds.BakeTile, new Vector4(bake.slot % m_RvtAtlasTiles * stride, bake.slot / m_RvtAtlasTiles * stride, 1f / m_RvtAtlasSize, 0));
                        cmd.SetGlobalVector(RvtIds.BakeUV, new Vector4((tile.x * VgRvtTile - VgRvtBorder) * texelUV, (tile.y * VgRvtTile - VgRvtBorder) * texelUV, texelUV, 0));
                        cmd.DrawProcedural(Matrix4x4.identity, t.resolve, t.resolve.FindPass("VgRvtBake"), MeshTopology.Triangles, 6);
                    }
                    m_RvtBakes.Clear();
                }
                ApplyRvtWrites();
                if (m_RvtWrites.Count > 0)
                {
                    int n = Mathf.Min(m_RvtWrites.Count, k_RvtUpdateCapacity);
                    cmd.SetBufferData(m_RvtUpdates, m_RvtWrites, 0, 0, n);
                    cmd.SetComputeIntParams(m_RvtCs, RvtIds.Params, k_RvtRequestCapacity, n, m_RvtBits.count, 0);
                    cmd.SetComputeBufferParam(m_RvtCs, k_RvtScatter, RvtIds.Updates, m_RvtUpdates);
                    cmd.SetComputeBufferParam(m_RvtCs, k_RvtScatter, RvtIds.PageTableOut, m_RvtPageTable);
                    cmd.DispatchCompute(m_RvtCs, k_RvtScatter, 1, 1, 1);
                    m_RvtWrites.RemoveRange(0, n);
                }
                cmd.SetComputeIntParams(m_RvtCs, RvtIds.Params, k_RvtRequestCapacity, 0, m_RvtBits.count, 0);
                cmd.SetComputeBufferParam(m_RvtCs, k_RvtReset, RvtIds.Bits, m_RvtBits);
                cmd.SetComputeBufferParam(m_RvtCs, k_RvtReset, RvtIds.Requests, m_RvtRequests);
                cmd.DispatchCompute(m_RvtCs, k_RvtReset, (m_RvtBits.count + 63) / 64, 1, 1);
            }

            // feedback: one pixel per 4x4 block, jittered over 16 frames
            var camera = ctx.hdCamera.camera;
            var proj = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
            var viewProj = proj * camera.worldToCameraMatrix;
            int j = (int)(m_RvtFrame * 7u % 16u);
            cmd.SetComputeMatrixParam(m_RvtCs, RvtIds.InvViewProj, viewProj.inverse);
            cmd.SetComputeIntParams(m_RvtCs, RvtIds.Screen, width, height, j & 3, j >> 2);
            cmd.SetComputeIntParams(m_RvtCs, RvtIds.Params, k_RvtRequestCapacity, 0, m_RvtBits.count, 0);
            cmd.SetComputeTextureParam(m_RvtCs, k_RvtFeedback, VisIds.VisBuffer, m_VisBuffer);
            cmd.SetComputeBufferParam(m_RvtCs, k_RvtFeedback, VisIds.Visible, m_Visible);
            cmd.SetComputeBufferParam(m_RvtCs, k_RvtFeedback, RvtIds.BinRvt, m_BinRvtBuffer);
            cmd.SetComputeBufferParam(m_RvtCs, k_RvtFeedback, RvtIds.Terrains, m_RvtTerrainBuffer);
            cmd.SetComputeBufferParam(m_RvtCs, k_RvtFeedback, RvtIds.Bits, m_RvtBits);
            cmd.SetComputeBufferParam(m_RvtCs, k_RvtFeedback, RvtIds.Requests, m_RvtRequests);
            cmd.DispatchCompute(m_RvtCs, k_RvtFeedback, (width / 4 + 7) / 8, (height / 4 + 7) / 8, 1);
            m_RvtFeedbackThisContext = true;
            cmd.EndSample(RvtSampler);
        }
#endif

        // End of a context render: read the request list back (polled at the next frame starts).
        void RequestRvtFeedback()
        {
            if (!m_RvtFeedbackThisContext || m_RvtRequests == null || !SystemInfo.supportsAsyncGPUReadback || m_RvtReadbacks.Count >= 4)
                return;
            m_RvtFeedbackThisContext = false;
            m_RvtReadbacks.Enqueue((m_RvtFrame, AsyncGPUReadback.Request(m_RvtRequests)));
        }

        /// <summary>Tests / tools: waits for the virtual-texture feedback in flight (the next frame bakes what it asked for).</summary>
        public void WaitForVirtualTextures()
        {
            foreach (var (_, request) in m_RvtReadbacks)
                request.WaitForCompletion();
        }
    }
}
