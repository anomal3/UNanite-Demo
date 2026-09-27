using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace UNanite.Samples.QuarryScene
{
    /// <summary>One imported Megascans asset: a 3D asset, a plant (with its variants) or a surface.</summary>
    [Serializable]
    public sealed class MegascansAsset
    {
        public string id, name, kind, source;     // kind: mesh, plant, surface
        public string[] tags = new string[0];
        public int textureSize;
        public string material, terrainLayer;
        public string[] prefabs = new string[0];  // one per variant: LOD0, coarser LODs (if any) in a LODGroup
        public Vector3 size;                      // bounds of the largest variant, metres
        public long triangles;                    // LOD0 triangles of the first variant

        public bool IsMesh => kind == "mesh";
        public bool IsPlant => kind == "plant";
        public bool IsSurface => kind == "surface";
        public bool HasTag(string tag) => tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) ||
                                          name.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    [Serializable]
    public sealed class MegascansLibrary
    {
        public List<MegascansAsset> assets = new List<MegascansAsset>();

        public static MegascansLibrary Load(string folder)
        {
            string path = folder + "/" + MegascansImporter.LibraryFile;
            return File.Exists(path) ? JsonUtility.FromJson<MegascansLibrary>(File.ReadAllText(path)) : null;
        }
    }

    /// <summary>
    /// Imports Megascans downloads - Fab zips (one asset: mesh, maps, json), Quixel Bridge zips (`..._3d_ms`,
    /// LOD0..5) and collection zips (a folder per asset) - as HDRP/Lit prefabs: every LOD of every variant,
    /// a mask map packed from the metalness, AO and roughness maps, materials, and terrain layers for surfaces.
    /// The downloads stay under their own license: nothing of them ships with UNanite.
    /// Three stages, so a long import can be split: Extract (per zip; files plus their .meta, no import),
    /// AssetDatabase.Refresh, Build (materials, prefabs, terrain layers, the library file).
    /// No AssetPostprocessor on purpose: adding one makes Unity reimport every texture / model of the project.
    /// </summary>
    public static class MegascansImporter
    {
        public const string MetaSuffix = ".megascans.json";
        public const string LibraryFile = "MegascansLibrary.json";

        // sources above this are halved on unpacking (8K scans); the import scales to the asset's textureSize
        const int k_MaxSourceSize = 4096;

        [Serializable]
        sealed class SourceMeta
        {
            public string id, name;
            public string[] tags;
            public SemanticTags semanticTags;
        }

        [Serializable]
        sealed class SemanticTags
        {
            public string name;
        }

        enum Map { Color, Normal, Roughness, Gloss, AO, Metal, Opacity, Height }

        static readonly (Map map, Regex re)[] k_Maps =
        {
            (Map.Color, new Regex(@"_(Base_?Color|Albedo)$", RegexOptions.IgnoreCase)),
            (Map.Normal, new Regex(@"_Normal(_LOD0)?$", RegexOptions.IgnoreCase)),
            (Map.Roughness, new Regex(@"_Roughness$", RegexOptions.IgnoreCase)),
            (Map.Gloss, new Regex(@"_Gloss$", RegexOptions.IgnoreCase)),
            (Map.AO, new Regex(@"_AO$", RegexOptions.IgnoreCase)),
            (Map.Metal, new Regex(@"_Metal(ness|lic)$", RegexOptions.IgnoreCase)),
            (Map.Opacity, new Regex(@"_Opacity$", RegexOptions.IgnoreCase)),
            (Map.Height, new Regex(@"_Displacement$", RegexOptions.IgnoreCase)),
        };

        // `name_VarB_LOD2` -> variant B, LOD 2; files without the suffix are LOD0 of the only variant
        static readonly Regex k_Lod = new Regex(@"(?:_Var(?<var>[A-Za-z0-9]+))?_LOD(?<lod>\d+)$", RegexOptions.IgnoreCase);

        // LODGroup transitions (screen height) of the Unity baseline, LOD0..; the last one culls
        static readonly float[] k_LodHeights = { 0.3f, 0.12f, 0.05f, 0.02f, 0.008f, 0.003f };

        // ------------------------------------------------------------------------------------------
        // Stage 1: unpack

        /// <summary>
        /// Unpacks the assets of one zip into `destination/&lt;Name&gt;/`: meshes as they are; color and normal maps
        /// as they are up to 4K; a mask map packed at `textureSize`; the plants' color with their opacity in alpha;
        /// the asset's `.megascans.json`. Every texture gets its .meta before the import (settings made by Unity
        /// itself on a template texture), so nothing is imported twice. Returns the asset folders.
        /// </summary>
        public static List<string> Extract(string zipPath, string destination, int textureSize = 2048, bool lods = true)
        {
            var folders = new List<string>();
            string zipName = Path.GetFileNameWithoutExtension(zipPath);
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var group in zip.Entries.Where(e => e.Length > 0).GroupBy(e => DirectoryOf(e.FullName)))
            {
                var entries = group.ToList();
                var maps = FindMaps(entries);
                if (!maps.ContainsKey(Map.Color))
                    continue;
                var meshes = entries.Where(e => e.Name.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)).ToList();
                var json = entries.FirstOrDefault(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
                SourceMeta meta = null;
                if (json != null)
                {
                    try { meta = JsonUtility.FromJson<SourceMeta>(ReadText(json)); }
                    catch (Exception) { meta = null; }
                }

                string id = !string.IsNullOrEmpty(meta?.id) ? meta.id : group.Key.Length > 0 ? group.Key.Split('/').Last() : zipName;
                // the json's name (Bridge), its semantic name (Fab), else the zip's name (+ the folder of a collection)
                string name = !string.IsNullOrEmpty(meta?.name) ? meta.name :
                              !string.IsNullOrEmpty(meta?.semanticTags?.name) ? meta.semanticTags.name :
                              group.Key.Length > 0 ? $"{Title(zipName)} {id}" : Title(zipName);
                var asset = new MegascansAsset
                {
                    id = id,
                    name = name,
                    kind = meshes.Count == 0 ? "surface" : maps.ContainsKey(Map.Opacity) ? "plant" : "mesh",
                    source = Path.GetFileName(zipPath) + (group.Key.Length > 0 ? "/" + group.Key : ""),
                    tags = meta?.tags ?? new string[0],
                    textureSize = textureSize,
                };
                string folder = $"{destination}/{Safe(name.EndsWith(id, StringComparison.OrdinalIgnoreCase) ? name : name + " " + id)}";
                Directory.CreateDirectory(folder);
                File.WriteAllText($"{folder}/{id}{MetaSuffix}", JsonUtility.ToJson(asset, true));

                foreach (var mesh in meshes.Where(e => lods || LodOf(e.Name).lod == 0))
                    mesh.ExtractToFile($"{folder}/{mesh.Name}", true);

                if (maps.TryGetValue(Map.Opacity, out var opacity))
                {
                    string colorPath = $"{folder}/{id}_BaseColor.png";
                    WritePng(colorPath, textureSize, (maps[Map.Color], 0, 0), (maps[Map.Color], 1, 0), (maps[Map.Color], 2, 0), (opacity, 0, 255));
                    WriteMeta(colorPath, Role.ColorAlpha, textureSize);
                }
                else
                    CopyTexture(maps[Map.Color], $"{folder}/{id}_BaseColor", Role.Color, textureSize);
                if (maps.TryGetValue(Map.Normal, out var normal))
                    CopyTexture(normal, $"{folder}/{id}_Normal", Role.Normal, textureSize);

                // HDRP mask map: R metallic, G ambient occlusion, B detail mask (terrain layers: height), A smoothness
                maps.TryGetValue(Map.Metal, out var metal);
                maps.TryGetValue(Map.AO, out var ao);
                maps.TryGetValue(Map.Height, out var height);
                bool gloss = !maps.TryGetValue(Map.Roughness, out var smooth) && maps.TryGetValue(Map.Gloss, out smooth);
                string maskPath = $"{folder}/{id}_Mask.png";
                WritePng(maskPath, textureSize, (metal, 0, 0), (ao, 0, 255), (asset.IsSurface ? height : null, 0, 128), (smooth, 0, gloss ? -1 : -2));
                WriteMeta(maskPath, Role.Mask, textureSize);
                folders.Add(folder);
            }
            return folders;
        }

        static Dictionary<Map, ZipArchiveEntry> FindMaps(List<ZipArchiveEntry> entries)
        {
            var maps = new Dictionary<Map, ZipArchiveEntry>();
            foreach (var e in entries)
            {
                string ext = Path.GetExtension(e.Name).ToLowerInvariant();
                if (ext != ".jpg" && ext != ".jpeg" && ext != ".png")
                    continue;
                string n = Path.GetFileNameWithoutExtension(e.Name);
                if (n.IndexOf("Billboard", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Preview", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                foreach (var (map, re) in k_Maps)
                    if (re.IsMatch(n) && (!maps.TryGetValue(map, out var previous) || previous.Length < e.Length))
                        maps[map] = e;
            }
            return maps;
        }

        static (string variant, int lod) LodOf(string file)
        {
            var m = k_Lod.Match(Path.GetFileNameWithoutExtension(file));
            return m.Success ? (m.Groups["var"].Value, int.Parse(m.Groups["lod"].Value)) : ("", 0);
        }

        // a color / normal map as it is, or re-encoded at 4K when larger (8K scans)
        static void CopyTexture(ZipArchiveEntry entry, string pathWithoutExtension, Role role, int textureSize)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            string path;
            try
            {
                var bytes = ReadBytes(entry);
                if (!tex.LoadImage(bytes, false))
                    throw new IOException("cannot decode " + entry.FullName);
                if (Mathf.Max(tex.width, tex.height) <= k_MaxSourceSize)
                {
                    path = pathWithoutExtension + Path.GetExtension(entry.Name).ToLowerInvariant();
                    File.WriteAllBytes(path, bytes);
                }
                else
                {
                    var rgb = new byte[3][];
                    int w = 0, h = 0;
                    for (int c = 0; c < 3; ++c)
                        rgb[c] = Downsample(tex, c, k_MaxSourceSize, out w, out h);
                    var data = new byte[w * h * 3];
                    for (int i = 0, o = 0; i < w * h; ++i, o += 3)
                    {
                        data[o] = rgb[0][i];
                        data[o + 1] = rgb[1][i];
                        data[o + 2] = rgb[2][i];
                    }
                    path = pathWithoutExtension + ".jpg";
                    File.WriteAllBytes(path, ImageConversion.EncodeArrayToJPG(data, GraphicsFormat.R8G8B8_UNorm, (uint)w, (uint)h, 0, 95));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
            WriteMeta(path, role, textureSize);
        }

        // ------------------------------------------------------------------------------------------
        // Channel packing (CPU: decode, box-filter down to `size`, interleave)

        /// <summary>
        /// Writes an RGBA PNG whose channels come from (entry, source channel, default). A null entry fills the
        /// channel with the default; default -2 = inverted source (roughness -> smoothness), -1 = source as is.
        /// </summary>
        static void WritePng(string path, int size, params (ZipArchiveEntry entry, int channel, int fill)[] channels)
        {
            var planes = new byte[4][];
            int w = 0, h = 0;
            var decoded = new Dictionary<ZipArchiveEntry, Texture2D>();
            try
            {
                for (int c = 0; c < 4; ++c)
                {
                    var (entry, channel, fill) = channels[c];
                    if (entry == null)
                        continue;
                    if (!decoded.TryGetValue(entry, out var tex))
                    {
                        tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                        if (!tex.LoadImage(ReadBytes(entry), false))
                            throw new IOException("cannot decode " + entry.FullName);
                        decoded[entry] = tex;
                    }
                    planes[c] = Downsample(tex, channel, size, out int pw, out int ph);
                    if (w != 0 && (pw != w || ph != h))
                        planes[c] = Resample(planes[c], pw, ph, w, h);
                    else
                    {
                        w = pw;
                        h = ph;
                    }
                    if (fill == -2)
                        for (int i = 0; i < planes[c].Length; ++i)
                            planes[c][i] = (byte)(255 - planes[c][i]);
                }
            }
            finally
            {
                foreach (var t in decoded.Values)
                    UnityEngine.Object.DestroyImmediate(t);
            }
            if (w == 0)
                w = h = 4;
            var rgba = new byte[w * h * 4];
            for (int c = 0; c < 4; ++c)
            {
                var plane = planes[c];
                byte fill = (byte)Mathf.Clamp(channels[c].fill, 0, 255);
                for (int i = 0, o = c; i < w * h; ++i, o += 4)
                    rgba[o] = plane != null ? plane[i] : fill;
            }
            File.WriteAllBytes(path, ImageConversion.EncodeArrayToPNG(rgba, GraphicsFormat.R8G8B8A8_UNorm, (uint)w, (uint)h));
        }

        // one channel of a decoded image, box-filtered by the power-of-two factor that brings it to <= size
        static unsafe byte[] Downsample(Texture2D tex, int channel, int size, out int dw, out int dh)
        {
            int w = tex.width, h = tex.height;
            int bpp, offset;
            switch (tex.format)
            {
                case TextureFormat.RGB24: bpp = 3; offset = Mathf.Min(channel, 2); break;
                case TextureFormat.RGBA32: bpp = 4; offset = channel; break;
                case TextureFormat.ARGB32: bpp = 4; offset = (channel + 1) & 3; break;
                case TextureFormat.BGRA32: bpp = 4; offset = channel == 3 ? 3 : 2 - channel; break;
                case TextureFormat.R8:
                case TextureFormat.Alpha8: bpp = 1; offset = 0; break;
                default: throw new NotSupportedException($"{tex.format} ({tex.name})");
            }
            int f = 1;
            while (Mathf.Max(w, h) / f > size && w % (f * 2) == 0 && h % (f * 2) == 0)
                f *= 2;
            dw = w / f;
            dh = h / f;
            var raw = tex.GetRawTextureData<byte>();
            byte* src = (byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(raw);
            var dst = new byte[dw * dh];
            int area = f * f;
            for (int y = 0; y < dh; ++y)
                for (int x = 0; x < dw; ++x)
                {
                    int sum = 0;
                    for (int j = 0; j < f; ++j)
                    {
                        byte* row = src + ((long)(y * f + j) * w + x * f) * bpp + offset;
                        for (int i = 0; i < f; ++i)
                            sum += row[i * bpp];
                    }
                    dst[y * dw + x] = (byte)((sum + area / 2) / area);
                }
            return dst;
        }

        static byte[] Resample(byte[] src, int w, int h, int dw, int dh)
        {
            var dst = new byte[dw * dh];
            for (int y = 0; y < dh; ++y)
                for (int x = 0; x < dw; ++x)
                    dst[y * dw + x] = src[(y * h / dh) * w + x * w / dw];
            return dst;
        }

        // ------------------------------------------------------------------------------------------
        // .meta files written before the import: a template per (role, size) that Unity itself produced

        enum Role { Color, ColorAlpha, Normal, Mask }

        const string k_TemplateFolder = "Assets/UNaniteMegascansTemplates";
        static readonly Dictionary<(Role, int), string> s_Templates = new Dictionary<(Role, int), string>();
        static readonly Regex k_Guid = new Regex(@"(?m)^guid: [0-9a-fA-F]{32}");

        static void WriteMeta(string path, Role role, int size) =>
            File.WriteAllText(path + ".meta", k_Guid.Replace(Template(role, size), "guid: " + Guid.NewGuid().ToString("N"), 1));

        static string Template(Role role, int size)
        {
            if (s_Templates.TryGetValue((role, size), out var text))
                return text;
            Directory.CreateDirectory(k_TemplateFolder);
            string path = $"{k_TemplateFolder}/{role}_{size}.png";
            var tiny = new byte[4 * 4 * 4];
            File.WriteAllBytes(path, ImageConversion.EncodeArrayToPNG(tiny, GraphicsFormat.R8G8B8A8_UNorm, 4, 4));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.maxTextureSize = size;
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            ti.mipmapEnabled = true;
            ti.anisoLevel = 8;
            switch (role)
            {
                case Role.Normal:
                    ti.textureType = TextureImporterType.NormalMap;
                    break;
                case Role.Mask:
                    ti.sRGBTexture = false;
                    break;
                case Role.ColorAlpha:
                    ti.alphaIsTransparency = true;
                    ti.mipMapsPreserveCoverage = true;
                    ti.alphaTestReferenceValue = 0.4f;
                    break;
            }
            ti.SaveAndReimport();
            text = File.ReadAllText(path + ".meta");
            AssetDatabase.DeleteAsset(path);
            if (Directory.GetFileSystemEntries(k_TemplateFolder).Length == 0)
                AssetDatabase.DeleteAsset(k_TemplateFolder);
            s_Templates[(role, size)] = text;
            return text;
        }

        // ------------------------------------------------------------------------------------------
        // Stage 3: materials, prefabs, terrain layers

        /// <summary>
        /// Builds (or refreshes) the materials, prefabs and terrain layers of every asset under `destination`
        /// and writes the library file the scene builder reads.
        /// </summary>
        public static MegascansLibrary Build(string destination, bool meshLods = true)
        {
            var library = new MegascansLibrary();
            foreach (var json in Directory.GetFiles(destination, "*" + MetaSuffix, SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                var asset = JsonUtility.FromJson<MegascansAsset>(File.ReadAllText(json));
                string folder = Path.GetDirectoryName(json).Replace('\\', '/');
                try
                {
                    BuildAsset(asset, folder, meshLods);
                }
                catch (Exception e)
                {
                    Debug.LogError($"Megascans: {asset.name} failed: {e}");
                    continue;
                }
                File.WriteAllText(json, JsonUtility.ToJson(asset, true));
                library.assets.Add(asset);
            }
            AssetDatabase.SaveAssets();
            File.WriteAllText($"{destination}/{LibraryFile}", JsonUtility.ToJson(library, true));
            AssetDatabase.ImportAsset($"{destination}/{LibraryFile}");
            return library;
        }

        /// <summary>`meshLods`: meshes without LOD files of their own get Unity's Mesh LOD (the regular-renderer
        /// baseline a Unity 6 project would use; virtual geometry builds from LOD0 either way).</summary>
        static void BuildAsset(MegascansAsset asset, string folder, bool meshLods)
        {
            var files = Directory.GetFiles(folder).Select(p => p.Replace('\\', '/')).Where(p => !p.EndsWith(".meta")).ToList();
            Texture2D Tex(string suffix) => files.Where(p => Path.GetFileNameWithoutExtension(p) == asset.id + suffix)
                                                  .Select(AssetDatabase.LoadAssetAtPath<Texture2D>).FirstOrDefault(t => t != null);
            var color = Tex("_BaseColor");
            var normal = Tex("_Normal");
            var mask = Tex("_Mask");

            string materialPath = $"{folder}/{asset.id}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                material = new Material(Shader.Find("HDRP/Lit"));
                AssetDatabase.CreateAsset(material, materialPath);
            }
            material.SetColor("_BaseColor", Color.white);
            material.SetTexture("_BaseColorMap", color);
            material.SetTexture("_NormalMap", normal);
            material.SetFloat("_NormalScale", 1f);
            material.SetTexture("_MaskMap", mask);
            material.SetFloat("_MetallicRemapMin", 0f);
            material.SetFloat("_MetallicRemapMax", 1f);
            material.SetFloat("_SmoothnessRemapMin", 0f);
            material.SetFloat("_SmoothnessRemapMax", 1f);
            material.SetFloat("_AORemapMin", 0f);
            material.SetFloat("_AORemapMax", 1f);
            if (asset.IsPlant)
            {
                material.SetFloat("_AlphaCutoffEnable", 1f);
                material.SetFloat("_AlphaCutoff", 0.4f);
                material.SetFloat("_DoubleSidedEnable", 1f);
                material.SetFloat("_DoubleSidedNormalMode", 1f); // mirror
            }
            HDMaterial.ValidateMaterial(material);
            EditorUtility.SetDirty(material);
            asset.material = materialPath;

            if (asset.IsSurface)
            {
                string layerPath = $"{folder}/{asset.id}.terrainlayer";
                var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(layerPath);
                if (layer == null)
                {
                    layer = new TerrainLayer();
                    AssetDatabase.CreateAsset(layer, layerPath);
                }
                layer.diffuseTexture = color;
                layer.normalMapTexture = normal;
                layer.maskMapTexture = mask;
                layer.normalScale = 1f;
                layer.tileSize = new Vector2(2f, 2f);   // Megascans surfaces are 2 m scans
                EditorUtility.SetDirty(layer);
                asset.terrainLayer = layerPath;
                return;
            }

            // prefabs: one per variant, LOD0 alone or all LODs in a LODGroup (the Unity baseline)
            var variants = files.Where(p => p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                                .Select(p => (path: p, lod: LodOf(p)))
                                .GroupBy(x => x.lod.variant)
                                .OrderBy(g => g.Key, StringComparer.Ordinal)
                                .ToList();
            var prefabs = new List<string>();
            asset.size = Vector3.zero;
            asset.triangles = 0;
            foreach (var variant in variants)
            {
                if (variant.Count() == 1 && AssetImporter.GetAtPath(variant.First().path) is ModelImporter mi && mi.generateMeshLods != meshLods)
                {
                    mi.generateMeshLods = meshLods;
                    mi.SaveAndReimport();
                }
                var lods = variant.OrderBy(x => x.lod.lod).Select(x => AssetDatabase.LoadAllAssetsAtPath(x.path).OfType<Mesh>().ToArray()).Where(m => m.Length > 0).ToList();
                if (lods.Count == 0)
                    continue;
                string prefabName = variant.Key.Length > 0 ? $"{Path.GetFileName(folder)}_{variant.Key}" : Path.GetFileName(folder);
                var root = new GameObject(prefabName);
                try
                {
                    if (lods.Count == 1)
                        AddMeshes(root, lods[0], material);
                    else
                    {
                        var group = root.AddComponent<LODGroup>();
                        var levels = new LOD[lods.Count];
                        for (int l = 0; l < lods.Count; ++l)
                        {
                            var child = new GameObject($"LOD{l}");
                            child.transform.SetParent(root.transform, false);
                            float height = k_LodHeights[Mathf.Min(l, k_LodHeights.Length - 1)];
                            levels[l] = new LOD(l == lods.Count - 1 ? Mathf.Min(height, 0.003f) : height, AddMeshes(child, lods[l], material));
                        }
                        group.SetLODs(levels);
                        group.RecalculateBounds();
                    }
                    var bounds = new Bounds();
                    bool first = true;
                    foreach (var m in lods[0])
                    {
                        if (first) bounds = m.bounds; else bounds.Encapsulate(m.bounds);
                        first = false;
                    }
                    if (bounds.size.sqrMagnitude > asset.size.sqrMagnitude)
                        asset.size = bounds.size;
                    if (asset.triangles == 0)
                        asset.triangles = lods[0].Sum(Triangles);
                    string prefabPath = $"{folder}/{prefabName}.prefab";
                    PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                    prefabs.Add(prefabPath);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }
            }
            asset.prefabs = prefabs.ToArray();
        }

        // the meshes of one FBX under `parent` (one renderer each; one child per mesh when there are several)
        static Renderer[] AddMeshes(GameObject parent, Mesh[] meshes, Material material)
        {
            var renderers = new List<Renderer>();
            foreach (var mesh in meshes)
            {
                var go = meshes.Length == 1 ? parent : new GameObject(mesh.name);
                if (go != parent)
                    go.transform.SetParent(parent.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterials = Enumerable.Repeat(material, Mathf.Max(1, mesh.subMeshCount)).ToArray();
                renderers.Add(mr);
            }
            return renderers.ToArray();
        }

        public static long Triangles(Mesh mesh)
        {
            long t = 0;
            for (int s = 0; s < mesh.subMeshCount; ++s)
                if (mesh.GetTopology(s) == MeshTopology.Triangles)
                    t += mesh.GetIndexCount(s) / 3;
            return t;
        }

        // ------------------------------------------------------------------------------------------
        // helpers

        static string DirectoryOf(string entry)
        {
            int slash = entry.LastIndexOf('/');
            return slash < 0 ? "" : entry.Substring(0, slash);
        }

        static byte[] ReadBytes(ZipArchiveEntry e)
        {
            using var s = e.Open();
            using var ms = new MemoryStream((int)e.Length);
            s.CopyTo(ms);
            return ms.ToArray();
        }

        static string ReadText(ZipArchiveEntry e)
        {
            using var r = new StreamReader(e.Open());
            return r.ReadToEnd();
        }

        // "south_african_slate_quarry_high" -> "South African Slate Quarry"
        static string Title(string zipName)
        {
            var words = zipName.Split('_', ' ', '-').Where(w => w.Length > 0).ToList();
            while (words.Count > 1 && Regex.IsMatch(words[words.Count - 1], @"^(high|mid|low|raw|ms|3d|\d+k)$", RegexOptions.IgnoreCase))
                words.RemoveAt(words.Count - 1);
            return string.Join(" ", words.Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1)));
        }

        static string Safe(string name) => Regex.Replace(name.Trim(), @"[^A-Za-z0-9]+", "_").Trim('_');
    }
}
