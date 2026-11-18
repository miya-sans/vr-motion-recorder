using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using VRMR.Render;

namespace VRMR.RenderDemo
{
    /// <summary>
    ///     What the demo shows: a take solved onto its avatar (or the built-in mannequin when no folder was given), and how
    ///     to put one of its frames into a <see cref="ViewFrame" />. This is the app's side of the render API in miniature.
    /// </summary>
    sealed class TakeSource
    {
        static readonly Rgba CameraColor = new(240, 186, 80),
            HeadColor = new(210, 214, 224),
            HandColor = new(110, 168, 255),
            TrackerColor = new(148, 152, 166);

        readonly List<Device> _devices = new();

        readonly List<(int Channel, float[] Curve)> _face = new();

        /// <summary>Moves and turns everything shown (the gizmo demo edits it).</summary>
        public Pose Placement = Pose.Identity;

        float _deviceSize = 1f;
        public AvatarProfile Profile { get; private set; }
        public BakedClip Clip { get; private set; }
        public MeshModel Model { get; private set; }
        public string Name { get; private set; } = "";

        /// <summary>How the body was produced, for the overlay.</summary>
        public string Source { get; private set; } = "rest pose";

        public int Frames => Clip?.Frames ?? 1;
        public float Fps => Clip?.Fps ?? 60f;

        /// <summary>The mannequin in its rest pose: something to look at without any data.</summary>
        public static TakeSource Mannequin()
        {
            var profile = VRMR.Mannequin.Profile();
            return new TakeSource
                { Profile = profile, Model = MeshModel.Create(MannequinMesh.For(profile)), Name = "mannequin" };
        }

        /// <summary>
        ///     Loads the first take in dataDir whose file name contains filter and solves it onto the avatar the library in
        ///     dataDir\Avatars has for it. Logs and the calibration cache go to workDir; nothing is written to dataDir.
        /// </summary>
        public static TakeSource Load(string dataDir, string filter, string workDir, Action<string> log)
        {
            Directory.CreateDirectory(workDir);
            AppLog.Init(Path.Combine(workDir, "log"));
            BodySolverSetup.Register();
            CalibrationLibrary.DirOverride = Path.Combine(workDir, "callib");
            Directory.CreateDirectory(CalibrationLibrary.DirOverride);
            SkiaTextureDecoder.Install();

            var settings = new Settings
                { RecordingsDir = dataDir, AvatarLibraryDir = Path.Combine(dataDir, "Avatars") };
            settings.UnityProjects.Clear();
            settings.AvatarModelMap.Clear();
            var library = new AvatarLibrary(settings);
            PoseBuilder.SourceResolver = r => library.MatchTake(r).Entry?.Profile();

            string path = Directory.GetFiles(dataDir, "*.vrmr").OrderBy(p => p, StringComparer.Ordinal)
                              .FirstOrDefault(p =>
                                  Path.GetFileName(p).Contains(filter ?? "", StringComparison.OrdinalIgnoreCase))
                          ?? throw new FileNotFoundException("No take in " + dataDir +
                                                             (string.IsNullOrEmpty(filter)
                                                                 ? ""
                                                                 : " matches '" + filter + "'"));
            var watch = Stopwatch.StartNew();
            var take = RecordingIO.Load(path);
            var target = library.TargetFor(take, out _);
            var clip = PoseBuilder.Bake(take, target,
                new BakeOptions
                {
                    Fps = 60, Smoothing = settings.Smoothing,
                    Calibration = (take.Edits.Calib ?? settings.CalibrationDefaults).Clone()
                });
            log?.Invoke($"solved {clip.Frames} frames in {watch.ElapsedMilliseconds} ms");

            watch.Restart();
            var avatar = SkinnedAvatar.ForProfile(clip.Profile);
            var model = MeshModel.Create(avatar);
            log?.Invoke(model == null
                ? "no avatar mesh: skeleton only"
                : $"mesh: {model.Parts.Count} parts, {model.VertexCount} vertices, {model.TriangleCount} triangles, {model.FaceChannelCount} face shapes, loaded in {watch.ElapsedMilliseconds} ms");

            var s = new TakeSource
            {
                Profile = clip.Profile, Clip = clip, Model = model,
                Name = take.Name ?? Path.GetFileNameWithoutExtension(path),
                Source = clip.BodySource == "humr" ? "VRChat IK" : clip.BodySource == "ik" ? "IK solve" : "no body"
            };
            if (model != null)
                foreach (var kv in clip.Face)
                {
                    int channel = model.FaceChannel(kv.Key); // looked up once, not per frame
                    if (channel >= 0) s._face.Add((channel, kv.Value));
                }

            if (clip.Calibration != null && clip.Calibration.TrackScale > 0.05f)
                s._deviceSize = clip.Calibration.TrackScale;
            foreach (var kv in clip.Trackers)
            {
                bool hand = kv.Key == "LeftHand" || kv.Key == "RightHand", head = kv.Key == "Head";
                s._devices.Add(new Device
                {
                    Poses = kv.Value, Valid = clip.TrackerValid[kv.Key],
                    Kind = head ? MarkerKind.Headset : hand ? MarkerKind.Controller : MarkerKind.Tracker,
                    Color = head ? HeadColor : hand ? HandColor : TrackerColor
                });
            }

            return s;
        }

        /// <summary>Adds the avatar at a frame, and optionally the take's devices and camera, to a view frame. Allocates nothing.</summary>
        public ActorView Fill(ViewFrame f, int frame, bool devices = true)
        {
            var actor = f.AddActor(Profile, Model);
            if (Clip == null)
            {
                for (int i = 0; i < actor.NodePos.Length; i++)
                {
                    actor.NodePos[i] = Profile.RestWorldPos[i];
                    actor.NodeRot[i] = Profile.RestWorldRot[i];
                }
            }
            else
            {
                frame = MathX.Clamp(frame, 0, Clip.Frames - 1);
                Clip.EvaluateNodes(frame, actor.NodePos, actor.NodeRot);
                actor.Scale = Clip.ScaleAt(frame);
                foreach ((int channel, float[] curve) in _face) actor.Face.Set(channel, curve[frame]);
                if (devices)
                {
                    if (Clip.Camera != null && Clip.CameraValid[frame])
                        f.Markers.Add(new Marker
                        {
                            Kind = MarkerKind.Camera, Pose = Clip.Camera[frame], Color = CameraColor, Label = "camera"
                        });
                    foreach (var d in _devices)
                        if (d.Valid[frame])
                            f.Markers.Add(new Marker
                            {
                                Kind = d.Kind, Pose = d.Poses[frame], Color = d.Color, Size = _deviceSize,
                                NoDropLine = true
                            });
                }
            }

            if (Placement.pos.SqrLength > 0 || Math.Abs(Placement.rot.w) < 1f) Move(f, actor);
            return actor;
        }

        void Move(ViewFrame f, ActorView actor)
        {
            var d = Placement;
            for (int i = 0; i < actor.NodePos.Length; i++)
            {
                actor.NodePos[i] = d.TransformPoint(actor.NodePos[i]);
                actor.NodeRot[i] = (d.rot * actor.NodeRot[i]).Normalized;
            }

            for (int i = 0; i < f.Markers.Count; i++)
            {
                var m = f.Markers[i];
                m.Pose = d * m.Pose;
                f.Markers[i] = m;
            }
        }

        struct Device
        {
            public Pose[] Poses;
            public bool[] Valid;
            public MarkerKind Kind;
            public Rgba Color;
        }
    }
}
