// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Port for VR Motion Recorder:
//  * Physics.Raycast / SphereCast / RaycastCommand ground queries are replaced by an analytic infinite
//    ground plane (FbikRig.GroundPoint / player up). Everything downstream of the query (filtering by
//    hips height, foot height offsets, surface-normal and toe-bend fitting) is unchanged.
//  * Transform reads of the avatar's current foot/hips bones are replaced by the last solved pose when
//    available (else the bone controls), see PORTING_NOTES.md.
//  * Gizmo drawing and height-change events are dropped; serialized tunables keep their upstream defaults.

namespace VRMR.Fbik
{
    public sealed class IkFootDriver
    {
        const int probeRays = 4;
        const int probeMaxHits = 8;
        internal const float SettledPlantedTime = 10f;
        const float heelProbeFrac = 0.45f;
        const float ballProbeFrac = 0.85f;
        const float toeProbeFrac = 1.30f;
        const float footHalfWidthFrac = 0.28f;
        const float toeMaxDorsiDeg = 40f;
        const float toeMaxPlantarDeg = 15f;
        const float toeBendRate = 12f;
        const float surfaceNormalRate = 14f;
        public static bool SurfaceProbesEnabled = true;
        readonly float bodyFwdChestWeight = 2f;
        readonly float bodyFwdHeadWeight = 1f;
        readonly float bodyFwdHipsWeight = 3f;
        readonly float bodyFwdRateMoving = 6f;
        readonly float bodyFwdRateStationary = 2.5f;
        readonly float fastSpeedMul = 1.2f;
        readonly float[] footGroundUp = new float[2];
        readonly bool[] footGroundValid = new bool[2];
        readonly float footHeightOffsetMul = 0.2f;
        readonly float footSideEnforceFraction = 0.2f;
        readonly float hipBobFraction = 0.02f;
        readonly float idealSideEnforceFraction = 0.3f;
        readonly float idleBoostFraction = 0.5f;
        readonly float idleSpeedThreshold = 0.05f;
        readonly float leadOffsetFactor = 0.6f;
        readonly float maxFootTiltDegrees = 35f;
        readonly float maxFootYawDegrees = 18f;
        readonly float maxPlantedYawDegrees = 20f;
        readonly float maxPredictionFraction = 0.35f;
        readonly float maxVelocityOffsetFraction = 0.35f;
        readonly float maxVerticalDriftFraction = 0.25f;
        readonly float plantedLerpSpeed = 40f;
        readonly float predictionFactor = 0.9f;

        readonly ProbeHit[] probeResults = new ProbeHit[probeRays * probeMaxHits];
        readonly float raySphereRadiusMul = 0.3f;
        readonly FbikRig rig;
        readonly float rotationLerpSpeed = 16f;
        readonly float stepArcDropExp = 1.4f;
        readonly float stepArcLiftExp = 0.6f;
        readonly float stepDurFastMul = 0.18f;
        readonly float stepDurSlowMul = 0.30f;
        readonly float stepHeightMinFraction = 0.4f;
        readonly float stepHeightMul = 0.30f;
        readonly float stepHeightStrideRefFraction = 0.45f;
        readonly float stepTargetSideFraction = 0.15f;
        readonly float stepTriggerMul = 0.18f;
        readonly float strideScaleMul = 0.15f;
        readonly float velocityBiasFactor = 0.18f;
        readonly float velocitySmoothAccel = 25f;
        readonly float velocitySmoothDecel = 50f;

        float baseRightShinLen,
            baseRightLegLen,
            baseFootLength,
            baseAnkleHeight,
            baseUpperLegToFootVertical,
            baseFootRestHeight;

        float baseStanceWidth, baseHipToFoot, baseLeftThighLen, baseLeftShinLen, baseLeftLegLen, baseRightThighLen;
        IkFootSimParams cachedParams;
        Vector3 cachedPlayerFwd = Vector3.forward;
        Vector3 cachedPlayerRight = Vector3.right;
        Vector3 cachedPlayerUp = Vector3.up;
        Quaternion footAlignLeft = Quaternion.identity;
        Quaternion footAlignRight = Quaternion.identity;
        NativeArray<IkFootNativeState> nativeFeet;
        NativeArray<IkFootSimInput> nativeInput;
        NativeArray<IkFootSimOutput> nativeOutput;
        NativeArray<IkFootSimState> nativeSimState;
        bool paramsDirty = true;
        float prevHeadYaw;
        float probeElapsedLeft, probeElapsedRight;
        int probeNextFoot;
        bool probePending;

        IkFootProbePlan probePlan;

        float rayCastRange;
        Vector3 smoothedVelocity;

        float leftLegLen,
            rightLegLen,
            leftThighLen,
            leftShinLen,
            rightThighLen,
            rightShinLen,
            footLength,
            ankleHeight,
            upperLegToFootVertical,
            footRestHeight;

        float strideScale,
            stepHeightCalc,
            stepDurSlow,
            stepDurFast,
            raySphereRadius,
            footHeightOffset,
            footHangOffset,
            fastSpeedRef;

        public IkFootDriver(FbikRig rig) { this.rig = rig; }
        public bool IsInitialized { get; private set; }
        public Vector3 LeftFootPosition => nativeFeet.IsCreated ? Foot(0).currentPos : Vector3.zero;
        public Quaternion LeftFootRotation => nativeFeet.IsCreated ? Foot(0).currentRot : Quaternion.identity;
        public Vector3 RightFootPosition => nativeFeet.IsCreated ? Foot(1).currentPos : Vector3.zero;
        public Quaternion RightFootRotation => nativeFeet.IsCreated ? Foot(1).currentRot : Quaternion.identity;
        public Vector3 LeftPlantedPos => nativeFeet.IsCreated ? Foot(0).plantedPos : Vector3.zero;
        public Vector3 RightPlantedPos => nativeFeet.IsCreated ? Foot(1).plantedPos : Vector3.zero;
        public bool LeftIsPlanted => nativeFeet.IsCreated && Foot(0).phase == 0;
        public bool RightIsPlanted => nativeFeet.IsCreated && Foot(1).phase == 0;
        public bool LastGroundHit { get; private set; }
        public float LastGroundUp { get; private set; }
        public float HipsUp { get; private set; }
        public bool IsSimulationPending { get; private set; }

