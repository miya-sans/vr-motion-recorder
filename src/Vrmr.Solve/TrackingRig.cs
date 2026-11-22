using System;
using System.Collections.Generic;
using System.Linq;

namespace VRMR
{
    /// <summary>
    ///     Turns raw recorded device streams into solver inputs in one consistent, centered, avatar-scaled space:
    ///     * VRChat's own head/wrist poses (exactly what VRChat feeds its IK) when available,
    ///     * SteamVR trackers mapped into VRChat's space by fusing the SteamVR HMD with VRChat's head (this also
    ///     captures thumbstick locomotion and world turning), or plain SteamVR playspace when VRChat poses are absent.
    ///     The world is centered on the user at Edits.OriginTime and stays fixed for the clip.
    ///     Calibration works like VRChat's full-body calibration: one calibration moment (a detected T-pose, or the
    ///     time picked with "Calib.") sets the user's size (eye height or arm span -> tracking scale) and binds the body
    ///     trackers. The headset and controllers are never calibrated. Everything is computed from that one moment, so
    ///     the scale, the floor and the tracker offsets agree with each other.
    /// </summary>
    /// <summary>
    ///     An explicit calibration for one stretch of a take (multi-calibration solves): a moment of the take or a
    ///     snapshot.
    /// </summary>
    public sealed class CalibrationSource
    {
        public CalibrationSnapshot Snapshot;
        public string Text = "";
        public double Time = -1;
    }

    public sealed class TrackingRig
    {
        /// <summary>Controller pose origin to wrist joint, per side (real metres): used for the arm-span measurement.</summary>
        const float WristInset = 0.07f;

        /// <summary>
        ///     VRChat places the head bone 1.94 avatar cm further behind the headset than the avatar's own view position
        ///     implies (HUMR, Lock head take: head bone (0, -4.1, -4.0) cm in the headset frame vs the test avatar's own (0,
        ///     -4.07, -2.06);
        ///     VRChat logs it as "eyeToNeck:(0.00, 0.04, 0.04)"). The headset pose is moved back by that much before it is fed
        ///     as the eye. Avatar metres for an avatar eye height of <see cref="FbikAdapter.VrcHandRefEye" />.
        /// </summary>
        public static float VrcEyeBehindHeadset = 0.0194f;

        // ------------------------------------------------------------------ trackers

        static readonly Dictionary<string, BodyRole> SteamRoles = new(StringComparer.OrdinalIgnoreCase)
        {
            ["waist"] = BodyRole.Hips, ["hip"] = BodyRole.Hips, ["chest"] = BodyRole.Chest,
            ["left_foot"] = BodyRole.LeftFoot, ["right_foot"] = BodyRole.RightFoot,
            ["left_knee"] = BodyRole.LeftKnee, ["right_knee"] = BodyRole.RightKnee,
            ["left_elbow"] = BodyRole.LeftElbow, ["right_elbow"] = BodyRole.RightElbow
        };

        // ------------------------------------------------------------------ solver feeding

        static readonly BodyRole[] TrackerRoles =
        {
            BodyRole.Hips, BodyRole.Chest, BodyRole.LeftFoot, BodyRole.RightFoot, BodyRole.LeftKnee, BodyRole.RightKnee,
            BodyRole.LeftElbow, BodyRole.RightElbow
        };

        // ------------------------------------------------------------------ fixed controller -> wrist offsets

        /// <summary>
        ///     SteamVR skeletal wrist (bone 1, parent space, "with controller", raw OpenVR) of a Valve Index controller.
        ///     Measured on a test take: it varies by at most 5.5 mm / 2.2° over the whole take,
        ///     i.e. it is a fixed per-device offset. Used when a take has no skeletal data (e.g. recorded before the
        ///     SteamVR manifest fix) or for gaps, mirrored for the right hand.
        /// </summary>
        public static readonly Vec3 IndexWristLeft = new(-0.0417f, 0.0398f, 0.1598f);

        public static readonly Quat IndexWristRotLeft = new Quat(-0.0841f, -0.9173f, 0.3819f, -0.0754f).Normalized;

        /// <summary>Diagnostics only: false reproduces the old per-frame skeletal wrist (zero when SteamVR input paused).</summary>
        public static bool FixedWrists = true;

        /// <summary>
        ///     Instant playspace jumps (reset / re-center): undone for the solver, put back on the output (
        ///     <see cref="UniverseAt" />).
        /// </summary>
        public readonly PlayspaceJumps Jumps;

        public readonly List<string> Notes = new();

        /// <summary>Repaired tracking losses of the trackers this solve uses (for the notes and the timeline).</summary>
        public readonly List<TrackerGap> RepairedGaps = new();

        /// <summary>How the tracker roles were found (for the solve notes).</summary>
        public readonly List<string> RoleNotes = new();

        /// <summary>The calibration snapshot in use (library / VRChat calibration), null = a moment of this take.</summary>
        public readonly CalibrationSnapshot Snapshot;

        /// <summary>Where each hand's controller -> wrist offset came from (for the solve notes).</summary>
        public readonly string[] WristSource = { "", "" };

        readonly CalibrationSettings _cal;

        // ------------------------------------------------------------------ tracking-loss repair

        readonly List<TrackerGap> _gaps;
        readonly PoseStream _hmd, _ctrlL, _ctrlR, _vHead, _vL, _vR;
        readonly HandSourceKind _kindL, _kindR;
        readonly bool _locomotion;
        readonly AvatarProfile _prof;
        readonly Recording _rec;
        readonly Dictionary<PoseStream, BodyRole> _roleOfStream;
        readonly Dictionary<BodyRole, PoseStream> _trackers = new();
        readonly bool _vrcSpace;
        readonly Vec3[] _wristPos = new Vec3[2];
        readonly Quat[] _wristRot = { Quat.Identity, Quat.Identity };
        public bool CalibrationAuto;
        public string CalibrationPoseText = "", ScaleText = "";

        /// <summary>Calibration moment (source seconds) and whether it was found automatically.</summary>
        public double CalibrationTime;

        /// <summary>Best T-pose found in the take (-1 = none), its quality text, and how the scale was measured.</summary>
        public double DetectedTPose = -1;

        /// <summary>
        ///     Scene editor hook: changes a device's pose (solve space, before the playspace jumps are put back) before the
        ///     solver sees it: (role, take time, pose, tracked) -> (pose, tracked). Null = the take as recorded.
        /// </summary>
        public Func<BodyRole, double, Pose, bool, (Pose, bool)> DeviceEdit;

        /// <summary>Devices this solve doesn't use at all (scene editor: a device's influence switched off). Null = all.</summary>
        public HashSet<BodyRole> Disabled;

        public int FeetDropFrames, FeetDropActive;

        /// <summary>Largest hips drop applied to keep the feet on their trackers (m), for the solve notes.</summary>
        public float MaxFeetDrop, SumFeetDrop;

        /// <summary>
        ///     Eye height measured from the headset at the calibration moment (real metres, head tilt removed), for
        ///     comparison.
        /// </summary>
        public float MeasuredEyeHeight;

        public float RealEyeHeight, AvatarEyeHeight, TrackScale = 1f;

        /// <summary>
        ///     The user's own eye height (real metres) from the height setting; with "by arm span" RealEyeHeight is the
        ///     implied one instead.
        /// </summary>
        public float UserEyeHeight;

        /// <summary>User's wrist-to-wrist span at calibration (real metres, 0 = not measured) and the avatar's.</summary>
        public float UserSpan, AvatarSpan;

        Bind _bHips, _bFootL, _bFootR;

        // the avatar's T-pose root at the calibration moment (solver space), as BindTargets placed it
        Pose _calRoot;
        bool _calRootOk;

        // the calibration pose's heading, set while binding: this calibration's tracker nudges are in this frame
        Quat _nudgeYaw;
        bool _nudging;
        Dictionary<string, Vec3> _nudges;

        /// <summary>Identifies this calibration in the take's edits (<see cref="EditList.TrackerNudges" />).</summary>
        public string CalibrationKey => EditList.CalibrationKey(Snapshot, CalibrationTime);

        // foot tracker heights at the calibration moment (standing flat on the floor), for KeepFeetPlanted
        float _calFootL = float.NaN, _calFootR = float.NaN;
        float _calHeadY = float.NaN;

        Pose[] _fP;

        // fusion cache (OVR centered+scaled -> VRC centered), sampled at 30 Hz and smoothed
        double[] _fT;
        bool _feetFirst;
        Vec3 _legL, _legR; // hips bone -> upper leg joint, avatar rest (avatar-scaled)
        float _legLenL, _legLenR; // upper leg joint -> foot, straight
        Pose _ovrOrigin = Pose.Identity, _vrcOrigin = Pose.Identity;

