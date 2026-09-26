using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UNanite.Editor
{
    /// <summary>
    /// Converts Unity terrains to virtual geometry (M8) and back. Converting writes a .vgterrain
    /// sidecar next to the TerrainData (built by <see cref="VirtualGeometryTerrainImporter"/>) and adds a
    /// <see cref="VirtualGeometryTerrain"/>, which stops Unity drawing the heightmap; trees, details
    /// and the TerrainCollider are untouched. Reverting removes the component (Unity draws the
    /// heightmap again); the sidecar stays, so converting again is instant.
    /// </summary>
    public static class VirtualGeometryTerrainConverter
    {
        [MenuItem("Tools/UNanite/Convert Selected Terrains to Virtual Geometry")]
        [MenuItem("GameObject/UNanite/Convert Terrain to Virtual Geometry", false, 20)]
        static void ConvertSelected()
        {
            foreach (var terrain in Selection.gameObjects.Select(g => g.GetComponent<Terrain>()).Where(t => t != null))
                Convert(terrain);
        }

        [MenuItem("Tools/UNanite/Revert Selected Terrains to Unity Terrain")]
        [MenuItem("GameObject/UNanite/Revert Terrain to Unity Terrain", false, 21)]
        static void RevertSelected()
        {
            foreach (var terrain in Selection.gameObjects.Select(g => g.GetComponent<Terrain>()).Where(t => t != null))
                Revert(terrain);
        }

        [MenuItem("Tools/UNanite/Convert Selected Terrains to Virtual Geometry", true)]
        [MenuItem("GameObject/UNanite/Convert Terrain to Virtual Geometry", true)]
        [MenuItem("Tools/UNanite/Revert Selected Terrains to Unity Terrain", true)]
        [MenuItem("GameObject/UNanite/Revert Terrain to Unity Terrain", true)]
        static bool HasTerrainSelection() => Selection.gameObjects.Any(g => g.GetComponent<Terrain>() != null);

        /// <summary>Builds (or reuses) the terrain's virtual geometry and renders the terrain with it.</summary>
        public static VirtualGeometryTerrain Convert(Terrain terrain, VgTerrainBuildSettings settings = null)
        {
            var td = terrain.terrainData;
            if (td == null || string.IsNullOrEmpty(AssetDatabase.GetAssetPath(td)))
            {
                Debug.LogError($"UNanite: terrain '{terrain.name}' has no TerrainData asset", terrain);
                return null;
            }
            string sidecar = VirtualGeometryTerrainImporter.SidecarPathFor(AssetDatabase.GetAssetPath(td));
            var data = settings == null ? AssetDatabase.LoadAssetAtPath<VirtualGeometryTerrainData>(sidecar) : null;
            if (data != null && data.SourceHash != AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(td)).ToString())
            {
                // the TerrainData changed since the last build (its reimport may not have run yet)
                AssetDatabase.ImportAsset(sidecar, ImportAssetOptions.ForceUpdate);
                data = AssetDatabase.LoadAssetAtPath<VirtualGeometryTerrainData>(sidecar);
            }
            if (data == null)
                data = VirtualGeometryTerrainImporter.CreateSidecar(td, settings);
            if (data == null)
            {
                Debug.LogError($"UNanite: building virtual geometry for terrain '{terrain.name}' failed (see the import log of {sidecar})", terrain);
                return null;
            }
            var vg = terrain.GetComponent<VirtualGeometryTerrain>();
            if (vg == null)
                vg = Undo.AddComponent<VirtualGeometryTerrain>(terrain.gameObject);
            Undo.RecordObject(vg, "Convert Terrain to Virtual Geometry");
            vg.Data = data;
            EditorUtility.SetDirty(vg);
            return vg;
        }

        /// <summary>
        /// M11: renders the terrain's tree instances through virtual geometry: builds (or reuses) a VG
        /// mesh for every LOD0 renderer of every tree prototype and adds a
        /// <see cref="VirtualGeometryTerrainTrees"/>. Returns null (trees stay Unity's) if a prototype
        /// cannot be converted.
        /// </summary>
        public static VirtualGeometryTerrainTrees ConvertTrees(Terrain terrain, List<string> problems = null)
        {
            var td = terrain.terrainData;
            if (td == null || td.treePrototypes.Length == 0)
                return null;
            var renderers = new List<VirtualGeometryTerrainTrees.PrototypeRenderer>();
            var winds = new VgSpeedTreeWindParams[td.treePrototypes.Length];
            var graphs = new HashSet<Shader>();
            for (int p = 0; p < td.treePrototypes.Length; ++p)
            {
                var prefab = td.treePrototypes[p].prefab;
                if (prefab == null)
                {
                    problems?.Add($"{terrain.name}: tree prototype {p} has no prefab");
                    return null;
                }
                winds[p] = VirtualGeometryTerrainTrees.ReadWind(prefab) ?? new VgSpeedTreeWindParams(); // M11: SpeedTree 8 wind
                var lodGroup = prefab.GetComponent<LODGroup>();
                var lods = lodGroup != null && lodGroup.lodCount > 0 ? lodGroup.GetLODs() : null;
                // foliage (alpha-tested cards thin out when simplified): the prefab's own LODs as
                // discrete levels of unsimplified clusters; everything else: LOD0 with the cluster DAG
                bool foliage = lods != null && lods.Length > 1 && lods.Any(l => l.renderers.Any(r => r != null && r.sharedMaterials.Any(IsAlphaTested)));
                int levels = foliage ? lods.Length : 1;
                int before = renderers.Count;
                for (int l = 0; l < levels; ++l)
                {
                    var sources = lods != null ? lods[l].renderers.OfType<MeshRenderer>().ToArray() : prefab.GetComponentsInChildren<MeshRenderer>();
                    foreach (var mr in sources)
                    {
                        var mf = mr != null ? mr.GetComponent<MeshFilter>() : null;
                        if (mf == null || mf.sharedMesh == null)
                            continue;
                        var vg = VirtualGeometryConverter.FindOrCreateVirtualGeometry(mf.sharedMesh, foliage, out string reason);
                        if (vg == null)
                        {
                            problems?.Add($"{prefab.name}/{mr.name}: {reason}");
                            return null;
                        }
                        foreach (var m in mr.sharedMaterials)
                            if (m != null && VgShaderVariantGenerator.CanGenerate(m.shader))
                                graphs.Add(m.shader);
                        renderers.Add(new VirtualGeometryTerrainTrees.PrototypeRenderer
                        {
                            prototype = p,
                            mesh = vg,
                            materials = mr.sharedMaterials,
                            localToPrefab = prefab.transform.worldToLocalMatrix * mr.transform.localToWorldMatrix,
                            castShadows = mr.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off,
                            lod = l,
                            lodCount = levels,
                            lodTransition = foliage ? lods[l].screenRelativeTransitionHeight : 0f,
                            lodReferencePoint = lodGroup != null ? lodGroup.localReferencePoint : Vector3.zero,
                            lodSize = lodGroup != null ? lodGroup.size : 1f,
                        });
                    }
                }
                if (renderers.Count == before)
                {
                    problems?.Add($"{prefab.name}: no mesh renderer in LOD0");
                    return null;
                }
            }
            foreach (var shader in graphs)
                VgShaderVariantGenerator.GetOrCreate(shader, out _);

            var trees = terrain.GetComponent<VirtualGeometryTerrainTrees>();
            if (trees == null)
                trees = Undo.AddComponent<VirtualGeometryTerrainTrees>(terrain.gameObject);
            Undo.RecordObject(trees, "Convert Terrain Trees to Virtual Geometry");
            trees.Winds = winds;
            trees.Renderers = renderers.ToArray();
            EditorUtility.SetDirty(trees);
            EditorUtility.SetDirty(terrain);
            return trees;
        }

        /// <summary>
        /// M11: renders the terrain's mesh details (grass, bushes) through virtual geometry: a VG mesh
        /// (source clusters, no simplification) per detail prototype mesh and a
        /// <see cref="VirtualGeometryTerrainDetails"/>. Texture (billboard) details are not supported:
        /// returns null and the details stay Unity's.
        /// </summary>
        public static VirtualGeometryTerrainDetails ConvertDetails(Terrain terrain, List<string> problems = null)
        {
            var td = terrain.terrainData;
            if (td == null || td.detailPrototypes.Length == 0)
                return null;
            var layers = new List<VirtualGeometryTerrainDetails.Layer>();
            var graphs = new HashSet<Shader>();
            for (int l = 0; l < td.detailPrototypes.Length; ++l)
            {
                var proto = td.detailPrototypes[l];
                if (!proto.usePrototypeMesh || proto.prototype == null)
                {
                    problems?.Add($"{terrain.name}: detail {l} is a texture detail");
                    return null;
                }
                foreach (var mr in proto.prototype.GetComponentsInChildren<MeshRenderer>())
                {
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null)
                        continue;
                    var vg = VirtualGeometryConverter.FindOrCreateVirtualGeometry(mf.sharedMesh, true, out string reason);
                    if (vg == null)
                    {
                        problems?.Add($"{proto.prototype.name}: {reason}");
                        return null;
                    }
                    foreach (var m in mr.sharedMaterials)
                        if (m != null && VgShaderVariantGenerator.CanGenerate(m.shader))
                            graphs.Add(m.shader);
                    layers.Add(new VirtualGeometryTerrainDetails.Layer
                    {
                        layer = l,
                        mesh = vg,
                        materials = mr.sharedMaterials,
                        localToPrototype = proto.prototype.transform.worldToLocalMatrix * mr.transform.localToWorldMatrix,
                        castShadows = mr.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off,
                    });
                }
            }
            foreach (var shader in graphs)
                VgShaderVariantGenerator.GetOrCreate(shader, out _);
            var details = terrain.GetComponent<VirtualGeometryTerrainDetails>();
            if (details == null)
                details = Undo.AddComponent<VirtualGeometryTerrainDetails>(terrain.gameObject);
            Undo.RecordObject(details, "Convert Terrain Details to Virtual Geometry");
            details.Layers = layers.ToArray();
            EditorUtility.SetDirty(details);
            EditorUtility.SetDirty(terrain);
            return details;
        }

        static bool IsAlphaTested(Material m) =>
            m != null && (m.IsKeywordEnabled("_ALPHATEST_ON") || (m.renderQueue >= (int)UnityEngine.Rendering.RenderQueue.AlphaTest && m.renderQueue <= (int)UnityEngine.Rendering.RenderQueue.GeometryLast));

        public static void Revert(Terrain terrain)
        {
            var trees = terrain.GetComponent<VirtualGeometryTerrainTrees>();
            if (trees != null)
                Undo.DestroyObjectImmediate(trees);
            var vg = terrain.GetComponent<VirtualGeometryTerrain>();
            if (vg != null)
                Undo.DestroyObjectImmediate(vg);
            terrain.drawHeightmap = true;
            EditorUtility.SetDirty(terrain);
        }
    }
}
