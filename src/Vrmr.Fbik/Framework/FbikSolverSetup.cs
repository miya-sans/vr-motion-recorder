// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Solver setup: default values, bone binds, the full-body rig (CreateFullBodyRig, hand landmarks) and applying the body fit.
// Transform reads (Mapping.X.position / .rotation) become rest-pose reads from IkAvatarModel (avatar root
// at the origin, identity rotation, exactly the frame the upstream T-pose snapshot is expressed in).
// Settings come from FbikSettings (IkSettingsDefaults values).

using System;

namespace VRMR.Fbik
{
    public static class FbikSolverSetup
    {
        /// <summary>Upstream: ScaleAvatarModification.ApplyScale (runtime avatar height override). Always 1 in the port.</summary>
        public static float AppliedAvatarScale()
        {
            return 1f;
        }

        public static void SetDefaultValues(ref FbikSolver job, FbikSettings settings)
        {
            job.offsetRotationHead = job.offsetRotationLeftFoot = job.offsetRotationRightFoot = Quaternion.identity;
            job.offsetRotationLeftHand = job.offsetRotationRightHand = Quaternion.identity;
            job.tposeLeftLowerArmTwistBind = job.tposeLeftLowerArmChildBind = Quaternion.identity;
            job.tposeRightLowerArmTwistBind = job.tposeRightLowerArmChildBind = Quaternion.identity;
            job.tposeLeftUpperArmTwistBind = job.tposeLeftUpperArmChildBind = Quaternion.identity;
            job.tposeRightUpperArmTwistBind = job.tposeRightUpperArmChildBind = Quaternion.identity;
            job.tposeArmFitScale = job.tposeTorsoFitScale = 1f;
            job.playerUp = Vector3.up;
            job.minHeadSpineHeight = 0f;
            job.minFactor = 0.95f;
            job.maxFactor = 1.05f;
            job.spineMaxIterations = 20;
            job.spineTolerance = 0.001f;
            job.chainChestIdx = -1;
            job.targetPositionHips = Vector3.zero;
            job.targetRotationHips = Quaternion.identity;
            job.offsetRotationHips = Quaternion.identity;
            job.leftDrivenTargetRot = job.rightDrivenTargetRot = Quaternion.identity;
            job.chestRadius = settings.FBIKChestRadius;
            job.collisionSkin = settings.FBIKCollisionSkin;
            job.collisionsEnabled = settings.FBIKCollisionsEnabled;
            job.handRadius = settings.FBIKHandRadius;
            job.handSkin = settings.FBIKHandSkin;
            job.protectElbow = settings.FBIKProtectElbow;
            job.collideTrackedElbow = settings.FBIKCollideTrackedElbow;
            job.shoulderSolveEnabled = settings.FBIKShoulderSolveEnabled;
            job.shoulderElevationFactor = settings.FBIKShoulderElevation;
            job.shoulderProtractionFactor = settings.FBIKShoulderProtraction;
            job.shoulderTrackerBlendTime = settings.FBIKShoulderTrackerBlend;
            job.armSwivelSmoothTime = settings.FBIKArmSwivelSmoothTime;
            job.armSwivelMaxRateDeg = settings.FBIKArmSwivelMaxRate;
            job.moveBodyBackWhenCrouching = settings.FBIKMoveBodyBackWhenCrouching;
            job.crouchDepth = 0f;
            job.standingHeadHeight = 0f;
            job.slotPositions.Length = FbikSolver.Count;
            job.slotRotations.Length = FbikSolver.Count;
            job.slotOffsets.Length = FbikSolver.Count;
            job.slotWeights.Length = FbikSolver.Count;
            job.slotBlends.Length = FbikSolver.Count;
            for (int i = 0; i < FbikSolver.Count; i++)
            {
                job.slotPositions[i] = Vector3.zero;
                job.slotRotations[i] = Quaternion.identity;
                job.slotOffsets[i] = Quaternion.identity;
                job.slotWeights[i] = false;
                job.slotBlends[i] = 1f;
            }
        }

