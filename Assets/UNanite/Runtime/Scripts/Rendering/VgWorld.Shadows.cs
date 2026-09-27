using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
#if UNANITE_HDRP
using UnityEngine.Rendering.HighDefinition;
#endif

namespace UNanite
{
    // M6: virtual geometry shadows.
    //
    //   vertex-pulled shadow raster   shadow splits keep VG_BIN_SHADOW bins out of the expansion; their
    //                                 visible records are compacted into VG_ShadowRecords, their
    //                                 triangles written as indices only (record << 7 | vertex) into the
    //                                 arena index buffer and drawn by two BRG indexed indirect draws of
    //                                 the arena mesh per split (Cull Back / Off) with
    //                                 Hidden/UNanite/ShadowRaster inside HDRP's shadow pass
    //   receiver culling              for cameras that ran the AfterOpaqueDepthAndNormal pass on their
    //                                 previous frame, a light's splits are only placeholders at culling
    //                                 time (empty args); the pass - which HDRP runs before rendering
    //                                 shadow maps - builds each split's receiver pyramid from the
    //                                 current camera depth (VG.ShadowReceivers) and culls the split
    //                                 against it (VG.CullView, _OccConfig.x = 3)
    public sealed unsafe partial class VgWorld
    {
        /// <summary>GPU/CPU sampler around the receiver pyramids of deferred shadow splits ("VG.ShadowReceivers").</summary>
        public static readonly CustomSampler ReceiverSampler = CustomSampler.Create("VG.ShadowReceivers", true);

        const uint BinFlagShadow = 4u;        // VG_BIN_SHADOW
        const int k_ReceiverSize = 256;       // receiver pyramid level 0 (texels per side)
        const int k_ReceiverTile = 16;        // camera pixels per tile side (VgShadowReceivers.compute)
        const int k_ReceiverBatch = 16;       // splits whose pyramids are built by one set of dispatches

        GraphicsBuffer m_ShadowRecords, m_ShadowViews, m_TileDepth, m_RcvSplat;
        readonly OcclusionHistory m_Receivers = new OcclusionHistory(); // k_ReceiverBatch pyramids of m_ReceiverTexels
        int m_ReceiverTexels;
        readonly Matrix4x4[] m_BatchMatrices = new Matrix4x4[k_ReceiverBatch];
        readonly Vector4[] m_BatchSpheres = new Vector4[k_ReceiverBatch];
        ComputeShader m_RcvCs;
        int k_RcvTileDepth, k_RcvTileUp, k_RcvClear, k_RcvSplat, k_RcvFinalize, k_RcvPyramid;
        int k_PrepareShadowRaster, k_CompactShadow, k_ClearShadowView, k_PrepareBatchCompact;
        GraphicsBuffer m_SplitViews;              // VgSplitView per split of a batch (RecordBatch)
        readonly float[] m_SplitViewData = new float[k_MaxBatchSplits * k_SplitViewStride / 4];
        readonly Material[] m_ShadowMaterials = new Material[2];
        readonly BatchMaterialID[] m_ShadowMaterialIds = new BatchMaterialID[2];
        bool m_ShadowRasterSupported;
        readonly bool[] m_AnyShadowBin = new bool[2]; // per cull mode (single / double-sided), this frame

        // Camera whose shadow splits are being culled, and its position + perspective LOD term
        // (shadowCameraLod). Set by the camera view of the BRG callback; a light's splits re-resolve
        // it (ResolveShadowOwner): HDRP 17 culls every camera of the frame first (a planar reflection
        // probe after the main camera) and their lights after that, so "the last culled camera" is not
        // the owner of a split. With it the caster frustum of the main camera's cascades was built from
        // the probe's camera (near cascade empty) and receiver culling deferred them to that camera's pass.
        Camera m_CullOwner
        {
            get => m_CullOwnerCamera;
            set => m_CullOwnerCamera = value;
        }
        Vector4 m_CullOwnerLod
        {
            get => m_CullOwnerLodValue;
            set
            {
                m_CullOwnerLodValue = value;
                if (m_CullOwnerCamera != null)
                    RememberCulledCamera(m_CullOwnerCamera, value);
            }
        }
        Camera m_CullOwnerCamera;
        Vector4 m_CullOwnerLodValue;
        // cameras culled this frame with their LOD vector (position, perspective term)
        readonly List<(Camera camera, Vector4 lod)> m_CulledCameras = new List<(Camera, Vector4)>();
        readonly Dictionary<Camera, List<(SubView view, int slot)>> m_PendingShadows = new Dictionary<Camera, List<(SubView, int)>>();
        // camera ran the AfterOpaqueDepthAndNormal pass on its last rendered frame (and it culled
        // every split deferred to it); cameras not in here cull their shadow splits immediately
        readonly Dictionary<Camera, bool> m_ShadowPassSeen = new Dictionary<Camera, bool>();

        /// <summary>Shadow splits culled inside the custom pass with receiver culling since creation (tests / diagnostics).</summary>
        public int DeferredShadowSplitCount { get; private set; }

        /// <summary>Why the last shadow split was or was not receiver-culled (diagnostics).</summary>
        public string LastShadowDecision { get; private set; }

