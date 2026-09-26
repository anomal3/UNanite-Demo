using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UNanite
{
    /// <summary>
    /// M9: regular meshes extracted from a virtual geometry DAG - the finest uniform-error cut with at
    /// most a given number of triangles (ray tracing and GI proxies, physics colliders).
    /// </summary>
    public static class VgProxy
    {
        /// <summary>
        /// Threshold of the finest uniform-error cut of `r` with at most `maxTriangles` triangles
        /// (never coarser than the DAG's roots: when even they exceed the budget, the root cut).
        /// </summary>
        public static float CutThreshold(VgMeshReader r, int maxTriangles)
        {
            var candidates = new List<float> { 0f };
            foreach (var g in r.Groups)
                if (g.error < float.MaxValue)
                    candidates.Add(g.error);
            candidates.Sort();
            var scratch = new List<int>();
            int lo = 0, hi = candidates.Count - 1;
            if (CutTriangles(r, candidates[hi], scratch) > maxTriangles)
                return candidates[hi];
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (CutTriangles(r, candidates[mid], scratch) <= maxTriangles)
                    hi = mid;
                else
                    lo = mid + 1;
            }
            return candidates[hi];
        }

        static int CutTriangles(VgMeshReader r, float t, List<int> scratch)
        {
            VgLodCut.Uniform(r, t, scratch);
            int tris = 0;
            foreach (int c in scratch)
                tris += r.Clusters[c].TriangleCount;
            return tris;
        }

        /// <summary>
        /// Mesh of the finest uniform cut with at most `maxTriangles` triangles: positions, normals,
        /// tangents / uv1 / colours when the source has them, uv0, one sub-mesh per material slot
        /// (`materialCount`, empty slots included so renderers keep the source's material order);
        /// vertices shared by clusters are welded. `error` = the cut's object-space error bound.
        /// </summary>
        public static Mesh Extract(VirtualGeometryMesh vg, int maxTriangles, out float error)
        {
            var r = vg.Reader;
            error = CutThreshold(r, Mathf.Max(1, maxTriangles));
            var clusters = new List<int>();
            VgLodCut.Uniform(r, error, clusters);

            var h = r.Header;
            int slots = Mathf.Max(1, vg.MaterialCount);
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var tangents = h.HasTangents ? new List<Vector4>() : null;
            var uv0 = new List<Vector2>();
            var uv1 = h.HasUv1 ? new List<Vector2>() : null;
            var colors = h.HasColors ? new List<Color32>() : null;
            var indices = new List<int>[slots];
            for (int s = 0; s < slots; ++s)
                indices[s] = new List<int>();
            var weld = new Dictionary<VgMeshReader.DecodedVertex, int>(new VertexComparer());
            var verts = new List<VgMeshReader.DecodedVertex>();
            var tris = new List<int>();
            var map = new List<int>();
            bool uvFromXZ = vg.UvFromPositionXZ != Vector2.zero;

            foreach (int c in clusters)
            {
                r.DecodeCluster(c, verts, tris);
                map.Clear();
                foreach (var v0 in verts)
                {
                    var v = v0;
                    if (uvFromXZ)
                        v.uv0 = new Vector2(v.position.x * vg.UvFromPositionXZ.x, v.position.z * vg.UvFromPositionXZ.y);
                    if (!weld.TryGetValue(v, out int index))
                    {
                        index = positions.Count;
                        weld.Add(v, index);
                        positions.Add(v.position);
                        normals.Add(v.normal);
                        tangents?.Add(v.tangent);
                        uv0.Add(v.uv0);
                        uv1?.Add(v.uv1);
                        colors?.Add(v.color);
                    }
                    map.Add(index);
                }
                var list = indices[Mathf.Min(r.Clusters[c].Material, slots - 1)];
                foreach (int t in tris)
                    list.Add(map[t]);
            }

            var mesh = new Mesh { name = $"{vg.name} (proxy {maxTriangles})" };
            mesh.indexFormat = positions.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            if (tangents != null)
                mesh.SetTangents(tangents);
            mesh.SetUVs(0, uv0);
            if (uv1 != null)
                mesh.SetUVs(1, uv1);
            if (colors != null)
                mesh.SetColors(colors);
            mesh.subMeshCount = slots;
            for (int s = 0; s < slots; ++s)
                mesh.SetTriangles(indices[s], s, false);
            mesh.bounds = vg.LocalBounds;
            return mesh;
        }

        sealed class VertexComparer : IEqualityComparer<VgMeshReader.DecodedVertex>
        {
            public bool Equals(VgMeshReader.DecodedVertex a, VgMeshReader.DecodedVertex b) =>
                a.position.Equals(b.position) && a.normal.Equals(b.normal) && a.tangent.Equals(b.tangent) && a.uv0.Equals(b.uv0) && a.uv1.Equals(b.uv1) &&
                a.color.r == b.color.r && a.color.g == b.color.g && a.color.b == b.color.b && a.color.a == b.color.a;

            public int GetHashCode(VgMeshReader.DecodedVertex v) =>
                HashCode.Combine(v.position, v.normal, v.uv0);
        }
    }
}
