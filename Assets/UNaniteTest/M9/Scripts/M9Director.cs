using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UNanite;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

// M9 demo director (Play Mode, M9_Sample): a scripted walk of the third-person character through the
// scene, boulders exploding into pieces, a rain of rocks, and (UNanite variant) the debug views.
// Fixed time step (captureFramerate), so the MeshRenderer and the UNanite runs are the same sequence.
// Optional frame capture (PNG per frame) and per-frame stats (FlyStats JSON) - never both in one run.
public sealed class M9Director : MonoBehaviour
{
    public bool useVg = true;
    public int frames = 600;
    public string captureDir;       // empty: no capture
    public string statsPath;        // empty: no stats
    public int fixedView = -1;      // UNanite: a debug view for the whole run (-1 = lit)
    public bool cycleViews;         // UNanite: cycle through the debug views
    public M9MeshPieces meshPieces; // MeshRenderer variant of the boulders' pieces
    public Mesh fallingRockMesh;    // MeshRenderer variant of the falling rocks
    public VirtualGeometryMesh fallingRockVg;
    public Material[] rockMaterials; // falling rocks and boulders (the sample scene's textured Rock_A)
    public Material interiorMaterial; // cut faces of the boulders' pieces (planar UVs)

    // debris never blocks the character: the walk (and so the whole sequence) is the same in every run
    const int DebrisLayer = 29;
    int m_PlayerLayer = -1;

    public static string Label = "";
    public bool Done { get; private set; }

    int m_Frame = -1;
    Component m_Input;
    System.Reflection.FieldInfo m_Move, m_Sprint;
    readonly StringBuilder m_Stats = new StringBuilder("[\n");
    readonly FrameTiming[] m_Timings = new FrameTiming[1];

    // timeline (frames at 30 fps): boulders dropped beside the path, a rain of rocks on both sides
    static readonly int[] BoulderFrames = { 60, 150, 240, 330, 420 };
    const int RainStart = 200, RainEnd = 320;
    static readonly (VgDebugView view, string label)[] Cycle =
    {
        (VgDebugView.Clusters, "view: Clusters"),
        (VgDebugView.Groups, "view: Groups"),
        (VgDebugView.LodLevel, "view: DAG level (LOD)"),
        (VgDebugView.Instances, "view: Instances"),
        (VgDebugView.Materials, "view: Materials"),
    };
    GameObject m_Template;
    readonly List<(GameObject go, float scale, bool broken)> m_Falling = new List<(GameObject, float, bool)>();

