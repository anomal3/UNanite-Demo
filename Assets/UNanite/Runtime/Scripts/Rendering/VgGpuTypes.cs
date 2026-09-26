using System.Runtime.InteropServices;
using UnityEngine;

namespace UNanite
{
    // Runtime GPU tables; mirrored by Runtime/ShaderLibrary/VgFormat.hlsl (VgMeshGpu, VgInstanceGpu).

    [StructLayout(LayoutKind.Sequential)]
    struct VgMeshGpu // 80 B
    {
        public uint groupBase;
        public uint nodeBase;
        public uint pageBase;
        public uint rootNodeCount;
        public int positionPrecision;
        public uint uvPrecision;
        public uint normalBits;
        public uint meshFlags;
        public Vector3 boundsCenter;
        public float boundsRadius;
        public uint materialCount;
        public uint tangentAngleBits;
        public float uvScaleX, uvScaleZ; // M8: uv0 = position.xz * scale when FlagUvFromXZ (terrain)
        public int positionPrecisionXZ;  // M8: quantisation of x and z (= positionPrecision for regular meshes)
        public uint pad0, pad1, pad2;

        public const int Stride = 80;
        public const uint FlagUvFromXZ = 1u << 16; // runtime-only mesh flag (VgFormat.hlsl VG_MESH_UV_FROM_XZ)
        public const uint FlagHardwareRaster = 1u << 17; // runtime-only: never the software raster (VG_MESH_HW_RASTER)
    }

    [StructLayout(LayoutKind.Sequential)]
    struct VgInstanceGpu // 192 B
    {
        public Vector4 localToWorld0;
        public Vector4 localToWorld1;
        public Vector4 localToWorld2;
        public Vector4 worldToLocal0;
        public Vector4 worldToLocal1;
        public Vector4 worldToLocal2;
        public uint meshIndex;
        public uint materialBase;
        public uint flags;
        public float maxScale;
        public Vector4 worldSphere;
        public uint lodSelf;    // M8 HLOD: switch record (VG_LodSwitches) this instance replaces its children at; ~0 = none
        public uint lodParent;  // M8 HLOD: switch record of the parent that replaces this instance; ~0 = none
        public uint lightmapIndex; // M9: slice of the lightmap texture arrays (lightmapped bins only)
        public float errorScale;   // M11: LOD error multiplier (1 = the view's pixel error)
        public Vector4 prevLocalToWorld0; // M9: transform of the previous rendered frame (motion vectors)
        public Vector4 prevLocalToWorld1;
        public Vector4 prevLocalToWorld2;

        public const int Stride = 192;
        public const uint FlagEnabled = 1u;
        public const uint FlagShadows = 2u;
        public const uint FlagMirrored = 4u;
        public const uint FlagOccludable = 8u; // M4: every material bin is resolve-capable (HZB-tested)
        public const uint FlagMoving = 16u;    // M9: prevLocalToWorld differs from localToWorld
        public const uint FlagDensityLod = 32u; // M11: thinned out with distance (VgFormat.hlsl VgDensityLodScale)

        /// <summary>M9: no motion: the previous transform is the current one.</summary>
        public void ResetPrevious()
        {
            prevLocalToWorld0 = localToWorld0;
            prevLocalToWorld1 = localToWorld1;
            prevLocalToWorld2 = localToWorld2;
            flags &= ~FlagMoving;
        }

        public void SetTransform(in Matrix4x4 m, in VgMeshGpu mesh)
        {
            localToWorld0 = m.GetRow(0);
            localToWorld1 = m.GetRow(1);
            localToWorld2 = m.GetRow(2);
            var inv = m.inverse;
            worldToLocal0 = inv.GetRow(0);
            worldToLocal1 = inv.GetRow(1);
            worldToLocal2 = inv.GetRow(2);
            maxScale = VgLodCut.MaxScale(m);
            var c = m.MultiplyPoint3x4(mesh.boundsCenter);
            worldSphere = new Vector4(c.x, c.y, c.z, mesh.boundsRadius * maxScale);
            if (m.determinant < 0f)
                flags |= FlagMirrored;
            else
                flags &= ~FlagMirrored;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct VgLodSwitchGpu // 32 B, M8 HLOD switch record
    {
        public Vector4 sphere; // world space
        public float error;    // world units; the owner is drawn instead of its children when it projects to <= 1 threshold unit
        public float morphStart; // M11 SpeedTree smooth LOD: error at which the LOD this record ends begins (0: no morph)
        public float fadeStart;  // M11 crossfade band (LOD.fadeTransitionWidth): error at which the coarse side fades in (0: none)
        public float fadeDuration; // M11 animated crossfade (LODGroup.animateCrossFading): seconds (0: none)

        public const int Stride = 32;
    }

    // Packed float3x4 as expected by DOTS instancing (column-major, 12 floats).
    [StructLayout(LayoutKind.Sequential)]
    struct PackedMatrix
    {
        public float c0x, c0y, c0z, c1x, c1y, c1z, c2x, c2y, c2z, c3x, c3y, c3z;

        public PackedMatrix(Matrix4x4 m)
        {
            c0x = m.m00; c0y = m.m10; c0z = m.m20;
            c1x = m.m01; c1y = m.m11; c1z = m.m21;
            c2x = m.m02; c2y = m.m12; c2z = m.m22;
            c3x = m.m03; c3y = m.m13; c3z = m.m23;
        }
    }
}