        public float LeftToeBendDegrees => IsInitialized && nativeFeet.IsCreated ? nativeFeet[0].toeBendDeg : 0f;
        public float RightToeBendDegrees => IsInitialized && nativeFeet.IsCreated ? nativeFeet[1].toeBendDeg : 0f;

        public Vector3 LeftToeBendAxis =>
            IsInitialized && nativeFeet.IsCreated ? nativeFeet[0].toeBendAxis : Vector3.zero;

        public Vector3 RightToeBendAxis =>
            IsInitialized && nativeFeet.IsCreated ? nativeFeet[1].toeBendAxis : Vector3.zero;

        public Vector3 SmoothedVelocity => smoothedVelocity;
        public float Speed => smoothedVelocity.magnitude;
        public float CalibratedStanceWidth { get; private set; }

        public float CalibratedHipToFoot { get; private set; }

        public float DerivedStepTrigger { get; private set; }

        public float FootHeightOffset => footHeightOffset;

        ref IkFootNativeState Foot(int slot)
        {
            return ref nativeFeet.ElementAt(slot);
        }

        public void NotifyReEngaging()
        {
            DiscardPendingProbes();
            footGroundValid[0] = footGroundValid[1] = false;
            if (!nativeFeet.IsCreated)
            {
                return;
            }

            ReEngageFoot(ref Foot(0), HumanBodyBones.LeftFoot,
                FootWorldPosition(rig.Bones.LeftFootControl, HumanBodyBones.LeftFoot));
            ReEngageFoot(ref Foot(1), HumanBodyBones.RightFoot,
                FootWorldPosition(rig.Bones.RightFootControl, HumanBodyBones.RightFoot));
        }

        void ReEngageFoot(ref IkFootNativeState f, HumanBodyBones bone, Vector3 position)
        {
            f.currentPos = f.plantedPos = position;
            f.phase = 0;
            f.plantedTime = SettledPlantedTime;
            if (rig.Model.Has(bone))
            {
                f.currentRot = f.plantedRot = f.stepStartRot = rig.BoneWorldRotation(bone);
            }

            f.plantedBodyFwd = float3.zero;
            ClearTransient(ref f);
        }

        static void ClearTransient(ref IkFootNativeState f)
        {
            f.wantsStep = false;
            f.stepUrgency = 0f;
            f.predictedTargetXZ = float3.zero;
        }

        public void Teleport(Vector3 delta)
        {
            if (!IsInitialized)
            {
                return;
            }

            IsSimulationPending = false;
            DiscardPendingProbes();
            footGroundValid[0] = footGroundValid[1] = false;
            if (nativeFeet.IsCreated)
            {
                ShiftFoot(ref Foot(0), delta);
                ShiftFoot(ref Foot(1), delta);
            }

            if (nativeSimState.IsCreated)
            {
                var sim = nativeSimState[0];
                sim.prevHeadPos += (float3)delta;
                nativeSimState[0] = sim;
            }
        }

        static void ShiftFoot(ref IkFootNativeState f, Vector3 delta)
        {
            float3 d = delta;
            f.plantedPos += d;
            f.currentPos += d;
            f.idealPos += d;
            f.stepStartPos += d;
            f.stepTargetPos += d;
            ClearTransient(ref f);
        }

        Vector3 HipsWorldPosition()
        {
            return rig.Bones.HipsControl.OutgoingWorldData.position;
        }

        Vector3 FootWorldPosition(IkBoneControl c, HumanBodyBones bone)
        {
            if (c != null) return c.OutgoingWorldData.position;
            return rig.BoneWorldPosition(bone);
        }

        public void InitializeVariables()
        {
            var model = rig.Model;
            CaptureFootAlignment();
            MeasureFromCalibration(model);
            StoreBaseMeasurements();
            DeriveStepParameters();
            rayCastRange = Mathf.Max(CalibratedHipToFoot + ankleHeight, Mathf.Max(leftLegLen, rightLegLen)) * 2.15f;
            var ltw = rig.localToWorldMatrix;
            cachedPlayerUp = ltw.MultiplyVector(Vector3.up).normalized;
            cachedPlayerFwd = ltw.MultiplyVector(Vector3.forward).normalized;
            cachedPlayerRight = ltw.MultiplyVector(Vector3.right).normalized;
            DisposeNativeArrays();
            nativeFeet = new NativeArray<IkFootNativeState>(2, Allocator.Persistent);
            nativeSimState = new NativeArray<IkFootSimState>(1, Allocator.Persistent);
            nativeInput = new NativeArray<IkFootSimInput>(1, Allocator.Persistent);
            nativeOutput = new NativeArray<IkFootSimOutput>(1, Allocator.Persistent);
            probePending = false;
            probeNextFoot = 0;
            probeElapsedLeft = probeElapsedRight = 0f;
            ref var leftN = ref Foot(0);
            ref var rightN = ref Foot(1);
            leftN.sideSign = -1;
            rightN.sideSign = +1;
            leftN.thighLen = leftThighLen;
            leftN.shinLen = leftShinLen;
            leftN.legLength = leftLegLen;
            rightN.thighLen = rightThighLen;
            rightN.shinLen = rightShinLen;
            rightN.legLength = rightLegLen;
            leftN.plantedTime = rightN.plantedTime = SettledPlantedTime;
            leftN.filteredNormal = rightN.filteredNormal = new float3(0f, 1f, 0f);
            InitPose(ref leftN, HumanBodyBones.LeftFoot);
            InitPose(ref rightN, HumanBodyBones.RightFoot);
            leftN.landRot = leftN.currentRot;
            rightN.landRot = rightN.currentRot;
            var bodyFwd = rig.AvatarRootRotation * Vector3.forward;
            var bodyRight = Vector3.Cross(cachedPlayerUp, bodyFwd).normalized;
            prevHeadYaw = HeadYaw();
            nativeSimState[0] = new IkFootSimState
            {
                prevHeadPos = HipsWorldPosition(),
                hasPrevHeadPos = true,
                prevHeadYaw = prevHeadYaw,
                smoothedVelocity = float3.zero,
                smoothedBodyFwd = bodyFwd,
                smoothedBodyRight = bodyRight
            };
            IsInitialized = true;
        }

