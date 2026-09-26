using System;
using UnityEngine;

namespace UNanite
{
    /// <summary>Settings of a pre-fractured mesh (.vgfracture sidecar, M9).</summary>
    [Serializable]
    public sealed class VgFractureSettings
    {
        [Min(2)] public int pieces = 24;
        public int seed = 1;
        [Tooltip("Mesh-space point the pieces get smaller around (with Focus Weight > 0).")]
        public Vector3 focus;
        [Range(0f, 1f)] public float focusWeight;
        [Tooltip("UV scale of the interior (cut) faces, per mesh unit.")]
        public float capUvScale = 1f;
        [Tooltip("Triangles of each piece's convex collider source (a coarse cut of the piece).")]
        [Min(8)] public int colliderTriangles = 128;
    }

    /// <summary>
    /// A mesh pre-fractured into Voronoi pieces (M9): every piece is its own virtual geometry mesh
    /// (source sub-meshes + one interior sub-mesh for the cut faces, material slot
    /// <see cref="InteriorSlot"/>) with a convex collider mesh. Produced by the .vgfracture importer;
    /// used by <see cref="VirtualGeometryDestructible"/>.
    /// </summary>
    public sealed class VirtualGeometryFracture : ScriptableObject
    {
        [Serializable]
        public sealed class Piece
        {
            public VirtualGeometryMesh mesh;
            public Mesh collider;
            public Vector3 center; // mesh space (centroid)
            public float volume;   // mesh units cubed
        }

        [SerializeField] Piece[] m_Pieces = new Piece[0];
        [SerializeField] int m_InteriorSlot;
        [SerializeField] float m_SourceVolume;
        [SerializeField] double m_MsFracture, m_MsBuild;
        [SerializeField] int m_SourceTriangles, m_PieceTriangles;

        public Piece[] Pieces => m_Pieces;
        /// <summary>Material slot of the cut faces (= the source's sub-mesh count).</summary>
        public int InteriorSlot => m_InteriorSlot;
        public float SourceVolume => m_SourceVolume;
        public double MsFracture => m_MsFracture;
        public double MsBuild => m_MsBuild;
        public int SourceTriangles => m_SourceTriangles;
        public int PieceTriangles => m_PieceTriangles;

        public void Init(Piece[] pieces, int interiorSlot, float sourceVolume, int sourceTriangles, int pieceTriangles, double msFracture, double msBuild)
        {
            m_Pieces = pieces;
            m_InteriorSlot = interiorSlot;
            m_SourceVolume = sourceVolume;
            m_SourceTriangles = sourceTriangles;
            m_PieceTriangles = pieceTriangles;
            m_MsFracture = msFracture;
            m_MsBuild = msBuild;
        }
    }
}
