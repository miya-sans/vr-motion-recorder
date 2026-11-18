// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Port for VR Motion Recorder: the per-frame "IK destinations" pipeline (Steps 01-16) and the rig setup
// (Spine(), ApplyTuningSettings, SetHandCollisionScale, RefreshBodyFit) are kept step for step. Dropped:
// PlayableGraph/Animator plumbing, the locomotion base pose (the stream is reset to the avatar rest pose
// instead), job scheduling (everything runs synchronously), transform scatter/publish, watchdogs and debug
// recorders. Static settings/bone-driver singletons are replaced by the FbikRig context.

namespace VRMR.Fbik
{
    public sealed class IkRigDriver
    {
        public const int sHips = 0,
            sHead = 1,
            sLeftFoot = 2,
            sRightFoot = 3,
            sChest = 4,
            sLeftLowerLeg = 5,
            sRightLowerLeg = 6,
            sLeftHand = 7,
            sRightHand = 8,
            sLeftLowerArm = 9,
            sRightLowerArm = 10,
            sLeftToe = 11,
            sRightToe = 12,
            sLeftShoulder = 13,
            sRightShoulder = 14,
            SlotCount = 15;

        public readonly bool[] EuroPos = new bool[SlotCount], EuroRot = new bool[SlotCount];
        public readonly bool[] SmoothPos = new bool[SlotCount], SmoothRot = new bool[SlotCount];
        readonly IkTrackingHardware[] groupHardware = new IkTrackingHardware[IkSmoothingProfiles.GroupCount];
        readonly bool[] groupOff = new bool[IkSmoothingProfiles.GroupCount];

        readonly float4[] groupPosTuning = new float4[IkSmoothingProfiles.GroupCount],
            groupRotTuning = new float4[IkSmoothingProfiles.GroupCount];

        readonly FbikRig rig;
        readonly Vector3[] unoverriddenPositions = new Vector3[FbikSolver.Count];
        readonly Quaternion[] unoverriddenRotations = new Quaternion[FbikSolver.Count];
        public IkBodyFitResult AppliedBodyFit = IkBodyFitResult.Identity;
        public bool HasRecalibratedRotationOffsets;
        public bool IKDataReady;
        public FbikSolver IKJob;
        public bool IKJobCreated;
        public Quaternion RecalibratedHead, RecalibratedHips, RecalibratedChest, RecalibratedLeftFoot;
        public Quaternion RecalibratedLeftShoulder, RecalibratedRightShoulder;
        public Quaternion RecalibratedRightFoot, RecalibratedLeftToe, RecalibratedRightToe;
        public bool RigLayerActive = true;
        NativeArray<IkEuroVec3State> euroPosStates;
        NativeArray<IkEuroQuatState> euroRotStates;
        NativeArray<float3> fallbackPosStates;
        NativeArray<quaternion> fallbackRotStates;
        float footIKBlendWeightLeft, footIKBlendWeightRight, footIKBlendWeight, stationaryTimer;
        FbikFrameFacts frameFacts;
        bool frameFootSimScheduled, frameFootReengage;

        Quaternion frameHipsRotation,
            frameLeftSimFootRotation,
            frameRightSimFootRotation,
            frameLeftElbowToBone,
            frameRightElbowToBone,
            frameLeftKneeToBone,
            frameRightKneeToBone;

        Vector3 framePlayerUpDirection;
        float framePlayerUpScale;
        bool hasFallbackState;
        NativeArray<float3> posInputs, posOutputs;
        NativeArray<byte> posModeNative, rotModeNative;
        NativeArray<float4> posTuning, rotTuning;
        NativeArray<quaternion> rotInputs, rotOutputs;
        public double timeAccumulator;
        bool trackersDisabled;
        bool unoverriddenValid;

        public IkRigDriver(FbikRig rig)
        {
            this.rig = rig;
            for (int i = 0; i < SlotCount; i++)
            {
                // IkSettingsDefaults FBIK*SmoothPos/SmoothRot/EuroPos/EuroRot are all true by default.
                SmoothPos[i] = SmoothRot[i] = EuroPos[i] = EuroRot[i] = true;
            }
        }

        public FbikFrameFacts LastFacts => frameFacts;
        public float FootIKBlendLeft => footIKBlendWeightLeft;
        public float FootIKBlendRight => footIKBlendWeightRight;

        // ---- setup ----------------------------------------------------------------------------------------
        public void BuildBuilder()
        {
            if (!IKDataReady)
            {
                return;
            }

            rig.Model.Skeleton.SetTranslationFree(rig.Model.Node(HumanBodyBones.Hips));
            FbikSolverSetup.Create(ref IKJob, rig.Model);
            IKJobCreated = true;
            unoverriddenValid = false;
            ResetSmoothingState();
            RefreshBodyFit();
        }

