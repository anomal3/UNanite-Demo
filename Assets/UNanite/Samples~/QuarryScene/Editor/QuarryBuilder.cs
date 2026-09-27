using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UNanite.Editor;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;
using Random = System.Random;

namespace UNanite.Samples.QuarryScene
{
    /// <summary>
    /// Builds the quarry sample scene: a terraced pit with a haul channel (Unity terrain), its walls clad with
    /// rock-face scans, scree at the foot of every wall, slabs, boulders, spoil heaps, grass and shrubs on the
    /// plateau, a brick ruin, a worker camp with slab stacks and a sandbag barrier - from a Megascans library
    /// (MegascansImporter) or, without one, from procedural rocks. Everything is placed by rules from the
    /// assets' own geometry (size, openness, facing), so any set of scans works. With `convert` every
    /// renderer and the terrain become virtual geometry; SampleVgToggle (V in Play Mode) switches back to
    /// Unity's renderers (Mesh LOD, LODGroups, terrain) for comparisons.
    /// </summary>
    public static class QuarryBuilder
    {
        public sealed class Options
        {
            public string libraryFolder;                   // MegascansImporter destination; null: procedural rocks
            public string outputFolder = "Assets/QuarryScene";
            public string sceneName = "Quarry";
            public int seed = 7;
            public float density = 1f;                     // scales every scatter count
            public int heightmapResolution = 2049;
            public bool convert = true;
        }

        [MenuItem("Tools/UNanite/Samples/Build Quarry Scene (procedural rocks)")]
        static void BuildProceduralMenu() => Debug.Log(Build(new Options()));

        /// <summary>The catalogue with every piece's size, openness and facing (placement debugging).</summary>
        public static string Describe(Options o)
        {
            var log = new StringBuilder();
            var c = Load(o, log);
            void List(string title, List<Piece> list)
            {
                log.AppendLine($"-- {title}");
                foreach (var p in list)
                    log.AppendLine($"   {p.name,-58} size {p.bounds.size.x,5:F2} {p.bounds.size.y,5:F2} {p.bounds.size.z,5:F2}  open {p.Openness:F2} facing {p.Facing.x,5:F2} {p.Facing.y,5:F2} {p.Facing.z,5:F2}  half {p.HalfThickness:F2}");
            }
            List("shells", c.shells);
            List("patches", c.patches);
            List("boulders", c.boulders);
            List("stones", c.stones);
            List("pebbles", c.pebbles);
            List("debris", c.debris);
            List("props", c.props);
            return log.ToString();
        }

        // ------------------------------------------------------------------------------------------
        // Pieces: prefabs with the geometry facts the placement rules use

        sealed class Piece
        {
            public GameObject prefab;
            public string name;
            public Bounds bounds;       // LOD0 local bounds
            public Vector3 facing;      // area-weighted mean normal (local); length = openness (0 closed .. 1 flat sheet)
            public float MaxExtent => Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            public float MinExtent => Mathf.Min(bounds.size.x, Mathf.Min(bounds.size.y, bounds.size.z));
            public float Openness => facing.magnitude;
            public Vector3 Facing => facing.sqrMagnitude > 1e-8f ? facing.normalized : Vector3.up;
            /// <summary>The local axis (X, Y or Z) with the largest extent across the facing, and that extent.</summary>
            public Vector3 LongAxis(out float extent)
            {
                var f = Facing;
                Vector3 best = Vector3.Cross(f, Mathf.Abs(f.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                extent = 0f;
                for (int a = 0; a < 3; ++a)
                {
                    var axis = a == 0 ? Vector3.right : a == 1 ? Vector3.up : Vector3.forward;
                    if (Mathf.Abs(Vector3.Dot(axis, f)) > 0.6f || bounds.size[a] <= extent)
                        continue;
                    extent = bounds.size[a];
                    best = axis;
                }
                if (extent == 0f)
                    extent = MaxExtent;
                return best;
            }

            // half thickness of the bounds along the facing direction
            public float HalfThickness
            {
                get
                {
                    var f = Facing;
                    var e = bounds.extents;
                    return Mathf.Abs(e.x * f.x) + Mathf.Abs(e.y * f.y) + Mathf.Abs(e.z * f.z);
                }
            }
        }

        sealed class Catalogue
        {
            public readonly List<Piece> shells = new List<Piece>(), patches = new List<Piece>(), boulders = new List<Piece>(),
                                        stones = new List<Piece>(), pebbles = new List<Piece>(), debris = new List<Piece>(),
                                        grass = new List<Piece>(), shrubs = new List<Piece>(), props = new List<Piece>();
            public readonly List<TerrainLayer> layers = new List<TerrainLayer>();
            public readonly Dictionary<string, TerrainLayer> layerById = new Dictionary<string, TerrainLayer>();

            public List<Piece> Props(params string[] words) =>
                props.Where(p => words.All(w => p.name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        }

        static readonly string[] k_PropWords =
        {
            "ruin", "castle", "wall", "curb", "sandbag", "table", "chair", "stool", "barrel", "trash", "lamp",
            "wheelbarrow", "hay", "pumpkin", "pot", "crate", "box", "tank", "cog",
        };
        static readonly string[] k_NaturalWords = { "rock", "stone", "boulder", "cliff", "slate", "quarry", "ledge", "formation", "debris", "rubble" };

        static Catalogue Load(Options o, StringBuilder log)
        {
            var c = new Catalogue();
            var library = o.libraryFolder != null ? MegascansLibrary.Load(o.libraryFolder) : null;
            if (library == null)
            {
                ProceduralPieces(o, c, log);
                return c;
            }
            foreach (var a in library.assets)
            {
                if (a.IsSurface)
                {
                    var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(a.terrainLayer);
                    if (layer != null)
                        c.layerById[a.id] = layer;
                    continue;
                }
                foreach (var path in a.prefabs)
                {
                    var p = MakePiece(AssetDatabase.LoadAssetAtPath<GameObject>(path), a.name);
                    if (p == null)
                        continue;
                    string n = a.name.ToLowerInvariant();
                    if (a.IsPlant)
                        (p.MaxExtent < 1.2f ? c.grass : c.shrubs).Add(p);
                    else if (n.Contains("debris") || n.Contains("rubble"))
                        c.debris.Add(p);
                    else if (k_PropWords.Any(n.Contains) && !k_NaturalWords.Any(w => n.Contains(w) && !n.Contains("wall")))
                        c.props.Add(p);
                    else if (p.MinExtent < 0.13f * p.MaxExtent && p.Openness > 0.3f)
                        c.patches.Add(p);
                    else if (p.Openness > 0.3f)
                        c.shells.Add(p);
                    else if (p.MaxExtent > 0.8f)
                        c.boulders.Add(p);
                    else if (p.MaxExtent > 0.25f)
                        c.stones.Add(p);
                    else
                        c.pebbles.Add(p);
                }
            }
            log.AppendLine($"library: {c.shells.Count} shells, {c.patches.Count} patches, {c.boulders.Count} boulders, {c.stones.Count} stones, " +
                           $"{c.pebbles.Count} pebbles, {c.debris.Count} debris, {c.grass.Count} grass, {c.shrubs.Count} shrubs, {c.props.Count} props, {c.layerById.Count} surfaces");
            return c;
        }

        static Piece MakePiece(GameObject prefab, string name)
        {
            if (prefab == null)
                return null;
            var lodGroup = prefab.GetComponent<LODGroup>();
            var renderers = lodGroup != null ? lodGroup.GetLODs()[0].renderers : prefab.GetComponentsInChildren<Renderer>();
            var filters = renderers.Where(r => r != null).Select(r => r.GetComponent<MeshFilter>()).Where(f => f != null && f.sharedMesh != null).ToList();
            if (filters.Count == 0)
                return null;
            var p = new Piece { prefab = prefab, name = name };
            bool first = true;
            var sum = Vector3.zero;
            float area = 0f;
            foreach (var f in filters)
            {
                var toRoot = prefab.transform.worldToLocalMatrix * f.transform.localToWorldMatrix;
                var b = TransformBounds(f.sharedMesh.bounds, toRoot);
                if (first) p.bounds = b; else p.bounds.Encapsulate(b);
                first = false;
                MeanNormal(f.sharedMesh, toRoot, ref sum, ref area);
            }
            p.facing = area > 0f ? sum / area : Vector3.zero;
            return p;
        }

        // area-weighted sum of triangle normals (LOD0 only for Mesh LOD meshes)
        static void MeanNormal(Mesh mesh, Matrix4x4 toRoot, ref Vector3 sum, ref float area)
        {
            using var dataArray = Mesh.AcquireReadOnlyMeshData(mesh);
            var data = dataArray[0];
            using var positions = new NativeArray<Vector3>(data.vertexCount, Allocator.Temp);
            data.GetVertices(positions);
            for (int s = 0; s < data.subMeshCount; ++s)
            {
                var desc = data.GetSubMesh(s);
                if (desc.topology != MeshTopology.Triangles)
                    continue;
                using var idx = new NativeArray<int>(desc.indexCount, Allocator.Temp);
                data.GetIndices(idx, s, true);
                int first = 0, count = idx.Length;
                if (mesh.lodCount > 1)
                {
                    var lod0 = mesh.GetLod(s, 0);
                    first = (int)lod0.indexStart;
                    count = Mathf.Min((int)lod0.indexCount, idx.Length - first);
                }
                for (int i = first; i + 2 < first + count; i += 3)
                {
                    var a = toRoot.MultiplyPoint3x4(positions[idx[i]]);
                    var b = toRoot.MultiplyPoint3x4(positions[idx[i + 1]]);
                    var c = toRoot.MultiplyPoint3x4(positions[idx[i + 2]]);
                    var n = Vector3.Cross(b - a, c - a);
                    sum += n;
                    area += n.magnitude;
                }
            }
        }

        static Bounds TransformBounds(Bounds b, Matrix4x4 m)
        {
            var r = new Bounds(m.MultiplyPoint3x4(b.center), Vector3.zero);
            for (int i = 0; i < 8; ++i)
                r.Encapsulate(m.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents, new Vector3((i & 1) != 0 ? 1 : -1, (i & 2) != 0 ? 1 : -1, (i & 4) != 0 ? 1 : -1))));
            return r;
        }

        // no library: procedural rocks (UNanite.Editor.ProceduralRock) as boulders, stones and pebbles
        static void ProceduralPieces(Options o, Catalogue c, StringBuilder log)
        {
            string folder = o.outputFolder + "/Procedural";
            Directory.CreateDirectory(folder);
            var shader = Shader.Find("HDRP/Lit");
            var materials = new[] { new Color(0.42f, 0.4f, 0.38f), new Color(0.33f, 0.33f, 0.35f), new Color(0.5f, 0.45f, 0.4f) }.Select((col, i) =>
            {
                string path = $"{folder}/Rock_{i}.mat";
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null)
                {
                    m = new Material(shader);
                    AssetDatabase.CreateAsset(m, path);
                }
                m.SetColor("_BaseColor", col);
                m.SetFloat("_Smoothness", 0.25f);
                HDMaterial.ValidateMaterial(m);
                return m;
            }).ToArray();
            for (int i = 0; i < 12; ++i)
            {
                string meshPath = $"{folder}/Rock_{i}.asset";
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                if (mesh == null)
                {
                    mesh = ProceduralRock.Create(i < 4 ? 7 : i < 8 ? 6 : 5, 100 + i, false);
                    AssetDatabase.CreateAsset(mesh, meshPath);
                }
                var go = new GameObject($"Rock_{i}");
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = materials[i % materials.Length];
                string prefabPath = $"{folder}/Rock_{i}.prefab";
                var prefab = PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
                Object.DestroyImmediate(go);
                var p = MakePiece(prefab, $"Procedural rock {i}");
                (i < 4 ? c.boulders : i < 8 ? c.stones : c.pebbles).Add(p);
            }
            log.AppendLine("no Megascans library: procedural rocks");
        }

