using System.IO;
using UnityEditor;
using UnityEditor.Compilation;

namespace UNanite.Editor
{
    /// <summary>
    /// Where UNanite is installed: as a UPM package (Packages/com.unanite.virtualgeometry, git URL,
    /// tarball, local folder) or copied into the project by the .unitypackage (Assets/UNanite).
    /// </summary>
    public static class VgPackagePaths
    {
        const string k_PackageName = "com.unanite.virtualgeometry";
        static string s_AssetRoot;

        /// <summary>Project path of the package root, e.g. "Packages/com.unanite.virtualgeometry" or "Assets/UNanite".</summary>
        public static string AssetRoot
        {
            get
            {
                if (s_AssetRoot == null)
                {
                    string asmdef = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName("UNanite.Editor");
                    s_AssetRoot = string.IsNullOrEmpty(asmdef)
                        ? $"Packages/{k_PackageName}"
                        : Path.GetDirectoryName(Path.GetDirectoryName(asmdef)).Replace('\\', '/');
                }
                return s_AssetRoot;
            }
        }

        /// <summary>Absolute path of the package root on disk.</summary>
        public static string FullRoot
        {
            get
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(VgPackagePaths).Assembly);
                return info != null ? info.resolvedPath : Path.GetFullPath(AssetRoot);
            }
        }

        /// <summary>Include path prefix of the HLSL library for generated shaders.</summary>
        public static string ShaderLibrary => AssetRoot + "/Runtime/ShaderLibrary/";
    }
}
