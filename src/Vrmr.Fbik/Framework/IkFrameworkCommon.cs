// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Framework-side math the solver setup depends on:
//   IkBoneTrackedRole (+ CheckItsFBTracker), IkSelectedHeightMode, IkTrackingHardware,
//   IkCalibrationMath, IkScaleResolver,
//   IkSmoothingProfiles + IkSmoothingGroup, IkHeightDriver (constants only).
// Static/global framework state (settings bindings, IkHeightDriver statics) is replaced by explicit
// parameters; defaults are the IkSettingsDefaults values.

using System.Collections.Generic;

namespace VRMR.Fbik
{
    public enum IkBoneTrackedRole
    {
        CenterEye = 0, Head = 1, Neck = 2, Chest = 3, Hips = 4, Spine = 5,
        LeftUpperLeg = 6, RightUpperLeg = 7, LeftLowerLeg = 8, RightLowerLeg = 9, LeftFoot = 10, RightFoot = 11,
        LeftShoulder = 12, RightShoulder = 13, LeftUpperArm = 14, RightUpperArm = 15, LeftLowerArm = 16,
        RightLowerArm = 17,
        LeftHand = 18, RightHand = 19, LeftToes = 20, RightToes = 21, Mouth = 22
    }

    public static class IkBoneTrackedRoleCommonCheck
    {
        public static bool CheckItsFBTracker(IkBoneTrackedRole role)
        {
            return role != IkBoneTrackedRole.LeftHand && role != IkBoneTrackedRole.RightHand
                                                      && role != IkBoneTrackedRole.LeftUpperLeg &&
                                                      role != IkBoneTrackedRole.RightUpperLeg
                                                      && role != IkBoneTrackedRole.RightUpperArm &&
                                                      role != IkBoneTrackedRole.LeftUpperArm
                                                      && role != IkBoneTrackedRole.CenterEye &&
                                                      role != IkBoneTrackedRole.Head
                                                      && role != IkBoneTrackedRole.Neck &&
                                                      role != IkBoneTrackedRole.Mouth &&
                                                      role != IkBoneTrackedRole.Spine;
        }

        public static bool TryConvertToHumanoidRole(IkBoneTrackedRole role, out HumanBodyBones bone)
        {
            switch (role)
            {
                case IkBoneTrackedRole.Head:
                    bone = HumanBodyBones.Head;
                    return true;
                case IkBoneTrackedRole.Neck:
                    bone = HumanBodyBones.Neck;
                    return true;
                case IkBoneTrackedRole.Chest:
                    bone = HumanBodyBones.Chest;
                    return true;
                case IkBoneTrackedRole.Hips:
                    bone = HumanBodyBones.Hips;
                    return true;
                case IkBoneTrackedRole.Spine:
                    bone = HumanBodyBones.Spine;
                    return true;
                case IkBoneTrackedRole.LeftUpperLeg:
                    bone = HumanBodyBones.LeftUpperLeg;
                    return true;
                case IkBoneTrackedRole.RightUpperLeg:
                    bone = HumanBodyBones.RightUpperLeg;
                    return true;
                case IkBoneTrackedRole.LeftLowerLeg:
                    bone = HumanBodyBones.LeftLowerLeg;
                    return true;
                case IkBoneTrackedRole.RightLowerLeg:
                    bone = HumanBodyBones.RightLowerLeg;
                    return true;
                case IkBoneTrackedRole.LeftFoot:
                    bone = HumanBodyBones.LeftFoot;
                    return true;
                case IkBoneTrackedRole.RightFoot:
                    bone = HumanBodyBones.RightFoot;
                    return true;
                case IkBoneTrackedRole.LeftShoulder:
                    bone = HumanBodyBones.LeftShoulder;
                    return true;
                case IkBoneTrackedRole.RightShoulder:
                    bone = HumanBodyBones.RightShoulder;
                    return true;
                case IkBoneTrackedRole.LeftUpperArm:
                    bone = HumanBodyBones.LeftUpperArm;
                    return true;
                case IkBoneTrackedRole.RightUpperArm:
                    bone = HumanBodyBones.RightUpperArm;
                    return true;
                case IkBoneTrackedRole.LeftLowerArm:
                    bone = HumanBodyBones.LeftLowerArm;
                    return true;
                case IkBoneTrackedRole.RightLowerArm:
                    bone = HumanBodyBones.RightLowerArm;
                    return true;
                case IkBoneTrackedRole.LeftHand:
                    bone = HumanBodyBones.LeftHand;
                    return true;
                case IkBoneTrackedRole.RightHand:
                    bone = HumanBodyBones.RightHand;
                    return true;
                case IkBoneTrackedRole.LeftToes:
                    bone = HumanBodyBones.LeftToes;
                    return true;
                case IkBoneTrackedRole.RightToes:
                    bone = HumanBodyBones.RightToes;
                    return true;
                default:
                    bone = HumanBodyBones.LastBone;
                    return false;
            }
        }

