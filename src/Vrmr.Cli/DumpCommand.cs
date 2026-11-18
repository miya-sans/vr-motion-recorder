using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace VRMR.Cli
{
    /// <summary>
    ///     vrmr dump &lt;takesDir&gt; &lt;outDir&gt; [filter] [--no-exports]: a reference dump for regression checks. Solves
    ///     every take in
    ///     takesDir (with the avatar library in takesDir\Avatars) and writes, per take: a summary line with hashes of the
    ///     solve
    ///     (hips + 55 world rotations per frame), the humanoid bone positions, face, parameters, camera and trackers; the bone
    ///     positions and the other curves as raw floats (.nodes.bin, .aux.bin) for numeric comparison; the clip exports; and
    ///     one
    ///     scene bake. Two dumps are compared with tools/compare_dumps.py. Only reads the takes; writes only to outDir.
    /// </summary>
    public static class DumpCommand
    {
        static string Sha(byte[] b)
        {
            using (var s = SHA256.Create())
                return BitConverter.ToString(s.ComputeHash(b)).Replace("-", "").Substring(0, 16).ToLowerInvariant();
        }

        static string ShaFile(string p)
        {
            return Sha(File.ReadAllBytes(p));
        }

        static void W(BinaryWriter w, Vec3 v)
        {
            w.Write(v.x);
            w.Write(v.y);
            w.Write(v.z);
        }

        static void W(BinaryWriter w, Quat q)
        {
            w.Write(q.x);
            w.Write(q.y);
            w.Write(q.z);
            w.Write(q.w);
        }

        static byte[] Floats(IEnumerable<KeyValuePair<string, float[]>> d)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            foreach (var kv in d.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                w.Write(kv.Key);
                foreach (float f in kv.Value) w.Write(f);
            }

            return ms.ToArray();
        }

        static byte[] Poses(IEnumerable<KeyValuePair<string, Pose[]>> d)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            foreach (var kv in d.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                w.Write(kv.Key);
                foreach (var p in kv.Value)
                {
                    W(w, p.pos);
                    W(w, p.rot);
                }
            }

            return ms.ToArray();
        }

        static void DumpClip(BakedClip c, string file, StringBuilder sum, string label)
        {
            var P = c.Profile;
            var wp = new Vec3[P.Nodes.Count];
            var wr = new Quat[P.Nodes.Count];
            var solve = new MemoryStream();
            var sw = new BinaryWriter(solve);
            using (var fs = new BinaryWriter(File.Create(file)))
            {
                int nb = 0;
                for (int b = 0; b < HumanBones.Count; b++)
                    if (P.Human[b] >= 0)
                        nb++;
                fs.Write(c.Frames);
                fs.Write(nb);
                for (int f = 0; f < c.Frames; f++)
                {
                    W(sw, c.Hips[f]);
                    for (int b = 0; b < c.Rot[f].Length; b++) W(sw, c.Rot[f][b]);
                    sw.Write(c.ScaleAt(f));
                    c.EvaluateNodes(f, wp, wr);
                    for (int b = 0; b < HumanBones.Count; b++)
                    {
                        int ni = P.Human[b];
                        if (ni >= 0) W(fs, wp[ni]);
                    }
                }
            }

            using (var ax = new BinaryWriter(File.Create(file.Replace(".nodes.bin", ".aux.bin"))))
            {
                void Sec(string n, IEnumerable<float> v)
                {
                    float[] arr = v.ToArray();
                    ax.Write(n);
                    ax.Write(arr.Length);
                    foreach (float x in arr) ax.Write(x);
                }

                foreach (var kv in c.Face.OrderBy(k => k.Key, StringComparer.Ordinal)) Sec("face/" + kv.Key, kv.Value);
                foreach (var kv in c.ParamValues.OrderBy(k => k.Key, StringComparer.Ordinal))
                    Sec("param/" + kv.Key, kv.Value);
                foreach (var kv in c.Trackers.OrderBy(k => k.Key, StringComparer.Ordinal))
                    Sec("tracker/" + kv.Key,
                        kv.Value.SelectMany(p => new[]
                            { p.pos.x, p.pos.y, p.pos.z, p.rot.x, p.rot.y, p.rot.z, p.rot.w }));
                if (c.Camera != null)
                    Sec("camera",
                        c.Camera.SelectMany(p => new[]
                            { p.pos.x, p.pos.y, p.pos.z, p.rot.x, p.rot.y, p.rot.z, p.rot.w }));
                Sec("solve",
                    Enumerable.Range(0, c.Frames).SelectMany(f =>
                        new[] { c.Hips[f].x, c.Hips[f].y, c.Hips[f].z }.Concat(c.Rot[f]
                            .SelectMany(q => new[] { q.x, q.y, q.z, q.w }))));
            }

            var cam = new MemoryStream();
            var cw = new BinaryWriter(cam);
            if (c.Camera != null)
                for (int f = 0; f < c.Camera.Length; f++)
                {
                    W(cw, c.Camera[f].pos);
                    W(cw, c.Camera[f].rot);
                    cw.Write(c.CameraValid[f]);
                }

            sum.AppendLine(
                $"{label} frames {c.Frames} body {c.BodySource} avatar {P.Name} scale {c.Scale:R} solve {Sha(solve.ToArray())} nodes {ShaFile(file)} face {c.Face.Count}:{Sha(Floats(c.Face))} params {c.ParamValues.Count}:{Sha(Floats(c.ParamValues))} camera {Sha(cam.ToArray())} trackers {c.Trackers.Count}:{Sha(Poses(c.Trackers))} warnings {c.Warnings.Count} held {c.Held.Count}");
        }

        public static int Run(string[] a)
        {
            bool exports = !a.Contains("--no-exports");
            a = a.Where(x => !x.StartsWith("--")).ToArray();
            string dir = Path.GetFullPath(a[0]), outDir = Path.GetFullPath(a[1]);
            Directory.CreateDirectory(outDir);
            string filter = a.Length > 2 ? a[2] : "";
            AppLog.Init(Path.Combine(outDir, "log"));
            BodySolverSetup.Register();
            CalibrationLibrary.DirOverride = Path.Combine(outDir, "callib");
            Directory.CreateDirectory(CalibrationLibrary.DirOverride);
            var S = new Settings { RecordingsDir = dir, AvatarLibraryDir = Path.Combine(dir, "Avatars") };
            S.UnityProjects.Clear();
            S.AvatarModelMap.Clear();
            var lib = new AvatarLibrary(S);
            PoseBuilder.SourceResolver = r => lib.MatchTake(r).Entry?.Profile();
            Func<Recording, BakeOptions> optFor = r => new BakeOptions
                { Fps = 60, Smoothing = S.Smoothing, Calibration = (r.Edits.Calib ?? S.CalibrationDefaults).Clone() };
            var sum = new StringBuilder();
            var times = new StringBuilder();
            var takes = Directory.GetFiles(dir, "*.vrmr").Where(t => t.Contains(filter))
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
            foreach (string p in takes)
            {
                string name = Path.GetFileNameWithoutExtension(p);
                var r = RecordingIO.Load(p);
                var target = lib.TargetFor(r, out _);
                var sw = Stopwatch.StartNew();
                var c = PoseBuilder.Bake(r, target, optFor(r));
                times.AppendLine($"{name} solve {sw.ElapsedMilliseconds} ms");
                DumpClip(c, Path.Combine(outDir, name + ".nodes.bin"), sum, "take " + name);
                var cal = c.Calibration;
                sum.AppendLine($"  calib {name} " + (cal == null ? "none" : $"scale {cal.TrackScale:R}"));
                // exports: armature FBX + anims + face json + sidecar for every take; the avatar-mesh FBX for two
                string ed = Path.Combine(outDir, "export", name);
                bool withMesh = name.EndsWith("05-11-48") || name.EndsWith("11-39-27");
                if (!exports) continue;
                var res = ClipExport.Write(c, ed, name,
                    new ClipExportOptions
                    {
                        Fbx = withMesh, FbxAvatarMesh = true, FbxArmature = true, FbxCamera = true, FbxTrackers = true,
                        BodyAnim = true, FaceAnim = true, ParamsAnim = true, AvatarName = target?.Name ?? ""
                    });
                foreach (string f in Directory.GetFiles(ed, "*", SearchOption.TopDirectoryOnly)
                             .OrderBy(x => x, StringComparer.Ordinal))
                    sum.AppendLine($"  export {Path.GetFileName(f)} {new FileInfo(f).Length}" +
                                   (f.EndsWith(".fbx") ? "" : " " + ShaFile(f)));
            }

            // ---- a scene: two takes with a transition, an upper-body layer from a third, a foot-lock modifier
            if (filter == "")
            {
                var ctx = new SceneContext
                {
                    AvatarById = id => lib.All().FirstOrDefault(e => e.Id == id)?.Profile(),
                    TakeAvatar = r => lib.TargetFor(r, out _), BakeOptionsFor = optFor
                };
                var ev = new SceneEvaluator(ctx);

                TakeRef Ref(string id, string p)
                {
                    var sm = RecordingIO.ReadSummary(p);
                    return new TakeRef
                        { Id = id, Path = p, Name = sm.Name, Created = sm.Created, Duration = sm.Duration };
                }

                var s = Scene.CreateNew("Parity");
                s.Fps = 60;
                var tA = Ref("tA", takes.First(t => t.Contains("07-32-39")));
                var tB = Ref("tB", takes.First(t => t.Contains("10-37-59")));
                var tC = Ref("tC", takes.First(t => t.Contains("05-11-48")));
                s.Takes.Add(tA);
                s.Takes.Add(tB);
                s.Takes.Add(tC);
                var act = s.Actors[0];
                var s1 = SceneOps.AddTakeStrip(s, act.Tracks[0], tA, 0, 30, 42);
                var s2 = SceneOps.AddTakeStrip(s, act.Tracks[0], tB, 12, 70, 82);
                SceneOps.AddTransition(act.Tracks[0], s1, s2, 1.0);
                s2.Modifiers.Add(new StripModifier { Type = "footlock" });
                var t2 = new NlaTrack { Id = Scene.NewId("k"), Name = "Upper" };
                act.Tracks.Add(t2);
                var s3 = SceneOps.AddTakeStrip(s, t2, tC, 4, 10, 16);
                s3.Mask = BodyMask.UpperBody;
                s3.BlendIn = 0.5f;
                s3.BlendOut = 0.5f;
                var bake = SceneExport.BakeComplete(ev, s);
                DumpClip(bake.Actors[0].Clip, Path.Combine(outDir, "scene.nodes.bin"), sum, "scene Parity");
                sum.AppendLine($"  scene warnings {bake.Warnings.Count} transitions {act.Tracks[0].Transitions.Count}");
                ev.Dispose();
            }

            File.WriteAllText(Path.Combine(outDir, "summary" + (filter == "" ? "" : "_" + filter) + ".txt"),
                sum.ToString());
            File.WriteAllText(Path.Combine(outDir, "times" + (filter == "" ? "" : "_" + filter) + ".txt"),
                times.ToString());
            Console.Write(sum.ToString());
            return 0;
        }
    }
}
