using System.Collections.Generic;
using UnityEngine;

namespace UNanite
{
    /// <summary>
    /// View parameters for LOD selection. Error is measured in pixels of the view's vertical
    /// resolution; for shadow views the "pixels" are shadow-map texels.
    /// </summary>
    public struct VgLodView
    {
        public Vector3 position;     // view origin in world space
        public float projScale;      // projection[1][1] (= cot(fovY / 2)) for perspective views
        public float screenHeight;   // pixels
        public float nearPlane;
        public float thresholdPixels;
        public bool orthographic;
        public float orthoSize;      // half height in world units (orthographic only)

        public static VgLodView FromCamera(Camera camera, float thresholdPixels)
        {
            var v = new VgLodView
            {
                position = camera.transform.position,
                screenHeight = Mathf.Max(1, camera.pixelHeight),
                nearPlane = Mathf.Max(camera.nearClipPlane, 1e-4f),
                thresholdPixels = thresholdPixels,
                orthographic = camera.orthographic,
                orthoSize = camera.orthographicSize,
            };
            v.projScale = 1f / Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            return v;
        }
    }

    /// <summary>
    /// CPU reference implementation of the per-cluster LOD rule used on the GPU:
    ///   render(C) = !coarsen(C.group) &amp;&amp; (C is a source cluster || coarsen(C.refinedGroup))
    ///   coarsen(G) = projectedError(G) &lt;= threshold
    /// Group errors and spheres are monotonic along the DAG, so the choice is made independently per
    /// cluster yet always forms a consistent (crack-free) cut.
    /// </summary>
    public static class VgLodCut
    {
        /// <summary>Projected error of a group in pixels, or +inf for groups that can never be coarsened.</summary>
        public static float ProjectedErrorPixels(in VgGroup g, in Matrix4x4 localToWorld, float scale, in VgLodView view)
        {
            if (g.error >= float.MaxValue)
                return float.PositiveInfinity;

            float error = g.error * scale;
            if (view.orthographic)
                return error / (2f * view.orthoSize) * view.screenHeight;

            Vector3 center = localToWorld.MultiplyPoint3x4(new Vector3(g.centerX, g.centerY, g.centerZ));
            float distance = Mathf.Max(Vector3.Distance(center, view.position) - g.radius * scale, view.nearPlane);
            return error / distance * view.projScale * 0.5f * view.screenHeight;
        }

        /// <summary>M8 HLOD switch record (world sphere, error): coarse enough at this view (same rule as the GPU).</summary>
        public static bool CoarsenSwitch(Vector4 worldSphere, float error, in VgLodView view)
        {
            var g = new VgGroup { centerX = worldSphere.x, centerY = worldSphere.y, centerZ = worldSphere.z, radius = worldSphere.w, error = error };
            return ProjectedErrorPixels(g, Matrix4x4.identity, 1f, view) <= view.thresholdPixels;
        }

        /// <summary>
        /// M8: the terrain nodes a view draws (CullInstances' rule): node N is drawn iff its parent's
        /// record is not coarse enough and its own is (leaves always are). `offset` = terrain position.
        /// </summary>
        public static void SelectHlod(IReadOnlyList<VgTerrainNode> nodes, Vector3 offset, in VgLodView view, List<int> visible)
        {
            visible.Clear();
            var coarse = new bool[nodes.Count];
            for (int i = 0; i < nodes.Count; ++i)
            {
                var n = nodes[i];
                coarse[i] = n.level == 0 || CoarsenSwitch(n.switchSphere + new Vector4(offset.x, offset.y, offset.z, 0f), n.switchError, view);
            }
            for (int i = 0; i < nodes.Count; ++i)
            {
                int parent = nodes[i].parent;
                if ((parent < 0 || !coarse[parent]) && coarse[i])
                    visible.Add(i);
            }
        }

        /// <summary>
        /// Clusters of the uniform-error cut of a DAG at threshold `t` (M8 terrain HLOD, M9 proxies):
        /// render(C) = error(group(C)) &gt; t and (C is source or error(refined(C)) &lt;= t).
        /// </summary>
        public static void Uniform(VgMeshReader r, float t, List<int> clusters)
        {
            clusters.Clear();
            var cl = r.Clusters;
            var groups = r.Groups;
            for (int c = 0; c < cl.Length; ++c)
            {
                if (groups[cl[c].group].error <= t)
                    continue; // coarsened: its parents are used
                uint refined = cl[c].refinedGroup;
                if (refined != VgFormat.Invalid && groups[refined].error > t)
                    continue; // the finer clusters it was simplified from are used
                clusters.Add(c);
            }
        }

        public static float MaxScale(in Matrix4x4 m)
        {
            float sx = new Vector3(m.m00, m.m10, m.m20).magnitude;
            float sy = new Vector3(m.m01, m.m11, m.m21).magnitude;
            float sz = new Vector3(m.m02, m.m12, m.m22).magnitude;
            return Mathf.Max(sx, Mathf.Max(sy, sz));
        }

        /// <summary>Selects the clusters of the cut. `resident` (optional, per page) models streaming.</summary>
        public static void Select(VgMeshReader mesh, in Matrix4x4 localToWorld, in VgLodView view, List<int> clusters, bool[] residentPages = null)
        {
            clusters.Clear();
            float scale = MaxScale(localToWorld);
            var groups = mesh.Groups;

            var coarsen = new bool[groups.Length];
            var resident = new bool[groups.Length];
            for (int g = 0; g < groups.Length; ++g)
            {
                coarsen[g] = ProjectedErrorPixels(groups[g], localToWorld, scale, view) <= view.thresholdPixels;
                resident[g] = residentPages == null || residentPages[groups[g].pageIndex];
            }

            var headers = mesh.Clusters;
            for (int c = 0; c < headers.Length; ++c)
            {
                ref readonly var h = ref headers[c];
                int g = (int)h.group;
                if (!resident[g] || coarsen[g])
                    continue;
                if (h.IsSourceCluster || coarsen[h.refinedGroup] || !resident[h.refinedGroup])
                    clusters.Add(c);
            }
        }
    }
}