        static class ShadowIds
        {
            public static readonly int ShadowRecords = Shader.PropertyToID("VG_ShadowRecords");
            public static readonly int ShadowViews = Shader.PropertyToID("VG_ShadowViews");
            public static readonly int CullMode = Shader.PropertyToID("_VgCullMode");
            public static readonly int TileDepth = Shader.PropertyToID("VG_TileDepth");
            public static readonly int RcvSplat = Shader.PropertyToID("VG_RcvSplat");
            public static readonly int CamInvViewProj = Shader.PropertyToID("_RcvCamInvViewProj");
            public static readonly int LightViewProj = Shader.PropertyToID("_RcvLightViewProj");
            public static readonly int Proj = Shader.PropertyToID("_RcvProj");
            public static readonly int Range = Shader.PropertyToID("_RcvRange");
            public static readonly int Sphere = Shader.PropertyToID("_RcvSphere");
            public static readonly int Margin = Shader.PropertyToID("_RcvMargin");
            public static readonly int SkyDepth = Shader.PropertyToID("_RcvSkyDepth");
            public static readonly int Screen = Shader.PropertyToID("_RcvScreen");
            public static readonly int Mask = Shader.PropertyToID("_RcvMask");
            public static readonly int Level = Shader.PropertyToID("_RcvLevel");
            public static readonly int Splits = Shader.PropertyToID("_RcvSplits");
        }

