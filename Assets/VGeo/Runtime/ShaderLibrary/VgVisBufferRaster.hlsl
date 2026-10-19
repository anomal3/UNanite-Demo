#ifndef VGEO_VG_VIS_BUFFER_RASTER_INCLUDED
#define VGEO_VG_VIS_BUFFER_RASTER_INCLUDED

// M3 visibility-buffer raster, shared by the HDRP and URP sub-shaders of Hidden/VGeo/VisBufferRaster.
// Included after the pipeline's shader variables (TransformWorldToHClip, GetCameraRelativePositionWS).

#include "VgFormat.hlsl"

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

struct VgRasterVaryings
{
    float4 positionCS : SV_POSITION;
    nointerpolation uint id : TEXCOORD0;
};

VgRasterVaryings VgRasterVert(uint vertexID, uint instanceID, bool doubleSided)
{
    VgRasterVaryings o;
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

VgRasterVaryings VgRasterVertSingleSided(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID) { return VgRasterVert(vertexID, instanceID, false); }
VgRasterVaryings VgRasterVertDoubleSided(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID) { return VgRasterVert(vertexID, instanceID, true); }

uint2 VgRasterFrag(VgRasterVaryings input) : SV_Target
{
    return uint2(input.id, asuint(input.positionCS.z));
}

#endif
