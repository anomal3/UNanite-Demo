#ifndef UNANITE_VG_RESOLVE_LIGHTMAP_INCLUDED
#define UNANITE_VG_RESOLVE_LIGHTMAP_INCLUDED

// M9 follow-up: baked lightmaps in the GBuffer resolve. Included by resolve shaders after
// ShaderVariables.hlsl and before the material code (Material.hlsl / BuiltinGIUtilities.hlsl).
// Bins of lightmapped instances enable VG_LIGHTMAP (VG_LIGHTMAP_DIR: directional lightmaps); the
// shader then compiles HDRP's LIGHTMAP_ON path, whose per-draw unity_LightmapST / unity_LightmapIndex
// become per-pixel values of the resolved instance (set by VgBuildFragInputs, VgResolve.hlsl). The
// lightmaps themselves are texture arrays on the resolve twin (unity_Lightmaps, unity_LightmapsInd,
// unity_ShadowMasks; VgWorld.Lightmaps.cs), the layout HDRP samples under DOTS instancing.

#if defined(VG_LIGHTMAP) || defined(VG_LIGHTMAP_DIR)
    #define LIGHTMAP_ON
    #define VG_RESOLVE_LIGHTMAP
    #if defined(VG_LIGHTMAP_DIR)
        #define DIRLIGHTMAP_COMBINED
    #endif

    StructuredBuffer<float4> VG_InstanceLightmaps; // per instance: lightmap uv scale (xy) and offset (zw)
    static float4 g_VgLightmapST;
    static float4 g_VgLightmapIndex;

    #undef unity_LightmapST
    #undef unity_LightmapIndex
    #define unity_LightmapST g_VgLightmapST
    #define unity_LightmapIndex g_VgLightmapIndex
#endif

#endif
