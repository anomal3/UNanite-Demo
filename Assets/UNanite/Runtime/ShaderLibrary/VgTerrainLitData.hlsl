#ifndef UNANITE_VG_TERRAIN_LIT_DATA_INCLUDED
#define UNANITE_VG_TERRAIN_LIT_DATA_INCLUDED

// M8: surface evaluation of virtual geometry terrains for the resolve (Hidden/UNanite/
// TerrainLitResolve). The layer blending is HDRP's own TerrainLit_Splatmap.hlsl; this file replaces
// TerrainLitData.hlsl, whose per-pixel-normal path depends on Unity's instanced terrain draws
// (UNITY_INSTANCING_ENABLED, _TerrainHeightmapRecipSize in a non-UnityPerMaterial cbuffer).
// Adapted from HDRP 17.4 TerrainLitData.hlsl (GetSurfaceAndBuiltinData with
// ENABLE_TERRAIN_PERPIXEL_NORMAL): keep in sync when HDRP changes.
//
// Inputs from the resolve: FragInputs.texCoord0 = terrain UV in [0, 1] (VG_MESH_UV_FROM_XZ).

#define SURFACE_GRADIENT

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Sampling/SampleUVMapping.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/MaterialUtilities.hlsl"

TEXTURE2D(_VgTerrainHeightmap); // TerrainData.heightmapTexture: height / 2 in R (UnpackHeightmap)
SAMPLER(sampler_VgTerrainHeightmap);

// We don't use emission for terrain
#define _EmissiveColor float3(0,0,0)
#define _AlbedoAffectEmissive 0
#define _EmissiveExposureWeight 0
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/LitBuiltinData.hlsl"
#undef _EmissiveColor
#undef _AlbedoAffectEmissive
#undef _EmissiveExposureWeight

#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Decal/DecalUtilities.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/LitDecalData.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/TerrainLit/TerrainLitSurfaceData.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/TerrainLit/TerrainLit_Splatmap.hlsl"

// Central-difference normal of the heightmap at terrain UV `uv` (bilinear taps one sample apart),
// the same formula the tile builder uses for vertex normals (VgTerrainSource.Normal).
float3 VgTerrainNormal(float2 uv)
{
    float res = _VgTerrainParams.x;
    float texel = _VgTerrainParams.y;
    float2 tc = (uv * (res - 1.0) + 0.5) * texel;
    float hl = UnpackHeightmap(SAMPLE_TEXTURE2D_LOD(_VgTerrainHeightmap, sampler_VgTerrainHeightmap, tc - float2(texel, 0), 0));
    float hr = UnpackHeightmap(SAMPLE_TEXTURE2D_LOD(_VgTerrainHeightmap, sampler_VgTerrainHeightmap, tc + float2(texel, 0), 0));
    float hd = UnpackHeightmap(SAMPLE_TEXTURE2D_LOD(_VgTerrainHeightmap, sampler_VgTerrainHeightmap, tc - float2(0, texel), 0));
    float hu = UnpackHeightmap(SAMPLE_TEXTURE2D_LOD(_VgTerrainHeightmap, sampler_VgTerrainHeightmap, tc + float2(0, texel), 0));
    float dx = (hr - hl) * _VgTerrainParams.z / (2.0 * _VgTerrainSpacing.x);
    float dz = (hu - hd) * _VgTerrainParams.z / (2.0 * _VgTerrainSpacing.z);
    return normalize(float3(-dx, 1.0, -dz));
}

float3 VgConvertToNormalTS(float3 normalData, float3 tangentWS, float3 bitangentWS)
{
#ifdef _NORMALMAP
    return SurfaceGradientFromTBN(normalData.xy, tangentWS, bitangentWS);
#else
    return float3(0.0, 0.0, 0.0); // no gradient
#endif
}