        public void RefreshBodyFit()
        {
            var skeleton = rig.Model.Skeleton;
            if (!skeleton.IsCreated || !IKJobCreated)
            {
                return;
            }

            if (!rig.Settings.FBIKBodyFit)
            {
                if (skeleton.FitActive)
                {
                    skeleton.ResetFit();
                    skeleton.WriteFittedLocalPositions();
                }

                IKJob.RescaleTposeFit(1f, 1f);
                AppliedBodyFit = IkBodyFitResult.Identity;
                rig.Feet.RefreshBodyFitScale();
                return;
            }

            var measurements = new IkBodyFitMeasurements
            {
                PlayerEyeHeight = rig.SizingEyeHeight,
                PlayerArmSpan = rig.PlayerArmSpan,
                AvatarEyeHeight = rig.Model.AvatarEyeHeight,
                AvatarArmSpan = rig.Model.AvatarArmSpan,
                AvatarShoulderWidth = rig.Model.AvatarShoulderWidth,
                UniformScale = rig.AppliedUniformScale
            };
            var fit = IkBodyFitCore.Solve(measurements, rig.Settings.FBIKBodyFitMaxDeviation);
            IkBodyFitApply.Apply(rig.Model, fit);
            IKJob.RescaleTposeFit(fit.HasArmFit ? fit.ArmScale : 1f, fit.HasBodyFit ? fit.TorsoScale : 1f);
            AppliedBodyFit = fit;
            skeleton.WriteFittedLocalPositions();
            rig.Feet.RefreshBodyFitScale();
        }

        /// <summary>Fitted, pose-independent T-pose position of a bone in avatar-root space (TryGetFittedTposeLocalScaled).</summary>
        public bool TryGetFittedTposeLocalScaled(HumanBodyBones bone, out Vector3 position)
        {
            position = Vector3.zero;
            if (!rig.Model.Has(bone) ||
                !rig.Model.Skeleton.TryGetRestPositionRelativeTo(rig.Model.Node(bone), IkAvatarModel.RootNode,
                    out var restLocal))
            {
                return false;
            }

            position = restLocal * FbikSolverSetup.AppliedAvatarScale();
            return !float.IsNaN(position.x) && !float.IsInfinity(position.x) && !float.IsNaN(position.y) &&
                   !float.IsInfinity(position.y) && !float.IsNaN(position.z) && !float.IsInfinity(position.z);
        }

        public void SetBodySettings()
        {
            HasRecalibratedRotationOffsets = false;
            Spine();
            rig.Calibration.ApplyCalibrationToCurrentAvatar();
        }

        void EnsureFilterArrays()
        {
            if (posInputs.IsCreated) return;
            posInputs = new NativeArray<float3>(SlotCount, Allocator.Persistent);
            posOutputs = new NativeArray<float3>(SlotCount, Allocator.Persistent);
            rotInputs = new NativeArray<quaternion>(SlotCount, Allocator.Persistent);
            rotOutputs = new NativeArray<quaternion>(SlotCount, Allocator.Persistent);
            posModeNative = new NativeArray<byte>(SlotCount, Allocator.Persistent);
            rotModeNative = new NativeArray<byte>(SlotCount, Allocator.Persistent);
            posTuning = new NativeArray<float4>(SlotCount, Allocator.Persistent);
            rotTuning = new NativeArray<float4>(SlotCount, Allocator.Persistent);
            fallbackPosStates = new NativeArray<float3>(SlotCount, Allocator.Persistent);
            fallbackRotStates = new NativeArray<quaternion>(SlotCount, Allocator.Persistent);
            euroPosStates = new NativeArray<IkEuroVec3State>(SlotCount, Allocator.Persistent);
            euroRotStates = new NativeArray<IkEuroQuatState>(SlotCount, Allocator.Persistent);
            for (int i = 0; i < SlotCount; i++)
            {
                rotInputs[i] = quaternion.identity;
                rotOutputs[i] = quaternion.identity;
                fallbackRotStates[i] = quaternion.identity;
            }
        }

        static byte PickMode(bool smoothEnabled, bool euroEnabled)
        {
            if (!smoothEnabled) return (byte)IkFilterMode.Passthrough;
            return euroEnabled ? (byte)IkFilterMode.Euro : (byte)IkFilterMode.Fallback;
        }

        bool SlotTracked(in FbikFrameFacts facts, int slot)
        {
            switch (slot)
            {
                case sHips: return facts.hipsTracked;
                case sHead: return rig.Bones.HeadControl.HasTracked;
                case sLeftFoot: return facts.leftFootTracked;
                case sRightFoot: return facts.rightFootTracked;
                case sChest: return facts.chestTracked;
                case sLeftLowerLeg: return facts.leftKneeTracked;
                case sRightLowerLeg: return facts.rightKneeTracked;
                case sLeftHand: return facts.leftHandWeight > 0f;
                case sRightHand: return facts.rightHandWeight > 0f;
                case sLeftLowerArm: return facts.leftElbowTracked;
                case sRightLowerArm: return facts.rightElbowTracked;
                case sLeftToe: return facts.leftToeTracked;
                case sRightToe: return facts.rightToeTracked;
                case sLeftShoulder: return facts.leftShoulderTracked;
                case sRightShoulder: return facts.rightShoulderTracked;
                default: return false;
            }
        }

        void ResolveGroupHardware()
        {
            for (int Index = 0; Index < groupHardware.Length; Index++)
            {
                groupHardware[Index] = IkTrackingHardware.Unknown;
            }

            // Upstream walks the input devices; the port keeps one hardware class per tracked role.
            for (int r = 0; r < IkBoneDriver.RoleCount; r++)
            {
                var role = (IkBoneTrackedRole)r;
                if (!rig.RoleHasDevice(role) || !TryGetGroupForRole(role, out int group))
                {
                    continue;
                }

                var hw = rig.RoleHardware[r];
                if ((byte)hw > (byte)groupHardware[group])
                {
                    groupHardware[group] = hw;
                }
            }
        }

