// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Context object replacing the IkLocalPlayer / IkLocalAvatarDriver / IkHeightDriver / device singletons
// for one avatar. Holds the bone controls, rig driver, virtual spine, foot driver and calibration, and ports:
//  * IkLocalAvatarDriver.Calibration path: CalculateTransformPositions/SetInitialData, ComputeOffsets (locks),
//    SetBodySettings, BuildBuilder, foot InitializeVariables.
//  * IkHeightDriver.ApplyScaleAndHeight / CapturePlayerHeight / ResolveHeightMode (scale + grounding).
//  * IkInput / IkOpenVRInputSpatial / IkOpenVRInputController / IkOpenXRHandInput pose conversion
//    (device -> unscaled -> scaled -> bone control incoming), incl. HMD centre-eye offset and controller IK offsets.
//  * IkAvatarStageCalibration.FullBodyCalibration (without the tracker-role classifier).
//  * IkLocalPlayer.Simulate frame order: devices -> virtual spine -> bone sim -> IK destinations -> solve.

using System.Collections.Generic;

namespace VRMR.Fbik
{
    /// <summary>How a hand device pose is turned into the solver's hand target.</summary>
    public enum IkHandSource
    {
        /// <summary>SteamVR/OpenVR controller raw pose (IkOpenVRInputController math).</summary>
        OpenVRController,

        /// <summary>OpenXR palm pose (IkOpenXRHandInput palm fallback math).</summary>
        OpenXRPalm,

        /// <summary>The pose already is the avatar hand bone's world pose (e.g. VRChat's wrist).</summary>
        HandBone
    }

    public sealed class FbikRig
    {
        static readonly IkBoneTrackedRole[] ControlRoles =
        {
            IkBoneTrackedRole.CenterEye, IkBoneTrackedRole.Head, IkBoneTrackedRole.Neck, IkBoneTrackedRole.Chest,
            IkBoneTrackedRole.Hips, IkBoneTrackedRole.Spine,
            IkBoneTrackedRole.LeftUpperLeg, IkBoneTrackedRole.RightUpperLeg, IkBoneTrackedRole.LeftLowerLeg,
            IkBoneTrackedRole.RightLowerLeg, IkBoneTrackedRole.LeftFoot, IkBoneTrackedRole.RightFoot,
            IkBoneTrackedRole.LeftShoulder, IkBoneTrackedRole.RightShoulder, IkBoneTrackedRole.LeftUpperArm,
            IkBoneTrackedRole.RightUpperArm, IkBoneTrackedRole.LeftLowerArm, IkBoneTrackedRole.RightLowerArm,
            IkBoneTrackedRole.LeftHand, IkBoneTrackedRole.RightHand, IkBoneTrackedRole.LeftToes,
            IkBoneTrackedRole.RightToes, IkBoneTrackedRole.Mouth
        };

        public readonly IkBoneDriver Bones;
        public readonly FbikCalibration Calibration;
        public readonly DeviceState[] Devices = new DeviceState[IkBoneDriver.RoleCount];
        public readonly IkFootDriver Feet;
        public readonly IkAvatarModel Model;
        public readonly IkRigDriver Rig;
        public readonly IkTrackingHardware[] RoleHardware = new IkTrackingHardware[IkBoneDriver.RoleCount];
        public readonly FbikSettings Settings;
        public readonly IkVirtualSpineDriver VirtualSpine;

        // IkHeightDriver state
        public float DeviceScale = 1f,
            HeightModeGroundingOffset,
            AppliedUniformScale,
            AppliedUpScale = 1f,
            ScaledToMatchValue = 1f;

        /// <summary>Ground plane point (world). Defaults to the play-space floor.</summary>
        public Vector3 GroundPoint = Vector3.zero;

        public bool HasGenuinePlayerEyeHeight;
        public bool HasGround = true;

        /// <summary>IkInput.CenterEyeOffset of the HMD (head-local tracking-space offset to the centre eye).</summary>
        public Vector3 HmdCenterEyeOffset = Vector3.zero;

        public bool IsLocomoting, IsProne, Seated;
        public bool IsVR = true;
        public float LandingCrouchEffect = 0f;

        public IkScaleFitResult LastScaleFit = IkScaleFitResult.Invalid;

        // OpenXR palm fallback (IkOpenXRHandInput).
        public Vector3 LeftHandPalmCorrection = new(0, 0, -90), RightHandPalmCorrection = new(0, 0, -90);

        /// <summary>
        ///     SteamVR skeleton wrist bone (skeletonAction.bonePositions/boneRotations[wrist]) per hand; zero/identity until
        ///     supplied (the upstream "not sampled" fallback).
        /// </summary>
        public Vector3 LeftWristLocalPosition = Vector3.zero, RightWristLocalPosition = Vector3.zero;

        public Quaternion LeftWristLocalRotation = Quaternion.identity, RightWristLocalRotation = Quaternion.identity;

        public Vector3 OpenXRLeftHandToIKPositionOffset = new(0, 0, -0.05f),
            OpenXRRightHandToIKPositionOffset = new(0, 0, -0.05f);

