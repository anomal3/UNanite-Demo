#ifndef UNANITE_VG_FORMAT_INCLUDED
#define UNANITE_VG_FORMAT_INCLUDED

// HLSL mirror of Native~/src/vg_format.h (see Documentation~/DataFormat.md) plus the runtime GPU
// tables built by VgWorld (Runtime/Scripts/Rendering/VgGpuTypes.cs). Keep all three in sync.

#define VG_INVALID 0xFFFFFFFFu
#define VG_CLUSTER_HEADER_SIZE 64
#define VG_PAGE_HEADER_SIZE 16
#define VG_MESH_HAS_TANGENTS 1u
#define VG_MESH_HAS_UV1 2u
#define VG_MESH_HAS_COLORS 4u
#define VG_MESH_HAS_EXTRA_UV 16u // M11: 12 halves after the colour: uv0.zw, uv1.zw, uv2.xyzw, uv3.xyzw
#define VG_FLT_MAX 3.402823466e+38

// ---- blob tables (uploaded verbatim, mesh-relative indices) ---------------------------------

struct VgGroup // 48 B
{
    float3 center;
    float radius;
    float error;
    uint depth;
    uint pageIndex;
    uint firstCluster;
    uint clusterCount;
    uint pageClusterOffset;
    uint flags;
    uint reserved;
};

struct VgNode // 32 B
{
    float3 center;
    float radius;
    float error;
    uint childOffset;
    uint childCount;
    int group;
};

// ---- runtime tables ------------------------------------------------------------------------

struct VgMeshGpu // 80 B, one per registered VirtualGeometryMesh
{
    uint groupBase;       // into VG_Groups
    uint nodeBase;        // into VG_Nodes
    uint pageBase;        // into VG_PageOffsets
    uint rootNodeCount;   // roots are nodes [nodeBase, nodeBase + rootNodeCount)
    int positionPrecision;
    uint uvPrecision;
    uint normalBits;
    uint meshFlags;
    float3 boundsCenter;  // mesh space
    float boundsRadius;
    uint materialCount;
    uint tangentAngleBits; // tangent = angle around the normal (+ sign bit), format v2
    float uvScaleX;        // M8: uv0 = position.xz * uvScale when VG_MESH_UV_FROM_XZ (terrain tiles)
    float uvScaleZ;
    int positionPrecisionXZ; // M8: quantisation of x and z (= positionPrecision unless a heightfield grid)
    uint3 pad;
};

float3 VgPositionStep(VgMeshGpu mesh)
{
    float y = exp2(-(float)mesh.positionPrecision);
    float xz = exp2(-(float)mesh.positionPrecisionXZ);
    return float3(xz, y, xz);
}

#define VG_MESH_UV_FROM_XZ (1u << 16) // runtime-only flag (VgWorld)
#define VG_MESH_HW_RASTER (1u << 17)  // runtime-only: never the software raster (terrain tiles, measured slower)

struct VgInstanceGpu // 192 B
{
    float4 localToWorld0; // rows of a 3x4 matrix
    float4 localToWorld1;
    float4 localToWorld2;
    float4 worldToLocal0;
    float4 worldToLocal1;
    float4 worldToLocal2;
    uint meshIndex;
    uint materialBase;    // into VG_InstanceBins: bin of material slot s = VG_InstanceBins[materialBase + s]
    uint flags;           // bit0 enabled, bit1 casts shadows, bit2 mirrored, bit3 occludable (M4), bit4 moving (M9)
    float maxScale;
    float4 worldSphere;   // world-space bounding sphere
    uint lodSelf;         // M8 HLOD: VG_LodSwitches record at which this instance replaces its children (~0: none)
    uint lodParent;       // M8 HLOD: record at which the parent replaces this instance (~0: none)
    uint lightmapIndex;   // M9: slice of the lightmap texture arrays (lightmapped bins only, VgResolveLightmap.hlsl)
    float errorScale;     // M11: LOD error multiplier (1 = the view's pixel error, 1/3 = three times coarser)
    float4 prevLocalToWorld0; // M9: rows of the transform of the previous rendered frame (motion vectors)
    float4 prevLocalToWorld1;
    float4 prevLocalToWorld2;
};

// M8 HLOD switch record: an instance is drawn iff its parent's record is not coarse enough and its
// own record is (children test the very same record as their parent: exact complements).
struct VgLodSwitch // 32 B
{
    float4 sphere;     // world space
    float error;
    float morphStart;  // M11 SpeedTree smooth LOD: error at which the LOD this record ends begins (0: no morph)
    float fadeStart;   // M11 crossfade band (LOD.fadeTransitionWidth): error at which the coarse side fades in (0: none)
    float fadeDuration; // M11 animated crossfade (LODGroup.animateCrossFading): seconds (0: none)
};

