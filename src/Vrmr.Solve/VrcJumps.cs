using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace VRMR
{
    /// <summary>
    ///     VRChat jumps (and drops off a ledge): VRChat moves the whole avatar through its world while the user stands still
    ///     in
    ///     the playspace. Found from the avatar parameters Grounded (false in the air) and VelocityY (the take-off), refined
    ///     with VRChat's own IK when the take has it (its head leaving the scaled headset height and coming back).
    ///     The vertical displacement curve is measured from VRChat's head bone against the headset where both are recorded,
    ///     else integrated from VelocityY. By default it is taken out of the body (HUMR) and kept out of the IK's VRChat-space
    ///     fusion, so a jump never lifts the exported motion; EditList.IncludeJumps puts it back.
    ///     Jumps never touch the calibration, the tracker binding or the tracker roles (all from raw SteamVR).
    /// </summary>
    public sealed class VrcJumps
    {
        public static double Pad = 0.15;

        /// <summary>Diagnostics: false = no jump handling (the old behaviour: jumps stay in HUMR and the fusion).</summary>
        public static bool Enabled = true;

        static readonly ConditionalWeakTable<Recording, VrcJumps> Cache = new();

        public readonly List<Jump> Jumps = new();

        readonly List<float> _d = new();

        // displacement samples (take seconds, VRChat world units, upward)
        readonly List<double> _t = new();

        public bool Any => Jumps.Count > 0;

        public static VrcJumps For(Recording rec, AvatarProfile prof = null)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(rec, out var j)) return j;
                j = Detect(rec, prof ?? rec.Profile);
                Cache.Add(rec, j);
                return j;
            }
        }

        public bool InJump(double t)
        {
            return Jumps.Any(j => t >= j.Start && t <= j.End);
        }

        /// <summary>VRChat's vertical jump displacement at t (VRChat world units, 0 outside jumps).</summary>
        public float Offset(double t)
        {
            if (_t.Count == 0 || !InJump(t)) return 0;
            int i = _t.BinarySearch(t);
            if (i < 0) i = ~i;
            if (i <= 0) return _d[0];
            if (i >= _t.Count) return _d[_t.Count - 1];
            double a = (t - _t[i - 1]) / Math.Max(1e-6, _t[i] - _t[i - 1]);
            return (float)(_d[i - 1] + (_d[i] - _d[i - 1]) * a);
        }

        public string Describe()
        {
            return Jumps.Count == 0 ? "" : $"VRChat jumps at {string.Join("; ", Jumps.Select(j => j.ToString()))}";
        }

        public static VrcJumps Detect(Recording rec, AvatarProfile prof)
        {
            var res = new VrcJumps();
            if (!Enabled) return res;
            var grounded = rec.Param("Grounded");
            var vy = rec.Param("VelocityY");
            var spans = new List<double[]>();
            if (grounded != null && grounded.Count > 0)
            {
                double up = -1;
                for (int i = 0; i < grounded.Count; i++)
                {
                    bool g = grounded.V[i] > 0.5f;
                    if (!g && up < 0) up = grounded.T[i];
                    if (g && up >= 0)
                    {
                        spans.Add(new[] { up, grounded.T[i] });
                        up = -1;
                    }
                }

                if (up >= 0) spans.Add(new[] { up, rec.Duration });
            }
            else if (vy != null)
            {
                // no Grounded: a take-off (VelocityY > 1.5 m/s) until VelocityY settles near 0 again after falling
                for (int i = 0; i < vy.Count; i++)
                {
                    if (vy.V[i] < 1.5f) continue;
                    double a = vy.T[i];
                    int k = i + 1;
                    bool fell = false;
                    for (; k < vy.Count; k++)
                    {
                        if (vy.V[k] < -0.3f) fell = true;
                        if (fell && Math.Abs(vy.V[k]) < 0.2f) break;
                    }

                    spans.Add(new[] { a, k < vy.Count ? vy.T[k] : rec.Duration });
                    while (i + 1 < vy.Count && vy.T[i + 1] <= spans[spans.Count - 1][1]) i++;
                }
            }

            if (spans.Count == 0) return res;
            foreach (double[] sp in spans)
                res.Jumps.Add(new Jump
                {
                    Start = Math.Max(0, sp[0] - Pad), End = Math.Min(rec.Duration, sp[1] + Pad),
                    Why = grounded != null ? "Grounded off" : "VelocityY"
                });
            // displacement: VRChat's head bone height against the scaled headset (HUMR + SteamVR), else VelocityY integrated
            var hmd = rec.Devices.Where(d => d.DeviceClass == 1 && d.Count > 2).OrderByDescending(d => d.Count)
                .FirstOrDefault();
            foreach (var j in res.Jumps)
            {
                var ts = new List<double>();
                var ds = new List<float>();
                bool measured = false;
                if (rec.HasHumr && hmd != null && prof != null && prof.Has(HumanBones.Head))
                {
                    var clip = new BakedClip
                    {
                        Profile = prof, Fps = 1, Frames = 1, BoneExists = prof.HumanExists, Scale = 1,
                        Hips = new Vec3[1], Rot = new Quat[1][]
                    };
                    var wp = new Vec3[prof.Nodes.Count];
                    var wr = new Quat[prof.Nodes.Count];
                    var e = new List<Tuple<double, float>>();
                    double a = j.Start - 0.4, b = j.End + 0.4;
                    for (int i = 0; i < rec.Humr.Count; i++)
                    {
                        double t = rec.Humr.T[i];
                        if (t < a || t > b) continue;
                        if (!hmd.Sample(t + LiveHumrMap.Lag, out var hp, out _, 0.1)) continue;
                        var rot = rec.Humr.Rot[i].Select((q, k) => q.Norm > 0.5f ? q.Normalized : prof.HumanRestRot(k))
                            .ToArray();
                        clip.Hips[0] = rec.Humr.Hips[i];
                        clip.Rot[0] = rot;
                        clip.EvaluateNodes(0, wp, wr);
                        e.Add(Tuple.Create(t, wp[prof.Human[HumanBones.Head]].y - hp.y * Scale(rec, prof)));
                    }

                    var pre = e.Where(x => x.Item1 < j.Start).Select(x => x.Item2).ToList();
                    var post = e.Where(x => x.Item1 > j.End).Select(x => x.Item2).ToList();
                    if (pre.Count >= 2 && post.Count >= 2)
                    {
                        float b0 = pre.OrderBy(x => x).ElementAt(pre.Count / 2),
                            b1 = post.OrderBy(x => x).ElementAt(post.Count / 2);
                        foreach (var x in e)
                        {
                            float w = (float)MathX.Clamp((x.Item1 - j.Start) / Math.Max(1e-6, j.End - j.Start), 0, 1);
                            float d = x.Item2 - (b0 + (b1 - b0) * w);
                            ts.Add(x.Item1);
                            ds.Add(x.Item1 < j.Start || x.Item1 > j.End ? 0 : Math.Max(0, d));
                        }

                        measured = true;
                        j.Why += ", height from VRChat's head";
                    }
                }

                if (!measured && vy != null)
                {
                    float h = 0;
                    double last = j.Start;
                    for (double t = j.Start; t <= j.End; t += 1 / 60.0)
                    {
                        h += vy.Sample(t) * (float)(t - last);
                        last = t;
                        ts.Add(t);
                        ds.Add(Math.Max(0, h));
                    }

                    // landing back at the start height
                    if (ds.Count > 1)
                    {
                        float end = ds[ds.Count - 1];
                        for (int k = 0; k < ds.Count; k++) ds[k] = Math.Max(0, ds[k] - end * k / (ds.Count - 1));
                    }

                    j.Why += ", height from VelocityY";
                }

                j.Height = ds.Count > 0 ? ds.Max() : 0;
                res._t.AddRange(ts);
                res._d.AddRange(ds);
            }

            // keep the samples sorted for Offset
            var order = res._t.Select((t, i) => i).OrderBy(i => res._t[i]).ToList();
            var t2 = order.Select(i => res._t[i]).ToList();
            var d2 = order.Select(i => res._d[i]).ToList();
            res._t.Clear();
            res._t.AddRange(t2);
            res._d.Clear();
            res._d.AddRange(d2);
            return res;
        }

        /// <summary>VRChat world units per real metre (avatar eye / the user's eye height by VRChat's User Real Height).</summary>
        static float Scale(Recording rec, AvatarProfile prof)
        {
            var vs = VrcSettings.Values.FromJson(rec.Extra.GetObj("vrchatSettings"));
            float eye = rec.AvatarEyeHeight > 0.2f ? rec.AvatarEyeHeight : prof.EyeHeight;
            return vs.PlayerHeight > 0.5f ? eye / (vs.PlayerHeight * CalibrationSettings.EyeToHeightRatio) : 0.62f;
        }

        public sealed class Jump
        {
            public float Height;

            /// <summary>Take-off and landing (take seconds), padded by <see cref="Pad" />.</summary>
            public double Start, End;

            public string Why = "";

            public override string ToString()
            {
                return $"{Start:F2}-{End:F2} s ({Height * 100:0} cm, {Why})";
            }
        }
    }
}
