// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Replaces IkTransformMapping + the avatar-load measurements of IkLocalAvatarDriver /
// IkLocalHeightCalculator (TposeBoneSnapshot, TposeTorsoTop, avatar eye height, arm span, shoulder width,
// body segments) for an avatar given as humanoid rest poses instead of Unity Transforms.
using System;
using System.Collections.Generic;

namespace VRMR.Fbik
{
    public sealed class IkAvatarModel
    {
        public const int HumanBoneCount = (int)HumanBodyBones.LastBone;
        public const int RootNode = 0;
        public readonly Vector3[] RestPosition = new Vector3[HumanBoneCount];
        public readonly Quaternion[] RestRotation = new Quaternion[HumanBoneCount];
        public readonly bool[] Exists = new bool[HumanBoneCount];
        public readonly int[] Parent = new int[HumanBoneCount];
        /// <summary>Skeleton node index per HumanBodyBones (-1 when the bone does not exist). Node 0 is the avatar root.</summary>
        public readonly int[] NodeOfBone = new int[HumanBoneCount];
        public int[] BoneOfNode;
        public readonly IkPoseSkeleton Skeleton = new IkPoseSkeleton();
        /// <summary>Authored eye height above the avatar root (upstream ActiveAvatarEyeHeight before sole correction).</summary>
        public float AuthoredEyeHeight;
        /// <summary>Authored centre-eye position in avatar-root space (upstream IkAvatar.AvatarEyePosition).</summary>
        public Vector3 EyeLocal;
        /// <summary>T-pose sole height above the avatar root (IkAvatarSole; mesh-based upstream, supplied here).</summary>
        public float SoleHeight;
        public readonly Dictionary<IkBoneTrackedRole, IkCalibratedCoords> TposeBoneSnapshot = new Dictionary<IkBoneTrackedRole, IkCalibratedCoords>();
        public Vector3 TposeTorsoTop;
        public bool HasTposeTorsoTop;
        // IkHeightDriver avatar metrics
        public float AvatarEyeHeight, AvatarArmSpan, AvatarShoulderWidth, AvatarHipHeight, AvatarLegSpan, AvatarSpineSpan, AvatarHipJointHeight, AvatarTorsoSpan;

