using System;
using System.Collections.Generic;

namespace VRMR
{
    /// <summary>
    ///     Thins the face tracking parameters of a take once, when its recording stops. Values move to steps of 1/4096
    ///     and samples that linear interpolation between their neighbours reproduces within <see cref="Tolerance" /> are
    ///     dropped. Kept samples are never more than <see cref="MaxGap" /> apart, so <see cref="ParamTrack.Sample" />
    ///     still interpolates between them. VRCFT sends each face value about 145 times a second; most of those samples
    ///     lie on a straight line within the tolerance.
    /// </summary>
    public static class FaceKeys
    {
        public const double Tolerance = 0.002, Step = 1.0 / 4096, MaxGap = 0.19;

        /// <summary>Face tracking parameters by their name prefix.</summary>
        public static bool IsFace(string name)
        {
            return name.StartsWith("FT/", StringComparison.Ordinal) || name.StartsWith("v2/", StringComparison.Ordinal);
        }

        /// <summary>
        ///     Reduces the face float parameters of a take that has not been reduced yet and marks it, so later saves leave
        ///     it alone. Returns the number of samples removed.
        /// </summary>
        public static int Reduce(Recording rec)
        {
            lock (rec.Sync)
            {
                if (rec.FaceKeyTolerance > 0) return 0;
                int removed = 0;
                var t = new List<double>();
                var v = new List<float>();
                foreach (var p in rec.Params)
                    if (p.Type == ParamType.Float && IsFace(p.Name))
                        removed += Reduce(p, t, v);
                rec.FaceKeyTolerance = Tolerance;
                return removed;
            }
        }

        static int Reduce(ParamTrack p, List<double> keptT, List<float> keptV)
        {
            var t = p.T;
            var v = p.V;
            int n = p.Count;
            for (int k = 0; k < n; k++) v[k] = Quantize(v[k]);
            if (n < 3) return 0;
            keptT.Clear();
            keptV.Clear();
            keptT.Add(t[0]);
            keptV.Add(v[0]);
            for (int a = 0; a < n - 1;)
            {
                int b = Extend(t, v, a);
                keptT.Add(t[b]);
                keptV.Add(v[b]);
                a = b;
            }

            t.Clear();
            t.AddRange(keptT);
            v.Clear();
            v.AddRange(keptV);
            return n - keptT.Count;
        }

        /// <summary>
        ///     The furthest sample after <paramref name="a" /> that a straight line from <paramref name="a" /> reaches with
        ///     every sample between them within the tolerance: the line's slope must stay inside the band each of those
        ///     samples allows.
        /// </summary>
        static int Extend(List<double> t, List<float> v, int a)
        {
            int n = t.Count, b = a + 1;
            double lo = double.NegativeInfinity, hi = double.PositiveInfinity;
            while (b + 1 < n)
            {
                double db = t[b] - t[a], dc = t[b + 1] - t[a];
                if (db <= 0 || dc > MaxGap) break;
                lo = Math.Max(lo, (v[b] - Tolerance - v[a]) / db);
                hi = Math.Min(hi, (v[b] + Tolerance - v[a]) / db);
                double slope = (v[b + 1] - v[a]) / dc;
                if (!(slope >= lo && slope <= hi)) break;
                b++;
            }

            return b;
        }

        static float Quantize(float v)
        {
            return float.IsFinite(v) ? (float)(Math.Round(v / Step) * Step) : v;
        }
    }
}
