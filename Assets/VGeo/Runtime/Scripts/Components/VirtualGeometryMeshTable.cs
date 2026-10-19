using System;
using System.Collections.Generic;
using UnityEngine;

namespace VGeo
{
    /// <summary>
    /// Mesh -> built VirtualGeometryMesh, for converting renderers at runtime
    /// (<see cref="VirtualGeometryRuntimeConversion"/>): the scene keeps its regular MeshRenderers and
    /// LODGroups, and the table (built in the editor, Tools > VGeo > Build Mesh Table for Selection) makes
    /// the virtual geometry of their meshes part of the player where it is referenced.
    /// </summary>
    [CreateAssetMenu(menuName = "VGeo/Mesh Table", fileName = "VirtualGeometryMeshTable")]
    public sealed class VirtualGeometryMeshTable : ScriptableObject
    {
        [Serializable]
        public struct Entry
        {
            public Mesh mesh;
            public VirtualGeometryMesh virtualGeometry;
        }

        [SerializeField] List<Entry> m_Entries = new List<Entry>();

        Dictionary<Mesh, VirtualGeometryMesh> m_Lookup;

        public IReadOnlyList<Entry> Entries => m_Entries;

        public int Count => m_Entries.Count;

        /// <summary>The virtual geometry built from `mesh`, if the table has it.</summary>
        public bool TryGet(Mesh mesh, out VirtualGeometryMesh virtualGeometry)
        {
            virtualGeometry = null;
            if (mesh == null)
                return false;
            if (m_Lookup == null)
            {
                m_Lookup = new Dictionary<Mesh, VirtualGeometryMesh>(m_Entries.Count);
                foreach (var e in m_Entries)
                    if (e.mesh != null && e.virtualGeometry != null)
                        m_Lookup[e.mesh] = e.virtualGeometry;
            }
            return m_Lookup.TryGetValue(mesh, out virtualGeometry) && virtualGeometry != null && virtualGeometry.IsValid;
        }

        /// <summary>Replaces the entries (editor tooling).</summary>
        public void SetEntries(IEnumerable<Entry> entries)
        {
            m_Entries = new List<Entry>(entries);
            m_Lookup = null;
        }

        void OnValidate() => m_Lookup = null;
    }
}