        void InitShadows()
        {
            k_PrepareShadowRaster = RequireKernel(m_Cull, "PrepareShadowRaster");
            k_CompactShadow = RequireKernel(m_Cull, "CompactShadow");
            k_ClearShadowView = RequireKernel(m_Cull, "ClearShadowView");
            k_PrepareBatchCompact = RequireKernel(m_Cull, "PrepareBatchCompact");
            m_ShadowRecords = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1024, m_Settings.shadowRecordCapacity), 16);
            m_ShadowViews = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, m_Settings.maxViewsPerFrame), 16);
            m_ShadowViews.SetData(new uint[m_ShadowViews.count * 4]);

            var shader = Shader.Find("Hidden/UNanite/ShadowRaster");
            m_ShadowRasterSupported = shader != null && shader.isSupported;
            if (m_ShadowRasterSupported)
            {
                for (int list = 0; list < 2; ++list)
                {
                    var m = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, name = list == 0 ? "UNanite Shadow Raster" : "UNanite Shadow Raster (double-sided)" };
                    m.SetFloat(ShadowIds.CullMode, list == 0 ? (float)CullMode.Back : (float)CullMode.Off);
                    m_ShadowMaterials[list] = m;
                    m_ShadowMaterialIds[list] = m_BRG.RegisterMaterial(m);
                }
            }
            else
            {
                Debug.LogWarning("UNanite: Hidden/UNanite/ShadowRaster unavailable; shadow maps use vertex expansion.");
            }

            m_RcvCs = Resources.Load<ComputeShader>("UNanite/VgShadowReceivers");
            if (!HasKernels(m_RcvCs, "TileDepth", "TileUp", "ClearSplat", "Splat", "Finalize", "Pyramid"))
            {
                m_RcvCs = null; // receiver culling disabled
                return;
            }
            k_RcvTileDepth = m_RcvCs.FindKernel("TileDepth");
            k_RcvTileUp = m_RcvCs.FindKernel("TileUp");
            k_RcvClear = m_RcvCs.FindKernel("ClearSplat");
            k_RcvSplat = m_RcvCs.FindKernel("Splat");
            k_RcvFinalize = m_RcvCs.FindKernel("Finalize");
            k_RcvPyramid = m_RcvCs.FindKernel("Pyramid");
            EnsureHzb(m_Receivers, 2 * k_ReceiverSize, 2 * k_ReceiverSize); // 256^2 .. 1^2: 9 levels (Pyramid does 4 + 4)
            m_ReceiverTexels = m_Receivers.hzb.count;
            m_Receivers.hzb.Dispose();
            m_Receivers.hzb = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_ReceiverTexels * k_ReceiverBatch, 4);
            m_RcvSplat = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_ReceiverTexels * k_ReceiverBatch, 4);
        }

        void DisposeShadows()
        {
            foreach (var b in new[] { m_ShadowRecords, m_ShadowViews, m_TileDepth, m_RcvSplat, m_Receivers.hzb })
                b?.Dispose();
            m_ShadowRecords = m_ShadowViews = m_TileDepth = m_RcvSplat = m_Receivers.hzb = null;
            for (int i = 0; i < 2; ++i)
                if (m_ShadowMaterials[i] != null)
                    CoreUtils.Destroy(m_ShadowMaterials[i]);
            m_PendingShadows.Clear();
        }

        /// <summary>
        /// A bin can use the vertex-pulled shadow raster when its ShadowCaster pass writes plain
        /// geometry depth: HDRP/Lit, or a Shader Graph whose generated variant keeps the object-space
        /// position (VgShaderVariants resolve), without alpha test, displacement, tessellation or depth offset.
        /// </summary>
        public static bool IsShadowRasterCapable(Material m)
        {
            if (m == null || m.shader == null)
                return false;
            // HDRP/Lit, graphs proven free of vertex displacement, or shaders declared free of it
            if (m.shader.name != "HDRP/Lit" && !(VgShaderVariants.TryGet(m.shader, out var entry) && entry.resolve) &&
                System.Array.IndexOf(VirtualGeometrySettings.Active.shadowRasterShaders ?? new string[0], m.shader.name) < 0)
                return false;
            if (!m.GetShaderPassEnabled("ShadowCaster"))
                return false;
            foreach (var kw in s_NonShadowRasterKeywords)
                if (m.IsKeywordEnabled(kw))
                    return false;
            return true;
        }

        static readonly string[] s_NonShadowRasterKeywords =
        {
            "_ALPHATEST_ON", "_VERTEX_DISPLACEMENT", "_PIXEL_DISPLACEMENT", "_DEPTHOFFSET_ON",
            "_TESSELLATION_DISPLACEMENT", "_TESSELLATION_PHONG",
        };

        uint ShadowBinFlag(Material source) => m_ShadowRasterSupported && IsShadowRasterCapable(source) ? BinFlagShadow : 0u;

        // ---------------------------------------------------------------------------------------
        // Frame

        void BeginShadowFrame()
        {
            m_CullOwner = null;
            m_CulledCameras.Clear();
            // splits deferred to a camera whose pass never ran: that camera culls immediately from now on
            foreach (var pair in m_PendingShadows)
                if (pair.Value.Count > 0)
                {
                    if (pair.Key != null)
                        m_ShadowPassSeen[pair.Key] = false;
                    pair.Value.Clear();
                }

            m_AnyShadowBin[0] = m_AnyShadowBin[1] = false;
            for (int b = 0; b < m_BinFlags.Count; ++b)
                if (m_BinRefCount[b] > 0 && (m_BinFlags[b] & BinFlagShadow) != 0)
                    m_AnyShadowBin[(m_BinFlags[b] & BinFlagDoubleSided) != 0 ? 1 : 0] = true;
            if (m_PagePool == null || !(m_AnyShadowBin[0] || m_AnyShadowBin[1]))
                return;
            // read by the shadow raster draws, which HDRP issues
            Shader.SetGlobalBuffer(VisIds.Meshes, m_MeshBuffer);
            Shader.SetGlobalBuffer(VisIds.Instances, m_InstanceBuffer);
            Shader.SetGlobalBuffer(VisIds.PagePool, m_PagePool);
            Shader.SetGlobalBuffer(ShadowIds.ShadowRecords, m_ShadowRecords);
            Shader.SetGlobalBuffer(ShadowIds.ShadowViews, m_ShadowViews);
        }

        void PruneShadowCameras()
        {
            m_DeadCameras.Clear();
            foreach (var cam in m_ShadowPassSeen.Keys)
                if (cam == null)
                    m_DeadCameras.Add(cam);
            foreach (var cam in m_DeadCameras)
            {
                m_ShadowPassSeen.Remove(cam);
                m_PendingShadows.Remove(cam);
            }
        }

        void RememberCulledCamera(Camera camera, Vector4 lod)
        {
            for (int i = 0; i < m_CulledCameras.Count; ++i)
                if (m_CulledCameras[i].camera == camera)
                {
                    m_CulledCameras[i] = (camera, lod);
                    return;
                }
            m_CulledCameras.Add((camera, lod));
        }

        // The camera a light's splits belong to: Unity culls shadow casters with the owning camera's
        // LOD parameters, so its position (and field of view, for cameras at one spot such as a scope
        // camera under the main one) picks it among this frame's culled cameras.
        void ResolveShadowOwner(in BatchCullingContext ctx)
        {
            var lod = ctx.lodParameters;
            Camera best = null;
            Vector4 bestLod = Vector4.zero;
            float bestScore = float.MaxValue;
            foreach (var (camera, camLod) in m_CulledCameras)
            {
                if (camera == null)
                    continue;
                float score = (camera.transform.position - lod.cameraPosition).sqrMagnitude;
                if (!lod.isOrthographic && !camera.orthographic)
                    score += Mathf.Abs(camera.fieldOfView - lod.fieldOfView) * 0.01f;
                else if (lod.isOrthographic != camera.orthographic)
                    score += 1e6f;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = camera;
                    bestLod = camLod;
                }
            }
            if (best == null)
                return; // no camera view this frame (e.g. culling-layer early-out): keep the last one
            m_CullOwnerCamera = best;
            m_CullOwnerLodValue = bestLod;
        }

        // Culling-time configuration of a shadow split (after the generic sub-view setup).
        void ConfigureShadowView(ref SubView sv, in BatchCullingContext ctx, in CullingSplit split, int splitIndex)
        {
            ResolveShadowOwner(ctx);
            sv.shadowRaster = m_Settings.shadowRaster && m_ShadowRasterSupported;
            sv.lightViewProj = split.cullingMatrix;
            sv.splitSphere = new Vector4(split.sphereCenter.x, split.sphereCenter.y, split.sphereCenter.z, split.sphereRadius);
            sv.camera = m_CullOwner;
            // casters are never finer than the camera needs them (low sun: the near cascades' caster
            // volumes reach kilometres of terrain at the finest texel LOD otherwise)
            sv.camLod = m_Settings.shadowCameraLod && m_CullOwner != null ? m_CullOwnerLod : Vector4.zero;
            sv.basePlaneCount = sv.planeCount;
            if (m_Settings.shadowCasterFrustum && m_CullOwner != null && split.sphereRadius > 0f)
                AppendCasterPlanes(ref sv, m_CullOwner, split);
            string reason = ReceiverCullingBlocker(ctx);
            sv.receiverCulling = reason == null;
            LastShadowDecision = $"{sv.name} of {(m_CullOwner != null ? m_CullOwner.name : "<no camera>")}: {reason ?? "receiver culling"}";
            sv.vsmEntry1 = VsmEntryFor(sv, ctx, splitIndex) + 1; // M13: cached pages
        }

        // M11 follow-up: casters of a directional split bounded like Unity's shadow caster culling - the
        // owner camera's frustum up to the far side of the split sphere, extruded toward the light (its
        // faces turned away from the light + silhouette planes along the light direction), widened by the
        // receiver margins. HDRP's split planes do this already unless "Extend Shadow Culling" of the ray
        // tracing settings replaced the camera's shadow culling planes by a box of the far plane's size
        // (ray tracing quality): then every caster in the cascade's light-space box up to the light was
        // kept, 4-5x the triangles of the same split without ray tracing.
        readonly Vector3[] m_FrustumCorners = new Vector3[8];
        readonly Vector3[] m_FrustumNormals = new Vector3[6]; // outward
        static readonly int[] s_FaceCorners = { 0, 2, 3, 1, /*near*/ 4, 5, 7, 6, /*far*/ 0, 4, 6, 2, /*left*/ 1, 3, 7, 5, /*right*/ 0, 1, 5, 4, /*bottom*/ 2, 6, 7, 3 /*top*/ };
        static readonly int[] s_EdgeFaces = { 0, 2, 0, 3, 0, 4, 0, 5, 1, 2, 1, 3, 1, 4, 1, 5, 2, 4, 2, 5, 3, 4, 3, 5 };
        static readonly int[] s_EdgeCorners = { 0, 2, 1, 3, 0, 1, 2, 3, 4, 6, 5, 7, 4, 5, 6, 7, 0, 4, 2, 6, 1, 5, 3, 7 };

        void AppendCasterPlanes(ref SubView sv, Camera camera, in CullingSplit split)
        {
            if (camera.orthographic)
                return;
            var t = camera.transform;
            Vector3 position = t.position, forward = t.forward, right = t.right, up = t.up;
            float near = Mathf.Max(camera.nearClipPlane, 1e-3f);
            float far = Mathf.Min(camera.farClipPlane, Vector3.Dot(split.sphereCenter - position, forward) + split.sphereRadius);
            if (far <= near)
                return;
            float tanY = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad), tanX = tanY * camera.aspect;
            for (int i = 0; i < 8; ++i)
            {
                float d = (i & 4) != 0 ? far : near;
                m_FrustumCorners[i] = position + forward * d + right * (((i & 1) != 0 ? 1f : -1f) * tanX * d) + up * (((i & 2) != 0 ? 1f : -1f) * tanY * d);
            }
            var center = Vector3.zero;
            foreach (var c in m_FrustumCorners)
                center += c * 0.125f;
            for (int f = 0; f < 6; ++f)
            {
                var a = m_FrustumCorners[s_FaceCorners[4 * f]];
                var n = Vector3.Cross(m_FrustumCorners[s_FaceCorners[4 * f + 1]] - a, m_FrustumCorners[s_FaceCorners[4 * f + 3]] - a).normalized;
                m_FrustumNormals[f] = Vector3.Dot(n, center - a) > 0f ? -n : n;
            }
            // light travel direction: where the split's (GL-style) clip depth grows
            var m = split.cullingMatrix;
            var light = new Vector3(m.m20, m.m21, m.m22).normalized;
            float margin = m_Settings.shadowReceiverMarginWorld + 2f * split.sphereRadius / Mathf.Max(64, m_Settings.shadowResolution) * m_Settings.shadowReceiverMarginTexels;
            for (int f = 0; f < 6 && sv.planeCount < 16; ++f)
                if (Vector3.Dot(m_FrustumNormals[f], light) >= 0f)
                    AddPlane(ref sv, -m_FrustumNormals[f], m_FrustumCorners[s_FaceCorners[4 * f]], margin);
            for (int e = 0; e < 12 && sv.planeCount < 16; ++e)
            {
                // silhouette: between a face turned away from the light and one facing it
                bool away0 = Vector3.Dot(m_FrustumNormals[s_EdgeFaces[2 * e]], light) >= 0f;
                bool away1 = Vector3.Dot(m_FrustumNormals[s_EdgeFaces[2 * e + 1]], light) >= 0f;
                if (away0 == away1)
                    continue;
                var a = m_FrustumCorners[s_EdgeCorners[2 * e]];
                var n = Vector3.Cross(m_FrustumCorners[s_EdgeCorners[2 * e + 1]] - a, light);
                if (n.sqrMagnitude < 1e-12f)
                    continue;
                n.Normalize();
                if (Vector3.Dot(n, center - a) < 0f)
                    n = -n; // inward
                AddPlane(ref sv, n, a, margin);
            }
        }

        // inward normal through `point`, pushed out by `margin`
        static void AddPlane(ref SubView sv, Vector3 inward, Vector3 point, float margin)
        {
            sv.planes[sv.planeCount++] = new Vector4(inward.x, inward.y, inward.z, -Vector3.Dot(inward, point) + margin);
        }

        string ReceiverCullingBlocker(in BatchCullingContext ctx)
        {
            if (m_Settings.shadowReceiverCulling == VgShadowReceiverCulling.Off)
                return "disabled in settings";
            if (m_RcvCs == null)
                return "shaders missing";
            if (m_Settings.debugStopAfterStage != 0)
                return "debug stage limit";
            if (m_CullOwner == null)
                return "no owning camera";
            if (!m_ShadowPassSeen.TryGetValue(m_CullOwner, out bool seen))
                return "first frame (custom pass not seen yet)";
            if (!seen)
                return "custom pass did not run on the camera's last frame";
#pragma warning disable 618
            var light = Resources.InstanceIDToObject(ctx.viewID.GetInstanceID()) as Light;
#pragma warning restore 618
            if (light == null)
                return "light not found";
#if UNANITE_HDRP
            // cached shadow maps are reused by later frames and other views
            if (light.TryGetComponent<HDAdditionalLightData>(out var hd) && hd.shadowUpdateMode != ShadowUpdateMode.EveryFrame)
                return "cached shadow map";
            return null;
#else
            return "requires HDRP";
#endif
        }

        // Culling callback: a deferred split only gets empty args (valid if the pass never runs).
        void RecordShadowPlaceholder(CommandBuffer cmd, in SubView v, int slot)
        {
            SetViewConstants(cmd, v, slot, 0);
            BindAll(cmd, k_ClearShadowView);
            cmd.DispatchCompute(m_Cull, k_ClearShadowView, 1, 1, 1);
            if (v.camera == null)
                return;
            if (!m_PendingShadows.TryGetValue(v.camera, out var list))
                m_PendingShadows[v.camera] = list = new List<(SubView, int)>();
            list.Add((v, slot));
        }

        // After the view's culling: reserve and fill its range of VG_ShadowRecords + draw args.
        void RecordShadowCompaction(CommandBuffer cmd, int slot)
        {
            cmd.DispatchCompute(m_Cull, k_PrepareShadowRaster, 1, 1, 1);
            cmd.DispatchCompute(m_Cull, k_CompactShadow, m_RasterArgs, (uint)((slot * k_RasterArgsStride + 16) * 4));
            // index writes are consumed by HDRP's shadow pass (arena index buffer)
        }

        void SetReceiverConstants(CommandBuffer cmd, in SubView v)
        {
            var r = m_Receivers;
            cmd.SetComputeIntParams(m_Cull, Ids.OccConfig, 3, PhaseStateBase(0), v.receiverBatchIndex * m_ReceiverTexels, m_OccCapacity);
            cmd.SetComputeIntParams(m_Cull, Ids.OccHzb, r.width0, r.height0, r.levels, 0); // GL-style light depth: larger = farther
            cmd.SetComputeMatrixParam(m_Cull, Ids.OccViewProj, v.lightViewProj);
            cmd.SetComputeVectorParam(m_Cull, Ids.OccScreen, new Vector4(r.screenWidth, r.screenHeight, 0, 0));
            m_BoundHzb = m_HzbOverride ?? r.hzb; // M13: dirty pyramids of cached splits
        }

        GraphicsBuffer m_HzbOverride;

        // Emits the two raster draws of a shadow-raster split: procedural, 384 vertices per visible
        // record (args by PrepareShadowRaster); the visible offset tells VgShadowRaster.shader its view
        // slot and list.
        int EmitShadowRasterDraws(BatchDrawCommandProceduralIndirect* procedural, int cp, in SubView v, int slot, uint windowSize)
        {
            for (int list = 0; list < 2; ++list)
            {
                if (!m_AnyShadowBin[list])
                    continue;
                procedural[cp++] = new BatchDrawCommandProceduralIndirect
                {
                    flags = BatchDrawCommandFlags.None,
                    batchID = m_BatchID,
                    materialID = m_ShadowMaterialIds[list],
                    topology = MeshTopology.Triangles,
                    splitVisibilityMask = v.splitMask,
                    lightmapIndex = 0xFFFF,
                    sortingPosition = 0,
                    visibleOffset = (uint)(slot * 2 + list),
                    visibleInstancesBufferHandle = m_VisibleInstances.bufferHandle,
                    visibleInstancesBufferWindowOffset = 0,
                    visibleInstancesBufferWindowSizeBytes = windowSize,
                    indirectArgsBufferHandle = m_RasterArgs.bufferHandle,
                    indirectArgsBufferOffset = (uint)((slot * k_RasterArgsStride + list * 8) * 4),
                };
            }
            return cp;
        }

        int ShadowRasterDrawCount(in SubView v) => v.shadowRaster ? (m_AnyShadowBin[0] ? 1 : 0) + (m_AnyShadowBin[1] ? 1 : 0) : 0;

        bool IsShadowRasterBin(in SubView v, int bin) => v.shadowRaster && (m_BinFlags[bin] & BinFlagShadow) != 0;

