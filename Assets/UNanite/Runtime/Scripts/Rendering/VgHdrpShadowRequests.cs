#if UNANITE_HDRP
using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace UNanite
{
    /// <summary>
    /// M13: reads HDRP's shadow requests of the frame (internal HDShadowRequestDatabase storage) - the
    /// exact render matrices, atlas resolution, slope bias and z-clip of every shadow split - so cached
    /// virtual-shadow-map pages rasterise exactly what HDRP's own shadow pass would. Field offsets are
    /// resolved once by reflection; reading is plain memory access. <see cref="Available"/> is false
    /// when the layout is not found (another HDRP version): virtual shadow maps then stay off.
    /// </summary>
    static unsafe class VgHdrpShadowRequests
    {
        public struct Request
        {
            public Matrix4x4 view;            // camera-relative when HDRP renders camera-relative
            public Matrix4x4 deviceProjectionYFlip;
            public Matrix4x4 projection;
            public Vector4 cullingSphere;
            public Vector2 viewportSize;
            public int splitIndex;
            public float slopeBias;
            public bool zClip, valid;
        }

        static bool s_Initialized, s_Available;
        static object s_Database;
        static FieldInfo s_Storage;
        static MethodInfo s_GetUnsafeList;
        static int s_Stride, s_ListPtr, s_ListLength;
        static int o_View, o_DevProjFlip, o_Projection, o_Sphere, o_Viewport, o_SplitIndex, o_SlopeBias, o_Flags;

        public static bool Available
        {
            get
            {
                Initialize();
                return s_Available;
            }
        }

        static void Initialize()
        {
            if (s_Initialized)
                return;
            s_Initialized = true;
            try
            {
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                var asm = typeof(HDRenderPipeline).Assembly;
                var dbType = asm.GetType("UnityEngine.Rendering.HighDefinition.HDShadowRequestDatabase");
                var reqType = asm.GetType("UnityEngine.Rendering.HighDefinition.HDShadowRequest");
                var splitType = asm.GetType("UnityEngine.Rendering.HighDefinition.HDShadowCullingSplit");
                if (dbType == null || reqType == null || splitType == null)
                    return;
                s_Database = dbType.GetProperty("instance", any)?.GetValue(null);
                s_Storage = dbType.GetField("m_HDShadowRequestStorage", any);
                if (s_Database == null || s_Storage == null)
                    return;
                s_GetUnsafeList = s_Storage.FieldType.GetMethod("GetUnsafeList", any, null, Type.EmptyTypes, null);
                var unsafeList = typeof(UnsafeList<>).MakeGenericType(reqType);
                var ptrField = unsafeList.GetField("Ptr", any);
                var lengthField = unsafeList.GetField("m_length", any);
                if (s_GetUnsafeList == null || ptrField == null || lengthField == null)
                    return;
                s_ListPtr = UnsafeUtility.GetFieldOffset(ptrField);
                s_ListLength = UnsafeUtility.GetFieldOffset(lengthField);
                s_Stride = UnsafeUtility.SizeOf(reqType);

                int split = Offset(reqType, "cullingSplit");
                o_View = split + Offset(splitType, "view");
                o_DevProjFlip = split + Offset(splitType, "deviceProjectionYFlip");
                o_Projection = split + Offset(splitType, "projection");
                o_Sphere = split + Offset(splitType, "cullingSphere");
                o_Viewport = split + Offset(splitType, "viewportSize");
                o_SplitIndex = split + Offset(splitType, "splitIndex");
                o_SlopeBias = Offset(reqType, "slopeBias");
                o_Flags = Offset(reqType, "flags"); // BitArray8: bit 4 valid, bit 5 zClip
                s_Available = split >= 0 && o_View >= split && o_SlopeBias >= 0 && o_Flags >= 0 && o_SplitIndex >= split;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"UNanite: HDRP shadow requests not readable ({e.Message}); virtual shadow maps are off.");
                s_Available = false;
            }
        }

        static int Offset(Type t, string field)
        {
            var f = t.GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return f == null ? int.MinValue / 2 : UnsafeUtility.GetFieldOffset(f);
        }

        /// <summary>Every request slot of HDRP's database (stale ones included: callers match them to their splits).</summary>
        public static int Read(List<Request> into)
        {
            into.Clear();
            if (!Available)
                return 0;
            var list = s_Storage.GetValue(s_Database);
            var boxed = s_GetUnsafeList.Invoke(list, null);
            var raw = (byte*)Pointer.Unbox(boxed);
            if (raw == null)
                return 0;
            byte* data = *(byte**)(raw + s_ListPtr);
            int length = *(int*)(raw + s_ListLength);
            if (data == null)
                return 0;
            for (int i = 0; i < length; ++i)
            {
                byte* r = data + (long)i * s_Stride;
                byte flags = *(r + o_Flags);
                into.Add(new Request
                {
                    view = *(Matrix4x4*)(r + o_View),
                    deviceProjectionYFlip = *(Matrix4x4*)(r + o_DevProjFlip),
                    projection = *(Matrix4x4*)(r + o_Projection),
                    cullingSphere = *(Vector4*)(r + o_Sphere),
                    viewportSize = *(Vector2*)(r + o_Viewport),
                    splitIndex = *(int*)(r + o_SplitIndex),
                    slopeBias = *(float*)(r + o_SlopeBias),
                    valid = (flags & (1 << 4)) != 0,
                    zClip = (flags & (1 << 5)) != 0,
                });
            }
            return into.Count;
        }
    }
}
#endif
