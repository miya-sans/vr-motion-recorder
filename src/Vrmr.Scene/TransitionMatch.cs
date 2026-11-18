using System;
using System.Collections.Generic;
using System.Linq;

namespace VRMR
{
    // =====================================================================================================  match score
    //
    // How seamless a cut from strip A (at source time ta) into strip B (at source time tb) is: both strips are sampled
    // on the scene frame grid over the crossfade window, B is placed onto A with exactly the alignment the evaluator
    // uses (Transitions: centre of mass / hips / feet + body facing), and the main joints are compared - positions and
    // velocities, weighted by body part (hips and feet count most: a sliding root or foot is what shows). The cost is
    // what the crossfade has to hide (RMS of the joint distances + 0.1 s x the mean velocity difference), in metres for a
    // 1.6 m body; the score maps it to 0..1.

    /// <summary>One strip's motion on the scene frame grid: element k = source time Src0 + k * SrcStep.</summary>
    public sealed class MotionSamples
    {
        public Vec3[] Com, ComVel, Hips, Feet, FootL, FootR;
        public int Count;
        public float[] Facing;

        /// <summary>
        ///     Score joints (TransitionMatch.Joints order): world position (strip root transform applied), velocity in m/s of
        ///     scene time.
        /// </summary>
        public Vec3[][] P, V;

        public double Src0, SrcStep;

        public int IndexOf(double src)
        {
            return (int)Math.Round((src - Src0) / SrcStep);
        }

        public double SrcOf(int k)
        {
            return Src0 + k * SrcStep;
        }

        public int Clamp(int k)
        {
            return k < 0 ? 0 : k >= Count ? Count - 1 : k;
        }

        public Vec3 Ref(TransitionAlign m, int k)
        {
            return m == TransitionAlign.Hips ? Hips[k] : m == TransitionAlign.Feet ? Feet[k] : Com[k];
        }
    }

    /// <summary>What the score is made of.</summary>
    public sealed class MatchResult
    {
        /// <summary>The root transform B gets (the evaluator's alignment for this cut).</summary>
        public Pose Align = Pose.Identity;

        /// <summary>Centre-of-mass velocity difference at the cut (m/s), horizontal.</summary>
        public float ComVelDiff;

        /// <summary>PosErr + Tau * VelErr, normalised to a 1.6 m body (m).</summary>
        public float Cost;

        /// <summary>Largest joint distance at the cut itself (m), and the largest joint velocity difference there (m/s).</summary>
        public float MaxJointDist, MaxJointVel;

        /// <summary>
        ///     Crossfade length (s) the pose difference at the cut needs (each joint's distance against the speed it may
        ///     gain).
        /// </summary>
        public float NeedLength;

        /// <summary>Mean position difference per body part (TransitionMatch.Parts), m; NaN = not compared.</summary>
        public float[] PartErr;

        /// <summary>
        ///     Joint position difference over the window (m, weighted root mean square: one limb far off counts more than all
        ///     a little off) and velocity difference (m/s, weighted mean).
        /// </summary>
        public float PosErr, VelErr;

        /// <summary>0..1 (1 = seamless).</summary>
        public double Score;

        public float WorstErr;

        /// <summary>The joint with the largest mean position difference, and that difference (m).</summary>
        public int WorstJoint = -1;
    }

    /// <summary>The fixed parts of a comparison: alignment, window, joints and weights, body size.</summary>
    public sealed class MatchSetup
    {
        public TransitionAlign Align = TransitionAlign.Com;
        public bool Aligned = true, MatchVelocity;
        public double Duration;
        public float Fps = 60, Height = 1.6f;

        /// <summary>Indices into TransitionMatch.Joints that are compared, their weights and body parts.</summary>
        public int[] J;

        public int[] JPart;
        public float[] JW;

        /// <summary>Window sample offsets (scene frames from the cut) and their weights.</summary>
        public int[] Off;

