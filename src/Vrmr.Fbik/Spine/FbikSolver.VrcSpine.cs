// VR Motion Recorder addition (not upstream): drives VrcSpine (the VRChat-matched spine) from the
// spine pass for a tracked hip. Called from SolveRalivSpineIK (FbikSolver.Spine.cs); returns false to fall back
// to the base solver's Raliv spine (no hip tracker, a chest tracker, or a skeleton without spine, chest, neck and thighs).
namespace VRMR.Fbik
{
    public partial struct FbikSolver
    {
        /// <summary>Use the VRChat-matched spine for a tracked hip (measured closer to VRChat); false = the base solver's Raliv spine.</summary>
        public static bool VrcSpine = true;
        /// <summary>A chest tracker also uses the VRChat-matched spine, with the tracker as its chest goal (see VrcSpineCore.ChestGoal*); false = the base solver's Raliv spine.</summary>
        public static bool VrcChestGoal = true;

        [System.ThreadStatic] static Vector3[] _vrcSpinePos;
        [System.ThreadStatic] static Quaternion[] _vrcSpineRot;

        bool SolveVrcSpine(Vector3 headTargetPosition, Quaternion headTargetRotation)
        {
            bool chestGoal = plan.chestTracked && plan.chestChain;
            if (!VrcSpine || !plan.hipsTracked || (chestGoal && !VrcChestGoal)) return false;
            if (!poseStream.IsValid(handleHips) || !poseStream.IsValid(handleSpine) || !poseStream.IsValid(handleChest)
                || !poseStream.IsValid(handleNeck) || !poseStream.IsValid(handleHead)
                || !poseStream.IsValid(handleLeftUpperLeg) || !poseStream.IsValid(handleRightUpperLeg)) return false;
            if (_vrcSpinePos == null) { _vrcSpinePos = new Vector3[VrcSpineCore.Count]; _vrcSpineRot = new Quaternion[VrcSpineCore.Count]; }
            var p = _vrcSpinePos; var r = _vrcSpineRot;
            // read pose: the chain was reset to rest and the hips placed on their target (SolveSpinePass)
            poseStream.GetPositionAndRotation(handleHips, out p[0], out r[0]);
            poseStream.GetPositionAndRotation(handleSpine, out p[1], out r[1]);
            poseStream.GetPositionAndRotation(handleChest, out p[2], out r[2]);
            poseStream.GetPositionAndRotation(handleNeck, out p[3], out r[3]);
            poseStream.GetPositionAndRotation(handleHead, out p[4], out r[4]);

            // the avatar frame of the hip target: hip rotation * inverse(rest hip rotation relative to the avatar root)
            Quaternion restHips = Quaternion.identity;
            for (int k = handleHips.Index; k >= 0; k = poseStream.Parent[k]) restHips = (Quaternion)poseStream.RestLocalRotation[k] * restHips;
            Quaternion anchor = r[0] * Quaternion.Inverse(restHips);
            // head height: the rest head's height above the avatar root
            float headHeight = standingHeadHeight > 1e-4f ? standingHeadHeight : Vector3.Dot(p[4] - p[0], playerUp) + Vector3.Dot(p[0] - poseStream.GetPosition(handleLeftFoot), playerUp);

            var input = new VrcSpineInput
            {
                HeadPos = headTargetPosition, HeadRot = headTargetRotation,
                PelvisPos = p[0], PelvisRot = r[0], Anchor = anchor, Up = playerUp,
                Hands = plan.leftArm.solve && plan.rightArm.solve,
                LeftHand = targetPositionLeftHand, RightHand = targetPositionRightHand,
                HeadHeight = headHeight,
                HeadFollow = TrackedHipsFollow,
                HasChestGoal = chestGoal,
                ChestGoalPos = targetPositionChest,
                ChestGoalRot = targetRotationChest * offsetRotationChest,
            };
            VrcSpineCore.Solve(in input, p, r);

            poseStream.SetPosition(handleHips, p[0]);
            poseStream.SetRotation(handleHips, r[0]);
            poseStream.SetRotation(handleSpine, r[1]);
            poseStream.SetRotation(handleChest, r[2]);   // an UpperChest keeps its rest rotation relative to the chest (the VRChat-matched spine has none)
            poseStream.SetRotation(handleNeck, r[3]);
            poseStream.SetRotation(handleHead, r[4]);
            return true;
        }
    }
}
