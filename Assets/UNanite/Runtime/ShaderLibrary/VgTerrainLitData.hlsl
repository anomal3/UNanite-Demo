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

#include "VgTerrainNormal.hlsl"

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
#ifdef VG_TERRAIN_RVT
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/NormalSurfaceGradient.hlsl"
#include "VgRvt.hlsl"
#endif

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

    float3 albedo = 0, normalTS = 0;
    float ao = 1, smoothness = 0, metallic = 0;
    float3 frameNormal;

#ifdef VG_TERRAIN_RVT
    // M12: the blended surface comes from the terrain's runtime virtual texture. Coarse derivatives
    // give one level of detail per 2x2 quad, so the direct-blend branch is quad-uniform and its
    // implicit derivatives stay valid. Detail finer than mip 0 (close to the camera) keeps the direct
    // splat blend, crossfaded with the cache over one mip.
    float2 duvdx = ddx_coarse(uv), duvdy = ddy_coarse(uv);
    float virtualSize = exp2(_VgRvtParams.y);
    float lod = _VgRvtParams.w > 0 ? VgRvtLod(duvdx * virtualSize, duvdy * virtualSize) : -2.0;
    float directWeight = saturate(-lod);
    VgRvtSurface cached = (VgRvtSurface)0;
    if (directWeight < 1)
    {
        cached = VgRvtSample(_VgRvtParams, uv, duvdx, duvdy, max(lod, 0.0));
        if (!cached.valid)
            directWeight = 1; // not baked yet (the terrain's first frames)
    }
    // frame of the gradients: the heightmap normal where the direct blend runs, else the vertex normal
    frameNormal = directWeight > 0 ? VgTerrainNormal(uv) : normalize(input.tangentToWorld[2]);
#else
    float directWeight = 1;
    frameNormal = VgTerrainNormal(uv); // per-pixel normal from the heightmap (LOD-independent)
#endif
    // terrains are never rotated or scaled (Unity Terrain); flat tangent frame (HDRP ConstructTerrainTangent)
    float3x3 frame = BuildTangentToWorld(VgTerrainTangent(frameNormal), frameNormal);

#ifdef VG_TERRAIN_RVT
    if (directWeight < 1)
    {
        albedo = cached.albedo;
        ao = cached.ao;
        smoothness = cached.smoothness;
        metallic = cached.metallic;
        normalTS = SurfaceGradientFromPerturbedNormal(frameNormal, cached.normalWS);
    }
    if (directWeight > 0)
#endif
    {
        TerrainLitSurfaceData terrainLitSurfaceData;
        InitializeTerrainLitSurfaceData(terrainLitSurfaceData);
        TerrainLitShade(uv, terrainLitSurfaceData);
        float3 directTS = VgConvertToNormalTS(terrainLitSurfaceData.normalData, frame[0], frame[1]);
        albedo = lerp(albedo, terrainLitSurfaceData.albedo, directWeight);
        ao = lerp(ao, terrainLitSurfaceData.ao, directWeight);
        smoothness = lerp(smoothness, terrainLitSurfaceData.smoothness, directWeight);
        metallic = lerp(metallic, terrainLitSurfaceData.metallic, directWeight);
        normalTS = lerp(normalTS, directTS, directWeight);
    }

    input.tangentToWorld = frame;
    surfaceData.normalWS = frameNormal;
    surfaceData.tangentWS = normalize(input.tangentToWorld[0].xyz);
    surfaceData.geomNormalWS = input.tangentToWorld[2];

    surfaceData.baseColor = albedo;
    surfaceData.perceptualSmoothness = smoothness;
    surfaceData.metallic = metallic;
    surfaceData.ambientOcclusion = ao;

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