        // ------------------------------------------------------------------------------------------
        // Terrain shape: pit floor, terraced walls, plateau with hills, a haul channel, spoil heaps

        sealed class Shape
        {
            public const float Size = 400f, HeightRange = 70f, FloorY = 6f, Depth = 28f, WallRun = 3.4f, R0 = 54f;
            public const int Rings = 3;
            public const float ChannelHalfWidth = 8f, ChannelShoulder = 6f, ChannelLength = 100f;
            public float channelAngle;
            public Vector2 ChannelDir => new Vector2(Mathf.Cos(channelAngle), Mathf.Sin(channelAngle));
            readonly float[] m_Phase = new float[12];
            readonly float[] m_Bench = { 9f, 8f };
            public readonly List<Vector4> heaps = new List<Vector4>(); // x, z, height, radius

            public Shape(Random rng)
            {
                for (int i = 0; i < m_Phase.Length; ++i)
                    m_Phase[i] = (float)(rng.NextDouble() * Math.PI * 2);
                channelAngle = (float)(rng.NextDouble() * Math.PI * 2);
            }

            public float Rim(float theta) =>
                R0 * (1f + 0.09f * Mathf.Sin(2 * theta + m_Phase[0]) + 0.05f * Mathf.Sin(3 * theta + m_Phase[1]) + 0.025f * Mathf.Sin(7 * theta + m_Phase[2]));

            float Jag(int ring, float theta, float radius) =>
                0.6f * (Mathf.PerlinNoise(theta * radius / 9f + ring * 31.7f, m_Phase[3] + ring * 3.1f) - 0.5f) +
                0.2f * (Mathf.PerlinNoise(theta * radius / 3f + ring * 11.3f, m_Phase[4]) - 0.5f);

            float Bench(int ring, float theta) => m_Bench[ring] + 3f * (Mathf.PerlinNoise(theta * 2.2f + m_Phase[5] + ring * 5f, ring * 1.7f) - 0.5f);

            /// <summary>Base radius (foot) of wall `ring` at angle theta.</summary>
            public float RingBase(int ring, float theta)
            {
                float r = Rim(theta);
                for (int k = 0; k < ring; ++k)
                    r += WallRun + Bench(k, theta);
                return r + Jag(ring, theta, r);
            }

            public float TopRadius(float theta) => RingBase(Rings - 1, theta) + WallRun;

            static float Smoother(float t)
            {
                t = Mathf.Clamp01(t);
                return t * t * t * (t * (t * 6f - 15f) + 10f);
            }

            static float Fbm(float x, float y, int octaves)
            {
                float sum = 0f, amp = 0.5f, freq = 1f;
                for (int i = 0; i < octaves; ++i)
                {
                    sum += amp * (Mathf.PerlinNoise(x * freq + 17.3f * i, y * freq - 9.1f * i) - 0.5f);
                    amp *= 0.5f;
                    freq *= 2.03f;
                }
                return sum * 2f; // about -1..1
            }

            /// <summary>0 outside the haul channel, 1 on its road; `along` = distance from the pit centre along the channel.</summary>
            public float Channel(float x, float z, out float along)
            {
                var d = ChannelDir;
                along = x * d.x + z * d.y;
                float across = Mathf.Abs(-x * d.y + z * d.x);
                float start = Rim(channelAngle) - 6f;
                float m = 1f - Mathf.SmoothStep(0f, 1f, (across - ChannelHalfWidth) / ChannelShoulder);
                m *= Mathf.SmoothStep(0f, 1f, (along - (start - 20f)) / 15f);
                m *= 1f - Mathf.SmoothStep(0f, 1f, (along - (start + ChannelLength + 10f)) / 20f);
                return Mathf.Clamp01(m);
            }

            public float RampHeight(float along)
            {
                float start = Rim(channelAngle) - 6f;
                return FloorY + Depth * Mathf.SmoothStep(0f, 1f, (along - start) / ChannelLength);
            }

