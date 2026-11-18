// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// VR Motion Recorder facade over the ported full-body IK solver and its setup path.
//
// Spaces: Unity convention (left-handed, +Y up, +Z forward, metres).
//  * Device poses passed to SetDevice are TRACKING-SPACE poses (raw SteamVR room-scale poses converted to Unity,
//    floor at y = 0, real-world metres). The solver scales them by DeviceScale (avatar size / player size) and lifts them
//    by its grounding offset exactly like an upstream local player.
//  * SetPlayspace places the tracking space in the world (the upstream IkLocalPlayer transform). Default identity.
//  * GetPose returns WORLD-space bone poses of the avatar (avatar-sized: scaled tracking space -> playspace).
using System;

namespace VRMR.Fbik
{
    public enum DeviceRole { Head, LeftHand, RightHand, Hips, Chest, LeftFoot, RightFoot, LeftKnee, RightKnee, LeftElbow, RightElbow }

    /// <summary>
    /// How a hand device pose is interpreted. The base solver has ONE OpenVR controller model (same IK offsets for Index,
    /// Vive wand and Touch); the per-controller difference comes from SteamVR's skeletal wrist bone, which can be
    /// supplied with <see cref="FbikBodySolver.SetControllerWrist"/>. Generic = the pose is the avatar hand bone
    /// itself (e.g. VRChat's wrist pose). OpenXRPalm = an OpenXR palm (XR_EXT_palm_pose) pose.
    /// </summary>
    public enum ControllerKind { Unknown, Index, ViveWand, OculusTouch, Generic, OpenXRPalm }

    public sealed class FbikBodySolver
    {
        public const int BoneCount = (int)HumanBodyBones.LastBone; // 55 (0..54, UpperChest = 54)
        readonly FbikRig rig;
        float realEyeHeight;
        float lastDt = 1f / 90f;

        /// <param name="restPos">World rest position per HumanBodyBones (0..54), avatar root at origin facing +Z, T-pose.</param>
        /// <param name="restRot">World rest rotation per HumanBodyBones.</param>
        /// <param name="exists">false for bones the avatar lacks.</param>
        /// <param name="parent">HumanBodyBones index of the nearest existing humanoid ancestor (-1 for Hips).</param>
        /// <param name="avatarEyeHeight">Authored avatar eye height above the avatar root (upstream AvatarEyePosition.y).</param>
        public FbikBodySolver(Vector3[] restPos, Quaternion[] restRot, bool[] exists, int[] parent, float avatarEyeHeight)
            : this(restPos, restRot, exists, parent, avatarEyeHeight, 0f, null) { }

        /// <param name="soleHeight">Height of the shoe soles above the avatar root in T-pose (upstream measures it from the foot meshes; usually ~0).</param>
        /// <param name="settings">Solver settings (defaults = IkSettingsDefaults).</param>
        public FbikBodySolver(Vector3[] restPos, Quaternion[] restRot, bool[] exists, int[] parent, float avatarEyeHeight, float soleHeight, FbikSettings settings)
        {
            IkAvatarModel model = new IkAvatarModel(restPos, restRot, exists, parent, avatarEyeHeight, soleHeight);
            rig = new FbikRig(model, settings ?? new FbikSettings());
        }

        /// <summary>Advanced access to the ported objects (rig driver, bone controls, solver job).</summary>
        public FbikRig Rig => rig;
        public FbikSettings Settings => rig.Settings;
        /// <summary>Device scale: tracking-space metres -> avatar metres.</summary>
        public float DeviceScale => rig.DeviceScale;
        /// <summary>Grounding offset (unscaled metres added to every device's height).</summary>
        public float GroundingOffset => rig.HeightModeGroundingOffset;
        /// <summary>Avatar eye height used for scaling (authored eye height minus sole height).</summary>
        public float AvatarEyeHeight => rig.Model.AvatarEyeHeight;
        /// <summary>Player eye height measured from the HMD at the last calibration.</summary>
        public float MeasuredEyeHeight => rig.PlayerEyeHeight;