        public void Dispose()
        {
            IsSimulationPending = false;
            DiscardPendingProbes();
            DisposeNativeArrays();
            IsInitialized = false;
        }

        void DiscardPendingProbes()
        {
            probePending = false;
        }

        void DisposeNativeArrays()
        {
            DiscardPendingProbes();
            if (nativeFeet.IsCreated) nativeFeet.Dispose();
            if (nativeSimState.IsCreated) nativeSimState.Dispose();
            if (nativeInput.IsCreated) nativeInput.Dispose();
            if (nativeOutput.IsCreated) nativeOutput.Dispose();
        }

        IkFootSimParams BuildParams()
        {
            return new IkFootSimParams
            {
                predictionFactor = predictionFactor,
                velocityBiasFactor = velocityBiasFactor,
                leadOffsetFactor = leadOffsetFactor,
                maxVelocityOffsetFraction = maxVelocityOffsetFraction,
                maxPredictionFraction = maxPredictionFraction,
                plantedLerpSpeed = plantedLerpSpeed,
                rotationLerpSpeed = rotationLerpSpeed,
                velocitySmoothAccel = velocitySmoothAccel,
                velocitySmoothDecel = velocitySmoothDecel,
                bodyFwdRateMoving = bodyFwdRateMoving,
                bodyFwdRateStationary = bodyFwdRateStationary,
                maxFootTiltDegrees = maxFootTiltDegrees,
                maxFootYawDegrees = maxFootYawDegrees,
                stepArcLiftExp = stepArcLiftExp,
                stepArcDropExp = stepArcDropExp,
                stepHeightMinFraction = stepHeightMinFraction,
                stepHeightStrideRefFraction = stepHeightStrideRefFraction,
                idleSpeedThreshold = idleSpeedThreshold * (fastSpeedRef / 2.921f),
                idleBoostFraction = idleBoostFraction,
                maxPlantedYawDegrees = maxPlantedYawDegrees,
                idealSideEnforceFraction = idealSideEnforceFraction,
                stepTargetSideFraction = stepTargetSideFraction,
                footSideEnforceFraction = footSideEnforceFraction,
                maxVerticalDriftFraction = maxVerticalDriftFraction,
                bodyFwdHipsWeight = bodyFwdHipsWeight,
                bodyFwdChestWeight = bodyFwdChestWeight,
                bodyFwdHeadWeight = bodyFwdHeadWeight,
                hipBobFraction = hipBobFraction,
                stanceWidth = CalibratedStanceWidth,
                hipToFoot = CalibratedHipToFoot,
                leftLegLen = leftLegLen,
                rightLegLen = rightLegLen,
                leftThighLen = leftThighLen,
                leftShinLen = leftShinLen,
                rightThighLen = rightThighLen,
                rightShinLen = rightShinLen,
                footLength = footLength,
                ankleHeight = ankleHeight,
                footAlignLeft = footAlignLeft,
                footAlignRight = footAlignRight,
                stepTriggerDist = DerivedStepTrigger,
                strideScale = strideScale,
                stepHeightCalc = stepHeightCalc,
                stepDurSlow = stepDurSlow,
                stepDurFast = stepDurFast,
                raySphereRadius = raySphereRadius,
                footHeightOffset = footHeightOffset,
                footHangOffset = footHangOffset,
                fastSpeedRef = fastSpeedRef,
                rayCastRange = rayCastRange
            };
        }

        // TposeWorld == the model's rest pose (avatar root at the origin).
        static bool TryTP(IkAvatarModel m, HumanBodyBones b, out Vector3 p)
        {
            p = Vector3.zero;
            if (!m.Has(b) || m.Pos(b) == Vector3.zero)
            {
                return false;
            }

            p = m.Pos(b);
            return true;
        }

        void MeasureFromCalibration(IkAvatarModel tpose)
        {
            bool hasHips = TryTP(tpose, HumanBodyBones.Hips, out var tH);
            bool hasLUL = TryTP(tpose, HumanBodyBones.LeftUpperLeg, out var tLUL);
            bool hasRUL = TryTP(tpose, HumanBodyBones.RightUpperLeg, out var tRUL);
            bool hasLLL = TryTP(tpose, HumanBodyBones.LeftLowerLeg, out var tLLL);
            bool hasRLL = TryTP(tpose, HumanBodyBones.RightLowerLeg, out var tRLL);
            bool hasLF = TryTP(tpose, HumanBodyBones.LeftFoot, out var tLF);
            bool hasRF = TryTP(tpose, HumanBodyBones.RightFoot, out var tRF);
            bool hasLT = TryTP(tpose, HumanBodyBones.LeftToes, out var tLT);
            bool hasRT = TryTP(tpose, HumanBodyBones.RightToes, out var tRT);
            if (hasLF && hasRF)
            {
                var d = tRF - tLF;
                d.y = 0f;
                CalibratedStanceWidth = d.magnitude;
            }
            else
            {
                FallbackStanceWidth();
            }

            if (hasHips && hasLF && hasRF)
            {
                CalibratedHipToFoot = Mathf.Abs(tH.y - (tLF.y + tRF.y) * 0.5f);
            }
            else
            {
                FallbackHipToFoot();
            }

            if (hasLUL && hasLLL && hasLF)
            {
                leftThighLen = Vector3.Distance(tLUL, tLLL);
                leftShinLen = Vector3.Distance(tLLL, tLF);
            }
            else if (hasHips && hasLF)
            {
                float t = Vector3.Distance(tH, tLF);
                leftThighLen = t * 0.55f;
                leftShinLen = t * 0.45f;
            }
            else
            {
                FallbackLegLens(true);
            }

            leftLegLen = leftThighLen + leftShinLen;
            if (hasRUL && hasRLL && hasRF)
            {
                rightThighLen = Vector3.Distance(tRUL, tRLL);
                rightShinLen = Vector3.Distance(tRLL, tRF);
            }
            else if (hasHips && hasRF)
            {
                float t = Vector3.Distance(tH, tRF);
                rightThighLen = t * 0.55f;
                rightShinLen = t * 0.45f;
            }
            else
            {
                FallbackLegLens(false);
            }

            rightLegLen = rightThighLen + rightShinLen;
            if (hasLF && hasLT && hasRF && hasRT)
            {
                footLength = (Vector3.Distance(tLF, tLT) + Vector3.Distance(tRF, tRT)) * 0.5f;
            }
            else if (hasLF && hasLT)
            {
                footLength = Vector3.Distance(tLF, tLT);
            }
            else if (hasRF && hasRT)
            {
                footLength = Vector3.Distance(tRF, tRT);
            }
            else
            {
                footLength = CalibratedHipToFoot * 0.15f;
            }

            footRestHeight = 0f;
            if (hasLF && hasRF)
            {
                float avgFootY = (tLF.y + tRF.y) * 0.5f;
                footRestHeight = avgFootY - rig.Model.SoleHeight;
                if (hasLT || hasRT)
                {
                    float groundRefY;
                    if (hasLT && hasRT) groundRefY = (tLT.y + tRT.y) * 0.5f;
                    else if (hasLT) groundRefY = tLT.y;
                    else groundRefY = tRT.y;
                    ankleHeight = Mathf.Max(0.01f, avgFootY - groundRefY);
                }
                else
                {
                    ankleHeight = Mathf.Max(0.01f, CalibratedHipToFoot * 0.05f);
                }
            }
            else
            {
                ankleHeight = Mathf.Max(0.01f, CalibratedHipToFoot * 0.05f);
            }

            if (hasLUL && hasRUL && hasLF && hasRF)
            {
                float leftV = Mathf.Abs(tLUL.y - tLF.y);
                float rightV = Mathf.Abs(tRUL.y - tRF.y);
                upperLegToFootVertical = (leftV + rightV) * 0.5f;
            }
            else
            {
                upperLegToFootVertical = CalibratedHipToFoot;
            }

            CalibratedStanceWidth = Mathf.Max(0.04f, CalibratedStanceWidth);
            CalibratedHipToFoot = Mathf.Max(0.15f, CalibratedHipToFoot);
            leftLegLen = Mathf.Max(0.15f, leftLegLen);
            rightLegLen = Mathf.Max(0.15f, rightLegLen);
            footLength = Mathf.Max(0.02f, footLength);
            ankleHeight = Mathf.Max(0.005f, ankleHeight);
            footRestHeight = footRestHeight > ankleHeight ? footRestHeight : ankleHeight;
        }

