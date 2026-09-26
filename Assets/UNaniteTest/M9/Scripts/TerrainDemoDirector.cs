using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using UNanite;
using UnityEngine;
using UnityEngine.Playables;
#if UNITY_EDITOR
using UnityEditor;
#endif

// M9 terrain demo director (Play Mode, M9_TerrainDemo): plays the scene's camera timeline
// (MainSequence, all shots) exactly once from 0, evaluated manually at 1/30 s per frame, so the
// MeshRenderer / Unity Terrain run and the UNanite runs show the same camera at every frame (the
// timeline loops by default). Optional JPG capture per frame and per-frame stats (FlyStats JSON).
public sealed class TerrainDemoDirector : MonoBehaviour
{
    public bool useVg = true;
    public bool vgTrees = true; // M11: terrain trees as virtual geometry (with useVg)
    public bool vgDetails = true; // M11: terrain details (grass) as virtual geometry (with useVg)
    public int frames = -1;          // -1: the whole timeline
    public string captureDir;        // empty: no capture
    public string statsPath;         // empty: no stats
    public int fixedView = -1;       // UNanite: a debug view for the whole run (-1 = lit)
    public bool cycleViews;          // UNanite: cycle through the debug views
    public int cycleFrames = 180;
    public int holdFrame = -1;       // M11: the camera stops at this timeline frame, the game goes on (wind comparisons)
    public int captureFrom = 0;      // first captured frame
    public int pixelErrorFrame = -1; // M11: VirtualGeometrySettings.pixelError becomes pixelErrorValue at this frame (LOD switch tests)
    public float pixelErrorValue = 1f;

    public static string Label = "";
    public bool Done { get; private set; }
    public int FrameCount => m_Frames;
    public int CurrentFrame => m_Frame;

    PlayableDirector m_Timeline;
    int m_Frame = -1, m_Frames;
    readonly StringBuilder m_Stats = new StringBuilder("[\n");
    readonly FrameTiming[] m_Timings = new FrameTiming[1];

    static readonly (VgDebugView view, string label)[] Cycle =
    {
        (VgDebugView.Clusters, "view: Clusters"),
        (VgDebugView.LodLevel, "view: DAG level (LOD)"),
        (VgDebugView.Instances, "view: Instances"),
        (VgDebugView.Groups, "view: Groups"),
        (VgDebugView.Materials, "view: Materials"),
    };

    void Start()
    {
        Time.captureFramerate = 30;
#if UNITY_EDITOR
        PlayerSettings.enableFrameTimingStats = true;
#endif
        foreach (var vgr in FindObjectsByType<VirtualGeometryRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var mr = vgr.GetComponent<MeshRenderer>();
            if (mr == null)
                continue;
            vgr.enabled = useVg;
            mr.enabled = !useVg;
        }
        foreach (var t in FindObjectsByType<VirtualGeometryTerrain>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            t.enabled = useVg; // disabled: Unity draws the heightmap again
        foreach (var t in FindObjectsByType<VirtualGeometryTerrainTrees>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            t.enabled = useVg && vgTrees; // disabled: Unity draws the trees again
        foreach (var t in FindObjectsByType<VirtualGeometryTerrainDetails>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            t.enabled = useVg && vgDetails; // disabled: Unity draws the details again

        foreach (var d in FindObjectsByType<PlayableDirector>(FindObjectsSortMode.None))
            if (m_Timeline == null || d.duration > m_Timeline.duration)
                m_Timeline = d;
        if (m_Timeline != null)
        {
            m_Timeline.extrapolationMode = DirectorWrapMode.None;
            m_Timeline.timeUpdateMode = DirectorUpdateMode.Manual;
            m_Timeline.time = 0;
            m_Timeline.Play();
            m_Timeline.Evaluate();
        }
        m_Frames = frames > 0 ? frames : m_Timeline != null ? Mathf.FloorToInt((float)m_Timeline.duration * 30f) : 300;
        Label = "";
    }

    void Update()
    {
        m_Frame++;
        if (m_Frame >= m_Frames)
        {
            Finish();
            return;
        }
        if (m_Timeline != null)
        {
            m_Timeline.time = (holdFrame >= 0 ? Mathf.Min(m_Frame, holdFrame) : m_Frame) / 30.0;
            m_Timeline.Evaluate();
        }

        var settings = VirtualGeometrySettings.Active;
        if (m_Frame == pixelErrorFrame)
            settings.pixelError = pixelErrorValue;
        var view = VgDebugView.None;
        Label = "";
        if (fixedView >= 0)
        {
            view = (VgDebugView)fixedView;
            Label = "view: " + view;
        }
        else if (cycleViews)
        {
            var c = Cycle[(m_Frame / cycleFrames) % Cycle.Length];
            view = c.view;
            Label = c.label;
        }
        if (useVg && settings.debugView != view)
            settings.debugView = view;

        if (!string.IsNullOrEmpty(captureDir) && m_Frame >= captureFrom)
            StartCoroutine(Capture(m_Frame));
        if (!string.IsNullOrEmpty(statsPath))
            RecordStats();
    }

    IEnumerator Capture(int frame)
    {
        yield return new WaitForEndOfFrame();
        var tex = ScreenCapture.CaptureScreenshotAsTexture();
        Directory.CreateDirectory(captureDir);
        File.WriteAllBytes(Path.Combine(captureDir, $"f{frame:0000}.jpg"), tex.EncodeToJPG(92));
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
