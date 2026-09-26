#ifndef UNANITE_VG_RESOLVE_SHADER_GRAPH_INCLUDED
#define UNANITE_VG_RESOLVE_SHADER_GRAPH_INCLUDED

// M10 visibility-buffer resolve of Shader Graphs (generated VG variants, keyword VG_RESOLVE; included
// by VgPulled.hlsl in the GBuffer and MotionVectors passes, whose own Vert / Frag are renamed away).
//
// Vertex stage: VgResolveVert (tiles of the bin, VgResolveCommon.hlsl).
// Pixel stage (GBuffer): the pixel's visible triangle is re-intersected along the view ray
// (VgIntersect: perspective-correct barycentrics), its three corners go through the pass's own
// VertMesh (the graph's vertex stage, custom interpolators, object matrices of the instance), the
// packed varyings are interpolated with the barycentrics (VgLerpPackedVaryings, generated per graph
// by VgShaderVariantGenerator from PackedVaryingsMeshToPS), unpacked by the graph's own
// UnpackVaryingsMeshToFragInputs and shaded by its GetSurfaceAndBuiltinData. Only graphs that do not
// move vertices use it (the visibility-buffer raster draws undisplaced positions).

VgResolveVaryings VgPulledVert(VgResolveAttributes input)
{
    return VgResolveVert(input);
}

#if SHADERPASS == SHADERPASS_GBUFFER

StructuredBuffer<uint> VG_BinFlags;
TYPED_TEXTURE2D_X(uint, _VgBaryBuffer); // M11: barycentrics of programmable bins (VgVisRaster.hlsl)

// Ray / triangle through camera-relative corners (as VgIntersect, for vertex-graph-moved corners).
bool VgIntersectCorners(float3 p0, float3 p1, float3 p2, float2 pixelCenter, inout VgHit h)
{
    float2 positionNDC = pixelCenter * _ScreenSize.zw;
#if UNITY_REVERSED_Z
    float3 rayOrigin = ComputeWorldSpacePosition(positionNDC, 1.0, UNITY_MATRIX_I_VP);
#else
    float3 rayOrigin = ComputeWorldSpacePosition(positionNDC, 0.0, UNITY_MATRIX_I_VP);
#endif
    h.rayDir = ComputeWorldSpacePosition(positionNDC, 0.5, UNITY_MATRIX_I_VP) - rayOrigin;
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
    h.bary = float3(1.0 - b1 - b2, b1, b2);
    h.positionRWS = rayOrigin + h.rayDir * (dot(h.e2, qv) * invDet);
    return true;
}

// Returns false for degenerate (edge-on) triangles.
bool VgShaderGraphFragInputs(uint packed, float2 pixelCenter, out FragInputs input, out float deviceDepth)
{
    ZERO_INITIALIZE(FragInputs, input);
    input.tangentToWorld = k_identity3x3;
    deviceDepth = 0;

    VgHit h;
    if (!VgIntersect(packed, pixelCenter, h))
        return false;
    VgMeshGpu mesh = VG_Meshes[h.inst.meshIndex];
    VaryingsMeshToPS m0 = VertMesh(VgAttributes(h.v0, mesh, h.instance));
    VaryingsMeshToPS m1 = VertMesh(VgAttributes(h.v1, mesh, h.instance));
    VaryingsMeshToPS m2 = VertMesh(VgAttributes(h.v2, mesh, h.instance));
#ifdef VARYINGS_NEED_POSITION_WS
    // M11: graphs that move vertices (wind) were rasterised with the moved corners (VgVisRaster.hlsl):
    // intersect the view ray with those
    if (!VgIntersectCorners(m0.positionRWS, m1.positionRWS, m2.positionRWS, pixelCenter, h))
        return false;
#endif
    input = UnpackVaryingsMeshToFragInputs(VgLerpPackedVaryings(PackVaryingsMeshToPS(m0), PackVaryingsMeshToPS(m1), PackVaryingsMeshToPS(m2), h.bary));

    float4 positionCS = TransformWorldToHClip(h.positionRWS);
    deviceDepth = positionCS.z / positionCS.w;
    input.positionSS = float4(pixelCenter, deviceDepth, positionCS.w);
    input.positionPixel = pixelCenter;
    input.positionRWS = h.positionRWS;
    bool mirrored = (h.inst.flags & VG_INSTANCE_MIRRORED) != 0;
    input.isFrontFace = (dot(cross(h.e1, h.e2), h.rayDir) < 0) != mirrored;
    input.primitiveID = h.triIndex;
    return true;
}