        static Quaternion BindLocal(IkAvatarModel m, HumanBodyBones parent, HumanBodyBones bone, bool hasBone)
        {
            if (!hasBone || !m.Has(parent) || !m.Has(bone))
            {
                return Quaternion.identity;
            }

            return Quaternion.Inverse(m.Rot(parent)) * m.Rot(bone);
        }

        static IkBoneHandle Bind(IkAvatarModel m, HumanBodyBones b)
        {
            return m.Has(b) ? m.Skeleton.Bind(m.Node(b)) : IkBoneHandle.Unbound;
        }

        public static void Create(ref FbikSolver job, IkAvatarModel m)
        {
            var skeleton = m.Skeleton;
            job.handleHips = Bind(m, HumanBodyBones.Hips);
            job.handleChest = Bind(m, HumanBodyBones.Chest);
            job.handleNeck = Bind(m, HumanBodyBones.Neck);
            job.handleHead = Bind(m, HumanBodyBones.Head);
            job.handleLeftUpperLeg = Bind(m, HumanBodyBones.LeftUpperLeg);
            job.handleLeftLowerLeg = Bind(m, HumanBodyBones.LeftLowerLeg);
            job.handleLeftFoot = Bind(m, HumanBodyBones.LeftFoot);
            job.handleRightUpperLeg = Bind(m, HumanBodyBones.RightUpperLeg);
            job.handleRightLowerLeg = Bind(m, HumanBodyBones.RightLowerLeg);
            job.handleRightFoot = Bind(m, HumanBodyBones.RightFoot);
            job.handleLeftToe = Bind(m, HumanBodyBones.LeftToes);
            job.handleRightToe = Bind(m, HumanBodyBones.RightToes);
            job.handleLeftUpperArm = Bind(m, HumanBodyBones.LeftUpperArm);
            job.handleLeftLowerArm = Bind(m, HumanBodyBones.LeftLowerArm);
            job.handleLeftHand = Bind(m, HumanBodyBones.LeftHand);
            job.handleRightUpperArm = Bind(m, HumanBodyBones.RightUpperArm);
            job.handleRightLowerArm = Bind(m, HumanBodyBones.RightLowerArm);
            job.handleRightHand = Bind(m, HumanBodyBones.RightHand);
            // Twist bones are not humanoid bones; the facade has no way to name them, so they stay unbound.
            job.handleLeftUpperArmTwist = IkBoneHandle.Unbound;
            job.handleLeftLowerArmTwist = IkBoneHandle.Unbound;
            job.handleRightUpperArmTwist = IkBoneHandle.Unbound;
            job.handleRightLowerArmTwist = IkBoneHandle.Unbound;
            job.handleSpine = Bind(m, HumanBodyBones.Spine);
            job.handleUpperChest = Bind(m, HumanBodyBones.UpperChest);
            job.handleLeftShoulder = Bind(m, HumanBodyBones.LeftShoulder);
            job.handleRightShoulder = Bind(m, HumanBodyBones.RightShoulder);

            job.tposeLeftShoulderRot = m.Has(HumanBodyBones.LeftShoulder)
                ? m.Rot(HumanBodyBones.LeftShoulder)
                : Quaternion.identity;
            job.tposeRightShoulderRot = m.Has(HumanBodyBones.RightShoulder)
                ? m.Rot(HumanBodyBones.RightShoulder)
                : Quaternion.identity;
            job.tposeChestRot = m.Has(HumanBodyBones.UpperChest) ? m.Rot(HumanBodyBones.UpperChest) :
                m.Has(HumanBodyBones.Chest) ? m.Rot(HumanBodyBones.Chest) : Quaternion.identity;
            job.tposeLeftShoulderLocalDir = m.Has(HumanBodyBones.LeftShoulder) && m.Has(HumanBodyBones.LeftUpperArm)
                ? (m.Pos(HumanBodyBones.LeftUpperArm) - m.Pos(HumanBodyBones.LeftShoulder)).normalized
                : Vector3.left;
            job.tposeRightShoulderLocalDir = m.Has(HumanBodyBones.RightShoulder) && m.Has(HumanBodyBones.RightUpperArm)
                ? (m.Pos(HumanBodyBones.RightUpperArm) - m.Pos(HumanBodyBones.RightShoulder)).normalized
                : Vector3.right;

            // IkHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale = ScaledAvatarEyeHeight / FallbackHeightInMeters.
            float fallbackArmLength = 0.6f * (m.AvatarEyeHeight / IkHeightConstants.FallbackHeightInMeters);
            job.tposeShoulderToHandLeft = m.Has(HumanBodyBones.LeftShoulder) && m.Has(HumanBodyBones.LeftHand)
                ? Vector3.Distance(m.Pos(HumanBodyBones.LeftShoulder), m.Pos(HumanBodyBones.LeftHand))
                : fallbackArmLength;
            job.tposeShoulderToHandRight = m.Has(HumanBodyBones.RightShoulder) && m.Has(HumanBodyBones.RightHand)
                ? Vector3.Distance(m.Pos(HumanBodyBones.RightShoulder), m.Pos(HumanBodyBones.RightHand))
                : fallbackArmLength;
            job.tposeClavicleLenLeft = m.Has(HumanBodyBones.LeftShoulder) && m.Has(HumanBodyBones.LeftUpperArm)
                ? Vector3.Distance(m.Pos(HumanBodyBones.LeftShoulder), m.Pos(HumanBodyBones.LeftUpperArm))
                : 0f;
            job.tposeClavicleLenRight = m.Has(HumanBodyBones.RightShoulder) && m.Has(HumanBodyBones.RightUpperArm)
                ? Vector3.Distance(m.Pos(HumanBodyBones.RightShoulder), m.Pos(HumanBodyBones.RightUpperArm))
                : 0f;
            job.tposeShoulderToElbowLeft = m.Has(HumanBodyBones.LeftShoulder) && m.Has(HumanBodyBones.LeftLowerArm)
                ? Vector3.Distance(m.Pos(HumanBodyBones.LeftShoulder), m.Pos(HumanBodyBones.LeftLowerArm))
                : 0f;
            job.tposeShoulderToElbowRight = m.Has(HumanBodyBones.RightShoulder) && m.Has(HumanBodyBones.RightLowerArm)
                ? Vector3.Distance(m.Pos(HumanBodyBones.RightShoulder), m.Pos(HumanBodyBones.RightLowerArm))
                : 0f;

            var avatarUp = m.Has(HumanBodyBones.Head) && m.Has(HumanBodyBones.Hips)
                ? (m.Pos(HumanBodyBones.Head) - m.Pos(HumanBodyBones.Hips)).normalized
                : Vector3.up;
            if (avatarUp.sqrMagnitude < 0.5f) avatarUp = Vector3.up;
            job.tposeLeftHandBackLocal = m.Has(HumanBodyBones.LeftHand)
                ? Quaternion.Inverse(m.Rot(HumanBodyBones.LeftHand)) * avatarUp
                : Vector3.zero;
            job.tposeRightHandBackLocal = m.Has(HumanBodyBones.RightHand)
                ? Quaternion.Inverse(m.Rot(HumanBodyBones.RightHand)) * avatarUp
                : Vector3.zero;

            // Twist binds: no twist bones -> BindLocal(..., null) == identity, exactly as upstream for avatars without twists.
            job.tposeLeftLowerArmTwistBind = Quaternion.identity;
            job.tposeLeftLowerArmChildBind = BindLocal(m, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, true);
            job.tposeRightLowerArmTwistBind = Quaternion.identity;
            job.tposeRightLowerArmChildBind =
                BindLocal(m, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, true);
            job.tposeLeftUpperArmTwistBind = Quaternion.identity;
            job.tposeLeftUpperArmChildBind =
                BindLocal(m, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, true);
            job.tposeRightUpperArmTwistBind = Quaternion.identity;
            job.tposeRightUpperArmChildBind =
                BindLocal(m, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, true);

            GenerateHeadToSpine(ref job, m);
            job.tposeArmFitScale = job.tposeTorsoFitScale = 1f;
            job.spineMaxIterations = 20;
            job.spineTolerance = 0.001f;
            job.chestSpring = new NativeArray<IkChestSpringState>(1, Allocator.Persistent);
            job.armState = new NativeArray<IkArmState>(FbikSolver.armCount, Allocator.Persistent);
            job.legState = new NativeArray<IkLegSlotState>(2, Allocator.Persistent);
            job.legDiagnostics = new NativeArray<IkLegDiagnostics>(2, Allocator.Persistent);
            job.solvedPositions = new NativeArray<Vector3>(FbikSolver.Count, Allocator.Persistent);
            job.solvedRotations = new NativeArray<Quaternion>(FbikSolver.Count, Allocator.Persistent);
            job.poseStream = skeleton.Stream;
            job.InitalizeRalivSpineIK();
            FbikPlanner.Bind(ref job);
        }