        public float PlayerCenterEyeVerticalOffset, PlayerHipHeight, PlayerEyeToHipDrop;

        public float PlayerEyeHeight = IkHeightConstants.FallbackHeightInMeters,
            PlayerArmSpan = IkHeightConstants.FallbackHeightInMeters;

        public Vector3 PlayspacePosition = Vector3.zero;
        public Quaternion PlayspaceRotation = Quaternion.identity;
        public float PlayspaceVerticalOffset = 0f; // IkLocalPlayspaceMover.VerticalOffset

        public float ScaledAvatarEyeHeight = IkHeightConstants.FallbackHeightInMeters,
            AvatarToDefaultRatioScaledWithAvatarScale = 1f;

        public float StatedEyeHeight = 0f; // IkStatedHeight.ImpliedEyeHeight (0 = not stated)
        public bool UseCustomGround;
        public bool UseIKPositionOffset = true;

        /// <summary>
        ///     VRMR port addition: estimate the floor from tracker heights at calibration (upstream default). Off when the
        ///     floor is known.
        /// </summary>
        public bool UseTrackedFloorEstimate = true;

        /// <summary>VRMR port addition: controller-local left hand position in solver metres. Right mirrors X.</summary>
        public Vector3? VrmrLeftControllerHandOffset;

        /// <summary>
        ///     VRMR port addition: no height-mode grounding lift. The caller puts the real floor at y = 0 and chooses the
        ///     calibration head height itself (VRChat hangs the avatar from the headset at calibration instead of standing it on
        ///     the floor).
        /// </summary>
        public bool VrmrNoGrounding;

        bool hasSolved;
        bool? heldAutoLowerBody;

        Vector3[] lastSolvedPos;
        Quaternion[] lastSolvedRot;

        public Vector3 leftHandToIKPositionOffset = new(0.02f, 0.08f, 0.02f);

        // OpenVR controller IK offsets (IkOpenVRInputController.Initialize).
        public Vector3 leftHandToIKRotationOffset = new(80, 69, 150);

        // IkLocalPlayer.localToWorldMatrix (play space -> world) and the avatar root (child of the player at identity).
        public Matrix4x4 localToWorldMatrix = Matrix4x4.identity;
        public Vector3 rightHandToIKPositionOffset = new(-0.02f, 0.08f, 0.02f);
        public Vector3 rightHandToIKRotationOffset = new(112, 80, 198);

        public FbikRig(IkAvatarModel model, FbikSettings settings)
        {
            Model = model;
            Settings = settings ?? new FbikSettings();
            Bones = new IkBoneDriver();
            Calibration = new FbikCalibration(this);
            Rig = new IkRigDriver(this);
            Feet = new IkFootDriver(this);
            VirtualSpine = new IkVirtualSpineDriver(this);
            for (int i = 0; i < RoleHardware.Length; i++) RoleHardware[i] = IkTrackingHardware.Lighthouse;
            for (int i = 0; i < Devices.Length; i++)
            {
                Devices[i].RawRotation = Quaternion.identity;
                Devices[i].DeviceOffsetRotation = Quaternion.identity;
                Devices[i].UnscaledRotation = Quaternion.identity;
                Devices[i].FinalScaledRotation = Quaternion.identity;
            }

            UpdatePlayspace();
            VirtualSpine.Initialize();
            AvatarCalibration();
        }

        public Vector3 AvatarRootPosition => PlayspacePosition;
        public Quaternion AvatarRootRotation => PlayspaceRotation;
        public IkBodyFitResult AppliedBodyFit => Rig.AppliedBodyFit;

        public float SizingEyeHeight =>
            IkCalibrationMath.SizingEyeHeight(PlayerEyeHeight, PlayerArmSpan, false, StatedEyeHeight);

        // ---- IkHeightDriver -------------------------------------------------------------------------------
        bool HasHipsFBIKTracker => Bones.HipsControl.HasTracked;

        bool HasLegFBIKTrackers => Bones.LeftFootControl.HasTracked || Bones.RightFootControl.HasTracked ||
                                   Bones.LeftLowerLegControl.HasTracked || Bones.RightLowerLegControl.HasTracked ||
                                   Bones.LeftUpperLegControl.HasTracked || Bones.RightUpperLegControl.HasTracked;

        public void SetPlayspace(Vector3 position, Quaternion rotation)
        {
            PlayspacePosition = position;
            PlayspaceRotation = Quaternion.Normalize(rotation);
            UpdatePlayspace();
        }

        void UpdatePlayspace()
        {
            localToWorldMatrix = Matrix4x4.TRS(PlayspacePosition, PlayspaceRotation, Vector3.one);
            if (!UseCustomGround) GroundPoint = localToWorldMatrix.MultiplyPoint3x4(Vector3.zero);
        }

