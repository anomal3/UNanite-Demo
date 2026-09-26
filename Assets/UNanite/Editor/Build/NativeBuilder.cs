using System;
using System.IO;
using UnityEditor;

namespace UNanite.Editor
{
    /// <summary>
    /// Editor side of the native builder: provides <see cref="VgNativeBuilder"/> with a shadow copy
    /// of the DLL (Library/UNanite/Native) so the original in Plugins~ is never locked: it can be
    /// rebuilt while the editor runs and is picked up after the next domain reload. The library is
    /// freed before every domain reload.
    /// </summary>
    [InitializeOnLoad]
    public static class NativeBuilder
    {
        static NativeBuilder()
        {
            VgNativeBuilder.EditorLibraryPath = ShadowCopy;
            AssemblyReloadEvents.beforeAssemblyReload += VgNativeBuilder.Unload;
            EditorApplication.quitting += VgNativeBuilder.Unload;
        }

        public static string SourceDllPath
        {
            get
            {
                // UPM package: Plugins~ (not imported); .unitypackage install: Plugins (imported with no platform enabled)
                string root = VgPackagePaths.FullRoot;
                string hidden = Path.Combine(root, "Plugins~", "win-x64", VgNativeBuilder.DllName);
                string visible = Path.Combine(root, "Plugins", "win-x64", VgNativeBuilder.DllName);
                return File.Exists(hidden) || !File.Exists(visible) ? hidden : visible;
            }
        }

        public static bool IsAvailable => VgNativeBuilder.IsAvailable;
        public static string LoadError => VgNativeBuilder.LoadError;

        static (string, string) ShadowCopy()
        {
            string source = SourceDllPath;
            if (!File.Exists(source))
                return (null, $"Native builder not found at '{source}'. Build it with CMake (Native~/CMakeLists.txt).");

            string shadowDir = Path.GetFullPath("Library/UNanite/Native");
            Directory.CreateDirectory(shadowDir);
            foreach (var stale in Directory.GetFiles(shadowDir, "unanite_builder_*.dll"))
            {
                try { File.Delete(stale); }
                catch (IOException) { } // still loaded by another editor instance
                catch (UnauthorizedAccessException) { }
            }

            string shadow = Path.Combine(shadowDir, $"unanite_builder_{DateTime.UtcNow.Ticks:x}.dll");
            File.Copy(source, shadow, true);
            return (shadow, null);
        }

        public static UnvgBuildSettings DefaultSettings() => VgNativeBuilder.DefaultSettings();

        public static VgNativeBuilder.Result Build(VgNativeBuilder.MeshStreams mesh, UnvgBuildSettings settings, Func<float, bool> onProgress = null) =>
            VgNativeBuilder.Build(mesh, settings, onProgress);
    }
}
