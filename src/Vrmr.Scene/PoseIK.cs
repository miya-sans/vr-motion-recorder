using System;
using System.Collections.Generic;
using System.Linq;

namespace VRMR
{
    /// <summary>
    ///     IK for posing a <see cref="ScenePose" /> by hand (the scene editor's bone mode): two-bone limbs with a bend
    ///     direction,
    ///     the spine as a weighted chain, head look-at, bone rotation in world space, hips moves with pinned feet / hands.
    ///     Works on local rotations (relative to the nearest existing humanoid parent, Hips = world) with the rig's FK, so
    ///     the result is keyed straight into <c>bone/&lt;Name&gt;</c> channels.
    /// </summary>
    public sealed class PoseIK
    {
        // =====================================================================================================  limbs

        public enum Limb { LeftArm, RightArm, LeftLeg, RightLeg }

        // =====================================================================================================  hips, pins

        /// <summary>The hand / foot bones, for pins.</summary>
        public static readonly int[] Effectors =
            { HumanBones.LeftHand, HumanBones.RightHand, HumanBones.LeftFoot, HumanBones.RightFoot };

        public readonly PoseRig Rig;
        public readonly float Scale;

        public PoseIK(PoseRig rig, float scale)
        {
            Rig = rig;
            Scale = scale;
        }

        /// <summary>World positions / rotations of the last <see cref="Fk" />.</summary>
        public Vec3[] Pos { get; } = new Vec3[HumanBones.Count];

        public Quat[] World { get; } = new Quat[HumanBones.Count];

        public void Fk(ScenePose p)
        {
            Rig.Fk(p.Hips, p.Local, Scale, Pos, World);
        }

        public bool Has(int b)
        {
            return b >= 0 && Rig.Exists[b];
        }

        public static (int Upper, int Lower, int End) Bones(Limb l)
        {
            switch (l)
            {
                case Limb.LeftArm: return (HumanBones.LeftUpperArm, HumanBones.LeftLowerArm, HumanBones.LeftHand);
                case Limb.RightArm: return (HumanBones.RightUpperArm, HumanBones.RightLowerArm, HumanBones.RightHand);
                case Limb.LeftLeg: return (HumanBones.LeftUpperLeg, HumanBones.LeftLowerLeg, HumanBones.LeftFoot);
                default: return (HumanBones.RightUpperLeg, HumanBones.RightLowerLeg, HumanBones.RightFoot);
            }
        }

        /// <summary>The limb a hand / foot (or a forearm / shin / upper bone) belongs to.</summary>
        public static Limb? LimbOf(int bone)
        {
            foreach (Limb l in Enum.GetValues(typeof(Limb)))
            {
                (int u, int m, int e) = Bones(l);
                if (bone == u || bone == m || bone == e) return l;
            }

            return null;
        }

        public static bool IsArm(Limb l)
        {
            return l == Limb.LeftArm || l == Limb.RightArm;
        }

        bool LimbOk(Limb l)
        {
            (int u, int m, int e) = Bones(l);
            return Has(u) && Has(m) && Has(e) && Rig.Parent[m] == u && Rig.Parent[e] == m;
        }

        /// <summary>
        ///     Where the middle joint goes when the limb is straight, in world space: knees forward, elbows back and down, carried
        ///     by the upper bone's rotation from rest (so a twisted thigh twists the knee direction with it).
        /// </summary>
        public Vec3 BendHint(Limb l)
        {
            (int u, _, _) = Bones(l);
            var rest = IsArm(l) ? new Vec3(0, -0.35f, -1f).Normalized : Vec3.Forward;
            return World[u] * Rig.RestWorld[u].Conjugate * rest;
        }

        /// <summary>The current bend direction of a limb (unit, perpendicular to the upper-joint -> end line).</summary>
        public Vec3 PoleDirection(Limb l)
        {
            (int u, int m, int e) = Bones(l);
            Vec3 a = Pos[u], b = Pos[m], c = Pos[e];
            var w = (c - a).Normalized;
            var p = Vec3.ProjectOnPlane(b - a, w);
            float bend = Vec3.Angle(b - a, c - b);
            if (p.Length < 1e-4f * Scale || bend < 2f) p = Vec3.ProjectOnPlane(BendHint(l), w);
            return p.Normalized;
        }