#define VG_INSTANCE_ENABLED 1u
#define VG_INSTANCE_SHADOWS 2u
#define VG_INSTANCE_MIRRORED 4u
#define VG_INSTANCE_MOVING 16u   // M9: prevLocalToWorld differs from localToWorld (object motion vectors)
#define VG_INSTANCE_DENSITY_LOD 32u // M11: thinned out with distance (terrain details, VgDensityLodScale); bits 16-31: its start (m, 0 = the view's)

// ---- cluster header decoding (from the page pool) ------------------------------------------
// ClusterHeader v2 (64 B): w0 cull sphere | w1 counts, bits, offsets, posMin.x |
// w2 posMin.yz, uv0Min | w3 uvBits, cone, group, refinedGroup (see DataFormat.md).

struct VgCluster
{
    float3 cullCenter;
    float cullRadius;
    uint cone;
    uint vertexCount;
    uint triangleCount;
    uint material;
    uint lodLevel;
    int3 posMin;
    uint3 posBits;
    uint indexBaseBits;
    uint indexDeltaBits;
    int2 uv0Min;
    uint4 uvBits; // u0x, u0y, u1x, u1y
    uint vertexStrideBits;
    uint vertexDataAddress; // absolute byte address in the page pool of the vertex bit stream
    uint indexDataAddress;  // absolute byte address of the index stream (anchor word first)
    uint group;
    uint refinedGroup;
};

void VgUnpackGeometry(inout VgCluster c, uint4 w1, uint2 posMinYZ, uint pageAddress)
{
    c.vertexCount = (w1.x & 127) + 1;
    c.triangleCount = ((w1.x >> 7) & 127) + 1;
    c.material = (w1.x >> 14) & 0xFF;
    c.lodLevel = (w1.x >> 22) & 63;
    c.posBits = uint3(w1.y & 31, (w1.y >> 5) & 31, (w1.y >> 10) & 31);
    c.indexBaseBits = (w1.y >> 15) & 7;
    c.indexDeltaBits = (w1.y >> 18) & 7;
    c.vertexStrideBits = (w1.y >> 21) & 511;
    c.vertexDataAddress = pageAddress + (w1.z & 0x7FFF) * 4;
    c.indexDataAddress = pageAddress + ((w1.z >> 15) & 0x7FFF) * 4;
    c.posMin = int3(asint(w1.w), asint(posMinYZ));
}

VgCluster VgLoadCluster(ByteAddressBuffer pool, uint pageAddress, uint clusterInPage)
{
    uint a = pageAddress + VG_PAGE_HEADER_SIZE + clusterInPage * VG_CLUSTER_HEADER_SIZE;
    uint4 w0 = pool.Load4(a + 0);
    uint4 w1 = pool.Load4(a + 16);
    uint4 w2 = pool.Load4(a + 32);
    uint4 w3 = pool.Load4(a + 48);

    VgCluster c = (VgCluster)0;
    c.cullCenter = asfloat(w0.xyz);
    c.cullRadius = asfloat(w0.w);
    VgUnpackGeometry(c, w1, w2.xy, pageAddress);
    c.uv0Min = asint(w2.zw);
    c.uvBits = uint4(w3.x & 31, (w3.x >> 5) & 31, (w3.x >> 10) & 31, (w3.x >> 15) & 31);
    c.cone = w3.y;
    c.group = w3.z;
    c.refinedGroup = w3.w;
    return c;
}

// Only what vertex pulling of positions and triangle decoding need (one Load4 + one Load2):
// counts, position and index encodings, data addresses. Other fields are left zero.
VgCluster VgLoadClusterGeometry(ByteAddressBuffer pool, uint pageAddress, uint clusterInPage)
{
    uint a = pageAddress + VG_PAGE_HEADER_SIZE + clusterInPage * VG_CLUSTER_HEADER_SIZE;
    uint4 w1 = pool.Load4(a + 16);
    uint2 posMinYZ = pool.Load2(a + 32);
    VgCluster c = (VgCluster)0;
    VgUnpackGeometry(c, w1, posMinYZ, pageAddress);
    return c;
}

