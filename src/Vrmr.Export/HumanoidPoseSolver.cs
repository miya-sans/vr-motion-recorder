using System;
using System.Collections.Generic;

namespace VRMR
{
    /// <summary>One frame in Unity's Humanoid form: muscle values, the root (body) transform and the IK goals.</summary>
    public sealed class HumanoidFrame
    {
        /// <summary>Goal order of <see cref="GoalT"/> and <see cref="GoalQ"/>.</summary>
        public const int LeftFoot = 0, RightFoot = 1, LeftHand = 2, RightHand = 3, GoalCount = 4;

        /// <summary>Muscle values in HumanTrait order (1 = the muscle's positive limit; not clamped).</summary>
        public readonly float[] Muscles = new float[HumanoidTables.MuscleCount];
        /// <summary>Body position: the centre of mass divided by the avatar's human scale.</summary>
        public Vec3 RootT;
        /// <summary>Body rotation.</summary>
        public Quat RootQ = Quat.Identity;
        /// <summary>IK goal positions relative to the body transform, divided by the human scale.</summary>
        public readonly Vec3[] GoalT = new Vec3[GoalCount];
        /// <summary>IK goal rotations relative to the body rotation.</summary>
        public readonly Quat[] GoalQ = { Quat.Identity, Quat.Identity, Quat.Identity, Quat.Identity };
    }

    /// <summary>
    /// Converts a world pose into Humanoid muscles for one avatar, and back the way Unity plays them. The conversion
    /// walks down the skeleton against the pose the muscles really produce: what a bone cannot express (a clavicle's
    /// twist, a finger's roll) is taken up by the next bone, so joints, hands and feet end where the pose has them.
    /// </summary>
    public sealed class HumanoidPoseSolver
    {
        const int N = HumanBones.Count;
        static readonly int[] SpineChain = { HumanBones.Spine, HumanBones.Chest, HumanBones.UpperChest, HumanBones.Neck, HumanBones.Head };
        /// <summary>An elbow or knee straighter than this (sine of the bend) no longer decides the limb's twist.</summary>
        const float StraightLimbStart = 0.017f, StraightLimbEnd = 0.07f;

        readonly HumanoidRig _rig;
        readonly Quat[] _axes = new Quat[N], _axesInv = new Quat[N], _zero = new Quat[N], _zeroInv = new Quat[N];
        readonly List<int>[] _guests = new List<int>[N];   // missing spine bones whose muscles act on this bone
        readonly int[] _order;                             // existing bones, parents first
        readonly Quat[] _target = new Quat[N];             // pose to convert, relative to the T-pose
        readonly Quat[] _full = new Quat[N];               // reconstructed rotation as the bone's children see it
        readonly Vec3[] _pos = new Vec3[N];
        readonly float[] _angles = new float[3];

        /// <summary>A solver for one avatar's muscle space.</summary>
        public HumanoidPoseSolver(HumanoidRig rig)
        {
            _rig = rig ?? throw new ArgumentNullException(nameof(rig));
            var order = new List<int>();
            void Visit(int b) { if (order.Contains(b)) return; if (rig.Parent[b] >= 0) Visit(rig.Parent[b]); order.Add(b); }
            for (int b = 0; b < N; b++)
            {
                _guests[b] = new List<int>();
                if (!rig.Exists[b]) continue;
                Visit(b);
                _axes[b] = rig.Axes[b]; _axesInv[b] = rig.Axes[b].Conjugate;
                _zero[b] = HumanoidTables.ZeroPose[b]; _zeroInv[b] = _zero[b].Conjugate;
            }
            _order = order.ToArray();
            AddGuest(HumanBones.Chest, HumanBones.Spine);
            AddGuest(HumanBones.UpperChest, rig.Exists[HumanBones.Chest] ? HumanBones.Chest : HumanBones.Spine);
            AddGuest(HumanBones.Neck, HumanBones.Head);
        }

        void AddGuest(int missing, int host) { if (!_rig.Exists[missing] && _rig.Exists[host]) _guests[host].Add(missing); }

        /// <summary>The avatar's muscle space.</summary>
        public HumanoidRig Rig => _rig;

        // ------------------------------------------------------------------ pose -> muscles

