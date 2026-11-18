using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace VRMR
{
    /// <summary>
    ///     The size VRChat showed the worn avatar at during a take, relative to the avatar's own (uploaded) size: VRChat's
    ///     avatar scaling (Quick Menu height slider, reset) reported over OSC as ScaleFactor (current / default eye height),
    ///     ScaleFactorInverse, EyeHeightAsMeters, EyeHeightAsPercent and ScaleModified.
    ///     Measured on a test take against VRChat's own IK (HUMR hips height vs the hip tracker): VRChat does not
    ///     always report a change. At 31.9 s it sent only ScaleFactor 1.3606 (EyeHeightAsMeters stayed 1.1179), and at
    ///     80.5 s it changed the avatar back to its own size without sending anything; each change logs a lone
    ///     "Measure Human Avatar ... isRemeasure:True". So: every lone re-measure (one that isn't part of a calibration or an
    ///     avatar reload) starts a new stretch; its size is what OSC sent around it, and a stretch with no OSC value is
    ///     marked uncertain (its size is carried over and the UI says so). With VRChat's IK (HUMR) in the take the
    ///     stretches are measured instead (<see cref="RefineWithHumr" />).
    ///     The solve keeps the avatar's own size by default (CalibrationSettings.FollowAvatarScale off).
    /// </summary>
    public sealed class VrcAvatarScale
    {
        static readonly ConditionalWeakTable<Recording, VrcAvatarScale> Cache = new();
        public readonly List<string> Notes = new();
        public readonly List<Stretch> Stretches = new();

        /// <summary>The worn avatar's eye height at its own size (VRChat ScaleFactor 1), metres; 0 = unknown.</summary>
        public float NaturalEye;

        string _key = "";

        public bool Changes => Stretches.Any(s => Math.Abs(s.Scale - 1f) > 0.003f);
        public bool AnyUncertain => Stretches.Any(s => s.Uncertain);

        public float At(double t)
        {
            for (int i = Stretches.Count - 1; i >= 0; i--)
                if (t >= Stretches[i].A)
                    return Stretches[i].Scale;
            return Stretches.Count > 0 ? Stretches[0].Scale : 1f;
        }

        public string Describe()
        {
            return Stretches.Count <= 1 && !Changes
                ? "VRChat avatar size: unchanged"
                : "VRChat avatar size: " + string.Join("; ",
                    Stretches.Select(s => s.ToString() + (s.Uncertain ? " uncertain" : "")));
        }

        /// <summary>
        ///     The take's scale timeline (cached per take); with VRChat's IK in the take and the worn avatar's skeleton,
        ///     measured on it.
        /// </summary>
        public static VrcAvatarScale For(Recording rec, AvatarProfile src = null)
        {
            string key = rec.Events.Count + "|" + rec.Params.Count + "|" +
                         (src == null ? "" : src.Name + src.Nodes.Count);
            lock (Cache)
            {
                if (Cache.TryGetValue(rec, out var v) && v._key == key) return v;
                v = Build(rec);
                v._key = key;
                if (src != null && rec.HasHumr)
                {
                    try
                    {
                        HumrSync.Ensure(rec, src);
                        v.RefineWithHumr(rec, src);
                    }
                    catch (Exception ex) { AppLog.Warn("Avatar scale from VRChat's IK: " + ex.Message); }
                }

                Cache.Remove(rec);
                Cache.Add(rec, v);
                return v;
            }
        }

        public static void Invalidate(Recording rec)
        {
            lock (Cache) Cache.Remove(rec);
        }

        public static VrcAvatarScale Build(Recording rec)
        {
            var v = new VrcAvatarScale();
            var sf = rec.Param("ScaleFactor");
            var sfi = rec.Param("ScaleFactorInverse");
            var eh = rec.Param("EyeHeightAsMeters");
            float sf0 = sf != null && sf.Count > 0 ? sf.V[0] : 0,
                eh0 = eh != null && eh.Count > 0 ? eh.V[0] : rec.AvatarEyeHeight;
            v.NaturalEye = eh0 > 0.05f ? sf0 > 0.05f ? eh0 / sf0 : eh0 : 0;

            // OSC size reports: (time, scale relative to the avatar's own size)
            var osc = new List<Tuple<double, float, string>>();
            if (sf != null)
                for (int i = 1; i < sf.Count; i++)
                    if (sf.V[i] > 0.01f)
                        osc.Add(Tuple.Create(sf.T[i], sf.V[i], "ScaleFactor"));
            if (sfi != null)
                for (int i = 1; i < sfi.Count; i++)
                    if (sfi.V[i] > 0.01f)
                        osc.Add(Tuple.Create(sfi.T[i], 1f / sfi.V[i], "ScaleFactorInverse"));
            if (eh != null && v.NaturalEye > 0.05f)
                for (int i = 1; i < eh.Count; i++)
                    if (eh.V[i] > 0.05f)
                        osc.Add(Tuple.Create(eh.T[i], eh.V[i] / v.NaturalEye, "EyeHeightAsMeters"));
            osc.Sort((a, b) => a.Item1.CompareTo(b.Item1));

            // lone re-measures: VRChat changed the avatar's size (not part of a calibration, an avatar reload or the IK set-up)
            var tl = VrcTimeline.For(rec);
            var busy = new List<double[]>();
            foreach (var c in tl.Calibrations) busy.Add(new[] { c.Click - 1.0, Math.Max(c.Confirm, c.Logged) + 1.0 });
            foreach (var r in tl.SettingReloads) busy.Add(new[] { r.Reload - 1.0, r.Ready + 1.0 });
            foreach (var e in rec.Events.Where(e =>
                         e.Type == "avatar" || e.Type == "avatarReload" || e.Type == "ikInit"))
                busy.Add(new[] { e.T - 0.5, e.T + 1.5 });
            var remeasures = rec.Events
                .Where(e => e.Type == "remeasure" && e.T > 0.2 && !busy.Any(b => e.T >= b[0] && e.T <= b[1]))
                .Select(e => e.T).ToList();

            // stretch starts: every OSC report and every lone re-measure (reports within 1 s of each other are one change)
            var starts = osc.Select(o => o.Item1).Concat(remeasures).OrderBy(t => t).ToList();
            var merged = new List<double>();
            foreach (double t in starts)
                if (merged.Count == 0 || t - merged[merged.Count - 1] > 1.0)
                    merged.Add(t);
            v.Stretches.Add(new Stretch
            {
                A = double.NegativeInfinity, Scale = sf0 > 0.05f ? sf0 : 1f,
                How = sf0 > 0.05f ? "ScaleFactor at the start" : "no ScaleFactor (the avatar's own size)"
            });
            foreach (double t in merged)
            {
                var near = osc.Where(o => o.Item1 >= t - 0.6 && o.Item1 <= t + 1.0).ToList();
                var prev = v.Stretches[v.Stretches.Count - 1];
                var st = new Stretch { A = t };
                if (near.Count > 0)
                {
                    // ScaleFactor first (VRChat sent it even when it skipped EyeHeightAsMeters), the latest one in the window
                    var pick = near.Where(o => o.Item3 == "ScaleFactor").LastOrDefault() ?? near.Last();
                    st.Scale = pick.Item2;
                    st.How = pick.Item3 + " (OSC)";
                    var others = near.Where(o => o.Item3 != pick.Item3).ToList();
                    if (others.Any(o => Math.Abs(o.Item2 - pick.Item2) > 0.02f * pick.Item2))
                        v.Notes.Add(
                            $"{t:F1} s: VRChat's OSC size reports disagree ({string.Join(", ", near.Select(o => $"{o.Item3} → ×{o.Item2:0.###}"))}); using {pick.Item3}");
                }
                else
                {
                    st.Scale = prev.Scale;
                    st.Uncertain = true;
                    st.How = "VRChat re-measured the avatar but sent no size over OSC";
                    v.Notes.Add(
                        $"{t:F1} s: VRChat re-measured the avatar (a height change or reset) but sent no new size over OSC; the size from here is uncertain (kept ×{prev.Scale:0.###})");
                }

                prev.B = t;
                v.Stretches.Add(st);
            }

            v.Stretches[v.Stretches.Count - 1].B = double.PositiveInfinity;
            // join neighbours with the same size
            for (int i = v.Stretches.Count - 1; i > 0; i--)
                if (Math.Abs(v.Stretches[i].Scale - v.Stretches[i - 1].Scale) < 0.002f && !v.Stretches[i].Uncertain)
                {
                    v.Stretches[i - 1].B = v.Stretches[i].B;
                    v.Stretches.RemoveAt(i);
                }

            return v;
        }

        /// <summary>
        ///     Measures each stretch's size from VRChat's own IK (HUMR): the hips height above VRChat's floor against the
        ///     natural-size FK of the same pose on the worn avatar's skeleton (hips above the lowest foot), while a foot is
        ///     planted. Fits one floor for the take and one slope per stretch; sizes are relative to the stretches where OSC
        ///     reported the avatar's own size. Replaces uncertain / disagreeing OSC sizes when the fit is clear.
        /// </summary>
        public void RefineWithHumr(Recording rec, AvatarProfile src)
        {
            if (rec.Humr.Count < 60 || src == null || Stretches.Count < 2) return;
            var clip = new BakedClip
            {
                Profile = src, Fps = 1, Frames = 1, BoneExists = src.HumanExists, Scale = 1, Hips = new Vec3[1],
                Rot = new Quat[1][]
            };
            var wp = new Vec3[src.Nodes.Count];
            var wr = new Quat[src.Nodes.Count];
            int[] feet = new[] { HumanBones.LeftFoot, HumanBones.RightFoot }.Where(src.Has).ToArray();
            if (feet.Length == 0) return;
            var samples =
                new List<Tuple<int, double, double>>(); // stretch, H (natural hips above lowest foot), y (VRChat hips)
            var notTracking = VrcTimeline.For(rec).NotTracking();
            Vec3[] prevFoot = null;
            double prevT = -1;
            for (int i = 0; i < rec.Humr.Count; i++)
            {
                double t = rec.Humr.T[i];
                if (notTracking.Any(s => t >= s[0] - 0.5 && t <= s[1] + 0.5))
                {
                    prevFoot = null;
                    continue;
                }

                var r = new Quat[HumanBones.Count];
                for (int b = 0; b < HumanBones.Count; b++)
                {
                    var q = rec.Humr.Rot[i][b];
                    r[b] = q.Norm > 0.5f ? q.Normalized : src.HumanRestRot(b);
                }

                clip.Hips[0] = Vec3.Zero;
                clip.Rot[0] = r;
                clip.EvaluateNodes(0, wp, wr);
                var fp = feet.Select(b => wp[src.Human[b]]).ToArray();
                float low = fp.Min(p => p.y);
                // a foot planted: the lowest foot hardly moves relative to the hips' vertical (the hips height above it is the leg's reach)
                bool planted = prevFoot != null && t - prevT < 0.1 &&
                               Math.Abs(fp.Min(p => p.y) - prevFoot.Min(p => p.y)) < 0.004f;
                prevFoot = fp;
                prevT = t;
                if (!planted) continue;
                int si = Stretches.FindLastIndex(s => t >= s.A);
                if (si < 0) si = 0;
                if (t < Stretches[si].A + 0.5 || t > Stretches[si].B - 0.3) continue; // around a change VRChat re-poses
                samples.Add(Tuple.Create(si, (double)-low, (double)rec.Humr.Hips[i].y));
            }

            if (samples.Count < 60) return;
            // y = F + S_k * H : least squares with one F and a slope per stretch
            int k = Stretches.Count, n = k + 1;
            double[,] A = new double[n, n];
            double[] bb = new double[n];
            foreach (var s in samples)
            {
                int j = s.Item1;
                // unknowns: S_0..S_{k-1}, F
                A[j, j] += s.Item2 * s.Item2;
                A[j, k] += s.Item2;
                A[k, j] += s.Item2;
                A[k, k] += 1;
                bb[j] += s.Item2 * s.Item3;
                bb[k] += s.Item3;
            }

            for (int j = 0; j < k; j++)
                if (A[j, j] < 1e-6)
                    A[j, j] = 1; // stretches without samples
            double[] x = Solve(A, bb, n);
            if (x == null) return;
            int[] counts = Enumerable.Range(0, k).Select(j => samples.Count(s => s.Item1 == j)).ToArray();
            // reference: stretches OSC called the avatar's own size (×1, certain), else all certain ones
            var refs = Enumerable.Range(0, k).Where(j =>
                counts[j] >= 20 && !Stretches[j].Uncertain && Math.Abs(Stretches[j].Scale - 1) < 0.003f).ToList();
            if (refs.Count == 0)
                refs = Enumerable.Range(0, k).Where(j => counts[j] >= 20 && !Stretches[j].Uncertain).ToList();
            if (refs.Count == 0) return;
            double natural = refs.Sum(j => x[j] / Stretches[j].Scale * counts[j]) / refs.Sum(j => counts[j]);
            for (int j = 0; j < k; j++)
            {
                if (counts[j] < 20) continue;
                float measured = (float)(x[j] / natural);
                var st = Stretches[j];
                // VRChat's sizes are the avatar's own (a reset) or one the user set before: snap to those within 3 %
                float known = new[] { 1f }.Concat(Stretches.Where(o => !o.Uncertain && o != st).Select(o => o.Scale))
                    .OrderBy(c => Math.Abs(c - measured)).First();
                if (Math.Abs(known - measured) < 0.03f * known) measured = known;
                if (st.Uncertain)
                {
                    // fills in what OSC didn't say; replaces the "uncertain" note
                    string at = $"{Math.Max(0, st.A):F1} s: ";
                    Notes.RemoveAll(nn =>
                        nn.StartsWith(at, StringComparison.Ordinal) && nn.Contains("sent no new size"));
                    Notes.Add(at +
                              $"VRChat changed the avatar's size without reporting it over OSC; VRChat's own IK shows ×{measured:0.###}");
                    st.Scale = measured;
                    st.How = "measured on VRChat's own IK";
                    st.Uncertain = false;
                }
                else if (Math.Abs(measured - st.Scale) > 0.10f * st.Scale)
                    // OSC sizes are kept (a wrong skeleton for the worn avatar would mislead the measurement): only noted
                    Notes.Add(
                        $"{Math.Max(0, st.A):F1} s: VRChat's own IK looks like ×{measured:0.###} where OSC said ×{st.Scale:0.###} (kept; check the take's avatar)");
            }
        }

        static double[] Solve(double[,] A, double[] b, int n)
        {
            double[,] M = (double[,])A.Clone();
            double[] x = (double[])b.Clone();
            for (int c = 0; c < n; c++)
            {
                int p = c;
                for (int r = c + 1; r < n; r++)
                    if (Math.Abs(M[r, c]) > Math.Abs(M[p, c]))
                        p = r;
                if (Math.Abs(M[p, c]) < 1e-12) return null;
                if (p != c)
                {
                    for (int j = 0; j < n; j++)
                    {
                        double t = M[c, j];
                        M[c, j] = M[p, j];
                        M[p, j] = t;
                    }

                    double tb = x[c];
                    x[c] = x[p];
                    x[p] = tb;
                }

                for (int r = 0; r < n; r++)
                {
                    if (r == c) continue;
                    double f = M[r, c] / M[c, c];
                    if (f == 0) continue;
                    for (int j = c; j < n; j++) M[r, j] -= f * M[c, j];
                    x[r] -= f * x[c];
                }
            }

            for (int i = 0; i < n; i++) x[i] /= M[i, i];
            return x;
        }

        public sealed class Stretch
        {
            public double A, B;
            public string How = "";
            public float Scale = 1f;
            public bool Uncertain;

            public override string ToString()
            {
                return
                    $"{Math.Max(0, A):F1}-{(double.IsPositiveInfinity(B) ? "end" : B.ToString("F1"))} s ×{Scale:0.###} ({How})";
            }
        }
    }
}
