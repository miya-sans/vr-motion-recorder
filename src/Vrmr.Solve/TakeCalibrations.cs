using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace VRMR
{
    /// <summary>Finds VRChat's full-body calibrations inside a recorded take (from its events, triggers and finger curls).</summary>
    public static class TakeCalibrations
    {
        /// <summary>Longest wait between the "Calibrate FBT" reload and the confirm.</summary>
        public const double MaxCalibrationSeconds = 180;

        /// <summary>A re-measure this long after the IK set-up is a confirm (the set-up's own re-measure is in the same second).</summary>
        public const double ConfirmAfterIkInit = 1.5;

        /// <summary>
        ///     How long before the full press of the confirm triggers (both above 0.7) VRChat takes its calibration pose (s).
        ///     Measured against VRChat's own IK (HUMR): on an 11-point test take the headset moved 8 cm in the 0.3 s before the
        ///     press and the binding fits the press (23.95-24.05 s, press logged at 24.01 s; the earlier "pull start" 23.74 s is
        ///     1 cm worse per bone); on another test take nothing moved and every moment within 0.4 s fits within 0.25 cm.
        /// </summary>
        public const double TriggerPullLead = 0.03;

        /// <summary>
        ///     Diagnostics only: false restores the earlier choice (live snapshot's pull start first, else the full press -
        ///     0.25 s).
        /// </summary>
        public static bool UseFullPress = true;

        /// <summary>
        ///     Index controllers: pulling the trigger curls the index finger, letting go uncurls it. Returns the last time both
        ///     index curls (either one when one-handed) are at least 0.6 and both then drop by at least 0.25 within 0.35 s, or -1.
        ///     (On the test takes: 0.79/0.90 -> 0.00/0.24 at the confirm; the menu click before it moves one finger only.)
        /// </summary>
        public static double BothIndexRelease(IList<double> t, IList<float> l, IList<float> r, bool oneHanded = false)
        {
            double best = -1;
            for (int i = 0; i < t.Count; i++)
            {
                bool hiL = l[i] >= 0.6f, hiR = r[i] >= 0.6f;
                if (oneHanded ? !(hiL || hiR) : !(hiL && hiR)) continue;
                float minL = l[i], minR = r[i];
                for (int k = i + 1; k < t.Count && t[k] - t[i] <= 0.35; k++)
                {
                    minL = Math.Min(minL, l[k]);
                    minR = Math.Min(minR, r[k]);
                }

                bool dropL = l[i] - minL >= 0.25f, dropR = r[i] - minR >= 0.25f;
                if (oneHanded ? (dropL && hiL) || (dropR && hiR) : dropL && dropR) best = t[i];
            }

            return best;
        }

        /// <summary>
        ///     Finds VRChat's first calibrate-and-confirm inside a take and returns the confirm moment (take seconds, the pose
        ///     just before the pull) or -1. See <see cref="FindAllInTake" />.
        /// </summary>
        public static double FindInTake(Recording rec, out string detail)
        {
            var all = FindAllInTake(rec, out _);
            detail = all.Count > 0 ? all[0].Detail : "";
            return all.Count > 0 ? all[0].Confirm : -1;
        }

        /// <summary>
        ///     Finds every VRChat calibrate-and-confirm inside a take (confirm moments in take seconds, the pose just before the
        ///     pull), and the setting-only avatar reloads (measurement changes: the IK set-up's re-measure follows in the same
        ///     second). Uses the take's own log events ("avatar"/"avatarReload", "ikInit", "remeasure") when it has them; older
        ///     takes read VRChat's output_log for the take's time instead. Each moment is refined with the "triggers" events or,
        ///     without them, the joint release of both index fingers.
        /// </summary>
        public static List<TakeCalibration> FindAllInTake(Recording rec, out List<TakeReload> settingReloads)
        {
            var res = new List<TakeCalibration>();
            settingReloads = new List<TakeReload>();
            bool oh = oneHanded(rec);
            var reloads = rec.Events.Where(e => e.Type == "avatar" || e.Type == "avatarReload").Select(e => e.T)
                .OrderBy(x => x).ToList();
            var worlds = rec.Events.Where(e => e.Type == "world" || e.Type == "joining").Select(e => e.T).ToList();
            var remeasures = rec.Events.Where(e => e.Type == "remeasure").Select(e => e.T).OrderBy(x => x).ToList();
            var ikInits = rec.Events.Where(e => e.Type == "ikInit" && e.Text != "Limb").Select(e => e.T).OrderBy(x => x)
                .ToList();
            // the loading avatar's own re-measure ("Initialize Limb Avatar" + re-measure) is neither a confirm nor a setting change
            var limb = rec.Events.Where(e => e.Type == "ikInit" && e.Text == "Limb").Select(e => e.T).ToList();
            remeasures = remeasures.Where(x => !limb.Any(l => x >= l - 0.05 && x - l < 0.3)).ToList();
            string src = "the take's log events";
            double gap = ConfirmAfterIkInit, slack = 0.05, after = 0.05;
            if (remeasures.Count == 0 && ReadVrcLog(rec, out var logReloads, out var logRemeasures, out var logIk,
                    out string file))
            {
                // log lines have whole-second times: the line's second starts at the returned time
                remeasures = logRemeasures;
                ikInits = logIk;
                if (reloads.Count == 0) reloads = logReloads;
                src = "VRChat's " + file;
                gap = 2;
                slack = 1.05;
                after = 0;
            }

            // collapse a reload's duplicates ("avatar" from the log + "avatarReload" from OSC, within 3 s)
            var rl = new List<double>();
            foreach (double x in reloads)
                if (rl.Count == 0 || x - rl[rl.Count - 1] > 3)
                    rl.Add(x);
            for (int k = 0; k < rl.Count; k++)
            {
                double reload = rl[k], next = k + 1 < rl.Count ? rl[k + 1] : double.MaxValue;
                if (worlds.Any(w => reload - w >= 0 && reload - w < 25)) continue;
                // the IK set-up that follows this reload
                double ik = ikInits.Where(x => x >= reload - 1.05 && x < next).DefaultIfEmpty(double.NaN).Min();
                if (double.IsNaN(ik)) continue;
                // the set-up's own re-measure (same second): a setting change, no calibration mode
                bool settingOnly = remeasures.Any(x => x >= ik - after && x - ik < gap && x < next);
                // the confirm: the first later re-measure before the next reload
                double r = remeasures
                    .Where(x => x - ik >= gap && x < next + slack && x - reload <= MaxCalibrationSeconds)
                    .DefaultIfEmpty(double.NaN).Min();
                if (settingOnly)
                {
                    settingReloads.Add(new TakeReload { Reload = reload, Ready = ik + slack });
                    continue;
                }

                if (double.IsNaN(r) || r + slack < 0.3) continue; // confirms inside the take only
                double at = r + slack * 0.5 - 0.3;
                string why = $"VRChat logged the confirm at {Math.Max(0, r):F1} s";
                // the "triggers" events (full press of both triggers: VRChat's pose, see TriggerPullLead), else the live
                // watcher's snapshot of this confirm (its id is the pose time), else both index fingers released together
                double snapT = double.NaN;
                foreach (var ce in rec.Events.Where(e => e.Type == "calibration"))
                    if (DateTime.TryParseExact(ce.Text ?? "", "yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeLocal, out var when))
                    {
                        double x = (when.ToUniversalTime() - rec.CreatedUtc).TotalSeconds;
                        if (x >= r - 3 && x <= r + slack + 0.2) snapT = x;
                    }

                var edge = rec.Events
                    .Where(e => e.Type == "triggers" && e.T >= r - 2.5 && e.T <= r + slack + 0.3 &&
                                (e.Text == "both" || (oh && e.Text != "none"))).OrderBy(e => e.T).LastOrDefault();
                if (!UseFullPress && !double.IsNaN(snapT)) at = Math.Max(0, snapT);
                else if (!UseFullPress && edge != null) at = Math.Max(0, edge.T - 0.25);
                else if (edge != null)
                {
                    at = Math.Max(0, edge.T - TriggerPullLead);
                    why = $"{(edge.Text == "both" ? "both triggers" : "trigger")} pressed at {edge.T:F2} s; " + why;
                }
                else if (!double.IsNaN(snapT))
                {
                    at = Math.Max(0, snapT);
                    why = "live calibration snapshot; " + why;
                }
                else
                {
                    double peak = IndexRelease(rec, r - 1.5, r + slack + 0.5, oh);
                    if (peak >= 0)
                    {
                        at = Math.Max(0, peak - 0.2);
                        why = $"{(oh ? "index finger" : "both index fingers")} pulled and released at {peak:F2} s; " +
                              why;
                    }
                }

                res.Add(new TakeCalibration
                {
                    Click = Math.Max(0, reload), Confirm = at, Logged = r,
                    Detail =
                        $"VRChat calibrate-and-confirm at {at:F2} s: {why} ({src}); \"Calibrate FBT\" reloaded the avatar at {Math.Max(0, reload):F1} s"
                });
            }

            // no log data at all: both index fingers pulled and released together after an avatar reload
            if (remeasures.Count == 0)
                foreach (double reload in rl)
                {
                    if (worlds.Any(w => reload - w >= 0 && reload - w < 25)) continue;
                    double peak = IndexRelease(rec, reload + 1.0, reload + 60, oh, true);
                    if (peak < 0) continue;
                    double at = Math.Max(0, peak - 0.2);
                    res.Add(new TakeCalibration
                    {
                        Click = Math.Max(0, reload), Confirm = at, Logged = peak,
                        Detail =
                            $"VRChat calibrate-and-confirm at {at:F2} s (estimated: no VRChat log): {(oh ? "index finger" : "both index fingers")} pulled and released {peak - reload:0.0} s after the avatar reload at {reload:F1} s"
                    });
                }

            return res;
        }

        /// <summary>Peak before the joint index-finger release in [a, b] of the take (last one, or the first when asked), or -1.</summary>
        internal static double IndexRelease(Recording rec, double a, double b, bool oneHanded, bool first = false)
        {
            var t = new List<double>();
            var l = new List<float>();
            var r = new List<float>();
            float[] s0 = new float[9];
            float[] s1 = new float[9];
            for (double x = Math.Max(0, a); x <= Math.Min(b, rec.Duration); x += 1 / 60.0)
            {
                if (!rec.Hands[0].SampleSummary(x, s0, 0.15) || !rec.Hands[1].SampleSummary(x, s1, 0.15)) continue;
                t.Add(x);
                l.Add(s0[1]);
                r.Add(s1[1]);
            }

            if (t.Count < 5) return -1;
            if (!first) return BothIndexRelease(t, l, r, oneHanded);
            // first release: the earliest peak followed by a joint drop
            for (int i = 0; i < t.Count; i++)
            {
                int j = i;
                while (j < t.Count && t[j] - t[i] < 0.5) j++;
                double p = BothIndexRelease(t.GetRange(i, j - i), l.GetRange(i, j - i), r.GetRange(i, j - i),
                    oneHanded);
                if (p >= 0) return p;
            }

            return -1;
        }

        /// <summary>
        ///     Reads VRChat's output_log around a take (older takes have no "remeasure" events): avatar reloads of the local user,
        ///     non-Limb IK set-ups and re-measures, as take seconds at the start of each line's (whole) second.
        /// </summary>
        internal static bool ReadVrcLog(Recording rec, out List<double> reloads, out List<double> remeasures,
            out List<double> ikInits, out string file)
        {
            reloads = new List<double>();
            remeasures = new List<double>();
            ikInits = new List<double>();
            file = "";
            try
            {
                var start = rec.CreatedUtc.ToLocalTime();
                var dir = new DirectoryInfo(VrcPaths.LogDir);
                if (!dir.Exists) return false;
                // the newest log started before the take
                var f = dir.GetFiles("output_log_*.txt").Where(x => x.CreationTime <= start.AddSeconds(5))
                    .OrderByDescending(x => x.CreationTime).FirstOrDefault();
                if (f == null) return false;
                file = f.Name;
                double dur = rec.Duration;
                using (var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs))
                {
                    string line;
                    bool
                        afterLimb =
                            false; // the loading avatar's own re-measure follows "Initialize Limb Avatar": not a confirm
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.Length < 20 || line[4] != '.' || line[13] != ':') continue;
                        if (line.IndexOf("[Behaviour] Initialize Limb ", StringComparison.Ordinal) >= 0 &&
                            line.IndexOf(" Avatar VRCPlayer[Local]", StringComparison.Ordinal) > 0)
                        {
                            afterLimb = true;
                            continue;
                        }

                        bool re =
                            line.IndexOf("Measure Human Avatar Avatar isRemeasure:True", StringComparison.Ordinal) >= 0;
                        if (re && afterLimb)
                        {
                            afterLimb = false;
                            continue;
                        }

                        bool ik = !re && line.IndexOf("[Behaviour] Initialize ", StringComparison.Ordinal) >= 0 &&
                                  line.IndexOf(" Avatar VRCPlayer[Local]", StringComparison.Ordinal) > 0 &&
                                  line.IndexOf("Initialize Limb ", StringComparison.Ordinal) < 0;
                        bool sw = !re && !ik && line.IndexOf("[Behaviour] Switching ", StringComparison.Ordinal) >= 0 &&
                                  (rec.LocalUser.Length == 0 ||
                                   line.IndexOf("Switching " + rec.LocalUser + " to avatar",
                                       StringComparison.Ordinal) >= 0);
                        if (!(re || ik || sw)) continue;
                        if (!DateTime.TryParseExact(line.Substring(0, 19), "yyyy.MM.dd HH:mm:ss",
                                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var when)) continue;
                        double t = (when - start).TotalSeconds;
                        if (t < -MaxCalibrationSeconds || t > dur + 2) continue;
                        (re ? remeasures : ik ? ikInits : reloads).Add(t);
                    }
                }

                return remeasures.Count > 0;
            }
            catch { return false; }
        }

        internal static bool oneHanded(Recording rec)
        {
            return VrcSettings.Values.FromJson(rec.Extra.GetObj("vrchatSettings")).OneHandedCalibration;
        }

        /// <summary>One VRChat calibrate-and-confirm in a take (take seconds).</summary>
        public sealed class TakeCalibration
        {
            /// <summary>"Calibrate FBT": the avatar reload that put VRChat in calibration mode.</summary>
            public double Click;

            /// <summary>The confirm moment: the pose when both triggers were pressed.</summary>
            public double Confirm;

            public string Detail = "";

            /// <summary>When VRChat logged the confirm (re-measure).</summary>
            public double Logged;
        }

        /// <summary>An avatar reload that only applied a setting (avatar measurement change): no calibration mode.</summary>
        public sealed class TakeReload
        {
            public double Reload, Ready;
        }
    }
}
