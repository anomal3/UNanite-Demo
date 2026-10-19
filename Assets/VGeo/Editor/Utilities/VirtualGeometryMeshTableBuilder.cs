using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace VGeo.Editor
{
    /// <summary>
    /// Builds a <see cref="VirtualGeometryMeshTable"/> for runtime conversion: every mesh of the renderers under
    /// the roots that <see cref="VirtualGeometryRuntimeConversion.Collect"/> would convert gets its virtual
    /// geometry (a .vgmesh sidecar next to its source asset, all missing sidecars imported in one batch).
    /// The scene is not changed.
    /// </summary>
    public static class VirtualGeometryMeshTableBuilder
    {
        [MenuItem("Tools/VGeo/Build Mesh Table for Selection", priority = 12)]
        static void BuildForSelection()
        {
            string path = EditorUtility.SaveFilePanelInProject("Virtual geometry mesh table", "VirtualGeometryMeshTable", "asset",
                "Mesh table for runtime conversion of the selected hierarchies (an existing table is replaced)");
            if (string.IsNullOrEmpty(path))
                return;
            var table = AssetDatabase.LoadAssetAtPath<VirtualGeometryMeshTable>(path);
            if (table == null)
            {
                table = ScriptableObject.CreateInstance<VirtualGeometryMeshTable>();
                AssetDatabase.CreateAsset(table, path);
            }
            Debug.Log(Build(Selection.gameObjects, table, VirtualGeometryRuntimeConversion.Options.Default));
        }

        [MenuItem("Tools/VGeo/Build Mesh Table for Selection", true)]
        static bool HasSelection() => Selection.gameObjects.Length > 0;

        const string BuiltinPath = "Library/unity default resources";
        const string BuiltinFolder = "Assets/VGeo Generated/Builtin";

        // a project copy of a built-in mesh (the table maps the built-in mesh itself to the copy's virtual geometry)
        static Mesh BuiltinCopy(Mesh builtin)
        {
            string path = $"{BuiltinFolder}/{builtin.name}.asset";
            var copy = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (copy != null)
                return copy;
            Directory.CreateDirectory(BuiltinFolder);
            copy = UnityEngine.Object.Instantiate(builtin);
            copy.name = builtin.name;
            AssetDatabase.CreateAsset(copy, path);
            return copy;
        }

        /// <summary>
        /// Fills `table` with the virtual geometry of every mesh the runtime conversion of `roots` under
        /// `options` would draw. Returns a report (meshes, triangles, skipped renderers by reason).
        /// </summary>
        /// <param name="simplifyMinTriangles">Source assets whose meshes all have fewer triangles are built from source clusters
        /// only (no simplified DAG levels): small props lose thin details (a sign on a wall, a panel) when simplified, since
        /// their geometric error is tiny while their colour is not, and gain nothing from LOD. 0 = simplify everything.</param>
        public static string Build(IEnumerable<GameObject> roots, VirtualGeometryMeshTable table, VirtualGeometryRuntimeConversion.Options options,
                                   int simplifyMinTriangles = 0)
        {
            var skipped = new Dictionary<string, int>();
            var renderers = VirtualGeometryRuntimeConversion.Collect(roots.Where(r => r != null).Select(r => r.transform), null, options, null, skipped);
            var meshes = renderers.Select(r => r.GetComponent<MeshFilter>().sharedMesh).Where(m => m != null).Distinct().ToList();

            // Unity's built-in primitives (Cube, Cylinder...) are not project assets: a copy in the project is built
            var builtinCopies = new Dictionary<Mesh, Mesh>();
            foreach (var mesh in meshes)
                if (AssetDatabase.GetAssetPath(mesh) == BuiltinPath)
                    builtinCopies[mesh] = BuiltinCopy(mesh);

            // largest mesh per source (decides simplification)
            var sourceMax = new Dictionary<string, long>();
            foreach (var original in meshes)
            {
                var m = builtinCopies.TryGetValue(original, out var c) ? c : original;
                string path = AssetDatabase.GetAssetPath(m);
                sourceMax.TryGetValue(path, out long t);
                sourceMax[path] = Math.Max(t, VirtualGeometryRuntimeConversion.TriangleCount(m));
            }
            int unsimplified = 0;

            // sidecars of every source first, imported together
            var sources = new List<string>();
            var failedMeshes = new Dictionary<string, int>();
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var original in meshes)
                {
                    var mesh = builtinCopies.TryGetValue(original, out var copy) ? copy : original;
                    string source = AssetDatabase.GetAssetPath(mesh);
                    if (string.IsNullOrEmpty(source) || source.StartsWith("Library/") || source == "Resources/unity_builtin_extra")
                        continue;
                    if (sources.Contains(source))
                        continue;
                    sources.Add(source);
                    string sidecar = VirtualGeometryConverter.SidecarFor(source);
                    bool noSimplification = simplifyMinTriangles > 0 && sourceMax[source] < simplifyMinTriangles;
                    if (noSimplification)
                        unsimplified++;
                    var def = File.Exists(sidecar) ? JsonUtility.FromJson<VgMeshDefinition>(File.ReadAllText(sidecar)) : null;
                    if (def == null || def.settings == null || def.settings.noSimplification != noSimplification)
                    {
                        var settings = def?.settings ?? new VgMeshBuildSettings { keepExtraUVs = VirtualGeometryConverter.HasExtraUVs(mesh) };
                        settings.noSimplification = noSimplification;
                        VirtualGeometryImporter.WriteSidecar(source, settings, sidecar);
                        AssetDatabase.ImportAsset(sidecar);
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            var entries = new List<VirtualGeometryMeshTable.Entry>();
            long triangles = 0;
            foreach (var mesh in meshes)
            {
                var built = builtinCopies.TryGetValue(mesh, out var copy) ? copy : mesh;
                var vg = VirtualGeometryConverter.FindOrCreateVirtualGeometry(built, out string reason);
                if (vg == null)
                {
                    failedMeshes.TryGetValue(reason ?? "?", out int n);
                    failedMeshes[reason ?? "?"] = n + 1;
                    continue;
                }
                entries.Add(new VirtualGeometryMeshTable.Entry { mesh = mesh, virtualGeometry = vg });
                triangles += VirtualGeometryRuntimeConversion.TriangleCount(mesh);
            }
            table.SetEntries(entries);
            EditorUtility.SetDirty(table);
            AssetDatabase.SaveAssetIfDirty(table); // only the table (SaveAssets would also write whatever else is dirty)

            var sb = new StringBuilder();
            sb.Append($"VGeo mesh table '{table.name}': {entries.Count} meshes ({triangles:N0} source triangles) from {sources.Count} source assets for {renderers.Count} renderers");
            if (simplifyMinTriangles > 0)
                sb.Append($"; {unsimplified} sources below {simplifyMinTriangles} triangles built without simplification");
            foreach (var pair in failedMeshes.OrderByDescending(p => p.Value))
                sb.Append($"\n  mesh not built ({pair.Key}): {pair.Value}");
            foreach (var pair in skipped.OrderByDescending(p => p.Value))
                sb.Append($"\n  renderers kept ({pair.Key}): {pair.Value}");
            return sb.ToString();
        }
    }
}
