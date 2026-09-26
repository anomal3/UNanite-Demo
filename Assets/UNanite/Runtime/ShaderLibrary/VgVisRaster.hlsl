#ifndef UNANITE_VG_VIS_RASTER_INCLUDED
#define UNANITE_VG_VIS_RASTER_INCLUDED

// M11 programmable raster: the "VgVisBuffer" pass of generated VG variants (a copy of the graph's
// DepthOnly pass), drawn by the visibility-buffer custom pass (renderer list, BRG indexed draws of
// the arena with the bin's raster twin) for bins whose material moves vertices or clips alpha
// (foliage). Indices written by VgCull.compute Expand: (VG_Visible record << 9 | triangle << 2 |
// corner); every corner goes through the pass's own Vert (vertex graph: wind, displacement), the
// pixel through its surface code (alpha test), and the visibility ID + depth are written like
// VgVisBufferRaster.shader. The resolve (VgResolveShaderGraph.hlsl) re-evaluates the same corners.

#include "VgResolveCommon.hlsl"
#include "VgAttributes.hlsl"

StructuredBuffer<uint> VG_TriangleIds; // visibility ID per arena triangle (VgCull.compute Expand)
StructuredBuffer<uint> VG_DrawArgs;    // indexed draw args (5 uints) per (view slot, bin)

// the draw's first arena triangle first: system-generated inputs of the pass's varyings
// (SV_IsFrontFace) must come last
struct VgVisVaryings
{
    nointerpolation uint firstTriangle : VG_FIRST_TRIANGLE;
    nointerpolation float2 lodDither : VG_LOD_DITHER; // M11 LOD crossfade (VgLodDither)
#if defined(VG_RASTER_MOTION)
    float2 motion : VG_MOTION; // encoded motion vector of the vertex (interpolated: small triangles)
#endif
    PackedVaryingsType p;
};

