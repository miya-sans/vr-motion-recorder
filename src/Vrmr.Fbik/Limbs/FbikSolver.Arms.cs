// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Plain C#: game-engine, Burst and job-system dependencies are replaced by the VRMR.Fbik shim.

namespace VRMR.Fbik
{
    public partial struct FbikSolver
    {
        public const int armLeft = 0, armRight = 1, armCount = 2;

        // Twist bone strength: 1 = each twist bone takes a share equal to its position along the bone (even roll gradient).
        const float armTwistBoneStrength = 1f;

        IkSwivelFrame BuildArmFrame()
        {
            if (plan.hasBodyRight && plan.torsoTo.IsBound)
            {
                var from = plan.hasHips ? handleHips : plan.torsoFrom;
                var f = IkSwivelHintCore.BuildFrame(poseStream.GetPosition(handleLeftUpperArm),
                    poseStream.GetPosition(handleRightUpperArm), poseStream.GetPosition(from),
                    poseStream.GetPosition(plan.torsoTo));
                if (f.Valid) return f;
            }

            IkSwivelFrame fallback = default;
            var right = targetRotationHead * Vector3.right;
            right -= playerUp * Vector3.Dot(right, playerUp);
            if (right.sqrMagnitude < sqrEpsilon) return fallback;
            fallback.Up = playerUp;
            fallback.Right = right.normalized;
            fallback.Forward = Vector3.Cross(fallback.Right, fallback.Up);
            fallback.Valid = true;
            return fallback;
        }

        void SolveShoulderPass()
        {
            if (!plan.hasLeftShoulder && !plan.hasRightShoulder)
            {
                return;
            }

            var frame = BuildArmFrame();
            SolveShoulder(armLeft, frame);
            SolveShoulder(armRight, frame);
        }

        void SolveShoulder(int slot, in IkSwivelFrame frame)
        {
            bool isLeft = slot == armLeft;
            if (!(isLeft ? plan.hasLeftShoulder : plan.hasRightShoulder))
            {
                return;
            }

            var mode = isLeft ? plan.leftShoulder : plan.rightShoulder;
            var arm = isLeft ? plan.leftArm : plan.rightArm;
            IkBoneHandle shoulder = isLeft ? handleLeftShoulder : handleRightShoulder,
                upperArm = isLeft ? handleLeftUpperArm : handleRightUpperArm;
            IkArmState scratch = default;
            ref var state = ref plan.hasArmState ? ref Ref(armState, slot) : ref scratch;
            bool tracked = mode == FbikShoulderMode.Tracker, rhythm = shoulderSolveEnabled && frame.Valid && arm.solve;
            float weight = tracked ? isLeft ? plan.leftShoulderWeight : plan.rightShoulderWeight : 0f;
            float blend = plan.hasArmState
                ? IkShoulderBlendCore.Step(state.ShoulderBlend, weight, poseStream.deltaTime, shoulderTrackerBlendTime)
                : weight;
            state.ShoulderBlend = blend;
            if (!tracked && blend <= 0f)
            {
                state.ShoulderHeld = false;
                if (!rhythm)
                {
                    return;
                }
            }

            Quaternion origRot = poseStream.GetRotation(shoulder), fallback = origRot;
            if (rhythm && SolveShoulderRhythm(isLeft, shoulder, upperArm, frame, out var solved))
            {
                fallback = arm.weight < 1f ? Quaternion.Slerp(origRot, solved, arm.weight) : solved;
            }

            poseStream.GetParentWorld(shoulder.Index, out _, out var parentWorld, out _);
            Quaternion parentRot = parentWorld;
            if (tracked)
            {
                state.ShoulderHold = Quaternion.Inverse(parentRot) *
                                     IkQuaternionExt.NormalizeSafe(
                                         (isLeft ? targetRotationLeftShoulder : targetRotationRightShoulder) *
                                         (isLeft ? offsetRotationLeftShoulder : offsetRotationRightShoulder));
                state.ShoulderHeld = true;
            }

            poseStream.SetRotation(shoulder,
                state.ShoulderHeld
                    ? IkShoulderBlendCore.Blend(fallback, parentRot * state.ShoulderHold, blend)
                    : fallback);
        }

