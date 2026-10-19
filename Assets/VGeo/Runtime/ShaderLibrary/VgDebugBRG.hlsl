#ifndef VGEO_VG_DEBUG_BRG_INCLUDED
#define VGEO_VG_DEBUG_BRG_INCLUDED

// Hidden/VGeo/DebugBRG, shared by its HDRP and URP sub-shaders (included after the pipeline's variables).

#include "VgDebugColors.hlsl"

CBUFFER_START(UnityPerMaterial)
    int _Mode;
    int _LevelCount;
CBUFFER_END

struct Attributes
{
    float3 positionOS : POSITION;   // world space (identity object matrix)
    float3 normalOS : NORMAL;
    float4 color : COLOR;           // debug id packed as RGBA8
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 normalWS : NORMAL;
    nointerpolation uint id : TEXCOORD0;
};

uint UnpackId(float4 c)
{
    uint4 b = (uint4)round(saturate(c) * 255.0);
    return b.r | (b.g << 8) | (b.b << 16) | (b.a << 24);
}

Varyings Vert(Attributes input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    Varyings o;
    float3 positionWS = TransformObjectToWorld(input.positionOS);
    o.positionCS = TransformWorldToHClip(positionWS);
    o.normalWS = TransformObjectToWorldNormal(input.normalOS);
    o.id = UnpackId(input.color);
    return o;
}

float4 Frag(Varyings input, uint primitiveId : SV_PrimitiveID) : SV_Target
{
    float3 color;
    if (_Mode == 1)
        color = VgHashColor(input.id * 2654435761u + primitiveId);
    else if (_Mode == 4)
        color = VgLevelColor(input.id, (uint)_LevelCount);
    else
        color = VgHashColor(input.id);

    float3 n = normalize(input.normalWS);
    float3 l = normalize(float3(0.4, 0.8, 0.3));
    float shade = 0.4 + 0.6 * saturate(dot(n, l) * 0.5 + 0.5);
    return float4(color * shade, 1);
}

#endif
