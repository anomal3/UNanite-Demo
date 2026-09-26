using System;
using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace UNanite.Editor
{
    /// <summary>Contents of a .vgterrain sidecar file (JSON).</summary>
    [Serializable]
    public sealed class VgTerrainDefinition
    {
        public string terrainDataGuid;
        public VgTerrainBuildSettings settings = new VgTerrainBuildSettings();
    }

    /// <summary>
    /// Imports a .vgterrain sidecar (M8): builds the HLOD quadtree of virtual geometry tiles of the
    /// referenced TerrainData. Result: a <see cref="VirtualGeometryTerrainData"/> main object plus one
    /// <see cref="VirtualGeometryMesh"/> sub-asset per node, whose streamable pages are side files of
    /// the import artifact (M7). Reimported when the TerrainData, the settings or the native builder
    /// change.
    /// </summary>
    [ScriptedImporter(1, "vgterrain", importQueueOffset: 100)]
    public sealed class VirtualGeometryTerrainImporter : ScriptedImporter
    {
        public const string Extension = "vgterrain";

        public override void OnImportAsset(AssetImportContext ctx)
        {
            ctx.DependsOnCustomDependency(VirtualGeometryImporter.BuilderDependency);

            VgTerrainDefinition def;
            try
            {
                def = JsonUtility.FromJson<VgTerrainDefinition>(File.ReadAllText(ctx.assetPath)) ?? new VgTerrainDefinition();
            }
            catch (Exception e)
            {
                ctx.LogImportError($"Invalid .vgterrain file: {e.Message}");
                return;
            }
            if (string.IsNullOrEmpty(def.terrainDataGuid) || !GUID.TryParse(def.terrainDataGuid, out var guid))
            {
                ctx.LogImportError(".vgterrain has no valid TerrainData GUID");
                return;
            }
            ctx.DependsOnArtifact(guid);
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var terrainData = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<TerrainData>(path);
            if (terrainData == null)
            {
                ctx.LogImportError($"TerrainData {def.terrainDataGuid} not found");
                return;
            }

            var settings = def.settings ?? new VgTerrainBuildSettings();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var src = VgTerrainSource.FromTerrainData(terrainData);
            int precision = VgTerrainBuilder.ChoosePrecision(src.size);
            var nodes = VgTerrainBuilder.Layout(src.Quads, settings.tileQuads);
            string title = $"UNanite: building terrain '{terrainData.name}' ({nodes.Length} tiles)";
            var results = VgTerrainBuilder.BuildAll(src, nodes, settings, precision,
                p => !EditorUtility.DisplayCancelableProgressBar(title, $"{p * 100f:F0}%", p), out string error);
            EditorUtility.ClearProgressBar();
            if (results == null)
            {
                ctx.LogImportError($"Failed to build virtual geometry for terrain '{terrainData.name}': {error}");
                return;
            }

            var stats = new VgTerrainBuildStats { nodes = nodes.Length };
            var uvScale = new Vector2(1f / src.size.x, 1f / src.size.z);
            for (int i = 0; i < nodes.Length; ++i)
            {
                var r = results[i];
                var n = nodes[i];
                var mesh = ScriptableObject.CreateInstance<VirtualGeometryMesh>();
                mesh.name = $"L{n.level}_{n.quads.x}_{n.quads.y}";
                var report = VgNativeBuilder.ToReport(r.stats);
                report.contentHash = Hash128.Compute(r.blob).ToString();
                mesh.Initialize(r.blob, mesh.name, 1, report);
                mesh.UvFromPositionXZ = uvScale;
                long root = mesh.ResidentReader.Header.streamDataOffset - mesh.ResidentReader.Header.pageDataOffset;
                if (settings.pageFile)
                {
                    string pageFile = i + VirtualGeometryMesh.PageFileExtension;
                    byte[] pages = mesh.SplitPages(pageFile);
                    if (pages != null)
                        File.WriteAllBytes(ctx.GetOutputArtifactFilePath(pageFile), pages);
                }
                ctx.AddObjectToAsset(mesh.name, mesh);
                nodes[i].mesh = mesh;

                if (n.level == 0)
                {
                    stats.leaves++;
                    stats.sourceTriangles += r.stats.sourceTriangles;
                }
                stats.levels = Math.Max(stats.levels, n.level + 1);
                stats.storedTriangles += r.stats.totalTriangles;
                stats.blobBytes += (long)r.stats.blobBytes;
                stats.rootBytes += root;
            }
            stats.msTotal = watch.Elapsed.TotalMilliseconds;

            var data = ScriptableObject.CreateInstance<VirtualGeometryTerrainData>();
            data.name = terrainData.name + " (VG)";
            data.Initialize(nodes, src.resolution, src.size, precision, settings, AssetDatabase.GetAssetDependencyHash(path).ToString(), stats);
            ctx.AddObjectToAsset("terrain", data);
            ctx.SetMainObject(data);
        }

        public static string SidecarPathFor(string terrainDataPath) => Path.ChangeExtension(terrainDataPath, Extension);

        /// <summary>Writes (or rewrites) the sidecar of a TerrainData and imports it; returns the built data.</summary>
        public static VirtualGeometryTerrainData CreateSidecar(TerrainData terrainData, VgTerrainBuildSettings settings = null)
        {
            string tdPath = AssetDatabase.GetAssetPath(terrainData);
            if (string.IsNullOrEmpty(tdPath))
                throw new ArgumentException("TerrainData must be an asset");
            var def = new VgTerrainDefinition
            {
                terrainDataGuid = AssetDatabase.AssetPathToGUID(tdPath),
                settings = settings ?? new VgTerrainBuildSettings(),
            };
            string path = SidecarPathFor(tdPath);
            File.WriteAllText(path, JsonUtility.ToJson(def, true));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return AssetDatabase.LoadAssetAtPath<VirtualGeometryTerrainData>(path);
        }
    }
}
