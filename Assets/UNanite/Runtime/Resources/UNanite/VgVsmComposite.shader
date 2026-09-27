// M13 virtual shadow maps: copies the cached pages of a shadow split into HDRP's shadow atlas. One
// BRG procedural draw per cached split inside HDRP's own shadow pass (atlas viewport, matrices and
// state are HDRP's): a quad per needed page (VgVsm.compute Mark list, 6 vertices each), the pixel
// shader turns the page's stored depth into this frame's device depth (SV_Depth; pancaking of
// directional lights by clamping). Pixels without a caster are discarded: the atlas keeps its clear
// value and the casters HDRP draws itself (skinned meshes, MeshRenderers, VG materials that are not
// plain depth) depth-test against the composited depth as usual.
Shader "Hidden/UNanite/VsmComposite"
{
    SubShader
    {
        PackageRequirements { "com.unity.render-pipelines.high-definition" }
        Tags { "RenderPipeline" = "HDRenderPipeline" }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            Cull Off
            ZClip Off
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch switch2
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma vertex Vert
            #pragma fragment Frag

            #define SHADERPASS SHADERPASS_SHADOWS
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/ShaderPass.cs.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
            #include "../../ShaderLibrary/VgVsm.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _VgVsmUnused;
            CBUFFER_END

            StructuredBuffer<VgVsmEntry> VG_VsmEntries;
            StructuredBuffer<uint4> VG_VsmPages;
            StructuredBuffer<uint> VG_VsmList;
            Texture2D<uint> _VgVsmPool;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 local : TEXCOORD0;                  // local texel coordinates (pixel centres at .5)
                nointerpolation uint entry : TEXCOORD1;
                nointerpolation uint slotIndex : TEXCOORD2; // VG_VsmPages index of the page
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
            #if defined(UNITY_DOTS_INSTANCING_ENABLED)
                UnitySetupInstanceID(UNITY_GET_INSTANCE_ID(input));
                SetupDOTSVisibleInstancingData();
                uint entry = GetDOTSIndirectVisibleIndex(); // VgWorld: visible offset = cache entry
            #else
                UNITY_SETUP_INSTANCE_ID(input);
                uint entry = 0; // only drawn through BatchRendererGroup
            #endif
                VgVsmEntry e = VG_VsmEntries[entry];
                uint idx = VG_VsmList[e.table.x + input.vertexID / 6u];
                uint corner = input.vertexID % 6u;
                // two triangles: (0,0) (1,0) (0,1) | (0,1) (1,0) (1,1)
                float2 k = float2(corner == 1 || corner == 4 || corner == 5 ? 1 : 0, corner == 2 || corner == 3 || corner == 5 ? 1 : 0);
                int2 page = VsmKeyPage(VG_VsmPages[idx].y);
                float res = (float)e.window.z;
                // atlas texel t shows cached texel floor(t + frac) + window (cascades are not texel-snapped)
                float2 frac = float2(e.cullToLocal0.w, e.cullToLocal1.w);
                float2 local = clamp(float2(page * VSM_PAGE - e.window.xy) - frac + k * VSM_PAGE, 0.0, res);
                // same rows as HDRP's render matrix of the split (row 0 at ndc.y = +1)
                o.positionCS = float4(local.x / res * 2.0 - 1.0, 1.0 - local.y / res * 2.0, 0.5, 1.0);
                o.local = local;
                o.entry = entry;
                o.slotIndex = idx;
                return o;
            }

            void Frag(Varyings input, out float outDepth : SV_Depth)
            {
                VgVsmEntry e = VG_VsmEntries[input.entry];
                uint4 p = VG_VsmPages[input.slotIndex];
                int2 page = VsmKeyPage(p.y);
                // (the last row / column of a shifted grid repeats the rendered edge texel)
                int2 g = min(int2(floor(input.local + float2(e.cullToLocal0.w, e.cullToLocal1.w))), e.window.z - 1) + e.window.xy;
                uint2 texel = VsmPhysOrigin(p.x & 0xFFFFu) + uint2(clamp(g - page * VSM_PAGE, 0, VSM_PAGE - 1));
                uint key = _VgVsmPool.Load(int3(texel, 0));
                if (key == 0)
                    discard; // no VG caster: the atlas keeps its clear value
            #if !UNITY_REVERSED_Z
                key = ~key;
            #endif
                float d = VsmOrderedFloat(key) * e.depth.x + e.depth.y;
                if ((e.window.w & VSM_ENTRY_ZCLIP) == 0)
                    d = saturate(d); // directional pancaking (HDRP renders its cascades with ZClip off)
                outDepth = d;
            }
            ENDHLSL
        }
    }
}
