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
    /// renderer serves as the fallback on unsupported devices. Foliage LODGroups (alpha-tested LOD0)
    /// keep every LOD, drawn as discrete virtual geometry LODs (VirtualGeometryLodGroup, M14).
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

            // LODGroups: keep only LOD0 renderers; the others become redundant (VG handles LOD). Foliage
            // groups convert every LOD (discrete LODs, see UsesDiscreteLods)
            var lodGroups = roots.SelectMany(r => r.GetComponentsInChildren<LODGroup>(true)).Distinct().ToList();
            var coarserLods = new HashSet<Renderer>();
            foreach (var g in lodGroups)
            {
                if (UsesDiscreteLods(g))
                    continue;
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
                if (ConvertOne(mf, mr, skipped, graphs))
                    converted++;
            }
            // M10: Shader Graph materials draw through generated VG variants of their shader
            foreach (var shader in graphs)
                VgShaderVariantGenerator.GetOrCreate(shader, out _);

            foreach (var g in lodGroups)
                DisableLodGroup(g);

            Undo.CollapseUndoOperations(group);
            return converted;
        }

        /// <summary>
        /// M14: a LODGroup whose LOD0 is alpha-tested (foliage) keeps its LODs: its renderers are built
        /// without simplification, so each LOD is converted and drawn as a discrete virtual geometry LOD
        /// (VirtualGeometryLodGroup) instead of the cluster DAG taking over from LOD0.
        /// </summary>
        public static bool UsesDiscreteLods(LODGroup group)
        {
            if (group == null || group.lodCount < 2)
                return false;
            var lod0 = group.GetLODs()[0].renderers;
            return lod0 != null && lod0.Any(r => r != null && r.sharedMaterials.Any(IsAlphaTested));
        }

        // after its renderers were converted: VG draws the LODs (discrete ones through a VirtualGeometryLodGroup)
        static void DisableLodGroup(LODGroup g)
        {
            if (UsesDiscreteLods(g))
            {
                // LOD renderers that could not be converted would draw at every distance with the group off
                foreach (var lod in g.GetLODs())
                    foreach (var r in lod.renderers)
                        if (r != null && r.enabled && r.GetComponent<VirtualGeometryRenderer>() == null)
                        {
                            Undo.RecordObject(r, "Disable LOD renderer");
                            r.enabled = false;
                        }
                if (g.GetComponent<VirtualGeometryLodGroup>() == null)
                    Undo.AddComponent<VirtualGeometryLodGroup>(g.gameObject);
            }
            Undo.RecordObject(g, "Disable LODGroup");
            g.enabled = false;
        }

        // the LODGroup draws again: its discrete-LOD component goes
        static void RemoveLodGroup(LODGroup g)
        {
            var discrete = g.GetComponent<VirtualGeometryLodGroup>();
            if (discrete != null)
                Undo.DestroyObjectImmediate(discrete);
        }

        // one MeshRenderer -> VirtualGeometryRenderer (undo), its Shader Graphs collected for variant generation.
        // Foliage (alpha-tested materials): source clusters only - simplifying alpha-tested cards removes
        // them (their small geometric error lets the DAG drop whole leaves even up close); M11 terrain
        // trees do the same with the prefab's own LODs as discrete levels.
        static bool ConvertOne(MeshFilter mf, MeshRenderer mr, List<string> skipped, HashSet<Shader> graphs)
        {
            var vg = FindOrCreateVirtualGeometry(mf.sharedMesh, mr.sharedMaterials.Any(IsAlphaTested), out string reason);
            if (vg == null)
            {
                skipped.Add($"{mf.name} ({reason})");
                return false;
            }
            var vgr = Undo.AddComponent<VirtualGeometryRenderer>(mf.gameObject);
            vgr.Mesh = vg;
            vgr.SharedMaterials = mr.sharedMaterials;
            vgr.ShadowCasting = mr.shadowCastingMode;
            Undo.RecordObject(mr, "Disable renderer");
            mr.enabled = false;
            foreach (var m in mr.sharedMaterials)
                if (m != null && VgShaderVariantGenerator.CanGenerate(m.shader))
                    graphs.Add(m.shader);
            return true;
        }

        /// <summary>
        /// M14: converts exactly these renderers (not their children). A renderer in LOD0 of a LODGroup also
        /// disables the group and its coarser LODs (virtual geometry does the LOD); coarser-LOD renderers are
        /// skipped. A renderer of a foliage group (UsesDiscreteLods) converts every LOD of the group.
        /// Transparent-only renderers are skipped when `skipTransparent`.
        /// </summary>
        public static int ConvertRenderers(IEnumerable<MeshRenderer> renderers, out List<string> skipped, bool skipTransparent = false)
        {
            skipped = new List<string>();
            int converted = 0;
            var graphs = new HashSet<Shader>();
            var groups = new HashSet<LODGroup>();
            Undo.SetCurrentGroupName("Convert to Virtual Geometry");
            int undoGroup = Undo.GetCurrentGroup();
            var list = renderers.Where(r => r != null).Distinct().ToList();
            // a renderer of a foliage group brings the group's other LODs (all drawn as discrete LODs)
            var listed = new HashSet<MeshRenderer>(list);
            var discreteGroups = list.Select(r => (r, g: r.GetComponentInParent<LODGroup>(true)))
                                     .Where(x => LodIndexOf(x.g, x.r) >= 0 && UsesDiscreteLods(x.g))
                                     .Select(x => x.g).Distinct().ToList();
            foreach (var g in discreteGroups)
                foreach (var lod in g.GetLODs())
                    foreach (var r in lod.renderers.OfType<MeshRenderer>())
                        if (listed.Add(r))
                            list.Add(r);
            foreach (var mr in list)
            {
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null || mr.GetComponent<VirtualGeometryRenderer>() != null)
                    continue;
                var lodGroup = mr.GetComponentInParent<LODGroup>(true);
                int lod = LodIndexOf(lodGroup, mr);
                if (lod > 0 && !UsesDiscreteLods(lodGroup))
                {
                    skipped.Add($"{mr.name} (coarser LOD of {lodGroup.name})");
                    continue;
                }
                if (skipTransparent && mr.sharedMaterials.All(m => m == null || m.renderQueue > (int)UnityEngine.Rendering.RenderQueue.GeometryLast))
                {
                    skipped.Add($"{mr.name} (transparent material)");
                    continue;
                }
                if (!ConvertOne(mf, mr, skipped, graphs))
                    continue;
                converted++;
                if (lod >= 0)
                    groups.Add(lodGroup);
            }
            foreach (var g in groups)
            {
                if (!UsesDiscreteLods(g))
                {
                    var lods = g.GetLODs();
                    for (int l = 1; l < lods.Length; ++l)
                        foreach (var r in lods[l].renderers)
                            if (r != null && r.enabled)
                            {
                                Undo.RecordObject(r, "Disable coarse LOD");
                                r.enabled = false;
                            }
                }
                DisableLodGroup(g);
            }
            foreach (var shader in graphs)
                VgShaderVariantGenerator.GetOrCreate(shader, out _);
            Undo.CollapseUndoOperations(undoGroup);
            return converted;
        }

        /// <summary>Alpha clip on (keyword or an alpha-test render queue): foliage, fences.</summary>
        public static bool IsAlphaTested(Material m) =>
            m != null && (m.IsKeywordEnabled("_ALPHATEST_ON") ||
                          (m.renderQueue >= (int)UnityEngine.Rendering.RenderQueue.AlphaTest && m.renderQueue <= (int)UnityEngine.Rendering.RenderQueue.GeometryLast));

        /// <summary>LOD index of `renderer` in `group` (-1: not in it or no group).</summary>
        public static int LodIndexOf(LODGroup group, Renderer renderer)
        {
            if (group == null)
                return -1;
            var lods = group.GetLODs();
            for (int l = 0; l < lods.Length; ++l)
                if (lods[l].renderers != null && System.Array.IndexOf(lods[l].renderers, renderer) >= 0)
                    return l;
            return -1;
        }

        /// <summary>
        /// M14: reverts exactly these virtual geometry renderers (their LODGroups come back too; a renderer
        /// of a discrete-LOD group reverts the whole group).
        /// </summary>
        public static int RevertRenderers(IEnumerable<VirtualGeometryRenderer> renderers)
        {
            var list = renderers.Where(r => r != null).Distinct().ToList();
            var listed = new HashSet<VirtualGeometryRenderer>(list);
            foreach (var d in list.Select(r => r.GetComponentInParent<VirtualGeometryLodGroup>(true)).Where(d => d != null).Distinct().ToList())
                foreach (var lod in d.Group.GetLODs())
                    foreach (var r in lod.renderers)
                        if (r != null && r.TryGetComponent<VirtualGeometryRenderer>(out var vgr) && listed.Add(vgr))
                            list.Add(vgr);
            Undo.SetCurrentGroupName("Revert Virtual Geometry");
            int undoGroup = Undo.GetCurrentGroup();
            var groups = new HashSet<LODGroup>();
            foreach (var vgr in list)
            {
                var mr = vgr.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    Undo.RecordObject(mr, "Enable renderer");
                    mr.enabled = true;
                    var g = mr.GetComponentInParent<LODGroup>(true);
                    int lod = LodIndexOf(g, mr);
                    if (lod == 0 || (lod > 0 && g.GetComponent<VirtualGeometryLodGroup>() != null))
                        groups.Add(g);
                }
                Undo.DestroyObjectImmediate(vgr);
            }
            foreach (var g in groups)
            {
                RemoveLodGroup(g);
                Undo.RecordObject(g, "Enable LODGroup");
                g.enabled = true;
                foreach (var lod in g.GetLODs())
                    foreach (var r in lod.renderers)
                        if (r != null && r.GetComponent<VirtualGeometryRenderer>() == null && !r.enabled)
                        {
                            Undo.RecordObject(r, "Enable LOD renderer");
                            r.enabled = true;
                        }
            }
            Undo.CollapseUndoOperations(undoGroup);
            return list.Count;
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
                RemoveLodGroup(g);
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
