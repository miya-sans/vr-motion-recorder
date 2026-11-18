using System;
using System.Collections.Generic;
using System.Linq;

namespace VRMR
{
    /// <summary>Edits on the scene model (no UI): strips, transitions, tracks, actions. Takes are never touched.</summary>
    public static class SceneOps
    {
        /// <summary>Two strips meet when the gap between them is under this (seconds).</summary>
        public const double Adjacent = 1.5 / 60.0;

        // ---------------------------------------------------------------- tracks

        public static NlaTrack AddTrack(Actor a, int index, string name = null)
        {
            var t = new NlaTrack { Id = Scene.NewId("k"), Name = name ?? NextTrackName(a) };
            a.Tracks.Insert(MathX.Clamp(index, 0, a.Tracks.Count), t);
            return t;
        }

        static string NextTrackName(Actor a)
        {
            for (int i = a.Tracks.Count + 1;; i++)
            {
                string n = "Track " + i;
                if (!a.Tracks.Any(t => t.Name == n)) return n;
            }
        }

        /// <summary>Is [start, end) free on the track (ignoring the given strips)?</summary>
        public static bool IsFree(NlaTrack t, double start, double end, params Strip[] ignore)
        {
            foreach (var s in t.Strips)
            {
                if (ignore.Contains(s)) continue;
                if (start < s.End - 1e-6 && end > s.Start + 1e-6)
                {
                    // strips joined by a transition may overlap by the transition's handles: they don't, strips stay adjacent
                    return false;
                }
            }

            return true;
        }

        /// <summary>The preferred track if [start, end) is free there, else the first free track above it (a new one if needed).</summary>
        public static NlaTrack FreeTrack(Actor a, NlaTrack preferred, double start, double end)
        {
            int i = preferred != null ? a.Tracks.IndexOf(preferred) : 0;
            if (i < 0) i = 0;
            for (int k = i; k < a.Tracks.Count; k++)
                if (IsFree(a.Tracks[k], start, end))
                    return a.Tracks[k];
            return AddTrack(a, a.Tracks.Count);
        }

        // ---------------------------------------------------------------- strips

        public static Strip AddTakeStrip(Scene s, NlaTrack t, TakeRef take, double start, double srcIn, double srcOut)
        {
            var st = new Strip
            {
                Id = Scene.NewId("s"), Name = take.Name, Kind = StripKind.Take, Source = take.Id, Start = start,
                SrcIn = srcIn, SrcOut = srcOut
            };
            t.Strips.Add(st);
            t.SortStrips();
            return st;
        }

        /// <summary>A new keyframe action and a strip playing it ("keyframe take": animate without mocap).</summary>
        public static Strip AddActionStrip(Scene s, NlaTrack t, string name, double start, double length,
            BlendMode blend = BlendMode.Replace, KeyAction act = null)
        {
            if (act == null)
            {
                act = new KeyAction { Id = Scene.NewId("x"), Name = name };
                s.Actions.Add(act);
            }

            var st = new Strip
            {
                Id = Scene.NewId("s"), Name = name, Kind = StripKind.Action, Source = act.Id, Start = start, SrcIn = 0,
                SrcOut = Math.Max(1.0 / s.Fps, length), Blend = blend
            };
            t.Strips.Add(st);
            t.SortStrips();
            return st;
        }

        /// <summary>Source time limits of a strip's data: a take's [0, duration]; actions are unlimited.</summary>
        public static void SourceLimits(Scene s, Strip st, out double lo, out double hi)
        {
            lo = double.NegativeInfinity;
            hi = double.PositiveInfinity;
            if (st.Kind == StripKind.Take)
            {
                var t = s.Take(st.Source);
                lo = 0;
                hi = t != null && t.Duration > 0 ? t.Duration : double.PositiveInfinity;
            }
        }

        /// <summary>
        ///     Cuts a strip in two at scene time t (nothing is lost: both halves still reference the whole take). Returns the
        ///     right half.
        /// </summary>
        public static Strip Split(Scene s, NlaTrack t, Strip st, double time)
        {
            if (time <= st.Start + 1e-3 || time >= st.End - 1e-3) return null;
            var right = st.Clone();
            right.Id = Scene.NewId("s");
            double cut = st.ToSource(time);
            st.SrcOut = cut;
            right.SrcIn = cut;
            right.Start = time;
            st.BlendOut = 0;
            right.BlendIn = 0;
            if (right.Name == st.Name) right.Name = st.Name;
            // the outgoing transition now leaves from the right half
            foreach (var tr in t.Transitions.Where(x => x.From == st.Id)) tr.From = right.Id;
            t.Strips.Add(right);
            t.SortStrips();
            return right;
        }

        public static void DeleteStrip(Scene s, NlaTrack t, Strip st)
        {
            t.Strips.Remove(st);
            t.Transitions.RemoveAll(x => x.From == st.Id || x.To == st.Id);
            // actions no strip uses any more are kept (undo restores them anyway); drop them on save? keep: they are small.
        }

        public static Strip Duplicate(Scene s, Actor a, NlaTrack t, Strip st)
        {
            var c = st.Clone();
            c.Id = Scene.NewId("s");
            c.Start = st.End;
            var dest = FreeTrack(a, t, c.Start, c.End);
            dest.Strips.Add(c);
            dest.SortStrips();
            return c;
        }