        static bool TryGetGroupForRole(IkBoneTrackedRole role, out int group)
        {
            switch (role)
            {
                case IkBoneTrackedRole.CenterEye:
                case IkBoneTrackedRole.Head:
                    group = (int)IkSmoothingGroup.Head;
                    return true;
                case IkBoneTrackedRole.LeftHand:
                case IkBoneTrackedRole.RightHand:
                case IkBoneTrackedRole.LeftShoulder:
                case IkBoneTrackedRole.RightShoulder:
                    group = (int)IkSmoothingGroup.Hands;
                    return true;
                case IkBoneTrackedRole.LeftLowerArm:
                case IkBoneTrackedRole.RightLowerArm:
                    group = (int)IkSmoothingGroup.Elbows;
                    return true;
                case IkBoneTrackedRole.Chest:
                    group = (int)IkSmoothingGroup.Chest;
                    return true;
                case IkBoneTrackedRole.Hips:
                    group = (int)IkSmoothingGroup.Hips;
                    return true;
                case IkBoneTrackedRole.LeftLowerLeg:
                case IkBoneTrackedRole.RightLowerLeg:
                    group = (int)IkSmoothingGroup.Knees;
                    return true;
                case IkBoneTrackedRole.LeftFoot:
                case IkBoneTrackedRole.RightFoot:
                case IkBoneTrackedRole.LeftToes:
                case IkBoneTrackedRole.RightToes:
                    group = (int)IkSmoothingGroup.Feet;
                    return true;
                default:
                    group = -1;
                    return false;
            }
        }

        void ResolveSmoothingGroups(float deltaTime)
        {
            var s = rig.Settings;
            bool anyAuto = false;
            for (int Index = 0; Index < IkSmoothingProfiles.GroupCount; Index++)
            {
                if (IkSmoothingProfiles.IsAuto(s.SmoothingPreset[Index])) anyAuto = true;
            }

            if (anyAuto)
            {
                ResolveGroupHardware();
            }

            for (int Index = 0; Index < IkSmoothingProfiles.GroupCount; Index++)
            {
                string preset = s.SmoothingPreset[Index];
                if (IkSmoothingProfiles.IsAuto(preset))
                {
                    preset = IkSmoothingProfiles.PresetForHardware(groupHardware[Index]);
                }

                groupOff[Index] = IkSmoothingProfiles.IsOff(preset) || !s.Smoothing;
                IkSmoothingProfile profile;
                float strength;
                if (!IkSmoothingProfiles.TryGetPreset(preset, out profile))
                {
                    profile = new IkSmoothingProfile(s.MinCutoff, s.Beta, s.DerivativeCutoff, s.PositionSmoothingHz,
                        s.RotationSmoothingHz);
                }

                strength = Mathf.Max(0f, s.SmoothingStrength);
                if (strength <= 0.001f)
                {
                    groupOff[Index] = true;
                    strength = 1f;
                }

                float minCutoff = profile.MinCutoff / strength, dCutoff = profile.DerivativeCutoff / strength;
                groupPosTuning[Index] = new float4(minCutoff, profile.Beta, dCutoff,
                    ExpAlpha(profile.PositionHz / strength, deltaTime));
                groupRotTuning[Index] = new float4(minCutoff, profile.Beta, dCutoff,
                    ExpAlpha(profile.RotationHz / strength, deltaTime));
            }
        }

        public void ResetSmoothingState()
        {
            timeAccumulator = 0;
            hasFallbackState = false;
            if (euroPosStates.IsCreated)
                for (int i = 0; i < SlotCount; i++)
                    euroPosStates[i] = default;
            if (euroRotStates.IsCreated)
                for (int i = 0; i < SlotCount; i++)
                    euroRotStates[i] = default;
            if (fallbackRotStates.IsCreated)
                for (int i = 0; i < SlotCount; i++)
                    fallbackRotStates[i] = quaternion.identity;
            if (fallbackPosStates.IsCreated)
                for (int i = 0; i < SlotCount; i++)
                    fallbackPosStates[i] = float3.zero;
            footIKBlendWeightLeft = footIKBlendWeightRight = footIKBlendWeight = 0f;
            stationaryTimer = 0f;
        }

        public Vector3 ApplyHintBias(IkBoneTrackedRole hintRole, Vector3 rawPos, Quaternion rawRot)
        {
            if (rig.Calibration.TryGetHintBias(hintRole, out var localOffset))
            {
                return rawPos + rawRot * localOffset;
            }

            return rawPos;
        }

        static float ExpAlpha(float hz, float dt)
        {
            return 1f - Mathf.Exp(-2f * Mathf.PI * Mathf.Max(0.0001f, hz) * Mathf.Max(0.000001f, dt));
        }

        /// <summary>Upstream OnPlayersHeightChangedNextFrame -> SetHandCollisionScale.</summary>
        public void OnPlayersHeightChanged()
        {
            SetHandCollisionScale(ref IKJob, rig.AvatarToDefaultRatioScaledWithAvatarScale);
        }

