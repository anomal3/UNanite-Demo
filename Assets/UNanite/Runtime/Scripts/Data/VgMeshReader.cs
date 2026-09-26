using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace UNanite
{
    /// <summary>
    /// CPU-side view over a VG blob: parses the always-resident tables and decodes clusters.
    /// Used by the editor (inspection, debug views, tests) and by the streaming system to find
    /// page ranges. The GPU consumes the same page bytes directly (see VgFormat.hlsl).
    ///
    /// The blob may be complete or only its resident prefix (header, tables and root pages, up to
    /// `Header.streamDataOffset`) when the streamable pages live in a separate file: the tables are
    /// always available, cluster headers and decoding need every page (`HasAllPages`).
    /// </summary>
    public sealed unsafe class VgMeshReader
    {
        readonly byte[] m_Blob;
        VgClusterHeader[] m_Clusters;
        uint[] m_ClusterPageBlobOffset;

        public VgMeshHeader Header { get; }
        public VgGroup[] Groups { get; }
        public VgNode[] Nodes { get; }
        public VgPage[] Pages { get; }
        public VgLevel[] Levels { get; }
        public uint[] PageDependencies { get; }

        /// <summary>True when the blob holds every page (not only the resident prefix).</summary>
        public bool HasAllPages => m_Blob.Length == (int)Header.blobSize;

        /// <summary>
        /// All cluster headers in global cluster order (parsed on first use). For a resident prefix
        /// only the clusters of the root pages are available (the others stay zero; decoding them throws).
        /// </summary>
        public VgClusterHeader[] Clusters
        {
            get
            {
                if (m_Clusters == null)
                    ParseClusters();
                return m_Clusters;
            }
        }

        /// <summary>Blob offset of the page that holds each cluster (offsets in the header are page-relative).</summary>
        public uint[] ClusterPageBlobOffset
        {
            get
            {
                if (m_ClusterPageBlobOffset == null)
                    ParseClusters();
                return m_ClusterPageBlobOffset;
            }
        }

        public byte[] Blob => m_Blob;

        public VgMeshReader(byte[] blob)
        {
            if (blob == null || blob.Length < VgFormat.MeshHeaderSize)
                throw new ArgumentException("VG blob is missing or truncated");

            m_Blob = blob;
            Header = Read<VgMeshHeader>(0);
            var h = Header;
            if (h.magic != VgFormat.Magic)
                throw new InvalidOperationException("Not a VG blob (bad magic)");
            if (h.version != VgFormat.Version)
                throw new InvalidOperationException($"VG blob version {h.version} != runtime version {VgFormat.Version}; reimport the asset");
            if (h.blobSize != (uint)blob.Length && h.streamDataOffset != (uint)blob.Length)
                throw new InvalidOperationException("VG blob size mismatch");

            Groups = ReadArray<VgGroup>(h.groupTableOffset, (int)h.groupCount);
            Nodes = ReadArray<VgNode>(h.nodeTableOffset, (int)h.nodeCount);
            Pages = ReadArray<VgPage>(h.pageTableOffset, (int)h.pageCount);
            Levels = ReadArray<VgLevel>(h.levelTableOffset, (int)h.levelCount);
            PageDependencies = ReadArray<uint>(h.pageDepsOffset, (int)h.pageDepsCount);
        }

        void ParseClusters()
        {
            var h = Header;
            var clusters = new VgClusterHeader[h.clusterCount];
            var offsets = new uint[h.clusterCount];
            for (int p = 0; p < Pages.Length; ++p)
            {
                var page = Pages[p];
                uint pageStart = h.pageDataOffset + page.dataOffset;
                if (pageStart + page.dataSize > (uint)m_Blob.Length)
                    continue; // streamable page of a resident prefix
                var ph = Read<VgPageHeader>(pageStart);
                for (uint k = 0; k < page.clusterCount; ++k)
                {
                    uint ci = page.firstCluster + k;
                    clusters[ci] = Read<VgClusterHeader>(pageStart + ph.clusterHeaderOffset + k * (uint)VgFormat.ClusterHeaderSize);
                    offsets[ci] = pageStart;
                }
            }
            m_ClusterPageBlobOffset = offsets;
            m_Clusters = clusters;
        }

        /// <summary>Blob offset of page `page` (a streamable page sits at this minus `Header.streamDataOffset` in the page file).</summary>
        public uint PageBlobOffset(int page) => Header.pageDataOffset + Pages[page].dataOffset;

        /// <summary>Bytes of the streamable part (pages from `rootPageCount` on).</summary>
        public long StreamDataSize => (long)Header.blobSize - Header.streamDataOffset;

        public float PositionStep => Mathf.Pow(2f, -Header.positionPrecision);
        /// <summary>Quantisation step of x and z (M8 heightfields may use a coarser grid than y).</summary>
        public float PositionStepXZ => Mathf.Pow(2f, -Header.PrecisionXZ);
        public float UvStep => Mathf.Pow(2f, -(int)Header.uvPrecision);

        public Bounds LocalBounds
        {
            get
            {
                var h = Header;
                var min = new Vector3(h.aabbMinX, h.aabbMinY, h.aabbMinZ);
                var max = new Vector3(h.aabbMaxX, h.aabbMaxY, h.aabbMaxZ);
                return new Bounds((min + max) * 0.5f, max - min);
            }
        }

        public ArraySegment<byte> GetPageBytes(int page)
        {
            uint at = PageBlobOffset(page);
            if (at + Pages[page].dataSize > (uint)m_Blob.Length)
                throw new InvalidOperationException($"page {page} is not in this blob (streamable pages live in the page file)");
            return new ArraySegment<byte>(m_Blob, (int)at, (int)Pages[page].dataSize);
        }

        T Read<T>(uint offset) where T : unmanaged
        {
            if (offset + (uint)sizeof(T) > (uint)m_Blob.Length)
                throw new InvalidOperationException("VG blob read out of range");
            fixed (byte* b = m_Blob)
                return *(T*)(b + offset);
        }

        T[] ReadArray<T>(uint offset, int count) where T : unmanaged
        {
            var result = new T[count];
            if (count == 0)
                return result;
            long bytes = (long)count * sizeof(T);
            if (offset + bytes > m_Blob.Length)
                throw new InvalidOperationException("VG blob table out of range");
            fixed (byte* b = m_Blob)
            fixed (T* dst = result)
                Buffer.MemoryCopy(b + offset, dst, bytes, bytes);
            return result;
        }

        // Reads `bits` (<= 32) at a bit offset relative to `byteOffset`. Streams are 4-byte aligned
        // and the second word may belong to the next stream; it is masked out.
        public uint ReadBits(uint byteOffset, ulong bitOffset, int bits)
        {
            if (bits == 0)
                return 0;
            ulong wordByte = byteOffset + (bitOffset >> 5) * 4;
            int shift = (int)(bitOffset & 31);
            uint lo = LoadWord(wordByte);
            uint hi = shift + bits > 32 ? LoadWord(wordByte + 4) : 0u;
            ulong v = (((ulong)hi << 32) | lo) >> shift;
            return (uint)(v & ((1ul << bits) - 1));
        }

        uint LoadWord(ulong at)
        {
            if (at + 4 > (ulong)m_Blob.Length)
                return 0;
            fixed (byte* b = m_Blob)
                return *(uint*)(b + at);
        }

        public struct DecodedVertex
        {
            public Vector3 position;
            public Vector3 normal;
            public Vector4 tangent;
            public Vector2 uv0;
            public Vector2 uv1;
            public Color32 color;
            public Vector2 uv0zw, uv1zw; // M11 extra UVs
            public Vector4 uv2, uv3;
        }

        /// <summary>Decodes a cluster into mesh-space vertices and cluster-local triangle indices.</summary>
        public void DecodeCluster(int cluster, List<DecodedVertex> vertices, List<int> triangles)
        {
            vertices.Clear();
            triangles.Clear();

            var c = Clusters[cluster];
            var h = Header;
            uint page = RequirePage(cluster);
            uint vbase = page + c.VertexDataOffset;

            int bx = c.PosBitsX, by = c.PosBitsY, bz = c.PosBitsZ;
            int u0x = (int)(c.uvBits & 31), u0y = (int)((c.uvBits >> 5) & 31);
            int u1x = (int)((c.uvBits >> 10) & 31), u1y = (int)((c.uvBits >> 15) & 31);
            int nb = (int)h.normalBits;
            int tb = (int)h.tangentAngleBits;
            float posStep = PositionStep, posStepXZ = PositionStepXZ, uvStep = UvStep;
            // meshes with lightmap UVs keep the cluster's uv1 minimum in the 8 bytes before the vertex stream
            int uv1MinX = 0, uv1MinY = 0;
            if (h.HasUv1)
            {
                uv1MinX = (int)LoadWord(vbase - 8);
                uv1MinY = (int)LoadWord(vbase - 4);
            }

            int vcount = c.VertexCount;
            for (int v = 0; v < vcount; ++v)
            {
                ulong o = (ulong)v * (ulong)c.VertexStrideBits;
                var d = new DecodedVertex();
                int qx = c.posMinX + (int)ReadBits(vbase, o, bx); o += (ulong)bx;
                int qy = c.posMinY + (int)ReadBits(vbase, o, by); o += (ulong)by;
                int qz = c.posMinZ + (int)ReadBits(vbase, o, bz); o += (ulong)bz;
                d.position = new Vector3(qx * posStepXZ, qy * posStep, qz * posStepXZ);

                uint ox = ReadBits(vbase, o, nb); o += (ulong)nb;
                uint oy = ReadBits(vbase, o, nb); o += (ulong)nb;
                d.normal = OctDecode(ox, oy, nb);

                if (h.HasTangents)
                {
                    uint angle = ReadBits(vbase, o, tb); o += (ulong)tb;
                    uint sign = ReadBits(vbase, o, 1); o += 1;
                    Vector3 t = DecodeTangent(d.normal, OctLowerHemisphere(ox, oy, nb), angle, tb);
                    d.tangent = new Vector4(t.x, t.y, t.z, sign != 0 ? -1f : 1f);
                }
                else
                    d.tangent = new Vector4(1, 0, 0, 1);

                int uq = c.uv0MinX + (int)ReadBits(vbase, o, u0x); o += (ulong)u0x;
                int vq = c.uv0MinY + (int)ReadBits(vbase, o, u0y); o += (ulong)u0y;
                d.uv0 = new Vector2(uq * uvStep, vq * uvStep);

                if (h.HasUv1)
                {
                    int uq1 = uv1MinX + (int)ReadBits(vbase, o, u1x); o += (ulong)u1x;
                    int vq1 = uv1MinY + (int)ReadBits(vbase, o, u1y); o += (ulong)u1y;
                    d.uv1 = new Vector2(uq1 * uvStep, vq1 * uvStep);
                }

                if (h.HasColors)
                {
                    uint lo = ReadBits(vbase, o, 16); o += 16;
                    uint hi = ReadBits(vbase, o, 16); o += 16;
                    uint rgba = lo | (hi << 16);
                    d.color = new Color32((byte)rgba, (byte)(rgba >> 8), (byte)(rgba >> 16), (byte)(rgba >> 24));
                }
                else
                    d.color = new Color32(255, 255, 255, 255);

                if (h.HasExtraUv)
                {
                    var f = new float[12];
                    for (int k = 0; k < 12; ++k)
                    {
                        f[k] = Mathf.HalfToFloat((ushort)ReadBits(vbase, o, 16));
                        o += 16;
                    }
                    d.uv0zw = new Vector2(f[0], f[1]);
                    d.uv1zw = new Vector2(f[2], f[3]);
                    d.uv2 = new Vector4(f[4], f[5], f[6], f[7]);
                    d.uv3 = new Vector4(f[8], f[9], f[10], f[11]);
                }

                vertices.Add(d);
            }

            DecodeTriangles(c, page, triangles);
        }

        /// <summary>Decodes only quantised positions (bit-exact, for crack tests).</summary>
        public void DecodeClusterQuantized(int cluster, List<Vector3Int> positions, List<int> triangles)
        {
            positions.Clear();
            triangles.Clear();
            var c = Clusters[cluster];
            uint page = RequirePage(cluster);
            uint vbase = page + c.VertexDataOffset;
            int bx = c.PosBitsX, by = c.PosBitsY, bz = c.PosBitsZ;
            for (int v = 0; v < c.VertexCount; ++v)
            {
                ulong o = (ulong)v * (ulong)c.VertexStrideBits;
                positions.Add(new Vector3Int(
                    c.posMinX + (int)ReadBits(vbase, o, bx),
                    c.posMinY + (int)ReadBits(vbase, o + (ulong)bx, by),
                    c.posMinZ + (int)ReadBits(vbase, o + (ulong)(bx + by), bz)));
            }
            DecodeTriangles(c, page, triangles);
        }

        uint RequirePage(int cluster)
        {
            uint page = ClusterPageBlobOffset[cluster];
            if (page == 0)
                throw new InvalidOperationException("VG blob holds only the resident pages; use VirtualGeometryMesh.Reader, which loads the page file");
            return page;
        }

        // Index stream: a 7-bit anchor per 32 triangles in the first word, then per triangle
        // (m - anchor) | (a - m - 1) << baseBits | (b - m - 1) << (baseBits + deltaBits); the triangle
        // is (m, a, b) with its original winding.
        void DecodeTriangles(in VgClusterHeader c, uint page, List<int> triangles)
        {
            uint ibase = page + c.IndexDataOffset;
            uint anchors = LoadWord(ibase);
            int baseBits = c.IndexBaseBits, deltaBits = c.IndexDeltaBits;
            int recBits = baseBits + 2 * deltaBits;
            uint baseMask = (1u << baseBits) - 1, deltaMask = (1u << deltaBits) - 1;
            for (int t = 0; t < c.TriangleCount; ++t)
            {
                uint rec = ReadBits(ibase + 4, (ulong)t * (ulong)recBits, recBits);
                int m = (int)((anchors >> (7 * (t >> 5))) & 127) + (int)(rec & baseMask);
                triangles.Add(m);
                triangles.Add(m + 1 + (int)((rec >> baseBits) & deltaMask));
                triangles.Add(m + 1 + (int)((rec >> (baseBits + deltaBits)) & deltaMask));
            }
        }

        static Vector3 OctDecode(uint ox, uint oy, int bits)
        {
            float max = (1u << bits) - 1;
            float x = ox / max * 2f - 1f;
            float y = oy / max * 2f - 1f;
            float z = 1f - Mathf.Abs(x) - Mathf.Abs(y);
            if (z < 0f)
            {
                float tx = (1f - Mathf.Abs(y)) * (x >= 0f ? 1f : -1f);
                float ty = (1f - Mathf.Abs(x)) * (y >= 0f ? 1f : -1f);
                x = tx;
                y = ty;
            }
            return new Vector3(x, y, z).normalized;
        }

        // Hemisphere of an octahedral code decided on the integers (as in the builder and on the GPU).
        static bool OctLowerHemisphere(uint ox, uint oy, int bits)
        {
            int m = (1 << bits) - 1;
            return Mathf.Abs(2 * (int)ox - m) + Mathf.Abs(2 * (int)oy - m) > m;
        }

        // Tangent = angle around the normal in the orthonormal basis of Duff et al. 2017.
        static Vector3 DecodeTangent(Vector3 n, bool lower, uint angle, int bits)
        {
            float s = lower ? -1f : 1f;
            float a = -1f / (s + n.z);
            float b = n.x * n.y * a;
            var b1 = new Vector3(1f + s * n.x * n.x * a, s * b, -s * n.x);
            var b2 = new Vector3(b, s + n.y * n.y * a, -n.y);
            float phi = angle * (2f * Mathf.PI / (1 << bits));
            return (Mathf.Cos(phi) * b1 + Mathf.Sin(phi) * b2).normalized;
        }
    }
}