// Reads `bits` (<= 32) starting `bitOffset` bits after the 4-byte aligned `address`.
uint VgReadBits(ByteAddressBuffer pool, uint address, uint bitOffset, uint bits)
{
    if (bits == 0)
        return 0;
    uint wordAddress = address + (bitOffset >> 5) * 4;
    uint shift = bitOffset & 31;
    uint2 w = pool.Load2(wordAddress); // second word may belong to the next stream: masked out
    uint lo = w.x >> shift;
    uint hi = shift ? (w.y << (32 - shift)) : 0;
    uint v = lo | hi;
    return bits >= 32 ? v : (v & ((1u << bits) - 1));
}

float3 VgOctDecode(uint2 q, uint bits)
{
    float maxv = (float)((1u << bits) - 1);
    float2 f = (float2)q / maxv * 2.0 - 1.0;
    float3 n = float3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    if (n.z < 0)
    {
        float2 s = float2(n.x >= 0 ? 1.0 : -1.0, n.y >= 0 ? 1.0 : -1.0);
        n.xy = (1.0 - abs(n.yx)) * s;
    }
    return normalize(n);
}

// Hemisphere of an octahedral code decided on the integers (|fx| + |fy| > 1), exactly as the
// builder does, so the tangent basis never flips between encoder and decoder.
bool VgOctLowerHemisphere(uint2 q, uint bits)
{
    int m = (int)((1u << bits) - 1);
    return abs(2 * (int)q.x - m) + abs(2 * (int)q.y - m) > m;
}

// Tangent stored as an angle around the normal in the orthonormal basis of Duff et al. 2017.
float3 VgDecodeTangent(float3 n, bool lower, uint angle, uint bits)
{
    float s = lower ? -1.0 : 1.0;
    float a = -1.0 / (s + n.z);
    float b = n.x * n.y * a;
    float3 b1 = float3(1.0 + s * n.x * n.x * a, s * b, -s * n.x);
    float3 b2 = float3(b, s + n.y * n.y * a, -n.y);
    float phi = (float)angle * (6.28318530718 / (float)(1u << bits));
    float sinPhi, cosPhi;
    sincos(phi, sinPhi, cosPhi);
    return normalize(cosPhi * b1 + sinPhi * b2);
}

struct VgVertex
{
    float3 position; // mesh space
    float3 normal;
    float4 tangent;
    float2 uv0;
    float2 uv1;
    uint color;      // RGBA8
    float4 uv0zw_uv1zw; // M11 extra UVs (VG_MESH_HAS_EXTRA_UV), else 0
    float4 uv2;
    float4 uv3;
};

VgVertex VgDecodeVertex(ByteAddressBuffer pool, VgCluster c, uint vertex, VgMeshGpu mesh)
{
    VgVertex v;
    uint o = vertex * c.vertexStrideBits;
    float3 posStep = VgPositionStep(mesh);
    float uvStep = exp2(-(float)mesh.uvPrecision);

    int3 q;
    q.x = c.posMin.x + (int)VgReadBits(pool, c.vertexDataAddress, o, c.posBits.x); o += c.posBits.x;
    q.y = c.posMin.y + (int)VgReadBits(pool, c.vertexDataAddress, o, c.posBits.y); o += c.posBits.y;
    q.z = c.posMin.z + (int)VgReadBits(pool, c.vertexDataAddress, o, c.posBits.z); o += c.posBits.z;
    v.position = (float3)q * posStep;

    uint2 on;
    on.x = VgReadBits(pool, c.vertexDataAddress, o, mesh.normalBits); o += mesh.normalBits;
    on.y = VgReadBits(pool, c.vertexDataAddress, o, mesh.normalBits); o += mesh.normalBits;
    v.normal = VgOctDecode(on, mesh.normalBits);

    v.tangent = float4(1, 0, 0, 1);
    if (mesh.meshFlags & VG_MESH_HAS_TANGENTS)
    {
        uint angle = VgReadBits(pool, c.vertexDataAddress, o, mesh.tangentAngleBits); o += mesh.tangentAngleBits;
        uint s = VgReadBits(pool, c.vertexDataAddress, o, 1); o += 1;
        v.tangent = float4(VgDecodeTangent(v.normal, VgOctLowerHemisphere(on, mesh.normalBits), angle, mesh.tangentAngleBits), s ? -1.0 : 1.0);
    }

    int2 u0;
    u0.x = c.uv0Min.x + (int)VgReadBits(pool, c.vertexDataAddress, o, c.uvBits.x); o += c.uvBits.x;
    u0.y = c.uv0Min.y + (int)VgReadBits(pool, c.vertexDataAddress, o, c.uvBits.y); o += c.uvBits.y;
    v.uv0 = (mesh.meshFlags & VG_MESH_UV_FROM_XZ) ? v.position.xz * float2(mesh.uvScaleX, mesh.uvScaleZ) : (float2)u0 * uvStep;

    v.uv1 = 0;
    if (mesh.meshFlags & VG_MESH_HAS_UV1)
    {
        // the cluster's uv1 minimum sits in the 8 bytes before its vertex stream
        int2 u1 = asint(pool.Load2(c.vertexDataAddress - 8));
        u1.x += (int)VgReadBits(pool, c.vertexDataAddress, o, c.uvBits.z); o += c.uvBits.z;
        u1.y += (int)VgReadBits(pool, c.vertexDataAddress, o, c.uvBits.w); o += c.uvBits.w;
        v.uv1 = (float2)u1 * uvStep;
    }

    v.color = 0xFFFFFFFFu;
    if (mesh.meshFlags & VG_MESH_HAS_COLORS)
    {
        uint lo = VgReadBits(pool, c.vertexDataAddress, o, 16); o += 16;
        uint hi = VgReadBits(pool, c.vertexDataAddress, o, 16); o += 16;
        v.color = lo | (hi << 16);
    }

    v.uv0zw_uv1zw = 0;
    v.uv2 = 0;
    v.uv3 = 0;
    if (mesh.meshFlags & VG_MESH_HAS_EXTRA_UV)
    {
        float f[12];
        [unroll] for (uint k = 0; k < 12; ++k)
        {
            f[k] = f16tof32(VgReadBits(pool, c.vertexDataAddress, o, 16));
            o += 16;
        }
        v.uv0zw_uv1zw = float4(f[0], f[1], f[2], f[3]);
        v.uv2 = float4(f[4], f[5], f[6], f[7]);
        v.uv3 = float4(f[8], f[9], f[10], f[11]);
    }
    return v;
}