        public float[] OffW;

        /// <summary>Frames the samples must reach beyond a pair's index (window + velocity spans).</summary>
        public int Pad;

        public int VelSpan, ComSpan;
    }

    public static class TransitionMatch
    {
        /// <summary>Seconds a velocity difference counts for as a position difference (0.5 m/s ~ 5 cm).</summary>
        public const float Tau = 0.1f;

        /// <summary>
        ///     Cost (m, 1.6 m body) at which the score is 0.5: the geometric mean of what skipping 0.1 s within a take costs
        ///     on real takes (~5-11 cm, median 9) and what a cut into another take costs (~22-57 cm, median 35).
        /// </summary>
        public const float C0 = 0.18f;

        public const float RefHeight = 1.6f;

        /// <summary>Half-width (s) of the compared window at least (a hard cut is still compared over ±0.1 s).</summary>
        public const double MinHalfWindow = 0.1;

        public static readonly string[] Parts =
            { "hips", "spine", "head", "left arm", "right arm", "left leg", "right leg" };

        /// <summary>
        ///     Joints compared (bone, weight, part index, the extra speed a blend may give it unnoticed in m/s). Hips and feet
        ///     weigh most and may move least: a sliding root or foot is what shows.
        /// </summary>
        public static readonly (int Bone, float W, int Part, float VMax)[] Joints =
        {
            (HumanBones.Hips, 2.0f, 0, 0.4f),
            (HumanBones.Spine, 0.5f, 1, 0.6f), (HumanBones.Chest, 0.75f, 1, 0.6f),
            (HumanBones.Neck, 0.25f, 2, 0.6f), (HumanBones.Head, 1.5f, 2, 0.6f),
            (HumanBones.LeftUpperArm, 0.3f, 3, 0.8f), (HumanBones.LeftLowerArm, 0.6f, 3, 1.0f),
            (HumanBones.LeftHand, 1.0f, 3, 1.2f),
            (HumanBones.RightUpperArm, 0.3f, 4, 0.8f), (HumanBones.RightLowerArm, 0.6f, 4, 1.0f),
            (HumanBones.RightHand, 1.0f, 4, 1.2f),
            (HumanBones.LeftUpperLeg, 0.3f, 5, 0.6f), (HumanBones.LeftLowerLeg, 0.8f, 5, 0.6f),
            (HumanBones.LeftFoot, 2.0f, 5, 0.5f), (HumanBones.LeftToes, 0.5f, 5, 0.6f),
            (HumanBones.RightUpperLeg, 0.3f, 6, 0.6f), (HumanBones.RightLowerLeg, 0.8f, 6, 0.6f),
            (HumanBones.RightFoot, 2.0f, 6, 0.5f), (HumanBones.RightToes, 0.5f, 6, 0.6f)
        };

        /// <summary>
        ///     Score 0..1 from a cost (m for a 1.6 m body): 3 cm -> 0.97, 9 cm -> 0.8, 18 cm -> 0.5, 36 cm -> 0.2, 54 cm ->
        ///     0.1.
        /// </summary>
        public static double ScoreFromCost(float cost)
        {
            double x = cost / C0;
            return 1.0 / (1.0 + x * x);
        }

        // ---------------------------------------------------------------- setup and sampling