        /// <summary>IkAvatarDriver.IsApartOfSpineVertical.</summary>
        public static bool IsApartOfSpineVertical(IkBoneTrackedRole role)
        {
            return role == IkBoneTrackedRole.Hips || role == IkBoneTrackedRole.Chest || role == IkBoneTrackedRole.Spine
                   || role == IkBoneTrackedRole.CenterEye || role == IkBoneTrackedRole.Mouth ||
                   role == IkBoneTrackedRole.Head;
        }
    }

    public enum IkSelectedHeightMode
    {
        ArmSpan,
        EyeHeight,
        Auto,
        BestFit
    }

    public enum IkTrackingHardware : byte
    {
        Unknown = 0,
        Simulated = 1,
        Lighthouse = 2,
        InsideOut = 3,
        Optical = 4,
        Estimated = 5,
        Inertial = 6
    }

    public static class IkHeightConstants
    {
        public const float FallbackHeightInMeters = 1.61f;
        public const float MinPlausibleBodyMeasure = 0.8f;
        public const float MaxPlausibleBodyMeasure = 2.8f;
    }

    public static class IkCalibrationMath
    {
        public const float DefaultArmRatio = 0.45f;
        public const float ArmRatioMin = 0.35f;
        public const float ArmRatioMax = 0.55f;
        public const float EyeToHeightRatio = 0.93f;
        public const float SpanToHeightRatio = 0.86f;

        public const float FootMountAllowanceMeters = 0.07f;
        public const float FootBandMeters = 0.22f;
        public const int MinFootBandTrackers = 2;
        public const float EyeOverSpanPersistBand = 1.15f;
        public const float MaxPlausibleStandingEyeMeters = 1.90f;

        public static void ComputeInverseOffset(Vector3 trackerPos, Quaternion trackerRot, Vector3 bonePos,
            Quaternion boneRot, out Vector3 inverseOffsetPosition, out Quaternion inverseOffsetRotation)
        {
            var invTrack = Quaternion.Inverse(trackerRot);
            inverseOffsetPosition = invTrack * (bonePos - trackerPos);
            inverseOffsetRotation = invTrack * boneRot;
        }

        public static void ApplyInverseOffset(Vector3 trackerPos, Quaternion trackerRot, Vector3 inverseOffsetPosition,
            Quaternion inverseOffsetRotation, out Vector3 bonePos, out Quaternion boneRot)
        {
            bonePos = trackerPos + trackerRot * inverseOffsetPosition;
            boneRot = trackerRot * inverseOffsetRotation;
        }

        public static void ScaleDeviceCoord(Vector3 unscaledPos, Quaternion unscaledRot, float deviceScale,
            Vector3 offsetPos, Quaternion offsetRot, out Vector3 scaledPos, out Quaternion scaledRot)
        {
            scaledPos = offsetPos + offsetRot * (unscaledPos * deviceScale);
            scaledRot = offsetRot * unscaledRot;
        }

        public static void UnscaleDeviceCoord(Vector3 scaledPos, Quaternion scaledRot, float deviceScale,
            Vector3 offsetPos, Quaternion offsetRot, out Vector3 unscaledPos, out Quaternion unscaledRot)
        {
            var invOffset = Quaternion.Inverse(offsetRot);
            float safeScale = float.IsNaN(deviceScale) || float.IsInfinity(deviceScale) || deviceScale <= 1e-6f
                ? 1f
                : deviceScale;
            unscaledPos = invOffset * (scaledPos - offsetPos) / safeScale;
            unscaledRot = invOffset * scaledRot;
        }

