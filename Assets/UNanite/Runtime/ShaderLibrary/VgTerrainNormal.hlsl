#ifndef UNANITE_VG_TERRAIN_NORMAL_INCLUDED
#define UNANITE_VG_TERRAIN_NORMAL_INCLUDED

// M8: per-pixel terrain normal from TerrainData.heightmapTexture, shared by the terrain resolve
// (VgTerrainLitData.hlsl) and the M12 virtual-texture bake. Needs _VgTerrainParams / _VgTerrainSpacing
// (UnityPerMaterial of the terrain shaders).

TEXTURE2D(_VgTerrainHeightmap); // TerrainData.heightmapTexture: height / 2 in R (UnpackHeightmap)
SAMPLER(sampler_VgTerrainHeightmap);

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

// Flat terrain tangent frame around `normalWS` (see HDRP ConstructTerrainTangent): tangent
// cross(n, +z), bitangent sign -1.
float4 VgTerrainTangent(float3 normalWS)
{
    return float4(cross(normalWS, float3(0, 0, 1)), -1);
}

#endif
