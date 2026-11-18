// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Tracker calibration (IkHintBiasStore, IkLimbRollStore,
//           TryGetFittedTposePositions, TryGetCalibrationReference, ReprojectTrackerOffsetsForCurrentAvatar,
//           RecomputeFbikRotationCalibration, ApplyCalibrationToCurrentAvatar, CaptureCalibrationReference,
//           EffectorReference, PureBind/OffsetFromReference, ComputeHints, CaptureLimbRoll, ComputeHintBiasLocal)
//           and the tracker-to-bone offsets (CalculateOffset + calibration snapshot).
// Dropped: the constellation classifier that guesses which tracker is which role (the app names the role),
//          tracker pairing (merging two trackers into one virtual tracker), UI/debug recorders.
using System.Collections.Generic;

namespace VRMR.Fbik
{
    public sealed class FbikCalibration
    {
        readonly FbikRig rig;
        readonly Dictionary<IkBoneTrackedRole, Vector3> hintBiasLocal = new Dictionary<IkBoneTrackedRole, Vector3>();
        public readonly Dictionary<IkBoneTrackedRole, Quaternion> TrackerToBone = new Dictionary<IkBoneTrackedRole, Quaternion>();
        public readonly Dictionary<IkBoneTrackedRole, Quaternion> TrackerToBody = new Dictionary<IkBoneTrackedRole, Quaternion>();
        public bool HasCalibrationReference;
        Quaternion s_refHead = Quaternion.identity, s_refHips = Quaternion.identity, s_refChest = Quaternion.identity, s_refLeftFoot = Quaternion.identity, s_refRightFoot = Quaternion.identity;
        Quaternion s_refLeftToe = Quaternion.identity, s_refRightToe = Quaternion.identity, s_refLeftShoulder = Quaternion.identity, s_refRightShoulder = Quaternion.identity;
        struct OffsetSnapshot
        {
            public bool Valid;
            public Vector3 TrackerUnscaledPos, HeadUnscaledPos;
            public Quaternion TrackerUnscaledRot, HeadUnscaledRot;
        }
        readonly OffsetSnapshot[] snapshots = new OffsetSnapshot[IkBoneDriver.RoleCount];
        public FbikCalibration(FbikRig rig) { this.rig = rig; }

        // ---- IkHintBiasStore / IkLimbRollStore -------------------------------------------------------
        float SizeRatio()
        {
            float ratio = rig.Model.AvatarEyeHeight * rig.AppliedUpScale / IkHeightConstants.FallbackHeightInMeters;
            return float.IsNaN(ratio) || float.IsInfinity(ratio) || ratio <= 1e-4f ? 1f : ratio;
        }
        public void SetHintBias(IkBoneTrackedRole role, Vector3 localOffset) => hintBiasLocal[role] = localOffset / SizeRatio();
        public bool TryGetHintBias(IkBoneTrackedRole role, out Vector3 localOffset)
        {
            if (hintBiasLocal.TryGetValue(role, out localOffset))
            {
                localOffset *= SizeRatio();
                return true;
            }
            return false;
        }
        public bool TryGetLimbRoll(IkBoneTrackedRole role, out Quaternion trackerToBone) => TrackerToBone.TryGetValue(role, out trackerToBone);
        public void ClearStores()
        {
            hintBiasLocal.Clear();
            TrackerToBone.Clear();
            TrackerToBody.Clear();
        }
        public void ClearSnapshot(IkBoneTrackedRole role) => snapshots[(int)role] = default;

        // ---- references -------------------------------------------------------------------------------------
        public bool TryGetFittedTposePositions(IkBoneTrackedRole role, out Vector3 headTpose, out Vector3 boneTpose)
        {
            headTpose = Vector3.zero;
            boneTpose = Vector3.zero;
            if (!IkBoneTrackedRoleCommonCheck.TryConvertToHumanoidRole(role, out HumanBodyBones bone) || !rig.Model.Has(bone))
            {
                return false;
            }
            return rig.Rig.TryGetFittedTposeLocalScaled(HumanBodyBones.Head, out headTpose) && rig.Rig.TryGetFittedTposeLocalScaled(bone, out boneTpose);
        }
        public bool TryGetCalibrationReference(IkBoneTrackedRole role, out Vector3 localPosition, out Quaternion localRotation)
        {
            localPosition = Vector3.zero;
            localRotation = Quaternion.identity;
            IkBoneControl headControl = rig.Bones.HeadControl;
            var headWorld = headControl.OutgoingWorldData;
            Vector3 headTpose, boneTpose;
            if (!TryGetFittedTposePositions(role, out headTpose, out boneTpose))
            {
                if (!rig.Model.TposeBoneSnapshot.TryGetValue(role, out var bind))
                {
                    return false;
                }
                float avatarScale = FbikSolverSetup.AppliedAvatarScale();
                headTpose = headControl.TposeLocalScaled.position;
                boneTpose = bind.position * avatarScale;
            }
            IkCalibrationMath.ComputeTposeAnchor(headWorld.position, headWorld.rotation, headTpose, out Vector3 anchorPos, out Quaternion anchorRot);
            Matrix4x4 playerToWorld = rig.localToWorldMatrix;
            localPosition = playerToWorld.inverse.MultiplyPoint3x4(anchorPos + anchorRot * boneTpose);
            localRotation = Quaternion.Inverse(playerToWorld.rotation) * anchorRot;
            return true;
        }

