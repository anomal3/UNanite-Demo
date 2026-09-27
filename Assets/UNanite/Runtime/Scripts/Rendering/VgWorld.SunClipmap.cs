using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
#if UNANITE_HDRP
using UnityEngine.Rendering.HighDefinition;
#endif

namespace UNanite
{
    // M13b (prototype): the sun's shadow from UNanite's own clipmap of cached pages (VgSunClipmap.hlsl,
    // VgVsm.compute ClipmapRequest / MarkRequested, Milestones.md M13b).
    //
    //   levels       orthographic views along the sun with a fixed basis, texel t0 * 2^k, res x res texels
    //                around the camera on a world-fixed texel grid (window = integer texel offset): camera
    //                motion scrolls the page tables and keeps every page still inside. Each level is an M13
    //                cache entry (fixed slots k_SunFirstEntry..): pool, LRU release, invalidation of moved
    //                casters and the dirty pyramid come from M13.
    //   per frame    (AfterOpaqueDepthAndNormal of the clipmap camera) release -> request pages from the
    //                camera depth (level by pixel footprint) -> mark coarse to fine within the page budget
    //                -> clear dirty pages -> dirty pyramids -> batched culling of the levels (shadow-raster
    //                bins over dirty pages, texel LOD) -> page raster -> globals for the HDRP patch
    //   HDRP         the optional patch (HdrpPatch~) samples the clipmap in GetDirectionalShadowAttenuation;
    //                without it the clipmap is built and never read.
    public sealed partial class VgWorld
    {
        /// <summary>GPU/CPU sampler around the clipmap upkeep (release, request, mark, clear, dirty pyramids).</summary>
        public static readonly CustomSampler SunClipmapSampler = CustomSampler.Create("VG.SunClipmap", true);
        /// <summary>GPU/CPU sampler around the raster of the clipmap's dirty pages.</summary>
        public static readonly CustomSampler SunClipmapRasterSampler = CustomSampler.Create("VG.SunClipmapRaster", true);

        const int k_SunMaxLevels = 16;
        const int k_SunFirstEntry = k_VsmMaxEntries - k_SunMaxLevels; // fixed M13 entry slots of the levels
        const float k_SunDepthScale = 1f / 4096f; // device depth per metre: d = -dot(sun forward, p) * s + 0.5

        int k_ClipRequest = -1, k_MarkRequested = -1;
        Vector3 m_SunForward, m_SunRight, m_SunUp;
        int m_SunRes, m_SunLevels;
        float m_SunTexel0, m_SunSlopeBias;
        Light m_SunLight;
        int m_SunLightFrame = -1;
        readonly System.Collections.Generic.List<(SubView view, int slot)> m_SunViews = new System.Collections.Generic.List<(SubView, int)>();

        /// <summary>M13b diagnostics: what the last clipmap frame did (or why it did not run).</summary>
        public string LastSunClipmapDecision { get; private set; }

        static class SunIds
        {
            public static readonly int Entries = Shader.PropertyToID("_UNaniteSunEntries");
            public static readonly int Pages = Shader.PropertyToID("_UNaniteSunPages");
            public static readonly int Pool = Shader.PropertyToID("_UNaniteSunPool");
            public static readonly int Params = Shader.PropertyToID("_UNaniteSunParams");
            public static readonly int Camera = Shader.PropertyToID("_UNaniteSunCamera");
            public static readonly int Forward = Shader.PropertyToID("_UNaniteSunForward");
            public static readonly int Bias = Shader.PropertyToID("_UNaniteSunBias");
            public static readonly int InvViewProj = Shader.PropertyToID("_ClipInvViewProj");
            public static readonly int Screen = Shader.PropertyToID("_ClipScreen");
            public static readonly int Reach = Shader.PropertyToID("_ClipReach");
        }