        // ---- IkLocalAvatarDriver.Calibration ----------------------------------------------------------------
        void AvatarCalibration()
        {
            CalculateTransformPositions();
            ComputeOffsets();
            ApplyAvatarScale(1f);
            Bones.SimulateWithoutLerp(localToWorldMatrix, 1f / 90f);
            Rig.SetBodySettings();
            Feet.InitializeVariables();
            Bones.HeadControl.HasRigLayer = true;
            Bones.HipsControl.HasRigLayer = true;
            Bones.SpineControl.HasRigLayer = true;
            Rig.BuildBuilder();
            CapturePlayerHeight(false);
            ApplyScaleAndHeight();
        }

        void CalculateTransformPositions()
        {
            var rootRot = Quaternion.identity;
            foreach (var role in ControlRoles)
            {
                var control = Bones.Find(role);
                Vector3 world;
                Quaternion worldRot;
                if (role == IkBoneTrackedRole.CenterEye)
                {
                    world = Model.EyeLocal;
                    worldRot = rootRot;
                    control.Exists = true;
                }
                else if (role == IkBoneTrackedRole.Mouth)
                {
                    world = Model.Pos(HumanBodyBones.Head);
                    worldRot = rootRot;
                    control.Exists = false;
                }
                else if (IkBoneTrackedRoleCommonCheck.TryConvertToHumanoidRole(role, out var human) && Model.Has(human))
                {
                    world = Model.Pos(human);
                    worldRot = Model.Rot(human);
                    control.Exists = true;
                }
                else
                {
                    world = FallbackPosition(role);
                    worldRot = rootRot;
                    control.Exists = false;
                }

                SetInitialData(control, role, world, worldRot);
            }
        }

        /// <summary>Port-specific fallback for bones the avatar lacks (upstream uses its IkFallBackBoneData asset).</summary>
        Vector3 FallbackPosition(IkBoneTrackedRole role)
        {
            Vector3 P(HumanBodyBones b)
            {
                return Model.Pos(b);
            }

            bool H(HumanBodyBones b)
            {
                return Model.Has(b);
            }

            Vector3 head = P(HumanBodyBones.Head), hips = P(HumanBodyBones.Hips);
            var chest = H(HumanBodyBones.UpperChest) ? P(HumanBodyBones.UpperChest) :
                H(HumanBodyBones.Chest) ? P(HumanBodyBones.Chest) : Vector3.Lerp(hips, head, 0.55f);
            switch (role)
            {
                case IkBoneTrackedRole.Neck: return Vector3.Lerp(chest, head, 0.6f);
                case IkBoneTrackedRole.Chest:
                    return Vector3.Lerp(H(HumanBodyBones.Spine) ? P(HumanBodyBones.Spine) : hips,
                        H(HumanBodyBones.Neck) ? P(HumanBodyBones.Neck) : head, 0.45f);
                case IkBoneTrackedRole.Spine: return Vector3.Lerp(hips, chest, 0.45f);
                case IkBoneTrackedRole.LeftShoulder:
                    return H(HumanBodyBones.LeftUpperArm)
                        ? Vector3.Lerp(
                            new Vector3(0f, P(HumanBodyBones.LeftUpperArm).y, P(HumanBodyBones.LeftUpperArm).z),
                            P(HumanBodyBones.LeftUpperArm), 0.3f)
                        : chest;
                case IkBoneTrackedRole.RightShoulder:
                    return H(HumanBodyBones.RightUpperArm)
                        ? Vector3.Lerp(
                            new Vector3(0f, P(HumanBodyBones.RightUpperArm).y, P(HumanBodyBones.RightUpperArm).z),
                            P(HumanBodyBones.RightUpperArm), 0.3f)
                        : chest;
                case IkBoneTrackedRole.LeftToes:
                    return H(HumanBodyBones.LeftFoot)
                        ? new Vector3(P(HumanBodyBones.LeftFoot).x, Model.SoleHeight + 0.01f,
                            P(HumanBodyBones.LeftFoot).z + 0.08f * Model.AuthoredEyeHeight)
                        : hips;
                case IkBoneTrackedRole.RightToes:
                    return H(HumanBodyBones.RightFoot)
                        ? new Vector3(P(HumanBodyBones.RightFoot).x, Model.SoleHeight + 0.01f,
                            P(HumanBodyBones.RightFoot).z + 0.08f * Model.AuthoredEyeHeight)
                        : hips;
                default: return new Vector3(0f, Model.AuthoredEyeHeight * 0.5f, 0f);
            }
        }

        void SetInitialData(IkBoneControl bone, IkBoneTrackedRole role, Vector3 worldTpose,
            Quaternion worldTposeRotation)
        {
            var outgoingPosition = worldTpose; // avatar root at the origin
            outgoingPosition.y -= Model.SoleHeight * FbikSolverSetup.AppliedAvatarScale();
            var outgoingRotation = worldTposeRotation;
            if (IkBoneTrackedRoleCommonCheck.IsApartOfSpineVertical(role))
            {
                outgoingPosition.x = 0;
            }

            bone.SetOutgoing(outgoingPosition, outgoingRotation);
            bone.SetTposeLocal(outgoingPosition, outgoingRotation);
            bone.SetTposeScaled(outgoingPosition, outgoingRotation);
        }

