#if VGEO_URP
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RendererUtils;
using UnityEngine.Rendering.Universal;

namespace VGeo
{
    // URP integration: the HDRP custom passes of M3 / M4 / M5 as URP render passes, enqueued for every camera
    // from beginCameraRendering (no renderer feature has to be added to the renderer assets):
    //
    //   "VGeo Visibility Buffer"   BeforeRenderingPrePasses, right after the camera targets are cleared:
    //                              occluder depth of regular opaque renderers (M4), phase-1 raster into the
    //                              visibility buffer + URP's camera depth attachment, software raster (M5),
    //                              phase 2 against the HZB of that depth (M4), tile classification (M3)
    //   URP's own passes           the resolve twins (Hidden/VGeo/URP/LitResolve) draw the bins' tiles in
    //                              the GBuffer pass (deferred), the opaque pass (forward / Forward+) and the
    //                              depth / depth-normals prepasses; every later pass sees VG depth
    //   "VGeo Occlusion History"   AfterRenderingOpaques: HZB of the complete opaque depth, the camera's
    //                              phase-1 occluders of the next frame (M4)
    //
    // Shadows: URP renders shadow maps before the camera's depth exists, so splits are culled at BRG time
    // (no receiver culling); shadow-capable bins use the URP sub-shader of Hidden/VGeo/ShadowRaster.
    // Cameras that cannot use the visibility buffer (forward with MSAA, rendering straight to the back
    // buffer, overlay cameras, XR, reflection probes, previews) draw every bin through vertex expansion.
    public sealed unsafe partial class VgWorld
    {
        VgUrpVisibilityPass m_UrpVisibilityPass;
        VgUrpHistoryPass m_UrpHistoryPass;
        readonly Dictionary<Camera, RTHandle> m_UrpVisBuffers = new Dictionary<Camera, RTHandle>();

        static readonly ShaderTagId[] s_UrpDepthTags = { new ShaderTagId("DepthOnly") };
        static readonly RenderStateBlock s_UrpNoColorWrites = new RenderStateBlock(RenderStateMask.Blend)
        {
            blendState = new BlendState { blendState0 = new RenderTargetBlendState((ColorWriteMask)0) },
        };

        /// <summary>Visibility-buffer passes executed by URP cameras since creation (tests / diagnostics).</summary>
        public int UrpVisibilityPassCount { get; private set; }

        // the asset (the pipeline instance may not exist yet when the world is created at scene load)
        static bool UrpActive => GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset;

        void InitUrp()
        {
            m_UrpVisibilityPass = new VgUrpVisibilityPass();
            m_UrpHistoryPass = new VgUrpHistoryPass();
            // URP's camera textures are Texture2D, HDRP's are (XR) texture arrays: the kernels reading the
            // visibility buffer and the camera depth compile both (VG_TEXTURE_2D)
            SetUrpTextureKeywords(UrpActive);
        }

        void SetUrpTextureKeywords(bool urp)
        {
            foreach (var cs in new[] { m_Classify, m_HzbCs, m_RcvCs })
                if (cs != null && cs.IsKeywordEnabled(new LocalKeyword(cs, "VG_TEXTURE_2D")) != urp)
                    cs.SetKeyword(new LocalKeyword(cs, "VG_TEXTURE_2D"), urp);
        }

        void DisposeUrp()
        {
            foreach (var rt in m_UrpVisBuffers.Values)
                rt?.Release();
            m_UrpVisBuffers.Clear();
            m_UrpVisibilityPass = null;
            m_UrpHistoryPass = null;
        }

        // beginCameraRendering: URP has reset the renderer's pass queue and adds the features' passes after
        // this, so the passes run for every camera without a renderer feature
        void EnqueueUrpPasses(Camera camera)
        {
            if (m_UrpVisibilityPass == null || !UrpActive)
                return;
            // preview cameras (inspector, asset thumbnails) never see virtual geometry through the visibility buffer
            if (camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView)
                return;
            var data = camera.GetUniversalAdditionalCameraData();
            if (data == null || !(data.scriptableRenderer is UniversalRenderer renderer))
                return;
            renderer.EnqueuePass(m_UrpVisibilityPass);
            renderer.EnqueuePass(m_UrpHistoryPass);
        }

        // culling time: the capability recorded by the camera's pass on its last frame
        string UrpVisibilityBufferBlocker(Camera camera)
        {
            if (camera == null)
                return "not a camera view";
            if (m_UrpVisibilityPass == null)
                return "platform";
            if (camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView)
                return $"camera type {camera.cameraType}";
            if (camera.stereoEnabled)
                return "XR";
            if (!m_CameraBlockers.TryGetValue(camera, out var blocker))
                return "first frame (render pass not seen yet)";
            return blocker;
        }

