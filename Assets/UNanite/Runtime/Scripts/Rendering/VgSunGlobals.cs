using UnityEngine;

namespace UNanite
{
    /// <summary>
    /// M13b: the global resources the optional HDRP patch declares in every lighting shader
    /// (VgSunClipmap.hlsl). HDRP binds them in every draw of a patched shader, used or not, so they must
    /// never point at a released buffer: they fall back to tiny dummies that live as long as the scripting
    /// domain whenever UNanite's buffers are released or replaced (a released structured buffer left bound
    /// crashed D3D12: "invalid CBV_SRV_UAV descriptor").
    /// </summary>
    static class VgSunGlobals
    {
        static readonly int s_Entries = Shader.PropertyToID("_UNaniteSunEntries");
        static readonly int s_Pages = Shader.PropertyToID("_UNaniteSunPages");
        static readonly int s_Pool = Shader.PropertyToID("_UNaniteSunPool");
        static readonly int s_Params = Shader.PropertyToID("_UNaniteSunParams");
        static GraphicsBuffer s_DummyEntries, s_DummyPages;

        /// <summary>UNanite's live buffers (the clipmap pass also sets them per camera).</summary>
        public static void Bind(GraphicsBuffer entries, GraphicsBuffer pages, Texture pool)
        {
            if (entries == null || pages == null || pool == null)
            {
                BindDummies();
                return;
            }
            Shader.SetGlobalBuffer(s_Entries, entries);
            Shader.SetGlobalBuffer(s_Pages, pages);
            Shader.SetGlobalTexture(s_Pool, pool);
        }

        /// <summary>Lookup off, dummy resources bound (before UNanite's buffers are released).</summary>
        public static void BindDummies()
        {
            if (s_DummyEntries == null || !s_DummyEntries.IsValid())
            {
                s_DummyEntries = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 192); // VgSunEntry
                s_DummyEntries.SetData(new uint[48]);
            }
            if (s_DummyPages == null || !s_DummyPages.IsValid())
            {
                s_DummyPages = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 16);
                s_DummyPages.SetData(new uint[] { 0xFFFFu, 0xFFFFFFFFu, 0u, 0u });
            }
            Shader.SetGlobalVector(s_Params, Vector4.zero);
            Shader.SetGlobalBuffer(s_Entries, s_DummyEntries);
            Shader.SetGlobalBuffer(s_Pages, s_DummyPages);
            Shader.SetGlobalTexture(s_Pool, Texture2D.blackTexture); // never read with the lookup off
        }

        static void Release()
        {
            s_DummyEntries?.Dispose();
            s_DummyPages?.Dispose();
            s_DummyEntries = s_DummyPages = null;
        }

#if UNITY_EDITOR
        // after every domain reload (the dummies of the old domain are gone)
        [UnityEditor.InitializeOnLoadMethod]
        static void OnEditorLoad()
        {
            BindDummies();
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Release;
        }
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void OnPlayerLoad() => BindDummies();
    }
}
