#ifndef UNANITE_VG_ATTRIBUTES_INCLUDED
#define UNANITE_VG_ATTRIBUTES_INCLUDED

// M10/M11: a decoded VG vertex as the AttributesMesh of a generated VG variant's pass (pulled
// passes, Shader Graph resolve, programmable raster). Requires VgFormat.hlsl and the pass's
// AttributesMesh / ATTRIBUTES_NEED_* defines.

StructuredBuffer<VgLodSwitch> VG_LodSwitches; // M8 HLOD switch records (M11: smooth LOD morph)
float4 VG_LodMorph; // M11: xyz position of the camera being rendered, w 1 / its LOD factor (0: off)
StructuredBuffer<uint2> VG_LodFadeState; // M11 animated LOD crossfade (VgCull.compute UpdateLodFade)
float4 VG_LodFadeParams; // x: first state of the camera's slot (-1: none), y: record count, z: time
float4 VG_RasterMotion;  // M11: x squared camera distance within which the programmable raster re-evaluates the vertex graph for motion

// M11 SpeedTree smooth LOD (LODFadeMode.SpeedTree): unity_LODFade.x of an instance's LOD as the
// crossfade byte of UnityDOTSInstancing.hlsl (0..127): 0 where the LOD starts, 127 at the switch that
// ends it (lodParent), linear in the camera distance like Unity's; the graph lerps its vertices to
// the next LOD's positions (uv2). Camera LOD in every pass (shadows too) keeps the shapes identical.
int VgLodMorphCrossfade(uint instance)
{
    VgInstanceGpu inst = VG_Instances[instance];
    if (inst.lodParent == VG_INVALID || VG_LodMorph.w <= 0.0)
        return 0;
    VgLodSwitch s = VG_LodSwitches[inst.lodParent];
    if (s.morphStart <= 0.0 || s.error <= s.morphStart)
        return 0;
    float x = distance(s.sphere.xyz, VG_LodMorph.xyz) * VG_LodMorph.w; // error that projects to one unit here
    return (int)(saturate((x - s.morphStart) / (s.error - s.morphStart)) * 127.0 + 0.5);
}

// M11 LOD crossfade of record `id` as seen by the camera being rendered: x the fraction of pixels the
// fine side keeps (the coarse side keeps the others), y 1 for the coarse side. False: not fading.
bool VgLodFade(uint id, bool coarseSide, out float2 dither)
{
    VgLodSwitch s = VG_LodSwitches[id];
    dither = float2(1.0, 0.0);
    float keepFine;
    if (s.fadeDuration > 0.0)
    {
        if (VG_LodFadeParams.x < 0.0)
            return false;
        uint2 state = VG_LodFadeState[(uint)VG_LodFadeParams.x + id];
        float t = (VG_LodFadeParams.z - asfloat(state.y)) / s.fadeDuration;
        if ((state.x & 2u) == 0 || t >= 1.0)
            return false;
        keepFine = (state.x & 1u) ? 1.0 - saturate(t) : saturate(t); // from the side left to the side switched to
    }
    else if (s.fadeStart > 0.0 && s.fadeStart < s.error && VG_LodMorph.w > 0.0)
        keepFine = saturate((s.error - distance(s.sphere.xyz, VG_LodMorph.xyz) * VG_LodMorph.w) / (s.error - s.fadeStart));
    else
        return false;
    dither = float2(keepFine, coarseSide ? 1.0 : 0.0);
    return true;
}

// M11 LOD crossfade of an instance (the record it ends, else the one it starts); (1, 0): not fading.
float2 VgLodDither(uint instance)
{
    VgInstanceGpu inst = VG_Instances[instance];
    float2 dither;
    if (inst.lodParent != VG_INVALID && VgLodFade(inst.lodParent, false, dither))
        return dither;
    if (inst.lodSelf != VG_INVALID && VgLodFade(inst.lodSelf, true, dither))
        return dither;
    return float2(1.0, 0.0);
}

// A decoded VG vertex as the pass's mesh attributes; `instance` selects the object matrices.
AttributesMesh VgAttributes(VgVertex v, VgMeshGpu mesh, uint instance)
{
    AttributesMesh a;
    ZERO_INITIALIZE(AttributesMesh, a);
    a.positionOS = v.position;
    VgInstanceGpu inst = VG_Instances[instance];
    if (inst.flags & VG_INSTANCE_DENSITY_LOD)
        a.positionOS *= VgDensityLodScale(inst); // M11 density LOD (VgFormat.hlsl)
#ifdef ATTRIBUTES_NEED_NORMAL
    a.normalOS = v.normal;
#endif
#ifdef ATTRIBUTES_NEED_TANGENT
    a.tangentOS = v.tangent;
#endif
#ifdef ATTRIBUTES_NEED_TEXCOORD0
    a.uv0 = float4(v.uv0, v.uv0zw_uv1zw.xy);
#endif
#ifdef ATTRIBUTES_NEED_TEXCOORD1
    // like Unity's lightmap convention, meshes without a second UV set read uv0
    a.uv1 = float4((mesh.meshFlags & VG_MESH_HAS_UV1) ? v.uv1 : v.uv0, v.uv0zw_uv1zw.zw);
#endif
#ifdef ATTRIBUTES_NEED_TEXCOORD2
    a.uv2 = v.uv2; // M11 (zero unless the mesh keeps extra UVs)
#endif
#ifdef ATTRIBUTES_NEED_TEXCOORD3
    a.uv3 = v.uv3;
#endif
#ifdef ATTRIBUTES_NEED_COLOR
    a.color = float4(v.color & 0xFF, (v.color >> 8) & 0xFF, (v.color >> 16) & 0xFF, v.color >> 24) / 255.0;
#endif
#if UNITY_ANY_INSTANCING_ENABLED
    a.instanceID = instance; // -> UNITY_SETUP_INSTANCE_ID -> VgSetupInstance
#endif
    return a;
}

#endif