        /// <summary>
        /// Converts a pose: world rotations per humanoid bone (hasRotation false = the bone follows its parent) and the
        /// hips position, both with the avatar root at the origin and the avatar at its base size.
        /// </summary>
        public void Solve(Quat[] worldRot, bool[] hasRotation, Vec3 hipsPos, HumanoidFrame frame)
        {
            Array.Clear(frame.Muscles, 0, frame.Muscles.Length);
            foreach (int b in _order)
            {
                bool own = hasRotation == null || hasRotation[b];
                _target[b] = own || _rig.Parent[b] < 0 ? (worldRot[b] * _rig.RestRot[b].Conjugate).Normalized : _target[_rig.Parent[b]];
            }
            _full[HumanBones.Hips] = _target[HumanBones.Hips];
            foreach (int b in SpineChain) if (_rig.Exists[b]) SolveBall(b, frame.Muscles);
            SolveSwing(HumanBones.LeftShoulder, frame.Muscles); SolveSwing(HumanBones.RightShoulder, frame.Muscles);
            SolveLimb(HumanBones.LeftUpperArm, frame.Muscles); SolveLimb(HumanBones.RightUpperArm, frame.Muscles);
            SolveLimb(HumanBones.LeftUpperLeg, frame.Muscles); SolveLimb(HumanBones.RightUpperLeg, frame.Muscles);
            SolveHinge(HumanBones.LeftToes, frame.Muscles); SolveHinge(HumanBones.RightToes, frame.Muscles);
            for (int b = HumanBones.LeftThumbProximal; b < HumanBones.UpperChest; b += 3)
            {
                SolveSwing(b, frame.Muscles); SolveHinge(b + 1, frame.Muscles); SolveHinge(b + 2, frame.Muscles);
            }
            SolveSwing(HumanBones.LeftEye, frame.Muscles); SolveSwing(HumanBones.RightEye, frame.Muscles); SolveSwing(HumanBones.Jaw, frame.Muscles);
            Positions(hipsPos);
            WriteRoot(frame);
        }

        /// <summary>Target rotation of bone b in its muscle axes, given the reconstructed parent.</summary>
        Quat MuscleSpace(int b, Quat targetRot) =>
            (_axesInv[b] * _zeroInv[b] * _full[_rig.Parent[b]].Conjugate * targetRot * _axes[b]).Normalized;

        /// <summary>Rotation of bone b (as its children see it) for a rotation given in its muscle axes.</summary>
        Quat Compose(int b, Quat muscleRot) => (_full[_rig.Parent[b]] * _zero[b] * _axes[b] * muscleRot * _axesInv[b]).Normalized;

        /// <summary>A bone with three muscles: its rotation is matched exactly.</summary>
        void SolveBall(int b, float[] muscles)
        {
            HumanoidMath.SwingTwist(MuscleSpace(b, _target[b]), out float x, out float y, out float z);
            SetMuscle(b, 0, x, muscles); SetMuscle(b, 1, y, muscles); SetMuscle(b, 2, z, muscles);
            _full[b] = Compose(b, HumanoidMath.FromSwingTwist(x, y, z));
        }

        /// <summary>A bone without a twist muscle: its direction is matched, its roll is left to the bones below.</summary>
        void SolveSwing(int b, float[] muscles)
        {
            if (!_rig.Exists[b]) return;
            HumanoidMath.SwingTwist(MuscleSpace(b, _target[b]), out _, out float y, out float z);
            SetMuscle(b, 1, y, muscles); SetMuscle(b, 2, z, muscles);
            _full[b] = Compose(b, HumanoidMath.FromSwingTwist(0, y, z));
        }

        /// <summary>A bone with one muscle (finger joints, toes): the bend that brings its direction closest to the pose's.</summary>
        void SolveHinge(int b, float[] muscles)
        {
            if (!_rig.Exists[b]) return;
            var dir = MuscleSpace(b, _target[b]) * Vec3.Right;
            float z = (float)Math.Atan2(dir.y, dir.x);
            SetMuscle(b, 2, z, muscles);
            _full[b] = Compose(b, HumanoidMath.FromSwingTwist(0, 0, z));
        }