        public static void ReprojectInverseOffsetPosition(Vector3 calibUnscaledTrackerPos,
            Quaternion calibUnscaledTrackerRot, Vector3 calibUnscaledHeadPos, Quaternion calibUnscaledHeadRot,
            float deviceScale, Vector3 offsetPos, Quaternion offsetRot, Vector3 headTposeLocalScaled,
            Vector3 boneTposeLocalScaled, out Vector3 inverseOffsetPosition)
        {
            ScaleDeviceCoord(calibUnscaledTrackerPos, calibUnscaledTrackerRot, deviceScale, offsetPos, offsetRot,
                out var trackerPos, out var trackerRot);
            ScaleDeviceCoord(calibUnscaledHeadPos, calibUnscaledHeadRot, deviceScale, offsetPos, offsetRot,
                out var headPos, out var headRot);
            ComputeTposeAnchor(headPos, headRot, headTposeLocalScaled, out var rootPos, out var rootRot);
            var reference = rootPos + rootRot * boneTposeLocalScaled;
            ComputeInverseOffset(trackerPos, trackerRot, reference, Quaternion.identity, out inverseOffsetPosition,
                out _);
        }

        public static void ComputeTposeAnchor(Vector3 headPos, Quaternion headRot, Vector3 headTposeLocalScaled,
            out Vector3 anchorPos, out Quaternion anchorRot)
        {
            var flatFwd = headRot * Vector3.forward;
            flatFwd.y = 0f;
            anchorRot = flatFwd.sqrMagnitude < 1e-6f
                ? Quaternion.identity
                : Quaternion.LookRotation(flatFwd.normalized, Vector3.up);
            anchorPos = headPos - anchorRot * headTposeLocalScaled;
        }

        public static float StandingEyeDenominator(float playerMeasuredHeight, float eyeReference,
            float additionalPlayerHeight)
        {
            return additionalPlayerHeight + eyeReference + playerMeasuredHeight;
        }

        public static float ComputeDeviceScale(float avatarUnscaledMetric, float appliedUpScale,
            float playerMeasuredHeight, float eyeReference, float additionalPlayerHeight)
        {
            float avatarScaledMetric = avatarUnscaledMetric * appliedUpScale;
            float denominator = StandingEyeDenominator(playerMeasuredHeight, eyeReference, additionalPlayerHeight);
            if (denominator > -1e-5f && denominator < 1e-5f)
            {
                return 1f;
            }

            return avatarScaledMetric / denominator;
        }

        public static float HeightModeGroundingOffset(float avatarUnscaledEye, float appliedUpScale, float deviceScale,
            float playerTrueEye)
        {
            if (!(deviceScale > 1e-5f) || float.IsInfinity(deviceScale) || !(playerTrueEye > 0f) ||
                float.IsInfinity(playerTrueEye))
            {
                return 0f;
            }

            float offset = avatarUnscaledEye * appliedUpScale / deviceScale - playerTrueEye;
            return float.IsNaN(offset) || float.IsInfinity(offset) || Mathf.Abs(offset) < 1e-5f ? 0f : offset;
        }

        public static float SpanFromArmRatio(float eyeHeight, float armRatio)
        {
            float ratio = float.IsNaN(armRatio) || float.IsInfinity(armRatio)
                ? DefaultArmRatio
                : Mathf.Clamp(armRatio, ArmRatioMin, ArmRatioMax);
            return eyeHeight * 2f * ratio;
        }

        public static float ImpliedHeightFromEye(float playerEye)
        {
            return playerEye / EyeToHeightRatio;
        }

        public static float ImpliedHeightFromSpan(float playerSpan)
        {
            return playerSpan / SpanToHeightRatio;
        }

        public static bool TryEstimateFloorFromTrackers(IReadOnlyList<float> trackerHeights, float hmdHeight,
            out float floorHeight)
        {
            floorHeight = 0f;
            if (trackerHeights == null || trackerHeights.Count < MinFootBandTrackers)
            {
                return false;
            }

            float lowest = float.MaxValue;
            for (int i = 0; i < trackerHeights.Count; i++)
            {
                if (trackerHeights[i] < lowest) lowest = trackerHeights[i];
            }

            int inFootBand = 0;
            for (int i = 0; i < trackerHeights.Count; i++)
            {
                if (trackerHeights[i] <= lowest + FootBandMeters) inFootBand++;
            }

            if (inFootBand < MinFootBandTrackers)
            {
                return false;
            }

            floorHeight = lowest - FootMountAllowanceMeters;
            float impliedEye = hmdHeight - floorHeight;
            return impliedEye >= IkHeightConstants.MinPlausibleBodyMeasure &&
                   impliedEye <= IkHeightConstants.MaxPlausibleBodyMeasure;
        }