        public IkAvatarModel(Vector3[] restPos, Quaternion[] restRot, bool[] exists, int[] parent, float avatarEyeHeight, float soleHeight)
        {
            if (restPos == null || restRot == null || exists == null || parent == null) throw new ArgumentNullException();
            int n = Math.Min(HumanBoneCount, Math.Min(Math.Min(restPos.Length, restRot.Length), Math.Min(exists.Length, parent.Length)));
            for (int i = 0; i < HumanBoneCount; i++)
            {
                bool has = i < n && exists[i];
                Exists[i] = has;
                RestPosition[i] = has ? restPos[i] : Vector3.zero;
                RestRotation[i] = has ? Quaternion.Normalize(restRot[i]) : Quaternion.identity;
                Parent[i] = has ? parent[i] : -1;
                NodeOfBone[i] = -1;
            }
            if (!Exists[(int)HumanBodyBones.Hips] || !Exists[(int)HumanBodyBones.Head])
            {
                throw new ArgumentException("FbikBodySolver needs at least the Hips and Head bones.");
            }
            for (int i = 0; i < HumanBoneCount; i++)
            {
                if (!Exists[i]) continue;
                int p = Parent[i];
                if (p < 0 || p >= HumanBoneCount || !Exists[p] || p == i)
                {
                    Parent[i] = i == (int)HumanBodyBones.Hips ? -1 : FallbackParent(i);
                }
            }
            SoleHeight = soleHeight;
            AuthoredEyeHeight = avatarEyeHeight > 0f ? avatarEyeHeight : RestPosition[(int)HumanBodyBones.Head].y * 1.06f;
            EyeLocal = ComputeEyeLocal();
            BuildSkeleton();
            CaptureSnapshot();
            MeasureAvatar();
        }
        int FallbackParent(int bone)
        {
            // Walk the standard humanoid chain up to the nearest existing bone.
            int[] chain = StandardParent;
            int walk = chain[bone];
            while (walk >= 0 && !Exists[walk]) walk = chain[walk];
            return walk < 0 ? (int)HumanBodyBones.Hips : walk;
        }
        static readonly int[] StandardParent = BuildStandardParents();
        static int[] BuildStandardParents()
        {
            int[] p = new int[HumanBoneCount];
            for (int i = 0; i < p.Length; i++) p[i] = (int)HumanBodyBones.Hips;
            p[(int)HumanBodyBones.Hips] = -1;
            p[(int)HumanBodyBones.LeftUpperLeg] = (int)HumanBodyBones.Hips; p[(int)HumanBodyBones.RightUpperLeg] = (int)HumanBodyBones.Hips;
            p[(int)HumanBodyBones.LeftLowerLeg] = (int)HumanBodyBones.LeftUpperLeg; p[(int)HumanBodyBones.RightLowerLeg] = (int)HumanBodyBones.RightUpperLeg;
            p[(int)HumanBodyBones.LeftFoot] = (int)HumanBodyBones.LeftLowerLeg; p[(int)HumanBodyBones.RightFoot] = (int)HumanBodyBones.RightLowerLeg;
            p[(int)HumanBodyBones.LeftToes] = (int)HumanBodyBones.LeftFoot; p[(int)HumanBodyBones.RightToes] = (int)HumanBodyBones.RightFoot;
            p[(int)HumanBodyBones.Spine] = (int)HumanBodyBones.Hips; p[(int)HumanBodyBones.Chest] = (int)HumanBodyBones.Spine;
            p[(int)HumanBodyBones.UpperChest] = (int)HumanBodyBones.Chest; p[(int)HumanBodyBones.Neck] = (int)HumanBodyBones.UpperChest;
            p[(int)HumanBodyBones.Head] = (int)HumanBodyBones.Neck;
            p[(int)HumanBodyBones.LeftShoulder] = (int)HumanBodyBones.UpperChest; p[(int)HumanBodyBones.RightShoulder] = (int)HumanBodyBones.UpperChest;
            p[(int)HumanBodyBones.LeftUpperArm] = (int)HumanBodyBones.LeftShoulder; p[(int)HumanBodyBones.RightUpperArm] = (int)HumanBodyBones.RightShoulder;
            p[(int)HumanBodyBones.LeftLowerArm] = (int)HumanBodyBones.LeftUpperArm; p[(int)HumanBodyBones.RightLowerArm] = (int)HumanBodyBones.RightUpperArm;
            p[(int)HumanBodyBones.LeftHand] = (int)HumanBodyBones.LeftLowerArm; p[(int)HumanBodyBones.RightHand] = (int)HumanBodyBones.RightLowerArm;
            p[(int)HumanBodyBones.LeftEye] = (int)HumanBodyBones.Head; p[(int)HumanBodyBones.RightEye] = (int)HumanBodyBones.Head; p[(int)HumanBodyBones.Jaw] = (int)HumanBodyBones.Head;
            for (int f = (int)HumanBodyBones.LeftThumbProximal; f <= (int)HumanBodyBones.RightLittleDistal; f++)
            {
                bool left = f <= (int)HumanBodyBones.LeftLittleDistal;
                int local = (f - (int)HumanBodyBones.LeftThumbProximal) % 15;
                p[f] = local % 3 == 0 ? (left ? (int)HumanBodyBones.LeftHand : (int)HumanBodyBones.RightHand) : f - 1;
            }
            return p;
        }
        Vector3 ComputeEyeLocal()
        {
            // Upstream reads the avatar's authored eye position; here: eye bones' midpoint depth when present, else the head.
            float z = RestPosition[(int)HumanBodyBones.Head].z;
            bool le = Exists[(int)HumanBodyBones.LeftEye], re = Exists[(int)HumanBodyBones.RightEye];
            if (le && re) z = (RestPosition[(int)HumanBodyBones.LeftEye].z + RestPosition[(int)HumanBodyBones.RightEye].z) * 0.5f;
            else if (le) z = RestPosition[(int)HumanBodyBones.LeftEye].z;
            else if (re) z = RestPosition[(int)HumanBodyBones.RightEye].z;
            return new Vector3(0f, AuthoredEyeHeight, z);
        }
        void BuildSkeleton()
        {
            List<int> order = new List<int>();
            int[] depth = new int[HumanBoneCount];
            for (int i = 0; i < HumanBoneCount; i++)
            {
                if (!Exists[i]) continue;
                int d = 0, walk = Parent[i], guard = 0;
                while (walk >= 0 && guard++ < 64) { d++; walk = Parent[walk]; }
                depth[i] = d;
                order.Add(i);
            }
            order.Sort((a, b) => depth[a] != depth[b] ? depth[a].CompareTo(depth[b]) : a.CompareTo(b));
            int count = order.Count + 1;
            BoneOfNode = new int[count];
            int[] parentNode = new int[count];
            Vector3[] lp = new Vector3[count];
            Quaternion[] lr = new Quaternion[count];
            Vector3[] ls = new Vector3[count];
            BoneOfNode[0] = -1;
            parentNode[0] = -1;
            lp[0] = Vector3.zero;
            lr[0] = Quaternion.identity;
            ls[0] = Vector3.one;
            for (int k = 0; k < order.Count; k++)
            {
                int bone = order[k], node = k + 1;
                NodeOfBone[bone] = node;
                BoneOfNode[node] = bone;
            }
            for (int k = 0; k < order.Count; k++)
            {
                int bone = order[k], node = k + 1;
                int pb = Parent[bone];
                Vector3 parentPos = pb >= 0 ? RestPosition[pb] : Vector3.zero;
                Quaternion parentRot = pb >= 0 ? RestRotation[pb] : Quaternion.identity;
                parentNode[node] = pb >= 0 ? NodeOfBone[pb] : RootNode;
                Quaternion inv = Quaternion.Inverse(parentRot);
                lp[node] = inv * (RestPosition[bone] - parentPos);
                lr[node] = Quaternion.Normalize(inv * RestRotation[bone]);
                ls[node] = Vector3.one;
            }
            Skeleton.Build(parentNode, lp, lr, ls);
        }
        public bool Has(HumanBodyBones b) => (int)b >= 0 && (int)b < HumanBoneCount && Exists[(int)b];
        public int Node(HumanBodyBones b) => (int)b >= 0 && (int)b < HumanBoneCount ? NodeOfBone[(int)b] : -1;
        public Vector3 Pos(HumanBodyBones b) => RestPosition[(int)b];
        public Quaternion Rot(HumanBodyBones b) => RestRotation[(int)b];
        /// <summary>Transform.IsChildOf: true when bone == ancestor or bone is below ancestor.</summary>
        public bool IsChildOf(HumanBodyBones bone, HumanBodyBones ancestor)
        {
            if (!Has(bone) || !Has(ancestor)) return false;
            int walk = (int)bone, guard = 0;
            while (walk >= 0 && guard++ < 64)
            {
                if (walk == (int)ancestor) return true;
                walk = Parent[walk];
            }
            return false;
        }
        public bool TryGetParent(HumanBodyBones bone, out HumanBodyBones parent)
        {
            parent = HumanBodyBones.LastBone;
            if (!Has(bone) || Parent[(int)bone] < 0) return false;
            parent = (HumanBodyBones)Parent[(int)bone];
            return true;
        }
        /// <summary>IkBodyFitApply.TorsoTop.</summary>
        public bool TryGetTorsoTop(out HumanBodyBones torsoTop)
        {
            HumanBodyBones shoulder = Has(HumanBodyBones.LeftShoulder) ? HumanBodyBones.LeftShoulder : HumanBodyBones.RightShoulder;
            HumanBodyBones upperArm = Has(HumanBodyBones.LeftUpperArm) ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm;
            HumanBodyBones hanger = HumanBodyBones.LastBone;
            bool hasHanger = false;
            if (Has(shoulder)) hasHanger = TryGetParent(shoulder, out hanger);
            else if (Has(upperArm)) hasHanger = TryGetParent(upperArm, out hanger);
            if (hasHanger && (hanger == HumanBodyBones.UpperChest || hanger == HumanBodyBones.Chest || hanger == HumanBodyBones.Spine))
            {
                torsoTop = hanger;
                return true;
            }
            torsoTop = Has(HumanBodyBones.UpperChest) ? HumanBodyBones.UpperChest : Has(HumanBodyBones.Chest) ? HumanBodyBones.Chest : HumanBodyBones.Spine;
            return Has(torsoTop);
        }
        public static readonly IkBoneTrackedRole[] SnapshotRoles =
        {
            IkBoneTrackedRole.Hips, IkBoneTrackedRole.Spine, IkBoneTrackedRole.Chest, IkBoneTrackedRole.Neck, IkBoneTrackedRole.Head,
            IkBoneTrackedRole.LeftShoulder, IkBoneTrackedRole.RightShoulder, IkBoneTrackedRole.LeftUpperArm, IkBoneTrackedRole.RightUpperArm,
            IkBoneTrackedRole.RightLowerArm, IkBoneTrackedRole.LeftLowerArm, IkBoneTrackedRole.LeftHand, IkBoneTrackedRole.RightHand,
            IkBoneTrackedRole.LeftUpperLeg, IkBoneTrackedRole.LeftLowerLeg, IkBoneTrackedRole.RightUpperLeg, IkBoneTrackedRole.RightLowerLeg,
            IkBoneTrackedRole.LeftFoot, IkBoneTrackedRole.LeftToes, IkBoneTrackedRole.RightFoot, IkBoneTrackedRole.RightToes,
        };
        /// <summary>IkLocalAvatarDriver T-pose bone snapshot: root-relative, sole-corrected (avatar root is the origin here).</summary>
        void CaptureSnapshot()
        {
            TposeBoneSnapshot.Clear();
            foreach (IkBoneTrackedRole role in SnapshotRoles)
            {
                if (!IkBoneTrackedRoleCommonCheck.TryConvertToHumanoidRole(role, out HumanBodyBones bone) || !Has(bone)) continue;
                TposeBoneSnapshot[role] = new IkCalibratedCoords(RestPosition[(int)bone] - new Vector3(0f, SoleHeight, 0f), RestRotation[(int)bone]);
            }
            if (TryGetTorsoTop(out HumanBodyBones top))
            {
                TposeTorsoTop = RestPosition[(int)top] - new Vector3(0f, SoleHeight, 0f);
                HasTposeTorsoTop = true;
            }
        }
        /// <summary>IkLocalHeightCalculator.CalculateAvatarEyeHeight / CalculateAvatarArmSpan / CalculateAvatarBodySegments.</summary>
        void MeasureAvatar()
        {
            AvatarEyeHeight = AuthoredEyeHeight - SoleHeight;
            AvatarEyeHeight = AvatarEyeHeight > 0f ? AvatarEyeHeight : IkHeightConstants.FallbackHeightInMeters;
            var snapshot = TposeBoneSnapshot;
            if (snapshot.TryGetValue(IkBoneTrackedRole.LeftHand, out var leftBind) && snapshot.TryGetValue(IkBoneTrackedRole.RightHand, out var rightBind))
            {
                Vector3 lb = leftBind.position, rb = rightBind.position;
                float flatSpan = Vector3.Distance(new Vector3(lb.x, 0f, lb.z), new Vector3(rb.x, 0f, rb.z));
                AvatarArmSpan = TryGetSegmentArmSpan(snapshot, out float segmentSpan) ? Mathf.Max(flatSpan, segmentSpan) : flatSpan;
            }
            else
            {
                AvatarArmSpan = AvatarEyeHeight;
            }
            if (!(AvatarArmSpan > 0f) || float.IsInfinity(AvatarArmSpan)) AvatarArmSpan = AvatarEyeHeight;
            bool hasHips = snapshot.TryGetValue(IkBoneTrackedRole.Hips, out var hipsBind);
            if (hasHips) AvatarHipHeight = hipsBind.position.y;
            if (hasHips && snapshot.TryGetValue(IkBoneTrackedRole.Head, out var headBind)) AvatarSpineSpan = headBind.position.y - hipsBind.position.y;
            if (hasHips && HasTposeTorsoTop) AvatarTorsoSpan = Mathf.Max(0f, TposeTorsoTop.y - hipsBind.position.y);
            if (snapshot.TryGetValue(IkBoneTrackedRole.LeftUpperLeg, out var upperLegBind) && snapshot.TryGetValue(IkBoneTrackedRole.LeftFoot, out var footBind))
            {
                AvatarLegSpan = upperLegBind.position.y - footBind.position.y;
                AvatarHipJointHeight = upperLegBind.position.y;
                if (snapshot.TryGetValue(IkBoneTrackedRole.RightUpperLeg, out var rightUpperLegBind))
                {
                    AvatarHipJointHeight = (upperLegBind.position.y + rightUpperLegBind.position.y) * 0.5f;
                }
            }
            if (snapshot.TryGetValue(IkBoneTrackedRole.LeftUpperArm, out var leftArmBind) && snapshot.TryGetValue(IkBoneTrackedRole.RightUpperArm, out var rightArmBind))
            {
                Vector3 la = leftArmBind.position, ra = rightArmBind.position;
                AvatarShoulderWidth = Vector3.Distance(new Vector3(la.x, 0f, la.z), new Vector3(ra.x, 0f, ra.z));
            }
        }
        public static bool TryGetSegmentArmSpan(IReadOnlyDictionary<IkBoneTrackedRole, IkCalibratedCoords> snapshot, out float span)
        {
            span = 0f;
            if (snapshot == null || !snapshot.TryGetValue(IkBoneTrackedRole.LeftUpperArm, out var lu) || !snapshot.TryGetValue(IkBoneTrackedRole.LeftLowerArm, out var ll) || !snapshot.TryGetValue(IkBoneTrackedRole.LeftHand, out var lh) || !snapshot.TryGetValue(IkBoneTrackedRole.RightUpperArm, out var ru) || !snapshot.TryGetValue(IkBoneTrackedRole.RightLowerArm, out var rl) || !snapshot.TryGetValue(IkBoneTrackedRole.RightHand, out var rh))
            {
                return false;
            }
            Vector3 l = lu.position, r = ru.position;
            span = Vector3.Distance(new Vector3(l.x, 0f, l.z), new Vector3(r.x, 0f, r.z)) + Vector3.Distance(lu.position, ll.position) + Vector3.Distance(ll.position, lh.position) + Vector3.Distance(ru.position, rl.position) + Vector3.Distance(rl.position, rh.position);
            return span > 0f && !float.IsNaN(span) && !float.IsInfinity(span);
        }
    }
}
