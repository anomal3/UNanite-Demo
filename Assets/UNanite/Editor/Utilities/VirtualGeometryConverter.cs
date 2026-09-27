using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UNanite.Editor
{
    /// <summary>
    /// Scene converter: MeshRenderer/MeshFilter (and LODGroup LOD0) → VirtualGeometryRenderer, keeping
    /// the original renderer disabled on the same GameObject so the change is revertible and the
    /// renderer serves as the fallback on unsupported devices.
    /// </summary>
    public static class VirtualGeometryConverter
    {
        [MenuItem("Tools/UNanite/Convert Selection to Virtual Geometry", priority = 10)]
        static void ConvertSelection()
        {
            var roots = Selection.gameObjects;
            int converted = Convert(roots, out var skipped);
            Debug.Log($"UNanite: converted {converted} renderer(s) to virtual geometry" + (skipped.Count > 0 ? $"; skipped: {string.Join(", ", skipped.Distinct())}" : ""));
        }

        [MenuItem("Tools/UNanite/Revert Selection to Mesh Renderers", priority = 11)]
        static void RevertSelection()
        {
            int reverted = Revert(Selection.gameObjects);
            Debug.Log($"UNanite: reverted {reverted} renderer(s)");
        }

        [MenuItem("Tools/UNanite/Convert Selection to Virtual Geometry", true)]
        [MenuItem("Tools/UNanite/Revert Selection to Mesh Renderers", true)]
        static bool HasSelection() => Selection.gameObjects.Length > 0;

        /// <summary>Converts every MeshRenderer under `roots` whose mesh can be built as virtual geometry.</summary>
        /// <summary>
        /// Converts every MeshRenderer under `roots` (LOD0 of LODGroups) to a VirtualGeometryRenderer.
        /// `skipTransparent`: renderers whose materials are all transparent stay regular renderers (M10: VG sorts
        /// transparent instances like MeshRenderers, up to VirtualGeometrySettings.maxSortedTransparentInstances).
        /// Mixed renderers (an opaque object with a small glass part) are converted with their transparent submeshes.
        /// </summary>
        public static int Convert(IEnumerable<GameObject> roots, out List<string> skipped, bool skipTransparent = false)
        {
            skipped = new List<string>();
            int converted = 0;
            var filters = roots.SelectMany(r => r.GetComponentsInChildren<MeshFilter>(true)).Distinct().ToList();

            // LODGroups: keep only LOD0 renderers; the others become redundant (VG handles LOD)
            var lodGroups = roots.SelectMany(r => r.GetComponentsInChildren<LODGroup>(true)).Distinct().ToList();
            var coarserLods = new HashSet<Renderer>();
            foreach (var g in lodGroups)
            {
                var lods = g.GetLODs();
                for (int l = 1; l < lods.Length; ++l)
                    foreach (var r in lods[l].renderers)
                        if (r != null)
                            coarserLods.Add(r);
            }

            var graphs = new HashSet<Shader>();
            Undo.SetCurrentGroupName("Convert to Virtual Geometry");
            int group = Undo.GetCurrentGroup();

            foreach (var mf in filters)
            {
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || mf.sharedMesh == null || mf.GetComponent<VirtualGeometryRenderer>() != null)
                    continue;
                if (coarserLods.Contains(mr))
                {
                    Undo.RecordObject(mr, "Disable coarse LOD");
                    mr.enabled = false;
                    continue;
                }

                if (skipTransparent && mr.sharedMaterials.All(m => m == null || m.renderQueue > (int)UnityEngine.Rendering.RenderQueue.GeometryLast))
                {
                    skipped.Add($"{mf.name} (transparent material)");
                    continue;
                }
                var vg = FindOrCreateVirtualGeometry(mf.sharedMesh, out string reason);
                if (vg == null)
                {
                    skipped.Add($"{mf.name} ({reason})");
                    continue;
                }

                var vgr = Undo.AddComponent<VirtualGeometryRenderer>(mf.gameObject);
                vgr.Mesh = vg;
                vgr.SharedMaterials = mr.sharedMaterials;
                vgr.ShadowCasting = mr.shadowCastingMode;
                Undo.RecordObject(mr, "Disable renderer");
                mr.enabled = false;
                converted++;
                foreach (var m in mr.sharedMaterials)
                    if (m != null && VgShaderVariantGenerator.CanGenerate(m.shader))
                        graphs.Add(m.shader);
            }
            // M10: Shader Graph materials draw through generated VG variants of their shader
            foreach (var shader in graphs)
                VgShaderVariantGenerator.GetOrCreate(shader, out _);

            foreach (var g in lodGroups)
            {
                Undo.RecordObject(g, "Disable LODGroup");
                g.enabled = false;
            }

            Undo.CollapseUndoOperations(group);
            return converted;
        }

        public static int Revert(IEnumerable<GameObject> roots)
        {
            int reverted = 0;
            Undo.SetCurrentGroupName("Revert Virtual Geometry");
            int group = Undo.GetCurrentGroup();
            foreach (var vgr in roots.SelectMany(r => r.GetComponentsInChildren<VirtualGeometryRenderer>(true)).Distinct().ToList())
            {
                var mr = vgr.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    Undo.RecordObject(mr, "Enable renderer");
                    mr.enabled = true;
                }
                Undo.DestroyObjectImmediate(vgr);
                reverted++;
            }
            foreach (var g in roots.SelectMany(r => r.GetComponentsInChildren<LODGroup>(true)).Distinct())
            {
                Undo.RecordObject(g, "Enable LODGroup");
                g.enabled = true;
                foreach (var lod in g.GetLODs())
                    foreach (var r in lod.renderers)
                        if (r != null && r.GetComponent<VirtualGeometryRenderer>() == null)
                        {
                            Undo.RecordObject(r, "Enable LOD renderer");
                            r.enabled = true;
                        }
            }
            Undo.CollapseUndoOperations(group);
            return reverted;
        }

        /// <summary>Returns the VirtualGeometryMesh built from `mesh`, enabling VG on its source asset if needed.</summary>
        public static VirtualGeometryMesh FindOrCreateVirtualGeometry(Mesh mesh, out string reason) => FindOrCreateVirtualGeometry(mesh, false, out reason);

        /// <summary>`noSimplification` (M11 foliage): the sidecar of the mesh's source builds source clusters only.</summary>
        public static VirtualGeometryMesh FindOrCreateVirtualGeometry(Mesh mesh, bool noSimplification, out string reason)
        {
            reason = null;
            string path = AssetDatabase.GetAssetPath(mesh);
            if (string.IsNullOrEmpty(path) || path.StartsWith("Library/") || path == "Resources/unity_builtin_extra")
            {
                reason = "mesh is not a project asset";
                return null;
            }

            string sidecar = VirtualGeometryImporter.SidecarPathFor(path);
            if (path.StartsWith("Packages/"))
            {
                // immutable package folders cannot hold the sidecar (M9): keep it in the project
                const string generated = "Assets/UNanite Generated";
                Directory.CreateDirectory(generated);
                sidecar = $"{generated}/{Path.GetFileNameWithoutExtension(path)}_{AssetDatabase.AssetPathToGUID(path).Substring(0, 8)}.{VirtualGeometryImporter.Extension}";
            }
            // M11: meshes with data beyond uv0.xy / uv1.xy (SpeedTree wind / LOD) keep it
            bool extraUVs = HasExtraUVs(mesh);
            if (!File.Exists(sidecar))
                VirtualGeometryImporter.CreateSidecar(path, new VgMeshBuildSettings { keepExtraUVs = extraUVs, noSimplification = noSimplification }, sidecar);
            else if (extraUVs || noSimplification)
            {
                var def = JsonUtility.FromJson<VgMeshDefinition>(File.ReadAllText(sidecar));
                if (def != null && ((extraUVs && !def.settings.keepExtraUVs) || (noSimplification && !def.settings.noSimplification)))
                {
                    def.settings.keepExtraUVs |= extraUVs;
                    def.settings.noSimplification |= noSimplification;
                    File.WriteAllText(sidecar, JsonUtility.ToJson(def, true));
                    AssetDatabase.ImportAsset(sidecar, ImportAssetOptions.ForceUpdate);
                }
            }

            var vg = FindBuilt(AssetDatabase.LoadAllAssetsAtPath(sidecar).OfType<VirtualGeometryMesh>(), mesh);
            if (vg == null)
                reason = "virtual geometry build failed (see console)";
            return vg;
        }

        // Sources can hold several meshes of the same name (glTF imports name every primitive after its
        // material): among those, the one with the mesh's triangle count and bounds.
        static VirtualGeometryMesh FindBuilt(IEnumerable<VirtualGeometryMesh> built, Mesh mesh)
        {
            var named = built.Where(v => v.SourceMeshName == mesh.name).ToList();
            if (named.Count <= 1)
                return named.FirstOrDefault();
            long triangles = 0;
            for (int s = 0; s < mesh.subMeshCount; ++s)
                if (mesh.GetTopology(s) == MeshTopology.Triangles)
                    triangles += mesh.GetIndexCount(s) / 3;
            var b = mesh.bounds;
            return named.OrderBy(v => v.Report.sourceTriangles == triangles ? 0 : 1)
                        .ThenBy(v => (v.LocalBounds.center - b.center).sqrMagnitude + (v.LocalBounds.size - b.size).sqrMagnitude)
                        .First();
        }

        /// <summary>M11: the mesh has UV components beyond uv0.xy / uv1.xy (e.g. SpeedTree 8 wind and LOD data).</summary>
        public static bool HasExtraUVs(Mesh mesh)
        {
            for (int channel = 0; channel <= 3; ++channel)
            {
                var attribute = (UnityEngine.Rendering.VertexAttribute)((int)UnityEngine.Rendering.VertexAttribute.TexCoord0 + channel);
                if (mesh.HasVertexAttribute(attribute) && (channel >= 2 || mesh.GetVertexAttributeDimension(attribute) > 2))
                    return true;
            }
            return false;
        }
    }
}
