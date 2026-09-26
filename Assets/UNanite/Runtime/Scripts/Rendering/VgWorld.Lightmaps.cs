using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace UNanite
{
    // M9 follow-up: baked lightmaps. A lightmapped instance puts its material slots in bins of their own
    // (key: material + lightmapped); their resolve twins enable VG_LIGHTMAP (VG_LIGHTMAP_DIR with
    // directional lightmaps) and get the scene's lightmaps as texture arrays (unity_Lightmaps,
    // unity_LightmapsInd, unity_ShadowMasks), the layout HDRP samples under DOTS instancing. The
    // instance's lightmap scale/offset is VG_InstanceLightmaps[instance], its array slice
    // VgInstanceGpu.lightmapIndex (VgResolveLightmap.hlsl). Realtime (Enlighten) lightmaps and terrain
    // lightmaps are not supported.
    partial class VgWorld
    {
        readonly List<bool> m_BinLightmapped = new List<bool>();
        readonly List<int> m_BinLightmapVersion = new List<int>(); // arrays version applied to the bin's twin
        Vector4[] m_InstanceLightmapST = new Vector4[256];
        bool m_LightmapSTDirty;
        GraphicsBuffer m_InstanceLightmapBuffer;
        int m_LightmappedBins;

        Texture2DArray m_LightmapColors, m_LightmapDirs, m_LightmapMasks, m_WhiteArray;
        Texture2D[] m_LightmapSources = new Texture2D[0];
        int m_LightmapVersion = 1;
        int m_LightmapCheckFrame = int.MinValue;

        static readonly int k_InstanceLightmaps = Shader.PropertyToID("VG_InstanceLightmaps");
        static readonly int k_Lightmaps = Shader.PropertyToID("unity_Lightmaps");
        static readonly int k_LightmapsInd = Shader.PropertyToID("unity_LightmapsInd");
        static readonly int k_ShadowMasks = Shader.PropertyToID("unity_ShadowMasks");

        /// <summary>Number of scene lightmaps copied into the texture arrays (0 = none).</summary>
        public int LightmapCount => m_LightmapColors != null ? m_LightmapColors.depth : 0;

        static bool IsLightmapped(int lightmapIndex) => lightmapIndex >= 0 && lightmapIndex < 0xFFFE;

        void EnsureLightmapCapacity(int capacity)
        {
            if (m_InstanceLightmapST.Length < capacity)
                Array.Resize(ref m_InstanceLightmapST, capacity);
        }

        void SetInstanceLightmap(int handle, int lightmapIndex, Vector4 scaleOffset)
        {
            EnsureLightmapCapacity(m_Instances.Length);
            bool lightmapped = IsLightmapped(lightmapIndex);
            m_Instances[handle].lightmapIndex = lightmapped ? (uint)lightmapIndex : VgFormat.Invalid;
            m_InstanceLightmapST[handle] = lightmapped ? scaleOffset : Vector4.zero;
            if (lightmapped)
                m_LightmapSTDirty = true;
        }

        // bins of lightmapped instances need a resolve twin (the expansion path draws the source
        // material without lightmaps) and not a registered override (terrain)
        bool CanLightmap(Material material) =>
            material != null && m_ResolveShader != null && !s_ResolveOverrides.ContainsKey(material) && IsResolveCapable(material);

        void OnBinCreated(int bin, bool lightmapped)
        {
            m_BinLightmapped.Add(lightmapped);
            m_BinLightmapVersion.Add(0);
            if (lightmapped)
            {
                m_LightmappedBins++;
                m_LightmapCheckFrame = int.MinValue; // build the arrays before the first draw
            }
        }

        // called from RefreshBin once the twin is in sync with its source (the source copy resets textures)
        void ApplyLightmapTwin(int bin, Material twin, bool propertiesCopied)
        {
            if (bin >= m_BinLightmapped.Count || !m_BinLightmapped[bin] || twin == null)
                return;
            if (!propertiesCopied && m_BinLightmapVersion[bin] == m_LightmapVersion)
                return;
            m_BinLightmapVersion[bin] = m_LightmapVersion;
            bool dir = m_LightmapDirs != null;
            if (dir)
            {
                twin.DisableKeyword("VG_LIGHTMAP");
                twin.EnableKeyword("VG_LIGHTMAP_DIR");
            }
            else
            {
                twin.DisableKeyword("VG_LIGHTMAP_DIR");
                twin.EnableKeyword("VG_LIGHTMAP");
            }
            twin.SetTexture(k_Lightmaps, m_LightmapColors != null ? m_LightmapColors : WhiteArray());
            twin.SetTexture(k_LightmapsInd, dir ? m_LightmapDirs : WhiteArray());
            twin.SetTexture(k_ShadowMasks, m_LightmapMasks != null ? m_LightmapMasks : WhiteArray());
        }

        Texture2DArray WhiteArray()
        {
            if (m_WhiteArray == null)
            {
                m_WhiteArray = new Texture2DArray(1, 1, 1, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, name = "UNanite white lightmap" };
                m_WhiteArray.SetPixels(new[] { Color.white }, 0);
                m_WhiteArray.Apply(false, true);
            }
            return m_WhiteArray;
        }

        // Rebuilds the arrays when the scene's lightmaps change (checked every 60 frames while
        // lightmapped bins exist: scene loads, bakes).
        void UpdateLightmaps()
        {
            if (m_LightmappedBins == 0)
                return;
            int frame = Time.frameCount;
            if (m_LightmapCheckFrame != int.MinValue && frame >= m_LightmapCheckFrame && frame - m_LightmapCheckFrame < 60)
                return;
            m_LightmapCheckFrame = frame;

            var lightmaps = LightmapSettings.lightmaps;
            bool same = lightmaps.Length == m_LightmapSources.Length;
            for (int i = 0; same && i < lightmaps.Length; ++i)
                same = lightmaps[i].lightmapColor == m_LightmapSources[i];
            if (same)
                return;

            m_LightmapSources = new Texture2D[lightmaps.Length];
            for (int i = 0; i < lightmaps.Length; ++i)
                m_LightmapSources[i] = lightmaps[i].lightmapColor;
            DestroyLightmapArrays();
            m_LightmapColors = BuildArray(lightmaps, l => l.lightmapColor, "colour");
            m_LightmapDirs = BuildArray(lightmaps, l => l.lightmapDir, "directionality");
            m_LightmapMasks = BuildArray(lightmaps, l => l.shadowMask, "shadow mask");
            m_LightmapVersion++;
            for (int b = 0; b < m_BinLightmapped.Count; ++b)
                ApplyLightmapTwin(b, m_BinResolveMaterials[b], false);
        }

        static Texture2DArray BuildArray(LightmapData[] lightmaps, Func<LightmapData, Texture2D> pick, string what)
        {
            Texture2D first = null;
            foreach (var l in lightmaps)
                if (pick(l) != null)
                {
                    first = pick(l);
                    break;
                }
            if (first == null)
                return null;
            var array = new Texture2DArray(first.width, first.height, lightmaps.Length, first.graphicsFormat,
                first.mipmapCount > 1 ? TextureCreationFlags.MipChain : TextureCreationFlags.None, first.mipmapCount)
            {
                hideFlags = HideFlags.HideAndDontSave,
                name = "UNanite lightmaps (" + what + ")",
                filterMode = first.filterMode,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = first.anisoLevel,
            };
            for (int i = 0; i < lightmaps.Length; ++i)
            {
                var t = pick(lightmaps[i]);
                if (t == null)
                    continue;
                if (t.width != first.width || t.height != first.height || t.graphicsFormat != first.graphicsFormat || t.mipmapCount != first.mipmapCount)
                {
                    Debug.LogWarning($"UNanite: lightmap {what} {i} ({t.width}x{t.height} {t.graphicsFormat}) differs from lightmap 0 ({first.width}x{first.height} {first.graphicsFormat}); " +
                                     "virtual geometry needs all lightmaps of one size and format (texture array). Instances on it render unlit by the lightmap.", t);
                    continue;
                }
                Graphics.CopyTexture(t, 0, array, i);
            }
            return array;
        }

        void UploadLightmapST()
        {
            if (m_LightmappedBins == 0 && m_InstanceLightmapBuffer != null)
                return;
            int count = Mathf.Max(1, m_Instances.Length);
            if (m_InstanceLightmapBuffer == null || m_InstanceLightmapBuffer.count < count)
            {
                m_InstanceLightmapBuffer?.Dispose();
                m_InstanceLightmapBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 16);
                m_LightmapSTDirty = true;
            }
            if (m_LightmapSTDirty)
            {
                m_LightmapSTDirty = false;
                EnsureLightmapCapacity(count);
                m_InstanceLightmapBuffer.SetData(m_InstanceLightmapST, 0, 0, Mathf.Min(count, Mathf.Max(1, m_InstanceHighWater)));
            }
            Shader.SetGlobalBuffer(k_InstanceLightmaps, m_InstanceLightmapBuffer);
        }

        void DestroyLightmapArrays()
        {
            foreach (var a in new[] { m_LightmapColors, m_LightmapDirs, m_LightmapMasks })
                if (a != null)
                    UnityEngine.Rendering.CoreUtils.Destroy(a);
            m_LightmapColors = m_LightmapDirs = m_LightmapMasks = null;
        }

        void DisposeLightmaps()
        {
            DestroyLightmapArrays();
            if (m_WhiteArray != null)
                UnityEngine.Rendering.CoreUtils.Destroy(m_WhiteArray);
            m_WhiteArray = null;
            m_InstanceLightmapBuffer?.Dispose();
            m_InstanceLightmapBuffer = null;
        }
    }
}
