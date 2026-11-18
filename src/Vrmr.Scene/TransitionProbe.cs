using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace VRMR
{
    /// <summary>A transition's match and how it plays (filled after each evaluation, read by the inspector).</summary>
    public sealed class TransitionReport
    {
        /// <summary>Peak centre-of-mass acceleration (m/s²) inside the crossfade and in the second around it.</summary>
        public float ComAccelIn, ComAccelAround;

        /// <summary>Largest centre-of-mass step between two frames (m) inside the crossfade and in the second around it.</summary>
        public float ComStepIn, ComStepAround;

        /// <summary>Take data (scene s) available past A's out point and before B's in point (∞ for keyframe strips).</summary>
        public double HandleA, HandleB;

        // from the evaluated output (HasOutput false until an evaluation with the transition in it)
        public bool HasOutput;
        public string LengthNote = "";
        public MatchResult Match;

        /// <summary>
        ///     Foot slide (m) during the crossfade in the output, and in the two takes on their own over the same time
        ///     (mean).
        /// </summary>
        public float SlideL, SlideR, SlideBaseL, SlideBaseR;

        /// <summary>Suggested crossfade length (s) and why it may be limited.</summary>
        public double Suggested;
    }

    /// <summary>The result of a best-cut search: scores over cut positions (scene-time offsets from where the cut was).</summary>
    public sealed class CutSearch
    {
        public double BestDa, BestDb;
        public float BestScore, StartScore;

        /// <summary>Offsets (scene s) of A's out point / B's in point; Score[i, j] for Da[i], Db[j]; NaN = not possible.</summary>
        public double[] Da, Db;

        /// <summary>Only B's in point moved (the whole of B was searched).</summary>
        public bool Entry;

        public long Ms;
        public float[,] Score;
        public double Ta0, Tb0, SpeedA, SpeedB, Fps;
        public string TransitionId, Key;

        public double TaOf(double da)
        {
            return Ta0 + da * SpeedA;
        }

        public double TbOf(double db)
        {
            return Tb0 + db * SpeedB;
        }
    }

    /// <summary>
    ///     One transition of a scene, ready to be measured: its two strips' samplers on the actor's rig (take solves from the
    ///     evaluator's cache), the comparison setup, the limits a roll edit has. Create on a snapshot to use it off the UI
    ///     thread.
    /// </summary>
    public sealed class TransitionProbe
    {
        // ---------------------------------------------------------------- searches

        /// <summary>Score penalty per second the cut moves (a tie-breaker: of equal cuts, the nearest wins).</summary>
        public const float MovePenalty = 0.002f;

        public Strip A, B;
        public Actor Actor;
        public double LoA, HiA, LoB, HiB;
        public PoseRig Rig;
        public float Scale, Fps;
        public Scene Scene;
        public MatchSetup Setup;
        public Transition Tr;
        public NlaTrack Track;
        Func<double, ScenePose, bool> _srcA, _srcB;

        /// <summary>Source time of A's out point / B's in point now, and the cut (scene s).</summary>
        public double Ta0 => A.SrcOut;

        public double Tb0 => B.SrcIn;
        public double Cut => B.Start;
        double StepA => A.Speed / Fps;
        double StepB => B.Speed / Fps;

        /// <summary>Whether B can be searched as a whole (a take strip with limits).</summary>
        public bool CanSearchEntry => !double.IsInfinity(LoB) && !double.IsInfinity(HiB);

        public static TransitionProbe Create(SceneEvaluator ev, Scene s, string transitionId, AvatarProfile profile,
            float scale, out string why)
        {
            why = null;
            var (actor, track, tr) = s.FindTransition(transitionId);
            if (tr == null)
            {
                why = "no transition";
                return null;
            }

            var a = track.Strips.FirstOrDefault(x => x.Id == tr.From);
            var b = track.Strips.FirstOrDefault(x => x.Id == tr.To);
            if (a == null || b == null)
            {
                why = "a strip is missing";
                return null;
            }

            var p = new TransitionProbe
            {
                Scene = s, Actor = actor, Track = track, Tr = tr, A = a, B = b, Rig = PoseRig.For(profile),
                Scale = scale, Fps = s.Fps
            };
            p._srcA = p.Sampler(ev, a, profile, out why);
            if (p._srcA == null) return null;
            p._srcB = p.Sampler(ev, b, profile, out why);
            if (p._srcB == null) return null;
            SceneOps.SourceLimits(s, a, out p.LoA, out p.HiA);
            SceneOps.SourceLimits(s, b, out p.LoB, out p.HiB);
            p.Setup = TransitionMatch.Setup(p.Rig, scale, s.Fps, tr, a, b);
            return p;
        }

        /// <summary>
        ///     Source time -> the strip's pose with its own root transform (B's alignment is computed per cut). Through the
        ///     evaluator, so a take strip uses its own solve from the last evaluation (re-solved with input-track edits, a
        ///     device take's solve), else the take as recorded.
        /// </summary>
        Func<double, ScenePose, bool> Sampler(SceneEvaluator ev, Strip st, AvatarProfile profile, out string why)
        {
            why = null;
            var off = st.Offset;
            var scene = Scene;
            var rig = Rig;
            float scale = Scale;
            if (st.Kind == StripKind.Take && scene.Take(st.Source) == null)
            {
                why = "take not in the scene";
                return null;
            }

            if (st.Kind == StripKind.Action && scene.Action(st.Source) == null)
            {
                why = "action missing";
                return null;
            }

            Func<double, ScenePose, bool> f = (src, o) => ev.SamplePose(scene, st, rig, scale, st.ToScene(src), o, off);
            if (!f(st.SrcIn, new ScenePose()))
            {
                why = "take still solving";
                return null;
            }

            return f;
        }

        /// <summary>Samples of A around source time ta (pad frames beyond ±range scene seconds), with the index of ta.</summary>
        MotionSamples SamplesAround(bool isA, double t0, double range, out int index)
        {
            double step = isA ? StepA : StepB;
            int k = (int)Math.Ceiling(range * Fps - 1e-6) + Setup.Pad;
            index = k;
            return TransitionMatch.Sample(Rig, Scale, Setup, isA ? _srcA : _srcB, t0 - k * step, step, 2 * k + 1);
        }

        // ---------------------------------------------------------------- the cut as it is

        /// <summary>Score and parts of the current cut, the suggested length, the take handles.</summary>
        public TransitionReport Report()
        {
            var sa = SamplesAround(true, Ta0, 0, out int ia);
            var sb = SamplesAround(false, Tb0, 0, out int ib);
            if (sa == null || sb == null) return null;
            var r = new TransitionReport { Match = TransitionMatch.Evaluate(Setup, sa, ia, sb, ib) };
            r.HandleA = double.IsInfinity(HiA) ? double.PositiveInfinity : Math.Max(0, (HiA - Ta0) / A.Speed);
            r.HandleB = double.IsInfinity(LoB) ? double.PositiveInfinity : Math.Max(0, (Tb0 - LoB) / B.Speed);
            double sug = TransitionMatch.SuggestLength(r.Match, Setup.Height);
            double room = Math.Min(A.Length, B.Length);
            if (sug > room)
            {
                sug = Math.Floor(room * 20) / 20;
                r.LengthNote = "limited by the strips' lengths";
            }

            double handle = 2 * Math.Min(r.HandleA, r.HandleB);
            if (sug > handle + 1e-6)
                r.LengthNote = r.HandleB < r.HandleA
                    ? $"{B.Name} has only {r.HandleB:0.00} s before its in point: the crossfade holds its first frame"
                    : $"{A.Name}'s take ends {r.HandleA:0.00} s after the cut: the crossfade holds its last frame";
            r.Suggested = Math.Max(0.05, sug);
            return r;
        }

        // ---------------------------------------------------------------- roll-edit limits

        /// <summary>Free scene time after B (and the strips chained to it by transitions), up to the next strip.</summary>
        public double GapAfter()
        {
            var chain = new HashSet<string> { A.Id, B.Id };
            var last = B;
            for (int guard = 0; guard < 1000; guard++)
            {
                var tr = Track.TransitionOutOf(last.Id);
                var nx = tr != null ? Track.Strips.FirstOrDefault(x => x.Id == tr.To) : null;
                if (nx == null || !chain.Add(nx.Id)) break;
                last = nx;
            }

            double gap = double.PositiveInfinity;
            foreach (var st in Track.Strips)
                if (!chain.Contains(st.Id) && st.Start >= last.End - 1e-6)
                    gap = Math.Min(gap, st.Start - last.End);
            return gap;
        }

        /// <summary>
        ///     Whether a roll edit to (ta, tb) is possible: both strips keep at least the crossfade, the takes have data for the
        ///     whole crossfade (half of it past A's out point and before B's in point: no held frames), nothing collides.
        /// </summary>
        public bool Allowed(double ta, double tb, double gap)
        {
            double minLen = Math.Max(Tr.Duration, 0.2), half = Tr.Duration / 2;
            if ((ta - A.SrcIn) / A.Speed < minLen - 1e-9 || ta + half * A.Speed > HiA + 1e-9) return false;
            if ((B.SrcOut - tb) / B.Speed < minLen - 1e-9 || tb - half * B.Speed < LoB - 1e-9) return false;
            double shift = (ta - Ta0) / A.Speed - (tb - Tb0) / B.Speed; // how much B's end moves later
            return shift <= gap + 1e-9;
        }

        /// <summary>
        ///     The most seamless cut with A's out point within ±rangeA s and B's in point within ±rangeB s of where they are
        ///     (2-frame grid, then refined to the frame).
        /// </summary>
        public CutSearch SearchCut(double rangeA = 2, double rangeB = 3, Func<bool> cancelled = null)
        {
            var sw = Stopwatch.StartNew();
            var sa = SamplesAround(true, Ta0, rangeA, out int ia0);
            var sb = SamplesAround(false, Tb0, rangeB, out int ib0);
            if (sa == null || sb == null) return null;
            double gap = GapAfter();
            int na = (int)Math.Floor(rangeA * Fps / 2 + 1e-6), nb = (int)Math.Floor(rangeB * Fps / 2 + 1e-6);
            var res = new CutSearch
            {
                TransitionId = Tr.Id, Ta0 = Ta0, Tb0 = Tb0, SpeedA = A.Speed, SpeedB = B.Speed, Fps = Fps,
                Da = new double[2 * na + 1], Db = new double[2 * nb + 1]
            };
            res.Score = new float[res.Da.Length, res.Db.Length];
            for (int i = 0; i < res.Da.Length; i++) res.Da[i] = (i - na) * 2 / (double)Fps;
            for (int j = 0; j < res.Db.Length; j++) res.Db[j] = (j - nb) * 2 / (double)Fps;
            double best = double.NegativeInfinity;
            int bi = 0, bj = 0;
            for (int i = 0; i < res.Da.Length; i++)
            {
                if (cancelled != null && cancelled()) return null;
                for (int j = 0; j < res.Db.Length; j++)
                {
                    int da = (i - na) * 2, db = (j - nb) * 2;
                    if (!Allowed(Ta0 + da * StepA, Tb0 + db * StepB, gap))
                    {
                        res.Score[i, j] = float.NaN;
                        continue;
                    }

                    float sc = (float)TransitionMatch.ScoreFromCost(TransitionMatch.Cost(Setup, sa, ia0 + da, sb,
                        ib0 + db));
                    res.Score[i, j] = sc;
                    double obj = sc - MovePenalty * (Math.Abs(da) + Math.Abs(db)) / Fps;
                    if (obj > best)
                    {
                        best = obj;
                        bi = da;
                        bj = db;
                    }
                }
            }

            res.StartScore = (float)TransitionMatch.ScoreFromCost(TransitionMatch.Cost(Setup, sa, ia0, sb, ib0));
            if (double.IsNegativeInfinity(best))
            {
                res.BestDa = 0;
                res.BestDb = 0;
                res.BestScore = res.StartScore;
                res.Ms = sw.ElapsedMilliseconds;
                return res;
            }

            // refine to the frame
            int ri = bi, rj = bj;
            for (int da = bi - 1; da <= bi + 1; da++)
            for (int db = bj - 1; db <= bj + 1; db++)
            {
                if (Math.Abs(da) > 2 * na || Math.Abs(db) > 2 * nb ||
                    !Allowed(Ta0 + da * StepA, Tb0 + db * StepB, gap)) continue;
                float sc = (float)TransitionMatch.ScoreFromCost(TransitionMatch.Cost(Setup, sa, ia0 + da, sb,
                    ib0 + db));
                double obj = sc - MovePenalty * (Math.Abs(da) + Math.Abs(db)) / Fps;
                if (obj > best + 1e-9)
                {
                    best = obj;
                    ri = da;
                    rj = db;
                }
            }

            res.BestDa = ri / (double)Fps;
            res.BestDb = rj / (double)Fps;
            res.BestScore =
                (float)TransitionMatch.ScoreFromCost(TransitionMatch.Cost(Setup, sa, ia0 + ri, sb, ib0 + rj));
            res.Ms = sw.ElapsedMilliseconds;
            return res;
        }

        /// <summary>
        ///     The best place to enter B: A's out point stays, B's in point is searched over the whole take (every frame; long
        ///     takes on a 2-frame grid, refined). For a strip that plays a whole take, whose start is often a setup pose.
        /// </summary>
        public CutSearch SearchEntry(Func<bool> cancelled = null)
        {
            if (!CanSearchEntry) return null;
            var sw = Stopwatch.StartNew();
            var sa = SamplesAround(true, Ta0, 0, out int ia);
            if (sa == null) return null;
            // B over the whole take, on the grid through its current in point
            double step = StepB;
            int before = (int)Math.Floor((Tb0 - LoB) / step + 1e-6), after = (int)Math.Floor((HiB - Tb0) / step + 1e-6);
            int pad = Setup.Pad;
            var sb = TransitionMatch.Sample(Rig, Scale, Setup, _srcB, Tb0 - (before + pad) * step, step,
                before + after + 2 * pad + 1);
            if (sb == null) return null;
            int ib0 = before + pad;
            int grid = before + after > 12000 ? 2 : 1;
            double gap = GapAfter();
            var cols = new List<int>();
            for (int d = -before; d <= after; d++)
                if ((d % grid + grid) % grid == 0)
                    cols.Add(d);
            var res = new CutSearch
            {
                TransitionId = Tr.Id, Entry = true, Ta0 = Ta0, Tb0 = Tb0, SpeedA = A.Speed, SpeedB = B.Speed, Fps = Fps,
                Da = new[] { 0.0 }, Db = cols.Select(d => d / (double)Fps).ToArray()
            };
            res.Score = new float[1, cols.Count];
            const float entryPenalty = 0.0005f;
            double best = double.NegativeInfinity;
            int bd = 0;
            for (int j = 0; j < cols.Count; j++)
            {
                if ((j & 255) == 0 && cancelled != null && cancelled()) return null;
                int d = cols[j];
                if (!Allowed(Ta0, Tb0 + d * step, gap))
                {
                    res.Score[0, j] = float.NaN;
                    continue;
                }

                float sc = (float)TransitionMatch.ScoreFromCost(TransitionMatch.Cost(Setup, sa, ia, sb, ib0 + d));
                res.Score[0, j] = sc;
                double obj = sc - entryPenalty * Math.Abs(d) / Fps;
                if (obj > best)
                {
                    best = obj;
                    bd = d;
                }
            }

            res.StartScore = (float)TransitionMatch.ScoreFromCost(TransitionMatch.Cost(Setup, sa, ia, sb, ib0));
            if (double.IsNegativeInfinity(best))
            {
                res.BestScore = res.StartScore;
                res.Ms = sw.ElapsedMilliseconds;
                return res;
            }

            int rd = bd;
            if (grid > 1)
                for (int d = bd - 1; d <= bd + 1; d++)
                {
                    if (d < -before || d > after || !Allowed(Ta0, Tb0 + d * step, gap)) continue;
                    double obj = TransitionMatch.ScoreFromCost(TransitionMatch.Cost(Setup, sa, ia, sb, ib0 + d)) -
                                 entryPenalty * Math.Abs(d) / Fps;
                    if (obj > best + 1e-9)
                    {
                        best = obj;
                        rd = d;
                    }
                }

            res.BestDb = rd / (double)Fps;
            res.BestScore = (float)TransitionMatch.ScoreFromCost(TransitionMatch.Cost(Setup, sa, ia, sb, ib0 + rd));
            res.Ms = sw.ElapsedMilliseconds;
            return res;
        }

        /// <summary>
        ///     What a search result depends on besides the cut position (the strips' other ends, offsets, masks, the
        ///     transition's length / alignment): a map stays valid while this is unchanged.
        /// </summary>
        public static string SetupKey(Scene s, Transition tr)
        {
            var (_, k, _) = s.FindTransition(tr.Id);
            var a = k?.Strips.FirstOrDefault(x => x.Id == tr.From);
            var b = k?.Strips.FirstOrDefault(x => x.Id == tr.To);
            if (a == null || b == null) return "";

            string St(Strip x)
            {
                return string.Join(",", x.Source, x.Kind, x.Speed, x.Blend, x.Mask, x.OffsetX, x.OffsetY, x.OffsetZ,
                    x.OffsetYaw, x.Modifiers.Count);
            }

            return string.Join("|", St(a), a.SrcIn, St(b), b.SrcOut, tr.Align, tr.Duration,
                Transitions.MatchVelocity(tr), s.Fps);
        }

        // ---------------------------------------------------------------- the evaluated output

        /// <summary>
        ///     Measures how the evaluated output plays through the transition: centre-of-mass steps and acceleration inside the
        ///     crossfade against the second around it, and foot slide against the takes on their own.
        /// </summary>
        public void MeasureOutput(SceneBake bake, BakedClip clip, TransitionReport r)
        {
            if (bake == null || clip == null || r == null || clip.Frames < 8) return;
            double half = Math.Max(Tr.Duration / 2, 1.0 / Fps);
            int f0 = bake.FrameAt(Cut - half), f1 = bake.FrameAt(Cut + half);
            int lo = Math.Max(0, bake.FrameAt(Cut - half - 1.0)),
                hi = Math.Min(clip.Frames - 1, bake.FrameAt(Cut + half + 1.0));
            if (f1 <= f0 || hi - lo < 6) return;
            int n = hi - lo + 1;
            var com = new Vec3[n];
            var fl = new Vec3[n];
            var fr = new Vec3[n];
            var local = new Quat[HumanBones.Count];
            var pos = new Vec3[HumanBones.Count];
            var world = new Quat[HumanBones.Count];
            bool feet = Rig.Exists[HumanBones.LeftFoot] && Rig.Exists[HumanBones.RightFoot];
            for (int k = 0; k < n; k++)
            {
                int f = lo + k;
                Rig.ToLocal(clip.Rot[f], local);
                Rig.Fk(clip.Hips[f], local, clip.Scale, pos, world);
                com[k] = Rig.CenterOfMass(pos);
                fl[k] = feet ? pos[HumanBones.LeftFoot] : pos[HumanBones.Hips];
                fr[k] = feet ? pos[HumanBones.RightFoot] : pos[HumanBones.Hips];
            }

            int a0 = f0 - lo, a1 = f1 - lo;
            float stepIn = 0, stepOut = 0, accIn = 0, accOut = 0;
            for (int k = 1; k < n; k++)
            {
                float st = (com[k] - com[k - 1]).Length;
                if (k >= a0 && k <= a1) stepIn = Math.Max(stepIn, st);
                else stepOut = Math.Max(stepOut, st);
            }

            int s2 = Math.Max(1, (int)Math.Round(Fps / 30.0));
            for (int k = 2 * s2; k < n - 2 * s2; k++)
            {
                // acceleration from velocities over ±s2 frames (tracking noise averaged a little)
                var v0 = (com[k] - com[k - 2 * s2]) * (Fps / (2f * s2));
                var v1 = (com[k + 2 * s2] - com[k]) * (Fps / (2f * s2));
                float acc = (v1 - v0).Length * (Fps / (2f * s2));
                if (k >= a0 && k <= a1) accIn = Math.Max(accIn, acc);
                else accOut = Math.Max(accOut, acc);
            }

            float contact = 0.03f * Setup.Height / TransitionMatch.RefHeight;
            r.ComStepIn = stepIn;
            r.ComStepAround = stepOut;
            r.ComAccelIn = accIn;
            r.ComAccelAround = accOut;
            r.SlideL = TransitionMatch.FootSlide(fl, a0, a1, 0, n - 1, contact);
            r.SlideR = TransitionMatch.FootSlide(fr, a0, a1, 0, n - 1, contact);
            // the takes on their own over the same scene time
            int pad = (int)Math.Ceiling((half + 1.0) * Fps) + 1;
            var sa = TransitionMatch.Sample(Rig, Scale, Setup, _srcA, Ta0 - pad * StepA, StepA, 2 * pad + 1);
            var sb = TransitionMatch.Sample(Rig, Scale, Setup, _srcB, Tb0 - pad * StepB, StepB, 2 * pad + 1);
            if (sa != null && sb != null)
            {
                int c = pad, w = (int)Math.Round(half * Fps);
                r.SlideBaseL = (TransitionMatch.FootSlide(sa.FootL, c - w, c + w, 0, sa.Count - 1, contact) +
                                TransitionMatch.FootSlide(sb.FootL, c - w, c + w, 0, sb.Count - 1, contact)) / 2;
                r.SlideBaseR = (TransitionMatch.FootSlide(sa.FootR, c - w, c + w, 0, sa.Count - 1, contact) +
                                TransitionMatch.FootSlide(sb.FootR, c - w, c + w, 0, sb.Count - 1, contact)) / 2;
            }

            r.HasOutput = true;
        }
    }
}