        static void BuildSpineAnatomy(ref FbikSolver job, HumanBodyBones[] chain, IkAvatarModel m)
        {
            int n = chain.Length;
            job.chainSpineRestFrames = new NativeArray<IkSpineRestFrame>(n, Allocator.Persistent);
            if (!m.Has(HumanBodyBones.LeftUpperArm) || !m.Has(HumanBodyBones.RightUpperArm))
            {
                return;
            }

            var hipsRight = m.Pos(HumanBodyBones.RightUpperArm) - m.Pos(HumanBodyBones.LeftUpperArm);
            for (int i = 1; i <= n - 2; i++)
            {
                HumanBodyBones bone = chain[i], child = chain[i - 1], parent = chain[i + 1];
                IkSpineSegment segment;
                if (bone == HumanBodyBones.Spine) segment = IkSpineSegment.Lumbar;
                else if (bone == HumanBodyBones.Chest) segment = IkSpineSegment.LowerThoracic;
                else if (bone == HumanBodyBones.UpperChest) segment = IkSpineSegment.UpperThoracic;
                else if (bone == HumanBodyBones.Neck) segment = IkSpineSegment.Cervical;
                else continue;
                var frame = IkSpineAnatomy.BuildRestFrame(m.Pos(bone), m.Pos(child), m.Rot(bone), m.Rot(parent),
                    hipsRight);
                frame.Segment = segment;
                job.chainSpineRestFrames[i] = frame;
            }
        }

