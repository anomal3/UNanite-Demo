using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace UNanite
{
    public enum VgDebugMode
    {
        Clusters = 0,
        Groups = 1,
        LodLevel = 2,
        Triangles = 3,
        Materials = 4,
    }

    /// <summary>
    /// M1 debug view: selects the LOD cut on the CPU (same rule as the GPU) for every camera that
    /// renders this object and draws it with a colour-coded debug shader. "Freeze cut" keeps the
    /// current selection so it can be inspected up close.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("UNanite/Virtual Geometry Debug View")]
    public sealed class VirtualGeometryDebugView : MonoBehaviour
    {
        public VirtualGeometryMesh mesh;
        public VgDebugMode mode = VgDebugMode.Clusters;
        [Tooltip("Maximum projected simplification error in pixels.")]
        [Range(0.1f, 32f)] public float pixelError = 1f;
        [Tooltip("Keep the current cut (fly closer to inspect it).")]
        public bool freezeCut;
        [Tooltip("Show a single DAG level instead of the view-dependent cut (-1 = cut).")]
        public int forceLevel = -1;

        [NonSerialized] public int lastClusterCount;
        [NonSerialized] public int lastTriangleCount;

        struct CameraCut
        {
            public Mesh mesh;
            public int hash;
            public List<int> clusters;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DebugVertex
        {
            public Vector3 position;
            public Vector3 normal;
            public Vector4 ids;
        }

        static Material s_Material;
        static readonly int s_ModeId = Shader.PropertyToID("_Mode");
        static readonly int s_LevelCountId = Shader.PropertyToID("_LevelCount");

        readonly Dictionary<Camera, CameraCut> m_Cuts = new Dictionary<Camera, CameraCut>();
        readonly Dictionary<int, (List<VgMeshReader.DecodedVertex> v, List<int> t)> m_Decoded = new Dictionary<int, (List<VgMeshReader.DecodedVertex>, List<int>)>();
        VirtualGeometryMesh m_DecodedFor;
        MaterialPropertyBlock m_Props;

        void OnEnable() => RenderPipelineManager.beginCameraRendering += OnBeginCamera;

        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            foreach (var c in m_Cuts.Values)
                DestroyImmediate(c.mesh);
            m_Cuts.Clear();
            m_Decoded.Clear();
        }

        void OnBeginCamera(ScriptableRenderContext context, Camera camera)
        {
            if (mesh == null || !mesh.IsValid || camera.cameraType == CameraType.Preview || camera.cameraType == CameraType.Reflection)
                return;

            var reader = mesh.Reader;
            if (m_DecodedFor != mesh)
            {
                m_Decoded.Clear();
                m_DecodedFor = mesh;
            }

            if (!m_Cuts.TryGetValue(camera, out var cut))
                cut = new CameraCut { clusters = new List<int>(), hash = 0 };

            if (!freezeCut || cut.mesh == null)
            {
                Select(reader, camera, cut.clusters);
                int hash = HashCut(cut.clusters, (int)mode);
                if (cut.mesh == null || hash != cut.hash)
                {
                    cut.hash = hash;
                    if (cut.mesh == null)
                        cut.mesh = new Mesh { name = "UNanite Debug Cut", hideFlags = HideFlags.HideAndDontSave };
                    BuildMesh(reader, cut.clusters, cut.mesh);
                }
            }
            m_Cuts[camera] = cut;

            if (s_Material == null)
            {
                var shader = Shader.Find("Hidden/UNanite/DebugView");
                if (shader == null)
                    return;
                s_Material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }

            m_Props ??= new MaterialPropertyBlock();
            m_Props.SetInt(s_ModeId, (int)mode);
            m_Props.SetInt(s_LevelCountId, reader.Levels.Length);

            var rp = new RenderParams(s_Material)
            {
                camera = camera,
                layer = gameObject.layer,
                worldBounds = TransformBounds(reader.LocalBounds, transform.localToWorldMatrix),
                matProps = m_Props,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
            };
            Graphics.RenderMesh(rp, cut.mesh, 0, transform.localToWorldMatrix);

            lastClusterCount = cut.clusters.Count;
            lastTriangleCount = cut.mesh.vertexCount > 0 ? (int)cut.mesh.GetIndexCount(0) / 3 : 0;
        }

        void Select(VgMeshReader reader, Camera camera, List<int> clusters)
        {
            if (forceLevel >= 0)
            {
                clusters.Clear();
                for (int c = 0; c < reader.Clusters.Length; ++c)
                {
                    ref readonly var h = ref reader.Clusters[c];
                    // a level's clusters are those members of groups at that depth
                    if (h.lodLevel == (uint)forceLevel)
                        clusters.Add(c);
                }
                return;
            }

            var view = VgLodView.FromCamera(camera, pixelError);
            VgLodCut.Select(reader, transform.localToWorldMatrix, view, clusters);
        }

        static int HashCut(List<int> clusters, int mode)
        {
            unchecked
            {
                int h = (int)2166136261u ^ mode;
                foreach (int c in clusters)
                    h = (h ^ c) * 16777619;
                return h ^ clusters.Count;
            }
        }

        void BuildMesh(VgMeshReader reader, List<int> clusters, Mesh target)
        {
            int vertexCount = 0, indexCount = 0;
            foreach (int c in clusters)
            {
                vertexCount += reader.Clusters[c].VertexCount;
                indexCount += reader.Clusters[c].TriangleCount * 3;
            }

            var vertices = new NativeArray<DebugVertex>(vertexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var indices = new NativeArray<int>(indexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            int vo = 0, io = 0;
            foreach (int c in clusters)
            {
                if (!m_Decoded.TryGetValue(c, out var d))
                {
                    d = (new List<VgMeshReader.DecodedVertex>(), new List<int>());
                    reader.DecodeCluster(c, d.v, d.t);
                    m_Decoded[c] = d;
                }
                ref readonly var h = ref reader.Clusters[c];
                var ids = new Vector4(c, h.group, h.lodLevel, h.Material);
                for (int i = 0; i < d.v.Count; ++i)
                    vertices[vo + i] = new DebugVertex { position = d.v[i].position, normal = d.v[i].normal, ids = ids };
                for (int i = 0; i < d.t.Count; ++i)
                    indices[io + i] = vo + d.t[i];
                vo += d.v.Count;
                io += d.t.Count;
            }

            target.Clear();
            target.SetVertexBufferParams(vertexCount,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4));
            target.SetVertexBufferData(vertices, 0, 0, vertexCount, 0, MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);
            target.SetIndexBufferParams(indexCount, IndexFormat.UInt32);
            target.SetIndexBufferData(indices, 0, 0, indexCount, MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);
            target.subMeshCount = 1;
            target.SetSubMesh(0, new SubMeshDescriptor(0, indexCount), MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            target.bounds = reader.LocalBounds;
            vertices.Dispose();
            indices.Dispose();
        }

        static Bounds TransformBounds(Bounds b, Matrix4x4 m)
        {
            var center = m.MultiplyPoint3x4(b.center);
            var e = b.extents;
            var ext = new Vector3(
                Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z,
                Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z,
                Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z);
            return new Bounds(center, ext * 2f);
        }

        void OnDrawGizmosSelected()
        {
            if (mesh == null || !mesh.IsValid)
                return;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(1f, 0.6f, 0.1f, 0.5f);
            var b = mesh.Reader.LocalBounds;
            Gizmos.DrawWireCube(b.center, b.size);
        }
    }
}