#if UNANITE_HDRP
        /// <summary>
        /// AfterOpaqueDepthAndNormal custom pass body: receiver pyramids + culling of the shadow splits
        /// deferred to this camera. HDRP renders the shadow maps after this injection point.
        /// </summary>
        internal void RenderDeferredShadows(CustomPassContext ctx)
        {
            var camera = ctx.hdCamera.camera;
            if (!m_ShadowPassSeen.TryGetValue(camera, out bool seen) || !seen)
                m_ShadowPassSeen[camera] = true; // capable from the next frame on
            if (!m_PendingShadows.TryGetValue(camera, out var pending) || pending.Count == 0)
                return;

            var cmd = ctx.cmd;
            int width = ctx.hdCamera.actualWidth, height = ctx.hdCamera.actualHeight;
            int tilesX = (width + k_ReceiverTile - 1) / k_ReceiverTile, tilesY = (height + k_ReceiverTile - 1) / k_ReceiverTile;
            // 16 px tiles + 64 px + 256 px nodes (VgShadowReceivers.compute)
            int nodes1 = ((tilesX + 3) / 4) * ((tilesY + 3) / 4), nodes2 = ((tilesX + 15) / 16) * ((tilesY + 15) / 16);
            int nodes = tilesX * tilesY + nodes1 + nodes2;
            if (m_TileDepth == null || m_TileDepth.count < nodes)
            {
                m_TileDepth?.Dispose();
                m_TileDepth = new GraphicsBuffer(GraphicsBuffer.Target.Structured, nodes, 16);
            }

            var proj = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
            var viewProj = proj * camera.worldToCameraMatrix;
            bool reversed = SystemInfo.usesReversedZBuffer;
            float near = Mathf.Max(camera.nearClipPlane, 1e-3f), far = Mathf.Max(camera.farClipPlane, near * 2f);
            int chunks = Mathf.Clamp(m_Settings.shadowReceiverDepthChunks, 1, 64);
            int mode = (int)m_Settings.shadowReceiverCulling;

            cmd.BeginSample(ReceiverSampler);
            cmd.SetComputeMatrixParam(m_RcvCs, ShadowIds.CamInvViewProj, viewProj.inverse);
            cmd.SetComputeVectorParam(m_RcvCs, ShadowIds.Proj, new Vector4(proj.m22, proj.m23, proj.m32, proj.m33));
            cmd.SetComputeVectorParam(m_RcvCs, ShadowIds.Range, new Vector4(near, far, chunks, mode));
            cmd.SetComputeFloatParam(m_RcvCs, ShadowIds.SkyDepth, reversed ? 0f : 1f);
            cmd.SetComputeIntParams(m_RcvCs, ShadowIds.Screen, width, height, tilesX, tilesY);
            var r = m_Receivers;
            cmd.SetComputeIntParams(m_RcvCs, ShadowIds.Mask, r.width0, r.levels, r.screenWidth, m_ReceiverTexels);
            float texelNdc = 2f / Mathf.Max(64, m_Settings.shadowResolution);
            cmd.SetComputeVectorParam(m_RcvCs, ShadowIds.Margin, new Vector4(m_Settings.shadowReceiverMarginTexels, m_Settings.shadowReceiverMarginWorld, texelNdc, 0));
            cmd.SetComputeTextureParam(m_RcvCs, k_RcvTileDepth, OccIds.Depth, ctx.cameraDepthBuffer, 0, RenderTextureSubElement.Depth);
            cmd.SetComputeBufferParam(m_RcvCs, k_RcvTileDepth, ShadowIds.TileDepth, m_TileDepth);
            cmd.DispatchCompute(m_RcvCs, k_RcvTileDepth, tilesX, tilesY, 1);
            cmd.SetComputeBufferParam(m_RcvCs, k_RcvTileUp, ShadowIds.TileDepth, m_TileDepth);
            cmd.SetComputeIntParams(m_RcvCs, ShadowIds.Level, 1, 0, 0, 0);
            cmd.DispatchCompute(m_RcvCs, k_RcvTileUp, (nodes1 + 63) / 64, 1, 1);
            cmd.SetComputeIntParams(m_RcvCs, ShadowIds.Level, 2, 0, 0, 0);
            cmd.DispatchCompute(m_RcvCs, k_RcvTileUp, (nodes2 + 63) / 64, 1, 1);
            cmd.EndSample(ReceiverSampler);

            int splatNodes = mode == (int)VgShadowReceiverCulling.VisibleVolume ? nodes : tilesX * tilesY;
            for (int first = 0; first < pending.Count; first += k_ReceiverBatch)
            {
                int count = Mathf.Min(k_ReceiverBatch, pending.Count - first);
                for (int i = 0; i < count; ++i)
                {
                    m_BatchMatrices[i] = pending[first + i].view.lightViewProj;
                    m_BatchSpheres[i] = pending[first + i].view.splitSphere;
                }
                BuildReceivers(cmd, count, splatNodes, chunks);
                // M9 follow-up: the splits of a batch are culled in one set of dispatches (RecordBatch);
                // one RecordView each costs ~17 dependent, mostly idle dispatches per split
                for (int i = 0; i < count; ++i)
                {
                    var (view, slot) = pending[first + i];
                    view.receiverBatchIndex = i;
                    pending[first + i] = (view, slot);
                    DeferredShadowSplitCount++;
                }
                // M13: cached splits: page upkeep, dirty-page culling and raster; their per-frame
                // culling below skips the shadow-raster bins
                if (m_VsmPool != null)
                    RenderVsmBatch(cmd, camera, pending, first, count);
                for (int sub = 0; sub < count; sub += k_MaxBatchSplits)
                {
                    int n = Mathf.Min(k_MaxBatchSplits, count - sub);
                    if (m_Settings.batchShadowCulling && n > 1 && SameRasterMode(pending, first + sub, n))
                        RecordBatch(cmd, pending, first + sub, n);
                    else
                        for (int i = 0; i < n; ++i)
                            RecordView(cmd, pending[first + sub + i].view, pending[first + sub + i].slot);
                }
            }
            pending.Clear();
        }

        /// <summary>GPU samplers of the batched shadow-split culling stages (inside "VG.CullView").</summary>
        public static readonly CustomSampler BatchCullSampler = CustomSampler.Create("VG.ShadowBatch.Cull", true);
        public static readonly CustomSampler BatchExpandSampler = CustomSampler.Create("VG.ShadowBatch.Expand", true);
        public static readonly CustomSampler BatchCompactSampler = CustomSampler.Create("VG.ShadowBatch.Compact", true);

        static bool SameRasterMode(List<(SubView view, int slot)> views, int first, int n)
        {
            for (int i = 1; i < n; ++i)
                if (views[first + i].view.shadowRaster != views[first].view.shadowRaster)
                    return false;
            return true;
        }

        // M9 follow-up: culls `n` receiver-culled shadow splits in one set of dispatches. Work items carry
        // their split (VgCull.compute, VgSplitView); counters, bin counts, visible records and raster lists
        // are per split (region 0 split evenly), the traversal queue is shared. Same results as n RecordViews.
        void RecordBatch(CommandBuffer cmd, List<(SubView view, int slot)> views, int first, int n)
        {
            var v0 = views[first].view;
            cmd.BeginSample(CullSampler);
            SetViewConstants(cmd, v0, views[first].slot, 0);
            SetReceiverConstants(cmd, v0);
            int recordsPerSplit = m_VisibleCapacity / n;
            cmd.SetComputeIntParams(m_Cull, Ids.Batch, n, recordsPerSplit, recordsPerSplit, m_BinCapacity);
            cmd.SetComputeVectorParam(m_Cull, SwIds.SwParams, Vector4.zero); // shadows: no software raster

            const int stride = k_SplitViewStride / 4;
            for (int i = 0; i < n; ++i)
            {
                var (v, slot) = views[first + i];
                int o = i * stride;
                for (int p = 0; p < 16; ++p)
                {
                    var pl = p < v.planeCount ? v.planes[p] : Vector4.zero;
                    m_SplitViewData[o + p * 4 + 0] = pl.x;
                    m_SplitViewData[o + p * 4 + 1] = pl.y;
                    m_SplitViewData[o + p * 4 + 2] = pl.z;
                    m_SplitViewData[o + p * 4 + 3] = pl.w;
                }
                o += 64;
                Put(o, new Vector4(v.position.x, v.position.y, v.position.z, v.lodA)); o += 4;
                Put(o, new Vector4(v.lodB, v.near, v.flags, v.planeCount)); o += 4;
                Put(o, v.camLod); o += 4;
                for (int r = 0; r < 4; ++r, o += 4)
                    Put(o, v.lightViewProj.GetRow(r));
                uint pyramid = (uint)(v.receiverBatchIndex * m_ReceiverTexels), args = (uint)(slot * k_RasterArgsStride), draws = (uint)(slot * m_BinCapacity);
                m_SplitViewData[o + 0] = System.BitConverter.Int32BitsToSingle((int)pyramid);
                m_SplitViewData[o + 1] = System.BitConverter.Int32BitsToSingle(slot);
                m_SplitViewData[o + 2] = System.BitConverter.Int32BitsToSingle((int)args);
                m_SplitViewData[o + 3] = System.BitConverter.Int32BitsToSingle((int)draws);
            }
            cmd.SetBufferData(m_SplitViews, m_SplitViewData, 0, 0, n * stride);

            foreach (int k in new[] { k_ResetView, k_CullInstances, k_PrepareNodes, k_TraverseNodes, k_PrepareClusters, k_CullClusters, k_AllocateBins, k_PrepareExpand, k_Expand, k_PrepareShadowRaster, k_PrepareBatchCompact, k_CompactShadow })
            {
                BindAll(cmd, k);
                cmd.SetComputeBufferParam(m_Cull, k, Ids.SplitViews, m_SplitViews);
            }
            cmd.BeginSample(BatchCullSampler);
            cmd.DispatchCompute(m_Cull, k_ResetView, 1, 1, 1);
            cmd.DispatchCompute(m_Cull, k_CullInstances, (m_InstanceHighWater + 63) / 64, n, 1);
            DispatchTraversal(cmd, views[first].slot, 0);
            cmd.DispatchCompute(m_Cull, k_PrepareClusters, 1, 1, 1);
            cmd.DispatchCompute(m_Cull, k_CullClusters, m_Counters, k_DispatchClustersOffset);
            cmd.EndSample(BatchCullSampler);
            cmd.BeginSample(BatchExpandSampler);
            cmd.DispatchCompute(m_Cull, k_AllocateBins, n, 1, 1);
            cmd.DispatchCompute(m_Cull, k_PrepareExpand, 1, 1, 1);
            cmd.DispatchCompute(m_Cull, k_Expand, m_Counters, k_DispatchExpandOffset);
            cmd.EndSample(BatchExpandSampler);
            if (v0.shadowRaster)
            {
                cmd.BeginSample(BatchCompactSampler);
                cmd.DispatchCompute(m_Cull, k_PrepareShadowRaster, n, 1, 1);
                cmd.DispatchCompute(m_Cull, k_PrepareBatchCompact, 1, 1, 1);
                cmd.DispatchCompute(m_Cull, k_CompactShadow, m_Counters, k_DispatchBatchOffset);
                cmd.EndSample(BatchCompactSampler);
            }
            cmd.SetComputeIntParams(m_Cull, Ids.Batch, 0, 0, 0, 0);
            cmd.EndSample(CullSampler);
        }

        void Put(int o, Vector4 v)
        {
            m_SplitViewData[o + 0] = v.x;
            m_SplitViewData[o + 1] = v.y;
            m_SplitViewData[o + 2] = v.z;
            m_SplitViewData[o + 3] = v.w;
        }

        // Receiver pyramids of `count` splits (m_BatchMatrices / m_BatchSpheres) in one set of dispatches.
        void BuildReceivers(CommandBuffer cmd, int count, int nodes, int chunks)
        {
            var r = m_Receivers;
            cmd.BeginSample(ReceiverSampler);
            cmd.SetComputeMatrixArrayParam(m_RcvCs, ShadowIds.LightViewProj, m_BatchMatrices);
            cmd.SetComputeVectorArrayParam(m_RcvCs, ShadowIds.Sphere, m_BatchSpheres);
            cmd.SetComputeIntParam(m_RcvCs, ShadowIds.Splits, count);
            foreach (int k in new[] { k_RcvClear, k_RcvSplat, k_RcvFinalize })
                cmd.SetComputeBufferParam(m_RcvCs, k, ShadowIds.RcvSplat, m_RcvSplat);
            cmd.SetComputeBufferParam(m_RcvCs, k_RcvSplat, ShadowIds.TileDepth, m_TileDepth);
            cmd.SetComputeBufferParam(m_RcvCs, k_RcvFinalize, OccIds.HzbOut, r.hzb);
            cmd.SetComputeBufferParam(m_RcvCs, k_RcvPyramid, OccIds.HzbOut, r.hzb);

            cmd.DispatchCompute(m_RcvCs, k_RcvClear, (m_ReceiverTexels * count + 63) / 64, 1, 1);
            cmd.DispatchCompute(m_RcvCs, k_RcvSplat, (nodes + 63) / 64, chunks, count);
            cmd.DispatchCompute(m_RcvCs, k_RcvFinalize, (r.width0 * r.height0 + 63) / 64, count, 1);
            // max pyramid (light depth grows away from the light, empty = -3e38): levels 1-4, 5-8
            cmd.SetComputeIntParams(m_RcvCs, ShadowIds.Level, 0, 0, 0, 0);
            cmd.DispatchCompute(m_RcvCs, k_RcvPyramid, r.width0 / k_ReceiverTile, r.height0 / k_ReceiverTile, count);
            cmd.SetComputeIntParams(m_RcvCs, ShadowIds.Level, 4, 0, 0, 0);
            cmd.DispatchCompute(m_RcvCs, k_RcvPyramid, 1, 1, count);
            cmd.EndSample(ReceiverSampler);
        }
