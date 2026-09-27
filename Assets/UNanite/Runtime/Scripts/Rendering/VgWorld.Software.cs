using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace UNanite
{
    // M5: software rasteriser for small clusters of visibility-buffer cameras.
    //
    //   culling (VgCull.compute)   small clusters in front of the near plane → raster list 2
    //   BeforeRendering pass       after each phase's hardware raster:
    //                                VG.RasterSW  VgSwRaster.compute → 64-bit per-pixel buffer
    //                                VG.MergeSW   VgSwMerge.shader → visibility buffer + depth (depth test)
    //
    // Raster lists (VG_RasterLists): per VG_Visible region four lists of absolute record indices
    // (hardware single-sided, hardware double-sided, software, expansion), capacity = visible
    // capacity. Region 0 (shadow / expansion views) uses lists 0 and 1 for the M6 shadow raster; list 3
    // holds the records Expand works on (clusters of bins the view does not rasterise itself).
    public sealed unsafe partial class VgWorld
    {
        /// <summary>GPU/CPU sampler around the software raster ("VG.RasterSW").</summary>
        public static readonly CustomSampler SoftwareRasterSampler = CustomSampler.Create("VG.RasterSW", true);
        /// <summary>GPU/CPU sampler around the software-to-visibility-buffer merge ("VG.MergeSW").</summary>
        public static readonly CustomSampler MergeSampler = CustomSampler.Create("VG.MergeSW", true);

        const int k_RasterArgsStride = 32;          // uints per view slot (VgCull.compute)
        const int k_RasterLists = 4;                // lists per VG_Visible region (VgCull.compute ListIndex)
        const int k_SwDispatchPhase1 = 16 * 4;      // byte offsets inside a slot
        const int k_SwDispatchPhase2 = 20 * 4;

        GraphicsBuffer m_RasterLists, m_SwVisBuffer, m_SwTileMask, m_SwTiles, m_SwMergeArgs;
        ComputeShader m_SwCs;
        int k_SwClear, k_SwRaster, k_SwMergeTiles, k_SwMergeArgs;
        Material m_SwMergeMaterial;
        bool m_SwSupported;

        /// <summary>True when the device and shader compiler support the software rasteriser.</summary>
        public bool SoftwareRasterSupported => m_SwSupported;

        static class SwIds
        {
            public static readonly int RasterLists = Shader.PropertyToID("VG_RasterLists");
            public static readonly int SwVisBuffer = Shader.PropertyToID("VG_SwVisBuffer");
            public static readonly int SwConfig = Shader.PropertyToID("_SwConfig");
            public static readonly int SwScreen = Shader.PropertyToID("_SwScreen");
            public static readonly int SwParams = Shader.PropertyToID("_SwParams");
            public static readonly int ListConfig = Shader.PropertyToID("_ListConfig");
            public static readonly int ListBase = Shader.PropertyToID("_VgListBase");
            public static readonly int ListCapacity = Shader.PropertyToID("_VgListCapacity");
            public static readonly int SwPitch = Shader.PropertyToID("_VgSwPitch");
            public static readonly int SwReversedZ = Shader.PropertyToID("_VgSwReversedZ");
            public static readonly int SwMergeScreen = Shader.PropertyToID("_VgSwScreen");
            public static readonly int SwTileMask = Shader.PropertyToID("VG_SwTileMask");
            public static readonly int SwTiles = Shader.PropertyToID("VG_SwTiles");
            public static readonly int SwMergeArgs = Shader.PropertyToID("VG_SwMergeArgs");
        }

        /// <summary>Debug/tests: software-rasterised clusters of the camera's last frame (phase 1 + 2), synchronous readback; -1 if not a visibility-buffer camera.</summary>
        public int DebugReadSoftwareClusters(Camera camera)
        {
            if (camera == null || !m_VisCameras.TryGetValue(camera, out var entry))
                return -1;
            var data = new uint[k_PhaseStateStride];
            m_PhaseState.GetData(data, 0, PhaseStateBase(entry.view.visRegion), k_PhaseStateStride);
            return (int)(data[6] + data[9]); // P_PHASE1_LIST + 2, P_PHASE2_LIST + 2
        }

        int RasterListBase(int region) => region * k_RasterLists * m_VisibleCapacity;

        void InitSoftwareRaster()
        {
            m_RasterLists = new GraphicsBuffer(GraphicsBuffer.Target.Structured, (1 + Mathf.Max(1, MaxVisibilityCameras)) * k_RasterLists * m_VisibleCapacity, 4);
#if UNANITE_HDRP
            m_SwCs = Resources.Load<ComputeShader>("UNanite/VgSwRaster");
#else
            m_SwCs = null; // HDRP includes; the software raster feeds the HDRP-only visibility buffer
#endif
            var merge = Shader.Find("Hidden/UNanite/SwMerge");
            if (!HasKernels(m_SwCs, "ClearSW", "RasterSW", "MergeTiles", "MergeArgs") || merge == null)
                return;
            k_SwClear = m_SwCs.FindKernel("ClearSW");
            k_SwRaster = m_SwCs.FindKernel("RasterSW");
            k_SwMergeTiles = m_SwCs.FindKernel("MergeTiles");
            k_SwMergeArgs = m_SwCs.FindKernel("MergeArgs");
            bool api = SystemInfo.graphicsDeviceType is GraphicsDeviceType.Direct3D12 or GraphicsDeviceType.Vulkan;
            m_SwSupported = api && m_SwCs.IsSupported(k_SwClear) && m_SwCs.IsSupported(k_SwRaster) && merge.isSupported;
            m_SwMergeArgs = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.IndirectArguments, 8, 4);
            m_SwMergeArgs.SetData(new uint[8]);
            if (m_SwSupported)
                m_SwMergeMaterial = new Material(merge) { hideFlags = HideFlags.HideAndDontSave, name = "UNanite SW Merge" };
        }

        void DisposeSoftwareRaster()
        {
            foreach (var b in new[] { m_RasterLists, m_SwVisBuffer, m_SwTileMask, m_SwTiles, m_SwMergeArgs })
                b?.Dispose();
            m_RasterLists = m_SwVisBuffer = m_SwTileMask = m_SwTiles = m_SwMergeArgs = null;
            if (m_SwMergeMaterial != null)
                CoreUtils.Destroy(m_SwMergeMaterial);
        }

        // Culling-time: software classification parameters of a camera view.
        void ConfigureSoftwareRaster(ref SubView sv, float height, in LODParameters lod)
        {
            sv.swEnabled = m_SwSupported && m_Settings.softwareRaster && sv.visRegion > 0;
            if (lod.isOrthographic)
                sv.swPixelsPerUnitOrtho = height / (2f * Mathf.Max(lod.orthoSize, 1e-4f));
            else
                sv.swPixelsPerUnit = 1f / Mathf.Tan(lod.fieldOfView * 0.5f * Mathf.Deg2Rad) * 0.5f * height;
        }

        void SetSoftwareConstants(CommandBuffer cmd, in SubView v)
        {
            cmd.SetComputeVectorParam(m_Cull, SwIds.SwParams, new Vector4(v.swPixelsPerUnit, v.swPixelsPerUnitOrtho, Mathf.Max(1f, m_Settings.swRasterThreshold), v.swEnabled ? 1f : 0f));
            cmd.SetComputeIntParams(m_Cull, SwIds.ListConfig, RasterListBase(v.visRegion), m_VisibleCapacity, 0, 0);
        }

        void SetRasterListProps(MaterialPropertyBlock props, int region)
        {
            props.SetInteger(SwIds.ListBase, RasterListBase(region));
            props.SetInteger(SwIds.ListCapacity, m_VisibleCapacity);
        }

        /// <summary>Software raster of one phase + merge into the visibility buffer and depth.</summary>
        void RenderSoftware(CommandBuffer cmd, RTHandle visBuffer, RTHandle depth, in SubView v, int slot, int phase, int width, int height)
        {
            int pixels = width * height;
            if (m_SwVisBuffer == null || m_SwVisBuffer.count < pixels)
            {
                m_SwVisBuffer?.Dispose();
                m_SwVisBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, pixels, 8);
            }
            int tiles = ((width + 7) / 8) * ((height + 7) / 8);
            if (m_SwTileMask == null || m_SwTileMask.count < tiles)
            {
                m_SwTileMask?.Dispose();
                m_SwTiles?.Dispose();
                m_SwTileMask = new GraphicsBuffer(GraphicsBuffer.Target.Structured, tiles, 4);
                m_SwTiles = new GraphicsBuffer(GraphicsBuffer.Target.Structured, tiles, 4);
            }

            cmd.BeginSample(SoftwareRasterSampler);
            bool reversed = SystemInfo.usesReversedZBuffer;
            cmd.SetComputeIntParams(m_SwCs, SwIds.SwConfig, RasterListBase(v.visRegion) + 2 * m_VisibleCapacity, PhaseStateBase(v.visRegion), phase, width);
            cmd.SetComputeVectorParam(m_SwCs, SwIds.SwScreen, new Vector4(width, height, reversed ? 1f : 0f, 0f));
            cmd.SetComputeVectorParam(m_SwCs, Ids.DensityLod, DensityLodParams()); // M11
            cmd.SetComputeVectorParam(m_SwCs, Ids.DensityLodOrigin, v.camera != null ? v.camera.transform.position : v.position);
            foreach (int k in new[] { k_SwClear, k_SwRaster, k_SwMergeTiles, k_SwMergeArgs })
            {
                cmd.SetComputeBufferParam(m_SwCs, k, SwIds.SwVisBuffer, m_SwVisBuffer);
                cmd.SetComputeBufferParam(m_SwCs, k, SwIds.SwTileMask, m_SwTileMask);
                cmd.SetComputeBufferParam(m_SwCs, k, SwIds.SwTiles, m_SwTiles);
                cmd.SetComputeBufferParam(m_SwCs, k, SwIds.SwMergeArgs, m_SwMergeArgs);
            }
            if (phase == 0)
                cmd.DispatchCompute(m_SwCs, k_SwClear, (width + 7) / 8, (height + 7) / 8, 1);
            cmd.SetComputeBufferParam(m_SwCs, k_SwRaster, SwIds.RasterLists, m_RasterLists);
            cmd.SetComputeBufferParam(m_SwCs, k_SwRaster, Ids.PhaseState, m_PhaseState);
            cmd.SetComputeBufferParam(m_SwCs, k_SwRaster, Ids.Visible, m_Visible);
            cmd.SetComputeBufferParam(m_SwCs, k_SwRaster, Ids.Instances, m_InstanceBuffer);
            cmd.SetComputeBufferParam(m_SwCs, k_SwRaster, Ids.Meshes, m_MeshBuffer);
            cmd.SetComputeBufferParam(m_SwCs, k_SwRaster, Ids.BinFlags, m_BinFlagsBuffer);
            cmd.SetComputeBufferParam(m_SwCs, k_SwRaster, Ids.PagePool, m_PagePool);
            cmd.DispatchCompute(m_SwCs, k_SwRaster, m_RasterArgs, (uint)(slot * k_RasterArgsStride * 4 + (phase == 0 ? k_SwDispatchPhase1 : k_SwDispatchPhase2)));
            cmd.EndSample(SoftwareRasterSampler);

            cmd.BeginSample(MergeSampler);
            cmd.DispatchCompute(m_SwCs, k_SwMergeTiles, (tiles + 63) / 64, 1, 1);
            cmd.DispatchCompute(m_SwCs, k_SwMergeArgs, 1, 1, 1);
            CoreUtils.SetRenderTarget(cmd, visBuffer, depth, ClearFlag.None);
            m_SwMergeMaterial.SetBuffer(SwIds.SwVisBuffer, m_SwVisBuffer);
            m_SwMergeMaterial.SetBuffer(SwIds.SwTiles, m_SwTiles);
            m_SwMergeMaterial.SetInteger(SwIds.SwPitch, width);
            m_SwMergeMaterial.SetFloat(SwIds.SwReversedZ, reversed ? 1f : 0f);
            m_SwMergeMaterial.SetVector(SwIds.SwMergeScreen, new Vector4(width, height, 1f / width, 1f / height));
            cmd.DrawProceduralIndirect(Matrix4x4.identity, m_SwMergeMaterial, 0, MeshTopology.Triangles, m_SwMergeArgs, 0);
            cmd.EndSample(MergeSampler);
        }
    }
}
