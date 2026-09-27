using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UNanite.Samples.QuarryScene
{
    /// <summary>
    /// Tools > UNanite > Samples > Quarry Scene: imports a folder of Megascans downloads (zips) and builds the
    /// quarry scene from them - or from procedural rocks without any downloads.
    /// </summary>
    sealed class QuarrySceneWindow : EditorWindow
    {
        const string k_Prefs = "UNanite.Samples.QuarryScene.";
        string m_Source, m_Library = "Assets/Megascans", m_Output = "Assets/QuarryScene";
        int m_TextureSize = 2048, m_Seed = 7;
        float m_Density = 1f;
        bool m_Convert = true, m_MeshLods = true;

        [MenuItem("Tools/UNanite/Samples/Quarry Scene", priority = 100)]
        static void Open() => GetWindow<QuarrySceneWindow>("Quarry Scene");

        void OnEnable()
        {
            m_Source = EditorPrefs.GetString(k_Prefs + "source", "");
            m_Library = EditorPrefs.GetString(k_Prefs + "library", m_Library);
            m_Output = EditorPrefs.GetString(k_Prefs + "output", m_Output);
        }

        void OnDisable()
        {
            EditorPrefs.SetString(k_Prefs + "source", m_Source ?? "");
            EditorPrefs.SetString(k_Prefs + "library", m_Library);
            EditorPrefs.SetString(k_Prefs + "output", m_Output);
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox("1. Import: a folder with Megascans downloads (Fab or Quixel Bridge zips, collections too) becomes HDRP/Lit " +
                                    "prefabs, materials and terrain layers in the library folder. The downloads stay under their own license.\n" +
                                    "2. Build: a quarry scene from the library (or from procedural rocks when the library is empty), " +
                                    "converted to virtual geometry. In Play Mode, V switches to Unity's renderers, P plays the flythrough.", MessageType.None);

            EditorGUILayout.LabelField("Megascans import", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                m_Source = EditorGUILayout.TextField("Downloads folder", m_Source);
                if (GUILayout.Button("...", GUILayout.Width(28)))
                {
                    string picked = EditorUtility.OpenFolderPanel("Megascans downloads", m_Source, "");
                    if (!string.IsNullOrEmpty(picked))
                        m_Source = picked;
                }
            }
            m_Library = EditorGUILayout.TextField("Library folder", m_Library);
            m_TextureSize = EditorGUILayout.IntPopup("Texture size", m_TextureSize, new[] { "1024", "2048", "4096" }, new[] { 1024, 2048, 4096 });
            m_MeshLods = EditorGUILayout.Toggle(new GUIContent("Unity Mesh LOD", "Meshes without LOD files of their own get Unity's Mesh LOD (the regular-renderer baseline)."), m_MeshLods);
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(m_Source) || !Directory.Exists(m_Source)))
                if (GUILayout.Button("Import downloads"))
                    Import();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Scene", EditorStyles.boldLabel);
            m_Output = EditorGUILayout.TextField("Output folder", m_Output);
            m_Density = EditorGUILayout.Slider("Density", m_Density, 0.25f, 2f);
            m_Seed = EditorGUILayout.IntField("Seed", m_Seed);
            m_Convert = EditorGUILayout.Toggle("Virtual geometry", m_Convert);
            bool hasLibrary = MegascansLibrary.Load(m_Library) != null;
            if (GUILayout.Button(hasLibrary ? "Build scene from the library" : "Build scene (procedural rocks)"))
            {
                if (!EditorUtility.DisplayDialog("Quarry Scene", "The open scene is replaced by the new one (unsaved changes are lost).", "Build", "Cancel"))
                    return;
                var log = QuarryBuilder.Build(new QuarryBuilder.Options
                {
                    libraryFolder = hasLibrary ? m_Library : null,
                    outputFolder = m_Output,
                    density = m_Density,
                    seed = m_Seed,
                    convert = m_Convert,
                });
                Debug.Log("UNanite quarry scene:\n" + log);
            }
        }

        void Import()
        {
            var zips = Directory.GetFiles(m_Source, "*.zip").OrderBy(p => new FileInfo(p).Length).ToArray();
            var sw = Stopwatch.StartNew();
            try
            {
                for (int i = 0; i < zips.Length; ++i)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Megascans", $"Unpacking {Path.GetFileName(zips[i])}", (float)i / zips.Length))
                        return;
                    MegascansImporter.Extract(zips[i], m_Library, m_TextureSize);
                }
                EditorUtility.DisplayProgressBar("Megascans", "Importing textures and meshes", 1f);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                EditorUtility.DisplayProgressBar("Megascans", "Materials, prefabs, terrain layers", 1f);
                var library = MegascansImporter.Build(m_Library, m_MeshLods);
                Debug.Log($"UNanite: {library.assets.Count} Megascans assets from {zips.Length} downloads in {sw.Elapsed.TotalSeconds:F0} s ({m_Library})");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }
    }
}
