using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace VRMR.UI
{
    /// <summary>
    /// One section of the Scene tab. Every class implementing this (with a parameterless constructor) is found by
    /// reflection; the sections that apply to the current selection are built in Order. Each feature keeps its
    /// inspector in its own file.
    /// </summary>
    public interface ISceneInspector
    {
        int Order { get; }
        bool Applies(SceneEditor ed);
        void Build(InspectorContext c);
    }

    /// <summary>What an inspector builds into: the page, the editor, refresh callbacks (run after each evaluation).</summary>
    public sealed class InspectorContext
    {
        public SceneEditor Editor;
        public StackPanel Page;
        internal readonly List<Action> Refreshers = new List<Action>();
        /// <summary>Called after every evaluation and edit while this section is shown.</summary>
        public void OnRefresh(Action a) => Refreshers.Add(a);
        public T Add<T>(T c) where T : Control => Page.Add(c);
        /// <summary>A section header that folds: click it to hide / show everything up to the next header (remembered per title).</summary>
        public void Section(string title) => Page.Add(new InspectorHeader(title));
        public InfoLabel Note(string text, bool faint = true) => Page.Add(new InfoLabel(text) { Color = faint ? Theme.TextFaint : Theme.TextDim });
    }

    /// <summary>A Scene-tab section header with a fold chevron; the host hides the controls under a folded header.</summary>
    public sealed class InspectorHeader : DarkControl
    {
        internal static readonly HashSet<string> Folded = new HashSet<string>();
        public bool IsFolded => Folded.Contains(Text);
        public InspectorHeader(string title) { Text = title; Height = Theme.S(28); Cursor = Cursors.Hand; }
        protected override void OnClick(EventArgs e)
        {
            if (!Folded.Remove(Text)) Folded.Add(Text);
            (Parent as InspectorHost)?.ApplyFolds();
            base.OnClick(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Panel);
            float cx = Theme.Sf(5), cy = Height - Theme.Sf(10), r = Theme.Sf(3.5f);
            using (var b = new SolidBrush(Theme.TextFaint))
                g.FillPolygon(b, IsFolded ? new[] { new PointF(cx - r / 2, cy - r), new PointF(cx + r, cy), new PointF(cx - r / 2, cy + r) }
                                          : new[] { new PointF(cx - r, cy - r / 2), new PointF(cx + r, cy - r / 2), new PointF(cx, cy + r) });
            TextRenderer.DrawText(g, Text.ToUpperInvariant(), Theme.Small, new Rectangle(Theme.S(14), 0, Width - Theme.S(14), Height - 2), Theme.TextFaint, TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Bottom);
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }
    }

    /// <summary>The Scene tab: inspector sections for the selection (scene, actor, track, strip, transition...).</summary>
    public sealed class InspectorHost : StackPanel
    {
        /// <summary>Hides the controls under folded headers.</summary>
        public void ApplyFolds()
        {
            SuspendLayout();
            bool hide = false;
            foreach (Control c in Controls)
            {
                if (c is InspectorHeader h) { hide = h.IsFolded; h.Visible = true; h.Invalidate(); continue; }
                c.Visible = !hide;
            }
            ResumeLayout();
            PerformLayout();
        }

        static List<ISceneInspector> _all;
        static List<ISceneInspector> All()
        {
            if (_all != null) return _all;
            var l = new List<ISceneInspector>();
            foreach (var t in typeof(InspectorHost).Assembly.GetTypes())
            {
                if (t.IsAbstract || t.IsInterface || !typeof(ISceneInspector).IsAssignableFrom(t) || t.GetConstructor(Type.EmptyTypes) == null) continue;
                try { l.Add((ISceneInspector)Activator.CreateInstance(t)); } catch (Exception ex) { AppLog.Warn("Inspector " + t.Name + ": " + ex.Message); }
            }
            return _all = l.OrderBy(i => i.Order).ToList();
        }

        InspectorContext _ctx;

        /// <summary>Rebuilds the sections for the editor's current selection.</summary>
        public void Rebuild(SceneEditor ed)
        {
            int scroll = -AutoScrollPosition.Y;
            SuspendLayout();
            var old = Controls.Cast<Control>().ToList();
            Controls.Clear();
            foreach (var c in old) c.Dispose();
            _ctx = new InspectorContext { Editor = ed, Page = this };
            if (ed.Scene != null)
                foreach (var i in All())
                {
                    bool ok;
                    try { ok = i.Applies(ed); } catch { ok = false; }
                    if (!ok) continue;
                    try { i.Build(_ctx); }
                    catch (Exception ex) { AppLog.Error("Inspector " + i.GetType().Name + ": " + ex); Add(new InfoLabel("Error: " + ex.Message) { Color = Theme.Rec }); }
                }
            ResumeLayout();
            ApplyFolds();
            AutoScrollPosition = new Point(0, scroll);
        }

        /// <summary>Runs the sections' refresh callbacks (after an evaluation).</summary>
        public void RefreshValues()
        {
            if (_ctx == null) return;
            foreach (var r in _ctx.Refreshers) try { r(); } catch (Exception ex) { AppLog.Error("Inspector refresh: " + ex.Message); }
            PerformLayout();
        }
    }

    /// <summary>Property rows for inspectors: a label on the left, an editor on the right. Text fields commit on Enter / leaving.</summary>
    public static class Props
    {
        public static int LabelW => Theme.S(96);

        sealed class PropRow : Panel
        {
            public readonly Label Lbl; public Control Editor;
            public PropRow(string label)
            {
                Lbl = new Label { Text = label, ForeColor = Theme.TextDim, Font = Theme.UI, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Panel };
                Controls.Add(Lbl);
                BackColor = Theme.Panel; Height = Theme.S(26);
            }
            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e);
                if (Lbl == null) return;
                Lbl.SetBounds(0, 0, LabelW, Height);
                Editor?.SetBounds(LabelW, (Height - Editor.Height) / 2, Math.Max(10, Width - LabelW), Editor.Height);
            }
        }

        static PropRow Row(InspectorContext c, string label, Control editor, string tip)
        {
            var r = new PropRow(label) { Editor = editor };
            r.Controls.Add(editor);
            if (tip != null) { Theme.Tip(r.Lbl, tip); Theme.Tip(editor, tip); }
            c.Page.Add(r);
            return r;
        }

        static string Fmt(double v, string format) => v.ToString(format, CultureInfo.InvariantCulture);
        static bool Parse(string s, out double v) => double.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        /// <summary>A number field (commit on Enter or leaving the field). Returns the text box.</summary>
        public static TextBox Number(InspectorContext c, string label, Func<double> get, Action<double> set, string format = "0.000", string suffix = "", string tip = null, double min = double.NegativeInfinity, double max = double.PositiveInfinity)
        {
            var b = new TextBox { Text = Fmt(get(), format) + suffix }; Theme.StyleInput(b); b.Height = Theme.S(22);
            void Commit()
            {
                string t = b.Text; if (suffix.Length > 0) t = t.Replace(suffix.Trim(), "");
                if (Parse(t, out double v)) { v = Math.Max(min, Math.Min(max, v)); if (Math.Abs(v - get()) > 1e-9) set(v); }
                b.Text = Fmt(get(), format) + suffix;
            }
            b.KeyDown += (o, e) => { if (e.KeyCode == Keys.Enter) { Commit(); e.SuppressKeyPress = true; } else if (e.KeyCode == Keys.Escape) { b.Text = Fmt(get(), format) + suffix; e.SuppressKeyPress = true; } };
            b.Leave += (o, e) => Commit();
            c.OnRefresh(() => { if (!b.Focused) b.Text = Fmt(get(), format) + suffix; });
            Row(c, label, b, tip);
            return b;
        }

        public static TextBox Text(InspectorContext c, string label, Func<string> get, Action<string> set, string tip = null)
        {
            var b = new TextBox { Text = get() ?? "" }; Theme.StyleInput(b); b.Height = Theme.S(22);
            void Commit() { if (b.Text != (get() ?? "")) set(b.Text); }
            b.KeyDown += (o, e) => { if (e.KeyCode == Keys.Enter) { Commit(); e.SuppressKeyPress = true; } };
            b.Leave += (o, e) => Commit();
            c.OnRefresh(() => { if (!b.Focused) b.Text = get() ?? ""; });
            Row(c, label, b, tip);
            return b;
        }

        public static DarkCombo Choice(InspectorContext c, string label, string[] items, Func<int> get, Action<int> set, string tip = null)
        {
            var cb = new DarkCombo(); cb.Items.AddRange(items);
            bool sync = true;
            cb.SelectedIndex = Math.Max(-1, Math.Min(items.Length - 1, get()));
            sync = false;
            cb.SelectedIndexChanged += (o, e) => { if (!sync && cb.SelectedIndex >= 0 && cb.SelectedIndex != get()) set(cb.SelectedIndex); };
            c.OnRefresh(() => { sync = true; int i = get(); if (cb.SelectedIndex != i) cb.SelectedIndex = Math.Max(-1, Math.Min(items.Length - 1, i)); sync = false; });
            Row(c, label, cb, tip);
            return cb;
        }

        public static DarkCheck Check(InspectorContext c, string text, Func<bool> get, Action<bool> set, string tip = null)
        {
            var ch = new DarkCheck(text, get());
            bool sync = false;
            ch.CheckedChanged += (o, e) => { if (!sync && ch.Checked != get()) set(ch.Checked); };
            c.OnRefresh(() => { sync = true; ch.Checked = get(); sync = false; });
            if (tip != null) Theme.Tip(ch, tip);
            c.Page.Add(ch);
            return ch;
        }

        /// <summary>A slider; one undo step per drag (BeginEdit on press, EndEdit on release).</summary>
        public static DarkSlider Slider(InspectorContext c, string label, float min, float max, Func<float> get, Action<float> set, float step = 0.01f, string format = "0.00", string suffix = "", string tip = null)
        {
            var s = new DarkSlider(label, min, max, get(), step) { Format = format, Suffix = suffix };
            bool down = false;
            s.MouseDown += (o, e) => { down = true; c.Editor.BeginEdit(label); };
            s.MouseUp += (o, e) => { if (down) { down = false; c.Editor.EndEdit(); } };
            s.ValueChanged += (o, e) => { if (down) { set(s.Value); c.Editor.Changing(); } };
            c.OnRefresh(() => { if (!down) { s.Value = get(); s.Invalidate(); } });
            if (tip != null) Theme.Tip(s, tip);
            c.Page.Add(s);
            return s;
        }

        public static Panel Buttons(InspectorContext c, params (string Text, Action Click)[] buttons)
        {
            var bs = buttons.Select(b => { var x = new DarkButton(b.Text); var a = b.Click; x.Click += (o, e) => a(); return (Control)x; }).ToArray();
            return c.Page.Row(bs);
        }
    }
}