        public static void DeriveFootHeights(float ankleHeight, float footRestHeight, float upperLegToFootVertical,
            float avgLeg, float hangMul, out float plantedOffset, out float hangOffset)
        {
            const float refLeg = 0.87f;
            float straightLegLimit = upperLegToFootVertical + ankleHeight - avgLeg;
            hangOffset = Mathf.Clamp(Mathf.Min(ankleHeight * hangMul, straightLegLimit), avgLeg * (0.001f / refLeg),
                avgLeg * (0.05f / refLeg));
            plantedOffset = Mathf.Max(hangOffset, footRestHeight);
        }

        void DeriveStepParameters()
        {
            float avgLeg = (leftLegLen + rightLegLen) * 0.5f;
            float avgShin = (leftShinLen + rightShinLen) * 0.5f;
            const float refLeg = 0.87f;
            float pendulum = Mathf.PI * Mathf.Sqrt(avgLeg / 9.81f);
            float speedRef = Mathf.Sqrt(avgLeg * 9.81f);
            raySphereRadius = Mathf.Clamp(footLength * raySphereRadiusMul, avgLeg * (0.02f / refLeg),
                avgLeg * (0.12f / refLeg));
            DeriveFootHeights(ankleHeight, footRestHeight, upperLegToFootVertical, avgLeg, footHeightOffsetMul,
                out footHeightOffset, out footHangOffset);
            DerivedStepTrigger =
                Mathf.Clamp(avgLeg * stepTriggerMul, avgLeg * (0.04f / refLeg), avgLeg * (0.18f / refLeg));
            strideScale = Mathf.Clamp(avgLeg * strideScaleMul, avgLeg * (0.02f / refLeg), avgLeg * (0.22f / refLeg));
            stepHeightCalc = Mathf.Clamp(avgShin * stepHeightMul, avgLeg * (0.03f / refLeg), avgLeg * (0.20f / refLeg));
            stepDurSlow = Mathf.Clamp(pendulum * stepDurSlowMul, pendulum * (0.10f / 0.9356f),
                pendulum * (0.30f / 0.9356f));
            stepDurFast = Mathf.Clamp(pendulum * stepDurFastMul, pendulum * (0.06f / 0.9356f),
                pendulum * (0.18f / 0.9356f));
            fastSpeedRef = Mathf.Clamp(fastSpeedMul * speedRef, speedRef * (1.0f / 2.921f), speedRef * 2.5f);
            paramsDirty = true;
        }

        void StoreBaseMeasurements()
        {
            baseStanceWidth = CalibratedStanceWidth;
            baseHipToFoot = CalibratedHipToFoot;
            baseLeftThighLen = leftThighLen;
            baseLeftShinLen = leftShinLen;
            baseLeftLegLen = leftLegLen;
            baseRightThighLen = rightThighLen;
            baseRightShinLen = rightShinLen;
            baseRightLegLen = rightLegLen;
            baseFootLength = footLength;
            baseAnkleHeight = ankleHeight;
            baseUpperLegToFootVertical = upperLegToFootVertical;
            baseFootRestHeight = footRestHeight;
        }

        public void RefreshBodyFitScale()
        {
            if (!IsInitialized)
            {
                return;
            }

            ApplyScaleToMeasurements(rig.ScaledToMatchValue);
        }

        float BodyFitLegScale()
        {
            var fit = rig.AppliedBodyFit;
            if (!fit.HasBodyFit)
            {
                return 1f;
            }

            float legScale = fit.LegScale;
            return legScale > 0f && !float.IsNaN(legScale) && !float.IsInfinity(legScale) ? legScale : 1f;
        }