            public float Height(float x, float z)
            {
                float r = Mathf.Sqrt(x * x + z * z);
                float theta = Mathf.Atan2(z, x);
                float h = FloorY;
                for (int k = 0; k < Rings; ++k)
                    h += Depth / Rings * Smoother((r - RingBase(k, theta)) / WallRun);
                float top = Smoother((r - TopRadius(theta) - 1f) / 8f);
                h += top * (1.8f * Fbm(x / 55f, z / 55f, 4) + 24f * Mathf.SmoothStep(0f, 1f, (r - 115f) / 75f) * (0.55f + 0.45f * Fbm(x / 70f, z / 70f, 4)));
                float floor = 1f - Smoother((r - Rim(theta) + 1.5f) / 3f);
                float heapsHeight = 0f;
                foreach (var hp in heaps)
                {
                    float dx = x - hp.x, dz = z - hp.y;
                    float t = (dx * dx + dz * dz) / (hp.w * hp.w);
                    heapsHeight += hp.z * Mathf.Exp(-t * 2.2f) * (1f + 0.15f * Fbm(x / 3f, z / 3f, 2));
                }
                h += floor * (0.3f * Fbm(x / 16f, z / 16f, 4) + heapsHeight);
                float m = Channel(x, z, out float along);
                if (m > 0f)
                    h = Mathf.Lerp(h, RampHeight(along) + 0.15f * Fbm(x / 9f, z / 9f, 3), m);
                return h;
            }
        }

        // ------------------------------------------------------------------------------------------
        // Placement

        sealed class Placer
        {
            public Terrain terrain;
            public Random rng;
            public Shape shape;
            public readonly List<Vector3> keepOut = new List<Vector3>(); // x, z, radius
            public int count;
            public long triangles;
            readonly Dictionary<GameObject, long> m_Triangles = new Dictionary<GameObject, long>();

            public float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
            public T Pick<T>(IList<T> list) => list[rng.Next(list.Count)];

            public float Ground(float x, float z) => terrain.SampleHeight(new Vector3(x, 0f, z)) + terrain.transform.position.y;

            public Vector3 Normal(float x, float z)
            {
                var td = terrain.terrainData;
                var p = terrain.transform.position;
                return td.GetInterpolatedNormal((x - p.x) / td.size.x, (z - p.z) / td.size.z);
            }

            public bool Free(float x, float z, float margin = 0f)
            {
                foreach (var k in keepOut)
                {
                    float dx = x - k.x, dz = z - k.y, r = k.z + margin;
                    if (dx * dx + dz * dz < r * r)
                        return false;
                }
                return true;
            }

            public GameObject Place(Piece p, Transform parent, Vector3 position, Quaternion rotation, float scale)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(p.prefab, parent);
                go.transform.SetPositionAndRotation(position, rotation);
                go.transform.localScale = Vector3.one * scale;
                // occlusion / reflection static only: batching static would merge thousands of scans into huge buffers
                GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ReflectionProbeStatic);
                foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
                    if (t != go.transform)
                        GameObjectUtility.SetStaticEditorFlags(t.gameObject, StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ReflectionProbeStatic);
                count++;
                if (!m_Triangles.TryGetValue(p.prefab, out long tris))
                {
                    var group = p.prefab.GetComponent<LODGroup>();
                    var renderers = group != null ? group.GetLODs()[0].renderers : p.prefab.GetComponentsInChildren<Renderer>();
                    tris = renderers.Where(r => r != null).Select(r => r.GetComponent<MeshFilter>()).Where(f => f != null && f.sharedMesh != null)
                                    .Sum(f => MegascansImporter.Triangles(f.sharedMesh));
                    m_Triangles[p.prefab] = tris;
                }
                triangles += tris;
                return go;
            }

            /// <summary>Aligns the piece's facing with `normal`, twisted randomly around it, sunk `sink` of its half thickness.</summary>
            public GameObject PlaceAligned(Piece p, Transform parent, Vector3 surface, Vector3 normal, float scale, float sink)
            {
                var rot = Quaternion.AngleAxis(R(0f, 360f), normal) * Quaternion.FromToRotation(p.Facing, normal);
                var center = surface - normal * (p.HalfThickness * scale * sink);
                return Place(p, parent, center - rot * (p.bounds.center * scale), rot, scale);
            }

            /// <summary>
            /// Wall cladding: the piece's facing along `normal`, its long axis along `tangent` (+- jitter, either way
            /// round), scaled so the long axis spans `span` metres; its bounds centre `sink` of its thickness behind the surface.
            /// </summary>
            public GameObject PlaceCladding(Piece p, Transform parent, Vector3 surface, Vector3 normal, Vector3 tangent, float span, float sink, float jitter)
            {
                var r0 = Quaternion.FromToRotation(p.Facing, normal);
                var axis = p.LongAxis(out float extent);
                var along = Vector3.ProjectOnPlane(r0 * axis, normal);
                float angle = along.sqrMagnitude > 1e-6f ? Vector3.SignedAngle(along, tangent, normal) : 0f;
                var rot = Quaternion.AngleAxis(angle + R(-jitter, jitter) + (rng.Next(2) == 0 ? 0f : 180f), normal) * r0;
                float scale = Mathf.Clamp(span / extent, 0.3f, 5f);
                var center = surface - normal * (p.HalfThickness * scale * sink);
                return Place(p, parent, center - rot * (p.bounds.center * scale), rot, scale);
            }

            /// <summary>Any orientation (rocks), resting on the ground sunk by `sink` of its height.</summary>
            public GameObject PlaceLoose(Piece p, Transform parent, float x, float z, float scale, float sink, bool upright = false)
            {
                // any orientation from the seeded generator (normalised random quaternion)
                var rot = upright ? Quaternion.Euler(R(-6f, 6f), R(0f, 360f), R(-6f, 6f))
                                  : new Quaternion(R(-1f, 1f), R(-1f, 1f), R(-1f, 1f), R(-1f, 1f)).normalized;
                var b = TransformBounds(p.bounds, Matrix4x4.TRS(Vector3.zero, rot, Vector3.one * scale));
                float ground = Ground(x, z);
                var pos = new Vector3(x, ground - b.min.y - b.size.y * sink, z);
                return Place(p, parent, pos, rot, scale);
            }

            /// <summary>Upright on the ground (props, structures): yaw only, the lowest point `sink` metres below the ground.</summary>
            public GameObject PlaceUpright(Piece p, Transform parent, Vector3 at, float yaw, float scale = 1f, float sink = 0.02f, float onTop = float.NaN)
            {
                var rot = Quaternion.Euler(0f, yaw, 0f);
                var b = TransformBounds(p.bounds, Matrix4x4.TRS(Vector3.zero, rot, Vector3.one * scale));
                float ground = float.IsNaN(onTop) ? Ground(at.x, at.z) : onTop;
                var pos = new Vector3(at.x - b.center.x, ground - b.min.y - sink, at.z - b.center.z);
                return Place(p, parent, pos, rot, scale);
            }
        }

        // ------------------------------------------------------------------------------------------

        public static string Build(Options o)
        {
            var sw = Stopwatch.StartNew();
            var log = new StringBuilder();
            Directory.CreateDirectory(o.outputFolder);
            var rng = new Random(o.seed);
            var cat = Load(o, log);
            var shape = new Shape(rng);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var terrain = BuildTerrain(o, shape, cat, rng, log);
            var place = new Placer { terrain = terrain, rng = rng, shape = shape };
            var root = new GameObject("Quarry").transform;
            Transform Group(string name)
            {
                var t = new GameObject(name).transform;
                t.SetParent(root, false);
                return t;
            }

            var d = shape.ChannelDir;
            var perp = new Vector2(-d.y, d.x);
            float rimB = shape.Rim(shape.channelAngle);
            Vector2 campCenter = d * (rimB - 16f) + perp * 9f;
            float ruinAngle = shape.channelAngle + 0.75f;
            Vector2 ruinDir = new Vector2(Mathf.Cos(ruinAngle), Mathf.Sin(ruinAngle));
            Vector2 ruinCenter = ruinDir * (shape.TopRadius(ruinAngle) + 26f);
            place.keepOut.Add(new Vector3(campCenter.x, campCenter.y, 15f));
            place.keepOut.Add(new Vector3(ruinCenter.x, ruinCenter.y, 16f));

            BuildRuin(place, cat, Group("Ruin"), ruinCenter, ruinDir, log);
            BuildCamp(place, cat, Group("Camp"), campCenter, d, log);
            BuildBarrier(place, cat, Group("Barrier"), shape, log);
            CladWalls(place, cat, Group("Walls"), o.density, log);
            Scree(place, cat, Group("Scree"), o.density, log);
            FloorAndBenches(place, cat, Group("Rocks"), o.density, log);
            Plants(place, cat, Group("Plants"), o.density, log);
            log.AppendLine($"placed {place.count} objects, {place.triangles / 1e6:F1} M triangles at LOD0");

            SetupLighting(o, scene, shape);
            var cam = SetupCamera(shape, place, ruinCenter, campCenter);

            if (o.convert)
            {
                var t = Stopwatch.StartNew();
                int converted = VirtualGeometryConverter.Convert(new[] { root.gameObject }, out var skipped);
                log.AppendLine($"converted {converted} renderers in {t.Elapsed.TotalSeconds:F0} s" + (skipped.Count > 0 ? $", skipped {skipped.Count}: {string.Join("; ", skipped.Distinct().Take(8))}" : ""));
                t.Restart();
                var vgt = VirtualGeometryTerrainConverter.Convert(terrain);
                log.AppendLine(vgt != null ? $"terrain as virtual geometry in {t.Elapsed.TotalSeconds:F0} s" : "terrain conversion FAILED");
            }

            string scenePath = $"{o.outputFolder}/{o.sceneName}.unity";
            EditorSceneManager.SaveScene(scene, scenePath);
            log.AppendLine($"{scenePath} in {sw.Elapsed.TotalSeconds:F0} s");
            return log.ToString();
        }

