using System;
using UnityEngine;

namespace UNanite
{
    /// <summary>Build options of a virtual geometry terrain (serialised in the .vgterrain sidecar).</summary>
    [Serializable]
    public sealed class VgTerrainBuildSettings
    {
        [Tooltip("Quads per leaf tile side. Smaller tiles rebuild faster after runtime edits; larger tiles have fewer locked border vertices.")]
        public int tileQuads = 256;
        [Tooltip("Target clusters per group for simplification (4..24).")]
        public int groupSize = 16;
        [Range(6, 15)] public int normalBits = 8;
        [Range(0f, 2f)] public float normalWeight = 0.5f;
        [Tooltip("Keep hole outlines at full resolution in every LOD (otherwise they simplify like any mesh border).")]
        public bool lockHoleBorders = true;
        [Tooltip("An internal node is built from its children's cut with 1/hlodReduction of their triangles.")]
        public float hlodReduction = 4f;
        [Tooltip("Native threads per tile build (0 = all hardware threads).")]
        public int threadsPerBuild = 4;
        [Tooltip("Tiles built at the same time (0 = hardware threads / threadsPerBuild).")]
        public int concurrentBuilds = 0;
        [Tooltip("Keep the streamable pages of every tile in a side file of the import artifact (M7).")]
        public bool pageFile = true;

        public VgTerrainBuildSettings Clone() => (VgTerrainBuildSettings)MemberwiseClone();
    }

    /// <summary>
    /// One node of the terrain HLOD quadtree. Leaves are built from the heightmap, internal nodes
    /// from the root clusters of their children. Every node is an ordinary virtual geometry mesh in
    /// terrain-local space.
    /// </summary>
    [Serializable]
    public struct VgTerrainNode
    {
        public int level;        // 0 = leaf
        public RectInt quads;    // heightmap quads covered (x, z)
        public int parent;       // -1 = root
        public int firstChild;   // children are nodes [firstChild, firstChild + childCount)
        public int childCount;
        // Switch record (terrain-local): the node replaces its children when its source error (the
        // error of the children's cut it was built from) projects to at most the pixel threshold
        // over the whole sphere. Monotonic up the tree (see VgTerrainBuilder). Leaves: error 0.
        public Vector4 switchSphere;
        public float switchError;
        public VirtualGeometryMesh mesh;
    }

    /// <summary>
    /// A Unity terrain converted to virtual geometry (M8): the HLOD quadtree of tile meshes. Produced
    /// by the .vgterrain importer; rendered by <see cref="VirtualGeometryTerrain"/>.
    /// </summary>
    public sealed class VirtualGeometryTerrainData : ScriptableObject
    {
        [SerializeField] VgTerrainNode[] m_Nodes = Array.Empty<VgTerrainNode>();
        [SerializeField] int m_HeightmapResolution;
        [SerializeField] Vector3 m_Size;
        [SerializeField] int m_PositionPrecision;
        [SerializeField] VgTerrainBuildSettings m_Settings = new VgTerrainBuildSettings();
        [SerializeField] string m_SourceHash;
        [SerializeField] VgTerrainBuildStats m_Stats;

        public VgTerrainNode[] Nodes => m_Nodes;
        public int HeightmapResolution => m_HeightmapResolution;
        public Vector3 Size => m_Size;
        public int PositionPrecision => m_PositionPrecision;
        public VgTerrainBuildSettings Settings => m_Settings;
        public string SourceHash => m_SourceHash;
        public VgTerrainBuildStats Stats => m_Stats;
        public int RootIndex => m_Nodes.Length - 1; // nodes are ordered leaves first, root last

        public void Initialize(VgTerrainNode[] nodes, int resolution, Vector3 size, int precision, VgTerrainBuildSettings settings, string sourceHash, VgTerrainBuildStats stats)
        {
            m_Nodes = nodes;
            m_HeightmapResolution = resolution;
            m_Size = size;
            m_PositionPrecision = precision;
            m_Settings = settings.Clone();
            m_SourceHash = sourceHash;
            m_Stats = stats;
        }
    }

    [Serializable]
    public struct VgTerrainBuildStats
    {
        public int leaves;
        public int nodes;
        public int levels;
        public long sourceTriangles;
        public long storedTriangles;   // every DAG level of every node
        public long blobBytes;
        public long rootBytes;         // always-resident pages of all nodes
        public double msTotal;
    }
}
