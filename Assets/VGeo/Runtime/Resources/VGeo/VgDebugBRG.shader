// Visualisation of the GPU-driven path ("Nanite view modes"): the expansion pass writes a debug id
// into the arena vertex colour (see VgCull.compute, Expand) and every material bin is drawn with this
// shader. Modes: 1 triangles, 2 clusters, 3 groups, 4 LOD level, 5 instances, 6 material bins.
// The code is in ShaderLibrary/VgDebugBRG.hlsl; every pass brings its pipeline's variables (an HLSLINCLUDE
// block would reach the programs of every sub-shader after it, HDRP includes in URP too).
Shader "Hidden/VGeo/DebugBRG"
{
    Properties
    {
        _Mode ("Mode", Int) = 1
        _LevelCount ("Level Count", Int) = 20
    }

    SubShader
    {
        // HDRP only: URP projects skip it instead of failing on the HDRP includes
        PackageRequirements { "com.unity.render-pipelines.high-definition" }
        Tags { "RenderPipeline" = "HDRenderPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

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
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan metal playstation xboxone xboxseries switch
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
            #include "../../ShaderLibrary/VgDebugBRG.hlsl"
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
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan metal playstation xboxone xboxseries switch
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
            #include "../../ShaderLibrary/VgDebugBRG.hlsl"
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }

    SubShader
    {
        // URP: a forward-only unlit material (drawn after the deferred lighting in deferred renderers)
        // with the depth passes of URP's prepasses
        PackageRequirements { "com.unity.render-pipelines.universal" }
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForwardOnly" }
            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan metal playstation xboxone xboxseries switch
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "../../ShaderLibrary/VgDebugBRG.hlsl"

            float4 FragDepth(Varyings input) : SV_Target { return input.positionCS.z; }
            float4 FragNormals(Varyings input) : SV_Target { return float4(normalize(input.normalWS), 0); }
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            Cull Back
            ColorMask R

            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan metal playstation xboxone xboxseries switch
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "../../ShaderLibrary/VgDebugBRG.hlsl"

            float4 FragDepth(Varyings input) : SV_Target { return input.positionCS.z; }
            float4 FragNormals(Varyings input) : SV_Target { return float4(normalize(input.normalWS), 0); }
            #pragma vertex Vert
            #pragma fragment FragDepth
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormalsOnly"
            Tags { "LightMode" = "DepthNormalsOnly" }
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan metal playstation xboxone xboxseries switch
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "../../ShaderLibrary/VgDebugBRG.hlsl"

            float4 FragDepth(Varyings input) : SV_Target { return input.positionCS.z; }
            float4 FragNormals(Varyings input) : SV_Target { return float4(normalize(input.normalWS), 0); }
            #pragma vertex Vert
            #pragma fragment FragNormals
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan metal playstation xboxone xboxseries switch
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "../../ShaderLibrary/VgDebugBRG.hlsl"

            float4 FragDepth(Varyings input) : SV_Target { return input.positionCS.z; }
            float4 FragNormals(Varyings input) : SV_Target { return float4(normalize(input.normalWS), 0); }
            #pragma vertex Vert
            #pragma fragment FragNormals
            ENDHLSL
        }
    }
}
