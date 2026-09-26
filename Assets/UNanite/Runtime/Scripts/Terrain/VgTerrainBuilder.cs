using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace UNanite
{
    /// <summary>Heightfield input of the terrain builder (a snapshot of a TerrainData).</summary>
    public sealed class VgTerrainSource
    {
        public int resolution;   // samples per side (quads + 1)
        public float[] heights;  // normalised [0, 1], row-major: z * resolution + x
        public bool[] holes;     // per quad, true = hole; null = no holes
        public Vector3 size;     // terrain size (world units)

        public int Quads => resolution - 1;
        public float SpacingX => size.x / Quads;
        public float SpacingZ => size.z / Quads;
        public float Height(int x, int z) => heights[z * resolution + x];
        public bool IsHole(int qx, int qz) => holes != null && qx >= 0 && qz >= 0 && qx < Quads && qz < Quads && holes[qz * Quads + qx];

        public static VgTerrainSource FromTerrainData(TerrainData data)
        {
            int res = data.heightmapResolution;
            var src = new VgTerrainSource { resolution = res, size = data.size, heights = new float[res * res] };
            var h = data.GetHeights(0, 0, res, res);
            for (int z = 0; z < res; ++z)
                for (int x = 0; x < res; ++x)
                    src.heights[z * res + x] = h[z, x];
            if (data.enableHolesTextureCompression || data.holesResolution > 0)
            {
                int q = res - 1;
                var solid = data.GetHoles(0, 0, q, q); // true = surface, false = hole
                bool any = false;
                var holes = new bool[q * q];
                for (int z = 0; z < q; ++z)
                    for (int x = 0; x < q; ++x)
                        any |= holes[z * q + x] = !solid[z, x];
                src.holes = any ? holes : null;
            }
            return src;
        }

        /// <summary>World-space position of sample (x, z) in terrain-local space (the formula every tile uses).</summary>
        public Vector3 Position(int x, int z) => new Vector3((float)(x * (double)size.x / Quads), Height(x, z) * size.y, (float)(z * (double)size.z / Quads));

        /// <summary>Central-difference normal of sample (x, z), one-sided at the terrain edge.</summary>
        public Vector3 Normal(int x, int z)
        {
            int x0 = Math.Max(x - 1, 0), x1 = Math.Min(x + 1, Quads);
            int z0 = Math.Max(z - 1, 0), z1 = Math.Min(z + 1, Quads);
            float dx = (Height(x1, z) - Height(x0, z)) * size.y / ((x1 - x0) * SpacingX);
            float dz = (Height(x, z1) - Height(x, z0)) * size.y / ((z1 - z0) * SpacingZ);
            return new Vector3(-dx, 1f, -dz).normalized;
        }

        /// <summary>True when a quad touching sample (x, z) is a hole.</summary>
        public bool TouchesHole(int x, int z) =>
            holes != null && (IsHole(x - 1, z - 1) || IsHole(x, z - 1) || IsHole(x - 1, z) || IsHole(x, z));
    }

    /// <summary>
    /// Builds a terrain as an HLOD quadtree of virtual geometry meshes (M8):
    ///   * leaves: tiles of `tileQuads`² heightmap quads, built from the grid (holes removed);
    ///   * internal nodes: the union of their children's root clusters (terminal groups, the
    ///     always-resident complete coarse version of each child), simplified further;
    ///   * every node locks its outer border at full resolution, and all nodes share one local space
    ///     and quantisation grid, so neighbours at any level and LOD cut share identical vertices;
    ///   * each node's switch record dominates the refined-group bounds of its children's root
    ///     clusters: when it is coarse enough, the children would be showing exactly their roots, i.e.
    ///     the node's own level 0 (no popping). Records are monotonic up the tree.
    /// Nodes are ordered leaves first, level by level, root last.
    /// </summary>
    public static class VgTerrainBuilder
    {
        /// <summary>Quantisation of every node: about twice the 16-bit height resolution, and positions exact in float.</summary>
        public static int ChoosePrecision(Vector3 size)
        {
            float maxAbs = Mathf.Max(size.x, Mathf.Max(size.y, size.z), 1e-3f);
            int heightPrecision = Mathf.CeilToInt(Mathf.Log(65536f / Mathf.Max(size.y, 1e-3f), 2f)) + 1;
            int floatExact = Mathf.FloorToInt(Mathf.Log((1 << 24) / maxAbs, 2f));
            return Mathf.Clamp(Mathf.Min(heightPrecision, floatExact), -8, 16);
        }

        sealed class LayoutNode
        {
            public int level;
            public RectInt rect;
            public readonly List<LayoutNode> children = new List<LayoutNode>();
            public int id;
        }

        /// <summary>
        /// The quadtree skeleton (no meshes, no switch records): leaves of `tileQuads`² quads, parents
        /// over 2 × 2 children up to one root. Ordered level by level from the leaves, children of a
        /// node contiguous, root last.
        /// </summary>
        public static VgTerrainNode[] Layout(int quads, int tileQuads)
        {
            tileQuads = Mathf.Max(8, tileQuads);
            int n = (quads + tileQuads - 1) / tileQuads;
            var grid = new LayoutNode[n, n];
            for (int tz = 0; tz < n; ++tz)
                for (int tx = 0; tx < n; ++tx)
                {
                    int x0 = tx * tileQuads, z0 = tz * tileQuads;
                    grid[tx, tz] = new LayoutNode { rect = new RectInt(x0, z0, Math.Min(tileQuads, quads - x0), Math.Min(tileQuads, quads - z0)) };
                }

            int level = 0;
            while (n > 1)
            {
                level++;
                int m = (n + 1) / 2;
                var next = new LayoutNode[m, m];
                for (int tz = 0; tz < m; ++tz)
                    for (int tx = 0; tx < m; ++tx)
                    {
                        var parent = new LayoutNode { level = level };
                        int xMin = int.MaxValue, zMin = int.MaxValue, xMax = int.MinValue, zMax = int.MinValue;
                        for (int dz = 0; dz < 2; ++dz)
                            for (int dx = 0; dx < 2; ++dx)
                            {
                                int cx = tx * 2 + dx, cz = tz * 2 + dz;
                                if (cx >= n || cz >= n)
                                    continue;
                                var c = grid[cx, cz];
                                parent.children.Add(c);
                                xMin = Math.Min(xMin, c.rect.xMin); zMin = Math.Min(zMin, c.rect.yMin);
                                xMax = Math.Max(xMax, c.rect.xMax); zMax = Math.Max(zMax, c.rect.yMax);
                            }
                        parent.rect = new RectInt(xMin, zMin, xMax - xMin, zMax - zMin);
                        next[tx, tz] = parent;
                    }
                grid = next;
                n = m;
            }

            // level lists top-down, so the children of every node are contiguous in their level
            var levels = new List<LayoutNode>[level + 1];
            for (int l = 0; l <= level; ++l)
                levels[l] = new List<LayoutNode>();
            levels[level].Add(grid[0, 0]);
            for (int l = level; l >= 1; --l)
                foreach (var p in levels[l])
                    levels[l - 1].AddRange(p.children);

            var ordered = new List<LayoutNode>();
            for (int l = 0; l <= level; ++l)
                ordered.AddRange(levels[l]);
            for (int i = 0; i < ordered.Count; ++i)
                ordered[i].id = i;

            var result = new VgTerrainNode[ordered.Count];
            for (int i = 0; i < ordered.Count; ++i)
            {
                var o = ordered[i];
                result[i] = new VgTerrainNode
                {
                    level = o.level,
                    quads = o.rect,
                    parent = -1,
                    firstChild = o.children.Count > 0 ? o.children[0].id : -1,
                    childCount = o.children.Count,
                };
            }
            for (int i = 0; i < ordered.Count; ++i)
                foreach (var c in ordered[i].children)
                    result[c.id].parent = i;
            return result;
        }

        /// <summary>
        /// Quantisation of x and z: the coarsest power of two that represents every heightmap sample
        /// position exactly (1 m spacing: 2^0), or `precision` when no such grid exists.
        /// </summary>
        public static int ChoosePrecisionXZ(Vector3 size, int resolution, int precision)
        {
            double sx = (double)size.x / (resolution - 1), sz = (double)size.z / (resolution - 1);
            for (int p = -8; p <= precision; ++p)
            {
                double fx = sx * Math.Pow(2.0, p), fz = sz * Math.Pow(2.0, p);
                if (Math.Abs(fx - Math.Round(fx)) < 1e-9 && Math.Abs(fz - Math.Round(fz)) < 1e-9)
                    return p;
            }
            return precision;
        }

        public static UnvgBuildSettings NativeSettings(VgTerrainBuildSettings s, int precision, int precisionXZ)
        {
            var n = VgNativeBuilder.DefaultSettings();
            n.groupSize = (uint)Mathf.Clamp(s.groupSize, 4, 24);
            n.threadCount = (uint)Mathf.Max(0, s.threadsPerBuild);
            n.positionPrecision = precision;
            n.positionPrecisionXZ = precisionXZ;
            n.normalBits = (uint)Mathf.Clamp(s.normalBits, 6, 15);
            n.normalWeight = s.normalWeight;
            n.uvWeight = 0f;
            n.colorWeight = 0f;
            n.flags = UnvgBuildSettings.FlagPermissive | UnvgBuildSettings.FlagSloppyFallback;
            return n;
        }

        static bool OnBorder(RectInt r, int x, int z) => x == r.xMin || x == r.xMax || z == r.yMin || z == r.yMax;

        /// <summary>Grid mesh of a leaf: Unity's quad diagonal, hole quads removed, border (and hole outlines) locked.</summary>
        public static VgNativeBuilder.MeshStreams LeafStreams(VgTerrainSource src, RectInt rect, VgTerrainBuildSettings settings)
        {
            int w = rect.width + 1, h = rect.height + 1;
            var s = new VgNativeBuilder.MeshStreams
            {
                positions = new float[w * h * 3],
                normals = new float[w * h * 3],
                vertexLock = new byte[w * h],
            };
            for (int j = 0; j < h; ++j)
                for (int i = 0; i < w; ++i)
                {
                    int x = rect.xMin + i, z = rect.yMin + j, v = j * w + i;
                    var p = src.Position(x, z);
                    var n = src.Normal(x, z);
                    s.positions[v * 3] = p.x; s.positions[v * 3 + 1] = p.y; s.positions[v * 3 + 2] = p.z;
                    s.normals[v * 3] = n.x; s.normals[v * 3 + 1] = n.y; s.normals[v * 3 + 2] = n.z;
                    if (OnBorder(rect, x, z) || (settings.lockHoleBorders && src.TouchesHole(x, z)))
                        s.vertexLock[v] = 1;
                }

            var indices = new List<uint>(rect.width * rect.height * 6);
            for (int j = 0; j < rect.height; ++j)
                for (int i = 0; i < rect.width; ++i)
                {
                    if (src.IsHole(rect.xMin + i, rect.yMin + j))
                        continue;
                    uint a = (uint)(j * w + i), b = (uint)((j + 1) * w + i), c = (uint)((j + 1) * w + i + 1), d = (uint)(j * w + i + 1);
                    // front faces point up (+Y): (a, b, c) and (a, c, d)
                    indices.Add(a); indices.Add(b); indices.Add(c);
                    indices.Add(a); indices.Add(c); indices.Add(d);
                }
            s.indices = indices.ToArray();
            return s;
        }

        /// <summary>
        /// Clusters of the uniform-error cut of a DAG at threshold `t`:
        /// render(C) = error(group(C)) &gt; t and (C is source or error(refined(C)) &lt;= t).
        /// </summary>
        public static void Cut(VgMeshReader r, float t, List<int> clusters) => VgLodCut.Uniform(r, t, clusters);

        static int CutTriangles(IList<VgMeshReader> children, float t, List<int> scratch)
        {
            int tris = 0;
            foreach (var r in children)
            {
                Cut(r, t, scratch);
                foreach (int c in scratch)
                    tris += r.Clusters[c].TriangleCount;
            }
            return tris;
        }

        /// <summary>
        /// Source of an internal node: the children's uniform-error cut with about 1/hlodReduction of
        /// their source triangles (never finer), welded by quantised position. `sourceError` = the
        /// largest error of the cut (floor of every LOD error of the node, and its switch error);
        /// `switchSphere` bounds the node and the children's own records, so records are monotonic
        /// up the tree.
        /// </summary>
        public static VgNativeBuilder.MeshStreams InternalStreams(VgTerrainSource src, RectInt rect, IList<VgMeshReader> children, IList<VgTerrainNode> childNodes,
            VgTerrainBuildSettings settings, int precision, out Vector4 switchSphere, out float sourceError)
        {
            int precisionXZ = ChoosePrecisionXZ(src.size, src.resolution, precision);
            double step = Math.Pow(2.0, -precision), stepXZ = Math.Pow(2.0, -precisionXZ);
            var scratch = new List<int>();

            // threshold: the finest uniform cut with at most `target` triangles
            var candidates = new List<float> { 0f };
            long sourceTris = 0;
            for (int k = 0; k < children.Count; ++k)
            {
                if (children[k].Header.positionPrecision != precision || children[k].Header.PrecisionXZ != precisionXZ)
                    throw new InvalidOperationException("terrain tiles use different quantisation grids");
                sourceTris += children[k].Header.sourceTriangles;
                foreach (var g in children[k].Groups)
                    if (g.error < float.MaxValue)
                        candidates.Add(g.error);
            }
            candidates.Sort();
            long target = Math.Max(1, (long)(sourceTris / Math.Max(1f, settings.hlodReduction)));
            int lo = 0, hi = candidates.Count - 1;
            if (CutTriangles(children, candidates[hi], scratch) <= target)
            {
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (CutTriangles(children, candidates[mid], scratch) <= target)
                        hi = mid;
                    else
                        lo = mid + 1;
                }
            }
            float t = candidates[hi]; // border-limited children: the coarsest cut (their roots)

            var map = new Dictionary<(int, int, int), int>();
            var positions = new List<float>();
            var normals = new List<float>();
            var locks = new List<byte>();
            var indices = new List<uint>();
            var qpos = new List<Vector3Int>();
            var tris = new List<int>();
            var seen = new Dictionary<(uint, uint, uint), int>();
            var removed = new HashSet<int>();
            switchSphere = new Vector4(0, 0, 0, -1);
            sourceError = 0f;

            for (int k = 0; k < children.Count; ++k)
            {
                var r = children[k];
                float childFloor = childNodes[k].switchError;
                sourceError = Mathf.Max(sourceError, childFloor);
                if (childNodes[k].level > 0)
                    switchSphere = MergeSpheres(switchSphere, childNodes[k].switchSphere);
                Cut(r, t, scratch);
                foreach (int c in scratch)
                {
                    uint refined = r.Clusters[c].refinedGroup;
                    sourceError = Mathf.Max(sourceError, refined == VgFormat.Invalid ? childFloor : r.Groups[refined].error);
                    r.DecodeClusterQuantized(c, qpos, tris);
                    var local = new int[qpos.Count];
                    for (int v = 0; v < qpos.Count; ++v)
                    {
                        var q = qpos[v];
                        if (!map.TryGetValue((q.x, q.y, q.z), out int id))
                        {
                            id = positions.Count / 3;
                            map.Add((q.x, q.y, q.z), id);
                            positions.Add((float)(q.x * stepXZ));
                            positions.Add((float)(q.y * step));
                            positions.Add((float)(q.z * stepXZ));
                            int sx = Mathf.Clamp((int)Math.Round(q.x * stepXZ / src.SpacingX), 0, src.Quads);
                            int sz = Mathf.Clamp((int)Math.Round(q.z * stepXZ / src.SpacingZ), 0, src.Quads);
                            var n = src.Normal(sx, sz);
                            normals.Add(n.x); normals.Add(n.y); normals.Add(n.z);
                            locks.Add((byte)(OnBorder(rect, sx, sz) || (settings.lockHoleBorders && src.TouchesHole(sx, sz)) ? 1 : 0));
                        }
                        local[v] = id;
                    }
                    for (int k2 = 0; k2 < tris.Count; k2 += 3)
                    {
                        uint i0 = (uint)local[tris[k2]], i1 = (uint)local[tris[k2 + 1]], i2 = (uint)local[tris[k2 + 2]];
                        // identical triangles from two children are the vertical "fins" both built on
                        // their shared (locked) border; the pair only closes the gap between the border
                        // polyline and each child's chord, so inside the parent both copies go
                        var key = SortedTriangle(i0, i1, i2);
                        if (seen.TryGetValue(key, out int at))
                        {
                            removed.Add(at);
                            continue;
                        }
                        seen.Add(key, indices.Count);
                        indices.Add(i0); indices.Add(i1); indices.Add(i2);
                    }
                }
            }
            if (removed.Count > 0)
            {
                var kept = new List<uint>(indices.Count);
                for (int k2 = 0; k2 < indices.Count; k2 += 3)
                    if (!removed.Contains(k2))
                    {
                        kept.Add(indices[k2]); kept.Add(indices[k2 + 1]); kept.Add(indices[k2 + 2]);
                    }
                indices = kept;
            }

            var b = BoundsOf(positions);
            switchSphere = MergeSpheres(switchSphere, new Vector4(b.center.x, b.center.y, b.center.z, b.extents.magnitude));
            return new VgNativeBuilder.MeshStreams
            {
                positions = positions.ToArray(),
                normals = normals.ToArray(),
                vertexLock = locks.ToArray(),
                indices = indices.ToArray(),
            };
        }

        public static (uint, uint, uint) SortedTriangle(uint a, uint b, uint c)
        {
            if (a > b) (a, b) = (b, a);
            if (b > c) (b, c) = (c, b);
            if (a > b) (a, b) = (b, a);
            return (a, b, c);
        }

        static Bounds BoundsOf(List<float> p)
        {
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = -min;
            for (int i = 0; i < p.Count; i += 3)
            {
                var v = new Vector3(p[i], p[i + 1], p[i + 2]);
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
            var b = new Bounds();
            b.SetMinMax(min, max);
            return b;
        }

        /// <summary>Smallest sphere containing both (w &lt; 0 = empty), slightly inflated for float safety.</summary>
        public static Vector4 MergeSpheres(Vector4 a, Vector4 b)
        {
            if (a.w < 0) return b;
            if (b.w < 0) return a;
            var ca = (Vector3)a;
            var cb = (Vector3)b;
            float d = Vector3.Distance(ca, cb);
            if (d + b.w <= a.w) return a;
            if (d + a.w <= b.w) return b;
            float r = (d + a.w + b.w) * 0.5f;
            var c = ca + (cb - ca) * ((r - a.w) / Mathf.Max(d, 1e-20f));
            return new Vector4(c.x, c.y, c.z, r * (1f + 1e-6f) + 1e-6f);
        }

        public sealed class NodeBuild
        {
            public byte[] blob;
            public UnvgBuildStats stats;
            public Vector4 switchSphere;
            public float switchError;
            public string error;
        }

        /// <summary>Builds one node; for internal nodes `children` are the readers of its children (resident prefixes suffice).</summary>
        public static NodeBuild BuildNode(VgTerrainSource src, VgTerrainNode[] nodes, int index, IList<VgMeshReader> children, VgTerrainBuildSettings settings, int precision)
        {
            var node = nodes[index];
            VgNativeBuilder.MeshStreams streams;
            var result = new NodeBuild();
            if (node.level == 0)
            {
                streams = LeafStreams(src, node.quads, settings);
                result.switchSphere = Vector4.zero;
                result.switchError = 0f;
            }
            else
            {
                var childNodes = new VgTerrainNode[node.childCount];
                Array.Copy(nodes, node.firstChild, childNodes, 0, node.childCount);
                streams = InternalStreams(src, node.quads, children, childNodes, settings, precision, out result.switchSphere, out result.switchError);
            }
            if (streams.indices.Length < 3)
            {
                result.error = "tile has no triangles (all holes)";
                return result;
            }
            var native = NativeSettings(settings, precision, ChoosePrecisionXZ(src.size, src.resolution, precision));
            native.sourceError = result.switchError; // internal nodes: error of the children's cut
            var built = VgNativeBuilder.Build(streams, native);
            if (!built.success)
            {
                result.error = built.error;
                return result;
            }
            result.blob = built.blob;
            result.stats = built.stats;
            return result;
        }

        /// <summary>
        /// Builds every node: leaves in parallel, then each level from its children. `onProgress`
        /// (0..1, called on the calling thread) may return false to cancel. Returns null on failure.
        /// </summary>
        public static NodeBuild[] BuildAll(VgTerrainSource src, VgTerrainNode[] nodes, VgTerrainBuildSettings settings, int precision, Func<float, bool> onProgress, out string error)
        {
            error = null;
            if (!VgNativeBuilder.EnsureLoaded())
            {
                error = VgNativeBuilder.LoadError;
                return null;
            }
            var results = new NodeBuild[nodes.Length];
            var readers = new VgMeshReader[nodes.Length];
            int threads = Math.Max(1, settings.threadsPerBuild);
            int concurrent = settings.concurrentBuilds > 0 ? settings.concurrentBuilds : Math.Max(1, Environment.ProcessorCount / threads);
            int done = 0;
            bool cancelled = false;
            string failure = null;

            int maxLevel = 0;
            foreach (var n in nodes)
                maxLevel = Math.Max(maxLevel, n.level);

            var work = Task.Run(() =>
            {
                for (int level = 0; level <= maxLevel && failure == null && !Volatile.Read(ref cancelled); ++level)
                {
                    var ids = new List<int>();
                    for (int i = 0; i < nodes.Length; ++i)
                        if (nodes[i].level == level)
                            ids.Add(i);
                    Parallel.ForEach(ids, new ParallelOptions { MaxDegreeOfParallelism = concurrent }, (i, state) =>
                    {
                        if (Volatile.Read(ref cancelled) || failure != null)
                        {
                            state.Stop();
                            return;
                        }
                        var node = nodes[i];
                        var children = new List<VgMeshReader>();
                        for (int c = 0; c < node.childCount; ++c)
                            children.Add(readers[node.firstChild + c]);
                        var r = BuildNode(src, nodes, i, children, settings, precision);
                        if (r.error != null)
                        {
                            failure = $"tile {i} (level {node.level}, quads {node.quads}): {r.error}";
                            state.Stop();
                            return;
                        }
                        results[i] = r;
                        readers[i] = new VgMeshReader(r.blob);
                        Interlocked.Increment(ref done);
                    });
                    // switch records of this level are read by the next one
                    foreach (int i in ids)
                        if (results[i] != null)
                        {
                            nodes[i].switchSphere = results[i].switchSphere;
                            nodes[i].switchError = results[i].switchError;
                        }
                }
            });

            while (!work.Wait(50))
            {
                if (onProgress != null && !onProgress((float)Volatile.Read(ref done) / nodes.Length))
                    Volatile.Write(ref cancelled, true);
            }
            if (work.IsFaulted)
            {
                error = work.Exception?.GetBaseException().Message;
                return null;
            }
            if (failure != null || cancelled)
            {
                error = failure ?? "cancelled";
                return null;
            }
            return results;
        }

        /// <summary>
        /// Builds a terrain in memory (tests, tools, runtime-generated terrains): every node's pages
        /// embedded in its mesh. Returns null and sets `error` on failure.
        /// </summary>
        public static VirtualGeometryTerrainData CreateData(VgTerrainSource src, VgTerrainBuildSettings settings, out string error)
        {
            int precision = ChoosePrecision(src.size);
            var nodes = Layout(src.Quads, settings.tileQuads);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var results = BuildAll(src, nodes, settings, precision, null, out error);
            if (results == null)
                return null;
            var stats = new VgTerrainBuildStats { nodes = nodes.Length };
            for (int i = 0; i < nodes.Length; ++i)
            {
                var mesh = CreateMesh(results[i], $"L{nodes[i].level}_{nodes[i].quads.x}_{nodes[i].quads.y}", src.size);
                mesh.hideFlags = HideFlags.DontSave;
                nodes[i].mesh = mesh;
                if (nodes[i].level == 0)
                {
                    stats.leaves++;
                    stats.sourceTriangles += results[i].stats.sourceTriangles;
                }
                stats.levels = Math.Max(stats.levels, nodes[i].level + 1);
                stats.storedTriangles += results[i].stats.totalTriangles;
                stats.blobBytes += (long)results[i].stats.blobBytes;
                stats.rootBytes += mesh.ResidentReader.Header.streamDataOffset - mesh.ResidentReader.Header.pageDataOffset;
            }
            stats.msTotal = watch.Elapsed.TotalMilliseconds;
            var data = ScriptableObject.CreateInstance<VirtualGeometryTerrainData>();
            data.hideFlags = HideFlags.DontSave;
            data.Initialize(nodes, src.resolution, src.size, precision, settings, "", stats);
            return data;
        }

        /// <summary>A node mesh from a build result (embedded pages; UVs from the position).</summary>
        public static VirtualGeometryMesh CreateMesh(NodeBuild build, string name, Vector3 terrainSize)
        {
            var mesh = ScriptableObject.CreateInstance<VirtualGeometryMesh>();
            mesh.name = name;
            var report = VgNativeBuilder.ToReport(build.stats);
            report.contentHash = Hash128.Compute(build.blob).ToString();
            mesh.Initialize(build.blob, name, 1, report);
            mesh.UvFromPositionXZ = new Vector2(1f / terrainSize.x, 1f / terrainSize.z);
            return mesh;
        }

        /// <summary>
        /// Nodes whose geometry depends on samples inside `samples` (inclusive sample rectangle):
        /// normals read one sample around, and border samples belong to both neighbours. Leaves
        /// first, then their ancestors (the order in which they must be rebuilt).
        /// </summary>
        public static List<int> AffectedNodes(VgTerrainNode[] nodes, RectInt samples)
        {
            var hit = new SortedSet<int>();
            int x0 = samples.xMin - 1, z0 = samples.yMin - 1, x1 = samples.xMax + 1, z1 = samples.yMax + 1;
            for (int i = 0; i < nodes.Length; ++i)
            {
                if (nodes[i].level != 0)
                    continue;
                var q = nodes[i].quads; // samples [xMin, xMax] inclusive
                if (q.xMax < x0 || q.xMin > x1 || q.yMax < z0 || q.yMin > z1)
                    continue;
                for (int n = i; n >= 0; n = nodes[n].parent)
                    hit.Add(n);
            }
            return new List<int>(hit); // ascending = leaves first, parents after children
        }
    }
}
