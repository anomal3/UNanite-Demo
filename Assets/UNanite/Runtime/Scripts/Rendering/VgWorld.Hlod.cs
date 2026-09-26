using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UNanite
{
    // M8 instance-level HLOD (terrain quadtrees): switch records (world sphere, error) shared by a
    // node and its children. CullInstances draws an instance iff the record of its parent is not
    // coarse enough and its own record is: `¬coarsen(parent) ∧ coarsen(self)`. A child evaluates
    // exactly the record its parent evaluates, so the two decisions are complements (never a hole),
    // and monotonic records make exactly one node per quadtree path visible, per view.
    public sealed partial class VgWorld
    {
        readonly List<VgLodSwitchGpu> m_LodSwitches = new List<VgLodSwitchGpu>();
        readonly Stack<int> m_FreeLodSwitches = new Stack<int>();
        bool m_LodSwitchesDirty;
        GraphicsBuffer m_LodSwitchBuffer;

        static readonly int s_LodSwitchesId = Shader.PropertyToID("VG_LodSwitches");

        /// <summary>
        /// Adds a switch record; returns its id. M11 `morphStart` > 0: instances whose LOD this record
        /// ends (lodParent) morph toward the next LOD like Unity's LODFadeMode.SpeedTree, from the
        /// camera distance where their error projects to one unit at `morphStart` (unity_LODFade.x 0)
        /// to the switch (1): SpeedTree 8 graphs lerp to the next LOD's positions (uv2).
        /// M11 dithered crossfade between the two sides of the record (camera views; programmable raster
        /// only, VgVisRaster.hlsl; shadows switch at once): `fadeDuration` > 0 like
        /// LODGroup.animateCrossFading - after each switch both sides are drawn for that many seconds,
        /// dithered from the old to the new one (per camera); else `fadeStart` in (0, error) like
        /// LOD.fadeTransitionWidth - the coarse side is drawn from where `fadeStart` projects to one unit
        /// and both are dithered with the distance up to the switch.
        /// </summary>
        public int AddLodSwitch(Vector4 worldSphere, float error, float morphStart = 0f, float fadeStart = 0f, float fadeDuration = 0f)
        {
            var rec = new VgLodSwitchGpu { sphere = worldSphere, error = error, morphStart = morphStart, fadeStart = fadeStart, fadeDuration = fadeDuration };
            int id;
            if (m_FreeLodSwitches.Count > 0)
            {
                id = m_FreeLodSwitches.Pop();
                m_LodSwitches[id] = rec;
            }
            else
            {
                id = m_LodSwitches.Count;
                m_LodSwitches.Add(rec);
            }
            m_LodSwitchesDirty = true;
            return id;
        }

        public void SetLodSwitch(int id, Vector4 worldSphere, float error, float morphStart = 0f, float fadeStart = 0f, float fadeDuration = 0f)
        {
            if (id < 0 || id >= m_LodSwitches.Count)
                return;
            m_LodSwitches[id] = new VgLodSwitchGpu { sphere = worldSphere, error = error, morphStart = morphStart, fadeStart = fadeStart, fadeDuration = fadeDuration };
            m_LodSwitchesDirty = true;
        }

        public void RemoveLodSwitch(int id)
        {
            if (id < 0 || id >= m_LodSwitches.Count)
                return;
            m_LodSwitches[id] = default;
            m_FreeLodSwitches.Push(id);
            m_LodSwitchesDirty = true;
        }

        /// <summary>Links an instance to its HLOD records (-1 = none: leaf / root).</summary>
        public void SetInstanceLod(int handle, int selfSwitch, int parentSwitch)
        {
            if (handle < 0 || handle >= m_InstanceHighWater)
                return;
            m_Instances[handle].lodSelf = selfSwitch < 0 ? VgFormat.Invalid : (uint)selfSwitch;
            m_Instances[handle].lodParent = parentSwitch < 0 ? VgFormat.Invalid : (uint)parentSwitch;
            MarkDirty(handle);
        }

        void UploadLodSwitches()
        {
            m_LodFadeTime = Application.isPlaying ? Time.time : (float)(Time.realtimeSinceStartupAsDouble % 100000.0);
            if (m_LodSwitchBuffer != null && !m_LodSwitchesDirty)
                return;
            m_LodSwitchesDirty = false;
            Realloc(ref m_LodSwitchBuffer, GraphicsBuffer.Target.Structured, Mathf.Max(16, Mathf.NextPowerOfTwo(m_LodSwitches.Count)), VgLodSwitchGpu.Stride);
            if (m_LodSwitches.Count > 0)
                m_LodSwitchBuffer.SetData(m_LodSwitches);
            m_AnyLodFade = false;
            foreach (var s in m_LodSwitches)
                m_AnyLodFade |= s.fadeDuration > 0f;
            // records changed: every camera starts over without a fade (state 0 = not initialised)
            Realloc(ref m_LodFadeState, GraphicsBuffer.Target.Structured, m_LodSwitchBuffer.count * k_LodFadeSlots, 8);
            m_LodFadeState.SetData(new uint[m_LodFadeState.count * 2]);
        }

        void BindHlod(CommandBuffer cmd, int kernel)
        {
            cmd.SetComputeBufferParam(m_Cull, kernel, s_LodSwitchesId, m_LodSwitchBuffer);
            cmd.SetComputeBufferParam(m_Cull, kernel, s_LodFadeStateId, m_LodFadeState);
            cmd.SetComputeBufferParam(m_Cull, kernel, s_LodFadeStateInId, m_LodFadeState);
        }

        // M11 animated LOD crossfade: per camera slot and switch record (side the camera last switched
        // to | initialised << 1, time of that switch), VgCull.compute UpdateLodFade
        const int k_LodFadeSlots = 4;
        GraphicsBuffer m_LodFadeState;
        readonly Dictionary<Camera, int> m_LodFadeSlots = new Dictionary<Camera, int>();
        readonly List<Camera> m_LodFadeScratch = new List<Camera>();
        bool m_AnyLodFade;
        float m_LodFadeTime;
        static readonly int s_LodFadeStateId = Shader.PropertyToID("VG_LodFadeState");
        static readonly int s_LodFadeStateInId = Shader.PropertyToID("VG_LodFadeStateIn");
        static readonly int s_LodFadeParamsId = Shader.PropertyToID("VG_LodFadeParams");
        int k_UpdateLodFade;

        // the camera's state slot (-1: none, the camera switches LODs at once)
        int LodFadeSlot(Camera camera)
        {
            if (!m_AnyLodFade || camera == null)
                return -1;
            if (m_LodFadeSlots.TryGetValue(camera, out int slot))
                return slot;
            if (m_LodFadeSlots.Count >= k_LodFadeSlots)
            {
                // free the slots of destroyed cameras
                m_LodFadeScratch.Clear();
                foreach (var pair in m_LodFadeSlots)
                    if (pair.Key == null)
                        m_LodFadeScratch.Add(pair.Key);
                foreach (var c in m_LodFadeScratch)
                    m_LodFadeSlots.Remove(c);
            }
            for (slot = 0; slot < k_LodFadeSlots; ++slot)
                if (!m_LodFadeSlots.ContainsValue(slot))
                {
                    m_LodFadeSlots[camera] = slot;
                    return slot;
                }
            return -1;
        }

        /// <summary>Debug/tests: records `camera` is cross-fading right now and records it keeps a fade state for (synchronous readback).</summary>
        public (int fading, int tracked) DebugReadLodFade(Camera camera)
        {
            if (m_LodFadeState == null || camera == null || !m_LodFadeSlots.TryGetValue(camera, out int slot))
                return (0, 0);
            int capacity = m_LodFadeState.count / k_LodFadeSlots;
            var data = new uint[capacity * 2];
            m_LodFadeState.GetData(data, 0, slot * capacity * 2, capacity * 2);
            int fading = 0, tracked = 0;
            for (int i = 0; i < m_LodSwitches.Count && i < capacity; ++i)
            {
                var s = m_LodSwitches[i];
                if (s.fadeDuration <= 0f || (data[2 * i] & 2u) == 0)
                    continue;
                tracked++;
                if (m_LodFadeTime - System.BitConverter.Int32BitsToSingle((int)data[2 * i + 1]) < s.fadeDuration)
                    fading++;
            }
            return (fading, tracked);
        }

        // x: first state of the slot (-1: none), y: record count, z: time bits
        Vector4 LodFadeConstants(int slot) =>
            new Vector4(slot >= 0 ? slot * m_LodFadeState.count / k_LodFadeSlots : -1, m_LodSwitches.Count, m_LodFadeTime, 0);
    }
}
