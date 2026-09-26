using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UNanite.Editor
{
    /// <summary>
    /// Player builds: the material twins of virtual geometry (HDRP/Lit -> Hidden/UNanite/LitResolve,
    /// Shader Graph -> its generated VG variant) are created at runtime and copy the source material's
    /// keywords. A build keeps only the shader_feature variants some built material uses, so without
    /// help the twins find no variant ("variant ... not found") and draw nothing. For the duration of
    /// the build, materials with every twin keyword set of the build's materials are written to a
    /// Resources folder (so they are built) and removed afterwards.
    /// </summary>
    sealed class VgShaderVariantBuildStep : BuildPlayerProcessor, IPostprocessBuildWithReport
    {
        const string Root = "Assets/UNanite Build Twins";
        const string Folder = Root + "/Resources";

        public override int callbackOrder => 0;

        public override void PrepareForBuild(BuildPlayerContext context)
        {
            Cleanup();
            var scenes = context.BuildPlayerOptions.scenes;
            if (scenes == null || scenes.Length == 0)
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            var roots = new List<string>(scenes);
            // materials reachable through Resources (spawned at runtime)
            foreach (string guid in AssetDatabase.FindAssets("t:Material t:Prefab t:ScriptableObject"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.Contains("/Resources/") && !path.StartsWith(Root))
                    roots.Add(path);
            }
            var materials = AssetDatabase.GetDependencies(roots.ToArray(), true)
                .Where(p => p.EndsWith(".mat"))
                .Select(AssetDatabase.LoadAssetAtPath<Material>)
                .Where(m => m != null && m.shader != null)
                .Distinct()
                .ToList();

            var resolve = Shader.Find("Hidden/UNanite/LitResolve");
            var twins = new Dictionary<string, Material>();
            foreach (var source in materials)
            {
                if (resolve != null && VgWorld.IsResolveCapable(source))
                    AddTwin(twins, resolve, source, false, false);
                if (VgShaderVariants.TryGet(source.shader, out var entry) && entry.variant != null)
                    for (int v = 0; v < 4; ++v)
                        AddTwin(twins, entry.variant, source, (v & 1) != 0, (v & 2) != 0);
            }
            if (twins.Count == 0)
                return;
            Directory.CreateDirectory(Folder);
            AssetDatabase.StartAssetEditing();
            try
            {
                int i = 0;
                foreach (var twin in twins.Values)
                    AssetDatabase.CreateAsset(twin, $"{Folder}/VgTwin{i++:0000}.mat");
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"UNanite: {twins.Count} twin keyword sets of {materials.Count} materials kept for the player build");
        }

        // a twin with the source's keywords; `noWind` / `noAlphaTest`: the variants runtime twins
        // switch to (bins without a wind slot, resolve of programmable bins)
        static void AddTwin(Dictionary<string, Material> twins, Shader shader, Material source, bool noWind, bool noAlphaTest)
        {
            var twin = new Material(shader) { name = source.name + " (VG build twin)" };
            twin.shaderKeywords = source.shaderKeywords;
            if (noWind)
            {
                if (!twin.HasProperty("_WindQuality"))
                {
                    Object.DestroyImmediate(twin);
                    return;
                }
                // as VgWorld.DisableSpeedTreeWind
                foreach (var kw in twin.shaderKeywords)
                    if (kw.StartsWith("_WINDQUALITY_"))
                        twin.DisableKeyword(kw);
                twin.EnableKeyword("_WINDQUALITY_NONE");
            }
            if (noAlphaTest)
            {
                if (!twin.IsKeywordEnabled("_ALPHATEST_ON"))
                {
                    Object.DestroyImmediate(twin);
                    return;
                }
                twin.DisableKeyword("_ALPHATEST_ON");
            }
            string key = shader.name + "|" + string.Join(" ", twin.enabledKeywords.Select(k => k.name).OrderBy(k => k));
            if (twins.ContainsKey(key))
                Object.DestroyImmediate(twin);
            else
                twins.Add(key, twin);
        }

        public void OnPostprocessBuild(BuildReport report) => Cleanup();

        static void Cleanup()
        {
            if (AssetDatabase.IsValidFolder(Root))
                AssetDatabase.DeleteAsset(Root);
        }
    }
}