        // ---- IkInput.CalculateOffset -----------------------------------------------------------------------
        /// <summary>Binds a full-body tracker to its control: inverse offset so the bone lands on the head-anchored T-pose reference.</summary>
        public void CalculateOffset(IkBoneControl control, IkBoneTrackedRole role, Vector3 trackerPosition, Quaternion trackerRotation, Vector3 unscaledPos, Quaternion unscaledRot)
        {
            IkCalibratedCoords bone = control.OutGoingData;
            Vector3 referencePosition = bone.position;
            Quaternion referenceRotation = bone.rotation;
            if (TryGetCalibrationReference(role, out Vector3 tposeLocalPosition, out Quaternion tposeLocalRotation))
            {
                referencePosition = tposeLocalPosition;
                referenceRotation = tposeLocalRotation;
            }
            IkCalibrationMath.ComputeInverseOffset(trackerPosition, trackerRotation, referencePosition, referenceRotation, out Vector3 InverseOffsetPosition, out Quaternion InverseOffsetRotation);
            control.SetInverseOffset(InverseOffsetPosition, InverseOffsetRotation);
            control.UseInverseOffset = true;
            IkCalibratedCoords anchorHeadOut = rig.Bones.HeadControl.OutGoingData;
            OffsetSnapshot s;
            s.TrackerUnscaledPos = unscaledPos;
            s.TrackerUnscaledRot = unscaledRot;
            IkCalibrationMath.UnscaleDeviceCoord(anchorHeadOut.position, anchorHeadOut.rotation, rig.DeviceScale, Vector3.zero, Quaternion.identity, out s.HeadUnscaledPos, out s.HeadUnscaledRot);
            s.Valid = true;
            snapshots[(int)role] = s;
        }
        public void ReprojectTrackerOffsetsForCurrentAvatar()
        {
            Vector3 headTpose = rig.Bones.HeadControl.TposeLocalScaled.position;
            float avatarScale = FbikSolverSetup.AppliedAvatarScale();
            for (int r = 0; r < snapshots.Length; r++)
            {
                if (!snapshots[r].Valid) continue;
                IkBoneTrackedRole role = (IkBoneTrackedRole)r;
                IkBoneControl control = rig.Bones.Controls[r];
                if (!IkBoneTrackedRoleCommonCheck.CheckItsFBTracker(role) || !control.UseInverseOffset) continue;
                Vector3 boneTpose = rig.Model.TposeBoneSnapshot.TryGetValue(role, out var bind) ? bind.position * avatarScale : control.TposeLocalScaled.position;
                Vector3 roleHeadTpose = headTpose;
                if (TryGetFittedTposePositions(role, out Vector3 fittedHeadTpose, out Vector3 fittedBoneTpose))
                {
                    roleHeadTpose = fittedHeadTpose;
                    boneTpose = fittedBoneTpose;
                }
                OffsetSnapshot s = snapshots[r];
                IkCalibrationMath.ReprojectInverseOffsetPosition(s.TrackerUnscaledPos, s.TrackerUnscaledRot, s.HeadUnscaledPos, s.HeadUnscaledRot, rig.DeviceScale, Vector3.zero, Quaternion.identity, roleHeadTpose, boneTpose, out Vector3 inverseOffsetPosition);
                control.SetInverseOffset(inverseOffsetPosition, control.InverseOffsetFromBone.rotation);
            }
        }