        void ComputeOffsets()
        {
            Bones.SetAndCreateLock(IkBoneTrackedRole.CenterEye, IkBoneTrackedRole.Head);
            Bones.SetAndCreateLock(IkBoneTrackedRole.Head, IkBoneTrackedRole.Neck);
            Bones.SetAndCreateLock(IkBoneTrackedRole.Head, IkBoneTrackedRole.Mouth);
            Bones.SetAndCreateLock(IkBoneTrackedRole.Neck, IkBoneTrackedRole.Chest);
            Bones.SetAndCreateLock(IkBoneTrackedRole.Chest, IkBoneTrackedRole.Spine);
            Bones.SetAndCreateLock(IkBoneTrackedRole.Spine, IkBoneTrackedRole.Hips);
            Bones.SetAndCreateLock(IkBoneTrackedRole.Chest, IkBoneTrackedRole.LeftShoulder);
            Bones.SetAndCreateLock(IkBoneTrackedRole.Chest, IkBoneTrackedRole.RightShoulder);
            Bones.SetAndCreateLock(IkBoneTrackedRole.LeftShoulder, IkBoneTrackedRole.LeftUpperArm);
            Bones.SetAndCreateLock(IkBoneTrackedRole.RightShoulder, IkBoneTrackedRole.RightUpperArm);
            Bones.SetAndCreateLock(IkBoneTrackedRole.LeftUpperArm, IkBoneTrackedRole.LeftLowerArm);
            Bones.SetAndCreateLock(IkBoneTrackedRole.RightUpperArm, IkBoneTrackedRole.RightLowerArm);
            Bones.SetAndCreateLock(IkBoneTrackedRole.LeftLowerArm, IkBoneTrackedRole.LeftHand);
            Bones.SetAndCreateLock(IkBoneTrackedRole.RightLowerArm, IkBoneTrackedRole.RightHand);
            Bones.SetAndCreateLock(IkBoneTrackedRole.Hips, IkBoneTrackedRole.LeftUpperLeg);
            Bones.SetAndCreateLock(IkBoneTrackedRole.Hips, IkBoneTrackedRole.RightUpperLeg);
            Bones.SetAndCreateLock(IkBoneTrackedRole.LeftUpperLeg, IkBoneTrackedRole.LeftLowerLeg);
            Bones.SetAndCreateLock(IkBoneTrackedRole.RightUpperLeg, IkBoneTrackedRole.RightLowerLeg);
            Bones.SetAndCreateLock(IkBoneTrackedRole.LeftLowerLeg, IkBoneTrackedRole.LeftFoot);
            Bones.SetAndCreateLock(IkBoneTrackedRole.RightLowerLeg, IkBoneTrackedRole.RightFoot);
            Bones.SetAndCreateLock(IkBoneTrackedRole.LeftFoot, IkBoneTrackedRole.LeftToes);
            Bones.SetAndCreateLock(IkBoneTrackedRole.RightFoot, IkBoneTrackedRole.RightToes);
        }

        /// <summary>IkHeightDriver.ApplyAvatarScale.</summary>
        void ApplyAvatarScale(float scaleFactor)
        {
            for (int i = 0; i < Bones.ControlsLength; i++)
            {
                var c = Bones.Controls[i];
                c.SetTposeScaled(c.TposeLocal.position * scaleFactor, c.TposeLocal.rotation);
                c.ScaledOffset = c.Offset * scaleFactor;
            }
        }

        public IkSelectedHeightMode ResolveHeightMode(IkSelectedHeightMode mode)
        {
            if (mode != IkSelectedHeightMode.Auto)
            {
                return mode;
            }

            bool hasLowerBodyTrackers = heldAutoLowerBody ?? (HasHipsFBIKTracker || HasLegFBIKTrackers);
            return hasLowerBodyTrackers ? IkSelectedHeightMode.EyeHeight : IkSelectedHeightMode.ArmSpan;
        }

        public float EstimatedPlayerArmSpan()
        {
            float eye = StatedEyeHeight > 0f
                ? StatedEyeHeight
                : SanitizePositive(PlayerEyeHeight, IkHeightConstants.FallbackHeightInMeters);
            return IkCalibrationMath.SpanFromArmRatio(eye, Settings.ArmRatio);
        }

        IkScaleInputs CurrentScaleInputs()
        {
            bool fitEnabled = Settings.FBIKBodyFit;
            return new IkScaleInputs
            {
                Mode = ResolveHeightMode(Settings.HeightMode),
                Desktop = !IsVR,
                Seated = Seated,
                SpanUsable = true,
                PlayerEye = SanitizePositive(SizingEyeHeight, IkHeightConstants.FallbackHeightInMeters),
                PlayerTrackedEye = SanitizePositive(PlayerEyeHeight, IkHeightConstants.FallbackHeightInMeters),
                PlayerSpan = SanitizePositive(PlayerArmSpan, IkHeightConstants.FallbackHeightInMeters),
                PlayerEyeOffset = PlayerCenterEyeVerticalOffset,
                AvatarEye = SanitizePositive(Model.AvatarEyeHeight, IkHeightConstants.FallbackHeightInMeters),
                AvatarSpan = SanitizePositive(Model.AvatarArmSpan, IkHeightConstants.FallbackHeightInMeters),
                AvatarShoulderWidth = Model.AvatarShoulderWidth,
                AppliedUpScale = SanitizePositive(AppliedUpScale, 1f),
                FitDeviation = fitEnabled
                    ? Mathf.Clamp(Settings.FBIKBodyFitMaxDeviation, 0f, IkBodyFitCore.MaxDeviationCeiling)
                    : 0f
            };
        }

