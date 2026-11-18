using System;
using E = VRMR.Fbik;

namespace VRMR
{
    /// <summary>
    /// IBodySolver backed by the ported full-body IK solver (VRMR.Fbik).
    /// Inputs arrive already centered and scaled to the avatar (TrackingRig), so the solver's own device scaling is pinned
    /// to 1 by stating the avatar's eye height as the user's height in eye-height mode.
    /// </summary>
    public sealed class FbikAdapter : IBodySolver
    {
        E.FbikBodySolver _s;
        AvatarProfile _prof;
        float _scale = 1f;
        readonly E.Vector3[] _pos = new E.Vector3[HumanBones.Count];
        readonly E.Quaternion[] _rot = new E.Quaternion[HumanBones.Count];
        Quat _restHandL = Quat.Identity, _restHandR = Quat.Identity;

        /// <summary>
        /// Orientation of VRChat's /tracking/vrsystem wrist pose when the hand is in a T-pose (palm down, fingers out).
        /// Assumed: +Z along the fingers, +Y out of the back of the hand. Adjustable once verified on real data.
        /// </summary>
        public static bool BodyFit = false;
        public static Quat VrcWristTPoseLeft = Quat.LookRotation(Vec3.Left, Vec3.Up);
        public static Quat VrcWristTPoseRight = Quat.LookRotation(Vec3.Right, Vec3.Up);

        static E.Vector3 V(Vec3 v) => new E.Vector3(v.x, v.y, v.z);
        static E.Quaternion Q(Quat q) => new E.Quaternion(q.x, q.y, q.z, q.w);
        static Vec3 V(E.Vector3 v) => new Vec3(v.x, v.y, v.z);
        static Quat Q(E.Quaternion q) => new Quat(q.x, q.y, q.z, q.w);

        public void Init(AvatarProfile profile, float avatarScale, float realEyeHeight)
        {
            _prof = profile; _scale = avatarScale;
            var rp = new E.Vector3[HumanBones.Count];
            var rr = new E.Quaternion[HumanBones.Count];
            var exists = profile.HumanExists;
            var parent = new int[HumanBones.Count];
            for (int b = 0; b < HumanBones.Count; b++)
            {
                rp[b] = V(profile.HumanRestPos(b) * avatarScale);
                rr[b] = Q(profile.HumanRestRot(b));
                parent[b] = exists[b] ? HumanBones.ResolveParent(b, exists) : -1;
            }
            float eye = profile.EyeHeight * avatarScale;
            _s = new E.FbikBodySolver(rp, rr, exists, parent, eye);
            _s.HeightMode = E.IkSelectedHeightMode.EyeHeight;
            _s.RealEyeHeight = eye; // inputs are pre-scaled to the avatar -> DeviceScale 1
            // The controller -> wrist offsets (the base solver's OpenVR IK offsets and SteamVR's skeletal wrist) are real-world
            // metres. The base solver applies them before its DeviceScale; the positions here are already scaled (DeviceScale 1),
            // so the offsets are scaled by the tracking scale too. Without this the hands sat ~1/scale too far behind
            // the controllers (one test avatar and user: 14.5 instead of 9.1 avatar cm, i.e. 8.6 cm in real terms).
            _deviceScale = ScaleDeviceOffsets && realEyeHeight > 0.1f ? eye / realEyeHeight : 1f;
            var r = _s.Rig;
            r.leftHandToIKPositionOffset = new E.Vector3(0.02f, 0.08f, 0.02f) * _deviceScale;
            r.rightHandToIKPositionOffset = new E.Vector3(-0.02f, 0.08f, 0.02f) * _deviceScale;
            r.OpenXRLeftHandToIKPositionOffset = new E.Vector3(0, 0, -0.05f) * _deviceScale;
            r.OpenXRRightHandToIKPositionOffset = new E.Vector3(0, 0, -0.05f) * _deviceScale;
            _s.IsLocomoting = false;
            _s.UseTrackedFloorEstimate = false; // TrackingRig puts the real floor at y = 0
            _s.Rig.VrmrNoGrounding = true;      // ... and places the calibration head itself (TrackingRig.CalibrationHead)
            _s.Settings.Smoothing = false;      // offline: causal one-euro lag is replaced by zero-phase smoothing in PoseBuilder
            _s.Settings.FBIKBodyFit = BodyFit;  // inputs are already on this avatar's proportions
            _restHandL = profile.HumanRestRot(HumanBones.LeftHand);
            _restHandR = profile.HumanRestRot(HumanBones.RightHand);
            _avatarEye = eye;
        }

        CalLock _lock = CalLock.Head;
        bool _vrcHands = true;
        float _avatarEye = 1f;

        public void Configure(CalibrationSettings cal)
        {
            if (cal == null) return;
            _s.Settings.FBIKShoulderSolveEnabled = cal.ShoulderTracking; // VRChat --disable-shoulder-tracking / the base solver's shoulder solve
            _s.Settings.FootIKEnabled = cal.ProceduralFeet;               // foot IK (procedural steps; tracked feet always follow their trackers)
            _lock = cal.Lock;
            _vrcHands = cal.VrcHands;
        }

        // ------------------------------------------------------------------ VRChat's hand on a Valve Index controller
        // Measured against VRChat's own IK (HUMR) on test takes (one test avatar, Index controllers): the hand bone in
        // the controller's frame, from frames where VRChat's elbow is bent (the hand reached its target). It is the same in
        // every spine and measurement mode (rotation within 0.5 deg, mirror-symmetric). The position follows the AVATAR's
        // size, not the tracking scale: 10.6-10.9 avatar cm at both tracking scale 0.62 (by height) and 0.59 (by arm span).
        // The base solver's offsets (SteamVR skeletal wrist + (+-0.02, 0.08, 0.02) + Euler(80,69,150) / (112,80,198)) put the hand
        // 2.4 cm further forward and 18 deg (left) / 37 deg (right) away from VRChat's.