        bool SolveShoulderRhythm(bool isLeft, IkBoneHandle shoulder, IkBoneHandle upperArm, in IkSwivelFrame frame,
            out Quaternion solved)
        {
            poseStream.ResetToRest(shoulder);
            IkShoulderSolveInput input = default;
            input.ShoulderPos = poseStream.GetPosition(shoulder);
            input.UpperArmPos = poseStream.GetPosition(upperArm);
            input.HandTargetPos = isLeft ? targetPositionLeftHand : targetPositionRightHand;
            input.ArmLength = isLeft
                ? tposeShoulderToHandLeft - tposeClavicleLenLeft
                : tposeShoulderToHandRight - tposeClavicleLenRight;
            input.TorsoUp = frame.Up;
            input.TorsoForward = frame.Forward;
            input.TorsoOut = isLeft ? -frame.Right : frame.Right;
            input.ElevationFactor = shoulderElevationFactor;
            input.ProtractionFactor = shoulderProtractionFactor;
            input.HasElbow = (isLeft ? plan.leftArm : plan.rightArm).trackerHint;
            input.ElbowPos = isLeft ? hintPositionLeftHand : hintPositionRightHand;
            input.UpperArmLength =
                isLeft
                    ? tposeShoulderToElbowLeft - tposeClavicleLenLeft
                    : tposeShoulderToElbowRight - tposeClavicleLenRight;
            var handRot = isLeft
                ? targetRotationLeftHand * offsetRotationLeftHand
                : targetRotationRightHand * offsetRotationRightHand;
            input.HandBack = handRot * (isLeft ? tposeLeftHandBackLocal : tposeRightHandBackLocal);
            IkShoulderSolveCore.Solve(input, out var result);
            solved = result.Delta * poseStream.GetRotation(shoulder);
            return result.Apply;
        }

        void SolveArmPass()
        {
            var frame = BuildArmFrame();
            SolveArm(armLeft, frame);
            SolveArm(armRight, frame);
        }

        public void SolveHand(bool isLeft)
        {
            SolveArm(isLeft ? armLeft : armRight, BuildArmFrame());
        }

