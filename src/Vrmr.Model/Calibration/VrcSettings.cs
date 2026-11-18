using System;
using System.Collections.Generic;

namespace VRMR
{
    /// <summary>
    ///     VRChat's own IK settings, read (never written) from the settings VRChat keeps in the registry
    ///     (see <see cref="UnityPlayerPrefs" />).
    /// </summary>
    public static class VrcSettings
    {
        static Values _cache;
        static DateTime _cacheTime;
        static IMachine _cacheMachine;

        /// <summary>
        ///     Current VRChat settings on the machine in use (cached for 10 s unless <paramref name="fresh" />). Never
        ///     throws.
        /// </summary>
        public static Values Read(bool fresh = false)
        {
            if (!fresh && _cache != null && ReferenceEquals(_cacheMachine, AppPaths.Current.Machine) &&
                (DateTime.UtcNow - _cacheTime).TotalSeconds < 10) return _cache;
            var machine = AppPaths.Current.Machine;
            var v = ReadFrom(machine);
            _cache = v;
            _cacheTime = DateTime.UtcNow;
            _cacheMachine = machine;
            return v;
        }

        /// <summary>
        ///     VRChat's settings as stored on a machine. Never throws; values that are not stored keep their "not found"
        ///     defaults.
        /// </summary>
        public static Values ReadFrom(IMachine machine)
        {
            var v = new Values();
            try
            {
                var prefs = new UnityPlayerPrefs(machine, "VRChat", "VRChat");
                v.PlayerHeight = prefs.Float("PlayerHeight") ?? 0;
                v.MeasurementType = prefs.Int("VRC_IK_AVATAR_MEASUREMENT_TYPE") ?? -1;
                v.SpineMode = prefs.Int("VRC_IK_FBT_SPINE_MODE") ?? -1;
                v.ArmRatio = prefs.Float("VRC_IK_HEIGHT_RATIO") ?? 0;
                v.OneHandedCalibration = (prefs.Int("VRC_IK_ONE_HANDED_CALIBRATION") ?? 0) != 0;
                v.ShoulderTrackingDisabled = (prefs.Int("VRC_IK_DISABLE_SHOULDER_TRACKING") ?? 0) != 0;
                v.SendsHeadWristOsc = (prefs.Int("VRC_TRACKING_SEND_VR_SYSTEM_HEAD_AND_WRIST_OSC_DATA") ?? 0) != 0;
            }
            catch (Exception ex) { AppLog.Warn("Reading VRChat settings: " + ex.Message); }

            if (v.PlayerHeight < 0.5f || v.PlayerHeight > 3f) v.PlayerHeight = 0;
            return v;
        }

        public sealed class Values
        {
            /// <summary>VRC_IK_HEIGHT_RATIO: the arm ratio used by measure-by-arms (VRChat default 0.4537).</summary>
            public float ArmRatio;

            /// <summary>VRC_IK_AVATAR_MEASUREMENT_TYPE raw value (-1 = not found).</summary>
            public int MeasurementType = -1;

            public bool OneHandedCalibration, ShoulderTrackingDisabled, SendsHeadWristOsc;

            /// <summary>"User Real Height" in metres (PlayerHeight), 0 = not found.</summary>
            public float PlayerHeight;

            /// <summary>VRC_IK_FBT_SPINE_MODE raw value (-1 = not found).</summary>
            public int SpineMode = -1;

            public bool Found => PlayerHeight > 0.3f;

            public Dictionary<string, object> ToJson()
            {
                return new Dictionary<string, object>
                {
                    ["playerHeight"] = (double)PlayerHeight, ["measurementType"] = MeasurementType,
                    ["spineMode"] = SpineMode, ["armRatio"] = (double)ArmRatio,
                    ["oneHandedCalibration"] = OneHandedCalibration,
                    ["shoulderTrackingDisabled"] = ShoulderTrackingDisabled, ["sendsHeadWristOsc"] = SendsHeadWristOsc
                };
            }

            public static Values FromJson(Dictionary<string, object> d)
            {
                var v = new Values();
                if (d == null) return v;
                v.PlayerHeight = d.GetF("playerHeight");
                v.MeasurementType = d.GetInt("measurementType", -1);
                v.SpineMode = d.GetInt("spineMode", -1);
                v.ArmRatio = d.GetF("armRatio");
                v.OneHandedCalibration = d.GetBool("oneHandedCalibration");
                v.ShoulderTrackingDisabled = d.GetBool("shoulderTrackingDisabled");
                v.SendsHeadWristOsc = d.GetBool("sendsHeadWristOsc");
                return v;
            }

            public string Describe()
            {
                return Found ? $"VRChat height {PlayerHeight * 100:0} cm" : "VRChat settings not found";
            }
        }
    }
}
