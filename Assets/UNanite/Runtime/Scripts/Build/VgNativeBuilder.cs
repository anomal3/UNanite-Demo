using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace UNanite
{
    // Interop structs: mirror Native~/include/unanite_builder.h.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct UnvgBuildSettings
    {
        public uint maxTriangles;
        public uint maxVertices;
        public uint groupSize;
        public uint threadCount;
        public float simplifyRatio;
        public float simplifyThreshold;
        public float normalWeight;
        public float uvWeight;
        public float colorWeight;
        public int positionPrecision;
        public uint normalBits;
        public uint uvPrecision;
        public uint pageSize;
        public uint flags;
        public float sourceError; // M8: error of the source geometry (floor of every LOD error)
        public int positionPrecisionXZ; // M8: separate x/z quantisation (heightfield grids); AutoPrecision = positionPrecision

        public const uint FlagPermissive = 1u << 0;
        public const uint FlagSloppyFallback = 1u << 1;
        public const uint FlagKeepTangents = 1u << 2;
        public const uint FlagKeepUv1 = 1u << 3;
        public const uint FlagKeepColors = 1u << 4;
        public const uint FlagKeepExtraUv = 1u << 6; // M11: uv0.zw, uv1.zw, uv2, uv3 as halves
        public const uint FlagNoSimplify = 1u << 7;  // M11: source clusters only (discrete LODs per instance)
        public const uint FlagRegularize = 1u << 5;
        public const int AutoPrecision = int.MinValue;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    unsafe struct UnvgMeshInput
    {
        public uint vertexCount;
        public uint indexCount;
        public float* positions;
        public float* normals;
        public float* tangents;
        public float* uv0;
        public float* uv1;
        public float* colors;
        public uint* indices;
        public uint* triangleMaterial;
        public uint materialCount;
        public byte* vertexLock;
        public float* extraUv; // M11: 12 floats per vertex
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct UnvgBuildStats
    {
        public uint sourceTriangles;
        public uint sourceVertices;
        public uint weldedVertices;
        public uint totalTriangles;
        public uint totalVertices;
        public uint clusterCount;
        public uint groupCount;
        public uint nodeCount;
        public uint pageCount;
        public uint levelCount;
        public uint stuckGroups;
        public uint threadsUsed;
        public ulong blobBytes;
        public ulong geometryBytes;
        public double msPrepare;
        public double msDag;
        public double msHierarchy;
        public double msEncode;
        public double msTotal;
    }

    /// <summary>
    /// The native cluster-DAG builder (Native~, Windows x64), callable from the editor importers and
    /// at runtime (M8: terrain edits rebuild tiles on worker threads). The editor loads a shadow copy
    /// of the DLL (UNanite.Editor.NativeBuilder sets <see cref="EditorLibraryPath"/>); Windows players
    /// load the copy the build step places in &lt;Data&gt;/Plugins/x86_64. Builds are independent and
    /// may run concurrently.
    /// </summary>
    public static unsafe class VgNativeBuilder
    {
        public const int StatusDone = 2;
        public const int StatusFailed = 3;
        public const int StatusCancelled = 4;
        public const string DllName = "unanite_builder.dll";

        /// <summary>Editor hook: returns the path of the library to load (a shadow copy) or null with the reason in `error`.</summary>
        public static Func<(string path, string error)> EditorLibraryPath;

        static readonly object s_Lock = new object();
        static IntPtr s_Module;
        static string s_LoadError;

        static delegate* unmanaged[Cdecl]<uint> s_GetFormatVersion;
        static delegate* unmanaged[Cdecl]<UnvgBuildSettings*, void> s_DefaultSettings;
        static delegate* unmanaged[Cdecl]<IntPtr> s_Create;
        static delegate* unmanaged[Cdecl]<IntPtr, void> s_Destroy;
        static delegate* unmanaged[Cdecl]<IntPtr, UnvgMeshInput*, UnvgBuildSettings*, int> s_Build;
        static delegate* unmanaged[Cdecl]<IntPtr, float> s_GetProgress;
        static delegate* unmanaged[Cdecl]<IntPtr, void> s_Cancel;
        static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> s_GetError;
        static delegate* unmanaged[Cdecl]<IntPtr, UnvgBuildStats*, void> s_GetStats;
        static delegate* unmanaged[Cdecl]<IntPtr, ulong> s_GetBlobSize;
        static delegate* unmanaged[Cdecl]<IntPtr, void*, ulong, int> s_CopyBlob;

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibraryW(string path);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32", SetLastError = true)]
        static extern bool FreeLibrary(IntPtr module);

        public static bool IsAvailable => EnsureLoaded();
        public static string LoadError => s_LoadError;

        /// <summary>Loads the library if needed. Call on the main thread before building on workers.</summary>
        public static bool EnsureLoaded()
        {
            lock (s_Lock)
            {
                if (s_Module != IntPtr.Zero)
                    return true;
                if (Application.platform != RuntimePlatform.WindowsEditor && Application.platform != RuntimePlatform.WindowsPlayer)
                {
                    s_LoadError = "The native VG builder is currently only built for Windows x64.";
                    return false;
                }

                string path;
                if (Application.isEditor)
                {
                    if (EditorLibraryPath == null)
                    {
                        s_LoadError = "The editor did not register the native builder (UNanite.Editor not loaded).";
                        return false;
                    }
                    string error;
                    (path, error) = EditorLibraryPath();
                    if (path == null)
                    {
                        s_LoadError = error;
                        return false;
                    }
                }
                else
                {
                    path = Path.Combine(Application.dataPath, "Plugins", "x86_64", DllName);
                    if (!File.Exists(path))
                    {
                        s_LoadError = $"Native builder not found at '{path}' (copied by the UNanite build step for Windows x64 players).";
                        return false;
                    }
                }

                s_Module = LoadLibraryW(path);
                if (s_Module == IntPtr.Zero)
                {
                    s_LoadError = $"LoadLibrary failed for '{path}' (error {Marshal.GetLastWin32Error()})";
                    return false;
                }

                s_LoadError = null;
                s_GetFormatVersion = (delegate* unmanaged[Cdecl]<uint>)Resolve("unvgGetFormatVersion");
                s_DefaultSettings = (delegate* unmanaged[Cdecl]<UnvgBuildSettings*, void>)Resolve("unvgDefaultSettings");
                s_Create = (delegate* unmanaged[Cdecl]<IntPtr>)Resolve("unvgCreate");
                s_Destroy = (delegate* unmanaged[Cdecl]<IntPtr, void>)Resolve("unvgDestroy");
                s_Build = (delegate* unmanaged[Cdecl]<IntPtr, UnvgMeshInput*, UnvgBuildSettings*, int>)Resolve("unvgBuild");
                s_GetProgress = (delegate* unmanaged[Cdecl]<IntPtr, float>)Resolve("unvgGetProgress");
                s_Cancel = (delegate* unmanaged[Cdecl]<IntPtr, void>)Resolve("unvgCancel");
                s_GetError = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)Resolve("unvgGetError");
                s_GetStats = (delegate* unmanaged[Cdecl]<IntPtr, UnvgBuildStats*, void>)Resolve("unvgGetStats");
                s_GetBlobSize = (delegate* unmanaged[Cdecl]<IntPtr, ulong>)Resolve("unvgGetBlobSize");
                s_CopyBlob = (delegate* unmanaged[Cdecl]<IntPtr, void*, ulong, int>)Resolve("unvgCopyBlob");

                if (s_LoadError != null)
                {
                    UnloadLocked();
                    return false;
                }

                uint version = s_GetFormatVersion();
                if (version != VgFormat.Version)
                {
                    s_LoadError = $"Native builder produces format v{version}, runtime expects v{VgFormat.Version}. Rebuild the native builder.";
                    UnloadLocked();
                    return false;
                }
                return true;
            }
        }

        static IntPtr Resolve(string name)
        {
            IntPtr p = GetProcAddress(s_Module, name);
            if (p == IntPtr.Zero)
                s_LoadError = $"Native builder is missing export '{name}' (stale DLL?)";
            return p;
        }

        /// <summary>Frees the library (editor: before a domain reload, so the shadow copy can be replaced).</summary>
        public static void Unload()
        {
            lock (s_Lock)
                UnloadLocked();
        }

        static void UnloadLocked()
        {
            if (s_Module != IntPtr.Zero)
                FreeLibrary(s_Module);
            s_Module = IntPtr.Zero;
            s_Create = null;
            s_Build = null;
        }

        public static UnvgBuildSettings DefaultSettings()
        {
            if (!EnsureLoaded())
                throw new InvalidOperationException(s_LoadError);
            UnvgBuildSettings s;
            s_DefaultSettings(&s);
            return s;
        }

        public sealed class Result
        {
            public bool success;
            public bool cancelled;
            public string error;
            public byte[] blob;
            public UnvgBuildStats stats;
        }

        /// <summary>Input streams (managed arrays; pinned for the duration of the build).</summary>
        public sealed class MeshStreams
        {
            public float[] positions;  // xyz
            public float[] normals;    // xyz
            public float[] tangents;   // xyzw, optional
            public float[] uv0;        // xy, optional
            public float[] uv1;        // xy, optional
            public float[] colors;     // rgba, optional
            public uint[] indices;
            public uint[] triangleMaterial; // optional
            public int materialCount = 1;
            public byte[] vertexLock;  // optional: non-zero = locked in every simplification step (M8)
            public float[] extraUv;    // optional (M11): 12 floats per vertex, uv0.zw, uv1.zw, uv2.xyzw, uv3.xyzw
            public int VertexCount => positions.Length / 3;
        }

        /// <summary>
        /// Builds on a helper thread; `onProgress` is polled on the calling thread and may return false
        /// to cancel. Without `onProgress` the build runs on the calling thread (plus the builder's
        /// own worker threads), which is what worker-thread callers want.
        /// </summary>
        public static Result Build(MeshStreams mesh, UnvgBuildSettings settings, Func<float, bool> onProgress = null)
        {
            if (!EnsureLoaded())
                return new Result { error = s_LoadError };

            IntPtr builder = s_Create();
            if (builder == IntPtr.Zero)
                return new Result { error = "unvgCreate failed" };

            try
            {
                fixed (float* pos = mesh.positions)
                fixed (float* nrm = mesh.normals)
                fixed (float* tan = mesh.tangents)
                fixed (float* uv0 = mesh.uv0)
                fixed (float* uv1 = mesh.uv1)
                fixed (float* col = mesh.colors)
                fixed (uint* idx = mesh.indices)
                fixed (uint* mat = mesh.triangleMaterial)
                fixed (byte* lck = mesh.vertexLock)
                fixed (float* extra = mesh.extraUv)
                {
                    // stack storage outlives the helper thread: it is always joined before returning
                    UnvgMeshInput* input = stackalloc UnvgMeshInput[1];
                    UnvgBuildSettings* settingsCopy = stackalloc UnvgBuildSettings[1];
                    *settingsCopy = settings;
                    *input = new UnvgMeshInput
                    {
                        vertexCount = (uint)mesh.VertexCount,
                        indexCount = (uint)mesh.indices.Length,
                        positions = pos,
                        normals = nrm,
                        tangents = tan,
                        uv0 = uv0,
                        uv1 = uv1,
                        colors = col,
                        indices = idx,
                        triangleMaterial = mat,
                        materialCount = (uint)Mathf.Max(1, mesh.materialCount),
                        vertexLock = lck,
                        extraUv = extra,
                    };

                    int status;
                    if (onProgress == null)
                    {
                        status = s_Build(builder, input, settingsCopy);
                    }
                    else
                    {
                        int threadStatus = 0;
                        IntPtr inputPtr = (IntPtr)input, settingsPtr = (IntPtr)settingsCopy;
                        var thread = new Thread(() => { threadStatus = s_Build(builder, (UnvgMeshInput*)inputPtr, (UnvgBuildSettings*)settingsPtr); })
                        {
                            Name = "UNanite VG Build",
                            IsBackground = true,
                        };
                        thread.Start();

                        bool cancelRequested = false;
                        while (!thread.Join(30))
                        {
                            if (!cancelRequested && !onProgress(s_GetProgress(builder)))
                            {
                                s_Cancel(builder);
                                cancelRequested = true;
                            }
                        }
                        status = threadStatus;
                    }

                    var result = new Result();
                    UnvgBuildStats stats;
                    s_GetStats(builder, &stats);
                    result.stats = stats;

                    if (status == StatusDone)
                    {
                        ulong size = s_GetBlobSize(builder);
                        result.blob = new byte[size];
                        fixed (byte* dst = result.blob)
                            s_CopyBlob(builder, dst, size);
                        result.success = true;
                    }
                    else
                    {
                        result.cancelled = status == StatusCancelled;
                        result.error = Marshal.PtrToStringAnsi(s_GetError(builder));
                    }
                    return result;
                }
            }
            finally
            {
                s_Destroy(builder);
            }
        }

        public static VgBuildReport ToReport(in UnvgBuildStats s)
        {
            return new VgBuildReport
            {
                sourceTriangles = (int)s.sourceTriangles,
                sourceVertices = (int)s.sourceVertices,
                weldedVertices = (int)s.weldedVertices,
                totalTriangles = (int)s.totalTriangles,
                totalVertices = (int)s.totalVertices,
                clusterCount = (int)s.clusterCount,
                groupCount = (int)s.groupCount,
                nodeCount = (int)s.nodeCount,
                pageCount = (int)s.pageCount,
                levelCount = (int)s.levelCount,
                stuckGroups = (int)s.stuckGroups,
                threadsUsed = (int)s.threadsUsed,
                blobBytes = (long)s.blobBytes,
                geometryBytes = (long)s.geometryBytes,
                msPrepare = s.msPrepare,
                msDag = s.msDag,
                msHierarchy = s.msHierarchy,
                msEncode = s.msEncode,
                msTotal = s.msTotal,
                builderVersion = $"format v{VgFormat.Version}",
            };
        }
    }
}