        public void SetHandCollisionScale(ref FbikSolver BodyData, float Scale)
        {
            var s = rig.Settings;
            BodyData.handSkin = s.FBIKHandSkin * Scale;
            BodyData.handRadius = s.FBIKHandRadius * Scale;
            BodyData.chestRadius = s.FBIKChestRadius * Scale;
            BodyData.collisionSkin = s.FBIKCollisionSkin * Scale;
            var b = rig.Bones;
            IkCalibratedCoords hips = b.HipsControl.TposeLocalScaled,
                spine = b.SpineControl.TposeLocalScaled,
                chest = b.ChestControl.TposeLocalScaled,
                neck = b.NeckControl.TposeLocalScaled,
                head = b.HeadControl.TposeLocalScaled;
            float minHeadSpineHeight = 0f;
            minHeadSpineHeight += Vector3.Distance(hips.position, spine.position);
            minHeadSpineHeight += Vector3.Distance(spine.position, chest.position);
            minHeadSpineHeight += Vector3.Distance(chest.position, neck.position);
            minHeadSpineHeight += Vector3.Distance(neck.position, head.position);
            BodyData.minHeadSpineHeight = minHeadSpineHeight *
                                          (BodyData.tposeTorsoFitScale > 0f ? BodyData.tposeTorsoFitScale : 1f);
            BodyData.RescaleTposeScalars(FbikSolverSetup.AppliedAvatarScale());
            float chain = BodyData.RalivRestChainLength();
            if (chain > 0f)
            {
                BodyData.minHeadSpineHeight = chain;
            }
        }

        public void Spine()
        {
            if (IKJobCreated)
            {
                IKJob.Destroy();
                IKJobCreated = false;
            }

            unoverriddenValid = false;
            IKJob = default;
            IkAnimationRiggingHelper.CreateFullBodyRig(rig.Model, rig.Settings, ref IKJob);
            IKDataReady = true;
            OnPlayersHeightChanged();
            ref var data = ref IKJob;
            for (int i = 0; i < FbikSolver.Count; i++)
            {
                int slot = i <= (int)HumanBodyBones.RightToes ? i : (int)HumanBodyBones.UpperChest;
                var bone = (HumanBodyBones)slot;
                if (!rig.Model.Has(bone))
                {
                    continue;
                }

                var world = rig.Model
                    .Rot(bone); // avatar root at the origin -> Inverse(AvatarTransform.rotation) == identity
                data.SetWeight(slot, false);
                data.SetOffsetRotation(slot, Quaternion.Inverse(Quaternion.identity) * world);
                data.SetTargetRotation(slot, world);
            }

            data.minFactor = 0.95f;
            data.maxFactor = 1.05f;
            ApplyTuningSettings(ref data);
        }

        void ApplyTuningSettings(ref FbikSolver data)
        {
            var s = rig.Settings;
            var rootUp = rig.localToWorldMatrix.MultiplyVector(Vector3.up);
            data.playerUp = rootUp.sqrMagnitude > 1e-8f ? rootUp.normalized : Vector3.up;
            data.moveBodyBackWhenCrouching = s.FBIKMoveBodyBackWhenCrouching;
            float collisionScale = rig.AvatarToDefaultRatioScaledWithAvatarScale;
            data.collisionsEnabled = s.FBIKCollisionsEnabled;
            data.protectElbow = s.FBIKProtectElbow;
            data.collideTrackedElbow = s.FBIKCollideTrackedElbow;
            data.shoulderSolveEnabled = s.FBIKShoulderSolveEnabled;
            data.shoulderElevationFactor = s.FBIKShoulderElevation;
            data.shoulderProtractionFactor = s.FBIKShoulderProtraction;
            data.shoulderTrackerBlendTime = s.FBIKShoulderTrackerBlend;
            data.armSwivelSmoothTime = s.FBIKArmSwivelSmoothTime;
            data.armSwivelMaxRateDeg = s.FBIKArmSwivelMaxRate;
            data.handRadius = s.FBIKHandRadius * collisionScale;
            data.handSkin = s.FBIKHandSkin * collisionScale;
            data.chestRadius = s.FBIKChestRadius * collisionScale;
            data.collisionSkin = s.FBIKCollisionSkin * collisionScale;
            if (HasRecalibratedRotationOffsets)
            {
                data.offsetRotationHead = RecalibratedHead;
                data.offsetRotationHips = RecalibratedHips;
                data.offsetRotationChest = RecalibratedChest;
                data.offsetRotationLeftFoot = RecalibratedLeftFoot;
                data.offsetRotationRightFoot = RecalibratedRightFoot;
                data.offsetRotationLeftToe = RecalibratedLeftToe;
                data.offsetRotationRightToe = RecalibratedRightToe;
                data.offsetRotationLeftShoulder = RecalibratedLeftShoulder;
                data.offsetRotationRightShoulder = RecalibratedRightShoulder;
            }
        }

        public void DisableAllTrackers()
        {
            trackersDisabled = true;
        }

        public void RestoreAllTrackers()
        {
            trackersDisabled = false;
        }

        public static bool TryFootTargetRotation(Quaternion footRot, Quaternion offset, out Quaternion target)
        {
            target = Quaternion.identity;
            float offSqr = offset.x * offset.x + offset.y * offset.y + offset.z * offset.z + offset.w * offset.w;
            if (!(offSqr > 0.5f)) return false;
            var result = footRot * Quaternion.Inverse(offset);
            float resSqr = result.x * result.x + result.y * result.y + result.z * result.z + result.w * result.w;
            if (!(resSqr > 0.5f)) return false;
            target = result;
            return true;
        }

