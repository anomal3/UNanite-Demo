// M3 visibility-buffer raster ("VG.RasterVisBuffer"): hardware rasterisation of the visible
// clusters of resolve-capable bins with vertex pulling from the page pool. Drawn by VgWorld from
// the BeforeRendering custom pass into (visibility buffer, HDRP camera depth).
//
//   DrawProceduralIndirect: vertexCountPerInstance = 384 (128 triangles x 3 corners),
//   instanceCount = clusters in the raster list (VG_RasterArgs, written by VgCull.compute).
//   Pass 0 (Cull Back): list 0 (hardware single-sided), pass 1 (Cull Off): list 1 (double-sided).
//   record = VG_RasterLists[_VgListBase + list * _VgListCapacity + first + instance], first = 0 for
//   phase 1 and the phase-1 list size (VG_PhaseState) for the M4 phase-2 draws.
Shader "Hidden/UNanite/VisBufferRaster"
{
    HLSLINCLUDE
    #pragma target 4.5
    #pragma only_renderers d3d11 vulkan metal playstation xboxone xboxseries switch

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
    #include "../../ShaderLibrary/VgFormat.hlsl"

    StructuredBuffer<VgMeshGpu> VG_Meshes;
    StructuredBuffer<VgInstanceGpu> VG_Instances;
    StructuredBuffer<uint4> VG_Visible;
    StructuredBuffer<uint> VG_BinFlags;
    ByteAddressBuffer VG_PagePool;
    StructuredBuffer<uint> VG_PhaseState;
    StructuredBuffer<uint> VG_RasterLists;

    uint _VgListBase;
    uint _VgListCapacity;
    uint _VgRasterPhase;     // 0: phase 1, 1: phase 2 (entries after the phase-1 list sizes)
    uint _VgPhaseStateBase;

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        nointerpolation uint id : TEXCOORD0;
    };

    Varyings RasterVert(uint vertexID, uint instanceID, bool doubleSided)
    {
        Varyings o;
        o.positionCS = asfloat(0x7FC00000u).xxxx; // NaN: the triangle is dropped
        o.id = 0;

        uint list = doubleSided ? 1 : 0;
        uint first = _VgRasterPhase != 0 ? VG_PhaseState[_VgPhaseStateBase + 4 + list] : 0; // P_PHASE1_LIST
        uint record = VG_RasterLists[_VgListBase + list * _VgListCapacity + first + instanceID];
        uint triIndex = vertexID / 3;
        uint corner = vertexID - triIndex * 3;

        uint4 vis = VG_Visible[record];
        if ((VG_BinFlags[vis.w] & (VG_BIN_RESOLVE | VG_BIN_PROGRAMMABLE)) != VG_BIN_RESOLVE)
            return o; // expanded / pulled, or rasterised by its own material (M11 VgVisBuffer pass)
        VgCluster c = VgLoadClusterGeometry(VG_PagePool, vis.y, vis.z);
        if (triIndex >= c.triangleCount)
            return o;

        VgInstanceGpu inst = VG_Instances[vis.x];
        VgMeshGpu mesh = VG_Meshes[inst.meshIndex];
        uint3 t = VgDecodeTriangle(VG_PagePool, c, triIndex);
        if (inst.flags & VG_INSTANCE_MIRRORED)
            t = t.xzy; // negative determinant flips the winding
        uint v = corner == 0 ? t.x : (corner == 1 ? t.y : t.z);

        float3 positionWS = VgTransformPoint(inst, VgDecodePosition(VG_PagePool, c, v, mesh));
        o.positionCS = TransformWorldToHClip(GetCameraRelativePositionWS(positionWS));
        o.id = VgPackVisibility(record, triIndex);
        return o;
    }

    Varyings VertSingleSided(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID) { return RasterVert(vertexID, instanceID, false); }
    Varyings VertDoubleSided(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID) { return RasterVert(vertexID, instanceID, true); }

    uint2 Frag(Varyings input) : SV_Target
    {
        return uint2(input.id, asuint(input.positionCS.z));
    }
    ENDHLSL

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
            #pragma vertex VertSingleSided
            #pragma fragment Frag
            ENDHLSL
        }

        Pass
        {
            Name "VisBufferDoubleSided"
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex VertDoubleSided
            #pragma fragment Frag
            ENDHLSL
        }
    }
}
