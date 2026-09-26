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

        int m_Handle = -1;
        VgWorld m_World;


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

        /// <summary>M9: the hidden ray tracing proxy renderer, or null.</summary>
        public MeshRenderer RayTracingProxy => m_Proxy;

        public bool IsRegistered => m_Handle >= 0 && m_World != null && m_World == VgWorld.Instance;

        MeshRenderer m_Proxy;
        static readonly Dictionary<(VirtualGeometryMesh, int), Mesh> s_ProxyMeshes = new Dictionary<(VirtualGeometryMesh, int), Mesh>();

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
            m_Handle = m_World.AddInstance(m_Mesh, m_Materials, transform.localToWorldMatrix, m_ShadowCasting != ShadowCastingMode.Off,
                                           lightmapIndex, lightmapScaleOffset);
            transform.hasChanged = false;
            CreateProxy();

            // static objects can still be moved while editing (M9: tracked by a Burst job, VgTransformTracker)
            if (!gameObject.isStatic || !Application.isPlaying)
                VgTransformTracker.Add(this);
        }

        void Unregister()
        {
            DestroyProxy();
            VgTransformTracker.Remove(this);
            if (m_Handle >= 0 && m_World != null && m_World == VgWorld.Instance)
                m_World.RemoveInstance(m_Handle);
            m_Handle = -1;
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

        // the world was recreated (e.g. after all instances were removed): register again
        internal void ReregisterTracked()
        {
            m_Handle = -1;
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
