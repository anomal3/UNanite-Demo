using System;
using UnityEngine;

namespace UNanite
{
    /// <summary>
    /// M11: the SpeedTree 8 wind configuration of a tree, copied from its SpeedTreeWindAsset
    /// (SpeedTreeWindConfig8, readable in the editor only): oscillation frequencies and per-effect
    /// curves over the wind strength (<see cref="CurvePoints"/> points for strength 0..1), gusting and
    /// branch / leaf / frond settings. <see cref="VgSpeedTreeWindSimulation"/> turns it into the
    /// shader parameters of SpeedTree8Wind.hlsl.
    /// </summary>
    [Serializable]
    public sealed class VgSpeedTreeWindParams
    {
        public const int CurvePoints = 10;
        /// <summary>Oscillators: global, branch 1, branch 2, leaf 1 ripple / tumble / twitch, leaf 2 ripple / tumble / twitch, frond ripple.</summary>
        public const int Oscillators = 10;

        [Serializable]
        public sealed class Branch
        {
            public float[] distance = new float[0];
            public float[] directionAdherence = new float[0];
            public float[] whip = new float[0];
            public float turbulence, twitch, twitchFreqScale;
        }

        [Serializable]
        public sealed class Leaf
        {
            public float[] rippleDistance = new float[0];
            public float[] tumbleFlip = new float[0];
            public float[] tumbleTwist = new float[0];
            public float[] tumbleDirectionAdherence = new float[0];
            public float[] twitchThrow = new float[0];
            public float twitchSharpness;
        }

        // arrays are empty until read from a wind asset: prototypes without wind serialise small
        /// <summary>8 = SpeedTree 8 wind; 0 = none (Unity's serializer never stores null for this class).</summary>
        public int version;
        public float strengthResponse, directionResponse;
        public float gustFrequency, gustStrengthMin, gustStrengthMax, gustDurationMin, gustDurationMax, gustRiseScalar, gustFallScalar;
        public float anchorOffset, anchorDistanceScale;
        public Vector3 branchAnchor;
        public float[] oscillation = new float[0]; // frequencies (Hz), [oscillator * CurvePoints + point]
        public float globalHeight, globalHeightExponent;
        public float[] globalDistance = new float[0];
        public float[] globalDirectionAdherence = new float[0];
        public Branch branch1 = new Branch(), branch2 = new Branch();
        public Leaf leaf1 = new Leaf(), leaf2 = new Leaf();
        public float[] frondRippleDistance = new float[0];
        public float frondRippleTile, frondRippleLightingScalar;

        public bool IsValid => version == 8 && oscillation != null && oscillation.Length == Oscillators * CurvePoints;

        /// <summary>The curve `values` (CurvePoints entries from `offset`) at `strength` (0..1).</summary>
        public static float Evaluate(float[] values, float strength, int offset = 0)
        {
            if (values == null || values.Length < offset + CurvePoints)
                return 0f;
            float x = Mathf.Clamp01(strength) * (CurvePoints - 1);
            int i = Mathf.Min((int)x, CurvePoints - 2);
            return Mathf.LerpUnclamped(values[offset + i], values[offset + i + 1], x - i);
        }

