using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UNanite.Editor
{
    /// <summary>
    /// M10: generates VG variants of material shaders: the same rasterisation passes with the vertex
    /// entry replaced by vertex pulling from the page pool (Runtime/ShaderLibrary/VgPulled.hlsl), so
    /// every pass of the material (vertex graph included) draws virtual geometry with per-instance
    /// object matrices. Sources: Shader Graphs (their generated code, ShaderGraphImporter internals).
    /// Variants are written to <c>Assets/UNanite Generated/Shaders</c> and registered in
    /// <see cref="VgShaderVariants"/> (Resources) for the runtime.
    /// </summary>
    public static class VgShaderVariantGenerator
    {
        public const string Folder = "Assets/UNanite Generated/Shaders";
        public const string RegistryPath = "Assets/UNanite Generated/Resources/" + VgShaderVariants.ResourceName + ".asset";
        const string k_GeneratorVersion = "5"; // bump when the patching changes: variants regenerate
        static string Lib => VgPackagePaths.ShaderLibrary; // Packages/... or Assets/UNanite/... (.unitypackage install)

        // passes without a mesh vertex stage we can pull (lightmap baking, ray tracing) or not needed
        // for BRG draws (picking / selection go through the fallback renderer, full-screen debug)
        static readonly HashSet<string> s_DroppedPasses = new HashSet<string>
        {
            "META", "RayTracingPrepass", "ScenePickingPass", "SceneSelectionPass", "FullScreenDebug",
        };

        [MenuItem("Tools/UNanite/Generate Shader Variants for Scene", priority = 30)]
        static void GenerateForScene()
        {
            var shaders = new HashSet<Shader>();
            foreach (var r in UnityEngine.Object.FindObjectsByType<VirtualGeometryRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                foreach (var m in r.SharedMaterials)
                    if (m != null && m.shader != null)
                        shaders.Add(m.shader);
            int made = 0;
            foreach (var s in shaders)
                if (GetOrCreate(s, out _) != null)
                    made++;
            Debug.Log($"UNanite: {made} of {shaders.Count} material shader(s) have a VG variant");
        }

        /// <summary>True if `shader` is a source we can generate a variant for.</summary>
        public static bool CanGenerate(Shader shader)
        {
            if (shader == null)
                return false;
            string path = AssetDatabase.GetAssetPath(shader);
            return path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns the VG variant of `source`, generating (or regenerating after a change of the source)
        /// it when needed; null if the shader is not supported or the variant does not compile.
        /// </summary>
        public static Shader GetOrCreate(Shader source, out string error)
        {
            error = null;
            if (!CanGenerate(source))
            {
                error = "not a Shader Graph";
                return null;
            }
            string sourcePath = AssetDatabase.GetAssetPath(source);
            string text = GetShaderGraphText(sourcePath, out error);
            if (text == null)
                return null;
            string hash = Hash(k_GeneratorVersion + text);

            var registry = LoadOrCreateRegistry();
            int index = registry.entries.FindIndex(e => e.source == source);
            if (index >= 0 && registry.entries[index].sourceHash == hash && registry.entries[index].variant != null)
                return registry.entries[index].variant;

            string variantText = Patch(text, source.name, out error);
            if (variantText == null)
                return null;

            Directory.CreateDirectory(Folder);
            string file = $"{Folder}/{Sanitize(source.name)}_VG.shader";
            File.WriteAllText(file, variantText);
            AssetDatabase.ImportAsset(file, ImportAssetOptions.ForceSynchronousImport);
            var variant = AssetDatabase.LoadAssetAtPath<Shader>(file);
            if (variant == null || ShaderUtil.ShaderHasError(variant))
            {
                var messages = variant != null ? ShaderUtil.GetShaderMessages(variant) : null;
                error = messages != null && messages.Length > 0 ? messages[0].message : "variant failed to import";
                Debug.LogWarning($"UNanite: VG variant of '{source.name}' does not compile ({error}); it keeps the vertex expansion.", variant);
                return null;
            }

            // the visibility-buffer raster draws undisplaced positions: only graphs that keep them resolve
            bool resolve = text.Contains("description.Position = IN.ObjectSpacePosition;") && variantText.Contains("VgLerpPackedVaryings");
            // M11: graphs that move vertices / clip alpha resolve after their own VgVisBuffer raster
            bool programmable = variantText.Contains("\"VgVisBuffer\"") && variantText.Contains("VgLerpPackedVaryings");
            var entry = new VgShaderVariants.Entry { source = source, variant = variant, sourceHash = hash, resolve = resolve, programmable = programmable };
            if (index >= 0)
                registry.entries[index] = entry;
            else
                registry.entries.Add(entry);
            EditorUtility.SetDirty(registry);
            AssetDatabase.SaveAssetIfDirty(registry);
            VgShaderVariants.Reload();
            return variant;
        }

        static VgShaderVariants LoadOrCreateRegistry()
        {
            var registry = AssetDatabase.LoadAssetAtPath<VgShaderVariants>(RegistryPath);
            if (registry != null)
                return registry;
            Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath));
            registry = ScriptableObject.CreateInstance<VgShaderVariants>();
            AssetDatabase.CreateAsset(registry, RegistryPath);
            return registry;
        }

        // ---------------------------------------------------------------------------------------
        // Shader Graph source

        static string GetShaderGraphText(string path, out string error)
        {
            error = null;
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Unity.ShaderGraph.Editor");
                var importer = asm?.GetType("UnityEditor.ShaderGraph.ShaderGraphImporter");
                var collection = asm?.GetType("UnityEditor.ShaderGraph.AssetCollection");
                var method = importer?.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "GetShaderText" && m.GetParameters().Length == 4 && m.GetParameters()[3].IsOut);
                if (method == null || collection == null)
                {
                    error = "ShaderGraphImporter.GetShaderText not found (Shader Graph version)";
                    return null;
                }
                var args = new object[] { path, null, Activator.CreateInstance(collection), null };
                return (string)method.Invoke(null, args);
            }
            catch (Exception e)
            {
                error = e.GetBaseException().Message;
                return null;
            }
        }

        // ---------------------------------------------------------------------------------------
        // Patching

        /// <summary>
        /// Renames the shader, keeps the first SubShader (rasterisation; the ray-tracing SubShader is
        /// served by the RT proxies' own materials), drops passes without a pullable vertex stage and
        /// replaces the vertex entry of the others.
        /// </summary>
        public static string Patch(string text, string sourceName, out string error)
        {
            error = null;
            var blocks = FindBlocks(text);
            var subShaders = blocks.Where(b => b.keyword == "SubShader" && b.depth == 1).ToList();
            if (subShaders.Count == 0)
            {
                error = "no SubShader";
                return null;
            }
            var first = subShaders[0];
            var sb = new StringBuilder();
            int cursor = 0;
            // header up to the first SubShader, renamed
            string header = text.Substring(0, first.start);
            header = Regex.Replace(header, "^\\s*Shader\\s+\"([^\"]*)\"", m => $"Shader \"Hidden/UNanite/Pulled/{m.Groups[1].Value}\"");
            sb.Append(header);
            cursor = first.start;

            int kept = 0;
            foreach (var pass in blocks.Where(b => b.keyword == "Pass" && b.start > first.start && b.end <= first.end))
            {
                sb.Append(text, cursor, pass.start - cursor);
                string body = text.Substring(pass.start, pass.end - pass.start);
                cursor = pass.end;
                var name = Regex.Match(body, "Name\\s+\"([^\"]*)\"");
                string passName = name.Success ? name.Groups[1].Value : "";
                if (s_DroppedPasses.Contains(passName) || body.Contains("#pragma hull") || !body.Contains("#pragma vertex Vert"))
                    continue;
                string patched = PatchPass(body, passName, out string passError);
                if (patched == null)
                {
                    error = $"pass {passName}: {passError}";
                    return null;
                }
                sb.Append(patched);
                kept++;
                // M11 programmable raster: a copy of the depth pass that writes the visibility buffer
                if (passName == "DepthOnly")
                {
                    string vis = PatchVisPass(body, out string visError);
                    if (vis == null)
                    {
                        error = $"VgVisBuffer pass: {visError}";
                        return null;
                    }
                    sb.Append("\n").Append(vis);
                }
            }
            sb.Append(text, cursor, first.end - cursor);
            // the rest after the first SubShader: drop other SubShaders and the fallback
            string tail = text.Substring(first.end);
            foreach (var other in subShaders.Skip(1).OrderByDescending(b => b.start))
                tail = tail.Remove(other.start - first.end, other.end - other.start);
            tail = Regex.Replace(tail, "^\\s*(FallBack|Fallback|CustomEditor|CustomEditorForRenderPipeline)\\b.*$", "", RegexOptions.Multiline);
            sb.Append(tail);
            if (kept == 0)
            {
                error = "no pullable pass";
                return null;
            }
            return $"// Generated by UNanite (VgShaderVariantGenerator) from \"{sourceName}\". Do not edit.\n" + sb;
        }

        const string k_ShaderVariables = "#include \"Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl\"";
        static readonly Regex s_PassInclude = new Regex("#include\\s+\"Packages/com\\.unity\\.render-pipelines\\.high-definition/Runtime/RenderPipeline/ShaderPass/ShaderPass[A-Za-z]+\\.hlsl\"");

        static string PatchPass(string body, string passName, out string error)
        {
            error = null;
            int sv = body.IndexOf(k_ShaderVariables, StringComparison.Ordinal);
            if (sv < 0)
            {
                error = "ShaderVariables.hlsl include not found";
                return null;
            }
            var includes = s_PassInclude.Matches(body);
            if (includes.Count == 0)
            {
                error = "ShaderPass include not found";
                return null;
            }
            var last = includes[includes.Count - 1];
            // the GBuffer / MotionVectors passes also serve the visibility-buffer resolve (keyword
            // VG_RESOLVE): there the pass's own Vert / Frag are renamed away and
            // VgResolveShaderGraph.hlsl defines the entry points
            string lerp = passName == "GBuffer" ? LerpFunction(body) : null;
            bool resolvePass = lerp != null || passName == "MotionVectors";
            var after = new StringBuilder("\n");
            if (resolvePass)
                after.Append("    #if defined(VG_RESOLVE)\n    #undef Vert\n    #undef Frag\n    #endif\n");
            if (lerp != null)
                after.Append("    #if defined(VG_RESOLVE)\n").Append(lerp).Append("    #endif\n");
            after.Append($"    #include \"{Lib}VgPulled.hlsl\"");
            // insert from the back so earlier offsets stay valid
            body = body.Insert(last.Index + last.Length, after.ToString());
            if (resolvePass)
                body = body.Insert(last.Index, "#if defined(VG_RESOLVE)\n    #define Vert VgUnusedVert\n    #define Frag VgUnusedFrag\n    #endif\n    ");
            body = body.Insert(sv + k_ShaderVariables.Length, $"\n    #include \"{Lib}VgPulledSetup.hlsl\"");
            string vertexPragma = "#pragma vertex VgPulledVert";
            if (resolvePass)
                vertexPragma += "\n#pragma multi_compile_local _ VG_RESOLVE";
            body = body.Replace("#pragma vertex Vert", vertexPragma);
            return body;
        }

        // M11: the graph's DepthOnly pass as "VgVisBuffer" (VgVisRaster.hlsl): vertex graph per corner,
        // alpha test, visibility ID + depth; no stencil / alpha-to-mask, depth tested
        static string PatchVisPass(string body, out string error)
        {
            error = null;
            int sv = body.IndexOf(k_ShaderVariables, StringComparison.Ordinal);
            var includes = s_PassInclude.Matches(body);
            if (sv < 0 || includes.Count == 0)
            {
                error = "includes not found";
                return null;
            }
            var last = includes[includes.Count - 1];
            body = body.Insert(last.Index + last.Length, $"\n    #include \"{Lib}VgVisRaster.hlsl\"");
            body = body.Insert(sv + k_ShaderVariables.Length, $"\n    #include \"{Lib}VgPulledSetup.hlsl\"");
            body = body.Replace("Name \"DepthOnly\"", "Name \"VgVisBuffer\"").Replace("\"LightMode\" = \"DepthOnly\"", "\"LightMode\" = \"VgVisBuffer\"");
            body = Regex.Replace(body, @"Stencil\s*\{[^}]*\}", "");
            body = Regex.Replace(body, @"^\s*AlphaToMask[^\n]*$", "", RegexOptions.Multiline);
            body = Regex.Replace(body, @"^\s*ZWrite On\s*$", "ZWrite On\nZTest LEqual", RegexOptions.Multiline);
            // M11: VG_RASTER_BARY also writes hardware barycentrics (SV_Barycentrics: DXC, D3D12 / Vulkan / Metal),
            // VG_RASTER_MOTION the pixel's motion (the vertex graph at the previous frame's time and wind)
            body = body.Replace("#pragma vertex Vert", "#pragma vertex VgVisVert").Replace("#pragma fragment Frag",
                "#pragma fragment VgVisFrag\n#pragma multi_compile_local _ VG_RASTER_BARY\n#pragma multi_compile_local _ VG_RASTER_MOTION\n" +
                "#pragma require barycentrics : VG_RASTER_BARY\n#pragma use_dxc\nstatic bool g_VgWindHistory; // VgVisRaster.hlsl: the previous frame's SpeedTree wind");
            // SpeedTree 8 wind with the history parameters while g_VgWindHistory is set (the graph itself
            // only evaluates the current ones for its positions)
            body = body.Replace(k_SpeedTree8Wind, k_SpeedTree8Wind + k_SpeedTree8WindHistory);
            return body;
        }

        const string k_SpeedTree8Wind = "#include_with_pragmas \"Packages/com.unity.shadergraph/ShaderGraphLibrary/Nature/SpeedTree8Wind.hlsl\"";
        const string k_SpeedTree8WindHistory = @"
