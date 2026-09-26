using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UNanite
{
    /// <summary>Timing of the last completed terrain edit (tests, stats overlay).</summary>
    public struct VgTerrainEditStats
    {
        public int edits;           // height edits folded into the rebuild
        public int nodesRebuilt;
        public double msBuild;      // worker: rebuilding the affected nodes
        public double msTotal;      // first edit -> new meshes swapped in (main thread)
        public long sourceTriangles;
        public double msSwapMain;     // main thread: new meshes + instance swap
        public double msApplyTerrain; // main thread, next frame: TerrainData.SetHeights (collider, heightmap texture)
    }

    // M8 runtime edits: heights are changed on a CPU copy, the affected leaves and their ancestors are
    // rebuilt on a worker thread (native builder), and the new meshes replace the old ones in place
    // (VgWorld registers them incrementally: every other page stays resident). The TerrainData
    // (collider, heightmap texture used for shading normals) is updated one frame after the swap,
    // i.e. in the first frame that renders the new geometry, so geometry, shading and physics agree.
    public sealed partial class VirtualGeometryTerrain
    {
        sealed class HeightEdit
        {
            public int x, z;
            public float[,] heights; // [z, x] like TerrainData.SetHeights
        }

        sealed class EditJob
        {
            public List<int> nodes;
            public List<HeightEdit> edits;
            public VgTerrainBuilder.NodeBuild[] results; // per job node
            public string error;
            public Task task;
            public readonly Stopwatch watch = Stopwatch.StartNew();
            public double msBuild;
        }

        VgTerrainSource m_Source;     // edited heights (main thread)
        VgTerrainSource m_JobSource;  // heights the worker reads (synced from m_Source at job start)
        RectInt m_Dirty;              // samples edited since the last job started (inclusive)
        bool m_HasDirty;
        readonly List<HeightEdit> m_PendingEdits = new List<HeightEdit>();
        readonly List<HeightEdit> m_ApplyNextFrame = new List<HeightEdit>();
        readonly List<VirtualGeometryMesh> m_RuntimeMeshes = new List<VirtualGeometryMesh>();
        readonly List<VirtualGeometryMesh> m_Retired = new List<VirtualGeometryMesh>();
        EditJob m_Job;
        Stopwatch m_FirstEdit;
        int m_EditsInJob;

        public bool EditInProgress => m_Job != null || m_HasDirty || m_ApplyNextFrame.Count > 0;
        public VgTerrainEditStats LastEditStats { get; private set; }

        /// <summary>
        /// Replaces heights like TerrainData.SetHeights (normalised values, [z, x]) starting at sample
        /// (xBase, zBase). The rebuilt geometry, the collider and the shading normals switch together
        /// a few frames later (rebuild time of the affected tiles).
        /// </summary>
        public void SetHeights(int xBase, int zBase, float[,] heights)
        {
            if (!IsRegistered || heights == null)
                return;
            EnsureSource();
            int res = m_Source.resolution;
            int h = heights.GetLength(0), w = heights.GetLength(1);
            int x0 = Mathf.Max(0, xBase), z0 = Mathf.Max(0, zBase);
            int x1 = Mathf.Min(res - 1, xBase + w - 1), z1 = Mathf.Min(res - 1, zBase + h - 1);
            if (x1 < x0 || z1 < z0)
                return;
            var clipped = new float[z1 - z0 + 1, x1 - x0 + 1];
            for (int z = z0; z <= z1; ++z)
                for (int x = x0; x <= x1; ++x)
                {
                    float v = Mathf.Clamp01(heights[z - zBase, x - xBase]);
                    clipped[z - z0, x - x0] = v;
                    m_Source.heights[z * res + x] = v;
                }
            m_PendingEdits.Add(new HeightEdit { x = x0, z = z0, heights = clipped });
            var rect = new RectInt(x0, z0, x1 - x0, z1 - z0);
            m_Dirty = m_HasDirty ? Union(m_Dirty, rect) : rect;
            m_HasDirty = true;
            m_FirstEdit ??= Stopwatch.StartNew();
            if (m_Job == null)
                StartJob();
        }

        /// <summary>Digs a bowl-shaped crater (world units: radius, depth below the surface at the centre) with a slight rim.</summary>
        public void ApplyCrater(Vector3 worldCenter, float radius, float depth)
        {
            if (!IsRegistered)
                return;
            EnsureSource();
            var size = m_Source.size;
            int res = m_Source.resolution;
            float sx = (worldCenter.x - m_Position.x) / size.x * (res - 1);
            float sz = (worldCenter.z - m_Position.z) / size.z * (res - 1);
            float rx = radius * 1.35f / size.x * (res - 1), rz = radius * 1.35f / size.z * (res - 1);
            int x0 = Mathf.FloorToInt(sx - rx), x1 = Mathf.CeilToInt(sx + rx);
            int z0 = Mathf.FloorToInt(sz - rz), z1 = Mathf.CeilToInt(sz + rz);
            x0 = Mathf.Clamp(x0, 0, res - 1); x1 = Mathf.Clamp(x1, 0, res - 1);
            z0 = Mathf.Clamp(z0, 0, res - 1); z1 = Mathf.Clamp(z1, 0, res - 1);
            if (x1 <= x0 || z1 <= z0)
                return;
            // depth below the surface at the centre (worldCenter.y is ignored)
            int ci = Mathf.Clamp(Mathf.RoundToInt(sx), 0, res - 1), cj = Mathf.Clamp(Mathf.RoundToInt(sz), 0, res - 1);
            float floor = m_Source.heights[cj * res + ci] - depth / size.y;
            var patch = new float[z1 - z0 + 1, x1 - x0 + 1];
            for (int z = z0; z <= z1; ++z)
                for (int x = x0; x <= x1; ++x)
                {
                    float dx = (x - sx) * size.x / (res - 1), dz = (z - sz) * size.z / (res - 1);
                    float r = Mathf.Sqrt(dx * dx + dz * dz) / radius;
                    float h = m_Source.heights[z * res + x];
                    if (r < 1f)
                    {
                        float bowl = floor + (depth / size.y) * r * r; // parabolic bowl from the crater floor
                        h = Mathf.Min(h, Mathf.Lerp(bowl, h, Mathf.Clamp01((r - 0.85f) / 0.15f)));
                    }
                    else if (r < 1.35f)
                        h += depth * 0.08f / size.y * Mathf.Sin((r - 1f) / 0.35f * Mathf.PI); // rim
                    patch[z - z0, x - x0] = h;
                }
            SetHeights(x0, z0, patch);
        }

        static RectInt Union(RectInt a, RectInt b)
        {
            int xMin = Math.Min(a.xMin, b.xMin), zMin = Math.Min(a.yMin, b.yMin);
            int xMax = Math.Max(a.xMax, b.xMax), zMax = Math.Max(a.yMax, b.yMax);
            return new RectInt(xMin, zMin, xMax - xMin, zMax - zMin);
        }

        void EnsureSource()
        {
            if (m_Source != null)
                return;
            m_Source = VgTerrainSource.FromTerrainData(Terrain.terrainData);
            m_JobSource = new VgTerrainSource
            {
                resolution = m_Source.resolution,
                size = m_Source.size,
                holes = m_Source.holes,
                heights = (float[])m_Source.heights.Clone(),
            };
        }

        void StartJob()
        {
            if (!m_HasDirty || m_Data == null)
                return;
            if (!VgNativeBuilder.EnsureLoaded())
            {
                Debug.LogError($"UNanite: terrain edit needs the native builder: {VgNativeBuilder.LoadError}", this);
                m_HasDirty = false;
                m_PendingEdits.Clear();
                return;
            }
            // the worker reads m_JobSource; every edit since the last job lies inside m_Dirty
            int res = m_Source.resolution;
            for (int z = m_Dirty.yMin; z <= m_Dirty.yMax; ++z)
                Array.Copy(m_Source.heights, z * res + m_Dirty.xMin, m_JobSource.heights, z * res + m_Dirty.xMin, m_Dirty.width + 1);

            var job = new EditJob
            {
                nodes = VgTerrainBuilder.AffectedNodes(m_Nodes, m_Dirty),
                edits = new List<HeightEdit>(m_PendingEdits),
            };
            m_PendingEdits.Clear();
            m_HasDirty = false;
            m_EditsInJob = job.edits.Count;
            job.results = new VgTerrainBuilder.NodeBuild[job.nodes.Count];

            // readers of children that are not rebuilt: every page (the cut may be below the roots);
            // resolved here because page files are found through main-thread APIs in the editor
            var rebuilt = new HashSet<int>(job.nodes);
            var readers = new Dictionary<int, VgMeshReader>();
            foreach (int i in job.nodes)
                for (int c = m_Nodes[i].firstChild; c >= 0 && c < m_Nodes[i].firstChild + m_Nodes[i].childCount; ++c)
                    if (!rebuilt.Contains(c) && m_Nodes[c].mesh != null)
                        readers[c] = m_Nodes[c].mesh.Reader;

            var nodes = (VgTerrainNode[])m_Nodes.Clone();
            var src = m_JobSource;
            var settings = m_Data.Settings.Clone();
            settings.threadsPerBuild = Math.Max(1, Environment.ProcessorCount - 2); // few nodes per level; leave the main and render threads room
            int precision = m_Data.PositionPrecision;
            job.task = Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                int maxLevel = 0;
                foreach (int i in job.nodes)
                    maxLevel = Math.Max(maxLevel, nodes[i].level);
                for (int level = 0; level <= maxLevel && job.error == null; ++level)
                {
                    var ids = new List<int>();
                    for (int k = 0; k < job.nodes.Count; ++k)
                        if (nodes[job.nodes[k]].level == level)
                            ids.Add(k);
                    Parallel.ForEach(ids, k =>
                    {
                        int i = job.nodes[k];
                        var children = new List<VgMeshReader>();
                        for (int c = nodes[i].firstChild; c >= 0 && c < nodes[i].firstChild + nodes[i].childCount; ++c)
                        {
                            lock (readers)
                                children.Add(readers[c]);
                        }
                        var r = VgTerrainBuilder.BuildNode(src, nodes, i, children, settings, precision);
                        if (r.error != null)
                        {
                            job.error = $"tile {i}: {r.error}";
                            return;
                        }
                        job.results[k] = r;
                        lock (readers)
                            readers[i] = new VgMeshReader(r.blob);
                    });
                    foreach (int k in ids)
                        if (job.results[k] != null)
                        {
                            nodes[job.nodes[k]].switchSphere = job.results[k].switchSphere;
                            nodes[job.nodes[k]].switchError = job.results[k].switchError;
                        }
                }
                job.msBuild = sw.Elapsed.TotalMilliseconds;
            });
            m_Job = job;
        }

        // Main thread, every frame: apply the TerrainData of the last swap, swap finished rebuilds in.
        void PollEdits()
        {
            if (m_Retired.Count > 0)
            {
                foreach (var mesh in m_Retired)
                    DestroyUnityObject(mesh);
                m_Retired.Clear();
            }
            if (m_ApplyNextFrame.Count > 0)
            {
                var applyWatch = Stopwatch.StartNew();
                var td = Terrain.terrainData;
                foreach (var e in m_ApplyNextFrame)
                    td.SetHeights(e.x, e.z, e.heights);
                m_ApplyNextFrame.Clear();
                // SetHeights may replace the heightmap texture the resolve reads normals from
                if (m_Resolve != null)
                    m_Resolve.SetTexture(s_Heightmap, td.heightmapTexture);
                var stats = LastEditStats;
                stats.msApplyTerrain = applyWatch.Elapsed.TotalMilliseconds;
                LastEditStats = stats;
            }
            if (m_Job == null || !m_Job.task.IsCompleted)
                return;

            var job = m_Job;
            m_Job = null;
            if (job.task.IsFaulted || job.error != null)
            {
                Debug.LogError($"UNanite: terrain edit rebuild failed: {job.error ?? job.task.Exception?.GetBaseException().Message}", this);
            }
            else
            {
                var swapWatch = Stopwatch.StartNew();
                SwapIn(job);
                LastEditStats = new VgTerrainEditStats
                {
                    edits = m_EditsInJob,
                    nodesRebuilt = job.nodes.Count,
                    msBuild = job.msBuild,
                    msTotal = m_FirstEdit != null ? m_FirstEdit.Elapsed.TotalMilliseconds : job.watch.Elapsed.TotalMilliseconds,
                    msSwapMain = swapWatch.Elapsed.TotalMilliseconds,
                };
                m_ApplyNextFrame.AddRange(job.edits);
            }
            m_FirstEdit = m_HasDirty ? Stopwatch.StartNew() : null;
            if (m_HasDirty)
                StartJob();
        }

        void SwapIn(EditJob job)
        {
            var materials = new[] { m_Fallback };
            bool shadows = m_ShadowCasting != UnityEngine.Rendering.ShadowCastingMode.Off;
            var localToWorld = Matrix4x4.Translate(m_Position);
            for (int k = 0; k < job.nodes.Count; ++k)
            {
                int i = job.nodes[k];
                var r = job.results[k];
                var mesh = ScriptableObject.CreateInstance<VirtualGeometryMesh>();
                mesh.name = m_Nodes[i].mesh != null ? m_Nodes[i].mesh.name : $"Terrain node {i}";
                mesh.hideFlags = HideFlags.DontSave;
                var report = VgNativeBuilder.ToReport(r.stats);
                mesh.Initialize(r.blob, mesh.name, 1, report);
                mesh.UvFromPositionXZ = m_Data.Nodes[i].mesh != null ? m_Data.Nodes[i].mesh.UvFromPositionXZ : new Vector2(1f / m_Source.size.x, 1f / m_Source.size.z);

                var old = m_Nodes[i].mesh;
                if (old != null && m_RuntimeMeshes.Remove(old))
                    m_Retired.Add(old);
                m_RuntimeMeshes.Add(mesh);
                m_Nodes[i].mesh = mesh;
                m_Nodes[i].switchSphere = r.switchSphere;
                m_Nodes[i].switchError = r.switchError;

                if (m_Handles[i] >= 0)
                    m_World.RemoveInstance(m_Handles[i]);
                m_Handles[i] = m_World.AddInstance(mesh, materials, localToWorld, shadows);
                float pixelError = Mathf.Max(0.25f, m_PixelError);
                if (m_Handles[i] >= 0 && pixelError != 1f)
                    m_World.SetInstancePixelError(m_Handles[i], pixelError);
                if (m_Switches[i] >= 0)
                    m_World.SetLodSwitch(m_Switches[i], WorldSphere(m_Nodes[i]), m_Nodes[i].switchError / pixelError);
            }
            foreach (int i in job.nodes)
            {
                int parent = m_Nodes[i].parent;
                m_World.SetInstanceLod(m_Handles[i], m_Switches[i], parent >= 0 ? m_Switches[parent] : -1);
            }
        }

        void CancelEdits()
        {
            if (m_Job != null)
            {
                try { m_Job.task.Wait(); }
                catch (AggregateException) { }
                m_Job = null;
            }
            // edits not rendered yet still reach the TerrainData, so physics and data stay consistent
            if (Terrain != null && Terrain.terrainData != null)
            {
                foreach (var e in m_ApplyNextFrame)
                    Terrain.terrainData.SetHeights(e.x, e.z, e.heights);
                foreach (var e in m_PendingEdits)
                    Terrain.terrainData.SetHeights(e.x, e.z, e.heights);
            }
            m_ApplyNextFrame.Clear();
            m_PendingEdits.Clear();
            m_HasDirty = false;
            m_Source = m_JobSource = null;
            m_FirstEdit = null;
        }

        void DestroyRuntimeMeshes()
        {
            foreach (var mesh in m_RuntimeMeshes)
                DestroyUnityObject(mesh);
            foreach (var mesh in m_Retired)
                DestroyUnityObject(mesh);
            m_RuntimeMeshes.Clear();
            m_Retired.Clear();
        }
    }
}
