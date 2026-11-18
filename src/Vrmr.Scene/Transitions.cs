using System;
using System.Runtime.CompilerServices;

namespace VRMR
{
    /// <summary>
    ///     Transition math: placing the strip after a cut onto the strip before it (centre of mass / hips / feet and the
    ///     body's facing), and the facing / reference points the match score (TransitionMatch) uses, so the score measures
    ///     exactly the alignment the evaluator applies.
    /// </summary>
    public static class Transitions
    {
        /// <summary>Half the time span (s) the centre-of-mass velocity is measured over for velocity matching.</summary>
        public const double VelocityHalfSpan = 0.1;

        static readonly ConditionalWeakTable<PoseRig, RigInfo> _info = new();

        /// <summary>Weight of the shoulder line against the hips in the body's facing (0 = hips only).</summary>
        public static float ShoulderWeight = 0.6f;

        // ---------------------------------------------------------------- simple pose distance (kept for callers of the first version)

        static readonly int[] ScoreBones =
        {
            HumanBones.Hips, HumanBones.Spine, HumanBones.Chest, HumanBones.Neck, HumanBones.Head,
            HumanBones.LeftUpperArm, HumanBones.LeftLowerArm, HumanBones.LeftHand, HumanBones.RightUpperArm,
            HumanBones.RightLowerArm, HumanBones.RightHand,
            HumanBones.LeftUpperLeg, HumanBones.LeftLowerLeg, HumanBones.LeftFoot, HumanBones.RightUpperLeg,
            HumanBones.RightLowerLeg, HumanBones.RightFoot
        };

        static RigInfo Info(PoseRig rig)
        {
            return _info.GetValue(rig, r =>
            {
                var p = new ScenePose();
                p.SetRest(r, 1);
                var pos = p.Positions(r, 1, out _);
                var i = new RigInfo();
                int l = HumanBones.LeftUpperArm, rr = HumanBones.RightUpperArm;
                if (r.Exists[l] && r.Exists[rr])
                {
                    var d = pos[rr] - pos[l];
                    d.y = 0;
                    if (d.Length > 1e-3f)
                    {
                        i.ShoulderRest = LineFacing(pos[l], pos[rr]);
                        i.ShoulderLen = d.Length;
                        i.HasShoulders = true;
                    }
                }

                float foot = float.MaxValue;
                foreach (int b in new[] { HumanBones.LeftFoot, HumanBones.RightFoot })
                    if (r.Exists[b])
                        foot = Math.Min(foot, pos[b].y);
                if (foot == float.MaxValue) foot = 0;
                float head = r.Exists[HumanBones.Head] ? pos[HumanBones.Head].y : pos[HumanBones.Hips].y * 1.75f;
                // head joint -> top of head ~ +12 %, ankle joint -> sole ~ a few cm
                i.Height = Math.Max(0.3f, (head - foot) * 1.12f + 0.06f);
                return i;
            });
        }

        /// <summary>Approximate standing height of the avatar at a scale (m): what the match score is normalised by.</summary>
        public static float BodyHeight(PoseRig rig, float scale)
        {
            return Info(rig).Height * scale;
        }

        /// <summary>Facing (deg about +Y) of the normal of a left -> right line (shoulders, hips): +Z forward for a line along +X.</summary>
        static float LineFacing(Vec3 left, Vec3 right)
        {
            var d = right - left;
            return (float)(Math.Atan2(-d.z, d.x) * MathX.Rad2Deg);
        }

        static Vec3 Dir(float deg)
        {
            float r = deg * MathX.Deg2Rad;
            return new Vec3((float)Math.Sin(r), 0, (float)Math.Cos(r));
        }

        static float Angle(Vec3 v)
        {
            return (float)(Math.Atan2(v.x, v.z) * MathX.Rad2Deg);
        }

        /// <summary>
        ///     The body's facing (deg about +Y): the hips' turn from the rest pose, averaged with the normal of the shoulder line
        ///     (so a twisted torso or a hips solve that is a few degrees off doesn't turn the whole next take). The shoulder line
        ///     counts less when it isn't level (leaning far over, lying).
        /// </summary>
        public static float BodyFacing(PoseRig rig, Vec3[] pos, Quat hipsWorld)
        {
            var v = Dir(rig.Facing(hipsWorld));
            var info = Info(rig);
            if (info.HasShoulders)
            {
                var a = pos[HumanBones.LeftUpperArm];
                var b = pos[HumanBones.RightUpperArm];
                var d = b - a;
                d.y = 0;
                float level = MathX.Clamp01(d.Length / Math.Max(1e-4f, info.ShoulderLen * ScaleOf(rig, pos)));
                if (level > 0.2f) v += Dir(LineFacing(a, b) - info.ShoulderRest) * (ShoulderWeight * level);
            }

            return v.SqrLength < 1e-8f ? 0 : Angle(v);
        }

