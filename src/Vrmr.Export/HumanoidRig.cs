using System;

namespace VRMR
{
    /// <summary>
    /// One avatar's Humanoid muscle space, rebuilt from its T-pose the way Unity builds an Avatar: per-bone muscle
    /// axes, twist sharing, masses, human scale. Rotations are "relative to the T-pose in world space"
    /// (N = world rotation * inverse(T-pose world rotation)), which makes them independent of the bones' own axes.
    /// </summary>
    public sealed class HumanoidRig
    {
        const int N = HumanBones.Count;

        /// <summary>Which humanoid bones the avatar has.</summary>
        public readonly bool[] Exists = new bool[N];
        /// <summary>Nearest existing humanoid ancestor of each bone (-1 for the hips).</summary>
        public readonly int[] Parent = new int[N];
        /// <summary>T-pose position of each bone, avatar root at the origin, avatar at its base size.</summary>
        public readonly Vec3[] RestPos = new Vec3[N];
        /// <summary>T-pose world rotation of each bone.</summary>
        public readonly Quat[] RestRot = new Quat[N];
        /// <summary>Muscle axes of each bone in the T-pose (world): X is the twist axis, Y and Z the swing axes.</summary>
        public readonly Quat[] Axes = new Quat[N];
        /// <summary>Angle in radians of each muscle at -1 (negative) and at +1.</summary>
        public readonly float[] MuscleMin = new float[HumanoidTables.MuscleCount], MuscleMax = new float[HumanoidTables.MuscleCount];
        /// <summary>Share of a twist the bone itself takes (the rest turns its child): the Avatar's twist settings.</summary>
        public float UpperArmTwist = 0.5f, LowerArmTwist = 0.5f, UpperLegTwist = 0.5f, LowerLegTwist = 0.5f;
        /// <summary>Height of the centre of mass in the T-pose: Unity divides body positions by it.</summary>
        public float HumanScale { get; private set; }
        /// <summary>T-pose height of each foot joint above the avatar root (where Unity puts the foot IK goal below the ankle).</summary>
        public float LeftFootHeight { get; private set; }
        public float RightFootHeight { get; private set; }

        readonly float[] _mass = new float[N];
        Quat _restBodyFrame = Quat.Identity;

        /// <summary>The muscle space of an avatar skeleton (its rest pose must be the T-pose Unity configured it with).</summary>
        public static HumanoidRig FromProfile(AvatarProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            var rig = new HumanoidRig();
            int n = profile.Nodes.Count;
            var pos = new Vec3[n]; var rot = new Quat[n]; var scl = new Vec3[n];
            foreach (int i in profile.TopoOrder())
            {
                var nd = profile.Nodes[i];
                if (nd.Parent < 0) { pos[i] = Vec3.Zero; rot[i] = Quat.Identity; scl[i] = Vec3.One; continue; }
                int p = nd.Parent;
                pos[i] = pos[p] + rot[p] * Vec3.Scale(scl[p], nd.LocalPos);
                rot[i] = (rot[p] * nd.LocalRot).Normalized;
                scl[i] = Vec3.Scale(scl[p], nd.LocalScale);
            }
            for (int b = 0; b < N; b++)
            {
                rig.Exists[b] = profile.Has(b);
                if (!rig.Exists[b]) continue;
                rig.RestPos[b] = pos[profile.Human[b]];
                rig.RestRot[b] = rot[profile.Human[b]];
            }
            rig.Build();
            return rig;
        }

        /// <summary>A rig from T-pose world positions and rotations given per humanoid bone (for tests and tools).</summary>
        public static HumanoidRig FromRestPose(bool[] exists, Vec3[] restPos, Quat[] restRot)
        {
            var rig = new HumanoidRig();
            Array.Copy(exists, rig.Exists, N); Array.Copy(restPos, rig.RestPos, N); Array.Copy(restRot, rig.RestRot, N);
            rig.Build();
            return rig;
        }

