// Debug visualisation of CPU-selected LOD cuts (M1). Per-vertex data: position, normal and
// uv0 = (clusterIndex, groupIndex, lodLevel, material). Uses the "SRPDefaultUnlit" pass, which
// HDRP draws in its forward opaque pass. HDRP include path for now; URP variant is part of M10.
Shader "Hidden/UNanite/DebugView"
{
    Properties
    {
        _Mode ("Mode", Int) = 0
        _LevelCount ("Level Count", Int) = 16
    }

    HLSLINCLUDE
    #pragma target 4.5
    #pragma only_renderers d3d11 vulkan metal playstation xboxone xboxseries switch glcore

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
    #include "../../ShaderLibrary/VgDebugColors.hlsl"

    int _Mode;
    int _LevelCount;

    struct Attributes
    {
        float3 positionOS : POSITION;
        float3 normalOS : NORMAL;
        float4 ids : TEXCOORD0; // cluster, group, level, material
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float3 normalWS : NORMAL;
        nointerpolation float4 ids : TEXCOORD0;
    };

    Varyings Vert(Attributes input)
    {
        Varyings o;
        float3 positionWS = TransformObjectToWorld(input.positionOS);
        o.positionCS = TransformWorldToHClip(positionWS);
        o.normalWS = TransformObjectToWorldNormal(input.normalOS);
        o.ids = input.ids;
        return o;
    }

    float4 Frag(Varyings input, uint primitiveId : SV_PrimitiveID) : SV_Target
    {
        uint cluster = (uint)input.ids.x;
        uint group = (uint)input.ids.y;
        uint level = (uint)input.ids.z;
        uint material = (uint)input.ids.w;

        float3 color;
        switch (_Mode)
        {
            case 0: color = VgHashColor(cluster); break;
            case 1: color = VgHashColor(group * 7919u + 13u); break;
            case 2: color = VgLevelColor(level, (uint)_LevelCount); break;
            case 3: color = VgHashColor(primitiveId * 2654435761u + cluster); break;
            case 4: color = VgHashColor(material * 104729u + 7u); break;
            default: color = 0.75.xxx; break;
        }

        // simple view-independent shading so shape reads well in every mode
        float3 n = normalize(input.normalWS);
        float3 l = normalize(float3(0.4, 0.8, 0.3));
        float shade = 0.35 + 0.65 * saturate(dot(n, l) * 0.5 + 0.5);
        return float4(color * shade, 1);
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }

        // HDRP draws forward-only opaques with ZTest Equal against its depth prepass, so the object
        // must be in the prepass (DepthForwardOnly) to be visible at all.
        Pass
        {
            Name "DepthForwardOnly"
            Tags { "LightMode" = "DepthForwardOnly" }
            ZWrite On
            ZTest LEqual
            Cull Back
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDepth
            void FragDepth(Varyings input) {}
            ENDHLSL
        }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "ForwardOnly" }
            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }
}
