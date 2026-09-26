using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace UNanite.Editor
{
    /// <summary>Per-mesh build options (serialised inside the .vgmesh file).</summary>
    [Serializable]
    public sealed class VgMeshBuildSettings
    {
        [Tooltip("Maximum triangles per cluster (<= 128).")]
        public int maxTriangles = 128;
        [Tooltip("Target clusters per group for simplification (4..24; partitions can be 1/3 larger).")]
        public int groupSize = 16;
        [Tooltip("Position quantisation: step = 2^-precision (mesh units). Use 'auto' for ~2^15 steps across the mesh.")]
        public bool autoPositionPrecision = true;
        public int positionPrecision = 12;
        [Range(6, 15)] public int normalBits = 10;
        [Tooltip("UV quantisation: step = 2^-precision UV units (13 = texel-exact for 8K textures).")]
        [Range(8, 16)] public int uvPrecision = 13;
        [Range(0f, 2f)] public float normalWeight = 0.5f;
        [Range(0f, 2f)] public float uvWeight = 0.25f;
        [Range(0f, 2f)] public float colorWeight = 0.25f;
        public bool keepTangents = true;
        public bool keepLightmapUVs = true;
        public bool keepVertexColors = true;
        [Tooltip("M11: keep the components beyond uv0.xy / uv1.xy: uv0.zw, uv1.zw, uv2, uv3 as half floats (SpeedTree wind and LOD data, foliage shaders). Adds 24 bytes per vertex.")]
        public bool keepExtraUVs = false;
        [Tooltip("M11: no simplified DAG levels, only the source clusters (culled per cluster). For foliage whose alpha-tested cards thin out when simplified: LODs are the prefab's own meshes, chosen per instance (VirtualGeometryTerrainTrees).")]
        public bool noSimplification = false;
        [Tooltip("More uniform triangle density; use for meshes that will be deformed.")]
        public bool regularize = false;
        [Tooltip("0 = all hardware threads.")]
        public int threadCount = 0;
        [Tooltip("M7: keep the streamable pages in a side file of the import artifact, read from disk on demand (players: StreamingAssets). Off = embedded in the asset (always in memory).")]
        public bool pageFile = true;

        public UnvgBuildSettings ToNative()
        {
            var s = NativeBuilder.DefaultSettings();
            s.maxTriangles = (uint)Mathf.Clamp(maxTriangles, 8, 128);
            s.maxVertices = 128;
            s.groupSize = (uint)Mathf.Clamp(groupSize, 4, 32);
            s.threadCount = (uint)Mathf.Max(0, threadCount);
            s.positionPrecision = autoPositionPrecision ? UnvgBuildSettings.AutoPrecision : positionPrecision;
            s.normalBits = (uint)normalBits;
            s.uvPrecision = (uint)uvPrecision;
            s.normalWeight = normalWeight;
            s.uvWeight = uvWeight;
            s.colorWeight = colorWeight;
            s.flags = UnvgBuildSettings.FlagPermissive | UnvgBuildSettings.FlagSloppyFallback;
            if (keepTangents) s.flags |= UnvgBuildSettings.FlagKeepTangents;
            if (keepLightmapUVs) s.flags |= UnvgBuildSettings.FlagKeepUv1;
            if (keepVertexColors) s.flags |= UnvgBuildSettings.FlagKeepColors;
            if (keepExtraUVs) s.flags |= UnvgBuildSettings.FlagKeepExtraUv;
            if (noSimplification) s.flags |= UnvgBuildSettings.FlagNoSimplify;
            if (regularize) s.flags |= UnvgBuildSettings.FlagRegularize;
            return s;
        }
    }

    public static class VirtualGeometryBuilder
    {
        // |uv| beyond this cannot be quantised at the finest uvPrecision (16) within the format's 31-bit
        // cluster ranges; real texture coordinates never get near it
        const float MaxAbsUv = 8192f;

        // Some exporters leave garbage in channels the renderer never reads (the Enemies demo's SpeedTree
        // trees carry +-FLT_MAX in their unused lightmap UVs): non-finite values become 0 and the rest
        // is clamped, so such meshes still build.
        static void SanitizeUvs(float[] uv, Mesh mesh, int channel, List<string> warnings)
        {
            int fixedCount = 0;
            for (int i = 0; i < uv.Length; ++i)
            {
                float v = uv[i];
                if (float.IsNaN(v) || float.IsInfinity(v))
                    uv[i] = 0f;
                else if (v > MaxAbsUv || v < -MaxAbsUv)
                    uv[i] = Mathf.Clamp(v, -MaxAbsUv, MaxAbsUv);
                else
                    continue;
                fixedCount++;
            }
            if (fixedCount > 0)
                warnings?.Add($"{mesh.name}: {fixedCount} out-of-range uv{channel} values replaced (|uv| > {MaxAbsUv} or not finite)");
        }

        /// <summary>Extracts triangle streams from a mesh (works for non-readable meshes in the editor).</summary>
        public static VgNativeBuilder.MeshStreams ExtractStreams(Mesh mesh, List<string> warnings = null)
        {
            using var dataArray = Mesh.AcquireReadOnlyMeshData(mesh);
            var data = dataArray[0];
            int vertexCount = data.vertexCount;
            var streams = new VgNativeBuilder.MeshStreams();

            using (var positions = new NativeArray<Vector3>(vertexCount, Allocator.Temp))
            {
                data.GetVertices(positions);
                streams.positions = positions.Reinterpret<float>(12).ToArray();
            }

            if (data.HasVertexAttribute(VertexAttribute.Tangent))
            {
                using var tangents = new NativeArray<Vector4>(vertexCount, Allocator.Temp);
                data.GetTangents(tangents);
                streams.tangents = tangents.Reinterpret<float>(16).ToArray();
            }

            if (data.HasVertexAttribute(VertexAttribute.TexCoord0))
            {
                using var uv = new NativeArray<Vector2>(vertexCount, Allocator.Temp);
                data.GetUVs(0, uv);
                streams.uv0 = uv.Reinterpret<float>(8).ToArray();
                SanitizeUvs(streams.uv0, mesh, 0, warnings);
            }

            if (data.HasVertexAttribute(VertexAttribute.TexCoord1))
            {
                using var uv = new NativeArray<Vector2>(vertexCount, Allocator.Temp);
                data.GetUVs(1, uv);
                streams.uv1 = uv.Reinterpret<float>(8).ToArray();
                SanitizeUvs(streams.uv1, mesh, 1, warnings);
            }

            // M11: components beyond uv0.xy / uv1.xy (SpeedTree 8 wind and LOD data)
            bool anyExtra = false;
            var extra = new float[vertexCount * 12];
            for (int channel = 0; channel <= 3; ++channel)
            {
                var attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
                if (!data.HasVertexAttribute(attribute) || (channel < 2 && data.GetVertexAttributeDimension(attribute) <= 2))
                    continue;
                anyExtra = true;
                using var uv = new NativeArray<Vector4>(vertexCount, Allocator.Temp);
                data.GetUVs(channel, uv);
                for (int v = 0; v < vertexCount; ++v)
                {
                    var u = uv[v];
                    if (channel < 2)
                    {
                        extra[v * 12 + channel * 2 + 0] = u.z;
                        extra[v * 12 + channel * 2 + 1] = u.w;
                    }
                    else
                        for (int k = 0; k < 4; ++k)
                            extra[v * 12 + 4 + (channel - 2) * 4 + k] = u[k];
                }
            }
            if (anyExtra)
                streams.extraUv = extra;

            if (data.HasVertexAttribute(VertexAttribute.Color))
            {
                using var colors = new NativeArray<Color>(vertexCount, Allocator.Temp);
                data.GetColors(colors);
                streams.colors = colors.Reinterpret<float>(16).ToArray();
            }

            var indices = new List<uint>();
            var materials = new List<uint>();
            for (int s = 0; s < data.subMeshCount; ++s)
            {
                var desc = data.GetSubMesh(s);
                if (desc.topology != MeshTopology.Triangles)
                {
                    warnings?.Add($"Submesh {s} of '{mesh.name}' uses {desc.topology} topology and is skipped (only triangles are supported).");
                    continue;
                }
                using var idx = new NativeArray<int>(desc.indexCount, Allocator.Temp);
                data.GetIndices(idx, s, true);
                for (int i = 0; i < idx.Length; ++i)
                    indices.Add((uint)idx[i]);
                for (int t = 0; t < idx.Length / 3; ++t)
                    materials.Add((uint)s);
            }
            streams.indices = indices.ToArray();
            streams.triangleMaterial = materials.ToArray();
            streams.materialCount = Mathf.Max(1, data.subMeshCount);

            if (data.HasVertexAttribute(VertexAttribute.Normal))
            {
                using var normals = new NativeArray<Vector3>(vertexCount, Allocator.Temp);
                data.GetNormals(normals);
                streams.normals = normals.Reinterpret<float>(12).ToArray();
            }
            else
            {
                warnings?.Add($"'{mesh.name}' has no normals; computing area-weighted smooth normals.");
                streams.normals = ComputeNormals(streams.positions, streams.indices);
            }

            return streams;
        }

        static float[] ComputeNormals(float[] positions, uint[] indices)
        {
            var normals = new float[positions.Length];
            for (int i = 0; i < indices.Length; i += 3)
            {
                uint a = indices[i], b = indices[i + 1], c = indices[i + 2];
                var pa = new Vector3(positions[a * 3], positions[a * 3 + 1], positions[a * 3 + 2]);
                var pb = new Vector3(positions[b * 3], positions[b * 3 + 1], positions[b * 3 + 2]);
                var pc = new Vector3(positions[c * 3], positions[c * 3 + 1], positions[c * 3 + 2]);
                var n = Vector3.Cross(pb - pa, pc - pa);
                foreach (uint v in new[] { a, b, c })
                {
                    normals[v * 3] += n.x;
                    normals[v * 3 + 1] += n.y;
                    normals[v * 3 + 2] += n.z;
                }
            }
            return normals;
        }

        public static VgBuildReport ToReport(in UnvgBuildStats s) => VgNativeBuilder.ToReport(s);

        /// <summary>Builds a VirtualGeometryMesh from a Unity mesh. Returns null and sets `error` on failure.</summary>
        public static VirtualGeometryMesh Build(Mesh mesh, VgMeshBuildSettings settings, out string error, List<string> warnings = null, Func<float, bool> onProgress = null)
        {
            error = null;
            if (!NativeBuilder.IsAvailable)
            {
                error = NativeBuilder.LoadError;
                return null;
            }

            var streams = ExtractStreams(mesh, warnings);
            if (streams.indices.Length < 3)
            {
                error = $"'{mesh.name}' has no triangles";
                return null;
            }

            var result = NativeBuilder.Build(streams, settings.ToNative(), onProgress);
            if (!result.success)
            {
                error = result.cancelled ? "cancelled" : result.error;
                return null;
            }

            var report = ToReport(result.stats);
            report.contentHash = Hash128.Compute(result.blob).ToString();

            var asset = ScriptableObject.CreateInstance<VirtualGeometryMesh>();
            asset.name = mesh.name;
            asset.Initialize(result.blob, mesh.name, streams.materialCount, report);
            return asset;
        }
    }
}