        // ------------------------------------------------------------------------------------------
        // Terrain

        static Terrain BuildTerrain(Options o, Shape shape, Catalogue cat, Random rng, StringBuilder log)
        {
            // spoil heaps on the floor, away from the channel
            for (int i = 0; i < 5; ++i)
            {
                for (int attempt = 0; attempt < 50; ++attempt)
                {
                    float a = (float)(rng.NextDouble() * Math.PI * 2);
                    float delta = Mathf.Abs(Mathf.DeltaAngle(a * Mathf.Rad2Deg, shape.channelAngle * Mathf.Rad2Deg));
                    if (delta < 50f)
                        continue;
                    float r = (float)(0.25 + rng.NextDouble() * 0.45) * Shape.R0;
                    var c = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                    if (shape.heaps.Any(h => Vector2.Distance(new Vector2(h.x, h.y), c) < 22f))
                        continue;
                    shape.heaps.Add(new Vector4(c.x, c.y, 1.5f + (float)rng.NextDouble() * 2.5f, 6f + (float)rng.NextDouble() * 5f));
                    break;
                }
            }

            int res = o.heightmapResolution;
            var td = new TerrainData { heightmapResolution = res };
            td.size = new Vector3(Shape.Size, Shape.HeightRange, Shape.Size);
            var heights = new float[res, res];
            float half = Shape.Size * 0.5f, step = Shape.Size / (res - 1);
            for (int z = 0; z < res; ++z)
                for (int x = 0; x < res; ++x)
                    heights[z, x] = Mathf.Clamp01(shape.Height(x * step - half, z * step - half) / Shape.HeightRange);
            td.SetHeights(0, 0, heights);
            string path = $"{o.outputFolder}/{o.sceneName}_Terrain.asset";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(td, path);

            // layers: gravel floor, dark dirt, two slate faces, dry grass, dark ground, road dust, cracked slate plates
            string[] wanted = { "udlladln", "xbmica0", "xb5ieey", "xbknedb", "xbreagf", "xbnedjk", "xbklfix", "xccibbi" };
            float[] tiles = { 3f, 4f, 6f, 6f, 4f, 4f, 5f, 4.5f };
            var layers = wanted.Select(id => cat.layerById.TryGetValue(id, out var l) ? l : null).ToArray();
            if (layers.Any(l => l == null))
                layers = FallbackLayers(o, cat, wanted.Length);
            if (layers.Length == wanted.Length)
                for (int l = 0; l < layers.Length; ++l)
                {
                    layers[l].tileSize = new Vector2(tiles[l], tiles[l]);
                    EditorUtility.SetDirty(layers[l]);
                }
            td.terrainLayers = layers;
            int ares = 1024;
            td.alphamapResolution = ares;
            var alpha = new float[ares, ares, layers.Length];
            var w = new float[8];
            for (int z = 0; z < ares; ++z)
                for (int x = 0; x < ares; ++x)
                {
                    float wx = (x + 0.5f) / ares * Shape.Size - half, wz = (z + 0.5f) / ares * Shape.Size - half;
                    var n = td.GetInterpolatedNormal((x + 0.5f) / ares, (z + 0.5f) / ares);
                    float h = td.GetInterpolatedHeight((x + 0.5f) / ares, (z + 0.5f) / ares);
                    float steep = Mathf.SmoothStep(0f, 1f, (1f - n.y - 0.3f) / 0.3f);
                    float channel = shape.Channel(wx, wz, out _);
                    float n1 = Mathf.PerlinNoise(wx / 23f + 3.1f, wz / 23f - 7.7f), n2 = Mathf.PerlinNoise(wx / 7f - 1.3f, wz / 7f + 2.9f);
                    float r = Mathf.Sqrt(wx * wx + wz * wz);
                    float theta = Mathf.Atan2(wz, wx);
                    bool plateau = r > shape.TopRadius(theta) + 1f;
                    bool floor = h < Shape.FloorY + 0.6f + (shape.heaps.Count > 0 ? 4f : 0f) && r < shape.Rim(theta) - 0.5f;
                    Array.Clear(w, 0, w.Length);
                    float flat = 1f - steep;
                    w[2] = steep * n1;
                    w[3] = steep * (1f - n1);
                    if (channel > 0.4f)
                    {
                        w[0] = flat * 0.75f;
                        w[1] = flat * 0.25f * n2;
                    }
                    else if (plateau)
                    {
                        float n3 = Mathf.PerlinNoise(wx / 41f - 11.3f, wz / 41f + 6.7f);
                        w[4] = flat * Mathf.SmoothStep(0f, 1f, n1 * 1.6f - 0.3f);
                        w[6] = (flat - w[4]) * Mathf.SmoothStep(0f, 1f, (n3 - 0.45f) * 4f);
                        w[5] = flat - w[4] - w[6];
                    }
                    else if (floor)
                    {
                        w[7] = flat * (0.45f + 0.4f * n2);
                        w[0] = flat * 0.35f * (1f - n2);
                        w[1] = flat * 0.3f * (1f - n1);
                    }
                    else
                    {
                        w[5] = flat * 0.5f;
                        w[7] = flat * 0.35f;
                        w[0] = flat * 0.15f;
                    }
                    float sum = 0f;
                    for (int l = 0; l < layers.Length; ++l)
                        sum += w[Mathf.Min(l, 7)];
                    for (int l = 0; l < layers.Length; ++l)
                        alpha[z, x, l] = sum > 0f ? w[Mathf.Min(l, 7)] / sum : (l == 0 ? 1f : 0f);
                }
            td.SetAlphamaps(0, 0, alpha);
            EditorUtility.SetDirty(td);
            AssetDatabase.SaveAssets();

            var go = Terrain.CreateTerrainGameObject(td);
            go.name = "Terrain";
            go.transform.position = new Vector3(-half, 0f, -half);
            var terrain = go.GetComponent<Terrain>();
            terrain.drawInstanced = true;
            terrain.heightmapPixelError = 5;
            terrain.basemapDistance = 400f;
            terrain.materialTemplate = GraphicsSettings.currentRenderPipeline.defaultTerrainMaterial;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
            log.AppendLine($"terrain {res}² over {Shape.Size} m, {shape.heaps.Count} spoil heaps, {layers.Length} layers");
            return terrain;
        }

        static TerrainLayer[] FallbackLayers(Options o, Catalogue cat, int count)
        {
            if (cat.layerById.Count > 0)
                return cat.layerById.Values.Take(count).ToArray();
            string path = $"{o.outputFolder}/Procedural/Ground.terrainlayer";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
            if (layer == null)
            {
                layer = new TerrainLayer { diffuseTexture = Texture2D.grayTexture, tileSize = new Vector2(4f, 4f) };
                AssetDatabase.CreateAsset(layer, path);
            }
            return new[] { layer };
        }

        // ------------------------------------------------------------------------------------------
        // Walls: rows of rock-face scans along every wall and the channel sides, long axis horizontal,
        // about three times overlapped; boulders along the lip of every bench