        /// <summary>
        ///     Two-bone IK: puts the limb's end joint (hand / foot) on target. The bend plane stays as it is (swung along with the
        ///     limb) unless a pole point is given (the knee / elbow points towards it). endWorld: the end bone's new world
        ///     rotation
        ///     (null = it keeps its world rotation). Returns false when the limb can't be solved (missing bones).
        /// </summary>
        public bool SolveLimb(ScenePose p, Limb l, Vec3 target, Quat? endWorld = null, Vec3? pole = null)
        {
            if (!LimbOk(l)) return false;
            Fk(p);
            (int u, int m, int e) = Bones(l);
            Vec3 A = Pos[u], B = Pos[m], C = Pos[e];
            Quat qu = World[u], qm = World[m], qe = endWorld ?? World[e];
            float la = (B - A).Length, lb = (C - B).Length;
            if (la < 1e-6f || lb < 1e-6f) return false;
            // old frame: limb direction and bend direction
            var w0 = C - A;
            if (w0.Length < 1e-6f) w0 = B - A;
            w0 = w0.Normalized;
            var p0 = PoleDirection(l);
            // new limb direction and bend direction
            var toT = target - A;
            float d = toT.Length;
            var w1 = d > 1e-6f ? toT / d : w0;
            float dMin = Math.Abs(la - lb) + 1e-4f * Scale, dMax = (la + lb) * 0.99999f;
            d = MathX.Clamp(d, dMin, dMax);
            Vec3 p1;
            if (pole.HasValue) p1 = Vec3.ProjectOnPlane(pole.Value - A, w1);
            else p1 = Vec3.ProjectOnPlane(Quat.FromToRotation(w0, w1) * p0, w1);
            if (p1.Length < 1e-6f) p1 = Vec3.ProjectOnPlane(BendHint(l), w1);
            p1 = p1.Normalized;
            // law of cosines: middle joint at "along" from A on the line, "h" off it towards the pole
            float along = (la * la - lb * lb + d * d) / (2 * d);
            float h = (float)Math.Sqrt(Math.Max(0, la * la - along * along));
            var B1 = A + w1 * along + p1 * h;
            var T1 = A + w1 * d;
            // the bend plane normal, old and new; frames (bone direction, plane normal) map the upper bone over
            var n0 = Vec3.Cross(w0, p0).Normalized;
            var n1 = Vec3.Cross(w1, p1).Normalized;
            var u0 = (B - A).Normalized;
            var u1 = (B1 - A).Normalized;
            var F0 = Quat.FromBasis(u0, n0, Vec3.Cross(u0, n0).Normalized);
            var F1 = Quat.FromBasis(u1, n1, Vec3.Cross(u1, n1).Normalized);
            var R1 = (F1 * F0.Conjugate).Normalized;
            var qu1 = (R1 * qu).Normalized;
            var qm1 = (R1 * qm).Normalized;
            // the lower bone hinges about the plane normal onto the target
            var cDir = R1 * (C - B);
            var R2 = HingeRotation(cDir, T1 - B1, n1);
            qm1 = (R2 * qm1).Normalized;
            SetWorld(p, u, qu1);
            SetLocalFromWorld(p, m, qm1, qu1);
            SetLocalFromWorld(p, e, qe, qm1);
            Fk(p);
            return true;
        }

        /// <summary>Rotation about axis n taking direction a onto direction b (both projected onto the plane of n).</summary>
        static Quat HingeRotation(Vec3 a, Vec3 b, Vec3 n)
        {
            a = Vec3.ProjectOnPlane(a, n);
            b = Vec3.ProjectOnPlane(b, n);
            if (a.Length < 1e-9f || b.Length < 1e-9f) return Quat.Identity;
            a = a.Normalized;
            b = b.Normalized;
            float ang = (float)Math.Atan2(Vec3.Dot(Vec3.Cross(a, b), n), Vec3.Dot(a, b));
            return Quat.AngleAxisRad(ang, n);
        }

        void SetLocalFromWorld(ScenePose p, int b, Quat world, Quat parentWorld)
        {
            p.Local[b] = (parentWorld.Conjugate * world).Normalized;
        }

        // =====================================================================================================  bones in world space

