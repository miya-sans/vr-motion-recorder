using System;
using System.Collections.Generic;

namespace VRMR
{
    /// <summary>Unity HumanBodyBones ordering (identical indices), plus helpers.</summary>
    public static class HumanBones
    {
        public const int Count = 55;
        public const int Hips = 0, LeftUpperLeg = 1, RightUpperLeg = 2, LeftLowerLeg = 3, RightLowerLeg = 4, LeftFoot = 5, RightFoot = 6,
            Spine = 7, Chest = 8, Neck = 9, Head = 10, LeftShoulder = 11, RightShoulder = 12, LeftUpperArm = 13, RightUpperArm = 14,
            LeftLowerArm = 15, RightLowerArm = 16, LeftHand = 17, RightHand = 18, LeftToes = 19, RightToes = 20, LeftEye = 21, RightEye = 22,
            Jaw = 23, LeftThumbProximal = 24, LeftIndexProximal = 27, LeftMiddleProximal = 30, LeftRingProximal = 33, LeftLittleProximal = 36,
            RightThumbProximal = 39, RightIndexProximal = 42, RightMiddleProximal = 45, RightRingProximal = 48, RightLittleProximal = 51,
            UpperChest = 54;

        /// <summary>Enum names exactly as UnityEngine.HumanBodyBones.</summary>
        public static readonly string[] Names =
        {
            "Hips","LeftUpperLeg","RightUpperLeg","LeftLowerLeg","RightLowerLeg","LeftFoot","RightFoot","Spine","Chest","Neck","Head",
            "LeftShoulder","RightShoulder","LeftUpperArm","RightUpperArm","LeftLowerArm","RightLowerArm","LeftHand","RightHand",
            "LeftToes","RightToes","LeftEye","RightEye","Jaw",
            "LeftThumbProximal","LeftThumbIntermediate","LeftThumbDistal","LeftIndexProximal","LeftIndexIntermediate","LeftIndexDistal",
            "LeftMiddleProximal","LeftMiddleIntermediate","LeftMiddleDistal","LeftRingProximal","LeftRingIntermediate","LeftRingDistal",
            "LeftLittleProximal","LeftLittleIntermediate","LeftLittleDistal",
            "RightThumbProximal","RightThumbIntermediate","RightThumbDistal","RightIndexProximal","RightIndexIntermediate","RightIndexDistal",
            "RightMiddleProximal","RightMiddleIntermediate","RightMiddleDistal","RightRingProximal","RightRingIntermediate","RightRingDistal",
            "RightLittleProximal","RightLittleIntermediate","RightLittleDistal","UpperChest"
        };

        /// <summary>Canonical humanoid parent (ignoring optional bones; resolve with ResolveParent).</summary>
        public static readonly int[] CanonicalParent = BuildParents();

        static int[] BuildParents()
        {
            var p = new int[Count];
            for (int i = 0; i < Count; i++) p[i] = -1;
            p[LeftUpperLeg] = Hips; p[RightUpperLeg] = Hips;
            p[LeftLowerLeg] = LeftUpperLeg; p[RightLowerLeg] = RightUpperLeg;
            p[LeftFoot] = LeftLowerLeg; p[RightFoot] = RightLowerLeg;
            p[LeftToes] = LeftFoot; p[RightToes] = RightFoot;
            p[Spine] = Hips; p[Chest] = Spine; p[UpperChest] = Chest; p[Neck] = UpperChest; p[Head] = Neck;
            p[LeftShoulder] = UpperChest; p[RightShoulder] = UpperChest;
            p[LeftUpperArm] = LeftShoulder; p[RightUpperArm] = RightShoulder;
            p[LeftLowerArm] = LeftUpperArm; p[RightLowerArm] = RightUpperArm;
            p[LeftHand] = LeftLowerArm; p[RightHand] = RightLowerArm;
            p[LeftEye] = Head; p[RightEye] = Head; p[Jaw] = Head;
            for (int f = 0; f < 5; f++)
            {
                int l = 24 + f * 3, r = 39 + f * 3;
                p[l] = LeftHand; p[l + 1] = l; p[l + 2] = l + 1;
                p[r] = RightHand; p[r + 1] = r; p[r + 2] = r + 1;
            }
            return p;
        }

        /// <summary>Nearest existing ancestor using the canonical humanoid chain.</summary>
        public static int ResolveParent(int bone, bool[] exists)
        {
            int p = CanonicalParent[bone];
            while (p >= 0 && !exists[p]) p = CanonicalParent[p];
            return p;
        }

        static Dictionary<string, int> _lookup;
        /// <summary>Accepts enum names ("LeftThumbProximal") and Unity HumanTrait names ("Left Thumb Proximal", "UpperChest").</summary>
        public static int FromName(string name)
        {
            if (_lookup == null)
            {
                var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < Count; i++) d[Names[i]] = i;
                _lookup = d;
            }
            if (name == null) return -1;
            return _lookup.TryGetValue(name.Replace(" ", ""), out int v) ? v : -1;
        }

        /// <summary>Unity's HumanTrait bone name as written in a model's humanDescription ("Left Thumb Proximal" for fingers).</summary>
        public static string UnityName(int b)
        {
            if (!IsFinger(b)) return Names[b];
            string n = Names[b];
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < n.Length; i++) { if (i > 0 && char.IsUpper(n[i])) sb.Append(' '); sb.Append(n[i]); }
            return sb.ToString();
        }

        public static bool IsLeft(int b) => Names[b].StartsWith("Left", StringComparison.Ordinal);
        public static bool IsFinger(int b) => b >= 24 && b <= 53;

        public static int Mirror(int b)
        {
            string n = Names[b];
            if (n.StartsWith("Left", StringComparison.Ordinal)) return FromName("Right" + n.Substring(4));
            if (n.StartsWith("Right", StringComparison.Ordinal)) return FromName("Left" + n.Substring(5));
            return b;
        }

        /// <summary>Bones drawn as segments in the viewport (child -> parent).</summary>
        public static readonly int[] BodyBones =
        {
            Hips, Spine, Chest, UpperChest, Neck, Head, LeftShoulder, LeftUpperArm, LeftLowerArm, LeftHand,
            RightShoulder, RightUpperArm, RightLowerArm, RightHand, LeftUpperLeg, LeftLowerLeg, LeftFoot, LeftToes,
            RightUpperLeg, RightLowerLeg, RightFoot, RightToes
        };
    }
}
