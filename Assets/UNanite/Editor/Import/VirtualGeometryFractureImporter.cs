using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace UNanite.Editor
{
    /// <summary>Contents of a .vgfracture sidecar file (JSON, M9).</summary>
    [Serializable]
    public sealed class VgFractureDefinition
    {
        public string sourceGuid;      // mesh asset (FBX / .asset ...), or empty with a procedural recipe
        public string meshName;        // mesh inside the source asset (empty = the first)
        public VgProceduralSource procedural = new VgProceduralSource();
        public VgFractureSettings fracture = new VgFractureSettings();
        public VgMeshBuildSettings settings = new VgMeshBuildSettings();
    }

    /// <summary>
    /// Imports a .vgfracture sidecar (M9): Voronoi-fractures the source mesh (<see cref="VgFracture"/>)
    /// and builds every piece into a <see cref="VirtualGeometryMesh"/> (pages in side files of the
    /// artifact, streamed like any VG mesh) with a convex collider source (a coarse cut of the piece).
    /// Main object: <see cref="VirtualGeometryFracture"/>.
    /// </summary>
    [ScriptedImporter(1, "vgfracture", importQueueOffset: 100)]
    public sealed class VirtualGeometryFractureImporter : ScriptedImporter
    {
        public const string Extension = "vgfracture";

        public override void OnImportAsset(AssetImportContext ctx)
        {
            ctx.DependsOnCustomDependency(VirtualGeometryImporter.BuilderDependency);

            VgFractureDefinition def;
            try
            {
                def = JsonUtility.FromJson<VgFractureDefinition>(File.ReadAllText(ctx.assetPath)) ?? new VgFractureDefinition();
            }
            catch (Exception e)
            {
                ctx.LogImportError($"Invalid .vgfracture file: {e.Message}");
                return;
            }

            Mesh source;
            bool generated = false;
            if (def.procedural != null && def.procedural.kind == "rock")
            {
                source = ProceduralRock.Create(Mathf.Clamp(def.procedural.subdivisions, 0, 9), def.procedural.seed);
                source.name = Path.GetFileNameWithoutExtension(ctx.assetPath);
                generated = true;
            }
            else
            {
                if (string.IsNullOrEmpty(def.sourceGuid) || !GUID.TryParse(def.sourceGuid, out var guid))
                {
                    ctx.LogImportError(".vgfracture has no valid source GUID or procedural recipe");
                    return;
                }
                ctx.DependsOnArtifact(guid);
                string sourcePath = AssetDatabase.GUIDToAssetPath(guid);
                var meshes = AssetDatabase.LoadAllAssetsAtPath(sourcePath).OfType<Mesh>().OrderBy(m => m.name, StringComparer.Ordinal).ToList();
                source = string.IsNullOrEmpty(def.meshName) ? meshes.FirstOrDefault() : meshes.FirstOrDefault(m => m.name == def.meshName);
                if (source == null)
                {
                    ctx.LogImportError($"mesh '{def.meshName}' not found in '{sourcePath}'");
                    return;
                }
            }

            try
            {
                Import(ctx, def, source);
            }
            finally
            {
                if (generated)
                    DestroyImmediate(source);
            }
        }

        static void Import(AssetImportContext ctx, VgFractureDefinition def, Mesh source)
        {
            var fs = def.fracture ?? new VgFractureSettings();
            if (VgFracture.OpenEdges(source) != 0)
            {
                ctx.LogImportError($"'{source.name}' is not closed (watertight): it cannot be fractured");
                return;
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var pieces = VgFracture.Fracture(source, new VgFracture.Settings
            {
                pieces = fs.pieces, seed = fs.seed, focus = fs.focus, focusWeight = fs.focusWeight, capUvScale = fs.capUvScale,
            }, out string warning);
            double msFracture = watch.Elapsed.TotalMilliseconds;
            if (warning != null)
                ctx.LogImportWarning($"fracture of '{source.name}': {warning}");
            if (pieces.Count == 0)
            {
                ctx.LogImportError($"fracture of '{source.name}' produced no pieces");
                return;
            }

            watch.Restart();
            var settings = def.settings ?? new VgMeshBuildSettings();
            var result = new List<VirtualGeometryFracture.Piece>();
            int pieceTriangles = 0;
            for (int i = 0; i < pieces.Count; ++i)
            {
                var p = pieces[i];
                string title = $"UNanite: building pieces of '{source.name}' ({i + 1}/{pieces.Count})";
                var vg = VirtualGeometryBuilder.Build(p.mesh, settings, out string error, null,
                    x => !EditorUtility.DisplayCancelableProgressBar(title, $"{x * 100f:F0}%", (i + x) / pieces.Count));
                if (vg == null)
                {
                    EditorUtility.ClearProgressBar();
                    ctx.LogImportError($"piece {i} of '{source.name}': {error}");
                    return;
                }
                vg.name = $"piece{i}";
                // convex collider source: a coarse cut of the piece (PhysX builds the hull from its vertices);
                // extracted before the pages move to the side file
                var collider = VgFracture.ColliderMesh(p.mesh, vg, fs.colliderTriangles);
                collider.name = $"piece{i}_collider";
                collider.hideFlags = HideFlags.None;
                if (settings.pageFile)
                {
                    string pageFile = i + VirtualGeometryMesh.PageFileExtension;
                    byte[] pages = vg.SplitPages(pageFile);
                    if (pages != null)
                        File.WriteAllBytes(ctx.GetOutputArtifactFilePath(pageFile), pages);
                }
                ctx.AddObjectToAsset(vg.name, vg);
                ctx.AddObjectToAsset(collider.name, collider);

                for (int s = 0; s < p.mesh.subMeshCount; ++s)
                    pieceTriangles += (int)(p.mesh.GetIndexCount(s) / 3);
                result.Add(new VirtualGeometryFracture.Piece { mesh = vg, collider = collider, center = p.center, volume = p.volume });
                DestroyImmediate(p.mesh);
            }
            EditorUtility.ClearProgressBar();

            var asset = ScriptableObject.CreateInstance<VirtualGeometryFracture>();
            asset.name = Path.GetFileNameWithoutExtension(ctx.assetPath);
            int sourceTriangles = 0;
            for (int s = 0; s < source.subMeshCount; ++s)
                sourceTriangles += (int)(source.GetIndexCount(s) / 3);
            asset.Init(result.ToArray(), source.subMeshCount, VgFracture.Volume(source), sourceTriangles, pieceTriangles, msFracture, watch.Elapsed.TotalMilliseconds);
            ctx.AddObjectToAsset("fracture", asset);
            ctx.SetMainObject(asset);
        }

        /// <summary>Creates `path` (.vgfracture) for a mesh asset (or a procedural rock when `sourcePath` is null).</summary>
        public static void CreateSidecar(string path, string sourcePath, string meshName, VgFractureSettings fracture,
            VgProceduralSource procedural = null, VgMeshBuildSettings settings = null)
        {
            var def = new VgFractureDefinition
            {
                sourceGuid = sourcePath != null ? AssetDatabase.AssetPathToGUID(sourcePath) : null,
                meshName = meshName,
                procedural = procedural ?? new VgProceduralSource(),
                fracture = fracture ?? new VgFractureSettings(),
                settings = settings ?? new VgMeshBuildSettings(),
            };
            File.WriteAllText(path, JsonUtility.ToJson(def, true));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }
    }
}