// indices (VG_Visible record << 7 | vertex in cluster); the draw's visibleOffset is its args entry
VgVisVaryings VgVisVert(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
{
#if defined(UNITY_DOTS_INSTANCING_ENABLED)
    UnitySetupInstanceID(instanceID);
    SetupDOTSVisibleInstancingData();
    uint args = GetDOTSIndirectVisibleIndex();
#else
    uint args = 0; // only drawn through BatchRendererGroup
#endif
    uint record = vertexID >> 7;
    uint4 vis = VG_Visible[record];
    VgInstanceGpu inst = VG_Instances[vis.x];
    VgMeshGpu mesh = VG_Meshes[inst.meshIndex];
    VgCluster c = VgLoadCluster(VG_PagePool, vis.y, vis.z);
    VgVisVaryings o;
    AttributesMesh attributes = VgAttributes(VgDecodeVertex(VG_PagePool, c, vertexID & 127u, mesh), mesh, vis.x);
    o.p = Vert(attributes);
    o.firstTriangle = VG_DrawArgs[args * 5 + 2] / 3;
    o.lodDither = VgLodDither(vis.x);
#if defined(VG_RASTER_MOTION)
    // M11 motion of moved vertices (wind, time): the rasterised position unjittered, and near the
    // camera the vertex graph at the previous frame's time and SpeedTree wind (g_VgWindHistory) under
    // the previous object matrix, like HDRP's MotionVectors pass; farther (VG_RasterMotion.x, squared)
    // the animation moves less than a pixel per frame: the rasterised position under the previous
    // object matrix (camera and object motion only, no second graph evaluation)
    float4 world = mul(UNITY_MATRIX_I_VP, o.p.vmesh.positionCS);
    float3 positionRWS = world.xyz / world.w;
    float4 motionCurrent = mul(UNITY_MATRIX_UNJITTERED_VP, float4(positionRWS, 1.0));
    float3 previousRWS = TransformPreviousObjectToWorld(TransformWorldToObject(positionRWS));
#if defined(HAVE_MESH_MODIFICATION)
    VgInstanceGpu motionInstance = VG_Instances[vis.x];
    float3 pivot = float3(motionInstance.localToWorld0.w, motionInstance.localToWorld1.w, motionInstance.localToWorld2.w) - VG_LodMorph.xyz;
    [branch] if (dot(pivot, pivot) < VG_RasterMotion.x)
    {
        AttributesMesh previous = attributes;
        g_VgWindHistory = true;
#ifdef USE_CUSTOMINTERP_SUBSTRUCT
        VaryingsMeshToPS unused = (VaryingsMeshToPS)0;
        previous = ApplyMeshModification(previous, _LastTimeParameters.xyz, unused);
#else
        previous = ApplyMeshModification(previous, _LastTimeParameters.xyz);
#endif
        g_VgWindHistory = false;
        previousRWS = TransformPreviousObjectToWorld(previous.positionOS);
    }
#endif
    float4 previousCS = mul(UNITY_MATRIX_PREV_VP, float4(previousRWS, 1.0));
    float4 currentCS = motionCurrent;
    // behind the camera in either frame: no motion (the triangle is clipped there anyway)
    float2 motion = currentCS.w > 1e-5 && previousCS.w > 1e-5 ? currentCS.xy / currentCS.w - previousCS.xy / previousCS.w : 0.0;
    motion = clamp(motion, -1.999, 1.999);
#if UNITY_UV_STARTS_AT_TOP
    motion.y = -motion.y;
#endif
    o.motion = motion * 0.5;
#endif
    return o;
}

// pixel input: the same registers as VgVisVaryings, then the system values in signature order
struct VgVisFragInput
{
    nointerpolation uint firstTriangle : VG_FIRST_TRIANGLE;
    nointerpolation float2 lodDither : VG_LOD_DITHER;
#if defined(VG_RASTER_MOTION)
    float2 motion : VG_MOTION;
#endif
    PackedVaryingsMeshToPS vmesh;
};

struct VgVisOutput
{
    uint2 visibility : SV_Target0; // visibility ID, depth
#if defined(VG_RASTER_BARY)
    // M11: perspective-correct barycentrics of corners 1 and 2 of the arena triangle (15 bits each) and
    // the front face (bit 30): the resolve (VgResolveShaderGraph.hlsl) takes them instead of
    // re-evaluating the moved corners and intersecting the view ray with them
    uint barycentrics : SV_Target1;
#endif
#if defined(VG_RASTER_MOTION)
    // M11: motion vector like ShaderPassMotionVectors.hlsl (only enabled with VG_RASTER_BARY); copied
    // into HDRP's motion-vector buffer by the resolve's MotionVectors pass (VG_MOTION_FROM_RASTER)
    float2 motion : SV_Target2;
#endif
};

VgVisOutput VgVisFrag(VgVisFragInput packedInput, uint primitive : SV_PrimitiveID
#if defined(VARYINGS_NEED_CULLFACE)
    , FRONT_FACE_TYPE cullFace : FRONT_FACE_SEMANTIC
#elif defined(VG_RASTER_BARY)
    , FRONT_FACE_TYPE vgFace : FRONT_FACE_SEMANTIC
#endif
#if defined(VG_RASTER_BARY)
    , float3 bary : SV_Barycentrics
#endif
    )
{
    // M11 LOD crossfade: the fine side keeps the pixels below its threshold, the coarse side the others
    // (one screen-space hash for both, like LODDitheringTransition)
    if (packedInput.lodDither.x < 1.0 || packedInput.lodDither.y > 0.0)
    {
        float h = GenerateHashedRandomFloat(uint2(packedInput.vmesh.positionCS.xy));
        if ((h < packedInput.lodDither.x) == (packedInput.lodDither.y > 0.0))
            discard;
    }
    PackedVaryingsType p;
    ZERO_INITIALIZE(PackedVaryingsType, p);
    p.vmesh = packedInput.vmesh;
    FragInputs input = UnpackVaryingsToFragInputs(p);
#if defined(VARYINGS_NEED_CULLFACE)
    input.isFrontFace = IS_FRONT_VFACE(cullFace, true, false);
#endif
    PositionInputs posInput = GetPositionInput(input.positionSS.xy, _ScreenSize.zw, input.positionSS.z, input.positionSS.w, input.positionRWS);
#ifdef VARYINGS_NEED_POSITION_WS
    float3 V = GetWorldSpaceNormalizeViewDir(input.positionRWS);
#else
    float3 V = float3(1.0, 1.0, 1.0);
#endif
    SurfaceData surfaceData;
    BuiltinData builtinData;
    GetSurfaceAndBuiltinData(input, V, posInput, surfaceData, builtinData); // alpha test (discard)
    VgVisOutput o;
    o.visibility = uint2(VG_TriangleIds[packedInput.firstTriangle + primitive], asuint(packedInput.vmesh.positionCS.z));
#if defined(VG_RASTER_BARY)
#if defined(VARYINGS_NEED_CULLFACE)
    bool front = input.isFrontFace;
#else
    bool front = IS_FRONT_VFACE(vgFace, true, false);
#endif
    uint2 q = (uint2)(saturate(bary.yz) * 32767.0 + 0.5);
    o.barycentrics = q.x | (q.y << 15) | (front ? 1u << 30 : 0u);
#endif
#if defined(VG_RASTER_MOTION)
    o.motion = packedInput.motion;
#endif
    return o;
}

#endif
