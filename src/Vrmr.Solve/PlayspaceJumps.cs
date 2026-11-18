using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace VRMR
{
    /// <summary>
    ///     Instant playspace changes in the raw SteamVR data: a playspace reset (OVR Advanced Settings and similar tools),
    ///     SteamVR re-centering, a room-setup change. Every device then jumps by the same rigid transform (yaw + translation)
    ///     between two consecutive polls, which no body motion can do: the headset and every tracker would have to move
    ///     12 cm or more within ~11 ms, all in the same way. Nothing here depends on which tool or button caused it.
    ///     VRChat follows the raw coordinates (its avatar teleports with them), so the solve keeps the jumps in its output.
    ///     The solver itself is fed the poses with the jumps undone, so it sees continuous motion (no one-frame teleport
    ///     through its temporal filters or the zero-phase smoothing), and the jump is put back on the solved body.
    ///     Slow playspace drags (the mover dragging the space while a button is held) are continuous: they need no
    ///     handling, because the solve does not assume the floor is at y = 0 (feet follow their trackers, the hips and
    ///     head limits are relative to the feet and the calibration).
    /// </summary>
    public sealed class PlayspaceJumps
    {
        /// <summary>Smallest move of every device for a jump (m, within one poll).</summary>
        public static float MinMove = 0.12f;

        /// <summary>Smallest common yaw for a rotation-only jump (degrees, within one poll).</summary>
        public static float MinYaw = 8f;

        static readonly ConditionalWeakTable<Recording, PlayspaceJumps> Cache = new();

        /// <summary>Diagnostics only: false feeds the raw jumps to the solver (the behaviour before this handling existed).</summary>
        public static bool Enabled = true;

        public readonly List<Jump> Jumps = new();

        // cumulative transform after each jump (raw = Total[k] * continuous)
        readonly List<Pose> _total = new();

        public bool Any => Jumps.Count > 0;

        public static PlayspaceJumps For(Recording rec)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(rec, out var j)) return j;
                j = Detect(rec);
                Cache.Add(rec, j);
                return j;
            }
        }

        public static PlayspaceJumps Detect(Recording rec)
        {
            var res = new PlayspaceJumps();
            if (!Enabled) return res;
            var devs = rec.Devices.Where(d => d.Count > 10).ToList();
            if (devs.Count < 2) return res;
            var clock = devs.OrderByDescending(d => d.Count).First();
            var a = new List<Pose>();
            var b = new List<Pose>();
            for (int i = 1; i < clock.Count; i++)
            {
                double t0 = clock.T[i - 1], t1 = clock.T[i];
                if (t1 - t0 > 0.1 || t1 <= t0) continue;
                // cheap pre-check on the clock device
                if ((clock.P[i] - clock.P[i - 1]).Length < MinMove &&
                    Math.Abs(MathX.DeltaAngle(clock.R[i - 1].Yaw, clock.R[i].Yaw)) < MinYaw) continue;
                a.Clear();
                b.Clear();
                foreach (var d in devs)
                    if (d.Sample(t0, out var p0, out var q0, 0.03) && d.Sample(t1, out var p1, out var q1, 0.03))
                    {
                        a.Add(new Pose(p0, q0));
                        b.Add(new Pose(p1, q1));
                    }

                if (a.Count < 2) continue;
                float minMove = float.MaxValue, meanMove = 0;
                for (int k = 0; k < a.Count; k++)
                {
                    float m = (b[k].pos - a[k].pos).Length;
                    minMove = Math.Min(minMove, m);
                    meanMove += m / a.Count;
                }

                // yaw: Kabsch in the horizontal plane (positions) and the orientations' mean yaw change
                float oriYaw = 0;
                for (int k = 0; k < a.Count; k++) oriYaw += MathX.DeltaAngle(a[k].rot.Yaw, b[k].rot.Yaw) / a.Count;
                if (!(minMove >= MinMove || Math.Abs(oriYaw) >= MinYaw)) continue;
                var R = Quat.YawRotation(oriYaw);
                Vec3 ca = Vec3.Zero, cb = Vec3.Zero;
                for (int k = 0; k < a.Count; k++)
                {
                    ca += a[k].pos;
                    cb += b[k].pos;
                }

                ca = ca / a.Count;
                cb = cb / a.Count;
                var T = cb - R * ca;
                float resid = 0, yawRes = 0;
                for (int k = 0; k < a.Count; k++)
                {
                    resid = Math.Max(resid, (R * a[k].pos + T - b[k].pos).Length);
                    yawRes = Math.Max(yawRes, Math.Abs(MathX.DeltaAngle(a[k].rot.Yaw + oriYaw, b[k].rot.Yaw)));
                }

                // all devices moved together (rigidly), much more than any of them moved relative to the others
                if (resid > Math.Max(0.03f, 0.15f * meanMove) || yawRes > 4f) continue;
                res.Jumps.Add(new Jump
                {
                    Before = t0, After = t1, Delta = new Pose(T, R), Moved = meanMove, Yaw = oriYaw, Residual = resid,
                    Devices = a.Count
                });
            }

            var tot = Pose.Identity;
            foreach (var j in res.Jumps)
            {
                tot = j.Delta * tot;
                res._total.Add(tot);
            }

            return res;
        }

        int Index(double t)
        {
            int k = -1;
            for (int i = 0; i < Jumps.Count; i++)
                if (t >= Jumps[i].After - 1e-6) k = i;
                else break;
            return k;
        }

        /// <summary>Raw = Total(t) * continuous, for a moment of the take.</summary>
        public Pose Total(double t)
        {
            int k = Index(t);
            return k < 0 ? Pose.Identity : _total[k];
        }

        /// <summary>Sampling time that never interpolates across a jump (between its two polls the pre-jump poll is used).</summary>
        public double SampleTime(double t)
        {
            foreach (var j in Jumps)
                if (t > j.Before && t < j.After)
                    return j.Before;
            return t;
        }

        public string Describe()
        {
            return Jumps.Count == 0
                ? ""
                : $"Playspace jumps (reset / re-center) at {string.Join("; ", Jumps.Select(j => j.ToString()))}: the body teleports with them, like VRChat's";
        }

        public sealed class Jump
        {
            /// <summary>Last poll before and first poll after the jump (take seconds).</summary>
            public double Before, After;

            /// <summary>Raw poses after = Delta * raw poses before.</summary>
            public Pose Delta;

            public int Devices;
            public float Moved, Yaw, Residual;

            public override string ToString()
            {
                return
                    $"{After:F2} s: {Moved * 100:0} cm, yaw {Yaw:+0;-0;0}° ({Devices} devices, residual {Residual * 100:0.0} cm)";
            }
        }
    }
}