        /// <summary>The comparison for a transition between strips a and b (alignment, window, masks) on a rig.</summary>
        public static MatchSetup Setup(PoseRig rig, float scale, float fps, Transition tr, Strip a, Strip b,
            double? duration = null)
        {
            var s = new MatchSetup
            {
                Align = tr?.Align ?? TransitionAlign.Com, Aligned = Transitions.Aligns(tr, b),
                MatchVelocity = Transitions.MatchVelocity(tr),
                Fps = fps, Height = Transitions.BodyHeight(rig, scale), Duration = duration ?? tr?.Duration ?? 0.5
            };
            // window: the crossfade (at least ±0.1 s), 9 samples, the middle weighing most
            double half = Math.Max(s.Duration / 2, MinHalfWindow);
            const int M = 4;
            var off = new List<int>();
            var ow = new List<float>();
            for (int m = -M; m <= M; m++)
            {
                int o = (int)Math.Round(m / (double)M * half * fps);
                if (off.Contains(o)) continue;
                off.Add(o);
                ow.Add(1f - 0.5f * Math.Abs(m) / M);
            }

            s.Off = off.ToArray();
            s.OffW = ow.ToArray();
            // joints both strips drive (a transition only crossfades what both have)
            var mask = (a?.Mask ?? BodyPart.All) & (b?.Mask ?? BodyPart.All);
            if ((mask & (BodyPart.Root | BodyPart.Hips | BodyPart.Spine | BodyPart.Head | BodyPart.LeftArm |
                         BodyPart.RightArm | BodyPart.LeftLeg | BodyPart.RightLeg)) == 0) mask = BodyPart.All;
            var j = new List<int>();
            var jw = new List<float>();
            var jp = new List<int>();
            for (int i = 0; i < Joints.Length; i++)
            {
                (int bone, float w, int part, _) = Joints[i];
                if (!rig.Exists[bone]) continue;
                var bp = bone == HumanBones.Hips ? BodyPart.Root : BodyMask.PartOf(bone);
                if ((mask & bp) == 0) continue;
                j.Add(i);
                jw.Add(w);
                jp.Add(part);
            }

            s.J = j.ToArray();
            s.JW = jw.ToArray();
            s.JPart = jp.ToArray();
            s.VelSpan = Math.Max(1, (int)Math.Round(fps / 30.0));
            s.ComSpan = Math.Max(1, (int)Math.Round(Transitions.VelocityHalfSpan * fps));
            s.Pad = s.Off.Max(Math.Abs) + Math.Max(s.VelSpan, s.ComSpan) + 1;
            return s;
        }

        /// <summary>
        ///     Samples a strip's motion: count elements from source time src0, one scene frame apart (step = strip speed / fps).
        ///     sample(src, pose) gives the pose at a source time with the strip's root transform. Null if the data isn't ready.
        /// </summary>
        public static MotionSamples Sample(PoseRig rig, float scale, MatchSetup setup,
            Func<double, ScenePose, bool> sample, double src0, double step, int count)
        {
            count = Math.Max(1, count);
            int nj = Joints.Length;
            var m = new MotionSamples
            {
                Src0 = src0, SrcStep = step, Count = count, P = new Vec3[count][], V = new Vec3[count][],
                Com = new Vec3[count], ComVel = new Vec3[count], Hips = new Vec3[count], Feet = new Vec3[count],
                FootL = new Vec3[count], FootR = new Vec3[count], Facing = new float[count]
            };
            var pose = new ScenePose();
            bool feet = rig.Exists[HumanBones.LeftFoot] && rig.Exists[HumanBones.RightFoot];
            for (int k = 0; k < count; k++)
            {
                if (!sample(src0 + k * step, pose)) return null;
                var pos = pose.Positions(rig, scale, out var world);
                var p = m.P[k] = new Vec3[nj];
                for (int i = 0; i < nj; i++) p[i] = pos[Joints[i].Bone];
                m.Com[k] = rig.CenterOfMass(pos);
                m.Hips[k] = pos[HumanBones.Hips];
                m.FootL[k] = feet ? pos[HumanBones.LeftFoot] : pos[HumanBones.Hips];
                m.FootR[k] = feet ? pos[HumanBones.RightFoot] : pos[HumanBones.Hips];
                m.Feet[k] = (m.FootL[k] + m.FootR[k]) * 0.5f;
                m.Facing[k] = Transitions.BodyFacing(rig, pos, world[HumanBones.Hips]);
            }

            int vs = setup.VelSpan, cs = setup.ComSpan;
            for (int k = 0; k < count; k++)
            {
                int k0 = m.Clamp(k - vs), k1 = m.Clamp(k + vs);
                float inv = k1 > k0 ? setup.Fps / (k1 - k0) : 0;
                var v = m.V[k] = new Vec3[nj];
                for (int i = 0; i < nj; i++) v[i] = (m.P[k1][i] - m.P[k0][i]) * inv;
                int c0 = m.Clamp(k - cs), c1 = m.Clamp(k + cs);
                m.ComVel[k] = c1 > c0 ? (m.Com[c1] - m.Com[c0]) * (setup.Fps / (c1 - c0)) : Vec3.Zero;
            }

            return m;
        }