        static bool HasRigLayer(IkBoneControl control)
        {
            return control.HasRigLayer;
        }

        static float RigWeight(IkBoneControl control)
        {
            if (!control.HasRigLayer) return 0f;
            float w = control.RigLayerWeight;
            return w > 0f ? w < 1f ? w : 1f : 0f;
        }

        public bool TryGetUnoverriddenPose(HumanBodyBones bone, out Vector3 position, out Quaternion rotation)
        {
            int s = FbikSolver.Slot((int)bone);
            if (!unoverriddenValid || s < 0 || s >= FbikSolver.Count || !IKJobCreated ||
                (IKJob.plan.boundSlots & (1u << s)) == 0)
            {
                position = default;
                rotation = Quaternion.identity;
                return false;
            }

            position = unoverriddenPositions[s];
            rotation = s < IKJob.slotOffsets.Length
                ? unoverriddenRotations[s] * Quaternion.Inverse(IKJob.slotOffsets[s])
                : unoverriddenRotations[s];
            return true;
        }

        void CaptureUnoverriddenPose()
        {
            if (!IKJob.solvedPositions.IsCreated || !IKJob.solvedRotations.IsCreated)
            {
                unoverriddenValid = false;
                return;
            }

            IKJob.solvedPositions.CopyTo(unoverriddenPositions);
            IKJob.solvedRotations.CopyTo(unoverriddenRotations);
            unoverriddenValid = true;
        }

        // ---- per frame (SimulateIKDestinations + CompleteIKSolve) -----------------------------------------
        public void SimulateIKDestinations(float deltaTime)
        {
            if (!IKDataReady || !IKJobCreated)
            {
                return;
            }

            timeAccumulator += Mathf.Max(deltaTime, 1e-6f);
            Step01PrepareFilters(deltaTime);
            Step02FootIK(deltaTime);
            Step03GatherTrackerTargets();
            Step04SmoothTrackerTargets(Mathf.Max(deltaTime, 1e-6f));
            Step05JoinFootSim();
            Step06PushTuning();
            Step07BuildFacts(deltaTime);
            Step08FillTorsoTargets();
            Step09FillFootTargets();
            Step10FillGaitPelvis();
            Step11FillKneeHints();
            Step12FillToeTargets();
            Step13FillKneeBendPreferences();
            Step14FillArmTargets();
            Step16Solve(deltaTime);
        }

        void Step01PrepareFilters(float deltaTime)
        {
            EnsureFilterArrays();
            ResolveSmoothingGroups(deltaTime);
        }

        void Step02FootIK(float deltaTime)
        {
            var footDriver = rig.Feet;
            var b = rig.Bones;
            ref var facts = ref frameFacts;
            facts = default;
            bool fbt = rig.Settings.EnableFBT;
            bool leftFoot = fbt && b.LeftFootControl.HasTracked, rightFoot = fbt && b.RightFootControl.HasTracked;
            facts.deltaTime = deltaTime;
            facts.footSimReady = footDriver.IsInitialized;
            facts.footIKSetting = rig.Settings.FootIKEnabled;
            facts.moving = rig.IsLocomoting;
            facts.prone = rig.IsProne;
            facts.seated = rig.Seated;
            facts.upright = true;
            facts.hipsTracked = fbt && b.HipsControl.HasTracked;
            facts.chestTracked = fbt && b.ChestControl.HasTracked;
            facts.chestFollowsBody = facts.chestTracked && b.ChestControl.TrackerFollowsBody;
            facts.leftFootTracked = leftFoot;
            facts.rightFootTracked = rightFoot;
            facts.leftLegTracked = leftFoot || (fbt && b.LeftUpperLegControl.HasTracked);
            facts.rightLegTracked = rightFoot || (fbt && b.RightUpperLegControl.HasTracked);
            facts.leftKneeTracked = fbt && b.LeftLowerLegControl.HasTracked;
            facts.rightKneeTracked = fbt && b.RightLowerLegControl.HasTracked;
            facts.leftElbowTracked = HasRigLayer(b.LeftLowerArmControl);
            facts.rightElbowTracked = HasRigLayer(b.RightLowerArmControl);
            facts.leftShoulderTracked = HasRigLayer(b.LeftShoulderControl);
            facts.rightShoulderTracked = HasRigLayer(b.RightShoulderControl);
            facts.leftToeTracked = HasRigLayer(b.LeftToeControl);
            facts.rightToeTracked = HasRigLayer(b.RightToeControl);
            facts.leftHandWeight = RigWeight(b.LeftHandControl);
            facts.rightHandWeight = RigWeight(b.RightHandControl);
            facts.leftShoulderWeight = RigWeight(b.LeftShoulderControl);
            facts.rightShoulderWeight = RigWeight(b.RightShoulderControl);
            FbikPlanner.FootIK(ref facts, ref stationaryTimer, ref footIKBlendWeightLeft, ref footIKBlendWeightRight,
                out frameFootSimScheduled, out frameFootReengage);
            footIKBlendWeight = Mathf.Min(footIKBlendWeightLeft, footIKBlendWeightRight);
            if (frameFootSimScheduled) footDriver.ScheduleSimulate(deltaTime);
        }

