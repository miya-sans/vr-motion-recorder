using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VRMR
{
    // =====================================================================================================  punch-in
    //
    // Re-recording chosen inputs (body parts by device, face tracking, finger tracking) against scene playback, like a
    // punch-in in a DAW:
    //   armed -> recording starts (Record button, the global record hotkey, the avatar's record parameter)
    //   -> the scene plays from PreRoll before the punch-in point, count-in beeps (the performer is in VR)
    //   -> the performer stops the recording (hotkey) or it stops by itself (scene end / a set length)
    //   -> the take lands as a strip on a new INPUT track: its chosen devices replace the base take's (re-solved),
    //      face / fingers / params layer directly. The pre-roll stays in the strip as a handle before its start.
    // Timing: everything runs on the shared Clock. The take's t = 0, the moment the scene started playing and the beeps
    // are all known on it, so take time maps exactly to scene time. The performer's own latency (audio output + reaction)
    // is measured from trigger taps on the count-in beeps when there are any, else a remembered value is used.

    /// <summary>What a punch-in records and how (remembered between sessions in %APPDATA%\VRMotionRecorder\punchin.json).</summary>
    public sealed class PunchInSettings
    {
        /// <summary>Tests: keep the file somewhere else.</summary>
        public static string DirOverride;

        public InputAlign Align = InputAlign.Head;
        public double Blend = 0.2;
        public bool CountIn = true;
        public bool KeepArmed = true;

        /// <summary>The performer's latency (s): audio out + reaction; the take is moved earlier by this.</summary>
        public double Latency;

        public double Length = 10;

        /// <summary>A new attempt mutes the previous attempt's track.</summary>
        public bool MutePrevious = true;

        public InputPart Parts = PunchIn.Arms;
        public double PreRoll = 3;

        /// <summary>0 = stop by hand (record hotkey), 1 = at the end of the scene, 2 = after <see cref="Length" /> seconds.</summary>
        public int StopMode;

        /// <summary>Measure the latency from trigger taps on the count-in beeps (when there are any).</summary>
        public bool TapSync = true;

        public static string FilePath => Path.Combine(DirOverride ?? Settings.AppDataDir, "punchin.json");

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                ["parts"] = InputMask.ToJson(Parts), ["preRoll"] = PreRoll, ["countIn"] = CountIn,
                ["stopMode"] = StopMode, ["length"] = Length,
                ["latency"] = Latency, ["tapSync"] = TapSync, ["mutePrevious"] = MutePrevious,
                ["keepArmed"] = KeepArmed, ["blend"] = Blend,
                ["align"] = Align.ToString().ToLowerInvariant()
            };
        }

        public static PunchInSettings FromJson(Dictionary<string, object> d)
        {
            var s = new PunchInSettings();
            if (d == null) return s;
            s.Parts = InputMask.FromJson(d.ContainsKey("parts") ? d["parts"] : null, PunchIn.Arms);
            s.PreRoll = MathX.Clamp(d.GetNum("preRoll", 3), 0, 30);
            s.CountIn = d.GetBool("countIn", true);
            s.StopMode = MathX.Clamp((int)d.GetNum("stopMode"), 0, 2);
            s.Length = Math.Max(0.5, d.GetNum("length", 10));
            s.Latency = MathX.Clamp(d.GetNum("latency"), -0.5, 1);
            s.TapSync = d.GetBool("tapSync", true);
            s.MutePrevious = d.GetBool("mutePrevious", true);
            s.KeepArmed = d.GetBool("keepArmed", true);
            s.Blend = MathX.Clamp(d.GetNum("blend", 0.2), 0, 2);
            if (Enum.TryParse(d.GetStr("align", "head"), true, out InputAlign a)) s.Align = a;
            return s;
        }

        public static PunchInSettings Load()
        {
            try
            {
                if (File.Exists(FilePath)) return FromJson(Json.ParseObject(File.ReadAllText(FilePath)));
            }
            catch (Exception ex) { AppLog.Warn("Punch-in settings: " + ex.Message); }

            return new PunchInSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, Json.Serialize(ToJson(), true));
            }
            catch (Exception ex) { AppLog.Warn("Punch-in settings: " + ex.Message); }
        }
    }

    /// <summary>One punch-in attempt: what was chosen and when things happened on the shared Clock.</summary>
    public sealed class PunchInTake
    {
        public string ActorId = "";
        public int Attempt = 1;

        /// <summary>Count-in beeps (offsets from the start of playback, s); the last one is the punch-in.</summary>
        public double[] Beeps = new double[0];

        /// <summary>Performer latency applied (s), and how it was found.</summary>
        public double Latency;

        public string LatencyHow = "";
        public InputPart Parts;

        /// <summary>Clock time the scene started playing (at PunchIn - PreRoll), and the take's t = 0 on the Clock.</summary>
        public double PlayClock0, RecZeroClock;

        /// <summary>Scene time where the new layer takes over, and the count-in before it (s).</summary>
        public double PunchIn, PreRoll;

        /// <summary>
        ///     How RecZeroClock was found: "recorder" (exact), "samples" (first device samples), "handler" (when we heard of
        ///     it).
        /// </summary>
        public string RecZeroHow = "";

        /// <summary>Scene time where the recording stops by itself (&lt; 0 = by hand).</summary>
        public double StopAt = -1;

        public int Taps;

        public double SceneStart => PunchIn - PreRoll;

        /// <summary>Scene time at the take's t = 0 (as played).</summary>
        public double SceneAtRecZero => SceneStart + (RecZeroClock - PlayClock0);

        /// <summary>How long after the take's t = 0 the scene started playing (s).</summary>
        public double StartLatency => PlayClock0 - RecZeroClock;

        /// <summary>Take time of each beep (as sent; the performer hears it a little later).</summary>
        public IEnumerable<double> BeepTakeTimes => Beeps.Select(b => PlayClock0 + b - RecZeroClock);

        /// <summary>Scene time playing at a Clock time.</summary>
        public double SceneTime(double clock)
        {
            return SceneStart + (clock - PlayClock0);
        }

        /// <summary>Scene time a take time belongs to (latency compensated): the strip's mapping.</summary>
        public double SceneOf(double takeT)
        {
            return SceneAtRecZero + takeT - Latency;
        }

        public string Describe()
        {
            return
                $"take t=0 at scene {SceneAtRecZero:0.000} s (scene started {StartLatency * 1000:0} ms after the take, {RecZeroHow}); " +
                $"latency {Latency * 1000:0} ms ({LatencyHow})";
        }
    }

    public static class PunchIn
    {
        public static readonly InputPart Arms = InputPart.LeftHand | InputPart.RightHand | InputPart.LeftElbow |
                                                InputPart.RightElbow;

        public static readonly InputPart Legs = InputPart.LeftFoot | InputPart.RightFoot | InputPart.LeftKnee |
                                                InputPart.RightKnee;

        /// <summary>What can be re-recorded, in menu order (devices by body part, face, fingers).</summary>
        public static readonly (string Name, InputPart Parts, string Tip)[] Presets =
        {
            ("Arms", Arms, "both controllers and elbow trackers"),
            ("Left arm", InputPart.LeftHand | InputPart.LeftElbow, "left controller and elbow tracker"),
            ("Right arm", InputPart.RightHand | InputPart.RightElbow, "right controller and elbow tracker"),
            ("Legs", Legs, "foot and knee trackers"),
            ("Hips + legs", Legs | InputPart.Hips, "hip, knee and foot trackers"),
            ("Head", InputPart.Head, "the headset"),
            ("Upper body", InputPart.Head | Arms | InputPart.Chest, "headset, controllers, chest and elbow trackers"),
            ("Whole body", InputMask.Devices, "every device (face and fingers stay)"),
            ("Face tracking", InputPart.Face, "face tracking only (the body stays)"),
            ("Finger tracking", InputMask.Fingers, "finger tracking only"),
            ("Face + fingers", InputPart.Face | InputMask.Fingers, "face and finger tracking"),
            ("Arms + fingers", Arms | InputMask.Fingers, "controllers, elbows and finger tracking")
        };

        public static string NameOf(InputPart p)
        {
            foreach (var x in Presets)
                if (x.Parts == p)
                    return x.Name;
            return InputMask.Describe(p);
        }

        /// <summary>
        ///     Count-in beeps for a pre-roll: one per second before the punch-in (offsets from the start of playback), then
        ///     the punch-in.
        /// </summary>
        public static double[] Beeps(double preRoll)
        {
            var l = new List<double>();
            for (int n = (int)Math.Floor(preRoll + 1e-6); n >= 1; n--) l.Add(preRoll - n);
            l.Add(preRoll);
            return l.Where(x => x >= -1e-9).ToArray();
        }

        // ---------------------------------------------------------------- performer latency from trigger taps

        /// <summary>Times (take s) a trigger was pressed (either hand), from the take's "triggers" events.</summary>
        public static List<double> TriggerPresses(Recording rec)
        {
            var res = new List<double>();
            bool l = false, r = false;
            lock (rec.Sync)
                foreach (var e in rec.Events.Where(e => e.Type == "triggers").OrderBy(e => e.T))
                {
                    bool nl = e.Text == "both" || e.Text == "left", nr = e.Text == "both" || e.Text == "right";
                    if ((nl && !l) || (nr && !r)) res.Add(e.T);
                    l = nl;
                    r = nr;
                }

            return res;
        }

        /// <summary>
        ///     The performer's latency from trigger taps on the count-in beeps: each press within 0.35 s of a beep is matched
        ///     to it; with 2+ matches whose offsets agree (spread under 90 ms), the median offset. False when there are none.
        /// </summary>
        public static bool MeasureLatency(IEnumerable<double> presses, IEnumerable<double> beeps, out double latency,
            out int taps)
        {
            latency = 0;
            taps = 0;
            var offs = new List<double>();
            var bl = beeps.ToList();
            foreach (double b in bl)
            {
                double? best = presses.Where(p => p > b - 0.15 && p < b + 0.35).OrderBy(p => Math.Abs(p - b - 0.08))
                    .Cast<double?>().FirstOrDefault();
                if (best.HasValue) offs.Add(best.Value - b);
            }

            taps = offs.Count;
            if (offs.Count < 2) return false;
            offs.Sort();
            double med = offs.Count % 2 == 1
                ? offs[offs.Count / 2]
                : (offs[offs.Count / 2 - 1] + offs[offs.Count / 2]) / 2;
            var close = offs.Where(o => Math.Abs(o - med) < 0.09).ToList();
            if (close.Count < 2) return false;
            latency = close.Average();
            taps = close.Count;
            return true;
        }

        // ---------------------------------------------------------------- landing the take

        /// <summary>Does the actor have a take strip under [a, b) on its tracks (what an input layer would change)?</summary>
        public static bool HasBase(Scene s, Actor a, double t0, double t1)
        {
            return a.Tracks.Any(t =>
                t.Kind == TrackKind.Pose && !t.Mute &&
                t.Strips.Any(x => x.Kind == StripKind.Take && !x.Mute && x.Start < t1 && x.End > t0));
        }

        /// <summary>
        ///     Adds a finished punch-in take to the scene: a take reference, a new input track on top of the actor's tracks and
        ///     a strip whose source time maps onto the scene exactly as it was performed (pre-roll kept as a handle before the
        ///     strip's start). With nothing under it to change, the take lands on a new pose track instead (a first take).
        /// </summary>
        public static Strip Land(Scene s, PunchInTake t, PunchInSettings o, string path, string name, string created,
            double duration, out NlaTrack track)
        {
            track = null;
            var actor = s.ActorById(t.ActorId) ?? s.Actors.FirstOrDefault();
            if (actor == null)
            {
                actor = new Actor { Id = Scene.NewId("a"), Name = "Actor 1" };
                s.Actors.Add(actor);
            }

            var tr = s.Takes.FirstOrDefault(x =>
                string.Equals(x.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
            if (tr == null)
            {
                tr = new TakeRef
                {
                    Id = Scene.NewId("t"), Path = Path.GetFullPath(path), Name = name, Created = created,
                    Duration = duration
                };
                s.Takes.Add(tr);
            }

            double zero = t.SceneOf(0); // scene time of the take's t = 0
            double srcIn = MathX.Clamp(t.PunchIn - zero, 0, duration);
            double srcOut = duration;
            if (t.StopAt > t.PunchIn) srcOut = Math.Min(srcOut, t.StopAt - zero);
            if (srcOut - srcIn < 1.0 / s.Fps)
            {
                srcIn = 0;
                srcOut = Math.Max(1.0 / s.Fps, duration);
            }

            bool layer = HasBase(s, actor, zero + srcIn, zero + srcOut);
            string label = NameOf(t.Parts);
            track = SceneOps.AddTrack(actor, actor.Tracks.Count,
                layer ? $"Rec {label} {t.Attempt}" : $"Take {t.Attempt}");
            track.Kind = layer ? TrackKind.Input : TrackKind.Pose;
            var st = SceneOps.AddTakeStrip(s, track, tr, zero + srcIn, srcIn, srcOut);
            st.Name = layer ? $"{label} ({name})" : name;
            if (layer)
            {
                st.Inputs = t.Parts;
                st.InputAlign = o.Align;
                st.BlendIn = Math.Min(o.Blend, st.Length / 3);
                st.BlendOut = Math.Min(o.Blend, st.Length / 3);
            }

            return st;
        }

        // ---------------------------------------------------------------- the take's t = 0 on the shared Clock

        /// <summary>
        ///     The take's t = 0 on the Clock without the recorder's own value: every device sample is stamped
        ///     "Clock at the poll - t0" when it is polled, so t0 &lt;= (when we see it) - (its time). The smallest such bound
        ///     over a few polls is within a poll interval (~11 ms at 90 Hz) of the truth.
        /// </summary>
        public sealed class RecZeroEstimator
        {
            public double Best = double.PositiveInfinity;
            public int Seen;

            public void Observe(Recording rec, double clockNow)
            {
                double last = double.NegativeInfinity;
                lock (rec.Sync)
                    foreach (var s in rec.Streams)
                        if (s.Count > 0)
                            last = Math.Max(last, s.T[s.Count - 1]);
                if (double.IsNegativeInfinity(last)) return;
                Best = Math.Min(Best, clockNow - last);
                Seen++;
            }
        }
    }
}
