#ifndef UNANITE_VG_RESOLVE_INCLUDED
#define UNANITE_VG_RESOLVE_INCLUDED

// M3 material resolve: entry points for the GBuffer pass of VG resolve shaders
// (Hidden/UNanite/LitResolve). Included after the material's surface code (LitData.hlsl), i.e.
// after GetSurfaceAndBuiltinData is defined, in place of ShaderPassGBuffer.hlsl.
//
// Vertex stage: one quad per 8x8 tile of the bin's VG_TileList range. The bin is the draw's
//   BRG visibleOffset (GetDOTSIndirectVisibleIndex), see VgClassify.compute.
// Pixel stage: visibility buffer -> cluster/triangle -> 3 decoded vertices -> view-ray/triangle
//   intersection (perspective-correct barycentrics and position) -> FragInputs -> the material's
//   own GetSurfaceAndBuiltinData -> GBuffer. SV_Depth is the rasterised depth stored in the
//   visibility buffer, so the depth test against regular opaques is exact.

#include "VgResolveCommon.hlsl"

// Fills FragInputs for `packed` evaluated along the view ray of `pixelCenter`. Returns false for
// degenerate (edge-on) triangles.
bool VgBuildFragInputs(uint packed, float2 pixelCenter, out FragInputs input, out float deviceDepth)
{
    ZERO_INITIALIZE(FragInputs, input);
    input.tangentToWorld = k_identity3x3;
    deviceDepth = 0;

    VgHit h;
    if (!VgIntersect(packed, pixelCenter, h))
        return false;
    VgInstanceGpu inst = h.inst;
    VgVertex v0 = h.v0, v1 = h.v1, v2 = h.v2;
    float3 bary = h.bary, e1 = h.e1, e2 = h.e2, rayDir = h.rayDir, positionRWS = h.positionRWS;
    uint triIndex = h.triIndex;

    bool mirrored = (inst.flags & VG_INSTANCE_MIRRORED) != 0;
    float3 n = bary.x * VgTransformNormal(inst, v0.normal) + bary.y * VgTransformNormal(inst, v1.normal) + bary.z * VgTransformNormal(inst, v2.normal);
    float3 tg = bary.x * normalize(VgTransformDirection(inst, v0.tangent.xyz)) +
                bary.y * normalize(VgTransformDirection(inst, v1.tangent.xyz)) +
                bary.z * normalize(VgTransformDirection(inst, v2.tangent.xyz));
    float tangentSign = (v0.tangent.w < 0 ? -1.0 : 1.0) * (mirrored ? -1.0 : 1.0);

    float4 positionCS = TransformWorldToHClip(positionRWS);
    deviceDepth = positionCS.z / positionCS.w;

    input.positionSS = float4(pixelCenter, deviceDepth, positionCS.w);
    input.positionRWS = positionRWS;
    input.positionPredisplacementRWS = positionRWS;
    input.positionPixel = pixelCenter;
    input.tangentToWorld = BuildTangentToWorld(float4(tg, tangentSign), n);
    input.texCoord0 = float4(bary.x * v0.uv0 + bary.y * v1.uv0 + bary.z * v2.uv0, 0, 0);
    input.texCoord1 = float4(bary.x * v0.uv1 + bary.y * v1.uv1 + bary.z * v2.uv1, 0, 0);
#ifdef VG_RESOLVE_LIGHTMAP
    // lightmap of the resolved instance (VgResolveLightmap.hlsl); like Unity, meshes without
    // lightmap UVs are baked with uv0
    g_VgLightmapST = VG_InstanceLightmaps[h.instance];
    g_VgLightmapIndex = float4(inst.lightmapIndex, 0, 0, 0);
    if ((h.meshFlags & VG_MESH_HAS_UV1) == 0)
        input.texCoord1 = input.texCoord0;
#endif
    input.color = bary.x * VgUnpackColor(v0.color) + bary.y * VgUnpackColor(v1.color) + bary.z * VgUnpackColor(v2.color);
    // Unity front faces: cross(b - a, c - a) points towards the viewer; mirroring flips it
    input.isFrontFace = (dot(cross(e1, e2), rayDir) < 0) != mirrored;
    input.primitiveID = triIndex;
    return true;
}

void VgResolveFrag(VgResolveVaryings packedInput, OUTPUT_GBUFFER(outGBuffer), out float outputDepth : SV_Depth)
{
    UNITY_SETUP_INSTANCE_ID(packedInput);

    int2 pixel = int2(packedInput.positionCS.xy);
    uint2 own = LOAD_TEXTURE2D_X(_VgVisBuffer, pixel).xy;
    uint packed = own.x;
    bool mine = VgInBin(packed, packedInput.bin);
    if (!mine)
    {
        // Not this bin's pixel (empty or another material): a helper only. Evaluate a quad
        // neighbour of this bin along this pixel's ray, so the implicit texture derivatives of the
        // neighbours stay in their own UV space (another bin's triangle can have unrelated UVs:
        // neighbouring terrains jump from u = 1 to u = 0 -> lowest mip, dark 2x2 quads). A quad
        // without any pixel of this bin is discarded as a whole (uniform within the quad).
        packed = VgLoadVisibility(pixel ^ int2(1, 0));
        if (!VgInBin(packed, packedInput.bin))
            packed = VgLoadVisibility(pixel ^ int2(0, 1));
        if (!VgInBin(packed, packedInput.bin))
            packed = VgLoadVisibility(pixel ^ int2(1, 1));
        if (!VgInBin(packed, packedInput.bin))
            discard;
    }

    FragInputs input;
    float deviceDepth;
    bool valid = VgBuildFragInputs(packed, packedInput.positionCS.xy, input, deviceDepth);

    PositionInputs posInput = GetPositionInput(input.positionSS.xy, _ScreenSize.zw, input.positionSS.z, input.positionSS.w, input.positionRWS);
    float3 V = GetWorldSpaceNormalizeViewDir(input.positionRWS);

    SurfaceData surfaceData;
    BuiltinData builtinData;
    GetSurfaceAndBuiltinData(input, V, posInput, surfaceData, builtinData);

    if (_VgResolveDebug & 2)
        surfaceData.baseColor = frac(float3(packedInput.tile * 0.61803, packedInput.tile * 0.41421, packedInput.bin * 0.5 + 0.25));

    ENCODE_INTO_GBUFFER(surfaceData, builtinData, posInput.positionSS, outGBuffer);

    outputDepth = asfloat(own.y);
    if ((_VgResolveDebug & 1) == 0)
        clip(mine && valid ? 1 : -1);
}

#endif