        /// <summary>Moves a strip to another track and/or time (no overlap check; see IsFree).</summary>
        public static void Move(NlaTrack from, NlaTrack to, Strip st, double start)
        {
            st.Start = start;
            if (from != to)
            {
                from.Strips.Remove(st);
                from.Transitions.RemoveAll(x => x.From == st.Id || x.To == st.Id);
                to.Strips.Add(st);
            }

            to.SortStrips();
        }

        // ---------------------------------------------------------------- transitions

        /// <summary>The strip that starts where this one ends, on the same track.</summary>
        public static Strip Next(NlaTrack t, Strip st)
        {
            return t.Strips.Where(x => x != st && Math.Abs(x.Start - st.End) < Adjacent).OrderBy(x => x.Start)
                .FirstOrDefault();
        }

        public static Strip Prev(NlaTrack t, Strip st)
        {
            return t.Strips.Where(x => x != st && Math.Abs(x.End - st.Start) < Adjacent).OrderBy(x => -x.Start)
                .FirstOrDefault();
        }

        /// <summary>
        ///     A crossfade between a strip and the next one on its track. If they don't meet, the second is moved to start
        ///     where the first ends. Duration is limited to the shorter strip.
        /// </summary>
        public static Transition AddTransition(NlaTrack t, Strip a, Strip b, double duration = 0.5)
        {
            if (a.Start > b.Start)
            {
                var x = a;
                a = b;
                b = x;
            }

            if (!IsFree(t, a.End, a.End + b.Length, a, b)) return null;
            b.Start = a.End;
            t.Transitions.RemoveAll(x => x.From == a.Id || x.To == b.Id);
            var tr = new Transition
            {
                Id = Scene.NewId("x"), From = a.Id, To = b.Id,
                Duration = Math.Max(0, Math.Min(duration, Math.Min(a.Length, b.Length)))
            };
            a.BlendOut = 0;
            b.BlendIn = 0;
            t.Transitions.Add(tr);
            t.SortStrips();
            return tr;
        }

        /// <summary>
        ///     Moves the cut of a transition (roll edit): the first strip now ends at source time srcOutA, the second starts
        ///     at source time srcInB right where the first ends; the second strip's own end in its take stays.
        /// </summary>
        public static void SetCut(Scene s, NlaTrack t, Transition tr, double srcOutA, double srcInB)
        {
            var a = t.Strips.FirstOrDefault(x => x.Id == tr.From);
            var b = t.Strips.FirstOrDefault(x => x.Id == tr.To);
            if (a == null || b == null) return;
            SourceLimits(s, a, out double alo, out double ahi);
            SourceLimits(s, b, out double blo, out double bhi);
            a.SrcOut = MathX.Clamp(srcOutA, a.SrcIn + 1.0 / s.Fps, ahi);
            b.SrcIn = MathX.Clamp(srcInB, blo, b.SrcOut - 1.0 / s.Fps);
            b.Start = a.End;
            ShiftAfter(t, b);
            tr.Score = -1;
        }

        /// <summary>Keeps strips that are chained by transitions after b adjacent (moves them with b's end).</summary>
        static void ShiftAfter(NlaTrack t, Strip b)
        {
            var guard = new HashSet<string>();
            var cur = b;
            while (cur != null && guard.Add(cur.Id))
            {
                var tr = t.TransitionOutOf(cur.Id);
                var nx = tr != null ? t.Strips.FirstOrDefault(x => x.Id == tr.To) : null;
                if (nx == null) break;
                nx.Start = cur.End;
                cur = nx;
            }

            t.SortStrips();
        }

        /// <summary>Removes transitions whose strips no longer meet, keeps strips sorted.</summary>
        public static void Normalize(NlaTrack t)
        {
            t.SortStrips();
            t.Transitions.RemoveAll(tr =>
            {
                var a = t.Strips.FirstOrDefault(x => x.Id == tr.From);
                var b = t.Strips.FirstOrDefault(x => x.Id == tr.To);
                return a == null || b == null || Math.Abs(a.End - b.Start) > Adjacent;
            });
            foreach (var tr in t.Transitions)
            {
                var a = t.Strips.First(x => x.Id == tr.From);
                var b = t.Strips.First(x => x.Id == tr.To);
                b.Start = a.End; // exact
                tr.Duration = Math.Max(0, Math.Min(tr.Duration, Math.Min(a.Length, b.Length)));
            }
        }

        public static void NormalizeAll(Scene s)
        {
            foreach (var a in s.Actors)
            foreach (var t in a.Tracks)
                Normalize(t);
        }

        // ---------------------------------------------------------------- takes

        /// <summary>Takes no strip uses any more (kept in the file until removed here).</summary>
        public static void DropUnusedTakes(Scene s)
        {
            var used = new HashSet<string>(s.AllStrips().Where(x => x.Strip.Kind == StripKind.Take)
                .Select(x => x.Strip.Source));
            s.Takes.RemoveAll(t => !used.Contains(t.Id));
            var usedA = new HashSet<string>(s.AllStrips().Where(x => x.Strip.Kind == StripKind.Action)
                .Select(x => x.Strip.Source));
            s.Actions.RemoveAll(a => !usedA.Contains(a.Id));
        }
    }
}
