using System;
using System.IO;
using UnityEngine;

namespace UNanite
{
    /// <summary>Build statistics stored with the asset (shown in the inspector, used by tests/reports).</summary>
    [Serializable]
    public struct VgBuildReport
    {
        public int sourceTriangles;
        public int sourceVertices;
        public int weldedVertices;
        public int totalTriangles;
        public int totalVertices;
        public int clusterCount;
        public int groupCount;
        public int nodeCount;
        public int pageCount;
        public int levelCount;
        public int stuckGroups;
        public int threadsUsed;
        public long blobBytes;
        public long geometryBytes;
        public double msPrepare;
        public double msDag;
        public double msHierarchy;
        public double msEncode;
        public double msTotal;
        public string builderVersion;
        public string contentHash;

        public float BytesPerSourceTriangle => sourceTriangles > 0 ? (float)blobBytes / sourceTriangles : 0f;
        public float BytesPerStoredTriangle => totalTriangles > 0 ? (float)blobBytes / totalTriangles : 0f;
    }

    /// <summary>
    /// Built virtual geometry for one source mesh: the cluster DAG, hierarchy and encoded pages
    /// (layout: Documentation~/DataFormat.md). Produced by the .vgmesh importer.
    ///
    /// The blob is either complete (pages embedded in the asset) or only its resident prefix
    /// (header, tables, root pages) with the streamable pages in a separate page file, which the
    /// streaming system reads on demand (M7): an import-artifact side file in the editor,
    /// StreamingAssets/UNanite/&lt;content hash&gt;.vgpages in players.
    /// </summary>
    [PreferBinarySerialization]
    public sealed class VirtualGeometryMesh : ScriptableObject
    {
        public const string PageFileExtension = ".vgpages";

        [SerializeField, HideInInspector] byte[] m_Blob;
        [SerializeField] string m_PageFile; // external pages: side-file name in the import artifact ("" = embedded)
        [SerializeField] string m_SourceMeshName;
        [SerializeField] Bounds m_LocalBounds;
        [SerializeField] int m_MaterialCount;
        [SerializeField] VgBuildReport m_Report;
        [SerializeField] Vector2 m_UvFromPositionXZ; // M8 terrain tiles: uv0 = position.xz * this (zero = encoded UVs)

        [NonSerialized] VgMeshReader m_ResidentReader;
        [NonSerialized] VgMeshReader m_Reader;

        /// <summary>Editor hook resolving the page file of an imported asset (set by the editor assembly).</summary>
        public static Func<VirtualGeometryMesh, string> EditorPageFileResolver;

        /// <summary>The stored blob: complete, or the resident prefix when the pages are external.</summary>
        public byte[] Blob => m_Blob;
        public string SourceMeshName => m_SourceMeshName;
        public Bounds LocalBounds => m_LocalBounds;
        public int MaterialCount => m_MaterialCount;
        public VgBuildReport Report => m_Report;
        public bool IsValid => m_Blob != null && m_Blob.Length >= VgFormat.MeshHeaderSize;
        public bool HasExternalPages => !string.IsNullOrEmpty(m_PageFile);
        public string PageFileName => m_PageFile;

        /// <summary>Terrain tiles (M8) carry no UVs: uv0 = position.xz * this. Zero = the encoded UVs.</summary>
        public Vector2 UvFromPositionXZ
        {
            get => m_UvFromPositionXZ;
            set => m_UvFromPositionXZ = value;
        }

        /// <summary>Tables and root pages only (all the GPU-driven runtime needs up front).</summary>
        public VgMeshReader ResidentReader
        {
            get
            {
                if (m_ResidentReader == null && IsValid)
                    m_ResidentReader = new VgMeshReader(m_Blob);
                return m_ResidentReader;
            }
        }

        /// <summary>
        /// Every page, parsed lazily (editor tools, tests, CPU debug views). For external pages this
        /// reads the page file synchronously.
        /// </summary>
        public VgMeshReader Reader
        {
            get
            {
                if (m_Reader == null && IsValid)
                    m_Reader = HasExternalPages ? new VgMeshReader(LoadFullBlob()) : ResidentReader;
                return m_Reader;
            }
        }

        /// <summary>Absolute path of the page file, or null (embedded pages, or not found).</summary>
        public string ResolvePageFile()
        {
            if (!HasExternalPages)
                return null;
#if UNITY_EDITOR
            if (EditorPageFileResolver != null)
                return EditorPageFileResolver(this);
#endif
            string path = Path.Combine(Application.streamingAssetsPath, "UNanite", m_Report.contentHash + PageFileExtension);
            return File.Exists(path) ? path : null;
        }

        byte[] LoadFullBlob()
        {
            string path = ResolvePageFile();
            if (path == null)
                throw new FileNotFoundException($"UNanite: page file of '{name}' not found");
            var stream = File.ReadAllBytes(path);
            var full = new byte[m_Blob.Length + stream.Length];
            Buffer.BlockCopy(m_Blob, 0, full, 0, m_Blob.Length);
            Buffer.BlockCopy(stream, 0, full, m_Blob.Length, stream.Length);
            return full;
        }

        public void Initialize(byte[] blob, string sourceMeshName, int materialCount, VgBuildReport report)
        {
            m_Blob = blob;
            m_PageFile = null;
            m_SourceMeshName = sourceMeshName;
            m_MaterialCount = materialCount;
            m_Report = report;
            m_ResidentReader = null;
            m_Reader = null;
            m_LocalBounds = ResidentReader != null ? ResidentReader.LocalBounds : default;
        }

        /// <summary>
        /// Keeps only the resident prefix in the asset; returns the streamable part for the caller to
        /// store as the page file `pageFile` (null when every page is a root page).
        /// </summary>
        public byte[] SplitPages(string pageFile)
        {
            var h = ResidentReader.Header;
            if (h.streamDataOffset >= h.blobSize)
                return null;
            var stream = new byte[h.blobSize - h.streamDataOffset];
            Buffer.BlockCopy(m_Blob, (int)h.streamDataOffset, stream, 0, stream.Length);
            var prefix = new byte[h.streamDataOffset];
            Buffer.BlockCopy(m_Blob, 0, prefix, 0, prefix.Length);
            m_Blob = prefix;
            m_PageFile = pageFile;
            m_ResidentReader = null;
            m_Reader = null;
            return stream;
        }

        void OnValidate()
        {
            m_ResidentReader = null;
            m_Reader = null;
        }
    }
}