// Position only (vertex pulling in the visibility-buffer raster).
float3 VgDecodePosition(ByteAddressBuffer pool, VgCluster c, uint vertex, VgMeshGpu mesh)
{
    uint o = vertex * c.vertexStrideBits;
    int3 q;
    q.x = c.posMin.x + (int)VgReadBits(pool, c.vertexDataAddress, o, c.posBits.x); o += c.posBits.x;
    q.y = c.posMin.y + (int)VgReadBits(pool, c.vertexDataAddress, o, c.posBits.y); o += c.posBits.y;
    q.z = c.posMin.z + (int)VgReadBits(pool, c.vertexDataAddress, o, c.posBits.z);
    return (float3)q * VgPositionStep(mesh);
}

// Index stream: a 7-bit anchor per 32 triangles in the first word, then per triangle
// (m - anchor) | (a - m - 1) << baseBits | (b - m - 1) << (baseBits + deltaBits): two loads.
uint3 VgDecodeTriangle(ByteAddressBuffer pool, VgCluster c, uint triIndex)
{
    uint anchors = pool.Load(c.indexDataAddress);
    uint recBits = c.indexBaseBits + 2 * c.indexDeltaBits;
    uint rec = VgReadBits(pool, c.indexDataAddress + 4, triIndex * recBits, recBits);
    uint m = ((anchors >> (7 * (triIndex >> 5))) & 127) + (rec & ((1u << c.indexBaseBits) - 1));
    uint deltaMask = (1u << c.indexDeltaBits) - 1;
    return uint3(m, m + 1 + ((rec >> c.indexBaseBits) & deltaMask), m + 1 + ((rec >> (c.indexBaseBits + c.indexDeltaBits)) & deltaMask));
}

// ---- M11 density LOD --------------------------------------------------------------------------
// Instances flagged VG_INSTANCE_DENSITY_LOD (terrain grass) are thinned out with the distance d from
// the owner camera: beyond `start` a fraction (start / d)^exponent survives, chosen by a stable hash
// of the instance's pivot, and the survivors grow by 1 / sqrt(fraction) (at most `max scale`) about
// their pivot so the covered area stays. An instance shrinks to nothing over the last `fade` part of
// the distance at which it vanishes (no popping). The scale applies to object-space positions: every
// path (culling bounds, expansion, pulled / programmable raster, resolve) uses the same function
// with the same constants (set per view for compute, per camera for the camera's passes).

float4 VG_DensityLod;       // x start distance (<= 0: off), y exponent, z max scale, w fade fraction
float4 VG_DensityLodOrigin; // xyz: world position of the camera the instances are thinned for