        void ApplyScaleToMeasurements(float scale)
        {
            float legScale = scale * BodyFitLegScale();
            CalibratedStanceWidth = baseStanceWidth * scale;
            CalibratedHipToFoot = baseHipToFoot * legScale;
            leftThighLen = baseLeftThighLen * legScale;
            leftShinLen = baseLeftShinLen * legScale;
            leftLegLen = baseLeftLegLen * legScale;
            rightThighLen = baseRightThighLen * legScale;
            rightShinLen = baseRightShinLen * legScale;
            rightLegLen = baseRightLegLen * legScale;
            footLength = baseFootLength * scale;
            ankleHeight = baseAnkleHeight * scale;
            footRestHeight = baseFootRestHeight * scale;
            upperLegToFootVertical = baseUpperLegToFootVertical * legScale;
            DeriveStepParameters();
            rayCastRange = Mathf.Max(CalibratedHipToFoot + ankleHeight, Mathf.Max(leftLegLen, rightLegLen)) * 2.15f;
            paramsDirty = true;
            if (nativeFeet.IsCreated)
            {
                var ln = nativeFeet[0];
                ln.thighLen = leftThighLen;
                ln.shinLen = leftShinLen;
                ln.legLength = leftLegLen;
                nativeFeet[0] = ln;
                var rn = nativeFeet[1];
                rn.thighLen = rightThighLen;
                rn.shinLen = rightShinLen;
                rn.legLength = rightLegLen;
                nativeFeet[1] = rn;
            }
        }

        /// <summary>Upstream OnHeightChanged (OnPlayersHeightChangedNextFrame).</summary>
        public void OnHeightChanged()
        {
            if (!IsInitialized)
            {
                return;
            }

            DiscardPendingProbes();
            ApplyScaleToMeasurements(rig.ScaledToMatchValue);
            if (nativeFeet.IsCreated)
            {
                ReSnapFoot(ref Foot(0), HumanBodyBones.LeftFoot);
                ReSnapFoot(ref Foot(1), HumanBodyBones.RightFoot);
                ClearTransient(ref Foot(0));
                ClearTransient(ref Foot(1));
            }
        }

        void ReSnapFoot(ref IkFootNativeState f, HumanBodyBones bone)
        {
            if (!rig.Model.Has(bone)) return;
            var origin =
                FootWorldPosition(f.sideSign < 0 ? rig.Bones.LeftFootControl : rig.Bones.RightFootControl, bone) +
                cachedPlayerUp * (CalibratedHipToFoot * 0.33f);
            if (GroundCast(origin, -cachedPlayerUp, rayCastRange, 0f, Vector3.Dot(HipsWorldPosition(), cachedPlayerUp),
                    out var hitPoint, out var hitNormal, out _))
            {
                var snapped = hitPoint + hitNormal * footHeightOffset;
                f.currentPos = f.plantedPos = f.idealPos = snapped;
                f.filteredNormal = hitNormal;
                int side = f.sideSign < 0 ? 0 : 1;
                footGroundUp[side] = Vector3.Dot(hitPoint, cachedPlayerUp);
                footGroundValid[side] = true;
            }
        }

        public void Simulate(float dt)
        {
            ScheduleSimulate(dt);
            CompleteSimulate();
            ScheduleSurfaceProbes();
        }

        public void ScheduleSimulate(float dt)
        {
            IsSimulationPending = false;
            if (!IsInitialized || dt <= 0f) return;
            var ltw = rig.localToWorldMatrix;
            cachedPlayerUp = ltw.MultiplyVector(Vector3.up).normalized;
            cachedPlayerFwd = ltw.MultiplyVector(Vector3.forward).normalized;
            cachedPlayerRight = ltw.MultiplyVector(Vector3.right).normalized;
            var headData = rig.Bones.HeadControl.OutgoingWorldData;
            var hipsData = rig.Bones.HipsControl.OutgoingWorldData;
            var chestCtrl = rig.Bones.ChestControl;
            var hipsPosition = HipsWorldPosition();
            float hipsUpComponent = Vector3.Dot(hipsPosition, cachedPlayerUp);
            bool groundHit = GroundCast(hipsPosition, -cachedPlayerUp, rayCastRange, 0f, hipsUpComponent,
                out var chPoint, out _, out _);
            LastGroundHit = groundHit;
            LastGroundUp = groundHit ? Vector3.Dot(chPoint, cachedPlayerUp) : float.NaN;
            HipsUp = hipsUpComponent;
            if (SurfaceProbesEnabled)
            {
                ApplySurfaceProbes(dt);
            }

            var avatarRotation = rig.AvatarRootRotation;
            nativeInput[0] = new IkFootSimInput
            {
                dt = dt,
                headPos = headData.position,
                hipsPos = hipsPosition,
                hipsRot = hipsData.rotation,
                chestRot = chestCtrl.OutgoingWorldData.rotation,
                headRot = headData.rotation,
                avatarForward = avatarRotation * Vector3.forward,
                avatarRight = avatarRotation * Vector3.right,
                hasChest = true,
                groundHit = groundHit,
                groundPoint = groundHit ? chPoint : float3.zero,
                leftGroundValid = footGroundValid[0],
                rightGroundValid = footGroundValid[1],
                leftGroundUp = footGroundUp[0],
                rightGroundUp = footGroundUp[1],
                playerUp = cachedPlayerUp
            };
            if (paramsDirty)
            {
                cachedParams = BuildParams();
                paramsDirty = false;
            }

            var job = new IkFootSimulateJob
            {
                p = cachedParams,
                feet = nativeFeet,
                simState = nativeSimState,
                input = nativeInput,
                output = nativeOutput
            };
            job.Execute();
            IsSimulationPending = true;
        }

        public void CompleteSimulate()
        {
            if (!IsSimulationPending) return;
            IsSimulationPending = false;
            ref var leftN = ref nativeFeet.ElementAt(0);
            ref var rightN = ref nativeFeet.ElementAt(1);
            if (leftN.wantsStep)
            {
                FinalizeStep(ref leftN);
                leftN.wantsStep = false;
            }

            if (rightN.wantsStep)
            {
                FinalizeStep(ref rightN);
                rightN.wantsStep = false;
            }

            smoothedVelocity = nativeSimState[0].smoothedVelocity;
        }

