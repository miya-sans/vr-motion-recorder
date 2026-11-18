using System;
using System.Collections.Generic;
using System.Linq;

namespace VRMR
{
    // =====================================================================================================  device cleanup
    //
    // Modifiers on an INPUT strip that clean the devices before the take below is solved again (IDeviceModifier):
    //   devsmooth  zero-phase smoothing of the strip's devices (a jittery tracker), positions and rotations;
    //   devfill    marked stretches (and optionally every frame a device wasn't tracking) filled from the good poses
    //              around them: interpolated, or held and blended back.
    // Only the devices in the strip's Inputs are touched (the engine hands over just those). Because the body is solved
    // from the cleaned devices, it follows them (unlike pose-level repair, which edits the solved bones).

    static class DeviceCleanupParams
    {
        public static readonly ModParam[] Smooth =
        {
            CleanupParamsShared.Num("sigma", "Strength", 0.05, 0.005, 0.5, "0", " ms", 1000,
                "Gaussian smoothing width (zero phase: no lag)"),
            CleanupParamsShared.Check("rotation", "Smooth rotations too", true, null)
        };

        public static readonly ModParam[] Fill =
        {
            CleanupParamsShared.Choice("mode", "Fill", 0, new[] { "interpolate", "hold" },
                new[] { "Interpolate", "Hold, then blend back" },
                "Interpolate between the good poses around each stretch, or hold the pose before it and blend back after"),
            CleanupParamsShared.Check("invalid", "Also fill where a device wasn't tracking", true,
                "Frames the take marks as not tracked (after its own tracking-loss repair)"),
            CleanupParamsShared.Num("blend", "Blend back", 0.2, 0.01, 2, "0.00", " s", 1,
                "Hold: how long the held pose takes to blend back"),
            new()
            {
                Key = "ranges", Label = "Ranges", Kind = ModParamKind.Ranges, Tip = "Stretches to fill (scene seconds)"
            }
        };
    }

    /// <summary>Shared builders for parameter lists (the pose-level ones live in Cleanup.cs).</summary>
    static class CleanupParamsShared
    {
        public static ModParam Num(string key, string label, double def, double min, double max, string format,
            string suffix, double uiScale, string tip, bool slider = false)
        {
            return new ModParam
            {
                Key = key, Label = label, Default = def, Min = min, Max = max, Format = format, Suffix = suffix,
                UiScale = uiScale, Tip = tip, Slider = slider
            };
        }

        public static ModParam Check(string key, string label, bool def, string tip)
        {
            return new ModParam
                { Key = key, Label = label, Kind = ModParamKind.Check, Default = def ? 1 : 0, Tip = tip };
        }

        public static ModParam Choice(string key, string label, int def, string[] keys, string[] labels, string tip)
        {
            return new ModParam
            {
                Key = key, Label = label, Kind = ModParamKind.Choice, Default = def, Choices = keys,
                ChoiceLabels = labels, Tip = tip
            };
        }
    }

    public static class DeviceCleanup
    {
        /// <summary>Zero-phase Gaussian smoothing of every device in d (valid frames only; invalid ones are left alone).</summary>
        public static int Smooth(DeviceFrames d, double sigma, bool rotation)
        {
            int n = d.Time.Length;
            if (n < 3 || sigma <= 1e-4) return 0;
            float fps = d.Fps > 0 ? d.Fps : 60;
            int r = Math.Max(1, (int)Math.Ceiling(3 * sigma * fps));
            float[] g = new float[r + 1];
            for (int i = 0; i <= r; i++) g[i] = (float)Math.Exp(-0.5 * (i / (sigma * fps)) * (i / (sigma * fps)));
            int count = 0;
            foreach (var role in d.Pose.Keys.ToList())
            {
                var p = d.Pose[role];
                bool[] v = d.Valid[role];
                var outp = new Pose[n];
                for (int f = 0; f < n; f++)
                {
                    if (!v[f])
                    {
                        outp[f] = p[f];
                        continue;
                    }

                    var s = Vec3.Zero;
                    float ws = 0, qx = 0, qy = 0, qz = 0, qw = 0;
                    var c = p[f].rot;
                    for (int i = -r; i <= r; i++)
                    {
                        int k = f + i;
                        if (k < 0 || k >= n || !v[k]) continue;
                        float w = g[Math.Abs(i)];
                        s += p[k].pos * w;
                        ws += w;
                        var q = p[k].rot;
                        if (Quat.Dot(q, c) < 0) q = q.Negated;
                        qx += q.x * w;
                        qy += q.y * w;
                        qz += q.z * w;
                        qw += q.w * w;
                    }

                    outp[f] = new Pose(s / ws, rotation ? new Quat(qx, qy, qz, qw).Normalized : c);
                }

                Array.Copy(outp, p, n);
                count++;
            }

            return count;
        }

