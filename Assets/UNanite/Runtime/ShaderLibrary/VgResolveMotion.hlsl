#ifndef UNANITE_VG_RESOLVE_MOTION_INCLUDED
#define UNANITE_VG_RESOLVE_MOTION_INCLUDED

// M9 object motion vectors of the visibility-buffer path: the resolve shaders' "MotionVectors" pass,
// drawn by HDRP's object motion-vector pass over the same classified tiles as the GBuffer resolve
// (only for bins that hold a moving instance: BatchDrawCommandFlags.HasMotion). Per pixel of a
// moving instance: the visible point in mesh space (VgIntersect) under the current and the previous
// transform (VgInstanceGpu.prevLocalToWorld), projected with the unjittered and the previous
// view-projection, written like ShaderPassMotionVectors.hlsl (NDC delta * 0.5, y flipped when UVs
// start at the top). The pass sets StencilUsage.ObjectMotionVector so HDRP's camera-motion pass keeps
// these pixels; pixels of static instances are discarded and get camera motion. Only SV_Target0 (the
// motion-vector buffer, no MSAA) is written: the pass masks the other bound targets (decal / normal
// buffer), which hold the GBuffer resolve's data.

#include "VgResolveCommon.hlsl"

#if defined(VG_MOTION_FROM_RASTER)
// M11: bins whose programmable raster wrote the motion of their moved vertices (VgVisRaster.hlsl
// VG_RASTER_MOTION): every pixel of the bin copies it (drawn every frame, BinFlagRasterMotion)
TEXTURE2D_X(_VgMotionBuffer);
#endif

void VgMotionFrag(VgResolveVaryings packedInput, out float4 outMotionVector : SV_Target0, out float outputDepth : SV_Depth)
{
    UNITY_SETUP_INSTANCE_ID(packedInput);

    int2 pixel = int2(packedInput.positionCS.xy);
    uint2 own = LOAD_TEXTURE2D_X(_VgVisBuffer, pixel).xy;
    if (!VgInBin(own.x, packedInput.bin))
        discard;
#if defined(VG_MOTION_FROM_RASTER)
    outMotionVector = float4(LOAD_TEXTURE2D_X(_VgMotionBuffer, pixel).xy, 0.0, 0.0);
    outputDepth = asfloat(own.y);
    return;
#endif
    VgHit h;
    if (!VgIntersect(own.x, packedInput.positionCS.xy, h) || (h.inst.flags & VG_INSTANCE_MOVING) == 0)
        discard;

    float3 positionOS = h.bary.x * h.v0.position + h.bary.y * h.v1.position + h.bary.z * h.v2.position;
    float3 positionRWS = GetCameraRelativePositionWS(VgTransformPoint(h.inst, positionOS));
    float3 previousRWS = GetCameraRelativePositionWS(VgTransformPrevPoint(h.inst, positionOS));
    float4 positionCS = mul(UNITY_MATRIX_UNJITTERED_VP, float4(positionRWS, 1.0));
    float4 previousCS = mul(UNITY_MATRIX_PREV_VP, float4(previousRWS, 1.0));

    float2 motion = positionCS.xy / positionCS.w - previousCS.xy / previousCS.w;
    motion = clamp(motion, -1.999, 1.999);
#if UNITY_UV_STARTS_AT_TOP
    motion.y = -motion.y;
#endif
    outMotionVector = float4(motion * 0.5, 0.0, 0.0);
    outputDepth = asfloat(own.y);
}

#endif