        void FinalizeStep(ref IkFootNativeState f)
        {
            var sim = nativeSimState[0];
            var velFlat = (float3)ProjectHorizontal(sim.smoothedVelocity);
            float speed = math.length(velFlat);
            float fastYawRef = Mathf.Max(1f, 0.5f * maxPlantedYawDegrees / Mathf.Max(0.01f, stepDurFast));
            float absYawRate = Mathf.Abs(sim.smoothedYawRateDeg);
            float yawPacing = Mathf.Clamp01(absYawRate / fastYawRef);
            float urgencyT = Mathf.Clamp01(f.stepUrgency);
            f.phase = 1;
            f.stepStartPos = f.currentPos;
            f.stepStartRot = f.currentRot;
            f.stepTimer = 0f;
            f.stepDur = Mathf.Lerp(stepDurSlow, stepDurFast, urgencyT);
            f.stepArcScale = IkFootSimulateJob.TurnStepArcFloor * yawPacing;
            float hipsUpComp = Vector3.Dot(HipsWorldPosition(), cachedPlayerUp);
            Vector3 targetXZ = f.predictedTargetXZ;
            var rayOrig = targetXZ + cachedPlayerUp * rayCastRange * 0.5f;
            int side = f.sideSign < 0 ? 0 : 1;
            if (GroundCast(rayOrig, -cachedPlayerUp, rayCastRange, raySphereRadius, hipsUpComp, out var hitPoint,
                    out var hitNormal, out _))
            {
                f.stepTargetPos = hitPoint + hitNormal * footHeightOffset;
                f.filteredNormal = hitNormal;
                footGroundUp[side] = Vector3.Dot(hitPoint, cachedPlayerUp);
                footGroundValid[side] = true;
            }
            else
            {
                float targetUpComp = hipsUpComp - CalibratedHipToFoot - ankleHeight + footHangOffset;
                var targetFlat = ProjectHorizontal(targetXZ);
                f.stepTargetPos = targetFlat + cachedPlayerUp * targetUpComp;
                footGroundValid[side] = false;
            }

            var bodyFwd = sim.smoothedBodyFwd;
            var rawR = Vector3.Cross(cachedPlayerUp, bodyFwd).normalized;
            if (rawR.sqrMagnitude < 0.001f) rawR = Vector3.right;
            Vector3 stp = f.stepTargetPos;
            float stpUpComp = Vector3.Dot(stp, cachedPlayerUp);
            var hipsFlat = ProjectHorizontal(HipsWorldPosition());
            var hGround = hipsFlat + cachedPlayerUp * stpUpComp;
            EnforceSide(ref stp, hGround, rawR, f.sideSign, CalibratedStanceWidth * stepTargetSideFraction);
            f.stepTargetPos = stp;
        }

        /// <summary>
        ///     Replaces Physics.RaycastNonAlloc / SphereCastNonAlloc against the world: one infinite ground plane
        ///     through rig.GroundPoint with normal = player up. Same filtering as upstream (distance > 0, hit not above
        ///     maxUpComponent).
        ///     A sphere cast reports the plane contact point and the distance travelled by the sphere centre.
        /// </summary>
        bool GroundCast(Vector3 origin, Vector3 dir, float maxDist, float sphereRadius, float maxUpComponent,
            out Vector3 point, out Vector3 normal, out float distance)
        {
            point = Vector3.zero;
            normal = Vector3.up;
            distance = 0f;
            if (!rig.HasGround)
            {
                return false;
            }

            var n = cachedPlayerUp;
            float denom = Vector3.Dot(dir, n);
            if (denom > -1e-6f)
            {
                return false;
            }

            float t = Vector3.Dot(rig.GroundPoint - origin, n) / denom;
            float d = t - (sphereRadius > 0f ? sphereRadius / -denom : 0f);
            if (t < 0f || d > maxDist || d <= 0f)
            {
                return false;
            }

            var p = origin + dir * t;
            if (Vector3.Dot(p, cachedPlayerUp) > maxUpComponent)
            {
                return false;
            }

            point = p;
            normal = n;
            distance = d;
            return true;
        }

        public void ScheduleSurfaceProbes()
        {
            if (!IsInitialized || !SurfaceProbesEnabled) return;
            DiscardPendingProbes();
            int foot = probeNextFoot;
            probeNextFoot ^= 1;
            ref var f = ref nativeFeet.ElementAt(foot);
            if (f.phase != 0 || footLength <= 0f) return;
            var fwd = ProjectHorizontal(f.plantedBodyFwd);
            if (fwd.sqrMagnitude < 1e-6f) fwd = ProjectHorizontal(cachedPlayerFwd);
            if (fwd.sqrMagnitude < 1e-6f) return;
            fwd.Normalize();
            var right = Vector3.Cross(cachedPlayerUp, fwd);
            if (right.sqrMagnitude < 1e-6f) return;
            right.Normalize();
            probePlan = new IkFootProbePlan
            {
                foot = foot,
                up = cachedPlayerUp,
                fwd = fwd,
                right = right,
                heelD = footLength * heelProbeFrac,
                ballD = footLength * ballProbeFrac,
                toeD = footLength * toeProbeFrac,
                halfW = footLength * footHalfWidthFrac,
                hipsUpComp = Vector3.Dot(HipsWorldPosition(), cachedPlayerUp)
            };
            var c = (Vector3)f.currentPos;
            var lift = cachedPlayerUp * (rayCastRange * 0.5f);
            var down = -cachedPlayerUp;
            for (int i = 0; i < probeResults.Length; i++) probeResults[i] = default;
            Probe(0, c - fwd * probePlan.heelD + lift, down);
            Probe(1, c + fwd * probePlan.ballD + right * probePlan.halfW + lift, down);
            Probe(2, c + fwd * probePlan.ballD - right * probePlan.halfW + lift, down);
            Probe(3, c + fwd * probePlan.toeD + lift, down);
            probePending = true;
        }

        // RaycastCommand against the ground plane (no hipsUp filter here; ResolveProbeHeight applies it).
        void Probe(int slot, Vector3 origin, Vector3 dir)
        {
            if (GroundCast(origin, dir, rayCastRange, 0f, float.PositiveInfinity, out var point, out _,
                    out float distance))
            {
                probeResults[slot * probeMaxHits] = new ProbeHit { point = point, distance = distance };
            }
        }