        void Build()
        {
            if (!Exists[HumanBones.Hips]) throw new ArgumentException("The avatar has no hips bone.");
            for (int b = 0; b < N; b++) Parent[b] = Exists[b] ? HumanBones.ResolveParent(b, Exists) : -1;
            for (int m = 0; m < HumanoidTables.MuscleCount; m++)
            {
                MuscleMin[m] = HumanoidTables.MuscleMin[m] * MathX.Deg2Rad;
                MuscleMax[m] = HumanoidTables.MuscleMax[m] * MathX.Deg2Rad;
            }
            for (int b = 0; b < N; b++) if (Exists[b]) Axes[b] = BuildAxes(b);
            // a missing bone's mass belongs to its nearest existing ancestor
            for (int b = 0; b < N; b++)
            {
                float m = HumanoidTables.Mass[b] / HumanoidTables.TotalMass;
                if (m <= 0) continue;
                int host = b;
                while (host >= 0 && !Exists[host]) host = HumanBones.CanonicalParent[host];
                if (host >= 0) _mass[host] += m;
            }
            _restBodyFrame = BodyFrame(RestPos);
            HumanScale = Math.Max(1e-4f, CentreOfMass(RestPos).y);
            LeftFootHeight = Exists[HumanBones.LeftFoot] ? RestPos[HumanBones.LeftFoot].y : 0;
            RightFootHeight = Exists[HumanBones.RightFoot] ? RestPos[HumanBones.RightFoot].y : 0;
        }

        // ------------------------------------------------------------------ axes

        /// <summary>True for bones whose muscle axes do not depend on the skeleton's shape.</summary>
        static bool HasFixedAxes(int b) =>
            b == HumanBones.Hips || b == HumanBones.Head || b == HumanBones.LeftFoot || b == HumanBones.RightFoot ||
            b == HumanBones.LeftToes || b == HumanBones.RightToes || b == HumanBones.LeftEye || b == HumanBones.RightEye || b == HumanBones.Jaw;

        /// <summary>The bone pair whose T-pose direction is the twist axis of bone b (from, to), or false when it has none.</summary>
        bool Direction(int b, out int from, out int to)
        {
            from = b; to = -1;
            if (b == HumanBones.LeftHand || b == HumanBones.RightHand) from = b - 2;               // the hand uses the forearm's direction
            else if (HumanBones.IsFinger(b) && (b - HumanBones.LeftThumbProximal) % 3 == 2) from = b - 1; // a distal uses its intermediate's
            if (!Exists[from]) return false;
            to = NextInChain(from);
            return to >= 0;
        }

        /// <summary>The humanoid bone that continues bone b's segment (the next existing one along the chain), or -1.</summary>
        public int NextInChain(int b)
        {
            int[] chain;
            switch (b)
            {
                case HumanBones.Spine: chain = new[] { HumanBones.Chest, HumanBones.UpperChest, HumanBones.Neck, HumanBones.Head }; break;
                case HumanBones.Chest: chain = new[] { HumanBones.UpperChest, HumanBones.Neck, HumanBones.Head }; break;
                case HumanBones.UpperChest: chain = new[] { HumanBones.Neck, HumanBones.Head }; break;
                case HumanBones.Neck: chain = new[] { HumanBones.Head }; break;
                case HumanBones.LeftShoulder: case HumanBones.RightShoulder:
                case HumanBones.LeftUpperArm: case HumanBones.RightUpperArm:
                case HumanBones.LeftLowerArm: case HumanBones.RightLowerArm:
                case HumanBones.LeftUpperLeg: case HumanBones.RightUpperLeg:
                case HumanBones.LeftLowerLeg: case HumanBones.RightLowerLeg:
                    chain = new[] { b + 2 }; break;
                default:
                    if (HumanBones.IsFinger(b) && (b - HumanBones.LeftThumbProximal) % 3 < 2) chain = new[] { b + 1 };
                    else return -1;
                    break;
            }
            foreach (int c in chain) if (Exists[c]) return c;
            return -1;
        }