        /// <summary>
        ///     Sets a bone's world rotation. keepChildren: its children keep their world rotations (only this bone turns);
        ///     else they follow (FK). Re-runs FK.
        /// </summary>
        public void SetWorld(ScenePose p, int b, Quat world, bool keepChildren = false)
        {
            if (!Has(b)) return;
            Fk(p);
            int pa = Rig.Parent[b];
            List<(int Bone, Quat World)> kids = null;
            if (keepChildren) kids = Rig.Order.Where(c => Rig.Parent[c] == b).Select(c => (c, World[c])).ToList();
            p.Local[b] = pa < 0 ? world.Normalized : (World[pa].Conjugate * world).Normalized;
            if (kids != null)
                foreach (var k in kids)
                    p.Local[k.Bone] = (world.Conjugate * k.World).Normalized;
            Fk(p);
        }

        /// <summary>Turns a bone by a world-space rotation (about its own joint).</summary>
        public void Rotate(ScenePose p, int b, Quat worldDelta, bool keepChildren = false)
        {
            if (!Has(b)) return;
            Fk(p);
            SetWorld(p, b, (worldDelta * World[b]).Normalized, keepChildren);
        }

        // =====================================================================================================  spine, head

        /// <summary>The spine bones (hips side first) that move a tip joint: Neck joint = Spine..UpperChest, Head joint = ..Neck.</summary>
        public int[] SpineChain(int tip)
        {
            var chain = new List<int>();
            for (int b = Rig.Parent[tip]; b >= 0 && b != HumanBones.Hips; b = Rig.Parent[b]) chain.Insert(0, b);
            return chain.ToArray();
        }

        /// <summary>
        ///     Moves a joint on the spine (the neck joint = top of the chest, or the head) towards a target by bending the spine
        ///     bones (weighted iterations, the upper bones turn more). keepHead: the head keeps its world rotation.
        ///     pins: hands / feet that stay where they are (their limbs are solved again).
        /// </summary>
        public void SolveSpine(ScenePose p, int tip, Vec3 target, bool keepHead = true, IEnumerable<int> pins = null,
            int iterations = 40)
        {
            if (!Has(tip)) return;
            int[] chain = SpineChain(tip);
            if (chain.Length == 0) return;
            Fk(p);
            var headW = Has(HumanBones.Head) ? World[HumanBones.Head] : Quat.Identity;
            var pinned = CapturePins(pins);
            for (int it = 0; it < iterations; it++)
            {
                for (int i = chain.Length - 1; i >= 0; i--)
                {
                    int b = chain[i];
                    var j = Pos[b];
                    var from = Pos[tip] - j;
                    var to = target - j;
                    if (from.Length < 1e-5f || to.Length < 1e-5f) continue;
                    // the bone nearest the tip carries more of the bend, but all of them share it; later passes
                    // converge (full CCD steps)
                    float share = 0.35f + 0.4f * (i + 1) / chain.Length;
                    float wgt = share + (1 - share) * Math.Min(1f, it / (iterations * 0.5f));
                    var r = Quat.Slerp(Quat.Identity, Quat.FromToRotation(from, to), wgt);
                    p.Local[b] = Local(b, (r * World[b]).Normalized);
                    Fk(p);
                }

                if ((Pos[tip] - target).Length < 2e-4f) break;
            }

            if (keepHead && tip != HumanBones.Head && Has(HumanBones.Head)) SetWorld(p, HumanBones.Head, headW);
            RestorePins(p, pinned);
        }

        Quat Local(int b, Quat world)
        {
            int pa = Rig.Parent[b];
            return pa < 0 ? world : (World[pa].Conjugate * world).Normalized;
        }

        /// <summary>The head's forward direction (the avatar's rest forward, +Z, carried by the head's rotation from rest).</summary>
        public Vec3 HeadForward()
        {
            return World[HumanBones.Head] * Rig.RestWorld[HumanBones.Head].Conjugate * Vec3.Forward;
        }

        /// <summary>Turns the neck and head so the head looks at a point (neckShare of the turn on the neck).</summary>
        public void LookAt(ScenePose p, Vec3 point, float neckShare = 0.4f)
        {
            if (!Has(HumanBones.Head)) return;
            Fk(p);
            var to = point - Pos[HumanBones.Head];
            if (to.Length < 1e-4f) return;
            var r = Quat.FromToRotation(HeadForward(), to);
            if (Has(HumanBones.Neck) && neckShare > 0)
            {
                Rotate(p, HumanBones.Neck, Quat.Slerp(Quat.Identity, r, neckShare));
                r = Quat.FromToRotation(HeadForward(), point - Pos[HumanBones.Head]);
            }

            Rotate(p, HumanBones.Head, r);
        }

