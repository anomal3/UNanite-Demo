using System.Collections.Generic;

namespace UNanite
{
    /// <summary>First-fit allocator of integer ranges in [0, capacity) with coalescing of freed ranges.</summary>
    public sealed class VgRangeAllocator
    {
        readonly List<(int start, int count)> m_Free = new List<(int, int)>(); // sorted by start, never adjacent
        public int Capacity { get; private set; }
        public int Used { get; private set; }

        public VgRangeAllocator(int capacity) => Reset(capacity);

        public void Reset(int capacity)
        {
            Capacity = capacity;
            Used = 0;
            m_Free.Clear();
            if (capacity > 0)
                m_Free.Add((0, capacity));
        }

        /// <summary>Start of a free range of `count` (aligned to `alignment`), or -1.</summary>
        public int Allocate(int count, int alignment = 1)
        {
            if (count <= 0)
                return 0;
            for (int i = 0; i < m_Free.Count; ++i)
            {
                var (start, size) = m_Free[i];
                int aligned = (start + alignment - 1) / alignment * alignment;
                int pad = aligned - start;
                if (size < pad + count)
                    continue;
                m_Free.RemoveAt(i);
                int tail = size - pad - count;
                if (tail > 0)
                    m_Free.Insert(i, (aligned + count, tail));
                if (pad > 0)
                    m_Free.Insert(i, (start, pad));
                Used += count;
                return aligned;
            }
            return -1;
        }

        public void Free(int start, int count)
        {
            if (count <= 0)
                return;
            Used -= count;
            int i = 0;
            while (i < m_Free.Count && m_Free[i].start < start)
                i++;
            m_Free.Insert(i, (start, count));
            // merge with the next and the previous range
            if (i + 1 < m_Free.Count && m_Free[i].start + m_Free[i].count == m_Free[i + 1].start)
            {
                m_Free[i] = (m_Free[i].start, m_Free[i].count + m_Free[i + 1].count);
                m_Free.RemoveAt(i + 1);
            }
            if (i > 0 && m_Free[i - 1].start + m_Free[i - 1].count == m_Free[i].start)
            {
                m_Free[i - 1] = (m_Free[i - 1].start, m_Free[i - 1].count + m_Free[i].count);
                m_Free.RemoveAt(i);
            }
        }
    }
}