uint VgHashU(uint x)
{
    x ^= x >> 16;
    x *= 0x7feb352du;
    x ^= x >> 15;
    x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

// Scale of the instance's object space: 1 = untouched, 0 = thinned out.
float VgDensityLodScale(VgInstanceGpu inst)
{
    if ((inst.flags & VG_INSTANCE_DENSITY_LOD) == 0 || VG_DensityLod.x <= 0.0)
        return 1.0;
    float3 pivot = float3(inst.localToWorld0.w, inst.localToWorld1.w, inst.localToWorld2.w);
    float d = max(distance(pivot, VG_DensityLodOrigin.xyz), 1e-3);
    // M11: the instance's own start (flags bits 16-31, metres: e.g. where its grass shader starts fading), not before the view's
    float start = max(VG_DensityLod.x, (float)(inst.flags >> 16));
    float rank = ((VgHashU(asuint(pivot.x) ^ VgHashU(asuint(pivot.z))) >> 8) + 1u) * (1.0 / 16777216.0); // (0, 1]
    float vanish = start * exp2(log2(rank) * (-1.0 / VG_DensityLod.y)); // the fraction left at vanish is rank
    float fade = saturate((vanish - d) / (VG_DensityLod.w * vanish));
    float keep = min(1.0, exp2(log2(start / d) * VG_DensityLod.y));
    return fade * min(rsqrt(keep), VG_DensityLod.z);
}

// ---- instance transforms ---------------------------------------------------------------------

float3 VgTransformPoint(VgInstanceGpu inst, float3 p)
{
    if (inst.flags & VG_INSTANCE_DENSITY_LOD)
        p *= VgDensityLodScale(inst);
    return float3(dot(inst.localToWorld0.xyz, p) + inst.localToWorld0.w,
                  dot(inst.localToWorld1.xyz, p) + inst.localToWorld1.w,
                  dot(inst.localToWorld2.xyz, p) + inst.localToWorld2.w);
}

// M9: the point under the previous frame's transform (motion vectors)
float3 VgTransformPrevPoint(VgInstanceGpu inst, float3 p)
{
    if (inst.flags & VG_INSTANCE_DENSITY_LOD)
        p *= VgDensityLodScale(inst); // density LOD instances do not move: no scale motion either
    return float3(dot(inst.prevLocalToWorld0.xyz, p) + inst.prevLocalToWorld0.w,
                  dot(inst.prevLocalToWorld1.xyz, p) + inst.prevLocalToWorld1.w,
                  dot(inst.prevLocalToWorld2.xyz, p) + inst.prevLocalToWorld2.w);
}

float3 VgTransformDirection(VgInstanceGpu inst, float3 d)
{
    return float3(dot(inst.localToWorld0.xyz, d), dot(inst.localToWorld1.xyz, d), dot(inst.localToWorld2.xyz, d));
}

float3 VgTransformNormal(VgInstanceGpu inst, float3 n)
{
    // inverse transpose: columns of worldToLocal
    return normalize(n.x * inst.worldToLocal0.xyz + n.y * inst.worldToLocal1.xyz + n.z * inst.worldToLocal2.xyz);
}

// ---- visibility buffer -----------------------------------------------------------------------
// R32G32_UInt per pixel: x = ((visible cluster index << 7) | triangle) + 1 (0 = no virtual
// geometry), y = asuint(device depth) exactly as rasterised. The visible cluster index is absolute
// in VG_Visible (camera regions included), whose records are (instance, page address, cluster in
// page, material bin).

#define VG_BIN_RESOLVE 1u       // material bin is drawn by the visibility-buffer resolve
#define VG_BIN_DOUBLE_SIDED 2u  // rasterised without back-face culling
#define VG_BIN_SHADOW 4u        // M6: shadow pass writes plain depth -> vertex-pulled shadow raster
#define VG_BIN_PULLED 8u        // M10: drawn by a generated VG variant of its material (pulled vertices, no expansion)
#define VG_BIN_PROGRAMMABLE 16u // M11: visibility-buffer views rasterise it with the variant's VgVisBuffer pass (alpha clip / vertex graph)
#define VG_BIN_BARYCENTRICS 32u // M11: that pass writes hardware barycentrics (_VgBaryBuffer): the resolve skips the vertex graph's positions
#define VG_BIN_RASTER_MOTION 64u // M11: that pass also writes the motion of its moved vertices (_VgMotionBuffer)

uint VgPackVisibility(uint visibleIndex, uint triIndex)
{
    return ((visibleIndex << 7) | triIndex) + 1u;
}

void VgUnpackVisibility(uint packed, out uint visibleIndex, out uint triIndex)
{
    packed -= 1u;
    visibleIndex = packed >> 7;
    triIndex = packed & 127u;
}

#endif
