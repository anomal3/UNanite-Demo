using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UNanite.Editor
{
    /// <summary>
    /// Adds a "Virtual Geometry" toggle to the header of every model/mesh asset inspector. Enabling it
    /// writes a .vgmesh sidecar next to the asset (built by VirtualGeometryImporter); disabling it
    /// deletes the sidecar.
    /// </summary>
    [InitializeOnLoad]
    static class VirtualGeometryToggle
    {
        static readonly GUIContent k_Label = new GUIContent("Virtual Geometry",
            "Build a Nanite-style cluster DAG for every mesh in this asset (writes a .vgmesh file next to it).");

        static VirtualGeometryToggle()
        {
            UnityEditor.Editor.finishedDefaultHeaderGUI += OnHeaderGUI;
        }

        static bool IsMeshSource(Object target, out string path)
        {
            path = null;
            if (target is ModelImporter importer)
                path = importer.assetPath;
            else if (target is Mesh mesh && AssetDatabase.IsMainAsset(mesh))
                path = AssetDatabase.GetAssetPath(mesh);
            return !string.IsNullOrEmpty(path) && !path.EndsWith("." + VirtualGeometryImporter.Extension);
        }

        static void OnHeaderGUI(UnityEditor.Editor editor)
        {
            var paths = editor.targets.Select(t => IsMeshSource(t, out var p) ? p : null).ToArray();
            if (paths.Length == 0 || paths.Any(p => p == null))
                return;

            bool[] states = paths.Select(p => File.Exists(VirtualGeometryImporter.SidecarPathFor(p))).ToArray();
            bool mixed = states.Distinct().Count() > 1;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.showMixedValue = mixed;
                EditorGUI.BeginChangeCheck();
                bool enabled = EditorGUILayout.ToggleLeft(k_Label, states[0]);
                EditorGUI.showMixedValue = false;
                if (EditorGUI.EndChangeCheck())
                    SetEnabled(paths, enabled);

                if (!mixed && states[0] && paths.Length == 1 && GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(50)))
                    Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(VirtualGeometryImporter.SidecarPathFor(paths[0]));
            }
        }

        public static void SetEnabled(string[] sourcePaths, bool enabled)
        {
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var path in sourcePaths)
                {
                    string sidecar = VirtualGeometryImporter.SidecarPathFor(path);
                    if (enabled && !File.Exists(sidecar))
                        VirtualGeometryImporter.CreateSidecar(path);
                    else if (!enabled && File.Exists(sidecar))
                        AssetDatabase.DeleteAsset(sidecar);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
        }
    }
}
