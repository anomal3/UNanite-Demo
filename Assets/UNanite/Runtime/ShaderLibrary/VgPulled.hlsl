#ifndef UNANITE_VG_PULLED_INCLUDED
#define UNANITE_VG_PULLED_INCLUDED

// M10 pulled passes, part 2: included by generated VG shader variants after the pass's own
// ShaderPass*.hlsl (which defines Vert / Frag). The pass's vertex entry becomes VgPulledVert.
//
// Pulled bins are drawn as BRG indexed indirect draws of the arena mesh whose indices are packed
// (frame record << 7 | vertex in cluster), written by VgCull.compute Expand. SV_VertexID is that
// index: the record (instance, page, cluster) is read from the frame-wide VG_ShadowRecords, the
// vertex is decoded from the page pool into the pass's AttributesMesh, and the pass's own Vert runs
// the material's vertex stage (vertex graph, wind, custom interpolators) with the instance's object
// matrices (VgPulledSetup.hlsl). No vertex input is declared, the arena's vertex buffer is unused.
//
// With VG_RESOLVE (GBuffer and MotionVectors passes of the bin's resolve twin) the pass instead
// shades visibility-buffer tiles (VgResolveShaderGraph.hlsl).

#if defined(VG_RESOLVE)
#include "VgResolveCommon.hlsl"
#else
#include "VgFormat.hlsl"
StructuredBuffer<VgMeshGpu> VG_Meshes;
StructuredBuffer<VgInstanceGpu> VG_Instances;
ByteAddressBuffer VG_PagePool;
#endif
StructuredBuffer<uint4> VG_ShadowRecords;

#include "VgAttributes.hlsl"

#if defined(VG_RESOLVE)

#include "VgResolveShaderGraph.hlsl"

#else

AttributesMesh VgPullVertex(uint id)
{
    uint4 vis = VG_ShadowRecords[id >> 7];
    VgInstanceGpu inst = VG_Instances[vis.x];
    VgMeshGpu mesh = VG_Meshes[inst.meshIndex];
    VgCluster c = VgLoadCluster(VG_PagePool, vis.y, vis.z);
    return VgAttributes(VgDecodeVertex(VG_PagePool, c, id & 127u, mesh), mesh, vis.x);
}

struct VgPulledAttributes
{
    uint vertexID : SV_VertexID;
    uint instanceID : SV_InstanceID;
};

#if (SHADERPASS == SHADERPASS_MOTION_VECTORS) || (SHADERPASS == SHADERPASS_FORWARD && defined(_WRITE_TRANSPARENT_MOTION_VECTOR))
PackedVaryingsType VgPulledVert(VgPulledAttributes input)
{
    AttributesMesh a = VgPullVertex(input.vertexID);
    AttributesPass p;
    ZERO_INITIALIZE(AttributesPass, p);
    p.previousPositionOS = a.positionOS; // no deformation: object motion comes from the previous matrix
    return Vert(a, p);
}
#else
PackedVaryingsType VgPulledVert(VgPulledAttributes input)
{
    return Vert(VgPullVertex(input.vertexID));
}
#endif

#endif

#endif
