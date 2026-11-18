using System;
using System.Collections.Generic;
using System.Linq;

namespace VRMR
{
    /// <summary>
    ///     A recording resolved onto an avatar skeleton at a fixed frame rate, on the edited timeline:
    ///     humanoid bone world rotations + hips position per frame, face shape weights, raw parameters,
    ///     camera and tracker poses. This is what the viewport plays and every exporter writes.
    /// </summary>
    public sealed class BakedClip
    {
        public readonly Dictionary<string, float[]> Face = new(); // shape -> 0..1

        /// <summary>
        ///     Source-time stretches where the pose is held, not tracked (VRChat's IK in calibration mode / an avatar
        ///     reload).
        /// </summary>
        public readonly List<double[]> Held = new();

        public readonly Dictionary<string, ParamType> ParamTypes = new();
        public readonly Dictionary<string, float[]> ParamValues = new(); // raw params
        public readonly Dictionary<string, bool[]> TrackerValid = new();
        public readonly Dictionary<string, Pose[]> Trackers = new();
        public string BodySource = "none"; // humr | ik | none
        public bool[] BoneExists;

        /// <summary>IK solve only: how the take was calibrated and how well the body follows its devices.</summary>
        public CalibrationResult Calibration;

        public Pose[] Camera;
        public bool[] CameraValid;
        public float Fps;
        public int Frames;
        public Vec3[] Hips; // world (centered) hips position per frame
        public float HumrK = 1f;

        /// <summary>VRChat IK path: VRChat world -> clip mapping (origin, and the worn avatar's size -> the target's).</summary>
        public Pose HumrOrigin = Pose.Identity;

        /// <summary>The placement the clip was moved / turned by after the solve (the take's Edits.Placement; identity in scenes).</summary>
        public Pose Placement = Pose.Identity;

        public AvatarProfile Profile;

        /// <summary>
        ///     The SteamVR room (Recording's SteamVRRoom: play area, base stations) in clip space per frame, like the
        ///     devices: point = Room[f].TransformPoint(RoomScale[f] * raw), rotation = Room[f].rot * raw. IK solves (null
        ///     otherwise); RoomScale[f] = 0 where unknown (no headset).
        /// </summary>
        public Pose[] Room;

        public float[] RoomScale;
        public Quat[][] Rot; // [frame][55] world rotations (centered)
        public float Scale = 1f; // avatar scale used for FK offsets

        /// <summary>Per-frame size on top of <see cref="Scale" /> (following VRChat's avatar scaling), or null.</summary>
        public float[] ScaleCurve;

        /// <summary>The skeleton of the avatar the take was recorded with (VRChat's IK is retargeted from it).</summary>
        public AvatarProfile SourceProfile;

        public double[] SourceTime;

        /// <summary>Scene editor: each device's influence on the solve per frame (input tracks), or null (all 1).</summary>
        public Dictionary<string, float[]> TrackerInfluence;

        public List<string> Warnings = new();
        int[] _humanOfNode;

        List<int> _order;
        Vec3[] _scaleW;

        public double Duration => Frames > 1 ? (Frames - 1) / Fps : 0;

        /// <summary>Uniform avatar scale at frame f.</summary>
        public float ScaleAt(int f)
        {
            return ScaleCurve != null && ScaleCurve.Length > 0
                ? Scale * ScaleCurve[MathX.Clamp(f, 0, ScaleCurve.Length - 1)]
                : Scale;
        }

        /// <summary>World transforms of every profile node at a frame (root at origin).</summary>
        /// <summary>World position and rotation of every profile node at frame f (wr receives world rotations).</summary>
        public void EvaluateNodes(int f, Vec3[] wp, Quat[] wr)
        {
            f = MathX.Clamp(f, 0, Math.Max(0, Frames - 1));
            var p = Profile;
            var order = _order ?? (_order = p.TopoOrder());
            int[] humanOfNode = _humanOfNode ?? (_humanOfNode = BuildHumanOfNode());
            var rot = Rot != null && Frames > 0 ? Rot[f] : null;
            int hipsNode = p.Human[HumanBones.Hips];
            var scl = _scaleW ?? (_scaleW = new Vec3[p.Nodes.Count]);
            foreach (int i in order)
            {
                var nd = p.Nodes[i];
                if (nd.Parent < 0)
                {
                    float sc = ScaleAt(f);
                    wp[i] = Vec3.Zero;
                    wr[i] = Quat.Identity;
                    scl[i] = new Vec3(sc, sc, sc);
                    continue;
                }

                int pa = nd.Parent;
                scl[i] = Vec3.Scale(scl[pa], nd.LocalScale);
                wp[i] = wp[pa] + wr[pa] * Vec3.Scale(scl[pa], nd.LocalPos);
                int hb = humanOfNode[i];
                wr[i] = hb >= 0 && rot != null ? rot[hb] : wr[pa] * nd.LocalRot;
                if (i == hipsNode && Hips != null && Frames > 0) wp[i] = Hips[f];
            }
        }

