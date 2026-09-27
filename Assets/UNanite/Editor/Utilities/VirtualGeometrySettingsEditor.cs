using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UNanite.Editor
{
    /// <summary>
    /// M14: settings inspector with presets (Quality / Balanced / Performance) and checks of the project
    /// conditions UNanite depends on, above the regular fields.
    /// </summary>
    [CustomEditor(typeof(VirtualGeometrySettings))]
    sealed class VirtualGeometrySettingsEditor : UnityEditor.Editor
    {
        struct Preset
        {
            public string name, tooltip;
            public float pixelError, shadowTexelError, densityLodStart;
            public int streamingPoolMB;
        }

        static readonly Preset[] s_Presets =
        {
            new Preset { name = "Quality", tooltip = "1 px geometry error, 1 texel shadow error, dense grass to 40 m, 1 GB streaming pool.",
                         pixelError = 1f, shadowTexelError = 1f, densityLodStart = 40f, streamingPoolMB = 1024 },
            new Preset { name = "Balanced", tooltip = "The defaults: 1 px, 2 texels, grass thinned from 25 m, 512 MB pool.",
                         pixelError = 1f, shadowTexelError = 2f, densityLodStart = 25f, streamingPoolMB = 512 },
            new Preset { name = "Performance", tooltip = "2 px, 4 texels, grass thinned from 15 m, 384 MB pool (4K: 2 px is the angular detail of 1 px at 1080p).",
                         pixelError = 2f, shadowTexelError = 4f, densityLodStart = 15f, streamingPoolMB = 384 },
        };

        public override void OnInspectorGUI()
        {
            var s = (VirtualGeometrySettings)target;
            EditorGUILayout.LabelField("Presets", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                foreach (var p in s_Presets)
                    if (GUILayout.Button(new GUIContent(p.name, p.tooltip)))
                        Apply(s, p);
            }
            EditorGUILayout.Space(4);
            DrawChecks(s);
            EditorGUILayout.Space(4);
            DrawDefaultInspector();
        }

        public static void Apply(VirtualGeometrySettings s, int preset) => Apply(s, s_Presets[Mathf.Clamp(preset, 0, s_Presets.Length - 1)]);

        static void Apply(VirtualGeometrySettings s, Preset p)
        {
            Undo.RecordObject(s, "UNanite preset " + p.name);
            s.pixelError = p.pixelError;
            s.shadowTexelError = p.shadowTexelError;
            s.densityLodStart = p.densityLodStart;
            s.streamingPoolMB = p.streamingPoolMB;
            EditorUtility.SetDirty(s);
        }

        static void DrawChecks(VirtualGeometrySettings s)
        {
            if (s.virtualShadowMaps || s.terrainVirtualTexture || s.sunShadowClipmap != VgSunClipmapMode.Off)
                EditorGUILayout.HelpBox("Experimental options are on (virtual shadow maps, terrain virtual texture or the sun shadow clipmap). " +
                                        "They are validated but measured no faster than the default path (Documentation~/Milestones.md M12, M13, M13b).", MessageType.Warning);
            if (s.sunShadowClipmap != VgSunClipmapMode.Off && !SunClipmapPatchInstalled())
                EditorGUILayout.HelpBox("The sun shadow clipmap is built but not used: HDRP is not patched (HdrpPatch~/README.md).", MessageType.Warning);
            var api = SystemInfo.graphicsDeviceType;
            if (s.softwareRaster && api != GraphicsDeviceType.Direct3D12 && api != GraphicsDeviceType.Vulkan)
                EditorGUILayout.HelpBox($"The software raster needs D3D12 or Vulkan (64-bit atomics); on {api} small clusters use the hardware raster.", MessageType.Info);
            if (ForcedDynamicResolution(out float percent))
                EditorGUILayout.HelpBox($"The active HDRP asset forces dynamic resolution at {percent:F0} %: cameras that allow it render smaller and upscale " +
                                        "(pixel error then applies to the smaller image). Turn it off on the camera or the asset for native-resolution comparisons.", MessageType.Info);
        }

        static bool SunClipmapPatchInstalled() =>
            File.Exists("Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/Shadow/UNaniteSunShadow.hlsl");

        // HDRP's GlobalDynamicResolutionSettings of the active asset, by reflection (the editor assembly does not reference HDRP)
        static bool ForcedDynamicResolution(out float percent)
        {
            percent = 100f;
            var asset = GraphicsSettings.currentRenderPipeline;
            if (asset == null || asset.GetType().Name != "HDRenderPipelineAsset")
                return false;
            var settings = asset.GetType().GetProperty("currentPlatformRenderPipelineSettings")?.GetValue(asset);
            var drs = settings?.GetType().GetField("dynamicResolutionSettings")?.GetValue(settings);
            if (drs == null)
                return false;
            var t = drs.GetType();
            bool enabled = (bool)(t.GetField("enabled")?.GetValue(drs) ?? false);
            bool forced = (bool)(t.GetField("forceResolution")?.GetValue(drs) ?? false);
            percent = (float)(t.GetField("forcedPercentage")?.GetValue(drs) ?? 100f);
            return enabled && forced && percent < 99.9f;
        }
    }
}
