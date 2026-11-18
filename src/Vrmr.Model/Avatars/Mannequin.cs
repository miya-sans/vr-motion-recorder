using System;
using System.Collections.Generic;

namespace VRMR
{
    /// <summary>
    ///     The built-in avatar: a plain humanoid generated in code, so the app has a body to solve onto before the user
    ///     has added an avatar of their own. Adult proportions, a clean T-pose (every bone's rest rotation is identity),
    ///     eyes, three bones per finger, toes. Nothing in it comes from a file or from another model.
    /// </summary>
    public static class Mannequin
    {
        /// <summary>The library id of the built-in avatar.</summary>
        public const string Id = "mannequin";

        /// <summary>The built-in avatar's name.</summary>
        public const string Name = "Mannequin";

        /// <summary>What a mannequin profile's <see cref="AvatarProfile.SourcePath" /> says instead of a file.</summary>
        public const string Source = "builtin:mannequin";

        /// <summary>Standing height of the default mannequin, metres.</summary>
        public const float DefaultHeight = 1.70f;

        /// <summary>Eye height as a fraction of the standing height.</summary>
        public const float EyeHeightRatio = 1.59f / 1.70f;

        /// <summary>Thumb, index, middle, ring, little (right hand, metres at the default height).</summary>
        static readonly Finger[] Fingers =
        {
            new()
            {
                RootX = 0.025f, RootY = -0.012f, RootZ = 0.022f, Direction = new Vec3(0.62f, -0.12f, 0.775f).Normalized,
                Lengths = new[] { 0.040f, 0.032f, 0.026f }
            },
            new()
            {
                RootX = 0.088f, RootY = 0, RootZ = 0.028f, Direction = Vec3.Right,
                Lengths = new[] { 0.040f, 0.025f, 0.021f }
            },
            new()
            {
                RootX = 0.090f, RootY = 0, RootZ = 0.006f, Direction = Vec3.Right,
                Lengths = new[] { 0.045f, 0.028f, 0.022f }
            },
            new()
            {
                RootX = 0.084f, RootY = 0, RootZ = -0.014f, Direction = Vec3.Right,
                Lengths = new[] { 0.041f, 0.026f, 0.021f }
            },
            new()
            {
                RootX = 0.075f, RootY = 0, RootZ = -0.032f, Direction = Vec3.Right,
                Lengths = new[] { 0.032f, 0.019f, 0.018f }
            }
        };

        /// <summary>Whether a profile is the built-in avatar (at any size).</summary>
        public static bool Is(AvatarProfile profile)
        {
            return profile != null && profile.SourcePath == Source;
        }

        /// <summary>A new mannequin skeleton of the given standing height.</summary>
        public static AvatarProfile Profile(float height = DefaultHeight)
        {
            float s = Math.Max(0.2f, height) / DefaultHeight;
            var p = new AvatarProfile { Name = Name, SourcePath = Source, LibraryId = Id };
            var world = new List<Vec3>();

            // every joint is given where it is in the T-pose (metres at the default height, +X the avatar's right, +Z forward)
            int Add(string name, int parent, float x, float y, float z, int human = -1)
            {
                var w = new Vec3(x, y, z) * s;
                p.Nodes.Add(new AvatarNode
                    { Name = name, Parent = parent, LocalPos = parent >= 0 ? w - world[parent] : Vec3.Zero });
                world.Add(parent >= 0 ? w : Vec3.Zero);
                if (human >= 0) p.Human[human] = p.Nodes.Count - 1;
                return p.Nodes.Count - 1;
            }

            int root = Add(Name, -1, 0, 0, 0);
            int armature = Add("Armature", root, 0, 0, 0);
            int hips = Add("Hips", armature, 0, 0.925f, 0, HumanBones.Hips);
            int spine = Add("Spine", hips, 0, 1.005f, 0, HumanBones.Spine);
            int chest = Add("Chest", spine, 0, 1.125f, 0, HumanBones.Chest);
            int upperChest = Add("UpperChest", chest, 0, 1.255f, 0, HumanBones.UpperChest);
            int neck = Add("Neck", upperChest, 0, 1.425f, 0, HumanBones.Neck);
            int head = Add("Head", neck, 0, 1.505f, 0.01f, HumanBones.Head);
            Add("LeftEye", head, -0.031f, 1.59f, 0.085f, HumanBones.LeftEye);
            Add("RightEye", head, 0.031f, 1.59f, 0.085f, HumanBones.RightEye);

            foreach (bool left in new[] { true, false })
            {
                float m = left ? -1 : 1;
                string side = left ? "Left" : "Right";

                int H(int leftBone)
                {
                    return left ? leftBone : HumanBones.Mirror(leftBone);
                }

                int shoulder = Add(side + "Shoulder", upperChest, m * 0.025f, 1.39f, 0, H(HumanBones.LeftShoulder));
                int upperArm = Add(side + "UpperArm", shoulder, m * 0.175f, 1.385f, 0, H(HumanBones.LeftUpperArm));
                int lowerArm = Add(side + "LowerArm", upperArm, m * 0.45f, 1.385f, 0, H(HumanBones.LeftLowerArm));
                int hand = Add(side + "Hand", lowerArm, m * 0.695f, 1.385f, 0, H(HumanBones.LeftHand));

                // fingers: in the T-pose the palms face down and the thumbs point forward
                for (int f = 0; f < 5; f++)
                {
                    var finger = Fingers[f];
                    var at = new Vec3(0.695f + finger.RootX, 1.385f + finger.RootY, finger.RootZ);
                    int parent = hand, bone = HumanBones.LeftThumbProximal + f * 3;
                    for (int k = 0; k < 3; k++)
                    {
                        parent = Add(HumanBones.Names[H(bone + k)], parent, m * at.x, at.y, at.z, H(bone + k));
                        at = at + finger.Direction * finger.Lengths[k];
                    }
                }

                int upperLeg = Add(side + "UpperLeg", hips, m * 0.085f, 0.885f, 0, H(HumanBones.LeftUpperLeg));
                int lowerLeg = Add(side + "LowerLeg", upperLeg, m * 0.085f, 0.48f, 0, H(HumanBones.LeftLowerLeg));
                int foot = Add(side + "Foot", lowerLeg, m * 0.085f, 0.085f, -0.015f, H(HumanBones.LeftFoot));
                Add(side + "Toes", foot, m * 0.085f, 0.02f, 0.115f, H(HumanBones.LeftToes));
            }

            p.Finish();
            return p;
        }

        /// <summary>
        ///     One finger of the right hand: where it starts relative to the wrist, which way it points, its three bone
        ///     lengths.
        /// </summary>
        sealed class Finger
        {
            public Vec3 Direction;
            public float[] Lengths;
            public float RootX, RootY, RootZ;
        }
    }
}
