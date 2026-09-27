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
    // M3: visibility buffer + material resolve for main cameras.
    //
    //   culling (per camera)      VgCull.compute keeps resolve-capable clusters out of the expansion,
    //                             stores them in the camera's VG_Visible region, PrepareRaster
    //   BeforeRendering pass      VG.RasterVisBuffer  VgVisBufferRaster.shader -> visibility buffer + depth
    //                             VG.Classify         VgClassify.compute -> per-bin tile lists + args
    //   HDRP GBuffer pass         BRG ProceduralIndirect draw per resolve bin, material = runtime twin
    //                             of the bin's HDRP/Lit material with Hidden/UNanite/LitResolve
    public sealed unsafe partial class VgWorld
    {
        const uint BinFlagResolve = 1u;      // VG_BIN_RESOLVE
        const uint BinFlagDoubleSided = 2u;  // VG_BIN_DOUBLE_SIDED

        /// <summary>GPU/CPU sampler around the visibility-buffer raster ("VG.RasterVisBuffer").</summary>
        public static readonly CustomSampler RasterSampler = CustomSampler.Create("VG.RasterVisBuffer", true);
        /// <summary>GPU/CPU sampler around the regular-mesh occluder depth ("VG.OccluderDepth", M4).</summary>
        public static readonly CustomSampler OccluderSampler = CustomSampler.Create("VG.OccluderDepth", true);
#if UNANITE_HDRP
        static readonly ShaderTagId[] s_DepthTags = { new ShaderTagId("DepthForwardOnly"), new ShaderTagId("DepthOnly") };
        static readonly RenderStateBlock s_NoColorWrites = new RenderStateBlock(RenderStateMask.Blend)
        {
            blendState = new BlendState { blendState0 = new RenderTargetBlendState((ColorWriteMask)0) },
        };
#endif
        /// <summary>GPU/CPU sampler around the material tile classification ("VG.Classify").</summary>
        public static readonly CustomSampler ClassifySampler = CustomSampler.Create("VG.Classify", true);

        int m_VisibleCapacity;
        int m_VisRegionCount;
        int m_VisRegionsUsed;
        GraphicsBuffer m_RasterArgs;
        GraphicsBuffer m_BinTileCount, m_BinTileOffset, m_BinTileCursor, m_ResolveArgs, m_TileList, m_ClassifyStats;
        ComputeShader m_Classify;
        int k_ClearBins, k_CountTiles, k_AllocateTiles, k_WriteTiles;
        Material m_RasterMaterial;
        Shader m_ResolveShader;
        MaterialPropertyBlock m_RasterProps;
        RTHandle m_VisBuffer;
        RTHandle m_BaryBuffer; // M11: barycentrics of programmable pixels (VgVisRaster.hlsl)
        RTHandle m_MotionBuffer; // M11: motion vectors of programmable pixels of vertex-animated bins
        readonly RenderTargetIdentifier[] m_ProgrammableTargets = new RenderTargetIdentifier[2];
        readonly RenderTargetIdentifier[] m_ProgrammableMotionTargets = new RenderTargetIdentifier[3];
        Material m_RasterMotionMaterial; // M11: LitResolve's MotionVectors pass copying the raster's motion (VG_MOTION_FROM_RASTER)
        BatchMaterialID m_RasterMotionMaterialId;
        bool m_AnyRasterMotionBin;
        readonly Dictionary<Camera, (SubView view, int slot)> m_VisCameras = new Dictionary<Camera, (SubView, int)>();
        // Per camera, as seen by the custom pass on its last rendered frame: null = can use the
        // visibility buffer, otherwise the reason it cannot. Culling runs before HDRP exposes the
        // frame's HDCamera (and render requests use a separate HDCamera), so the decision for frame
        // N uses what the custom pass observed on frame N-1; a camera's first frame uses expansion.
        readonly Dictionary<Camera, string> m_CameraBlockers = new Dictionary<Camera, string>();
        readonly List<Camera> m_DeadCameras = new List<Camera>();
        readonly List<int> m_BinSourceCrc = new List<int>();
        bool m_AnyResolveBin, m_AnyProgrammableBin;
        /// <summary>GPU/CPU sampler around the M11 programmable raster ("VG.RasterProgrammable").</summary>
        public static readonly CustomSampler ProgrammableSampler = CustomSampler.Create("VG.RasterProgrammable", true);
#if UNANITE_HDRP
        VgVisibilityCustomPass m_CustomPass;
        VgOcclusionHistoryPass m_HistoryPass;
#endif

        /// <summary>Visibility-buffer raster passes executed since creation (tests / diagnostics).</summary>
        public int VisibilityPassCount { get; private set; }

        int MaxVisibilityCameras => Mathf.Clamp(m_Settings.maxVisibilityBufferCameras, 1, 8);

        /// <summary>Tiles written by the last classification (debug / tests).</summary>
        public uint[] DebugReadClassifyStats()
        {
            var data = new uint[2];
            m_ClassifyStats?.GetData(data);
            return data;
        }

        static class VisIds
        {
            public static readonly int VisBuffer = Shader.PropertyToID("_VgVisBuffer");
            public static readonly int BaryBuffer = Shader.PropertyToID("_VgBaryBuffer");
            public static readonly int MotionBuffer = Shader.PropertyToID("_VgMotionBuffer");
            public static readonly int PhaseStateBase = Shader.PropertyToID("_VgPhaseStateBase");
            public static readonly int RasterPhase = Shader.PropertyToID("_VgRasterPhase");
            public static readonly int ClassifyParams = Shader.PropertyToID("_ClassifyParams");
            public static readonly int Meshes = Shader.PropertyToID("VG_Meshes");
            public static readonly int Instances = Shader.PropertyToID("VG_Instances");
            public static readonly int Visible = Shader.PropertyToID("VG_Visible");
            public static readonly int BinFlags = Shader.PropertyToID("VG_BinFlags");
            public static readonly int PagePool = Shader.PropertyToID("VG_PagePool");
            public static readonly int TileList = Shader.PropertyToID("VG_TileList");
            public static readonly int BinTileCount = Shader.PropertyToID("VG_BinTileCount");
            public static readonly int BinTileOffset = Shader.PropertyToID("VG_BinTileOffset");
            public static readonly int BinTileCursor = Shader.PropertyToID("VG_BinTileCursor");
            public static readonly int ResolveArgs = Shader.PropertyToID("VG_ResolveArgs");
            public static readonly int ClassifyStats = Shader.PropertyToID("VG_ClassifyStats");
        }

        void InitVisibility()
        {
            // M9: the visibility buffer is an RTHandle of the active pipeline's RTHandle system: release it
            // with the pipeline (quality level / pipeline asset switch), it is re-allocated on first use
            RenderPipelineManager.activeRenderPipelineDisposed += ReleaseVisBuffer;
            m_VisRegionCount = MaxVisibilityCameras;
            InitOcclusion();
            m_ClassifyStats = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2, 4);
            m_Classify = Resources.Load<ComputeShader>("UNanite/VgClassify");
            var raster = Shader.Find("Hidden/UNanite/VisBufferRaster");
            m_ResolveShader = Shader.Find("Hidden/UNanite/LitResolve");
            if (!HasKernels(m_Classify, "ClearBins", "CountTiles", "AllocateTiles", "WriteTiles") || raster == null || m_ResolveShader == null)
            {
                Debug.LogWarning("UNanite: visibility-buffer shaders not found; every view uses vertex expansion.");
                return;
            }
            k_ClearBins = m_Classify.FindKernel("ClearBins");
            k_CountTiles = m_Classify.FindKernel("CountTiles");
            k_AllocateTiles = m_Classify.FindKernel("AllocateTiles");
            k_WriteTiles = m_Classify.FindKernel("WriteTiles");
            m_RasterMaterial = new Material(raster) { hideFlags = HideFlags.HideAndDontSave, name = "UNanite VisBuffer Raster" };
            m_RasterProps = new MaterialPropertyBlock();
#if UNANITE_HDRP
            m_CustomPass = new VgVisibilityCustomPass { name = "UNanite Visibility Buffer" };
            CustomPassVolume.RegisterUniqueGlobalCustomPass(CustomPassInjectionPoint.BeforeRendering, m_CustomPass, 1000f);
            m_HistoryPass = new VgOcclusionHistoryPass { name = "UNanite After Depth" };
            CustomPassVolume.RegisterUniqueGlobalCustomPass(CustomPassInjectionPoint.AfterOpaqueDepthAndNormal, m_HistoryPass, 1000f);
#endif
        }

        void ReleaseVisBuffer()
        {
            if (m_VisBuffer != null)
                RTHandles.Release(m_VisBuffer);
            m_VisBuffer = null;
            if (m_BaryBuffer != null)
                RTHandles.Release(m_BaryBuffer);
            m_BaryBuffer = null;
            if (m_MotionBuffer != null)
                RTHandles.Release(m_MotionBuffer);
            m_MotionBuffer = null;
        }

        // M11: one material for every raster-motion bin (its MotionVectors pass only reads tiles and
        // the motion target; the GBuffer pass is off)
        BatchMaterialID RasterMotionMaterialId()
        {
            if (m_RasterMotionMaterial == null)
            {
                m_RasterMotionMaterial = new Material(m_ResolveShader) { hideFlags = HideFlags.HideAndDontSave, name = "UNanite Raster Motion" };
                m_RasterMotionMaterial.EnableKeyword("VG_MOTION_FROM_RASTER");
                m_RasterMotionMaterial.SetShaderPassEnabled("GBuffer", false);
                m_RasterMotionMaterial.SetShaderPassEnabled("MotionVectors", true);
                m_RasterMotionMaterialId = m_BRG.RegisterMaterial(m_RasterMotionMaterial);
            }
            return m_RasterMotionMaterialId;
        }

        void DisposeVisibility()
        {
#if UNANITE_HDRP
            if (m_CustomPass != null)
            {
                CustomPassVolume.UnregisterGlobalCustomPass(m_CustomPass);
                m_CustomPass = null;
            }
            if (m_HistoryPass != null)
            {
                CustomPassVolume.UnregisterGlobalCustomPass(m_HistoryPass);
                m_HistoryPass = null;
            }
#endif
            DisposeOcclusion();
            foreach (var b in new[] { m_BinTileCount, m_BinTileOffset, m_BinTileCursor, m_ResolveArgs, m_TileList, m_ClassifyStats })
                b?.Dispose();
            m_BinTileCount = m_BinTileOffset = m_BinTileCursor = m_ResolveArgs = m_TileList = m_ClassifyStats = null;
            if (m_RasterMaterial != null)
                CoreUtils.Destroy(m_RasterMaterial);
            if (m_RasterMotionMaterial != null)
                CoreUtils.Destroy(m_RasterMotionMaterial);
            RenderPipelineManager.activeRenderPipelineDisposed -= ReleaseVisBuffer;
            ReleaseVisBuffer();
        }

        void EnsureClassifyCapacity(int bins)
        {
            if (m_ResolveArgs != null && m_BinTileCount.count >= bins)
                return;
            foreach (var b in new[] { m_BinTileCount, m_BinTileOffset, m_BinTileCursor, m_ResolveArgs })
                b?.Dispose();
            m_BinTileCount = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bins, 4);
            m_BinTileOffset = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bins, 4);
            m_BinTileCursor = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bins, 4);
            m_ResolveArgs = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.IndirectArguments, bins * 4, 4);
            m_ResolveArgs.SetData(new uint[bins * 4]); // no tiles until the first classification
        }

        // ---------------------------------------------------------------------------------------
        // Material capability + twins

        /// <summary>
        /// A bin can be shaded by the resolve when its material is HDRP/Lit, opaque, not alpha
        /// tested and does no vertex-stage or depth work (displacement, tessellation, depth offset).
        /// Everything else keeps the expansion path, also in visibility-buffer views.
        /// </summary>
        public static bool IsResolveCapable(Material m)
        {
            if (m == null || m.shader == null || m.shader.name != "HDRP/Lit")
                return false;
            if (m.renderQueue >= (int)RenderQueue.AlphaTest)
                return false;
            foreach (var kw in s_NonResolveKeywords)
                if (m.IsKeywordEnabled(kw))
                    return false;
            return true;
        }

        // M10: HDRP/Lit -> Hidden/UNanite/LitResolve; Shader Graphs whose generated VG variant has a
        // resolve pass (graphs that do not move vertices), opaque and without alpha clip
        bool ResolveShaderFor(Material source, out Shader shader, out bool shaderGraph, out bool programmable)
        {
            shader = null;
            shaderGraph = false;
            programmable = false;
            if (source == null)
                return false;
            if (m_ResolveShader != null && m_ResolveShader.isSupported && IsResolveCapable(source))
            {
                shader = m_ResolveShader;
                return true;
            }
            if (!m_Settings.pulledMaterials || !m_PulledSupported || source.renderQueue > (int)RenderQueue.GeometryLast ||
                !VgShaderVariants.TryGet(source.shader, out var entry))
                return false;
            bool clean = source.renderQueue < (int)RenderQueue.AlphaTest;
            foreach (var kw in s_NonResolveKeywords)
                if (source.IsKeywordEnabled(kw))
                    clean = false;
            // M11: alpha-clipped / vertex-moving graphs rasterise with their own VgVisBuffer pass
            programmable = !(clean && entry.resolve);
            if (programmable && (!m_Settings.programmableRaster || !entry.programmable || source.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT") ||
                                 source.IsKeywordEnabled("_TESSELLATION_DISPLACEMENT") || source.IsKeywordEnabled("_DEPTHOFFSET_ON")))
                return false;
            shader = entry.variant;
            shaderGraph = true;
            return true;
        }

        // Shader Graph resolve twins draw screen tiles through the graph's GBuffer pass (VG_RESOLVE):
        // no culling, LEqual against the visibility-buffer depth; every other pass off (their pulled
        // vertex stage would read tile vertices). Moving instances keep camera motion vectors (the
        // graph's motion pass writes more targets than the resolve fills).
        static void ConfigureShaderGraphResolveTwin(Material twin, bool programmable, bool wind)
        {
            twin.EnableKeyword("VG_RESOLVE");
            if (!wind)
                DisableSpeedTreeWind(twin);
            // the programmable raster already clipped: the resolve shades every pixel it wrote
            if (programmable)
                twin.DisableKeyword("_ALPHATEST_ON");
            twin.SetFloat("_CullMode", (float)CullMode.Off);
            twin.SetFloat("_ZTestGBuffer", (float)CompareFunction.LessEqual);
            foreach (var pass in s_ShaderGraphResolveOffPasses)
                twin.SetShaderPassEnabled(pass, false);
        }

        static readonly string[] s_ShaderGraphResolveOffPasses =
        {
            "ShadowCaster", "DepthOnly", "DepthForwardOnly", "Forward", "ForwardOnly", "MotionVectors",
            "TransparentDepthPrepass", "TransparentDepthPostpass", "TransparentBackface",
            // M11: only raster twins may draw in the programmable raster's renderer list (a resolve
            // draw's procedural vertex IDs read as arena indices there rasterise garbage)
            "VgVisBuffer",
        };

        bool IsShaderGraphResolveBin(int bin) => m_BinResolveMaterials[bin] != null && !m_BinResolveOverride[bin] && m_BinResolveMaterials[bin].IsKeywordEnabled("VG_RESOLVE");

        static readonly string[] s_NonResolveKeywords =
        {
            "_ALPHATEST_ON", "_SURFACE_TYPE_TRANSPARENT", "_VERTEX_DISPLACEMENT", "_PIXEL_DISPLACEMENT",
            "_DEPTHOFFSET_ON", "_TESSELLATION_DISPLACEMENT", "_TESSELLATION_PHONG",
        };

        /// <summary>Double-sided materials disable cluster cone culling and rasterise without back-face culling.</summary>
        public static bool IsDoubleSided(Material m)
        {
            if (m == null)
                return false;
            if (m.IsKeywordEnabled("_DOUBLESIDED_ON"))
                return true;
            // HDRP/URP and most Shader Graphs expose the cull mode as a float property (0 = Off)
            foreach (var name in s_CullProperties)
                if (m.HasProperty(name) && Mathf.RoundToInt(m.GetFloat(name)) == (int)CullMode.Off)
                    return true;
            return false;
        }

        static readonly string[] s_CullProperties = { "_CullMode", "_Cull" };

        // M8: materials whose resolve is provided by their owner (terrain: TerrainLitResolve), keyed
        // by the bin's source material (the expansion / shadow fallback). Not owned by the world.
        static readonly Dictionary<Material, Material> s_ResolveOverrides = new Dictionary<Material, Material>();

        /// <summary>
        /// Registers `resolve` (a material with a GBuffer resolve pass, e.g. Hidden/UNanite/TerrainLitResolve)
        /// as the visibility-buffer material of bins using `source`; null removes it. `source` stays the
        /// material of expansion views and shadows.
        /// </summary>
        public static void SetResolveOverride(Material source, Material resolve)
        {
            if (source == null)
                return;
            if (resolve == null)
                s_ResolveOverrides.Remove(source);
            else
                s_ResolveOverrides[source] = resolve;
            var world = s_Instance;
            if (world == null)
                return;
            for (int b = 0; b < world.m_BinMaterials.Count; ++b)
                if (world.m_BinMaterials[b] == source)
                    world.RefreshBin(b);
        }

        void RefreshBin(int bin)
        {
            var source = m_BinMaterials[bin];
            uint flags = (IsDoubleSided(source) ? BinFlagDoubleSided : 0u) | ShadowBinFlag(source);
            if (source != null && s_ResolveOverrides.TryGetValue(source, out var resolve) && resolve != null && resolve.shader.isSupported)
            {
                flags |= BinFlagResolve;
                if (m_BinResolveMaterials[bin] != resolve)
                {
                    if (m_BinResolveMaterials[bin] != null && !m_BinResolveOverride[bin])
                        CoreUtils.Destroy(m_BinResolveMaterials[bin]);
                    m_BinResolveMaterials[bin] = resolve;
                    m_BinResolveIds[bin] = m_BRG.RegisterMaterial(resolve);
                    m_BinResolveOverride[bin] = true;
                }
            }
            else if (ResolveShaderFor(source, out var resolveShader, out bool shaderGraph, out bool programmable))
            {
                flags |= BinFlagResolve | (programmable ? BinFlagProgrammable : 0u);

                while (m_BinSourceCrc.Count <= bin)
                    m_BinSourceCrc.Add(0);
                var twin = m_BinResolveMaterials[bin];
                if (m_BinResolveOverride[bin])
                {
                    twin = null; // an override was removed: back to an owned twin
                    m_BinResolveOverride[bin] = false;
                }
                if (twin != null && twin.shader != resolveShader)
                {
                    CoreUtils.Destroy(twin);
                    twin = null;
                }
                if (twin == null)
                {
                    twin = new Material(resolveShader) { hideFlags = HideFlags.HideAndDontSave, name = source.name + " (VG resolve)" };
                    m_BinResolveMaterials[bin] = twin;
                    m_BinResolveIds[bin] = m_BRG.RegisterMaterial(twin);
                    m_BinSourceCrc[bin] = 0;
                }
                int crc = source.ComputeCRC();
                if (crc != m_BinSourceCrc[bin] || m_BinSourceCrc[bin] == 0)
                {
                    m_BinSourceCrc[bin] = crc;
                    twin.CopyPropertiesFromMaterial(source);
                    twin.shaderKeywords = source.shaderKeywords;
                    // M9: HDRP/Lit disables its own MotionVectors pass unless it needs it and the copy
                    // carries that over; the twin's pass draws object motion of moving instances
                    twin.SetShaderPassEnabled("MotionVectors", true);
                    if (shaderGraph)
                        ConfigureShaderGraphResolveTwin(twin, programmable, BinHasWind(bin));
                    ApplyLightmapTwin(bin, twin, true);
                }
                else
                    ApplyLightmapTwin(bin, twin, false);
            }
            flags |= RefreshPulled(bin, source);
            RefreshRasterTwin(bin, source, flags);
            if ((flags & BinFlagProgrammable) != 0 && m_BinRasterMaterials[bin] != null && BarycentricRaster)
            {
                flags |= BinFlagBarycentrics; // M11: resolved from the raster's barycentrics
                if (m_BinRasterMaterials[bin].IsKeywordEnabled("VG_RASTER_MOTION"))
                    flags |= BinFlagRasterMotion; // and its motion vectors from the raster's motion
            }
            if (m_BinFlags[bin] != flags)
            {
                m_BinFlags[bin] = flags;
                m_BinFlagsDirty = true;
            }
        }

        // ---------------------------------------------------------------------------------------
        // Frame

        void BeginVisibilityFrame()
        {
            m_VisRegionsUsed = 0;
            m_VisCameras.Clear();
            if (m_CameraBlockers.Count > 0 && Time.frameCount % 64 == 0)
            {
                m_DeadCameras.Clear();
                foreach (var cam in m_CameraBlockers.Keys)
                    if (cam == null)
                        m_DeadCameras.Add(cam);
                foreach (var cam in m_DeadCameras)
                    m_CameraBlockers.Remove(cam);
                PruneOcclusionHistory();
                PruneShadowCameras();
            }
            m_AnyResolveBin = m_AnyProgrammableBin = m_AnyRasterMotionBin = false;
            for (int b = 0; b < m_BinFlags.Count; ++b)
                if (m_BinRefCount[b] > 0 && (m_BinFlags[b] & BinFlagResolve) != 0)
                {
                    m_AnyResolveBin = true;
                    m_AnyProgrammableBin |= IsProgrammableBin(b);
                    m_AnyRasterMotionBin |= IsRasterMotionBin(b);
                }
            if (!m_AnyResolveBin || m_PagePool == null)
                return;

            // read by the raster and resolve shaders (globals: the resolve draws are issued by HDRP)
            Shader.SetGlobalBuffer(VisIds.Meshes, m_MeshBuffer);
            Shader.SetGlobalBuffer(VisIds.Instances, m_InstanceBuffer);
            Shader.SetGlobalBuffer(VisIds.Visible, m_Visible);
            Shader.SetGlobalBuffer(VisIds.BinFlags, m_BinFlagsBuffer);
            Shader.SetGlobalBuffer(VisIds.PagePool, m_PagePool);
            Shader.SetGlobalBuffer(Ids.PhaseState, m_PhaseState);
            Shader.SetGlobalBuffer(SwIds.RasterLists, m_RasterLists);
        }

        /// <summary>Returns the VG_Visible region (1..N) of a camera using the visibility buffer, or 0.</summary>
        int AssignVisibilityRegion(Camera camera)
        {
            string reason = null;
            if (!m_Settings.visibilityBuffer)
                reason = "disabled in settings";
            else if (DebugView != VgDebugView.None)
                reason = "debug view active";
            else if (!m_AnyResolveBin)
                reason = "no resolve-capable material";
            else if (m_RasterMaterial == null)
                reason = "shaders missing";
            else if (m_VisRegionsUsed >= m_VisRegionCount)
                reason = "too many visibility-buffer cameras this frame";
            else
                reason = VisibilityBufferBlocker(camera);
            LastVisibilityDecision = $"{(camera != null ? camera.name : "<null>")}: {reason ?? "visibility buffer"}";
            return reason == null ? ++m_VisRegionsUsed : 0;
        }

        /// <summary>Why the last culled camera did or did not use the visibility buffer (diagnostics).</summary>
        public string LastVisibilityDecision { get; private set; }

        void RegisterVisibilityCamera(in SubView view, int slot)
        {
            if (view.camera != null && view.visRegion > 0)
                m_VisCameras[view.camera] = (view, slot);
        }

