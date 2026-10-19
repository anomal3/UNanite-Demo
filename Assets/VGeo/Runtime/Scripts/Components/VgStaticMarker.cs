using UnityEngine;

namespace VGeo
{
    /// <summary>
    /// Player builds: the static editor flags are editor-only, so the scene build step
    /// (VgStaticMarkerBuildStep) marks every static GameObject with a renderer with this empty component
    /// in the built scene (the saved scene is unchanged). <see cref="IsStatic"/> reads the flag in the
    /// editor and the marker in players.
    /// </summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class VgStaticMarker : MonoBehaviour
    {
        /// <summary>The GameObject is marked static (editor: its static flags; players: the build step's marker).</summary>
        public static bool IsStatic(GameObject go)
        {
            if (go == null)
                return false;
#if UNITY_EDITOR
            if (go.isStatic)
                return true;
#endif
            return go.TryGetComponent<VgStaticMarker>(out _);
        }
    }
}
