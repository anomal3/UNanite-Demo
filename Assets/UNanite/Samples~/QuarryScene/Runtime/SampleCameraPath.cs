using UnityEngine;

namespace UNanite.Samples.QuarryScene
{
    /// <summary>
    /// Flythrough along the child transforms (in order): Catmull-Rom positions at a constant speed, rotations
    /// eased between the waypoints. Drives the main camera while `playing`; drivers (videos, measurements)
    /// can set `time` and call Apply for exact, frame-rate independent flights.
    /// </summary>
    [ExecuteAlways]
    public sealed class SampleCameraPath : MonoBehaviour
    {
        public Camera target;
        public float speed = 6f;           // metres per second
        public bool playing;
        public bool loop = true;
        public double time;                // seconds along the path

        float[] m_Start;                   // path time at each waypoint
        int m_Count;

        public float Duration
        {
            get
            {
                Rebuild();
                return m_Count > 1 ? m_Start[m_Count - 1] : 0f;
            }
        }

        void Rebuild()
        {
            m_Count = transform.childCount;
            if (m_Start == null || m_Start.Length != m_Count)
                m_Start = new float[m_Count];
            float t = 0f;
            for (int i = 0; i < m_Count; ++i)
            {
                if (i > 0)
                    t += Vector3.Distance(transform.GetChild(i - 1).position, transform.GetChild(i).position) / Mathf.Max(0.01f, speed);
                m_Start[i] = t;
            }
        }

        public void Evaluate(double at, out Vector3 position, out Quaternion rotation)
        {
            Rebuild();
            if (m_Count == 0)
            {
                position = transform.position;
                rotation = transform.rotation;
                return;
            }
            float duration = m_Start[m_Count - 1];
            float t = (float)(loop && duration > 0f ? at % duration : System.Math.Min(at, duration));
            int i = 0;
            while (i < m_Count - 2 && m_Start[i + 1] <= t)
                i++;
            float span = m_Count > 1 ? m_Start[i + 1] - m_Start[i] : 1f;
            float u = span > 0f ? Mathf.Clamp01((t - m_Start[i]) / span) : 0f;
            Transform P(int k) => transform.GetChild(Mathf.Clamp(k, 0, m_Count - 1));
            var p0 = P(i - 1).position;
            var p1 = P(i).position;
            var p2 = P(i + 1).position;
            var p3 = P(i + 2).position;
            position = 0.5f * (2f * p1 + (-p0 + p2) * u + (2f * p0 - 5f * p1 + 4f * p2 - p3) * (u * u) + (-p0 + 3f * p1 - 3f * p2 + p3) * (u * u * u));
            rotation = Quaternion.Slerp(P(i).rotation, P(i + 1).rotation, u * u * (3f - 2f * u));
        }

        public void Apply()
        {
            var cam = target != null ? target : Camera.main;
            if (cam == null)
                return;
            Evaluate(time, out var p, out var r);
            cam.transform.SetPositionAndRotation(p, r);
        }

        bool m_Driving;

        void LateUpdate()
        {
            if (!Application.isPlaying)
                return;
            if (playing != m_Driving)
            {
                // the free-fly camera rests while the path drives (and picks up the pose afterwards)
                var cam = target != null ? target : Camera.main;
                var fly = cam != null ? cam.GetComponent<SampleFlyCamera>() : null;
                if (fly != null)
                    fly.enabled = !playing;
                m_Driving = playing;
            }
            if (!playing)
                return;
            time += Time.deltaTime;
            Apply();
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            float d = Duration;
            if (d <= 0f)
                return;
            Evaluate(0, out var prev, out _);
            for (float t = 0.25f; t <= d; t += 0.25f)
            {
                Evaluate(t, out var p, out _);
                Gizmos.DrawLine(prev, p);
                prev = p;
            }
        }
    }
}
