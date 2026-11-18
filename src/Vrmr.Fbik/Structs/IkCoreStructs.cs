// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Plain C#: game-engine, Burst and job-system dependencies are replaced by the VRMR.Fbik shim.

namespace VRMR.Fbik
{
    public struct IkCervicalInput
    {
        public float BaseDeg, NeckShare, MaxHeadPitchDeg, ExtremeStartDeg, ExtremeFullDeg, ExtremeRollForwardMaxDeg;
        public float ExtremeRollBackwardMaxDeg, ExtremeHipsHorizontalMax, ExtremeChestHorizontalMax;
        public float ExtremeHipsHorizontalLookUp, ExtremeChestHorizontalLookUp, ExtremeHipsDownMax, ExtremeChestDownMax;
        public float ExtremeHipsDownLookUp, ExtremeChestDownLookUp, PitchGainDeg;
        public Vector3 ReferenceUp;
        public Quaternion HeadTargetRot;
        public bool HasUpperChest;
    }

    public struct IkCervicalResult
    {
        public bool EarlyOut;
        public Quaternion HeadRotClamped;
        public float BhDeg, NeckDeg;
        public bool HasExtreme;
        public float HipsForwardAmount, HipsDownAmount, ChestForwardAmount, ChestDownAmount, HeadPitchInputDeg;
        public float HeadPitchClampedDeg, LordosisDeg, UpperChestLordosisDeg, ExtremeFrac, ExtremeRollDeg, SignedPitch;
        public float LookUpFrac, LookDownFrac;
    }

    public struct IkNodPivotSettings
    {
        public float PriorWeight, MinPitchRangeDeg, MinFitQuality, MaxPivotSpreadMeters, MaxVerticalRangeMeters;
        public float3 MaxArm;
        public float Scale;
    }

    public struct IkNodPivotResult
    {
        public float3 Arm;
        public bool Accepted;
        public float FitQuality, PitchRangeDeg, PivotSpreadMeters, VerticalRangeMeters;
    }

    public struct IkSwivelFrame
    {
        public Vector3 Right, Up, Forward;
        public bool Valid;
    }
}

namespace VRMR.Fbik
{
    public struct IkEuroVec3State
    {
        public bool xHasPrev, dxHasPrev;
        public float3 hatX, hatDx;
    }

    public struct IkEuroQuatState
    {
        public bool hasPrev;
        public quaternion prev;
        public IkEuroVec3State logVecState;
    }
}