    void Start()
    {
        Time.captureFramerate = 30;
#if UNITY_EDITOR
        PlayerSettings.enableFrameTimingStats = true;
#endif
        foreach (var vgr in FindObjectsByType<VirtualGeometryRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (vgr.GetComponent<VirtualGeometryDestructible>() != null && vgr.GetComponent<MeshRenderer>() == null)
                continue;
            var mr = vgr.GetComponent<MeshRenderer>();
            if (mr == null)
                continue;
            vgr.enabled = useVg;
            mr.enabled = !useVg;
        }
        foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            if (mb.GetType().Name == "StarterAssetsInputs")
            {
                m_Input = mb;
                m_Move = mb.GetType().GetField("move");
                m_Sprint = mb.GetType().GetField("sprint");
            }
        Label = "";
        var player = GameObject.Find("PlayerArmature");
        if (player != null)
        {
            m_PlayerLayer = player.layer;
            Physics.IgnoreLayerCollision(m_PlayerLayer, DebrisLayer, true);
        }
        foreach (var d in FindObjectsByType<VirtualGeometryDestructible>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (!d.gameObject.activeInHierarchy)
            {
                m_Template = d.gameObject;
                break;
            }
    }

    void OnDestroy()
    {
        if (m_PlayerLayer >= 0)
            Physics.IgnoreLayerCollision(m_PlayerLayer, DebrisLayer, false);
    }

    void Update()
    {
        m_Frame++;
        if (m_Frame >= frames)
        {
            Finish();
            return;
        }
        // walk forward, turn slightly right after the first boulder
        if (m_Input != null)
        {
            Vector2 move = m_Frame < 20 || m_Frame >= 520 ? Vector2.zero : new Vector2(0f, 1f);
            m_Move.SetValue(m_Input, move);
            m_Sprint?.SetValue(m_Input, false);
        }
        for (int b = 0; b < BoulderFrames.Length; ++b)
            if (m_Frame == BoulderFrames[b])
                DropBoulder(b);
        // a dropped boulder breaks when it reaches the ground (the same frame in every run)
        for (int i = 0; i < m_Falling.Count; ++i)
        {
            var (go, scale, broken) = m_Falling[i];
            if (broken || go == null)
                continue;
            var pos = go.transform.position;
            float floor = Physics.Raycast(new Vector3(pos.x, 4f, pos.z), Vector3.down, out var hit, 20f) ? hit.point.y : 2f;
            if (pos.y - floor < 0.95f * scale)
            {
                Break(go.GetComponent<VirtualGeometryDestructible>());
                m_Falling[i] = (go, scale, true);
            }
        }
        if (m_Frame >= RainStart && m_Frame < RainEnd && m_Frame % 4 == 0)
            SpawnRock(m_Frame);

        var settings = VirtualGeometrySettings.Active;
        var view = VgDebugView.None;
        Label = "";
        if (fixedView >= 0)
        {
            view = (VgDebugView)fixedView;
            Label = "view: " + view;
        }
        else if (cycleViews)
        {
            var c = Cycle[(m_Frame / 120) % Cycle.Length];
            view = c.view;
            Label = c.label;
        }
        if (useVg && settings.debugView != view)
            settings.debugView = view;

        if (!string.IsNullOrEmpty(captureDir))
            StartCoroutine(Capture(m_Frame));
        if (!string.IsNullOrEmpty(statsPath))
            RecordStats();
    }

    // a boulder (copy of the scene's inactive template) falls beside the path, 6-9 m ahead, 3-5 m aside
    void DropBoulder(int index)
    {
        if (m_Template == null)
            return;
        var rng = new System.Random(1000 + index);
        Vector3 player = PlayerPos();
        float side = (index & 1) == 0 ? 1f : -1f;
        float scale = 0.8f + (float)rng.NextDouble() * 0.5f;
        var pos = player + new Vector3(6f + (float)rng.NextDouble() * 3f, 9f + (float)rng.NextDouble() * 3f, side * (3f + (float)rng.NextDouble() * 2f));
        var go = Instantiate(m_Template, pos, Quaternion.Euler(rng.Next(360), rng.Next(360), rng.Next(360)));
        go.name = "Falling boulder " + index;
        go.transform.localScale = Vector3.one * scale;
        go.layer = DebrisLayer; // VG pieces inherit it
        if (rockMaterials != null && rockMaterials.Length > 0)
        {
            go.GetComponent<VirtualGeometryRenderer>().SharedMaterials = rockMaterials;
            go.GetComponent<MeshRenderer>().sharedMaterials = rockMaterials;
        }
        go.GetComponent<VirtualGeometryRenderer>().enabled = useVg;
        go.GetComponent<MeshRenderer>().enabled = !useVg;
        var destructible = go.GetComponent<VirtualGeometryDestructible>();
        destructible.enabled = true;
        if (interiorMaterial != null)
            destructible.InteriorMaterial = interiorMaterial;
        destructible.BreakOnCollision = false; // broken by the director at the same frame in every run
        go.SetActive(true);
        var rb = go.AddComponent<Rigidbody>();
        rb.mass = 3000f * scale * scale * scale;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        rb.angularVelocity = new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble()) * 2f;
        m_Falling.Add((go, scale, false));
    }

    Vector3 PlayerPos()
    {
        var p = GameObject.Find("PlayerArmature");
        return p != null ? p.transform.position : Vector3.zero;
    }

    void Break(VirtualGeometryDestructible d)
    {
        Vector3 point = d.transform.position + (PlayerPos() - d.transform.position).normalized * 0.8f;
        const float impulse = 60f;
        if (useVg)
        {
            d.Break(point, impulse);
            return;
        }
        // MeshRenderer variant: the same pieces, colliders, masses and explosion as the VG component
        var fracture = d.Fracture;
        var intact = d.GetComponent<VirtualGeometryRenderer>();
        var src = intact.SharedMaterials;
        int slots = fracture.InteriorSlot + 1;
        var mats = new Material[slots];
        for (int s = 0; s < slots - 1; ++s)
            mats[s] = src[Mathf.Min(s, src.Length - 1)];
        mats[slots - 1] = d.InteriorMaterial != null ? d.InteriorMaterial : src[0];
        var t = d.transform;
        Vector3 scale = t.lossyScale;
        float radius = intact.Mesh.LocalBounds.extents.magnitude * scale.x * 2f;
        for (int i = 0; i < fracture.Pieces.Length; ++i)
        {
            var p = fracture.Pieces[i];
            var go = new GameObject($"{d.name} mesh piece {i}") { layer = DebrisLayer };
            go.transform.SetPositionAndRotation(t.position, t.rotation);
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = meshPieces.pieces[i];
            go.AddComponent<MeshRenderer>().sharedMaterials = mats;
            var c = go.AddComponent<MeshCollider>();
            c.sharedMesh = p.collider;
            c.convex = true;
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = Mathf.Max(0.01f, 2400f * p.volume * scale.x * scale.y * scale.z);
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.AddExplosionForce(impulse, point, radius, 0.2f, ForceMode.Impulse);
        }
        var mr = d.GetComponent<MeshRenderer>();
        if (mr != null)
            mr.enabled = false;
        var body = d.GetComponent<Rigidbody>();
        if (body != null)
            body.isKinematic = true;
        foreach (var col in d.GetComponents<Collider>())
            col.enabled = false;
    }

