using System;
using System.IO;
using System.Linq;

namespace VRMR.Cli
{
    /// <summary>
    ///     vrmr humanoid &lt;takesDir&gt; &lt;outDir&gt; [filter] [--avatar &lt;libraryId&gt;] [--body ik|humr] [--no-fbx]:
    ///     solves the takes
    ///     (avatar library in takesDir\Avatars), writes each take's Humanoid clip, armature FBX and Unity sidecar, and
    ///     reports how far the pose Unity will play from the clip is from the solved pose. For checking the Humanoid
    ///     export against Unity: &lt;take&gt;.pose.bin holds the solved joint positions, &lt;take&gt;.played.bin the predicted
    ///     ones
    ///     (int frames, int 55, then x y z per bone and frame; NaN for bones the avatar lacks).
    /// </summary>
    public static class HumanoidCommand
    {
        static readonly (string Name, int[] Bones)[] Groups =
        {
            ("hips", new[] { HumanBones.Hips }),
            ("head", new[] { HumanBones.Head }),
            ("hands", new[] { HumanBones.LeftHand, HumanBones.RightHand }),
            ("feet", new[] { HumanBones.LeftFoot, HumanBones.RightFoot }),
            ("fingertips", Enumerable.Range(0, 10).Select(i => HumanBones.LeftThumbProximal + i * 3 + 2).ToArray()),
            ("all", Enumerable.Range(0, HumanBones.Count).ToArray())
        };

        public static int Run(string[] a)
        {
            string Opt(string name)
            {
                int i = Array.IndexOf(a, name);
                return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
            }

            string avatarId = Opt("--avatar"), body = Opt("--body");
            bool fbx = !a.Contains("--no-fbx");
            string[] args = a.Where((x, i) =>
                !x.StartsWith("--") && (i == 0 || (a[i - 1] != "--avatar" && a[i - 1] != "--body"))).ToArray();
            if (args.Length < 2)
            {
                Console.Error.WriteLine(
                    "usage: vrmr humanoid <takesDir> <outDir> [filter] [--avatar <libraryId>] [--body ik|humr] [--no-fbx]");
                return 2;
            }

            string dir = Path.GetFullPath(args[0]),
                outDir = Path.GetFullPath(args[1]),
                filter = args.Length > 2 ? args[2] : "";
            Directory.CreateDirectory(outDir);
            AppLog.Init(Path.Combine(outDir, "log"));
            BodySolverSetup.Register();
            CalibrationLibrary.DirOverride = Path.Combine(outDir, "callib");
            Directory.CreateDirectory(CalibrationLibrary.DirOverride);
            var settings = new Settings { RecordingsDir = dir, AvatarLibraryDir = Path.Combine(dir, "Avatars") };
            settings.UnityProjects.Clear();
            settings.AvatarModelMap.Clear();
            var lib = new AvatarLibrary(settings);
            PoseBuilder.SourceResolver = r => lib.MatchTake(r).Entry?.Profile();
            foreach (string path in Directory.GetFiles(dir, "*.vrmr").Where(t => t.Contains(filter))
                         .OrderBy(x => x, StringComparer.Ordinal))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                var rec = RecordingIO.Load(path);
                if (body != null) rec.Edits.BodySource = body;
                var target = avatarId != null
                    ? lib.All().FirstOrDefault(e => e.Id == avatarId)?.Profile()
                    : lib.TargetFor(rec, out _);
                if (target == null)
                {
                    Console.Error.WriteLine($"{name}: no avatar");
                    continue;
                }

                var clip = PoseBuilder.Bake(rec, target,
                    new BakeOptions
                    {
                        Fps = 60, Smoothing = settings.Smoothing,
                        Calibration = (rec.Edits.Calib ?? settings.CalibrationDefaults).Clone()
                    });
                if (clip.BodySource == "none")
                {
                    Console.Error.WriteLine($"{name}: no body");
                    continue;
                }

                var files = ClipExport.Write(clip, outDir, name, new ClipExportOptions
                {
                    Fbx = false, FbxArmature = fbx, BodyAnim = false, FaceAnim = false, ParamsAnim = false,
                    FaceJson = false, HumanoidAnim = true
                });
                Report(clip, outDir, name, new FileInfo(Path.Combine(outDir, files.Files["humanoidAnim"])).Length);
            }

            return 0;
        }

        /// <summary>Compares the pose the clip's curves give (before key reduction) with the solved pose, per bone group.</summary>
        static void Report(BakedClip clip, string outDir, string name, long bytes)
        {
            var profile = clip.Profile;
            var rig = HumanoidRig.FromProfile(profile);
            var solver = new HumanoidPoseSolver(rig);
            var frames = HumanoidAnimExporter.Convert(clip, rig);
            var wp = new Vec3[profile.Nodes.Count];
            var wr = new Quat[profile.Nodes.Count];
            var pos = new Vec3[HumanBones.Count];
            var rot = new Quat[HumanBones.Count];
            float[,] err = new float[clip.Frames, HumanBones.Count];
            using (var solved = new BinaryWriter(File.Create(Path.Combine(outDir, name + ".pose.bin"))))
            using (var played = new BinaryWriter(File.Create(Path.Combine(outDir, name + ".played.bin"))))
            {
                solved.Write(clip.Frames);
                solved.Write(HumanBones.Count);
                played.Write(clip.Frames);
                played.Write(HumanBones.Count);
                for (int f = 0; f < clip.Frames; f++)
                {
                    clip.EvaluateNodes(f, wp, wr);
                    solver.Evaluate(frames[f], pos, rot);
                    float inv = 1f / Math.Max(1e-6f, clip.ScaleAt(f));
                    for (int b = 0; b < HumanBones.Count; b++)
                    {
                        bool has = rig.Exists[b];
                        var s = has ? wp[profile.Human[b]] * inv : new Vec3(float.NaN, float.NaN, float.NaN);
                        var p = has ? pos[b] : s;
                        solved.Write(s.x);
                        solved.Write(s.y);
                        solved.Write(s.z);
                        played.Write(p.x);
                        played.Write(p.y);
                        played.Write(p.z);
                        err[f, b] = has ? (s - p).Length * 1000f : float.NaN;
                    }
                }
            }

            Console.WriteLine(
                $"{name}: avatar {profile.Name}, body {clip.BodySource}, {clip.Frames} frames, clip {bytes / 1048576.0:0.0} MB; predicted playback vs solve (mm: mean / 95% / max)");
            foreach ((string group, int[] bones) in Groups)
            {
                float[] v = (from f in Enumerable.Range(0, clip.Frames)
                    from b in bones
                    where !float.IsNaN(err[f, b])
                    select err[f, b]).OrderBy(x => x).ToArray();
                if (v.Length > 0)
                    Console.WriteLine(
                        $"  {group,-10} {v.Average():0.000} / {v[(int)(v.Length * 0.95)]:0.000} / {v[v.Length - 1]:0.000}");
            }
        }
    }
}
