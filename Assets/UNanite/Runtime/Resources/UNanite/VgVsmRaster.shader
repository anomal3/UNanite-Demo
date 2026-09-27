// M13 virtual shadow maps: raster of the dirty pages of a cached shadow split (VgWorld.Vsm.cs), drawn
// by UNanite itself in the AfterOpaqueDepthAndNormal pass, before HDRP renders its shadow maps.
// Vertex pulling like Hidden/UNanite/ShadowRaster (384 vertices per record of the split's cached
// slot) with HDRP's render matrix of the split; the render target is a dummy of the split's size
// (colour writes off): every pixel of a dirty page writes the ordered key of its depth (anchor frame,
// HDRP's slope bias applied like SetGlobalDepthBias) into the page pool with InterlockedMax
// (reversed Z: nearest caster wins).
Shader "Hidden/UNanite/VsmRaster"
{
    Properties
    {
        [HideInInspector] _VgCullMode("Cull mode", Float) = 2
        [HideInInspector] _VgZClip("Z clip", Float) = 1
    }

    SubShader
    {
        PackageRequirements { "com.unity.render-pipelines.high-definition" }

        Pass
        {
            Name "VgVsmRaster"

            Cull [_VgCullMode]
            ZClip [_VgZClip]
            ZWrite Off
            ZTest Always
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch switch2
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "../../ShaderLibrary/VgFormat.hlsl"
            #include "../../ShaderLibrary/VgVsm.hlsl"

            StructuredBuffer<VgMeshGpu> VG_Meshes;
            StructuredBuffer<VgInstanceGpu> VG_Instances;
            StructuredBuffer<uint4> VG_ShadowRecords;
            StructuredBuffer<uint4> VG_ShadowViews; // per view slot: (first record, list 0 size, list 1 size, 0)
            ByteAddressBuffer VG_PagePool;
            StructuredBuffer<VgVsmEntry> VG_VsmEntries;
            StructuredBuffer<uint4> VG_VsmPages;
            RWTexture2D<uint> _VgVsmPool : register(u1);

            uint _VgVsmDraw;   // cached view slot << 1 | list
            uint _VgVsmEntry;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float anchorDepth : TEXCOORD0; // directional: device depth of the anchor frame (unclamped)
            };

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings o;
                uint4 view = VG_ShadowViews[_VgVsmDraw >> 1];
                uint record = view.x + ((_VgVsmDraw & 1u) ? view.y : 0u) + vertexID / 384u;
                uint corner = vertexID % 384u;
                uint4 vis = VG_ShadowRecords[record];
                VgCluster c = VgLoadClusterGeometry(VG_PagePool, vis.y, vis.z);
                uint triangleIndex = corner / 3u;
                o.anchorDepth = 0;
                if (triangleIndex >= c.triangleCount)
                {
                    o.positionCS = asfloat(0x7FC00000u).xxxx; // NaN: the triangle is culled
                    return o;
                }
                VgInstanceGpu inst = VG_Instances[vis.x];
                VgMeshGpu mesh = VG_Meshes[inst.meshIndex];
                uint3 t = VgDecodeTriangle(VG_PagePool, c, triangleIndex);
                if (inst.flags & VG_INSTANCE_MIRRORED)
                    t = t.xzy; // negative determinant flips the winding
                uint v = t[corner % 3u];
                float3 positionWS = VgTransformPoint(inst, VgDecodePosition(VG_PagePool, c, v, mesh));
                VgVsmEntry e = VG_VsmEntries[_VgVsmEntry];
                o.positionCS = VsmClip(e, positionWS);
                o.anchorDepth = dot(e.anchorDepth.xyz, positionWS) + e.anchorDepth.w;
                return o;
            }

            void Frag(Varyings input)
            {
                VgVsmEntry e = VG_VsmEntries[_VgVsmEntry];
                bool ortho = (e.window.w & VSM_ENTRY_ORTHO) != 0;
                // HDRP's SetGlobalDepthBias(1, slopeBias): the triangle's depth slope (quad derivatives
                // of one triangle's plane) in the stored depth's units, pushed away from the light
                float z = ortho ? input.anchorDepth : input.positionCS.z;
                float slope = max(abs(ddx_fine(z)), abs(ddy_fine(z)));
                float biased = z;
            #if UNITY_REVERSED_Z
                biased -= e.depth.z * slope;
            #else
                biased += e.depth.z * slope;
            #endif

                int2 g = int2(input.positionCS.xy) + e.window.xy;
                int2 page = int2(VsmPageOf(g.x), VsmPageOf(g.y));
                uint4 p = VG_VsmPages[e.table.x + VsmSlot(e, page)];
                if (p.y != VsmKey(page) || (p.x & VSM_DIRTY) == 0)
                    return; // cached page (or not needed): nothing to write
                uint2 texel = VsmPhysOrigin(p.x & 0xFFFFu) + uint2(g - page * VSM_PAGE);
            #if UNITY_REVERSED_Z
                InterlockedMax(_VgVsmPool[texel], VsmOrderedKey(biased));
            #else
                // non-reversed depth: nearest = smallest; keys are inverted so InterlockedMax still works
                InterlockedMax(_VgVsmPool[texel], ~VsmOrderedKey(biased));
            #endif
            }
            ENDHLSL
        }
    }
}
