using UnityEngine;

namespace UNanite
{
    /// <summary>Visualisation modes of the GPU-driven path (shown in the Scene View overlay).</summary>
    public enum VgDebugView
    {
        None = 0,
        Triangles = 1,
        Clusters = 2,
        Groups = 3,
        LodLevel = 4,
        Instances = 5,
        Materials = 6,
    }

    /// <summary>Which camera-visible region keeps shadow casters alive in receiver culling (M6).</summary>
    public enum VgShadowReceiverCulling
    {
        /// <summary>Every caster inside the split's frustum is rendered.</summary>
        Off = 0,
        /// <summary>Casters must shadow some point on a camera view ray between the near plane and the opaque depth (safe for transparent receivers and volumetric fog).</summary>
        VisibleVolume = 1,
        /// <summary>Casters must shadow a visible opaque surface. Faster; shadows on transparent surfaces and in volumetric fog may miss casters.</summary>
        VisibleSurfaces = 2,
    }

    /// <summary>
    /// Project-wide virtual geometry settings. Put an asset named "VirtualGeometrySettings" in a
    /// Resources folder to override the defaults (Assets/Create/UNanite/Settings).
    /// </summary>
    [CreateAssetMenu(menuName = "UNanite/Settings", fileName = "VirtualGeometrySettings")]
    public sealed class VirtualGeometrySettings : ScriptableObject
    {
        [Header("Quality")]
        [Tooltip("Maximum projected simplification error in pixels for camera views.")]
        [Range(0.25f, 16f)] public float pixelError = 1f;
        [Tooltip("Maximum projected simplification error in shadow-map texels.")]
        [Range(0.25f, 32f)] public float shadowTexelError = 2f;
        [Tooltip("Shadow casters are never finer than the camera needs them: the coarser of the shadow-texel LOD and the camera's pixel-error LOD. Keeps distant casters of long low-sun cascades cheap (Unity Terrain's shadows use camera LOD too).")]
        public bool shadowCameraLod = true;
        [Tooltip("M11: casters of directional shadow splits must lie in the camera's frustum extruded toward the light (like Unity's shadow caster culling). HDRP's ray tracing 'Extend Shadow Culling' removes that bound from the split planes.")]
        public bool shadowCasterFrustum = true;
        [Tooltip("Cull the shadow splits of a camera (receiver culling on) in one set of dispatches instead of one sequence per split (M9 follow-up; ~17 dependent dispatches per split otherwise).")]
        public bool batchShadowCulling = true;
        [Tooltip("Shaders besides HDRP/Lit whose shadows may use the vertex-pulled shadow raster: opaque shaders (e.g. Shader Graphs) that do not move vertices. Alpha-tested / displaced materials are still excluded by their keywords.")]
        public string[] shadowRasterShaders = new string[0];
        [Tooltip("M10: materials whose shader has a generated VG variant (Shader Graphs, see Tools > UNanite > Generate Shader Variants) draw every pass with vertices pulled from the page pool, with per-instance object matrices, instead of the world-space vertex expansion.")]
        public bool pulledMaterials = true;
        [Tooltip("M10: instances of transparent materials get their own draw (sorted back to front by HDRP like MeshRenderers) up to this count; further ones share one draw per material (unsorted).")]
        public int maxSortedTransparentInstances = 256;
        [Tooltip("M11: in visibility-buffer views, Shader Graph materials with alpha clip or a vertex graph (foliage, wind) rasterise into the visibility buffer with their own pass and are shaded once per pixel by the resolve. Off = they draw through their pulled passes (depth prepass + GBuffer).")]
        public bool programmableRaster = true;
        [Tooltip("M11: the programmable raster also writes hardware barycentrics (D3D12 / Vulkan / Metal, DXC): the resolve of foliage takes them and the raster depth instead of evaluating the vertex graph's moved positions (wind) at the three corners of every pixel and intersecting them.")]
        public bool barycentricRaster = true;
        [Tooltip("M11: with barycentricRaster, the programmable raster of graphs that move vertices (wind, time) also writes each pixel's motion vector from the vertex graph at the previous frame's time and SpeedTree wind: swaying foliage gets motion vectors (TAA, motion blur) instead of camera motion only.")]
        public bool rasterMotionVectors = true;
        [Tooltip("M11: instances (pivot) within this camera distance (m) get the motion of their animated vertices; farther, wind moves foliage by less than a pixel per frame and it keeps camera / object motion (no second vertex graph evaluation).")]
        public float rasterMotionDistance = 60f;
        [Tooltip("M11 density LOD: instances flagged for it (terrain details) are thinned out beyond this distance from the camera (m): a fraction (start / distance)^exponent survives, the survivors grow to keep the covered area. 0 = off.")]
        public float densityLodStart = 25f;
        [Tooltip("M11 density LOD: 2 keeps the number of instances per screen area roughly constant (like a LOD keeps the triangle size); 3 thins faster (the default: grass shaders fade out with distance anyway).")]
        [Range(0.5f, 4f)] public float densityLodExponent = 3f;
        [Tooltip("M11 density LOD: the largest growth of a surviving instance (coverage compensation); beyond it the density falls.")]
        [Range(1f, 4f)] public float densityLodMaxScale = 2f;
        [Tooltip("M11 density LOD: part of its vanishing distance over which an instance shrinks to nothing (no popping).")]
        [Range(0.01f, 1f)] public float densityLodFade = 0.25f;
        [Tooltip("M11: SpeedTree 8 materials of instances with wind parameters (terrain trees) sway in the scene's directional WindZones (SpeedTree's wind update re-implemented; Unity's own is internal). Off = virtual geometry trees are static.")]
        public bool speedTreeWind = true;
        [Tooltip("Shadow map resolution assumed for shadow LOD selection (per cascade / face).")]
        public int shadowResolution = 2048;

