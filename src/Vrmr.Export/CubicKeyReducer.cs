using System;
using System.Collections.Generic;

namespace VRMR
{
    /// <summary>One key of a reduced curve: frame index, value, and the slope used for both tangents (per second).</summary>
    public struct CurveKey
    {
        public int Frame;
        public float Value, Slope;
        /// <summary>True for a key that holds its value until the next one (whole-number parameter curves).</summary>
        public bool Stepped;
    }

    /// <summary>
    /// Reduces a curve sampled at every frame to the keys a cubic (Hermite) curve needs to stay within a tolerance of
    /// every sample. Each key keeps the sampled slope as both tangents, which is how Unity evaluates the result.
    /// </summary>
    public static class CubicKeyReducer
    {
        /// <summary>Longest stretch one curve segment may span, in frames.</summary>
        const int MaxSpan = 600;

        /// <summary>Keys for values sampled at fps; the first and the last frame are always kept.</summary>
        public static List<CurveKey> Reduce(float[] v, float fps, float tolerance)
        {
            int n = v.Length;
            var keys = new List<CurveKey>();
            if (n == 0) return keys;
            double dt = 1.0 / fps;
            var slope = new double[n];
            for (int f = 0; f < n; f++)
                slope[f] = n == 1 ? 0 : f == 0 ? (v[1] - v[0]) / dt : f == n - 1 ? (v[n - 1] - v[n - 2]) / dt : (v[f + 1] - v[f - 1]) / (2 * dt);
            int a = 0;
            keys.Add(Key(v, slope, 0));
            while (a < n - 1)
            {
                int best = a + 1;
                for (int b = a + 2; b < n && b - a <= MaxSpan; b++)
                {
                    if (!Fits(v, slope, a, b, dt, tolerance)) break;
                    best = b;
                }
                keys.Add(Key(v, slope, best));
                a = best;
            }
            return keys;
        }

        /// <summary>Keys of a curve that only ever holds whole numbers: one stepped key per change.</summary>
        public static List<CurveKey> ReduceStepped(float[] v)
        {
            var keys = new List<CurveKey>();
            for (int f = 0; f < v.Length; f++)
                if (f == 0 || v[f] != v[f - 1] || f == v.Length - 1) keys.Add(new CurveKey { Frame = f, Value = v[f], Stepped = true });
            return keys;
        }

        /// <summary>The reduced curve's value at a frame (what Unity plays there).</summary>
        public static float Evaluate(List<CurveKey> keys, int frame, float fps)
        {
            int i = 0;
            while (i < keys.Count - 2 && keys[i + 1].Frame <= frame) i++;
            var k0 = keys[i];
            if (keys.Count == 1 || frame <= k0.Frame || k0.Stepped) return frame >= keys[keys.Count - 1].Frame ? keys[keys.Count - 1].Value : k0.Value;
            var k1 = keys[i + 1];
            if (frame >= k1.Frame) return k1.Value;
            return (float)Hermite(k0.Value, k0.Slope, k1.Value, k1.Slope, (k1.Frame - k0.Frame) / (double)fps, (frame - k0.Frame) / (double)(k1.Frame - k0.Frame));
        }

        static CurveKey Key(float[] v, double[] slope, int f) => new CurveKey { Frame = f, Value = v[f], Slope = (float)slope[f] };

        static bool Fits(float[] v, double[] slope, int a, int b, double dt, float tolerance)
        {
            double span = (b - a) * dt;
            for (int m = a + 1; m < b; m++)
                if (Math.Abs(Hermite(v[a], slope[a], v[b], slope[b], span, (m - a) / (double)(b - a)) - v[m]) > tolerance) return false;
            return true;
        }

        static double Hermite(double v0, double s0, double v1, double s1, double span, double h)
        {
            double h2 = h * h, h3 = h2 * h;
            return (2 * h3 - 3 * h2 + 1) * v0 + (h3 - 2 * h2 + h) * span * s0 + (-2 * h3 + 3 * h2) * v1 + (h3 - h2) * span * s1;
        }
    }
}
