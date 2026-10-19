#ifndef VGEO_VG_URP_RESOLVE_INCLUDED
#define VGEO_VG_URP_RESOLVE_INCLUDED

// URP material resolve (Hidden/VGeo/URP/*Resolve): the visibility-buffer pixels of a material bin are
// shaded by URP's own pass code of the source shader. For every pixel the visible triangle is
// re-intersected along the pixel's view ray (VgResolveCommon.hlsl), its vertex attributes are
// interpolated with perspective-correct barycentrics, handed to the pass's vertex function as a
// one-vertex "mesh" (the draw's BRG batch has identity object matrices, so object space is world
// space) and the resulting Varyings go to the pass's fragment function with the pixel's own
// SV_Position. Lighting, fog, shadows, SH, Forward+ / deferred, decals and every material keyword
// therefore work exactly as for a MeshRenderer with that material.
//
// Vertex stage: VgResolveVert (VgResolveCommon.hlsl), one quad per 8x8 tile of the bin's
// VG_TileList range (VgClassify.compute). SV_Depth is the rasterised depth stored in the visibility
// buffer, so the depth test against regular opaques (LEqual) is exact.
//
// Include after the URP pass file (LitGBufferPass.hlsl, LitForwardPass.hlsl, ...).

#include "VgResolveCommon.hlsl"

struct VgUrpSurface
{
    float3 positionWS;
    float3 normalWS;
    float4 tangentWS;   // xyz tangent, w sign (mirroring included)
    float2 uv0;
    float2 uv1;
    float4 color;
    float4 positionCS;  // the pixel's SV_Position: xy pixel centre, z rasterised device depth, w clip w
    float depth;        // SV_Depth
    bool keep;          // the pixel belongs to the draw's bin (else a derivative helper, clipped at the end)
    uint instanceID;
};

// The pixel's visible triangle of the draw's bin, interpolated along the pixel's view ray. Pixels of
// other bins borrow a 2x2-quad neighbour's triangle (evaluated along their own ray) so the implicit
// texture derivatives of the bin's pixels stay in their UV space; a quad without any pixel of the bin
// is discarded as a whole (the test is uniform within the quad).
VgUrpSurface VgUrpResolvePixel(VgResolveVaryings tile)
{
    int2 pixel = int2(tile.positionCS.xy);
    uint2 own = LOAD_TEXTURE2D_X(_VgVisBuffer, pixel).xy;
    uint packed = own.x;
    bool mine = VgInBin(packed, tile.bin);
    if (!mine)
    {
        packed = VgLoadVisibility(pixel ^ int2(1, 0));
        if (!VgInBin(packed, tile.bin))
            packed = VgLoadVisibility(pixel ^ int2(0, 1));
        if (!VgInBin(packed, tile.bin))
            packed = VgLoadVisibility(pixel ^ int2(1, 1));
        if (!VgInBin(packed, tile.bin))
            discard;
    }

    VgUrpSurface s = (VgUrpSurface)0;
    VgHit h;
    bool valid = VgIntersect(packed, tile.positionCS.xy, h);
    VgInstanceGpu inst = h.inst;
    float3 bary = h.bary;
    bool mirrored = (inst.flags & VG_INSTANCE_MIRRORED) != 0;

    s.positionWS = GetAbsolutePositionWS(h.positionRWS);
    s.normalWS = normalize(bary.x * VgTransformNormal(inst, h.v0.normal) + bary.y * VgTransformNormal(inst, h.v1.normal) + bary.z * VgTransformNormal(inst, h.v2.normal));
    float3 tangent = bary.x * normalize(VgTransformDirection(inst, h.v0.tangent.xyz)) +
                     bary.y * normalize(VgTransformDirection(inst, h.v1.tangent.xyz)) +
                     bary.z * normalize(VgTransformDirection(inst, h.v2.tangent.xyz));
    s.tangentWS = float4(normalize(tangent), (h.v0.tangent.w < 0 ? -1.0 : 1.0) * (mirrored ? -1.0 : 1.0));
    s.uv0 = bary.x * h.v0.uv0 + bary.y * h.v1.uv0 + bary.z * h.v2.uv0;
    s.uv1 = (h.meshFlags & VG_MESH_HAS_UV1) ? bary.x * h.v0.uv1 + bary.y * h.v1.uv1 + bary.z * h.v2.uv1 : s.uv0;
    s.color = bary.x * VgUnpackColor(h.v0.color) + bary.y * VgUnpackColor(h.v1.color) + bary.z * VgUnpackColor(h.v2.color);

    float4 positionCS = TransformWorldToHClip(s.positionWS);
    s.depth = asfloat(own.y);
    // the rasterised depth for the pixel's own triangle (exact, also what GBuffer depth targets get)
    s.positionCS = float4(tile.positionCS.xy, mine ? s.depth : positionCS.z / positionCS.w, positionCS.w);
    s.keep = mine && valid;
#if UNITY_ANY_INSTANCING_ENABLED
    s.instanceID = tile.instanceID;
#endif
    return s;
}

// End of every resolve fragment: pixels of other bins were derivative helpers only.
void VgUrpClip(VgUrpSurface s)
{
    if ((_VgResolveDebug & 1) == 0)
        clip(s.keep ? 1 : -1);
}

#if UNITY_ANY_INSTANCING_ENABLED
    #define VG_URP_SET_INSTANCE_ID(a, s) a.instanceID = s.instanceID
#else
    #define VG_URP_SET_INSTANCE_ID(a, s)
#endif

#endif