        /// <summary>
        /// The user's real eye height (metres, floor to eyes). Acts like the upstream "stated height": when &gt; 0 it is used
        /// for scaling (SizingEyeHeight) instead of the HMD height. The solver still measures the HMD at calibration for the
        /// grounding offset; until a measurement exists this value is used as the measured eye height too.
        /// Setting it re-runs ApplyScaleAndHeight.
        /// </summary>
        public float RealEyeHeight
        {
            get => realEyeHeight;
            set
            {
                realEyeHeight = value > 0f && !float.IsNaN(value) && !float.IsInfinity(value) ? value : 0f;
                rig.StatedEyeHeight = realEyeHeight;
                if (realEyeHeight > 0f && !rig.HasGenuinePlayerEyeHeight)
                {
                    rig.PlayerEyeHeight = realEyeHeight;
                }
                rig.PlayerArmSpan = rig.EstimatedPlayerArmSpan();
                rig.ApplyScaleAndHeight();
            }
        }
        /// <summary>Height mode (Auto = eye height with hips/leg trackers, arm span otherwise).</summary>
        public IkSelectedHeightMode HeightMode
        {
            get => rig.Settings.HeightMode;
            set { rig.Settings.HeightMode = value; rig.ApplyScaleAndHeight(); }
        }
        /// <summary>HMD origin -> centre eye, head-local tracking-space metres (SteamVR: average of the two eye-to-head positions).</summary>
        public Vector3 HmdCenterEyeOffset { get => rig.HmdCenterEyeOffset; set => rig.HmdCenterEyeOffset = value; }
        /// <summary>Upstream LocalCharacterDriver.IsLocomoting. The base solver disables procedural foot IK while true (it expects a walk animation), so leave false for recordings.</summary>
        public bool IsLocomoting { get => rig.IsLocomoting; set => rig.IsLocomoting = value; }
        /// <summary>VRMR port addition: upstream estimates the floor from the lowest tracker (minus 7 cm) at calibration; turn off when tracking-space y = 0 is the true floor.</summary>
        public bool UseTrackedFloorEstimate { get => rig.UseTrackedFloorEstimate; set => rig.UseTrackedFloorEstimate = value; }
        /// <summary>Use a flat ground plane for foot planting (default true, through the play-space floor).</summary>
        public bool HasGround { get => rig.HasGround; set => rig.HasGround = value; }
        /// <summary>Overrides the ground plane height (world Y). NaN restores the play-space floor.</summary>
        public void SetGroundHeight(float worldY)
        {
            if (float.IsNaN(worldY)) { rig.UseCustomGround = false; rig.SetPlayspace(rig.PlayspacePosition, rig.PlayspaceRotation); return; }
            rig.UseCustomGround = true;
            rig.GroundPoint = new Vector3(rig.PlayspacePosition.x, worldY, rig.PlayspacePosition.z);
        }
        /// <summary>Places the tracking space in the world (the upstream player root). Rotation should be yaw-only.</summary>
        public void SetPlayspace(Vector3 position, Quaternion rotation)
        {
            rig.SetPlayspace(position, rotation);
        }
        /// <summary>
        /// SteamVR skeletal-input wrist bone of a controller (bone index 1 of skeletonAction.bonePositions/boneRotations,
        /// as the SteamVR Unity plugin reports it). The base solver subtracts it from the raw controller pose before its IK offsets.
        /// Until supplied the upstream "skeleton not sampled yet" fallback (zero / identity) is used.
        /// </summary>
        public void SetControllerWrist(bool left, Vector3 wristLocalPosition, Quaternion wristLocalRotation)
        {
            if (left) { rig.LeftWristLocalPosition = wristLocalPosition; rig.LeftWristLocalRotation = wristLocalRotation; }
            else { rig.RightWristLocalPosition = wristLocalPosition; rig.RightWristLocalRotation = wristLocalRotation; }
        }
        /// <summary>
        /// Same as SetControllerWrist but takes the raw OpenVR value of IVRInput::GetSkeletalBoneData(skeleton action,
        /// VRSkeletalTransformSpace_Parent, VRSkeletalMotionRange_WithController) for bone 1 (wrist), and converts it the
        /// way SteamVR_Action_Skeleton does: position (-x, y, z), rotation (x, -y, -z, w).
        /// </summary>
        public void SetControllerWristFromOpenVR(bool left, float px, float py, float pz, float qx, float qy, float qz, float qw)
        {
            SetControllerWrist(left, new Vector3(-px, py, pz), new Quaternion(qx, -qy, -qz, qw));
        }
        /// <summary>Optional per-device user offset (upstream IkDeviceOffsets), device-local, unscaled metres.</summary>
        public void SetDeviceOffset(DeviceRole role, Vector3 position, Quaternion rotation) => rig.SetDeviceOffset(Map(role), position, rotation);
        /// <summary>Tracking hardware class per device (drives the "Auto" smoothing preset). Default Lighthouse.</summary>
        public void SetDeviceHardware(DeviceRole role, IkTrackingHardware hardware) => rig.RoleHardware[(int)Map(role)] = hardware;

        static IkBoneTrackedRole Map(DeviceRole role)
        {
            switch (role)
            {
                case DeviceRole.Head: return IkBoneTrackedRole.CenterEye;
                case DeviceRole.LeftHand: return IkBoneTrackedRole.LeftHand;
                case DeviceRole.RightHand: return IkBoneTrackedRole.RightHand;
                case DeviceRole.Hips: return IkBoneTrackedRole.Hips;
                case DeviceRole.Chest: return IkBoneTrackedRole.Chest;
                case DeviceRole.LeftFoot: return IkBoneTrackedRole.LeftFoot;
                case DeviceRole.RightFoot: return IkBoneTrackedRole.RightFoot;
                case DeviceRole.LeftKnee: return IkBoneTrackedRole.LeftLowerLeg;
                case DeviceRole.RightKnee: return IkBoneTrackedRole.RightLowerLeg;
                case DeviceRole.LeftElbow: return IkBoneTrackedRole.LeftLowerArm;
                case DeviceRole.RightElbow: return IkBoneTrackedRole.RightLowerArm;
                default: throw new ArgumentOutOfRangeException(nameof(role));
            }
        }
        static IkHandSource HandSource(ControllerKind kind)
        {
            switch (kind)
            {
                case ControllerKind.Generic: return IkHandSource.HandBone;
                case ControllerKind.OpenXRPalm: return IkHandSource.OpenXRPalm;
                default: return IkHandSource.OpenVRController;
            }
        }