        static void CladWalls(Placer place, Catalogue cat, Transform parent, float density, StringBuilder log)
        {
            var shape = place.shape;
            var pieces = cat.shells.Where(p => p.MaxExtent >= 2.3f).ToList();
            bool shells = pieces.Count > 0;
            if (!shells)
                pieces = cat.boulders;
            if (pieces.Count == 0)
                return;
            int placed = 0, lip = 0;
            float scale = 1f / Mathf.Clamp(density, 0.3f, 3f);
            // the face normal: horizontal direction from the terrain (follows the rim), a fixed tilt per row
            Vector3 FaceNormal(Vector3 terrainNormal, float tiltFromVertical)
            {
                var h = new Vector3(terrainNormal.x, 0f, terrainNormal.z);
                h = h.sqrMagnitude > 1e-6f ? h.normalized : Vector3.forward;
                float a = tiltFromVertical * Mathf.Deg2Rad;
                return (h * Mathf.Cos(a) + Vector3.up * Mathf.Sin(a)).normalized;
            }
            void Clad(float x, float z, float tilt, float spanMin, float spanMax, float sink)
            {
                var terrainNormal = place.Normal(x, z);
                if (terrainNormal.y > 0.85f)
                    return;
                var n = FaceNormal(terrainNormal, tilt + place.R(-4f, 4f));
                var tangent = Vector3.Cross(Vector3.up, n).normalized;
                var p = place.Pick(pieces);
                var surface = new Vector3(x, place.Ground(x, z), z);
                if (shells)
                    place.PlaceCladding(p, parent, surface, n, tangent, place.R(spanMin, spanMax), sink, 15f);
                else
                    place.PlaceLoose(p, parent, x, z, Mathf.Clamp(place.R(3f, 6f) / p.MaxExtent, 0.5f, 6f), 0.3f);
                placed++;
            }
            // rows: (fraction up the wall run, tilt of the face above horizontal, spacing, span range, sink)
            var rows = new[]
            {
                (at: 0.5f, tilt: 20f, spacing: 5.0f, min: 14f, max: 22f, sink: -0.55f),
                (at: 0.9f, tilt: 36f, spacing: 6.5f, min: 7f, max: 12f, sink: -0.2f),
            };
            for (int k = 0; k < Shape.Rings; ++k)
            {
                float perimeter = 2f * Mathf.PI * (Shape.R0 + k * (Shape.WallRun + 8.5f));
                foreach (var row in rows)
                {
                    int n = Mathf.CeilToInt(perimeter / (row.spacing * scale));
                    for (int i = 0; i < n; ++i)
                    {
                        float theta = (i + place.R(-0.35f, 0.35f)) / n * Mathf.PI * 2f;
                        float r = shape.RingBase(k, theta) + Shape.WallRun * (row.at + place.R(-0.06f, 0.06f));
                        float x = r * Mathf.Cos(theta), z = r * Mathf.Sin(theta);
                        if (shape.Channel(x, z, out _) > 0.25f || !place.Free(x, z, 1f))
                            continue;
                        Clad(x, z, row.tilt, row.min, row.max, row.sink);
                    }
                }
                // the lip of the bench above the wall: half-buried boulders break the edge
                int lips = Mathf.CeilToInt(perimeter / (6.5f * scale));
                for (int i = 0; i < lips && cat.boulders.Count > 0; ++i)
                {
                    float theta = (i + place.R(-0.4f, 0.4f)) / lips * Mathf.PI * 2f;
                    float r = shape.RingBase(k, theta) + Shape.WallRun + place.R(0.3f, 2f);
                    float x = r * Mathf.Cos(theta), z = r * Mathf.Sin(theta);
                    if (shape.Channel(x, z, out _) > 0.2f || !place.Free(x, z, 1f))
                        continue;
                    var b = place.Pick(cat.boulders);
                    place.PlaceLoose(b, parent, x, z, Mathf.Clamp(place.R(1.2f, 3f) / b.MaxExtent, 0.5f, 5f), 0.45f);
                    lip++;
                }
            }
            // channel sides: wherever the cut is steep
            var d = shape.ChannelDir;
            var perp = new Vector2(-d.y, d.x);
            float start = shape.Rim(shape.channelAngle) - 26f;
            for (float a = start; a < start + Shape.ChannelLength + 40f; a += 5f * scale)
                foreach (float side in new[] { -1f, 1f })
                {
                    var xz = d * a + perp * (side * (Shape.ChannelHalfWidth + Shape.ChannelShoulder * 0.5f) + place.R(-1f, 1f));
                    if (!place.Free(xz.x, xz.y, 1f))
                        continue;
                    Clad(xz.x, xz.y, 25f, 10f, 16f, -0.4f);
                }
            log.AppendLine($"walls: {placed} {(shells ? "rock faces" : "boulders")}, {lip} boulders along the bench lips");
        }

        // scree at the foot of every wall: dense near the wall, thinning out over a few metres
        static void Scree(Placer place, Catalogue cat, Transform parent, float density, StringBuilder log)
        {
            var small = cat.pebbles.Concat(cat.stones).ToList();
            if (small.Count == 0)
                return;
            var shape = place.shape;
            int perRing = Mathf.RoundToInt(1500 * density);
            int placed = 0;
            for (int ring = 0; ring < Shape.Rings; ++ring)
                for (int i = 0; i < perRing; ++i)
                {
                    float theta = place.R(-Mathf.PI, Mathf.PI);
                    float u = -Mathf.Log(Mathf.Max(1e-4f, 1f - place.R(0f, 0.999f))) * 2.2f;
                    float r = shape.RingBase(ring, theta) + 0.6f - u;
                    float x = r * Mathf.Cos(theta), z = r * Mathf.Sin(theta);
                    if (shape.Channel(x, z, out _) > 0.3f || !place.Free(x, z))
                        continue;
                    var p = place.Pick(small);
                    float size = p.MaxExtent;
                    float target = size < 0.25f ? place.R(0.12f, 0.55f) : place.R(0.3f, 1.1f);
                    place.PlaceLoose(p, parent, x, z, Mathf.Clamp(target / size, 0.5f, 5f), 0.3f);
                    placed++;
                }
            log.AppendLine($"scree: {placed} stones");
        }