        static float SanitizePositive(float v, float fallback)
        {
            return float.IsNaN(v) || float.IsInfinity(v) || v <= 0f ? fallback : v;
        }

        /// <summary>
        ///     IkHeightDriver.ApplyScaleAndHeight: span estimate -> uniform scale -> body fit -> device scale/grounding ->
        ///     height-changed callbacks -> offset reprojection.
        /// </summary>
        public void ApplyScaleAndHeight()
        {
            PlayerArmSpan = EstimatedPlayerArmSpan();
            var state = IkScaleResolver.Resolve(CurrentScaleInputs());
            LastScaleFit = state.BestFit;
            AppliedUniformScale = state.UniformScale;
            Rig.RefreshBodyFit();
            var inputs = CurrentScaleInputs();
            inputs.AppliedUpScale = AppliedUpScale;
            state = IkScaleResolver.Resolve(inputs);
            ScaledAvatarEyeHeight = SanitizePositive(state.ScaledAvatarEye, IkHeightConstants.FallbackHeightInMeters);
            AvatarToDefaultRatioScaledWithAvatarScale =
                ScaledAvatarEyeHeight / IkHeightConstants.FallbackHeightInMeters;
            DeviceScale = SanitizePositive(state.DeviceScale, 1f);
            AppliedUniformScale = state.UniformScale;
            HeightModeGroundingOffset = VrmrNoGrounding ? 0f : state.GroundingOffset;
            // OnPlayersHeightChangedNextFrame listeners (fired immediately in the port).
            Rig.OnPlayersHeightChanged();
            VirtualSpine.OnHeightChanged();
            Feet.OnHeightChanged();
            Calibration.ReprojectTrackerOffsetsForCurrentAvatar();
        }

        /// <summary>IkHeightDriver.CapturePlayerHeight / IkLocalHeightCalculator.CalculatePlayerEyeHeight.</summary>
        public void CapturePlayerHeight(bool recaptureEyeHeight)
        {
            if (recaptureEyeHeight || !HasGenuinePlayerEyeHeight)
            {
                CalculatePlayerEyeHeight();
            }

            PlayerEyeHeight = SanitizePositive(PlayerEyeHeight, IkHeightConstants.FallbackHeightInMeters);
            PlayerArmSpan = EstimatedPlayerArmSpan();
        }

        void CalculatePlayerEyeHeight()
        {
            var hmd = Devices[(int)IkBoneTrackedRole.CenterEye];
            PlayerCenterEyeVerticalOffset = HmdCenterEyeOffset.y;
            bool genuine = true;
            if (Seated)
            {
                PlayerCenterEyeVerticalOffset = 0f;
                PlayerEyeHeight = StatedEyeHeight > 0f ? StatedEyeHeight : IkHeightConstants.FallbackHeightInMeters;
                genuine = false;
            }
            else if (hmd.Present && hmd.HasPose)
            {
                float rawEyeY = hmd.RawPosition.y + UnscaledVerticalLift();
                // VRMR port addition: when the caller knows the true floor (tracking-space y = 0), skip the upstream
                // "lowest tracker minus mount allowance" floor estimate.
                if (UseTrackedFloorEstimate && TryGetTrackedFloor(rawEyeY, out float trackedFloorY))
                {
                    PlayerEyeHeight = rawEyeY - trackedFloorY;
                }
                else
                {
                    PlayerEyeHeight = rawEyeY - PlayspaceVerticalOffset - HeightModeGroundingOffset;
                }
            }
            else
            {
                // Upstream falls back to the avatar eye height (desktop); the port prefers a stated eye height when given.
                PlayerEyeHeight = StatedEyeHeight > 0f ? StatedEyeHeight :
                    Model.AvatarEyeHeight > 0f ? Model.AvatarEyeHeight : IkHeightConstants.FallbackHeightInMeters;
                genuine = false;
            }

            if (PlayerEyeHeight <= 0f)
            {
                PlayerEyeHeight = IkHeightConstants.FallbackHeightInMeters;
                genuine = false;
            }

            HasGenuinePlayerEyeHeight = genuine;
        }

