using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace UNanite
{
    /// <summary>Instance transform fields computed off the main thread (M9 transform tracker).</summary>
    internal struct VgTransformData
    {
        public float4 l0, l1, l2;  // localToWorld rows
        public float4 w0, w1, w2;  // worldToLocal rows
        public float4 sphere;      // world bounding sphere
        public float maxScale;
        public int mirrored;
        public int changed;
    }

    /// <summary>
    /// M9: transform tracking of dynamic virtual geometry renderers. A Burst job over a
    /// TransformAccessArray reads every tracked transform in parallel, compares it with the matrix of the
    /// previous frame and, for the changed ones, computes the instance's transform fields (inverse,
    /// scale, bounding sphere); the main thread only copies the changed results into the world.
    /// </summary>
    internal static class VgTransformTracker
    {
        static readonly List<VirtualGeometryRenderer> s_Renderers = new List<VirtualGeometryRenderer>();
        static TransformAccessArray s_Transforms;
        static NativeList<float4x4> s_Last;
        static NativeList<float4> s_Bounds;     // mesh-space centre, radius
        static NativeList<VgTransformData> s_Out;
        static bool s_Dirty, s_Hooked;
        static VgWorld s_World;

        public static int Count => s_Renderers.Count;

        public static void Add(VirtualGeometryRenderer r)
        {
            if (s_Renderers.Contains(r))
                return;
            s_Renderers.Add(r);
            s_Dirty = true;
            if (!s_Hooked)
            {
                s_Hooked = true;
                VgWorld.BeforeFrame += Track;
#if UNITY_EDITOR
                UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Dispose;
#endif
                Application.quitting += Dispose;
            }
        }

        public static void Remove(VirtualGeometryRenderer r)
        {
            if (s_Renderers.Remove(r))
                s_Dirty = true;
        }

        static void Dispose()
        {
            if (s_Transforms.isCreated) s_Transforms.Dispose();
            if (s_Last.IsCreated) s_Last.Dispose();
            if (s_Bounds.IsCreated) s_Bounds.Dispose();
            if (s_Out.IsCreated) s_Out.Dispose();
            s_Dirty = true;
        }

        static void Rebuild()
        {
            s_Renderers.RemoveAll(r => r == null);
            Dispose();
            int n = s_Renderers.Count;
            s_Transforms = new TransformAccessArray(n);
            s_Last = new NativeList<float4x4>(n, Allocator.Persistent);
            s_Bounds = new NativeList<float4>(n, Allocator.Persistent);
            s_Out = new NativeList<VgTransformData>(n, Allocator.Persistent);
            foreach (var r in s_Renderers)
            {
                s_Transforms.Add(r.transform);
                s_Last.Add(r.transform.localToWorldMatrix); // registered with this matrix
                var b = r.Mesh != null ? r.Mesh.LocalBounds : default;
                s_Bounds.Add(new float4(b.center, b.extents.magnitude));
            }
            s_Out.Resize(n, NativeArrayOptions.ClearMemory);
            s_Dirty = false;
        }

        [BurstCompile]
        struct TrackJob : IJobParallelForTransform
        {
            public NativeArray<float4x4> last;
            [ReadOnly] public NativeArray<float4> bounds;
            [WriteOnly] public NativeArray<VgTransformData> output;

            public void Execute(int i, TransformAccess t)
            {
                float4x4 m = t.localToWorldMatrix;
                var d = new VgTransformData();
                if (!t.isValid || math.all(m.c0 == last[i].c0) && math.all(m.c1 == last[i].c1) && math.all(m.c2 == last[i].c2) && math.all(m.c3 == last[i].c3))
                {
                    output[i] = d;
                    return;
                }
                last[i] = m;
                float4x4 inv = math.inverse(m);
                var tm = math.transpose(m);
                var ti = math.transpose(inv);
                d.l0 = tm.c0; d.l1 = tm.c1; d.l2 = tm.c2;
                d.w0 = ti.c0; d.w1 = ti.c1; d.w2 = ti.c2;
                float3 scale = new float3(math.length(m.c0.xyz), math.length(m.c1.xyz), math.length(m.c2.xyz));
                d.maxScale = math.cmax(scale);
                float3 c = math.transform(m, bounds[i].xyz);
                d.sphere = new float4(c, bounds[i].w * d.maxScale);
                d.mirrored = math.determinant(new float3x3(m.c0.xyz, m.c1.xyz, m.c2.xyz)) < 0f ? 1 : 0;
                d.changed = 1;
                output[i] = d;
            }
        }

        static void Track()
        {
            var world = VgWorld.Instance;
            if (world != s_World)
            {
                // the world was recreated (e.g. after all instances were removed): re-add everything
                s_World = world;
                foreach (var r in s_Renderers.ToArray())
                    if (r != null && r.isActiveAndEnabled && !r.IsRegistered)
                        r.ReregisterTracked();
                s_Dirty = true;
            }
            if (s_Dirty)
                Rebuild();
            int n = s_Renderers.Count;
            if (n == 0 || world == null)
                return;
            new TrackJob { last = s_Last.AsArray(), bounds = s_Bounds.AsArray(), output = s_Out.AsArray() }
                .ScheduleReadOnly(s_Transforms, 64).Complete();
            var output = s_Out.AsArray();
            for (int i = 0; i < n; ++i)
            {
                if (output[i].changed == 0)
                    continue;
                var r = s_Renderers[i];
                if (r != null && r.IsRegistered)
                    world.ApplyTransform(r.Handle, output[i]);
            }
        }
    }
}