        // floor and benches: slate patches, boulders, stones, loose slabs on the spoil heaps; outcrops on the plateau
        static void FloorAndBenches(Placer place, Catalogue cat, Transform parent, float density, StringBuilder log)
        {
            var shape = place.shape;
            int patches = 0, boulders = 0, stones = 0, outcrops = 0;
            float Disk(float max) => max * Mathf.Sqrt(place.R(0f, 1f));
            for (int i = 0; i < Mathf.RoundToInt(260 * density) && cat.patches.Count > 0; ++i)
            {
                float a = place.R(-Mathf.PI, Mathf.PI), r = Disk(shape.TopRadius(a) + 30f);
                float x = r * Mathf.Cos(a), z = r * Mathf.Sin(a);
                var n = place.Normal(x, z);
                if (n.y < 0.9f || !place.Free(x, z, 1f))
                    continue;
                var p = place.Pick(cat.patches);
                place.PlaceAligned(p, parent, new Vector3(x, place.Ground(x, z), z), n, place.R(0.8f, 2.2f), 0.4f);
                patches++;
            }
            foreach (var heap in shape.heaps)
                for (int i = 0; i < Mathf.RoundToInt(45 * density); ++i)
                {
                    float a = place.R(-Mathf.PI, Mathf.PI), r = heap.w * 0.9f * Mathf.Sqrt(place.R(0f, 1f));
                    float x = heap.x + r * Mathf.Cos(a), z = heap.y + r * Mathf.Sin(a);
                    var list = i % 3 == 0 && cat.patches.Count > 0 ? cat.patches : cat.stones.Count > 0 ? cat.stones : cat.boulders;
                    if (list.Count == 0)
                        break;
                    var p = place.Pick(list);
                    if (list == cat.patches)
                        place.PlaceAligned(p, parent, new Vector3(x, place.Ground(x, z), z), place.Normal(x, z), place.R(0.5f, 1.2f), 0.2f);
                    else
                        place.PlaceLoose(p, parent, x, z, Mathf.Clamp(place.R(0.4f, 1.4f) / p.MaxExtent, 0.4f, 4f), 0.3f);
                    stones++;
                }
            for (int i = 0; i < Mathf.RoundToInt(320 * density) && cat.boulders.Count > 0; ++i)
            {
                float a = place.R(-Mathf.PI, Mathf.PI), r = Disk(shape.TopRadius(a) + 15f);
                float x = r * Mathf.Cos(a), z = r * Mathf.Sin(a);
                if (place.Normal(x, z).y < 0.8f || shape.Channel(x, z, out _) > 0.2f || !place.Free(x, z, 2f))
                    continue;
                var p = place.Pick(cat.boulders);
                bool large = place.R(0f, 1f) < 0.25f;
                place.PlaceLoose(p, parent, x, z, Mathf.Clamp((large ? place.R(2.5f, 4.5f) : place.R(0.9f, 2.2f)) / p.MaxExtent, 0.4f, 5f), 0.3f);
                boulders++;
            }
            for (int i = 0; i < Mathf.RoundToInt(900 * density) && cat.stones.Count + cat.pebbles.Count > 0; ++i)
            {
                float a = place.R(-Mathf.PI, Mathf.PI), r = Disk(shape.TopRadius(a) + 6f);
                float x = r * Mathf.Cos(a), z = r * Mathf.Sin(a);
                if (shape.Channel(x, z, out _) > 0.5f || !place.Free(x, z))
                    continue;
                var p = place.Pick(cat.stones.Count > 0 && (i & 1) == 0 ? cat.stones : cat.pebbles.Count > 0 ? cat.pebbles : cat.stones);
                place.PlaceLoose(p, parent, x, z, Mathf.Clamp(place.R(0.15f, 0.7f) / p.MaxExtent, 0.4f, 5f), 0.3f);
                stones++;
            }
            // debris patches (flat scans) near the foot of the walls and on the floor, aligned with the ground
            for (int i = 0; i < Mathf.RoundToInt(420 * density) && cat.debris.Count > 0; ++i)
            {
                float a = place.R(-Mathf.PI, Mathf.PI);
                int ring = place.rng.Next(Shape.Rings);
                float r = i % 3 == 0 ? Disk(shape.Rim(a) - 2f) : shape.RingBase(ring, a) - place.R(0.5f, 6f);
                float x = r * Mathf.Cos(a), z = r * Mathf.Sin(a);
                var n = place.Normal(x, z);
                if (n.y < 0.8f || shape.Channel(x, z, out _) > 0.4f || !place.Free(x, z))
                    continue;
                var p = place.Pick(cat.debris);
                place.PlaceAligned(p, parent, new Vector3(x, place.Ground(x, z), z), n, Mathf.Clamp(place.R(0.6f, 2f) / p.MaxExtent, 0.8f, 4f), 0.3f);
                stones++;
            }
            // outcrops: big rock faces lying on the plateau around the rim
            var big = cat.shells.Where(p => p.MaxExtent > 2f).ToList();
            for (int i = 0; i < Mathf.RoundToInt(45 * density) && big.Count > 0; ++i)
            {
                float a = place.R(-Mathf.PI, Mathf.PI), r = shape.TopRadius(a) + place.R(6f, 90f);
                float x = r * Mathf.Cos(a), z = r * Mathf.Sin(a);
                if (shape.Channel(x, z, out _) > 0.1f || !place.Free(x, z, 3f))
                    continue;
                var p = place.Pick(big);
                var n = Quaternion.Euler(place.R(-8f, 8f), 0f, place.R(-8f, 8f)) * place.Normal(x, z);
                bool landmark = p.MaxExtent > 10f;
                place.PlaceAligned(p, parent, new Vector3(x, place.Ground(x, z), z), n.normalized,
                                   Mathf.Clamp((landmark ? place.R(14f, 22f) : place.R(3f, 7f)) / p.MaxExtent, 0.3f, 4f), 0.75f);
                outcrops++;
            }
            log.AppendLine($"rocks: {patches} slate patches, {boulders} boulders, {stones} stones, {outcrops} outcrops");
        }

        static void Plants(Placer place, Catalogue cat, Transform parent, float density, StringBuilder log)
        {
            var shape = place.shape;
            int grass = 0, shrubs = 0;
            var spots = new List<Vector2>();
            for (int i = 0; i < Mathf.RoundToInt(420 * density) && cat.shrubs.Count > 0; ++i)
            {
                float a = place.R(-Mathf.PI, Mathf.PI), r = place.R(shape.TopRadius(a) + 3f, 190f);
                if (i % 6 == 0)
                    r = place.R(shape.Rim(a) + 5f, shape.TopRadius(a)); // a few on the benches
                float x = r * Mathf.Cos(a), z = r * Mathf.Sin(a);
                if (place.Normal(x, z).y < 0.88f || shape.Channel(x, z, out _) > 0.2f || !place.Free(x, z, 1.5f))
                    continue;
                if (Mathf.PerlinNoise(x / 25f - 8f, z / 25f + 4f) < 0.4f)
                    continue;
                var p = place.Pick(cat.shrubs);
                place.PlaceUpright(p, parent, new Vector3(x, 0f, z), place.R(0f, 360f), place.R(0.6f, 1.15f), 0.1f);
                spots.Add(new Vector2(x, z));
                shrubs++;
            }
            foreach (var spot in spots)
                for (int i = 0; i < 9 && cat.grass.Count > 0; ++i)
                {
                    float a = place.R(-Mathf.PI, Mathf.PI), r = place.R(1.2f, 4.5f);
                    float x = spot.x + r * Mathf.Cos(a), z = spot.y + r * Mathf.Sin(a);
                    if (place.Normal(x, z).y < 0.85f || !place.Free(x, z))
                        continue;
                    var p = place.Pick(cat.grass);
                    place.PlaceUpright(p, parent, new Vector3(x, 0f, z), place.R(0f, 360f), place.R(0.8f, 1.4f), 0.05f);
                    grass++;
                }
            log.AppendLine($"plants: {shrubs} shrubs with {grass} grass tufts around them");
        }

        // ------------------------------------------------------------------------------------------
        // Structures

        static void BuildRuin(Placer place, Catalogue cat, Transform parent, Vector2 center, Vector2 towardPit, StringBuilder log)
        {
            var windowA = cat.Props("ruin", "window", "01").FirstOrDefault() ?? cat.Props("ruin", "window").FirstOrDefault();
            var windowB = cat.Props("ruin", "window", "04").FirstOrDefault() ?? windowA;
            var door = cat.Props("ruin", "door").FirstOrDefault();
            var wall = cat.Props("ruin", "wall", "straight").FirstOrDefault(p => p.name.IndexOf("chimney", StringComparison.OrdinalIgnoreCase) < 0);
            var chimney = cat.Props("chimney").FirstOrDefault();
            if (windowA == null || door == null || wall == null)
                return;
            // local frame: +x along the front wall, +z away from the pit (the front faces the pit)
            var back = -towardPit.normalized;
            var right = new Vector2(back.y, -back.x);
            float yaw = Mathf.Atan2(right.x, right.y) * Mathf.Rad2Deg - 90f;
            float ground = place.Ground(center.x, center.y);
            int pieces = 0;
            float Wall(Piece[] row, Vector2 start, Vector2 dir, float rowYaw)
            {
                float offset = 0f;
                foreach (var p in row)
                {
                    float width = p.bounds.size.x;
                    var at = start + dir * (offset + width * 0.5f);
                    place.PlaceUpright(p, parent, new Vector3(at.x, 0f, at.y), rowYaw, 1f, 0.35f, Mathf.Min(ground, place.Ground(at.x, at.y)));
                    offset += width - 0.35f;
                    pieces++;
                }
                return offset;
            }
            var front = new[] { wall, door, windowA };
            float frontWidth = front.Sum(p => p.bounds.size.x - 0.35f);
            var corner = center - right * (frontWidth * 0.5f) - back * 5f;
            Wall(front, corner, right, yaw);
            var sideDir = back;
            float side = Wall(new[] { windowB, wall }, corner, sideDir, yaw - 90f);
            float backWidth = Wall(new[] { windowA, wall, windowB }, corner + sideDir * side, right, yaw + 180f);
            if (chimney != null)
            {
                var at = corner + sideDir * side + right * (backWidth + chimney.bounds.size.x * 0.5f - 0.3f);
                place.PlaceUpright(chimney, parent, new Vector3(at.x, 0f, at.y), yaw + 180f, 1f, 0.4f, Mathf.Min(ground, place.Ground(at.x, at.y)));
                pieces++;
            }
            // rubble inside and around
            var rubble = cat.debris.Concat(cat.Props("broken")).ToList();
            for (int i = 0; i < 40 && rubble.Count > 0; ++i)
            {
                var at = center + right * place.R(-frontWidth * 0.5f, frontWidth * 0.5f) + back * place.R(-7f, side - 3f);
                var p = place.Pick(rubble);
                if (p.MaxExtent > 1.2f)
                    place.PlaceUpright(p, parent, new Vector3(at.x, 0f, at.y), place.R(0f, 360f), 1f, 0.15f);
                else
                    place.PlaceLoose(p, parent, at.x, at.y, place.R(0.8f, 1.5f), 0.25f);
                pieces++;
            }
            // an old boundary wall along the rim
            var castle = cat.Props("castle", "wall").Where(p => p.bounds.size.x > p.bounds.size.z).ToList();
            var along = new Vector2(-towardPit.y, towardPit.x);
            for (int i = 0; i < 16 && castle.Count > 0; ++i)
            {
                var p = castle[0];
                var at = center + towardPit * 16f + along * (i - 8) * (p.bounds.size.x - 0.1f);
                if (place.R(0f, 1f) < 0.2f)
                    continue; // gaps
                place.PlaceUpright(p, parent, new Vector3(at.x, 0f, at.y), yaw + place.R(-4f, 4f), 1f, 0.12f);
                pieces++;
            }
            log.AppendLine($"ruin: {pieces} pieces");
        }

