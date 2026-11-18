// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Plain C#: game-engine, Burst and job-system dependencies are replaced by the VRMR.Fbik shim.

namespace VRMR.Fbik
{
    public struct IkFootNativeState
    {
        public int sideSign;
        public float thighLen, shinLen, legLength;
        public int phase;
        public float3 plantedPos;
        public quaternion plantedRot;
        public float3 plantedBodyFwd, stepStartPos, stepTargetPos;
        public quaternion stepStartRot;
        public float stepTimer, stepDur, stepArcScale, plantedTime, stepUrgency;
        public quaternion landRot;
        public float3 idealPos, filteredNormal, currentPos;
        public quaternion currentRot;
        public float toeBendDeg;
        public float3 toeBendAxis;
        public bool wantsStep;
        public float3 predictedTargetXZ;
    }

    public struct IkFootSimState
    {
        public float3 prevHeadPos;
        public float prevHeadYaw;
        public float3 smoothedVelocity, smoothedBodyFwd, smoothedBodyRight, prevBodyFwd;
        public float smoothedYawRateDeg, smoothedAccelMag;
        public float3 prevRootFwd;
        public bool wasAirborne, hasPrevHeadPos;
    }

    public struct IkFootSimInput
    {
        public float dt;
        public float3 headPos, hipsPos;
        public quaternion hipsRot, chestRot, headRot;
        public float3 avatarForward, avatarRight;
        public bool hasChest, groundHit;
        public float3 groundPoint;
        public bool leftGroundValid, rightGroundValid;
        public float leftGroundUp, rightGroundUp;
        public float3 playerUp;
    }

    public struct IkFootSimParams
    {
        public float predictionFactor, velocityBiasFactor, leadOffsetFactor, maxVelocityOffsetFraction;

        public float maxPredictionFraction,
            plantedLerpSpeed,
            rotationLerpSpeed,
            velocitySmoothAccel,
            velocitySmoothDecel;

        public float bodyFwdRateMoving, bodyFwdRateStationary, maxFootTiltDegrees, maxFootYawDegrees;

        public float stepArcLiftExp,
            stepArcDropExp,
            stepHeightMinFraction,
            stepHeightStrideRefFraction,
            idleSpeedThreshold;

        public float idleBoostFraction, maxPlantedYawDegrees, idealSideEnforceFraction, stepTargetSideFraction;
        public float footSideEnforceFraction, maxVerticalDriftFraction;
        public float bodyFwdHipsWeight, bodyFwdChestWeight, bodyFwdHeadWeight, hipBobFraction;
        public quaternion footAlignLeft, footAlignRight;
        public float stanceWidth, hipToFoot, leftLegLen, rightLegLen, leftThighLen, leftShinLen, rightThighLen;
        public float rightShinLen, footLength, ankleHeight, stepTriggerDist, strideScale, stepHeightCalc, stepDurSlow;
        public float stepDurFast, raySphereRadius, footHeightOffset, footHangOffset, fastSpeedRef, rayCastRange;
    }

    public struct IkFootSimOutput
    {
        public float hipBob;
        public float3 hipSway;
        public bool airborne;
        public quaternion pelvisDelta;
    }
}

namespace VRMR.Fbik
{
    public struct IkArmSolveInput
    {
        public Vector3 Chest,
            Shoulder,
            RestElbow,
            RestHand,
            TargetPosition,
            HeadPosition,
            HintPosition,
            TorsoUp,
            TorsoForward,
            TorsoOut,
            TorsoA,
            TorsoB;

        public Quaternion TargetRotation, RestHandRotation, RestMidRotation, HintRotation;
        public bool HasHead, HasTrackerHint, HasHintRotation, IsLeft, TorsoCapsule;
        public float TorsoRadius, Dt, SmoothTime, MaxRateDeg;

        public static IkArmSolveInput Defaults(bool isLeft)
        {
            return new IkArmSolveInput
            {
                TorsoUp = Vector3.up, TorsoForward = Vector3.forward, TorsoOut = isLeft ? Vector3.left : Vector3.right,
                IsLeft = isLeft, Dt = 1f / 90f, SmoothTime = 0.08f, MaxRateDeg = 720f,
                TargetRotation = Quaternion.identity, RestHandRotation = Quaternion.identity,
                RestMidRotation = Quaternion.identity, HintRotation = Quaternion.identity
            };
        }
    }

