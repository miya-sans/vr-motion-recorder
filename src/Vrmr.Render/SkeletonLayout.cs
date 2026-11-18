using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace VRMR.Render
{
    /// <summary>Which side of the body a bone is on (it picks the colour).</summary>
    enum BoneSide : byte { Left, Right, Center }

    /// <summary>A bone as the view draws it: a line between two profile nodes.</summary>
    struct BoneSegment
    {
        public int From, To;
        public BoneSide Side;
        public bool Finger;
    }

    /// <summary>
    ///     What to draw for a skeleton, worked out once per avatar profile: the humanoid bones (each from its nearest
    ///     humanoid ancestor, so intermediate transforms are skipped), the secondary bones (hair, ears, tail, skirt) and the
    ///     head circle.
    /// </summary>
    sealed class SkeletonLayout
    {
        static readonly ConditionalWeakTable<AvatarProfile, SkeletonLayout> Cache = new();
        public readonly float HeadRadius, EyeHeight;

        /// <summary>Profile node of the hips and the head, or -1.</summary>
        public readonly int Hips, Head;

        public readonly BoneSegment[] Human;
        public readonly BoneSegment[] Secondary;

        SkeletonLayout(AvatarProfile p)
        {
            int n = p.Nodes.Count;
            bool[] exists = p.HumanExists;
            bool[] isHuman = new bool[n];
            for (int b = 0; b < HumanBones.Count; b++)
                if (p.Human[b] >= 0 && p.Human[b] < n)
                    isHuman[p.Human[b]] = true;

            var human = new List<BoneSegment>();
            for (int hb = 0; hb < HumanBones.Count; hb++)
            {
                if (!exists[hb] || hb == HumanBones.LeftEye || hb == HumanBones.RightEye ||
                    hb == HumanBones.Jaw) continue;
                int ph = HumanBones.ResolveParent(hb, exists);
                if (ph < 0) continue;
                string name = HumanBones.Names[hb];
                human.Add(new BoneSegment
                {
                    From = p.Human[ph], To = p.Human[hb], Finger = HumanBones.IsFinger(hb),
                    Side = name.StartsWith("Left", StringComparison.Ordinal) ? BoneSide.Left :
                        name.StartsWith("Right", StringComparison.Ordinal) ? BoneSide.Right : BoneSide.Center
                });
            }

            Human = human.ToArray();

            bool[] isBone = new bool[n];
            foreach (int i in p.ArmatureNodes())
                if (i >= 0 && i < n)
                    isBone[i] = true;
            var secondary = new List<BoneSegment>();
            for (int i = 0; i < n; i++)
            {
                int parent = p.Nodes[i].Parent;
                if (parent < 0 || isHuman[i] || !isBone[i] || !isBone[parent]) continue;
                // directly under the armature root: not a bone of the body
                if (!isHuman[parent] && p.Nodes[parent].Parent >= 0 &&
                    p.Nodes[p.Nodes[parent].Parent].Parent < 0) continue;
                secondary.Add(new BoneSegment { From = parent, To = i, Side = BoneSide.Center });
            }

            Secondary = secondary.ToArray();

            Hips = p.Has(HumanBones.Hips) ? p.Human[HumanBones.Hips] : -1;
            Head = p.Has(HumanBones.Head) ? p.Human[HumanBones.Head] : -1;
            EyeHeight = p.EyeHeight;
            HeadRadius = Head < 0 ? 0 : Math.Max(0.06f, (EyeHeight - p.HumanRestPos(HumanBones.Head).y) * 1.8f);
        }

        public static SkeletonLayout For(AvatarProfile profile)
        {
            return Cache.GetValue(profile, p => new SkeletonLayout(p));
        }
    }
}