        public TrackingRig(Recording rec, AvatarProfile prof, float avatarScale, CalibrationSettings cal,
            CalibrationSource source = null)
        {
            _rec = rec;
            _prof = prof;
            _cal = cal ?? new CalibrationSettings();
            // the solver's avatar eye height (its skeleton is the profile at avatarScale), so calibration and solve agree
            AvatarEyeHeight = prof.EyeHeight * avatarScale;
            var devs = rec.Devices.Where(d => d.Count > 2).ToList();
            _hmd = devs.Where(d => d.DeviceClass == 1).OrderByDescending(d => d.Count).FirstOrDefault();
            _ctrlL = devs.Where(d => d.DeviceClass == 2 && d.ControllerRole == 1).OrderByDescending(d => d.Count)
                .FirstOrDefault();
            _ctrlR = devs.Where(d => d.DeviceClass == 2 && d.ControllerRole == 2).OrderByDescending(d => d.Count)
                .FirstOrDefault();
            _kindL = Kind(_ctrlL);
            _kindR = Kind(_ctrlR);
            _vHead = Valid(rec.Stream(StreamKind.VrcHead));
            _vL = Valid(rec.Stream(StreamKind.VrcLeftWrist));
            _vR = Valid(rec.Stream(StreamKind.VrcRightWrist));
            _locomotion = rec.Edits.IncludeLocomotion;
            _vrcSpace = _vHead != null && (_locomotion || _hmd == null);
            Jumps = PlayspaceJumps.For(rec);
            if (_hmd == null && _vHead == null) Notes.Add("No head tracking in this recording");

            double tRef = rec.Edits.OriginTime;
            if (_hmd != null && _hmd.Sample(tRef, out var hp, out var hr, 2.0))
                _ovrOrigin = new Pose(new Vec3(hp.x, 0, hp.z), Quat.YawRotation(hr.Yaw));

            // calibration moment: the linked calibration snapshot, else the T-pose in the take (raw real-world data,
            // independent of the scale)
            DetectedTPose = DetectTPose(out string bestText);
            var srcSnap = source != null ? source.Snapshot :
                rec.Edits.CalibrationTime == EditList.UseSnapshot ||
                rec.Edits.CalibrationTime == EditList.UseVrcTimeline ? rec.Edits.Snapshot : null;
            if (source != null && source.Snapshot == null && source.Time >= 0)
            {
                // one of VRChat's calibrations in this take (or another explicit moment)
                CalibrationAuto = false;
                CalibrationTime = source.Time;
                CalibrationPoseText =
                    source.Text.Length > 0 ? source.Text : $"calibration moment {CalibrationTime:F2} s";
            }
            else if (srcSnap != null && srcSnap.Hmd != null)
            {
                Snapshot = srcSnap;
                CalibrationAuto = false;
                CalibrationTime = EditList.UseSnapshot;
                CalibrationPoseText = (source != null && source.Text.Length > 0 ? source.Text + ": " : "") +
                                      Snapshot.Label + (string.IsNullOrEmpty(Snapshot.Detail)
                                          ? ""
                                          : " - " + Snapshot.Detail);
                if (RawCal(out var sh, out var sl, out var sr, out _))
                    CalibrationPoseText += TPoseScore(sh, sl, sr, out string swhy) >= 0
                        ? $"; T-pose ({swhy})"
                        : $"; not a T-pose ({swhy}): body trackers are bound as if standing straight";
                Snapshot.Compare(rec, out var missing, out var extra);
                if (missing.Count > 0)
                    Notes.Add(
                        $"Calibration snapshot has no pose for tracker(s) {string.Join(", ", missing)}: they are not used");
                string session = rec.Extra.GetStr("steamvrSession", "");
                if (session.Length > 0 && Snapshot.SteamVRSession.Length > 0 && session != Snapshot.SteamVRSession)
                    Notes.Add(
                        "Calibration snapshot is from another SteamVR session: if the playspace or tracker straps changed, recalibrate");
            }
            else
            {
                CalibrationAuto = rec.Edits.CalibrationTime < 0;
                CalibrationTime =
                    CalibrationAuto ? DetectedTPose >= 0 ? DetectedTPose : tRef : rec.Edits.CalibrationTime;
                if (CalibrationAuto)
                    CalibrationPoseText = DetectedTPose >= 0
                        ? $"T-pose found at {DetectedTPose:F2} s ({bestText})"
                        : "no T-pose found, calibrating at the recording origin";
                else
                {
                    CalibrationPoseText = $"calibration moment {CalibrationTime:F2} s (set with Calib.)";
                    if (RawPose(CalibrationTime, out var ch, out var cl, out var cr, out _) &&
                        TPoseScore(ch, cl, cr, out string why) < 0)
                        CalibrationPoseText += $": not a T-pose ({why})" + (DetectedTPose >= 0
                            ? $"; best T-pose in the take is at {DetectedTPose:F2} s"
                            : "");
                }
            }

            ResolveScale();

            if (_vHead != null && _vHead.Sample(tRef, out var vp, out var vr, 2.0))
            {
                float eye = AvatarEyeHeight;
                if (_hmd != null && _hmd.Sample(tRef, out var hp2, out _, 2.0))
                    eye = hp2.y * TrackScale; // works even if crouched
                _vrcOrigin = new Pose(new Vec3(vp.x, vp.y - eye, vp.z), Quat.YawRotation(vr.Yaw));
            }

            // user origin tweaks
            var e = rec.Edits;
            var tweak = new Pose(new Vec3(e.OriginOffsetX, 0, e.OriginOffsetZ), Quat.YawRotation(e.OriginYawOffset));
            _ovrOrigin = _ovrOrigin * tweak;
            _vrcOrigin = _vrcOrigin * tweak;

            if (_vrcSpace && _hmd != null) BuildFusion();
            if (Jumps.Any) Notes.Add(Jumps.Describe());
            ResolveWrists(Snapshot);
            AssignTrackers(devs);
            // tracking-loss repair for the trackers in use (TrackerGaps)
            _gaps = TrackerGaps.For(rec);
            _roleOfStream = new Dictionary<PoseStream, BodyRole>();
            foreach (var kv in _trackers)
                if (_cal.UsesRole(kv.Key))
                    _roleOfStream[kv.Value] = kv.Key;
            foreach (var g in _gaps)
                if (!g.Ignored && _roleOfStream.Keys.Any(k => k.Serial == g.Serial))
                    RepairedGaps.Add(g);
            if (RepairedGaps.Count > 0)
            {
                var bySerial = RepairedGaps.GroupBy(g => g.Serial).Select(gr =>
                    $"{_roleOfStream.First(kv => kv.Key.Serial == gr.Key).Value} {gr.Count()}x ({gr.Sum(x => double.IsInfinity(x.Length) ? 0 : x.Length):0.0} s{(gr.Any(x => double.IsInfinity(x.Length)) ? ", lost to the end" : "")})");
                Notes.Add("Tracking loss repaired (carried with the neighbouring tracker): " +
                          string.Join(", ", bySerial));
            }

            var used = _trackers.Keys.Where(_cal.UsesRole).ToList();
            var ignored = _trackers.Keys.Where(k => !_cal.UsesRole(k)).ToList();
            Notes.Add(
                $"Body inputs: head={(_vrcSpace ? "VRChat" : _hmd != null ? "SteamVR" : "none")}, hands={(_vrcSpace && _vL != null ? "VRChat wrists" : _ctrlL != null || _ctrlR != null ? "SteamVR controllers" : "none")}, trackers={used.Count} ({string.Join(", ", used)})" +
                (ignored.Count > 0 ? $", ignored {string.Join(", ", ignored)}" : ""));
            Notes.AddRange(RoleNotes);
            Notes.Add("Calibration: " + CalibrationPoseText);
            Notes.Add("Scale: " + ScaleText);
            if (_ctrlL != null || _ctrlR != null)
            {
                bool vrcHands = _cal.VrcHands && (_kindL == HandSourceKind.Index || _kindR == HandSourceKind.Index);
                Notes.Add(vrcHands
                    ? "Hands: VRChat's hand position on Index controllers (measured)"
                    : "Controller -> wrist: " + (WristSource[0] == WristSource[1]
                        ? WristSource[0]
                        : $"left {WristSource[0]}, right {WristSource[1]}"));
            }
        }

        /// <summary>
        ///     The SteamVR room (play area, base stations: raw standing space, captured at <paramref name="captureT" />) in the
        ///     solved clip's space at a moment, like the devices (TrackerTracks): world point = M.TransformPoint(TrackScale *
        ///     raw),
        ///     world rotation = M.rot * raw. The playspace jumps, VRChat-space fusion and the tracking scale are in it.
        /// </summary>
        public bool HasHmd => _hmd != null;

