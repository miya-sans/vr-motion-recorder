// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Plain C#: game-engine, Burst and job-system dependencies are replaced by the VRMR.Fbik shim.
namespace VRMR.Fbik
{
    public struct FbikFrameFacts
    {
        public bool hipsTracked, chestTracked, chestFollowsBody, prone, seated, upright, moving, footIKSetting, footSimReady;
        public bool leftLegTracked, rightLegTracked, leftFootTracked, rightFootTracked, leftKneeTracked, rightKneeTracked;
        public bool leftElbowTracked, rightElbowTracked, leftShoulderTracked, rightShoulderTracked, leftToeTracked, rightToeTracked;
        public bool leftSimFootRotation, rightSimFootRotation, leftToeBend, rightToeBend, leftElbowRoll, rightElbowRoll, leftKneeRoll, rightKneeRoll;
        public float deltaTime, leftHandWeight, rightHandWeight, leftShoulderWeight, rightShoulderWeight, leftFootSim, rightFootSim;
    }
    public struct FbikArmPlan
    {
        public bool has, hasUpperTwist, hasLowerTwist, solve, trackerHint, hintRoll, upperTwist, lowerTwist, elbowProtect;
        public float weight;
    }
    public struct FbikLegPlan
    {
        public bool has, solve, tracked, kneeTracked, preserveTip, hintRoll;
        public float weight;
        public FbikSource target;
    }
    public struct FbikPlan
    {
        public bool hasHips, hasSpine, hasChest, hasUpperChest, hasNeck, hasHead, hasSpineChain, hasChestJoint, hasSpineRestFrames, hasSpineBend;
        public bool hasBodyRight, hasTorso, hasLegFrame, hasChestRef, hasLeftShoulder, hasRightShoulder, hasLeftToe, hasRightToe;
        public bool hasChestSpring, hasArmState, hasLegState, hasLegDiagnostics;
        public int chestIdx;
        public uint boundSlots;
        public IkBoneHandle torsoFrom, torsoTo, legFrameTo, chestRef;
        public bool prone, hipsTracked, chestTracked, chestChain, headChain, crouchOffset, gaitPelvis, headPinnedHips;
        public bool leftShoulderTracked, rightShoulderTracked, leftToeTracked, rightToeTracked, leftToeDriven, rightToeDriven;
        public bool leftToeSurface, rightToeSurface;
        public FbikShoulderMode leftShoulder, rightShoulder;
        public float leftShoulderWeight, rightShoulderWeight;
        public FbikArmPlan leftArm, rightArm;
        public FbikLegPlan leftLeg, rightLeg;
    }
}
