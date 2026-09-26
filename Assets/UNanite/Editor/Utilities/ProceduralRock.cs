using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UNanite.Editor
{
    /// <summary>
    /// Deterministic procedural "photogrammetry-like" rocks (displaced icospheres with fbm noise,
    /// a real UV seam and two material slots) used by tests, benchmarks and sample scenes.
    /// Subdivision n gives 20 * 4^n triangles (7 = 327K, 8 = 1.3M, 9 = 5.2M).
    /// </summary>
    public static class ProceduralRock
    {
        public static Mesh Create(int subdivisions, int seed = 0, bool twoMaterials = true)
        {
            float t = (1f + Mathf.Sqrt(5f)) / 2f;
            var v = new List<Vector3>
            {
                new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0),
                new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t),
                new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
            };
            var f = new List<int>
            {
                0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
            };

            for (int s = 0; s < subdivisions; ++s)
            {
                var mid = new Dictionary<long, int>(f.Count, EdgeKeyComparer.Instance);
                int Midpoint(int a, int b)
                {
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    if (mid.TryGetValue(key, out int id))
                        return id;
                    id = v.Count;
                    v.Add((v[a] + v[b]) * 0.5f);
                    mid.Add(key, id);
                    return id;
                }
                var nf = new List<int>(f.Count * 4);
                for (int i = 0; i < f.Count; i += 3)
                {
                    int a = f[i], b = f[i + 1], c = f[i + 2];
                    int ab = Midpoint(a, b), bc = Midpoint(b, c), ca = Midpoint(c, a);
                    nf.Add(a); nf.Add(ab); nf.Add(ca);
                    nf.Add(b); nf.Add(bc); nf.Add(ab);
                    nf.Add(c); nf.Add(ca); nf.Add(bc);
                    nf.Add(ab); nf.Add(bc); nf.Add(ca);
                }
                f = nf;
            }

            var offset = new Vector3(seed * 17.3f, seed * -5.1f, seed * 9.7f);
            var squash = new Vector3(1f, 0.8f + 0.1f * Mathf.Sin(seed), 1f);
            int vc = v.Count;
            var pos = new Vector3[vc];
            var sphereUv = new Vector2[vc];
            for (int i = 0; i < vc; ++i)
            {
                var n = v[i].normalized;
                float r = 1f + 0.35f * Fbm(n * 1.7f + offset);
                pos[i] = Vector3.Scale(n * r, squash);
                sphereUv[i] = new Vector2(0.5f + Mathf.Atan2(n.z, n.x) / (2f * Mathf.PI), 0.5f + Mathf.Asin(Mathf.Clamp(n.y, -1f, 1f)) / Mathf.PI);
            }

            var nrm = new Vector3[vc];
            for (int i = 0; i < f.Count; i += 3)
            {
                var fn = Vector3.Cross(pos[f[i + 1]] - pos[f[i]], pos[f[i + 2]] - pos[f[i]]);
                nrm[f[i]] += fn;
                nrm[f[i + 1]] += fn;
                nrm[f[i + 2]] += fn;
            }

            // duplicate vertices across the u = 0/1 seam
            var positions = new List<Vector3>(pos);
            var normals = new List<Vector3>(nrm);
            var uvs = new List<Vector2>(sphereUv);
            var wrapped = new Dictionary<int, int>();
            var sub0 = new List<int>();
            var sub1 = new List<int>();
            for (int i = 0; i < f.Count; i += 3)
            {
                int[] tri = { f[i], f[i + 1], f[i + 2] };
                float umin = Mathf.Min(sphereUv[tri[0]].x, Mathf.Min(sphereUv[tri[1]].x, sphereUv[tri[2]].x));
                float umax = Mathf.Max(sphereUv[tri[0]].x, Mathf.Max(sphereUv[tri[1]].x, sphereUv[tri[2]].x));
                if (umax - umin > 0.5f)
                {
                    for (int k = 0; k < 3; ++k)
                    {
                        if (sphereUv[tri[k]].x >= 0.5f)
                            continue;
                        if (!wrapped.TryGetValue(tri[k], out int w))
                        {
                            w = positions.Count;
                            positions.Add(pos[tri[k]]);
                            normals.Add(nrm[tri[k]]);
                            uvs.Add(sphereUv[tri[k]] + Vector2.right);
                            wrapped.Add(tri[k], w);
                        }
                        tri[k] = w;
                    }
                }
                float cy = pos[f[i]].y + pos[f[i + 1]].y + pos[f[i + 2]].y;
                (twoMaterials && cy > 0f ? sub1 : sub0).AddRange(tri);
            }

            var mesh = new Mesh { name = $"Rock_s{subdivisions}_{seed}", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(positions);
            for (int i = 0; i < normals.Count; ++i)
                normals[i] = normals[i].normalized;
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            if (twoMaterials && sub1.Count > 0)
            {
                mesh.subMeshCount = 2;
                mesh.SetTriangles(sub0, 0, false);
                mesh.SetTriangles(sub1, 1, false);
            }
            else
            {
                sub0.AddRange(sub1);
                mesh.SetTriangles(sub0, 0, false);
            }
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        /// <summary>long.GetHashCode() is lo ^ hi, which collides for (a &lt;&lt; 32 | b) edge keys.</summary>
        sealed class EdgeKeyComparer : IEqualityComparer<long>
        {
            public static readonly EdgeKeyComparer Instance = new EdgeKeyComparer();
            public bool Equals(long a, long b) => a == b;
            public int GetHashCode(long k)
            {
                unchecked
                {
                    ulong h = (ulong)k * 0x9E3779B97F4A7C15ul;
                    return (int)(h >> 32);
                }
            }
        }

        static float Hash(int x, int y, int z)
        {
            unchecked
            {
                uint h = (uint)x * 73856093u ^ (uint)y * 19349663u ^ (uint)z * 83492791u;
                h = (h ^ (h >> 13)) * 1274126177u;
                return (h & 0xFFFFFF) / (float)0xFFFFFF * 2f - 1f;
            }
        }

        static float ValueNoise(Vector3 p)
        {
            int ix = Mathf.FloorToInt(p.x), iy = Mathf.FloorToInt(p.y), iz = Mathf.FloorToInt(p.z);
            float fx = p.x - ix, fy = p.y - iy, fz = p.z - iz;
            float ux = fx * fx * (3 - 2 * fx), uy = fy * fy * (3 - 2 * fy), uz = fz * fz * (3 - 2 * fz);
            float r = 0;
            for (int k = 0; k < 8; ++k)
            {
                int dx = k & 1, dy = (k >> 1) & 1, dz = (k >> 2) & 1;
                float w = (dx != 0 ? ux : 1 - ux) * (dy != 0 ? uy : 1 - uy) * (dz != 0 ? uz : 1 - uz);
                r += w * Hash(ix + dx, iy + dy, iz + dz);
            }
            return r;
        }

        static float Fbm(Vector3 p)
        {
            float a = 0.5f, freq = 1f, r = 0f;
            for (int o = 0; o < 7; ++o)
            {
                r += a * ValueNoise(p * freq);
                a *= 0.5f;
                freq *= 2.03f;
            }
            return r;
        }

        /// <summary>
        /// Saves a mesh into a binary container asset (a text-serialised multi-million triangle
        /// mesh would be hundreds of MB of YAML) and enables virtual geometry for it.
        /// </summary>
        public static string SaveWithVirtualGeometry(Mesh mesh, string folder)
        {
            Directory.CreateDirectory(folder);
            string path = $"{folder}/{mesh.name}.asset";
            var container = ScriptableObject.CreateInstance<BinaryMeshContainer>();
            container.name = mesh.name;
            AssetDatabase.CreateAsset(container, path);
            AssetDatabase.AddObjectToAsset(mesh, container);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path);
            VirtualGeometryImporter.CreateSidecar(path);
            return path;
        }

        [MenuItem("Tools/UNanite/Create Test Rocks (327K, 1.3M)")]
        static void CreateTestRocks()
        {
            foreach (var (subdiv, seed) in new[] { (7, 1), (8, 2) })
                SaveWithVirtualGeometry(Create(subdiv, seed), "Assets/UNaniteTest");
        }
    }

}
