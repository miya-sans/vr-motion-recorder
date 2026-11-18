// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Plain C#: game-engine, Burst and job-system dependencies are replaced by the VRMR.Fbik shim.
using System;
namespace VRMR.Fbik
{
[Serializable]
public class IkStoredHandPose
{
    public IkFingerPoseParams FingerPoseForPosition;
    public IkCalibratedCoords[] FingerJoints;
    public IkStoredHandPose(IkFingerPoseParams FingerPose, IkCalibratedCoords[] HandBonePositions)
    {
        FingerPoseForPosition = FingerPose;
        FingerJoints = HandBonePositions;
    }
}
[Serializable]
public class IkFingerPoseParams
{
    public float Stretch, Spread;
    public IkFingerPoseParams(float stretch, float spread)
    {
        Stretch = stretch;
        Spread = spread;
    }
}
}
