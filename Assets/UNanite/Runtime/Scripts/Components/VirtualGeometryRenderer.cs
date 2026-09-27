using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UNanite
{
    /// <summary>
    /// Renders a VirtualGeometryMesh through the GPU-driven pipeline. Static objects cost nothing per
    /// frame on the CPU; objects that are not marked static are tracked by a Burst job over a
    /// TransformAccessArray (VgTransformTracker, M9), so moving instances need no per-object Update.
    /// When virtual geometry is unsupported, a MeshRenderer on the same GameObject (kept disabled by
    /// the converter) is re-enabled as the fallback. Its baked lightmap (lightmapIndex,
    /// lightmapScaleOffset) lights the virtual geometry too (M9).
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("UNanite/Virtual Geometry Renderer")]
    public sealed class VirtualGeometryRenderer : MonoBehaviour
    {
        [SerializeField] VirtualGeometryMesh m_Mesh;
        [SerializeField] Material[] m_Materials = new Material[0];
        [SerializeField] ShadowCastingMode m_ShadowCasting = ShadowCastingMode.On;
        [Tooltip("M9: triangles of the proxy mesh put in the ray tracing acceleration structure (ray-traced shadows, reflections, GI, path tracing). 0 = no proxy.")]
        [SerializeField, Min(0)] int m_RayTracingProxyTriangles;
        [Tooltip("SpeedTree 8 wind of the converted tree (read from its Tree component at conversion); SpeedTree materials of the instance sway under the scene's WindZones like the terrain trees of VirtualGeometryTerrainTrees. Empty = static.")]
        [SerializeField] VgSpeedTreeWindParams m_SpeedTreeWind;

        int m_Handle = -1;
        int m_WindSlot = -1;
        VgWorld m_World;
        VirtualGeometryLodGroup m_LodGroup;


        public VirtualGeometryMesh Mesh
        {
            get => m_Mesh;
            set { m_Mesh = value; Reregister(); }
        }

        public Material[] SharedMaterials
        {
            get => m_Materials;
            set { m_Materials = value ?? new Material[0]; Reregister(); }
        }

        public ShadowCastingMode ShadowCasting
        {
            get => m_ShadowCasting;
            set { m_ShadowCasting = value; Reregister(); }
        }

        /// <summary>M9: triangle budget of the ray tracing proxy (0 = none).</summary>
        public int RayTracingProxyTriangles
        {
            get => m_RayTracingProxyTriangles;
            set { m_RayTracingProxyTriangles = Mathf.Max(0, value); Reregister(); }
        }

        /// <summary>SpeedTree 8 wind of this instance (null or invalid = static), e.g. VgSpeedTreeWindParams.FromRenderer of the source renderer.</summary>
        public VgSpeedTreeWindParams SpeedTreeWind
        {
            get => m_SpeedTreeWind;
            set { m_SpeedTreeWind = value; Reregister(); }
        }

        /// <summary>M9: the hidden ray tracing proxy renderer, or null (created at the next frame start;
        /// reading this creates a pending one right away).</summary>
        public MeshRenderer RayTracingProxy
        {
            get
            {
                if (m_Proxy == null && s_PendingProxies.Remove(this) && IsRegistered)
                    BuildProxy();
                return m_Proxy;
            }
        }

        public bool IsRegistered => m_Handle >= 0 && m_World != null && m_World == VgWorld.Instance;

        MeshRenderer m_Proxy;
        static readonly Dictionary<(VirtualGeometryMesh, int), Mesh> s_ProxyMeshes = new Dictionary<(VirtualGeometryMesh, int), Mesh>();
        // proxies wait for the next Update (player loop; editor update outside Play Mode): creating them
        // (SetParent) inside OnEnable / OnValidate sends "SendMessage cannot be called during Awake,
        // CheckConsistency, or OnValidate" warnings, and during rendering Unity refuses new renderers
        // ("Unable to add Renderer to the Scene after Culling")
        static readonly HashSet<VirtualGeometryRenderer> s_PendingProxies = new HashSet<VirtualGeometryRenderer>();
        static readonly List<VirtualGeometryRenderer> s_ProxyScratch = new List<VirtualGeometryRenderer>();
#if UNITY_EDITOR
        static bool s_ProxyHooked;
#endif

        void OnEnable()
        {
            Register();
        }

        void OnDisable()
        {
            Unregister();
        }

        void OnValidate()
        {
            if (isActiveAndEnabled)
                Reregister();
        }

        void Reregister()
        {
            if (!isActiveAndEnabled)
                return;
            Unregister();
            Register();
        }

        void Register()
        {
            if (m_Handle >= 0)
                return; // OnValidate can run before OnEnable while loading; never register twice
            var fallback = GetComponent<MeshRenderer>();
            if (m_Mesh == null || !m_Mesh.IsValid)
                return;

            m_World = VgWorld.GetOrCreate();
            if (m_World == null)
            {
                // unsupported platform/device: let the regular renderer draw
                if (fallback != null)
                    fallback.enabled = true;
                return;
            }
            if (fallback != null && fallback.enabled)
                fallback.enabled = false;

            // M9: baked lightmap of the converted MeshRenderer (kept on the GameObject, disabled)
            int lightmapIndex = fallback != null ? fallback.lightmapIndex : -1;
            Vector4 lightmapScaleOffset = fallback != null ? fallback.lightmapScaleOffset : Vector4.zero;
            m_WindSlot = m_SpeedTreeWind != null && m_SpeedTreeWind.IsValid ? m_World.AcquireSpeedTreeWind(m_SpeedTreeWind) : -1;
            m_Handle = m_World.AddInstance(m_Mesh, m_Materials, transform.localToWorldMatrix, m_ShadowCasting != ShadowCastingMode.Off,
                                           lightmapIndex, lightmapScaleOffset, m_WindSlot);
            transform.hasChanged = false;
            // M14: one LOD of a LODGroup drawn as discrete virtual geometry LODs (foliage)
            m_LodGroup = GetComponentInParent<VirtualGeometryLodGroup>();
            if (m_LodGroup != null)
                m_LodGroup.Link(this);
            CreateProxy();

            // static objects can still be moved while editing (M9: tracked by a Burst job, VgTransformTracker)
            if (!gameObject.isStatic || !Application.isPlaying)
                VgTransformTracker.Add(this);
        }

        void Unregister()
        {
            DestroyProxy();
            VgTransformTracker.Remove(this);
            if (m_LodGroup != null)
                m_LodGroup.Unlink(this);
            m_LodGroup = null;
            if (m_Handle >= 0 && m_World != null && m_World == VgWorld.Instance)
            {
                m_World.RemoveInstance(m_Handle);
                m_World.ReleaseSpeedTreeWind(m_WindSlot);
            }
            m_Handle = -1;
            m_WindSlot = -1;
            m_World = null;
        }

        // M9: a hidden child MeshRenderer with the DAG cut of `m_RayTracingProxyTriangles` triangles and the
        // renderer's materials. It follows the transform as a child, never rasterises (forceRenderingOff:
        // VG draws the object and its shadow maps) and is picked up by HDRP's ray tracing acceleration
        // structure like any renderer (ray tracing ignores the camera culling mask).
        void CreateProxy()
        {
            if (m_RayTracingProxyTriangles <= 0 || !SystemInfo.supportsRayTracing)
                return;
            s_PendingProxies.Add(this);
#if UNITY_EDITOR
            // the editor's update (edit and Play Mode); never PlayerLoop.SetPlayerLoop in the editor: a
            // loop modified in Edit Mode crashed Unity on entering Play Mode (RendererScene::NotifyInvisible)
            if (!s_ProxyHooked)
            {
                s_ProxyHooked = true;
                UnityEditor.EditorApplication.update += BuildPendingProxies;
            }
#endif
        }

        struct VgProxyUpdate { }

#if !UNITY_EDITOR
        // players: a player-loop step at the start of Update, inserted once at startup
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void InsertProxyUpdate()
        {
            var loop = UnityEngine.LowLevel.PlayerLoop.GetCurrentPlayerLoop();
            for (int i = 0; i < loop.subSystemList.Length; ++i)
            {
                if (loop.subSystemList[i].type != typeof(UnityEngine.PlayerLoop.Update))
                    continue;
                var update = loop.subSystemList[i];
                foreach (var s in update.subSystemList)
                    if (s.type == typeof(VgProxyUpdate))
                        return;
                var list = new List<UnityEngine.LowLevel.PlayerLoopSystem>(update.subSystemList);
                list.Insert(0, new UnityEngine.LowLevel.PlayerLoopSystem { type = typeof(VgProxyUpdate), updateDelegate = BuildPendingProxies });
                update.subSystemList = list.ToArray();
                loop.subSystemList[i] = update;
                UnityEngine.LowLevel.PlayerLoop.SetPlayerLoop(loop);
                return;
            }
        }
#endif

        static void BuildPendingProxies()
        {
            if (s_PendingProxies.Count == 0)
                return;
            s_ProxyScratch.AddRange(s_PendingProxies);
            s_PendingProxies.Clear();
            foreach (var r in s_ProxyScratch)
                if (r != null && r.m_Proxy == null && r.IsRegistered)
                    r.BuildProxy();
            s_ProxyScratch.Clear();
        }

        void BuildProxy()
        {
            var key = (m_Mesh, m_RayTracingProxyTriangles);
            if (!s_ProxyMeshes.TryGetValue(key, out var mesh) || mesh == null)
            {
                mesh = VgProxy.Extract(m_Mesh, m_RayTracingProxyTriangles, out _);
                mesh.hideFlags = HideFlags.HideAndDontSave;
                s_ProxyMeshes[key] = mesh;
            }
            var go = new GameObject("VG ray tracing proxy") { hideFlags = HideFlags.HideAndDontSave };
            go.layer = gameObject.layer;
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            m_Proxy = go.AddComponent<MeshRenderer>();
            m_Proxy.sharedMaterials = m_Materials;
            m_Proxy.shadowCastingMode = m_ShadowCasting;
            m_Proxy.forceRenderingOff = true;
            m_Proxy.rayTracingMode = UnityEngine.Experimental.Rendering.RayTracingMode.Static;
        }

        void DestroyProxy()
        {
            s_PendingProxies.Remove(this);
            if (m_Proxy == null)
                return;
            var go = m_Proxy.gameObject;
            m_Proxy = null;
            if (Application.isPlaying)
                Destroy(go);
            else
                DestroyImmediate(go);
        }

        internal int Handle => m_Handle;

        // VgTransformTracker moved the instance: the switch records of its LOD group follow
        internal void OnTransformApplied()
        {
            if (m_LodGroup != null)
                m_LodGroup.Refresh();
        }

        // the world was recreated (e.g. after all instances were removed): register again
        internal void ReregisterTracked()
        {
            m_Handle = -1;
            m_WindSlot = -1; // the old world's slots went with it
            m_World = null;
            Register();
        }

        void OnDrawGizmosSelected()
        {
            if (m_Mesh == null || !m_Mesh.IsValid)
                return;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.6f);
            var b = m_Mesh.LocalBounds;
            Gizmos.DrawWireCube(b.center, b.size);
        }
    }
}