        void Step03GatherTrackerTargets()
        {
            var b = rig.Bones;
            IkCalibratedCoords hipsData = b.HipsControl.OutGoingData,
                headData = b.HeadControl.OutGoingData,
                leftFootData = b.LeftFootControl.OutGoingData,
                rightFootData = b.RightFootControl.OutGoingData,
                chestData = b.ChestControl.OutGoingData,
                leftLLData = b.LeftLowerLegControl.OutGoingData,
                rightLLData = b.RightLowerLegControl.OutGoingData,
                leftHandData = b.LeftHandControl.OutGoingData,
                rightHandData = b.RightHandControl.OutGoingData,
                leftLAData = b.LeftLowerArmControl.OutGoingData,
                rightLAData = b.RightLowerArmControl.OutGoingData,
                leftToeData = b.LeftToeControl.OutGoingData,
                rightToeData = b.RightToeControl.OutGoingData;
            Quaternion leftShoulderRot = b.LeftShoulderControl.OutGoingData.rotation,
                rightShoulderRot = b.RightShoulderControl.OutGoingData.rotation;
            posInputs[sHips] = hipsData.position;
            rotInputs[sHips] = hipsData.rotation;
            posInputs[sHead] = headData.position;
            rotInputs[sHead] = headData.rotation;
            posInputs[sLeftFoot] = leftFootData.position;
            rotInputs[sLeftFoot] = leftFootData.rotation;
            posInputs[sRightFoot] = rightFootData.position;
            rotInputs[sRightFoot] = rightFootData.rotation;
            posInputs[sChest] = chestData.position;
            rotInputs[sChest] = chestData.rotation;
            posInputs[sLeftLowerLeg] = leftLLData.position;
            rotInputs[sLeftLowerLeg] = leftLLData.rotation;
            posInputs[sRightLowerLeg] = rightLLData.position;
            rotInputs[sRightLowerLeg] = rightLLData.rotation;
            posInputs[sLeftHand] = leftHandData.position;
            rotInputs[sLeftHand] = leftHandData.rotation;
            posInputs[sRightHand] = rightHandData.position;
            rotInputs[sRightHand] = rightHandData.rotation;
            posInputs[sLeftLowerArm] = leftLAData.position;
            rotInputs[sLeftLowerArm] = leftLAData.rotation;
            posInputs[sRightLowerArm] = rightLAData.position;
            rotInputs[sRightLowerArm] = rightLAData.rotation;
            posInputs[sLeftToe] = leftToeData.position;
            rotInputs[sLeftToe] = leftToeData.rotation;
            posInputs[sRightToe] = rightToeData.position;
            rotInputs[sRightToe] = rightToeData.rotation;
            posInputs[sLeftShoulder] = float3.zero;
            rotInputs[sLeftShoulder] = leftShoulderRot;
            posInputs[sRightShoulder] = float3.zero;
            rotInputs[sRightShoulder] = rightShoulderRot;
            for (int i = 0; i < SlotCount; i++)
            {
                byte group = IkSmoothingProfiles.SlotGroup[i];
                posTuning[i] = groupPosTuning[group];
                rotTuning[i] = groupRotTuning[group];
                byte newPosMode = (byte)IkFilterMode.Passthrough;
                byte newRotMode = (byte)IkFilterMode.Passthrough;
                if (!groupOff[group] && SlotTracked(in frameFacts, i))
                {
                    newPosMode = PickMode(SmoothPos[i], EuroPos[i]);
                    newRotMode = PickMode(SmoothRot[i], EuroRot[i]);
                }

                if (newPosMode != posModeNative[i])
                {
                    euroPosStates[i] = default;
                    fallbackPosStates[i] = posInputs[i];
                }

                if (newRotMode != rotModeNative[i])
                {
                    euroRotStates[i] = default;
                    fallbackRotStates[i] = rotInputs[i];
                }

                posModeNative[i] = newPosMode;
                rotModeNative[i] = newRotMode;
            }

            posModeNative[sLeftShoulder] = (byte)IkFilterMode.Passthrough;
            posModeNative[sRightShoulder] = (byte)IkFilterMode.Passthrough;
            if (!hasFallbackState)
            {
                hasFallbackState = true;
                fallbackPosStates.CopyFrom(posInputs);
                fallbackRotStates.CopyFrom(rotInputs);
            }
        }

        void Step04SmoothTrackerTargets(float safeDt)
        {
            var playspaceMatrix = rig.localToWorldMatrix;
            var posJob = new IkBatchPositionFilterJob
            {
                mode = posModeNative, rawInputs = posInputs, tuning = posTuning, euroStates = euroPosStates,
                fallbackStates = fallbackPosStates, outputs = posOutputs, dt = safeDt,
                playspaceToWorld = playspaceMatrix
            };
            var rotJob = new IkBatchRotationFilterJob
            {
                mode = rotModeNative, rawInputs = rotInputs, tuning = rotTuning, euroStates = euroRotStates,
                fallbackStates = fallbackRotStates, outputs = rotOutputs, dt = safeDt,
                playspaceRotation = playspaceMatrix.rotation
            };
            posJob.Run(SlotCount);
            rotJob.Run(SlotCount);
        }

        void Step05JoinFootSim()
        {
            var footDriver = rig.Feet;
            if (frameFootSimScheduled) footDriver.CompleteSimulate();
            if (frameFootReengage) footDriver.NotifyReEngaging();
            if (frameFootSimScheduled) footDriver.ScheduleSurfaceProbes();
        }

        void Step06PushTuning()
        {
            ApplyTuningSettings(ref IKJob);
        }

