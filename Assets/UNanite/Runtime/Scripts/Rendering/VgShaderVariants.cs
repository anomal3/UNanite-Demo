using System;
using System.Collections.Generic;
using UnityEngine;

namespace UNanite
{
    /// <summary>
    /// M10: maps material shaders to their generated VG variants (the same passes with vertex pulling,
    /// Runtime/ShaderLibrary/VgPulled.hlsl). Written by the editor (VgShaderVariantGenerator) into
    /// <c>Assets/UNanite Generated/Resources/UNaniteShaderVariants.asset</c>; loaded from Resources at
    /// runtime so player builds include the variants.
    /// </summary>
    public sealed class VgShaderVariants : ScriptableObject
    {
        [Serializable]
        public struct Entry
        {
            public Shader source;
            public Shader variant;
            /// <summary>Hash of the source text the variant was generated from (editor: regenerate when it changes).</summary>
            public string sourceHash;
            /// <summary>The variant has a visibility-buffer resolve pass (keyword VG_RESOLVE).</summary>
            public bool resolve;
            /// <summary>M11: the variant has the VgVisBuffer pass (programmable visibility-buffer raster).</summary>
            public bool programmable;
        }

        public const string ResourceName = "UNaniteShaderVariants";

        public List<Entry> entries = new List<Entry>();

        static VgShaderVariants s_Loaded;
        static Dictionary<Shader, Entry> s_Lookup;
        static int s_Version;

        /// <summary>Bumped whenever the registry changes (editor regeneration); bins re-check their variants.</summary>
        public static int Version => s_Version;

        public static bool TryGet(Shader source, out Entry entry)
        {
            entry = default;
            if (source == null)
                return false;
            if (s_Lookup == null)
                Reload();
            return s_Lookup.TryGetValue(source, out entry) && entry.variant != null && entry.variant.isSupported;
        }

        /// <summary>Re-reads the registry (called by the editor after it wrote new variants).</summary>
        public static void Reload()
        {
            s_Loaded = Resources.Load<VgShaderVariants>(ResourceName);
            s_Lookup = new Dictionary<Shader, Entry>();
            if (s_Loaded != null)
                foreach (var e in s_Loaded.entries)
                    if (e.source != null && e.variant != null)
                        s_Lookup[e.source] = e;
            s_Version++;
        }
    }
}
