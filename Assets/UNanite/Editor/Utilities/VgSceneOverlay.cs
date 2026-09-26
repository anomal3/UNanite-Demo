using UnityEditor;
using UnityEditor.Overlays;
using UnityEditorInternal;
using UnityEngine;

namespace UNanite.Editor
{
    /// <summary>
    /// Scene View overlay: virtual geometry view mode (triangles / clusters / groups / LOD level /
    /// instances / materials), pixel error, visibility-buffer toggle and last-frame statistics per
    /// view ("VB" = visibility buffer + material resolve, otherwise vertex expansion).
    /// </summary>
    [Overlay(typeof(SceneView), k_Id, "UNanite", true)]
    sealed class VgSceneOverlay : IMGUIOverlay
    {
        const string k_Id = "unanite-overlay";
        const string k_PrefMode = "UNanite.DebugView";
        const string k_PrefError = "UNanite.PixelError";
        const string k_PrefVisBuffer = "UNanite.VisibilityBuffer";
        const string k_PrefOcclusion = "UNanite.OcclusionCulling";
        const string k_PrefReceivers = "UNanite.ShadowReceivers";

        [InitializeOnLoadMethod]
        static void RestorePrefs()
        {
            var s = VirtualGeometrySettings.Active;
            s.debugView = (VgDebugView)EditorPrefs.GetInt(k_PrefMode, 0);
            s.pixelError = EditorPrefs.GetFloat(k_PrefError, s.pixelError);
            s.visibilityBuffer = EditorPrefs.GetBool(k_PrefVisBuffer, s.visibilityBuffer);
            s.occlusionCulling = EditorPrefs.GetBool(k_PrefOcclusion, s.occlusionCulling);
            s.shadowReceiverCulling = (VgShadowReceiverCulling)EditorPrefs.GetInt(k_PrefReceivers, (int)s.shadowReceiverCulling);
        }

        public static void SetMode(VgDebugView mode)
        {
            VirtualGeometrySettings.Active.debugView = mode;
            EditorPrefs.SetInt(k_PrefMode, (int)mode);
            InternalEditorUtility.RepaintAllViews();
        }

        public override void OnGUI()
        {
            var s = VirtualGeometrySettings.Active;
            using (new EditorGUIUtility.IconSizeScope(Vector2.one * 16))
            {
                EditorGUIUtility.labelWidth = 70;
                EditorGUI.BeginChangeCheck();
                var mode = (VgDebugView)EditorGUILayout.EnumPopup("View", s.debugView, GUILayout.Width(220));
                float error = EditorGUILayout.Slider("Pixel error", s.pixelError, 0.25f, 16f, GUILayout.Width(220));
                bool visBuffer = EditorGUILayout.Toggle(new GUIContent("Vis buffer", "Main cameras: visibility buffer + material resolve (M3). Off: vertex expansion for every view."), s.visibilityBuffer);
                bool occlusion = EditorGUILayout.Toggle(new GUIContent("Occlusion", "Two-phase HZB occlusion culling for visibility-buffer cameras (M4)."), s.occlusionCulling);
                bool freeze = EditorGUILayout.Toggle(new GUIContent("Freeze", "Freeze culling: cameras keep culling from the current position; move them to see what was culled."), s.freezeCulling);
                var receivers = (VgShadowReceiverCulling)EditorGUILayout.EnumPopup(new GUIContent("Receivers", "Shadow receiver culling (M6): skip shadow casters whose shadows cannot reach anything the camera sees."), s.shadowReceiverCulling, GUILayout.Width(220));
                if (EditorGUI.EndChangeCheck())
                {
                    s.pixelError = error;
                    s.visibilityBuffer = visBuffer;
                    s.occlusionCulling = occlusion;
                    s.shadowReceiverCulling = receivers;
                    s.freezeCulling = freeze;
                    EditorPrefs.SetBool(k_PrefOcclusion, occlusion);
                    EditorPrefs.SetInt(k_PrefReceivers, (int)receivers);
                    EditorPrefs.SetFloat(k_PrefError, error);
                    EditorPrefs.SetBool(k_PrefVisBuffer, visBuffer);
                    SetMode(mode);
                }
            }

            var world = VgWorld.Instance;
            if (world == null)
            {
                EditorGUILayout.LabelField(VgWorld.IsSupported ? "No virtual geometry in the scene" : "Virtual geometry unsupported on this device", EditorStyles.miniLabel);
                return;
            }

            EditorGUILayout.LabelField($"{world.InstanceCount} instances, {world.MeshCount} meshes, pool {world.PagePoolBytes / 1048576.0:F1} MB", EditorStyles.miniLabel);
            var st = world.StreamingStats;
            if (st.enabled)
            {
                string sat = st.saturated ? "  POOL FULL" : "";
                EditorGUILayout.LabelField($"streaming: {st.residentPages:N0}/{st.slots:N0} slots, {st.residentBytes / 1048576.0:F0} of {st.streamableBytes / 1048576.0:F0} MB resident, " +
                                           $"{st.loadingPages} loading, {st.totalBytesUploaded / 1048576.0:F0} MB loaded{sat}", EditorStyles.miniLabel);
            }
            foreach (var v in world.LastFrameStats)
            {
                string overflow = v.overflowMask != 0 ? $"  OVERFLOW 0x{v.overflowMask:X}" : "";
                string path = v.visibilityBuffer ? " [VB]" : v.shadowRaster ? " [SR]" : "";
                if (v.receiverCulling)
                    path += $" [RC -{v.receiverCulled:N0}]";
                int clusters = v.visibleClusters + v.phase2Clusters;
                int tris = v.triangles + v.phase2Triangles;
                EditorGUILayout.LabelField($"{v.name}{path}: {clusters:N0} clusters, {tris:N0} tris{overflow}", EditorStyles.miniLabel);
                if (v.softwareClusters + v.softwareClustersPhase2 > 0)
                    EditorGUILayout.LabelField($"   software raster: {v.softwareClusters + v.softwareClustersPhase2:N0} clusters", EditorStyles.miniLabel);
                if (v.deferredClusters + v.deferredNodes + v.deferredInstances > 0)
                    EditorGUILayout.LabelField($"   occlusion: deferred {v.deferredInstances:N0} inst / {v.deferredNodes:N0} nodes / {v.deferredClusters:N0} clusters, phase 2 +{v.phase2Clusters:N0}", EditorStyles.miniLabel);
            }
        }

        const string k_Menu = "Tools/UNanite/Debug View/";
        [MenuItem(k_Menu + "Off", priority = 100)] static void Off() => SetMode(VgDebugView.None);
        [MenuItem(k_Menu + "Triangles", priority = 101)] static void Triangles() => SetMode(VgDebugView.Triangles);
        [MenuItem(k_Menu + "Clusters", priority = 102)] static void Clusters() => SetMode(VgDebugView.Clusters);
        [MenuItem(k_Menu + "Groups", priority = 103)] static void Groups() => SetMode(VgDebugView.Groups);
        [MenuItem(k_Menu + "LOD Level", priority = 104)] static void Lod() => SetMode(VgDebugView.LodLevel);
        [MenuItem(k_Menu + "Instances", priority = 105)] static void Instances() => SetMode(VgDebugView.Instances);
        [MenuItem(k_Menu + "Materials", priority = 106)] static void Materials() => SetMode(VgDebugView.Materials);
    }
}
