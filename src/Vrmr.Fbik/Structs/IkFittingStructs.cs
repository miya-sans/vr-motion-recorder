// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Plain C#: game-engine, Burst and job-system dependencies are replaced by the VRMR.Fbik shim.

namespace VRMR.Fbik
{
    public struct IkBodyFitMeasurements
    {
        public float PlayerEyeHeight, PlayerArmSpan, AvatarEyeHeight, AvatarArmSpan, AvatarShoulderWidth, UniformScale;
    }

    public struct IkBodyFitResult
    {
        public float ArmScale, LegScale, TorsoScale;
        public IkBodyFitStatus ArmStatus, BodyStatus;
        public bool HasArmFit => ArmStatus == IkBodyFitStatus.Fitted;
        public bool HasBodyFit => BodyStatus == IkBodyFitStatus.Fitted;

        public static IkBodyFitResult Identity => new()
        {
            ArmScale = 1f,
            LegScale = 1f,
            TorsoScale = 1f,
            ArmStatus = IkBodyFitStatus.Disabled,
            BodyStatus = IkBodyFitStatus.Disabled
        };

        public bool IsIdentity => Mathf.Approximately(ArmScale, 1f) && Mathf.Approximately(LegScale, 1f) &&
                                  Mathf.Approximately(TorsoScale, 1f);
    }

    public struct IkBodyEvidenceTrack
    {
        public FixedList64Bytes<float> Top;
        public int SampleCount;
        public float Previous;
        public bool HasPrevious;
        public int LowStreak;
    }

    public struct IkBodyEvidenceSample
    {
        public float HeadY, HandSpan, InjectedVerticalOffset, DeltaSeconds;
        public bool HeadValid, HandsValid;
    }

    public struct IkBodyEvidenceState
    {
        public IkBodyEvidenceTrack Eye, ArmSpan;
    }

    public struct IkScaleFitSample
    {
        public float Player, Avatar, Slack, Weight;
        public static IkScaleFitSample None => default;
    }

    public struct IkScaleFitInput
    {
        public IkScaleFitSample Eye, ArmSpan, HipHeight;
        public float MaxEyeDeviation;
    }

    public struct IkScaleFitResult
    {
        public float Scale;
        public IkScaleFitStatus Status;
        public int UsedCount;
        public float EyeResidual, ArmResidual, HipResidual;
        public bool IsValid => Status != IkScaleFitStatus.NoData && Scale > 0f;

        public static IkScaleFitResult Invalid => new()
        {
            Scale = 0f,
            Status = IkScaleFitStatus.NoData,
            UsedCount = 0,
            EyeResidual = 1f,
            ArmResidual = 1f,
            HipResidual = 1f
        };
    }
}