        static PoseStream Valid(PoseStream s)
        {
            return s != null && s.Count > 2 ? s : null;
        }

        static HandSourceKind Kind(PoseStream s)
        {
            if (s == null) return HandSourceKind.Unknown;
            string k = (s.ControllerType + " " + s.ModelNumber + " " + s.Info.GetStr("renderModel", ""))
                .ToLowerInvariant();
            if (k.Contains("knuckles") || k.Contains("index")) return HandSourceKind.Index;
            if (k.Contains("vive_controller") || k.Contains("vive. controller") || k.Contains("vive controller"))
                return HandSourceKind.ViveWand;
            if (k.Contains("oculus") || k.Contains("touch") || k.Contains("quest") || k.Contains("meta"))
                return HandSourceKind.OculusTouch;
            return HandSourceKind.Generic;
        }

        // ------------------------------------------------------------------ calibration: T-pose and size

        /// <summary>Raw SteamVR head and controllers (playspace, real metres, floor at y = 0).</summary>
        bool RawPose(double t, out Pose head, out Pose l, out Pose r, out float eye)
        {
            head = l = r = Pose.Identity;
            eye = 0;
            if (_hmd == null || _ctrlL == null || _ctrlR == null) return false;
            if (!_hmd.Sample(t, out var hp, out var hq, 0.1) || !_ctrlL.Sample(t, out var lp, out var lq, 0.1) ||
                !_ctrlR.Sample(t, out var rp, out var rq, 0.1)) return false;
            head = new Pose(hp, hq);
            l = new Pose(lp, lq);
            r = new Pose(rp, rq);
            eye = hp.y;
            return hp.y > 0.6f;
        }

        /// <summary>
        ///     Raw pose (playspace, real metres) of a take device at the calibration moment: from the snapshot (by serial) or
        ///     the take.
        /// </summary>
        bool CalRaw(PoseStream s, out Pose p)
        {
            p = Pose.Identity;
            if (s == null) return false;
            if (Snapshot != null)
            {
                var d = Snapshot.Find(s.Serial);
                if (d == null && s.DeviceClass == 1) d = Snapshot.Hmd; // a replaced headset is still the head
                if (d == null && s.DeviceClass == 2 && (s.ControllerRole == 1 || s.ControllerRole == 2))
                    d = Snapshot.Controller(s.ControllerRole == 1);
                if (d == null) return false;
                p = new Pose(d.Pos, d.Rot);
                return true;
            }

            if (!s.Sample(CalibrationTime, out var pos, out var rot)) return false;
            p = new Pose(pos, rot);
            return true;
        }

        /// <summary>Head and controllers at the calibration moment (raw), like RawPose.</summary>
        bool RawCal(out Pose head, out Pose l, out Pose r, out float eye)
        {
            if (Snapshot == null) return RawPose(CalibrationTime, out head, out l, out r, out eye);
            head = l = r = Pose.Identity;
            eye = 0;
            if (!CalRaw(_hmd, out head) || !CalRaw(_ctrlL, out l) || !CalRaw(_ctrlR, out r)) return false;
            eye = head.pos.y;
            return head.pos.y > 0.6f;
        }

        /// <summary>A device at the calibration moment in solver space (centered, scaled, VRChat space when fused).</summary>
        bool CalOvr(PoseStream s, out Pose p)
        {
            if (!CalRaw(s, out var raw))
            {
                p = Pose.Identity;
                return false;
            }

            if (Snapshot == null && Jumps.Any)
                raw = Jumps.Total(CalibrationTime).Inverse * raw; // same continuous space as SampleOvr
            p = OvrToCentered(raw);
            if (_vrcSpace) p = Fusion(Snapshot != null ? _rec.Edits.OriginTime : CalibrationTime) * p;
            foreach (var kv in _trackers)
                if (kv.Value == s)
                    p = Nudged(kv.Key, p);
            return true;
        }

        /// <summary>
        ///     A body tracker at the calibration moment moved by the take's nudge (avatar cm in the calibration pose's frame),
        ///     so the bone is bound that much the other way. Unchanged outside the binding.
        /// </summary>
        Pose Nudged(BodyRole role, Pose p)
        {
            if (!_nudging || _nudges == null || !_nudges.TryGetValue(role.ToString(), out var n)) return p;
            return new Pose(p.pos + _nudgeYaw * (n * 0.01f), p.rot);
        }

        /// <summary>The head at the calibration moment in solver space.</summary>
        bool CalHead(out Pose p)
        {
            return Snapshot == null ? Head(CalibrationTime, out p) : CalOvr(_hmd, out p);
        }

        /// <summary>
        ///     How much a moment looks like VRChat's calibration pose: standing, looking ahead, arms out to the sides.
        ///     Returns a score (higher is better) or -1 with the reason. All thresholds are relative to the eye height.
        /// </summary>
        static float TPoseScore(Pose h, Pose l, Pose r, out string why)
        {
            float H = h.pos.y;
            var yi = Quat.YawRotation(-h.rot.Yaw);
            var ll = yi * (l.pos - h.pos);
            var rl = yi * (r.pos - h.pos);
            var eu = h.rot.ToEulerUnity();
            float pitch = MathX.DeltaAngle(0, eu.x), roll = MathX.DeltaAngle(0, eu.z);
            float span = rl.x - ll.x;
            var bad = new List<string>();
            if (span < 0.7f * H) bad.Add($"arms not out: span {span / H:0.00} x eye height");
            if (ll.y < -0.38f * H || rl.y < -0.38f * H) bad.Add("hands too low");
            if (ll.y > 0.12f * H || rl.y > 0.12f * H) bad.Add("hands too high");
            if (Math.Abs(ll.z) > 0.3f * H || Math.Abs(rl.z) > 0.3f * H) bad.Add("hands too far forward/back");
            if (Math.Abs(pitch) > 30) bad.Add($"head {(pitch > 0 ? "down" : "up")} {Math.Abs(pitch):0}°");
            if (Math.Abs(roll) > 20) bad.Add($"head tilted {Math.Abs(roll):0}°");
            why = string.Join(", ", bad);
            if (bad.Count > 0) return -1;
            why = $"span {span / H:0.00} x eye, head pitch {pitch:+0;-0;0}° roll {roll:+0;-0;0}°";
            return Math.Min(span / H, 1.1f) + 0.3f * (1 - Math.Abs(pitch) / 30f) + 0.3f * (1 - Math.Abs(roll) / 20f) -
                   Math.Abs(ll.y - rl.y) / H;
        }

        /// <summary>Finds the best T-pose (VRChat calibration pose) in the trimmed take: arms out, head level, holding still.</summary>
        public double DetectTPose(out string text)
        {
            text = "";
            var e = _rec.Edits;
            double a = Math.Max(0, e.TrimIn), b = e.TrimOut < 0 ? _rec.Duration : e.TrimOut;
            double best = -1;
            float bestScore = float.MinValue;
            const double hold = 0.3;
            for (double t = a; t <= b - hold; t += 0.05)
            {
                if (!RawPose(t, out var h, out var l, out var r, out float H)) continue;
                float s = TPoseScore(h, l, r, out string why);
                if (s < 0) continue;
                // holding still (VRChat calibrates on a deliberate pose, not a pass-through)
                if (!RawPose(t + hold, out var h2, out var l2, out var r2, out _)) continue;
                float move = ((l2.pos - l.pos).Length + (r2.pos - r.pos).Length) / H + 2f * (h2.pos - h.pos).Length / H;
                if (move > 0.08f) continue;
                s -= move * 3f;
                if (s > bestScore)
                {
                    bestScore = s;
                    best = t + hold * 0.5;
                    text = why;
                }
            }

            if (best >= 0 && RawPose(best, out var bh, out var bl, out var br, out _)) TPoseScore(bh, bl, br, out text);
            return best;
        }

        /// <summary>Center eye minus head bone, avatar rest (avatar-scaled metres): the lever arm of head tilt.</summary>
        Vec3 EyeFromHeadPivot()
        {
            if (!_prof.Has(HumanBones.Head)) return Vec3.Zero;
            float s = AvatarEyeHeight / Math.Max(0.05f, _prof.EyeHeight);
            return (_prof.ViewPoint - _prof.HumanRestPos(HumanBones.Head)) * s;
        }

        /// <summary>
        ///     The headset pose with head tilt removed: the head is turned upright about the avatar's head pivot, keeping
        ///     only its yaw. The user is asked to look straight ahead when calibrating; this makes a calibration with the
        ///     head slightly down or tilted come out the same.
        /// </summary>
        Pose Upright(Pose eye, Vec3 lever)
        {
            var pivot = eye.pos - eye.rot * lever;
            var yaw = Quat.YawRotation(eye.rot.Yaw);
            return new Pose(pivot + yaw * lever, yaw);
        }