        /// <summary>
        /// An arm or a leg from its upper bone: the upper bone's swing puts the elbow / knee in place, its twist turns
        /// the hinge so the wrist / ankle is in place, and the lower bone's twist leaves the hand / foot a pure swing
        /// onto its world rotation.
        /// </summary>
        void SolveLimb(int upper, float[] muscles)
        {
            int lower = upper + 2, end = upper + 4;
            int parent = _rig.Parent[upper];
            HumanoidMath.SwingTwist(MuscleSpace(upper, _target[upper]), out float poseTwist, out float uy, out float uz);
            var swing = HumanoidMath.FromSwingTwist(0, uy, uz);

            // twist of the upper bone: the lower bone only bends about its Z, so its direction must lie in the hinge plane
            var lowerDir = _target[lower] * (_axes[lower] * Vec3.Right);
            var w = swing.Conjugate * (_axesInv[upper] * (_zeroInv[upper] * (_full[parent].Conjugate * lowerDir)));
            var k = _axesInv[upper] * (_zero[lower] * (_axes[lower] * Vec3.Forward));
            float a = w.y * k.y + w.z * k.z, bb = w.z * k.y - w.y * k.z, c = w.x * k.x;
            float bend = (float)Math.Sqrt(a * a + bb * bb);
            float twist = poseTwist;
            if (bend > 1e-6f)
            {
                float mid = (float)Math.Atan2(bb, a), half = (float)Math.Acos(MathX.Clamp(-c / bend, -1f, 1f));
                float t1 = HumanoidMath.Nearest(mid + half, poseTwist), t2 = HumanoidMath.Nearest(mid - half, poseTwist);
                float solved = Math.Abs(t1 - poseTwist) <= Math.Abs(t2 - poseTwist) ? t1 : t2;
                // a straight limb does not define its twist: fade to the pose's own twist there
                twist = poseTwist + (solved - poseTwist) * MathX.SmoothStep(MathX.InverseLerp(StraightLimbStart, StraightLimbEnd, bend));
            }
            SetMuscle(upper, 0, twist, muscles); SetMuscle(upper, 1, uy, muscles); SetMuscle(upper, 2, uz, muscles);
            _full[upper] = Compose(upper, HumanoidMath.FromSwingTwist(twist, uy, uz));

            // lower bone: the bend from its direction, then the twist that leaves the end bone free of twist
            var dir = _axesInv[lower] * (_zeroInv[lower] * (_full[upper].Conjugate * lowerDir));
            float stretch = (float)Math.Atan2(dir.y, dir.x);
            var bendRot = HumanoidMath.FromSwingTwist(0, 0, stretch);
            var q = bendRot.Conjugate * _axesInv[lower] * _zeroInv[lower] * _full[upper].Conjugate * _target[end] * _axes[end];
            var k2 = _axesInv[end] * _zeroInv[end] * _axes[lower];
            float ax = (k2 * q).x, bx = (k2 * new Quat(-1, 0, 0, 0) * q).x;
            float lowerTwist = Math.Abs(bx) > 1e-9f ? 2f * (float)Math.Atan(-ax / bx) : (float)Math.PI;
            SetMuscle(lower, 0, lowerTwist, muscles); SetMuscle(lower, 2, stretch, muscles);
            _full[lower] = Compose(lower, HumanoidMath.FromSwingTwist(lowerTwist, 0, stretch));
            SolveSwing(end, muscles);
        }

        /// <summary>Writes the muscle of bone b's axis (0 twist, 1, 2) for an angle; missing spine bones share it.</summary>
        void SetMuscle(int b, int axis, float angle, float[] muscles)
        {
            int own = HumanoidTables.MuscleOfBone[b, axis];
            if (own < 0) return;
            float a = angle * HumanoidTables.AxisSign[b, axis];
            float limit = Limit(own, a);
            foreach (int g in _guests[b]) limit += Limit(HumanoidTables.MuscleOfBone[g, axis], a);
            float v = limit > 1e-6f ? a / limit : 0;
            muscles[own] = v;
            foreach (int g in _guests[b]) muscles[HumanoidTables.MuscleOfBone[g, axis]] = v;
        }

        float Limit(int muscle, float angle) => angle >= 0 ? _rig.MuscleMax[muscle] : -_rig.MuscleMin[muscle];

        // ------------------------------------------------------------------ muscles -> pose