// M11 programmable bins with hardware barycentrics: the raster's barycentrics and depth give the
// pixel's attributes and position, so the corners' vertex graph only has to produce the attributes
// (the compiler drops the moved positions: wind, displacement) and nothing is intersected.
bool VgShaderGraphFragInputsBary(uint packed, uint baryBits, float rasterDepth, float2 pixelCenter, out FragInputs input, out float deviceDepth)
{
    ZERO_INITIALIZE(FragInputs, input);
    input.tangentToWorld = k_identity3x3;
    VgHit h;
    VgDecodeCorners(packed, h);
    VgMeshGpu mesh = VG_Meshes[h.inst.meshIndex];
    float b1 = (baryBits & 0x7FFFu) * (1.0 / 32767.0);
    float b2 = ((baryBits >> 15) & 0x7FFFu) * (1.0 / 32767.0);
    float3 bary = float3(1.0 - b1 - b2, b1, b2);
    if (h.inst.flags & VG_INSTANCE_MIRRORED)
        bary = bary.xzy; // Expand wrote mirrored triangles as (x, z, y)
    VaryingsMeshToPS m0 = VertMesh(VgAttributes(h.v0, mesh, h.instance));
    VaryingsMeshToPS m1 = VertMesh(VgAttributes(h.v1, mesh, h.instance));
    VaryingsMeshToPS m2 = VertMesh(VgAttributes(h.v2, mesh, h.instance));
    input = UnpackVaryingsMeshToFragInputs(VgLerpPackedVaryings(PackVaryingsMeshToPS(m0), PackVaryingsMeshToPS(m1), PackVaryingsMeshToPS(m2), bary));

    deviceDepth = rasterDepth;
    float3 positionRWS = ComputeWorldSpacePosition(pixelCenter * _ScreenSize.zw, deviceDepth, UNITY_MATRIX_I_VP);
    float4 positionCS = TransformWorldToHClip(positionRWS);
    input.positionSS = float4(pixelCenter, deviceDepth, positionCS.w);
    input.positionPixel = pixelCenter;
    input.positionRWS = positionRWS;
    input.isFrontFace = (baryBits & (1u << 30)) != 0;
    input.primitiveID = h.triIndex;
    return true;
}

void Frag(VgResolveVaryings packedInput, OUTPUT_GBUFFER(outGBuffer), out float outputDepth : SV_Depth)
{
    int2 pixel = int2(packedInput.positionCS.xy);
    uint2 own = LOAD_TEXTURE2D_X(_VgVisBuffer, pixel).xy;
    uint packed = own.x;
    int2 source = pixel;
    bool mine = VgInBin(packed, packedInput.bin);
    if (!mine)
    {
        // helper lane of the quad (see VgResolve.hlsl): evaluate a neighbour of this bin
        source = pixel ^ int2(1, 0);
        packed = VgLoadVisibility(source);
        if (!VgInBin(packed, packedInput.bin))
            packed = VgLoadVisibility(source = pixel ^ int2(0, 1));
        if (!VgInBin(packed, packedInput.bin))
            packed = VgLoadVisibility(source = pixel ^ int2(1, 1));
        if (!VgInBin(packed, packedInput.bin))
            discard;
    }

    FragInputs input;
    float deviceDepth;
    bool valid;
    if (VG_BinFlags[packedInput.bin] & VG_BIN_BARYCENTRICS)
    {
        // helper lanes take their neighbour's barycentrics and depth (derivatives are one-sided there)
        uint baryBits = LOAD_TEXTURE2D_X(_VgBaryBuffer, source).x;
        float rasterDepth = asfloat(LOAD_TEXTURE2D_X(_VgVisBuffer, source).y);
        valid = VgShaderGraphFragInputsBary(packed, baryBits, rasterDepth, float2(source) + 0.5, input, deviceDepth);
    }
    else
        valid = VgShaderGraphFragInputs(packed, packedInput.positionCS.xy, input, deviceDepth);

    PositionInputs posInput = GetPositionInput(input.positionSS.xy, _ScreenSize.zw, input.positionSS.z, input.positionSS.w, input.positionRWS);
    float3 V = GetWorldSpaceNormalizeViewDir(input.positionRWS);

    SurfaceData surfaceData;
    BuiltinData builtinData;
    GetSurfaceAndBuiltinData(input, V, posInput, surfaceData, builtinData);
    ENCODE_INTO_GBUFFER(surfaceData, builtinData, posInput.positionSS, outGBuffer);

    outputDepth = asfloat(own.y);
    if ((_VgResolveDebug & 1) == 0)
        clip(mine && valid ? 1 : -1);
}

#elif SHADERPASS == SHADERPASS_MOTION_VECTORS

#include "VgResolveMotion.hlsl"

void Frag(VgResolveVaryings packedInput, out float4 outMotionVector : SV_Target0, out float outputDepth : SV_Depth)
{
    VgMotionFrag(packedInput, outMotionVector, outputDepth);
}

#endif

#endif
