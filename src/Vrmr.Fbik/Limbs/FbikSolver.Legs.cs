// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Plain C#: game-engine, Burst and job-system dependencies are replaced by the VRMR.Fbik shim.
using System.Runtime.CompilerServices;
namespace VRMR.Fbik
{
    public partial struct FbikSolver
    {
        void SolveLegPass()
        {
            SolveLeg(0);
            SolveLeg(1);
        }
        void SolveToePass()
        {
            if (plan.leftToeDriven) poseStream.SetRotation(handleLeftToe, leftDrivenTargetRot * offsetRotationLeftToe);
            else if (plan.leftToeSurface) ApplyToeSurfaceBend(handleLeftToe, leftToeBendDeg, leftToeBendAxis);

            if (plan.rightToeDriven) poseStream.SetRotation(handleRightToe, rightDrivenTargetRot * offsetRotationRightToe);
            else if (plan.rightToeSurface) ApplyToeSurfaceBend(handleRightToe, rightToeBendDeg, rightToeBendAxis);
        }
        IkSwivelFrame BuildLegFrame()
        {
            if (!plan.hasLegFrame)
            {
                return default;
            }

            return IkSwivelHintCore.BuildFrame( poseStream.GetPosition(handleLeftUpperLeg), poseStream.GetPosition(handleRightUpperLeg), poseStream.GetPosition(handleHips), poseStream.GetPosition(plan.legFrameTo));
        }
        // Hips facing used to place our own knee hint. Falls back to the hips bone rotation if the leg frame can't be built.
        IkSwivelFrame BuildLegBodyFrame()
        {
            IkSwivelFrame f = BuildLegFrame();
            if (f.Valid || !plan.hasHips)
            {
                return f;
            }

            Quaternion hipsRot = poseStream.GetRotation(handleHips) * Quaternion.Inverse(offsetRotationHips);
            f.Up = hipsRot * Vector3.up;
            f.Right = hipsRot * Vector3.right;
            f.Forward = hipsRot * Vector3.forward;
            f.Valid = true;
            return f;
        }
        public void SolveLeg(int legSlot)
        {
            bool isLeft = legSlot == 0;
            FbikLegPlan leg = isLeft ? plan.leftLeg : plan.rightLeg;
            if (!leg.solve)
            {
                return;
            }

            float posWeight = leg.weight;
            IkBoneHandle root = isLeft ? handleLeftUpperLeg : handleRightUpperLeg;
            IkBoneHandle mid = isLeft ? handleLeftLowerLeg : handleRightLowerLeg;
            IkBoneHandle tip = isLeft ? handleLeftFoot : handleRightFoot;
            Vector3 hint = isLeft ? hintPositionLeftLowerLeg : hintPositionRightLowerLeg;
            Quaternion hintRot = isLeft ? hintRotationLeftLowerLeg : hintRotationRightLowerLeg;
            Quaternion targetOffset = isLeft ? offsetRotationLeftFoot : offsetRotationRightFoot;
            Vector3 bendNormal = isLeft ? kneeBendPrefLeft : kneeBendPrefRight;
            bool hintIsTracker = leg.kneeTracked;
            Quaternion origRootRot = poseStream.GetRotation(root), origMidRot = poseStream.GetRotation(mid);
            Quaternion origTipRot = poseStream.GetRotation(tip);
            ResetToRest(root, mid, tip);
            Quaternion tRot = isLeft ? targetRotationLeftLowerLeg : targetRotationRightLowerLeg;
            bool preserveTip = leg.preserveTip;
            if (preserveTip) tRot = origTipRot;

            Vector3 targetPos = isLeft ? targetPositionLeftLowerLeg : targetPositionRightLowerLeg;

            // If no knee tracker, then we build our own knee hint from the hips frame
            IkSwivelFrame bodyFrame = hintIsTracker ? default : BuildLegBodyFrame();

            IkLegSolveInput input = default;
            poseStream.GetPositionAndRotation(root, out Vector3 rootPos, out Quaternion rootRot);
            poseStream.GetPositionAndRotation(mid, out Vector3 midPos, out Quaternion midRot);
            input.Root = rootPos;
            input.Mid = midPos;
            input.Tip = poseStream.GetPosition(tip);
            input.RootRotation = rootRot;
            input.MidRotation = midRot;
            input.TargetPosition = targetPos;
            input.TargetRotation = tRot;
            input.HintPosition = hint;
            input.HintWeight = hintIsTracker ? 1f : 0f;
            input.TargetOffset = targetOffset;
            input.BendNormal = bendNormal;
            input.AnteriorNormal = kneeAnteriorRef;
            input.HintRotation = hintRot;
            input.HasHintRotation = leg.hintRoll;
            input.HintIsTracker = hintIsTracker;
            input.HasBodyFrame = bodyFrame.Valid;
            input.HipsForward = bodyFrame.Forward;
            input.FootForward = (isLeft ? targetRotationLeftLowerLeg : targetRotationRightLowerLeg) * Vector3.forward; // Foot target's control rotation, before any preserveTip swap
            input.Up = playerUp;
            // VRMR: right axes for the VRChat-style knee plane (hips control rotation, foot target control rotation)
            input.FootRight = (isLeft ? targetRotationLeftLowerLeg : targetRotationRightLowerLeg) * Vector3.right;
            input.HipsRight = plan.hasHips ? (poseStream.GetRotation(handleHips) * Quaternion.Inverse(offsetRotationHips)) * Vector3.right : Vector3.zero;

            IkLegSolveCore.Solve(input, out IkLegSolveResult result);

            if (plan.hasLegDiagnostics)
            {
                ref IkLegDiagnostics d = ref Ref(legDiagnostics, legSlot);
                d.ReachRatio = result.ReachRatio;
                d.KneeAngleDeg = result.KneeAngleDeg;
                d.AxisSource = result.AxisSource;
                d.HintApplied = result.HintApplied ? 1f : 0f;
                d.ShinRollDeg = result.ShinRollDeg;
            }

            Quaternion restTipRot = poseStream.GetRotation(tip); // rest leg under the hips (ResetToRest above)
            poseStream.SetRotation(mid, result.MidDelta * poseStream.GetRotation(mid));
            poseStream.SetRotation(root, result.RootDelta * poseStream.GetRotation(root));
            poseStream.SetRotation(root, result.HintDelta * poseStream.GetRotation(root));
            poseStream.SetRotation(mid, result.MidPostRoll * poseStream.GetRotation(mid));
            poseStream.SetRotation(tip, result.TipRotation);
            Quaternion shinRoll = result.MidPostRoll;
            if (VrmrLegTwist && hintIsTracker && !preserveTip) ApplyVrmrLegTwist(root, mid, tip, rootRot, midRot, restTipRot);

            if (posWeight < 1f)
            {
                Quaternion solvedRootRot = poseStream.GetRotation(root), solvedMidRot = poseStream.GetRotation(mid), solvedTipRot = poseStream.GetRotation(tip);
                poseStream.SetRotation(root, Quaternion.Slerp(origRootRot, solvedRootRot, posWeight));
                poseStream.SetRotation(mid, Quaternion.Slerp(origMidRot, solvedMidRot, posWeight));
                poseStream.SetRotation(tip, Quaternion.Slerp(origTipRot, solvedTipRot, posWeight));
            }
            if (preserveTip)
            {
                Quaternion carriedTip = shinRoll * origTipRot;
                poseStream.SetRotation(tip, posWeight < 1f ? Quaternion.Slerp(origTipRot, carriedTip, posWeight) : carriedTip);
            }

            RecordHipDiagnostics(root, mid, legSlot);

            // Store gizmo data, same as the arm state does.
            if (plan.hasLegState)
            {
                ref IkLegSlotState state = ref Ref(legState, legSlot);
                Vector3 hipPos = poseStream.GetPosition(root), axis = poseStream.GetPosition(tip) - hipPos, kneeDir = poseStream.GetPosition(mid) - hipPos;
                if (axis.sqrMagnitude > sqrEpsilon) kneeDir = Vector3.ProjectOnPlane(kneeDir, axis.normalized);
                state.KneeDir = kneeDir.sqrMagnitude > sqrEpsilon ? kneeDir.normalized : Vector3.zero;
                state.PoleDir = result.PoleDir;
                state.ConstrainedHintPosition = result.HintPosition;
            }
        }
        // ---- VRMR addition (not upstream): VRChat's leg twist
        /// <summary>
        /// Re-twists the solved thigh and shin about their own axes (the knee and foot stay where the solve put them) the way
        /// VRChat's IK does, measured on its own pose (HUMR, an 11-point test take, 5 windows, both legs): the thigh takes
        /// <see cref="VrmrThighTwistShare"/> and the shin <see cref="VrmrShinTwistShare"/> of the foot's twist against the
        /// rest leg under the hips. A thigh twist taken fully from the foot gives 14-27 deg on VRChat's
        /// own bones, the plain swing from the rest leg 6-15 deg (thigh) / 13-29 deg (shin); the shares give 4-13 / 4-12 deg.
        /// The base solver rolled the shin by the knee tracker (VRChat's shin is not bound to it: 6-36 deg median drift).
        /// Only for legs with a knee tracker: without one, the leg solve as it was already matches VRChat's twist (shin 2-5 deg
        /// on the 3-tracker HUMR takes) and the shares made it worse (shin 3-13 deg, p95 up to 37 deg).
        /// </summary>
        public static bool VrmrLegTwist = true;
        public static float VrmrThighTwistShare = 0.2f, VrmrShinTwistShare = 0.5f;

