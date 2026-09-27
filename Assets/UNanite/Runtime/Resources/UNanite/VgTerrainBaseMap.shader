// M8: bakes the composited albedo of a virtual geometry terrain (HDRP TerrainLit's splat blend) into
// a base map for the HDRP/Lit fallback material (views without the visibility buffer). Rendered by
// VirtualGeometryTerrain with one full-screen triangle; properties are copied from the terrain's
// resolve material (Hidden/UNanite/TerrainLitResolve).
Shader "Hidden/UNanite/TerrainBaseMap"
{
    SubShader
    {
        // HDRP only: URP projects skip it instead of failing on the HDRP includes
        PackageRequirements { "com.unity.render-pipelines.high-definition" }
        Pass
        {
            ZTest Always ZWrite Off Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma shader_feature_local _TERRAIN_8_LAYERS
            #pragma shader_feature_local _MASKMAP
            #pragma shader_feature_local _TERRAIN_BLEND_HEIGHT

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/TerrainLit/TerrainLit_Splatmap_Includes.hlsl"

            CBUFFER_START(UnityPerMaterial)
                // all 8 layers in every variant: UnityPerMaterial must have one layout across
        // variants (HDRP's UNITY_TERRAIN_CB_VARS grows with _TERRAIN_8_LAYERS)
        DECLARE_TERRAIN_LAYER_PROPS(0)
        DECLARE_TERRAIN_LAYER_PROPS(1)
        DECLARE_TERRAIN_LAYER_PROPS(2)
        DECLARE_TERRAIN_LAYER_PROPS(3)
        DECLARE_TERRAIN_LAYER_PROPS(4)
        DECLARE_TERRAIN_LAYER_PROPS(5)
        DECLARE_TERRAIN_LAYER_PROPS(6)
        DECLARE_TERRAIN_LAYER_PROPS(7)
        float4 _Control0_TexelSize;
        float4 _Control1_TexelSize;
        float _HeightTransition;
                float4 _Control0_ST;
                float4 _Control1_ST;
                float4 _VgTerrainParams;
                float4 _VgTerrainSpacing;
            CBUFFER_END

            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/TerrainLit/TerrainLitSurfaceData.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/TerrainLit/TerrainLit_Splatmap.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(uint id : SV_VertexID)
            {
                Varyings o;
                float2 p = float2((id << 1) & 2, id & 2);
                o.positionCS = float4(p * 2.0 - 1.0, 0.5, 1.0);
                o.uv = p;
            #if UNITY_UV_STARTS_AT_TOP
                o.uv.y = 1.0 - o.uv.y;
            #endif
                return o;
            }

            float4 Frag(Varyings i) : SV_Target
            {
                TerrainLitSurfaceData s;
                InitializeTerrainLitSurfaceData(s);
                TerrainSplatBlend(i.uv, i.uv, s);
                return float4(s.albedo, s.smoothness);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
