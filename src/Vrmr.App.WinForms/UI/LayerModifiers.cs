using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace VRMR.UI
{
    /// <summary>
    ///     The modifier stack of a strip or an actor in the Scene tab: each modifier with its on/off switch, order, remove and
    ///     parameters (from IModifierParams; unknown modifiers get generic rows), and an "Add" menu. Used by the Layer
    ///     inspector (strips) and the actor cleanup section.
    /// </summary>
    public static class LayerModifiers
    {
        static readonly Dictionary<string, double> PendingMark = new();

        public static void Build(InspectorContext c, Host h, string title = "Modifiers")
        {
            var ed = c.Editor;
            c.Section(title);
            var mods = h.List();
            if (mods == null) return;
            if (mods.Count == 0)
                c.Note(h.What == "strip"
                    ? "None. Cleanup modifiers repair this strip's poses before it is layered."
                    : h.What == "input"
                        ? "None. Device cleanup smooths or fills this layer's devices before the take below is solved again."
                        : "None. Cleanup on the whole actor works on the final pose, after all layers and transitions.");
            for (int i = 0; i < mods.Count; i++) BuildOne(c, h, i);
            // add menu
            if (h.Types.Count > 0)
            {
                var add = new DarkButton("+ Add modifier") { Height = Theme.S(28) };
                var menu = new ContextMenuStrip { ShowImageMargin = false };
                foreach (var t in h.Types)
                {
                    var tt = t;
                    var it = menu.Items.Add(t.Label);
                    if (t.Impl is IModifierParams mp) it.ToolTipText = mp.Help;
                    it.Click += (o, e) => ed.Edit("Add " + tt.Label.ToLowerInvariant(),
                        () => h.List()?.Add(ModArgs.Create(tt.Type, tt.Impl as IModifierParams)));
                }

                add.Click += (o, e) => menu.Show(add, new Point(0, add.Height));
                c.Page.Row(add, new Panel { BackColor = Theme.Panel });
            }
        }

        static void BuildOne(InspectorContext c, Host h, int index)
        {
            var ed = c.Editor;

            StripModifier M()
            {
                var l = h.List();
                return l != null && index < l.Count ? l[index] : null;
            }

            var m0 = M();
            if (m0 == null) return;
            var info = h.Types.FirstOrDefault(t => t.Type == m0.Type);
            var desc = info.Impl as IModifierParams;
            string label = info.Label ?? m0.Type;

            // header: on/off, name, up / down / remove
            var on = new DarkCheck(label, m0.Enabled) { Font = Theme.UIBold };
            on.CheckedChanged += (o, e) =>
            {
                var m = M();
                if (m != null && m.Enabled != on.Checked)
                    ed.Edit((on.Checked ? "Enable " : "Disable ") + label.ToLowerInvariant(),
                        () => M().Enabled = on.Checked);
            };
            if (desc != null) new ToolTip().SetToolTip(on, desc.Help);
            var up = Small("▲", "Move up (applied earlier)", () => Move(ed, h, index, -1));
            up.Enabled = index > 0;
            var dn = Small("▼", "Move down (applied later)", () => Move(ed, h, index, +1));
            dn.Enabled = index < h.List().Count - 1;
            var rm = Small("✕", "Remove", () => ed.Edit("Remove " + label.ToLowerInvariant(), () =>
            {
                var l = h.List();
                if (index < l.Count) l.RemoveAt(index);
            }));
            c.Add(new HeaderRow(on, up, dn, rm));
            if (info.Type == null) c.Note($"Unknown modifier type \"{m0.Type}\" (kept as it is).");

            if (desc != null)
            {
                foreach (var p in desc.Params) BuildParam(c, h, M, p, label);
            }
            else
            {
                // generic: numbers and switches as they are stored
                foreach (var kv in m0.Params.ToList())
                {
                    string k = kv.Key;
                    if (kv.Value is bool)
                        Props.Check(c, k, () => M()?.Flag(k, false) ?? false,
                            v => ed.Edit(label + " " + k, () => M().Params[k] = v));
                    else if (kv.Value is double || kv.Value is int || kv.Value is float || kv.Value is long)
                        Props.Number(c, k, () => M()?.Num(k, 0) ?? 0,
                            v => ed.Edit(label + " " + k, () => M().Params[k] = v), "0.###");
                }
            }
        }

        static DarkButton Small(string text, string tip, Action click)
        {
            var b = new DarkButton(text, DarkButton.Kind.Ghost) { Width = Theme.S(26), Height = Theme.S(24) };
            b.Click += (o, e) => click();
            new ToolTip().SetToolTip(b, tip);
            return b;
        }

        static void Move(SceneEditor ed, Host h, int i, int d)
        {
            var l = h.List();
            int j = i + d;
            if (l == null || j < 0 || j >= l.Count) return;
            ed.Edit("Reorder modifiers", () =>
            {
                var l2 = h.List();
                var x = l2[i];
                l2.RemoveAt(i);
                l2.Insert(j, x);
            });
        }

        static string Ui(double v, ModParam p)
        {
            return (v * p.UiScale).ToString(p.Format, CultureInfo.InvariantCulture);
        }

        static void BuildParam(InspectorContext c, Host h, Func<StripModifier> M, ModParam p, string label)
        {
            var ed = c.Editor;
            string undo = label + ": " + p.Label.ToLowerInvariant();
            switch (p.Kind)
            {
                case ModParamKind.Number:
                    if (p.Slider)
                        Props.Slider(c, p.Label, (float)(p.Min * p.UiScale), (float)(p.Max * p.UiScale),
                            () => (float)((M()?.Num(p.Key, p.Default) ?? p.Default) * p.UiScale),
                            v =>
                            {
                                var m = M();
                                if (m != null) m.Params[p.Key] = v / p.UiScale;
                            }, 0.01f, p.Format, p.Suffix, p.Tip);
                    else
                        Props.Number(c, p.Label, () => (M()?.Num(p.Key, p.Default) ?? p.Default) * p.UiScale,
                            v => ed.Edit(undo, () => M().Params[p.Key] = MathX.Clamp(v / p.UiScale, p.Min, p.Max)),
                            p.Format, p.Suffix, p.Tip, p.Min * p.UiScale, p.Max * p.UiScale);
                    break;
                case ModParamKind.Check:
                    Props.Check(c, p.Label, () => M()?.Flag(p.Key, p.Default != 0) ?? false,
                        v => ed.Edit(undo, () => M().Params[p.Key] = v), p.Tip);
                    break;
                case ModParamKind.Choice:
                    Props.Choice(c, p.Label, p.ChoiceLabels,
                        () => Math.Max(0,
                            Array.IndexOf(p.Choices, M()?.Str(p.Key, p.Choices[(int)p.Default]) ?? p.Choices[0])),
                        i => ed.Edit(undo, () => M().Params[p.Key] = p.Choices[i]), p.Tip);
                    break;
                case ModParamKind.Parts:
                    PartsChoice(c, p.Label, () => M()?.Parts(p.Key, p.DefaultParts) ?? p.DefaultParts,
                        v => ed.Edit(undo, () => M().Params[p.Key] = BodyMask.ToJson(v)), p.Tip);
                    break;
                case ModParamKind.Ranges:
                    BuildRanges(c, h, M, p, label);
                    break;
            }
        }

        /// <summary>A body-part preset list (with "Custom: ..." when the mask isn't a preset).</summary>
        public static DarkCombo PartsChoice(InspectorContext c, string label, Func<BodyPart> get, Action<BodyPart> set,
            string tip = null)
        {
            var presets = BodyMask.Presets.ToList();
            presets.Insert(2, ("Body, not the root", BodyMask.Body & ~BodyPart.Root));
            presets = presets.GroupBy(x => x.Mask).Select(g => g.First()).ToList();
            var items = presets.Select(x => x.Name).ToList();
            items.Add("Custom");

            int Idx()
            {
                var m = get();
                int i = presets.FindIndex(x => x.Mask == m);
                return i >= 0 ? i : items.Count - 1;
            }

            var cb = Props.Choice(c, label, items.ToArray(), Idx, i =>
            {
                if (i < presets.Count) set(presets[i].Mask);
            }, tip);
            var note = c.Note("");
            c.OnRefresh(() =>
            {
                var m = get();
                note.Text = presets.Any(x => x.Mask == m) ? "" : "Custom: " + BodyMask.Describe(m);
                note.Visible = note.Text.Length > 0;
            });
            note.Visible = false;
            return cb;
        }

        // ---------------------------------------------------------------- ranges (repair)

        static void BuildRanges(InspectorContext c, Host h, Func<StripModifier> M, ModParam p, string label)
        {
            var ed = c.Editor;
            var list = new StackPanel { Pad = 0, Gap = 2, AutoScroll = false, Height = Theme.S(24) };
            c.Add(list);
            string pendKey = h.Key + "|" + p.Key;

            void Fill()
            {
                list.SuspendLayout();
                foreach (var x in list.Controls.Cast<Control>().ToList())
                {
                    list.Controls.Remove(x);
                    x.Dispose();
                }

                var rs = M()?.Ranges(p.Key) ?? new List<RepairRange>();
                if (rs.Count == 0)
                    list.Controls.Add(new InfoLabel("No ranges yet: mark them at the playhead, or find glitches.")
                        { Color = Theme.TextFaint, Height = Theme.S(20) });
                for (int i = 0; i < rs.Count; i++)
                {
                    var r = rs[i];
                    int idx = i;
                    string txt = h.RangeUnits == "scene"
                        ? $"{r.A:0.00} – {r.B:0.00} s"
                        : $"{h.ToScene(r.A):0.00} – {h.ToScene(r.B):0.00} s (take {r.A:0.00} – {r.B:0.00})";
                    if (r.Parts.HasValue) txt += "  ·  " + BodyMask.Describe(r.Parts.Value);
                    if (r.Note == "found") txt += "  ·  found";
                    var lbl = new InfoLabel(txt) { Wrap = false, Color = Theme.TextDim, Cursor = Cursors.Hand };
                    lbl.Click += (o, e) => ed.Seek(h.ToScene(r.A));
                    new ToolTip().SetToolTip(lbl, "Click: go to its start");
                    var del = Small("✕", "Remove this range", () => ed.Edit(label + ": remove range", () =>
                    {
                        var l = M().Ranges(p.Key);
                        if (idx < l.Count)
                        {
                            l.RemoveAt(idx);
                            M().SetRanges(l, p.Key);
                        }
                    }));
                    list.Controls.Add(new HeaderRow(lbl, del) { Height = Theme.S(22) });
                }

                list.Height = list.Controls.Cast<Control>().Sum(x => x.Height + Theme.S(2)) + Theme.S(2);
                list.ResumeLayout();
                list.PerformLayout();
            }

            Fill();
            c.OnRefresh(Fill);
            DarkButton mark = null;
            mark = new DarkButton(PendingMark.ContainsKey(pendKey) ? "End range here" : "Start range here");
            new ToolTip().SetToolTip(mark,
                "Marks a stretch to repair: click at its start, move the playhead to its end, click again");
            mark.Click += (o, e) =>
            {
                double now = h.FromScene(ed.Playhead);
                if (!PendingMark.TryGetValue(pendKey, out double a))
                {
                    PendingMark[pendKey] = now;
                    mark.Text = "End range here";
                    mark.Invalidate();
                    return;
                }

                PendingMark.Remove(pendKey);
                if (Math.Abs(now - a) < 1e-3)
                {
                    mark.Text = "Start range here";
                    mark.Invalidate();
                    return;
                }

                ed.Edit(label + ": add range", () =>
                {
                    var l = M().Ranges(p.Key);
                    l.Add(new RepairRange { A = Math.Min(a, now), B = Math.Max(a, now) });
                    M().SetRanges(l, p.Key);
                });
            };
            var find = new DarkButton("Find glitches");
            new ToolTip().SetToolTip(find,
                "Adds stretches where a hand, foot or the head jumps faster than a body can move, or a limb snaps round");
            find.Enabled = h.FindGlitches != null;
            find.Click += (o, e) =>
            {
                var found = h.FindGlitches?.Invoke();
                if (found == null)
                {
                    MessageBox.Show(ed.Panel, "The take is still being solved. Try again in a moment.",
                        "Find glitches");
                    return;
                }

                if (found.Count == 0)
                {
                    MessageBox.Show(ed.Panel, "No glitches found (nothing moves faster than a body can).",
                        "Find glitches");
                    return;
                }

                ed.Edit(label + ": find glitches", () =>
                {
                    var l = M().Ranges(p.Key);
                    foreach (var f in found)
                        if (!l.Any(x => x.A < f.B && x.B > f.A))
                            l.Add(f);
                    M().SetRanges(l, p.Key);
                });
            };
            var clear = new DarkButton("Clear");
            clear.Click += (o, e) =>
            {
                if ((M()?.Ranges(p.Key).Count ?? 0) > 0)
                    ed.Edit(label + ": clear ranges", () => M().SetRanges(new List<RepairRange>(), p.Key));
            };
            c.Page.Row(mark, find, clear);
        }

        /// <summary>Where the stack lives and how its time ranges map (strip: take seconds; actor: scene seconds).</summary>
        public sealed class Host
        {
            /// <summary>Finds glitch stretches (stored time); null = no finder.</summary>
            public Func<List<RepairRange>> FindGlitches;

            /// <summary>Range time (stored) of a scene time, and back; a label for range units.</summary>
            public Func<double, double> FromScene = t => t, ToScene = t => t;

            /// <summary>A key that stays the same across undo (for pending marks).</summary>
            public string Key = "";

            public Func<List<StripModifier>> List;
            public string RangeUnits = "scene";

            /// <summary>Available types: type, label, implementation (IModifierParams if it describes its parameters).</summary>
            public List<(string Type, string Label, object Impl)> Types;

            public string What = "strip";
        }

        /// <summary>Header row: a check on the left (fills), small buttons on the right.</summary>
        sealed class HeaderRow : Panel
        {
            readonly Control[] _btns;
            readonly Control _main;

            public HeaderRow(Control main, params Control[] btns)
            {
                _main = main;
                _btns = btns;
                BackColor = Theme.Panel;
                Height = Theme.S(28);
                Controls.Add(main);
                foreach (var b in btns) Controls.Add(b);
            }

            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e);
                int x = Width;
                for (int i = _btns.Length - 1; i >= 0; i--)
                {
                    x -= _btns[i].Width;
                    _btns[i].SetBounds(x, (Height - _btns[i].Height) / 2, _btns[i].Width, _btns[i].Height);
                    x -= Theme.S(2);
                }

                _main.SetBounds(0, (Height - _main.Height) / 2, Math.Max(10, x - Theme.S(6)), _main.Height);
            }
        }
    }
}