void GetSurfaceAndBuiltinData(inout FragInputs input, float3 V, inout PositionInputs posInput, out SurfaceData surfaceData, out BuiltinData builtinData)
{
    ZERO_INITIALIZE(SurfaceData, surfaceData);
    ZERO_INITIALIZE(BuiltinData, builtinData);

    float2 uv = input.texCoord0.xy;
    // terrain lightmap uvs are always taken from uv0
    input.texCoord1 = input.texCoord2 = input.texCoord0;

    TerrainLitSurfaceData terrainLitSurfaceData;
    InitializeTerrainLitSurfaceData(terrainLitSurfaceData);
    TerrainLitShade(uv, terrainLitSurfaceData);

    // per-pixel normal from the heightmap; terrains are never rotated or scaled (Unity Terrain)
    float3 normalWS = VgTerrainNormal(uv);
    // flat terrain: tangent (1, 0, 0), bitangent (0, 0, 1) (see HDRP ConstructTerrainTangent)
    float4 tangentWS = float4(cross(normalWS, float3(0, 0, 1)), -1);
    input.tangentToWorld = BuildTangentToWorld(tangentWS, normalWS);
    surfaceData.normalWS = normalWS;
    surfaceData.tangentWS = normalize(input.tangentToWorld[0].xyz);
    surfaceData.geomNormalWS = input.tangentToWorld[2];

    surfaceData.baseColor = terrainLitSurfaceData.albedo;
    surfaceData.perceptualSmoothness = terrainLitSurfaceData.smoothness;
    surfaceData.metallic = terrainLitSurfaceData.metallic;
    surfaceData.ambientOcclusion = terrainLitSurfaceData.ao;

    surfaceData.subsurfaceMask = 0;
    surfaceData.transmissionMask = 0;
    surfaceData.thickness = 1;
    surfaceData.diffusionProfileHash = 0;
    surfaceData.materialFeatures = MATERIALFEATUREFLAGS_LIT_STANDARD;
    surfaceData.anisotropy = 0.0;
    surfaceData.specularColor = float3(0.0, 0.0, 0.0);
    surfaceData.coatMask = 0.0;
    surfaceData.iridescenceThickness = 0.0;
    surfaceData.iridescenceMask = 0.0;
    surfaceData.ior = 1.0;
    surfaceData.transmittanceColor = float3(1.0, 1.0, 1.0);
    surfaceData.atDistance = 1000000.0;
    surfaceData.transmittanceMask = 0.0;
    surfaceData.specularOcclusion = 1.0;

    float3 normalTS = VgConvertToNormalTS(terrainLitSurfaceData.normalData, input.tangentToWorld[0], input.tangentToWorld[1]);
#ifdef DECAL_NORMAL_BLENDING
    if (_EnableDecals)
    {
        float alpha = 1.0; // unused
        DecalSurfaceData decalSurfaceData = GetDecalSurfaceData(posInput, input, alpha);
        ApplyDecalToSurfaceData(decalSurfaceData, input.tangentToWorld[2], surfaceData, normalTS);
    }
    GetNormalWS_SG(input, normalTS, surfaceData.normalWS, float3(1.0, 1.0, 1.0));
#else
    GetNormalWS(input, normalTS, surfaceData.normalWS, float3(1.0, 1.0, 1.0));
    #if HAVE_DECALS
    if (_EnableDecals)
    {
        float alpha = 1.0; // unused
        DecalSurfaceData decalSurfaceData = GetDecalSurfaceData(posInput, input, alpha);
        ApplyDecalToSurfaceData(decalSurfaceData, input.tangentToWorld[2], surfaceData);
    }
    #endif
#endif

    float3 bentNormalWS = surfaceData.normalWS;

#if defined(DEBUG_DISPLAY)
    if (_DebugMipMapMode != DEBUGMIPMAPMODE_NONE)
    {
        TerrainLitDebug(uv, posInput.positionSS, surfaceData.baseColor);
        surfaceData.metallic = 0;
    }
    ApplyDebugToSurfaceData(input.tangentToWorld, surfaceData);
#endif

#if defined(_MASKMAP) && !defined(_SPECULAR_OCCLUSION_NONE)
    surfaceData.specularOcclusion = GetSpecularOcclusionFromAmbientOcclusion(ClampNdotV(dot(surfaceData.normalWS, V)), surfaceData.ambientOcclusion, PerceptualSmoothnessToRoughness(surfaceData.perceptualSmoothness));
#endif

    GetBuiltinData(input, V, posInput, surfaceData, 1, bentNormalWS, 0, builtinData);
}

#endif
