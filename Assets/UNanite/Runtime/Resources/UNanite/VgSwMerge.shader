// M5 "VG.MergeSW": pass over the 8x8 tiles touched by the software raster (VG_SwTiles, drawn with
// DrawProceduralIndirect, 6 vertices per tile) that moves software-rasterised pixels (VgSwRaster.compute) into
// the visibility buffer and HDRP's depth buffer. Normal depth test: whichever of hardware VG,
// software VG and regular occluder depth is nearest wins; SV_Depth is the exact depth also stored in
// the visibility buffer, so the material resolve's depth test stays exact.
Shader "Hidden/UNanite/SwMerge"
{
    HLSLINCLUDE
    #pragma target 4.5
    #pragma only_renderers d3d11 vulkan metal playstation xboxone xboxseries switch

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

    StructuredBuffer<uint2> VG_SwVisBuffer; // uint64 per pixel: x = visibility id, y = depth key bits
    StructuredBuffer<uint> VG_SwTiles;
    uint _VgSwPitch;
    float _VgSwReversedZ;
    float4 _VgSwScreen; // xy: size, zw: 1 / size

    float4 Vert(uint vertexID : SV_VertexID) : SV_POSITION
    {
        uint tile = VG_SwTiles[vertexID / 6];
        uint corner = vertexID % 6;
        uint2 k = uint2(corner == 1 || corner == 4 || corner == 5 ? 1 : 0, corner == 2 || corner == 3 || corner == 5 ? 1 : 0);
        float2 pixel = float2(uint2(tile & 0xFFFF, tile >> 16) * 8 + k * 8);
        float2 ndc = pixel * _VgSwScreen.zw * 2.0 - 1.0;
    #if UNITY_UV_STARTS_AT_TOP
        ndc.y = -ndc.y;
    #endif
        return float4(ndc, UNITY_NEAR_CLIP_VALUE, 1.0);
    }

    uint2 Frag(float4 positionCS : SV_POSITION, out float depth : SV_Depth) : SV_Target
    {
        uint2 pixel = (uint2)positionCS.xy;
        if (pixel.x >= (uint)_VgSwScreen.x || pixel.y >= (uint)_VgSwScreen.y)
            discard;
        uint2 v = VG_SwVisBuffer[pixel.y * _VgSwPitch + pixel.x];
        if (v.y == 0)
            discard;
        float key = asfloat(v.y);
        depth = _VgSwReversedZ != 0 ? key : 1.0 - key;
        return uint2(v.x, asuint(depth));
    }
    ENDHLSL

    SubShader
    {
        Pass
        {
            Name "MergeSW"
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }
}
