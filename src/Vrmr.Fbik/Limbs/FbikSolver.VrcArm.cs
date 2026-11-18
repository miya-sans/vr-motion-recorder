// VR Motion Recorder addition (not upstream): drives VrcArmCore (the VRChat-style arm) from the
// arm pass. Called from SolveArm (FbikSolver.Arms.cs); returns false to fall back to the base solver's arm solve (an elbow
// tracker, a shoulder tracker, or a skeleton without chest / upper arm / forearm / hand).
namespace VRMR.Fbik
{
    public partial struct FbikSolver
    {
        /// <summary>Use the VRChat-style arm (shoulder + elbow) for arms without elbow/shoulder trackers (measured closer to VRChat); false = the base solver's arm.</summary>
        public static bool VrcArm = true;
        /// <summary>Diagnostics: false keeps the base solver's shoulder (clavicle) pass and uses the VRChat-style arm only for the elbow.</summary>
        public static bool VrcShoulder = true;
        /// <summary>
        /// Arms with an elbow tracker also use the VRChat-style arm, with the tracker's bound elbow point as its bend goal at
        /// <see cref="VrmrElbowGoalWeight"/> (VRChat's elbow is not bound to its tracker: 3-9 cm p95 drift on HUMR); false = the base solver's arm.
        /// </summary>
        public static bool VrcElbowGoal = true;
        public static float VrmrElbowGoalWeight = 1f;

        [System.ThreadStatic] static Vector3[] _vrcArmPos;
        [System.ThreadStatic] static Quaternion[] _vrcArmRot;

        bool SolveVrcArm(int slot, in FbikArmPlan arm)
        {
            if (!VrcArm || (arm.trackerHint && !VrcElbowGoal)) return false;
            bool isLeft = slot == armLeft;
            if ((isLeft ? plan.leftShoulder : plan.rightShoulder) == FbikShoulderMode.Tracker) return false;
            IkBoneHandle shoulder = isLeft ? handleLeftShoulder : handleRightShoulder;
            IkBoneHandle upper = isLeft ? handleLeftUpperArm : handleRightUpperArm, lower = isLeft ? handleLeftLowerArm : handleRightLowerArm, hand = isLeft ? handleLeftHand : handleRightHand;
            if (!poseStream.IsValid(handleChest) || !poseStream.IsValid(upper) || !poseStream.IsValid(lower) || !poseStream.IsValid(hand)) return false;
            bool hasShoulder = (isLeft ? plan.hasLeftShoulder : plan.hasRightShoulder) && poseStream.IsValid(shoulder);
            if (_vrcArmPos == null) { _vrcArmPos = new Vector3[VrcArmCore.Count]; _vrcArmRot = new Quaternion[VrcArmCore.Count]; }
            var p = _vrcArmPos; var r = _vrcArmRot;

            // read pose: the rest arm under the solved chest
            bool matchedShoulder = VrcShoulder;
            if (hasShoulder && matchedShoulder) poseStream.ResetToRest(shoulder);
            ResetToRest(upper, lower, hand);
            if (hasShoulder) poseStream.GetPositionAndRotation(shoulder, out p[0], out r[0]);
            poseStream.GetPositionAndRotation(upper, out p[1], out r[1]);
            poseStream.GetPositionAndRotation(lower, out p[2], out r[2]);
            poseStream.GetPositionAndRotation(hand, out p[3], out r[3]);
            if (!hasShoulder) { p[0] = p[1]; r[0] = r[1]; }

            // the avatar frame carried by the chest: chest rotation * inverse(rest chest rotation relative to the avatar root)
            Quaternion restChest = Quaternion.identity;
            for (int k = handleChest.Index; k >= 0; k = poseStream.Parent[k]) restChest = (Quaternion)poseStream.RestLocalRotation[k] * restChest;
            Quaternion chestFrame = poseStream.GetRotation(handleChest) * Quaternion.Inverse(restChest);

            // guessed hand axes, from the read (rest) arm: wrist->palm and, from the hand's closest child, palm->thumb
            Vector3 closest = p[3]; float best = float.MaxValue;
            for (int k = 0; k < poseStream.Count; k++)
            {
                if (poseStream.Parent[k] != hand.Index) continue;
                Vector3 c = poseStream.GetWorldPosition(k);
                float d = (c - p[3]).sqrMagnitude;
                if (d < best && d > 1e-12f) { best = d; closest = c; }
            }
            var input = new VrcArmInput
            {
                IsLeft = isLeft,
                HandPos = isLeft ? targetPositionLeftHand : targetPositionRightHand,
                HandRot = (isLeft ? targetRotationLeftHand : targetRotationRightHand) * (isLeft ? offsetRotationLeftHand : offsetRotationRightHand),
                ChestFrame = chestFrame,
                HasShoulder = hasShoulder,
                ShoulderWeight = shoulderSolveEnabled && matchedShoulder ? 1f : 0f,
                HasBendGoal = arm.trackerHint,
                BendGoal = isLeft ? hintPositionLeftHand : hintPositionRightHand,
                BendGoalWeight = VrmrElbowGoalWeight,
            };
            input.WristToPalm = VrcArmCore.WristToPalmAxis(r[3], p[3], p[2]);
            input.PalmToThumb = best < float.MaxValue ? VrcArmCore.PalmToThumbAxis(r[3], p[3], p[2], closest) : Vector3.zero;
            input.UpperArmBendAxis = VrcArmCore.UpperArmBendAxis(r[1], p[1], p[2], chestFrame * Vector3.forward);

            VrcArmCore.Solve(in input, p, r);

            if (hasShoulder && matchedShoulder) poseStream.SetRotation(shoulder, r[0]);
            poseStream.SetRotation(upper, r[1]);
            poseStream.SetRotation(lower, r[2]);
            if (arm.upperTwist) ApplyArmTwist(isLeft ? handleLeftUpperArmTwist : handleRightUpperArmTwist, upper, lower, armTwistBoneStrength, isLeft ? tposeLeftUpperArmChildBind : tposeRightUpperArmChildBind, isLeft ? tposeLeftUpperArmTwistBind : tposeRightUpperArmTwistBind);
            poseStream.SetRotation(hand, input.HandRot);
            if (arm.lowerTwist) ApplyArmTwist(isLeft ? handleLeftLowerArmTwist : handleRightLowerArmTwist, lower, hand, armTwistBoneStrength, isLeft ? tposeLeftLowerArmChildBind : tposeRightLowerArmChildBind, isLeft ? tposeLeftLowerArmTwistBind : tposeRightLowerArmTwistBind);
            return true;
        }
    }
}