        /// <summary>
        ///     The head pose the solver calibrates with: upright (optional) and standing on the floor, i.e. at the avatar's
        ///     eye height.
        /// </summary>
        public Pose CalibrationHead(Pose head)
        {
            var p = _cal.UprightCalibration ? Upright(head, EyeFromHeadPivot()) : new Pose(head.pos, head.rot);
            // VRChat hangs the avatar from the headset at calibration: measured on its own IK (HUMR), one test avatar's feet stay
            // 2 cm below the floor by height and 9.7 cm below by arm span, with
            // straight legs. Standing the avatar on the floor instead (as the base solver does) bends the knees when the scaled headset is
            // lower than the avatar's eyes.
            if (!_cal.HangFromHeadset) p.pos = new Vec3(p.pos.x, AvatarEyeHeight, p.pos.z);
            return p;
        }

        Pose SolverEye(Pose hmd)
        {
            if (!_cal.VrcHands || VrcEyeBehindHeadset == 0) return hmd;
            float k = AvatarEyeHeight / FbikAdapter.VrcHandRefEye;
            return new Pose(hmd.pos + hmd.rot * new Vec3(0, 0, -VrcEyeBehindHeadset * k), hmd.rot);
        }

        float AvatarWristSpan()
        {
            var p = _prof;
            int lu = HumanBones.LeftUpperArm,
                ll = HumanBones.LeftLowerArm,
                lh = HumanBones.LeftHand,
                ru = HumanBones.RightUpperArm,
                rl = HumanBones.RightLowerArm,
                rh = HumanBones.RightHand;
            if (!(p.Has(lu) && p.Has(ll) && p.Has(lh) && p.Has(ru) && p.Has(rl) && p.Has(rh))) return 0;

            Vec3 P(int b)
            {
                return p.HumanRestPos(b);
            }

            float shoulders = new Vec3(P(lu).x - P(ru).x, 0, P(lu).z - P(ru).z).Length;
            float span = shoulders + (P(lu) - P(ll)).Length + (P(ll) - P(lh)).Length + (P(ru) - P(rl)).Length +
                         (P(rl) - P(rh)).Length;
            return span * AvatarEyeHeight / Math.Max(0.05f, p.EyeHeight);
        }

        /// <summary>User eye height (real metres) and tracking scale from the calibration settings.</summary>
        void ResolveScale()
        {
            float measured = 0;
            string how = "";
            if (CalRaw(_hmd, out var hpose) && hpose.pos.y > 0.5f)
            {
                var hp = hpose.pos;
                var hq = hpose.rot;
                measured = hp.y;
                if (_cal.UprightCalibration)
                {
                    float k = hp.y / Math.Max(0.1f, AvatarEyeHeight); // avatar -> real metres (first guess)
                    measured = Upright(new Pose(hp, hq), EyeFromHeadPivot() * k).pos.y;
                }

                how = Snapshot != null ? "headset in the calibration snapshot" : $"headset at {CalibrationTime:F2} s";
            }
            else if (_rec.UserEyeHeight > 0.5f)
            {
                measured = _rec.UserEyeHeight;
                how = "recorded eye height";
            }

            if (measured < 0.6f || measured > 2.4f)
            {
                measured = 1.6f;
                how = "default (no headset at the calibration moment)";
            }

            MeasuredEyeHeight = measured;
            switch (_cal.Height)
            {
                case CalHeightMode.VRChat when VrcHeight(out float vh, out string src):
                    RealEyeHeight = vh * CalibrationSettings.EyeToHeightRatio;
                    how = $"VRChat User Real Height {vh * 100:0} cm, {src}";
                    break;
                case CalHeightMode.RealHeight when _cal.HeightValue > 0.5f:
                    RealEyeHeight = _cal.HeightValue * CalibrationSettings.EyeToHeightRatio;
                    how = $"real height {_cal.HeightValue:0.00} m";
                    break;
                case CalHeightMode.EyeHeight when _cal.HeightValue > 0.4f:
                    RealEyeHeight = _cal.HeightValue;
                    how = $"eye height {_cal.HeightValue:0.00} m";
                    break;
                case CalHeightMode.Scale when _cal.HeightValue > 0.01f:
                    TrackScale = _cal.HeightValue;
                    RealEyeHeight = UserEyeHeight = AvatarEyeHeight / TrackScale;
                    ScaleText = $"fixed tracking scale {TrackScale:0.000} (eye height {RealEyeHeight:0.000} m)";
                    return;
                default:
                    RealEyeHeight = measured;
                    how = "measured from the " + how;
                    break;
            }

            TrackScale = AvatarEyeHeight / RealEyeHeight;
            UserEyeHeight = RealEyeHeight;
            ScaleText =
                $"eye height {RealEyeHeight:0.000} m ({how}) -> avatar eye {AvatarEyeHeight:0.000} m, scale {TrackScale:0.000}";

            if (_cal.Measure == CalMeasure.ArmSpan)
            {
                AvatarSpan = AvatarWristSpan();
                if (_cal.Height == CalHeightMode.Measure && RawCal(out var h, out var l, out var r, out _) &&
                    TPoseScore(h, l, r, out _) >= 0)
                {
                    // user's span measured between the controllers in the T-pose
                    var yi = Quat.YawRotation(-h.rot.Yaw);
                    UserSpan = Math.Max(0.3f, (yi * (r.pos - l.pos)).x - 2 * WristInset);
                    how = $"arm span {UserSpan:0.000} m measured " +
                          (Snapshot != null ? "in the calibration snapshot" : $"at {CalibrationTime:F2} s");
                    if (AvatarSpan > 0.1f)
                    {
                        TrackScale = AvatarSpan / UserSpan;
                        RealEyeHeight = AvatarEyeHeight / TrackScale;
                        ScaleText =
                            $"{how} -> avatar span {AvatarSpan:0.000} m, scale {TrackScale:0.000} (implied eye height {RealEyeHeight:0.000} m)";
                    }
                }
                else
                {
                    // VRChat "by arm span" (Immersive Scaler's model; matches VRChat's own IK within 0.3% on one test avatar): the
                    // avatar's head bone to wrist, arm straight out in the T-pose, counts as VRC_IK_HEIGHT_RATIO (0.4537) of
                    // an eye height, and that "arm eye height" is mapped onto the user's eye height.
                    float reach = AvatarHeadToWrist();
                    float ratio = VrcArmRatio();
                    if (reach > 0.05f)
                    {
                        float armEye = reach / ratio, userEye = RealEyeHeight;
                        TrackScale = armEye / userEye;
                        UserSpan = 0;
                        RealEyeHeight = AvatarEyeHeight / TrackScale;
                        ScaleText =
                            $"by arm span (VRChat): avatar head-to-wrist {reach:0.000} m / {ratio:0.0000} = {armEye:0.000} m on your eye height {userEye:0.000} m ({how}); scale {TrackScale:0.000} (implied eye height {RealEyeHeight:0.000} m)";
                    }
                }
            }
        }

        /// <summary>VRC_IK_HEIGHT_RATIO stored with the snapshot / take, else VRChat's default.</summary>
        float VrcArmRatio()
        {
            if (Snapshot != null && Snapshot.Vrc.ArmRatio > 0.2f && Snapshot.Vrc.ArmRatio < 0.8f)
                return Snapshot.Vrc.ArmRatio;
            var t = VrcSettings.Values.FromJson(_rec.Extra.GetObj("vrchatSettings"));
            if (t.ArmRatio > 0.2f && t.ArmRatio < 0.8f) return t.ArmRatio;
            return CalibrationSettings.VrcArmRatio;
        }

        /// <summary>
        ///     Head bone to wrist with the arm held straight out from the upper-arm joint (avatar rest, avatar-scaled
        ///     metres), longer side.
        /// </summary>
        float AvatarHeadToWrist()
        {
            var p = _prof;
            if (!p.Has(HumanBones.Head)) return 0;
            float best = 0;
            foreach (bool left in new[] { true, false })
            {
                int u = left ? HumanBones.LeftUpperArm : HumanBones.RightUpperArm,
                    l = left ? HumanBones.LeftLowerArm : HumanBones.RightLowerArm,
                    h = left ? HumanBones.LeftHand : HumanBones.RightHand;
                if (!(p.Has(u) && p.Has(l) && p.Has(h))) continue;

                Vec3 P(int b)
                {
                    return p.HumanRestPos(b);
                }

                float arm = (P(u) - P(l)).Length + (P(l) - P(h)).Length;
                var wrist = P(u) + new Vec3(Math.Sign(P(u).x == 0 ? left ? -1 : 1 : P(u).x) * arm, 0, 0);
                best = Math.Max(best, (P(HumanBones.Head) - wrist).Length);
            }

            return best * AvatarEyeHeight / Math.Max(0.05f, p.EyeHeight);
        }