        // render time: what the camera's actual frame allows (recorded for its next culling)
        static string UrpFrameBlocker(RenderGraph renderGraph, UniversalCameraData cameraData, UniversalResourceData resourceData)
        {
            if (!(cameraData.renderer is UniversalRenderer))
                return "not a Universal Renderer";
            if (cameraData.renderType != CameraRenderType.Base)
                return "overlay camera";
            if (cameraData.xrRendering && cameraData.xr != null && cameraData.xr.enabled)
                return "XR";
            if (resourceData.isActiveTargetBackBuffer || !resourceData.activeDepthTexture.IsValid())
                return "renders to the back buffer (no intermediate texture)";
            var depth = renderGraph.GetTextureDesc(resourceData.activeDepthTexture);
            if (depth.msaaSamples != MSAASamples.None)
                return "MSAA";
            if (depth.sizeMode != TextureSizeMode.Explicit || depth.useDynamicScale)
                return "dynamic resolution";
            return null;
        }

        // the camera's visibility buffer: one per camera, exactly the size of its depth attachment
        RTHandle UrpVisBuffer(Camera camera, int width, int height)
        {
            m_UrpVisBuffers.TryGetValue(camera, out var rt);
            if (rt == null || rt.rt == null || rt.rt.width != width || rt.rt.height != height)
            {
                rt?.Release();
                rt = RTHandles.Alloc(width, height, colorFormat: GraphicsFormat.R32G32_UInt, filterMode: FilterMode.Point,
                    name: "VGeo VisBuffer " + camera.name);
                m_UrpVisBuffers[camera] = rt;
            }
            return rt;
        }

        void PruneUrpVisBuffers()
        {
            m_DeadCameras.Clear();
            foreach (var pair in m_UrpVisBuffers)
                if (pair.Key == null)
                    m_DeadCameras.Add(pair.Key);
            foreach (var cam in m_DeadCameras)
            {
                m_UrpVisBuffers[cam]?.Release();
                m_UrpVisBuffers.Remove(cam);
            }
        }

        internal sealed class UrpVisibilityData
        {
            public VgWorld world;
            public Camera camera;
            public TextureHandle depth;
            public RendererListHandle occluders;
            public bool drawOccluders;
            public int width, height;
            public VgSwView swView;
        }

        internal sealed class UrpHistoryData
        {
            public VgWorld world;
            public Camera camera;
            public TextureHandle depth;
            public int width, height;
        }

