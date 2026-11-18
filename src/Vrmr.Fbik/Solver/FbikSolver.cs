// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Plain C#: game-engine, Burst and job-system dependencies are replaced by the VRMR.Fbik shim.
using System.Runtime.CompilerServices;
namespace VRMR.Fbik
{
    public partial struct FbikSolver
    {
        public const float epsilon = 1e-5f, minMag = 1e-6f, sqrEpsilon = 1e-8f;
        public const int Count = 22, UpperChestSlot = Count - 1;
        public FixedList512Bytes<Vector3> slotPositions;
        public FixedList512Bytes<Quaternion> slotRotations, slotOffsets;
        public FixedList64Bytes<bool> slotWeights;
        public FixedList128Bytes<float> slotBlends;
        public NativeArray<Vector3> solvedPositions;
        public NativeArray<Quaternion> solvedRotations;
        public IkBoneHandle handleHips, handleSpine, handleChest, handleUpperChest, handleNeck, handleHead;
        public IkBoneHandle handleLeftShoulder, handleLeftUpperArm, handleLeftLowerArm, handleLeftHand;
        public IkBoneHandle handleRightShoulder, handleRightUpperArm, handleRightLowerArm, handleRightHand;
        public IkBoneHandle handleLeftUpperArmTwist, handleLeftLowerArmTwist, handleRightUpperArmTwist;
        public IkBoneHandle handleRightLowerArmTwist, handleLeftUpperLeg, handleLeftLowerLeg, handleLeftFoot;
        public IkBoneHandle handleLeftToe, handleRightUpperLeg, handleRightLowerLeg, handleRightFoot, handleRightToe;
        public NativeArray<IkBoneHandle> chainHeadToSpine;
        public NativeArray<IkSpineRestFrame> chainSpineRestFrames;
        public RalivIKSpine.SpineData SpineData;
        public int chainChestIdx, chainTorsoTopIdx;
        public Vector3 targetPositionHead, targetPositionHips;
        public Quaternion targetRotationHead, targetRotationHips, targetRotationChest;
        public Vector3 targetPositionChest, targetPositionChestRaw, playerUp, targetPositionLeftHand;
        public Vector3 hintPositionLeftHand, targetPositionRightHand, hintPositionRightHand;
        public Quaternion targetRotationLeftHand, hintRotationLeftHand, targetRotationRightHand, hintRotationRightHand;
        public Quaternion targetRotationLeftShoulder, targetRotationRightShoulder;
        public Vector3 targetPositionLeftLowerLeg, hintPositionLeftLowerLeg, targetPositionRightLowerLeg;
        public Vector3 hintPositionRightLowerLeg;
        public Quaternion targetRotationLeftLowerLeg, hintRotationLeftLowerLeg, targetRotationRightLowerLeg;
        public Quaternion hintRotationRightLowerLeg;
        public Vector3 kneeBendPrefLeft, kneeBendPrefRight, kneeAnteriorRef;
        public Quaternion leftDrivenTargetRot, rightDrivenTargetRot;
        public float leftToeBendDeg, rightToeBendDeg;
        public Vector3 leftToeBendAxis, rightToeBendAxis;
        public Quaternion offsetRotationHips, offsetRotationHead, offsetRotationChest, offsetRotationLeftFoot;
        public Quaternion offsetRotationRightFoot, offsetRotationLeftToe, offsetRotationRightToe;
        public Quaternion offsetRotationLeftShoulder, offsetRotationRightShoulder, offsetRotationLeftHand;
        public Quaternion offsetRotationRightHand;
        public FbikPlan plan;
        public float tposeBakeScale;
        public float tposeArmFitScale, tposeTorsoFitScale;
        public Vector3 tposeLengthNeckToHips, tposeHeadToNeckLocal, tposeLeftShoulderLocalDir;
        public Vector3 tposeRightShoulderLocalDir;
        public Quaternion tposeLeftShoulderRot, tposeRightShoulderRot, tposeChestRot;
        public float tposeShoulderToHandLeft, tposeShoulderToHandRight, tposeClavicleLenLeft, tposeClavicleLenRight;
        public float tposeShoulderToElbowLeft, tposeShoulderToElbowRight;
        // Back-of-hand direction in each hand bone's local space, captured in T-pose (zero = unknown).
        public Vector3 tposeLeftHandBackLocal, tposeRightHandBackLocal;
        public Quaternion tposeLeftLowerArmTwistBind, tposeLeftLowerArmChildBind, tposeRightLowerArmTwistBind;
        public Quaternion tposeRightLowerArmChildBind, tposeLeftUpperArmTwistBind, tposeLeftUpperArmChildBind;
        public Quaternion tposeRightUpperArmTwistBind, tposeRightUpperArmChildBind;
        public int spineMaxIterations;
        public float spineTolerance, minHeadSpineHeight, minFactor, maxFactor;
        public float moveBodyBackWhenCrouching, crouchDepth, standingHeadHeight;
        public float restChordHeadHips, restChordHeadLumbar, restChordHeadUpper, restReachHeadLumbar;
        public Quaternion chestTrackedRot, chestRestFromHead;
        public const float SpineHeadFollow = 0.3f; // How much spine bends with head, no hip tracker,
        public const float TrackedHipsPositionFollow = 1f;
        public const float TrackedHipsRotationFollow = 0f;
        public bool shoulderSolveEnabled;
        public float shoulderElevationFactor, shoulderProtractionFactor, shoulderTrackerBlendTime;
        public bool protectElbow, collideTrackedElbow;
        public float armSwivelSmoothTime, armSwivelMaxRateDeg;
        public bool collisionsEnabled;
        public float chestRadius, collisionSkin, handRadius, handSkin;
        public NativeArray<IkChestSpringState> chestSpring;
        public NativeArray<IkArmState> armState;
        public NativeArray<IkLegSlotState> legState;
        public NativeArray<IkLegDiagnostics> legDiagnostics;
        public IkPoseStream poseStream;
        public IkGizmoRecorder gizmos;
        static unsafe ref T Ref<T>(NativeArray<T> array, int index) where T : unmanaged
        {
            return ref array.ElementAt(index);
        }
        internal IkBoneHandle SlotHandle(int slot)
        {
            switch (slot)
            {
                case 0: return handleHips;
                case 1: return handleLeftUpperLeg;
                case 2: return handleRightUpperLeg;
                case 3: return handleLeftLowerLeg;
                case 4: return handleRightLowerLeg;
                case 5: return handleLeftFoot;
                case 6: return handleRightFoot;
                case 7: return handleSpine;
                case 8: return handleChest;
                case 9: return handleNeck;
                case 10: return handleHead;
                case 11: return handleLeftShoulder;
                case 12: return handleRightShoulder;
                case 13: return handleLeftUpperArm;
                case 14: return handleRightUpperArm;
                case 15: return handleLeftLowerArm;
                case 16: return handleRightLowerArm;
                case 17: return handleLeftHand;
                case 18: return handleRightHand;
                case 19: return handleLeftToe;
                case 20: return handleRightToe;
                case UpperChestSlot: return handleUpperChest;
                default: return IkBoneHandle.Unbound;
            }
        }
        public void Execute() => ProcessAnimation();
        public void ProcessAnimation()
        {
            poseStream.InvalidateWorldCache();
            RecordTargetGizmos();
            FbikMarkers.Spine.Begin();
            SolveSpinePass();
            FbikMarkers.Spine.End();
            RecordSpineGizmos();
            FbikMarkers.Shoulders.Begin();
            SolveShoulderPass();
            FbikMarkers.Shoulders.End();
            RecordShoulderGizmos();
            FbikMarkers.Legs.Begin();
            SolveLegPass();
            FbikMarkers.Legs.End();
            RecordLegGizmos();
            FbikMarkers.Arms.Begin();
            SolveArmPass();
            FbikMarkers.Arms.End();
            RecordArmGizmos();
            FbikMarkers.Toes.Begin();
            SolveToePass();
            FbikMarkers.Toes.End();
            RecordToeGizmos();
            FbikMarkers.TrackerOverrides.Begin();
            ApplyTrackerOverrides();
            FbikMarkers.TrackerOverrides.End();
            RecordOverrideGizmos();
            RecordFrameGizmos();
            RecordLimitGizmos();
            RecordReachGizmos();
            RecordNumberGizmos();
            RecordSkeletonGizmos();
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Slot(int humanBodyBone)
        {
            if (humanBodyBone >= 0 && humanBodyBone <= (int)HumanBodyBones.RightToes)
            {
                return humanBodyBone;
            }
            return humanBodyBone == (int)HumanBodyBones.UpperChest ? UpperChestSlot : -1;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetTargetPosition(int idx, in Vector3 v)
        {
            int s = Slot(idx);
            if (s >= 0 && s < slotPositions.Length)
            {
                slotPositions[s] = v;
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetTargetRotation(int idx, in Quaternion q)
        {
            int s = Slot(idx);
            if (s >= 0 && s < slotRotations.Length)
            {
                slotRotations[s] = q;
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetOffsetRotation(int idx, in Quaternion q)
        {
            int s = Slot(idx);
            if (s >= 0 && s < slotOffsets.Length)
            {
                slotOffsets[s] = q;
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetWeight(int idx, bool State)
        {
            int s = Slot(idx);
            if (s >= 0 && s < slotWeights.Length)
            {
                slotWeights[s] = State;
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetBlend(int idx, float blend)
        {
            int s = Slot(idx);
            if (s >= 0 && s < slotBlends.Length)
            {
                slotBlends[s] = blend > 0f ? (blend < 1f ? blend : 1f) : 0f;
            }
        }
        public void RescaleTposeScalars(float newScale)
        {
            if (float.IsNaN(newScale) || float.IsInfinity(newScale) || newScale <= 0f || tposeBakeScale <= 0f)
            {
                return;
            }

            float k = newScale / tposeBakeScale;
            if (Mathf.Abs(k - 1f) < 1e-6f)
            {
                return;
            }

            tposeShoulderToHandLeft *= k;
            tposeShoulderToHandRight *= k;
            tposeClavicleLenLeft *= k;
            tposeClavicleLenRight *= k;
            tposeShoulderToElbowLeft *= k;
            tposeShoulderToElbowRight *= k;
            tposeHeadToNeckLocal *= k;
            tposeLengthNeckToHips *= k;
            RescaleRalivSpineIK(k);

            tposeBakeScale = newScale;
        }
        public void RescaleTposeFit(float armScale, float torsoScale)
        {
            if (!(armScale > 0f) || !(torsoScale > 0f) || float.IsInfinity(armScale) || float.IsInfinity(torsoScale))
            {
                return;
            }
            float ka = armScale / (tposeArmFitScale > 0f ? tposeArmFitScale : 1f), kt = torsoScale / (tposeTorsoFitScale > 0f ? tposeTorsoFitScale : 1f);
            if (Mathf.Abs(ka - 1f) >= 1e-6f)
            {
                tposeShoulderToHandLeft = tposeClavicleLenLeft + (tposeShoulderToHandLeft - tposeClavicleLenLeft) * ka;
                tposeShoulderToHandRight = tposeClavicleLenRight + (tposeShoulderToHandRight - tposeClavicleLenRight) * ka;
                tposeShoulderToElbowLeft = tposeClavicleLenLeft + (tposeShoulderToElbowLeft - tposeClavicleLenLeft) * ka;
                tposeShoulderToElbowRight = tposeClavicleLenRight + (tposeShoulderToElbowRight - tposeClavicleLenRight) * ka;
            }
            if (Mathf.Abs(kt - 1f) >= 1e-6f)
            {
                RescaleRalivSpineSegments(kt, chainTorsoTopIdx);
                float chain = RalivRestChainLength();
                if (chain > 0f)
                {
                    minHeadSpineHeight = chain;
                }
            }
            tposeArmFitScale = armScale;
            tposeTorsoFitScale = torsoScale;
        }
        public void Destroy()
        {
            if (chainHeadToSpine.IsCreated) chainHeadToSpine.Dispose();
            if (chainSpineRestFrames.IsCreated) chainSpineRestFrames.Dispose();
            if (chestSpring.IsCreated) chestSpring.Dispose();
            if (armState.IsCreated) armState.Dispose();
            if (legState.IsCreated) legState.Dispose();
            if (legDiagnostics.IsCreated) legDiagnostics.Dispose();
            if (solvedPositions.IsCreated) solvedPositions.Dispose();
            if (solvedRotations.IsCreated) solvedRotations.Dispose();

            gizmos.Dispose();
        }
    }
}
