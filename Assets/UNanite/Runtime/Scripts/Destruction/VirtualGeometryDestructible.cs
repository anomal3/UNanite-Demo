using System.Collections.Generic;
using UnityEngine;

namespace UNanite
{
    /// <summary>
    /// M9: breaks a virtual geometry object into its pre-fractured pieces
    /// (<see cref="VirtualGeometryFracture"/>). Until <see cref="Break"/> the object is its intact
    /// <see cref="VirtualGeometryRenderer"/>; then the intact instance is disabled and every piece
    /// becomes a moving virtual geometry instance with a Rigidbody and a convex MeshCollider (object
    /// motion vectors, streaming and shadows as any VG instance). Nothing is built at runtime:
    /// breaking costs instance registration and physics setup only.
    /// </summary>
    [AddComponentMenu("UNanite/Virtual Geometry Destructible")]
    [RequireComponent(typeof(VirtualGeometryRenderer))]
    public sealed class VirtualGeometryDestructible : MonoBehaviour
    {
        [SerializeField] VirtualGeometryFracture m_Fracture;
        [Tooltip("Material of the cut faces (defaults to the renderer's first material).")]
        [SerializeField] Material m_InteriorMaterial;
        [Tooltip("kg per cubic metre (piece masses from their volumes).")]
        [SerializeField, Min(0.001f)] float m_Density = 2400f;
        [Tooltip("Break when a collision's impulse exceeds Break Impulse.")]
        [SerializeField] bool m_BreakOnCollision = true;
        [SerializeField, Min(0f)] float m_BreakImpulse = 200f;

        readonly List<GameObject> m_Pieces = new List<GameObject>();

        public VirtualGeometryFracture Fracture
        {
            get => m_Fracture;
            set => m_Fracture = value;
        }

        public Material InteriorMaterial
        {
            get => m_InteriorMaterial;
            set => m_InteriorMaterial = value;
        }

        /// <summary>Break when a collision's impulse exceeds <see cref="BreakImpulse"/>.</summary>
        public bool BreakOnCollision
        {
            get => m_BreakOnCollision;
            set => m_BreakOnCollision = value;
        }

        public float BreakImpulse
        {
            get => m_BreakImpulse;
            set => m_BreakImpulse = Mathf.Max(0f, value);
        }

        public bool IsBroken { get; private set; }
        public IReadOnlyList<GameObject> Pieces => m_Pieces;
        /// <summary>Main-thread milliseconds of the last Break (instance registration + physics setup).</summary>
        public double LastBreakMs { get; private set; }

        /// <summary>
        /// Replaces the object with its pieces; `impulse` (N·s) pushes them away from `worldPoint`
        /// (explosion falloff over `radius`, 0 = the object's size).
        /// </summary>
        public void Break(Vector3 worldPoint, float impulse, float radius = 0f)
        {
            if (IsBroken || m_Fracture == null || m_Fracture.Pieces.Length == 0)
                return;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var intact = GetComponent<VirtualGeometryRenderer>();
            var source = intact.SharedMaterials;
            int slots = m_Fracture.InteriorSlot + 1;
            var materials = new Material[slots];
            for (int s = 0; s < slots - 1; ++s)
                materials[s] = source.Length > 0 ? source[Mathf.Min(s, source.Length - 1)] : null;
            materials[slots - 1] = m_InteriorMaterial != null ? m_InteriorMaterial : (source.Length > 0 ? source[0] : null);

            var body = GetComponent<Rigidbody>();
            Vector3 velocity = body != null ? body.linearVelocity : Vector3.zero;
            Vector3 angular = body != null ? body.angularVelocity : Vector3.zero;
            var t = transform;
            Vector3 scale = t.lossyScale;
            float volumeScale = Mathf.Abs(scale.x * scale.y * scale.z);
            if (radius <= 0f)
                radius = intact.Mesh != null ? intact.Mesh.LocalBounds.extents.magnitude * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z)) * 2f : 5f;

            foreach (var p in m_Fracture.Pieces)
            {
                var go = new GameObject($"{name} {p.mesh.name}");
                go.layer = gameObject.layer;
                go.transform.SetPositionAndRotation(t.position, t.rotation);
                go.transform.localScale = scale;
                var r = go.AddComponent<VirtualGeometryRenderer>();
                r.ShadowCasting = intact.ShadowCasting;
                r.SharedMaterials = materials;
                r.Mesh = p.mesh;
                var collider = go.AddComponent<MeshCollider>();
                collider.sharedMesh = p.collider;
                collider.convex = true;
                var rb = go.AddComponent<Rigidbody>();
                rb.mass = Mathf.Max(0.01f, m_Density * p.volume * volumeScale);
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative; // small fast pieces: no tunnelling
                rb.linearVelocity = velocity + Vector3.Cross(angular, t.TransformPoint(p.center) - t.position);
                rb.angularVelocity = angular;
                if (impulse > 0f)
                    rb.AddExplosionForce(impulse, worldPoint, radius, 0.2f, ForceMode.Impulse);
                m_Pieces.Add(go);
            }

            intact.enabled = false;
            foreach (var c in GetComponents<Collider>())
                c.enabled = false;
            if (body != null)
                body.isKinematic = true;
            IsBroken = true;
            LastBreakMs = watch.Elapsed.TotalMilliseconds;
        }

        void OnCollisionEnter(Collision collision)
        {
            if (m_BreakOnCollision && !IsBroken && collision.impulse.magnitude >= m_BreakImpulse)
                Break(collision.GetContact(0).point, collision.impulse.magnitude * 0.25f);
        }

        void OnDestroy()
        {
            foreach (var p in m_Pieces)
                if (p != null)
                {
                    if (Application.isPlaying)
                        Destroy(p);
                    else
                        DestroyImmediate(p);
                }
            m_Pieces.Clear();
        }
    }
}
