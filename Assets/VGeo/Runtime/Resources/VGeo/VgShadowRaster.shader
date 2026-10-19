// M6 vertex-pulled shadow raster: HDRP's shadow pass draws the visible clusters of a shadow split
// through BRG procedural indirect draws with this material (one draw per split and cull mode), so
// no vertex expansion is needed. HDRP owns everything else: atlas viewport, split matrices, slope
// bias (SetGlobalDepthBias) and pancaking (_ZClip).
//
//   384 vertices per record of the split's list (VgCull.compute CompactShadow / PrepareShadowRaster):
//   SV_VertexID / 384 -> VG_ShadowRecords[view base (+ list 0 size) + that], the rest -> triangle and
//   corner; triangles past the cluster's count are culled (NaN position). The draw's BRG visible
//   offset is its (view slot << 1 | list). No index buffer and no per-triangle index writes.
Shader "Hidden/VGeo/ShadowRaster"
{
    Properties
    {
        [HideInInspector] _VgCullMode("Cull mode", Float) = 2
    }

    SubShader
    {
        // HDRP only: URP projects skip it instead of failing on the HDRP includes
        PackageRequirements { "com.unity.render-pipelines.high-definition" }
        Tags { "RenderPipeline" = "HDRenderPipeline" }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            Cull [_VgCullMode]
            ZClip [_ZClip]
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
            #include "../../ShaderLibrary/VgFormat.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _VgCullMode;
            CBUFFER_END

            StructuredBuffer<VgMeshGpu> VG_Meshes;
            StructuredBuffer<VgInstanceGpu> VG_Instances;
            StructuredBuffer<uint4> VG_ShadowRecords;
            StructuredBuffer<uint4> VG_ShadowViews; // per view slot: (first record, list 0 size, list 1 size, 0)
            ByteAddressBuffer VG_PagePool;
            StructuredBuffer<VgGroup> VG_Groups;
            StructuredBuffer<VgLodSwitch> VG_LodSwitches;

            // How far the drawn cluster may lie from the full-detail surface: the error of the group it
            // was simplified from, and for an HLOD node (terrain tiles) its switch error (the node's own
            // source is a cut of its children). The camera draws finer clusters of the same surface, so a
            // caster left in place shadows it wherever the coarse surface runs above it (concave slopes
            // at low sun: whole hillsides in shadow); pushed that far away from the light it never does.
            float CasterError(VgInstanceGpu inst, VgMeshGpu mesh, uint pageAddress, uint clusterInPage)
            {
                uint refined = VG_PagePool.Load(pageAddress + VG_PAGE_HEADER_SIZE + clusterInPage * VG_CLUSTER_HEADER_SIZE + 60);
                float e = 0.0;
                if (refined != VG_INVALID)
                {
                    float g = VG_Groups[mesh.groupBase + refined].error;
                    e = g < VG_FLT_MAX ? g * inst.maxScale : 0.0;
                }
                if (inst.lodSelf != VG_INVALID)
                    e = max(e, VG_LodSwitches[inst.lodSelf].error);
                return e;
            }

            struct Attributes
            {
                uint vertexID : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
            #if defined(UNITY_DOTS_INSTANCING_ENABLED)
                UnitySetupInstanceID(UNITY_GET_INSTANCE_ID(input));
                SetupDOTSVisibleInstancingData();
                uint draw = GetDOTSIndirectVisibleIndex(); // VgWorld: visible offset = view slot << 1 | list
            #else
                UNITY_SETUP_INSTANCE_ID(input);
                uint draw = 0; // only drawn through BatchRendererGroup
            #endif
                uint4 view = VG_ShadowViews[draw >> 1];
                uint record = view.x + ((draw & 1u) ? view.y : 0u) + input.vertexID / 384u;
                uint corner = input.vertexID % 384u;
                uint4 vis = VG_ShadowRecords[record];
                VgCluster c = VgLoadClusterGeometry(VG_PagePool, vis.y, vis.z);
                uint triangleIndex = corner / 3u;
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
                float3 positionVS = TransformWorldToView(GetCameraRelativePositionWS(positionWS));
                float error = CasterError(inst, mesh, vis.y, vis.z);
                if (error > 0.0)
                {
                    // away from the light: along the view axis (directional) or the ray from the light (spot,
                    // point); the split's own projection tells (unity_OrthoParams belongs to the camera)
                    if (UNITY_MATRIX_P[3][3] < 0.5)
                        positionVS *= 1.0 + error / max(length(positionVS), 1e-4);
                    else
                        positionVS.z -= error;
                }
                o.positionCS = TransformWViewToHClip(positionVS);
                return o;
            }

            void Frag(Varyings input)
            {
            }
            ENDHLSL
        }
    }

    SubShader
    {
        // URP: drawn inside URP's shadow caster passes (main light cascades, additional light
        // shadows), which set the split's view / projection and URP's own depth and normal bias
        // (_ShadowBias, _LightDirection / _LightPosition) like for ShadowCasterPass.hlsl
        PackageRequirements { "com.unity.render-pipelines.universal" }
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            Cull [_VgCullMode]
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch switch2
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "../../ShaderLibrary/VgFormat.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _VgCullMode;
            CBUFFER_END

            // set by URP's shadow caster passes (ShadowUtils.SetupShadowCasterConstantBuffer)
            float3 _LightDirection;
            float3 _LightPosition;

            StructuredBuffer<VgMeshGpu> VG_Meshes;
            StructuredBuffer<VgInstanceGpu> VG_Instances;
            StructuredBuffer<uint4> VG_ShadowRecords;
            StructuredBuffer<uint4> VG_ShadowViews; // per view slot: (first record, list 0 size, list 1 size, 0)
            ByteAddressBuffer VG_PagePool;
            StructuredBuffer<VgGroup> VG_Groups;
            StructuredBuffer<VgLodSwitch> VG_LodSwitches;

            // see the HDRP sub-shader: casters are pushed away from the light by their LOD error
            float CasterError(VgInstanceGpu inst, VgMeshGpu mesh, uint pageAddress, uint clusterInPage)
            {
                uint refined = VG_PagePool.Load(pageAddress + VG_PAGE_HEADER_SIZE + clusterInPage * VG_CLUSTER_HEADER_SIZE + 60);
                float e = 0.0;
                if (refined != VG_INVALID)
                {
                    float g = VG_Groups[mesh.groupBase + refined].error;
                    e = g < VG_FLT_MAX ? g * inst.maxScale : 0.0;
                }
                if (inst.lodSelf != VG_INVALID)
                    e = max(e, VG_LodSwitches[inst.lodSelf].error);
                return e;
            }

            struct Attributes
            {
                uint vertexID : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
            #if defined(UNITY_DOTS_INSTANCING_ENABLED)
                UnitySetupInstanceID(UNITY_GET_INSTANCE_ID(input));
                SetupDOTSVisibleInstancingData();
                uint draw = GetDOTSIndirectVisibleIndex(); // VgWorld: visible offset = view slot << 1 | list
            #else
                UNITY_SETUP_INSTANCE_ID(input);
                uint draw = 0; // only drawn through BatchRendererGroup
            #endif
                uint4 view = VG_ShadowViews[draw >> 1];
                uint record = view.x + ((draw & 1u) ? view.y : 0u) + input.vertexID / 384u;
                uint corner = input.vertexID % 384u;
                uint4 vis = VG_ShadowRecords[record];
                VgCluster c = VgLoadClusterGeometry(VG_PagePool, vis.y, vis.z);
                uint triangleIndex = corner / 3u;
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
                float3 normalOS;
                float3 positionWS = VgTransformPoint(inst, VgDecodePositionNormal(VG_PagePool, c, v, mesh, normalOS));
                float3 normalWS = normalize(VgTransformNormal(inst, normalOS));

            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif
                // a coarser caster never shadows the finer surface the camera draws (see CasterError)
                float error = CasterError(inst, mesh, vis.y, vis.z);
                positionWS -= lightDirectionWS * error;

                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                o.positionCS = ApplyShadowClamping(positionCS);
                return o;
            }

            void Frag(Varyings input)
            {
            }
            ENDHLSL
        }
    }
}
