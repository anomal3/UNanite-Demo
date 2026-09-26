using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace UNanite
{
    // M11: SpeedTree 8 wind.
    //
    //   slots      one per distinct wind configuration (VgSpeedTreeWindParams, e.g. a terrain tree
    //              prototype's), ref-counted; its VgSpeedTreeWindSimulation advances once per frame
    //              under the scene's directional WindZones
    //   GPU        a slot's 16 current + 16 previous-frame float4 (SpeedTree8Wind.hlsl's _ST_Wind*) at
    //              the end of the pulled batch buffer, after the per-instance matrices
    //   batches    per slot a BRG batch over that buffer: the pulled batch's per-instance matrices plus
    //              DOTS_ST_WindParam0..15 / DOTS_ST_WindHistoryParam0..15 as per-batch values
    //   bins       keyed by slot too: a wind bin's draws (pulled passes, raster twin, resolve) use the
    //              slot's batch and its twins keep the material's wind quality; bins without a slot
    //              render SpeedTree materials without wind (their parameters would be zero)
    public sealed unsafe partial class VgWorld
    {
        const int k_WindFloat4s = 2 * VgSpeedTreeWindSimulation.ParamCount; // current + previous frame
        const int k_WindSlotBytes = k_WindFloat4s * 16;

        readonly List<VgSpeedTreeWindSimulation> m_WindSims = new List<VgSpeedTreeWindSimulation>();
        readonly List<int> m_WindHashes = new List<int>();
        readonly List<int> m_WindRefCount = new List<int>();
        readonly List<BatchID> m_WindBatchIDs = new List<BatchID>();
        readonly List<int> m_BinWind = new List<int>(); // per bin: wind slot or -1
        int m_WindCapacity;      // slots the pulled batch buffer has room for
        int m_WindBase;          // byte offset of slot 0 in the pulled batch buffer
        float[] m_WindUpload;
        int m_WindFrame = -1;
        WindZone[] m_WindZones;
        int m_WindZonesFrame = -1000;

        static readonly int[] s_WindParamIds = WindPropertyIds("DOTS_ST_WindParam");
        static readonly int[] s_WindHistoryIds = WindPropertyIds("DOTS_ST_WindHistoryParam");

        static int[] WindPropertyIds(string prefix)
        {
            var ids = new int[VgSpeedTreeWindSimulation.ParamCount];
            for (int i = 0; i < ids.Length; ++i)
                ids[i] = Shader.PropertyToID(prefix + i);
            return ids;
        }

        /// <summary>
        /// M11: registers a SpeedTree 8 wind configuration and returns its wind slot for
        /// <see cref="AddInstance(VirtualGeometryMesh, IReadOnlyList{Material}, in Matrix4x4, bool, int, Vector4, int)"/>
        /// (equal configurations share a slot), or -1 (no wind: invalid parameters, settings off or no
        /// pulled materials). Release with <see cref="ReleaseSpeedTreeWind"/>.
        /// </summary>
        public int AcquireSpeedTreeWind(VgSpeedTreeWindParams p)
        {
            if (p == null || !p.IsValid || !m_PulledSupported || !m_Settings.speedTreeWind)
                return -1;
            int hash = p.ComputeHash();
            int slot = -1;
            for (int s = 0; s < m_WindSims.Count && slot < 0; ++s)
                if (m_WindRefCount[s] > 0 && m_WindHashes[s] == hash)
                    slot = s;
            if (slot < 0)
            {
                slot = m_WindRefCount.IndexOf(0);
                var sim = new VgSpeedTreeWindSimulation(p, (uint)hash);
                if (slot < 0)
                {
                    slot = m_WindSims.Count;
                    m_WindSims.Add(sim);
                    m_WindHashes.Add(hash);
                    m_WindRefCount.Add(0);
                }
                else
                {
                    m_WindSims[slot] = sim;
                    m_WindHashes[slot] = hash;
                }
                m_WindFrame = -1; // write the new slot this frame
            }
            m_WindRefCount[slot]++;
            return slot;
        }

        public void ReleaseSpeedTreeWind(int slot)
        {
            if (slot >= 0 && slot < m_WindRefCount.Count && m_WindRefCount[slot] > 0)
                m_WindRefCount[slot]--;
        }

        /// <summary>The batch of a pulled bin's draws: its wind slot's, else the per-instance batch.</summary>
        BatchID PulledBatchFor(int bin)
        {
            int slot = bin < m_BinWind.Count ? m_BinWind[bin] : -1;
            return slot >= 0 && slot < m_WindBatchIDs.Count ? m_WindBatchIDs[slot] : m_PulledBatchID;
        }

        bool BinHasWind(int bin) => bin < m_BinWind.Count && m_BinWind[bin] >= 0;

        static bool IsSpeedTreeMaterial(Material m) => m != null && m.HasProperty("_WindQuality");

        // the pulled batch buffer grows by slots in steps (every batch is re-created with it)
        int WindCapacityNeeded() => m_WindSims.Count == 0 ? 0 : Mathf.Max(4, Mathf.NextPowerOfTwo(m_WindSims.Count));

        // batches over the pulled batch buffer: per-instance matrices (as the pulled batch) + the slot's wind
        void CreateWindBatches(int zeroBytes, int matrixBytes, int capacity)
        {
            m_WindBatchIDs.Clear();
            if (m_WindCapacity == 0)
                return;
            string[] names = { "unity_ObjectToWorld", "unity_WorldToObject", "unity_MatrixPreviousM", "unity_MatrixPreviousMI" };
            var metadata = new NativeArray<MetadataValue>(4 + k_WindFloat4s, Allocator.Temp);
            for (int m = 0; m < 4; ++m)
                metadata[m] = new MetadataValue { NameID = Shader.PropertyToID(names[m]), Value = 0x80000000u | (uint)(zeroBytes + m * matrixBytes * capacity) };
            for (int s = 0; s < m_WindCapacity; ++s)
            {
                int slotBase = m_WindBase + s * k_WindSlotBytes;
                for (int i = 0; i < VgSpeedTreeWindSimulation.ParamCount; ++i)
                {
                    // no per-instance bit: every instance of the batch reads the slot's value
                    metadata[4 + i] = new MetadataValue { NameID = s_WindParamIds[i], Value = (uint)(slotBase + i * 16) };
                    metadata[4 + VgSpeedTreeWindSimulation.ParamCount + i] = new MetadataValue
                    {
                        NameID = s_WindHistoryIds[i],
                        Value = (uint)(slotBase + (VgSpeedTreeWindSimulation.ParamCount + i) * 16),
                    };
                }
                m_WindBatchIDs.Add(m_BRG.AddBatch(metadata, m_PulledBatchData.bufferHandle));
            }
            metadata.Dispose();
            m_WindFrame = -1; // new buffer: write every slot
        }

        void RemoveWindBatches()
        {
            foreach (var id in m_WindBatchIDs)
                m_BRG.RemoveBatch(id);
            m_WindBatchIDs.Clear();
        }

        // the scene's directional WindZones (sum of their forces; spherical zones are local, ignored)
        void DesiredWind(float time, out Vector3 direction, out float strength)
        {
            if (m_WindZones == null || Time.frameCount - m_WindZonesFrame > 30 || Time.frameCount < m_WindZonesFrame)
            {
                m_WindZones = Object.FindObjectsByType<WindZone>();
                m_WindZonesFrame = Time.frameCount;
            }
            var force = Vector3.zero;
            foreach (var z in m_WindZones)
            {
                if (z == null || !z.gameObject.activeInHierarchy || z.mode != WindZoneMode.Directional)
                    continue;
                // Unity's wind pulse: the main force breathes by windPulseMagnitude at windPulseFrequency
                float phase = time * Mathf.PI * z.windPulseFrequency;
                float pulse = Mathf.Sin(phase) * Mathf.Cos(phase * 0.375f) * Mathf.Cos(phase * 0.05f);
                float main = z.windMain * (1f + z.windPulseMagnitude * pulse * 0.5f);
                force += z.transform.forward * Mathf.Max(0f, main);
            }
            strength = Mathf.Clamp01(force.magnitude);
            direction = strength > 1e-5f ? force.normalized : Vector3.forward;
        }

        // once per frame, before the frame's draws: advance every live slot and upload it
        void UpdateSpeedTreeWind()
        {
            if (m_WindCapacity == 0 || m_PulledBatchData == null || m_WindFrame == Time.frameCount)
                return;
            bool first = m_WindFrame < 0;
            m_WindFrame = Time.frameCount;
            float time = Application.isPlaying ? Time.time : Time.realtimeSinceStartup;
            DesiredWind(time, out var direction, out float strength);
            int floats = m_WindCapacity * k_WindFloat4s * 4;
            if (m_WindUpload == null || m_WindUpload.Length != floats)
                m_WindUpload = new float[floats];
            int used = 0;
            for (int s = 0; s < m_WindSims.Count && s < m_WindCapacity; ++s)
            {
                if (m_WindRefCount[s] <= 0 && !first)
                    continue;
                var sim = m_WindSims[s];
                sim.Advance(time, direction, strength);
                int o = s * k_WindFloat4s * 4;
                for (int i = 0; i < VgSpeedTreeWindSimulation.ParamCount; ++i)
                {
                    var c = sim.Current[i];
                    var h = sim.Previous[i];
                    int a = o + i * 4, b = o + (VgSpeedTreeWindSimulation.ParamCount + i) * 4;
                    m_WindUpload[a] = c.x; m_WindUpload[a + 1] = c.y; m_WindUpload[a + 2] = c.z; m_WindUpload[a + 3] = c.w;
                    m_WindUpload[b] = h.x; m_WindUpload[b + 1] = h.y; m_WindUpload[b + 2] = h.z; m_WindUpload[b + 3] = h.w;
                }
                used = s + 1;
            }
            if (used > 0)
                m_PulledBatchData.SetData(m_WindUpload, 0, m_WindBase / 4, used * k_WindFloat4s * 4);
        }

        /// <summary>M11 diagnostics: the current wind parameters of a slot (null if none).</summary>
        public Vector4[] GetSpeedTreeWind(int slot) =>
            slot >= 0 && slot < m_WindSims.Count ? (Vector4[])m_WindSims[slot].Current.Clone() : null;
    }
}
