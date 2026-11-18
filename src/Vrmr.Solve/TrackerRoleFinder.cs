using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace VRMR
{
    /// <summary>
    /// Which body part each SteamVR tracker is on (hips, chest, elbows, knees, feet), worked out from the whole take:
    ///  * with VRChat's own IK in the take (HUMR): VRChat's own assignment. A tracker on a bone keeps its distances to the
    ///    headset and the two controllers changing exactly like that bone's distances to VRChat's head and hands, so each
    ///    tracker is matched to the bone whose distance curves it follows best (offsets removed), one bone per tracker;
    ///  * otherwise from how the tracker moves while the user stands: how far below the eyes it stays (feet, knees, hips,
    ///    chest by the avatar's proportions), and whether it stays within a forearm of one controller (an elbow).
    /// Both are independent of where the playspace floor is and of the calibration pose (a T-pose or arms down).
    /// TrackingRig falls back to the heights at the calibration moment when neither is conclusive.
    /// </summary>
    public static class TrackerRoleFinder
    {
        public sealed class Result
        {
            public readonly Dictionary<PoseStream, BodyRole> Roles = new Dictionary<PoseStream, BodyRole>();
            public string Method = "";
            /// <summary>Per tracker: how well the chosen role fits (for the notes).</summary>
            public readonly Dictionary<PoseStream, string> Why = new Dictionary<PoseStream, string>();
        }

        static readonly ConditionalWeakTable<Recording, Result> _cache = new ConditionalWeakTable<Recording, Result>();
        static readonly object _lock = new object();

        /// <summary>Diagnostics: false = only the old heights at the calibration moment.</summary>
        public static bool Enabled = true;

        static readonly BodyRole[] Roles = { BodyRole.Hips, BodyRole.Chest, BodyRole.LeftElbow, BodyRole.RightElbow, BodyRole.LeftKnee, BodyRole.RightKnee, BodyRole.LeftFoot, BodyRole.RightFoot };

        static int BoneOf(BodyRole r, AvatarProfile p)
        {
            switch (r)
            {
                case BodyRole.Hips: return HumanBones.Hips;
                case BodyRole.Chest: return p.Has(HumanBones.UpperChest) ? HumanBones.UpperChest : HumanBones.Chest;
                case BodyRole.LeftElbow: return HumanBones.LeftLowerArm;
                case BodyRole.RightElbow: return HumanBones.RightLowerArm;
                case BodyRole.LeftKnee: return HumanBones.LeftLowerLeg;
                case BodyRole.RightKnee: return HumanBones.RightLowerLeg;
                case BodyRole.LeftFoot: return HumanBones.LeftFoot;
                case BodyRole.RightFoot: return HumanBones.RightFoot;
            }
            return -1;
        }

        /// <summary>Roles for the given trackers (cached per take). Empty when nothing conclusive.</summary>
        public static Result For(Recording rec, AvatarProfile prof, List<PoseStream> trackers, PoseStream hmd, PoseStream cl, PoseStream cr, float userEye)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(rec, out var hit) && trackers.All(t => hit.Roles.ContainsKey(t) || hit.Why.ContainsKey(t))) return hit;
                var res = new Result();
                if (Enabled && trackers.Count > 0 && hmd != null)
                {
                    bool humr = FromHumr(rec, prof, trackers, hmd, cl, cr, res);
                    var left = trackers.Where(t => !res.Roles.ContainsKey(t)).ToList();
                    if (left.Count > 0)
                    {
                        // the rest (all of them without VRChat's IK in the take): from how they move
                        var r2 = new Result();
                        FromMotion(rec, prof, left, hmd, cl, cr, userEye, r2);
                        foreach (var kv in r2.Roles)
                            if (!res.Roles.ContainsValue(kv.Value)) { res.Roles[kv.Key] = kv.Value; res.Why[kv.Key] = r2.Why[kv.Key] + (humr ? " (from motion)" : ""); }
                        if (!humr) res.Method = r2.Method; else if (r2.Roles.Count > 0) res.Method += " and " + r2.Method;
                    }
                }
                foreach (var t in trackers) if (!res.Why.ContainsKey(t)) res.Why[t] = "";
                _cache.Remove(rec);
                _cache.Add(rec, res);
                return res;
            }
        }

        public static void Invalidate(Recording rec) { lock (_lock) _cache.Remove(rec); }

        // ------------------------------------------------------------------ VRChat's own assignment (HUMR)

        static bool FromHumr(Recording rec, AvatarProfile prof, List<PoseStream> trackers, PoseStream hmd, PoseStream cl, PoseStream cr, Result res)
        {
            var h = rec.Humr;
            // VRChat's bones come from the avatar the take was recorded with (the target may be another avatar)
            prof = rec.Profile ?? PoseBuilder.SourceResolver?.Invoke(rec) ?? prof;
            if (h == null || h.Count < 60 || prof == null || cl == null || cr == null || !prof.Has(HumanBones.Head) || !prof.Has(HumanBones.LeftHand) || !prof.Has(HumanBones.RightHand)) return false;
            // VRChat's avatar scaling changes every VRChat distance (a test take: x1.36 for 49 s, x0.69 for 6 s):
            // VRChat's distances are taken back to the avatar's own size
            HumrSync.Ensure(rec, prof);
            var vscale = VrcAvatarScale.For(rec, prof);
            var roles = Roles.Where(r => prof.Has(BoneOf(r, prof))).ToArray();
            var notTracking = VrcTimeline.For(rec).NotTracking();
            var clip = new BakedClip { Profile = prof, Fps = 1, Frames = 1, BoneExists = prof.HumanExists, Scale = 1, Hips = new Vec3[1], Rot = new Quat[1][] };
            var wp = new Vec3[prof.Nodes.Count]; var wr = new Quat[prof.Nodes.Count];
            const double lag = 0.05; // HUMR frames trail SteamVR by 0-0.13 s; distances change slowly enough
            var ts = new List<double>(); var refD = new List<float[]>(); var bones = new List<float[][]>(); var devD = new List<float[][]>();
            double last = -1;
            for (int i = 0; i < h.Count; i++)
            {
                double t = h.T[i];
                if (t - last < 0.1) continue;
                if (notTracking.Any(sp => t >= sp[0] - 0.5 && t <= sp[1] + 0.5)) continue;
                if (!hmd.Sample(t + lag, out var ph, out _, 0.05) || !cl.Sample(t + lag, out var pl, out _, 0.05) || !cr.Sample(t + lag, out var pr, out _, 0.05)) continue;
                var r = new Quat[HumanBones.Count];
                for (int b = 0; b < HumanBones.Count; b++) { var q = h.Rot[i][b]; r[b] = q.Norm > 0.5f ? q.Normalized : prof.HumanRestRot(b); }
                clip.Hips[0] = h.Hips[i]; clip.Rot[0] = r; clip.EvaluateNodes(0, wp, wr);
                float inv = 1f / Math.Max(0.05f, vscale.At(t));
                Vec3 B(int b) => wp[prof.Human[b]] * inv;
                var vh = B(HumanBones.Head); var vl = B(HumanBones.LeftHand); var vr = B(HumanBones.RightHand);
                var bd = new float[roles.Length][];
                for (int k = 0; k < roles.Length; k++) { var p = B(BoneOf(roles[k], prof)); bd[k] = new[] { (p - vh).Length, (p - vl).Length, (p - vr).Length }; }
                var dd = new float[trackers.Count][];
                for (int k = 0; k < trackers.Count; k++)
                    dd[k] = trackers[k].Sample(t + lag, out var p, out _, 0.05) ? new[] { (p - ph).Length, (p - pl).Length, (p - pr).Length } : null;
                ts.Add(t); refD.Add(new[] { (vh - vl).Length, (vh - vr).Length, (ph - pl).Length, (ph - pr).Length }); bones.Add(bd); devD.Add(dd);
                last = t;
            }
            if (ts.Count < 50) return false;
            // VRChat units per real metre from the head-hand distances
            var ratios = refD.Where(x => x[2] > 0.2f && x[3] > 0.2f).Select(x => (x[0] + x[1]) / (x[2] + x[3])).OrderBy(x => x).ToList();
            if (ratios.Count < 20) return false;
            float s = ratios[ratios.Count / 2];
            // cost: mean absolute deviation of (tracker distance * s - bone distance) from its median (robust to tracking glitches)
            var cost = new double[trackers.Count, roles.Length];
            for (int i = 0; i < trackers.Count; i++)
                for (int k = 0; k < roles.Length; k++)
                {
                    double sum = 0; int n = 0;
                    for (int c = 0; c < 3; c++)
                    {
                        var diff = new List<float>();
                        for (int f = 0; f < ts.Count; f++) if (devD[f][i] != null) diff.Add(devD[f][i][c] * s - bones[f][k][c]);
                        if (diff.Count < 30) { sum = double.MaxValue; break; }
                        diff.Sort(); float med = diff[diff.Count / 2];
                        foreach (var d in diff) { sum += Math.Abs(d - med); n++; }
                    }
                    cost[i, k] = sum == double.MaxValue || n == 0 ? 1e9 : sum / n;
                }
            if (Environment.GetEnvironmentVariable("VRMR_ROLE_DEBUG") == "1")
                for (int i = 0; i < trackers.Count; i++) Console.WriteLine($"  role cost {trackers[i].Serial}: " + string.Join(" ", Enumerable.Range(0, roles.Length).Select(k => $"{roles[k]} {cost[i, k] * 100:0.0}")));
            var assign = BestAssignment(cost, trackers.Count, roles.Length);
            int good = 0;
            for (int i = 0; i < trackers.Count; i++)
            {
                int k = assign[i];
                // the margin over this tracker's next best bone: a clear winner, and a fit within a few centimetres
                double second = double.MaxValue; for (int k2 = 0; k2 < roles.Length; k2++) if (k2 != k) second = Math.Min(second, cost[i, k2]);
                if (k < 0 || cost[i, k] > 0.08 || cost[i, k] > 0.85 * second) { res.Why[trackers[i]] = k < 0 ? "no VRChat bone fits" : $"VRChat bone {roles[k]} fits {cost[i, k] * 100:0.0} avatar cm, not clearly better than {second * 100:0.0}"; continue; }
                res.Roles[trackers[i]] = roles[k];
                res.Why[trackers[i]] = $"VRChat's {roles[k]}: {cost[i, k] * 100:0.0} cm, next {(second < 1e8 ? (second * 100).ToString("0.0") : "-")} cm";
                good++;
            }
            if (good == 0) return false;
            res.Method = $"VRChat's own assignment (its IK in this take, {ts.Count} frames)";
            return true;
        }

        /// <summary>Minimum-cost one-to-one assignment (trackers to roles); -1 = unassigned. Small sizes: exhaustive with pruning.</summary>
        static int[] BestAssignment(double[,] cost, int n, int m)
        {
            var best = new int[n]; double bestC = double.MaxValue;
            var cur = new int[n]; var used = new bool[m];
            void Rec(int i, double acc)
            {
                if (acc >= bestC) return;
                if (i == n) { bestC = acc; Array.Copy(cur, best, n); return; }
                for (int k = 0; k < m; k++)
                {
                    if (used[k]) continue;
                    used[k] = true; cur[i] = k; Rec(i + 1, acc + Math.Min(cost[i, k], 1.0)); used[k] = false;
                }
                if (n > m) { cur[i] = -1; Rec(i + 1, acc + 1.0); }
            }
            Rec(0, 0);
            return best;
        }

        // ------------------------------------------------------------------ from motion (no VRChat IK in the take)

        static void FromMotion(Recording rec, AvatarProfile prof, List<PoseStream> trackers, PoseStream hmd, PoseStream cl, PoseStream cr, float userEye, Result res)
        {
            // standing frames: the headset within 15% of its usual standing height
            var hs = new List<float>();
            for (double t = 0; t < rec.Duration; t += 0.1) if (hmd.Sample(t, out var p, out _, 0.05)) hs.Add(p.y);
            if (hs.Count < 30) return;
            hs.Sort();
            float standH = hs[(int)(hs.Count * 0.8)];
            float eye = userEye > 0.5f ? userEye : standH;
            var stats = trackers.Select(_ => new List<float[]>()).ToList();
            for (double t = 0; t < rec.Duration; t += 0.1)
            {
                if (!hmd.Sample(t, out var ph, out var qh, 0.05) || ph.y < standH * 0.85f) continue;
                Vec3 pl = Vec3.Zero, pr = Vec3.Zero;
                bool hl = cl != null && cl.Sample(t, out pl, out _, 0.05), hr = cr != null && cr.Sample(t, out pr, out _, 0.05);
                var yi = Quat.YawRotation(-qh.Yaw);
                for (int i = 0; i < trackers.Count; i++)
                {
                    if (!trackers[i].Sample(t, out var p, out _, 0.05)) continue;
                    var local = yi * (p - ph);
                    stats[i].Add(new[] { (ph.y - p.y) / eye, local.x / eye, hl ? (p - pl).Length / eye : 9f, hr ? (p - pr).Length / eye : 9f });
                }
            }
            float Med(List<float[]> l, int c) { var v = l.Select(x => x[c]).OrderBy(x => x).ToList(); return v[v.Count / 2]; }
            float Pct(List<float[]> l, int c, float q) { var v = l.Select(x => x[c]).OrderBy(x => x).ToList(); return v[Math.Min(v.Count - 1, (int)(q * v.Count))]; }
            // depth below the eyes of each body part, from this avatar's proportions (rest pose)
            float restEye = Math.Max(0.2f, prof?.EyeHeight ?? 1.6f);
            float D(int bone, float fallback) => prof != null && prof.Has(bone) ? (restEye - prof.HumanRestPos(bone).y) / restEye : fallback;
            var depth = new Dictionary<BodyRole, float>
            {
                [BodyRole.LeftFoot] = D(HumanBones.LeftFoot, 0.93f), [BodyRole.LeftKnee] = D(HumanBones.LeftLowerLeg, 0.7f),
                [BodyRole.Hips] = D(HumanBones.Hips, 0.42f),
                [BodyRole.Chest] = prof != null && prof.Has(HumanBones.UpperChest) ? D(HumanBones.UpperChest, 0.25f) : D(HumanBones.Chest, 0.28f),
            };
            var cands = new List<Tuple<int, BodyRole, float>>();
            for (int i = 0; i < trackers.Count; i++)
            {
                if (stats[i].Count < 30) { res.Why[trackers[i]] = "too few standing frames"; continue; }
                float dy = Med(stats[i], 0), lat = Med(stats[i], 1), dl = Med(stats[i], 2), dr = Med(stats[i], 3);
                bool left = lat < 0;
                int hc = dl < dr ? 2 : 3;
                float spread = Pct(stats[i], hc, 0.9f) - Pct(stats[i], hc, 0.1f);
                // an elbow stays within a forearm (+ controller) of its hand, about a fifth of the eye height, and that
                // distance hardly changes (the forearm is rigid). A hip tracker near a resting hand is as close, but the
                // distance swings with the arm (a test take: 35 cm median, 10-90 % spread 41 cm)
                if (Math.Min(dl, dr) < 0.22f && dy > 0.05f && dy < 0.7f && spread < 0.07f)
                {
                    var role = dl < dr ? BodyRole.LeftElbow : BodyRole.RightElbow;
                    var other = res.Roles.Where(kv => kv.Value == role).Select(kv => kv.Key).FirstOrDefault();
                    if (other != null) continue; // a second tracker near the same hand: not an elbow we can place
                    res.Roles[trackers[i]] = role; res.Why[trackers[i]] = $"stays {Math.Min(dl, dr) * eye * 100:0} cm from the {(dl < dr ? "left" : "right")} controller";
                    continue;
                }
                var best = depth.OrderBy(kv => Math.Abs(kv.Value - dy)).First();
                if (Math.Abs(best.Value - dy) > 0.15f) { res.Why[trackers[i]] = $"{dy * eye * 100:0} cm below the eyes fits no body part"; continue; }
                var r = best.Key;
                if (r == BodyRole.LeftFoot && !left) r = BodyRole.RightFoot;
                if (r == BodyRole.LeftKnee && !left) r = BodyRole.RightKnee;
                cands.Add(Tuple.Create(i, r, lat));
                res.Why[trackers[i]] = $"{dy * eye * 100:0} cm below the eyes while standing";
            }
            // two trackers for one sided role: the further-left one is the left
            foreach (var g in cands.GroupBy(c => Side(c.Item2)))
            {
                var l = g.OrderBy(c => c.Item3).ToList();
                if (g.Key == "hips" || g.Key == "chest")
                {
                    // both at hips/chest height: the higher one is the chest
                    if (l.Count == 1) { res.Roles[trackers[l[0].Item1]] = l[0].Item2; continue; }
                    var byH = l.OrderBy(c => Med(stats[c.Item1], 0)).ToList();
                    res.Roles[trackers[byH[0].Item1]] = BodyRole.Chest; res.Roles[trackers[byH[1].Item1]] = BodyRole.Hips;
                    continue;
                }
                if (l.Count == 1) { res.Roles[trackers[l[0].Item1]] = l[0].Item2; continue; }
                res.Roles[trackers[l[0].Item1]] = g.Key == "foot" ? BodyRole.LeftFoot : BodyRole.LeftKnee;
                res.Roles[trackers[l[l.Count - 1].Item1]] = g.Key == "foot" ? BodyRole.RightFoot : BodyRole.RightKnee;
            }
            if (res.Roles.Count > 0) res.Method = "how each tracker moves while standing (below the eyes, near a controller)";
        }

        static string Side(BodyRole r) => r == BodyRole.LeftFoot || r == BodyRole.RightFoot ? "foot" : r == BodyRole.LeftKnee || r == BodyRole.RightKnee ? "knee" : r == BodyRole.Hips || r == BodyRole.Chest ? "hips" : r.ToString();
    }
}
