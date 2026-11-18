// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Plain C#: game-engine, Burst and job-system dependencies are replaced by the VRMR.Fbik shim.
namespace VRMR.Fbik
{
    public struct IkSpineRom
    {
        public float FlexDeg, ExtDeg, LatDeg, AxialDeg;
        public IkSpineRom(float flexDeg, float extDeg, float latDeg, float axialDeg)
        {
            FlexDeg = flexDeg;
            ExtDeg = extDeg;
            LatDeg = latDeg;
            AxialDeg = axialDeg;
        }
    }
    public struct IkSpineRestFrame
    {
        public Quaternion RestLocalRot;
        public Vector3 Right, Up, Forward;
        public IkSpineSegment Segment;
        public bool Valid;
    }
    public struct IkSpineClampInfo
    {
        public bool SwingClamped, TwistClamped;
        public float FlexDeg, LatDeg, AxialDeg;
        public bool Touched => SwingClamped || TwistClamped;
    }
    public struct IkSpineBendInput
    {
        public Quaternion HipsRot;
        public Vector3 HipsPos, ChestPos, SmoothedHead;
        public Quaternion HipsBind, HeadTargetRot;
        public float SpineMaxForwardDeg, SpineMaxBackwardDeg, SpineMaxLateralDeg, SpineBendPitch, SpineBendYaw;
        public float SpineBendRoll, UpperBendPitch, UpperBendYaw, UpperBendRoll;
        public bool AnatDifferentialStiffness, AnatPelvicTwistRouting;
        public float BendTwistCoupling, SquishBoost, RestLen;
        public bool HasSpine, HasUpper;
    }
    public struct IkSpineBendChestInput
    {
        public float ChestBendPitch, ChestBendYaw, ChestBendRoll, TautBandFrac;
        public Vector3 NeckCue;
        public bool HasChest, HasNeckCue;
    }
    public struct IkSpineBendResult
    {
        public bool EarlyOut;
        public bool WriteSpine; public Vector3 SpineEuler;
        public bool WriteChest; public Vector3 ChestEuler;
        public bool WriteUpper; public Vector3 UpperEuler;
        public float BendPitchDeg, TwistY, SquishMult, BendGate, SpineYawEff, UpperYawEff, ChestYawEff, BowDeg;
    }
    public struct IkCrouchOffsetInput
    {
        public Vector3 HeadTargetPos, HipsPos;
        public Quaternion HipsRot, Bind;
        public Vector3 PlayerUp;
        public float Factor, RestDist, CrouchDepth, StandingHeadHeight, Fade;
    }
    public struct IkCrouchOffsetResult
    {
        public Vector3 HipsPos;
        public Quaternion HipsRot;
        public bool Applied;
        public float SetbackMeters, LeanDeg;
    }
}