        void Step07BuildFacts(float deltaTime)
        {
            ref var data = ref IKJob;
            ref var facts = ref frameFacts;
            var footDriver = rig.Feet;
            var playspace = rig.localToWorldMatrix;
            var playerUpScaled = playspace.MultiplyVector(Vector3.up);
            framePlayerUpScale = playerUpScaled.magnitude;
            framePlayerUpDirection = framePlayerUpScale > 1e-6f ? playerUpScaled / framePlayerUpScale : Vector3.up;
            if (trackersDisabled)
            {
                facts.hipsTracked = facts.chestTracked = facts.chestFollowsBody = facts.leftLegTracked =
                    facts.rightLegTracked = facts.leftFootTracked =
                        facts.rightFootTracked = facts.leftKneeTracked = facts.rightKneeTracked = false;
                facts.leftElbowTracked = facts.rightElbowTracked = facts.leftShoulderTracked =
                    facts.rightShoulderTracked = facts.leftToeTracked = facts.rightToeTracked = false;
            }

            facts.leftSimFootRotation = FbikPlanner.footRotationFromSim && facts.footSimReady &&
                                        TryFootTargetRotation(footDriver.LeftFootRotation, data.offsetRotationLeftFoot,
                                            out frameLeftSimFootRotation);
            facts.rightSimFootRotation = FbikPlanner.footRotationFromSim && facts.footSimReady &&
                                         TryFootTargetRotation(footDriver.RightFootRotation,
                                             data.offsetRotationRightFoot, out frameRightSimFootRotation);
            facts.leftToeBend = facts.footSimReady &&
                                Mathf.Abs(footDriver.LeftToeBendDegrees * footIKBlendWeightLeft) >= 0.01f &&
                                footDriver.LeftToeBendAxis.sqrMagnitude >= 1e-6f;
            facts.rightToeBend = facts.footSimReady &&
                                 Mathf.Abs(footDriver.RightToeBendDegrees * footIKBlendWeightRight) >= 0.01f &&
                                 footDriver.RightToeBendAxis.sqrMagnitude >= 1e-6f;
            facts.leftElbowRoll =
                rig.Calibration.TryGetLimbRoll(IkBoneTrackedRole.LeftLowerArm, out frameLeftElbowToBone);
            facts.rightElbowRoll =
                rig.Calibration.TryGetLimbRoll(IkBoneTrackedRole.RightLowerArm, out frameRightElbowToBone);
            facts.leftKneeRoll =
                rig.Calibration.TryGetLimbRoll(IkBoneTrackedRole.LeftLowerLeg, out frameLeftKneeToBone);
            facts.rightKneeRoll =
                rig.Calibration.TryGetLimbRoll(IkBoneTrackedRole.RightLowerLeg, out frameRightKneeToBone);
            frameHipsRotation = rotOutputs[sHips];
            FbikPlanner.Frame(ref data, in facts);
        }

        void Step08FillTorsoTargets()
        {
            ref var data = ref IKJob;
            var b = rig.Bones;
            Vector3 hipsPos = posOutputs[sHips];
            hipsPos -= framePlayerUpDirection * rig.LandingCrouchEffect;
            data.targetPositionHips = hipsPos;
            data.targetRotationHips = frameHipsRotation;
            data.targetPositionHead = posOutputs[sHead];
            data.targetRotationHead = rotOutputs[sHead];
            float restHeadLocalY = b.HeadControl.TposeLocalScaled.position.y;
            data.standingHeadHeight = Mathf.Max(0f, restHeadLocalY * framePlayerUpScale);
            if (framePlayerUpScale <= 1e-6f)
            {
                data.crouchDepth = 0f;
            }
            else
            {
                var worldToPlayer = rig.localToWorldMatrix.inverse;
                float headDrop = restHeadLocalY - worldToPlayer.MultiplyPoint3x4(posOutputs[sHead]).y;
                float eyeDrop = b.EyeControl.TposeLocalScaled.position.y -
                                worldToPlayer.MultiplyPoint3x4(b.EyeControl.OutgoingWorldData.position).y;
                data.crouchDepth = Mathf.Max(0f, Mathf.Min(headDrop, eyeDrop) * framePlayerUpScale);
            }

            Vector3 chestPos = posOutputs[sChest];
            Quaternion chestRot = rotOutputs[sChest];
            data.targetPositionChestRaw = chestPos;
            data.targetPositionChest = chestPos;
            data.targetRotationChest = chestRot;
        }

        void Step09FillFootTargets()
        {
            ref var data = ref IKJob;
            var footDriver = rig.Feet;
            switch (data.plan.leftLeg.target)
            {
                case FbikSource.Tracker:
                    data.targetPositionLeftLowerLeg = posOutputs[sLeftFoot];
                    data.targetRotationLeftLowerLeg = rotOutputs[sLeftFoot];
                    break;
                case FbikSource.Sim:
                    data.targetPositionLeftLowerLeg = footDriver.LeftFootPosition;
                    if (!data.plan.leftLeg.preserveTip) data.targetRotationLeftLowerLeg = frameLeftSimFootRotation;
                    break;
            }

            switch (data.plan.rightLeg.target)
            {
                case FbikSource.Tracker:
                    data.targetPositionRightLowerLeg = posOutputs[sRightFoot];
                    data.targetRotationRightLowerLeg = rotOutputs[sRightFoot];
                    break;
                case FbikSource.Sim:
                    data.targetPositionRightLowerLeg = footDriver.RightFootPosition;
                    if (!data.plan.rightLeg.preserveTip) data.targetRotationRightLowerLeg = frameRightSimFootRotation;
                    break;
            }
        }