#if UNANITE_HDRP
        // null = the camera can use the visibility buffer, otherwise the reason it cannot
        string VisibilityBufferBlocker(Camera camera)
        {
            if (camera == null)
                return "not a camera view";
            if (m_CustomPass == null || !TextureXR.useTexArray)
                return "platform";
            if (camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView)
                return $"camera type {camera.cameraType}";
            if (camera.stereoEnabled)
                return "XR";
            if (!m_CameraBlockers.TryGetValue(camera, out var blocker))
                return "first frame (custom pass not seen yet)";
            return blocker;
        }

        // Evaluated inside the custom pass, where HDRP provides the camera's actual HDCamera.
        static string FrameBlocker(HDCamera hd)
        {
            var fs = hd.frameSettings;
            if (fs.litShaderMode != LitShaderMode.Deferred)
                return "forward rendering";
            if (hd.msaaEnabled)
                return "MSAA";
            return null; // custom passes are enabled, otherwise this would not run
        }

        /// <summary>BeforeRendering custom pass body: raster + classification for one camera.</summary>
        internal void RenderVisibility(CustomPassContext ctx)
        {
            var camera = ctx.hdCamera.camera;
            m_CameraBlockers[camera] = FrameBlocker(ctx.hdCamera);
            if (!m_VisCameras.TryGetValue(camera, out var view))
                return;

            VisibilityPassCount++;
            int width = ctx.hdCamera.actualWidth, height = ctx.hdCamera.actualHeight;
            m_VisBuffer ??= RTHandles.Alloc(Vector2.one, TextureXR.slices, dimension: TextureXR.dimension,
                colorFormat: GraphicsFormat.R32G32_UInt, useDynamicScale: true, name: "UNanite VisBuffer");

            int tilesX = (width + 7) / 8, tilesY = (height + 7) / 8;
            int tileCapacity = Mathf.Max(1024, tilesX * tilesY * Mathf.Max(1, m_Settings.resolveTileListFactor));
            if (m_TileList == null || m_TileList.count < tileCapacity)
            {
                m_TileList?.Dispose();
                m_TileList = new GraphicsBuffer(GraphicsBuffer.Target.Structured, tileCapacity, 8);
            }

            var cmd = ctx.cmd;
            CoreUtils.SetRenderTarget(cmd, m_VisBuffer, ctx.cameraDepthBuffer, ClearFlag.Color, Color.clear);

            // M4: depth of regular opaque meshes first, so they occlude VG in the raster and in the
            // phase-2 HZB (HDRP draws them again in its own depth prepass with the same depth).
            if (view.view.occlusion && m_Settings.occlusionFromMeshRenderers)
            {
                cmd.BeginSample(OccluderSampler);
                // alpha-tested renderers (foliage, grass) are poor occluders and expensive to draw twice
                var queue = m_Settings.occluderAlphaTested ? CustomPass.RenderQueueType.AllOpaque : CustomPass.RenderQueueType.OpaqueNoAlphaTest;
                CustomPassUtils.DrawRenderers(ctx, s_DepthTags, ~0, queue, null, 0, s_NoColorWrites);
                cmd.EndSample(OccluderSampler);
            }

            cmd.BeginSample(RasterSampler);
            SetRasterListProps(m_RasterProps, view.view.visRegion);
            m_RasterProps.SetInteger(VisIds.PhaseStateBase, PhaseStateBase(view.view.visRegion));
            m_RasterProps.SetInteger(VisIds.RasterPhase, 0);
            int argsBytes = view.slot * k_RasterArgsStride * 4;
            cmd.DrawProceduralIndirect(Matrix4x4.identity, m_RasterMaterial, 0, MeshTopology.Triangles, m_RasterArgs, argsBytes, m_RasterProps);
            cmd.DrawProceduralIndirect(Matrix4x4.identity, m_RasterMaterial, 1, MeshTopology.Triangles, m_RasterArgs, argsBytes + 16, m_RasterProps);
            cmd.EndSample(RasterSampler);
            if (view.view.swEnabled)
                RenderSoftware(cmd, m_VisBuffer, ctx.cameraDepthBuffer, view.view, view.slot, 0, width, height);

            // M11 programmable raster: foliage bins through their own VgVisBuffer pass (raster twins),
            // before the HZB so they occlude too
            if (m_AnyProgrammableBin)
            {
                cmd.BeginSample(ProgrammableSampler);
                if (BarycentricRaster)
                {
                    // M11: + the barycentrics target (read only where the visibility buffer shows such a bin)
                    m_BaryBuffer ??= RTHandles.Alloc(Vector2.one, TextureXR.slices, dimension: TextureXR.dimension,
                        colorFormat: GraphicsFormat.R32_UInt, useDynamicScale: true, name: "UNanite Barycentrics");
                    if (m_AnyRasterMotionBin)
                    {
                        // + the motion target of vertex-animated bins (read where the visibility buffer shows one)
                        m_MotionBuffer ??= RTHandles.Alloc(Vector2.one, TextureXR.slices, dimension: TextureXR.dimension,
                            colorFormat: GraphicsFormat.R16G16_SFloat, useDynamicScale: true, name: "UNanite Raster Motion");
                        m_ProgrammableMotionTargets[0] = m_VisBuffer;
                        m_ProgrammableMotionTargets[1] = m_BaryBuffer;
                        m_ProgrammableMotionTargets[2] = m_MotionBuffer;
                        CoreUtils.SetRenderTarget(cmd, m_ProgrammableMotionTargets, ctx.cameraDepthBuffer, ClearFlag.None);
                    }
                    else
                    {
                        m_ProgrammableTargets[0] = m_VisBuffer;
                        m_ProgrammableTargets[1] = m_BaryBuffer;
                        CoreUtils.SetRenderTarget(cmd, m_ProgrammableTargets, ctx.cameraDepthBuffer, ClearFlag.None);
                    }
                }
                else
                    CoreUtils.SetRenderTarget(cmd, m_VisBuffer, ctx.cameraDepthBuffer, ClearFlag.None);
                CustomPassUtils.DrawRenderers(ctx, new[] { s_VisRasterTag }, ~0, CustomPass.RenderQueueType.AllOpaque);
                cmd.EndSample(ProgrammableSampler);
            }

            // M4 phase 2: HZB of the phase-1 depth, re-test deferred work, raster what became visible
            if (view.view.occPhase1 && !view.view.frozen)
            {
                CoreUtils.SetRenderTarget(cmd, m_VisBuffer, ClearFlag.None); // depth is read by VG.BuildHZB
                RecordPhase2(cmd, ctx.cameraDepthBuffer, view.view, view.slot, width, height);
                cmd.BeginSample(RasterSampler);
                CoreUtils.SetRenderTarget(cmd, m_VisBuffer, ctx.cameraDepthBuffer, ClearFlag.None);
                m_RasterProps.SetInteger(VisIds.RasterPhase, 1);
                cmd.DrawProceduralIndirect(Matrix4x4.identity, m_RasterMaterial, 0, MeshTopology.Triangles, m_RasterArgs, argsBytes + 32, m_RasterProps);
                cmd.DrawProceduralIndirect(Matrix4x4.identity, m_RasterMaterial, 1, MeshTopology.Triangles, m_RasterArgs, argsBytes + 48, m_RasterProps);
                cmd.EndSample(RasterSampler);
                if (view.view.swEnabled)
                    RenderSoftware(cmd, m_VisBuffer, ctx.cameraDepthBuffer, view.view, view.slot, 1, width, height);
            }

            cmd.BeginSample(ClassifySampler);
            int bins = m_BinCapacity;
            cmd.SetComputeIntParams(m_Classify, VisIds.ClassifyParams, width, height, bins, m_TileList.count);
            foreach (int k in new[] { k_ClearBins, k_CountTiles, k_AllocateTiles, k_WriteTiles })
            {
                cmd.SetComputeTextureParam(m_Classify, k, VisIds.VisBuffer, m_VisBuffer);
                cmd.SetComputeBufferParam(m_Classify, k, VisIds.Visible, m_Visible);
                cmd.SetComputeBufferParam(m_Classify, k, VisIds.BinTileCount, m_BinTileCount);
                cmd.SetComputeBufferParam(m_Classify, k, VisIds.BinTileOffset, m_BinTileOffset);
                cmd.SetComputeBufferParam(m_Classify, k, VisIds.BinTileCursor, m_BinTileCursor);
                cmd.SetComputeBufferParam(m_Classify, k, VisIds.ResolveArgs, m_ResolveArgs);
                cmd.SetComputeBufferParam(m_Classify, k, VisIds.TileList, m_TileList);
                cmd.SetComputeBufferParam(m_Classify, k, VisIds.ClassifyStats, m_ClassifyStats);
            }
            cmd.DispatchCompute(m_Classify, k_ClearBins, (bins + 63) / 64, 1, 1);
            cmd.DispatchCompute(m_Classify, k_CountTiles, tilesX, tilesY, 1);
            cmd.DispatchCompute(m_Classify, k_AllocateTiles, 1, 1, 1);
            cmd.DispatchCompute(m_Classify, k_WriteTiles, tilesX, tilesY, 1);
            cmd.EndSample(ClassifySampler);

            RenderRvt(ctx, width, height); // M12: tile bakes + feedback of VG terrains' virtual textures

            // consumed by the resolve draws in HDRP's GBuffer pass of this camera
            cmd.SetGlobalTexture(VisIds.VisBuffer, m_VisBuffer);
            if (m_BaryBuffer != null)
                cmd.SetGlobalTexture(VisIds.BaryBuffer, m_BaryBuffer);
            if (m_MotionBuffer != null)
                cmd.SetGlobalTexture(VisIds.MotionBuffer, m_MotionBuffer);
            cmd.SetGlobalBuffer(VisIds.TileList, m_TileList);
            cmd.SetGlobalBuffer(VisIds.BinTileOffset, m_BinTileOffset);
        }
#else
        string VisibilityBufferBlocker(Camera camera) => "requires HDRP";
#endif
    }

#if UNANITE_HDRP
    /// <summary>Global HDRP custom pass (BeforeRendering) driving the visibility-buffer raster.</summary>
    sealed class VgVisibilityCustomPass : CustomPass
    {
        protected override void Execute(CustomPassContext ctx)
        {
            VgWorld.Instance?.RenderVisibility(ctx);
        }
    }

    /// <summary>Global HDRP custom pass (AfterOpaqueDepthAndNormal), with the complete opaque depth
    /// (VG + regular meshes): HZB for the next frame's phase-1 occlusion test (M4), receiver
    /// culling of the shadow splits deferred to this camera (M6; HDRP renders shadow maps later),
    /// the sun shadow clipmap (M13b; HDRP's lighting reads it later through the optional patch).</summary>
    sealed class VgOcclusionHistoryPass : CustomPass
    {
        protected override void Execute(CustomPassContext ctx)
        {
            var world = VgWorld.Instance;
            if (world == null)
                return;
            world.RenderOcclusionHistory(ctx);
            world.RenderDeferredShadows(ctx);
            world.RenderSunClipmap(ctx); // M13b
        }
    }
#endif
}
