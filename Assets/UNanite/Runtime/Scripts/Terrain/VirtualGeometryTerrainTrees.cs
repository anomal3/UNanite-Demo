using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UNanite
{
    /// <summary>
    /// M11: renders a Unity Terrain's tree instances through virtual geometry. Every tree instance
    /// becomes one VG instance per LOD0 renderer of its prototype prefab (built by
    /// VirtualGeometryTerrainConverter.ConvertTrees); the cluster DAG replaces the prefab's LODs and
    /// billboards. While enabled, Unity's tree rendering is off (Terrain.treeDistance = 0, restored on
    /// disable); details (grass) stay Unity's unless converted separately.
    ///
    /// Materials are the prototypes' own (Shader Graphs such as SpeedTree 8 draw through their
    /// generated VG variants). SpeedTree 8 wind: each prototype's wind configuration (copied from its
    /// Tree component's wind asset, see <see cref="Winds"/>) drives a VgWorld wind slot shared by its
    /// instances (VgWorld.Wind.cs).
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Terrain))]
    [AddComponentMenu("UNanite/Virtual Geometry Terrain Trees")]
    public sealed class VirtualGeometryTerrainTrees : MonoBehaviour
    {
        /// <summary>One LOD0 renderer of a tree prototype.</summary>
        [Serializable]
        public struct PrototypeRenderer
        {
            public int prototype;
            public VirtualGeometryMesh mesh;
            public Material[] materials;
            public Matrix4x4 localToPrefab; // the renderer's transform relative to the prefab root
            public bool castShadows;
            // M11 discrete LODs (foliage built without simplification): LOD `lod` of `lodCount` is drawn
            // while the prototype's screen-relative height is between the previous LOD's transition
            // and `lodTransition` (LODGroup.screenRelativeTransitionHeight); single-LOD renderers: lodCount 1
            public int lod;
            public int lodCount;
            public float lodTransition;
            public Vector3 lodReferencePoint; // prefab space (LODGroup.localReferencePoint)
            public float lodSize;             // LODGroup.size
        }

        /// <summary>M11: screen height (pixels) at which discrete LOD switches match Unity's LODGroup transitions.</summary>
        public const float ReferenceScreenHeight = 1080f;

        [SerializeField] PrototypeRenderer[] m_Renderers = new PrototypeRenderer[0];
        [Tooltip("M11: SpeedTree 8 wind configuration per tree prototype (copied from the prefab's Tree wind asset when converting; in the editor a missing one is read from the prefab).")]
        [SerializeField] VgSpeedTreeWindParams[] m_Winds = new VgSpeedTreeWindParams[0];
        [Tooltip("Tree distance Unity used before the conversion (restored when this component is disabled or removed).")]
        [SerializeField] float m_UnityTreeDistance = -1f;

        Terrain m_Terrain;
        VgWorld m_World;
        readonly List<int> m_Handles = new List<int>();
        readonly List<int> m_Switches = new List<int>();
        readonly List<int> m_WindSlots = new List<int>();

        public PrototypeRenderer[] Renderers
        {
            get => m_Renderers;
            set { m_Renderers = value ?? new PrototypeRenderer[0]; Reregister(); }
        }

        /// <summary>M11: SpeedTree 8 wind configuration per tree prototype (invalid / missing = no wind).</summary>
        public VgSpeedTreeWindParams[] Winds
        {
            get => m_Winds;
            set { m_Winds = value ?? new VgSpeedTreeWindParams[0]; Reregister(); }
        }

#if UNITY_EDITOR
        static readonly Dictionary<GameObject, VgSpeedTreeWindParams> s_EditorWinds = new Dictionary<GameObject, VgSpeedTreeWindParams>();

        /// <summary>Editor: the wind configuration of a tree prefab (its first LOD0 renderer with SpeedTree wind).</summary>
        public static VgSpeedTreeWindParams ReadWind(GameObject prefab)
        {
            if (prefab == null)
                return null;
            var lodGroup = prefab.GetComponent<LODGroup>();
            var renderers = lodGroup != null && lodGroup.lodCount > 0 ? lodGroup.GetLODs()[0].renderers : prefab.GetComponentsInChildren<Renderer>();
            foreach (var r in renderers)
            {
                var wind = VgSpeedTreeWindParams.FromRenderer(r);
                if (wind != null)
                    return wind;
            }
            return null;
        }
#endif

        VgSpeedTreeWindParams WindOf(int prototype, GameObject prefab)
        {
            if (prototype < m_Winds.Length && m_Winds[prototype] != null && m_Winds[prototype].IsValid)
                return m_Winds[prototype];
#if UNITY_EDITOR
            // scenes converted before M11 wind: read (and cache) it from the prefab
            if (prefab != null && !s_EditorWinds.TryGetValue(prefab, out var wind))
                s_EditorWinds[prefab] = wind = ReadWind(prefab);
            return prefab != null ? s_EditorWinds[prefab] : null;
#else
            return null;
#endif
        }

        public int InstanceCount => m_Handles.Count;

        Terrain Terrain => m_Terrain != null ? m_Terrain : m_Terrain = GetComponent<Terrain>();

        void OnEnable() => Register();
        void OnDisable() => Unregister();

        void Reregister()
        {
            if (!isActiveAndEnabled)
                return;
            Unregister();
            Register();
        }

        void Register()
        {
            var terrain = Terrain;
            if (m_Handles.Count > 0 || terrain == null || terrain.terrainData == null || m_Renderers.Length == 0)
                return;
            var td = terrain.terrainData;
            var prototypes = td.treePrototypes;
            // every prototype needs a VG renderer: Unity's tree rendering is switched off as a whole
            var covered = new bool[prototypes.Length];
            foreach (var r in m_Renderers)
                if (r.prototype >= 0 && r.prototype < covered.Length && r.mesh != null && r.mesh.IsValid)
                    covered[r.prototype] = true;
            if (Array.IndexOf(covered, false) >= 0)
                return;
            m_World = VgWorld.GetOrCreate();
            if (m_World == null)
                return; // unsupported: Unity keeps drawing the trees

            var byPrototype = new List<PrototypeRenderer>[prototypes.Length];
            for (int p = 0; p < prototypes.Length; ++p)
                byPrototype[p] = new List<PrototypeRenderer>();
            foreach (var r in m_Renderers)
                byPrototype[r.prototype].Add(r);

            // M11: one wind slot per prototype (shared with equal configurations of other terrains)
            var windSlots = new int[prototypes.Length];
            for (int p = 0; p < prototypes.Length; ++p)
            {
                windSlots[p] = m_World.AcquireSpeedTreeWind(WindOf(p, prototypes[p].prefab));
                if (windSlots[p] >= 0)
                    m_WindSlots.Add(windSlots[p]);
            }

            // Unity's terrain scales a tree by its prototype prefab's root scale too (e.g. 1.5 for the
            // sample's Cypress, 6 for a rock): renderers and LOD sizes are relative to the root
            var rootScales = new Vector3[prototypes.Length];
            for (int p = 0; p < prototypes.Length; ++p)
                rootScales[p] = prototypes[p].prefab != null ? prototypes[p].prefab.transform.localScale : Vector3.one;

            // M11 SpeedTree smooth LOD (LODFadeMode.SpeedTree): LODs before the last cross-faded one
            // morph toward the next (Unity: all but the last, or the last two with a billboard LOD)
            var morphLods = new int[prototypes.Length];
            var fades = new (float ratio, float duration)[prototypes.Length][];
            for (int p = 0; p < prototypes.Length; ++p)
            {
                morphLods[p] = SpeedTreeMorphLods(prototypes[p].prefab);
                fades[p] = SpeedTreeCrossFades(prototypes[p].prefab, morphLods[p]);
            }

            Vector3 origin = terrain.GetPosition(), size = td.size;
            // switch errors: a LOD is left where Unity's LODGroup would (VgCull.compute ProjectedError at
            // the reference screen height; QualitySettings.lodBias like Unity)
            float errorScale = QualitySettings.lodBias * VirtualGeometrySettings.Active.pixelError / ReferenceScreenHeight;
            var switches = new int[8];
            foreach (var tree in td.treeInstances)
            {
                var position = origin + Vector3.Scale(tree.position, size);
                var rootScale = rootScales[tree.prototypeIndex];
                var trs = Matrix4x4.TRS(position, Quaternion.Euler(0f, tree.rotation * Mathf.Rad2Deg, 0f),
                                        Vector3.Scale(new Vector3(tree.widthScale, tree.heightScale, tree.widthScale), rootScale));
                var list = byPrototype[tree.prototypeIndex];
                // one switch per LOD transition of the prototype (leaving LOD l), shared by its renderers
                int lodCount = 0;
                foreach (var r in list)
                    lodCount = Mathf.Max(lodCount, r.lodCount);
                float previous = 0f;
                for (int l = 0; l < lodCount - 1; ++l)
                {
                    var r = list.Find(x => x.lod == l);
                    var center = trs.MultiplyPoint3x4(r.lodReferencePoint);
                    float worldSize = r.lodSize * Mathf.Max(tree.widthScale, tree.heightScale) * Mathf.Max(Mathf.Abs(rootScale.x), Mathf.Abs(rootScale.y), Mathf.Abs(rootScale.z));
                    float error = worldSize * errorScale / Mathf.Max(r.lodTransition, 1e-4f);
                    // LOD l starts where LOD l-1's switch left it (LOD 0: at full-screen height, like Unity)
                    float morphStart = l < morphLods[tree.prototypeIndex] ? (l == 0 ? worldSize * errorScale : previous) : 0f;
                    var fade = fades[tree.prototypeIndex];
                    var (fadeRatio, fadeDuration) = fade != null && l < fade.Length ? fade[l] : (0f, 0f);
                    switches[l] = m_World.AddLodSwitch(new Vector4(center.x, center.y, center.z, 0f), error, morphStart, error * fadeRatio, fadeDuration);
                    m_Switches.Add(switches[l]);
                    previous = error;
                }
                foreach (var r in list)
                {
                    int handle = m_World.AddInstance(r.mesh, r.materials, trs * r.localToPrefab, r.castShadows, -1, Vector4.zero, windSlots[tree.prototypeIndex]);
                    if (handle < 0)
                        continue;
                    m_Handles.Add(handle);
                    if (lodCount > 1)
                        // the last LOD stays to the tree distance like Unity's terrain trees (never culled)
                        m_World.SetInstanceLod(handle, r.lod > 0 ? switches[r.lod - 1] : -1, r.lod < lodCount - 1 ? switches[r.lod] : -1);
                }
            }

            if (m_UnityTreeDistance < 0f)
                m_UnityTreeDistance = terrain.treeDistance;
            terrain.treeDistance = 0f;
        }

        // M11: LODs of a SpeedTree-fading prefab that morph toward the next one (Unity's GPU driven LOD:
        // crossFadeLODBegin = lodCount - 2 with a billboard last LOD, else lodCount - 1)
        static int SpeedTreeMorphLods(GameObject prefab)
        {
            var lodGroup = prefab != null ? prefab.GetComponent<LODGroup>() : null;
            if (lodGroup == null || lodGroup.fadeMode != LODFadeMode.SpeedTree || lodGroup.lodCount < 2)
                return 0;
            var lods = lodGroup.GetLODs();
            var last = lods[lods.Length - 1].renderers;
            bool billboard = last.Length == 1 && last[0] != null && Array.Exists(last[0].sharedMaterials, m => m != null && m.IsKeywordEnabled("EFFECT_BILLBOARD"));
            return billboard ? Mathf.Max(lods.Length, 2) - 2 : lods.Length - 1;
        }

        // M11: Unity's dithered crossfade of a SpeedTree-fading prefab's LODs from crossFadeLODBegin on
        // (the last mesh LOD into the billboard), per LOD: animated (LODGroup.animateCrossFading: for
        // LODGroup.crossFadeAnimationDuration after the switch) or a band before the switch
        // (LOD.fadeTransitionWidth of its screen-height range: ratio of the fade start to the switch
        // distance, like the GPU resident drawer); null: none
        static (float ratio, float duration)[] SpeedTreeCrossFades(GameObject prefab, int morphLods)
        {
            var lodGroup = prefab != null ? prefab.GetComponent<LODGroup>() : null;
            if (lodGroup == null || lodGroup.fadeMode != LODFadeMode.SpeedTree || lodGroup.lodCount < 2)
                return null;
            var lods = lodGroup.GetLODs();
            var fades = new (float, float)[lods.Length];
            bool any = false;
            for (int l = morphLods; l < lods.Length - 1; ++l)
            {
                if (lodGroup.animateCrossFading)
                    fades[l] = (0f, LODGroup.crossFadeAnimationDuration);
                else
                {
                    float h = lods[l].screenRelativeTransitionHeight, previous = l > 0 ? lods[l - 1].screenRelativeTransitionHeight : 1f;
                    float fadeHeight = h + lods[l].fadeTransitionWidth * (previous - h);
                    fades[l] = (fadeHeight > h ? h / fadeHeight : 0f, 0f);
                }
                any |= fades[l].Item1 > 0f || fades[l].Item2 > 0f;
            }
            return any ? fades : null;
        }

        void Unregister()
        {
            if (m_World != null && m_World == VgWorld.Instance)
            {
                foreach (int h in m_Handles)
                    m_World.RemoveInstance(h);
                foreach (int s in m_Switches)
                    m_World.RemoveLodSwitch(s);
                foreach (int s in m_WindSlots)
                    m_World.ReleaseSpeedTreeWind(s);
            }
            m_Handles.Clear();
            m_Switches.Clear();
            m_WindSlots.Clear();
            m_World = null;
            var terrain = Terrain;
            if (terrain != null && m_UnityTreeDistance >= 0f)
                terrain.treeDistance = m_UnityTreeDistance;
        }
    }
}
