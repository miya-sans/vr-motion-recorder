using System;
using VRMR.Render;

namespace VRMR.RenderDemo
{
    /// <summary>
    /// Fourteen SteamVR devices made up in code (a headset, two controllers, eight trackers, three base stations) that
    /// move like someone dancing on the spot, plus a play area: the live view's load without SteamVR running.
    /// </summary>
    internal sealed class LiveDevices
    {
        static readonly Rgba Head = new Rgba(210, 214, 224), Hand = new Rgba(110, 168, 255), Tracker = new Rgba(86, 206, 132), Station = new Rgba(150, 160, 185);
        static readonly Vec3[] TrackerRest =
        {
            new Vec3(0, 1.0f, -0.05f), new Vec3(0, 1.3f, 0.1f), new Vec3(-0.12f, 0.08f, 0), new Vec3(0.12f, 0.08f, 0),
            new Vec3(-0.14f, 0.5f, 0.05f), new Vec3(0.14f, 0.5f, 0.05f), new Vec3(-0.3f, 1.25f, 0), new Vec3(0.3f, 1.25f, 0),
        };
        // labels are made once: a frame must not build strings
        static readonly string[] TrackerLabels = { "Hip\n87 %", "Chest\n91 %", "Left foot\n64 %", "Right foot\n66 %", "Left knee\n72 %", "Right knee\n70 %", "Left elbow\n95 %", "Right elbow\n93 %" };
        static readonly string[] StationLabels = { "Base station A", "Base station B", "Base station C" };

        /// <summary>Adds the devices as they are at time t (seconds) to a frame.</summary>
        public void Fill(ViewFrame f, double t)
        {
            float sway = (float)Math.Sin(t * 1.7) * 0.12f, bob = (float)Math.Sin(t * 3.4) * 0.03f, turn = (float)Math.Sin(t * 0.6) * 35f;
            var body = new Pose(new Vec3(sway, bob, (float)Math.Cos(t * 1.1) * 0.08f), Quat.YawRotation(turn));

            f.Markers.Add(new Marker { Kind = MarkerKind.Headset, Pose = body * new Pose(new Vec3(0, 1.6f, 0.05f), Quat.EulerUnity((float)Math.Sin(t * 2.3) * 10, 0, 0)), Color = Head, Label = "Headset" });
            for (int side = -1; side <= 1; side += 2)
            {
                float swing = (float)Math.Sin(t * 2.9 + side) * 0.25f;
                var hand = new Pose(new Vec3(side * 0.35f, 1.1f + swing, 0.25f + swing * 0.5f), Quat.EulerUnity(-30 + swing * 90, side * 15, 0));
                f.Markers.Add(new Marker { Kind = MarkerKind.Controller, Pose = body * hand, Color = Hand, Label = side < 0 ? "Left controller" : "Right controller" });
            }
            for (int i = 0; i < TrackerRest.Length; i++)
            {
                var wobble = new Vec3((float)Math.Sin(t * 2.1 + i) * 0.04f, (float)Math.Sin(t * 3.3 + i * 0.7) * 0.03f, (float)Math.Cos(t * 1.9 + i) * 0.04f);
                var pose = body * new Pose(TrackerRest[i] + wobble, Quat.EulerUnity((float)Math.Sin(t + i) * 20, i * 40, 0));
                f.Markers.Add(new Marker { Kind = MarkerKind.Tracker, Pose = pose, Color = Tracker, Label = TrackerLabels[i] });
            }
            for (int i = 0; i < 3; i++)
            {
                float angle = i * 120f + 30f;
                var at = Quat.YawRotation(angle) * new Vec3(0, 0, -2.6f) + new Vec3(0, 2.2f, 0);
                var look = Quat.LookRotation((new Vec3(0, 0.9f, 0) - at).Normalized, Vec3.Up);
                f.Markers.Add(new Marker { Kind = MarkerKind.BaseStation, Pose = new Pose(at, look), Color = Station, Label = StationLabels[i], FovLeft = 75, FovRight = 75, FovTop = 55, FovBottom = 55 });
            }
            f.PlayArea.Add(new Vec3(-1.5f, 0, -1.2f)); f.PlayArea.Add(new Vec3(1.5f, 0, -1.2f));
            f.PlayArea.Add(new Vec3(1.5f, 0, 1.2f)); f.PlayArea.Add(new Vec3(-1.5f, 0, 1.2f));
        }
    }
}
