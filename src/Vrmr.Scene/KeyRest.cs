using System;
using System.Collections.Generic;
using System.Linq;

namespace VRMR
{
    /// <summary>
    /// Device poses that hold an avatar in a given pose under the VRChat-matched solve: the headset at the eyes (moved
    /// forward by VRChat's eye-behind-headset offset), Index controllers holding the hands the way VRChat does
    /// (<see cref="FbikAdapter.VrcIndexHand"/> inverted), body trackers on their bones (bound at a T-pose with the bone's
    /// rotation from rest, so a tracker at (joint, Δbone) gives back the bone). Used for keyframe-only takes (devices
    /// keyed from a standing pose) and for "key the devices from this pose". Solve space (avatar-scaled, floor at y = 0).
    /// </summary>
    public static class KeyRest
    {
        public static readonly BodyRole[] ThreePoint = { BodyRole.Head, BodyRole.LeftHand, BodyRole.RightHand };
        public static readonly BodyRole[] SixPoint = { BodyRole.Head, BodyRole.LeftHand, BodyRole.RightHand, BodyRole.Hips, BodyRole.LeftFoot, BodyRole.RightFoot };
        public static readonly BodyRole[] FullBody = (BodyRole[])Enum.GetValues(typeof(BodyRole));

        public static readonly (string Name, BodyRole[] Roles)[] Sets =
        {
            ("3 points: headset + controllers", ThreePoint),
            ("6 points: + hips, feet", SixPoint),
            ("Full body: + chest, knees, elbows", FullBody),
        };

        /// <summary>The bone a device role sits on.</summary>
        public static int BoneOf(BodyRole r)
        {
            switch (r)
            {
                case BodyRole.Head: return HumanBones.Head;
                case BodyRole.LeftHand: return HumanBones.LeftHand;
                case BodyRole.RightHand: return HumanBones.RightHand;
                case BodyRole.Hips: return HumanBones.Hips;
                case BodyRole.Chest: return HumanBones.Chest;
                case BodyRole.LeftFoot: return HumanBones.LeftFoot;
                case BodyRole.RightFoot: return HumanBones.RightFoot;
                case BodyRole.LeftKnee: return HumanBones.LeftLowerLeg;
                case BodyRole.RightKnee: return HumanBones.RightLowerLeg;
                case BodyRole.LeftElbow: return HumanBones.LeftLowerArm;
                default: return HumanBones.RightLowerArm;
            }
        }

        /// <summary>The device role on a bone (hands, feet, hips, chest, head; knees / elbows for the shin / forearm), or null.</summary>
        public static BodyRole? RoleOn(int bone)
        {
            foreach (BodyRole r in FullBody) if (BoneOf(r) == bone) return r;
            if (bone == HumanBones.UpperChest || bone == HumanBones.Spine) return BodyRole.Chest;
            if (bone == HumanBones.Neck) return BodyRole.Head;
            return null;
        }

        /// <summary>The avatar's rest pose (a T-pose for VRChat avatars): what devices are calibrated on.</summary>
        public static void TPose(PoseRig rig, float scale, ScenePose o) => o.SetRest(rig, scale);

        /// <summary>A relaxed standing pose: the T-pose with the arms down along the body, elbows a little bent.</summary>
        public static void Standing(PoseRig rig, float scale, ScenePose o)
        {
            o.SetRest(rig, scale);
            var ik = new PoseIK(rig, scale);
            ik.Fk(o);
            foreach (bool left in new[] { true, false })
            {
                int ua = left ? HumanBones.LeftUpperArm : HumanBones.RightUpperArm, la = left ? HumanBones.LeftLowerArm : HumanBones.RightLowerArm;
                int hand = left ? HumanBones.LeftHand : HumanBones.RightHand;
                if (!rig.Exists[ua] || !rig.Exists[la] || !rig.Exists[hand]) continue;
                ik.Fk(o);
                // upper arm down to ~12° from vertical (slightly out, slightly forward)
                var dir = (ik.Pos[la] - ik.Pos[ua]).Normalized;
                var want = new Vec3(left ? -0.2f : 0.2f, -1f, 0.05f).Normalized;
                ik.Rotate(o, ua, Quat.FromToRotation(dir, want));
                // forearm bent ~15° forward (about the body's left-right axis)
                ik.Fk(o);
                var fdir = (ik.Pos[hand] - ik.Pos[la]).Normalized;
                var fwant = (want + new Vec3(0, 0, 0.27f)).Normalized;
                ik.Rotate(o, la, Quat.FromToRotation(fdir, fwant));
                // hands: palms facing the thighs (turned about the forearm)
                ik.Fk(o);
                var axis = (ik.Pos[hand] - ik.Pos[la]).Normalized;
                ik.Rotate(o, hand, Quat.AngleAxis(left ? -70f : 70f, axis));
            }
        }

