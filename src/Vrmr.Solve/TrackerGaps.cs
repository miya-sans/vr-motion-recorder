using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace VRMR
{
    /// <summary>A stretch where one tracker wasn't tracking (detected, or marked by hand in the Clip tab).</summary>
    public sealed class TrackerGap
    {
        public string Serial = "";
        /// <summary>Last good sample before the loss and first good one after it (take seconds); B = +inf when it never came back.</summary>
        public double A, B;
        public string Why = "";
        /// <summary>Marked by hand (EditList.TrackerGaps) rather than detected.</summary>
        public bool Manual;
        /// <summary>A detected gap the user chose to keep as recorded (not repaired).</summary>
        public bool Ignored;
        public double Length => double.IsPositiveInfinity(B) ? double.PositiveInfinity : B - A;
    }

    /// <summary>
    /// Tracking loss per tracker (lying on a tracker, occlusion, a tracker that dies) and the fill used instead of the
    /// recorded pose there. Detected from the take:
    ///  * SteamVR's own tracking result, when the take recorded it ("tracking" events: anything but "running OK");
    ///  * gaps: no valid pose for more than <see cref="MinGap"/> (SteamVR reported the pose invalid);
    ///  * frozen poses: the same pose repeated for at least <see cref="MinFrozen"/> (SteamVR holding the last pose);
    ///  * glitches: the tracker moving faster than <see cref="MaxSpeed"/> (a person's tracked body parts don't).
    /// The fill carries the tracker with a neighbouring tracker on the same body segment (knee with the foot, foot with the
    /// knee, elbow with the hand, hips with the chest and so on; else the headset), blending its offset from the one before
    /// the loss to the one after it, so it moves with the body and lands exactly on the recovered pose.
    /// </summary>
    public static class TrackerGaps
    {
        public static double MinGap = 0.1, MinFrozen = 0.08;
        /// <summary>Real metres per second; faster single steps are tracking glitches.</summary>
        public static float MaxSpeed = 12f;
        /// <summary>A single jump bigger than this (m) that stays is a drifted pose snapping back: the repair starts <see cref="SnapBlend"/> s before it.</summary>
        public static float SnapStep = 0.2f;
        public static double SnapBlend = 0.3;
        /// <summary>Diagnostics: false turns detection and repair off.</summary>
        public static bool Enabled = true;

        static readonly ConditionalWeakTable<Recording, List<TrackerGap>> _cache = new ConditionalWeakTable<Recording, List<TrackerGap>>();
        static readonly object _lock = new object();

        public static void Invalidate(Recording rec) { lock (_lock) _cache.Remove(rec); }

        /// <summary>Detected gaps of every tracker with the take's manual marks applied (cached per take).</summary>
        public static List<TrackerGap> For(Recording rec)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(rec, out var hit)) return hit;
                var all = new List<TrackerGap>();
                foreach (var s in rec.Devices.Where(d => d.Count > 2 && (d.DeviceClass == 3 || (d.DeviceClass == 2 && d.ControllerRole != 1 && d.ControllerRole != 2))))
                    all.AddRange(Detect(rec, s));
                // manual: marks add gaps, "keep" marks switch detected ones off
                foreach (var m in rec.Edits.TrackerGaps)
                {
                    if (m.Keep)
                    {
                        foreach (var g in all.Where(g => g.Serial == m.Serial && g.A < m.B && g.B > m.A)) g.Ignored = true;
                        continue;
                    }
                    all.Add(new TrackerGap { Serial = m.Serial, A = m.A, B = m.B, Why = "marked by hand", Manual = true });
                }
                all = all.OrderBy(g => g.Serial).ThenBy(g => g.A).ToList();
                _cache.Add(rec, all);
                return all;
            }
        }

        /// <summary>Loss intervals of one tracker stream: [last good sample, first good sample].</summary>
        public static List<TrackerGap> Detect(Recording rec, PoseStream s)
        {
            var res = new List<TrackerGap>();
            if (!Enabled || s.Count < 3) return res;
            var bad = new List<Tuple<double, double, string>>();
            // SteamVR's tracking result, when recorded
            double lostAt = double.NaN;
            foreach (var e in rec.Events.Where(e => e.Type == "tracking" && (e.Text ?? "").StartsWith(s.Serial + " ")).OrderBy(e => e.T))
            {
                bool ok = e.Text.EndsWith(" 200");
                if (!ok && double.IsNaN(lostAt)) lostAt = e.T;
                if (ok && !double.IsNaN(lostAt)) { bad.Add(Tuple.Create(lostAt, e.T, "SteamVR: not tracking")); lostAt = double.NaN; }
            }
            if (!double.IsNaN(lostAt)) bad.Add(Tuple.Create(lostAt, double.PositiveInfinity, "SteamVR: not tracking"));
            int frozenFrom = -1;
            var jumps = PlayspaceJumps.For(rec);
            bool PlayspaceJump(double a, double b) => jumps.Jumps.Any(j => j.Before <= b + 0.02 && j.After >= a - 0.02);
            for (int i = 1; i < s.Count; i++)
            {
                double dt = s.T[i] - s.T[i - 1];
                float step = (s.P[i] - s.P[i - 1]).Length;
                if (step > 0.05f && PlayspaceJump(s.T[i - 1], s.T[i])) continue; // the whole playspace moved (reset / re-center), not this tracker
                if (dt > MinGap) bad.Add(Tuple.Create(s.T[i - 1], s.T[i], $"no pose for {dt:0.0} s"));
                bool same = step < 1e-6f && Quat.Dot(s.R[i], s.R[i - 1]) > 0.9999999f;
                if (same) { if (frozenFrom < 0) frozenFrom = i - 1; }
                else
                {
                    if (frozenFrom >= 0 && s.T[i - 1] - s.T[frozenFrom] >= MinFrozen) bad.Add(Tuple.Create(s.T[frozenFrom], s.T[i], $"frozen for {s.T[i - 1] - s.T[frozenFrom]:0.0} s"));
                    frozenFrom = -1;
                }
                if (dt > 1e-4 && dt <= MinGap && step / dt > MaxSpeed)
                {
                    // a big snap that doesn't come back: the pose before it had drifted (IMU only), blend into it over SnapBlend
                    bool back = i + 1 < s.Count && (s.P[i + 1] - s.P[i - 1]).Length < step * 0.5f;
                    double a = !back && step > SnapStep ? Math.Max(s.T[0], s.T[i - 1] - SnapBlend) : s.T[i - 1];
                    bad.Add(Tuple.Create(a, s.T[i], $"jumped {step * 100:0} cm"));
                }
            }
            if (frozenFrom >= 0 && s.T[s.Count - 1] - s.T[frozenFrom] >= MinFrozen) bad.Add(Tuple.Create(s.T[frozenFrom], double.PositiveInfinity, "frozen to the end"));
            if (rec.Duration - s.T[s.Count - 1] > MinGap) bad.Add(Tuple.Create(s.T[s.Count - 1], double.PositiveInfinity, "no pose to the end of the take"));
            if (s.T[0] > MinGap) bad.Add(Tuple.Create(double.NegativeInfinity, s.T[0], "no pose from the start of the take"));
            // merge overlapping / touching stretches (a glitch right after a freeze is one loss)
            foreach (var b in bad.OrderBy(x => x.Item1))
            {
                var last = res.Count > 0 ? res[res.Count - 1] : null;
                if (last != null && b.Item1 <= last.B + 0.05) { if (b.Item2 > last.B) last.B = b.Item2; if (!last.Why.Contains(b.Item3)) last.Why += ", " + b.Item3; continue; }
                res.Add(new TrackerGap { Serial = s.Serial, A = b.Item1, B = b.Item2, Why = b.Item3 });
            }
            return res;
        }

        public static TrackerGap At(List<TrackerGap> gaps, string serial, double t)
        {
            foreach (var g in gaps) if (!g.Ignored && g.Serial == serial && t > g.A && t < g.B) return g;
            return null;
        }

        /// <summary>Smooth 0..1 blend across a gap (the fill starts on the last good offset and ends on the recovered one).</summary>
        public static float Blend(TrackerGap g, double t)
        {
            if (double.IsPositiveInfinity(g.B)) return 0f;
            if (double.IsNegativeInfinity(g.A)) return 1f;
            float x = (float)MathX.Clamp((t - g.A) / Math.Max(1e-6, g.B - g.A), 0, 1);
            return x * x * (3 - 2 * x);
        }

        /// <summary>The neighbouring trackers (most rigid first) whose motion carries a lost tracker of this role.</summary>
        public static BodyRole[] Carriers(BodyRole r)
        {
            switch (r)
            {
                case BodyRole.LeftKnee: return new[] { BodyRole.LeftFoot, BodyRole.Hips, BodyRole.Head };
                case BodyRole.RightKnee: return new[] { BodyRole.RightFoot, BodyRole.Hips, BodyRole.Head };
                case BodyRole.LeftFoot: return new[] { BodyRole.LeftKnee, BodyRole.Hips, BodyRole.Head };
                case BodyRole.RightFoot: return new[] { BodyRole.RightKnee, BodyRole.Hips, BodyRole.Head };
                case BodyRole.LeftElbow: return new[] { BodyRole.LeftHand, BodyRole.Chest, BodyRole.Head };
                case BodyRole.RightElbow: return new[] { BodyRole.RightHand, BodyRole.Chest, BodyRole.Head };
                case BodyRole.Hips: return new[] { BodyRole.Chest, BodyRole.Head };
                case BodyRole.Chest: return new[] { BodyRole.Hips, BodyRole.Head };
                default: return new[] { BodyRole.Head };
            }
        }
    }
}