        List<(Limb Limb, Vec3 Pos, Quat Rot)> CapturePins(IEnumerable<int> pins)
        {
            var l = new List<(Limb, Vec3, Quat)>();
            if (pins == null) return l;
            foreach (int b in pins.Distinct())
            {
                var lim = LimbOf(b);
                if (lim == null || !LimbOk(lim.Value) || Bones(lim.Value).End != b) continue;
                l.Add((lim.Value, Pos[b], World[b]));
            }

            return l;
        }

        void RestorePins(ScenePose p, List<(Limb Limb, Vec3 Pos, Quat Rot)> pins)
        {
            foreach (var pn in pins) SolveLimb(p, pn.Limb, pn.Pos, pn.Rot);
            Fk(p);
        }

        /// <summary>Moves / turns the hips (the whole body follows) and puts pinned hands / feet back where they were.</summary>
        public void MoveHips(ScenePose p, Vec3 hips, Quat? hipsWorld, IEnumerable<int> pins)
        {
            Fk(p);
            var pinned = CapturePins(pins);
            p.Hips = hips;
            if (hipsWorld.HasValue) p.Local[HumanBones.Hips] = hipsWorld.Value.Normalized;
            Fk(p);
            RestorePins(p, pinned);
        }

        /// <summary>Turns any bone in world space while pinned hands / feet stay (e.g. the chest with the hands on a rail).</summary>
        public void RotatePinned(ScenePose p, int b, Quat worldDelta, IEnumerable<int> pins, bool keepHead = false)
        {
            Fk(p);
            var pinned = CapturePins(pins?.Where(x => !IsBelow(x, b) || LimbOf(b) == null));
            var headW = Has(HumanBones.Head) ? World[HumanBones.Head] : Quat.Identity;
            if (b == HumanBones.Hips) p.Local[b] = (worldDelta * World[b]).Normalized;
            else Rotate(p, b, worldDelta);
            Fk(p);
            if (keepHead && b != HumanBones.Head && b != HumanBones.Neck && Has(HumanBones.Head) &&
                IsBelow(HumanBones.Head, b)) SetWorld(p, HumanBones.Head, headW);
            RestorePins(p, pinned);
        }

        /// <summary>Is bone a in the subtree of bone b (or b itself)?</summary>
        public bool IsBelow(int a, int b)
        {
            for (int x = a; x >= 0; x = Rig.Parent[x])
                if (x == b)
                    return true;
            return false;
        }

        // =====================================================================================================  mirror

        /// <summary>
        ///     The pose mirrored across the body's own middle plane (through the hips, facing direction from the hips): left and
        ///     right bones swap, rotations from rest are reflected. The hips stay where they are. Rest must be symmetric (T-pose).
        /// </summary>
        public void Mirror(ScenePose src, ScenePose dst)
        {
            Fk(src);
            var world = (Quat[])World.Clone();
            var face = Quat.YawRotation(Rig.Facing(world[HumanBones.Hips]));
            var fi = face.Conjugate;
            var outW = new Quat[HumanBones.Count];
            foreach (int b in Rig.Order)
            {
                int m = HumanBones.Mirror(b);
                if (!Has(m)) m = b;
                // rotation from rest, reflected across the body's middle plane (a frame: F · refl(F⁻¹ · Δ)), onto the mirror bone's rest
                var l = fi * (world[b] * Rig.RestWorld[b].Conjugate);
                outW[m] = (face * new Quat(l.x, -l.y, -l.z, l.w) * Rig.RestWorld[m]).Normalized;
            }

            if (!ReferenceEquals(src, dst)) dst.CopyFrom(src);
            foreach (int b in Rig.Order)
            {
                int pa = Rig.Parent[b];
                dst.Local[b] = pa < 0 ? outW[b] : (outW[pa].Conjugate * outW[b]).Normalized;
            }

            Fk(dst);
        }

        /// <summary>
        ///     Reflects a world rotation / position across the vertical plane through origin with the given facing (yaw,
        ///     degrees).
        /// </summary>
        public static Pose MirrorPose(Pose p, Vec3 origin, float facingYaw)
        {
            var face = Quat.YawRotation(facingYaw);
            var fi = face.Conjugate;
            var lp = fi * (p.pos - origin);
            var lr = fi * p.rot;
            var mp = new Vec3(-lp.x, lp.y, lp.z);
            var mr = new Quat(lr.x, -lr.y, -lr.z, lr.w);
            return new Pose(origin + face * mp, (face * mr).Normalized);
        }
    }
}
