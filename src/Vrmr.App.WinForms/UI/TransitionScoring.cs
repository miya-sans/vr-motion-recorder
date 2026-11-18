using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace VRMR.UI
{
    /// <summary>
    /// Scores every transition after each evaluation (Transition.Score is set directly: a cached measurement, not an
    /// edit), keeps a report per transition for the inspector (what the score is made of, the suggested length, how the
    /// evaluated output plays through the crossfade), and runs best-cut searches in the background.
    /// </summary>
    public sealed class TransitionScoring : ISceneModule
    {
        static readonly ConditionalWeakTable<SceneEditor, TransitionScoring> _of = new ConditionalWeakTable<SceneEditor, TransitionScoring>();
        public static TransitionScoring Of(SceneEditor ed) => ed != null && _of.TryGetValue(ed, out var m) ? m : null;

        SceneEditor _ed;
        sealed class Entry { public string Key, OutKey; public TransitionReport R; }
        int _solveStamp;
        readonly Dictionary<string, Entry> _reports = new Dictionary<string, Entry>();
        /// <summary>The last search per transition id (kept while the transition exists).</summary>
        public readonly Dictionary<string, CutSearch> Searches = new Dictionary<string, CutSearch>();
        /// <summary>Transitions with a search running, and what the last search said.</summary>
        readonly HashSet<string> _running = new HashSet<string>();
        public readonly Dictionary<string, string> Status = new Dictionary<string, string>();
        int _searchGen;

        public void Attach(SceneEditor ed)
        {
            _ed = ed;
            _of.Remove(ed); _of.Add(ed, this);
            ed.Evaluated += Update;
            ed.Nla.TransitionMenu += AddMenuItems;
            // any solve finishing (a take, an input-track re-solve, a device take) can change what a strip plays: re-score
            ed.Eval.Takes.Changed += () => Interlocked.Increment(ref _solveStamp);
            // a different scene object (opened, undo / redo): re-score; a running search is for the old one
            ed.SceneChanged += () =>
            {
                _reports.Clear();
                if (_running.Count == 0) return;
                _searchGen++;
                foreach (var id in _running) Status[id] = "Search cancelled (the scene changed).";
                _running.Clear();
            };
        }

        void AddMenuItems(System.Windows.Forms.ContextMenuStrip m, Transition tr)
        {
            if (tr == null) return;
            string id = tr.Id;
            var r = Report(id);
            m.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            m.Items.Add(new System.Windows.Forms.ToolStripMenuItem("Find best cut", null, (o, e) => { _ed.Select(tr); FindBest(id, false); }) { Enabled = !Searching(id) });
            var (_, k, _) = _ed.Scene.FindTransition(id);
            var b = k?.Strips.FirstOrDefault(x => x.Id == tr.To);
            m.Items.Add(new System.Windows.Forms.ToolStripMenuItem("Best entry into " + (b?.Name ?? "B"), null, (o, e) => { _ed.Select(tr); FindBest(id, true); }) { Enabled = !Searching(id) && b?.Kind == StripKind.Take });
            if (r != null && Math.Abs(tr.Duration - r.Suggested) > 1e-6)
                m.Items.Add(new System.Windows.Forms.ToolStripMenuItem($"Use suggested length ({r.Suggested.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} s)", null,
                    (o, e) => _ed.Edit("Suggested transition length", () => { var t = _ed.Scene.FindTransition(id).Transition; if (t != null) t.Duration = r.Suggested; })));
        }

        public TransitionReport Report(string id) => id != null && _reports.TryGetValue(id, out var e) ? e.R : null;
        public bool Searching(string id) => _running.Contains(id);

        // ---------------------------------------------------------------- after each evaluation

        /// <summary>
        /// What a transition's score depends on: both strips, the transition (not its score), their sources, the rig, and
        /// the solves (any finished solve bumps the stamp: input-track re-solves change what a take strip plays).
        /// </summary>
        string Key(Scene s, NlaTrack k, Transition tr, BakedClip clip)
        {
            var a = k.Strips.FirstOrDefault(x => x.Id == tr.From); var b = k.Strips.FirstOrDefault(x => x.Id == tr.To);
            if (a == null || b == null) return null;
            string Src(Strip st)
            {
                if (st.Kind == StripKind.Take) return s.Take(st.Source)?.Path ?? "?";
                var act = s.Action(st.Source);
                return act != null ? Json.Serialize(act.ToJson()).GetHashCode().ToString() : "?";
            }
            var tj = tr.ToJson(); tj.Remove("score");
            return string.Join("|", Json.Serialize(a.ToJson()), Src(a), Json.Serialize(b.ToJson()), Src(b), Json.Serialize(tj), clip.Profile.Name, clip.Scale, s.Fps, _solveStamp);
        }

        void Update()
        {
            var s = _ed.Scene; var bake = _ed.Bake;
            if (s == null || bake == null) return;
            bool changed = false;
            var seen = new HashSet<string>();
            var actorKeys = new Dictionary<Actor, string>();
            string actorKey(Actor x) { if (!actorKeys.TryGetValue(x, out var v)) actorKeys[x] = v = Json.Serialize(x.ToJson()).GetHashCode() + "|" + s.Actions.Count; return v; }
            foreach (var a in s.Actors)
            {
                var clip = bake.ClipOf(a.Id);
                if (clip == null) continue;
                foreach (var k in a.Tracks.Where(x => x.Kind == TrackKind.Pose))   // input tracks crossfade devices, not poses
                    foreach (var tr in k.Transitions)
                    {
                        seen.Add(tr.Id);
                        string key = Key(s, k, tr, clip);
                        if (key == null) continue;
                        _reports.TryGetValue(tr.Id, out var e);
                        TransitionProbe probe = null;
                        if (e == null || e.Key != key || tr.Score < 0)
                        {
                            try
                            {
                                probe = TransitionProbe.Create(_ed.Eval, s, tr.Id, clip.Profile, clip.Scale, out _);
                                var r = probe?.Report();
                                if (r == null) continue;
                                e = _reports[tr.Id] = new Entry { Key = key, R = r };
                            }
                            catch (Exception ex) { AppLog.Error("Transition score: " + ex); continue; }
                        }
                        if (Math.Abs(tr.Score - e.R.Match.Score) > 1e-9) { tr.Score = e.R.Match.Score; changed = true; }
                        // how the output plays: only when the evaluation is of the scene as it is now, and again only when
                        // something of this actor changed (layers above change the output too)
                        if (!bake.Pending && bake.Scene != null)
                        {
                            var (ba, bk, btr) = bake.Scene.FindTransition(tr.Id);
                            string outKey = key + "|" + actorKey(a);
                            if (btr != null && Key(bake.Scene, bk, btr, clip) == key && e.OutKey != outKey)
                                try
                                {
                                    probe = probe ?? TransitionProbe.Create(_ed.Eval, s, tr.Id, clip.Profile, clip.Scale, out _);
                                    if (probe != null) { probe.MeasureOutput(bake, clip, e.R); e.OutKey = outKey; }
                                }
                                catch (Exception ex) { AppLog.Error("Transition output: " + ex); }
                        }
                    }
            }
            foreach (var id in _reports.Keys.Where(x => !seen.Contains(x)).ToList()) { _reports.Remove(id); Searches.Remove(id); Status.Remove(id); }
            if (changed) _ed.Nla.Invalidate();
            if (_ed.SelectedTransition != null) _ed.Inspector.RefreshValues();
        }

        // ---------------------------------------------------------------- best cut / best entry

        /// <summary>
        /// Searches the most seamless cut of a transition in the background (entry: B's in point over the whole take, else A's
        /// out point ±2 s and B's in point ±3 s), then applies it as one undo step (roll edit), unless the scene changed meanwhile.
        /// </summary>
        public void FindBest(string id, bool entry)
        {
            var s = _ed.Scene; var bake = _ed.Bake;
            if (s == null || _running.Contains(id)) return;
            var (actor, k, tr) = s.FindTransition(id);
            if (tr == null) return;
            var clip = bake?.ClipOf(actor.Id);
            if (clip == null) { Status[id] = "Wait for the scene to evaluate first."; _ed.Inspector.RefreshValues(); return; }
            var snap = s.Clone();
            var probe = TransitionProbe.Create(_ed.Eval, snap, id, clip.Profile, clip.Scale, out string why);
            if (probe == null) { Status[id] = "Can't search yet: " + why + "."; _ed.Inspector.RefreshValues(); return; }
            if (entry && !probe.CanSearchEntry) { Status[id] = "The strip after the cut isn't a take: nothing to search."; _ed.Inspector.RefreshValues(); return; }
            string setup = TransitionProbe.SetupKey(s, tr);
            double ta0 = probe.Ta0, tb0 = probe.Tb0;
            int gen = _searchGen;
            _running.Add(id);
            Status[id] = entry ? "Searching all of " + probe.B.Name + "…" : "Searching cuts around this one…";
            _ed.Inspector.RefreshValues();
            new Thread(() =>
            {
                CutSearch res = null; string err = null;
                try { res = entry ? probe.SearchEntry(() => gen != _searchGen) : probe.SearchCut(2, 3, () => gen != _searchGen); }
                catch (Exception ex) { err = ex.Message; AppLog.Error("Transition search: " + ex); }
                Post(() =>
                {
                    _running.Remove(id);
                    if (gen != _searchGen) return;
                    if (res == null) { Status[id] = "Search failed" + (err != null ? ": " + err : "."); _ed.Inspector.RefreshValues(); return; }
                    res.Key = setup;
                    Searches[id] = res;
                    var (_, k2, tr2) = _ed.Scene.FindTransition(id);
                    var a2 = k2?.Strips.FirstOrDefault(x => x.Id == tr2.From); var b2 = k2?.Strips.FirstOrDefault(x => x.Id == tr2.To);
                    bool same = tr2 != null && a2 != null && b2 != null && TransitionProbe.SetupKey(_ed.Scene, tr2) == setup && Math.Abs(a2.SrcOut - ta0) < 1e-9 && Math.Abs(b2.SrcIn - tb0) < 1e-9;
                    string where = Describe(res, res.BestDa, res.BestDb);
                    if (!same) Status[id] = $"The scene changed while searching. Best was {res.BestScore * 100:0} ({where}); click the map to use it.";
                    else if (res.BestScore <= res.StartScore + 0.005f) Status[id] = $"This cut is already the best one here ({res.StartScore * 100:0}). {res.Ms} ms.";
                    else
                    {
                        Apply(id, res, res.BestDa, res.BestDb, entry ? "Best entry point" : "Best cut");
                        Status[id] = $"Applied: {res.StartScore * 100:0} → {res.BestScore * 100:0} ({where}). Ctrl+Z undoes it.";
                    }
                    _ed.Inspector.Rebuild(_ed);
                });
            }) { IsBackground = true, Name = "transition search", Priority = ThreadPriority.BelowNormal }.Start();
        }

        /// <summary>Moves the cut to a point of a search (one undo step).</summary>
        public void Apply(string id, CutSearch res, double da, double db, string label = "Cut from match map")
        {
            var s = _ed.Scene; if (s == null) return;
            _ed.Edit(label, () =>
            {
                var (_, k, tr) = s.FindTransition(id);
                if (tr == null) return;
                SceneOps.SetCut(s, k, tr, res.TaOf(da), res.TbOf(db));
            });
        }

        public static string Describe(CutSearch res, double da, double db)
        {
            if (res.Entry) return $"enter at {res.TbOf(db):0.00} s";
            return $"A ends {Sec(da)}, B starts {Sec(db)}";
        }

        static string Sec(double d) => Math.Abs(d) < 0.5 / 60 ? "as before" : (d > 0 ? "+" : "−") + Math.Abs(d).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " s";

        void Post(Action a)
        {
            try { var c = _ed.Nla; if (c.IsHandleCreated && !c.IsDisposed) c.BeginInvoke(a); } catch { }
        }
    }
}