        void InitSunClipmap(ComputeShader cs)
        {
            if (HasKernels(cs, "ClipmapRequest", "MarkRequested"))
            {
                k_ClipRequest = cs.FindKernel("ClipmapRequest");
                k_MarkRequested = cs.FindKernel("MarkRequested");
            }
        }

        // the sun: the brightest enabled directional light with shadows (looked up once per frame)
        Light SunLight()
        {
            if (m_SunLightFrame == Time.frameCount && m_SunLight != null)
                return m_SunLight;
            m_SunLightFrame = Time.frameCount;
            var sun = RenderSettings.sun;
            if (sun == null || !sun.isActiveAndEnabled || sun.type != LightType.Directional || sun.shadows == LightShadows.None)
            {
                sun = null;
                foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude))
                    if (l.type == LightType.Directional && l.shadows != LightShadows.None && (sun == null || l.intensity > sun.intensity))
                        sun = l;
            }
            m_SunLight = sun;
            return sun;
        }

#if UNANITE_HDRP
        /// <summary>
        /// AfterOpaqueDepthAndNormal custom pass body (after the deferred shadow splits): upkeep, culling and
        /// raster of the sun clipmap for the clipmap camera, and the globals the HDRP patch reads. Other
        /// cameras switch the patch's lookup off (HDRP's cascades).
        /// </summary>
        internal void RenderSunClipmap(CustomPassContext ctx)
        {
            if (m_SunClipmapFailed)
            {
                ctx.cmd.SetGlobalVector(SunIds.Params, Vector4.zero);
                return;
            }
            try
            {
                RecordSunClipmap(ctx);
            }
            catch (System.Exception ex)
            {
                // prototype: an exception must not leave half a frame of clipmap work (UAV bindings,
                // raster state) behind every frame - off until the world is recreated
                m_SunClipmapFailed = true;
                LastSunClipmapDecision = "failed: " + ex.Message;
                Debug.LogException(ex);
                if (m_SunOpenSampler != null)
                    ctx.cmd.EndSample(m_SunOpenSampler); // unbalanced GPU samples break the render graph
                m_SunOpenSampler = null;
                ctx.cmd.ClearRandomWriteTargets();
                ctx.cmd.SetGlobalVector(SunIds.Params, Vector4.zero);
            }
        }

        bool m_SunClipmapFailed;
        CustomSampler m_SunOpenSampler;

        void RecordSunClipmap(CustomPassContext ctx)
        {
            var cmd = ctx.cmd;
            var camera = ctx.hdCamera.camera;
            string why = SunClipmapBlocker(camera);
            if (why != null)
            {
                LastSunClipmapDecision = why;
                cmd.SetGlobalVector(SunIds.Params, Vector4.zero);
                return;
            }
            var sun = m_SunLight;
            int levels = Mathf.Clamp(m_Settings.sunClipmapLevels, 1, k_SunMaxLevels);
            int res = Mathf.Clamp(m_Settings.sunClipmapResolution / k_VsmPage * k_VsmPage, 1024, 8192);
            float texel0 = Mathf.Max(1e-4f, m_Settings.sunClipmapTexel0);
            int W = res / k_VsmPage + 1;
            var t = sun.transform;
            Vector3 f = t.forward, r = t.right, u = t.up;

            // a new basis, layout or bias: every level starts over
            bool resetAll = res != m_SunRes || levels != m_SunLevels || texel0 != m_SunTexel0 || m_Settings.sunClipmapSlopeBias != m_SunSlopeBias ||
                            Vector3.Dot(f, m_SunForward) < 0.999999f || Vector3.Dot(r, m_SunRight) < 0.999999f;
            if (resetAll)
            {
                m_SunForward = f;
                m_SunRight = r;
                m_SunUp = u;
                m_SunRes = res;
                m_SunLevels = levels;
                m_SunTexel0 = texel0;
                m_SunSlopeBias = m_Settings.sunClipmapSlopeBias;
            }
            f = m_SunForward;
            r = m_SunRight;
            u = m_SunUp;
            var camPos = camera.transform.position;

            cmd.BeginSample(m_SunOpenSampler = SunClipmapSampler);
            // levels: matrices, windows, tables
            for (int k = 0; k < levels; ++k)
            {
                var e = SunEntry(k, camera);
                double q = 1.0 / (texel0 * (1 << k)); // texels per metre
                int wx = (int)System.Math.Round(Vector3.Dot(r, camPos) * q) - res / 2;
                int wy = (int)System.Math.Round(-Vector3.Dot(u, camPos) * q) - res / 2;
                bool far = System.Math.Abs(wx) / k_VsmPage > 30000 || System.Math.Abs(wy) / k_VsmPage > 30000;
                float sx = (float)(2.0 * q / res);
                var vp = Matrix4x4.zero;
                vp.SetRow(0, new Vector4(r.x * sx, r.y * sx, r.z * sx, -2f * wx / res - 1f));
                vp.SetRow(1, new Vector4(u.x * sx, u.y * sx, u.z * sx, 1f + 2f * wy / res));
                vp.SetRow(2, new Vector4(-f.x * k_SunDepthScale, -f.y * k_SunDepthScale, -f.z * k_SunDepthScale, 0.5f));
                vp.SetRow(3, new Vector4(0f, 0f, 0f, 1f));
                var cull = vp; // GL-style depth for the receiver / dirty-pyramid test: grows away from the sun
                cull.SetRow(2, new Vector4(f.x * k_SunDepthScale, f.y * k_SunDepthScale, f.z * k_SunDepthScale, 0f));

                e.reset = (resetAll || far || !e.hasAnchor) && e.tableBase >= 0;
                e.hasAnchor = !far;
                e.lastCulled = m_VsmFrame;
                e.res = res;
                e.ortho = true;
                e.zClip = false;
                e.slopeBias = m_Settings.sunClipmapSlopeBias;
                e.renderVP = vp;
                e.anchorVP = vp; // row 2 = the stored depth (the same for every frame)
                e.cullVP = cull;
                e.window = new Vector2Int(wx, wy);
                e.frac = Vector2.zero;
                e.depthA = 1f;
                e.depthB = 0f;
                e.projScale = sx;
                e.W = W;
                e.resolved = true;
                if (e.reset)
                {
                    WriteVsmEntry(e);
                    cmd.SetBufferData(m_VsmEntries, m_VsmEntryData, e.index * k_VsmEntryFloats, e.index * k_VsmEntryFloats, k_VsmEntryFloats);
                    DispatchVsmRelease(cmd, e);
                    if (e.tableW != W)
                    {
                        m_VsmTables.Free(e.tableBase, e.tableW * e.tableW);
                        e.tableBase = -1;
                    }
                }
                e.reset = false;
                if (e.tableBase < 0)
                {
                    e.tableW = W;
                    e.tableBase = m_VsmTables.Allocate(W * W);
                    if (e.tableBase < 0)
                    {
                        GrowVsmTables(cmd, m_VsmTables.Capacity + W * W);
                        e.tableBase = m_VsmTables.Allocate(W * W);
                    }
                    cmd.SetBufferData(m_VsmPages, EmptyVsmSlots(W * W), 0, e.tableBase * 4, W * W * 4);
                }
                WriteVsmEntry(e);
            }
            cmd.SetBufferData(m_VsmEntries, m_VsmEntryData, k_SunFirstEntry * k_VsmEntryFloats, k_SunFirstEntry * k_VsmEntryFloats, levels * k_VsmEntryFloats);

            // globals of the lookup (also read by ClipmapRequest)
            float tanHalf = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            int width = ctx.hdCamera.actualWidth, height = ctx.hdCamera.actualHeight;
            float footprint = 2f * tanHalf / Mathf.Max(1, height) * m_Settings.sunClipmapLodBias;
            var prm = new Vector4((float)m_Settings.sunShadowClipmap, levels, k_SunFirstEntry, texel0);
            var cam = new Vector4(camPos.x, camPos.y, camPos.z, footprint);
            var fwd = camera.transform.forward;
            var forward = new Vector4(fwd.x, fwd.y, fwd.z, m_Settings.sunClipmapNormalOffset);
            var bias = new Vector4(m_Settings.sunClipmapDepthBias, m_Settings.sunClipmapFilterRadius, k_SunDepthScale, 0f);

            // counters, release (LRU, scrolled slots), requests from the camera depth
            for (int k = 0; k < levels; ++k)
                PutVsmBatch(k, k_SunFirstEntry + k, k);
            SetVsmBatch(cmd, levels, k_VsmRelease);
            cmd.SetComputeBufferParam(m_VsmCs, k_VsmBegin, VsmIds.Counters, m_VsmCounters);
            cmd.DispatchCompute(m_VsmCs, k_VsmBegin, (16 + k_VsmMaxEntries + 63) / 64, 1, 1);
            DispatchVsmRelease(cmd, levels, W);

            var proj = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
            cmd.SetComputeMatrixParam(m_VsmCs, SunIds.InvViewProj, (proj * camera.worldToCameraMatrix).inverse);
            cmd.SetComputeIntParams(m_VsmCs, SunIds.Screen, width, height, SystemInfo.usesReversedZBuffer ? 1 : 0, m_Settings.sunClipmapPageBudget);
            cmd.SetComputeVectorParam(m_VsmCs, SunIds.Reach, new Vector4(m_Settings.sunClipmapFilterRadius + m_Settings.sunClipmapNormalOffset + 1f, 0f, 0f, 0f));
            cmd.SetComputeVectorParam(m_VsmCs, SunIds.Params, prm);
            cmd.SetComputeVectorParam(m_VsmCs, SunIds.Camera, cam);
            cmd.SetComputeVectorParam(m_VsmCs, SunIds.Forward, forward);
            cmd.SetComputeVectorParam(m_VsmCs, SunIds.Bias, bias);
            cmd.SetComputeIntParams(m_VsmCs, VsmIds.Params, levels, m_VsmFrame, 0, m_VsmPoolPages);
            cmd.SetComputeTextureParam(m_VsmCs, k_ClipRequest, OccIds.Depth, ctx.cameraDepthBuffer, 0, RenderTextureSubElement.Depth);
            cmd.SetComputeBufferParam(m_VsmCs, k_ClipRequest, VsmIds.Entries, m_VsmEntries);
            cmd.SetComputeBufferParam(m_VsmCs, k_ClipRequest, SunIds.Entries, m_VsmEntries);
            cmd.SetComputeBufferParam(m_VsmCs, k_ClipRequest, VsmIds.Pages, m_VsmPages);
            cmd.DispatchCompute(m_VsmCs, k_ClipRequest, (width + 7) / 8, (height + 7) / 8, 1);

            // mark coarse to fine: the page budget goes to the coarse levels first
            foreach (var b in new[] { (VsmIds.Entries, m_VsmEntries), (VsmIds.Pages, m_VsmPages), (VsmIds.Free, m_VsmFree), (VsmIds.Counters, m_VsmCounters), (VsmIds.Dirty, m_VsmDirty) })
                cmd.SetComputeBufferParam(m_VsmCs, k_MarkRequested, b.Item1, b.Item2);
            for (int k = levels - 1; k >= 0; --k)
            {
                PutVsmBatch(0, k_SunFirstEntry + k, k);
                SetVsmBatch(cmd, 1, k_MarkRequested);
                cmd.DispatchCompute(m_VsmCs, k_MarkRequested, (W + 7) / 8, (W + 7) / 8, 1);
            }
            for (int k = 0; k < levels; ++k)
                PutVsmBatch(k, k_SunFirstEntry + k, k);
            SetVsmBatch(cmd, levels, k_VsmFinalize, k_VsmDirty);
            cmd.SetComputeBufferParam(m_VsmCs, k_VsmFinalize, VsmIds.Counters, m_VsmCounters);
            cmd.SetComputeBufferParam(m_VsmCs, k_VsmFinalize, VsmIds.Args, m_VsmArgs);
            cmd.DispatchCompute(m_VsmCs, k_VsmFinalize, 1, 1, 1);
            cmd.SetComputeBufferParam(m_VsmCs, k_VsmClear, VsmIds.Dirty, m_VsmDirty);
            cmd.SetComputeTextureParam(m_VsmCs, k_VsmClear, VsmIds.Pool, m_VsmPool);
            cmd.DispatchCompute(m_VsmCs, k_VsmClear, m_VsmArgs, 0);

            // dirty pyramids -> culling keeps casters over dirty pages only
            var rcv = m_Receivers;
            cmd.SetComputeIntParams(m_VsmCs, VsmIds.Pyramid, m_ReceiverTexels, 0, rcv.width0, rcv.levels);
            cmd.SetComputeBufferParam(m_VsmCs, k_VsmDirty, VsmIds.Entries, m_VsmEntries);
            cmd.SetComputeBufferParam(m_VsmCs, k_VsmDirty, VsmIds.Pages, m_VsmPages);
            cmd.SetComputeBufferParam(m_VsmCs, k_VsmDirty, VsmIds.HzbOut, m_VsmHzb);
            cmd.DispatchCompute(m_VsmCs, k_VsmDirty, (rcv.width0 + 7) / 8, (rcv.width0 + 7) / 8, levels);
            BuildPyramidLevels(cmd, m_VsmHzb, levels);
            cmd.EndSample(SunClipmapSampler);
            m_SunOpenSampler = null;

            // culling: one view slot per level, batched like HDRP's splits
            int firstSlot = m_ViewSlotsUsed;
            m_ViewSlotsUsed += levels;
            m_SunViews.Clear();
            for (int k = 0; k < levels; ++k)
            {
                var e = m_VsmSlots[k_SunFirstEntry + k];
                float tk = texel0 * (1 << k);
                var v = new SubView
                {
                    planes = new Vector4[16],
                    planeCount = 4,
                    basePlaneCount = 4,
                    position = camPos,
                    near = 0.01f,
                    lodA = 1f / (tk * Mathf.Max(0.25f, m_Settings.shadowTexelError)),
                    flags = k_ViewFlagShadow | k_ViewFlagNoCone | k_ViewFlagOnlyRaster,
                    splitMask = 1,
                    name = $"Sun clipmap {k}",
                    camera = camera,
                    lodFadeSlot = -1,
                    shadowRaster = true,
                    receiverCulling = true,
                    lightViewProj = e.cullVP,
                    receiverBatchIndex = k,
                };
                float x0 = e.window.x * tk, x1 = (e.window.x + res) * tk, y0 = e.window.y * tk, y1 = (e.window.y + res) * tk;
                v.planes[0] = new Vector4(r.x, r.y, r.z, -x0);   // dot(r, p) >= x0
                v.planes[1] = new Vector4(-r.x, -r.y, -r.z, x1); // dot(r, p) <= x1
                v.planes[2] = new Vector4(u.x, u.y, u.z, y1);    // dot(u, p) >= -y1 (rows grow along -u)
                v.planes[3] = new Vector4(-u.x, -u.y, -u.z, -y0); // dot(u, p) <= -y0
                m_ViewNames.Add(v.name);
                m_SunViews.Add((v, firstSlot + k));
            }
            m_HzbOverride = m_VsmHzb;
            for (int s = 0; s < m_SunViews.Count; s += k_MaxBatchSplits)
            {
                int n = Mathf.Min(k_MaxBatchSplits, m_SunViews.Count - s);
                if (n > 1)
                    RecordBatch(cmd, m_SunViews, s, n);
                else
                    RecordView(cmd, m_SunViews[s].view, m_SunViews[s].slot);
            }
            m_HzbOverride = null;

            // raster of the dirty pages
            EnsureVsmDummy(res);
            cmd.BeginSample(m_SunOpenSampler = SunClipmapRasterSampler);
            cmd.SetRenderTarget(m_VsmDummy);
            cmd.SetRandomWriteTarget(1, m_VsmPool);
            cmd.SetViewport(new Rect(0, 0, res, res));
            foreach (var (view, slot) in m_SunViews)
            {
                for (int list = 0; list < 2; ++list)
                {
                    if (!m_AnyShadowBin[list])
                        continue;
                    m_VsmProps.Clear();
                    m_VsmProps.SetInteger(VsmIds.Draw, slot * 2 + list);
                    m_VsmProps.SetInteger(VsmIds.Entry, k_SunFirstEntry + view.receiverBatchIndex);
                    cmd.DrawProceduralIndirect(Matrix4x4.identity, m_VsmRasterMaterials[2 + list], 0, MeshTopology.Triangles, m_RasterArgs,
                        (slot * k_RasterArgsStride + list * 8) * 4, m_VsmProps);
                }
                m_VsmRasterSlots.Add(slot);
            }
            cmd.ClearRandomWriteTargets();
            cmd.EndSample(SunClipmapRasterSampler);
            m_SunOpenSampler = null;
            RequestVsmStats(cmd);

            // lookup globals for the HDRP patch
            cmd.SetGlobalBuffer(SunIds.Entries, m_VsmEntries);
            cmd.SetGlobalBuffer(SunIds.Pages, m_VsmPages);
            cmd.SetGlobalTexture(SunIds.Pool, m_VsmPool);
            cmd.SetGlobalVector(SunIds.Params, prm);
            cmd.SetGlobalVector(SunIds.Camera, cam);
            cmd.SetGlobalVector(SunIds.Forward, forward);
            cmd.SetGlobalVector(SunIds.Bias, bias);
            LastSunClipmapDecision = $"{levels} levels of {res}, texel {texel0 * 1000f:F1} mm .. {texel0 * (1 << (levels - 1)):F2} m, camera {camera.name}{(resetAll ? ", reset" : "")}";
        }

        // null = the clipmap runs for this camera, else why not
        string SunClipmapBlocker(Camera camera)
        {
            if (m_Settings.sunShadowClipmap == VgSunClipmapMode.Off)
                return "off in settings";
            if (!m_VsmSupported || k_ClipRequest < 0 || m_RcvCs == null)
                return "shaders missing";
            if (camera == null || camera != Camera.main)
                return "not the clipmap camera (Camera.main)";
            if (camera.orthographic)
                return "orthographic camera";
            if (!(m_AnyShadowBin[0] || m_AnyShadowBin[1]))
                return "no shadow-raster bins";
            if (SunLight() == null)
                return "no directional light with shadows";
            if (m_ViewSlotsUsed + Mathf.Clamp(m_Settings.sunClipmapLevels, 1, k_SunMaxLevels) > Mathf.Max(1, m_Settings.maxViewsPerFrame))
                return "view slots exhausted (raise maxViewsPerFrame)";
            if (!EnsureVsmResources())
                return "page pool unavailable";
            return null;
        }

        // the level's fixed M13 entry slot (an M13 split entry that took it is dropped first)
        VsmEntry SunEntry(int level, Camera camera)
        {
            int index = k_SunFirstEntry + level;
            var e = m_VsmSlots[index];
            if (e != null && e.lightId == -1)
                return e;
            if (e != null)
            {
                if (e.tableBase >= 0)
                {
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
            }
            e = new VsmEntry { index = index, lightId = -1, split = level, camera = camera };
            m_VsmSlots[index] = e;
            return e;
        }
#endif
    }
}