        public static bool EyeHeightLooksLiftPoisoned(float playerEye)
        {
            return playerEye > MaxPlausibleStandingEyeMeters;
        }

        public static bool ArmSpanLooksUnderMeasured(float playerEye, float playerSpan)
        {
            if (playerEye <= 0f || playerSpan <= 0f)
            {
                return false;
            }

            return ImpliedHeightFromEye(playerEye) > ImpliedHeightFromSpan(playerSpan) * EyeOverSpanPersistBand;
        }

        public static float SizingEyeHeight(float measuredEye, float span, bool spanGenuine, float statedEye)
        {
            if (statedEye > 0f)
            {
                return statedEye;
            }

            if (!EyeHeightLooksLiftPoisoned(measuredEye))
            {
                return measuredEye;
            }

            return spanGenuine && ArmSpanLooksUnderMeasured(measuredEye, span)
                ? Mathf.Max(ImpliedHeightFromSpan(span) * EyeToHeightRatio, IkHeightConstants.MinPlausibleBodyMeasure)
                : measuredEye;
        }
    }

    public struct IkScaleInputs
    {
        public IkSelectedHeightMode Mode;
        public bool Desktop, Seated, SpanUsable;

        public float PlayerEye,
            PlayerTrackedEye,
            PlayerSpan,
            PlayerEyeOffset,
            AvatarEye,
            AvatarSpan,
            AvatarShoulderWidth,
            AppliedUpScale,
            FitDeviation;
    }

    public struct IkScaleState
    {
        public IkSelectedHeightMode Mode;
        public IkScaleFitResult BestFit;
        public float AvatarMetric, PlayerMetric, EyeOffset, UniformScale, DeviceScale, GroundingOffset, ScaledAvatarEye;
    }

    public static class IkScaleResolver
    {
        public static IkScaleState Resolve(in IkScaleInputs i)
        {
            IkScaleState s = default;
            s.BestFit = IkScaleFitResult.Invalid;
            s.Mode = i.Desktop ? IkSelectedHeightMode.EyeHeight :
                i.Mode == IkSelectedHeightMode.Auto ? IkSelectedHeightMode.BestFit : i.Mode;
            float avatarEye = Positive(i.AvatarEye, IkHeightConstants.FallbackHeightInMeters),
                playerEye = Positive(i.PlayerEye, IkHeightConstants.FallbackHeightInMeters);
            float avatarSpan = Positive(i.AvatarSpan, avatarEye),
                playerSpan = Positive(i.PlayerSpan, playerEye),
                k = Positive(i.AppliedUpScale, 1f),
                fullOffset = Finite(i.PlayerEyeOffset),
                trackedEye = Positive(i.PlayerTrackedEye, playerEye);
            if (s.Mode == IkSelectedHeightMode.ArmSpan)
            {
                s.AvatarMetric = avatarSpan;
                s.PlayerMetric = playerSpan;
                s.EyeOffset = 0f;
            }
            else
            {
                s.AvatarMetric = avatarEye;
                s.PlayerMetric = playerEye;
                s.EyeOffset = fullOffset;
                if (s.Mode == IkSelectedHeightMode.BestFit && !i.Seated)
                {
                    s.BestFit = SolveBestFit(in i, avatarEye, playerEye + fullOffset, avatarSpan, playerSpan);
                    if (s.BestFit.IsValid)
                    {
                        s.AvatarMetric = s.BestFit.Scale * (playerEye + fullOffset);
                    }
                }
            }

            float denominator = s.PlayerMetric + s.EyeOffset;
            s.UniformScale = denominator > 1e-5f ? Positive(s.AvatarMetric / denominator, 1f) : 1f;
            s.DeviceScale = s.UniformScale * k;
            s.ScaledAvatarEye = avatarEye * k;
            s.GroundingOffset = i.Desktop || i.Seated
                ? 0f
                : IkCalibrationMath.HeightModeGroundingOffset(avatarEye, k, s.DeviceScale, trackedEye + fullOffset);
            return s;
        }