        static void BuildCamp(Placer place, Catalogue cat, Transform parent, Vector2 center, Vector2 channelDir, StringBuilder log)
        {
            var right = new Vector2(channelDir.y, -channelDir.x);
            float baseYaw = Mathf.Atan2(channelDir.x, channelDir.y) * Mathf.Rad2Deg;
            int pieces = 0;
            Vector3 At(float x, float z)
            {
                var v = center + right * x + channelDir * z;
                return new Vector3(v.x, 0f, v.y);
            }
            GameObject Put(Piece p, float x, float z, float yaw, float onTop = float.NaN)
            {
                if (p == null)
                    return null;
                pieces++;
                return place.PlaceUpright(p, parent, At(x, z), baseYaw + yaw, 1f, 0.02f, onTop);
            }
            float Top(GameObject go) => go != null ? go.GetComponentsInChildren<Renderer>().Select(r => r.bounds.max.y).DefaultIfEmpty(0f).Max() : float.NaN;

            var benchTable = cat.Props("table").Where(p => p.name.IndexOf("metal", StringComparison.OrdinalIgnoreCase) < 0).OrderByDescending(p => p.MaxExtent).FirstOrDefault();
            var roundTable = cat.Props("table").Where(p => p.name.IndexOf("metal", StringComparison.OrdinalIgnoreCase) < 0).OrderBy(p => p.MaxExtent).FirstOrDefault();
            var metalTable = cat.Props("metal", "table").FirstOrDefault();
            var bench = Put(benchTable, 0f, 0f, 0f);
            var metal = Put(metalTable, 4.8f, 0.8f, 90f);
            var round = roundTable != benchTable ? Put(roundTable, -3.6f, 2.2f, 20f) : null;
            Put(cat.Props("chair").FirstOrDefault(), -0.9f, 1.1f, 180f);
            Put(cat.Props("chair").FirstOrDefault(), 1.1f, -1.0f, 15f);
            Put(cat.Props("stool").FirstOrDefault(), -3.3f, 3.3f, 40f);
            Put(cat.Props("pot").FirstOrDefault(), -3.6f, 2.2f, 0f, Top(round));
            Put(cat.Props("pumpkin").FirstOrDefault(), 0.7f, 0.05f, 0f, Top(bench));
            Put(cat.Props("cog").FirstOrDefault(), 4.6f, 1.2f, 30f, Top(metal));
            Put(cat.Props("lamp").FirstOrDefault(), 2.1f, 2.4f, 0f);
            var barrels = cat.Props("barrel");
            for (int i = 0; i < 5 && barrels.Count > 0; ++i)
                Put(barrels[i % barrels.Count], 6.5f + (i % 3) * 0.85f, -4f - (i / 3) * 0.9f, place.R(0f, 360f));
            Put(cat.Props("tank").FirstOrDefault(), 5.4f, -5.6f, 70f);
            var cans = cat.Props("trash");
            for (int i = 0; i < 2 && cans.Count > 0; ++i)
                Put(cans[i % cans.Count], -6.2f + i * 0.7f, -3.1f - i * 0.5f, place.R(0f, 360f));
            Put(cat.Props("wheelbarrow").FirstOrDefault(), -7.2f, 1.8f, 35f);
            var crate = cat.Props("crate").FirstOrDefault();
            if (crate != null)
            {
                var c0 = Put(crate, 6.8f, 1.6f, 5f);
                var c1 = Put(crate, 6.9f, 1.6f, 12f, Top(c0));
                Put(cat.Props("box").FirstOrDefault(), 7.4f, 2.4f, 40f);
            }
            var hay = cat.Props("hay").FirstOrDefault();
            if (hay != null)
            {
                Put(hay, -9.5f, -6f, 0f);
                Put(hay, -9.6f, -4.4f, 10f);
            }
            // stacks of slate slabs: the quarry's product
            var slabs = cat.patches.Where(p => p.MaxExtent > 0.9f).ToList();
            for (int s = 0; s < 4 && slabs.Count > 0; ++s)
            {
                var basePos = At(11f + (s % 2) * 3.2f, 2f + (s / 2) * 3.4f - 3f);
                float y = place.Ground(basePos.x, basePos.z);
                int layers = 5 + place.rng.Next(6);
                float stackYaw = baseYaw + place.R(-15f, 15f);
                for (int l = 0; l < layers; ++l)
                {
                    var p = place.Pick(slabs);
                    var rot = Quaternion.Euler(0f, stackYaw + place.R(-8f, 8f), 0f) * Quaternion.FromToRotation(p.Facing, Vector3.up);
                    float scale = Mathf.Clamp(1.5f / p.MaxExtent, 0.5f, 2f);
                    var b = TransformBounds(p.bounds, Matrix4x4.TRS(Vector3.zero, rot, Vector3.one * scale));
                    var pos = new Vector3(basePos.x + place.R(-0.12f, 0.12f) - b.center.x, y - b.min.y, basePos.z + place.R(-0.12f, 0.12f) - b.center.z);
                    place.Place(p, parent, pos, rot, scale);
                    y += b.size.y * 0.8f;
                    pieces++;
                }
            }
            // an old kerb line along the road
            var curb = cat.Props("curb").FirstOrDefault();
            for (int i = 0; i < 24 && curb != null; ++i)
            {
                if (place.R(0f, 1f) < 0.15f)
                    continue;
                Put(curb, -2f - Shape.ChannelHalfWidth * 0.5f, 8f + i * (curb.bounds.size.x - 0.02f), 90f + place.R(-3f, 3f));
            }
            log.AppendLine($"camp: {pieces} pieces");
        }

