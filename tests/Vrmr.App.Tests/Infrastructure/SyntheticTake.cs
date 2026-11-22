using System;
using System.Collections.Generic;
using System.IO;

namespace VRMR.App.Tests
{
    /// <summary>
    ///     A small made-up take (a headset and two controllers swaying for a few seconds, optionally hip and foot
    ///     trackers) for tests that need something to solve.
    /// </summary>
    public static class SyntheticTake
    {
        /// <summary>Builds the take in memory. It has its own generic skeleton, so no avatar library is needed.</summary>
        public static Recording Create(double seconds = 3, string name = "Synthetic", bool trackers = false)
        {
            var rec = new Recording { Name = name, AvatarName = "Test avatar", Profile = AvatarProfile.Generic() };
            var hmd = rec.AddStream(StreamKind.OpenVRDevice, "hmd", Info("TEST-HMD", 1, 0));
            var left = rec.AddStream(StreamKind.OpenVRDevice, "left", Info("TEST-L", 2, 1));
            var right = rec.AddStream(StreamKind.OpenVRDevice, "right", Info("TEST-R", 2, 2));
            var hips = trackers ? rec.AddStream(StreamKind.OpenVRDevice, "hips", Info("TEST-HIPS", 3, 0)) : null;
            var footL = trackers ? rec.AddStream(StreamKind.OpenVRDevice, "footL", Info("TEST-FOOT-L", 3, 0)) : null;
            var footR = trackers ? rec.AddStream(StreamKind.OpenVRDevice, "footR", Info("TEST-FOOT-R", 3, 0)) : null;
            int n = (int)(seconds * 90);
            for (int i = 0; i <= n; i++)
            {
                double t = i / 90.0;
                float sway = (float)Math.Sin(t * 2) * 0.05f;
                hmd.Add(t, new Vec3(sway, 1.6f, 0), Quat.YawRotation((float)Math.Sin(t) * 10));
                left.Add(t, new Vec3(-0.3f + sway, 1.0f + (float)Math.Sin(t * 3) * 0.1f, 0.25f), Quat.Identity);
                right.Add(t, new Vec3(0.3f + sway, 1.0f + (float)Math.Cos(t * 3) * 0.1f, 0.25f), Quat.Identity);
                if (!trackers) continue;
                // a little tremor: a pose that never changes reads as a lost tracker
                float shake = (float)Math.Sin(t * 37) * 0.0005f;
                hips.Add(t, new Vec3(sway, 0.95f, -0.09f), Quat.YawRotation((float)Math.Sin(t) * 5));
                footL.Add(t, new Vec3(-0.1f + shake, 0.1f, 0), Quat.Identity);
                footR.Add(t, new Vec3(0.1f - shake, 0.1f, 0), Quat.Identity);
            }

            var jaw = rec.GetOrAddParam("FT/v2/JawOpen", ParamType.Float);
            for (int i = 0; i <= (int)(seconds * 10); i++) jaw.Add(i * 0.1, (float)Math.Abs(Math.Sin(i * 0.4)));
            rec.RecomputeDuration();
            return rec;
        }

        static Dictionary<string, object> Info(string serial, int deviceClass, int role)
        {
            return new Dictionary<string, object>
                { ["serial"] = serial, ["class"] = deviceClass, ["role"] = role, ["model"] = "Test device" };
        }

        /// <summary>Writes the take into a folder and returns the file.</summary>
        public static string Save(string dir, double seconds = 3, string name = "Synthetic", bool trackers = false)
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name.Replace(' ', '_') + ".vrmr");
            RecordingIO.Save(Create(seconds, name, trackers), path);
            return path;
        }
    }
}
