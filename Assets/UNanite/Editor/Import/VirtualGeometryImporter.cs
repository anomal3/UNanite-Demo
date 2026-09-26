using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace UNanite.Editor
{
    /// <summary>Procedural source of a .vgmesh without a source asset (test and sample content).</summary>
    [Serializable]
    public sealed class VgProceduralSource
    {
        [Tooltip("\"rock\": ProceduralRock (20 * 4^subdivisions triangles). Empty = use sourceGuid.")]
        public string kind;
        public int subdivisions = 7;
        public int seed;
    }

    /// <summary>Contents of a .vgmesh sidecar file (JSON).</summary>
    [Serializable]
    public sealed class VgMeshDefinition
    {
        public string sourceGuid;
        public VgProceduralSource procedural = new VgProceduralSource();
        public VgMeshBuildSettings settings = new VgMeshBuildSettings();
    }

    /// <summary>
    /// Imports a .vgmesh sidecar: builds one VirtualGeometryMesh per Mesh found in the referenced
    /// source asset (FBX/OBJ/.asset...), or from a procedural recipe. Incrementality and caching come
    /// from the asset database: the result is keyed on this file, the source asset's import
    /// artifact, the importer version and the native builder's content hash (custom dependency),
    /// and is shareable via the Accelerator.
    ///
    /// With `settings.pageFile` (default) the streamable pages of every mesh are written to a side
    /// file of the import artifact ("&lt;index&gt;.vgpages", M7) and the asset keeps only the resident
    /// prefix; the streaming system reads the pages from that file on demand.
    /// </summary>
    [ScriptedImporter(2, "vgmesh", importQueueOffset: 100)]
    public sealed class VirtualGeometryImporter : ScriptedImporter
    {
        public const string Extension = "vgmesh";
        public const string BuilderDependency = "UNanite/NativeBuilder";

        public override void OnImportAsset(AssetImportContext ctx)
        {
            ctx.DependsOnCustomDependency(BuilderDependency);

            VgMeshDefinition def;
            try
            {
                def = JsonUtility.FromJson<VgMeshDefinition>(File.ReadAllText(ctx.assetPath)) ?? new VgMeshDefinition();
            }
            catch (Exception e)
            {
                ctx.LogImportError($"Invalid .vgmesh file: {e.Message}");
                return;
            }

            List<Mesh> meshes;
            var generated = new List<Mesh>();
            if (def.procedural != null && def.procedural.kind == "rock")
            {
                var rock = ProceduralRock.Create(Mathf.Clamp(def.procedural.subdivisions, 0, 9), def.procedural.seed);
                rock.name = Path.GetFileNameWithoutExtension(ctx.assetPath);
                generated.Add(rock);
                meshes = generated;
            }
            else
            {
                if (string.IsNullOrEmpty(def.sourceGuid) || !GUID.TryParse(def.sourceGuid, out var guid))
                {
                    ctx.LogImportError(".vgmesh has no valid source GUID or procedural recipe");
                    return;
                }

                ctx.DependsOnArtifact(guid);
                string sourcePath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(sourcePath))
                {
                    ctx.LogImportError($"Source asset {def.sourceGuid} not found");
                    return;
                }

                meshes = AssetDatabase.LoadAllAssetsAtPath(sourcePath).OfType<Mesh>().OrderBy(m => m.name, StringComparer.Ordinal).ToList();
                if (meshes.Count == 0)
                {
                    ctx.LogImportWarning($"'{sourcePath}' contains no meshes (it may not be imported yet)");
                    return;
                }
            }

            VirtualGeometryMesh main = null;
            var usedNames = new HashSet<string>();
            for (int i = 0; i < meshes.Count; ++i)
            {
                var mesh = meshes[i];
                var warnings = new List<string>();
                string title = $"UNanite: building '{mesh.name}' ({i + 1}/{meshes.Count})";
                var vg = VirtualGeometryBuilder.Build(mesh, def.settings, out string error, warnings,
                    p => !EditorUtility.DisplayCancelableProgressBar(title, $"{mesh.name}: {p * 100f:F0}%", p));
                EditorUtility.ClearProgressBar();

                foreach (var w in warnings)
                    ctx.LogImportWarning(w);

                if (vg == null)
                {
                    ctx.LogImportError($"Failed to build virtual geometry for '{mesh.name}': {error}");
                    continue;
                }

                string id = mesh.name;
                for (int k = 1; !usedNames.Add(id); ++k)
                    id = $"{mesh.name}_{k}";

                if (def.settings.pageFile)
                {
                    string pageFile = i + VirtualGeometryMesh.PageFileExtension;
                    byte[] pages = vg.SplitPages(pageFile);
                    if (pages != null)
                        File.WriteAllBytes(ctx.GetOutputArtifactFilePath(pageFile), pages);
                }

                ctx.AddObjectToAsset(id, vg);
                if (main == null)
                    main = vg;
            }

            if (main != null)
                ctx.SetMainObject(main);
            foreach (var mesh in generated)
                DestroyImmediate(mesh);
        }

        public static string SidecarPathFor(string sourcePath) => Path.ChangeExtension(sourcePath, Extension);

        /// <summary>Creates a .vgmesh for a procedural rock (no source asset): `path` must end in .vgmesh.</summary>
        public static void CreateProceduralRock(string path, int subdivisions, int seed, VgMeshBuildSettings settings = null)
        {
            var def = new VgMeshDefinition
            {
                procedural = new VgProceduralSource { kind = "rock", subdivisions = subdivisions, seed = seed },
                settings = settings ?? new VgMeshBuildSettings(),
            };
            File.WriteAllText(path, JsonUtility.ToJson(def, true));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }

        public static void CreateSidecar(string sourcePath, VgMeshBuildSettings settings = null, string sidecarPath = null)
        {
            var def = new VgMeshDefinition
            {
                sourceGuid = AssetDatabase.AssetPathToGUID(sourcePath),
                settings = settings ?? new VgMeshBuildSettings(),
            };
            string path = sidecarPath ?? SidecarPathFor(sourcePath);
            File.WriteAllText(path, JsonUtility.ToJson(def, true));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }
    }

    /// <summary>Resolves the page file of an imported VirtualGeometryMesh: a side file of the .vgmesh import artifact.</summary>
    [InitializeOnLoad]
    static class VgPageFileResolver
    {
        static VgPageFileResolver()
        {
            VirtualGeometryMesh.EditorPageFileResolver = Resolve;
        }

        public static string Resolve(VirtualGeometryMesh mesh)
        {
            if (mesh == null || !mesh.HasExternalPages || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string guid, out long _))
                return null;
            // artifact paths are virtual ("VirtualArtifacts/Primary/<guid>.<file>"): map to the local file
            var id = UnityEditor.Experimental.AssetDatabaseExperimental.LookupArtifact(new UnityEditor.Experimental.ArtifactKey(new GUID(guid)));
            if (!UnityEditor.Experimental.AssetDatabaseExperimental.GetArtifactPaths(id, out string[] paths))
                return null;
            foreach (var path in paths)
            {
                if (!path.EndsWith("." + mesh.PageFileName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (Unity.IO.LowLevel.Unsafe.VirtualFileSystem.GetLocalFileSystemName(path, out string local, out ulong offset, out ulong _) && offset == 0 && File.Exists(local))
                    return Path.GetFullPath(local);
            }
            return null;
        }
    }

    /// <summary>Registers the native builder's content hash so that rebuilding it reimports every .vgmesh.</summary>
    [InitializeOnLoad]
    static class NativeBuilderDependency
    {
        static NativeBuilderDependency()
        {
            // registered before the editor's startup import pass: a dependency registered later
            // (delayCall) is unknown to that pass, which then re-imports every .vgmesh on each start
            Register(refresh: false);
            EditorApplication.delayCall += () => Register(refresh: true);
        }

        static void Register(bool refresh)
        {
            string dll = NativeBuilder.SourceDllPath;
            var hash = File.Exists(dll) ? Hash128.Compute(File.ReadAllBytes(dll)) : new Hash128();
            hash.Append(VgFormat.Version);
            AssetDatabase.RegisterCustomDependency(VirtualGeometryImporter.BuilderDependency, hash);
            if (!refresh)
                return;

            // only refresh when the builder actually changed (Refresh re-evaluates dependencies)
            const string key = "UNanite.NativeBuilderHash";
            if (SessionState.GetString(key, "") != hash.ToString())
            {
                SessionState.SetString(key, hash.ToString());
                AssetDatabase.Refresh();
            }
        }
    }
}