        [Header("Visibility buffer (M3)")]
        [Tooltip("Main cameras (HDRP deferred) rasterise VG into a visibility buffer and shade it with a per-material resolve inside HDRP's GBuffer pass. Off = every view uses vertex expansion.")]
        public bool visibilityBuffer = true;
        [Tooltip("Cameras per rendered frame that can use the visibility buffer (each keeps its own visible-cluster list: 16 bytes x visible cluster capacity).")]
        [Range(1, 8)] public int maxVisibilityBufferCameras = 4;
        [Tooltip("Resolve tile list capacity as a multiple of the screen's 8x8 tile count (tiles containing several materials use several entries).")]
        [Range(1, 16)] public int resolveTileListFactor = 4;

        [Header("Occlusion culling (M4)")]
        [Tooltip("Two-phase HZB occlusion culling for visibility-buffer cameras: previous-frame HZB first, deferred work re-tested against the current frame's depth.")]
        public bool occlusionCulling = true;
        [Tooltip("Deferred instances / nodes / clusters per camera and phase (overflow is rendered, never dropped). 28 bytes per entry per camera.")]
        public int occlusionListCapacity = 1 << 17;
        [Tooltip("Draw the depth of regular opaque MeshRenderers before the VG raster so walls and terrain occlude VG in the current frame too (costs one extra depth draw of those meshes).")]
        public bool occlusionFromMeshRenderers = true;
        [Tooltip("Also draw alpha-tested renderers (foliage, grass) as occluders. Off: only opaque ones without alpha test; leaves and grass are poor occluders and costly to draw twice (3.4 ms for the Terrain Sample's trees and details).")]
        public bool occluderAlphaTested;

        [Header("Software raster (M5)")]
        [Tooltip("Visibility-buffer cameras rasterise small clusters in a compute shader (64-bit atomics, D3D12/Vulkan with DXC). Ignored where unsupported.")]
        public bool softwareRaster = true;
        [Tooltip("Clusters whose projected bounding-sphere diameter is below this many pixels use the software raster.")]
        [Range(4f, 256f)] public float swRasterThreshold = 32f;

