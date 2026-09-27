using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace UNanite.Editor
{
    /// <summary>M14 Scene Setup: what an audited object is now.</summary>
    public enum VgAuditStatus
    {
        /// <summary>Rendered as virtual geometry.</summary>
        VirtualGeometry,
        /// <summary>A regular renderer the converter can turn into virtual geometry.</summary>
        Convertible,
        /// <summary>Stays a regular renderer (see the reason).</summary>
        Skipped,
    }

    /// <summary>M14 Scene Setup: the path a material takes as virtual geometry.</summary>
    public enum VgMaterialPath
    {
        None,
        /// <summary>HDRP/Lit: visibility buffer + UNanite's material resolve (fastest).</summary>
        Resolve,
        /// <summary>Shader Graph whose generated variant resolves from the visibility buffer.</summary>
        GraphResolve,
        /// <summary>Shader Graph with alpha clip or a vertex graph (wind): programmable raster (M11).</summary>
        Programmable,
        /// <summary>Shader Graph drawn through its generated pulled-vertex passes.</summary>
        Pulled,
        /// <summary>Shader Graph without a generated variant yet (Generate Shader Variants).</summary>
        GraphVariantMissing,
        /// <summary>Transparent: one sorted draw per instance (M10).</summary>
        Transparent,
        /// <summary>Any other shader: world-space vertex expansion drawn with the material itself.</summary>
        Expansion,
    }

    /// <summary>One audited object of the open scenes.</summary>
    public sealed class VgAuditEntry
    {
        public Object target;          // MeshRenderer, VirtualGeometryRenderer's MeshRenderer, SkinnedMeshRenderer or Terrain
        public string name, kind;      // kind: Mesh, Terrain, Terrain trees, Terrain details, Skinned mesh
        public string scenePath;       // hierarchy path
        public VgAuditStatus status;
        public string reason;          // why skipped / notes
        public long triangles;
        public Material[] materials = new Material[0];
        public VgMaterialPath[] paths = new VgMaterialPath[0];
        public bool shadowRaster;      // every opaque material can use the vertex-pulled shadow raster
        public bool IsTerrain => target is Terrain;
    }

    /// <summary>
    /// M14: audit of the open scenes for the Scene Setup window - every MeshRenderer, skinned mesh and
    /// terrain with its virtual-geometry status, triangles and the path of each material.
    /// </summary>
    public static class VgSceneAudit
    {
        public static List<VgAuditEntry> Scan(int minTriangles = 0)
        {
            var result = new List<VgAuditEntry>();
            for (int s = 0; s < SceneManager.sceneCount; ++s)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded)
                    continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
                        if (!IsHidden(mr.gameObject))
                            result.Add(AuditMesh(mr, minTriangles));
                    foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        if (!IsHidden(smr.gameObject))
                            result.Add(new VgAuditEntry
                            {
                                target = smr, name = smr.name, kind = "Skinned mesh", scenePath = PathOf(smr.transform),
                                status = VgAuditStatus.Skipped, reason = "skinned meshes are not virtual geometry",
                                triangles = smr.sharedMesh != null ? Triangles(smr.sharedMesh) : 0,
                                materials = smr.sharedMaterials, paths = smr.sharedMaterials.Select(_ => VgMaterialPath.None).ToArray(),
                            });
                    foreach (var terrain in root.GetComponentsInChildren<Terrain>(true))
                        if (!IsHidden(terrain.gameObject))
                            AuditTerrain(terrain, result);
                }
            }
            return result;
        }

        // editor-only / generated objects (ray tracing proxies, preview objects) are not the user's content
        static bool IsHidden(GameObject go) => (go.hideFlags & (HideFlags.HideInHierarchy | HideFlags.DontSave)) != 0;

        static VgAuditEntry AuditMesh(MeshRenderer mr, int minTriangles)
        {
            var mf = mr.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            var materials = mr.sharedMaterials;
            var e = new VgAuditEntry
            {
                target = mr, name = mr.name, kind = "Mesh", scenePath = PathOf(mr.transform),
                triangles = mesh != null ? Triangles(mesh) : 0,
                materials = materials,
                paths = materials.Select(Classify).ToArray(),
                shadowRaster = materials.Length > 0 && materials.All(m => m == null || IsTransparent(m) || VgWorld.IsShadowRasterCapable(m)),
            };
            var vgr = mr.GetComponent<VirtualGeometryRenderer>();
            if (vgr != null && vgr.enabled)
            {
                e.status = VgAuditStatus.VirtualGeometry;
                if (vgr.Mesh == null)
                    e.reason = "no virtual geometry mesh assigned";
                return e;
            }
            e.status = VgAuditStatus.Skipped;
            if (mesh == null)
                e.reason = "no mesh";
            else if (VirtualGeometryConverter.LodIndexOf(mr.GetComponentInParent<LODGroup>(true), mr) > 0)
                e.reason = VirtualGeometryConverter.UsesDiscreteLods(mr.GetComponentInParent<LODGroup>(true))
                    ? "coarser LOD of foliage (converted with LOD0, drawn as a discrete LOD)"
                    : "coarser LOD (virtual geometry builds its own LODs from LOD0)";
            else if (!IsProjectAsset(mesh))
                e.reason = "mesh is not a project asset (built-in or generated at runtime)";
            else if (!mr.enabled)
                e.reason = "renderer disabled";
            else if (e.triangles < minTriangles)
                e.reason = $"fewer than {minTriangles} triangles";
            else
            {
                e.status = VgAuditStatus.Convertible;
                if (e.paths.Contains(VgMaterialPath.GraphVariantMissing))
                    e.reason = "Shader Graph variant is generated on conversion";
                else if (e.paths.Length > 0 && e.paths.All(p => p == VgMaterialPath.Transparent || p == VgMaterialPath.None))
                    e.reason = "transparent: one sorted draw per instance";
            }
            return e;
        }

        static void AuditTerrain(Terrain terrain, List<VgAuditEntry> result)
        {
            var data = terrain.terrainData;
            var vgt = terrain.GetComponent<VirtualGeometryTerrain>();
            long heightmapTriangles = data != null ? 2L * (data.heightmapResolution - 1) * (data.heightmapResolution - 1) : 0;
            var material = terrain.materialTemplate;
            result.Add(new VgAuditEntry
            {
                target = terrain, name = terrain.name, kind = "Terrain", scenePath = PathOf(terrain.transform),
                status = vgt != null && vgt.enabled ? VgAuditStatus.VirtualGeometry : data == null ? VgAuditStatus.Skipped : VgAuditStatus.Convertible,
                reason = data == null ? "no terrain data" : null,
                triangles = heightmapTriangles,
                materials = new[] { material },
                paths = new[] { material != null && material.shader.name == "HDRP/TerrainLit" ? VgMaterialPath.Resolve : VgMaterialPath.Expansion },
            });
            if (data == null)
                return;
            if (data.treeInstanceCount > 0)
            {
                var trees = terrain.GetComponent<VirtualGeometryTerrainTrees>();
                result.Add(new VgAuditEntry
                {
                    target = terrain, name = terrain.name + " (trees)", kind = "Terrain trees", scenePath = PathOf(terrain.transform),
                    status = trees != null && trees.enabled ? VgAuditStatus.VirtualGeometry : VgAuditStatus.Convertible,
                    reason = $"{data.treeInstanceCount} tree instances of {data.treePrototypes.Length} prototypes",
                });
            }
            if (data.detailPrototypes.Length > 0)
            {
                var details = terrain.GetComponent<VirtualGeometryTerrainDetails>();
                result.Add(new VgAuditEntry
                {
                    target = terrain, name = terrain.name + " (details)", kind = "Terrain details", scenePath = PathOf(terrain.transform),
                    status = details != null && details.enabled ? VgAuditStatus.VirtualGeometry : VgAuditStatus.Convertible,
                    reason = $"{data.detailPrototypes.Length} detail prototypes (grass, meshes)",
                });
            }
        }

        /// <summary>The path `m` takes as virtual geometry (VgWorld's resolve / variant rules).</summary>
        public static VgMaterialPath Classify(Material m)
        {
            if (m == null || m.shader == null)
                return VgMaterialPath.None;
            if (IsTransparent(m))
                return VgMaterialPath.Transparent;
            if (VgWorld.IsResolveCapable(m))
                return VgMaterialPath.Resolve;
            if (VgShaderVariants.TryGet(m.shader, out var entry) && entry.variant != null)
            {
                bool alpha = m.IsKeywordEnabled("_ALPHATEST_ON") || m.renderQueue >= (int)RenderQueue.AlphaTest;
                if (entry.resolve && !alpha)
                    return VgMaterialPath.GraphResolve;
                return entry.programmable ? VgMaterialPath.Programmable : VgMaterialPath.Pulled;
            }
            if (VgShaderVariantGenerator.CanGenerate(m.shader))
                return VgMaterialPath.GraphVariantMissing;
            return VgMaterialPath.Expansion;
        }

        public static string Describe(VgMaterialPath p) => p switch
        {
            VgMaterialPath.Resolve => "resolve",
            VgMaterialPath.GraphResolve => "graph resolve",
            VgMaterialPath.Programmable => "programmable raster",
            VgMaterialPath.Pulled => "pulled passes",
            VgMaterialPath.GraphVariantMissing => "graph (variant missing)",
            VgMaterialPath.Transparent => "transparent",
            VgMaterialPath.Expansion => "expansion",
            _ => "-",
        };

        static bool IsTransparent(Material m) => m.renderQueue > (int)RenderQueue.GeometryLast;

        static bool IsProjectAsset(Mesh mesh)
        {
            string path = AssetDatabase.GetAssetPath(mesh);
            return !string.IsNullOrEmpty(path) && !path.StartsWith("Library/") && path != "Resources/unity_builtin_extra";
        }

        public static long Triangles(Mesh mesh)
        {
            long t = 0;
            for (int s = 0; s < mesh.subMeshCount; ++s)
                if (mesh.GetTopology(s) == MeshTopology.Triangles)
                    t += mesh.GetIndexCount(s) / 3;
            return t;
        }

        static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (; t != null; t = t.parent)
                parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
