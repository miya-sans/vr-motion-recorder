using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VRMR
{
    public enum ParamType : byte { Float = 0, Int = 1, Bool = 2 }

    /// <summary>Event-based avatar parameter track (values as received from VRChat OSC).</summary>
    public sealed class ParamTrack
    {
        public readonly List<double> T = new();
        public readonly List<float> V = new();
        public bool Export = true;
        public string Name;
        public ParamType Type;

        public int Count => T.Count;

        public void Add(double t, float v)
        {
            int n = T.Count;
            if (n > 0 && t < T[n - 1]) t = T[n - 1];
            if (n > 0 && V[n - 1] == v && n > 1 && V[n - 2] == v)
            {
                T[n - 1] = t;
                return;
            } // collapse holds

            T.Add(t);
            V.Add(v);
        }

        int Find(double t)
        {
            int lo = 0, hi = T.Count - 1;
            if (hi < 0 || t < T[0]) return -1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (T[mid] <= t) lo = mid;
                else hi = mid - 1;
            }

            return lo;
        }

        /// <summary>Value at time t. Floats interpolate between close samples (VRChat applies them per frame), others step.</summary>
        public float Sample(double t, float def = 0f)
        {
            int i = Find(t);
            if (i < 0) return T.Count > 0 ? V[0] : def;
            if (Type != ParamType.Float || i >= T.Count - 1) return V[i];
            double t0 = T[i], t1 = T[i + 1];
            if (t1 - t0 > 0.2 || t1 <= t0) return V[i];
            float a = (float)((t - t0) / (t1 - t0));
            return V[i] + (V[i + 1] - V[i]) * a;
        }

        public float SampleStep(double t, float def = 0f)
        {
            int i = Find(t);
            if (i < 0) return T.Count > 0 ? V[0] : def;
            return V[i];
        }

        /// <summary>True if the parameter looks like one of VRCFT's binary-encoded sync bits (e.g. JawOpen4, JawOpenNegative).</summary>
        public bool IsBinaryBit(ICollection<string> allNames)
        {
            if (Type != ParamType.Bool) return false;
            string n = Name;
            if (n.EndsWith("Negative", StringComparison.Ordinal))
                return allNames.Contains(n.Substring(0, n.Length - 8));
            int k = n.Length;
            while (k > 0 && char.IsDigit(n[k - 1])) k--;
            if (k == n.Length || k == 0) return false;
            string num = n.Substring(k);
            if (!(num == "1" || num == "2" || num == "4" || num == "8" || num == "16" || num == "32" ||
                  num == "64")) return false;
            return allNames.Contains(n.Substring(0, k));
        }
    }

    public enum StreamKind : byte
    {
        OpenVRDevice = 0, // raw SteamVR device pose (converted to Unity space, standing universe)
        VrcHead = 1, // /tracking/vrsystem/head/pose
        VrcLeftWrist = 2,
        VrcRightWrist = 3,
        VrcCamera = 4 // /usercamera/Pose
    }

    /// <summary>A stream of timestamped rigid poses (one tracked device or VRChat pose output).</summary>
    public sealed class PoseStream
    {
        public readonly List<Vec3> P = new();
        public readonly List<Quat> R = new();
        public readonly List<double> T = new();
        public int Id;
        public Dictionary<string, object> Info = new();
        public StreamKind Kind;
        public string Name;

        public int Count => T.Count;
        public string Serial => Info.GetStr("serial", Name);
        public int DeviceClass => Info.GetInt("class"); // 1 HMD, 2 controller, 3 generic tracker, 4 base station
        public int ControllerRole => Info.GetInt("role"); // 1 left, 2 right
        public string ModelNumber => Info.GetStr("model", "");
        public string ControllerType => Info.GetStr("ctype", "");

        /// <summary>User/auto assigned body role (e.g. "Hips", "LeftFoot"); empty = unassigned.</summary>
        public string BodyRole { get => Info.GetStr("bodyRole", ""); set => Info["bodyRole"] = value ?? ""; }

        public double Rate
        {
            get
            {
                if (T.Count < 3) return 0;
                double span = T[T.Count - 1] - T[0];
                return span > 0 ? (T.Count - 1) / span : 0;
            }
        }

        public void Add(double t, Vec3 p, Quat r)
        {
            int n = T.Count;
            if (n > 0 && t <= T[n - 1]) t = T[n - 1] + 1e-6;
            T.Add(t);
            P.Add(p);
            R.Add(r);
        }

        /// <summary>Interpolated pose at time t. Returns false if no sample within maxGap seconds.</summary>
        public bool Sample(double t, out Vec3 pos, out Quat rot, double maxGap = 0.3)
        {
            pos = Vec3.Zero;
            rot = Quat.Identity;
            int n = T.Count;
            if (n == 0) return false;
            if (t <= T[0])
            {
                pos = P[0];
                rot = R[0];
                return T[0] - t < maxGap;
            }

            if (t >= T[n - 1])
            {
                pos = P[n - 1];
                rot = R[n - 1];
                return t - T[n - 1] < maxGap;
            }

            int lo = 0, hi = n - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (T[mid] <= t) lo = mid;
                else hi = mid;
            }

            double t0 = T[lo], t1 = T[hi];
            if (t1 - t0 > maxGap)
            {
                bool nearLo = t - t0 < t1 - t;
                pos = nearLo ? P[lo] : P[hi];
                rot = nearLo ? R[lo] : R[hi];
                return Math.Min(t - t0, t1 - t) < maxGap * 0.5;
            }

            float a = (float)((t - t0) / (t1 - t0));
            pos = Vec3.Lerp(P[lo], P[hi], a);
            rot = Quat.Slerp(R[lo], R[hi], a);
            return true;
        }
    }

    /// <summary>VRChat's own IK result (HUMR log frames): hips position + world rotation of all 55 humanoid bones.</summary>
    public sealed class HumrStream
    {
        public readonly List<Vec3> Hips = new();
        public readonly List<Quat[]> Rot = new();
        public readonly List<double> SrcT = new();
        public readonly List<double> T = new();
        public string Target = "";
        public int Count => T.Count;

        public void Add(double t, double srcT, Vec3 hips, Quat[] rot)
        {
            int n = T.Count;
            if (n > 0 && t <= T[n - 1]) t = T[n - 1] + 1e-6;
            T.Add(t);
            SrcT.Add(srcT);
            Hips.Add(hips);
            Rot.Add(rot);
        }

        public bool Sample(double t, out Vec3 hips, Quat[] rotOut)
        {
            hips = Vec3.Zero;
            int n = T.Count;
            if (n == 0) return false;
            int lo, hi;
            float a;
            if (t <= T[0])
            {
                lo = hi = 0;
                a = 0;
            }
            else if (t >= T[n - 1])
            {
                lo = hi = n - 1;
                a = 0;
            }
            else
            {
                lo = 0;
                hi = n - 1;
                while (hi - lo > 1)
                {
                    int mid = (lo + hi) >> 1;
                    if (T[mid] <= t) lo = mid;
                    else hi = mid;
                }

                a = (float)((t - T[lo]) / (T[hi] - T[lo]));
                if (T[hi] - T[lo] > 0.5)
                {
                    if (a > 0.5f) lo = hi;
                    a = 0;
                }
            }

            hips = Vec3.Lerp(Hips[lo], Hips[hi], a);
            var r0 = Rot[lo];
            var r1 = Rot[hi];
            for (int b = 0; b < HumanBones.Count; b++) rotOut[b] = Quat.Slerp(r0[b], r1[b], a);
            return Math.Abs(t - T[lo]) < 1.0 || Math.Abs(t - T[hi]) < 1.0;
        }
    }

    /// <summary>
    ///     Raw SteamVR Skeletal Input for one hand (Index finger tracking, or SteamVR's estimate on other controllers).
    ///     Summary: finger curl[5] (thumb..pinky) + splay[4], FromDevice. Bones: the 31-bone OpenVR hand skeleton in
    ///     parent space, motion range "with controller", raw OpenVR (right-handed) values: 31 rotations (x,y,z,w as
    ///     int16/32767)
    ///     followed by root and wrist positions (int16, 0.1 mm).
    /// </summary>
    public sealed class HandStream
    {
        public const int BoneCount = 31, PackedLength = BoneCount * 4 + 6;
        public readonly List<short[]> Bones = new();
        public readonly List<float[]> Summary = new();
        public readonly List<double> T = new();
        public int Side; // 0 left, 1 right
        public int Count => T.Count;

        public void Add(double t, float[] summary, short[] bones)
        {
            int n = T.Count;
            if (n > 0 && t <= T[n - 1]) t = T[n - 1] + 1e-6;
            T.Add(t);
            Summary.Add(summary);
            Bones.Add(bones);
        }

        int Find(double t, double maxGap, out float a)
        {
            a = 0;
            int n = T.Count;
            if (n == 0) return -1;
            if (t <= T[0]) return T[0] - t < maxGap ? 0 : -1;
            if (t >= T[n - 1]) return t - T[n - 1] < maxGap ? n - 1 : -1;
            int lo = 0, hi = n - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (T[mid] <= t) lo = mid;
                else hi = mid;
            }

            if (T[hi] - T[lo] > maxGap) return -1;
            a = (float)((t - T[lo]) / (T[hi] - T[lo]));
            return lo;
        }

        public bool SampleSummary(double t, float[] outSummary, double maxGap = 0.3)
        {
            int i = Find(t, maxGap, out float a);
            if (i < 0) return false;
            float[] s0 = Summary[i];
            float[] s1 = Summary[Math.Min(i + 1, Count - 1)];
            for (int k = 0; k < 9; k++) outSummary[k] = s0[k] + (s1[k] - s0[k]) * a;
            return true;
        }

        public static Quat Rot(short[] b, int bone)
        {
            return new Quat(b[bone * 4] / 32767f, b[bone * 4 + 1] / 32767f, b[bone * 4 + 2] / 32767f,
                b[bone * 4 + 3] / 32767f).Normalized;
        }

        public static Vec3 Pos(short[] b, int which)
        {
            int o = BoneCount * 4 + which * 3;
            return new Vec3(b[o] / 10000f, b[o + 1] / 10000f, b[o + 2] / 10000f);
        }

        /// <summary>Raw OpenVR wrist bone (index 1) in parent (controller) space at time t.</summary>
        public bool SampleWrist(double t, out Vec3 pos, out Quat rot, double maxGap = 0.3)
        {
            pos = Vec3.Zero;
            rot = Quat.Identity;
            int i = Find(t, maxGap, out float a);
            if (i < 0) return false;
            short[] b0 = Bones[i];
            short[] b1 = Bones[Math.Min(i + 1, Count - 1)];
            pos = Vec3.Lerp(Pos(b0, 1), Pos(b1, 1), a);
            rot = Quat.Slerp(Rot(b0, 1), Rot(b1, 1), a);
            return true;
        }

        public static short[] Pack(float[] rotXYZW, float[] rootPos, float[] wristPos)
        {
            return Pack(rotXYZW, rootPos, wristPos, new short[PackedLength]);
        }

        /// <summary>Packs into a caller-owned array of <see cref="PackedLength" /> items and returns it.</summary>
        public static short[] Pack(float[] rotXYZW, float[] rootPos, float[] wristPos, short[] s)
        {
            for (int k = 0; k < BoneCount * 4; k++) s[k] = (short)Math.Round(MathX.Clamp(rotXYZW[k], -1f, 1f) * 32767f);
            for (int k = 0; k < 3; k++)
            {
                s[BoneCount * 4 + k] =
                    (short)MathX.Clamp((int)Math.Round(rootPos[k] * 10000f), short.MinValue, short.MaxValue);
                s[BoneCount * 4 + 3 + k] =
                    (short)MathX.Clamp((int)Math.Round(wristPos[k] * 10000f), short.MinValue, short.MaxValue);
            }

            return s;
        }
    }

    /// <summary>
    ///     A VRChat IK setting change at a moment of a take: spine mode (VRC_IK_FBT_SPINE_MODE 0 Lock Hip, 1 Lock Head, 2
    ///     Lock Both) and/or avatar measurement (VRC_IK_AVATAR_MEASUREMENT_TYPE 0 arm span, 1 height); -1 = unchanged.
    /// </summary>
    public sealed class VrcChange
    {
        public int Spine = -1, Measure = -1;
        public double T;

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object> { ["t"] = T, ["spine"] = Spine, ["measure"] = Measure };
        }

        public static VrcChange FromJson(Dictionary<string, object> d)
        {
            return new VrcChange
                { T = d.GetNum("t"), Spine = d.GetInt("spine", -1), Measure = d.GetInt("measure", -1) };
        }
    }

    /// <summary>
    ///     A tracker stretch marked by hand in the Clip tab: repair it as lost tracking, or keep a detected loss as
    ///     recorded (Keep).
    /// </summary>
    public sealed class TrackerGapMark
    {
        public double A, B;
        public bool Keep;
        public string Serial = "";

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object> { ["serial"] = Serial, ["a"] = A, ["b"] = B, ["keep"] = Keep };
        }

        public static TrackerGapMark FromJson(Dictionary<string, object> d)
        {
            return new TrackerGapMark
            {
                Serial = d.GetStr("serial", ""), A = d.GetNum("a"), B = d.GetNum("b"), Keep = d.GetBool("keep")
            };
        }
    }

    /// <summary>
    ///     The SteamVR room when a take was recorded (Extra "steamvrRoom"): the play area's floor corners and the base
    ///     stations,
    ///     in raw SteamVR standing space like the devices' poses, captured at T (take seconds). Takes recorded before
    ///     2026-11-17 don't have it. Replays map it like the devices (BakedClip.Room).
    /// </summary>
    public sealed class SteamVRRoom
    {
        public const string Key = "steamvrRoom";
        public readonly List<Station> BaseStations = new();
        public Vec3[] PlayArea;
        public double T;
        public bool Empty => PlayArea == null && BaseStations.Count == 0;

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object> { ["t"] = T };
            if (PlayArea != null) d["playArea"] = PlayArea.Select(p => (object)Json.V3(p)).ToList();
            if (BaseStations.Count > 0)
                d["baseStations"] = BaseStations.Select(s => (object)new Dictionary<string, object>
                {
                    ["serial"] = s.Serial ?? "", ["pos"] = Json.V3(s.Pose.pos), ["rot"] = Json.Q(s.Pose.rot),
                    ["fov"] = new List<object> { (double)s.FovL, (double)s.FovR, (double)s.FovT, (double)s.FovB }
                }).ToList();
            return d;
        }

        /// <summary>The take's room, or null (not recorded / unreadable).</summary>
        public static SteamVRRoom From(Recording rec)
        {
            if (rec == null || !rec.Extra.TryGetValue(Key, out object o) ||
                !(o is Dictionary<string, object> d)) return null;
            try
            {
                var r = new SteamVRRoom { T = d.GetNum("t") };
                if (d.TryGetValue("playArea", out object pa) && pa is List<object> pl && pl.Count == 4)
                    r.PlayArea = pl.Select(x => Json.ToV3(x)).ToArray();
                if (d.TryGetValue("baseStations", out object bs) && bs is List<object> bl)
                    foreach (var x in bl.OfType<Dictionary<string, object>>())
                    {
                        var q = Json.ToQ(x.TryGetValue("rot", out object ro) ? ro : null);
                        var st = new Station
                        {
                            Serial = x.GetStr("serial", ""),
                            Pose = new Pose(Json.ToV3(x.TryGetValue("pos", out object po) ? po : null),
                                q.Norm > 0.5f ? q.Normalized : Quat.Identity)
                        };
                        if (x.TryGetValue("fov", out object fo) && fo is List<object> fl && fl.Count == 4)
                        {
                            st.FovL = Convert.ToSingle(fl[0]);
                            st.FovR = Convert.ToSingle(fl[1]);
                            st.FovT = Convert.ToSingle(fl[2]);
                            st.FovB = Convert.ToSingle(fl[3]);
                        }

                        r.BaseStations.Add(st);
                    }

                return r.Empty ? null : r;
            }
            catch { return null; }
        }

        public struct Station
        {
            public string Serial;
            public Pose Pose;
            public float FovL, FovR, FovT, FovB;
        }
    }

    public sealed class RecEvent
    {
        public double T;
        public string Text;
        public string Type;
    }

    /// <summary>Non-destructive edits: trim, removed ranges, face sync offset, origin, calibration frame.</summary>
    public sealed class EditList
    {
        public const double UseSnapshot = -2;

        /// <summary>
        ///     Follow VRChat: the calibration in effect when the take started (the linked snapshot, if it is older than the take),
        ///     then every VRChat calibrate-and-confirm inside the take from its "Calibrate FBT" click on.
        /// </summary>
        public const double UseVrcTimeline = -3;

        public string BodySource = "auto"; // auto | humr | ik

        // after-the-fact recalibration (IK solve): null = use the global calibration defaults
        public CalibrationSettings Calib;

        public double
            CalibrationTime =
                -1; // -1 = auto (T-pose detection, else start); -2 = the linked calibration snapshot; -3 = VRChat's calibrations as they happened

        public List<double[]> Cuts = new();
        public double FaceOffset; // seconds added to parameter timestamps
        public string FingerSource = "auto"; // auto | steamvr | gestures | none

        /// <summary>Keep VRChat's jumps (the avatar leaving the ground in VRChat's world) in the body; off by default (VrcJumps).</summary>
        public bool IncludeJumps;

        public bool IncludeLocomotion = true;
        public double OriginTime; // the world is centred on the user at this source time
        public float OriginYawOffset, OriginOffsetX, OriginOffsetZ;

        /// <summary>
        ///     Where the solved clip is placed: a rigid move + turn of the whole body, camera and devices, applied after
        ///     the solve (Clip tab: Placement, the 3D view's placement gizmo). Identity = as solved.
        /// </summary>
        public Vec3 PlacePos = Vec3.Zero;

        public Quat PlaceRot = Quat.Identity;

        /// <summary>Copy of the calibration snapshot this take is linked to (library or VRChat calibration), or null.</summary>
        public CalibrationSnapshot Snapshot;

        /// <summary>
        ///     Avatar library entry the take is solved onto ("" = the avatar it was recorded with). Any humanoid works: the
        ///     raw data is solved anew.
        /// </summary>
        public string TargetAvatar = "";

        /// <summary>Tracking-loss marks added or switched off by hand (TrackerGaps).</summary>
        public List<TrackerGapMark> TrackerGaps = new();

        public double TrimIn, TrimOut = -1; // source seconds; TrimOut < 0 = end of recording

        /// <summary>
        ///     VRChat IK setting changes added by hand (take seconds), for takes recorded without the settings log: spine
        ///     mode / measurement.
        /// </summary>
        public List<VrcChange> VrcChanges = new();

        public Pose Placement
        {
            get => new(PlacePos, PlaceRot);
            set
            {
                PlacePos = value.pos;
                PlaceRot = value.rot.Normalized;
            }
        }

        public bool HasPlacement => PlacePos.SqrLength > 1e-12f || Math.Abs(PlaceRot.w) < 1 - 1e-7f;

        public List<double[]> Segments(double duration)
        {
            double a = Math.Max(0, TrimIn), b = TrimOut < 0 ? duration : Math.Min(duration, TrimOut);
            var segs = new List<double[]>();
            if (b <= a) return segs;
            segs.Add(new[] { a, b });
            foreach (double[] c in Cuts.OrderBy(c => c[0]))
            {
                var next = new List<double[]>();
                foreach (double[] s in segs)
                {
                    if (c[1] <= s[0] || c[0] >= s[1])
                    {
                        next.Add(s);
                        continue;
                    }

                    if (c[0] > s[0]) next.Add(new[] { s[0], c[0] });
                    if (c[1] < s[1]) next.Add(new[] { c[1], s[1] });
                }

                segs = next;
            }

            return segs.Where(s => s[1] - s[0] > 1e-4).ToList();
        }

        public double EditedDuration(double duration)
        {
            return Segments(duration).Sum(s => s[1] - s[0]);
        }

        public double EditedToSource(double te, double duration)
        {
            var segs = Segments(duration);
            if (segs.Count == 0) return 0;
            foreach (double[] s in segs)
            {
                double len = s[1] - s[0];
                if (te <= len) return s[0] + Math.Max(0, te);
                te -= len;
            }

            return segs[segs.Count - 1][1];
        }

        public double SourceToEdited(double ts, double duration)
        {
            double acc = 0;
            foreach (double[] s in Segments(duration))
            {
                if (ts < s[0]) return acc;
                if (ts <= s[1]) return acc + (ts - s[0]);
                acc += s[1] - s[0];
            }

            return acc;
        }

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                ["trimIn"] = TrimIn, ["trimOut"] = TrimOut,
                ["cuts"] = Cuts.Select(c => (object)new List<object> { c[0], c[1] }).ToList(),
                ["faceOffset"] = FaceOffset, ["originTime"] = OriginTime,
                ["originYaw"] = (double)OriginYawOffset, ["originX"] = (double)OriginOffsetX,
                ["originZ"] = (double)OriginOffsetZ,
                ["placePos"] = Json.V3(PlacePos), ["placeRot"] = Json.Q(PlaceRot),
                ["calibrationTime"] = CalibrationTime, ["bodySource"] = BodySource, ["locomotion"] = IncludeLocomotion,
                ["jumps"] = IncludeJumps,
                ["calibration"] = Calib?.ToJson(),
                ["calibrationSnapshot"] = Snapshot?.ToJson(),
                ["fingerSource"] = FingerSource, ["targetAvatar"] = TargetAvatar,
                ["vrcChanges"] = VrcChanges.Select(c => (object)c.ToJson()).ToList(),
                ["trackerGaps"] = TrackerGaps.Select(c => (object)c.ToJson()).ToList()
            };
        }

        public static EditList FromJson(Dictionary<string, object> d)
        {
            var e = new EditList();
            if (d == null) return e;
            e.TrimIn = d.GetNum("trimIn");
            e.TrimOut = d.GetNum("trimOut", -1);
            foreach (object c in d.GetList("cuts") ?? new List<object>())
                if (c is List<object> l && l.Count == 2)
                    e.Cuts.Add(new[] { (double)l[0], (double)l[1] });
            e.FaceOffset = d.GetNum("faceOffset");
            e.OriginTime = d.GetNum("originTime");
            e.OriginYawOffset = d.GetF("originYaw");
            e.OriginOffsetX = d.GetF("originX");
            e.OriginOffsetZ = d.GetF("originZ");
            if (d.ContainsKey("placePos")) e.PlacePos = Json.ToV3(d["placePos"]);
            if (d.ContainsKey("placeRot"))
            {
                var q = Json.ToQ(d["placeRot"]);
                e.PlaceRot = q.Norm > 0.5f ? q.Normalized : Quat.Identity;
            }

            e.CalibrationTime = d.GetNum("calibrationTime", -1);
            e.BodySource = d.GetStr("bodySource", "auto");
            e.IncludeLocomotion = d.GetBool("locomotion", true);
            e.IncludeJumps = d.GetBool("jumps");
            var cal = d.GetObj("calibration");
            if (cal != null) e.Calib = CalibrationSettings.FromJson(cal);
            else
            {
                // files from before calibration settings: keep their per-clip overrides
                float eye = d.GetF("realEyeHeight"), scale = d.GetF("avatarScale");
                var fix = Json.ToV3(d.ContainsKey("handRotFix") ? d["handRotFix"] : null);
                if (eye > 0.3f || scale > 0.01f || fix.SqrLength > 1e-6f)
                    e.Calib = new CalibrationSettings
                    {
                        Height = eye > 0.3f ? CalHeightMode.EyeHeight : CalHeightMode.Measure,
                        HeightValue = eye > 0.3f ? eye : 0, AvatarScale = scale, HandRotFix = fix
                    };
            }

            e.FingerSource = d.GetStr("fingerSource", "auto");
            e.TargetAvatar = d.GetStr("targetAvatar", "");
            e.Snapshot = CalibrationSnapshot.FromJson(d.GetObj("calibrationSnapshot"));
            if (e.CalibrationTime == UseSnapshot && e.Snapshot == null) e.CalibrationTime = -1;
            foreach (object c in d.GetList("vrcChanges") ?? new List<object>())
                if (c is Dictionary<string, object> cd)
                    e.VrcChanges.Add(VrcChange.FromJson(cd));
            foreach (object c in d.GetList("trackerGaps") ?? new List<object>())
                if (c is Dictionary<string, object> cd)
                    e.TrackerGaps.Add(TrackerGapMark.FromJson(cd));
            return e;
        }
    }

    public sealed class Recording
    {
        public readonly List<RecEvent> Events = new();
        public readonly HandStream[] Hands = { new() { Side = 0 }, new() { Side = 1 } };
        public readonly Dictionary<string, ParamTrack> ParamByName = new();

        public readonly List<ParamTrack> Params = new();
        public readonly List<PoseStream> Streams = new();

        public readonly object Sync = new();
        public float AvatarEyeHeight; // from /avatar/eyeheight (meters)
        public string AvatarId = "", AvatarName = "", WorldName = "", LocalUser = "";
        public DateTime CreatedUtc = DateTime.UtcNow;
        public double Duration;
        public EditList Edits = new();
        public Dictionary<string, object> Extra = new();

        /// <summary>The tolerance <see cref="FaceKeys" /> reduced the face parameters with; 0 when they are as received.</summary>
        public double FaceKeyTolerance;

        public string FilePath;
        public HumrStream Humr = new();
        public string Name = "Recording";
        public string Notes = "";
        public AvatarProfile Profile;

        /// <summary>Chunks <see cref="RecordingIO.Load" /> skipped because they were damaged; not saved.</summary>
        public int SkippedChunks;

        public float UserEyeHeight; // measured HMD height at calibration (meters, real world)

        List<object> _pendingExportOff;

        public IEnumerable<PoseStream> Devices => Streams.Where(s => s.Kind == StreamKind.OpenVRDevice);

        public double EditedDuration => Edits.EditedDuration(Duration);

        public bool HasHumr => Humr.Count > 2;
        public bool HasFingers => Hands[0].Count > 2 || Hands[1].Count > 2;
        public bool HasOpenVR => Devices.Any(s => s.Count > 2);
        public bool HasVrcPoses => (Stream(StreamKind.VrcHead)?.Count ?? 0) > 2;

        /// <summary>
        ///     A second view of this take with another edit list: the recorded data (streams, parameters, events) and
        ///     <see cref="Sync" /> are shared and must not be changed through the copy; the edit list is the copy's own. For
        ///     solving a take with other edits without touching the take others are working on.
        /// </summary>
        public Recording WithEdits(EditList edits)
        {
            var copy = (Recording)MemberwiseClone();
            copy.Edits = edits ?? throw new ArgumentNullException(nameof(edits));
            return copy;
        }

        public ParamTrack GetOrAddParam(string name, ParamType type)
        {
            if (ParamByName.TryGetValue(name, out var p)) return p;
            p = new ParamTrack { Name = name, Type = type };
            Params.Add(p);
            ParamByName[name] = p;
            return p;
        }

        public ParamTrack Param(string name)
        {
            return ParamByName.TryGetValue(name, out var p) ? p : null;
        }

        public PoseStream Stream(StreamKind kind, string name = null)
        {
            foreach (var s in Streams)
                if (s.Kind == kind && (name == null || s.Name == name))
                    return s;
            return null;
        }

        public PoseStream AddStream(StreamKind kind, string name, Dictionary<string, object> info = null)
        {
            var s = new PoseStream { Id = Streams.Count, Kind = kind, Name = name };
            if (info != null) s.Info = info;
            Streams.Add(s);
            return s;
        }

        public void RecomputeDuration()
        {
            double d = 0;
            foreach (var p in Params)
                if (p.Count > 0)
                    d = Math.Max(d, p.T[p.Count - 1]);
            foreach (var s in Streams)
                if (s.Count > 0)
                    d = Math.Max(d, s.T[s.Count - 1]);
            if (Humr.Count > 0) d = Math.Max(d, Humr.T[Humr.Count - 1]);
            foreach (var h in Hands)
                if (h.Count > 0)
                    d = Math.Max(d, h.T[h.Count - 1]);
            Duration = Math.Max(Duration, d);
        }

        public Dictionary<string, object> MetaJson()
        {
            return new Dictionary<string, object>
            {
                ["app"] = "VR Motion Recorder", ["formatVersion"] = (int)RecordingIO.Version,
                ["name"] = Name, ["created"] = CreatedUtc.ToString("o"), ["duration"] = Duration,
                ["avatarId"] = AvatarId, ["avatarName"] = AvatarName, ["world"] = WorldName, ["user"] = LocalUser,
                ["avatarEyeHeight"] = (double)AvatarEyeHeight, ["userEyeHeight"] = (double)UserEyeHeight,
                ["notes"] = Notes, ["edits"] = Edits.ToJson(), ["extra"] = Extra,
                ["paramExport"] = Params.Where(p => !p.Export).Select(p => (object)p.Name).ToList(),
                ["humrTarget"] = Humr.Target, ["faceKeys"] = FaceKeyTolerance
            };
        }

        public void ApplyMeta(Dictionary<string, object> m)
        {
            Name = m.GetStr("name", Name);
            if (DateTime.TryParse(m.GetStr("created", ""), null, DateTimeStyles.RoundtripKind, out var dt))
                CreatedUtc = dt;
            Duration = Math.Max(Duration, m.GetNum("duration"));
            AvatarId = m.GetStr("avatarId", "");
            AvatarName = m.GetStr("avatarName", "");
            WorldName = m.GetStr("world", "");
            LocalUser = m.GetStr("user", "");
            AvatarEyeHeight = m.GetF("avatarEyeHeight");
            UserEyeHeight = m.GetF("userEyeHeight");
            Notes = m.GetStr("notes", "");
            Edits = EditList.FromJson(m.GetObj("edits"));
            Extra = m.GetObj("extra") ?? new Dictionary<string, object>();
            Humr.Target = m.GetStr("humrTarget", "");
            FaceKeyTolerance = m.GetNum("faceKeys");
            var off = m.GetList("paramExport");
            if (off != null)
                foreach (object o in off)
                    if (o is string s && ParamByName.TryGetValue(s, out var p))
                        p.Export = false;
            _pendingExportOff = off;
        }

        internal void ReapplyExportFlags()
        {
            if (_pendingExportOff == null) return;
            foreach (object o in _pendingExportOff)
                if (o is string s && ParamByName.TryGetValue(s, out var p))
                    p.Export = false;
        }
    }
}