        public void SolveArm(int slot, in IkSwivelFrame frame)
        {
            bool isLeft = slot == armLeft;
            var arm = isLeft ? plan.leftArm : plan.rightArm;
            if (!arm.solve || !frame.Valid)
            {
                return;
            }

            if (SolveVrcArm(slot, arm))
                return; // VRMR: the VRChat-style arm without elbow/shoulder trackers (FbikSolver.VrcArm.cs)
            IkBoneHandle root = isLeft ? handleLeftUpperArm : handleRightUpperArm,
                mid = isLeft ? handleLeftLowerArm : handleRightLowerArm,
                tip = isLeft ? handleLeftHand : handleRightHand;
            ResetToRest(root, mid, tip);
            poseStream.GetPositionAndRotation(handleChest, out var chest, out _);
            poseStream.GetPositionAndRotation(root, out var shoulder, out var restRootRot);
            poseStream.GetPositionAndRotation(mid, out var elbow, out var restMidRot);
            poseStream.GetPositionAndRotation(tip, out var hand, out var restTipRot);
            IkArmSolveInput input = default;
            input.Chest = chest;
            input.Shoulder = shoulder;
            input.RestElbow = elbow;
            input.RestHand = hand;
            input.RestHandRotation = restTipRot;
            input.RestMidRotation = restMidRot;
            input.TargetPosition = isLeft ? targetPositionLeftHand : targetPositionRightHand;
            input.TargetRotation = (isLeft ? targetRotationLeftHand : targetRotationRightHand) *
                                   (isLeft ? offsetRotationLeftHand : offsetRotationRightHand);
            input.HasHead = plan.hasHead;
            input.HeadPosition = plan.hasHead ? poseStream.GetPosition(handleHead) : Vector3.zero;
            input.TorsoUp = frame.Up;
            input.TorsoForward = frame.Forward;
            input.TorsoOut = isLeft ? -frame.Right : frame.Right;
            input.IsLeft = isLeft;
            input.HasTrackerHint = arm.trackerHint;
            input.HintPosition = isLeft ? hintPositionLeftHand : hintPositionRightHand;
            input.HasHintRotation = arm.hintRoll;
            input.HintRotation = isLeft ? hintRotationLeftHand : hintRotationRightHand;
            input.TorsoCapsule = arm.elbowProtect;
            if (arm.elbowProtect)
            {
                input.TorsoA = poseStream.GetPosition(plan.hasHips ? handleHips : handleChest);
                input.TorsoB = poseStream.GetPosition(handleNeck);
                input.TorsoRadius = chestRadius + collisionSkin;
            }

            input.Dt = poseStream.deltaTime;
            input.SmoothTime = armSwivelSmoothTime;
            input.MaxRateDeg = armSwivelMaxRateDeg;
            IkArmState scratch = default;
            ref var state = ref plan.hasArmState ? ref Ref(armState, slot) : ref scratch;
            IkArmSolveCore.Solve(input, ref state, out var result);
            if (!result.Valid)
            {
                return;
            }

            IkArmSolveCore.Pose(input, result, restRootRot, restMidRot, out var upperRot, out var lowerRot,
                out float forearmRollDeg);
            // A tracked hand is a hard endpoint. The tracking weight gates whether
            // this pass runs; once active, blending these rotations would pull a
            // reachable hand away from its controller before the final override.
            poseStream.SetRotation(root, upperRot);
            poseStream.SetRotation(mid, lowerRot);
            if (arm.upperTwist)
                ApplyArmTwist(isLeft ? handleLeftUpperArmTwist : handleRightUpperArmTwist, root, mid,
                    armTwistBoneStrength, isLeft ? tposeLeftUpperArmChildBind : tposeRightUpperArmChildBind,
                    isLeft ? tposeLeftUpperArmTwistBind : tposeRightUpperArmTwistBind);
            ApplyForearmRoll(mid, tip, forearmRollDeg);
            poseStream.SetRotation(tip, input.TargetRotation);
            if (arm.lowerTwist)
                ApplyArmTwist(isLeft ? handleLeftLowerArmTwist : handleRightLowerArmTwist, mid, tip,
                    armTwistBoneStrength, isLeft ? tposeLeftLowerArmChildBind : tposeRightLowerArmChildBind,
                    isLeft ? tposeLeftLowerArmTwistBind : tposeRightLowerArmTwistBind);
        }

        void ApplyArmTwist(IkBoneHandle twist, IkBoneHandle parent, IkBoneHandle child, float fraction,
            Quaternion childBind, Quaternion twistBind)
        {
            poseStream.GetPositionAndRotation(parent, out var parentPos, out var parentRot);
            poseStream.GetPositionAndRotation(child, out var childPos, out var childRot);
            float share = IkTwistSolveCore.SegmentPositionFraction(parentPos, childPos, poseStream.GetPosition(twist)) *
                          fraction;
            if (IkTwistSolveCore.Solve(parentRot, childRot, childPos - parentPos, share, childBind, twistBind,
                    out var twistWorld, out _, out _))
            {
                poseStream.SetRotation(twist, twistWorld);
            }
        }

        void ApplyForearmRoll(IkBoneHandle mid, IkBoneHandle tip, float rollDeg)
        {
            if (rollDeg < 0.001f && rollDeg > -0.001f)
            {
                return;
            }

            var axis = poseStream.GetPosition(tip) - poseStream.GetPosition(mid);
            if (axis.sqrMagnitude < sqrEpsilon)
            {
                return;
            }

            poseStream.SetRotation(mid, Quaternion.AngleAxis(rollDeg, axis.normalized) * poseStream.GetRotation(mid));
        }
    }
}