        public static IkScaleFitResult SolveBestFit(in IkScaleInputs i, float avatarEye, float playerTrueEye,
            float avatarSpan, float playerSpan)
        {
            var measurements = new IkBodyFitMeasurements
            {
                AvatarArmSpan = avatarSpan,
                AvatarShoulderWidth = i.AvatarShoulderWidth
            };
            var input = new IkScaleFitInput
            {
                MaxEyeDeviation = IkScaleFitCore.DefaultMaxEyeDeviation,
                Eye = new IkScaleFitSample
                    { Player = playerTrueEye, Avatar = avatarEye, Weight = IkScaleFitCore.EyeWeight },
                ArmSpan = new IkScaleFitSample
                {
                    Player = playerSpan, Avatar = avatarSpan,
                    Slack = IkBodyFitCore.ArmSpanSlack(in measurements, i.FitDeviation),
                    Weight = i.SpanUsable ? IkScaleFitCore.ArmSpanWeight : 0f
                },
                HipHeight = IkScaleFitSample.None
            };
            return IkScaleFitCore.Solve(in input);
        }

        static float Positive(float value, float fallback)
        {
            return float.IsNaN(value) || float.IsInfinity(value) || value <= 0f ? fallback : value;
        }

        static float Finite(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;
        }
    }

    public struct IkSmoothingProfile
    {
        public float MinCutoff, Beta, DerivativeCutoff, PositionHz, RotationHz;

        public IkSmoothingProfile(float minCutoff, float beta, float derivativeCutoff, float positionHz,
            float rotationHz)
        {
            MinCutoff = minCutoff;
            Beta = beta;
            DerivativeCutoff = derivativeCutoff;
            PositionHz = positionHz;
            RotationHz = rotationHz;
        }
    }

    public enum IkSmoothingGroup : byte
    {
        Head = 0, Hands = 1, Elbows = 2, Chest = 3, Hips = 4, Knees = 5, Feet = 6
    }

    public static class IkSmoothingProfiles
    {
        public const int GroupCount = 7;
        const float SmoothingReferenceFps = 90f;

        public const string PresetOff = "Off",
            PresetLight = "Light",
            PresetStandard = "Standard",
            PresetHeavy = "Heavy",
            PresetOptical = "Optical",
            PresetCustom = "Custom",
            PresetAuto = "Auto";

        static readonly IkSmoothingProfile Light = new(8f, 2f, 3f, 30f, 35f);
        static readonly IkSmoothingProfile Heavy = new(2f, 5f, 2f, 10f, 12f);
        static readonly IkSmoothingProfile Optical = new(1f, 9f, 1.5f, 8f, 10f);
        public static readonly byte[] SlotGroup = BuildSlotGroups();

        public static float FramerateIndependentAlpha(float speed, float dt)
        {
            float alphaAtRef = math.saturate(math.max(0f, speed) / SmoothingReferenceFps);
            if (alphaAtRef >= 0.999f) return 1f;
            if (alphaAtRef <= 0f) return 0f;
            float rate = -SmoothingReferenceFps * math.log(1f - alphaAtRef);
            return 1f - math.exp(-rate * math.max(0f, dt));
        }

        public static bool IsOff(string preset)
        {
            return preset == PresetOff;
        }

        public static bool IsCustom(string preset)
        {
            return preset == PresetCustom;
        }

        public static bool IsAuto(string preset)
        {
            return preset == PresetAuto;
        }

        public static string PresetForHardware(IkTrackingHardware hardware)
        {
            switch (hardware)
            {
                case IkTrackingHardware.Simulated: return PresetOff;
                case IkTrackingHardware.Lighthouse:
                case IkTrackingHardware.InsideOut: return PresetLight;
                case IkTrackingHardware.Optical: return PresetOptical;
                case IkTrackingHardware.Estimated:
                case IkTrackingHardware.Inertial: return PresetHeavy;
                default: return PresetStandard;
            }
        }

        public static bool TryGetPreset(string preset, out IkSmoothingProfile profile)
        {
            switch (preset)
            {
                case PresetLight:
                    profile = Light;
                    return true;
                case PresetHeavy:
                    profile = Heavy;
                    return true;
                case PresetOptical:
                    profile = Optical;
                    return true;
                default:
                    profile = default;
                    return false;
            }
        }