    void SpawnRock(int frame)
    {
        var rng = new System.Random(frame);
        Vector3 player = PlayerPos();
        var go = new GameObject($"falling rock {frame}") { layer = DebrisLayer };
        float side = rng.Next(2) == 0 ? 1f : -1f;
        go.transform.position = player + new Vector3(3f + (float)rng.NextDouble() * 6f, 8f + (float)rng.NextDouble() * 3f, side * (2.5f + (float)rng.NextDouble() * 3.5f));
        go.transform.rotation = Quaternion.Euler(rng.Next(360), rng.Next(360), rng.Next(360));
        go.transform.localScale = Vector3.one * (0.3f + (float)rng.NextDouble() * 0.3f);
        if (useVg)
        {
            var r = go.AddComponent<VirtualGeometryRenderer>();
            r.SharedMaterials = rockMaterials;
            r.Mesh = fallingRockVg;
        }
        else
        {
            go.AddComponent<MeshFilter>().sharedMesh = fallingRockMesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = rockMaterials;
        }
        go.AddComponent<SphereCollider>().radius = 0.9f;
        var rb = go.AddComponent<Rigidbody>();
        rb.mass = 200f;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
    }

    IEnumerator Capture(int frame)
    {
        yield return new WaitForEndOfFrame();
        var tex = ScreenCapture.CaptureScreenshotAsTexture();
        Directory.CreateDirectory(captureDir);
        File.WriteAllBytes(Path.Combine(captureDir, $"f{frame:0000}.png"), tex.EncodeToPNG());
        Destroy(tex);
    }

    void RecordStats()
    {
        FrameTimingManager.CaptureFrameTimings();
        double gpu = 0, cpu = 0;
        if (FrameTimingManager.GetLatestTimings(1, m_Timings) > 0)
        {
            gpu = m_Timings[0].gpuFrameTime;
            cpu = m_Timings[0].cpuFrameTime;
        }
        long camTris = 0, shadowTris = 0;
        int clusters = 0, instances = 0, splits = 0;
        var w = VgWorld.Instance;
        var cam = Camera.main;
        if (w != null && useVg)
            foreach (var v in w.LastFrameStats)
            {
                if (cam != null && v.name == cam.name)
                {
                    camTris += v.triangles + v.phase2Triangles;
                    clusters += v.visibleClusters + v.phase2Clusters;
                    instances += v.visibleInstances;
                }
                else if (v.name.StartsWith("Shadow"))
                {
                    shadowTris += v.triangles;
                    splits++;
                }
            }
        int drawCalls = 0, indirect = 0, setPass = 0, unityTris = 0;
#if UNITY_EDITOR
        drawCalls = UnityStats.drawCalls;
        indirect = UnityStats.totalIndirectDrawCalls;
        setPass = UnityStats.setPassCalls;
        unityTris = UnityStats.triangles;
#endif
        var st = w != null ? w.StreamingStats : default;
        m_Stats.Append(string.Format(CultureInfo.InvariantCulture,
            "{{\"gpu\":{0:F3},\"cpu\":{1:F3},\"dt\":{2:F3},\"drawCalls\":{3},\"indirect\":{4},\"setPass\":{5},\"camTris\":{6},\"clusters\":{7},\"instances\":{8},\"shadowTris\":{9},\"splits\":{10},\"streaming\":{11},\"residentMB\":{12:F2},\"streamableMB\":{13:F2},\"unityTris\":{14},\"moving\":{15},\"label\":\"{16}\"}},\n",
            gpu, cpu, Time.unscaledDeltaTime * 1000f, drawCalls, indirect, setPass, camTris, clusters, instances, shadowTris, splits,
            st.enabled && useVg ? 1 : 0, st.residentBytes / 1048576.0, st.streamableBytes / 1048576.0, unityTris, w != null ? w.MovingInstanceCount : 0, Label));
    }

    void Finish()
    {
        if (Done)
            return;
        Done = true;
        if (!string.IsNullOrEmpty(statsPath))
        {
            var s = m_Stats.ToString().TrimEnd('\n', ',');
            File.WriteAllText(statsPath, s + "\n]\n");
        }
        VirtualGeometrySettings.Active.debugView = VgDebugView.None;
        Time.captureFramerate = 0;
    }
}