        /// <summary>VRChat's "User Real Height": stored with the snapshot or the take, else read from VRChat's settings now.</summary>
        bool VrcHeight(out float h, out string src)
        {
            h = 0;
            src = "";
            if (Snapshot != null && Snapshot.Vrc.PlayerHeight > 0.5f)
            {
                h = Snapshot.Vrc.PlayerHeight;
                src = "at calibration";
                return true;
            }

            var t = VrcSettings.Values.FromJson(_rec.Extra.GetObj("vrchatSettings"));
            if (t.PlayerHeight > 0.5f)
            {
                h = t.PlayerHeight;
                src = "when recorded";
                return true;
            }

            var now = VrcSettings.Read();
            if (now.PlayerHeight > 0.5f)
            {
                h = now.PlayerHeight;
                src = "VRChat settings now";
                return true;
            }

            return false;
        }

        // ------------------------------------------------------------------ spaces

        Pose OvrToCentered(Pose p)
        {
            var inv = _ovrOrigin.Inverse;
            var c = inv * p;
            return new Pose(c.pos * TrackScale, c.rot);
        }

        Pose VrcToCentered(Pose p)
        {
            return _vrcOrigin.Inverse * p;
        }

        public Pose RoomAt(double t, double captureT)
        {
            float k = TrackScale;
            var ok = new Pose(_ovrOrigin.pos * k, _ovrOrigin.rot);
            var m = ok.Inverse;
            if (Jumps.Any)
            {
                var j = Jumps.Total(captureT);
                m = m * new Pose(j.pos * k, j.rot).Inverse;
            } // raw then -> the continuous space

            if (_vrcSpace) m = Fusion(t) * m;
            return UniverseAt(t) * m;
        }

        void BuildFusion()
        {
            double dur = _rec.Duration;
            int n = Math.Max(2, (int)(dur * 30) + 1);
            double[] t = new double[n];
            var raw = new Pose[n];
            bool[] ok = new bool[n];
            // VRChat jumps move VRChat's head, not the user: held over (VrcJumps) unless the take keeps jumps
            var jumps = _rec.Edits.IncludeJumps ? null : VrcJumps.For(_rec, _prof);
            for (int i = 0; i < n; i++)
            {
                t[i] = i / 30.0;
                if (jumps != null && jumps.InJump(t[i])) continue;
                if (SampleRaw(_hmd, t[i], out var hraw, 0.2) && _vHead.Sample(t[i], out var vp, out var vr, 0.2))
                {
                    var o = OvrToCentered(hraw);
                    var v = VrcToCentered(new Pose(vp, vr));
                    float dyaw = MathX.DeltaAngle(o.rot.Yaw, v.rot.Yaw);
                    var yq = Quat.YawRotation(dyaw);
                    raw[i] = new Pose(v.pos - yq * o.pos, yq);
                    ok[i] = true;
                }
            }

            // fill gaps, then smooth (locomotion changes slowly compared to tracking jitter between the two clocks)
            int last = -1;
            for (int i = 0; i < n; i++)
            {
                if (ok[i])
                {
                    if (last < 0)
                        for (int k = 0; k < i; k++)
                            raw[k] = raw[i];
                    last = i;
                }
                else if (last >= 0) raw[i] = raw[last];
            }

            if (last < 0)
            {
                _fT = null;
                return;
            }

            var sm = new Pose[n];
            const int W = 3;
            for (int i = 0; i < n; i++)
            {
                var acc = Vec3.Zero;
                float yawAcc = 0;
                int c = 0;
                float y0 = raw[i].rot.Yaw;
                for (int k = Math.Max(0, i - W); k <= Math.Min(n - 1, i + W); k++)
                {
                    acc += raw[k].pos;
                    yawAcc += MathX.DeltaAngle(y0, raw[k].rot.Yaw);
                    c++;
                }

                sm[i] = new Pose(acc / c, Quat.YawRotation(y0 + yawAcc / c));
            }

            _fT = t;
            _fP = sm;
            // report whether VRChat's space moved relative to the playspace (= locomotion captured)
            float maxMove = 0;
            for (int i = 0; i < n; i++) maxMove = Math.Max(maxMove, (sm[i].pos - sm[0].pos).Length);
            Notes.Add(maxMove > 0.3f
                ? $"Locomotion captured from VRChat ({maxMove:F1} m)"
                : "No thumbstick locomotion detected");
        }

        Pose Fusion(double t)
        {
            if (_fT == null) return Pose.Identity;
            double x = t * 30.0;
            int i = MathX.Clamp((int)Math.Floor(x), 0, _fT.Length - 1);
            int j = Math.Min(_fT.Length - 1, i + 1);
            float a = (float)MathX.Clamp(x - i, 0, 1);
            return Pose.Lerp(_fP[i], _fP[j], a);
        }

        /// <summary>
        ///     Raw SteamVR pose with the playspace jumps undone (continuous for the solver); lost tracking repaired
        ///     (TrackerGaps).
        /// </summary>
        bool SampleRaw(PoseStream s, double t, out Pose raw, double maxGap = 0.3)
        {
            raw = Pose.Identity;
            double ts = Jumps.SampleTime(t);
            if (s == null) return false;
            var g = _roleOfStream != null && _roleOfStream.ContainsKey(s) ? TrackerGaps.At(_gaps, s.Serial, ts) : null;
            if (g != null && Repaired(s, g, ts, out raw)) { }
            else if (!s.Sample(ts, out var pos, out var rot, maxGap)) return false;
            else raw = new Pose(pos, rot);

            if (Jumps.Any) raw = Jumps.Total(t).Inverse * raw;
            return true;
        }

        PoseStream StreamOf(BodyRole r)
        {
            return r == BodyRole.Head ? _hmd :
                r == BodyRole.LeftHand ? _ctrlL :
                r == BodyRole.RightHand ? _ctrlR :
                _trackers.TryGetValue(r, out var s) && _cal.UsesRole(r) ? s : null;
        }

        /// <summary>
        ///     A lost tracker carried by a neighbouring device on the same body segment (TrackerGaps.Carriers): its offset from
        ///     that device blends from the last good one before the loss to the first good one after it.
        /// </summary>
        bool Repaired(PoseStream s, TrackerGap g, double t, out Pose raw)
        {
            raw = Pose.Identity;
            bool hasA = !double.IsNegativeInfinity(g.A), hasB = !double.IsPositiveInfinity(g.B);
            Pose sa = Pose.Identity, sb = Pose.Identity;
            if (hasA)
            {
                hasA = s.Sample(g.A, out var p, out var q, 0.05);
                sa = new Pose(p, q);
            }

            if (hasB)
            {
                hasB = s.Sample(g.B, out var p, out var q, 0.05);
                sb = new Pose(p, q);
            }

            if (!hasA && !hasB) return false;
            float w = TrackerGaps.Blend(g, t);
            foreach (var cr in TrackerGaps.Carriers(_roleOfStream[s]))
            {
                var c = StreamOf(cr);
                if (c == null || c == s) continue;
                if (TrackerGaps.At(_gaps, c.Serial, t) != null) continue; // lost too
                if (!c.Sample(t, out var cp, out var cq)) continue;
                Pose ra = Pose.Identity, rb = Pose.Identity;
                bool okA = hasA && c.Sample(g.A, out var ap, out var aq),
                    okB = hasB && c.Sample(g.B, out var bp, out var bq);
                if (okA)
                {
                    c.Sample(g.A, out ap, out aq);
                    ra = new Pose(ap, aq).Inverse * sa;
                }

                if (okB)
                {
                    c.Sample(g.B, out bp, out bq);
                    rb = new Pose(bp, bq).Inverse * sb;
                }

                if (!okA && !okB) continue;
                var rel = okA && okB ? Pose.Lerp(ra, rb, w) : okA ? ra : rb;
                raw = new Pose(cp, cq) * rel;
                return true;
            }

            // nothing to carry it: straight across in the playspace
            raw = hasA && hasB ? Pose.Lerp(sa, sb, w) : hasA ? sa : sb;
            return true;
        }

        /// <summary>
        ///     The playspace jumps in solver space at a moment: true pose = this * the pose the solver was fed (and solved).
        ///     Identity without jumps.
        /// </summary>
        public Pose UniverseAt(double t)
        {
            if (!Jumps.Any) return Pose.Identity;
            var m = _ovrOrigin.Inverse * Jumps.Total(t) * _ovrOrigin;
            return new Pose(m.pos * TrackScale, m.rot);
        }

