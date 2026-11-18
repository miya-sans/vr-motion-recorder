using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace VRMR
{
    /// <summary>
    ///     Puts VRChat's own IK frames (HUMR) back in step with the SteamVR devices. HUMR timestamps frames with VRChat's
    ///     game clock, which falls behind real time on long frames (avatar reloads, calibrations, height changes). On
    ///     a test take (four calibrations, four height changes) the frames were in step for the first 31 s, then
    ///     0.6 s late at 110-141 s and 0.8 s late at 151-167 s; the recorder used to apply one clock offset per HUMR take.
    ///     New takes are timed against a sliding minimum of the frames' arrival (Recorder); older ones are re-aligned here:
    ///     VRChat's head-bone height against the headset height in 16 s windows every 4 s (best correlation within ±1.5 s),
    ///     interpolated between windows. Only the in-memory frame times change; the HUMR clock (SrcT) is kept.
    /// </summary>
    public static class HumrSync
    {
        static readonly ConditionalWeakTable<Recording, Done> _done = new();

        public static bool Enabled = true;

        /// <summary>Re-aligns once per take (in memory); returns a note when it shifted frames, else "".</summary>
        public static string Ensure(Recording rec, AvatarProfile src)
        {
            if (!Enabled || rec == null || src == null) return "";
            lock (_done)
            {
                if (_done.TryGetValue(rec, out var d)) return d.Note;
                d = new Done();
                try { d.Note = Align(rec, src); }
                catch (Exception ex) { AppLog.Warn("HUMR timing: " + ex.Message); }

                _done.Add(rec, d);
                return d.Note;
            }
        }

        static string Align(Recording rec, AvatarProfile src)
        {
            var h = rec.Humr;
            var hmd = rec.Devices.Where(d => d.DeviceClass == 1 && d.Count > 100).OrderByDescending(d => d.Count)
                .FirstOrDefault();
            if (h.Count < 200 || hmd == null || !src.Has(HumanBones.Head)) return "";
            // VRChat's head height per frame (FK on the worn avatar)
            var clip = new BakedClip
            {
                Profile = src, Fps = 1, Frames = 1, BoneExists = src.HumanExists, Scale = 1, Hips = new Vec3[1],
                Rot = new Quat[1][]
            };
            var wp = new Vec3[src.Nodes.Count];
            var wr = new Quat[src.Nodes.Count];
            double[] ts = new double[h.Count];
            double[] hy = new double[h.Count];
            lock (rec.Sync)
                for (int i = 0; i < h.Count; i++)
                {
                    var r = new Quat[HumanBones.Count];
                    for (int b = 0; b < HumanBones.Count; b++)
                    {
                        var q = h.Rot[i][b];
                        r[b] = q.Norm > 0.5f ? q.Normalized : src.HumanRestRot(b);
                    }

                    clip.Hips[0] = h.Hips[i];
                    clip.Rot[0] = r;
                    clip.EvaluateNodes(0, wp, wr);
                    ts[i] = h.T[i];
                    hy[i] = wp[src.Human[HumanBones.Head]].y;
                }

            var notTracking = VrcTimeline.For(rec).NotTracking();
            var knots = new List<Tuple<double, double>>(); // (frame time, lag)
            double t0 = ts[0], t1 = ts[ts.Length - 1];
            for (double c = t0 + 8; c <= t1 - 4; c += 4)
            {
                var idx = Enumerable.Range(0, ts.Length).Where(i =>
                        Math.Abs(ts[i] - c) <= 8 && !notTracking.Any(s => ts[i] >= s[0] - 0.3 && ts[i] <= s[1] + 0.3))
                    .ToList();
                if (idx.Count < 150) continue;
                double bestR = -2, bestL = 0;
                for (double lag = -1.5; lag <= 1.5001; lag += 0.025)
                {
                    var xs = new List<double>();
                    var ys = new List<double>();
                    foreach (int i in idx)
                        if (hmd.Sample(ts[i] + lag, out var p, out _, 0.05))
                        {
                            xs.Add(p.y);
                            ys.Add(hy[i]);
                        }

                    if (xs.Count < 120 || Std(xs) < 0.03) continue;
                    double r = Corr(xs, ys);
                    if (r > bestR)
                    {
                        bestR = r;
                        bestL = lag;
                    }
                }

                if (bestR > 0.9) knots.Add(Tuple.Create(c, bestL));
            }

            if (knots.Count < 2) return "";
            // robust: drop knots far from both neighbours (single bad windows)
            var k2 = new List<Tuple<double, double>>();
            for (int i = 0; i < knots.Count; i++)
            {
                double prev = i > 0 ? knots[i - 1].Item2 : knots[i].Item2,
                    next = i + 1 < knots.Count ? knots[i + 1].Item2 : knots[i].Item2;
                if (Math.Abs(knots[i].Item2 - prev) > 0.3 && Math.Abs(knots[i].Item2 - next) > 0.3) continue;
                k2.Add(knots[i]);
            }

            if (k2.Count < 2 || k2.Max(k => Math.Abs(k.Item2)) < 0.06) return "";

            double Lag(double t)
            {
                if (t <= k2[0].Item1) return k2[0].Item2;
                for (int i = 1; i < k2.Count; i++)
                    if (t <= k2[i].Item1)
                    {
                        double a = (t - k2[i - 1].Item1) / (k2[i].Item1 - k2[i - 1].Item1);
                        return k2[i - 1].Item2 + (k2[i].Item2 - k2[i - 1].Item2) * a;
                    }

                return k2[k2.Count - 1].Item2;
            }

            lock (rec.Sync)
            {
                for (int i = 0; i < h.Count; i++) h.T[i] = ts[i] + Lag(ts[i]);
                for (int i = 1; i < h.Count; i++)
                    if (h.T[i] <= h.T[i - 1])
                        h.T[i] = h.T[i - 1] + 1e-4;
            }

            string note =
                $"VRChat's IK frames (HUMR) re-aligned to the SteamVR devices: VRChat's clock was {k2.First().Item2:+0.00;-0.00} s off at {k2.First().Item1:F0} s and {k2.Last().Item2:+0.00;-0.00} s at {k2.Last().Item1:F0} s (it falls behind on avatar reloads)";
            AppLog.Info(note);
            return note;
        }

        static double Std(List<double> x)
        {
            double m = x.Average();
            return Math.Sqrt(x.Sum(v => (v - m) * (v - m)) / x.Count);
        }

        static double Corr(List<double> x, List<double> y)
        {
            int n = x.Count;
            double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
            for (int i = 0; i < n; i++)
            {
                sxy += (x[i] - mx) * (y[i] - my);
                sxx += (x[i] - mx) * (x[i] - mx);
                syy += (y[i] - my) * (y[i] - my);
            }

            return sxy / Math.Sqrt(sxx * syy + 1e-12);
        }

        sealed class Done
        {
            public string Note = "";
        }
    }
}