        static byte[] BuildSlotGroups()
        {
            byte[] map = new byte[IkRigDriver.SlotCount];
            map[IkRigDriver.sHead] = (byte)IkSmoothingGroup.Head;
            map[IkRigDriver.sLeftHand] = (byte)IkSmoothingGroup.Hands;
            map[IkRigDriver.sRightHand] = (byte)IkSmoothingGroup.Hands;
            map[IkRigDriver.sLeftShoulder] = (byte)IkSmoothingGroup.Hands;
            map[IkRigDriver.sRightShoulder] = (byte)IkSmoothingGroup.Hands;
            map[IkRigDriver.sLeftLowerArm] = (byte)IkSmoothingGroup.Elbows;
            map[IkRigDriver.sRightLowerArm] = (byte)IkSmoothingGroup.Elbows;
            map[IkRigDriver.sChest] = (byte)IkSmoothingGroup.Chest;
            map[IkRigDriver.sHips] = (byte)IkSmoothingGroup.Hips;
            map[IkRigDriver.sLeftLowerLeg] = (byte)IkSmoothingGroup.Knees;
            map[IkRigDriver.sRightLowerLeg] = (byte)IkSmoothingGroup.Knees;
            map[IkRigDriver.sLeftFoot] = (byte)IkSmoothingGroup.Feet;
            map[IkRigDriver.sRightFoot] = (byte)IkSmoothingGroup.Feet;
            map[IkRigDriver.sLeftToe] = (byte)IkSmoothingGroup.Feet;
            map[IkRigDriver.sRightToe] = (byte)IkSmoothingGroup.Feet;
            return map;
        }
    }

    /// <summary>
    ///     Settings the framework reads from IkSettingsDefaults, with the upstream default values.
    ///     One instance per solver instead of global settings bindings.
    /// </summary>
    public sealed class FbikSettings
    {
        public float ArmRatio = IkCalibrationMath.DefaultArmRatio;
        public float FBIKArmSwivelSmoothTime = 0.08f, FBIKArmSwivelMaxRate = 720f;
        public bool FBIKBodyFit = true;

        public float FBIKBodyFitMaxDeviation = IkBodyFitCore.DefaultMaxDeviation;

        // FBIK
        public float FBIKChestRadius = 0.055f, FBIKCollisionSkin = 0.025f, FBIKHandRadius = 0.01f, FBIKHandSkin = 0.03f;
        public bool FBIKCollisionsEnabled = true, FBIKProtectElbow = true, FBIKCollideTrackedElbow = false;
        public float FBIKMoveBodyBackWhenCrouching = 1f;
        public float FBIKNeckExtensionDamp = 0.65f, FBIKNeckFlexionDamp = 0.5f;
        public float FBIKShoulderElevation = 1f, FBIKShoulderProtraction = 1f, FBIKShoulderTrackerBlend = 0.25f;
        public bool FBIKShoulderSolveEnabled = true;

        public bool FootIKEnabled = true, EnableFBT = true;

        // Height / calibration
        public IkSelectedHeightMode HeightMode = IkSelectedHeightMode.Auto;

        public float MinCutoff = 5.5f,
            Beta = 3.25f,
            DerivativeCutoff = 3f,
            SmoothingStrength = 1f,
            PositionSmoothingHz = 20f,
            RotationSmoothingHz = 25f;

        public bool QuestControllerFix = false;

        // Smoothing (one-euro filter on tracked slots). Per-group preset "Auto" resolves from the tracking hardware.
        public bool Smoothing = true;

        public string[] SmoothingPreset =
        {
            IkSmoothingProfiles.PresetAuto, IkSmoothingProfiles.PresetAuto, IkSmoothingProfiles.PresetAuto,
            IkSmoothingProfiles.PresetAuto, IkSmoothingProfiles.PresetAuto, IkSmoothingProfiles.PresetAuto,
            IkSmoothingProfiles.PresetAuto
        };

        // Virtual spine
        public float VSpineChestPitchFrac = 0.30f,
            VSpineChestRollFrac = 0.30f,
            VSpineSpinePitchFrac = 0.10f,
            VSpineSpineRollFrac = 0.10f;

        public float VSpineGazeSwingRemoval = 1f;
        public float VSpineHipsForwardBias = 0f, VSpineHipsCompressionStrength = 0.85f, VSpineHipsMaxDropMeters = 0.3f;

        public float VSpineNeckRotationSpeed = 40f,
            VSpineChestRotationSpeed = 25f,
            VSpineSpineRotationSpeed = 30f,
            VSpineHipsRotationSpeed = 20f;

        public bool VSpineNodPivotEstimate = true;
        public bool VSpinePostureModel = true;
        public float VSpineTorsoYawDeadzoneDeg = 45f, VSpineTorsoYawBlendSpeed = 8f;
    }
}
