using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
#if UNANITE_HDRP
using UnityEngine.Rendering.HighDefinition;
#endif

namespace UNanite
{
    /// <summary>Per-camera HZB of the last frame's complete opaque depth (M4).</summary>
    sealed class OcclusionHistory
    {
        public GraphicsBuffer hzb;
        public int width0, height0, levels;   // HZB level-0 size (power of two) and level count
        public int screenWidth, screenHeight; // pixels of the depth it was built from
        public Matrix4x4 viewProj;            // world -> clip of that frame (unjittered)
        public bool valid;
    }

    // M4: two-phase HZB occlusion culling for visibility-buffer cameras.
    //
    //   frame N culling (phase 1)   test vs camera history HZB (frame N-1), defer occluded items
    //   BeforeRendering pass        phase-1 raster → VG.BuildHZB (phase-1 depth, scratch buffer)
    //                               → VG.CullPhase2 (deferred items vs current HZB) → phase-2 raster
    //   AfterOpaqueDepthAndNormal   VG.BuildHZB of the full opaque depth → camera history
    //
    // Deferred lists live per camera region (like the visible records) because every camera is
    // culled before any of them renders.
    public sealed unsafe partial class VgWorld
    {
        /// <summary>GPU/CPU sampler around every HZB build ("VG.BuildHZB").</summary>
        public static readonly CustomSampler HzbSampler = CustomSampler.Create("VG.BuildHZB", true);
        /// <summary>GPU/CPU sampler around the phase-2 re-test of deferred work ("VG.CullPhase2").</summary>
        public static readonly CustomSampler Phase2Sampler = CustomSampler.Create("VG.CullPhase2", true);

        const int k_PhaseStateStride = 16;
        const int k_DispatchDeferredNodesOffset = 24 * 4;
        const int k_DispatchDeferredClustersOffset = 28 * 4;

        ComputeShader m_HzbCs;
        int k_HzbFromDepth, k_HzbDownsample;
        GraphicsBuffer m_PhaseState, m_OccInstances, m_OccNodes, m_OccClusters, m_HzbDummy;
        int m_OccCapacity;
        readonly OcclusionHistory m_Scratch = new OcclusionHistory();
        readonly Dictionary<Camera, OcclusionHistory> m_History = new Dictionary<Camera, OcclusionHistory>();
        GraphicsBuffer m_BoundHzb;

        static class OccIds
        {
            public static readonly int Depth = Shader.PropertyToID("_VgDepth");
            public static readonly int HzbOut = Shader.PropertyToID("VG_HzbOut");
            public static readonly int HzbSize = Shader.PropertyToID("_HzbSize");
            public static readonly int HzbLevel = Shader.PropertyToID("_HzbLevel");
            public static readonly int HzbParams = Shader.PropertyToID("_HzbParams");
        }

        static int PhaseStateBase(int region) => region * k_PhaseStateStride;