        // ---- rotation calibration ---------------------------------------------------------------------------
        public void RecomputeFbikRotationCalibration()
        {
            IkRigDriver r = rig.Rig;
            if (r == null || !r.IKDataReady) return;
            IkBoneControl headControl = rig.Bones.HeadControl;
            var headWorld = headControl.OutgoingWorldData;
            IkCalibrationMath.ComputeTposeAnchor(headWorld.position, headWorld.rotation, headControl.TposeLocalScaled.position, out _, out Quaternion rootRot);
            IkBoneDriver b = rig.Bones;
            s_refHead = Quaternion.identity;
            s_refHips = CaptureCalibrationReference(b.HipsControl, rootRot);
            s_refChest = CaptureCalibrationReference(b.ChestControl, rootRot);
            s_refLeftFoot = CaptureCalibrationReference(b.LeftFootControl, rootRot);
            s_refRightFoot = CaptureCalibrationReference(b.RightFootControl, rootRot);
            s_refLeftToe = CaptureCalibrationReference(b.LeftToeControl, rootRot);
            s_refRightToe = CaptureCalibrationReference(b.RightToeControl, rootRot);
            s_refLeftShoulder = CaptureCalibrationReference(b.LeftShoulderControl, rootRot);
            s_refRightShoulder = CaptureCalibrationReference(b.RightShoulderControl, rootRot);
            HasCalibrationReference = true;
            ApplyCalibrationToCurrentAvatar();
        }
        public void ApplyCalibrationToCurrentAvatar()
        {
            if (!HasCalibrationReference) return;
            IkRigDriver r = rig.Rig;
            if (r == null || !r.IKDataReady) return;
            ref FbikSolver data = ref r.IKJob;
            r.RecalibratedHead = OffsetFromReference(s_refHead, IkBoneTrackedRole.Head, data.offsetRotationHead);
            r.RecalibratedHips = OffsetFromReference(s_refHips, IkBoneTrackedRole.Hips, data.offsetRotationHips);
            r.RecalibratedChest = OffsetFromReference(s_refChest, IkBoneTrackedRole.Chest, data.offsetRotationChest);
            r.RecalibratedLeftFoot = OffsetFromReference(s_refLeftFoot, IkBoneTrackedRole.LeftFoot, data.offsetRotationLeftFoot);
            r.RecalibratedRightFoot = OffsetFromReference(s_refRightFoot, IkBoneTrackedRole.RightFoot, data.offsetRotationRightFoot);
            r.RecalibratedLeftToe = OffsetFromReference(s_refLeftToe, IkBoneTrackedRole.LeftToes, data.offsetRotationLeftToe);
            r.RecalibratedRightToe = OffsetFromReference(s_refRightToe, IkBoneTrackedRole.RightToes, data.offsetRotationRightToe);
            r.RecalibratedLeftShoulder = OffsetFromReference(s_refLeftShoulder, IkBoneTrackedRole.LeftShoulder, data.offsetRotationLeftShoulder);
            r.RecalibratedRightShoulder = OffsetFromReference(s_refRightShoulder, IkBoneTrackedRole.RightShoulder, data.offsetRotationRightShoulder);
            r.HasRecalibratedRotationOffsets = true;
            List<KeyValuePair<IkBoneTrackedRole, Quaternion>> pairs = new List<KeyValuePair<IkBoneTrackedRole, Quaternion>>(TrackerToBody);
            foreach (KeyValuePair<IkBoneTrackedRole, Quaternion> pair in pairs)
            {
                TrackerToBone[pair.Key] = pair.Value * IkAnimationRiggingHelper.PureBind(rig.Model, pair.Key);
            }
        }
        static Quaternion CaptureCalibrationReference(IkBoneControl control, Quaternion animatorRootRot)
        {
            return control != null ? EffectorReference(control.HasTracked, control.OutgoingWorldData.rotation, animatorRootRot) : Quaternion.identity;
        }
        public static Quaternion EffectorReference(bool tracked, Quaternion controlWorldRotation, Quaternion anchorRotation)
        {
            return tracked ? Quaternion.Inverse(controlWorldRotation) * anchorRotation : Quaternion.identity;
        }
        Quaternion OffsetFromReference(Quaternion reference, IkBoneTrackedRole role, Quaternion current)
        {
            if (rig.Model.TposeBoneSnapshot.TryGetValue(role, out var bind))
            {
                return reference * bind.rotation;
            }
            return current;
        }

