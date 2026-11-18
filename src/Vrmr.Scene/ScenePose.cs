using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace VRMR
{
    /// <summary>
    /// What the scene evaluator needs to know about an avatar skeleton: each humanoid bone's nearest existing humanoid
    /// parent, its rest rotation relative to that parent, and its rest offset in the parent's frame. FK with these gives
    /// the same humanoid bone positions as BakedClip.EvaluateNodes.
    /// </summary>
    public sealed class PoseRig
    {
        public readonly AvatarProfile Profile;
        public readonly bool[] Exists;
        /// <summary>Nearest existing humanoid parent (-1 for the hips).</summary>
        public readonly int[] Parent;
        /// <summary>Existing bones, parents before children.</summary>
        public readonly int[] Order;
        public readonly Quat[] RestLocal;
        /// <summary>Rest position relative to the parent bone, in the parent's rest frame, at avatar scale 1.</summary>
        public readonly Vec3[] RestOffset;
        public readonly Quat[] RestWorld;
        /// <summary>Hips rest position at scale 1 (root at the origin).</summary>
        public readonly Vec3 RestHips;

        static readonly ConditionalWeakTable<AvatarProfile, PoseRig> Cache = new ConditionalWeakTable<AvatarProfile, PoseRig>();
        public static PoseRig For(AvatarProfile p) => Cache.GetValue(p, x => new PoseRig(x));

        PoseRig(AvatarProfile p)
        {
            Profile = p;
            if (p.RestWorldPos == null) p.Finish();
            Exists = p.HumanExists;
            Parent = new int[HumanBones.Count];
            RestLocal = new Quat[HumanBones.Count];
            RestOffset = new Vec3[HumanBones.Count];
            RestWorld = new Quat[HumanBones.Count];
            var order = new List<int>();
            for (int b = 0; b < HumanBones.Count; b++)
            {
                RestWorld[b] = p.HumanRestRot(b);
                Parent[b] = Exists[b] ? HumanBones.ResolveParent(b, Exists) : -1;
            }
            // parents before children: depth order
            int Depth(int b) { int d = 0; for (int x = Parent[b]; x >= 0; x = Parent[x]) d++; return d; }
            Order = Enumerable.Range(0, HumanBones.Count).Where(b => Exists[b]).OrderBy(Depth).ToArray();
            foreach (int b in Order)
            {
                int pa = Parent[b];
                if (pa < 0) { RestLocal[b] = RestWorld[b]; RestOffset[b] = p.HumanRestPos(b); continue; }
                var ir = RestWorld[pa].Conjugate;
                RestLocal[b] = (ir * RestWorld[b]).Normalized;
                RestOffset[b] = ir * (p.HumanRestPos(b) - p.HumanRestPos(pa));
            }
            RestHips = Exists[HumanBones.Hips] ? p.HumanRestPos(HumanBones.Hips) : Vec3.Zero;
        }

        /// <summary>Local rotations from world rotations (BakedClip.Rot layout).</summary>
        public void ToLocal(Quat[] world, Quat[] local)
        {
            foreach (int b in Order)
            {
                int pa = Parent[b];
                local[b] = pa < 0 ? world[b] : (world[pa].Conjugate * world[b]).Normalized;
            }
        }

        /// <summary>World rotations from local rotations; bones that don't exist get their parent's rotation times rest.</summary>
        public void ToWorld(Quat[] local, Quat[] world)
        {
            foreach (int b in Order)
            {
                int pa = Parent[b];
                world[b] = pa < 0 ? local[b] : (world[pa] * local[b]).Normalized;
            }
            for (int b = 0; b < HumanBones.Count; b++) if (!Exists[b]) world[b] = Quat.Identity;
        }

        /// <summary>Forward kinematics: world position and rotation of every existing humanoid bone.</summary>
        public void Fk(Vec3 hips, Quat[] local, float scale, Vec3[] pos, Quat[] world)
        {
            foreach (int b in Order)
            {
                int pa = Parent[b];
                if (pa < 0) { world[b] = local[b]; pos[b] = hips; continue; }
                world[b] = (world[pa] * local[b]).Normalized;
                pos[b] = pos[pa] + world[pa] * (RestOffset[b] * scale);
            }
        }

        // ---------------------------------------------------------------- centre of mass, facing

        // segment masses (share of body mass, de Leva 1996), from a bone to its child joint
        static readonly (int Bone, int End, float Mass, float At)[] Segments =
        {
            (HumanBones.Hips, HumanBones.Spine, 0.1117f, 0.5f),
            (HumanBones.Spine, HumanBones.Chest, 0.1633f, 0.5f),
            (HumanBones.Chest, HumanBones.Neck, 0.1596f, 0.5f),
            (HumanBones.Head, -1, 0.0694f, 0.6f),
            (HumanBones.LeftUpperArm, HumanBones.LeftLowerArm, 0.0271f, 0.45f), (HumanBones.RightUpperArm, HumanBones.RightLowerArm, 0.0271f, 0.45f),
            (HumanBones.LeftLowerArm, HumanBones.LeftHand, 0.0162f, 0.45f), (HumanBones.RightLowerArm, HumanBones.RightHand, 0.0162f, 0.45f),
            (HumanBones.LeftHand, -1, 0.0061f, 0.4f), (HumanBones.RightHand, -1, 0.0061f, 0.4f),
            (HumanBones.LeftUpperLeg, HumanBones.LeftLowerLeg, 0.1416f, 0.45f), (HumanBones.RightUpperLeg, HumanBones.RightLowerLeg, 0.1416f, 0.45f),
            (HumanBones.LeftLowerLeg, HumanBones.LeftFoot, 0.0433f, 0.45f), (HumanBones.RightLowerLeg, HumanBones.RightFoot, 0.0433f, 0.45f),
            (HumanBones.LeftFoot, HumanBones.LeftToes, 0.0137f, 0.5f), (HumanBones.RightFoot, HumanBones.RightToes, 0.0137f, 0.5f),
        };

        /// <summary>Whole-body centre of mass from FK positions (segment masses; missing bones fall back to their parent).</summary>
        public Vec3 CenterOfMass(Vec3[] pos)
        {
            Vec3 sum = Vec3.Zero; float m = 0;
            foreach (var s in Segments)
            {
                int a = Resolve(s.Bone); if (a < 0) continue;
                Vec3 pa = pos[a], pb;
                if (s.End >= 0 && Resolve(s.End) >= 0 && Resolve(s.End) != a) pb = pos[Resolve(s.End)];
                else
                {
                    // end segment (head, hand): extend along the bone from its parent
                    int pp = Parent[a];
                    pb = pp >= 0 ? pa + (pa - pos[pp]) * 0.8f : pa;
                }
                sum += Vec3.Lerp(pa, pb, s.At) * s.Mass; m += s.Mass;
            }
            return m > 0 ? sum / m : pos[HumanBones.Hips];
        }

        int Resolve(int b) { while (b >= 0 && !Exists[b]) b = HumanBones.CanonicalParent[b]; return b; }

        /// <summary>Facing (degrees about +Y) of a pose: the hips' turn from the rest pose applied to the avatar's forward (+Z).</summary>
        public float Facing(Quat hipsWorld)
        {
            var f = (hipsWorld * RestWorld[HumanBones.Hips].Conjugate) * Vec3.Forward;
            f = new Vec3(f.x, 0, f.z);
            if (f.SqrLength < 1e-8f) return 0;
            return (float)(Math.Atan2(f.x, f.z) * MathX.Rad2Deg);
        }
    }

    /// <summary>A single pose in scene space: hips position + local rotations (Hips = world), face shapes, raw parameters.</summary>
    public sealed class ScenePose
    {
        public Vec3 Hips;
        public readonly Quat[] Local = new Quat[HumanBones.Count];
        public readonly Dictionary<string, float> Face = new Dictionary<string, float>();
        public readonly Dictionary<string, float> Params = new Dictionary<string, float>();

        public ScenePose() { for (int b = 0; b < HumanBones.Count; b++) Local[b] = Quat.Identity; }

        public void SetRest(PoseRig rig, float scale)
        {
            Hips = rig.RestHips * scale;
            for (int b = 0; b < HumanBones.Count; b++) Local[b] = rig.RestLocal[b].Norm > 0.5f ? rig.RestLocal[b] : Quat.Identity;
            Face.Clear(); Params.Clear();
        }

        public void CopyFrom(ScenePose o)
        {
            Hips = o.Hips;
            Array.Copy(o.Local, Local, Local.Length);
            Face.Clear(); foreach (var kv in o.Face) Face[kv.Key] = kv.Value;
            Params.Clear(); foreach (var kv in o.Params) Params[kv.Key] = kv.Value;
        }

        /// <summary>Moves the whole pose by a root transform (about the scene origin).</summary>
        public void Transform(Pose t)
        {
            Hips = t.TransformPoint(Hips);
            Local[HumanBones.Hips] = (t.rot * Local[HumanBones.Hips]).Normalized;
        }

        readonly Vec3[] _pos = new Vec3[HumanBones.Count];
        readonly Quat[] _world = new Quat[HumanBones.Count];
        /// <summary>FK (cached arrays, valid until the next call).</summary>
        public Vec3[] Positions(PoseRig rig, float scale, out Quat[] world) { rig.Fk(Hips, Local, scale, _pos, _world); world = _world; return _pos; }
        public Vec3 CenterOfMass(PoseRig rig, float scale) => rig.CenterOfMass(Positions(rig, scale, out _));
    }
}
