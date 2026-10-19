using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VGeo.Editor
{
    /// <summary>
    /// Player builds: static editor flags do not exist in players, so every static GameObject with a
    /// MeshRenderer gets a <see cref="VgStaticMarker"/> in the built scene (the saved scene is not changed).
    /// Runtime conversion (VirtualGeometryRuntimeConversion, onlyStatic) and the transform tracking of
    /// VirtualGeometryRenderer read it through VgStaticMarker.IsStatic.
    /// </summary>
    sealed class VgStaticMarkerBuildStep : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report == null)
                return; // entering Play Mode: the editor has the flags
            int marked = 0;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var go = renderer.gameObject;
                    if (!go.isStatic || go.GetComponent<VgStaticMarker>() != null)
                        continue;
                    go.AddComponent<VgStaticMarker>();
                    marked++;
                }
            if (marked > 0)
                Debug.Log($"VGeo: {marked} static renderers marked in '{scene.name}' (VgStaticMarker)");
        }
    }
}
