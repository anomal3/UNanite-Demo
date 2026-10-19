using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VGeo
{
    /// <summary>
    /// Runtime conversion of regular renderers to virtual geometry and back. The scene keeps its
    /// MeshRenderers and LODGroups (what devices without virtual geometry, or quality levels that do not
    /// use it, render), and a <see cref="VirtualGeometryMeshTable"/> maps their meshes to built virtual
    /// geometry. <see cref="Convert"/> adds a VirtualGeometryRenderer to every renderer that qualifies and
    /// disables the MeshRenderer; a LODGroup whose LOD0 is converted is disabled together with its coarser
    /// LODs (the cluster DAG does the LOD). <see cref="Revert"/> restores exactly what was changed.
    ///
    /// The same rules (<see cref="Collect"/>) decide in the editor which meshes a table needs.
    /// </summary>
    public sealed class VirtualGeometryRuntimeConversion
    {
        /// <summary>Which renderers are converted.</summary>
        [Serializable]
        public struct Options
        {
            [Tooltip("Only GameObjects marked static (players: VgStaticMarker from the scene build step): moving objects keep their renderers and their per-object motion vectors.")]
            public bool onlyStatic;
            [Tooltip("Renderers with a transparent material keep their renderer (virtual geometry sorts transparent instances only up to a budget).")]
            public bool skipTransparent;
            [Tooltip("Renderers with an alpha-tested material keep their renderer (and their LODGroup).")]
            public bool skipAlphaTested;
            [Tooltip("Rendering layer mask a converted renderer must have exactly (0 = any). Virtual geometry draws with one mask (VirtualGeometrySettings.renderingLayerMask), so renderers relying on another one stay regular.")]
            public uint renderingLayerMask;
            [Tooltip("Layers whose renderers may be converted (virtual geometry draws on the Default layer: cameras and passes that filter by layer see it there).")]
            public LayerMask layers;
            [Tooltip("Meshes with fewer triangles stay regular renderers (0 = any).")]
            public int minTriangles;
            [Tooltip("Converted renderers keep casting shadows themselves (shadows-only MeshRenderers) and the virtual geometry casts none: the camera gets virtual geometry, shadow maps the pipeline's own cheap path. Pays off where shadow views are many and small (URP: cascades plus the six faces of every shadowed point light, each a full culling pass for virtual geometry).")]
            public bool shadowsFromRenderers;

            public static Options Default => new Options
            {
                onlyStatic = true,
                skipTransparent = true,
                skipAlphaTested = true,
                renderingLayerMask = 0,
                layers = ~0,
                minTriangles = 0,
            };
        }

        /// <summary>Extra per-renderer rule of the project (null = none): false keeps the renderer regular.</summary>
        public static Func<MeshRenderer, bool> Filter;

        readonly List<(MeshRenderer renderer, VirtualGeometryRenderer vg, ShadowCastingMode shadows)> m_Converted = new List<(MeshRenderer, VirtualGeometryRenderer, ShadowCastingMode)>();
        readonly List<Renderer> m_DisabledLodRenderers = new List<Renderer>();
        readonly List<LODGroup> m_DisabledGroups = new List<LODGroup>();

        /// <summary>Renderers drawn as virtual geometry by this conversion.</summary>
        public int ConvertedCount => m_Converted.Count;

        /// <summary>LODGroups replaced by the cluster LOD of their converted LOD0.</summary>
        public int ConvertedGroupCount => m_DisabledGroups.Count;

        /// <summary>Source triangles of the converted renderers.</summary>
        public long ConvertedTriangles { get; private set; }

        /// <summary>Why renderers were left regular: reason -> count (diagnostics).</summary>
        public readonly Dictionary<string, int> Skipped = new Dictionary<string, int>();

        VirtualGeometryRuntimeConversion() { }

        /// <summary>
        /// A renderer can be drawn as virtual geometry under `options` (its mesh still has to be in the table):
        /// active and enabled, a MeshFilter with a mesh, materials of the rules with a DOTS instancing variant,
        /// no property block, no baked lightmap outside HDRP, not shadows-only. `reason` says why not.
        /// </summary>
        public static bool Qualifies(MeshRenderer renderer, in Options options, out string reason)
        {
            reason = null;
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                return Fail(out reason, "inactive");
            if (renderer.GetComponent<VirtualGeometryRenderer>() != null)
                return Fail(out reason, "already virtual geometry");
            var filter = renderer.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
                return Fail(out reason, "no mesh");
            if (options.onlyStatic && !VgStaticMarker.IsStatic(renderer.gameObject))
                return Fail(out reason, "not static");
            if ((options.layers.value & (1 << renderer.gameObject.layer)) == 0)
                return Fail(out reason, "layer");
            if (options.renderingLayerMask != 0 && renderer.renderingLayerMask != options.renderingLayerMask)
                return Fail(out reason, "rendering layer mask");
            if (renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly)
                return Fail(out reason, "shadows only");
            if (renderer.HasPropertyBlock())
                return Fail(out reason, "material property block");
            if (renderer.lightmapIndex >= 0 && renderer.lightmapIndex < 0xFFFE && !IsHdrp)
                return Fail(out reason, "baked lightmap (HDRP only)");
            var materials = renderer.sharedMaterials;
            if (materials.Length == 0)
                return Fail(out reason, "no material");
            foreach (var m in materials)
            {
                if (m == null || m.shader == null)
                    return Fail(out reason, "missing material");
                if (options.skipTransparent && m.renderQueue > (int)RenderQueue.GeometryLast)
                    return Fail(out reason, "transparent material");
                if (options.skipAlphaTested && (m.IsKeywordEnabled("_ALPHATEST_ON") || (m.renderQueue >= (int)RenderQueue.AlphaTest && m.renderQueue <= (int)RenderQueue.GeometryLast)))
                    return Fail(out reason, "alpha-tested material");
                if (!m.shader.keywordSpace.FindKeyword("DOTS_INSTANCING_ON").isValid)
                    return Fail(out reason, "shader without DOTS instancing (" + m.shader.name + ")");
            }
            if (options.minTriangles > 0 && TriangleCount(filter.sharedMesh) < options.minTriangles)
                return Fail(out reason, "too few triangles");
            if (Filter != null && !Filter(renderer))
                return Fail(out reason, "project filter");
            return true;
        }

        static bool IsHdrp => GraphicsSettings.currentRenderPipeline != null && GraphicsSettings.currentRenderPipeline.GetType().Name == "HDRenderPipelineAsset";

        static bool Fail(out string reason, string why)
        {
            reason = why;
            return false;
        }

        /// <summary>Triangles of a mesh (triangle topology submeshes).</summary>
        public static long TriangleCount(Mesh mesh)
        {
            long t = 0;
            for (int s = 0; s < mesh.subMeshCount; ++s)
                if (mesh.GetTopology(s) == MeshTopology.Triangles)
                    t += mesh.GetIndexCount(s) / 3;
            return t;
        }

        // qualifies and (with a table) its mesh was built
        static bool Convertible(Renderer r, VirtualGeometryMeshTable table, in Options options, out string reason)
        {
            if (!(r is MeshRenderer mr))
            {
                reason = "not a MeshRenderer";
                return false;
            }
            if (!Qualifies(mr, options, out reason))
                return false;
            if (table != null && !table.TryGet(mr.GetComponent<MeshFilter>().sharedMesh, out _))
            {
                reason = "mesh not in table";
                return false;
            }
            return true;
        }

        /// <summary>
        /// The renderers under `roots` that convert: renderers outside LODGroups that qualify, and the LOD0
        /// renderers of LODGroups whose LOD0 converts completely (`convertedGroups`); every other renderer of a
        /// LODGroup stays regular. `table` null: every renderer that qualifies (the editor builds the table from this).
        /// </summary>
        public static List<MeshRenderer> Collect(IEnumerable<Transform> roots, VirtualGeometryMeshTable table, in Options options,
                                                 List<LODGroup> convertedGroups = null, Dictionary<string, int> skipped = null)
        {
            var result = new List<MeshRenderer>();
            var seen = new HashSet<Renderer>();
            foreach (var root in roots)
            {
                if (root == null)
                    continue;
                foreach (var g in root.GetComponentsInChildren<LODGroup>(false))
                {
                    var lods = g.GetLODs();
                    var members = new List<Renderer>();
                    foreach (var lod in lods)
                        foreach (var r in lod.renderers)
                            if (r != null && seen.Add(r))
                                members.Add(r);
                    if (!g.enabled || lods.Length == 0)
                        continue;
                    string reason = null;
                    int count = 0;
                    foreach (var r in lods[0].renderers)
                    {
                        if (r == null)
                            continue;
                        if (!Convertible(r, table, options, out reason))
                            break;
                        count++;
                        reason = null;
                    }
                    if (reason != null || count == 0)
                    {
                        Count(skipped, "LODGroup kept: " + (reason ?? "empty LOD0"), members.Count);
                        continue;
                    }
                    foreach (var r in lods[0].renderers)
                        if (r is MeshRenderer mr && !result.Contains(mr))
                            result.Add(mr);
                    convertedGroups?.Add(g);
                }

                foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(false))
                {
                    if (!seen.Add(mr))
                        continue; // a LODGroup member (decided above) or under two roots
                    if (Convertible(mr, table, options, out var reason))
                        result.Add(mr);
                    else
                        Count(skipped, reason, 1);
                }
            }
            return result;
        }

        static void Count(Dictionary<string, int> skipped, string reason, int n)
        {
            if (skipped == null || reason == null)
                return;
            skipped.TryGetValue(reason, out int c);
            skipped[reason] = c + n;
        }

        /// <summary>
        /// Converts the qualifying renderers under `roots` whose meshes are in `table`. Returns null when
        /// virtual geometry cannot run here (the renderers stay as they are).
        /// </summary>
        public static VirtualGeometryRuntimeConversion Convert(IEnumerable<Transform> roots, VirtualGeometryMeshTable table, in Options options)
        {
            if (table == null || VgWorld.GetOrCreate() == null)
                return null;
            var conversion = new VirtualGeometryRuntimeConversion();
            var groups = new List<LODGroup>();
            var renderers = Collect(roots, table, options, groups, conversion.Skipped);
            var failed = new HashSet<MeshRenderer>();

            foreach (var mr in renderers)
            {
                var mesh = mr.GetComponent<MeshFilter>().sharedMesh;
                table.TryGet(mesh, out var vg);
                var vgr = mr.gameObject.AddComponent<VirtualGeometryRenderer>();
                vgr.hideFlags |= HideFlags.DontSave; // never saved into a scene (edit-mode conversions)
                var shadows = mr.shadowCastingMode;
                vgr.Configure(vg, mr.sharedMaterials, options.shadowsFromRenderers ? ShadowCastingMode.Off : shadows);
                if (!vgr.IsRegistered)
                {
                    // the world refused it: keep the regular renderer
                    UnityEngine.Object.DestroyImmediate(vgr);
                    mr.enabled = true;
                    failed.Add(mr);
                    Count(conversion.Skipped, "registration failed", 1);
                    continue;
                }
                if (options.shadowsFromRenderers && shadows != ShadowCastingMode.Off)
                {
                    mr.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                    mr.enabled = true; // VirtualGeometryRenderer disabled it on registration
                }
                else
                    mr.enabled = false;
                conversion.m_Converted.Add((mr, vgr, shadows));
                conversion.ConvertedTriangles += TriangleCount(mesh);
            }

            foreach (var g in groups)
            {
                var lods = g.GetLODs();
                bool complete = true;
                foreach (var r in lods[0].renderers)
                    if (r is MeshRenderer mr && failed.Contains(mr))
                        complete = false;
                if (!complete)
                    continue; // the group keeps choosing its LODs (its converted LOD0 renderers draw as VG meanwhile)
                var lod0 = new HashSet<Renderer>(lods[0].renderers);
                for (int l = 1; l < lods.Length; ++l)
                    foreach (var r in lods[l].renderers)
                        if (r != null && !lod0.Contains(r) && r.enabled)
                        {
                            r.enabled = false;
                            conversion.m_DisabledLodRenderers.Add(r);
                        }
                g.enabled = false;
                conversion.m_DisabledGroups.Add(g);
            }
            return conversion;
        }

        /// <summary>Back to the regular renderers (the virtual geometry components are removed).</summary>
        public void Revert()
        {
            foreach (var (renderer, vg, shadows) in m_Converted)
            {
                if (vg != null)
                {
                    vg.enabled = false; // unregisters now (Destroy waits for the end of the frame)
                    if (Application.isPlaying)
                        UnityEngine.Object.Destroy(vg);
                    else
                        UnityEngine.Object.DestroyImmediate(vg);
                }
                if (renderer != null)
                {
                    renderer.shadowCastingMode = shadows;
                    renderer.enabled = true;
                }
            }
            foreach (var r in m_DisabledLodRenderers)
                if (r != null)
                    r.enabled = true;
            foreach (var g in m_DisabledGroups)
                if (g != null)
                    g.enabled = true;
            m_Converted.Clear();
            m_DisabledLodRenderers.Clear();
            m_DisabledGroups.Clear();
            ConvertedTriangles = 0;
        }
    }
}