        void ApplySurfaceProbes(float dt)
        {
            probeElapsedLeft += dt;
            probeElapsedRight += dt;
            ref var leftF = ref nativeFeet.ElementAt(0);
            ref var rightF = ref nativeFeet.ElementAt(1);
            if (leftF.phase != 0) leftF.toeBendDeg = Mathf.MoveTowards(leftF.toeBendDeg, 0f, toeMaxDorsiDeg * dt * 4f);
            if (rightF.phase != 0)
                rightF.toeBendDeg = Mathf.MoveTowards(rightF.toeBendDeg, 0f, toeMaxDorsiDeg * dt * 4f);
            if (!probePending) return;
            probePending = false;
            int foot = probePlan.foot;
            float elapsed = foot == 0 ? probeElapsedLeft : probeElapsedRight;
            if (foot == 0) probeElapsedLeft = 0f;
            else probeElapsedRight = 0f;
            ref var f = ref nativeFeet.ElementAt(foot);
            if (f.phase != 0) return;
            FitFootSurface(ref f, Mathf.Min(elapsed, 0.25f));
        }

        void FitFootSurface(ref IkFootNativeState f, float dt)
        {
            var fwd = probePlan.fwd;
            var right = probePlan.right;
            var up = probePlan.up;
            float heelD = probePlan.heelD;
            float ballD = probePlan.ballD;
            float toeD = probePlan.toeD;
            float halfW = probePlan.halfW;
            bool okHeel = ResolveProbeHeight(0, out float heelH);
            bool okA = ResolveProbeHeight(1, out float ballAH);
            bool okB = ResolveProbeHeight(2, out float ballBH);
            bool okToe = ResolveProbeHeight(3, out float toeH);
            if (okHeel && okA && okB)
            {
                float ballH = (ballAH + ballBH) * 0.5f;
                int side = probePlan.foot;
                float span = Mathf.Max(1e-3f, heelD + ballD);
                float ankleH = Mathf.Lerp(heelH, ballH, heelD / span);
                footGroundUp[side] = footGroundValid[side]
                    ? Mathf.Lerp(footGroundUp[side], ankleH, 1f - Mathf.Exp(-surfaceNormalRate * dt))
                    : ankleH;
                footGroundValid[side] = true;
                var tFwd = fwd * (heelD + ballD) + up * (ballH - heelH);
                var tRight = right * (2f * halfW) + up * (ballAH - ballBH);
                var n = Vector3.Cross(tFwd, tRight);
                if (n.sqrMagnitude > 1e-8f)
                {
                    n.Normalize();
                    if (Vector3.Dot(n, up) < 0f) n = -n;
                    var prev = (Vector3)f.filteredNormal;
                    if (prev.sqrMagnitude < 1e-6f) prev = up;
                    f.filteredNormal = Vector3.Slerp(prev, n, 1f - Mathf.Exp(-surfaceNormalRate * dt)).normalized;
                }

                float expectedToeH = ballH + (ballH - heelH) / span * (toeD - ballD);
                float toeDelta = toeH - expectedToeH;
                bool toeHasSurface = okToe && toeDelta > -footLength * 0.5f;
                if (toeHasSurface)
                {
                    float bend = Mathf.Atan2(toeDelta, Mathf.Max(1e-3f, toeD - ballD)) * Mathf.Rad2Deg;
                    bend = Mathf.Clamp(bend, -toeMaxPlantarDeg, toeMaxDorsiDeg);
                    f.toeBendDeg = Mathf.Lerp(f.toeBendDeg, bend, 1f - Mathf.Exp(-toeBendRate * dt));
                }
                else
                {
                    f.toeBendDeg = Mathf.Lerp(f.toeBendDeg, 0f, 1f - Mathf.Exp(-toeBendRate * dt));
                }

                f.toeBendAxis = right;
            }
            else
            {
                footGroundValid[probePlan.foot] = false;
                f.toeBendDeg = Mathf.Lerp(f.toeBendDeg, 0f, 1f - Mathf.Exp(-toeBendRate * dt));
            }
        }

        bool ResolveProbeHeight(int slot, out float height)
        {
            int baseIndex = slot * probeMaxHits;
            float bestDist = float.MaxValue;
            bool found = false;
            height = 0f;
            for (int Index = 0; Index < probeMaxHits; Index++)
            {
                var h = probeResults[baseIndex + Index];
                if (h.distance <= 0f) continue;
                if (h.distance >= bestDist) continue;
                float upC = Vector3.Dot(h.point, probePlan.up);
                if (upC > probePlan.hipsUpComp) continue;
                bestDist = h.distance;
                height = upC;
                found = true;
            }

            return found;
        }

        Vector3 ProjectHorizontal(Vector3 v)
        {
            return v - cachedPlayerUp * Vector3.Dot(v, cachedPlayerUp);
        }

        float HDist(Vector3 a, Vector3 b)
        {
            return ProjectHorizontal(a - b).magnitude;
        }

        static void EnforceSide(ref Vector3 idealPos, Vector3 center, Vector3 bodyRight, int sideSign, float minDist)
        {
            var toIdeal = idealPos - center;
            float lateral = Vector3.Dot(toIdeal, bodyRight);
            float required = sideSign * minDist;
            if (sideSign > 0 && lateral < required)
            {
                idealPos += bodyRight * (required - lateral);
            }
            else if (sideSign < 0 && lateral > -minDist)
            {
                idealPos -= bodyRight * (lateral + minDist);
            }
        }

        float HeadYaw()
        {
            var hc = rig.Bones.HeadControl;
            var fwd = ProjectHorizontal(hc.OutgoingWorldData.rotation * Vector3.forward);
            if (fwd.sqrMagnitude < 0.001f) return prevHeadYaw;
            return Mathf.Atan2(Vector3.Dot(fwd, cachedPlayerRight), Vector3.Dot(fwd, cachedPlayerFwd)) * Mathf.Rad2Deg;
        }

        void CaptureFootAlignment()
        {
            footAlignLeft = Quaternion.identity;
            footAlignRight = Quaternion.identity;
            // Upstream reads the avatar root and the live foot bones at initialisation; the avatar is in its
            // rest pose then, so the rest pose (root at origin, identity) is used.
            var avatarRot = Quaternion.identity;
            var avatarUp = avatarRot * Vector3.up;
            var restFrame = BuildFootFrame(avatarRot * Vector3.forward, avatarUp, avatarUp);
            var invRest = Quaternion.Inverse(restFrame);
            if (rig.Model.Has(HumanBodyBones.LeftFoot))
                footAlignLeft = invRest * rig.Model.Rot(HumanBodyBones.LeftFoot);
            if (rig.Model.Has(HumanBodyBones.RightFoot))
                footAlignRight = invRest * rig.Model.Rot(HumanBodyBones.RightFoot);
        }