        bool SampleOvr(PoseStream s, double t, out Pose p)
        {
            p = Pose.Identity;
            if (!SampleRaw(s, t, out var raw)) return false;
            p = OvrToCentered(raw);
            if (_vrcSpace) p = Fusion(t) * p;
            return true;
        }

        bool SampleVrc(PoseStream s, double t, out Pose p)
        {
            p = Pose.Identity;
            if (s == null || !s.Sample(t, out var pos, out var rot, 0.5)) return false;
            p = VrcToCentered(new Pose(pos, rot));
            return true;
        }

        bool Off(BodyRole r)
        {
            return Disabled != null && Disabled.Contains(r);
        }

        bool Edit(BodyRole r, double t, ref Pose p, bool ok)
        {
            if (DeviceEdit == null) return ok;
            var x = DeviceEdit(r, t, p, ok);
            p = x.Item1;
            return x.Item2;
        }

        /// <summary>
        ///     A body tracker's pose (with the scene editor's edits); false when it isn't there, not tracked, or switched
        ///     off.
        /// </summary>
        bool Tracker(BodyRole role, double t, out Pose p, bool evenIfOff = false)
        {
            p = Pose.Identity;
            if (!_trackers.TryGetValue(role, out var s) || (!evenIfOff && Off(role))) return false;
            bool ok = SampleOvr(s, t, out p);
            return Edit(role, t, ref p, ok);
        }

        public bool Head(double t, out Pose p)
        {
            bool ok = _vrcSpace ? SampleVrc(_vHead, t, out p) : SampleOvr(_hmd, t, out p);
            return Edit(BodyRole.Head, t, ref p, ok) && !Off(BodyRole.Head);
        }

        public bool Hand(bool left, double t, out Pose p, out HandSourceKind kind)
        {
            bool ok;
            var v = left ? _vL : _vR;
            if (_vrcSpace && v != null && SampleVrc(v, t, out p))
            {
                kind = HandSourceKind.VrcWrist;
                ok = true;
            }
            else
            {
                kind = left ? _kindL : _kindR;
                ok = SampleOvr(left ? _ctrlL : _ctrlR, t, out p);
            }

            if (ok) HandFix(left, ref p);
            var role = left ? BodyRole.LeftHand : BodyRole.RightHand;
            return Edit(role, t, ref p, ok) && !Off(role);
        }

        /// <summary>After-the-fact hand rotation correction (local to the hand target, mirrored for the right hand).</summary>
        void HandFix(bool left, ref Pose p)
        {
            var fix = _cal.HandRotFix;
            if (fix.SqrLength > 1e-6f)
                p = new Pose(p.pos,
                    p.rot * (left ? Quat.EulerUnity(fix.x, fix.y, fix.z) : Quat.EulerUnity(fix.x, -fix.y, -fix.z)));
        }

        void AssignTrackers(List<PoseStream> devs)
        {
            var cands = devs.Where(d =>
                d.DeviceClass == 3 || (d.DeviceClass == 2 && d.ControllerRole != 1 && d.ControllerRole != 2)).ToList();
            var unassigned = new List<Tuple<PoseStream, Vec3>>();
            CalHead(out var head);
            // heights are taken below the headset, not above y = 0: a playspace moved up or down (playspace mover) must not
            // change which tracker is which. H = the user's eye height in solver units (standing upright).
            float H = Math.Max(0.3f, (UserEyeHeight > 0.3f ? UserEyeHeight : RealEyeHeight) * TrackScale);
            foreach (var d in cands)
            {
                BodyRole role;
                string manual = d.BodyRole;
                if (!string.IsNullOrEmpty(manual))
                {
                    if (manual == "None") continue;
                    if (Enum.TryParse(manual, out role))
                    {
                        _trackers[role] = d;
                        continue;
                    }
                }

                string ct = d.ControllerType.ToLowerInvariant();
                var hint = SteamRoles.FirstOrDefault(kv => ct.EndsWith(kv.Key));
                if (hint.Key != null && !_trackers.ContainsKey(hint.Value))
                {
                    _trackers[hint.Value] = d;
                    continue;
                }

                if (CalOvr(d, out var p)) unassigned.Add(Tuple.Create(d, p.pos));
            }

            // the whole take: VRChat's own assignment when its IK is in the take, else how each tracker moves (TrackerRoleFinder)
            if (unassigned.Count > 0)
            {
                var tr = TrackerRoleFinder.For(_rec, _prof, unassigned.Select(u => u.Item1).ToList(), _hmd, _ctrlL,
                    _ctrlR, UserEyeHeight > 0.3f ? UserEyeHeight : RealEyeHeight);
                var placed = new List<string>();
                foreach (var u in unassigned.ToList())
                {
                    if (!tr.Roles.TryGetValue(u.Item1, out var role) || _trackers.ContainsKey(role)) continue;
                    _trackers[role] = u.Item1;
                    u.Item1.Info["autoRole"] = role.ToString();
                    unassigned.Remove(u);
                    placed.Add($"{role} {u.Item1.Serial}" +
                               (tr.Why.TryGetValue(u.Item1, out string why) && why.Length > 0 ? $" ({why})" : ""));
                }

                if (placed.Count > 0) RoleNotes.Add($"Tracker roles from {tr.Method}: {string.Join("; ", placed)}");
            }

            // geometric assignment relative to the head, using this avatar's own proportions (inputs are avatar-scaled)
            var yawInv = Quat.YawRotation(-head.rot.Yaw);
            float eyeRest = Math.Max(0.2f, _prof.EyeHeight);
            float k = H / eyeRest;

            float Y(int bone, float fallback)
            {
                return _prof.Has(bone) ? _prof.HumanRestPos(bone).y * k : fallback * H;
            }

            float yFoot = Y(HumanBones.LeftFoot, 0.07f),
                yKnee = Y(HumanBones.LeftLowerLeg, 0.3f),
                yHips = Y(HumanBones.Hips, 0.55f);
            float yChest = _prof.Has(HumanBones.UpperChest)
                ? Y(HumanBones.UpperChest, 0.75f)
                : Y(HumanBones.Chest, 0.72f);
            float shoulderHalf = _prof.Has(HumanBones.LeftUpperArm)
                ? Math.Abs(_prof.HumanRestPos(HumanBones.LeftUpperArm).x) * k
                : 0.12f * H;
            foreach (var u in unassigned.OrderBy(u => u.Item2.y))
            {
                var local = yawInv * (u.Item2 - new Vec3(head.pos.x, 0, head.pos.z));
                float y = H - (head.pos.y - u.Item2.y); // height above the floor the headset implies
                bool left = local.x < 0;
                BodyRole? role = null;
                if (Math.Abs(local.x) > shoulderHalf * 1.1f && y > yHips)
                    role = left ? BodyRole.LeftElbow : BodyRole.RightElbow;
                else
                {
                    var roleCands = new[]
                    {
                        Tuple.Create(left ? BodyRole.LeftFoot : BodyRole.RightFoot, yFoot),
                        Tuple.Create(left ? BodyRole.LeftKnee : BodyRole.RightKnee, yKnee),
                        Tuple.Create(BodyRole.Hips, yHips), Tuple.Create(BodyRole.Chest, yChest)
                    };
                    var best = roleCands.OrderBy(c => Math.Abs(c.Item2 - y)).First();
                    if (Math.Abs(best.Item2 - y) < 0.25f * H) role = best.Item1;
                }

                if (role == null) continue;
                var rr = role.Value;
                if (_trackers.ContainsKey(rr))
                {
                    // two trackers on the same side: the other foot/knee, or skip
                    var mirror = Mirror(rr);
                    if (mirror.HasValue && !_trackers.ContainsKey(mirror.Value)) rr = mirror.Value;
                    else continue;
                }

                _trackers[rr] = u.Item1;
                u.Item1.Info["autoRole"] = rr.ToString();
                RoleNotes.Add($"Tracker role {rr} {u.Item1.Serial} from its height at the calibration moment");
            }
        }

        static BodyRole? Mirror(BodyRole r)
        {
            switch (r)
            {
                case BodyRole.LeftFoot: return BodyRole.RightFoot;
                case BodyRole.RightFoot: return BodyRole.LeftFoot;
                case BodyRole.LeftKnee: return BodyRole.RightKnee;
                case BodyRole.RightKnee: return BodyRole.LeftKnee;
                case BodyRole.LeftElbow: return BodyRole.RightElbow;
                case BodyRole.RightElbow: return BodyRole.LeftElbow;
            }

            return null;
        }

        public bool HasTracker(BodyRole role)
        {
            return _trackers.ContainsKey(role) && _cal.UsesRole(role) && !Off(role);
        }