    public struct IkArmSolveResult
    {
        public bool Valid, Switched;
        public Vector3 Elbow, Hand, Axis, Hinge, ElbowDir;

        public float SwivelDeg,
            PriorDeg,
            RawDeg,
            ReachRatio,
            ElbowDeg,
            HumeralDeg,
            PronationDeg,
            WristFlexDeg,
            WristDevDeg,
            TargetDistance,
            EffectiveDistance,
            UpperLength,
            LowerLength,
            Cost;
    }

    public struct IkShoulderSolveInput
    {
        public Vector3 ShoulderPos, UpperArmPos, HandTargetPos, TorsoUp, TorsoForward, TorsoOut;

        public float ArmLength, ElevationFactor, ProtractionFactor;

        // Tracked elbow: drives the lift and the shrug. UpperArmLength is the rest upper-arm joint -> elbow distance.
        public bool HasElbow;
        public Vector3 ElbowPos;

        public float UpperArmLength;

        // World direction the back of the hand points (zero if unknown). Used for the upside-down hand shrug.
        public Vector3 HandBack;
    }

    public struct IkShoulderSolveResult
    {
        public bool Apply;
        public Quaternion Delta;
        public float ElevationDeg, ProtractionDeg, HumeralElevationDeg, ReachRatio, ShrugDeg;
    }

    public struct IkLegSolveInput
    {
        public Vector3 Root, Mid, Tip;
        public Quaternion RootRotation, MidRotation;
        public Vector3 TargetPosition;
        public Quaternion TargetRotation;
        public Vector3 HintPosition;
        public float HintWeight;
        public Quaternion TargetOffset;
        public Vector3 BendNormal, AnteriorNormal;
        public Quaternion HintRotation;
        public bool HintIsTracker, HasHintRotation;
        public Vector3 FootForward, Up, HipsForward;

        public bool HasBodyFrame;

        // VRMR addition: right axes of the hips and of the foot target (control frames: world-aligned facing at calibration),
        // for the VRChat-style knee bend plane (see IkLegSolveCore.VrcKnee).
        public Vector3 HipsRight, FootRight;
    }

    public struct IkLegSolveResult
    {
        public Vector3 HintPosition;
        public Vector3 PoleDir;
        public Quaternion MidDelta, RootDelta, HintDelta, MidPostRoll, TipRotation;
        public bool HintApplied;
        public float ShinRollDeg;
        public Vector3 KneeSolved, FootSolved;
        public Quaternion RootRotationSolved, MidRotationSolved;
        public float UpperLength, LowerLength, TargetDistance, ReachRatio, KneeAngleDeg;
        public byte AxisSource;
        public float FootError;
    }

    public struct IkLegDiagnostics
    {
        public float ReachRatio, KneeAngleDeg, AxisSource, HintApplied;
        public float RawSwivelDeg, SmoothSwivelDeg, Conditioning, HoldGate, AnteriorGuardApplied, Seeded, ShinRollDeg;
        public float HipFlexionDeg, HipAbductionDeg, FemurTwistDeg;

        public static string Header =>
            "leg,reach,kneeDeg,axisSrc,hintApplied,rawSwivel,smoothSwivel,cond,holdGate,antGuard,seeded,shinRoll,hipFlex,hipAbd,femurTwist";

        public string ToRow(string leg)
        {
            return $"{leg},{ReachRatio:F4},{KneeAngleDeg:F2},{AxisSource:F0},{HintApplied:F0}," +
                   $"{RawSwivelDeg:F2},{SmoothSwivelDeg:F2}," +
                   $"{Conditioning:F4},{HoldGate:F3},{AnteriorGuardApplied:F0},{Seeded:F0},{ShinRollDeg:F2}," +
                   $"{HipFlexionDeg:F2},{HipAbductionDeg:F2},{FemurTwistDeg:F2}";
        }
    }
}
