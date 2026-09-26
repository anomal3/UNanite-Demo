// M8 material resolve of virtual geometry terrains: HDRP TerrainLit's own splat blending
// (TerrainLit_Splatmap.hlsl: 4 or 8 layers, height or density blending, normal and mask maps,
// per-layer remaps) evaluated from the visibility buffer. Created at runtime by
// VirtualGeometryTerrain (properties mirror what Unity's Terrain sets for HDRP/TerrainLit); only the
// GBuffer pass exists, drawn by HDRP through BatchRendererGroup like Hidden/UNanite/LitResolve.
// Shading normals come per pixel from the terrain heightmap texture (LOD-independent, as Unity's
// instanced per-pixel normals); UVs are the terrain UVs derived from the position.
Shader "Hidden/UNanite/TerrainLitResolve"
{
    Properties
    {
        [HideInInspector] _Control0("Control 0", 2D) = "red" {}
        [HideInInspector] _Control1("Control 1", 2D) = "black" {}
        [HideInInspector] _HeightTransition("Height Transition", Float) = 0
        [HideInInspector][NoScaleOffset] _VgTerrainHeightmap("Heightmap", 2D) = "black" {}
        [HideInInspector] _VgTerrainParams("Heightmap params", Vector) = (1, 1, 1, 1)
        [HideInInspector] _VgTerrainSpacing("Sample spacing", Vector) = (1, 1, 1, 1)
        [HideInInspector] _Splat0("Layer 0 Albedo", 2D) = "grey" {}
        [HideInInspector][NoScaleOffset] _Normal0("Layer 0 Normal", 2D) = "bump" {}
        [HideInInspector][NoScaleOffset] _Mask0("Layer 0 Mask", 2D) = "grey" {}
        [HideInInspector] _Metallic0("Layer 0 Metallic", Float) = 0
        [HideInInspector] _Smoothness0("Layer 0 Smoothness", Float) = 0.5
        [HideInInspector] _NormalScale0("Layer 0 Normal Scale", Float) = 1
        [HideInInspector] _DiffuseRemapScale0("Layer 0 Diffuse Remap", Vector) = (1, 1, 1, 1)
        [HideInInspector] _MaskMapRemapOffset0("Layer 0 Mask Remap Offset", Vector) = (0, 0, 0, 0)
        [HideInInspector] _MaskMapRemapScale0("Layer 0 Mask Remap Scale", Vector) = (1, 1, 1, 1)
        [HideInInspector] _LayerHasMask0("Layer 0 Has Mask", Float) = 0
        [HideInInspector] _Splat1("Layer 1 Albedo", 2D) = "grey" {}
        [HideInInspector][NoScaleOffset] _Normal1("Layer 1 Normal", 2D) = "bump" {}
        [HideInInspector][NoScaleOffset] _Mask1("Layer 1 Mask", 2D) = "grey" {}
        [HideInInspector] _Metallic1("Layer 1 Metallic", Float) = 0
        [HideInInspector] _Smoothness1("Layer 1 Smoothness", Float) = 0.5
        [HideInInspector] _NormalScale1("Layer 1 Normal Scale", Float) = 1
        [HideInInspector] _DiffuseRemapScale1("Layer 1 Diffuse Remap", Vector) = (1, 1, 1, 1)
        [HideInInspector] _MaskMapRemapOffset1("Layer 1 Mask Remap Offset", Vector) = (0, 0, 0, 0)
        [HideInInspector] _MaskMapRemapScale1("Layer 1 Mask Remap Scale", Vector) = (1, 1, 1, 1)
        [HideInInspector] _LayerHasMask1("Layer 1 Has Mask", Float) = 0
        [HideInInspector] _Splat2("Layer 2 Albedo", 2D) = "grey" {}
        [HideInInspector][NoScaleOffset] _Normal2("Layer 2 Normal", 2D) = "bump" {}
        [HideInInspector][NoScaleOffset] _Mask2("Layer 2 Mask", 2D) = "grey" {}
        [HideInInspector] _Metallic2("Layer 2 Metallic", Float) = 0
        [HideInInspector] _Smoothness2("Layer 2 Smoothness", Float) = 0.5
        [HideInInspector] _NormalScale2("Layer 2 Normal Scale", Float) = 1
        [HideInInspector] _DiffuseRemapScale2("Layer 2 Diffuse Remap", Vector) = (1, 1, 1, 1)
        [HideInInspector] _MaskMapRemapOffset2("Layer 2 Mask Remap Offset", Vector) = (0, 0, 0, 0)
        [HideInInspector] _MaskMapRemapScale2("Layer 2 Mask Remap Scale", Vector) = (1, 1, 1, 1)
        [HideInInspector] _LayerHasMask2("Layer 2 Has Mask", Float) = 0
        [HideInInspector] _Splat3("Layer 3 Albedo", 2D) = "grey" {}
        [HideInInspector][NoScaleOffset] _Normal3("Layer 3 Normal", 2D) = "bump" {}
        [HideInInspector][NoScaleOffset] _Mask3("Layer 3 Mask", 2D) = "grey" {}
        [HideInInspector] _Metallic3("Layer 3 Metallic", Float) = 0
        [HideInInspector] _Smoothness3("Layer 3 Smoothness", Float) = 0.5
        [HideInInspector] _NormalScale3("Layer 3 Normal Scale", Float) = 1
        [HideInInspector] _DiffuseRemapScale3("Layer 3 Diffuse Remap", Vector) = (1, 1, 1, 1)
        [HideInInspector] _MaskMapRemapOffset3("Layer 3 Mask Remap Offset", Vector) = (0, 0, 0, 0)
        [HideInInspector] _MaskMapRemapScale3("Layer 3 Mask Remap Scale", Vector) = (1, 1, 1, 1)
        [HideInInspector] _LayerHasMask3("Layer 3 Has Mask", Float) = 0
        [HideInInspector] _Splat4("Layer 4 Albedo", 2D) = "grey" {}
        [HideInInspector][NoScaleOffset] _Normal4("Layer 4 Normal", 2D) = "bump" {}
        [HideInInspector][NoScaleOffset] _Mask4("Layer 4 Mask", 2D) = "grey" {}
        [HideInInspector] _Metallic4("Layer 4 Metallic", Float) = 0
        [HideInInspector] _Smoothness4("Layer 4 Smoothness", Float) = 0.5
        [HideInInspector] _NormalScale4("Layer 4 Normal Scale", Float) = 1
        [HideInInspector] _DiffuseRemapScale4("Layer 4 Diffuse Remap", Vector) = (1, 1, 1, 1)
        [HideInInspector] _MaskMapRemapOffset4("Layer 4 Mask Remap Offset", Vector) = (0, 0, 0, 0)
        [HideInInspector] _MaskMapRemapScale4("Layer 4 Mask Remap Scale", Vector) = (1, 1, 1, 1)
        [HideInInspector] _LayerHasMask4("Layer 4 Has Mask", Float) = 0
        [HideInInspector] _Splat5("Layer 5 Albedo", 2D) = "grey" {}
        [HideInInspector][NoScaleOffset] _Normal5("Layer 5 Normal", 2D) = "bump" {}
        [HideInInspector][NoScaleOffset] _Mask5("Layer 5 Mask", 2D) = "grey" {}
        [HideInInspector] _Metallic5("Layer 5 Metallic", Float) = 0
        [HideInInspector] _Smoothness5("Layer 5 Smoothness", Float) = 0.5
        [HideInInspector] _NormalScale5("Layer 5 Normal Scale", Float) = 1
        [HideInInspector] _DiffuseRemapScale5("Layer 5 Diffuse Remap", Vector) = (1, 1, 1, 1)
        [HideInInspector] _MaskMapRemapOffset5("Layer 5 Mask Remap Offset", Vector) = (0, 0, 0, 0)
        [HideInInspector] _MaskMapRemapScale5("Layer 5 Mask Remap Scale", Vector) = (1, 1, 1, 1)
        [HideInInspector] _LayerHasMask5("Layer 5 Has Mask", Float) = 0
        [HideInInspector] _Splat6("Layer 6 Albedo", 2D) = "grey" {}
        [HideInInspector][NoScaleOffset] _Normal6("Layer 6 Normal", 2D) = "bump" {}
        [HideInInspector][NoScaleOffset] _Mask6("Layer 6 Mask", 2D) = "grey" {}
        [HideInInspector] _Metallic6("Layer 6 Metallic", Float) = 0
        [HideInInspector] _Smoothness6("Layer 6 Smoothness", Float) = 0.5
        [HideInInspector] _NormalScale6("Layer 6 Normal Scale", Float) = 1
        [HideInInspector] _DiffuseRemapScale6("Layer 6 Diffuse Remap", Vector) = (1, 1, 1, 1)
        [HideInInspector] _MaskMapRemapOffset6("Layer 6 Mask Remap Offset", Vector) = (0, 0, 0, 0)
        [HideInInspector] _MaskMapRemapScale6("Layer 6 Mask Remap Scale", Vector) = (1, 1, 1, 1)
        [HideInInspector] _LayerHasMask6("Layer 6 Has Mask", Float) = 0
        [HideInInspector] _Splat7("Layer 7 Albedo", 2D) = "grey" {}
        [HideInInspector][NoScaleOffset] _Normal7("Layer 7 Normal", 2D) = "bump" {}
        [HideInInspector][NoScaleOffset] _Mask7("Layer 7 Mask", 2D) = "grey" {}
        [HideInInspector] _Metallic7("Layer 7 Metallic", Float) = 0
        [HideInInspector] _Smoothness7("Layer 7 Smoothness", Float) = 0.5
        [HideInInspector] _NormalScale7("Layer 7 Normal Scale", Float) = 1
        [HideInInspector] _DiffuseRemapScale7("Layer 7 Diffuse Remap", Vector) = (1, 1, 1, 1)
        [HideInInspector] _MaskMapRemapOffset7("Layer 7 Mask Remap Offset", Vector) = (0, 0, 0, 0)
        [HideInInspector] _MaskMapRemapScale7("Layer 7 Mask Remap Scale", Vector) = (1, 1, 1, 1)
        [HideInInspector] _LayerHasMask7("Layer 7 Has Mask", Float) = 0

        // HDRP GBuffer stencil (same values HDRP/TerrainLit uses)
        [HideInInspector] _StencilRefGBuffer("_StencilRefGBuffer", Int) = 2
        [HideInInspector] _StencilWriteMaskGBuffer("_StencilWriteMaskGBuffer", Int) = 3
        [HideInInspector] _StencilRefMV("_StencilRefMV", Int) = 32 // StencilUsage.ObjectMotionVector
        [HideInInspector] _StencilWriteMaskMV("_StencilWriteMaskMV", Int) = 32
    }

    HLSLINCLUDE

    #pragma target 4.5

    #pragma shader_feature_local _TERRAIN_8_LAYERS
    #pragma shader_feature_local _NORMALMAP
    #pragma shader_feature_local _MASKMAP
    #pragma shader_feature_local _SPECULAR_OCCLUSION_NONE
    #pragma shader_feature_local _TERRAIN_BLEND_HEIGHT
    #pragma shader_feature_local _DISABLE_DECALS

    #define _DEFERRED_CAPABLE_MATERIAL
    #define SUPPORT_GLOBAL_MIP_BIAS
    #define PREFER_HALF 0

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/TerrainLit/TerrainLit_Splatmap_Includes.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/FragInputs.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/ShaderPass.cs.hlsl"

    // every material property in UnityPerMaterial (BatchRendererGroup / SRP Batcher)
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
        float4 _VgTerrainParams;  // x: heightmap resolution, y: 1 / resolution, z: world height of a texel value of 1 (2 * size.y), w: unused
        float4 _VgTerrainSpacing; // x, z: world spacing of heightmap samples
    CBUFFER_END
    #ifdef DEBUG_DISPLAY
    UNITY_TERRAIN_CB_DEBUG_VARS // TerrainLitDebug (texture streaming debug), outside UnityPerMaterial: its layout stays the same
    #endif

    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" "RenderType" = "Opaque" }

        Pass
        {
            Name "GBuffer"
            Tags { "LightMode" = "GBuffer" }

            // tiles are screen-aligned quads; depth is the rasterised visibility-buffer depth
            Cull Off
            ZTest LEqual
            ZWrite Off

            Stencil
            {
                WriteMask [_StencilWriteMaskGBuffer]
                Ref [_StencilRefGBuffer]
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM

            #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch switch2
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma multi_compile _ DEBUG_DISPLAY
            #pragma multi_compile_fragment _ SHADOWS_SHADOWMASK
            #pragma multi_compile_fragment _ PROBE_VOLUMES_L1 PROBE_VOLUMES_L2
            #pragma multi_compile_fragment DECALS_OFF DECALS_3RT DECALS_4RT
            #pragma multi_compile_fragment _ DECAL_SURFACE_GRADIENT
            #pragma multi_compile_fragment _ RENDERING_LAYERS

            #define SHADERPASS SHADERPASS_GBUFFER
            #ifdef DEBUG_DISPLAY
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Debug/DebugDisplay.hlsl"
            #endif
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Material.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/Lit.hlsl"
            #include "../../ShaderLibrary/VgTerrainLitData.hlsl"
            #include "../../ShaderLibrary/VgResolve.hlsl"

            #pragma vertex VgResolveVert
            #pragma fragment VgResolveFrag

            ENDHLSL
        }

        // M9: object motion vectors of moving instances (bins drawn with BatchDrawCommandFlags.HasMotion)
        Pass
        {
            Name "MotionVectors"
            Tags { "LightMode" = "MotionVectors" }

            Cull Off
            ZTest LEqual
            ZWrite On
            // only the motion-vector buffer (target 0 without MSAA); decal / normal buffers keep the resolve's data
            ColorMask 0 1
            ColorMask 0 2
            ColorMask 0 3

            Stencil
            {
                WriteMask [_StencilWriteMaskMV]
                Ref [_StencilRefMV]
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM

            #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch switch2
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #define SHADERPASS SHADERPASS_MOTION_VECTORS
            #include "../../ShaderLibrary/VgResolveMotion.hlsl"

            #pragma vertex VgResolveVert
            #pragma fragment VgMotionFrag

            ENDHLSL
        }
    }

    FallBack Off
}