        /// <summary>The take has this tracker (used or not).</summary>
        public bool HasTrackerStream(BodyRole role)
        {
            return _trackers.ContainsKey(role);
        }

        /// <summary>
        ///     Replicates the T-pose reference the solver binds trackers to: the avatar at rest standing on the floor
        ///     under the calibration head, facing its yaw (upstream TryGetCalibrationReference / ComputeTposeAnchor).
        /// </summary>
        void BindTargets(Pose calHead)
        {
            _bHips = _bFootL = _bFootR = default;
            float s = AvatarEyeHeight / Math.Max(0.05f, _prof.EyeHeight);
            var yaw = Quat.YawRotation(calHead.rot.Yaw);
            var eyeLocal = new Vec3(0, AvatarEyeHeight, _prof.ViewPoint.z * s);
            var root = calHead.pos - yaw * eyeLocal;
            _calRoot = new Pose(root, yaw);
            _calRootOk = true;

            Bind B(BodyRole role, int bone)
            {
                var b = new Bind();
                if (!_prof.Has(bone) || !HasTracker(role) || !CalOvr(_trackers[role], out var tp)) return b;
                var rest = _prof.HumanRestPos(bone) * s;
                if (bone == HumanBones.Hips)
                    rest = new Vec3(0, rest.y, rest.z); // the base solver centres spine bones (SetInitialData)
                var ic = tp.rot.Conjugate;
                b.Pos = ic * (root + yaw * rest - tp.pos);
                b.Rot = ic * yaw;
                b.Ok = true;
                return b;
            }

            _bHips = B(BodyRole.Hips, HumanBones.Hips);
            _bFootL = B(BodyRole.LeftFoot, HumanBones.LeftFoot);
            _bFootR = B(BodyRole.RightFoot, HumanBones.RightFoot);

            Vec3 R(int b)
            {
                return _prof.HumanRestPos(b) * s;
            }

            if (_prof.Has(HumanBones.LeftUpperLeg) && _prof.Has(HumanBones.LeftLowerLeg) &&
                _prof.Has(HumanBones.LeftFoot))
            {
                _legL = R(HumanBones.LeftUpperLeg) - new Vec3(0, R(HumanBones.Hips).y, R(HumanBones.Hips).z);
                _legLenL = (R(HumanBones.LeftUpperLeg) - R(HumanBones.LeftLowerLeg)).Length +
                           (R(HumanBones.LeftLowerLeg) - R(HumanBones.LeftFoot)).Length;
            }

            if (_prof.Has(HumanBones.RightUpperLeg) && _prof.Has(HumanBones.RightLowerLeg) &&
                _prof.Has(HumanBones.RightFoot))
            {
                _legR = R(HumanBones.RightUpperLeg) - new Vec3(0, R(HumanBones.Hips).y, R(HumanBones.Hips).z);
                _legLenR = (R(HumanBones.RightUpperLeg) - R(HumanBones.RightLowerLeg)).Length +
                           (R(HumanBones.RightLowerLeg) - R(HumanBones.RightFoot)).Length;
            }
        }

        /// <summary>
        ///     Feet first: how far the hips must come down so both legs can reach their foot targets. The base solver binds the
        ///     hip tracker to the avatar's rest hips, where the legs are fully straight, so any frame where the hip
        ///     tracker is a few millimetres further from a foot than at calibration (tracker slip, rotation, standing
        ///     taller) would otherwise leave that foot short of its tracker.
        /// </summary>
        float FeetDrop(Pose hipsTracker, double t)
        {
            if (!_bHips.Ok) return 0;
            var hipsPos = hipsTracker.pos + hipsTracker.rot * _bHips.Pos;
            var hipsYawRot = hipsTracker.rot * _bHips.Rot; // rest frame -> now
            float drop = 0;
            foreach (bool left in new[] { true, false })
            {
                var bind = left ? _bFootL : _bFootR;
                float len = left ? _legLenL : _legLenR;
                if (!bind.Ok || len <= 0) continue;
                if (!Tracker(left ? BodyRole.LeftFoot : BodyRole.RightFoot, t, out var ft)) continue;
                var foot = ft.pos + ft.rot * bind.Pos;
                var joint = hipsPos + hipsYawRot * (left ? _legL : _legR);
                var d = joint - foot;
                float reach = len * 0.995f; // keep a hint of knee bend so the knee direction stays stable
                float horiz2 = d.x * d.x + d.z * d.z;
                if (horiz2 >= reach * reach) continue; // out of reach sideways: lowering the hips can't fix it
                float need = d.y - (float)Math.Sqrt(reach * reach - horiz2);
                if (need > drop) drop = need;
            }

            return Math.Min(drop, 0.12f);
        }

        /// <summary>
        ///     Keeps the feet on their trackers: when the headset is higher than the avatar can reach standing on its
        ///     feet (e.g. headset pushed up on the forehead, standing taller than at calibration), the head target stops
        ///     at full standing height instead of lifting the whole body off the foot trackers.
        /// </summary>
        void KeepFeetPlanted(double t, ref Pose head)
        {
            if (!_cal.KeepFeetPlanted || !_cal.UseFeet) return;
            float lift = float.MaxValue;
            if (!float.IsNaN(_calFootL) && Tracker(BodyRole.LeftFoot, t, out var lp))
                lift = Math.Min(lift, lp.pos.y - _calFootL);
            if (!float.IsNaN(_calFootR) && Tracker(BodyRole.RightFoot, t, out var rp))
                lift = Math.Min(lift, rp.pos.y - _calFootR);
            if (lift == float.MaxValue) return;
            float max = (float.IsNaN(_calHeadY) ? AvatarEyeHeight : _calHeadY) + lift +
                        0.005f; // standing height at calibration
            if (head.pos.y > max) head.pos = new Vec3(head.pos.x, max, head.pos.z);
        }

        public static Vec3 DefaultWrist(bool left)
        {
            return left ? IndexWristLeft : new Vec3(-IndexWristLeft.x, IndexWristLeft.y, IndexWristLeft.z);
        }

        public static Quat DefaultWristRot(bool left)
        {
            return left
                ? IndexWristRotLeft
                : new Quat(IndexWristRotLeft.x, -IndexWristRotLeft.y, -IndexWristRotLeft.z, IndexWristRotLeft.w);
        }

        /// <summary>
        ///     The controller -> wrist offset per hand, fixed for the whole take like VRChat's per-device hand offsets:
        ///     the median of SteamVR's skeletal wrist over the take, else the calibration snapshot's, else Index defaults.
        /// </summary>
        void ResolveWrists(CalibrationSnapshot snap)
        {
            for (int s = 0; s < 2; s++)
            {
                bool left = s == 0;
                var h = _rec.Hands[s];
                if (h.Count >= 10)
                {
                    int n = h.Count;
                    float[] xs = new float[n];
                    float[] ys = new float[n];
                    float[] zs = new float[n];
                    var acc = new Quat(0, 0, 0, 0);
                    var q0 = HandStream.Rot(h.Bones[n / 2], 1);
                    for (int i = 0; i < n; i++)
                    {
                        var p = HandStream.Pos(h.Bones[i], 1);
                        xs[i] = p.x;
                        ys[i] = p.y;
                        zs[i] = p.z;
                        var q = HandStream.Rot(h.Bones[i], 1);
                        if (q.x * q0.x + q.y * q0.y + q.z * q0.z + q.w * q0.w < 0) q = new Quat(-q.x, -q.y, -q.z, -q.w);
                        acc = new Quat(acc.x + q.x, acc.y + q.y, acc.z + q.z, acc.w + q.w);
                    }

                    Array.Sort(xs);
                    Array.Sort(ys);
                    Array.Sort(zs);
                    _wristPos[s] = new Vec3(xs[n / 2], ys[n / 2], zs[n / 2]);
                    _wristRot[s] = acc.Normalized;
                    WristSource[s] = $"SteamVR skeleton ({n} samples)";
                }
                else if (snap != null && snap.TryWrist(left, out var sp, out var sq))
                {
                    _wristPos[s] = sp;
                    _wristRot[s] = sq;
                    WristSource[s] = "calibration snapshot";
                }
                else
                {
                    _wristPos[s] = DefaultWrist(left);
                    _wristRot[s] = DefaultWristRot(left);
                    WristSource[s] = "Index default (no skeletal data in this take)";
                }
            }
        }

