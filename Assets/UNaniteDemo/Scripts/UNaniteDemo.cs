using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UNanite;
using UnityEngine;
using UnityEngine.InputSystem;

// UNanite demo (the HDRP sample scene with everything opaque as virtual geometry, 100 rocks of 0.3-1.3 M
// triangles, a character to walk around, falling and breaking rocks). Works in players: hotkeys switch UNanite
// on / off (regular MeshRenderers with their LODGroups), the debug views, the rock rain and boulders; a stats overlay and a welcome window explain what the demo is.
public sealed class UNaniteDemo : MonoBehaviour
{
    [Header("Rocks")]
    public M9MeshPieces meshPieces;         // MeshRenderer twins of the boulder pieces
    public Mesh fallingRockMesh;            // MeshRenderer twin of the falling rocks
    public VirtualGeometryMesh fallingRockVg;
    public Material[] rockMaterials;
    public Material interiorMaterial;       // cut faces of the boulder pieces
    public GameObject boulderTemplate;      // inactive destructible boulder of the scene
    public int maxDebris = 400;

    [Header("Start")]
    public bool showWelcome = true;
    public float rocksPerSecond = 8f;

    // debris never collides with the character (it would block the walk)
    const int DebrisLayer = 29;

    static readonly (VgDebugView view, string name)[] Views =
    {
        (VgDebugView.None, "Lit (final image)"),
        (VgDebugView.Triangles, "Triangles"),
        (VgDebugView.Clusters, "Clusters"),
        (VgDebugView.Groups, "Cluster groups"),
        (VgDebugView.LodLevel, "LOD level (DAG)"),
        (VgDebugView.Instances, "Instances"),
        (VgDebugView.Materials, "Materials"),
    };

    const string WelcomeTitle = "UNanite demo - work in progress";
    const string WelcomeText =
        "Hi! This is small demo of UNanite, a virtual geometry system for Unity HDRP that I am making, " +
        "something like Nanite in Unreal Engine. Please keep in mind that it is only a demo and the project " +
        "is still in work, so some bugs and strange things can happen.\n\n" +
        "What is working now:\n" +
        "  - all opaque meshes of this scene is rendered as virtual geometry: walls, the floor, props and 100 rocks " +
        "with 0.3 - 1.3 million triangles each (82 million triangles in total)\n" +
        "  - clusters with automatic LOD, GPU culling with occlusion, visibility buffer, software raster " +
        "for tiny triangles\n" +
        "  - shadows, baked lightmaps, motion vectors, streaming (only the detail you see stay in GPU memory)\n" +
        "  - destruction: boulders break into pieces and every piece is also virtual geometry\n\n" +
        "Not working yet: skinned meshes (the character is regular Unity), transparent glass and water " +
        "use normal Unity rendering, no URP and no mobile for now.\n\n" +
        "Press U to turn UNanite off and see same scene with regular Unity MeshRenderers, and compare the FPS. " +
        "The rocks don't have hand made LODs, this is exactly the case where virtual geometry help the most. " +
        "Made for HDRP on DirectX 12, I tested it on RTX 3060.";

    const string ControlsText =
        "WASD / mouse - walk and look      Shift - run      Space - jump\n" +
        "U - UNanite on / off (compare with Unity MeshRenderers)\n" +
        "1..7 - view: lit, triangles, clusters, groups, LOD, instances, materials\n" +
        "R - rain of rocks on / off      B - drop a boulder      C - clear rocks\n" +
        "F2 - stats      F1 / Esc - this window      F10 - quit";

    bool m_UseVg = true;
    int m_View;
    bool m_Rain;
    bool m_ShowStats = true;
    bool m_ShowWelcome;
    float m_RainAccum;
    int m_Spawned;

    // converted scene objects: VirtualGeometryRenderer + its disabled MeshRenderer twin
    readonly List<(VirtualGeometryRenderer vgr, MeshRenderer mr)> m_Pairs = new List<(VirtualGeometryRenderer, MeshRenderer)>();
    // LODGroups the converter disabled, with the coarse LOD renderers (Unity's own LODs when UNanite is off)
    readonly List<(LODGroup group, List<Renderer> coarse)> m_LodGroups = new List<(LODGroup, List<Renderer>)>();
    readonly List<GameObject> m_Debris = new List<GameObject>();
    readonly List<(VirtualGeometryDestructible d, float scale)> m_Falling = new List<(VirtualGeometryDestructible, float)>();

