using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UNanite
{
    /// <summary>
    /// Renders a Unity Terrain through virtual geometry (M8): every node of the imported HLOD
    /// quadtree (<see cref="VirtualGeometryTerrainData"/>) becomes a VG instance linked to its switch
    /// records, so each view draws exactly one node per quadtree path (GPU, per camera / shadow
    /// split). Unity stops drawing the heightmap; trees, details and the TerrainCollider stay Unity's.
    ///
    /// Materials: the resolve uses HDRP TerrainLit's splat blending (Hidden/UNanite/TerrainLitResolve)
    /// with the terrain's layers, control maps and heightmap (per-pixel normals); views without the
    /// visibility buffer and shadow casting use an HDRP/Lit fallback with a baked base map.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Terrain))]
    [AddComponentMenu("UNanite/Virtual Geometry Terrain")]
    public sealed partial class VirtualGeometryTerrain : MonoBehaviour
    {
        [SerializeField] VirtualGeometryTerrainData m_Data;
        [SerializeField] ShadowCastingMode m_ShadowCasting = ShadowCastingMode.On;
        [Tooltip("Base map resolution of the fallback material (views without the visibility buffer).")]
        [SerializeField] int m_BaseMapResolution = 1024;
        [Tooltip("M11: LOD error of this terrain in pixels (times VirtualGeometrySettings.pixelError), like Unity's heightmapPixelError: 1 = the virtual geometry default, larger = fewer triangles.")]
        [SerializeField, Range(0.25f, 32f)] float m_PixelError = 1f;

        Terrain m_Terrain;
        VgWorld m_World;
        Material m_Fallback, m_Resolve;
        RenderTexture m_BaseMap;

        // runtime state per node (meshes change after edits)
        VgTerrainNode[] m_Nodes;
        int[] m_Handles, m_Switches;
        Vector3 m_Position;

        public VirtualGeometryTerrainData Data
        {
            get => m_Data;
            set { m_Data = value; Reregister(); }
        }

        public Terrain Terrain => m_Terrain != null ? m_Terrain : m_Terrain = GetComponent<Terrain>();

        /// <summary>M11: LOD error in pixels (times VirtualGeometrySettings.pixelError).</summary>
        public float PixelError
        {
            get => m_PixelError;
            set { m_PixelError = Mathf.Max(0.25f, value); Reregister(); }
        }
        public bool IsRegistered => m_Handles != null && m_World != null && m_World == VgWorld.Instance;
        /// <summary>Current nodes (meshes are replaced by runtime edits).</summary>
        public IReadOnlyList<VgTerrainNode> Nodes => m_Nodes;
        public Material ResolveMaterial => m_Resolve;
        public Material FallbackMaterial => m_Fallback;

        static readonly List<VirtualGeometryTerrain> s_Active = new List<VirtualGeometryTerrain>();
        static bool s_Hooked;

        void OnEnable()
        {
            Register();
        }

        void OnDisable()
        {
            Unregister();
        }

        void OnValidate()
        {
            if (isActiveAndEnabled && m_Handles != null)
                Reregister();
        }

        void Reregister()
        {
            if (!isActiveAndEnabled)
                return;
            Unregister();
            Register();
        }

        void Register()
        {
            if (m_Handles != null)
                return;
            var terrain = Terrain;
            if (m_Data == null || terrain == null || terrain.terrainData == null || m_Data.Nodes.Length == 0)
                return;
            m_World = VgWorld.GetOrCreate();
            if (m_World == null)
                return; // unsupported: Unity keeps drawing the terrain

            CreateMaterials(terrain);
            m_Position = terrain.GetPosition();
            m_Nodes = (VgTerrainNode[])m_Data.Nodes.Clone();
            m_Handles = new int[m_Nodes.Length];
            m_Switches = new int[m_Nodes.Length];
            bool shadows = m_ShadowCasting != ShadowCastingMode.Off;
            var materials = new[] { m_Fallback };
            var localToWorld = Matrix4x4.Translate(m_Position); // Unity terrains are never rotated or scaled
            for (int i = 0; i < m_Nodes.Length; ++i)
            {
                var node = m_Nodes[i];
                float pixelError = Mathf.Max(0.25f, m_PixelError);
                m_Switches[i] = node.level > 0 ? m_World.AddLodSwitch(WorldSphere(node), node.switchError / pixelError) : -1;
                m_Handles[i] = node.mesh != null ? m_World.AddInstance(node.mesh, materials, localToWorld, shadows) : -1;
                if (m_Handles[i] >= 0 && pixelError != 1f)
                    m_World.SetInstancePixelError(m_Handles[i], pixelError);
            }
            for (int i = 0; i < m_Nodes.Length; ++i)
            {
                int parent = m_Nodes[i].parent;
                m_World.SetInstanceLod(m_Handles[i], m_Switches[i], parent >= 0 ? m_Switches[parent] : -1);
            }

            // Unity draws the heightmap whenever virtual geometry does not (restored in Unregister;
            // never serialised as off by this component's lifetime alone)
            terrain.drawHeightmap = false;
            s_Active.Add(this);
            HookTracker();
        }

        void Unregister()
        {
            s_Active.Remove(this);
            CancelEdits();
            if (m_Handles != null && m_World != null && m_World == VgWorld.Instance)
            {
                for (int i = 0; i < m_Handles.Length; ++i)
                {
                    if (m_Handles[i] >= 0)
                        m_World.RemoveInstance(m_Handles[i]);
                    if (m_Switches[i] >= 0)
                        m_World.RemoveLodSwitch(m_Switches[i]);
                }
            }
            if (m_Handles != null && Terrain != null)
                Terrain.drawHeightmap = true;
            m_Handles = null;
            m_Switches = null;
            m_World = null;
            DestroyMaterials();
            DestroyRuntimeMeshes();
        }

        Vector4 WorldSphere(in VgTerrainNode node) =>
            new Vector4(node.switchSphere.x + m_Position.x, node.switchSphere.y + m_Position.y, node.switchSphere.z + m_Position.z, node.switchSphere.w);

        static void HookTracker()
        {
            if (s_Hooked)
                return;
            s_Hooked = true;
            VgWorld.BeforeFrame += Track; // before the world applies mesh changes and uploads instances
        }

        // Moved terrains and recreated worlds (like VirtualGeometryRenderer's tracker); finished edits.
        static void Track()
        {
            for (int i = s_Active.Count - 1; i >= 0; --i)
            {
                var t = s_Active[i];
                if (t == null)
                {
                    s_Active.RemoveAt(i);
                    continue;
                }
                if (!t.IsRegistered)
                {
                    if (t.isActiveAndEnabled)
                    {
                        t.m_Handles = null;
                        s_Active.RemoveAt(i);
                        t.DestroyMaterials();
                        t.Register();
                    }
                    continue;
                }
                if (t.Terrain != null && t.Terrain.GetPosition() != t.m_Position)
                    t.Reregister();
                else
                    t.PollEdits();
            }
        }

        // ---------------------------------------------------------------------------------------
        // Materials

        static readonly int s_Heightmap = Shader.PropertyToID("_VgTerrainHeightmap");
        static readonly int s_Params = Shader.PropertyToID("_VgTerrainParams");
        static readonly int s_Spacing = Shader.PropertyToID("_VgTerrainSpacing");

        void CreateMaterials(Terrain terrain)
        {
            var resolveShader = Shader.Find("Hidden/UNanite/TerrainLitResolve");
            var lit = Shader.Find("HDRP/Lit");
            if (lit == null)
                lit = GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.defaultMaterial.shader : null;
            m_Fallback = new Material(lit) { name = $"{name} (VG terrain fallback)", hideFlags = HideFlags.HideAndDontSave };
            if (resolveShader != null && resolveShader.isSupported)
                m_Resolve = new Material(resolveShader) { name = $"{name} (VG terrain resolve)", hideFlags = HideFlags.HideAndDontSave };
            RefreshMaterials();
            if (m_Resolve != null)
                VgWorld.SetResolveOverride(m_Fallback, m_Resolve);
        }

        void DestroyMaterials()
        {
            if (m_Fallback != null)
                VgWorld.SetResolveOverride(m_Fallback, null);
            DestroyUnityObject(m_Fallback);
            DestroyUnityObject(m_Resolve);
            m_Fallback = m_Resolve = null;
            if (m_BaseMap != null)
            {
                m_BaseMap.Release();
                DestroyUnityObject(m_BaseMap);
                m_BaseMap = null;
            }
        }

        static void DestroyUnityObject(UnityEngine.Object o)
        {
            if (o == null)
                return;
            if (Application.isPlaying)
                Destroy(o);
            else
                DestroyImmediate(o);
        }

        /// <summary>Copies the terrain's layers, control maps and heightmap into the VG materials (call after editing layers or splats).</summary>
        public void RefreshMaterials()
        {
            var terrain = Terrain;
            if (terrain == null || terrain.terrainData == null)
                return;
            var td = terrain.terrainData;
            if (m_Resolve != null)
                SetupResolve(m_Resolve, terrain);
            if (m_Fallback != null)
            {
                RenderBaseMap(td);
                m_Fallback.SetTexture("_BaseColorMap", m_BaseMap);
                m_Fallback.SetFloat("_Smoothness", 0.2f);
                m_Fallback.SetFloat("_Metallic", 0f);
            }
        }

        static void SetupResolve(Material m, Terrain terrain)
        {
            m.SetShaderPassEnabled("MotionVectors", true); // M9: moving terrains draw object motion
            var td = terrain.terrainData;
            var size = td.size;
            var layers = td.terrainLayers ?? Array.Empty<TerrainLayer>();
            int n = Mathf.Min(layers.Length, 8);
            var controls = td.alphamapTextures;
            if (controls.Length > 0) m.SetTexture("_Control0", controls[0]);
            if (controls.Length > 1) m.SetTexture("_Control1", controls[1]);
            bool anyNormal = false, anyMask = false;
            for (int i = 0; i < 8; ++i)
            {
                var l = i < n ? layers[i] : null;
                if (l == null)
                {
                    m.SetTexture($"_Splat{i}", null);
                    continue;
                }
                m.SetTexture($"_Splat{i}", l.diffuseTexture);
                var tile = new Vector2(Mathf.Max(l.tileSize.x, 1e-3f), Mathf.Max(l.tileSize.y, 1e-3f));
                m.SetTextureScale($"_Splat{i}", new Vector2(size.x / tile.x, size.z / tile.y));
                m.SetTextureOffset($"_Splat{i}", new Vector2(l.tileOffset.x / tile.x, l.tileOffset.y / tile.y));
                m.SetTexture($"_Normal{i}", l.normalMapTexture);
                m.SetTexture($"_Mask{i}", l.maskMapTexture);
                m.SetFloat($"_NormalScale{i}", l.normalScale);
                m.SetFloat($"_Metallic{i}", l.metallic);
                m.SetFloat($"_Smoothness{i}", l.smoothness);
                var remap = l.diffuseRemapMax;
                remap.w = l.diffuseRemapMin.w > 0f ? 0f : 1f; // 1 = opacity-as-density off
                m.SetVector($"_DiffuseRemapScale{i}", remap);
                m.SetVector($"_MaskMapRemapOffset{i}", l.maskMapRemapMin);
                m.SetVector($"_MaskMapRemapScale{i}", l.maskMapRemapMax - l.maskMapRemapMin);
                m.SetFloat($"_LayerHasMask{i}", l.maskMapTexture != null ? 1f : 0f);
                anyNormal |= l.normalMapTexture != null;
                anyMask |= l.maskMapTexture != null;
            }
            SetKeyword(m, "_TERRAIN_8_LAYERS", n > 4);
            SetKeyword(m, "_NORMALMAP", anyNormal);
            SetKeyword(m, "_MASKMAP", anyMask);
            var template = terrain.materialTemplate;
            bool heightBlend = template != null && template.HasProperty("_EnableHeightBlend") && template.GetFloat("_EnableHeightBlend") > 0f;
            SetKeyword(m, "_TERRAIN_BLEND_HEIGHT", heightBlend);
            if (template != null && template.HasProperty("_HeightTransition"))
                m.SetFloat("_HeightTransition", template.GetFloat("_HeightTransition"));

            int res = td.heightmapResolution;
            m.SetTexture(s_Heightmap, td.heightmapTexture);
            m.SetVector(s_Params, new Vector4(res, 1f / res, 2f * size.y, 0f));
            m.SetVector(s_Spacing, new Vector4(size.x / (res - 1), 0f, size.z / (res - 1), 0f));
        }

        static void SetKeyword(Material m, string keyword, bool on)
        {
            if (on) m.EnableKeyword(keyword);
            else m.DisableKeyword(keyword);
        }

        // Base map of the fallback: the resolve material's splat blend, flattened (VgTerrainBaseMap.shader).
        void RenderBaseMap(TerrainData td)
        {
            int res = Mathf.Clamp(m_BaseMapResolution, 64, 8192);
            if (m_BaseMap == null || m_BaseMap.width != res)
            {
                if (m_BaseMap != null)
                {
                    m_BaseMap.Release();
                    DestroyUnityObject(m_BaseMap);
                }
                m_BaseMap = new RenderTexture(res, res, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    name = $"{name} (VG terrain base map)",
                    hideFlags = HideFlags.HideAndDontSave,
                    useMipMap = true,
                    autoGenerateMips = true,
                    wrapMode = TextureWrapMode.Clamp,
                };
                m_BaseMap.Create();
            }
            var shader = Shader.Find("Hidden/UNanite/TerrainBaseMap");
            if (shader == null || m_Resolve == null)
            {
                var active = RenderTexture.active;
                RenderTexture.active = m_BaseMap;
                GL.Clear(false, true, new Color(0.4f, 0.4f, 0.4f, 1f));
                RenderTexture.active = active;
                return;
            }
            var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            mat.CopyPropertiesFromMaterial(m_Resolve);
            mat.shaderKeywords = m_Resolve.shaderKeywords;
            var cmd = new CommandBuffer { name = "UNanite terrain base map" };
            cmd.SetRenderTarget(m_BaseMap);
            cmd.DrawProcedural(Matrix4x4.identity, mat, 0, MeshTopology.Triangles, 3);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
            DestroyUnityObject(mat);
        }
    }
}
