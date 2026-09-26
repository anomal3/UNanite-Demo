#ifndef UNANITE_VG_RESOLVE_COMMON_INCLUDED
#define UNANITE_VG_RESOLVE_COMMON_INCLUDED

// Shared by the visibility-buffer passes drawn over classified tiles (VgClassify.compute):
// the GBuffer resolve (VgResolve.hlsl) and the motion-vector pass (VgResolveMotion.hlsl, M9).
// Vertex stage: one quad per 8x8 tile of the bin's VG_TileList range; the bin is the draw's BRG
// visibleOffset. VgIntersect re-intersects the pixel's view ray with its visible triangle.

#include "VgFormat.hlsl"

StructuredBuffer<VgMeshGpu> VG_Meshes;
StructuredBuffer<VgInstanceGpu> VG_Instances;
StructuredBuffer<uint4> VG_Visible;
StructuredBuffer<uint2> VG_TileList;
StructuredBuffer<uint> VG_BinTileOffset;
ByteAddressBuffer VG_PagePool;
TYPED_TEXTURE2D_X(uint2, _VgVisBuffer);
uint _VgResolveDebug; // bit0: no per-pixel rejection, bit1: colour by tile

struct VgResolveAttributes
{
    uint vertexID : SV_VertexID;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct VgResolveVaryings
{
    float4 positionCS : SV_POSITION;
    nointerpolation uint bin : TEXCOORD0;
    nointerpolation uint tile : TEXCOORD1;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

VgResolveVaryings VgResolveVert(VgResolveAttributes input)
{
    VgResolveVaryings output;
#if defined(UNITY_DOTS_INSTANCING_ENABLED)
    // explicit (not UNITY_SETUP_INSTANCE_ID, which Shader Graph resolve variants redirect to a VG
    // instance, VgPulledSetup.hlsl): the draw's visible offset is the bin
    UnitySetupInstanceID(UNITY_GET_INSTANCE_ID(input));
    SetupDOTSVisibleInstancingData();
#else
    UNITY_SETUP_INSTANCE_ID(input);
#endif
    UNITY_TRANSFER_INSTANCE_ID(input, output);

#ifdef DOTS_INSTANCING_ON
    uint bin = GetDOTSIndirectVisibleIndex(); // VgWorld: visibleOffset of the bin's resolve draw
#else
    uint bin = 0;
#endif
    uint2 entry = VG_TileList[VG_BinTileOffset[bin] + input.vertexID / 6];
    uint corner = input.vertexID % 6;
    // two triangles: (0,0) (1,0) (0,1) | (0,1) (1,0) (1,1)
    uint2 k = uint2(corner == 1 || corner == 4 || corner == 5 ? 1 : 0, corner == 2 || corner == 3 || corner == 5 ? 1 : 0);
    float2 pixel = float2(uint2(entry.x & 0xFFFF, entry.x >> 16) * 8 + k * 8);
    float2 ndc = pixel * _ScreenSize.zw * 2.0 - 1.0;
#if UNITY_UV_STARTS_AT_TOP
    ndc.y = -ndc.y;
#endif
    output.positionCS = float4(ndc, UNITY_NEAR_CLIP_VALUE, 1.0);
    output.bin = bin;
    output.tile = entry.x;
    return output;
}

uint VgLoadVisibility(int2 pixel)
{
    return LOAD_TEXTURE2D_X(_VgVisBuffer, pixel).x;
}

bool VgInBin(uint packed, uint bin)
{
    if (packed == 0)
        return false;
    uint record, triIndex;
    VgUnpackVisibility(packed, record, triIndex);
    return VG_Visible[record].w == bin;
}

float4 VgUnpackColor(uint c)
{
    return float4(c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF, c >> 24) / 255.0;
}

// A visible triangle re-intersected along a pixel's view ray (perspective-correct barycentrics).
struct VgHit
{
    VgInstanceGpu inst;
    VgVertex v0, v1, v2;     // mesh space
    float3 bary;
    float3 e1, e2;           // camera-relative world-space edges (p1 - p0, p2 - p0)
    float3 rayDir;
    float3 positionRWS;      // camera-relative world-space hit
    uint triIndex;
    uint instance;           // VG_Instances index
    uint meshFlags;
};

// The visible triangle's instance and corners (mesh space), in the cluster's own order.
void VgDecodeCorners(uint packed, out VgHit h)
{
    ZERO_INITIALIZE(VgHit, h);
    uint record, triIndex;
    VgUnpackVisibility(packed, record, triIndex);
    uint4 vis = VG_Visible[record];
    h.instance = vis.x;
    h.inst = VG_Instances[vis.x];
    VgMeshGpu mesh = VG_Meshes[h.inst.meshIndex];
    h.meshFlags = mesh.meshFlags;
    VgCluster c = VgLoadCluster(VG_PagePool, vis.y, vis.z);
    uint3 t = VgDecodeTriangle(VG_PagePool, c, triIndex);
    h.v0 = VgDecodeVertex(VG_PagePool, c, t.x, mesh);
    h.v1 = VgDecodeVertex(VG_PagePool, c, t.y, mesh);
    h.v2 = VgDecodeVertex(VG_PagePool, c, t.z, mesh);
    h.triIndex = triIndex;
}

// Returns false for degenerate (edge-on) triangles.
bool VgIntersect(uint packed, float2 pixelCenter, out VgHit h)
{
    VgDecodeCorners(packed, h);

    float3 p0 = GetCameraRelativePositionWS(VgTransformPoint(h.inst, h.v0.position));
    float3 p1 = GetCameraRelativePositionWS(VgTransformPoint(h.inst, h.v1.position));
    float3 p2 = GetCameraRelativePositionWS(VgTransformPoint(h.inst, h.v2.position));

    // view ray through the pixel centre (camera-relative world space; also valid for ortho)
    float2 positionNDC = pixelCenter * _ScreenSize.zw;
#if UNITY_REVERSED_Z
    float3 rayOrigin = ComputeWorldSpacePosition(positionNDC, 1.0, UNITY_MATRIX_I_VP);
#else
    float3 rayOrigin = ComputeWorldSpacePosition(positionNDC, 0.0, UNITY_MATRIX_I_VP);
#endif
    h.rayDir = ComputeWorldSpacePosition(positionNDC, 0.5, UNITY_MATRIX_I_VP) - rayOrigin;

    // Moeller-Trumbore without culling: barycentrics of the ray/plane intersection
    h.e1 = p1 - p0;
    h.e2 = p2 - p0;
    float3 pv = cross(h.rayDir, h.e2);
    float det = dot(h.e1, pv);
    if (abs(det) < 1e-20)
        return false;
    float invDet = 1.0 / det;
    float3 tv = rayOrigin - p0;
    float b1 = dot(tv, pv) * invDet;
    float3 qv = cross(tv, h.e1);
    float b2 = dot(h.rayDir, qv) * invDet;
    float hit = dot(h.e2, qv) * invDet;
    h.bary = float3(1.0 - b1 - b2, b1, b2);
    h.positionRWS = rayOrigin + h.rayDir * hit;
    return true;
}

#endif
