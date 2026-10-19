using System.IO;
using UnityEditor;
using UnityEditor.Compilation;

namespace VGeo.Editor
{
    /// <summary>
    /// Where VGeo is installed: as a UPM package (Packages/com.vgeo.virtualgeometry, git URL,
    /// tarball, local folder) or copied into the project by the .unitypackage (Assets/VGeo).
    /// </summary>
    public static class VgPackagePaths
    {
        const string k_PackageName = "com.vgeo.virtualgeometry";
        static string s_AssetRoot;

        /// <summary>Project path of the package root, e.g. "Packages/com.vgeo.virtualgeometry" or "Assets/VGeo".</summary>
        public static string AssetRoot
        {
            get
            {
                if (s_AssetRoot == null)
                {
                    string asmdef = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName("VGeo.Editor");
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
