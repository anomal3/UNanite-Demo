using System;
using System.Collections.Generic;
using UnityEngine;

namespace UNanite
{
    // M9: object motion vectors. Every instance keeps the transform of the previous rendered frame
    // (VgInstanceGpu.prevLocalToWorld*). The first UpdateTransform of an instance in a frame saves its
    // current matrix as the previous one and flags it moving; a frame in which it did not move
    // resets previous = current. A "frame" is Time.frameCount in Play Mode (several context renders of
    // one frame - game and scene views - see the same motion) and every context render in Edit Mode
    // (render requests of tests and tools).
    // Bins holding a moving instance get BatchDrawCommandFlags.HasMotion on their resolve draws
    // (MotionVectorGenerationMode.Object range), so HDRP's object motion-vector pass runs the resolve
    // shader's MotionVectors pass over their tiles; a static scene issues no such draw.
    public sealed partial class VgWorld
    {
        /// <summary>
        /// Called at the start of every context render, before instance data is uploaded: transform
        /// trackers (VirtualGeometryRenderer, terrains) push their changes here so the frame renders
        /// the current transforms (no one-frame lag) with the right motion.
        /// </summary>
        public static event Action BeforeFrame;

        readonly HashSet<int> m_Touched = new HashSet<int>();    // moved since the current frame started
        readonly HashSet<int> m_PrevSaved = new HashSet<int>();  // previous matrix already saved this frame
        readonly HashSet<int> m_Moving = new HashSet<int>();     // flagged moving on the GPU
        readonly List<int> m_MotionScratch = new List<int>();
        bool[] m_BinMotion = new bool[0];
        int m_MotionFrame = int.MinValue;
        int m_EditRenderCounter;

        /// <summary>Instances with object motion in the current frame (stats, tests).</summary>
        public int MovingInstanceCount => m_Moving.Count;

        void MotionOnTransform(int handle, ref VgInstanceGpu inst)
        {
            if (m_PrevSaved.Add(handle))
            {
                inst.prevLocalToWorld0 = inst.localToWorld0;
                inst.prevLocalToWorld1 = inst.localToWorld1;
                inst.prevLocalToWorld2 = inst.localToWorld2;
            }
            inst.flags |= VgInstanceGpu.FlagMoving;
            m_Touched.Add(handle);
        }

        void MotionOnRemove(int handle)
        {
            m_Touched.Remove(handle);
            m_PrevSaved.Remove(handle);
            m_Moving.Remove(handle);
        }

        // Start of a context render (after the trackers ran): retire motion of instances that did not
        // move in a new frame, then flag the bins of moving instances.
        void BeginMotionFrame()
        {
            int frame = Application.isPlaying ? Time.frameCount : ++m_EditRenderCounter;
            if (frame != m_MotionFrame)
            {
                m_MotionFrame = frame;
                m_MotionScratch.Clear();
                foreach (int h in m_Moving)
                    if (!m_Touched.Contains(h))
                        m_MotionScratch.Add(h);
                foreach (int h in m_MotionScratch)
                {
                    m_Moving.Remove(h);
                    if (h < m_InstanceHighWater && (m_Instances[h].flags & VgInstanceGpu.FlagMoving) != 0)
                    {
                        m_Instances[h].ResetPrevious();
                        MarkDirty(h);
                    }
                }
                m_Moving.UnionWith(m_Touched);
                m_Touched.Clear();
                m_PrevSaved.Clear(); // moves after this point belong to the next frame
            }
            else
            {
                // another context render of the same frame: moves that arrived in between still count
                m_Moving.UnionWith(m_Touched);
                m_Touched.Clear();
            }

            if (m_BinMotion.Length < m_BinCapacity)
                m_BinMotion = new bool[m_BinCapacity];
            Array.Clear(m_BinMotion, 0, m_BinMotion.Length);
            foreach (int h in m_Moving)
            {
                ref var inst = ref m_Instances[h];
                int slots = m_InstanceMaterialCount[h];
                for (int s = 0; s < slots; ++s)
                {
                    int bin = (int)m_InstanceBins[(int)inst.materialBase + s];
                    if (bin >= 0 && bin < m_BinMotion.Length) // -1: an undrawn submesh
                        m_BinMotion[bin] = true;
                }
            }
        }

        bool BinHasMotion(int bin) => bin < m_BinMotion.Length && m_BinMotion[bin];
    }
}