        public static void GenerateHeadToSpine(ref FbikSolver job, IkAvatarModel m)
        {
            HumanBodyBones[] candidates =
            {
                HumanBodyBones.Head, HumanBodyBones.Neck, HumanBodyBones.UpperChest, HumanBodyBones.Chest,
                HumanBodyBones.Spine, HumanBodyBones.Hips
            };
            bool solvable = m.Has(HumanBodyBones.Head) && m.Has(HumanBodyBones.Spine) && m.Has(HumanBodyBones.Hips);
            int presentCount = 0;
            if (solvable)
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (m.Has(candidates[i]))
                    {
                        presentCount++;
                    }
                }
            }

            var HeadToSpine = new HumanBodyBones[presentCount];
            int write = 0;
            job.chainChestIdx = -1;
            for (int i = 0; write < presentCount && i < candidates.Length; i++)
            {
                if (!m.Has(candidates[i]))
                {
                    continue;
                }

                if (m.Has(HumanBodyBones.Chest) && candidates[i] == HumanBodyBones.Chest)
                {
                    job.chainChestIdx = write;
                }

                HeadToSpine[write++] = candidates[i];
            }

            int SpineToHeadLength = HeadToSpine.Length;
            int torsoTopHeadFirst = m.TryGetTorsoTop(out var torsoTop) ? Array.IndexOf(HeadToSpine, torsoTop) : -1;
            job.chainTorsoTopIdx = torsoTopHeadFirst >= 0 ? SpineToHeadLength - 1 - torsoTopHeadFirst : 0;
            job.chainHeadToSpine = new NativeArray<IkBoneHandle>(SpineToHeadLength, Allocator.Persistent);
            BuildSpineAnatomy(ref job, HeadToSpine, m);
            for (int i = 0; i < SpineToHeadLength; i++)
            {
                job.chainHeadToSpine[i] = m.Skeleton.Bind(m.Node(HeadToSpine[i]));
            }