        static void BuildBarrier(Placer place, Catalogue cat, Transform parent, Shape shape, StringBuilder log)
        {
            var bags = cat.Props("sandbag").Where(p => p.name.IndexOf("barrier", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (bags.Count == 0)
                return;
            var d = shape.ChannelDir;
            var perp = new Vector2(-d.y, d.x);
            float along = shape.Rim(shape.channelAngle) - 6f + Shape.ChannelLength + 4f;
            var mid = d * along;
            float yaw = Mathf.Atan2(perp.x, perp.y) * Mathf.Rad2Deg - 90f;
            int pieces = 0;
            for (float x = -18f; x <= 18f;)
            {
                var p = place.Pick(bags);
                float width = p.bounds.size.x;
                if (Mathf.Abs(x + width * 0.5f) > 2.6f)
                {
                    var at = mid + perp * (x + width * 0.5f) + d * (0.08f * (x * x) / 18f);
                    float jitter = place.R(-6f, 6f);
                    place.PlaceUpright(p, parent, new Vector3(at.x, 0f, at.y), yaw + jitter, 1f, 0.08f);
                    pieces++;
                    if (p.Openness > 0.3f)
                    {
                        var back = at + d * (p.bounds.size.z * 0.8f);
                        place.PlaceUpright(p, parent, new Vector3(back.x, 0f, back.y), yaw + jitter + 180f, 1f, 0.08f);
                        pieces++;
                    }
                }
                x += width - 0.15f;
            }
            var walls = cat.Props("wall", "wood");
            for (int i = 0; i < 4 && walls.Count > 0; ++i)
            {
                var at = mid + perp * ((i < 2 ? -1f : 1f) * (6f + (i % 2) * 2.2f)) + d * 1.2f;
                place.PlaceUpright(walls[i % walls.Count], parent, new Vector3(at.x, 0f, at.y), yaw + place.R(-5f, 5f), 1f, 0.2f);
                pieces++;
            }
            foreach (var extra in cat.Props("sandbag").Where(p => p.name.IndexOf("barrier", StringComparison.OrdinalIgnoreCase) < 0))
            {
                var at = mid + perp * place.R(-12f, 12f) + d * place.R(2f, 4f);
                if (extra.Openness > 0.3f) // open scans (torn cloth) lie on the ground
                    place.PlaceAligned(extra, parent, new Vector3(at.x, place.Ground(at.x, at.y), at.y), place.Normal(at.x, at.y), 1f, 0.3f);
                else
                    place.PlaceUpright(extra, parent, new Vector3(at.x, 0f, at.y), place.R(0f, 360f), 1f, 0.05f);
                pieces++;
            }
            place.keepOut.Add(new Vector3(mid.x, mid.y, 6f));
            log.AppendLine($"barrier: {pieces} pieces");
        }

        // ------------------------------------------------------------------------------------------
        // Lighting, camera, flythrough

        static void SetupLighting(Options o, UnityEngine.SceneManagement.Scene scene, Shape shape)
        {
            var sun = Object.FindAnyObjectByType<Light>();
            if (sun != null)
            {
                sun.name = "Sun";
                sun.transform.rotation = Quaternion.Euler(44f, shape.channelAngle * -Mathf.Rad2Deg + 150f, 0f);
                sun.shadows = LightShadows.Soft;
            }
            string profilePath = $"{o.outputFolder}/{o.sceneName}_Volume.asset";
            AssetDatabase.DeleteAsset(profilePath);
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, profilePath);
            var env = profile.Add<VisualEnvironment>(true);
            env.skyType.Override((int)SkyType.PhysicallyBased);
            env.skyAmbientMode.Override(SkyAmbientMode.Dynamic);
            var sky = profile.Add<PhysicallyBasedSky>(true);
            sky.groundTint.Override(new Color(0.35f, 0.3f, 0.25f));
            var exposure = profile.Add<Exposure>(true);
            exposure.mode.Override(ExposureMode.Fixed);
            exposure.fixedExposure.Override(12.9f);
            var fog = profile.Add<Fog>(true);
            fog.enabled.Override(true);
            fog.meanFreePath.Override(900f);
            fog.baseHeight.Override(0f);
            fog.maximumHeight.Override(120f);
            var shadows = profile.Add<HDShadowSettings>(true);
            shadows.maxShadowDistance.Override(320f);
            var contact = profile.Add<ContactShadows>(true);
            contact.enable.Override(true);
            contact.length.Override(0.35f);
            var ao = profile.Add<ScreenSpaceAmbientOcclusion>(true);
            ao.intensity.Override(1.2f);
            foreach (var c in profile.components)
                AssetDatabase.AddObjectToAsset(c, profile);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            var volume = new GameObject("Sky and Fog Volume").AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 1f;
            volume.sharedProfile = profile;
        }

        static Camera SetupCamera(Shape shape, Placer place, Vector2 ruin, Vector2 camp)
        {
            var cam = Camera.main;
            cam.fieldOfView = 55f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 1500f;
            if (cam.GetComponent<SampleFlyCamera>() == null)
                cam.gameObject.AddComponent<SampleFlyCamera>();
            var hud = cam.GetComponent<SampleHud>();
            if (hud == null) // no ?? with components: the editor returns a fake null
                hud = cam.gameObject.AddComponent<SampleHud>();

            var d = shape.ChannelDir;
            var perp = new Vector2(-d.y, d.x);
            Vector3 P(Vector2 xz, float above)
            {
                return new Vector3(xz.x, place.Ground(xz.x, xz.y) + above, xz.y);
            }
            Quaternion Look(Vector3 from, Vector3 to) => Quaternion.LookRotation(to - from, Vector3.up);
            Vector2 Dir(float degrees)
            {
                float a = shape.channelAngle + degrees * Mathf.Deg2Rad;
                return new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            }
            float rim = shape.Rim(shape.channelAngle);
            var centre = P(Vector2.zero, 2f);

            // views for stills
            var views = new GameObject("Views").transform;
            void View(string name, Vector3 at, Vector3 target)
            {
                var v = new GameObject(name).transform;
                v.SetParent(views, false);
                v.SetPositionAndRotation(at, Look(at, target));
            }
            var overview = P(Dir(200f) * (shape.TopRadius(shape.channelAngle + Mathf.PI) + 30f), 26f);
            View("View 1 Overview", overview, centre + Vector3.down * 4f);
            var floorView = P(camp + d * -6f + perp * -10f, 2.2f);
            View("View 2 Camp", floorView, P(camp, 1f));
            var wallView = P(Dir(120f) * (shape.Rim(shape.channelAngle + 120f * Mathf.Deg2Rad) - 9f), 1.8f);
            View("View 3 Wall and scree", wallView, P(Dir(135f) * shape.RingBase(0, shape.channelAngle + 135f * Mathf.Deg2Rad), 5f));
            var ruinView = P(ruin + (ruin.normalized * 24f) + new Vector2(-ruin.normalized.y, ruin.normalized.x) * 10f, 3f);
            View("View 4 Ruin", ruinView, P(ruin, 3f));
            var channelView = P(d * (rim + Shape.ChannelLength + 14f), 2.5f);
            View("View 5 Channel", channelView, P(d * (rim - 10f), 2f));

            // flythrough
            var path = new GameObject("Flythrough").AddComponent<SampleCameraPath>();
            path.speed = 9f;
            path.target = cam;
            hud.flythrough = path;
            void Way(Vector3 at, Vector3 target)
            {
                var w = new GameObject($"Waypoint {path.transform.childCount}").transform;
                w.SetParent(path.transform, false);
                w.SetPositionAndRotation(at, Look(at, target));
            }
            var outside = P(d * (rim + Shape.ChannelLength + 60f), 38f);
            Way(outside, centre);
            Way(P(d * (rim + Shape.ChannelLength + 10f), 14f), P(d * (rim - 20f), 0f));
            Way(P(d * (rim + 40f), 6f), P(d * (rim - 30f), 0f));
            Way(P(d * (rim - 4f) + perp * 3f, 3.5f), P(camp, 1f));
            Way(P(camp + perp * -14f + d * -12f, 3f), P(Dir(160f) * rim, 4f));
            Way(P(Dir(150f) * (rim * 0.45f), 4f), P(Dir(185f) * rim, 6f));
            Way(P(Dir(185f) * (shape.Rim(shape.channelAngle + 185f * Mathf.Deg2Rad) - 10f), 3f), P(Dir(215f) * rim, 4f));
            Way(P(Dir(230f) * (shape.Rim(shape.channelAngle + 230f * Mathf.Deg2Rad) - 8f), 6f), P(Dir(260f) * rim, 10f));
            Way(P(Dir(262f) * (shape.RingBase(1, shape.channelAngle + 262f * Mathf.Deg2Rad) + 2f), 9f), P(Dir(290f) * rim, 14f));
            Way(P(Dir(295f) * (shape.TopRadius(shape.channelAngle + 295f * Mathf.Deg2Rad) + 6f), 10f), P(ruin, 4f));
            Way(P(ruin + ruin.normalized * -20f + new Vector2(-ruin.normalized.y, ruin.normalized.x) * 18f, 7f), P(ruin, 3f));
            Way(P(ruin + ruin.normalized * 25f + new Vector2(-ruin.normalized.y, ruin.normalized.x) * -6f, 9f), P(ruin, 2f));
            Way(overview + Vector3.up * 12f, centre);
            cam.transform.SetPositionAndRotation(overview, Look(overview, centre + Vector3.down * 4f));
            return cam;
        }
    }
}
