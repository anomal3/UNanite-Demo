using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UNanite.Editor
{
    /// <summary>
    /// Player builds (M7): page files live in import artifacts, which players cannot read, so they
    /// are copied to StreamingAssets/UNanite/&lt;content hash&gt;.vgpages for the duration of the build
    /// (VirtualGeometryMesh.ResolvePageFile looks there at runtime) and removed afterwards.
    /// Every imported VirtualGeometryMesh with external pages is copied, used by the build or not.
    /// Windows x64 players also get the native builder (runtime terrain edits, M8) in
    /// &lt;Data&gt;/Plugins/x86_64, where VgNativeBuilder loads it.
    /// </summary>
    sealed class VgPageFileBuildStep : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        const string Parent = "Assets/StreamingAssets";
        const string Folder = Parent + "/UNanite";
        static readonly List<string> s_Copied = new List<string>();
        static bool s_CreatedFolder, s_CreatedParent;

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            s_Copied.Clear();
            s_CreatedFolder = !Directory.Exists(Folder);
            s_CreatedParent = !Directory.Exists(Parent);
            var guids = AssetDatabase.FindAssets("t:VirtualGeometryMesh").Concat(AssetDatabase.FindAssets("t:VirtualGeometryTerrainData")).Distinct();
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                foreach (var mesh in AssetDatabase.LoadAllAssetsAtPath(path).OfType<VirtualGeometryMesh>())
                {
                    if (!mesh.HasExternalPages)
                        continue;
                    string source = VgPageFileResolver.Resolve(mesh);
                    if (source == null)
                    {
                        Debug.LogError($"UNanite: page file of '{mesh.name}' ({path}) not found; reimport it.", mesh);
                        continue;
                    }
                    Directory.CreateDirectory(Folder);
                    string target = $"{Folder}/{mesh.Report.contentHash}{VirtualGeometryMesh.PageFileExtension}";
                    if (!File.Exists(target))
                    {
                        File.Copy(source, target);
                        s_Copied.Add(target);
                    }
                }
            }
            if (s_Copied.Count > 0)
                AssetDatabase.Refresh();
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform == BuildTarget.StandaloneWindows64 && File.Exists(NativeBuilder.SourceDllPath))
            {
                string exe = report.summary.outputPath;
                string plugins = Path.Combine(Path.GetDirectoryName(exe), Path.GetFileNameWithoutExtension(exe) + "_Data", "Plugins", "x86_64");
                Directory.CreateDirectory(plugins);
                File.Copy(NativeBuilder.SourceDllPath, Path.Combine(plugins, VgNativeBuilder.DllName), true);
            }
            foreach (string file in s_Copied)
                AssetDatabase.DeleteAsset(file);
            if (s_CreatedFolder && Directory.Exists(Folder) && !Directory.EnumerateFileSystemEntries(Folder).Any())
                AssetDatabase.DeleteAsset(Folder);
            // StreamingAssets itself when the build step made it (no empty folder left in the project)
            if (s_CreatedParent && Directory.Exists(Parent) && !Directory.EnumerateFileSystemEntries(Parent).Any(e => !e.EndsWith(".meta")))
                AssetDatabase.DeleteAsset(Parent);
            s_Copied.Clear();
        }
    }
}
