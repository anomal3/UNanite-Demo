using System.Collections.Generic;
using UnityEngine;

namespace UNanite
{
    /// <summary>
    /// M14: draws the LODs of a LODGroup as discrete virtual geometry LODs, the way
    /// VirtualGeometryTerrainTrees draws terrain trees (M11). Foliage needs it: alpha-tested cards are
    /// built without simplification (the cluster DAG would drop whole leaves), so the group's own LOD
    /// meshes stand in for the DAG. Every LOD renderer carries a VirtualGeometryRenderer; the LODGroup
    /// (disabled, virtual geometry draws) keeps the LODs, their screen-relative transition heights, the
    /// reference point and the size. One HLOD switch record per transition makes every view draw exactly
    /// one LOD, switched where Unity's LODGroup switches at 1080 lines; below the last transition the
    /// object is culled like Unity's. The scene converter adds it to LODGroups whose LOD0 is alpha-tested.
    /// The records follow the group when its renderers move (VgTransformTracker); after editing the
    /// LODGroup's LODs, disable and enable this component.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(LODGroup))]
    [AddComponentMenu("UNanite/Virtual Geometry LOD Group")]
    public sealed class VirtualGeometryLodGroup : MonoBehaviour
    {
        LODGroup m_Group;
        VgWorld m_World;
        float[] m_Transitions;
        readonly Dictionary<Renderer, int> m_LodOf = new Dictionary<Renderer, int>();
        readonly List<int> m_Switches = new List<int>(); // record l: leaving LOD l (after the last LOD: culled)
        readonly List<VirtualGeometryRenderer> m_Linked = new List<VirtualGeometryRenderer>();

        public LODGroup Group => m_Group != null ? m_Group : m_Group = GetComponent<LODGroup>();

        /// <summary>Renderers currently drawn as discrete LODs of this group.</summary>
        public int LinkedCount => m_Linked.Count;

        void OnEnable()
        {
            ReadLods();
            // renderers registered before this component was enabled (the others link themselves)
            foreach (var r in GetComponentsInChildren<VirtualGeometryRenderer>())
                if (r.IsRegistered)
                    Link(r);
        }

        void OnDisable()
        {
            // every LOD draws again, like the renderers of a disabled LODGroup
            if (m_World != null && m_World == VgWorld.Instance)
                foreach (var r in m_Linked)
                    if (r != null && r.IsRegistered)
                        m_World.SetInstanceLod(r.Handle, -1, -1);
            m_Linked.Clear();
            ReleaseSwitches();
        }

        void ReadLods()
        {
            m_LodOf.Clear();
            var lods = Group != null ? Group.GetLODs() : new LOD[0];
            m_Transitions = new float[lods.Length];
            for (int l = 0; l < lods.Length; ++l)
            {
                m_Transitions[l] = lods[l].screenRelativeTransitionHeight;
                if (lods[l].renderers != null)
                    foreach (var r in lods[l].renderers)
                        if (r != null && !m_LodOf.ContainsKey(r))
                            m_LodOf[r] = l;
            }
        }

        /// <summary>Called by a registered renderer below this group: draws it as the LOD its MeshRenderer has in the LODGroup.</summary>
        internal void Link(VirtualGeometryRenderer r)
        {
            if (!isActiveAndEnabled || r == null || !r.IsRegistered)
                return;
            if (m_Transitions == null)
                ReadLods();
            var source = r.GetComponent<MeshRenderer>();
            if (source == null || !m_LodOf.TryGetValue(source, out int lod))
                return; // not one of the group's LODs: drawn on its own
            var world = VgWorld.Instance;
            if (world != m_World)
            {
                // a new world (the old one's records and instances went with it): renderers link again
                m_World = world;
                m_Switches.Clear();
                m_Linked.Clear();
            }
            if (m_Switches.Count == 0)
                CreateSwitches();
            // drawn iff the record of its own LOD is coarse enough and the next one is not (VgWorld.Hlod.cs)
            int self = lod > 0 ? m_Switches[lod - 1] : -1;
            int parent = lod < m_Switches.Count ? m_Switches[lod] : -1;
            world.SetInstanceLod(r.Handle, self, parent);
            if (!m_Linked.Contains(r))
                m_Linked.Add(r);
        }

        internal void Unlink(VirtualGeometryRenderer r)
        {
            if (m_Linked.Remove(r) && m_Linked.Count == 0)
                ReleaseSwitches();
        }

        /// <summary>Moves the switch records with the group (a linked renderer's transform changed).</summary>
        internal void Refresh()
        {
            if (m_World == null || m_World != VgWorld.Instance)
                return;
            var sphere = Sphere();
            for (int l = 0; l < m_Switches.Count; ++l)
                m_World.SetLodSwitch(m_Switches[l], sphere, Error(l));
        }

        void CreateSwitches()
        {
            int count = m_Transitions.Length;
            if (count > 0 && m_Transitions[count - 1] <= 0f)
                --count; // a last LOD without a transition height is never culled (Unity)
            var sphere = Sphere();
            for (int l = 0; l < count; ++l)
                m_Switches.Add(m_World.AddLodSwitch(sphere, Error(l)));
        }

        void ReleaseSwitches()
        {
            if (m_World != null && m_World == VgWorld.Instance)
                foreach (int s in m_Switches)
                    m_World.RemoveLodSwitch(s);
            m_Switches.Clear();
            m_World = null;
        }

        Vector4 Sphere()
        {
            var c = transform.TransformPoint(Group.localReferencePoint);
            return new Vector4(c.x, c.y, c.z, 0f);
        }

        // leaving LOD l where Unity's LODGroup would at the reference screen height (VgCull.compute
        // ProjectedError; QualitySettings.lodBias like Unity), as VirtualGeometryTerrainTrees does
        float Error(int l)
        {
            var s = transform.lossyScale;
            float worldSize = Group.size * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
            float errorScale = QualitySettings.lodBias * VirtualGeometrySettings.Active.pixelError / VirtualGeometryTerrainTrees.ReferenceScreenHeight;
            return worldSize * errorScale / Mathf.Max(m_Transitions[l], 1e-4f);
        }
    }
}
