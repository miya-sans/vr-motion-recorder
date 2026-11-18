using System;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace VRMR.UI
{
    /// <summary>
    /// A transition: its match score (what it is made of, how the crossfade plays), length (with a suggestion from the
    /// pose difference), curve, alignment, and the best-cut / best-entry search with its match map.
    /// </summary>
    public sealed class TransitionInspector : ISceneInspector
    {
        public int Order => 30;
        public bool Applies(SceneEditor ed) => ed.SelectionKind == "transition";

        static string Cm(float m) => (m * 100).ToString(m < 0.1f ? "0.0" : "0", CultureInfo.InvariantCulture);
        static string S2(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

        public void Build(InspectorContext c)
        {
            var ed = c.Editor; var t0 = ed.SelectedTransition; if (t0 == null) return;
            string id = t0.Id;
            var mod = TransitionScoring.Of(ed);
            Transition T() => ed.Scene.FindTransition(id).Transition;
            NlaTrack K() => ed.Scene.FindTransition(id).Track;
            Strip SA() { var t = T(); return t == null ? null : K()?.Strips.FirstOrDefault(x => x.Id == t.From); }
            Strip SB() { var t = T(); return t == null ? null : K()?.Strips.FirstOrDefault(x => x.Id == t.To); }

            c.Section("Transition");
            var info = c.Note("", false);
            var score = c.Add(new TransitionScoreView());
            var parts = c.Note("", true);
            var plays = c.Note("", true);
            new ToolTip().SetToolTip(score, "How well the poses match across the cut (0-100): joint positions and velocities over the crossfade, with the alignment below; hips and feet count most. Green from 80.");

            Props.Number(c, "Length", () => T()?.Duration ?? 0, v => ed.Edit("Transition length", () => T().Duration = Math.Max(0, v)), "0.00", " s", "Crossfade length, centred on the cut", 0);
            var useBtn = new DarkButton("Use suggested length");
            useBtn.Click += (o, e) =>
            {
                var r = mod?.Report(id); var t = T();
                if (r == null || t == null || Math.Abs(t.Duration - r.Suggested) < 1e-6) return;
                ed.Edit("Suggested transition length", () => T().Duration = r.Suggested);
            };
            new ToolTip().SetToolTip(useBtn, "A bigger pose difference needs a longer crossfade so no joint (hips and feet least) is swept along too fast");
            c.Page.Row(useBtn);
            var lenNote = c.Note("", true);

            Props.Choice(c, "Curve", new[] { "Smooth", "Linear" }, () => (int)(T()?.Curve ?? TransitionCurve.Smooth), i => ed.Edit("Transition curve", () => T().Curve = (TransitionCurve)i));
            Props.Choice(c, "Align", new[] { "Don't move", "Centre of mass + facing", "Hips + facing", "Feet + facing" }, () => (int)(T()?.Align ?? TransitionAlign.Com),
                i => ed.Edit("Transition alignment", () => T().Align = (TransitionAlign)i),
                "Moves and turns the strip after the cut so the body continues from where the strip before it is: the chosen point lands on the same spot on the floor, facing from hips and shoulders. Heights don't change, so the feet stay on their floor.");
            Props.Check(c, "Match travel direction", () => T()?.MatchVelocity ?? false, v => ed.Edit("Transition velocity matching", () => T().MatchVelocity = v),
                "When both strips are moving (walking), turn the next strip so its centre of mass keeps travelling the same way instead of only matching the body's facing");

            c.Section("Best cut");
            var bestBtn = new DarkButton("Find best cut", DarkButton.Kind.Accent);
            var entryBtn = new DarkButton("Best entry into B");
            bestBtn.Click += (o, e) => mod?.FindBest(id, false);
            entryBtn.Click += (o, e) => mod?.FindBest(id, true);
            new ToolTip().SetToolTip(bestBtn, "Searches A's end ±2 s and B's start ±3 s for the most seamless cut and moves the cut there (roll edit, one undo step)");
            new ToolTip().SetToolTip(entryBtn, "Keeps A's end and searches all of B's take for the best place to enter it (for a strip that plays a whole take, whose start is often a setup pose)");
            c.Page.Row(bestBtn, entryBtn);
            var status = c.Note("", false);
            var map = c.Add(new TransitionMap());
            map.Pick += (da, db) =>
            {
                var res = mod?.Searches.TryGetValue(id, out var r) == true ? r : null;
                if (res == null || map.Stale) return;
                mod.Apply(id, res, da, db);
                mod.Status[id] = "Cut moved to " + TransitionScoring.Describe(res, da, db) + ". Ctrl+Z undoes it.";
            };
            c.Note("Drag the middle of a transition in the editor below to move the cut by hand (both takes keep their other ends); drag its edges to change its length.");

            Action refresh = () =>
            {
                var t = T(); var a = SA(); var b = SB();
                if (t == null || a == null || b == null) return;
                info.Text = $"A   {a.Name}\nB   {b.Name}\nCut at {S2(b.Start)} s: A ends at {S2(a.SrcOut)} s of its take, B starts at {S2(b.SrcIn)} s";
                var r = mod?.Report(id);
                score.Score = r != null ? r.Match.Score : t.Score;
                score.Pending = ed.Bake == null || ed.Bake.Pending ? "Scoring when the takes are solved…" : "Scoring…";
                score.Invalidate();
                if (r != null)
                {
                    var m = r.Match;
                    string worst = m.WorstJoint >= 0 ? $"  ·  most: {Nice(HumanBones.Names[m.WorstJoint])} {Cm(m.WorstErr)} cm" : "";
                    parts.Text = $"Pose {Cm(m.PosErr)} cm apart, moving {(m.VelErr * 100):0} cm/s differently{worst}";
                    if (r.HasOutput)
                    {
                        bool jump = r.ComStepIn > Math.Max(0.01f, r.ComStepAround * 1.5f);
                        string com = jump ? $"centre of mass jumps {r.ComStepIn * 1000:0} mm in a frame ({r.ComStepAround * 1000:0} mm around)"
                                          : $"centre of mass continuous (largest step {r.ComStepIn * 1000:0} mm, {r.ComStepAround * 1000:0} mm around)";
                        plays.Text = $"Crossfade: {com}; foot slide L {Cm(r.SlideL)} / R {Cm(r.SlideR)} cm (the takes alone {Cm((r.SlideBaseL + r.SlideBaseR) / 2)} cm)";
                        plays.Color = jump ? Theme.Warn : Theme.TextFaint;
                    }
                    else plays.Text = "";
                    bool same = Math.Abs(t.Duration - r.Suggested) < 1e-6;
                    useBtn.Text = same ? $"Length is the suggested {S2(r.Suggested)} s" : $"Use suggested length: {S2(r.Suggested)} s";
                    useBtn.Enabled = !same; useBtn.Invalidate();
                    lenNote.Text = r.LengthNote ?? "";
                }
                else { parts.Text = ""; plays.Text = ""; useBtn.Text = "Use suggested length"; useBtn.Enabled = false; useBtn.Invalidate(); lenNote.Text = ""; }
                bool busy = mod != null && mod.Searching(id);
                bestBtn.Enabled = !busy && mod != null; entryBtn.Enabled = !busy && mod != null && b.Kind == StripKind.Take;
                bestBtn.Invalidate(); entryBtn.Invalidate();
                status.Text = mod != null && mod.Status.TryGetValue(id, out var st) ? st : "";
                status.Color = busy ? Theme.Accent : Theme.TextDim;
                var res = mod != null && mod.Searches.TryGetValue(id, out var sr) ? sr : null;
                map.Search = res;
                map.Visible = res != null;
                if (res != null)
                {
                    map.Stale = TransitionProbe.SetupKey(ed.Scene, t) != res.Key;
                    map.CurDa = (a.SrcOut - res.Ta0) / Math.Max(1e-9, res.SpeedA);
                    map.CurDb = (b.SrcIn - res.Tb0) / Math.Max(1e-9, res.SpeedB);
                }
                map.Invalidate();
            };
            c.OnRefresh(refresh);
            refresh(); // filled now, not only after the next evaluation
        }

        /// <summary>"LeftHand" -> "left hand".</summary>
        static string Nice(string bone)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in bone) { if (char.IsUpper(ch) && sb.Length > 0) sb.Append(' '); sb.Append(char.ToLowerInvariant(ch)); }
            return sb.ToString();
        }
    }
}