        /// <summary>
        ///     Fills marked ranges (scene s; and invalid frames when asked) from the valid poses around them. Returns the
        ///     frames filled.
        /// </summary>
        public static int Fill(DeviceFrames d, List<RepairRange> ranges, bool hold, double blend, bool invalid)
        {
            int n = d.Time.Length, filled = 0;
            if (n < 2) return 0;
            int nb = Math.Max(1, (int)Math.Round(blend * (d.Fps > 0 ? d.Fps : 60)));
            foreach (var role in d.Pose.Keys.ToList())
            {
                var p = d.Pose[role];
                bool[] v = d.Valid[role];
                bool[] bad = new bool[n];
                foreach (var rr in ranges)
                    for (int f = 0; f < n; f++)
                        if (d.Time[f] >= rr.A - 1e-6 && d.Time[f] <= rr.B + 1e-6)
                            bad[f] = true;
                if (invalid)
                    for (int f = 0; f < n; f++)
                        if (!v[f])
                            bad[f] = true;
                for (int f = 0; f < n; f++)
                {
                    if (!bad[f]) continue;
                    int a = f;
                    while (f + 1 < n && bad[f + 1]) f++;
                    int b = f, k0 = a - 1, k1 = b + 1;
                    while (k0 >= 0 && !v[k0]) k0--;
                    while (k1 < n && !v[k1]) k1++;
                    if (k0 < 0 && k1 >= n) continue;
                    var pa = k0 >= 0 ? p[k0] : p[k1];
                    var pb = k1 < n ? p[k1] : p[k0];
                    Vec3 va = k0 >= 1 && v[k0 - 1] ? p[k0].pos - p[k0 - 1].pos : Vec3.Zero,
                        vb = k1 + 1 < n && v[k1 + 1] ? p[k1 + 1].pos - p[k1].pos : Vec3.Zero;
                    int lo = k0 >= 0 ? k0 : a - 1, span = (k1 < n ? k1 : b + 1) - lo;
                    int last = hold ? Math.Min(n - 1, b + nb) : b;
                    for (int k = a; k <= last; k++)
                    {
                        float u = (k - lo) / (float)span;
                        Pose x;
                        if (hold || k1 >= n) x = pa;
                        else if (k0 < 0) x = pb;
                        else
                            x = new Pose(Hermite(pa.pos, va * span, pb.pos, vb * span, u),
                                Quat.Slerp(pa.rot, pb.rot, u));
                        if (k > b)
                        {
                            float w = MathX.SmoothStep((k - b) / (float)nb);
                            if (v[k]) x = Pose.Lerp(x, p[k], w);
                        }

                        p[k] = x;
                        v[k] = true;
                        filled++;
                    }

                    f = Math.Max(f, last);
                }
            }

            return filled;
        }

        static Vec3 Hermite(Vec3 p0, Vec3 m0, Vec3 p1, Vec3 m1, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return p0 * (2 * t3 - 3 * t2 + 1) + m0 * (t3 - 2 * t2 + t) + p1 * (-2 * t3 + 3 * t2) + m1 * (t3 - t2);
        }
    }

    /// <summary>Smooths the strip's devices before the solve (a jittery tracker).</summary>
    public sealed class DeviceSmoothModifier : IDeviceModifier, IModifierParams
    {
        public string Type => "devsmooth";
        public string Label => "Device smoothing";

        public void Apply(StripModifierContext c, StripModifier m, DeviceFrames d)
        {
            DeviceCleanup.Smooth(d, m.Num("sigma", 0.05), m.Flag("rotation", true));
        }

        public string Help =>
            "Smooths this layer's devices before the body is solved from them (a jittery tracker); the body follows.";

        public IReadOnlyList<ModParam> Params => DeviceCleanupParams.Smooth;
    }

    /// <summary>Fills marked stretches of this layer's devices (tracking loss the take's own repair missed).</summary>
    public sealed class DeviceFillModifier : IDeviceModifier, IModifierParams
    {
        public string Type => "devfill";
        public string Label => "Device gap fill";

        public void Apply(StripModifierContext c, StripModifier m, DeviceFrames d)
        {
            DeviceCleanup.Fill(d, m.Ranges(), m.Str("mode", "interpolate") == "hold", m.Num("blend", 0.2),
                m.Flag("invalid", true));
        }

        public string Help =>
            "Fills marked stretches of this layer's devices from the good poses around them (interpolated or held), before the body is solved.";

        public IReadOnlyList<ModParam> Params => DeviceCleanupParams.Fill;
    }
}