        void ApplyVrmrLegTwist(IkBoneHandle root, IkBoneHandle mid, IkBoneHandle tip, Quaternion restRoot, Quaternion restMid, Quaternion restTip)
        {
            Vector3 hip = poseStream.GetPosition(root), knee = poseStream.GetPosition(mid), foot = poseStream.GetPosition(tip);
            Quaternion footRot = poseStream.GetRotation(tip);
            // bone axes in local space, from the solved pose (rotation-independent: the bone points at its child)
            Vector3 tLocal = Quaternion.Inverse(poseStream.GetRotation(root)) * (knee - hip), sLocal = Quaternion.Inverse(poseStream.GetRotation(mid)) * (foot - knee);
            if (tLocal.sqrMagnitude < sqrEpsilon || sLocal.sqrMagnitude < sqrEpsilon) return;
            Quaternion footDelta = footRot * Quaternion.Inverse(restTip);           // foot now vs the rest leg under the hips
            Quaternion thigh = Quaternion.Slerp(restRoot, footDelta * restRoot, VrmrThighTwistShare);
            thigh = IkQuaternionExt.FromToRotation(thigh * tLocal, knee - hip) * thigh;
            Quaternion shinFromThigh = thigh * (Quaternion.Inverse(restRoot) * restMid);
            Quaternion shin = Quaternion.Slerp(shinFromThigh, footDelta * restMid, VrmrShinTwistShare);
            shin = IkQuaternionExt.FromToRotation(shin * sLocal, foot - knee) * shin;
            poseStream.SetRotation(root, thigh);
            poseStream.SetRotation(mid, shin);
            poseStream.SetRotation(tip, footRot);
        }
        void RecordHipDiagnostics(IkBoneHandle root, IkBoneHandle mid, int slot)
        {
            if (!plan.hasLegDiagnostics || !plan.hasHips)
            {
                return;
            }

            Vector3 femur = poseStream.GetPosition(mid) - poseStream.GetPosition(root);
            if (!(femur.sqrMagnitude > 1e-8f))
            {
                return;
            }

            Quaternion hipsRot = poseStream.GetRotation(handleHips) * Quaternion.Inverse(offsetRotationHips), hipsInv = Quaternion.Inverse(hipsRot);
            Vector3 femurLocal = (hipsInv * femur).normalized;

            ref IkLegDiagnostics d = ref Ref(legDiagnostics, slot);
            d.HipFlexionDeg = Mathf.Atan2(femurLocal.z, -femurLocal.y) * Mathf.Rad2Deg;
            d.HipAbductionDeg = Mathf.Atan2(femurLocal.x, -femurLocal.y) * Mathf.Rad2Deg;
            d.FemurTwistDeg = TwistDeg(hipsInv * poseStream.GetRotation(root), femurLocal);
        }
        public void ApplyToeSurfaceBend(IkBoneHandle handle, float bendDeg, Vector3 axis)
        {
            Quaternion current = poseStream.GetRotation(handle);
            poseStream.SetRotation(handle, Quaternion.AngleAxis(-bendDeg, axis.normalized) * current);
        }
    }
}
