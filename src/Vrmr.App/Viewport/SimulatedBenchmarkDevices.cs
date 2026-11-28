using System;

namespace VRMR.App.Viewport
{
    sealed class SimulatedBenchmarkDevices(double started)
    {
        static readonly string[] StationLabels = ["Base station A", "Base station B", "Base station C"];
        static readonly string[] TrackerLabels =
        [
            "Hip\n87 %", "Chest\n91 %", "Left foot\n64 %", "Right foot\n66 %", "Left knee\n72 %", "Right knee\n70 %",
            "Left elbow\n95 %", "Right elbow\n93 %"
        ];
        static readonly Vec3[] TrackerRest =
        [
            new(0, 1.0f, -0.05f), new(0, 1.3f, 0.1f), new(-0.12f, 0.08f, 0), new(0.12f, 0.08f, 0),
            new(-0.14f, 0.5f, 0.05f), new(0.14f, 0.5f, 0.05f), new(-0.3f, 1.25f, 0), new(0.3f, 1.25f, 0)
        ];

        readonly Vec3[] _playArea =
        [
            new(-1.5f, 0, -1.2f), new(1.5f, 0, -1.2f), new(1.5f, 0, 1.2f), new(-1.5f, 0, 1.2f)
        ];

        public void Fill(ViewScene scene, double now)
        {
            double t = now - started;
            float sway = (float)Math.Sin(t * 1.7) * 0.12f,
                bob = (float)Math.Sin(t * 3.4) * 0.03f,
                turn = (float)Math.Sin(t * 0.6) * 35f;
            var body = new Pose(new Vec3(sway, bob, (float)Math.Cos(t * 1.1) * 0.08f), Quat.YawRotation(turn));
            var markers = scene.Markers;
            markers.Clear();
            markers.Add(new ViewMarker
            {
                Kind = MarkerKind.Headset,
                Tint = MarkerTint.Head,
                Size = 1,
                Label = "Headset",
                Pose = body * new Pose(new Vec3(0, 1.6f, 0.05f), Quat.EulerUnity((float)Math.Sin(t * 2.3) * 10, 0, 0))
            });
            for (int side = -1; side <= 1; side += 2)
            {
                float swing = (float)Math.Sin(t * 2.9 + side) * 0.25f;
                var hand = new Pose(new Vec3(side * 0.35f, 1.1f + swing, 0.25f + swing * 0.5f),
                    Quat.EulerUnity(-30 + swing * 90, side * 15, 0));
                markers.Add(new ViewMarker
                {
                    Kind = MarkerKind.Controller,
                    Tint = side < 0 ? MarkerTint.Left : MarkerTint.Right,
                    Pose = body * hand,
                    Size = 1,
                    Label = side < 0 ? "Left controller" : "Right controller"
                });
            }

            for (int i = 0; i < TrackerRest.Length; i++)
            {
                var wobble = new Vec3((float)Math.Sin(t * 2.1 + i) * 0.04f,
                    (float)Math.Sin(t * 3.3 + i * 0.7) * 0.03f, (float)Math.Cos(t * 1.9 + i) * 0.04f);
                var pose = body * new Pose(TrackerRest[i] + wobble, Quat.EulerUnity((float)Math.Sin(t + i) * 20, i * 40, 0));
                markers.Add(new ViewMarker
                {
                    Kind = MarkerKind.Tracker,
                    Tint = MarkerTint.Tracker,
                    Pose = pose,
                    Size = 1,
                    Label = TrackerLabels[i]
                });
            }

            for (int i = 0; i < StationLabels.Length; i++)
            {
                float angle = i * 120f + 30f;
                var at = Quat.YawRotation(angle) * new Vec3(0, 0, -2.6f) + new Vec3(0, 2.2f, 0);
                var look = Quat.LookRotation((new Vec3(0, 0.9f, 0) - at).Normalized, Vec3.Up);
                markers.Add(new ViewMarker
                {
                    Kind = MarkerKind.BaseStation,
                    Tint = MarkerTint.Station,
                    Pose = new Pose(at, look),
                    Size = 1,
                    Label = StationLabels[i],
                    FovL = 75,
                    FovR = 75,
                    FovT = 55,
                    FovB = 55
                });
            }

            scene.PlayArea = _playArea;
        }
    }
}