        void InitOcclusion()
        {
            m_OccCapacity = Mathf.Max(1024, m_Settings.occlusionListCapacity);
            int regions = m_VisRegionCount;
            m_PhaseState = new GraphicsBuffer(GraphicsBuffer.Target.Structured, (1 + regions) * k_PhaseStateStride, 4);
            m_PhaseState.SetData(new uint[(1 + regions) * k_PhaseStateStride]);
            m_OccInstances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, regions * m_OccCapacity, 4);
            m_OccNodes = new GraphicsBuffer(GraphicsBuffer.Target.Structured, regions * m_OccCapacity, 8);
            m_OccClusters = new GraphicsBuffer(GraphicsBuffer.Target.Structured, regions * m_OccCapacity, 16);
            m_HzbDummy = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4);
            m_BoundHzb = m_HzbDummy;
            m_HzbCs = Resources.Load<ComputeShader>("UNanite/VgHzb");
            if (!HasKernels(m_HzbCs, "FromDepth", "Downsample"))
                m_HzbCs = null; // occlusion culling disabled
            if (m_HzbCs != null)
            {
                k_HzbFromDepth = m_HzbCs.FindKernel("FromDepth");
                k_HzbDownsample = m_HzbCs.FindKernel("Downsample");
            }
        }

        void DisposeOcclusion()
        {
            foreach (var b in new[] { m_PhaseState, m_OccInstances, m_OccNodes, m_OccClusters, m_HzbDummy, m_Scratch.hzb })
                b?.Dispose();
            m_PhaseState = m_OccInstances = m_OccNodes = m_OccClusters = m_HzbDummy = m_Scratch.hzb = null;
            foreach (var h in m_History.Values)
                h.hzb?.Dispose();
            m_History.Clear();
        }

        // ---------------------------------------------------------------------------------------
        // Instance eligibility: an occluded instance may only be deferred when phase 2 can render
        // everything it contains, i.e. every material bin goes through the visibility buffer.

        bool AllBinsResolve(int binBase, int slots)
        {
            for (int s = 0; s < slots; ++s)
            {
                int bin = (int)m_InstanceBins[binBase + s];
                if (bin < 0)
                    continue; // an undrawn submesh (beyond the renderer's materials)
                // M11: programmable bins are rasterised from the culling-time lists only (no phase 2)
                if (bin >= m_BinFlags.Count || (m_BinFlags[bin] & BinFlagResolve) == 0 || (m_BinFlags[bin] & BinFlagProgrammable) != 0)
                    return false;
            }
            return true;
        }

        void RefreshOccludableFlags()
        {
            for (int i = 0; i < m_InstanceHighWater; ++i)
            {
                int slots = m_InstanceMaterialCount[i];
                if (slots == 0)
                    continue;
                ref var inst = ref m_Instances[i];
                uint flags = AllBinsResolve((int)inst.materialBase, slots) ? inst.flags | VgInstanceGpu.FlagOccludable : inst.flags & ~VgInstanceGpu.FlagOccludable;
                if (flags != inst.flags)
                {
                    inst.flags = flags;
                    MarkDirty(i);
                }
            }
        }

        // ---------------------------------------------------------------------------------------
        // Culling-time configuration

        void ConfigureOcclusion(ref SubView sv)
        {
            sv.occlusion = m_Settings.occlusionCulling && sv.visRegion > 0 && m_HzbCs != null && sv.camera != null;
            sv.occHistory = null;
            sv.occPhase1 = false;
            if (sv.occlusion && m_History.TryGetValue(sv.camera, out var history) && history.valid)
            {
                sv.occHistory = history;
                sv.occPhase1 = true;
            }
        }

        void SetOcclusionConstants(CommandBuffer cmd, in SubView v, int phase, OcclusionHistory hzb)
        {
            int region = Mathf.Max(0, v.visRegion);
            int listBase = Mathf.Max(0, region - 1) * m_OccCapacity;
            if (phase == 0 || hzb == null || hzb.hzb == null)
            {
                cmd.SetComputeIntParams(m_Cull, Ids.OccConfig, 0, PhaseStateBase(region), listBase, m_OccCapacity);
                cmd.SetComputeIntParams(m_Cull, Ids.OccHzb, 1, 1, 1, SystemInfo.usesReversedZBuffer ? 1 : 0);
                m_BoundHzb = m_HzbDummy;
                return;
            }
            cmd.SetComputeIntParams(m_Cull, Ids.OccConfig, phase, PhaseStateBase(region), listBase, m_OccCapacity);
            cmd.SetComputeIntParams(m_Cull, Ids.OccHzb, hzb.width0, hzb.height0, hzb.levels, SystemInfo.usesReversedZBuffer ? 1 : 0);
            cmd.SetComputeMatrixParam(m_Cull, Ids.OccViewProj, hzb.viewProj);
            cmd.SetComputeVectorParam(m_Cull, Ids.OccScreen, new Vector4(hzb.screenWidth, hzb.screenHeight, 0, 0));
            m_BoundHzb = hzb.hzb;
        }

        void BindOcclusion(CommandBuffer cmd, int kernel)
        {
            cmd.SetComputeBufferParam(m_Cull, kernel, Ids.Hzb, m_BoundHzb ?? m_HzbDummy);
            cmd.SetComputeBufferParam(m_Cull, kernel, Ids.PhaseState, m_PhaseState);
            cmd.SetComputeBufferParam(m_Cull, kernel, Ids.OccludedInstances, m_OccInstances);
            cmd.SetComputeBufferParam(m_Cull, kernel, Ids.OccludedNodes, m_OccNodes);
            cmd.SetComputeBufferParam(m_Cull, kernel, Ids.OccludedClusters, m_OccClusters);
        }

        /// <summary>World → clip matrix HDRP rasterises with (render-to-texture flip, no TAA jitter).</summary>
        static Matrix4x4 CameraViewProj(Camera camera)
        {
            return GL.GetGPUProjectionMatrix(camera.projectionMatrix, true) * camera.worldToCameraMatrix;
        }

        // ---------------------------------------------------------------------------------------
        // HZB

        static void EnsureHzb(OcclusionHistory h, int screenWidth, int screenHeight)
        {
            int w0 = Mathf.NextPowerOfTwo(Mathf.Max(1, (screenWidth + 1) / 2));
            int h0 = Mathf.NextPowerOfTwo(Mathf.Max(1, (screenHeight + 1) / 2));
            int levels = 1;
            int total = 0;
            for (int w = w0, hh = h0; ; ++levels)
            {
                total += w * hh;
                if (w == 1 && hh == 1)
                    break;
                w = Mathf.Max(1, w / 2);
                hh = Mathf.Max(1, hh / 2);
            }
            if (h.hzb == null || h.hzb.count < total)
            {
                h.hzb?.Dispose();
                h.hzb = new GraphicsBuffer(GraphicsBuffer.Target.Structured, total, 4);
            }
            h.width0 = w0;
            h.height0 = h0;
            h.levels = levels;
            h.screenWidth = screenWidth;
            h.screenHeight = screenHeight;
        }

        void BuildHzb(CommandBuffer cmd, RenderTargetIdentifier depth, OcclusionHistory h)
        {
            cmd.BeginSample(HzbSampler);
            bool reversed = SystemInfo.usesReversedZBuffer;
            cmd.SetComputeVectorParam(m_HzbCs, OccIds.HzbParams, new Vector4(reversed ? 0f : 1f, reversed ? 1f : 0f, 0, 0));
            cmd.SetComputeIntParams(m_HzbCs, OccIds.HzbSize, h.width0, h.height0, h.screenWidth, h.screenHeight);
            cmd.SetComputeTextureParam(m_HzbCs, k_HzbFromDepth, OccIds.Depth, depth, 0, RenderTextureSubElement.Depth);
            cmd.SetComputeBufferParam(m_HzbCs, k_HzbFromDepth, OccIds.HzbOut, h.hzb);
            cmd.SetComputeIntParams(m_HzbCs, OccIds.HzbLevel, 0, h.levels, 0, 0);
            cmd.DispatchCompute(m_HzbCs, k_HzbFromDepth, (h.width0 + 15) / 16, (h.height0 + 15) / 16, 1);

            // each dispatch writes the next 4 levels (16x16 groups, groupshared reduction)
            cmd.SetComputeBufferParam(m_HzbCs, k_HzbDownsample, OccIds.HzbOut, h.hzb);
            for (int level = 4; level + 1 < h.levels; level += 4)
            {
                int srcW = Mathf.Max(1, h.width0 >> level), srcH = Mathf.Max(1, h.height0 >> level);
                cmd.SetComputeIntParams(m_HzbCs, OccIds.HzbLevel, level, h.levels, 0, 0);
                cmd.DispatchCompute(m_HzbCs, k_HzbDownsample, (srcW + 15) / 16, (srcH + 15) / 16, 1);
            }
            cmd.EndSample(HzbSampler);
        }

        // ---------------------------------------------------------------------------------------
        // Phase 2 (inside the BeforeRendering custom pass, after the phase-1 raster)

        void RecordPhase2(CommandBuffer cmd, RenderTargetIdentifier depth, in SubView v, int slot, int width, int height)
        {
            EnsureHzb(m_Scratch, width, height);
            m_Scratch.viewProj = CameraViewProj(v.camera);
            m_Scratch.valid = true;
            BuildHzb(cmd, depth, m_Scratch);

            cmd.BeginSample(Phase2Sampler);
            SetViewConstants(cmd, v, slot, 0);
            SetOcclusionConstants(cmd, v, 2, m_Scratch);
            foreach (int k in new[] { k_BeginPhase2, k_CullDeferredInstances, k_SeedDeferredNodes, k_PrepareNodes, k_TraverseNodes, k_PrepareClusters, k_CullClusters, k_CullDeferredClusters, k_FinishPhase2 })
                BindAll(cmd, k);
            cmd.DispatchCompute(m_Cull, k_BeginPhase2, 1, 1, 1);
            cmd.DispatchCompute(m_Cull, k_CullDeferredInstances, (m_InstanceHighWater + 63) / 64, 1, 1);
            cmd.DispatchCompute(m_Cull, k_SeedDeferredNodes, m_Counters, k_DispatchDeferredNodesOffset);
            DispatchTraversal(cmd, slot, 0);
            cmd.DispatchCompute(m_Cull, k_PrepareClusters, 1, 1, 1);
            cmd.DispatchCompute(m_Cull, k_CullClusters, m_Counters, k_DispatchClustersOffset);
            cmd.DispatchCompute(m_Cull, k_CullDeferredClusters, m_Counters, k_DispatchDeferredClustersOffset);
            cmd.DispatchCompute(m_Cull, k_FinishPhase2, 1, 1, 1);
            cmd.EndSample(Phase2Sampler);
        }

