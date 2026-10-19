// M3 visibility-buffer raster ("VG.RasterVisBuffer"): hardware rasterisation of the visible
// clusters of resolve-capable bins with vertex pulling from the page pool. Drawn by VgWorld from
// the BeforeRendering custom pass (HDRP) or the "VGeo Visibility Buffer" render pass (URP, before
// the prepasses) into (visibility buffer, camera depth).
//
//   DrawProceduralIndirect: vertexCountPerInstance = 384 (128 triangles x 3 corners),
//   instanceCount = clusters in the raster list (VG_RasterArgs, written by VgCull.compute).
//   Pass 0 (Cull Back): list 0 (hardware single-sided), pass 1 (Cull Off): list 1 (double-sided).
//   record = VG_RasterLists[_VgListBase + list * _VgListCapacity + first + instance], first = 0 for
//   phase 1 and the phase-1 list size (VG_PhaseState) for the M4 phase-2 draws.
//   The code is in ShaderLibrary/VgVisBufferRaster.hlsl; every pass brings its pipeline's variables (an
//   HLSLINCLUDE block would reach the programs of every sub-shader after it, HDRP includes in URP too).
Shader "Hidden/VGeo/VisBufferRaster"
{
    SubShader
    {
        // HDRP only: URP projects skip it instead of failing on the HDRP includes
        PackageRequirements { "com.unity.render-pipelines.high-definition" }
        Tags { "RenderPipeline" = "HDRenderPipeline" }

        Pass
        {
            Name "VisBufferSingleSided"
            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan metal playstation xboxone xboxseries switch

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
            #include "../../ShaderLibrary/VgVisBufferRaster.hlsl"
            #pragma vertex VgRasterVertSingleSided
            #pragma fragment VgRasterFrag
            ENDHLSL
        }

        Pass
        {
            Name "VisBufferDoubleSided"
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan metal playstation xboxone xboxseries switch

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
            #include "../../ShaderLibrary/VgVisBufferRaster.hlsl"
            #pragma vertex VgRasterVertDoubleSided
            #pragma fragment VgRasterFrag
            ENDHLSL
        }
    }

    SubShader
    {
        // URP: the camera matrices URP set up for the camera (flipped when it renders to a texture,
        // like every pass of the camera, so the visibility buffer and the resolve draws agree)
        PackageRequirements { "com.unity.render-pipelines.universal" }
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "VisBufferSingleSided"
            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan metal playstation xboxone xboxseries switch

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "../../ShaderLibrary/VgVisBufferRaster.hlsl"
            #pragma vertex VgRasterVertSingleSided
            #pragma fragment VgRasterFrag
            ENDHLSL
        }

        Pass
        {
            Name "VisBufferDoubleSided"
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan metal playstation xboxone xboxseries switch

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "../../ShaderLibrary/VgVisBufferRaster.hlsl"
            #pragma vertex VgRasterVertDoubleSided
            #pragma fragment VgRasterFrag
            ENDHLSL
        }
    }
}