        [Header("Shadows (M6)")]
        [Tooltip("Shadow maps rasterise VG with a vertex-pulling ShadowCaster pass instead of expanding the geometry (materials whose shadow pass writes plain depth: HDRP/Lit without alpha test, displacement or depth offset).")]
        public bool shadowRaster = true;
        [Tooltip("Cull shadow casters whose shadows cannot reach anything the camera sees, using the camera depth of the current frame (HDRP renders shadow maps after the depth prepass). Lights with cached shadow maps are never receiver-culled.")]
        public VgShadowReceiverCulling shadowReceiverCulling = VgShadowReceiverCulling.VisibleVolume;
        [Tooltip("Receiver footprint dilation in shadow texels (at Shadow Resolution): PCF kernels, normal bias.")]
        [Range(0f, 64f)] public float shadowReceiverMarginTexels = 8f;
        [Tooltip("Receiver footprint dilation in world units: PCSS blocker search / penumbra.")]
        [Range(0f, 8f)] public float shadowReceiverMarginWorld = 0.5f;
        [Tooltip("Depth chunks per camera view ray used to project the visible volume into light space (more = tighter, slower).")]
        [Range(4, 64)] public int shadowReceiverDepthChunks = 24;
        [Tooltip("Visible shadow clusters per frame over all shadow splits (16 bytes each).")]
        public int shadowRecordCapacity = 1 << 20;

        [Header("Streaming (M7)")]
        [Tooltip("Stream pages on demand: the GPU reports the pages its LOD cut wants, they are loaded (from the page file or memory) into a fixed pool and evicted least-recently-used. Off = every page resident.")]
        public bool streaming = true;
        [Tooltip("GPU page pool budget in MB: root pages of every registered mesh plus 128 KB streaming slots (at most 2000 MB).")]
        [Range(16, 2000)] public int streamingPoolMB = 512;
        [Tooltip("Upload budget per frame in MB (page copies into the pool).")]
        [Range(0.5f, 64f)] public float streamingUploadMBPerFrame = 8f;
        [Tooltip("Page loads in flight (each holds a 128 KB staging buffer).")]
        [Range(4, 256)] public int streamingLoadsInFlight = 64;
        [Tooltip("Distinct pages the GPU can report per rendered frame (8 bytes each in the readback).")]
        public int streamingFeedbackCapacity = 1 << 14;

        [Header("Capacities (GPU memory)")]
        [Tooltip("Vertices in the per-frame expansion arena, shared by all views (48 bytes each).")]
        public int arenaVertexCapacity = 4 << 20;
        [Tooltip("Indices in the per-frame expansion arena, shared by all views (4 bytes each).")]
        public int arenaIndexCapacity = 24 << 20;
        [Tooltip("Visible clusters per view.")]
        public int visibleClusterCapacity = 1 << 18;
        [Tooltip("BVH node work items per view.")]
        public int nodeQueueCapacity = 1 << 20;
        [Tooltip("Group work items per view.")]
        public int groupItemCapacity = 1 << 19;
        [Tooltip("Views (cameras, cascades, faces) per rendered frame.")]
        public int maxViewsPerFrame = 64;

        [Header("Debug")]
        [Tooltip("Colour-code virtual geometry in camera views (shadows keep the real materials).")]
        public VgDebugView debugView = VgDebugView.None;
        public bool disableConeCulling;
        [Tooltip("Cameras keep culling (frustum, LOD, occlusion) from where they were when this was switched on, while rendering from where they move: shows what was culled.")]
        public bool freezeCulling;
        [Tooltip("Debug: run the GPU pipeline only up to a stage. 0 = everything, 1 = instance cull, 2 = + BVH traversal, 3 = + cluster cull, 4 = + expansion (no draws).")]
        [Range(0, 4)] public int debugStopAfterStage;

        static VirtualGeometrySettings s_Active;

        public static VirtualGeometrySettings Active
        {
            get
            {
                if (s_Active == null)
                {
                    s_Active = Resources.Load<VirtualGeometrySettings>("VirtualGeometrySettings");
                    if (s_Active == null)
                    {
                        s_Active = CreateInstance<VirtualGeometrySettings>();
                        s_Active.hideFlags = HideFlags.HideAndDontSave;
                    }
                }
                return s_Active;
            }
            set => s_Active = value;
        }
    }
}