        /// <summary>The three axis angles of bone b for a set of muscles (with the muscles of missing spine bones).</summary>
        void Angles(int b, float[] muscles)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                float a = MuscleAngle(HumanoidTables.MuscleOfBone[b, axis], muscles);
                foreach (int g in _guests[b]) a += MuscleAngle(HumanoidTables.MuscleOfBone[g, axis], muscles);
                _angles[axis] = a * HumanoidTables.AxisSign[b, axis];
            }
        }

        float MuscleAngle(int muscle, float[] muscles)
        {
            if (muscle < 0) return 0;
            float v = muscles[muscle];
            return v >= 0 ? v * _rig.MuscleMax[muscle] : -v * _rig.MuscleMin[muscle];
        }

        /// <summary>
        /// The pose Unity plays for a frame with the clip's motion kept on the body: position and world rotation per
        /// humanoid bone, avatar root at the origin, base size.
        /// </summary>
        public void Evaluate(HumanoidFrame frame, Vec3[] pos, Quat[] rot)
        {
            var twists = new float[N];
            _full[HumanBones.Hips] = Quat.Identity;
            foreach (int b in _order)
            {
                if (b == HumanBones.Hips) continue;
                Angles(b, frame.Muscles);
                twists[b] = _angles[0];
                _full[b] = Compose(b, HumanoidMath.FromSwingTwist(_angles[0], _angles[1], _angles[2]));
            }
            Positions(Vec3.Zero);
            // Unity places the hips so that the pose's centre of mass and body rotation are the root curves
            var turn = (frame.RootQ * _rig.BodyRotation(_pos).Conjugate).Normalized;
            var hips = frame.RootT * _rig.HumanScale - turn * _rig.CentreOfMass(_pos);
            foreach (int b in _order)
            {
                pos[b] = hips + turn * _pos[b];
                // a twist-sharing bone itself only turns by its share; its child got the whole twist
                float rest = HumanoidMath.Wrap(twists[b]) * (1f - _rig.TwistShare(b));
                var own = rest != 0 ? _full[b] * _axes[b] * HumanoidMath.FromSwingTwist(-rest, 0, 0) * _axesInv[b] : _full[b];
                rot[b] = (turn * own * _rig.RestRot[b]).Normalized;
            }
        }

        /// <summary>Joint positions of the reconstructed pose (bone offsets are the T-pose's).</summary>
        void Positions(Vec3 hipsPos)
        {
            foreach (int b in _order)
            {
                int p = _rig.Parent[b];
                _pos[b] = p < 0 ? hipsPos : _pos[p] + _full[p] * (_rig.RestPos[b] - _rig.RestPos[p]);
            }
        }

        /// <summary>Root and IK goal curves from the reconstructed pose, as Unity defines them.</summary>
        void WriteRoot(HumanoidFrame frame)
        {
            frame.RootQ = _rig.BodyRotation(_pos);
            frame.RootT = _rig.CentreOfMass(_pos) / _rig.HumanScale;
            var inv = frame.RootQ.Conjugate;
            WriteGoal(frame, HumanoidFrame.LeftFoot, HumanBones.LeftFoot, _rig.LeftFootHeight, inv);
            WriteGoal(frame, HumanoidFrame.RightFoot, HumanBones.RightFoot, _rig.RightFootHeight, inv);
            WriteGoal(frame, HumanoidFrame.LeftHand, HumanBones.LeftHand, 0, inv);
            WriteGoal(frame, HumanoidFrame.RightHand, HumanBones.RightHand, 0, inv);
        }

        /// <summary>A goal sits at the joint (a foot's: the sole below the ankle), turned like the bone's muscle axes.</summary>
        void WriteGoal(HumanoidFrame frame, int goal, int bone, float soleBelowJoint, Quat invRoot)
        {
            var rot = (_full[bone] * _axes[bone]).Normalized;
            var pos = _pos[bone] + rot * new Vec3(soleBelowJoint, 0, 0);
            frame.GoalT[goal] = invRoot * (pos / _rig.HumanScale - frame.RootT);
            frame.GoalQ[goal] = (invRoot * rot).Normalized;
        }
    }

    /// <summary>Unity's swing-twist form of a muscle rotation.</summary>
    internal static class HumanoidMath
    {
        /// <summary>Swing(y, z) * Twist(x): the swing is the quaternion (0, tan(y/2), tan(z/2), 1) normalised.</summary>
        public static Quat FromSwingTwist(float x, float y, float z)
        {
            var swing = new Quat(0, (float)Math.Tan(y * 0.5), (float)Math.Tan(z * 0.5), 1).Normalized;
            return x == 0 ? swing : swing * new Quat((float)Math.Sin(x * 0.5), 0, 0, (float)Math.Cos(x * 0.5));
        }

        /// <summary>The angles of <see cref="FromSwingTwist"/> for a rotation (twist in -pi..pi).</summary>
        public static void SwingTwist(Quat q, out float x, out float y, out float z)
        {
            double h = Math.Atan2(q.x, q.w);
            if (h > Math.PI / 2) h -= Math.PI; else if (h < -Math.PI / 2) h += Math.PI;   // q and -q are the same rotation
            x = (float)(2 * h);
            var swing = q * new Quat(-(float)Math.Sin(h), 0, 0, (float)Math.Cos(h));
            y = 2f * (float)Math.Atan2(swing.y, swing.w);
            z = 2f * (float)Math.Atan2(swing.z, swing.w);
            if (swing.w < 0) { y = 2f * (float)Math.Atan2(-swing.y, -swing.w); z = 2f * (float)Math.Atan2(-swing.z, -swing.w); }
        }

        /// <summary>The angle equal to a (mod 2 pi) that is nearest to reference.</summary>
        public static float Nearest(float a, float reference)
        {
            const float turn = (float)(2 * Math.PI);
            return a - turn * (float)Math.Round((a - reference) / turn);
        }

        /// <summary>An angle wrapped into -pi..pi.</summary>
        public static float Wrap(float a) => Nearest(a, 0);
    }
}
