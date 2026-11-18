using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace VRMR
{
    /// <summary>
    ///     One full-body calibration: the raw SteamVR poses of every device at the moment the user calibrated (VRChat's
    ///     calibrate-and-confirm, a manual capture, or a moment of a take), plus the context needed to reuse it for other
    ///     takes: tracker serials and roles, user height, avatar, SteamVR session. Raw poses are SteamVR playspace in
    ///     Unity convention (standing universe, floor at y = 0, metres), exactly like the recorded device streams.
    /// </summary>
    public sealed class CalibrationSnapshot
    {
        public readonly bool[] HasWrist = new bool[2];

        /// <summary>SteamVR skeletal wrist (bone 1, raw OpenVR, parent space) per hand, if finger tracking was live.</summary>
        public readonly Vec3[] WristPos = new Vec3[2];

        public readonly Quat[] WristRot = { Quat.Identity, Quat.Identity };
        public float AvatarEyeHeight;
        public string AvatarId = "", AvatarName = "";
        public DateTime CreatedUtc = DateTime.UtcNow;

        /// <summary>How the moment was found (trigger, log line, TrackingType...).</summary>
        public string Detail = "";

        public List<Device> Devices = new();

        /// <summary>Recording this snapshot was taken from (Source = take), else empty.</summary>
        public string FromTake = "";

        public double FromTakeTime = -1;

        public string Id = "";
        public string Name = "";

        /// <summary>vrchat (VRChat's calibrate-and-confirm), manual (Capture calibration now), take (a moment of a take).</summary>
        public string Source = "manual";

        /// <summary>vrserver.exe start time: the same value means the same SteamVR session (same playspace).</summary>
        public string SteamVRSession = "";

        /// <summary>VRChat TrackingType after the calibration (6 = full body, 4 = hip, 3 = head and hands).</summary>
        public int TrackingType = -1;

        public VrcSettings.Values Vrc = new();

        public Device Hmd => Devices.FirstOrDefault(d => d.Class == 1);

        public IEnumerable<Device> Trackers =>
            Devices.Where(d => d.Class == 3 || (d.Class == 2 && d.Role != 1 && d.Role != 2));

        /// <summary>Short label for lists: "VRChat calibration 16 Nov 05:11:50 (6-point)".</summary>
        public string Label
        {
            get
            {
                var local = CreatedUtc.ToLocalTime();
                string what = !string.IsNullOrEmpty(Name) ? Name :
                    Source == "vrchat" ? "VRChat calibration" :
                    Source == "take" ? "Take calibration" : "Captured calibration";
                int n = Trackers.Count();
                return $"{what} {local:dd MMM HH:mm:ss} ({n} tracker{(n == 1 ? "" : "s")})";
            }
        }

        /// <summary>Serials of the body trackers (not HMD/controllers), sorted.</summary>
        public List<string> TrackerSerials =>
            Trackers.Select(t => t.Serial).OrderBy(x => x, StringComparer.Ordinal).ToList();

        /// <summary>Snapshot ids are the local creation time, to the millisecond.</summary>
        public static string NewId(DateTime utc)
        {
            return utc.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture);
        }

        public Device Controller(bool left)
        {
            return Devices.FirstOrDefault(d => d.Class == 2 && d.Role == (left ? 1 : 2));
        }

        public Device Find(string serial)
        {
            return string.IsNullOrEmpty(serial) ? null : Devices.FirstOrDefault(d => d.Serial == serial);
        }

        public bool TryWrist(bool left, out Vec3 pos, out Quat rot)
        {
            int s = left ? 0 : 1;
            pos = WristPos[s];
            rot = WristRot[s];
            return HasWrist[s];
        }

        public Dictionary<string, object> ToJson()
        {
            var wr = new List<object>();
            for (int s = 0; s < 2; s++)
                wr.Add(HasWrist[s]
                    ? (object)new Dictionary<string, object>
                        { ["pos"] = Json.V3(WristPos[s]), ["rot"] = Json.Q(WristRot[s]) }
                    : null);
            return new Dictionary<string, object>
            {
                ["app"] = "VR Motion Recorder", ["kind"] = "calibrationSnapshot", ["version"] = 1,
                ["id"] = Id, ["name"] = Name, ["created"] = CreatedUtc.ToString("o"), ["source"] = Source,
                ["detail"] = Detail,
                ["steamvrSession"] = SteamVRSession, ["avatarId"] = AvatarId, ["avatarName"] = AvatarName,
                ["avatarEyeHeight"] = (double)AvatarEyeHeight,
                ["trackingType"] = TrackingType, ["vrchat"] = Vrc.ToJson(),
                ["devices"] = Devices.Select(d => (object)d.ToJson()).ToList(), ["wrists"] = wr,
                ["fromTake"] = FromTake, ["fromTakeTime"] = FromTakeTime
            };
        }

        public static CalibrationSnapshot FromJson(Dictionary<string, object> d)
        {
            if (d == null) return null;
            var s = new CalibrationSnapshot
            {
                Id = d.GetStr("id", ""), Name = d.GetStr("name", ""), Source = d.GetStr("source", "manual"),
                Detail = d.GetStr("detail", ""),
                SteamVRSession = d.GetStr("steamvrSession", ""), AvatarId = d.GetStr("avatarId", ""),
                AvatarName = d.GetStr("avatarName", ""),
                AvatarEyeHeight = d.GetF("avatarEyeHeight"), TrackingType = d.GetInt("trackingType", -1),
                Vrc = VrcSettings.Values.FromJson(d.GetObj("vrchat")),
                FromTake = d.GetStr("fromTake", ""), FromTakeTime = d.GetNum("fromTakeTime", -1)
            };
            if (DateTime.TryParse(d.GetStr("created", ""), null, DateTimeStyles.RoundtripKind, out var dt))
                s.CreatedUtc = dt.ToUniversalTime();
            foreach (object o in d.GetList("devices") ?? new List<object>())
                if (o is Dictionary<string, object> dd)
                    s.Devices.Add(Device.FromJson(dd));
            var wr = d.GetList("wrists");
            if (wr != null)
                for (int i = 0; i < Math.Min(2, wr.Count); i++)
                    if (wr[i] is Dictionary<string, object> w)
                    {
                        s.WristPos[i] = Json.ToV3(w.ContainsKey("pos") ? w["pos"] : null);
                        s.WristRot[i] = Json.ToQ(w.ContainsKey("rot") ? w["rot"] : null).Normalized;
                        s.HasWrist[i] = true;
                    }

            return s;
        }

        public CalibrationSnapshot Clone()
        {
            return FromJson(Json.ParseObject(Json.Serialize(ToJson())));
        }

        /// <summary>How well this snapshot fits a take: tracker serials that are in the take but not here, and vice versa.</summary>
        public void Compare(Recording rec, out List<string> missing, out List<string> extra)
        {
            var takeTrackers = rec.Devices
                .Where(s => s.Count > 2 && (s.DeviceClass == 3 ||
                                            (s.DeviceClass == 2 && s.ControllerRole != 1 && s.ControllerRole != 2)))
                .Select(s => s.Serial).ToList();
            var mine = TrackerSerials;
            missing = takeTrackers.Where(t => !mine.Contains(t)).ToList();
            extra = mine.Where(t => !takeTrackers.Contains(t)).ToList();
        }

        /// <summary>A snapshot from a moment of a recording (its raw device poses), e.g. its best T-pose.</summary>
        public static CalibrationSnapshot FromTakeMoment(Recording rec, double t, string detail)
        {
            var s = new CalibrationSnapshot
            {
                Source = "take", Detail = detail, CreatedUtc = rec.CreatedUtc.AddSeconds(Math.Max(0, t)),
                AvatarId = rec.AvatarId, AvatarName = rec.AvatarName, AvatarEyeHeight = rec.AvatarEyeHeight,
                FromTake = Path.GetFileName(rec.FilePath ?? rec.Name), FromTakeTime = t
            };
            s.SteamVRSession = rec.Extra.GetStr("steamvrSession", "");
            var vrc = rec.Extra.GetObj("vrchatSettings");
            if (vrc != null) s.Vrc = VrcSettings.Values.FromJson(vrc);
            foreach (var d in rec.Devices.Where(d => d.Count > 2))
            {
                if (!d.Sample(t, out var p, out var q, 0.2)) continue;
                s.Devices.Add(new Device
                {
                    Serial = d.Serial, Class = d.DeviceClass, Role = d.ControllerRole, Model = d.ModelNumber,
                    ControllerType = d.ControllerType, TrackerRole = d.BodyRole, Pos = p, Rot = q
                });
            }

            for (int i = 0; i < 2; i++)
                if (rec.Hands[i].SampleWrist(t, out var wp, out var wq, 0.5))
                {
                    s.WristPos[i] = wp;
                    s.WristRot[i] = wq;
                    s.HasWrist[i] = true;
                }

            s.Id = NewId(s.CreatedUtc);
            return s;
        }

        public sealed class Device
        {
            public int Class, Role;
            public Vec3 Pos;
            public Quat Rot = Quat.Identity;
            public string Serial = "", Model = "", ControllerType = "", TrackerRole = "";

            public string Kind => Class == 1 ? "HMD" :
                Class == 2 ? Role == 1 ? "left controller" : Role == 2 ? "right controller" : "controller" :
                Class == 3 ? "tracker" : "device";

            public Dictionary<string, object> ToJson()
            {
                return new Dictionary<string, object>
                {
                    ["serial"] = Serial, ["class"] = Class, ["role"] = Role, ["model"] = Model,
                    ["ctype"] = ControllerType, ["trackerRole"] = TrackerRole,
                    ["pos"] = Json.V3(Pos), ["rot"] = Json.Q(Rot)
                };
            }

            public static Device FromJson(Dictionary<string, object> d)
            {
                return new Device
                {
                    Serial = d.GetStr("serial", ""), Class = d.GetInt("class"), Role = d.GetInt("role"),
                    Model = d.GetStr("model", ""), ControllerType = d.GetStr("ctype", ""),
                    TrackerRole = d.GetStr("trackerRole", ""), Pos = Json.ToV3(d.ContainsKey("pos") ? d["pos"] : null),
                    Rot = Json.ToQ(d.ContainsKey("rot") ? d["rot"] : null).Normalized
                };
            }
        }
    }
}