        public void Feed(IBodySolver solver, double t)
        {
            bool hok = Head(t, out var h);
            if (hok) h = SolverEye(h);
            // What VRChat does with a leg that can't reach its foot tracker depends on the spine mode (measured against its
            // own IK, HUMR test takes):
            //  * Lock head: the head stays on the headset and the hips come down (up to 3 cm) so the feet stay planted;
            //  * Lock hips: the hips stay on the tracker (p95 0.9 cm) and the feet lift off their trackers (p95 2-3 cm);
            //  * Lock both: the hips stay within ~1 cm and the feet lift a little (p95 0.7-1.8 cm).
            // So the hips are only lowered with Lock head; the head clamp (standing taller than at calibration) only
            // matters where the hips can follow the head.
            bool headLocked = _cal.Lock == CalLock.Head;
            if (hok && _cal.Lock == CalLock.Both) KeepFeetPlanted(t, ref h);
            // feet first (Lock head): lower the hips just enough for both legs to reach their trackers
            float drop = 0;
            var hipsTr = Pose.Identity;
            bool hipsOk = _feetFirst && headLocked && HasTracker(BodyRole.Hips) &&
                          Tracker(BodyRole.Hips, t, out hipsTr);
            if (hipsOk)
            {
                drop = FeetDrop(hipsTr, t);
                if (drop > MaxFeetDrop) MaxFeetDrop = drop;
                SumFeetDrop += drop;
                FeetDropFrames++;
                if (drop > 0.002f) FeetDropActive++;
            }

            if (hok && drop > 0 && !headLocked) h.pos = new Vec3(h.pos.x, h.pos.y - drop, h.pos.z);
            solver.SetDevice(BodyRole.Head, h, hok, HandSourceKind.Unknown);
            foreach (bool left in new[] { true, false })
            {
                bool ok = Hand(left, t, out var p, out var kind);
                // controller-driven hands: a fixed controller -> wrist offset (SteamVR's skeletal wrist), so the hand
                // is rigidly attached to its controller like in VRChat
                if (kind != HandSourceKind.VrcWrist)
                {
                    if (FixedWrists) solver.SetControllerWrist(left, _wristPos[left ? 0 : 1], _wristRot[left ? 0 : 1]);
                    else if (_rec.Hands[left ? 0 : 1].SampleWrist(t, out var wp, out var wr))
                        solver.SetControllerWrist(left, wp, wr); // old per-frame behaviour
                }

                solver.SetDevice(left ? BodyRole.LeftHand : BodyRole.RightHand, p, ok, kind);
            }

            foreach (var role in TrackerRoles)
            {
                var p = Pose.Identity;
                bool ok = _cal.UsesRole(role) && Tracker(role, t, out p);
                if (ok && role == BodyRole.Hips && drop > 0) p.pos = new Vec3(p.pos.x, p.pos.y - drop, p.pos.z);
                solver.SetDevice(role, p, ok, HandSourceKind.Generic);
            }
        }

        /// <summary>Feeds every device at the calibration moment (snapshot poses, or the take at CalibrationTime).</summary>
        void FeedCalibration(IBodySolver solver)
        {
            if (Snapshot == null)
            {
                Feed(solver, CalibrationTime);
                foreach (var role in TrackerRoles)
                    if (_nudging && _nudges.ContainsKey(role.ToString()) && _cal.UsesRole(role) &&
                        Tracker(role, CalibrationTime, out var tp))
                        solver.SetDevice(role, Nudged(role, tp), true, HandSourceKind.Generic);
                return;
            }

            bool hok = CalHead(out var h);
            if (hok) h = SolverEye(h);
            solver.SetDevice(BodyRole.Head, h, hok, HandSourceKind.Unknown);
            foreach (bool left in new[] { true, false })
            {
                bool ok = CalOvr(left ? _ctrlL : _ctrlR, out var p) &&
                          !Off(left ? BodyRole.LeftHand : BodyRole.RightHand);
                if (ok) HandFix(left, ref p);
                solver.SetControllerWrist(left, _wristPos[left ? 0 : 1], _wristRot[left ? 0 : 1]);
                solver.SetDevice(left ? BodyRole.LeftHand : BodyRole.RightHand, p, ok, left ? _kindL : _kindR);
            }

            foreach (var role in TrackerRoles)
            {
                var p = Pose.Identity;
                bool ok = _cal.UsesRole(role) && !Off(role) && _trackers.TryGetValue(role, out var s) &&
                          CalOvr(s, out p);
                solver.SetDevice(role, p, ok, HandSourceKind.Generic);
            }
        }

        /// <summary>
        ///     Feeds the calibration moment with the head made upright (and, unless HangFromHeadset, standing on the floor),
        ///     then calibrates (VRChat-style FBT calibration).
        /// </summary>
        public void Calibrate(IBodySolver solver)
        {
            _nudging = false; // the standing foot heights are the real ones
            _calFootL = _calFootR = float.NaN;
            if (_trackers.TryGetValue(BodyRole.LeftFoot, out var ls) && CalOvr(ls, out var lp)) _calFootL = lp.pos.y;
            if (_trackers.TryGetValue(BodyRole.RightFoot, out var rs) && CalOvr(rs, out var rp)) _calFootR = rp.pos.y;
            _feetFirst = false; // bind at the raw tracker poses
            _calHeadY = float.NaN;
            _calRootOk = false;
            bool hasHead = CalHead(out var h);
            _nudgeYaw = hasHead ? Quat.YawRotation(CalibrationHead(SolverEye(h)).rot.Yaw) : Quat.Identity;
            _nudges = _rec.Edits.NudgesFor(CalibrationKey);
            _nudging = hasHead && _nudges != null;
            FeedCalibration(solver);
            if (hasHead)
            {
                var ch = CalibrationHead(SolverEye(h));
                _calHeadY = ch.pos.y;
                solver.SetDevice(BodyRole.Head, ch, true, HandSourceKind.Unknown);
                BindTargets(ch);
            }

            solver.Calibrate();
            _feetFirst = _cal.KeepFeetPlanted && _cal.UseFeet;
            MaxFeetDrop = SumFeetDrop = 0;
            FeetDropFrames = FeetDropActive = 0;
        }

        /// <summary>
        ///     The calibration moment as it was bound: the avatar's T-pose root and every device then, in the clip's space
        ///     before placement. Null before <see cref="Calibrate" /> or without a head.
        /// </summary>
        public CalibrationPose CalibrationPose()
        {
            if (!_calRootOk) return null;
            var universe = Snapshot == null ? UniverseAt(CalibrationTime) : Pose.Identity;
            var res = new CalibrationPose
            {
                Key = CalibrationKey,
                Root = universe * _calRoot,
                Scale = AvatarEyeHeight / Math.Max(0.05f, _prof.EyeHeight)
            };
            if (CalHead(out var head)) res.Devices["Head"] = universe * head;
            if (CalOvr(_ctrlL, out var l)) res.Devices["LeftHand"] = universe * l;
            if (CalOvr(_ctrlR, out var r)) res.Devices["RightHand"] = universe * r;
            foreach (var kv in _trackers)
                if (CalOvr(kv.Value, out var p))
                {
                    res.Devices[kv.Key.ToString()] = universe * p;
                    if (!HasTracker(kv.Key)) res.Unused.Add(kv.Key.ToString());
                }

            return res;
        }

        public Dictionary<string, Tuple<Pose[], bool[]>> TrackerTracks(double[] times)
        {
            var res = new Dictionary<string, Tuple<Pose[], bool[]>>();

            void Add(string name, Func<double, Tuple<bool, Pose>> f)
            {
                var p = new Pose[times.Length];
                bool[] v = new bool[times.Length];
                for (int i = 0; i < times.Length; i++)
                {
                    var s = f(times[i]);
                    v[i] = s.Item1;
                    p[i] = s.Item2;
                }

                res[name] = Tuple.Create(p, v);
            }

            // true space (with the playspace jumps), like the solved body once BakeIK puts the jumps back
            Add("Head", t =>
            {
                bool ok = Head(t, out var p);
                return Tuple.Create(ok, UniverseAt(t) * p);
            });
            Add("LeftHand", t =>
            {
                bool ok = Hand(true, t, out var p, out _);
                return Tuple.Create(ok, UniverseAt(t) * p);
            });
            Add("RightHand", t =>
            {
                bool ok = Hand(false, t, out var p, out _);
                return Tuple.Create(ok, UniverseAt(t) * p);
            });
            // the devices as fed (scene editor edits included); a device switched off is still shown where it is
            foreach (var kv in _trackers)
            {
                var role = kv.Key;
                Add(role.ToString(), t =>
                {
                    bool ok = Tracker(role, t, out var p, true);
                    return Tuple.Create(ok, UniverseAt(t) * p);
                });
            }

            return res;
        }

        // the solver's bone targets, replicated (upstream CalculateOffset): bone = tracker * offset, bound at calibration
        struct Bind
        {
            public bool Ok;
            public Vec3 Pos;
            public Quat Rot;
        }
    }
}
