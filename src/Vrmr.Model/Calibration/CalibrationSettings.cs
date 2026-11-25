using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VRMR
{
    /// <summary>How the user's size is mapped onto the avatar (VRChat: "User Height" + "Avatar Measurement").</summary>
    public enum CalHeightMode
    {
        /// <summary>Eye height measured from the headset at the calibration moment (head tilt removed).</summary>
        Measure,

        /// <summary>The user's real body height (stature) in metres; eye height = height x 0.94 (VRChat's ratio, measured).</summary>
        RealHeight,

        /// <summary>The user's real eye height (floor to eyes) in metres.</summary>
        EyeHeight,

        /// <summary>A fixed tracking scale (real metres -> avatar metres).</summary>
        Scale,

        /// <summary>
        ///     VRChat's "User Real Height" (read from VRChat's settings; stored with each calibration and take), eye height =
        ///     height x 0.94.
        /// </summary>
        VRChat
    }

    /// <summary>VRChat IK 2.0 "Avatar Measurement": match the avatar by height (best for FBT) or by arm span.</summary>
    public enum CalMeasure { Height, ArmSpan }

    /// <summary>VRChat IK 2.0 FBT spine mode: which of head and hips is strictly enforced.</summary>
    public enum CalLock
    {
        /// <summary>Head stays on the headset; the hips may leave the hip tracker.</summary>
        Head,

        /// <summary>Hips stay on the hip tracker; the head may leave the headset.</summary>
        Hips,

        /// <summary>Both are held; the spine bends to connect them, and when they can't be connected the gap is shared.</summary>
        Both
    }

    /// <summary>
    ///     Full-body calibration settings (VRChat-style). The headset and controllers map to head and hands through
    ///     device-local offsets. Body trackers are bound to their bones at the calibration
    ///     moment, with the avatar standing in its T-pose on the floor under the headset.
    ///     Stored per clip (EditList.Calib, null = global defaults), as global defaults (Settings) and as named presets.
    /// </summary>
    public sealed class CalibrationSettings
    {
        /// <summary>
        ///     Eye height / "User Real Height". Measured against VRChat's own IK (HUMR, one user,
        ///     three Height-mode takes): the tracking scale held within 0.3 %, which means VRChat uses an eye
        ///     height of 0.940 x the User Real Height. (The base solver uses 0.93, which made everything 1% too big.)
        /// </summary>
        public const float EyeToHeightRatio = 0.94f;

        /// <summary>
        ///     VRChat "by arm span": head bone to wrist (arm straight out in T-pose) is this fraction of an eye height
        ///     (VRC_IK_HEIGHT_RATIO).
        /// </summary>
        public const float VrcArmRatio = 0.4537f;

        public float AvatarScale; // avatar scale in VRChat; 0 = from VRChat's reported eye height

        /// <summary>
        ///     Follow VRChat's avatar scaling (height slider / reset) during the take; off (default) keeps the avatar at its
        ///     own size (VrcAvatarScale).
        /// </summary>
        public bool FollowAvatarScale;

        /// <summary>Use VRChat's recorded measurement when available, falling back to <see cref="Measure" />.</summary>
        public bool FollowVrcMeasure = true;

        /// <summary>Use VRChat's recorded spine mode when available, falling back to <see cref="Lock" />.</summary>
        public bool FollowVrcSpine = true;

        /// <summary>Manual left controller to hand position, in reference avatar metres. Right mirrors X.</summary>
        public Vec3? HandOffset;

        public Vec3 HandRotFix = Vec3.Zero; // degrees, local to the hand target, mirrored for the right hand

        public bool
            HangFromHeadset =
                true; // VRChat: at calibration the avatar hangs from the headset (feet may end up below / above the floor); off = stood on the floor (as the base solver does)

        /// <summary>Manual headset-local eye target shift, in reference avatar metres.</summary>
        public (float Y, float Z)? HeadOffset;

        public CalHeightMode Height = CalHeightMode.VRChat;
        public float HeightValue; // metres (RealHeight / EyeHeight) or scale factor (Scale)

        public bool
            KeepFeetPlanted =
                true; // Lock head: the hips come down instead of lifting the feet off their trackers (VRChat does this); Lock hips / Lock both keep the hips and let the feet lift, like VRChat

        public CalLock Lock = CalLock.Head; // head exactly on the headset, like the hands on the controllers
        public CalMeasure Measure = CalMeasure.Height;
        public bool ProceduralFeet = true; // foot IK: procedural stepping for feet without trackers

        public bool
            ShoulderTracking = true; // the solver's FBIKShoulderSolveEnabled / VRChat --disable-shoulder-tracking

        public bool UprightCalibration = true; // remove head tilt at calibration and stand the avatar on the floor
        public bool UseHips = true, UseChest = true, UseFeet = true, UseKnees = true, UseElbows = true;

        public bool
            VrcHands = true; // VRChat's headset and Index offsets; off uses manual offsets when available.

        public CalibrationSettings Clone()
        {
            var c = (CalibrationSettings)MemberwiseClone();
            return c;
        }

        public bool UsesRole(BodyRole r)
        {
            switch (r)
            {
                case BodyRole.Hips: return UseHips;
                case BodyRole.Chest: return UseChest;
                case BodyRole.LeftFoot:
                case BodyRole.RightFoot: return UseFeet;
                case BodyRole.LeftKnee:
                case BodyRole.RightKnee: return UseKnees;
                case BodyRole.LeftElbow:
                case BodyRole.RightElbow: return UseElbows;
                default: return true;
            }
        }

        public Dictionary<string, object> ToJson()
        {
            var json = new Dictionary<string, object>
            {
                ["height"] = Height.ToString(), ["heightValue"] = (double)HeightValue, ["measure"] = Measure.ToString(),
                ["avatarScale"] = (double)AvatarScale, ["lock"] = Lock.ToString(),
                ["useHips"] = UseHips, ["useChest"] = UseChest, ["useFeet"] = UseFeet, ["useKnees"] = UseKnees,
                ["useElbows"] = UseElbows,
                ["shoulderTracking"] = ShoulderTracking, ["proceduralFeet"] = ProceduralFeet,
                ["upright"] = UprightCalibration, ["keepFeetPlanted"] = KeepFeetPlanted, ["vrcHands"] = VrcHands,
                ["hangFromHeadset"] = HangFromHeadset,
                ["followVrcSpine"] = FollowVrcSpine,
                ["followVrcMeasure"] = FollowVrcMeasure,
                ["followAvatarScale"] = FollowAvatarScale, ["handRotFix"] = Json.V3(HandRotFix)
            };
            if (HeadOffset is { } head)
                json["headOffset"] = new Dictionary<string, object>
                {
                    ["y"] = (double)head.Y,
                    ["z"] = (double)head.Z
                };
            if (HandOffset is { } hand) json["handOffset"] = Json.V3(hand);
            return json;
        }

        public static CalibrationSettings FromJson(Dictionary<string, object> d)
        {
            var c = new CalibrationSettings();
            if (d == null) return c;
            c.Height = ParseEnum(d.GetStr("height"), c.Height);
            c.HeightValue = d.GetF("heightValue", c.HeightValue);
            c.Measure = ParseEnum(d.GetStr("measure"), c.Measure);
            c.AvatarScale = d.GetF("avatarScale", c.AvatarScale);
            c.Lock = ParseEnum(d.GetStr("lock"), c.Lock);
            c.UseHips = d.GetBool("useHips", true);
            c.UseChest = d.GetBool("useChest", true);
            c.UseFeet = d.GetBool("useFeet", true);
            c.UseKnees = d.GetBool("useKnees", true);
            c.UseElbows = d.GetBool("useElbows", true);
            c.ShoulderTracking = d.GetBool("shoulderTracking", true);
            c.ProceduralFeet = d.GetBool("proceduralFeet", true);
            c.UprightCalibration = d.GetBool("upright", true);
            c.KeepFeetPlanted = d.GetBool("keepFeetPlanted", true);
            c.VrcHands = d.GetBool("vrcHands", true);
            c.HangFromHeadset = d.GetBool("hangFromHeadset", true);
            bool followVrc = d.GetBool("followVrc", true);
            c.FollowVrcSpine = d.GetBool("followVrcSpine", followVrc);
            c.FollowVrcMeasure = d.GetBool("followVrcMeasure", followVrc);
            c.FollowAvatarScale = d.GetBool("followAvatarScale");
            if (d.GetObj("headOffset") is { } head) c.HeadOffset = (head.GetF("y"), head.GetF("z"));
            if (d.TryGetValue("handOffset", out object hand) && hand != null) c.HandOffset = Json.ToV3(hand);
            c.HandRotFix = Json.ToV3(d.ContainsKey("handRotFix") ? d["handRotFix"] : null);
            return c;
        }

        static T ParseEnum<T>(string s, T def) where T : struct
        {
            return !string.IsNullOrEmpty(s) && Enum.TryParse(s, true, out T v) ? v : def;
        }

        public bool SameAs(CalibrationSettings o)
        {
            return o != null && Json.Serialize(ToJson()) == Json.Serialize(o.ToJson());
        }

        public string Describe()
        {
            string h;
            switch (Height)
            {
                case CalHeightMode.RealHeight: h = $"real height {HeightValue:0.00} m"; break;
                case CalHeightMode.EyeHeight: h = $"eye height {HeightValue:0.00} m"; break;
                case CalHeightMode.Scale: h = $"scale {HeightValue:0.000}"; break;
                case CalHeightMode.VRChat: h = "VRChat height"; break;
                default: h = "height measured at calibration"; break;
            }

            string measure = FollowVrcMeasure
                ? "VRChat measurement"
                : Measure == CalMeasure.ArmSpan ? "by arm span" : "by height";
            string spine = FollowVrcSpine ? "VRChat spine" : $"lock {Lock.ToString().ToLowerInvariant()}";
            return $"{h}, {measure}, {spine}";
        }
    }

    /// <summary>Named calibration presets: JSON files in the app data folder's calibrations folder.</summary>
    public static class CalibrationPresets
    {
        /// <summary>The presets folder in use (<see cref="AppPaths.CalibrationPresetsDir" />).</summary>
        public static string Dir => AppPaths.Current.CalibrationPresetsDir;

        public static List<string> List()
        {
            try
            {
                if (!Directory.Exists(Dir)) return new List<string>();
                return Directory.GetFiles(Dir, "*.json").Select(Path.GetFileNameWithoutExtension)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch { return new List<string>(); }
        }

        static string FileFor(string name)
        {
            char[] bad = Path.GetInvalidFileNameChars();
            string safe = new((name ?? "").Trim().Select(ch => bad.Contains(ch) ? '_' : ch).ToArray());
            if (safe.Length == 0) throw new ArgumentException("Preset name is empty");
            return Path.Combine(Dir, safe + ".json");
        }

        public static void Save(string name, CalibrationSettings c)
        {
            Directory.CreateDirectory(Dir);
            var d = new Dictionary<string, object>
            {
                ["app"] = "VR Motion Recorder", ["kind"] = "calibration", ["name"] = name.Trim(),
                ["settings"] = c.ToJson()
            };
            File.WriteAllText(FileFor(name), Json.Serialize(d, true), Encoding.UTF8);
        }

        public static CalibrationSettings Load(string name)
        {
            var d = Json.ParseObject(File.ReadAllText(FileFor(name), Encoding.UTF8));
            return CalibrationSettings.FromJson(d?.GetObj("settings") ?? d);
        }

        /// <summary>Moves a preset to the Recycle Bin.</summary>
        public static void Delete(string name)
        {
            RecycleBin.Delete(FileFor(name));
        }
    }
}
