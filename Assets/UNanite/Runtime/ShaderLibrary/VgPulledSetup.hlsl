#ifndef UNANITE_VG_PULLED_SETUP_INCLUDED
#define UNANITE_VG_PULLED_SETUP_INCLUDED

// M10 pulled passes, part 1: included by generated VG shader variants (VgShaderVariantGenerator.cs)
// right after ShaderVariables.hlsl in every pass, before any of the pass's own code.
//
// Pulled draws are one BRG indexed draw per material bin; every vertex belongs to its own VG
// instance. UNITY_SETUP_INSTANCE_ID is redirected so the DOTS instance index is the VG instance
// carried in the instance ID field of the pass's attributes / varyings (VgPulledVert writes it):
// UNITY_MATRIX_M / I_M / PREV_MATRIX_M then load that instance's matrices from the per-instance
// BRG batch (VgWorld.PulledBatch), in the vertex and in the pixel stage.

#if defined(UNITY_DOTS_INSTANCING_ENABLED)

int VgLodMorphCrossfade(uint instance); // VgAttributes.hlsl (after the pass's buffers)

void VgSetupInstance(uint vgInstance)
{
    unity_InstanceID = 0;
    unity_SampledDOTSIndirectVisibleIndex = 0;
    unity_SampledDOTSInstanceIndex = vgInstance;
    // M11 SpeedTree smooth LOD: unity_LODFade.x where vertices are placed (vertex stages, and the
    // resolve's corners); fragment stages do not dither (no LOD_FADE_CROSSFADE draws)
#if defined(SHADER_STAGE_VERTEX) || defined(VG_RESOLVE)
    unity_SampledLODCrossfade = VgLodMorphCrossfade(vgInstance);
#else
    unity_SampledLODCrossfade = 0;
#endif
    SetupDOTSInstanceSelectMasks();
}

#undef UNITY_SETUP_INSTANCE_ID
#define UNITY_SETUP_INSTANCE_ID(input) {\
    VgSetupInstance(UNITY_GET_INSTANCE_ID(input));\
    UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES();\
    UNITY_SETUP_DOTS_SH_COEFFS;\
    UNITY_SETUP_DOTS_RENDER_BOUNDS; }

#endif

#endif
