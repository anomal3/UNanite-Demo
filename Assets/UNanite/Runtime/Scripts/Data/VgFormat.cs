using System.Runtime.InteropServices;

namespace UNanite
{
    // C# mirror of Native~/src/vg_format.h (and Runtime/ShaderLibrary/VgFormat.hlsl).
    // Layout, sizes and field order must match exactly; see Documentation~/DataFormat.md.
    public static class VgFormat
    {
        public const uint Magic = 0x47564E55u; // 'UNVG'
        public const uint Version = 2u;
        public const uint Invalid = 0xFFFFFFFFu;

        public const int MeshHeaderSize = 256;
        public const int GroupSize = 48;
        public const int NodeSize = 32;
        public const int PageSize = 32;
        public const int LevelSize = 16;
        public const int PageHeaderSize = 16;
        public const int ClusterHeaderSize = 64;

        public const uint MeshHasTangents = 1u << 0;
        public const uint MeshHasUv1 = 1u << 1;
        public const uint MeshHasColors = 1u << 2;
        public const uint MeshHasExtraUv = 1u << 4; // M11: 12 halves per vertex after the colour (uv0.zw, uv1.zw, uv2, uv3)
        public const uint MeshXZPrecision = 1u << 3; // M8: x/z use positionPrecisionXZ

        public const uint GroupTerminal = 1u << 0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public unsafe struct VgMeshHeader
    {
        public uint magic;
        public uint version;
        public uint headerSize;
        public uint meshFlags;

        public uint clusterCount;
        public uint groupCount;
        public uint nodeCount;
        public uint pageCount;

        public uint levelCount;
        public uint materialCount;
        public uint rootPageCount;
        public uint pageSize;

        public int positionPrecision;
        public uint uvPrecision;
        public uint normalBits;
        public uint maxClusterTriangles;

        public float aabbMinX, aabbMinY, aabbMinZ;
        public float lodRootError;
        public float aabbMaxX, aabbMaxY, aabbMaxZ;
        public float boundsRadius;

        public uint sourceTriangles;
        public uint sourceVertices;
        public uint totalTriangles;
        public uint totalVertices;

        public uint groupTableOffset;
        public uint nodeTableOffset;
        public uint pageTableOffset;
        public uint pageDepsOffset;

        public uint levelTableOffset;
        public uint pageDataOffset;
        public uint pageDepsCount;
        public uint blobSize;

        public uint rootNodeCount;
        public uint streamDataOffset; // blob offset of the first streamable page (= blobSize when every page is a root page)
        public uint tangentAngleBits;
        public int positionPrecisionXZ; // x/z quantisation when MeshXZPrecision (M8)
        public fixed uint reserved[24];

        /// <summary>Quantisation of x and z (equals positionPrecision unless MeshXZPrecision).</summary>
        public int PrecisionXZ => (meshFlags & VgFormat.MeshXZPrecision) != 0 ? positionPrecisionXZ : positionPrecision;

        public bool HasTangents => (meshFlags & VgFormat.MeshHasTangents) != 0;
        public bool HasUv1 => (meshFlags & VgFormat.MeshHasUv1) != 0;
        public bool HasColors => (meshFlags & VgFormat.MeshHasColors) != 0;
        public bool HasExtraUv => (meshFlags & VgFormat.MeshHasExtraUv) != 0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct VgGroup
    {
        public float centerX, centerY, centerZ;
        public float radius;
        public float error;        // float.MaxValue = never coarsen
        public uint depth;
        public uint pageIndex;
        public uint firstCluster;
        public uint clusterCount;
        public uint pageClusterOffset;
        public uint flags;
        public uint reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct VgNode
    {
        public float centerX, centerY, centerZ;
        public float radius;
        public float error;
        public uint childOffset;
        public uint childCount;
        public int group; // -1 for internal nodes
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct VgPage
    {
        public uint dataOffset;
        public uint dataSize;
        public uint firstGroup;
        public uint groupCount;
        public uint firstCluster;
        public uint clusterCount;
        public uint depsOffset;
        public uint depsCount;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct VgLevel
    {
        public uint triangles;
        public uint clusters;
        public uint groups;
        public uint reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct VgPageHeader
    {
        public uint clusterCount;
        public uint clusterHeaderOffset;
        public uint geometryOffset;
        public uint dataSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct VgClusterHeader
    {
        public float cullCenterX, cullCenterY, cullCenterZ;
        public float cullRadius;

        public uint counts;  // (vertexCount-1) | (triangleCount-1) << 7 | material << 14 | lodLevel << 22 | flags << 28
        public uint bits;    // bx | by << 5 | bz << 10 | index base bits << 15 | index delta bits << 18 | vertex stride bits << 21
        public uint offsets; // vertex data offset / 4 | index data offset / 4 << 15 (page-relative)
        public int posMinX, posMinY, posMinZ;

        public int uv0MinX, uv0MinY;
        public uint uvBits; // u0x | u0y << 5 | u1x << 10 | u1y << 15
        public uint cone;   // axis s8 xyz | cutoff s8 << 24
        public uint group;
        public uint refinedGroup; // VgFormat.Invalid for source clusters

        public int VertexCount => (int)(counts & 127) + 1;
        public int TriangleCount => (int)((counts >> 7) & 127) + 1;
        public int Material => (int)((counts >> 14) & 0xFF);
        public uint lodLevel => (counts >> 22) & 63;
        public int PosBitsX => (int)(bits & 31);
        public int PosBitsY => (int)((bits >> 5) & 31);
        public int PosBitsZ => (int)((bits >> 10) & 31);
        public int IndexBaseBits => (int)((bits >> 15) & 7);
        public int IndexDeltaBits => (int)((bits >> 18) & 7);
        public int VertexStrideBits => (int)((bits >> 21) & 511);
        public uint VertexDataOffset => (offsets & 0x7FFF) * 4;
        public uint IndexDataOffset => ((offsets >> 15) & 0x7FFF) * 4;
        public bool IsSourceCluster => refinedGroup == VgFormat.Invalid;
    }
}