void VgSpeedTreeWind_float(float3 vPos, float3 vNormal, float4 vTexcoord0, float4 vTexcoord1, float4 vTexcoord2, float4 vTexcoord3, int iWindQuality, bool bBillboard, bool bHistory, out float3 outPos)
{
    outPos = SpeedTreeWind(vPos, vNormal, vTexcoord0, vTexcoord1, vTexcoord2, vTexcoord3, iWindQuality, bBillboard, bHistory || g_VgWindHistory);
}
#define SpeedTreeWind_float VgSpeedTreeWind_float";

        // VgLerpPackedVaryings: barycentric interpolation of the pass's PackedVaryingsMeshToPS (every
        // interpolated field; SV_POSITION, the instance ID and nointerpolation fields from corner a).
        static string LerpFunction(string body)
        {
            var m = Regex.Match(body, @"struct PackedVaryingsMeshToPS\s*\{(.*?)\};", RegexOptions.Singleline);
            if (!m.Success)
                return null;
            var sb = new StringBuilder();
            sb.Append("    PackedVaryingsMeshToPS VgLerpPackedVaryings(PackedVaryingsMeshToPS a, PackedVaryingsMeshToPS b, PackedVaryingsMeshToPS c, float3 w)\n    {\n");
            sb.Append("        PackedVaryingsMeshToPS o = a;\n");
            foreach (var line in m.Groups[1].Value.Split('\n'))
            {
                var f = Regex.Match(line, @"^\s*(?:centroid\s+|linear\s+|noperspective\s+)?(float|half|min16float)([1-4])?\s+(\w+)\s*:\s*(\w+)\s*;");
                if (!f.Success || f.Groups[4].Value == "SV_POSITION" || line.Contains("nointerpolation"))
                    continue;
                string n = f.Groups[3].Value;
                sb.Append($"        o.{n} = a.{n} * w.x + b.{n} * w.y + c.{n} * w.z;\n");
            }
            sb.Append("        return o;\n    }\n");
            return sb.ToString();
        }

        struct Block
        {
            public string keyword;
            public int start, end; // keyword start .. after the closing brace
            public int depth;      // brace depth of the keyword (Shader body = 1)
        }

        // Keyword blocks (SubShader / Pass) of ShaderLab text; HLSL program blocks, strings and
        // comments are skipped so braces in code do not count.
        static List<Block> FindBlocks(string t)
        {
            var result = new List<Block>();
            var open = new Stack<(string keyword, int start, int depth)>();
            int depth = 0;
            string pendingKeyword = null;
            int pendingStart = 0;
            for (int i = 0; i < t.Length; ++i)
            {
                char c = t[i];
                if (c == '/' && i + 1 < t.Length && t[i + 1] == '/')
                {
                    int nl = t.IndexOf('\n', i);
                    i = nl < 0 ? t.Length : nl;
                    continue;
                }
                if (c == '"')
                {
                    int q = t.IndexOf('"', i + 1);
                    i = q < 0 ? t.Length : q;
                    continue;
                }
                if (char.IsLetter(c) && (i == 0 || !char.IsLetterOrDigit(t[i - 1])))
                {
                    int j = i;
                    while (j < t.Length && (char.IsLetterOrDigit(t[j]) || t[j] == '_'))
                        j++;
                    string word = t.Substring(i, j - i);
                    if (word == "HLSLPROGRAM" || word == "HLSLINCLUDE" || word == "CGPROGRAM" || word == "CGINCLUDE")
                    {
                        int end = t.IndexOf(word.StartsWith("CG") ? "ENDCG" : "ENDHLSL", j, StringComparison.Ordinal);
                        i = end < 0 ? t.Length : end + 5;
                        continue;
                    }
                    if (word == "SubShader" || word == "Pass")
                    {
                        pendingKeyword = word;
                        pendingStart = i;
                    }
                    i = j - 1;
                    continue;
                }
                if (c == '{')
                {
                    open.Push((pendingKeyword, pendingStart, depth));
                    pendingKeyword = null;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (open.Count > 0)
                    {
                        var o = open.Pop();
                        if (o.keyword != null)
                            result.Add(new Block { keyword = o.keyword, start = o.start, end = i + 1, depth = o.depth });
                    }
                }
            }
            return result.OrderBy(b => b.start).ToList();
        }

        static string Sanitize(string name) => Regex.Replace(name, "[^A-Za-z0-9_-]+", "_");

        static string Hash(string text)
        {
            using (var md5 = MD5.Create())
                return BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
        }
    }

    /// <summary>Regenerates registered variants when their Shader Graph is re-imported.</summary>
    sealed class VgShaderVariantPostprocessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            var graphs = imported.Where(p => p.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase)).ToList();
            if (graphs.Count == 0)
                return;
            var registry = AssetDatabase.LoadAssetAtPath<VgShaderVariants>(VgShaderVariantGenerator.RegistryPath);
            if (registry == null)
                return;
            foreach (var p in graphs)
            {
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(p);
                if (shader != null && registry.entries.Any(e => e.source == shader))
                    EditorApplication.delayCall += () => VgShaderVariantGenerator.GetOrCreate(shader, out _);
            }
        }
    }
}