        // ---------------------------------------------------------------- comparison

        /// <summary>The alignment the evaluator gives B for a cut from A at element ia into B at element ib.</summary>
        public static Pose AlignOf(MatchSetup s, MotionSamples A, int ia, MotionSamples B, int ib)
        {
            if (!s.Aligned) return Pose.Identity;
            float yaw = s.MatchVelocity
                ? Transitions.VelocityYaw(A.Facing[ia], B.Facing[ib], A.ComVel[ia], B.ComVel[ib])
                : MathX.DeltaAngle(B.Facing[ib], A.Facing[ia]);
            return Transitions.AlignYaw(yaw, A.Ref(s.Align, ia), B.Ref(s.Align, ib));
        }

        /// <summary>Cost of a cut (m, 1.6 m body) without the details: for searches.</summary>
        public static float Cost(MatchSetup s, MotionSamples A, int ia, MotionSamples B, int ib)
        {
            var T = AlignOf(s, A, ia, B, ib);
            double sp = 0, sv = 0, sw = 0;
            for (int m = 0; m < s.Off.Length; m++)
            {
                int ka = A.Clamp(ia + s.Off[m]), kb = B.Clamp(ib + s.Off[m]);
                var pa = A.P[ka];
                var pb = B.P[kb];
                var va = A.V[ka];
                var vb = B.V[kb];
                float ow = s.OffW[m];
                for (int q = 0; q < s.J.Length; q++)
                {
                    int i = s.J[q];
                    float w = ow * s.JW[q];
                    sp += w * (pa[i] - T.TransformPoint(pb[i])).SqrLength;
                    sv += w * (va[i] - T.rot * vb[i]).Length;
                    sw += w;
                }
            }

            if (sw <= 0) return 0;
            return (float)(Math.Sqrt(sp / sw) + Tau * sv / sw) * RefHeight / Math.Max(0.3f, s.Height);
        }