        // records the visibility pass of a camera (URP's RecordRenderGraph)
        internal void RecordUrpVisibility(RenderGraph renderGraph, ContextContainer frameData, ProfilingSampler sampler)
        {
            var cameraData = frameData.Get<UniversalCameraData>();
            var resourceData = frameData.Get<UniversalResourceData>();
            var camera = cameraData.camera;
            m_CameraBlockers[camera] = UrpFrameBlocker(renderGraph, cameraData, resourceData);
            if (m_CameraBlockers[camera] != null || !m_VisCameras.TryGetValue(camera, out var view) || m_PagePool == null)
                return;

            var depthDesc = renderGraph.GetTextureDesc(resourceData.activeDepthTexture);
            using (var builder = renderGraph.AddUnsafePass<UrpVisibilityData>("VGeo Visibility Buffer", out var data, sampler))
            {
                data.world = this;
                data.camera = camera;
                data.depth = resourceData.activeDepthTexture;
                data.width = cameraData.cameraTargetDescriptor.width;
                data.height = cameraData.cameraTargetDescriptor.height;
                data.swView = new VgSwView
                {
                    viewProj = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(), true) * cameraData.GetViewMatrix(),
                    origin = Vector3.zero,
                    cameraPos = camera.transform.position,
                    forward = camera.transform.forward,
                    perspective = !camera.orthographic,
                };
                // the attachment's own size (it can exceed the camera's pixels only with scaling, blocked above)
                data.width = Mathf.Min(data.width, depthDesc.width);
                data.height = Mathf.Min(data.height, depthDesc.height);
                builder.UseTexture(data.depth, AccessFlags.ReadWrite);

                // M4: regular opaque renderers occlude VG in the raster and in the phase-2 HZB; VG's own draws
                // (batch layer k_BatchLayer) and the GPU Resident Drawer's indirect draws (their instance
                // culling of this frame runs later) stay out
                data.drawOccluders = view.view.occlusion && m_Settings.occlusionFromMeshRenderers;
                if (data.drawOccluders)
                {
                    var renderingData = frameData.Get<UniversalRenderingData>();
                    var desc = new RendererListDesc(s_UrpDepthTags, renderingData.cullResults, camera)
                    {
                        renderQueueRange = m_Settings.occluderAlphaTested ? RenderQueueRange.opaque : new RenderQueueRange(RenderQueueRange.opaque.lowerBound, (int)RenderQueue.AlphaTest - 1),
                        sortingCriteria = SortingCriteria.CommonOpaque,
                        stateBlock = s_UrpNoColorWrites,
                        layerMask = camera.cullingMask,
                        batchLayerMask = ~((1u << k_BatchLayer) | (1u << 28)),
                    };
                    data.occluders = renderGraph.CreateRendererList(desc);
                    builder.UseRendererList(data.occluders);
                }

                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((UrpVisibilityData d, UnsafeGraphContext ctx) =>
                    d.world.RenderUrpVisibility(CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd), d));
            }
        }

        void RenderUrpVisibility(CommandBuffer cmd, UrpVisibilityData d)
        {
            if (!m_VisCameras.TryGetValue(d.camera, out var view))
                return;
            UrpVisibilityPassCount++;
            VisibilityPassCount++;
            SetUrpTextureKeywords(true);
            RTHandle depth = d.depth;
            int width = d.width, height = d.height;
            var visBuffer = UrpVisBuffer(d.camera, depth.rt.width, depth.rt.height);
            EnsureTileList(width, height);

            CoreUtils.SetRenderTarget(cmd, visBuffer, depth, ClearFlag.Color, Color.clear);
            if (d.drawOccluders)
            {
                cmd.BeginSample(OccluderSampler);
                cmd.DrawRendererList(d.occluders);
                cmd.EndSample(OccluderSampler);
            }

            DrawRaster(cmd, view.view, view.slot, 0);
            if (view.view.swEnabled)
                RenderSoftware(cmd, visBuffer, depth, view.view, view.slot, 0, width, height, d.swView);

            // M4 phase 2: HZB of the phase-1 depth, re-test deferred work, raster what became visible
            if (view.view.occPhase1 && !view.view.frozen)
            {
                CoreUtils.SetRenderTarget(cmd, visBuffer, ClearFlag.None); // depth is read by VG.BuildHZB
                RecordPhase2(cmd, depth, view.view, view.slot, width, height);
                CoreUtils.SetRenderTarget(cmd, visBuffer, depth, ClearFlag.None);
                DrawRaster(cmd, view.view, view.slot, 1);
                if (view.view.swEnabled)
                    RenderSoftware(cmd, visBuffer, depth, view.view, view.slot, 1, width, height, d.swView);
            }

            RecordClassification(cmd, visBuffer, width, height);
            SetResolveGlobals(cmd, visBuffer);
        }

        // records the occlusion history pass of a camera (AfterRenderingOpaques)
        internal void RecordUrpHistory(RenderGraph renderGraph, ContextContainer frameData, ProfilingSampler sampler)
        {
            var cameraData = frameData.Get<UniversalCameraData>();
            var resourceData = frameData.Get<UniversalResourceData>();
            var camera = cameraData.camera;
            if (!m_VisCameras.TryGetValue(camera, out var entry) || !entry.view.occlusion || entry.view.frozen)
                return;
            if (m_CameraBlockers.TryGetValue(camera, out var blocker) && blocker != null)
                return; // the visibility pass did not run this frame
            if (!resourceData.activeDepthTexture.IsValid())
                return;
            using (var builder = renderGraph.AddUnsafePass<UrpHistoryData>("VGeo Occlusion History", out var data, sampler))
            {
                data.world = this;
                data.camera = camera;
                data.depth = resourceData.activeDepthTexture;
                var depthDesc = renderGraph.GetTextureDesc(data.depth);
                data.width = Mathf.Min(cameraData.cameraTargetDescriptor.width, depthDesc.width);
                data.height = Mathf.Min(cameraData.cameraTargetDescriptor.height, depthDesc.height);
                builder.UseTexture(data.depth, AccessFlags.Read);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((UrpHistoryData d, UnsafeGraphContext ctx) =>
                    d.world.RenderUrpHistory(CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd), d));
            }
        }

        void RenderUrpHistory(CommandBuffer cmd, UrpHistoryData d)
        {
            SetUrpTextureKeywords(true);
            if (!m_History.TryGetValue(d.camera, out var history))
                m_History[d.camera] = history = new OcclusionHistory();
            EnsureHzb(history, d.width, d.height);
            history.viewProj = CameraViewProj(d.camera);
            RTHandle depth = d.depth;
            BuildHzb(cmd, depth, history);
            history.valid = true;
        }
    }

    /// <summary>URP: the visibility-buffer raster of a camera, before URP's prepasses (see VgWorld.Urp.cs).</summary>
    sealed class VgUrpVisibilityPass : ScriptableRenderPass
    {
        public VgUrpVisibilityPass()
        {
            renderPassEvent = RenderPassEvent.BeforeRenderingPrePasses;
            profilingSampler = new ProfilingSampler("VGeo Visibility Buffer");
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            VgWorld.Instance?.RecordUrpVisibility(renderGraph, frameData, profilingSampler);
        }
    }

    /// <summary>URP: HZB of a camera's complete opaque depth for its next frame's occlusion culling.</summary>
    sealed class VgUrpHistoryPass : ScriptableRenderPass
    {
        public VgUrpHistoryPass()
        {
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
            profilingSampler = new ProfilingSampler("VGeo Occlusion History");
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            VgWorld.Instance?.RecordUrpHistory(renderGraph, frameData, profilingSampler);
        }
    }
}
#endif