        /// <summary>Local (parent-relative) rotation and position of every node at a frame, for exporters.</summary>
        public void EvaluateLocal(int f, Vec3[] wp, Quat[] wr, Vec3[] lp, Quat[] lr)
        {
            EvaluateNodes(f, wp, wr);
            var p = Profile;
            var scl = _scaleW;
            for (int i = 0; i < p.Nodes.Count; i++)
            {
                var nd = p.Nodes[i];
                if (nd.Parent < 0)
                {
                    lp[i] = Vec3.Zero;
                    lr[i] = Quat.Identity;
                    continue;
                }

                var pr = wr[nd.Parent].Conjugate;
                lr[i] = (pr * wr[i]).Normalized;
                var d = pr * (wp[i] - wp[nd.Parent]);
                var s = scl[nd.Parent];
                lp[i] = new Vec3(Div(d.x, s.x), Div(d.y, s.y), Div(d.z, s.z));
            }
        }

        static float Div(float a, float b)
        {
            return Math.Abs(b) > 1e-9f ? a / b : a;
        }

        int[] BuildHumanOfNode()
        {
            int[] r = Enumerable.Repeat(-1, Profile.Nodes.Count).ToArray();
            for (int b = 0; b < HumanBones.Count; b++)
                if (Profile.Human[b] >= 0 && BoneExists[b])
                    r[Profile.Human[b]] = b;
            return r;
        }

        public int FrameAt(double t)
        {
            return MathX.Clamp((int)Math.Round(t * Fps), 0, Math.Max(0, Frames - 1));
        }
    }

    /// <summary>Outcome of a VRChat-style calibration for one solve (shown in the Clip page).</summary>
    public sealed class CalibrationResult
    {
        /// <summary>
        ///     Per device (Head, Hips, LeftFoot, RightFoot): RMS drift of the bone in the device's frame (m); ~0 = rigidly
        ///     bound.
        /// </summary>
        public readonly Dictionary<string, float> Drift = new();

        /// <summary>Every solve segment (recalibrations, VRChat setting changes), this one included.</summary>
        public readonly List<CalibrationResult> Segments = new();

        /// <summary>Body tracker roles that drove the solve.</summary>
        public readonly HashSet<string> Trackers = new();

        public bool Auto;

        /// <summary>
        ///     Feet first: how far the hips were lowered to keep both feet on their trackers (m), and in what share of
        ///     frames.
        /// </summary>
        public float FeetDropMax, FeetDropMean, FeetDropShare;

        /// <summary>
        ///     10th percentile of the lowest foot/toe bone height minus its standing (rest) height (m); ~0 = feet on the
        ///     floor.
        /// </summary>
        public float FootFloorGap = float.NaN;

        /// <summary>Index into <see cref="Segments" /> per clip frame.</summary>
        public int[] FrameSegment;

        public string PoseText = "", ScaleText = "";
        public CalibrationSettings Settings;

        /// <summary>Source-time range this calibration was solved for (multi-calibration takes; infinite = open end).</summary>
        public double Start = double.NegativeInfinity, End = double.PositiveInfinity;

        public double Time, DetectedTPose = -1;
        public float TrackScale, RealEyeHeight, AvatarEyeHeight;

        public string Summary()
        {
            var parts = Drift.OrderBy(k => k.Key == "Head" ? 0 : k.Key == "Hips" ? 1 : 2).Select(k =>
                $"{(k.Key == "LeftFoot" ? "L foot" : k.Key == "RightFoot" ? "R foot" : k.Key.ToLowerInvariant())} {k.Value * 100:0.0}");
            string s = "Body follows devices (drift, cm): " + string.Join(", ", parts);
            if (!float.IsNaN(FootFloorGap)) s += $"\nStanding feet vs floor: {FootFloorGap * 100:+0.0;-0.0;0.0} cm";
            if (FeetDropMax > 0.002f)
                s +=
                    $"\nHips lowered to keep the feet on their trackers in {FeetDropShare * 100:0}% of frames (mean {FeetDropMean * 100:0.0} cm, max {FeetDropMax * 100:0.0} cm)";
            if (Segments.Count > 1)
                s += $"\n{Segments.Count} segments (VRChat recalibrated or changed settings): " + string.Join("; ",
                    Segments.Select(g =>
                        $"{(double.IsNegativeInfinity(g.Start) ? 0 : g.Start):F1} s on: {g.Settings?.Describe()}, scale {g.TrackScale:0.000}, calibrated at {(g.Time >= 0 ? g.Time.ToString("F2") + " s" : "a snapshot")}"));
            return s;
        }
    }
}