        /// <summary>Hand bone position in the left Index controller's frame (avatar metres, for an avatar eye height of <see cref="VrcHandRefEye"/>); mirrored in x for the right hand.</summary>
        public static readonly Vec3 VrcIndexHandPosLeft = new Vec3(-0.021f, 0.022f, -0.1032f);
        public const float VrcHandRefEye = 1.0969f;
        /// <summary>Hand bone rotation = controller * this * the avatar's rest (T-pose) hand rotation; left hand, mirrored for the right.</summary>
        public static readonly Quat VrcIndexHandRotLeft = new Quat(-0.22455f, 0.33790f, 0.63999f, 0.65255f).Normalized;
        static Vec3 VrcHandPos(bool left) => left ? VrcIndexHandPosLeft : new Vec3(-VrcIndexHandPosLeft.x, VrcIndexHandPosLeft.y, VrcIndexHandPosLeft.z);
        static Quat VrcHandRot(bool left) => left ? VrcIndexHandRotLeft : new Quat(VrcIndexHandRotLeft.x, -VrcIndexHandRotLeft.y, -VrcIndexHandRotLeft.z, VrcIndexHandRotLeft.w);

        /// <summary>The hand bone pose VRChat gives an Index controller pose (solver space: positions already avatar-scaled).</summary>
        public static Pose VrcIndexHand(bool left, Pose controller, Quat restHand, float avatarEye)
        {
            var q = controller.rot.Normalized;
            var pos = controller.pos + q * (VrcHandPos(left) * (avatarEye / VrcHandRefEye));
            return new Pose(pos, (q * VrcHandRot(left) * restHand).Normalized);
        }

        static E.DeviceRole Map(BodyRole r) => (E.DeviceRole)(int)r; // identical ordering

        public void SetDevice(BodyRole role, Pose pose, bool tracked, HandSourceKind kind)
        {
            var p = pose.pos; var q = pose.rot.Normalized;
            var ck = E.ControllerKind.Unknown;
            if (role == BodyRole.LeftHand || role == BodyRole.RightHand)
            {
                bool left = role == BodyRole.LeftHand;
                switch (kind)
                {
                    case HandSourceKind.VrcWrist:
                        // VRChat's wrist target -> this avatar's hand bone rotation
                        q = (q * (left ? VrcWristTPoseLeft : VrcWristTPoseRight).Conjugate * (left ? _restHandL : _restHandR)).Normalized;
                        ck = E.ControllerKind.Generic;
                        break;
                    case HandSourceKind.Index:
                        if (_vrcHands)
                        {
                            // VRChat's measured hand pose: the hand bone is given directly (the solver's "hand bone" source)
                            var hp = VrcIndexHand(left, new Pose(p, q), left ? _restHandL : _restHandR, _avatarEye);
                            p = hp.pos; q = hp.rot;
                            ck = E.ControllerKind.Generic;
                        }
                        else ck = E.ControllerKind.Index;
                        break;
                    case HandSourceKind.ViveWand: ck = E.ControllerKind.ViveWand; break;
                    case HandSourceKind.OculusTouch: ck = E.ControllerKind.OculusTouch; break;
                    case HandSourceKind.Generic: ck = E.ControllerKind.Generic; break;
                    default: ck = E.ControllerKind.Unknown; break;
                }
            }
            _s.SetDevice(Map(role), V(p), Q(q), tracked, ck);
        }

        /// <summary>Diagnostics only: false reproduces the old unscaled controller offsets (hands too far behind the controllers).</summary>
        public static bool ScaleDeviceOffsets = true;
        /// <summary>Tracking scale (real metres -> avatar metres) applied to the per-device offsets.</summary>
        float _deviceScale = 1f;

        public void SetControllerWrist(bool left, Vec3 rawPos, Quat rawRot) =>
            _s.SetControllerWristFromOpenVR(left, rawPos.x * _deviceScale, rawPos.y * _deviceScale, rawPos.z * _deviceScale, rawRot.x, rawRot.y, rawRot.z, rawRot.w);

        public void Calibrate() => _s.Calibrate();
        /// <summary>
        /// Hip follow for "Lock both": how much of a gap the spine can't close moves the hips (0 = none, all to the head).
        /// VRChat's Lock both (HUMR, two takes) keeps the hips within 1.0-1.2 cm (p95) of the tracker and lets the head
        /// give 2.3-2.6 cm; 0.25 gives 1.0-1.3 cm at the hips (0.5 gave 2.1-2.5 cm).
        /// </summary>
        public static float LockBothHipsFollow = 0.25f;

        public void Solve(float dt)
        {
            // VRChat FBT spine mode for a tracked hip (the base solver itself always behaves like "Lock head")
            E.FbikSolver.VrmrHipsFollowOverride = true;
            E.FbikSolver.VrmrHipsFollow = _lock == CalLock.Head ? 1f : _lock == CalLock.Hips ? 0f : LockBothHipsFollow;
            try { _s.Solve(dt); }
            finally { E.FbikSolver.VrmrHipsFollowOverride = false; }
        }

        public void GetPose(Vec3[] pos, Quat[] rot)
        {
            _s.GetPose(_pos, _rot);
            for (int b = 0; b < HumanBones.Count; b++) { pos[b] = V(_pos[b]); rot[b] = Q(_rot[b]); }
        }
    }
}