        Quaternion BuildFootFrame(Vector3 bodyFwd, Vector3 normal, Vector3 up)
        {
            if (normal.sqrMagnitude < 0.001f)
            {
                normal = up;
            }

            var fwd = Vector3.ProjectOnPlane(bodyFwd, normal);
            if (fwd.sqrMagnitude < 1e-6f)
            {
                fwd = Vector3.ProjectOnPlane(Vector3.forward, normal);
            }

            fwd.Normalize();
            var surfaceRot = Quaternion.LookRotation(fwd, normal);
            var uprightRot = Quaternion.LookRotation(fwd, up);
            float tiltAngle = Quaternion.Angle(uprightRot, surfaceRot);
            return tiltAngle > 0.01f
                ? Quaternion.Slerp(uprightRot, surfaceRot, Mathf.Clamp01(maxFootTiltDegrees / tiltAngle))
                : uprightRot;
        }

        Quaternion FootRotation(Vector3 bodyFwd, Vector3 normal, Quaternion footAlign)
        {
            if (normal.sqrMagnitude < 0.001f)
            {
                normal = cachedPlayerUp;
            }

            var result = BuildFootFrame(bodyFwd, normal, cachedPlayerUp);
            var footFwd = result * Vector3.forward;
            var footFwdFlat = ProjectHorizontal(footFwd);
            var bodyFwdFlat = ProjectHorizontal(bodyFwd);
            if (footFwdFlat.sqrMagnitude > 1e-6f && bodyFwdFlat.sqrMagnitude > 1e-6f)
            {
                footFwdFlat.Normalize();
                bodyFwdFlat.Normalize();
                float yawAngle = Vector3.SignedAngle(bodyFwdFlat, footFwdFlat, cachedPlayerUp);
                if (Mathf.Abs(yawAngle) > maxFootYawDegrees)
                {
                    float clampedYaw = Mathf.Clamp(yawAngle, -maxFootYawDegrees, maxFootYawDegrees);
                    float correction = clampedYaw - yawAngle;
                    result = Quaternion.AngleAxis(correction, cachedPlayerUp) * result;
                }
            }

            return result * footAlign;
        }

        void FallbackStanceWidth()
        {
            if (!rig.Model.Has(HumanBodyBones.LeftFoot) || !rig.Model.Has(HumanBodyBones.RightFoot))
            {
                CalibratedStanceWidth = 0.2f;
            }
            else
            {
                CalibratedStanceWidth = Mathf.Max(0.04f,
                    ProjectHorizontal(rig.BoneWorldPosition(HumanBodyBones.RightFoot) -
                                      rig.BoneWorldPosition(HumanBodyBones.LeftFoot)).magnitude);
            }
        }

        void FallbackHipToFoot()
        {
            if (rig.Model.Has(HumanBodyBones.Hips) && rig.Model.Has(HumanBodyBones.LeftFoot) &&
                rig.Model.Has(HumanBodyBones.RightFoot))
            {
                float hipsAlongUp = Vector3.Dot(rig.BoneWorldPosition(HumanBodyBones.Hips), cachedPlayerUp);
                float feetAlongUp = (Vector3.Dot(rig.BoneWorldPosition(HumanBodyBones.LeftFoot), cachedPlayerUp) +
                                     Vector3.Dot(rig.BoneWorldPosition(HumanBodyBones.RightFoot), cachedPlayerUp)) *
                                    0.5f;
                CalibratedHipToFoot = Mathf.Max(0.15f, Mathf.Abs(hipsAlongUp - feetAlongUp));
            }
            else
            {
                CalibratedHipToFoot = 0.85f;
            }
        }

        void FallbackLegLens(bool isLeft)
        {
            float total = CalibratedHipToFoot;
            float th = total * 0.55f, sh = total * 0.45f;
            if (isLeft)
            {
                leftThighLen = th;
                leftShinLen = sh;
            }
            else
            {
                rightThighLen = th;
                rightShinLen = sh;
            }
        }

        void InitPose(ref IkFootNativeState f, HumanBodyBones bone)
        {
            if (!rig.Model.Has(bone))
            {
                return;
            }

            var bp = rig.BoneWorldPosition(bone);
            int side = f.sideSign < 0 ? 0 : 1;
            if (GroundCast(bp + cachedPlayerUp * (CalibratedHipToFoot * 0.33f), -cachedPlayerUp, rayCastRange, 0f,
                    Vector3.Dot(rig.BoneWorldPosition(HumanBodyBones.Hips), cachedPlayerUp), out var hitPoint,
                    out var hitNormal, out _))
            {
                f.currentPos = f.plantedPos = f.idealPos = hitPoint + hitNormal * footHeightOffset;
                f.filteredNormal = hitNormal;
                footGroundUp[side] = Vector3.Dot(hitPoint, cachedPlayerUp);
                footGroundValid[side] = true;
            }
            else
            {
                f.currentPos = f.plantedPos = f.idealPos = bp;
                f.filteredNormal = cachedPlayerUp;
                footGroundValid[side] = false;
            }

            var fwd = rig.AvatarRootRotation * Vector3.forward;
            f.currentRot = f.plantedRot = f.stepStartRot =
                FootRotation(fwd, f.filteredNormal, f.sideSign < 0 ? footAlignLeft : footAlignRight);
            f.phase = 0;
        }

        public float ComputeHipBob()
        {
            if (!IsInitialized || !nativeOutput.IsCreated) return 0f;
            return nativeOutput[0].hipBob;
        }

        public Vector3 ComputeHipSway()
        {
            if (!IsInitialized || !nativeOutput.IsCreated) return Vector3.zero;
            return nativeOutput[0].hipSway;
        }

        public Quaternion ComputePelvisDelta()
        {
            if (!IsInitialized || !nativeOutput.IsCreated) return Quaternion.identity;
            return nativeOutput[0].pelvisDelta;
        }

        struct ProbeHit
        {
            public Vector3 point;
            public float distance;
        }

        struct IkFootProbePlan
        {
            public int foot;
            public Vector3 up, fwd, right;
            public float heelD, ballD, toeD, halfW, hipsUpComp;
        }
    }
}