        /// <summary>Content hash: equal configurations share one wind slot (VgWorld.AcquireSpeedTreeWind).</summary>
        public int ComputeHash()
        {
            unchecked
            {
                int h = version;
                void Add(float v) => h = h * 31 + v.GetHashCode();
                void AddArray(float[] a)
                {
                    if (a == null) { h = h * 31 + 1; return; }
                    foreach (float v in a) Add(v);
                }
                Add(strengthResponse); Add(directionResponse);
                Add(gustFrequency); Add(gustStrengthMin); Add(gustStrengthMax); Add(gustDurationMin); Add(gustDurationMax); Add(gustRiseScalar); Add(gustFallScalar);
                Add(anchorOffset); Add(anchorDistanceScale); Add(branchAnchor.x); Add(branchAnchor.y); Add(branchAnchor.z);
                AddArray(oscillation); Add(globalHeight); Add(globalHeightExponent); AddArray(globalDistance); AddArray(globalDirectionAdherence);
                foreach (var b in new[] { branch1, branch2 })
                {
                    AddArray(b?.distance); AddArray(b?.directionAdherence); AddArray(b?.whip);
                    Add(b?.turbulence ?? 0f); Add(b?.twitch ?? 0f); Add(b?.twitchFreqScale ?? 0f);
                }
                foreach (var l in new[] { leaf1, leaf2 })
                {
                    AddArray(l?.rippleDistance); AddArray(l?.tumbleFlip); AddArray(l?.tumbleTwist); AddArray(l?.tumbleDirectionAdherence); AddArray(l?.twitchThrow);
                    Add(l?.twitchSharpness ?? 0f);
                }
                AddArray(frondRippleDistance); Add(frondRippleTile); Add(frondRippleLightingScalar);
                return h;
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// Editor: the SpeedTree 8 wind configuration of the renderer's Tree component (null if it has
        /// none, or its wind asset is not a SpeedTree 8 one).
        /// </summary>
        public static VgSpeedTreeWindParams FromRenderer(Renderer renderer)
        {
            var tree = renderer != null ? renderer.GetComponent<Tree>() : null;
            return tree != null && tree.hasSpeedTreeWind ? FromAsset(tree.windAsset) : null;
        }

        /// <summary>Editor: reads a SpeedTreeWindAsset's SpeedTree 8 configuration (null if unavailable).</summary>
        public static VgSpeedTreeWindParams FromAsset(SpeedTreeWindAsset asset)
        {
            if (asset == null)
                return null;
            using var so = new UnityEditor.SerializedObject(asset);
            var version = so.FindProperty("m_eVersion");
            var c = so.FindProperty("m_Config8");
            if (version == null || version.intValue != 0 || c == null)
                return null;
            float F(UnityEditor.SerializedProperty parent, string name)
            {
                var p = parent.FindPropertyRelative(name);
                return p != null ? p.floatValue : 0f;
            }
            float[] Curve(UnityEditor.SerializedProperty parent, string name)
            {
                var a = new float[CurvePoints];
                for (int i = 0; i < CurvePoints; ++i)
                    a[i] = F(parent, name + "_" + i);
                return a;
            }
            Branch ReadBranch(string name)
            {
                var b = c.FindPropertyRelative(name);
                if (b == null)
                    return new Branch();
                return new Branch
                {
                    distance = Curve(b, "m_afDistance"),
                    directionAdherence = Curve(b, "m_afDirectionAdherence"),
                    whip = Curve(b, "m_afWhip"),
                    turbulence = F(b, "m_fTurbulence"),
                    twitch = F(b, "m_fTwitch"),
                    twitchFreqScale = F(b, "m_fTwitchFreqScale"),
                };
            }
            Leaf ReadLeaf(string name)
            {
                var l = c.FindPropertyRelative(name);
                if (l == null)
                    return new Leaf();
                return new Leaf
                {
                    rippleDistance = Curve(l, "m_afRippleDistance"),
                    tumbleFlip = Curve(l, "m_afTumbleFlip"),
                    tumbleTwist = Curve(l, "m_afTumbleTwist"),
                    tumbleDirectionAdherence = Curve(l, "m_afTumbleDirectionAdherence"),
                    twitchThrow = Curve(l, "m_afTwitchThrow"),
                    twitchSharpness = F(l, "m_fTwitchSharpness"),
                };
            }
            var r = new VgSpeedTreeWindParams
            {
                version = 8,
                oscillation = new float[Oscillators * CurvePoints],
                strengthResponse = F(c, "m_fStrengthResponse"),
                directionResponse = F(c, "m_fDirectionResponse"),
                gustFrequency = F(c, "m_fGustFrequency"),
                gustStrengthMin = F(c, "m_fGustStrengthMin"),
                gustStrengthMax = F(c, "m_fGustStrengthMax"),
                gustDurationMin = F(c, "m_fGustDurationMin"),
                gustDurationMax = F(c, "m_fGustDurationMax"),
                gustRiseScalar = F(c, "m_fGustRiseScalar"),
                gustFallScalar = F(c, "m_fGustFallScalar"),
                anchorOffset = F(c, "m_fAnchorOffset"),
                anchorDistanceScale = F(c, "m_fAnchorDistanceScale"),
                branchAnchor = new Vector3(F(c, "BranchWindAnchor0"), F(c, "BranchWindAnchor1"), F(c, "BranchWindAnchor2")),
                globalHeight = F(c, "m_fGlobalHeight"),
                globalHeightExponent = F(c, "m_fGlobalHeightExponent"),
                globalDistance = Curve(c, "m_afGlobalDistance"),
                globalDirectionAdherence = Curve(c, "m_afGlobalDirectionAdherence"),
                branch1 = ReadBranch("BranchLevel1"),
                branch2 = ReadBranch("BranchLevel2"),
                leaf1 = ReadLeaf("LeafGroup1"),
                leaf2 = ReadLeaf("LeafGroup2"),
                frondRippleDistance = Curve(c, "m_afFrondRippleDistance"),
                frondRippleTile = F(c, "m_fFrondRippleTile"),
                frondRippleLightingScalar = F(c, "m_fFrondRippleLightingScalar"),
            };
            for (int o = 0; o < Oscillators; ++o)
                for (int i = 0; i < CurvePoints; ++i)
                    r.oscillation[o * CurvePoints + i] = F(c, $"Oscillation{o}_{i}");
            return r;
        }
#endif
    }

    /// <summary>
    /// M11: SpeedTree 8 wind state of one wind configuration, advanced once per frame (a
    /// re-implementation of the SpeedTree runtime's wind update; Unity's own is internal): wind
    /// strength and direction follow the scene's WindZones with the configured response and gusts,
    /// oscillator phases accumulate their strength-dependent frequencies, and every effect's
    /// amplitude is read from its strength curve. <see cref="Current"/> / <see cref="Previous"/> are
    /// the <see cref="ParamCount"/> float4 of SpeedTree8Wind.hlsl (_ST_WindVector ... _ST_WindAnimation)
    /// of this and the previous frame.
    /// </summary>
    sealed class VgSpeedTreeWindSimulation
    {
        public const int ParamCount = 16;

        readonly VgSpeedTreeWindParams m_Params;
        readonly float[] m_Phase = new float[VgSpeedTreeWindParams.Oscillators];
        public readonly Vector4[] Current = new Vector4[ParamCount];
        public readonly Vector4[] Previous = new Vector4[ParamCount];
        bool m_Started;
        float m_Time, m_Strength;
        Vector3 m_Direction;
        uint m_Random;
        float m_NextGust, m_GustStart, m_GustRise, m_GustHold, m_GustFall, m_GustAmount;

        public VgSpeedTreeWindSimulation(VgSpeedTreeWindParams p, uint seed)
        {
            m_Params = p;
            m_Random = seed * 747796405u + 2891336453u;
        }

        public VgSpeedTreeWindParams Params => m_Params;

        float Random01()
        {
            m_Random = m_Random * 747796405u + 2891336453u;
            uint w = ((m_Random >> (int)((m_Random >> 28) + 4u)) ^ m_Random) * 277803737u;
            return ((w >> 22) ^ w) * (1f / 4294967296f);
        }

        // gusts: on average 1 / gustFrequency seconds apart, each raising the strength by
        // gustStrengthMin..Max (relative to the wind strength) with a rise, hold and fall
        float Gust(float time)
        {
            var p = m_Params;
            if (p.gustFrequency <= 0f)
                return 0f;
            if (time >= m_NextGust)
            {
                float duration = Mathf.Max(0.25f, 1f + Mathf.Lerp(p.gustDurationMin, p.gustDurationMax, Random01()));
                m_GustStart = time;
                m_GustRise = duration * Mathf.Clamp(p.gustRiseScalar, 0.05f, 1f);
                m_GustFall = duration * Mathf.Clamp(p.gustFallScalar, 0.05f, 1f);
                m_GustHold = duration;
                m_GustAmount = Mathf.Lerp(p.gustStrengthMin, p.gustStrengthMax, Random01());
                m_NextGust = time + m_GustRise + m_GustHold + m_GustFall - Mathf.Log(Mathf.Max(1e-4f, Random01())) / p.gustFrequency;
            }
            float t = time - m_GustStart;
            if (t < 0f)
                return 0f;
            float envelope = t < m_GustRise ? t / m_GustRise
                : t < m_GustRise + m_GustHold ? 1f
                : 1f - Mathf.Clamp01((t - m_GustRise - m_GustHold) / m_GustFall);
            return m_GustAmount * Mathf.SmoothStep(0f, 1f, envelope);
        }

        /// <summary>
        /// Advances to `time` (seconds) under the wind `direction` (world space) with `strength` (0..1;
        /// 0 = no wind: the parameters make SpeedTree8Wind.hlsl skip the wind).
        /// </summary>
        public void Advance(float time, Vector3 direction, float strength)
        {
            Array.Copy(Current, Previous, ParamCount);
            var p = m_Params;
            if (direction.sqrMagnitude < 1e-8f)
                direction = Vector3.forward;
            direction.Normalize();
            float dt = m_Started ? Mathf.Clamp(time - m_Time, 0f, 0.25f) : 0f;
            m_Time = time;
            float target = Mathf.Clamp01(strength * (1f + Gust(time)));
            if (!m_Started)
            {
                m_Strength = target;
                m_Direction = direction;
            }
            else
            {
                m_Strength = Mathf.Lerp(m_Strength, target, 1f - Mathf.Exp(-dt * Mathf.Max(0.1f, p.strengthResponse)));
                m_Direction = Vector3.Slerp(m_Direction, direction, 1f - Mathf.Exp(-dt * Mathf.Max(0.1f, p.directionResponse))).normalized;
            }
            float s = m_Strength;
            for (int o = 0; o < VgSpeedTreeWindParams.Oscillators; ++o)
                m_Phase[o] += dt * VgSpeedTreeWindParams.Evaluate(p.oscillation, s, o * VgSpeedTreeWindParams.CurvePoints);

            float E(float[] curve) => VgSpeedTreeWindParams.Evaluate(curve, s);
            var d = m_Direction;
            var anchor = p.branchAnchor + d * p.anchorOffset;
            float anchorLength = anchor.magnitude;
            var anchorDir = anchorLength > 1e-5f ? anchor / anchorLength : Vector3.up;
            var b1 = p.branch1;
            var b2 = p.branch2;
            var l1 = p.leaf1;
            var l2 = p.leaf2;

            Current[0] = strength > 0f ? new Vector4(d.x, d.y, d.z, s) : Vector4.zero; // _ST_WindVector
            Current[1] = new Vector4(m_Phase[0], E(p.globalDistance), 1f / Mathf.Max(p.globalHeight, 1e-3f), p.globalHeightExponent); // _ST_WindGlobal
            Current[2] = new Vector4(m_Phase[1], E(b1.distance), m_Phase[2], E(b2.distance));       // _ST_WindBranch
            Current[3] = new Vector4(b1.twitch, b1.twitchFreqScale, b2.twitch, b2.twitchFreqScale); // _ST_WindBranchTwitch
            Current[4] = new Vector4(E(b1.whip), E(b2.whip), 0f, 0f);                                // _ST_WindBranchWhip
            Current[5] = new Vector4(anchorDir.x, anchorDir.y, anchorDir.z, anchorLength * p.anchorDistanceScale); // _ST_WindBranchAnchor
            Current[6] = new Vector4(E(p.globalDirectionAdherence), E(b1.directionAdherence), E(b2.directionAdherence), 0f); // _ST_WindBranchAdherences
            Current[7] = new Vector4(b1.turbulence, b2.turbulence, 0f, 0f);                          // _ST_WindTurbulences
            Current[8] = new Vector4(m_Phase[3], E(l1.rippleDistance), 0f, 0f);                      // _ST_WindLeaf1Ripple
            Current[9] = new Vector4(m_Phase[4], E(l1.tumbleFlip), E(l1.tumbleTwist), E(l1.tumbleDirectionAdherence)); // _ST_WindLeaf1Tumble
            Current[10] = new Vector4(E(l1.twitchThrow), l1.twitchSharpness, m_Phase[5], 0f);        // _ST_WindLeaf1Twitch
            Current[11] = new Vector4(m_Phase[6], E(l2.rippleDistance), 0f, 0f);                     // _ST_WindLeaf2Ripple
            Current[12] = new Vector4(m_Phase[7], E(l2.tumbleFlip), E(l2.tumbleTwist), E(l2.tumbleDirectionAdherence)); // _ST_WindLeaf2Tumble
            Current[13] = new Vector4(E(l2.twitchThrow), l2.twitchSharpness, m_Phase[8], 0f);        // _ST_WindLeaf2Twitch
            Current[14] = new Vector4(m_Phase[9], E(p.frondRippleDistance), p.frondRippleTile, p.frondRippleLightingScalar); // _ST_WindFrondRipple
            Current[15] = new Vector4(time, 0f, 0f, 0f);                                              // _ST_WindAnimation

            if (!m_Started)
            {
                Array.Copy(Current, Previous, ParamCount);
                m_Started = true;
            }
        }
    }
}