        /// <summary>
        ///     The scale a pose was made at, from its hips -> upper leg distance (FK positions only carry the scale
        ///     implicitly).
        /// </summary>
        static float ScaleOf(PoseRig rig, Vec3[] pos)
        {
            int l = HumanBones.LeftUpperLeg;
            if (!rig.Exists[l]) return 1;
            float rest = rig.RestOffset[l].Length;
            return rest > 1e-5f ? (pos[l] - pos[HumanBones.Hips]).Length / rest : 1;
        }

        public static float BodyFacing(PoseRig rig, float scale, ScenePose p)
        {
            var pos = p.Positions(rig, scale, out var w);
            return BodyFacing(rig, pos, w[HumanBones.Hips]);
        }

        // ---------------------------------------------------------------- alignment

        /// <summary>
        ///     Root transform (a turn about the vertical axis through the origin, then a horizontal move) that puts pose
        ///     <paramref name="to" /> onto pose <paramref name="from" />: the same body facing (hips + shoulders), and the chosen
        ///     reference point (centre of mass, hips or the midpoint of the feet) at the same place on the floor. Heights are
        ///     never changed, so the feet stay at the floor height they were recorded at.
        /// </summary>
        public static Pose AlignOffset(TransitionAlign mode, PoseRig rig, float scale, ScenePose from, ScenePose to)
        {
            if (mode == TransitionAlign.None) return Pose.Identity;
            var pf = from.Positions(rig, scale, out var wf);
            float ff = BodyFacing(rig, pf, wf[HumanBones.Hips]);
            var rf = Reference(mode, rig, pf);
            var pt = to.Positions(rig, scale, out var wt);
            float ft = BodyFacing(rig, pt, wt[HumanBones.Hips]);
            var rt = Reference(mode, rig, pt);
            return AlignYaw(MathX.DeltaAngle(ft, ff), rf, rt);
        }

        /// <summary>
        ///     The alignment of a transition with access to both strips around the cut (scene time -> pose with the strip's
        ///     root transform). Same as AlignOffset, plus, when the transition matches velocities, the turn follows the
        ///     direction the centre of mass travels in both strips (walking into walking keeps its path).
        /// </summary>
        public static Pose AlignTransition(Transition tr, PoseRig rig, float scale, ScenePose from, ScenePose to,
            Func<double, ScenePose, bool> sampleFrom, Func<double, ScenePose, bool> sampleTo, double cut)
        {
            if (tr == null || tr.Align == TransitionAlign.None) return Pose.Identity;
            if (!MatchVelocity(tr) || sampleFrom == null || sampleTo == null)
                return AlignOffset(tr.Align, rig, scale, from, to);
            var pf = from.Positions(rig, scale, out var wf);
            float ff = BodyFacing(rig, pf, wf[HumanBones.Hips]);
            var rf = Reference(tr.Align, rig, pf);
            var pt = to.Positions(rig, scale, out var wt);
            float ft = BodyFacing(rig, pt, wt[HumanBones.Hips]);
            var rt = Reference(tr.Align, rig, pt);
            var tmp = new ScenePose();

            Vec3? ComVel(Func<double, ScenePose, bool> smp)
            {
                if (!smp(cut - VelocityHalfSpan, tmp)) return null;
                var c0 = tmp.CenterOfMass(rig, scale);
                if (!smp(cut + VelocityHalfSpan, tmp)) return null;
                var c1 = tmp.CenterOfMass(rig, scale);
                return (c1 - c0) / (float)(2 * VelocityHalfSpan);
            }

            var va = ComVel(sampleFrom);
            var vb = ComVel(sampleTo);
            float yaw = va.HasValue && vb.HasValue ? VelocityYaw(ff, ft, va.Value, vb.Value) : MathX.DeltaAngle(ft, ff);
            return AlignYaw(yaw, rf, rt);
        }

