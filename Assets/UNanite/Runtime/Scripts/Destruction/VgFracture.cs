using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UNanite
{
    /// <summary>
    /// M9: Voronoi fracture of a closed (watertight) mesh. Each piece is the source clipped by the
    /// planes of its Voronoi cell, one plane at a time; every cut is closed with a cap (the cut
    /// segments chained into loops and ear-clipped in the plane) in an extra "interior" sub-mesh with
    /// planar UVs. Topology is kept on welded positions: the point where a plane crosses an edge is
    /// computed once per edge from its endpoints in a canonical order, so both triangles of the edge,
    /// and the cap, get bit-identical vertices - every piece is closed exactly (tested: every edge of
    /// welded positions is used an even number of times).
    /// Sections with holes (a plane below the rim of a dent) are bridged into their outer loop.
    /// Limit: the source must be closed (watertight).
    /// </summary>
    public static class VgFracture
    {
        public struct Settings
        {
            public int pieces;          // Voronoi sites
            public int seed;
            public Vector3 focus;       // mesh space; sites are concentrated around it when `focusWeight` > 0
            public float focusWeight;   // 0 = uniform in the bounds, 1 = all sites near `focus`
            public float capUvScale;    // cap UVs = plane coordinates * this
        }

        public sealed class Piece
        {
            public Mesh mesh;          // source sub-meshes + the interior sub-mesh (index = source subMeshCount)
            public Vector3 center;     // centroid (mesh space)
            public float volume;
        }

        // corner of a triangle: a welded position + the attributes of this corner
        struct Corner
        {
            public int pos;
            public Vector3 normal;
            public Vector4 tangent;
            public Vector2 uv0;
        }

        struct Tri
        {
            public Corner a, b, c;
            public int subMesh;
        }

        sealed class Work
        {
            public readonly List<Vector3> positions = new List<Vector3>();
            public List<Tri> tris = new List<Tri>();
        }

        /// <summary>Fractures `source` (closed, mesh space). Returns the pieces (empty cells are skipped).</summary>
        public static List<Piece> Fracture(Mesh source, Settings settings, out string warning)
        {
            warning = null;
            var baseWork = FromMesh(source, out int subMeshes);
            var bounds = source.bounds;
            var sites = Sites(bounds, settings);
            var pieces = new List<Piece>();
            var warnings = new List<string>();
            for (int i = 0; i < sites.Count; ++i)
            {
                // clip by the nearest sites first: the piece shrinks fast, later planes touch fewer triangles
                var order = new List<int>();
                for (int j = 0; j < sites.Count; ++j)
                    if (j != i)
                        order.Add(j);
                order.Sort((x, y) => (sites[x] - sites[i]).sqrMagnitude.CompareTo((sites[y] - sites[i]).sqrMagnitude));

                var w = Clone(baseWork);
                bool ok = true;
                foreach (int j in order)
                {
                    Vector3 n = (sites[j] - sites[i]).normalized;
                    float d = Vector3.Dot(n, (sites[i] + sites[j]) * 0.5f);
                    if (!Clip(w, n, d, subMeshes, settings.capUvScale, out string err))
                    {
                        warnings.Add($"piece {i}: {err}");
                        ok = false;
                        break;
                    }
                    if (w.tris.Count == 0)
                        break;
                }
                if (!ok || w.tris.Count == 0)
                    continue;
                var piece = ToPiece(w, subMeshes + 1, source.name + "_piece" + pieces.Count);
                if (piece.volume > 1e-9f)
                    pieces.Add(piece);
            }
            if (warnings.Count > 0)
                warning = string.Join("; ", warnings);
            return pieces;
        }

        static List<Vector3> Sites(Bounds b, Settings s)
        {
            var rng = new System.Random(s.seed);
            var sites = new List<Vector3>();
            for (int i = 0; i < Mathf.Max(1, s.pieces); ++i)
            {
                var p = new Vector3(
                    Mathf.Lerp(b.min.x, b.max.x, (float)rng.NextDouble()),
                    Mathf.Lerp(b.min.y, b.max.y, (float)rng.NextDouble()),
                    Mathf.Lerp(b.min.z, b.max.z, (float)rng.NextDouble()));
                if (s.focusWeight > 0f)
                {
                    // pull towards the focus: pieces get smaller near the impact
                    float k = Mathf.Pow((float)rng.NextDouble(), 1f + 3f * s.focusWeight) * s.focusWeight;
                    p = Vector3.Lerp(p, s.focus, k);
                }
                sites.Add(p);
            }
            return sites;
        }

        static Work FromMesh(Mesh m, out int subMeshes)
        {
            var w = new Work();
            var verts = m.vertices;
            var normals = m.normals;
            var tangents = m.tangents;
            var uvs = m.uv;
            var weld = new Dictionary<Vector3, int>();
            var posOf = new int[verts.Length];
            for (int v = 0; v < verts.Length; ++v)
            {
                if (!weld.TryGetValue(verts[v], out int id))
                {
                    id = w.positions.Count;
                    weld.Add(verts[v], id);
                    w.positions.Add(verts[v]);
                }
                posOf[v] = id;
            }
            Corner C(int v) => new Corner
            {
                pos = posOf[v],
                normal = normals.Length > v ? normals[v] : Vector3.up,
                tangent = tangents.Length > v ? tangents[v] : new Vector4(1, 0, 0, 1),
                uv0 = uvs.Length > v ? uvs[v] : Vector2.zero,
            };
            subMeshes = m.subMeshCount;
            for (int s = 0; s < m.subMeshCount; ++s)
            {
                var idx = m.GetTriangles(s);
                for (int t = 0; t + 2 < idx.Length; t += 3)
                    w.tris.Add(new Tri { a = C(idx[t]), b = C(idx[t + 1]), c = C(idx[t + 2]), subMesh = s });
            }
            return w;
        }

        static Work Clone(Work src)
        {
            var w = new Work();
            w.positions.AddRange(src.positions);
            w.tris = new List<Tri>(src.tris);
            return w;
        }

        // Keeps the part of `w` with dot(n, p) < d (strictly inside; points on the plane count as
        // outside, so they are never split twice) and closes the cut with caps.
        static bool Clip(Work w, Vector3 n, float d, int interiorSubMesh, float uvScale, out string error)
        {
            error = null;
            var dist = new Dictionary<int, float>();
            float Dist(int pos)
            {
                if (!dist.TryGetValue(pos, out float v))
                {
                    v = Vector3.Dot(n, w.positions[pos]) - d;
                    dist.Add(pos, v);
                }
                return v;
            }

            // quick outs: all inside / all outside
            bool anyIn = false, anyOut = false;
            foreach (var t in w.tris)
            {
                foreach (int p in new[] { t.a.pos, t.b.pos, t.c.pos })
                {
                    if (Dist(p) < 0f) anyIn = true;
                    else anyOut = true;
                }
                if (anyIn && anyOut)
                    break;
            }
            if (!anyOut)
                return true;
            if (!anyIn)
            {
                w.tris.Clear();
                return true;
            }

            var edgePoint = new Dictionary<(int, int), int>();
            int Cross(int p, int q)
            {
                var key = p < q ? (p, q) : (q, p);
                if (!edgePoint.TryGetValue(key, out int id))
                {
                    // canonical order: bit-identical for both triangles of the edge and for caps
                    float d0 = Dist(key.Item1), d1 = Dist(key.Item2);
                    float t = d0 / (d0 - d1);
                    id = w.positions.Count;
                    w.positions.Add(Vector3.Lerp(w.positions[key.Item1], w.positions[key.Item2], t));
                    edgePoint.Add(key, id);
                }
                return id;
            }
            Corner Lerp(Corner x, Corner y, int pos)
            {
                // attribute parameter along x -> y from the welded positions (consistent with Cross)
                float dx = Dist(x.pos), dy = Dist(y.pos);
                float t = dx / (dx - dy);
                var tg = Vector4.Lerp(x.tangent, y.tangent, t);
                return new Corner { pos = pos, normal = Vector3.Lerp(x.normal, y.normal, t).normalized, tangent = tg, uv0 = Vector2.Lerp(x.uv0, y.uv0, t) };
            }

            var kept = new List<Tri>(w.tris.Count);
            var next = new Dictionary<int, int>(); // cut segments: from -> to (directed, around each triangle)
            var poly = new List<Corner>(4);
            foreach (var t in w.tris)
            {
                bool ia = Dist(t.a.pos) < 0f, ib = Dist(t.b.pos) < 0f, ic = Dist(t.c.pos) < 0f;
                if (ia && ib && ic)
                {
                    kept.Add(t);
                    continue;
                }
                if (!ia && !ib && !ic)
                    continue;
                poly.Clear();
                int exitPos = -1, entryPos = -1;
                var cs = new[] { t.a, t.b, t.c };
                for (int k = 0; k < 3; ++k)
                {
                    var x = cs[k];
                    var y = cs[(k + 1) % 3];
                    bool ix = Dist(x.pos) < 0f, iy = Dist(y.pos) < 0f;
                    if (ix)
                        poly.Add(x);
                    if (ix != iy)
                    {
                        int pos = Cross(x.pos, y.pos);
                        poly.Add(Lerp(x, y, pos));
                        if (ix) exitPos = pos;  // leaving the kept side
                        else entryPos = pos;    // entering it
                    }
                }
                // fan
                for (int k = 1; k + 1 < poly.Count; ++k)
                    kept.Add(new Tri { a = poly[0], b = poly[k], c = poly[k + 1], subMesh = t.subMesh });
                // the kept polygon runs exit -> entry along the cut; the cap uses the edge the other way
                // (entry -> exit), so cap loops are oriented like the cap (outward normal +n)
                if (exitPos >= 0 && entryPos >= 0 && exitPos != entryPos)
                {
                    if (next.ContainsKey(entryPos))
                    {
                        error = "non-manifold cut (the source mesh is not closed?)";
                        return false;
                    }
                    next.Add(entryPos, exitPos);
                }
            }
            w.tris = kept;

            // chain the segments into loops
            var loops = new List<List<int>>();
            var visited = new HashSet<int>();
            foreach (var start in next.Keys)
            {
                if (visited.Contains(start))
                    continue;
                var loop = new List<int>();
                int cur = start;
                while (!visited.Contains(cur))
                {
                    visited.Add(cur);
                    loop.Add(cur);
                    if (!next.TryGetValue(cur, out cur))
                    {
                        error = "open cut loop (the source mesh is not closed)";
                        return false;
                    }
                }
                if (cur != start)
                {
                    error = "cut loops touch (non-manifold section)";
                    return false;
                }
                if (loop.Count >= 3)
                    loops.Add(loop);
            }

            // plane basis (u, v, n right-handed: u x v = n); caps face +n (the outside of the kept part):
            // counter-clockwise in (u, v) = outward
            Vector3 u = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 v = Vector3.Cross(n, u);
            // outer loops (counter-clockwise around +n) and holes (clockwise); every hole is bridged into
            // the smallest outer loop containing it, then each outer polygon is ear-clipped
            var outers = new List<(List<int> ids, List<Vector2> pts, float area)>();
            var holes = new List<(List<int> ids, List<Vector2> pts)>();
            foreach (var loop in loops)
            {
                var pts = new List<Vector2>(loop.Count);
                foreach (int p in loop)
                    pts.Add(new Vector2(Vector3.Dot(w.positions[p], u), Vector3.Dot(w.positions[p], v)));
                float area = SignedArea(pts);
                if (area > 0f)
                    outers.Add((new List<int>(loop), pts, area));
                else if (area < 0f)
                    holes.Add((new List<int>(loop), pts));
            }
            var holesOf = new List<List<(List<int> ids, List<Vector2> pts)>>();
            foreach (var o in outers)
                holesOf.Add(new List<(List<int>, List<Vector2>)>());
            foreach (var h in holes)
            {
                int best = -1;
                for (int o = 0; o < outers.Count; ++o)
                    if (PointInPolygon(h.pts[0], outers[o].pts) && (best < 0 || outers[o].area < outers[best].area))
                        best = o;
                if (best < 0)
                {
                    error = "cap hole outside every outer loop";
                    return false;
                }
                holesOf[best].Add(h);
            }
            for (int o = 0; o < outers.Count; ++o)
            {
                var ids = outers[o].ids;
                var pts = outers[o].pts;
                // bridge holes right to left (Eberly): each bridge stays visible from the remaining holes
                holesOf[o].Sort((a, b) => MaxX(b.pts).CompareTo(MaxX(a.pts)));
                foreach (var h in holesOf[o])
                {
                    if (!Bridge(ids, pts, h.ids, h.pts))
                    {
                        error = "cap hole bridge failed";
                        return false;
                    }
                }
                var tris = EarClip(pts);
                if (tris == null)
                {
                    error = "cap triangulation failed";
                    return false;
                }
                Corner CapCorner(int k) => new Corner
                {
                    pos = ids[k],
                    normal = n,
                    tangent = new Vector4(u.x, u.y, u.z, -1f),
                    uv0 = pts[k] * uvScale,
                };
                for (int k = 0; k + 2 < tris.Count; k += 3)
                    kept.Add(new Tri { a = CapCorner(tris[k]), b = CapCorner(tris[k + 1]), c = CapCorner(tris[k + 2]), subMesh = interiorSubMesh });
            }
            return true;
        }

        static float MaxX(List<Vector2> p)
        {
            float m = float.MinValue;
            foreach (var q in p)
                m = Mathf.Max(m, q.x);
            return m;
        }

        static bool PointInPolygon(Vector2 q, List<Vector2> p)
        {
            bool inside = false;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
                if ((p[i].y > q.y) != (p[j].y > q.y) && q.x < (p[j].x - p[i].x) * (q.y - p[i].y) / (p[j].y - p[i].y) + p[i].x)
                    inside = !inside;
            return inside;
        }

        // Splices hole (clockwise) into outer (counter-clockwise) through a mutually visible vertex pair:
        // outer ... P, M, hole from M around to M, P, ... (M = the hole's rightmost vertex).
        static bool Bridge(List<int> ids, List<Vector2> pts, List<int> holeIds, List<Vector2> holePts)
        {
            int m = 0;
            for (int i = 1; i < holePts.Count; ++i)
                if (holePts[i].x > holePts[m].x)
                    m = i;
            Vector2 M = holePts[m];

            // nearest edge crossing the ray M + t (1, 0)
            int edge = -1;
            float bestX = float.MaxValue;
            for (int i = 0; i < pts.Count; ++i)
            {
                Vector2 a = pts[i], b = pts[(i + 1) % pts.Count];
                if ((a.y > M.y) == (b.y > M.y))
                    continue;
                float x = a.x + (M.y - a.y) * (b.x - a.x) / (b.y - a.y);
                if (x >= M.x && x < bestX)
                {
                    bestX = x;
                    edge = i;
                }
            }
            if (edge < 0)
                return false;
            Vector2 I = new Vector2(bestX, M.y);
            int pi = pts[edge].x > pts[(edge + 1) % pts.Count].x ? edge : (edge + 1) % pts.Count;
            // a reflex vertex inside triangle (M, I, P) would block the bridge: take the one closest in angle
            Vector2 P = pts[pi];
            float bestAngle = float.MaxValue, bestDist = float.MaxValue;
            int chosen = pi;
            for (int i = 0; i < pts.Count; ++i)
            {
                if (i == pi)
                    continue;
                Vector2 prev = pts[(i + pts.Count - 1) % pts.Count], cur = pts[i], nextP = pts[(i + 1) % pts.Count];
                float turn = (cur.x - prev.x) * (nextP.y - prev.y) - (cur.y - prev.y) * (nextP.x - prev.x);
                if (turn >= 0f)
                    continue; // convex
                if (!InTriangleLoose(cur, M, I, P) && !InTriangleLoose(cur, M, P, I))
                    continue;
                Vector2 d = cur - M;
                float angle = Mathf.Abs(Mathf.Atan2(d.y, d.x));
                if (angle < bestAngle || (angle == bestAngle && d.sqrMagnitude < bestDist))
                {
                    bestAngle = angle;
                    bestDist = d.sqrMagnitude;
                    chosen = i;
                }
            }
            var newIds = new List<int>(ids.Count + holeIds.Count + 2);
            var newPts = new List<Vector2>(pts.Count + holePts.Count + 2);
            for (int i = 0; i <= chosen; ++i)
            {
                newIds.Add(ids[i]);
                newPts.Add(pts[i]);
            }
            for (int k = 0; k <= holePts.Count; ++k)
            {
                int h = (m + k) % holePts.Count;
                newIds.Add(holeIds[h]);
                newPts.Add(holePts[h]);
            }
            for (int i = chosen; i < pts.Count; ++i)
            {
                newIds.Add(ids[i]);
                newPts.Add(pts[i]);
            }
            ids.Clear();
            ids.AddRange(newIds);
            pts.Clear();
            pts.AddRange(newPts);
            return true;
        }

        static bool InTriangleLoose(Vector2 q, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (b.x - a.x) * (q.y - a.y) - (b.y - a.y) * (q.x - a.x);
            float d2 = (c.x - b.x) * (q.y - b.y) - (c.y - b.y) * (q.x - b.x);
            float d3 = (a.x - c.x) * (q.y - c.y) - (a.y - c.y) * (q.x - c.x);
            return d1 >= 0f && d2 >= 0f && d3 >= 0f;
        }

        static float SignedArea(List<Vector2> p)
        {
            float a = 0f;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
                a += p[j].x * p[i].y - p[i].x * p[j].y;
            return 0.5f * a;
        }

        // Ear clipping of a counter-clockwise simple polygon: triangles as index triples (CCW).
        static List<int> EarClip(List<Vector2> p)
        {
            int n = p.Count;
            var idx = new List<int>(n);
            for (int i = 0; i < n; ++i)
                idx.Add(i);
            var result = new List<int>((n - 2) * 3);
            int guard = 0;
            while (idx.Count > 3 && guard++ < 4 * n * n)
            {
                bool clipped = false;
                for (int k = 0; k < idx.Count; ++k)
                {
                    int i0 = idx[(k + idx.Count - 1) % idx.Count], i1 = idx[k], i2 = idx[(k + 1) % idx.Count];
                    Vector2 a = p[i0], b = p[i1], c = p[i2];
                    float cross = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
                    if (cross <= 0f)
                        continue; // reflex or degenerate
                    bool inside = false;
                    foreach (int j in idx)
                    {
                        if (j == i0 || j == i1 || j == i2 || p[j] == a || p[j] == b || p[j] == c)
                            continue; // bridge duplicates share the ear's corners
                        if (InTriangle(p[j], a, b, c))
                        {
                            inside = true;
                            break;
                        }
                    }
                    if (inside)
                        continue;
                    result.Add(i0);
                    result.Add(i1);
                    result.Add(i2);
                    idx.RemoveAt(k);
                    clipped = true;
                    break;
                }
                if (!clipped)
                {
                    // numerically degenerate remainder (collinear runs): drop the flattest vertex
                    int best = 0;
                    float bestCross = float.MaxValue;
                    for (int k = 0; k < idx.Count; ++k)
                    {
                        Vector2 a = p[idx[(k + idx.Count - 1) % idx.Count]], b = p[idx[k]], c = p[idx[(k + 1) % idx.Count]];
                        float cr = Mathf.Abs((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x));
                        if (cr < bestCross)
                        {
                            bestCross = cr;
                            best = k;
                        }
                    }
                    int j0 = idx[(best + idx.Count - 1) % idx.Count], j1 = idx[best], j2 = idx[(best + 1) % idx.Count];
                    result.Add(j0);
                    result.Add(j1);
                    result.Add(j2);
                    idx.RemoveAt(best);
                }
            }
            if (idx.Count == 3)
            {
                result.Add(idx[0]);
                result.Add(idx[1]);
                result.Add(idx[2]);
            }
            return result;
        }

        static bool InTriangle(Vector2 q, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (b.x - a.x) * (q.y - a.y) - (b.y - a.y) * (q.x - a.x);
            float d2 = (c.x - b.x) * (q.y - b.y) - (c.y - b.y) * (q.x - b.x);
            float d3 = (a.x - c.x) * (q.y - c.y) - (a.y - c.y) * (q.x - c.x);
            return d1 >= 0f && d2 >= 0f && d3 >= 0f;
        }

        static Piece ToPiece(Work w, int subMeshes, string name)
        {
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var tangents = new List<Vector4>();
            var uvs = new List<Vector2>();
            var weld = new Dictionary<(int, Vector3, Vector2, Vector4), int>();
            var indices = new List<int>[subMeshes];
            for (int s = 0; s < subMeshes; ++s)
                indices[s] = new List<int>();
            double volume = 0;
            Vector3 centroid = Vector3.zero;
            int Index(Corner c)
            {
                var key = (c.pos, c.normal, c.uv0, c.tangent);
                if (!weld.TryGetValue(key, out int i))
                {
                    i = positions.Count;
                    weld.Add(key, i);
                    positions.Add(w.positions[c.pos]);
                    normals.Add(c.normal);
                    tangents.Add(c.tangent);
                    uvs.Add(c.uv0);
                }
                return i;
            }
            foreach (var t in w.tris)
            {
                var list = indices[Mathf.Min(t.subMesh, subMeshes - 1)];
                list.Add(Index(t.a));
                list.Add(Index(t.b));
                list.Add(Index(t.c));
                // signed tetrahedron volume (outward normal = cross(b - a, c - a), as Unity's front faces)
                Vector3 pa = w.positions[t.a.pos], pb = w.positions[t.b.pos], pc = w.positions[t.c.pos];
                double v6 = Vector3.Dot(pa, Vector3.Cross(pb, pc));
                volume += v6;
                centroid += (pa + pb + pc) * (float)v6;
            }
            var mesh = new Mesh { name = name };
            mesh.indexFormat = positions.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetTangents(tangents);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = subMeshes;
            for (int s = 0; s < subMeshes; ++s)
                mesh.SetTriangles(indices[s], s, false);
            mesh.RecalculateBounds();
            float vol = (float)(volume / 6.0);
            return new Piece
            {
                mesh = mesh,
                volume = vol,
                center = Mathf.Abs(vol) > 1e-12f ? centroid / (float)(volume * 4.0) : mesh.bounds.center,
            };
        }

        /// <summary>
        /// Convex collider source of a piece: its DAG cut of `budget` triangles (`vg`), or - for pieces so
        /// small that the cut has fewer than 16 distinct positions (PhysX needs at least 4 non-coplanar
        /// points) - the piece's own triangles. PhysX builds the hull (at most 255 faces) from the points.
        /// </summary>
        public static Mesh ColliderMesh(Mesh piece, VirtualGeometryMesh vg, int budget)
        {
            var proxy = VgProxy.Extract(vg, budget, out _);
            if (new HashSet<Vector3>(proxy.vertices).Count >= 16)
                return proxy;
            UnityEngine.Object.DestroyImmediate(proxy);
            // tiny piece: its own triangles (few) in one sub-mesh
            var tris = new List<int>();
            for (int sm = 0; sm < piece.subMeshCount; ++sm)
                tris.AddRange(piece.GetTriangles(sm));
            var mesh = new Mesh { name = piece.name + " (collider)" };
            mesh.indexFormat = piece.vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(piece.vertices);
            mesh.SetTriangles(tris, 0);
            return mesh;
        }

        /// <summary>Edges of welded positions used an odd number of times (0 for a closed piece).</summary>
        public static int OpenEdges(Mesh m)
        {
            var verts = m.vertices;
            var weld = new Dictionary<Vector3, int>();
            var id = new int[verts.Length];
            for (int v = 0; v < verts.Length; ++v)
            {
                if (!weld.TryGetValue(verts[v], out int w))
                {
                    w = weld.Count;
                    weld.Add(verts[v], w);
                }
                id[v] = w;
            }
            var count = new Dictionary<(int, int), int>();
            for (int s = 0; s < m.subMeshCount; ++s)
            {
                var idx = m.GetTriangles(s);
                for (int t = 0; t + 2 < idx.Length; t += 3)
                    for (int k = 0; k < 3; ++k)
                    {
                        int a = id[idx[t + k]], b = id[idx[t + (k + 1) % 3]];
                        if (a == b)
                            continue;
                        var key = a < b ? (a, b) : (b, a);
                        count[key] = count.TryGetValue(key, out int c) ? c + 1 : 1;
                    }
            }
            int odd = 0;
            foreach (var c in count.Values)
                if ((c & 1) != 0)
                    odd++;
            return odd;
        }

        /// <summary>Signed volume of a closed mesh (positive for outward, Unity-wound faces).</summary>
        public static float Volume(Mesh m)
        {
            var verts = m.vertices;
            double v = 0;
            for (int s = 0; s < m.subMeshCount; ++s)
            {
                var idx = m.GetTriangles(s);
                for (int t = 0; t + 2 < idx.Length; t += 3)
                    v += Vector3.Dot(verts[idx[t]], Vector3.Cross(verts[idx[t + 1]], verts[idx[t + 2]]));
            }
            return (float)(v / 6.0);
        }
    }
}