        void Step10FillGaitPelvis()
        {
            if (!IKJob.plan.gaitPelvis)
            {
                return;
            }

            ref var data = ref IKJob;
            var footDriver = rig.Feet;
            data.targetPositionHips += framePlayerUpDirection * (footDriver.ComputeHipBob() * footIKBlendWeight);
            data.targetPositionHips += footDriver.ComputeHipSway() * footIKBlendWeight;
            var pelvis = Quaternion.Slerp(Quaternion.identity, footDriver.ComputePelvisDelta(), footIKBlendWeight);
            data.targetRotationHips = pelvis * data.targetRotationHips;
        }

        void Step11FillKneeHints()
        {
            ref var data = ref IKJob;
            if (data.plan.leftLeg.kneeTracked)
                data.hintPositionLeftLowerLeg = ApplyHintBias(IkBoneTrackedRole.LeftLowerLeg, posOutputs[sLeftLowerLeg],
                    rotOutputs[sLeftLowerLeg]);
            if (data.plan.rightLeg.kneeTracked)
                data.hintPositionRightLowerLeg = ApplyHintBias(IkBoneTrackedRole.RightLowerLeg,
                    posOutputs[sRightLowerLeg], rotOutputs[sRightLowerLeg]);
        }

        void Step12FillToeTargets()
        {
            ref var data = ref IKJob;
            var footDriver = rig.Feet;
            data.leftDrivenTargetRot = rotOutputs[sLeftToe];
            data.rightDrivenTargetRot = rotOutputs[sRightToe];
            if (data.plan.leftLeg.target == FbikSource.Sim)
            {
                data.leftToeBendDeg = footDriver.LeftToeBendDegrees * footIKBlendWeightLeft;
                data.leftToeBendAxis = footDriver.LeftToeBendAxis;
            }
            else
            {
                data.leftToeBendDeg = 0f;
                data.leftToeBendAxis = Vector3.zero;
            }

            if (data.plan.rightLeg.target == FbikSource.Sim)
            {
                data.rightToeBendDeg = footDriver.RightToeBendDegrees * footIKBlendWeightRight;
                data.rightToeBendAxis = footDriver.RightToeBendAxis;
            }
            else
            {
                data.rightToeBendDeg = 0f;
                data.rightToeBendAxis = Vector3.zero;
            }
        }

        void Step13FillKneeBendPreferences()
        {
            ref var data = ref IKJob;
            var b = rig.Bones;
            Quaternion leftLowerLeg = b.LeftLowerLegControl.OutgoingWorldData.rotation,
                rightLowerLeg = b.RightLowerLegControl.OutgoingWorldData.rotation;
            data.hintRotationLeftLowerLeg =
                frameFacts.leftKneeRoll ? leftLowerLeg * frameLeftKneeToBone : Quaternion.identity;
            data.hintRotationRightLowerLeg =
                frameFacts.rightKneeRoll ? rightLowerLeg * frameRightKneeToBone : Quaternion.identity;
            var hipsRight = frameHipsRotation * Vector3.right;
            data.kneeAnteriorRef = hipsRight;
            data.kneeBendPrefLeft = hipsRight;
            data.kneeBendPrefRight = hipsRight;
        }

        void Step14FillArmTargets()
        {
            ref var data = ref IKJob;
            data.targetPositionLeftHand = posOutputs[sLeftHand];
            data.targetRotationLeftHand = rotOutputs[sLeftHand];
            data.targetPositionRightHand = posOutputs[sRightHand];
            data.targetRotationRightHand = rotOutputs[sRightHand];
            Vector3 llaPos = posOutputs[sLeftLowerArm];
            Quaternion llaRot = rotOutputs[sLeftLowerArm];
            data.hintPositionLeftHand = llaPos;
            data.hintRotationLeftHand = frameFacts.leftElbowRoll ? llaRot * frameLeftElbowToBone : Quaternion.identity;
            Vector3 rlaPos = posOutputs[sRightLowerArm];
            Quaternion rlaRot = rotOutputs[sRightLowerArm];
            data.hintPositionRightHand = rlaPos;
            data.hintRotationRightHand =
                frameFacts.rightElbowRoll ? rlaRot * frameRightElbowToBone : Quaternion.identity;
            data.targetRotationLeftShoulder = rotOutputs[sLeftShoulder];
            data.targetRotationRightShoulder = rotOutputs[sRightShoulder];
        }

        /// <summary>Step15/16 + CompleteIKSolve: base pose -> fit -> solve, synchronously.</summary>
        void Step16Solve(float deltaTime)
        {
            var skeleton = rig.Model.Skeleton;
            if (!RigLayerActive || !IKJobCreated || !skeleton.IsCreated)
            {
                return;
            }

            skeleton.SetRootLocal(rig.AvatarRootPosition, rig.AvatarRootRotation);
            skeleton.GatherNow();
            skeleton.ApplyFit();
            IKJob.poseStream = skeleton.Stream;
            IKJob.poseStream.deltaTime = deltaTime;
            IKJob.Execute();
            CaptureUnoverriddenPose();
        }
    }
}
