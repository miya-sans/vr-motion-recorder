using System;
using System.Collections.Generic;

namespace VRMR.Tests
{
    /// <summary>
    ///     A take made from a script instead of a person: a headset, two controllers and three trackers (hips and feet)
    ///     moved by a simple puppet through standing in a T-pose, a squat, a quarter turn and a wave. It lets the whole
    ///     solve run in a test without anybody's recording. Everything is in Unity space, like a recorded take.
    /// </summary>
    public static class SyntheticTake
    {
        public const double Duration = 6.0;
        public const int Rate = 90;

        /// <summary>The puppet's standing height and where its devices sit, metres.</summary>
        public const float Height = 1.70f,
            EyeHeight = 1.59f,
            HandHeight = 1.385f,
            HandReach = 0.72f,
            HipsHeight = 0.95f,
            FootHeight = 0.10f,
            FootSide = 0.10f;

        public const float SquatDepth = 0.35f, WaveHeight = 1.95f;

        /// <summary>When the script does what (seconds).</summary>
        public const double SquatStart = 1.5, SquatEnd = 3.0, TurnStart = 3.0, TurnEnd = 4.5, WaveStart = 4.5;

        /// <summary>How deep the squat is at t (0..1).</summary>
        public static float Squat(double t)
        {
            return t < SquatStart || t > SquatEnd
                ? 0
                : (float)Math.Sin(Math.PI * (t - SquatStart) / (SquatEnd - SquatStart));
        }

        /// <summary>The body's turn to its right at t, degrees.</summary>
        public static float Yaw(double t)
        {
            return 90f * MathX.SmoothStep((float)((t - TurnStart) / (TurnEnd - TurnStart)));
        }

        /// <summary>How far the right arm is raised at t (0..1).</summary>
        public static float Raise(double t)
        {
            return MathX.SmoothStep((float)((t - WaveStart) / 0.5));
        }

        public static Recording Create()
        {
            var rec = new Recording
            {
                Name = "synthetic", AvatarName = "Synthetic", Duration = Duration,
                CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };
            // what VRChat's settings were "when recorded", so the solve asks nothing of the machine it runs on
            rec.Extra["vrchatSettings"] = new VrcSettings.Values
            {
                PlayerHeight = Height, MeasurementType = 1, SpineMode = 1, ArmRatio = CalibrationSettings.VrcArmRatio
            }.ToJson();
            var head = Device(rec, "HMD-0001", 1, 0, "Synthetic HMD", "");
            var left = Device(rec, "CTL-LEFT", 2, 1, "Synthetic Controller", "generic");
            var right = Device(rec, "CTL-RIGHT", 2, 2, "Synthetic Controller", "generic");
            var hips = Device(rec, "TRK-HIPS", 3, 0, "Synthetic Tracker", "");
            var footL = Device(rec, "TRK-FOOT-L", 3, 0, "Synthetic Tracker", "");
            var footR = Device(rec, "TRK-FOOT-R", 3, 0, "Synthetic Tracker", "");

            // calibrated while standing in the T-pose, as a user would
            rec.Edits.CalibrationTime = 0.5;

            int samples = (int)(Duration * Rate);
            for (int i = 0; i <= samples; i++)
            {
                double t = (double)i / Rate;
                float squat = Squat(t) * SquatDepth;
                var turn = Quat.YawRotation(Yaw(t));
                // feet stay where they are; everything above turns about the vertical axis through the body
                Add(head, i, t, turn * new Vec3(0, EyeHeight - squat, 0.085f), turn);
                Add(hips, i, t, turn * new Vec3(0, HipsHeight - squat, -0.09f), turn);
                Add(footL, i, t, new Vec3(-FootSide, FootHeight, 0), Quat.Identity);
                Add(footR, i, t, new Vec3(FootSide, FootHeight, 0), Quat.Identity);
                // arms out to the sides; in the squat the hands come forward; at the end the right hand waves overhead
                float forward = Squat(t) * 0.35f, raise = Raise(t);
                var leftHand = new Vec3(-HandReach + forward * 0.4f, HandHeight - squat, forward);
                var down = new Vec3(HandReach - forward * 0.4f, HandHeight - squat, forward);
                var up = new Vec3(0.30f + 0.12f * (float)Math.Sin((t - WaveStart) * 2 * Math.PI * 1.5), WaveHeight,
                    0.05f);
                Add(left, i, t, turn * leftHand, turn);
                Add(right, i, t, turn * Vec3.Lerp(down, up, raise), turn);
            }

            return rec;
        }

        /// <summary>
        ///     Adds a sample with the tremor every real device has (a few tenths of a millimetre, a few hundredths of a
        ///     degree): a pose that never changes at all reads as a device that lost tracking. The tremor is a fixed
        ///     function of the stream and the sample number, so the take is the same on every run.
        /// </summary>
        static void Add(PoseStream stream, int sample, double t, Vec3 pos, Quat rot)
        {
            float N(int axis)
            {
                uint h = (uint)(stream.Id * 73856093) ^ (uint)(sample * 19349663) ^ (uint)(axis * 83492791);
                h ^= h >> 13;
                h *= 0x5bd1e995;
                h ^= h >> 15;
                return (h & 0xFFFF) / 32767.5f - 1f; // -1..1
            }

            var jitter = new Vec3(N(0), N(1), N(2)) * 0.0003f;
            var wobble = Quat.EulerUnity(N(3) * 0.03f, N(4) * 0.03f, N(5) * 0.03f);
            stream.Add(t, pos + jitter, (rot * wobble).Normalized);
        }

        static PoseStream Device(Recording rec, string serial, int deviceClass, int role, string model,
            string controllerType)
        {
            return rec.AddStream(StreamKind.OpenVRDevice, serial, new Dictionary<string, object>
            {
                ["serial"] = serial, ["class"] = deviceClass, ["role"] = role, ["model"] = model,
                ["ctype"] = controllerType,
                ["system"] = "synthetic", ["manufacturer"] = "Synthetic", ["renderModel"] = ""
            });
        }
    }
}
