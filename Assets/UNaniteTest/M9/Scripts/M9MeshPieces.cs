using UnityEngine;

// M9 demo: the pieces of a fracture as regular meshes (same order as the VirtualGeometryFracture's
// pieces), for the "MeshRenderer" variant of the comparison videos.
public sealed class M9MeshPieces : ScriptableObject
{
    public Mesh[] pieces;
}
