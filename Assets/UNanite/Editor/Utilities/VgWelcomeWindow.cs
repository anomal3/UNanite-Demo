using UnityEditor;
using UnityEngine;

namespace UNanite.Editor
{
    /// <summary>
    /// Shown after install and on every editor start (until the checkbox at the bottom is cleared):
    /// what the package is, that it is a test build, where the demo project is.
    /// Tools > UNanite > Welcome opens it again.
    /// </summary>
    [InitializeOnLoad]
    public sealed class VgWelcomeWindow : EditorWindow
    {
        public const string RepositoryUrl = "https://github.com/anomal3/UNanite-Demo";
        const string k_ShowPref = "UNanite.Welcome.ShowAtStartup";
        const string k_SessionKey = "UNanite.Welcome.Shown";

        const string k_Text =
            "Thanks for installing UNanite! This is a test package of my Nanite-style virtual geometry system for Unity HDRP. " +
            "It is not stable yet, I am working on it since this summer and some things still can break, but the package is " +
            "available for download so everybody can try it in own project.\n\n" +
            "What it does: meshes are split into small clusters with automatic LOD, culled on the GPU with occlusion and drawn " +
            "through a visibility buffer. Shadows, baked lightmaps, motion vectors, streaming, terrain, foliage and destruction " +
            "are working too.";

        const string k_Start =
            "1.  Your project must use HDRP (Unity 6000.4, HDRP 17.4).\n" +
            "2.  Select objects in the scene and use Tools > UNanite > Convert Selection to Virtual Geometry.\n" +
            "3.  Press Play. Debug views (triangles, clusters, LOD) are in the UNanite overlay of the Scene view.";

        const string k_Demo =
            "The demo project with the pier, falling rocks and 82 million triangles is on GitHub, I upload it yesterday together " +
            "with a ready Windows build. Tested only on Windows, DirectX 12 and RTX 3060, on AMD and Vulkan I didn't test it yet. " +
            "If you will find a bug, please open an issue there.";

        static VgWelcomeWindow()
        {
            if (Application.isBatchMode)
                return;
            EditorApplication.delayCall += () =>
            {
                if (!EditorPrefs.GetBool(k_ShowPref, true) || SessionState.GetBool(k_SessionKey, false))
                    return;
                SessionState.SetBool(k_SessionKey, true);
                Open();
            };
        }

        [MenuItem("Tools/UNanite/Welcome", priority = 100)]
        public static void Open()
        {
            var w = GetWindow<VgWelcomeWindow>(true, "UNanite", true);
            w.minSize = new Vector2(640, 700);
            w.maxSize = new Vector2(900, 1000);
        }

        Texture2D m_Promo;
        Vector2 m_Scroll;
        GUIStyle m_Title, m_Body, m_Badge, m_Head;

        void OnEnable()
        {
            m_Promo = AssetDatabase.LoadAssetAtPath<Texture2D>(VgPackagePaths.AssetRoot + "/Editor/Images/UNanitePromo.png");
        }

        void Styles()
        {
            if (m_Body != null)
                return;
            m_Title = new GUIStyle(EditorStyles.boldLabel) { fontSize = 20 };
            m_Head = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 };
            m_Body = new GUIStyle(EditorStyles.label) { wordWrap = true, fontSize = 12, richText = true };
            m_Badge = new GUIStyle(EditorStyles.helpBox) { fontSize = 12, alignment = TextAnchor.MiddleCenter };
        }

        void OnGUI()
        {
            Styles();
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            if (m_Promo != null)
            {
                float w = position.width - 8f;
                var r = GUILayoutUtility.GetRect(w, w * m_Promo.height / m_Promo.width);
                GUI.DrawTexture(r, m_Promo, ScaleMode.ScaleToFit);
            }
            GUILayout.Space(6);
            using (new EditorGUILayout.VerticalScope(new GUIStyle { padding = new RectOffset(12, 12, 0, 0) }))
            {
                EditorGUILayout.LabelField("UNanite - virtual geometry for Unity HDRP", m_Title, GUILayout.Height(28));
                EditorGUILayout.LabelField("Test package, not stable. Version " + Version(), m_Badge, GUILayout.Height(24));
                GUILayout.Space(6);
                EditorGUILayout.LabelField(k_Text, m_Body);
                GUILayout.Space(8);
                EditorGUILayout.LabelField("How to start", m_Head);
                EditorGUILayout.LabelField(k_Start, m_Body);
                GUILayout.Space(8);
                EditorGUILayout.LabelField("Demo project", m_Head);
                EditorGUILayout.LabelField(k_Demo, m_Body);
                GUILayout.Space(8);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Demo project on GitHub", GUILayout.Height(30)))
                        Application.OpenURL(RepositoryUrl);
                    if (GUILayout.Button("Downloads (releases)", GUILayout.Height(30)))
                        Application.OpenURL(RepositoryUrl + "/releases");
                    if (GUILayout.Button("Support the project", GUILayout.Height(30)))
                        Application.OpenURL(RepositoryUrl + "#support-the-project");
                }
            }
            EditorGUILayout.EndScrollView();

            // bottom bar: the startup checkbox
            var line = GUILayoutUtility.GetRect(position.width, 1f);
            EditorGUI.DrawRect(line, new Color(0f, 0f, 0f, 0.3f));
            using (new EditorGUILayout.HorizontalScope(GUILayout.Height(30)))
            {
                GUILayout.Space(10);
                bool show = EditorPrefs.GetBool(k_ShowPref, true);
                bool next = EditorGUILayout.ToggleLeft("Show this window at every startup", show, GUILayout.Width(260));
                if (next != show)
                    EditorPrefs.SetBool(k_ShowPref, next);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Close", GUILayout.Width(90)))
                    Close();
                GUILayout.Space(8);
            }
            GUILayout.Space(4);
        }

        static string Version()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(VgWelcomeWindow).Assembly);
            if (info != null)
                return info.version;
            var json = AssetDatabase.LoadAssetAtPath<TextAsset>(VgPackagePaths.AssetRoot + "/package.json");
            if (json != null)
            {
                var v = JsonUtility.FromJson<PackageVersion>(json.text);
                if (v != null && !string.IsNullOrEmpty(v.version))
                    return v.version;
            }
            return "?";
        }

        [System.Serializable]
        sealed class PackageVersion
        {
            public string version;
        }
    }
}