        /// <summary>The full comparison of a cut (score, its parts, the worst joint).</summary>
        public static MatchResult Evaluate(MatchSetup s, MotionSamples A, int ia, MotionSamples B, int ib)
        {
            var T = AlignOf(s, A, ia, B, ib);
            var r = new MatchResult { Align = T, PartErr = new float[Parts.Length] };
            double[] partSum = new double[Parts.Length];
            double[] partW = new double[Parts.Length];
            double[] jointSum = new double[Joints.Length];
            double[] jointW = new double[Joints.Length];
            double sp = 0, sv = 0, sw = 0;
            for (int m = 0; m < s.Off.Length; m++)
            {
                int ka = A.Clamp(ia + s.Off[m]), kb = B.Clamp(ib + s.Off[m]);
                var pa = A.P[ka];
                var pb = B.P[kb];
                var va = A.V[ka];
                var vb = B.V[kb];
                float ow = s.OffW[m];
                for (int q = 0; q < s.J.Length; q++)
                {
                    int i = s.J[q];
                    float w = ow * s.JW[q];
                    float d = (pa[i] - T.TransformPoint(pb[i])).Length;
                    float dv = (va[i] - T.rot * vb[i]).Length;
                    sp += w * d * d;
                    sv += w * dv;
                    sw += w;
                    partSum[s.JPart[q]] += ow * d;
                    partW[s.JPart[q]] += ow;
                    jointSum[i] += ow * d;
                    jointW[i] += ow;
                    if (s.Off[m] == 0)
                    {
                        r.NeedLength = Math.Max(r.NeedLength, 1.5f * d / Joints[i].VMax);
                        float vis = i == 0 || Joints[i].Bone == HumanBones.Head ||
                                    Joints[i].Bone == HumanBones.LeftHand || Joints[i].Bone == HumanBones.RightHand
                                    || Joints[i].Bone == HumanBones.LeftFoot || Joints[i].Bone == HumanBones.RightFoot
                            ? 1f
                            : 0.7f;
                        r.MaxJointDist = Math.Max(r.MaxJointDist, d * vis);
                        r.MaxJointVel = Math.Max(r.MaxJointVel, dv * vis);
                    }
                }
            }

            float norm = RefHeight / Math.Max(0.3f, s.Height);
            if (sw > 0)
            {
                r.PosErr = (float)Math.Sqrt(sp / sw);
                r.VelErr = (float)(sv / sw);
            }

            r.Cost = (r.PosErr + Tau * r.VelErr) * norm;
            r.Score = ScoreFromCost(r.Cost);
            for (int p = 0; p < Parts.Length; p++)
                r.PartErr[p] = partW[p] > 0 ? (float)(partSum[p] / partW[p]) : float.NaN;
            for (int i = 0; i < Joints.Length; i++)
                if (jointW[i] > 0)
                {
                    float e = (float)(jointSum[i] / jointW[i]);
                    if (e > r.WorstErr)
                    {
                        r.WorstErr = e;
                        r.WorstJoint = Joints[i].Bone;
                    }
                }

            var dvc = A.ComVel[ia] - T.rot * B.ComVel[ib];
            dvc.y = 0;
            r.ComVelDiff = dvc.Length;
            return r;
        }

        // ---------------------------------------------------------------- transition length

        /// <summary>
        ///     A crossfade length (s) for a cut: long enough that blending away the pose difference doesn't give any joint more
        ///     extra speed than it can take unnoticed (smoothstep peak = 1.5 x distance / length; hips 0.4, feet 0.5, hands
        ///     1.2 m/s, for a 1.6 m body) and a velocity difference changes over a few tenths of a second; 0.2 .. 1.2 s, in
        ///     0.05 s steps. A bigger pose difference gets a longer crossfade.
        /// </summary>
        public static double SuggestLength(MatchResult r, float height)
        {
            float norm = RefHeight / Math.Max(0.3f, height);
            double len = Math.Max(0.2, Math.Max(r.NeedLength * norm, 0.5 * r.VelErr * norm));
            len = Math.Min(1.2, len);
            return Math.Round(len * 20) / 20;
        }

        // ---------------------------------------------------------------- foot slide, from positions

        /// <summary>
        ///     Horizontal travel (m) of a foot over frames [f0, f1] while it is on the floor (within contact of the lowest it
        ///     gets in [lo, hi]): what a planted foot slides.
        /// </summary>
        public static float FootSlide(Vec3[] foot, int f0, int f1, int lo, int hi, float contact)
        {
            if (foot == null || foot.Length == 0) return 0;
            lo = Math.Max(0, lo);
            hi = Math.Min(foot.Length - 1, hi);
            f0 = Math.Max(1, f0);
            f1 = Math.Min(foot.Length - 1, f1);
            float floor = float.MaxValue;
            for (int f = lo; f <= hi; f++) floor = Math.Min(floor, foot[f].y);
            float sum = 0;
            for (int f = f0; f <= f1; f++)
            {
                if (foot[f].y > floor + contact || foot[f - 1].y > floor + contact) continue;
                var d = foot[f] - foot[f - 1];
                d.y = 0;
                sum += d.Length;
            }

            return sum;
        }
    }
}