    Component m_Inputs;
    System.Reflection.FieldInfo m_Move, m_CursorLocked, m_CursorLook;
    Transform m_Player;
    int m_PlayerLayer = -1;

    // stats
    readonly FrameTiming[] m_Timings = new FrameTiming[1];
    float m_StatTime, m_FrameAccum, m_GpuAccum;
    int m_FrameCount, m_GpuCount;
    float m_FrameMs, m_GpuMs;
    long m_SceneSourceTris;
    GUIStyle m_Box, m_Text, m_Title, m_Small, m_Button;
    readonly StringBuilder m_Sb = new StringBuilder(512);

    void Start()
    {
        // numbers in the overlay and the self-test log: "13.1 ms", "1.1 M" on any system locale
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        Application.targetFrameRate = -1;
        QualitySettings.vSyncCount = 0;
        m_ShowWelcome = showWelcome;

        foreach (var vgr in FindObjectsByType<VirtualGeometryRenderer>(FindObjectsInactive.Include))
        {
            var mr = vgr.GetComponent<MeshRenderer>();
            if (mr != null && vgr.GetComponent<VirtualGeometryDestructible>() == null)
                m_Pairs.Add((vgr, mr));
            if (vgr.Mesh != null && vgr.gameObject.activeInHierarchy)
                m_SceneSourceTris += vgr.Mesh.Report.sourceTriangles;
        }
        foreach (var g in FindObjectsByType<LODGroup>(FindObjectsInactive.Include))
        {
            if (g.enabled)
                continue;
            var coarse = new List<Renderer>();
            var lods = g.GetLODs();
            for (int l = 1; l < lods.Length; ++l)
                foreach (var r in lods[l].renderers)
                    if (r != null && r.GetComponent<VirtualGeometryRenderer>() == null)
                        coarse.Add(r);
            m_LodGroups.Add((g, coarse));
        }

        foreach (var mb in FindObjectsByType<MonoBehaviour>())
            if (mb.GetType().Name == "StarterAssetsInputs")
            {
                m_Inputs = mb;
                var t = mb.GetType();
                m_Move = t.GetField("move");
                m_CursorLocked = t.GetField("cursorLocked");
                m_CursorLook = t.GetField("cursorInputForLook");
            }
        var player = GameObject.Find("PlayerArmature");
        if (player != null)
        {
            m_Player = player.transform;
            m_PlayerLayer = player.layer;
            Physics.IgnoreLayerCollision(m_PlayerLayer, DebrisLayer, true);
        }
        if (boulderTemplate == null)
            foreach (var d in FindObjectsByType<VirtualGeometryDestructible>(FindObjectsInactive.Include))
                if (!d.gameObject.activeInHierarchy)
                {
                    boulderTemplate = d.gameObject;
                    break;
                }
        ApplyVg();
        ApplyCursor();

        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; ++i)
            if (args[i] == "-selftest")
                StartCoroutine(SelfTest(args[i + 1]));
    }

    // "-selftest <folder>": the hotkey actions in a fixed order, screenshots with the overlay and the
    // averaged timings of each phase, then quit (checks a player build without anybody at the keyboard)
    IEnumerator SelfTest(string folder)
    {
        Directory.CreateDirectory(folder);
        var log = new StringBuilder();
        IEnumerator Phase(string name, float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds * 0.4f);
            float frame = 0f, gpu = 0f;
            int n = 0, g = 0;
            for (float t = 0f; t < seconds * 0.6f; t += Time.unscaledDeltaTime)
            {
                frame += Time.unscaledDeltaTime * 1000f;
                n++;
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, m_Timings) > 0 && m_Timings[0].gpuFrameTime > 0.0)
                {
                    gpu += (float)m_Timings[0].gpuFrameTime;
                    g++;
                }
                yield return null;
            }
            yield return new WaitForEndOfFrame();
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(Path.Combine(folder, name + ".png"), tex.EncodeToPNG());
            Destroy(tex);
            var w = VgWorld.Instance;
            log.Append($"{name}: vg={m_UseVg} view={Views[m_View].name} frame={frame / Mathf.Max(1, n):F2} ms gpu={(g > 0 ? gpu / g : 0f):F2} ms rocks={m_Debris.Count} vgInstances={(w != null ? w.InstanceCount : 0)} moving={(w != null ? w.MovingInstanceCount : 0)}\n");
            File.WriteAllText(Path.Combine(folder, "selftest.txt"), log.ToString());
        }
        yield return Phase("01_welcome", 6f);
        m_ShowWelcome = false;
        ApplyCursor();
        yield return Phase("02_unanite", 5f);
        m_Rain = true;
        DropBoulder();
        yield return new WaitForSecondsRealtime(2f);
        DropBoulder();
        yield return Phase("03_rain_unanite", 8f);
        m_UseVg = false;
        ApplyVg();
        yield return Phase("04_rain_unity", 6f);
        m_UseVg = true;
        ApplyVg();
        yield return Phase("05_rain_unanite_again", 4f);
        m_Rain = false;
        for (int v = 1; v < Views.Length; ++v)
        {
            m_View = v;
            yield return Phase($"06_view_{v}_{Views[v].view}", 2f);
        }
        m_View = 0;
        m_UseVg = false;
        ApplyVg();
        yield return Phase("07_unity_no_rain", 4f);
        m_UseVg = true;
        ApplyVg();
        yield return Phase("08_unanite_no_rain", 4f);
        log.Append("done\n");
        File.WriteAllText(Path.Combine(folder, "selftest.txt"), log.ToString());
        Quit();
    }

    void OnDestroy()
    {
        if (m_PlayerLayer >= 0)
            Physics.IgnoreLayerCollision(m_PlayerLayer, DebrisLayer, false);
        var s = VirtualGeometrySettings.Active;
        if (s != null)
            s.debugView = VgDebugView.None;
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.f1Key.wasPressedThisFrame || kb.escapeKey.wasPressedThisFrame)
            {
                m_ShowWelcome = !m_ShowWelcome;
                ApplyCursor();
            }
            if (kb.enterKey.wasPressedThisFrame && m_ShowWelcome)
            {
                m_ShowWelcome = false;
                ApplyCursor();
            }
            if (kb.f10Key.wasPressedThisFrame)
                Quit();
            if (kb.f2Key.wasPressedThisFrame)
                m_ShowStats = !m_ShowStats;
            if (kb.uKey.wasPressedThisFrame)
            {
                m_UseVg = !m_UseVg;
                ApplyVg();
            }
            for (int i = 0; i < Views.Length; ++i)
                if (kb[Key.Digit1 + i].wasPressedThisFrame || kb[Key.Numpad1 + i].wasPressedThisFrame)
                {
                    m_View = i;
                    if (!m_UseVg)
                    {
                        m_UseVg = true;
                        ApplyVg();
                    }
                }
            if (kb.rKey.wasPressedThisFrame)
                m_Rain = !m_Rain;
            if (kb.bKey.wasPressedThisFrame)
                DropBoulder();
            if (kb.cKey.wasPressedThisFrame)
                ClearDebris();
        }

        var settings = VirtualGeometrySettings.Active;
        var view = m_UseVg ? Views[m_View].view : VgDebugView.None;
        if (settings != null && settings.debugView != view)
            settings.debugView = view;

        if (m_Rain)
        {
            m_RainAccum += Time.deltaTime * rocksPerSecond;
            while (m_RainAccum >= 1f)
            {
                m_RainAccum -= 1f;
                SpawnRock();
            }
        }
        UpdateFalling();
        UpdateStats();
    }

    void ApplyVg()
    {
        foreach (var (vgr, mr) in m_Pairs)
        {
            if (vgr == null || mr == null)
                continue;
            vgr.enabled = m_UseVg;
            mr.enabled = !m_UseVg;
        }
        foreach (var (group, coarse) in m_LodGroups)
        {
            if (group == null)
                continue;
            foreach (var r in coarse)
                if (r != null)
                    r.enabled = !m_UseVg;
            group.enabled = !m_UseVg;
        }
        foreach (var go in m_Debris)
            if (go != null)
                SetDebrisVg(go);
    }

    void SetDebrisVg(GameObject go)
    {
        var vgr = go.GetComponent<VirtualGeometryRenderer>();
        var mr = go.GetComponent<MeshRenderer>();
        var d = go.GetComponent<VirtualGeometryDestructible>();
        bool alive = d == null || !d.IsBroken;
        if (vgr != null)
            vgr.enabled = alive && m_UseVg;
        if (mr != null)
            mr.enabled = alive && !m_UseVg;
    }

    void ApplyCursor()
    {
        bool locked = !m_ShowWelcome;
        if (m_Inputs != null)
        {
            m_CursorLocked?.SetValue(m_Inputs, locked);
            m_CursorLook?.SetValue(m_Inputs, locked);
            if (!locked)
            {
                m_Move?.SetValue(m_Inputs, Vector2.zero);
                var look = m_Inputs.GetType().GetField("look");
                look?.SetValue(m_Inputs, Vector2.zero);
            }
        }
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    static void Quit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    Vector3 PlayerPos() => m_Player != null ? m_Player.position : Vector3.zero;
    Vector3 PlayerForward()
    {
        if (m_Player == null)
            return Vector3.right;
        var f = m_Player.forward;
        f.y = 0f;
        return f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.right;
    }

    static float FloorAt(Vector3 p) =>
        Physics.Raycast(new Vector3(p.x, p.y + 4f, p.z), Vector3.down, out var hit, 30f, ~(1 << DebrisLayer), QueryTriggerInteraction.Ignore) ? hit.point.y : p.y - 1f;

    void Track(GameObject go)
    {
        m_Debris.Add(go);
        while (m_Debris.Count > maxDebris)
        {
            if (m_Debris[0] != null)
                Destroy(m_Debris[0]);
            m_Debris.RemoveAt(0);
        }
    }

    // a rock falls from the sky 3-9 m ahead of the character, 2.5-6 m to a side
    void SpawnRock()
    {
        if (fallingRockVg == null && fallingRockMesh == null)
            return;
        var rng = new System.Random(unchecked(m_Spawned++ * 7919 + 17));
        Vector3 fwd = PlayerForward(), side = Vector3.Cross(Vector3.up, fwd);
        float s = rng.Next(2) == 0 ? 1f : -1f;
        var go = new GameObject("Falling rock") { layer = DebrisLayer };
        go.transform.position = PlayerPos() + fwd * (3f + (float)rng.NextDouble() * 6f) + side * s * (2.5f + (float)rng.NextDouble() * 3.5f) + Vector3.up * (8f + (float)rng.NextDouble() * 3f);
        go.transform.rotation = Quaternion.Euler(rng.Next(360), rng.Next(360), rng.Next(360));
        go.transform.localScale = Vector3.one * (0.3f + (float)rng.NextDouble() * 0.3f);
        if (fallingRockVg != null)
        {
            var r = go.AddComponent<VirtualGeometryRenderer>();
            r.SharedMaterials = rockMaterials;
            r.Mesh = fallingRockVg;
        }
        if (fallingRockMesh != null)
        {
            go.AddComponent<MeshFilter>().sharedMesh = fallingRockMesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = rockMaterials;
        }
        go.AddComponent<SphereCollider>().radius = 0.9f;
        var rb = go.AddComponent<Rigidbody>();
        rb.mass = 200f;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        SetDebrisVg(go);
        Track(go);
    }

    // a boulder (copy of the scene's inactive template) falls beside the path and breaks on the ground
    void DropBoulder()
    {
        if (boulderTemplate == null)
            return;
        var rng = new System.Random(unchecked(m_Spawned++ * 104729 + 3));
        Vector3 fwd = PlayerForward(), side = Vector3.Cross(Vector3.up, fwd);
        float s = rng.Next(2) == 0 ? 1f : -1f;
        float scale = 0.8f + (float)rng.NextDouble() * 0.5f;
        var pos = PlayerPos() + fwd * (6f + (float)rng.NextDouble() * 3f) + side * s * (3f + (float)rng.NextDouble() * 2f) + Vector3.up * (9f + (float)rng.NextDouble() * 3f);
        var go = Instantiate(boulderTemplate, pos, Quaternion.Euler(rng.Next(360), rng.Next(360), rng.Next(360)));
        go.name = "Falling boulder";
        go.transform.localScale = Vector3.one * scale;
        go.layer = DebrisLayer; // VG pieces inherit it
        var vgr = go.GetComponent<VirtualGeometryRenderer>();
        var mr = go.GetComponent<MeshRenderer>();
        if (rockMaterials != null && rockMaterials.Length > 0)
        {
            vgr.SharedMaterials = rockMaterials;
            if (mr != null)
                mr.sharedMaterials = rockMaterials;
        }
        var d = go.GetComponent<VirtualGeometryDestructible>();
        d.enabled = true;
        if (interiorMaterial != null)
            d.InteriorMaterial = interiorMaterial;
        d.BreakOnCollision = false; // broken when it reaches the ground
        go.SetActive(true);
        var rb = go.AddComponent<Rigidbody>();
        rb.mass = 3000f * scale * scale * scale;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        rb.angularVelocity = new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble()) * 2f;
        SetDebrisVg(go);
        Track(go);
        m_Falling.Add((d, scale));
    }

    void UpdateFalling()
    {
        for (int i = m_Falling.Count - 1; i >= 0; --i)
        {
            var (d, scale) = m_Falling[i];
            if (d == null)
            {
                m_Falling.RemoveAt(i);
                continue;
            }
            var pos = d.transform.position;
            if (pos.y - FloorAt(pos) >= 0.95f * scale && pos.y > -50f)
                continue;
            m_Falling.RemoveAt(i);
            Vector3 point = pos + (PlayerPos() - pos).normalized * 0.8f;
            d.Break(point, 60f);
            // MeshRenderer twins of the pieces (same order as the fracture's pieces)
            var pieces = d.Pieces;
            for (int p = 0; p < pieces.Count; ++p)
            {
                var piece = pieces[p];
                if (meshPieces != null && p < meshPieces.pieces.Length)
                {
                    piece.AddComponent<MeshFilter>().sharedMesh = meshPieces.pieces[p];
                    piece.AddComponent<MeshRenderer>().sharedMaterials = piece.GetComponent<VirtualGeometryRenderer>().SharedMaterials;
                }
                SetDebrisVg(piece);
                Track(piece);
            }
            SetDebrisVg(d.gameObject);
        }
    }

    void ClearDebris()
    {
        foreach (var go in m_Debris)
            if (go != null)
                Destroy(go);
        m_Debris.Clear();
        m_Falling.Clear();
    }

    void UpdateStats()
    {
        m_FrameAccum += Time.unscaledDeltaTime * 1000f;
        m_FrameCount++;
        FrameTimingManager.CaptureFrameTimings();
        if (FrameTimingManager.GetLatestTimings(1, m_Timings) > 0 && m_Timings[0].gpuFrameTime > 0.0)
        {
            m_GpuAccum += (float)m_Timings[0].gpuFrameTime;
            m_GpuCount++;
        }
        m_StatTime += Time.unscaledDeltaTime;
        if (m_StatTime < 0.5f)
            return;
        m_FrameMs = m_FrameAccum / Mathf.Max(1, m_FrameCount);
        m_GpuMs = m_GpuCount > 0 ? m_GpuAccum / m_GpuCount : 0f;
        m_StatTime = m_FrameAccum = m_GpuAccum = 0f;
        m_FrameCount = m_GpuCount = 0;
    }

    static string Millions(long n) => n >= 1000000 ? $"{n / 1e6:F1} M" : $"{n / 1e3:F0} K";

    void Styles()
    {
        if (m_Box != null)
            return;
        var bg = new Texture2D(1, 1);
        bg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.72f));
        bg.Apply();
        m_Box = new GUIStyle { normal = { background = bg }, padding = new RectOffset(14, 14, 10, 10) };
        m_Text = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true, richText = true, normal = { textColor = Color.white } };
        m_Small = new GUIStyle(m_Text) { fontSize = 14, wordWrap = false };
        m_Title = new GUIStyle(m_Text) { fontSize = 22, fontStyle = FontStyle.Bold };
        m_Button = new GUIStyle(GUI.skin.button) { fontSize = 16, fixedHeight = 34 };
    }

    void OnGUI()
    {
        Styles();
        float k = Mathf.Max(1f, Screen.height / 1080f);
        var old = GUI.matrix;
        GUI.matrix = Matrix4x4.Scale(new Vector3(k, k, 1f));
        float sw = Screen.width / k, sh = Screen.height / k;

        if (m_ShowStats)
        {
            m_Sb.Clear();
            m_Sb.Append(m_UseVg ? "<b><color=#7CFC8A>UNanite ON</color></b>" : "<b><color=#FFB060>UNanite OFF - regular Unity MeshRenderers + LODGroups</color></b>").Append("   (U)\n");
            if (m_FrameMs > 0f)
                m_Sb.Append($"{1000f / m_FrameMs:F0} FPS   frame {m_FrameMs:F1} ms");
            if (m_GpuMs > 0f)
                m_Sb.Append($"   GPU {m_GpuMs:F1} ms");
            m_Sb.Append('\n');
            var w = VgWorld.Instance;
            if (m_UseVg && w != null)
            {
                long tris = 0, shadowTris = 0;
                int clusters = 0, instances = 0;
                var cam = Camera.main;
                foreach (var v in w.LastFrameStats)
                {
                    if (cam != null && v.name == cam.name)
                    {
                        tris += v.triangles + v.phase2Triangles;
                        clusters += v.visibleClusters + v.phase2Clusters;
                        instances += v.visibleInstances;
                    }
                    else if (v.name.StartsWith("Shadow"))
                        shadowTris += v.triangles;
                }
                m_Sb.Append($"Scene: {Millions(m_SceneSourceTris)} source triangles, {w.InstanceCount} VG instances ({w.MovingInstanceCount} moving)\n");
                m_Sb.Append($"Drawn: {Millions(tris)} triangles in {clusters:N0} clusters, {instances} visible instances\n".Replace(',', ' '));
                m_Sb.Append($"Shadows: {Millions(shadowTris)} triangles\n");
                var st = w.StreamingStats;
                if (st.enabled)
                    m_Sb.Append($"Streaming: {st.residentBytes / 1048576.0:F0} MB in GPU of {st.streamableBytes / 1048576.0:F0} MB\n");
                m_Sb.Append($"View: {Views[m_View].name}   (1..7)\n");
            }
            else
                m_Sb.Append($"Scene: {Millions(m_SceneSourceTris)} triangles as regular meshes\n");
            m_Sb.Append($"Rock rain: {(m_Rain ? "on" : "off")} (R)   Rocks: {m_Debris.Count}\n");
            m_Sb.Append($"{SystemInfo.graphicsDeviceName}, {SystemInfo.graphicsDeviceType}, {Screen.width}x{Screen.height}\n");
            m_Sb.Append("<color=#BBBBBB>F1 - help and controls</color>");
            var content = new GUIContent(m_Sb.ToString());
            float width = 560f;
            float height = m_Small.CalcHeight(content, width) + 20f;
            GUI.Box(new Rect(10, 10, width + 28, height), GUIContent.none, m_Box);
            GUI.Label(new Rect(24, 20, width, height), content, m_Small);
        }

        if (m_ShowWelcome)
        {
            float ww = Mathf.Min(820f, sw - 40f);
            var body = new GUIContent(WelcomeText);
            var controls = new GUIContent(ControlsText);
            float hb = m_Text.CalcHeight(body, ww - 28f);
            float hc = m_Small.CalcHeight(controls, ww - 28f);
            float wh = 40f + hb + 16f + hc + 60f;
            var r = new Rect((sw - ww) * 0.5f, Mathf.Max(10f, (sh - wh) * 0.5f), ww, wh);
            GUI.Box(r, GUIContent.none, m_Box);
            GUILayout.BeginArea(new Rect(r.x + 14, r.y + 10, r.width - 28, r.height - 20));
            GUILayout.Label(WelcomeTitle, m_Title);
            GUILayout.Label(body, m_Text);
            GUILayout.Space(8);
            GUILayout.Label(controls, m_Small);
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Ok, let's go (Enter)", m_Button))
            {
                m_ShowWelcome = false;
                ApplyCursor();
            }
            if (GUILayout.Button("Quit (F10)", m_Button, GUILayout.Width(160)))
                Quit();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }
        GUI.matrix = old;
    }
}
