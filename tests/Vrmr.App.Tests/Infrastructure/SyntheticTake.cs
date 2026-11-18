using System;
using System.Collections.Generic;
using System.IO;

namespace VRMR.App.Tests
{
    /// <summary>
    ///     A small made-up take (a headset and two controllers swaying for a few seconds) for tests that need something
    ///     to solve.
    /// </summary>
    public static class SyntheticTake
    {
        /// <summary>Builds the take in memory. It has its own generic skeleton, so no avatar library is needed.</summary>
        public static Recording Create(double seconds = 3, string name = "Synthetic")
        {
            var rec = new Recording { Name = name, AvatarName = "Test avatar", Profile = AvatarProfile.Generic() };
            var hmd = rec.AddStream(StreamKind.OpenVRDevice, "hmd", Info("TEST-HMD", 1, 0));
            var left = rec.AddStream(StreamKind.OpenVRDevice, "left", Info("TEST-L", 2, 1));
            var right = rec.AddStream(StreamKind.OpenVRDevice, "right", Info("TEST-R", 2, 2));
            int n = (int)(seconds * 90);
            for (int i = 0; i <= n; i++)
            {
                double t = i / 90.0;
                float sway = (float)Math.Sin(t * 2) * 0.05f;
                hmd.Add(t, new Vec3(sway, 1.6f, 0), Quat.YawRotation((float)Math.Sin(t) * 10));
                left.Add(t, new Vec3(-0.3f + sway, 1.0f + (float)Math.Sin(t * 3) * 0.1f, 0.25f), Quat.Identity);
                right.Add(t, new Vec3(0.3f + sway, 1.0f + (float)Math.Cos(t * 3) * 0.1f, 0.25f), Quat.Identity);
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
        public static string Save(string dir, double seconds = 3, string name = "Synthetic")
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name.Replace(' ', '_') + ".vrmr");
            RecordingIO.Save(Create(seconds, name), path);
            return path;
        }
    }
}
