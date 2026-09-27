using System.Text;
using UnityEngine;

namespace UNanite.Samples.QuarryScene
{
    /// <summary>
    /// Sample overlay: frame times, virtual geometry statistics of the main camera and its shadows, streaming
    /// residency. V switches between virtual geometry and Unity's renderers (SampleVgToggle), P starts / stops
    /// the flythrough (SampleCameraPath), H hides the overlay.
    /// </summary>
    public sealed class SampleHud : MonoBehaviour
    {
        public SampleCameraPath flythrough;
        public bool visible = true;

        readonly FrameTiming[] m_Timings = new FrameTiming[1];
        readonly StringBuilder m_Text = new StringBuilder();
        float m_Cpu, m_Gpu, m_Fps, m_Accum;
        int m_Frames;
        GUIStyle m_Style;

        void Update()
        {
            if (SampleInput.Pressed('v'))
                SampleVgToggle.Set(!SampleVgToggle.Enabled);
            if (SampleInput.Pressed('h'))
                visible = !visible;
            if (SampleInput.Pressed('p') && flythrough != null)
                flythrough.playing = !flythrough.playing;

            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, m_Timings) > 0)
            {
                m_Cpu = Mathf.Lerp(m_Cpu, (float)m_Timings[0].cpuFrameTime, 0.1f);
                m_Gpu = Mathf.Lerp(m_Gpu, (float)m_Timings[0].gpuFrameTime, 0.1f);
            }
            m_Accum += Time.unscaledDeltaTime;
            if (++m_Frames >= 15)
            {
                m_Fps = m_Frames / m_Accum;
                m_Frames = 0;
                m_Accum = 0f;
            }
        }

        void OnGUI()
        {
            if (!visible)
                return;
            if (m_Style == null)
                m_Style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 14, richText = true, padding = new RectOffset(10, 10, 8, 8) };

            m_Text.Clear();
            m_Text.AppendLine(SampleVgToggle.Enabled ? "<b>UNanite virtual geometry</b>  (V: Unity renderers)" : "<b>Unity renderers</b>  (V: virtual geometry)");
            m_Text.AppendLine($"{m_Fps:F0} FPS   CPU {m_Cpu:F2} ms   GPU {(m_Gpu > 0f ? m_Gpu.ToString("F2") + " ms" : "n/a")}");
            var world = VgWorld.Instance;
            var cam = Camera.main;
            if (world != null && SampleVgToggle.Enabled)
            {
                long camTris = 0, shadowTris = 0;
                int clusters = 0, instances = 0, splits = 0;
                foreach (var v in world.LastFrameStats)
                {
                    if (cam != null && v.name == cam.name)
                    {
                        camTris += v.triangles + v.phase2Triangles;
                        clusters += v.visibleClusters + v.phase2Clusters;
                        instances += v.visibleInstances;
                    }
                    else if (v.name.StartsWith("Shadow"))
                    {
                        shadowTris += v.triangles;
                        splits++;
                    }
                }
                m_Text.AppendLine($"camera: {Millions(camTris)} triangles, {clusters:N0} clusters, {instances:N0} instances");
                m_Text.AppendLine($"shadows: {Millions(shadowTris)} triangles in {splits} views");
                var st = world.StreamingStats;
                if (st.enabled)
                    m_Text.AppendLine($"streaming: {st.residentBytes / 1048576.0:F0} of {st.streamableBytes / 1048576.0:F0} MB resident");
            }
            m_Text.Append("RMB look, WASD / QE move, Shift fast, wheel speed");
            if (flythrough != null)
                m_Text.Append(flythrough.playing ? ", P stop" : ", P flythrough");
            m_Text.Append(", H hide");
            var content = new GUIContent(m_Text.ToString());
            var size = m_Style.CalcSize(content);
            GUI.Box(new Rect(10, 10, size.x, size.y), content, m_Style);
        }

        static string Millions(long n) => n >= 1000000 ? $"{n / 1e6:F2} M" : $"{n / 1e3:F0} K";
    }

    /// <summary>Key presses through the Input System when the project uses it, else the legacy input manager.</summary>
    public static class SampleInput
    {
        public static bool Pressed(char key)
        {
#if UNANITE_SAMPLE_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null)
                return false;
            var k = key switch
            {
                'v' => kb.vKey,
                'h' => kb.hKey,
                'p' => kb.pKey,
                _ => null,
            };
            return k != null && k.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown((KeyCode)key);
#else
            return false;
#endif
        }
    }
}