        /// <summary>
        /// Sets a device's tracking-space pose. Head = HMD pose. Hands = controller pose (see ControllerKind).
        /// Body trackers only drive the body after <see cref="Calibrate"/>. tracked=false keeps the last pose
        /// (the solver keeps a device's last pose while its pose is invalid); use RemoveDevice when it is gone.
        /// </summary>
        public void SetDevice(DeviceRole role, Vector3 pos, Quaternion rot, bool tracked, ControllerKind kind = ControllerKind.Unknown)
        {
            bool valid = tracked && IsFinite(pos) && IsFinite(rot) && (rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w) > 1e-6f;
            rig.SetDevicePose(Map(role), pos, rot, valid, HandSource(kind));
        }
        /// <summary>Unassigns a device (upstream: device disconnected / UnAssignTracker). Body trackers lose their calibration.</summary>
        public void RemoveDevice(DeviceRole role) => rig.RemoveDevice(Map(role));
        /// <summary>Is the role currently driving the body (hands/HMD: has a pose; trackers: calibrated and present).</summary>
        public bool IsDriving(DeviceRole role) => rig.Bones.Find(Map(role)).HasTracked;

        /// <summary>
        /// Full-body calibration from the current device poses: measures eye height (unless RealEyeHeight is set),
        /// resolves scale, then binds every present body tracker to its bone assuming the user stands in the
        /// calibration pose (upright, facing the HMD's yaw, T-pose or arms down; trackers on their body parts).
        /// </summary>
        public void Calibrate()
        {
            rig.FullBodyCalibration(lastDt);
        }

        /// <summary>One frame: devices -> virtual spine -> bone controls -> IK targets -> IK solve.</summary>
        public void Solve(float deltaTime)
        {
            if (!(deltaTime > 0f) || float.IsInfinity(deltaTime))
            {
                deltaTime = lastDt;
            }
            lastDt = deltaTime;
            rig.Simulate(deltaTime);
        }

        /// <summary>World pose per HumanBodyBones (arrays of length &gt;= 55). Missing bones get zero / identity.</summary>
        public void GetPose(Vector3[] worldPos, Quaternion[] worldRot)
        {
            IkPoseSkeleton sk = rig.Model.Skeleton;
            sk.Stream.InvalidateWorldCache();
            int n = Math.Min(BoneCount, Math.Min(worldPos != null ? worldPos.Length : BoneCount, worldRot != null ? worldRot.Length : BoneCount));
            for (int b = 0; b < n; b++)
            {
                int node = rig.Model.NodeOfBone[b];
                Vector3 p = Vector3.zero;
                Quaternion r = Quaternion.identity;
                if (node >= 0)
                {
                    sk.Stream.GetWorldPositionAndRotation(node, out p, out r);
                }
                if (worldPos != null) worldPos[b] = p;
                if (worldRot != null) worldRot[b] = r;
            }
        }
        /// <summary>The IK target computed for a hand this frame (world; position + bone rotation).</summary>
        public void GetHandTarget(bool left, out Vector3 position, out Quaternion boneRotation)
        {
            ref FbikSolver j = ref rig.Rig.IKJob;
            position = left ? j.targetPositionLeftHand : j.targetPositionRightHand;
            boneRotation = left ? j.targetRotationLeftHand * j.offsetRotationLeftHand : j.targetRotationRightHand * j.offsetRotationRightHand;
        }
        /// <summary>The head target (world position, bone rotation) computed this frame.</summary>
        public void GetHeadTarget(out Vector3 position, out Quaternion boneRotation)
        {
            ref FbikSolver j = ref rig.Rig.IKJob;
            position = j.targetPositionHead;
            boneRotation = j.targetRotationHead * j.offsetRotationHead;
        }
        /// <summary>Foot planting state of the procedural foot simulation (world).</summary>
        public void GetFootState(bool left, out Vector3 plantedPosition, out bool planted)
        {
            plantedPosition = left ? rig.Feet.LeftPlantedPos : rig.Feet.RightPlantedPos;
            planted = left ? rig.Feet.LeftIsPlanted : rig.Feet.RightIsPlanted;
        }
        static bool IsFinite(Vector3 v) => !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
        static bool IsFinite(Quaternion q) => !(float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z) || float.IsNaN(q.w) || float.IsInfinity(q.x) || float.IsInfinity(q.y) || float.IsInfinity(q.z) || float.IsInfinity(q.w));
    }
}