#if UNANITE_HDRP
        /// <summary>AfterOpaqueDepthAndNormal custom pass body: next frame's phase-1 HZB.</summary>
        internal void RenderOcclusionHistory(CustomPassContext ctx)
        {
            var camera = ctx.hdCamera.camera;
            if (!m_VisCameras.TryGetValue(camera, out var entry) || !entry.view.occlusion || entry.view.frozen)
                return;
            if (!m_History.TryGetValue(camera, out var history))
                m_History[camera] = history = new OcclusionHistory();
            EnsureHzb(history, ctx.hdCamera.actualWidth, ctx.hdCamera.actualHeight);
            history.viewProj = CameraViewProj(camera);
            BuildHzb(ctx.cmd, ctx.cameraDepthBuffer, history);
            history.valid = true;
        }
#endif

        /// <summary>
        /// Debug/tests: synchronous readback of the occlusion counters of the camera's last frame:
        /// (deferred instances + nodes + clusters in phase 1, clusters made visible again in phase 2).
        /// Returns (-1, -1) when the camera did not use two-phase occlusion.
        /// </summary>
        public (int deferred, int recovered) DebugReadOcclusion(Camera camera)
        {
            if (camera == null || !m_VisCameras.TryGetValue(camera, out var entry) || !entry.view.occlusion)
                return (-1, -1);
            var data = new uint[k_PhaseStateStride];
            m_PhaseState.GetData(data, 0, PhaseStateBase(entry.view.visRegion), k_PhaseStateStride);
            if (!entry.view.occPhase1)
                return (0, 0);
            return ((int)(data[0] + data[1] + data[2]), (int)(data[5] + data[6]));
        }

        void PruneOcclusionHistory()
        {
            m_DeadCameras.Clear();
            foreach (var cam in m_History.Keys)
                if (cam == null)
                    m_DeadCameras.Add(cam);
            foreach (var cam in m_DeadCameras)
            {
                m_History[cam].hzb?.Dispose();
                m_History.Remove(cam);
            }
        }
    }
}