            var headToHips = m.Has(HumanBodyBones.Hips) && m.Has(HumanBodyBones.Head)
                ? m.Pos(HumanBodyBones.Head) - m.Pos(HumanBodyBones.Hips)
                : Vector3.zero;
            if (m.Has(HumanBodyBones.Head) && m.Has(HumanBodyBones.Neck))
            {
                job.tposeHeadToNeckLocal = Quaternion.Inverse(m.Rot(HumanBodyBones.Head)) *
                                           (m.Pos(HumanBodyBones.Neck) - m.Pos(HumanBodyBones.Head));
            }
            else
            {
                job.tposeHeadToNeckLocal = Vector3.zero;
            }

            if (m.Has(HumanBodyBones.Hips) && m.Has(HumanBodyBones.Neck))
            {
                job.tposeLengthNeckToHips = m.Pos(HumanBodyBones.Neck) - m.Pos(HumanBodyBones.Hips);
            }
            else
            {
                job.tposeLengthNeckToHips = headToHips;
            }

            job.tposeBakeScale = AppliedAvatarScale();
        }
    }

    /// <summary>Port of IkAnimationRiggingHelper (CreateFullBodyRig and the hand landmark frame).</summary>
    public static class IkAnimationRiggingHelper
    {
        public static Quaternion CalibratedRotationOffset(Quaternion boneOutgoingWorldRotation,
            Quaternion avatarBoneRotation)
        {
            return Quaternion.Inverse(boneOutgoingWorldRotation) * avatarBoneRotation;
        }

        /// <summary>IkAvatarStageCalibration.PureBind with the T-pose snapshot (root-relative bind rotation).</summary>
        public static Quaternion PureBind(IkAvatarModel m, IkBoneTrackedRole role)
        {
            if (m.TposeBoneSnapshot.TryGetValue(role, out var bind))
            {
                return bind.rotation;
            }

            return IkBoneTrackedRoleCommonCheck.TryConvertToHumanoidRole(role, out var b) && m.Has(b)
                ? m.Rot(b)
                : Quaternion.identity;
        }

        public static void CreateFullBodyRig(IkAvatarModel m, FbikSettings settings, ref FbikSolver job)
        {
            FbikSolverSetup.SetDefaultValues(ref job, settings);
            job.offsetRotationHead =
                m.Has(HumanBodyBones.Head) ? PureBind(m, IkBoneTrackedRole.Head) : Quaternion.identity;
            job.offsetRotationLeftFoot = m.Has(HumanBodyBones.LeftFoot)
                ? PureBind(m, IkBoneTrackedRole.LeftFoot)
                : Quaternion.identity;
            job.offsetRotationRightFoot = m.Has(HumanBodyBones.RightFoot)
                ? PureBind(m, IkBoneTrackedRole.RightFoot)
                : Quaternion.identity;
            Quaternion leftLandmarkBind = Quaternion.identity, rightLandmarkBind = Quaternion.identity;
            Quaternion lastGoodLeftRot = Quaternion.identity, lastGoodRightRot = Quaternion.identity;
            bool hasLastLeft = false, hasLastRight = false;
            if (m.Has(HumanBodyBones.LeftHand))
            {
                var wrist = m.Pos(HumanBodyBones.LeftHand);
                var a = new[]
                {
                    GetLM(m, HumanBodyBones.LeftIndexProximal), GetLM(m, HumanBodyBones.LeftIndexProximal),
                    GetLM(m, HumanBodyBones.LeftMiddleProximal)
                };
                var b = new[]
                {
                    GetLM(m, HumanBodyBones.LeftLittleProximal), GetLM(m, HumanBodyBones.LeftMiddleProximal),
                    GetLM(m, HumanBodyBones.LeftLittleProximal)
                };
                leftLandmarkBind = ComputeHandRotationWithFallback(wrist, a, b, ref hasLastLeft, ref lastGoodLeftRot);
            }

            if (m.Has(HumanBodyBones.RightHand))
            {
                var wrist = m.Pos(HumanBodyBones.RightHand);
                var a = new[]
                {
                    GetLM(m, HumanBodyBones.RightIndexProximal), GetLM(m, HumanBodyBones.RightIndexProximal),
                    GetLM(m, HumanBodyBones.RightMiddleProximal)
                };
                var b = new[]
                {
                    GetLM(m, HumanBodyBones.RightLittleProximal), GetLM(m, HumanBodyBones.RightMiddleProximal),
                    GetLM(m, HumanBodyBones.RightLittleProximal)
                };
                rightLandmarkBind =
                    ComputeHandRotationWithFallback(wrist, a, b, ref hasLastRight, ref lastGoodRightRot);
            }

            var leftBoneBind = m.Has(HumanBodyBones.LeftHand) ? m.Rot(HumanBodyBones.LeftHand) : Quaternion.identity;
            var rightBoneBind = m.Has(HumanBodyBones.RightHand) ? m.Rot(HumanBodyBones.RightHand) : Quaternion.identity;
            job.offsetRotationLeftHand = Quaternion.Inverse(leftLandmarkBind) * leftBoneBind;
            job.offsetRotationRightHand = Quaternion.Inverse(rightLandmarkBind) * rightBoneBind;
            job.offsetRotationChest =
                m.Has(HumanBodyBones.Chest) ? PureBind(m, IkBoneTrackedRole.Chest) : Quaternion.identity;
            job.offsetRotationLeftToe = m.Has(HumanBodyBones.LeftToes)
                ? PureBind(m, IkBoneTrackedRole.LeftToes)
                : Quaternion.identity;
            job.offsetRotationRightToe = m.Has(HumanBodyBones.RightToes)
                ? PureBind(m, IkBoneTrackedRole.RightToes)
                : Quaternion.identity;
            job.offsetRotationLeftShoulder = m.Has(HumanBodyBones.LeftShoulder)
                ? PureBind(m, IkBoneTrackedRole.LeftShoulder)
                : Quaternion.identity;
            job.offsetRotationRightShoulder = m.Has(HumanBodyBones.RightShoulder)
                ? PureBind(m, IkBoneTrackedRole.RightShoulder)
                : Quaternion.identity;
            job.offsetRotationHips =
                m.Has(HumanBodyBones.Hips) ? PureBind(m, IkBoneTrackedRole.Hips) : Quaternion.identity;
        }

        static (bool valid, Vector3 pos) GetLM(IkAvatarModel m, HumanBodyBones b)
        {
            return m.Has(b) ? (true, m.Pos(b)) : (false, Vector3.zero);
        }

        static Quaternion ComputeHandRotationWithFallback(Vector3 wrist, (bool valid, Vector3 pos)[] pointsA,
            (bool valid, Vector3 pos)[] pointsB, ref bool hasLast, ref Quaternion lastRot)
        {
            for (int Index = 0; Index < pointsA.Length; Index++)
            {
                if (!pointsA[Index].valid || !pointsB[Index].valid)
                {
                    continue;
                }

                var rot = HandRotationFromLandmarks(wrist, pointsA[Index].pos, pointsB[Index].pos);
                if (rot == Quaternion.identity) continue;
                lastRot = rot;
                hasLast = true;
                return rot;
            }

            return hasLast ? lastRot : Quaternion.identity;
        }

        public static Quaternion HandRotationFromLandmarks(Vector3 wrist, Vector3 indexMCP, Vector3 pinkyMCP)
        {
            var right = pinkyMCP - indexMCP;
            var knuckleMid = (indexMCP + pinkyMCP) * 0.5f;
            var forward = knuckleMid - wrist;
            if (right.sqrMagnitude < 1e-8f || forward.sqrMagnitude < 1e-8f)
            {
                return Quaternion.identity;
            }

            right.Normalize();
            forward.Normalize();
            var up = Vector3.Cross(forward, right);
            if (up.sqrMagnitude < 1e-8f)
            {
                return Quaternion.identity;
            }

            up.Normalize();
            return Quaternion.LookRotation(forward, up);
        }
    }

    /// <summary>Port of IkBodyFitApply (arm/leg/torso rest-length scaling of the pose skeleton).</summary>
    public static class IkBodyFitApply
    {
        public const int BoneCount = 17;

        static HumanBodyBones Below(IkAvatarModel m, HumanBodyBones bone, bool hasTop, HumanBodyBones torsoTop)
        {
            return m.Has(bone) && hasTop && m.IsChildOf(torsoTop, bone) ? bone : HumanBodyBones.LastBone;
        }

        public static void CollectBones(IkAvatarModel m, HumanBodyBones[] bones)
        {
            bones[0] = HumanBodyBones.LeftLowerArm;
            bones[1] = HumanBodyBones.LeftHand;
            bones[2] = HumanBodyBones.LastBone; // left upper arm twist (n/a)
            bones[3] = HumanBodyBones.LastBone; // left lower arm twist (n/a)
            bones[4] = HumanBodyBones.RightLowerArm;
            bones[5] = HumanBodyBones.RightHand;
            bones[6] = HumanBodyBones.LastBone;
            bones[7] = HumanBodyBones.LastBone;
            bones[8] = HumanBodyBones.LeftLowerLeg;
            bones[9] = HumanBodyBones.LeftFoot;
            bones[10] = HumanBodyBones.RightLowerLeg;
            bones[11] = HumanBodyBones.RightFoot;
            bool hasTop = m.TryGetTorsoTop(out var torsoTop);
            bones[12] = Below(m, HumanBodyBones.Spine, hasTop, torsoTop);
            bones[13] = Below(m, HumanBodyBones.Chest, hasTop, torsoTop);
            bones[14] = Below(m, HumanBodyBones.UpperChest, hasTop, torsoTop);
            bones[15] = HumanBodyBones.Neck;
            bones[16] = HumanBodyBones.Head;
        }

        public static void CollectScales(in IkBodyFitResult fit, float[] scales)
        {
            float arm = fit.HasArmFit ? fit.ArmScale : 1f;
            for (int i = 0; i < 8; i++) scales[i] = arm;
            float leg = fit.HasBodyFit ? fit.LegScale : 1f;
            for (int i = 8; i < 12; i++) scales[i] = leg;
            float torso = fit.HasBodyFit ? fit.TorsoScale : 1f;
            for (int i = 12; i < 15; i++) scales[i] = torso;
            scales[15] = 1f;
            scales[16] = 1f;
        }

        public static void Apply(IkAvatarModel m, in IkBodyFitResult fit)
        {
            var skeleton = m.Skeleton;
            if (skeleton == null || !skeleton.IsCreated)
            {
                return;
            }

            skeleton.ResetFit();
            var bones = new HumanBodyBones[BoneCount];
            float[] scales = new float[BoneCount];
            CollectBones(m, bones);
            CollectScales(in fit, scales);
            for (int i = 0; i < BoneCount; i++)
            {
                if (bones[i] != HumanBodyBones.LastBone && m.Has(bones[i]) && scales[i] != 1f)
                {
                    skeleton.SetFitScale(m.Node(bones[i]), scales[i]);
                }
            }
        }
    }
}