        /// <summary>
        ///     The turn (deg) to apply to the strip after the cut: the facing difference, blended towards the difference of the
        ///     centre-of-mass travel directions when both move (fully from 0.5 m/s) and the two don't disagree by much (a
        ///     strip walking backwards isn't turned around).
        /// </summary>
        public static float VelocityYaw(float facingFrom, float facingTo, Vec3 velFrom, Vec3 velTo)
        {
            float body = MathX.DeltaAngle(facingTo, facingFrom);
            velFrom.y = 0;
            velTo.y = 0;
            float sa = velFrom.Length, sb = velTo.Length;
            if (sa < 1e-3f || sb < 1e-3f) return body;
            float vel = MathX.DeltaAngle(Angle(velTo), Angle(velFrom));
            float diff = MathX.DeltaAngle(body, vel);
            float w = MathX.Clamp01((Math.Min(sa, sb) - 0.15f) / 0.35f) * MathX.Clamp01((60 - Math.Abs(diff)) / 30f);
            return body + diff * w;
        }

        /// <summary>A turn by yaw (about the origin), then the horizontal move that puts refTo (turned) onto refFrom.</summary>
        public static Pose AlignYaw(float yaw, Vec3 refFrom, Vec3 refTo)
        {
            var rot = Quat.YawRotation(yaw);
            var d = refFrom - rot * refTo;
            return new Pose(new Vec3(d.x, 0, d.z), rot);
        }

        /// <summary>Whether a transition also matches the centre of mass's travel direction (Transition.MatchVelocity).</summary>
        public static bool MatchVelocity(Transition tr)
        {
            return tr != null && tr.MatchVelocity;
        }

        /// <summary>The point a transition aligns on.</summary>
        public static Vec3 Reference(TransitionAlign mode, PoseRig rig, float scale, ScenePose p)
        {
            return Reference(mode, rig, p.Positions(rig, scale, out _));
        }

        public static Vec3 Reference(TransitionAlign mode, PoseRig rig, Vec3[] pos)
        {
            switch (mode)
            {
                case TransitionAlign.Hips: return pos[HumanBones.Hips];
                case TransitionAlign.Feet:
                    if (rig.Exists[HumanBones.LeftFoot] && rig.Exists[HumanBones.RightFoot])
                        return (pos[HumanBones.LeftFoot] + pos[HumanBones.RightFoot]) * 0.5f;
                    return pos[HumanBones.Hips];
                default: return rig.CenterOfMass(pos);
            }
        }

        /// <summary>
        ///     Whether the evaluator aligns the strip after the cut (it moves the whole strip, so only a full-root Replace
        ///     strip).
        /// </summary>
        public static bool Aligns(Transition tr, Strip to)
        {
            return tr != null && to != null && tr.Align != TransitionAlign.None && to.Blend == BlendMode.Replace &&
                   (to.Mask & BodyPart.Root) != 0;
        }

        /// <summary>
        ///     Mean distance (m, avatar scale) of the main joints between two poses, both seen from their own hips on the
        ///     floor (facing and horizontal position taken out). The full match score (velocities, body-part weights, the
        ///     transition's own alignment) is TransitionMatch.
        /// </summary>
        public static float PoseDistance(PoseRig rig, float scale, ScenePose a, ScenePose b)
        {
            var pa = (Vec3[])a.Positions(rig, scale, out var wa).Clone();
            float faDeg = BodyFacing(rig, pa, wa[HumanBones.Hips]);
            var pb = b.Positions(rig, scale, out var wb);
            float fbDeg = BodyFacing(rig, pb, wb[HumanBones.Hips]);
            var fa = Quat.YawRotation(-faDeg);
            var fb = Quat.YawRotation(-fbDeg);
            var ha = pa[HumanBones.Hips];
            var hb = pb[HumanBones.Hips];
            float sum = 0;
            int n = 0;
            foreach (int bone in ScoreBones)
            {
                if (!rig.Exists[bone]) continue;
                var va = fa * (pa[bone] - new Vec3(ha.x, 0, ha.z));
                var vb = fb * (pb[bone] - new Vec3(hb.x, 0, hb.z));
                sum += (va - vb).Length;
                n++;
            }

            return n > 0 ? sum / n : 0;
        }

        /// <summary>Match score 0..1 from a mean joint distance (m): the same curve as TransitionMatch, for a 1.6 m body.</summary>
        public static double ScoreFromDistance(float meters)
        {
            return TransitionMatch.ScoreFromCost(meters);
        }
        // ---------------------------------------------------------------- facing

        sealed class RigInfo
        {
            public bool HasShoulders;

            /// <summary>Standing height at scale 1 (m, approximate: top of the head above the soles).</summary>
            public float Height;

            /// <summary>Facing (deg) of the shoulder line in the rest pose, and its horizontal length at scale 1.</summary>
            public float ShoulderRest, ShoulderLen;
        }
    }
}