#endif

        /// <summary>
        /// Debug/tests: synchronous readback over the views of the last culled frame: shadow splits
        /// culled with receiver culling, items they rejected, splits drawn with the shadow raster.
        /// </summary>
        public (int receiverSplits, int receiverCulled, int rasterSplits) DebugReadShadowStats()
        {
            int views = m_ViewSlotsUsed;
            if (views == 0)
                return (0, 0, 0);
            var data = new uint[views * k_StatsPerView];
            m_Stats.GetData(data, 0, 0, data.Length);
            int splits = 0, culled = 0, raster = 0;
            for (int v = 0; v < views; ++v)
            {
                int o = v * k_StatsPerView;
                if (data[o + 15] != 0)
                {
                    splits++;
                    culled += (int)data[o + 14];
                }
                if ((data[o + 6] & BinFlagShadow) != 0)
                    raster++;
            }
            return (splits, culled, raster);
        }

        /// <summary>Debug/tests: synchronous readback of the receiver pyramid level 0 of the last deferred split.</summary>
        public float[] DebugReadReceivers()
        {
            if (m_Receivers.hzb == null)
                return null;
            var data = new float[m_Receivers.width0 * m_Receivers.height0];
            m_Receivers.hzb.GetData(data, 0, 0, data.Length);
            return data;
        }
    }
}
