using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace UNanite
{
    // M10: pulled material passes.
    //
    //   bins whose material shader has a generated VG variant (VgShaderVariants) are drawn with a twin
    //   of the material using that variant: every pass of the material (depth, GBuffer, forward,
    //   shadows, motion vectors, transparent passes) pulls its vertices from the page pool
    //   (VgPulled.hlsl) instead of drawing the expansion arena's vertices.
    //   Expand           VG_BIN_PULLED bins: one frame-wide record (VG_ShadowRecords) per visible
    //                    cluster and indices (record << 7 | vertex), no vertices
    //   draws            the usual indexed indirect draw of the arena, material = variant twin,
    //                    batch = the per-instance batch below
    //   PulledBatch      BRG batch whose unity_ObjectToWorld / unity_WorldToObject / previous matrices
    //                    are per-VG-instance arrays (VgCull.compute WriteInstanceMatrices); the
    //                    variant's UNITY_SETUP_INSTANCE_ID selects the VG instance of the vertex
    public sealed unsafe partial class VgWorld
    {
        const uint BinFlagPulled = 8u; // VG_BIN_PULLED
        const uint BinFlagProgrammable = 16u; // VG_BIN_PROGRAMMABLE (M11)
        const uint BinFlagBarycentrics = 32u; // VG_BIN_BARYCENTRICS (M11)
        const uint BinFlagRasterMotion = 64u; // VG_BIN_RASTER_MOTION (M11)

        // M11: hardware barycentrics from the programmable raster (SV_Barycentrics needs DXC targets)
        static readonly bool s_BarycentricsSupported = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12 ||
            SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Metal;
        bool BarycentricRaster => m_Settings.barycentricRaster && s_BarycentricsSupported;
        // M11: + per-pixel motion of vertex-animated bins (SV_Target2 after the barycentrics)
        bool RasterMotion => m_Settings.rasterMotionVectors && BarycentricRaster;

        // M11: the bin's graph moves vertices (wind, time): its motion comes from the programmable raster
        static bool MovesVertices(Material source) =>
            source != null && VgShaderVariants.TryGet(source.shader, out var entry) && entry.programmable && !entry.resolve;

        // M11 raster twins: the variant with only its VgVisBuffer pass enabled, drawn in visibility-
        // buffer views through the custom pass's renderer list (VgVisRaster.hlsl)
        readonly List<Material> m_BinRasterMaterials = new List<Material>();
        readonly List<BatchMaterialID> m_BinRasterIds = new List<BatchMaterialID>();
        readonly List<int> m_BinRasterCrc = new List<int>();
        static readonly ShaderTagId s_VisRasterTag = new ShaderTagId("VgVisBuffer");
        static readonly string[] s_RasterTwinOffPasses =
        {
            "ShadowCaster", "DepthOnly", "DepthForwardOnly", "GBuffer", "Forward", "ForwardOnly", "MotionVectors",
            "TransparentDepthPrepass", "TransparentDepthPostpass", "TransparentBackface",
        };

        readonly List<Material> m_BinPulledMaterials = new List<Material>();
        readonly List<BatchMaterialID> m_BinPulledIds = new List<BatchMaterialID>();
        readonly List<int> m_BinPulledCrc = new List<int>();
        readonly List<int> m_BinPulledVersion = new List<int>();

        bool m_PulledSupported;
        int k_WriteInstanceMatrices;
        BatchID m_PulledBatchID;
        GraphicsBuffer m_PulledBatchData;
        int m_PulledCapacity;
        readonly CommandBuffer m_PulledCmd = new CommandBuffer { name = "UNanite.InstanceMatrices" };
        GraphicsBuffer m_TriangleIds; // M11: visibility ID per arena triangle (programmable raster)

        static class PulledIds
        {
            public static readonly int InstanceMatrices = Shader.PropertyToID("VG_InstanceMatrices");
            public static readonly int TriangleIds = Shader.PropertyToID("VG_TriangleIds");
            public static readonly int DrawArgs = Shader.PropertyToID("VG_DrawArgs");
            public static readonly int VisibleIn = Shader.PropertyToID("VG_VisibleIn");
            public static readonly int RasterListsIn = Shader.PropertyToID("VG_RasterListsIn");
        }

        void InitPulled()
        {
            // the per-instance batch needs raw (SSBO) batch buffers: constant-buffer platforms expand
            m_PulledSupported = m_RawBatchBuffers && HasKernels(m_Cull, "WriteInstanceMatrices") && SystemInfo.supportsComputeShaders;
            if (m_PulledSupported)
                k_WriteInstanceMatrices = m_Cull.FindKernel("WriteInstanceMatrices");
            m_TriangleIds = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, m_ArenaIndices.count / 3), 4);
        }

        void OnBinCreatedPulled()
        {
            m_BinPulledMaterials.Add(null);
            m_BinPulledIds.Add(BatchMaterialID.Null);
            m_BinPulledCrc.Add(0);
            m_BinPulledVersion.Add(-1);
            m_BinRasterMaterials.Add(null);
            m_BinRasterIds.Add(BatchMaterialID.Null);
            m_BinRasterCrc.Add(0);
        }

        // M11: SpeedTree 8 wind reads DOTS_ST_WindParam* that only bins with a wind slot have
        // (VgWorld.Wind.cs): the twins of other bins render without wind
        internal static void DisableSpeedTreeWind(Material twin)
        {
            if (!twin.HasProperty("_WindQuality"))
                return;
            foreach (var kw in twin.shaderKeywords)
                if (kw.StartsWith("_WINDQUALITY_"))
                    twin.DisableKeyword(kw);
            twin.EnableKeyword("_WINDQUALITY_NONE");
            twin.SetFloat("_WindQuality", 0f);
        }

        bool IsProgrammableBin(int bin) => (m_BinFlags[bin] & BinFlagProgrammable) != 0 && m_BinRasterMaterials[bin] != null;

        // M11: the raster twin of a programmable bin (after RefreshBin decided the flags)
        void RefreshRasterTwin(int bin, Material source, uint flags)
        {
            var twin = m_BinRasterMaterials[bin];
            var resolve = m_BinResolveMaterials[bin];
            if ((flags & BinFlagProgrammable) == 0 || resolve == null)
            {
                if (twin != null)
                {
                    CoreUtils.Destroy(twin);
                    m_BinRasterMaterials[bin] = null;
                    m_BinRasterIds[bin] = BatchMaterialID.Null;
                }
                return;
            }
            if (twin == null || twin.shader != resolve.shader)
            {
                if (twin != null)
                    CoreUtils.Destroy(twin);
                twin = new Material(resolve.shader) { hideFlags = HideFlags.HideAndDontSave, name = source.name + " (VG raster)" };
                m_BinRasterMaterials[bin] = twin;
                m_BinRasterIds[bin] = m_BRG.RegisterMaterial(twin);
                m_BinRasterCrc[bin] = 0;
            }
            int crc = source.ComputeCRC();
            if (crc != m_BinRasterCrc[bin])
            {
                m_BinRasterCrc[bin] = crc;
                twin.CopyPropertiesFromMaterial(source);
                twin.shaderKeywords = source.shaderKeywords;
                twin.renderQueue = source.renderQueue;
                foreach (var pass in s_RasterTwinOffPasses)
                    twin.SetShaderPassEnabled(pass, false);
                twin.SetShaderPassEnabled("VgVisBuffer", true);
                if (!BinHasWind(bin))
                    DisableSpeedTreeWind(twin);
            }
            if (twin.IsKeywordEnabled("VG_RASTER_BARY") != BarycentricRaster)
            {
                if (BarycentricRaster)
                    twin.EnableKeyword("VG_RASTER_BARY");
                else
                    twin.DisableKeyword("VG_RASTER_BARY");
            }
            bool motion = RasterMotion && MovesVertices(source);
            if (twin.IsKeywordEnabled("VG_RASTER_MOTION") != motion)
            {
                if (motion)
                    twin.EnableKeyword("VG_RASTER_MOTION");
                else
                    twin.DisableKeyword("VG_RASTER_MOTION");
            }
        }

        /// <summary>M10: whether `bin` draws with a pulled variant twin; creates / syncs the twin.</summary>
        uint RefreshPulled(int bin, Material source)
        {
            Material twin = m_BinPulledMaterials[bin];
            VgShaderVariants.Entry entry = default;
            bool use = m_PulledSupported && m_Settings.pulledMaterials && source != null &&
                       VgShaderVariants.TryGet(source.shader, out entry);
            if (!use)
            {
                if (twin != null)
                {
                    CoreUtils.Destroy(twin);
                    m_BinPulledMaterials[bin] = null;
                    m_BinPulledIds[bin] = BatchMaterialID.Null;
                }
                return 0u;
            }
            if (twin == null || twin.shader != entry.variant)
            {
                if (twin != null)
                    CoreUtils.Destroy(twin);
                twin = new Material(entry.variant) { hideFlags = HideFlags.HideAndDontSave, name = source.name + " (VG pulled)" };
                m_BinPulledMaterials[bin] = twin;
                m_BinPulledIds[bin] = m_BRG.RegisterMaterial(twin);
                m_BinPulledCrc[bin] = 0;
            }
            int crc = source.ComputeCRC();
            if (crc != m_BinPulledCrc[bin])
            {
                m_BinPulledCrc[bin] = crc;
                twin.CopyPropertiesFromMaterial(source);
                twin.shaderKeywords = source.shaderKeywords;
                twin.renderQueue = source.renderQueue;
                twin.SetShaderPassEnabled("VgVisBuffer", false); // M11: raster twins only
                if (!BinHasWind(bin))
                    DisableSpeedTreeWind(twin);
            }
            return BinFlagPulled;
        }

        bool IsPulledBin(int bin) => (m_BinFlags[bin] & BinFlagPulled) != 0 && m_BinPulledMaterials[bin] != null;

        // Re-creates the per-instance batch when the instance capacity grew; rewrites every matrix.
        void EnsurePulledBatch()
        {
            if (!m_PulledSupported || m_InstanceBuffer == null)
                return;
            int capacity = m_InstanceBuffer.count;
            int windCapacity = Mathf.Max(m_WindCapacity, WindCapacityNeeded());
            if (m_PulledBatchData != null && m_PulledCapacity == capacity && m_WindCapacity == windCapacity)
                return;
            if (m_PulledBatchData != null)
            {
                m_BRG.RemoveBatch(m_PulledBatchID);
                RemoveWindBatches();
                m_PulledBatchData.Dispose();
            }
            m_PulledCapacity = capacity;
            m_WindCapacity = windCapacity;
            const int zeroBytes = 64, matrixBytes = 48;
            m_WindBase = zeroBytes + 4 * matrixBytes * capacity; // M11: wind slots after the matrices
            int bytes = m_WindBase + m_WindCapacity * k_WindSlotBytes;
            m_PulledBatchData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, bytes / 4, 4);
            m_PulledBatchData.SetData(new uint[zeroBytes / 4]); // zero defaults for unset properties

            var metadata = new NativeArray<MetadataValue>(4, Allocator.Temp);
            string[] names = { "unity_ObjectToWorld", "unity_WorldToObject", "unity_MatrixPreviousM", "unity_MatrixPreviousMI" };
            for (int m = 0; m < 4; ++m)
                metadata[m] = new MetadataValue { NameID = Shader.PropertyToID(names[m]), Value = 0x80000000u | (uint)(zeroBytes + m * matrixBytes * capacity) };
            m_PulledBatchID = m_BRG.AddBatch(metadata, m_PulledBatchData.bufferHandle);
            metadata.Dispose();
            CreateWindBatches(zeroBytes, matrixBytes, capacity);
            WritePulledMatrices(0, m_InstanceHighWater);
        }

        void WritePulledMatrices(int start, int count)
        {
            if (!m_PulledSupported || m_PulledBatchData == null || count <= 0)
                return;
            m_PulledCmd.Clear();
            m_PulledCmd.SetComputeIntParams(m_Cull, Ids.Counts, m_PulledCapacity, 0, start, count);
            m_PulledCmd.SetComputeBufferParam(m_Cull, k_WriteInstanceMatrices, Ids.Instances, m_InstanceBuffer);
            m_PulledCmd.SetComputeBufferParam(m_Cull, k_WriteInstanceMatrices, PulledIds.InstanceMatrices, m_PulledBatchData);
            m_PulledCmd.DispatchCompute(m_Cull, k_WriteInstanceMatrices, (count + 63) / 64, 1, 1);
            Graphics.ExecuteCommandBuffer(m_PulledCmd);
        }

        /// <summary>Diagnostics: one line per live material bin (material, resolve kind, pulled, sorted owner).</summary>
        public string DescribeBins()
        {
            var sb = new System.Text.StringBuilder();
            for (int b = 0; b < m_BinMaterials.Count; ++b)
            {
                if (m_BinRefCount[b] <= 0)
                    continue;
                string resolve = (m_BinFlags[b] & BinFlagResolve) == 0 ? "none" :
                    m_BinResolveOverride[b] ? "override" : IsShaderGraphResolveBin(b) ? "graph" : "lit";
                sb.Append($"bin {b} '{(m_BinMaterials[b] != null ? m_BinMaterials[b].name : "null")}' resolve:{resolve} pulled:{IsPulledBin(b)} owner:{m_BinOwner[b]} wind:{m_BinWind[b]}\n");
            }
            return sb.ToString();
        }

        // globals read by the pulled draws, which HDRP issues
        void BeginPulledFrame()
        {
            if (m_PagePool == null || m_ShadowRecords == null)
                return;
            bool any = false;
            for (int b = 0; b < m_BinFlags.Count && !any; ++b)
                any = m_BinRefCount[b] > 0 && IsPulledBin(b);
            if (!any)
                return;
            Shader.SetGlobalBuffer(VisIds.Meshes, m_MeshBuffer);
            Shader.SetGlobalBuffer(VisIds.Instances, m_InstanceBuffer);
            Shader.SetGlobalBuffer(VisIds.PagePool, m_PagePool);
            Shader.SetGlobalBuffer(ShadowIds.ShadowRecords, m_ShadowRecords);
            Shader.SetGlobalBuffer(PulledIds.TriangleIds, m_TriangleIds);
            Shader.SetGlobalBuffer(PulledIds.DrawArgs, m_DrawArgs);
            Shader.SetGlobalBuffer(VisIds.Visible, m_Visible);
            if (m_LodSwitchBuffer != null)
                Shader.SetGlobalBuffer(s_LodSwitchesId, m_LodSwitchBuffer); // M11 smooth LOD
            if (m_LodFadeState != null)
                Shader.SetGlobalBuffer(s_LodFadeStateId, m_LodFadeState); // M11 LOD crossfade
        }

        void DisposePulled()
        {
            for (int b = 0; b < m_BinPulledMaterials.Count; ++b)
                if (m_BinPulledMaterials[b] != null)
                    CoreUtils.Destroy(m_BinPulledMaterials[b]);
            m_BinPulledMaterials.Clear();
            foreach (var m in m_BinRasterMaterials)
                if (m != null)
                    CoreUtils.Destroy(m);
            m_BinRasterMaterials.Clear();
            m_PulledBatchData?.Dispose();
            m_PulledBatchData = null;
            m_TriangleIds?.Dispose();
            m_TriangleIds = null;
            m_PulledCmd.Release();
        }
    }
}
