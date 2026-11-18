using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace VRMR
{
    public static class MiscExporters
    {
        static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        /// <summary>
        ///     JSON compatible with the OSC_Recorder Unity tool (VRCFT_OscRecorder): replays parameters into VRChat.
        ///     Uses the edited timeline; each segment starts with a full state snapshot so cuts stay consistent.
        /// </summary>
        public static void ExportOscRecorderJson(Recording rec, string path, string clipName,
            bool includeBinaryBits = true)
        {
            var frames = new List<Tuple<double, string, ParamType, float>>();
            double dur = rec.Duration;
            var segs = rec.Edits.Segments(dur);
            double off = rec.Edits.FaceOffset;
            double acc = 0;
            var names = new HashSet<string>(rec.Params.Select(p => p.Name));
            var tracks = rec.Params.Where(p => p.Export && (includeBinaryBits || !p.IsBinaryBit(names))).ToList();
            foreach (double[] s in segs)
            {
                foreach (var p in tracks)
                {
                    frames.Add(Tuple.Create(acc, p.Name, p.Type, p.SampleStep(s[0] - off)));
                    for (int i = 0; i < p.Count; i++)
                    {
                        double t = p.T[i] + off;
                        if (t <= s[0] || t > s[1]) continue;
                        frames.Add(Tuple.Create(acc + (t - s[0]), p.Name, p.Type, p.V[i]));
                    }
                }

                acc += s[1] - s[0];
            }

            frames.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            var sb = new StringBuilder();
            sb.Append("{\n    \"ClipName\": ").Append(Q(clipName)).Append(",\n");
            sb.Append("    \"RecordedDate\": ").Append(Q(rec.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")))
                .Append(",\n");
            sb.Append("    \"Duration\": ").Append(acc.ToString("R", CI)).Append(",\n    \"Frames\": [\n");
            for (int i = 0; i < frames.Count; i++)
            {
                var f = frames[i];
                string tag = f.Item3 == ParamType.Bool ? f.Item4 > 0.5f ? ",T" : ",F" :
                    f.Item3 == ParamType.Int ? ",i" : ",f";
                sb.Append("        {\n            \"Time\": ").Append(((float)f.Item1).ToString("R", CI))
                    .Append(",\n            \"Address\": ").Append(Q("/avatar/parameters/" + f.Item2))
                    .Append(",\n            \"TypeTag\": ").Append(Q(tag))
                    .Append(",\n            \"FloatVal\": ").Append(f.Item4.ToString("R", CI))
                    .Append(",\n            \"BoolVal\": ")
                    .Append(f.Item3 == ParamType.Bool && f.Item4 > 0.5f ? "true" : "false")
                    .Append("\n        }").Append(i < frames.Count - 1 ? ",\n" : "\n");
            }

            sb.Append("    ]\n}");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>
        ///     HUMR v2 compatible log (open with HUMR's HumrRecordingLoader in Unity to make humanoid .anim/.fbx with
        ///     HumanPoseHandler).
        ///     Works for every body source, including the IK reconstruction.
        /// </summary>
        public static void ExportHumrLog(BakedClip clip, string path, string target)
        {
            var sb = new StringBuilder();
            long take = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var start = DateTime.Now;
            for (int f = 0; f < clip.Frames; f++)
            {
                double t = f / (double)clip.Fps;
                sb.Append(start.AddSeconds(t).ToString("yyyy.MM.dd HH:mm:ss", CI))
                    .Append(" Debug      -  [HUMR] RECORDING;BoneRotations;")
                    .Append(target).Append(';').Append(take).Append(';').Append(((float)t).ToString("F6", CI))
                    .Append(';');
                var h = clip.Hips[f];
                sb.Append(h.x.ToString("F6", CI)).Append(',').Append(h.y.ToString("F6", CI)).Append(',')
                    .Append(h.z.ToString("F6", CI));
                var r = clip.Rot[f];
                for (int b = 0; b < HumanBones.Count; b++)
                {
                    var q = clip.Profile.Has(b) ? r[b] : Quat.Identity;
                    sb.Append(';').Append(q.x.ToString("F6", CI)).Append(',').Append(q.y.ToString("F6", CI)).Append(',')
                        .Append(q.z.ToString("F6", CI)).Append(',').Append(q.w.ToString("F6", CI));
                }

                sb.Append('\n');
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>Face + parameter curves as JSON (used by the Blender add-on to key any mesh's shape keys).</summary>
        public static void ExportFaceJson(BakedClip clip, string path, Dictionary<string, string> rename,
            Dictionary<string, object> meta = null)
        {
            var shapes = new Dictionary<string, object>();
            foreach (var kv in clip.Face)
            {
                string name = rename != null && rename.TryGetValue(kv.Key, out string rn) ? rn : kv.Key;
                shapes[name] = kv.Value.Select(v => (object)Math.Round(v, 4)).ToList();
            }

            var pars = new Dictionary<string, object>();
            foreach (var kv in clip.ParamValues) pars[kv.Key] = kv.Value.Select(v => (object)Math.Round(v, 4)).ToList();
            var types = new Dictionary<string, object>();
            foreach (var kv in clip.ParamTypes) types[kv.Key] = kv.Value.ToString();
            var d = new Dictionary<string, object>
            {
                ["fps"] = (double)clip.Fps, ["frames"] = clip.Frames, ["shapes"] = shapes, ["params"] = pars,
                ["paramTypes"] = types
            };
            if (meta != null)
                foreach (var kv in meta)
                    d[kv.Key] = kv.Value;
            File.WriteAllText(path, Json.Serialize(d), new UTF8Encoding(false));
        }

        public static void ExportCsv(BakedClip clip, string path)
        {
            var cols = clip.ParamValues.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            var shapes = clip.Face.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                w.WriteLine("time," + string.Join(",", cols.Select(c => "param:" + c)) + (shapes.Count > 0 ? "," : "") +
                            string.Join(",", shapes.Select(s => "shape:" + s)));
                for (int f = 0; f < clip.Frames; f++)
                {
                    var sb = new StringBuilder();
                    sb.Append((f / (double)clip.Fps).ToString("F4", CI));
                    foreach (string c in cols) sb.Append(',').Append(clip.ParamValues[c][f].ToString("G6", CI));
                    foreach (string s in shapes) sb.Append(',').Append(clip.Face[s][f].ToString("G6", CI));
                    w.WriteLine(sb.ToString());
                }
            }
        }

        static string Q(string s)
        {
            return Json.Serialize(s);
        }
    }
}