        // ---- hints -------------------------------------------------------------------------------------------
        public void ComputeHints()
        {
            Quaternion hipsRefRot = Quaternion.identity;
            IkBoneControl headAnchorControl = rig.Bones.HeadControl;
            var headWorld = headAnchorControl.OutgoingWorldData;
            IkCalibrationMath.ComputeTposeAnchor(headWorld.position, headWorld.rotation, headAnchorControl.TposeLocalScaled.position, out _, out Quaternion anchorRot);
            if (rig.Model.TposeBoneSnapshot.TryGetValue(IkBoneTrackedRole.Hips, out var hipsBind))
            {
                hipsRefRot = anchorRot * hipsBind.rotation;
            }
            float renderedEyeHeight = rig.Model.AvatarEyeHeight * rig.AppliedUpScale;
            float hs = (float.IsNaN(renderedEyeHeight) || float.IsInfinity(renderedEyeHeight) || renderedEyeHeight <= 0f) ? 1f : renderedEyeHeight / IkHeightConstants.FallbackHeightInMeters;
            float kneePush = 0.10f * hs;
            float maxPush = 0.25f * hs;
            {
                var lll = rig.Bones.LeftLowerLegControl;
                float fwdWeight = 1;
                if (lll.HasTracked) fwdWeight = 0.55f;
                Quaternion trackerRot = lll.OutgoingWorldData.rotation;
                Vector3 localOffset = ComputeHintBiasLocal(trackerRot, hipsRefRot, isLeft: true, distanceMeters: kneePush, outWeight: 0, upWeight: 0.25f, fwdWeight);
                localOffset = Vector3.ClampMagnitude(localOffset, maxPush);
                SetHintBias(IkBoneTrackedRole.LeftLowerLeg, localOffset);
                if (lll.HasTracked) CaptureLimbRoll(IkBoneTrackedRole.LeftLowerLeg, trackerRot, anchorRot);
                var rll = rig.Bones.RightLowerLegControl;
                fwdWeight = 1;
                if (rll.HasTracked) fwdWeight = 0.55f;
                trackerRot = rll.OutgoingWorldData.rotation;
                localOffset = ComputeHintBiasLocal(trackerRot, hipsRefRot, isLeft: false, distanceMeters: kneePush, outWeight: 0, upWeight: 0.25f, fwdWeight);
                localOffset = Vector3.ClampMagnitude(localOffset, maxPush);
                SetHintBias(IkBoneTrackedRole.RightLowerLeg, localOffset);
                if (rll.HasTracked) CaptureLimbRoll(IkBoneTrackedRole.RightLowerLeg, trackerRot, anchorRot);
            }
            {
                var lla = rig.Bones.LeftLowerArmControl;
                if (lla.HasTracked) CaptureLimbRoll(IkBoneTrackedRole.LeftLowerArm, lla.OutgoingWorldData.rotation, anchorRot);
                var rla = rig.Bones.RightLowerArmControl;
                if (rla.HasTracked) CaptureLimbRoll(IkBoneTrackedRole.RightLowerArm, rla.OutgoingWorldData.rotation, anchorRot);
            }
        }
        /// <summary>
        /// Upstream reads the avatar's live bone rotation (the avatar stands in the calibration pose). The port has
        /// no live avatar, so the bone is taken as its rest pose placed on the head-derived calibration anchor.
        /// </summary>
        void CaptureLimbRoll(IkBoneTrackedRole role, Quaternion trackerWorldRot, Quaternion anchorRot)
        {
            if (!IkBoneTrackedRoleCommonCheck.TryConvertToHumanoidRole(role, out HumanBodyBones b) || !rig.Model.Has(b)) return;
            Quaternion boneRotation = anchorRot * rig.Model.Rot(b); // anchorRot is already a world rotation (from the head's world pose)
            Quaternion trackerToBone = Quaternion.Inverse(trackerWorldRot) * boneRotation;
            TrackerToBone[role] = trackerToBone;
            TrackerToBody[role] = trackerToBone * Quaternion.Inverse(IkAnimationRiggingHelper.PureBind(rig.Model, role));
        }
        static Vector3 ComputeHintBiasLocal(Quaternion trackerWorldRot, Quaternion referenceWorldRot, bool isLeft, float distanceMeters, float outWeight = 0.85f, float upWeight = 0.35f, float fwdWeight = 0.00f)
        {
            Vector3 up = referenceWorldRot * Vector3.up;
            Vector3 outDir = referenceWorldRot * (isLeft ? Vector3.left : Vector3.right);
            Vector3 fwd = referenceWorldRot * Vector3.forward;
            Vector3 worldDir = (outDir * outWeight + up * upWeight + fwd * fwdWeight);
            if (worldDir.sqrMagnitude < 1e-8f) worldDir = up;
            worldDir.Normalize();
            Vector3 localDir = Quaternion.Inverse(trackerWorldRot) * worldDir;
            if (localDir.sqrMagnitude < 1e-8f) localDir = Vector3.up;
            return localDir.normalized * distanceMeters;
        }
    }
}