        bool TryGetTrackedFloor(float hmdY, out float floorY)
        {
            var heights = new List<float>(16);
            for (int r = 0; r < Devices.Length; r++)
            {
                var d = Devices[r];
                if (!d.Present || !d.HasPose || r == (int)IkBoneTrackedRole.CenterEye) continue;
                var unscaled = d.RawPosition + new Vector3(0f, UnscaledVerticalLift(), 0f);
                if (unscaled.sqrMagnitude < 1e-4f) continue;
                heights.Add(unscaled.y);
            }

            return IkCalibrationMath.TryEstimateFloorFromTrackers(heights, hmdY, out floorY);
        }

        void CalculatePlayerHipHeight()
        {
            if (Seated)
            {
                PlayerEyeToHipDrop = 0f;
                PlayerHipHeight = 0f;
                return;
            }

            DeviceState hips = Devices[(int)IkBoneTrackedRole.Hips], head = Devices[(int)IkBoneTrackedRole.CenterEye];
            if (!hips.Present || !hips.HasPose || !head.Present || !head.HasPose) return;
            float drop = head.RawPosition.y - hips.RawPosition.y;
            if (drop <= 0f || float.IsNaN(drop) || float.IsInfinity(drop)) return;
            PlayerEyeToHipDrop = drop;
            PlayerHipHeight = PlayerEyeHeight - drop;
        }

        // ---- device input -> bone controls ---------------------------------------------------------------------
        public float UnscaledVerticalLift()
        {
            if (!IsVR) return 0f;
            return PlayspaceVerticalOffset + HeightModeGroundingOffset;
        }

        public bool RoleHasDevice(IkBoneTrackedRole role)
        {
            return Devices[(int)role].Present;
        }

        public void SetDevicePose(IkBoneTrackedRole role, Vector3 trackingPosition, Quaternion trackingRotation,
            bool valid, IkHandSource handSource)
        {
            ref var d = ref Devices[(int)role];
            d.Present = true;
            d.HandSource = handSource;
            if (valid)
            {
                d.RawPosition = trackingPosition;
                d.RawRotation = Quaternion.Normalize(trackingRotation);
                d.HasPose = true;
            }
        }

        public void RemoveDevice(IkBoneTrackedRole role)
        {
            Devices[(int)role] = new DeviceState
            {
                RawRotation = Quaternion.identity, DeviceOffsetRotation = Quaternion.identity,
                UnscaledRotation = Quaternion.identity, FinalScaledRotation = Quaternion.identity
            };
            var c = Bones.Find(role);
            if (IkBoneTrackedRoleCommonCheck.CheckItsFBTracker(role))
            {
                c.SetInverseOffset(Vector3.zero, Quaternion.identity);
                c.UseInverseOffset = false;
                Calibration.ClearSnapshot(role);
            }

            c.SetIncoming(Vector3.zero, Quaternion.identity);
            c.SetTracked(false);
        }

        public void SetDeviceOffset(IkBoneTrackedRole role, Vector3 position, Quaternion rotation)
        {
            ref var d = ref Devices[(int)role];
            d.DeviceOffsetPosition = position;
            d.DeviceOffsetRotation = rotation;
            d.HasDeviceOffset = !(position.sqrMagnitude <= 1e-10f && Mathf.Abs(rotation.w) >= 1f - 1e-7f);
        }

        /// <summary>One device poll (OnLatePollData): device -> unscaled -> scaled -> control incoming.</summary>
        public void PollDevices()
        {
            for (int r = 0; r < Devices.Length; r++)
            {
                ref var d = ref Devices[r];
                if (!d.Present || !d.HasPose) continue;
                var role = (IkBoneTrackedRole)r;
                var control = Bones.Controls[r];
                var physical = d.RawPosition;
                physical.y += UnscaledVerticalLift(); // ComputeUnscaledDeviceCoord
                var physicalRot = d.RawRotation;
                if (role == IkBoneTrackedRole.LeftHand || role == IkBoneTrackedRole.RightHand)
                {
                    bool isLeft = role == IkBoneTrackedRole.LeftHand;
                    ComputeHand(ref d, isLeft, physical, physicalRot, out var handPos, out var handRot);
                    d.FinalScaledPosition = handPos;
                    d.FinalScaledRotation = handRot;
                    // ControlOnlyAsHand: only while the hand role holds a tracker.
                    if (control.HasTracked) control.SetIncoming(handPos, handRot);
                    continue;
                }

                // IkInput: UnscaledDeviceCoord = Physical (device offset applied at the final pose for non-controllers).
                d.UnscaledPosition = physical;
                d.UnscaledRotation = physicalRot;
                IkCalibrationMath.ScaleDeviceCoord(physical, physicalRot, DeviceScale, Vector3.zero,
                    Quaternion.identity, out var scaledPos, out var scaledRot);
                var finalPos = scaledPos;
                var finalRot = scaledRot;
                if (d.HasDeviceOffset)
                {
                    finalPos += finalRot * (d.DeviceOffsetPosition * DeviceScale);
                    finalRot *= d.DeviceOffsetRotation;
                }

                d.FinalScaledPosition = finalPos;
                d.FinalScaledRotation = finalRot;
                var scaledControlPositionOffset = Vector3.zero;
                if (role == IkBoneTrackedRole.CenterEye)
                {
                    scaledControlPositionOffset = scaledRot * (HmdCenterEyeOffset * DeviceScale);
                }

                if (control.HasTracked) control.SetIncoming(finalPos + scaledControlPositionOffset, finalRot);
            }
        }

