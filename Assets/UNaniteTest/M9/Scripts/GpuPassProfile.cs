using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;

// Agent tool: GPU time of every profiler sampler over frames [skip, skip + frames), summed per
// sampler and written as "name<TAB>ms per frame" lines, largest first (compare two runs per pass).
public sealed class GpuPassProfile : MonoBehaviour
{
    public int skip = 30;
    public int frames = 60;
    public string outPath;
    public bool Done { get; private set; }

    readonly Dictionary<string, Recorder> m_Recorders = new Dictionary<string, Recorder>();
    readonly Dictionary<string, double> m_Sum = new Dictionary<string, double>();
    int m_Frame;

    void Start()
    {
        var names = new List<string>();
        Sampler.GetNames(names);
        foreach (var n in names.Distinct())
        {
            var r = Recorder.Get(n);
            if (r == null || !r.isValid)
                continue;
            r.enabled = true;
            m_Recorders[n] = r;
        }
    }

    void Update()
    {
        m_Frame++;
        if (Done || m_Frame <= skip)
            return;
        foreach (var kv in m_Recorders)
        {
            long ns = kv.Value.gpuElapsedNanoseconds;
            if (ns <= 0)
                continue;
            m_Sum.TryGetValue(kv.Key, out double s);
            m_Sum[kv.Key] = s + ns / 1e6;
        }
        if (m_Frame >= skip + frames)
        {
            Done = true;
            var sb = new StringBuilder();
            foreach (var kv in m_Sum.OrderByDescending(k => k.Value))
                sb.Append(kv.Key).Append('\t').Append((kv.Value / frames).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            File.WriteAllText(outPath, sb.ToString());
            foreach (var r in m_Recorders.Values)
                r.enabled = false;
        }
    }
}