        static float AvatarEye(PoseRig rig, float scale) => Math.Max(0.1f, rig.Profile.EyeHeight * scale);

        /// <summary>Index controller pose that gives this hand bone pose under VRChat's measured hand offset.</summary>
        public static Pose ControllerFor(bool left, Pose hand, Quat restHand, float avatarEye)
        {
            var hp = left ? FbikAdapter.VrcIndexHandPosLeft : new Vec3(-FbikAdapter.VrcIndexHandPosLeft.x, FbikAdapter.VrcIndexHandPosLeft.y, FbikAdapter.VrcIndexHandPosLeft.z);
            var hr = left ? FbikAdapter.VrcIndexHandRotLeft : new Quat(FbikAdapter.VrcIndexHandRotLeft.x, -FbikAdapter.VrcIndexHandRotLeft.y, -FbikAdapter.VrcIndexHandRotLeft.z, FbikAdapter.VrcIndexHandRotLeft.w);
            // hand.rot = c.rot * hr * restHand  ->  c.rot = hand.rot * restHand⁻¹ * hr⁻¹
            var crot = (hand.rot * restHand.Conjugate * hr.Conjugate).Normalized;
            var cpos = hand.pos - crot * (hp * (avatarEye / FbikAdapter.VrcHandRefEye));
            return new Pose(cpos, crot);
        }

        /// <summary>The headset pose for a head pose (eye point from the avatar's rest view point; VRChat's offset in front of the eyes).</summary>
        public static Pose HeadsetFor(PoseRig rig, float scale, Vec3 headPos, Quat headWorld)
        {
            var d = (headWorld * rig.RestWorld[HumanBones.Head].Conjugate).Normalized;
            var eye = headPos + d * ((rig.Profile.ViewPoint - rig.Profile.HumanRestPos(HumanBones.Head)) * scale);
            float k = AvatarEye(rig, scale) / FbikAdapter.VrcHandRefEye;
            return new Pose(eye + d * new Vec3(0, 0, TrackingRig.VrcEyeBehindHeadset * k), d);
        }

        /// <summary>Device poses that hold the avatar in this pose (solve space).</summary>
        public static Dictionary<BodyRole, Pose> DevicesFor(PoseRig rig, float scale, ScenePose pose, IEnumerable<BodyRole> roles = null)
        {
            var res = new Dictionary<BodyRole, Pose>();
            var pos = new Vec3[HumanBones.Count]; var w = new Quat[HumanBones.Count];
            rig.Fk(pose.Hips, pose.Local, scale, pos, w);
            float eye = AvatarEye(rig, scale);
            foreach (var r in roles ?? FullBody)
            {
                int b = BoneOf(r);
                if (b == HumanBones.Chest && !rig.Exists[b]) b = rig.Exists[HumanBones.UpperChest] ? HumanBones.UpperChest : HumanBones.Spine;
                if (!rig.Exists[b]) continue;
                if (r == BodyRole.Head) res[r] = HeadsetFor(rig, scale, pos[b], w[b]);
                else if (r == BodyRole.LeftHand || r == BodyRole.RightHand) res[r] = ControllerFor(r == BodyRole.LeftHand, new Pose(pos[b], w[b]), rig.RestWorld[b], eye);
                else
                {
                    // a tracker on the joint, turned with the bone (knees / elbows: a little way down the shin / forearm)
                    var p = pos[b];
                    if (r == BodyRole.LeftKnee || r == BodyRole.RightKnee || r == BodyRole.LeftElbow || r == BodyRole.RightElbow)
                    {
                        int child = rig.Order.FirstOrDefault(c => rig.Parent[c] == b && c != b);
                        if (child > 0) p = Vec3.Lerp(pos[b], pos[child], 0.15f);
                    }
                    res[r] = new Pose(p, (w[b] * rig.RestWorld[b].Conjugate).Normalized);
                }
            }
            return res;
        }
    }
}