        Quat BuildAxes(int b)
        {
            var canonical = HumanoidTables.CanonicalAxes[b];
            if (HasFixedAxes(b) || !Direction(b, out int from, out int to)) return canonical;
            var x = (RestPos[to] - RestPos[from]).Normalized;
            if (x.SqrLength < 0.5f) return canonical;
            var zRef = canonical * Vec3.Forward;
            var z = zRef - x * Vec3.Dot(zRef, x);
            if (z.SqrLength < 1e-10f) return canonical;
            z = z.Normalized;
            return Quat.FromBasis(x, Vec3.Cross(z, x), z).Normalized;
        }

        /// <summary>Share of bone b's twist that the bone itself takes; 1 for bones that do not share their twist.</summary>
        public float TwistShare(int b)
        {
            switch (b)
            {
                case HumanBones.LeftUpperArm: case HumanBones.RightUpperArm: return UpperArmTwist;
                case HumanBones.LeftLowerArm: case HumanBones.RightLowerArm: return LowerArmTwist;
                case HumanBones.LeftUpperLeg: case HumanBones.RightUpperLeg: return UpperLegTwist;
                case HumanBones.LeftLowerLeg: case HumanBones.RightLowerLeg: return LowerLegTwist;
                default: return 1f;
            }
        }

        // ------------------------------------------------------------------ root

        /// <summary>Unity's centre of mass for joint positions given per humanoid bone (missing bones are ignored).</summary>
        public Vec3 CentreOfMass(Vec3[] p)
        {
            Vec3 sum = Vec3.Zero;
            int lastSpine = Exists[HumanBones.UpperChest] ? HumanBones.UpperChest : Exists[HumanBones.Chest] ? HumanBones.Chest : HumanBones.Spine;
            for (int b = 0; b < N; b++)
            {
                float m = _mass[b];
                if (m <= 0) continue;
                Vec3 c = p[b];
                int next = NextInChain(b);
                if (b == HumanBones.Hips) c = (p[HumanBones.Spine] + p[HumanBones.LeftUpperLeg] + p[HumanBones.RightUpperLeg]) / 3f;
                else if (b == lastSpine)
                {
                    int neck = Exists[HumanBones.Neck] ? HumanBones.Neck : HumanBones.Head;
                    int ls = Exists[HumanBones.LeftShoulder] ? HumanBones.LeftShoulder : HumanBones.LeftUpperArm;
                    int rs = Exists[HumanBones.RightShoulder] ? HumanBones.RightShoulder : HumanBones.RightUpperArm;
                    c = (p[b] + p[neck] + p[ls] + p[rs]) / 4f;
                }
                else if (b == HumanBones.LeftUpperArm) c = p[b] + (p[HumanBones.LeftLowerArm] - p[b]) / 8f; // as measured: not the midpoint
                else if (next >= 0 && !HumanBones.IsFinger(b)) c = (p[b] + p[next]) * 0.5f;
                sum += c * m;
            }
            return sum;
        }

        /// <summary>The body orientation Unity derives from the upper leg and upper arm joints (not yet relative to the T-pose).</summary>
        public static Quat BodyFrame(Vec3[] p)
        {
            var ll = p[HumanBones.LeftUpperLeg]; var rl = p[HumanBones.RightUpperLeg];
            var la = p[HumanBones.LeftUpperArm]; var ra = p[HumanBones.RightUpperArm];
            var up = ((la + ra) - (ll + rl)).Normalized;
            var forward = Vec3.Cross((rl - ll) + (ra - la), up).Normalized;
            return Quat.FromBasis(Vec3.Cross(up, forward), up, forward).Normalized;
        }

        /// <summary>Unity's body rotation (RootQ) for joint positions: the body frame relative to the T-pose's.</summary>
        public Quat BodyRotation(Vec3[] p) => (BodyFrame(p) * _restBodyFrame.Conjugate).Normalized;
    }
}