        void ComputeHand(ref DeviceState d, bool isLeft, Vector3 physical, Quaternion physicalRot, out Vector3 handPos,
            out Quaternion handRot)
        {
            var unscaledPos = physical;
            var unscaledRot = physicalRot;
            if (d.HasDeviceOffset)
            {
                // ResolveUnscaledFromPhysical(true): controllers apply the device offset at source.
                unscaledPos = physical + physicalRot * d.DeviceOffsetPosition;
                unscaledRot = physicalRot * d.DeviceOffsetRotation;
            }

            float scale = DeviceScale;
            switch (d.HandSource)
            {
                case IkHandSource.HandBone:
                {
                    // Port-specific: the pose is the hand bone; the base solver's hand target is the landmark frame, bone = target * offset.
                    var offset = isLeft ? Rig.IKJob.offsetRotationLeftHand : Rig.IKJob.offsetRotationRightHand;
                    handPos = unscaledPos * scale;
                    handRot = unscaledRot * Quaternion.Inverse(offset);
                    d.UnscaledPosition = unscaledPos;
                    d.UnscaledRotation = unscaledRot;
                    return;
                }
                case IkHandSource.OpenXRPalm:
                {
                    var corrected = unscaledRot *
                                    Quaternion.Euler(isLeft ? LeftHandPalmCorrection : RightHandPalmCorrection);
                    handRot = corrected;
                    handPos = unscaledPos * scale;
                    if (UseIKPositionOffset)
                    {
                        handPos += handRot *
                                   ((isLeft ? OpenXRLeftHandToIKPositionOffset : OpenXRRightHandToIKPositionOffset) *
                                    scale);
                    }

                    d.UnscaledPosition = unscaledPos;
                    d.UnscaledRotation = unscaledRot;
                    return;
                }
                default:
                {
                    // IkOpenVRInputController.UpdateHandPose
                    var wristLocalPos = isLeft ? LeftWristLocalPosition : RightWristLocalPosition;
                    var wristLocalRot = isLeft ? LeftWristLocalRotation : RightWristLocalRotation;
                    var rotOffset = Quaternion.Euler(isLeft ? leftHandToIKRotationOffset : rightHandToIKRotationOffset);
                    var wristWorldPosUnscaled = unscaledPos - unscaledRot * wristLocalPos;
                    var posOffsetLocal = isLeft ? leftHandToIKPositionOffset : rightHandToIKPositionOffset;
                    if (VrmrLeftControllerHandOffset is { } manual)
                    {
                        if (!isLeft) manual.x = -manual.x;
                        wristWorldPosUnscaled = unscaledPos + unscaledRot * manual;
                    }
                    else if (UseIKPositionOffset)
                    {
                        wristWorldPosUnscaled += unscaledRot * posOffsetLocal;
                    }

                    var baseWristWorldRot = unscaledRot * wristLocalRot;
                    var wristWorldRot = baseWristWorldRot * rotOffset;
                    handPos = wristWorldPosUnscaled * scale;
                    handRot = wristWorldRot;
                    d.UnscaledPosition = wristWorldPosUnscaled;
                    d.UnscaledRotation = unscaledRot;
                    return;
                }
            }
        }

        /// <summary>
        ///     Hands/HMD get their role (and rig layer) as soon as the device appears; full-body trackers only at
        ///     calibration.
        /// </summary>
        void AssignNonFbRoles()
        {
            for (int r = 0; r < Devices.Length; r++)
            {
                var role = (IkBoneTrackedRole)r;
                if (IkBoneTrackedRoleCommonCheck.CheckItsFBTracker(role)) continue;
                var c = Bones.Controls[r];
                bool want = Devices[r].Present && Devices[r].HasPose;
                if (want != c.HasTracked) c.SetTracked(want);
            }
        }

        // ---- frame ----------------------------------------------------------------------------------------------
        public void Simulate(float deltaTime)
        {
            // Upstream measures the player at avatar load (CapturePlayerHeight(false)) with the HMD already present;
            // the port does it on the first frame that has an HMD pose, unless a stated eye height was given.
            if (!HasGenuinePlayerEyeHeight && StatedEyeHeight <= 0f &&
                Devices[(int)IkBoneTrackedRole.CenterEye].Present && Devices[(int)IkBoneTrackedRole.CenterEye].HasPose)
            {
                CapturePlayerHeight(false);
                ApplyScaleAndHeight();
            }

            AssignNonFbRoles();
            PollDevices();
            VirtualSpine.Simulate(deltaTime);
            Bones.Simulate(deltaTime, localToWorldMatrix);
            Rig.SimulateIKDestinations(deltaTime);
            CacheSolvedPose();
        }

        void SimulateAndApplyWithoutLerp(float deltaTime)
        {
            AssignNonFbRoles();
            PollDevices();
            VirtualSpine.Simulate(deltaTime);
            Bones.SimulateWithoutLerp(localToWorldMatrix, deltaTime);
        }

