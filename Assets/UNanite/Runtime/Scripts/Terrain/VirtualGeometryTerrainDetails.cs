using System;
using System.Collections.Generic;
using UnityEngine;

namespace UNanite
{
    /// <summary>
    /// M11: renders a Unity Terrain's detail meshes (grass, bushes) through virtual geometry. Like
    /// Unity's detail renderer, instances exist only in detail patches within the detail distance of
    /// the camera: patches are generated (TerrainData.ComputeDetailInstanceTransforms) and added as
    /// VG instances as the camera moves, a few per frame, and removed behind it. The materials'
    /// generated VG variants rasterise alpha-clipped, wind-animated cards into the visibility buffer
    /// (programmable raster) and shade each pixel once. While enabled, Unity's detail rendering is off
    /// (Terrain.detailObjectDistance = 0, restored on disable).
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Terrain))]
    [AddComponentMenu("UNanite/Virtual Geometry Terrain Details")]
    public sealed class VirtualGeometryTerrainDetails : MonoBehaviour
    {
        [Serializable]
        public struct Layer
        {
            public int layer;                 // detail prototype index
            public VirtualGeometryMesh mesh;  // built without simplification (foliage cards)
            public Material[] materials;
            public Matrix4x4 localToPrototype;
            public bool castShadows;
        }

        [SerializeField] Layer[] m_Layers = new Layer[0];
        [Tooltip("Detail distance Unity used before the conversion (the VG distance; restored when this component is disabled or removed).")]
        [SerializeField] float m_UnityDetailDistance = -1f;
        [Tooltip("Detail patches generated per frame (instances appear gradually as the camera moves).")]
        [SerializeField] int m_PatchesPerFrame = 16;
        [Tooltip("M11 density LOD: distant instances are thinned out and the survivors grow to keep the covered area (VirtualGeometrySettings.densityLod*).")]
        [SerializeField] bool m_DensityLod = true;
        [Tooltip("M11 density LOD: where in the distance range over which a detail's shader fades it out (Distance_Fade_Start .. End) thinning starts: 0 = where the fade starts (~3 ms faster on the Terrain Sample at 1080p, far heather and bushes sparser), 1 = where it has faded out (Unity's look: only instances the shader already made invisible are dropped).")]
        [Range(0f, 1f)] [SerializeField] float m_DensityLodFadePoint = 1f;

        Terrain m_Terrain;
        VgWorld m_World;
        readonly Dictionary<int, List<int>> m_Patches = new Dictionary<int, List<int>>(); // patch -> instance handles
        readonly List<int> m_Remove = new List<int>();

        public Layer[] Layers
        {
            get => m_Layers;
            set { m_Layers = value ?? new Layer[0]; Clear(); }
        }

        /// <summary>Live detail instances.</summary>
        public int InstanceCount { get; private set; }
        public int PatchCount => m_Patches.Count;
        /// <summary>M11: thin distant instances out (applies to patches generated from now on).</summary>
        public bool DensityLod { get => m_DensityLod; set => m_DensityLod = value; }
        /// <summary>M11: thinning start within each detail's shader fade range (0 start .. 1 end; patches generated from now on).</summary>
        public float DensityLodFadePoint { get => m_DensityLodFadePoint; set => m_DensityLodFadePoint = Mathf.Clamp01(value); }
        /// <summary>Camera the patches follow (default: Camera.main).</summary>
        public Camera TargetCamera { get; set; }

        Terrain Terrain => m_Terrain != null ? m_Terrain : m_Terrain = GetComponent<Terrain>();

        void OnEnable()
        {
            var terrain = Terrain;
            if (terrain == null || m_Layers.Length == 0)
                return;
            if (m_UnityDetailDistance < 0f)
                m_UnityDetailDistance = terrain.detailObjectDistance;
            terrain.detailObjectDistance = 0f;
            VgWorld.BeforeFrame += OnBeforeFrame;
        }

        void OnDisable()
        {
            VgWorld.BeforeFrame -= OnBeforeFrame;
            Clear();
            var terrain = Terrain;
            if (terrain != null && m_UnityDetailDistance >= 0f)
                terrain.detailObjectDistance = m_UnityDetailDistance;
        }

        /// <summary>Generates every patch in range now (measurements, captures).</summary>
        public void Warm(Camera camera)
        {
            TargetCamera = camera;
            UpdatePatches(int.MaxValue);
        }

        void Clear()
        {
            if (m_World != null && m_World == VgWorld.Instance)
                foreach (var list in m_Patches.Values)
                    foreach (int h in list)
                        m_World.RemoveInstance(h);
            m_Patches.Clear();
            InstanceCount = 0;
            m_World = null;
        }

        void OnBeforeFrame() => UpdatePatches(m_PatchesPerFrame);

        void UpdatePatches(int budget)
        {
            var terrain = Terrain;
            var camera = TargetCamera != null ? TargetCamera : Camera.main;
            if (terrain == null || terrain.terrainData == null || camera == null || m_Layers.Length == 0)
                return;
            if (m_World == null)
                m_World = VgWorld.GetOrCreate();
            if (m_World == null)
                return;
            var td = terrain.terrainData;
            int patches = td.detailPatchCount;
            if (patches <= 0)
                return;
            Vector3 origin = terrain.GetPosition(), size = td.size;
            float patchX = size.x / patches, patchZ = size.z / patches;
            float distance = Mathf.Max(0f, m_UnityDetailDistance);
            var cam = camera.transform.position;

            // remove patches out of range (+ half a patch of hysteresis)
            m_Remove.Clear();
            foreach (var kv in m_Patches)
                if (PatchDistance(kv.Key, patches, origin, patchX, patchZ, cam) > distance + 0.5f * Mathf.Max(patchX, patchZ))
                    m_Remove.Add(kv.Key);
            foreach (int key in m_Remove)
            {
                foreach (int h in m_Patches[key])
                    m_World.RemoveInstance(h);
                InstanceCount -= m_Patches[key].Count;
                m_Patches.Remove(key);
            }

            // add the nearest missing patches in range
            int x0 = Mathf.Clamp(Mathf.FloorToInt((cam.x - distance - origin.x) / patchX), 0, patches - 1);
            int x1 = Mathf.Clamp(Mathf.FloorToInt((cam.x + distance - origin.x) / patchX), 0, patches - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt((cam.z - distance - origin.z) / patchZ), 0, patches - 1);
            int z1 = Mathf.Clamp(Mathf.FloorToInt((cam.z + distance - origin.z) / patchZ), 0, patches - 1);
            if (cam.x + distance < origin.x || cam.x - distance > origin.x + size.x || cam.z + distance < origin.z || cam.z - distance > origin.z + size.z)
                return;
            var missing = new List<(float d, int key)>();
            for (int z = z0; z <= z1; ++z)
                for (int x = x0; x <= x1; ++x)
                {
                    int key = z * patches + x;
                    if (m_Patches.ContainsKey(key))
                        continue;
                    float d = PatchDistance(key, patches, origin, patchX, patchZ, cam);
                    if (d <= distance)
                        missing.Add((d, key));
                }
            missing.Sort((a, b) => a.d.CompareTo(b.d));
            for (int i = 0; i < missing.Count && i < budget; ++i)
                AddPatch(missing[i].key, patches, origin, td, terrain.detailObjectDensity);
        }

        // M11: density LOD of a detail starts inside the distance range where its shader fades it out (the
        // Terrain Sample's grass / heather / bush graphs: Distance_Fade_Start .. End, 40-100 .. 80-200 m),
        // never before the settings' start: thinning all details from 25 m left far heather and bushes
        // much sparser than Unity's
        float DensityLodStart(Material[] materials)
        {
            float start = 0f;
            if (materials != null)
                foreach (var m in materials)
                    if (m != null && m.HasProperty("Distance_Fade_Start") && m.HasProperty("Distance_Fade_End"))
                        start = Mathf.Max(start, Mathf.Lerp(m.GetFloat("Distance_Fade_Start"), m.GetFloat("Distance_Fade_End"), m_DensityLodFadePoint));
            return start;
        }

        static float PatchDistance(int key, int patches, Vector3 origin, float patchX, float patchZ, Vector3 cam)
        {
            int x = key % patches, z = key / patches;
            float minX = origin.x + x * patchX, minZ = origin.z + z * patchZ;
            float dx = Mathf.Max(0f, Mathf.Max(minX - cam.x, cam.x - (minX + patchX)));
            float dz = Mathf.Max(0f, Mathf.Max(minZ - cam.z, cam.z - (minZ + patchZ)));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        void AddPatch(int key, int patches, Vector3 origin, TerrainData td, float density)
        {
            var handles = new List<int>();
            int x = key % patches, z = key / patches;
            foreach (var layer in m_Layers)
            {
                if (layer.mesh == null || !layer.mesh.IsValid)
                    continue;
                float thinStart = DensityLodStart(layer.materials);
                var transforms = td.ComputeDetailInstanceTransforms(x, z, layer.layer, density, out _);
                foreach (var t in transforms)
                {
                    var trs = Matrix4x4.TRS(origin + new Vector3(t.posX, t.posY, t.posZ), Quaternion.Euler(0f, t.rotationY * Mathf.Rad2Deg, 0f),
                                            new Vector3(t.scaleXZ, t.scaleY, t.scaleXZ));
                    int h = m_World.AddInstance(layer.mesh, layer.materials, trs * layer.localToPrototype, layer.castShadows);
                    if (h < 0)
                        continue;
                    if (m_DensityLod)
                        m_World.SetInstanceDensityLod(h, true, thinStart);
                    handles.Add(h);
                }
            }
            m_Patches[key] = handles;
            InstanceCount += handles.Count;
        }
    }
}