        void CacheSolvedPose()
        {
            var sk = Model.Skeleton;
            if (!sk.IsCreated) return;
            if (lastSolvedPos == null)
            {
                lastSolvedPos = new Vector3[IkAvatarModel.HumanBoneCount];
                lastSolvedRot = new Quaternion[IkAvatarModel.HumanBoneCount];
            }

            sk.Stream.InvalidateWorldCache();
            for (int b = 0; b < IkAvatarModel.HumanBoneCount; b++)
            {
                int node = Model.NodeOfBone[b];
                if (node < 0) continue;
                sk.Stream.GetWorldPositionAndRotation(node, out lastSolvedPos[b], out lastSolvedRot[b]);
            }

            hasSolved = true;
        }

        public Vector3 BoneWorldPosition(HumanBodyBones bone)
        {
            if (hasSolved && Model.Has(bone)) return lastSolvedPos[(int)bone];
            return localToWorldMatrix.MultiplyPoint3x4(Model.Has(bone) ? Model.Pos(bone) : Vector3.zero);
        }

        public Quaternion BoneWorldRotation(HumanBodyBones bone)
        {
            if (hasSolved && Model.Has(bone)) return lastSolvedRot[(int)bone];
            return PlayspaceRotation * (Model.Has(bone) ? Model.Rot(bone) : Quaternion.identity);
        }

        // ---- IkAvatarStageCalibration.FullBodyCalibration ---------------------------------------------
        public void FullBodyCalibration(float deltaTime)
        {
            heldAutoLowerBody = HasFbTrackerCandidates();
            try
            {
                CapturePlayerHeight(true); // OnAvatarFBCalibration (RemeasureHeightOnCalibration default)
                ApplyScaleAndHeight();
                Calibration.ClearStores();
                UnassignFBTrackers();
                SimulateAndApplyWithoutLerp(deltaTime);
                ApplyScaleAndHeight();
                SimulateAndApplyWithoutLerp(deltaTime);
                for (int r = 0; r < Devices.Length; r++)
                {
                    var role = (IkBoneTrackedRole)r;
                    if (!IkBoneTrackedRoleCommonCheck.CheckItsFBTracker(role) || !Settings.EnableFBT) continue;
                    ref var d = ref Devices[r];
                    if (!d.Present || !d.HasPose) continue;
                    var control = Bones.Controls[r];
                    Calibration.CalculateOffset(control, role, d.FinalScaledPosition, d.FinalScaledRotation,
                        d.UnscaledPosition, d.UnscaledRotation);
                    control.SetTracked(true);
                    control.SetIncoming(d.FinalScaledPosition, d.FinalScaledRotation);
                }

                SimulateAndApplyWithoutLerp(deltaTime);
                Calibration.RecomputeFbikRotationCalibration();
                Calibration.ComputeHints();
                CalculatePlayerHipHeight();
                ApplyScaleAndHeight();
                bool held = heldAutoLowerBody ?? false;
                heldAutoLowerBody = null;
                if (held != (HasHipsFBIKTracker || HasLegFBIKTrackers))
                {
                    ApplyScaleAndHeight();
                }

                Rig.ResetSmoothingState();
            }
            finally
            {
                heldAutoLowerBody = null;
            }
        }

        bool HasFbTrackerCandidates()
        {
            for (int r = 0; r < Devices.Length; r++)
            {
                if (IkBoneTrackedRoleCommonCheck.CheckItsFBTracker((IkBoneTrackedRole)r) && Devices[r].Present &&
                    Devices[r].HasPose) return true;
            }

            return false;
        }

        void UnassignFBTrackers()
        {
            for (int r = 0; r < Devices.Length; r++)
            {
                var role = (IkBoneTrackedRole)r;
                if (!IkBoneTrackedRoleCommonCheck.CheckItsFBTracker(role)) continue;
                var c = Bones.Controls[r];
                if (!c.HasTracked && !c.UseInverseOffset) continue;
                c.SetInverseOffset(Vector3.zero, Quaternion.identity);
                c.UseInverseOffset = false;
                c.SetIncoming(Vector3.zero, Quaternion.identity);
                c.SetTracked(false);
                Calibration.ClearSnapshot(role);
            }
        }

        // ---- devices ----------------------------------------------------------------------------------------
        public struct DeviceState
        {
            public bool Present; // a device is assigned to this role (IkInput with a role)
            public bool HasPose; // at least one valid pose received
            public Vector3 RawPosition; // tracking space, Unity convention, metres (PhysicalDeviceCoord before lift)
            public Quaternion RawRotation;
            public IkHandSource HandSource;
            public bool HasDeviceOffset;
            public Vector3 DeviceOffsetPosition;
            public Quaternion DeviceOffsetRotation;
            public Vector3 UnscaledPosition;
            public Quaternion UnscaledRotation;
            public Vector3 FinalScaledPosition;
            public Quaternion FinalScaledRotation;
        }
    }
}
