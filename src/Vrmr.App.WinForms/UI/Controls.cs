using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VRMR.UI
{
    public class DarkControl : Control
    {
        public DarkControl()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Theme.Panel;
            ForeColor = Theme.Text;
            Font = Theme.UI;
        }
    }

    public sealed class DarkButton : DarkControl
    {
        public enum Kind { Normal, Accent, Danger, Ghost }

        public string Glyph;

        /// <summary>
        ///     A vector icon drawn before the text. Without one, a leading glyph in the text ("⏮", "▶  Play", "✂ Cut")
        ///     is drawn as its icon (those glyphs render as boxes in Segoe UI).
        /// </summary>
        public Theme.Icon IconKind = Theme.Icon.None;

        public Kind Style = Kind.Normal;
        public bool Toggle, Checked;
        bool _hover, _down;

        public DarkButton(string text, Kind k = Kind.Normal)
        {
            Text = text;
            Style = k;
            Height = Theme.S(28);
            Cursor = Cursors.Hand;
        }

        public event EventHandler CheckedChanged;

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            _down = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _down = true;
                Invalidate();
            }

            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _down = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            Invalidate();
            base.OnTextChanged(e);
        }

        protected override void OnClick(EventArgs e)
        {
            if (!Enabled) return;
            if (Toggle)
            {
                Checked = !Checked;
                CheckedChanged?.Invoke(this, EventArgs.Empty);
                Invalidate();
            }

            base.OnClick(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Invalidate();
            base.OnEnabledChanged(e);
        }

        Theme.Icon Content(out string label)
        {
            label = Text ?? "";
            if (IconKind != Theme.Icon.None) return IconKind;
            if (!string.IsNullOrEmpty(Glyph))
            {
                var gi = Theme.IconOf(Glyph, out _);
                if (gi != Theme.Icon.None) return gi;
                label = string.IsNullOrEmpty(label) ? Glyph : Glyph + "  " + label;
                return Theme.Icon.None;
            }

            return Theme.IconOf(label, out label);
        }

        /// <summary>The width the icon and text need (with padding).</summary>
        public int PreferredW()
        {
            var ic = Content(out string label);
            int w = ic != Theme.Icon.None ? Theme.S(12) : 0;
            if (label.Length > 0) w += (w > 0 ? Theme.S(6) : 0) + TextRenderer.MeasureText(label, Font).Width;
            return w + Theme.S(18);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Panel);
            Color bg, fg = Theme.Text;
            switch (Style)
            {
                case Kind.Accent: bg = Theme.AccentDim; break;
                case Kind.Danger: bg = Color.FromArgb(120, 40, 44); break;
                case Kind.Ghost: bg = Color.FromArgb(0, 0, 0, 0); break;
                default: bg = Theme.Panel2; break;
            }

            if (Toggle && Checked) bg = Theme.AccentDim;
            if (_hover) bg = Theme.Mix(bg.A == 0 ? Theme.Panel2 : bg, Color.White, 0.08f);
            if (_down) bg = Theme.Mix(bg, Color.Black, 0.15f);
            if (!Enabled)
            {
                bg = Theme.Mix(bg.A == 0 ? Theme.Panel : bg, Theme.Panel, 0.5f);
                fg = Theme.TextFaint;
            }

            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            if (bg.A > 0) Theme.FillRound(g, bg, r, Theme.Sf(5));
            if (Style != Kind.Ghost)
                Theme.StrokeRound(g, Toggle && Checked ? Theme.Accent : Theme.Border, r, Theme.Sf(5));
            var icon = Content(out string label);
            const TextFormatFlags tf = TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter |
                                       TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
            if (icon == Theme.Icon.None)
            {
                TextRenderer.DrawText(g, label, Font, ClientRectangle, fg, tf | TextFormatFlags.HorizontalCenter);
                return;
            }

            int isz = Theme.S(label.Length > 0 ? 10 : 11), gap = label.Length > 0 ? Theme.S(6) : 0;
            int tw = label.Length > 0
                ? Math.Min(TextRenderer.MeasureText(label, Font).Width, Width - isz - gap - Theme.S(8))
                : 0;
            int x = (Width - (isz + gap + tw)) / 2;
            Theme.DrawIcon(g, icon, new RectangleF(x, (Height - isz) / 2f, isz, isz), fg);
            if (tw > 0)
                TextRenderer.DrawText(g, label, Font,
                    new Rectangle(x + isz + gap - Theme.S(2), 0, tw + Theme.S(4), Height), fg,
                    tf | TextFormatFlags.Left);
        }
    }

    /// <summary>Big round record button with timer text.</summary>
    public sealed class RecordButton : DarkControl
    {
        readonly Timer _t = new() { Interval = 50 };
        public bool Recording;
        public string Sub = "";
        bool _hover;
        float _pulse;

        public RecordButton()
        {
            Cursor = Cursors.Hand;
            _t.Tick += (s, e) =>
            {
                if (Recording)
                {
                    _pulse += 0.12f;
                    Invalidate();
                }
            };
            _t.Start();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Panel);
            var r = new RectangleF(1, 1, Width - 3, Height - 3);
            var bg = Recording ? Color.FromArgb(70, 24, 28) : Theme.Panel2;
            if (_hover) bg = Theme.Mix(bg, Color.White, 0.07f);
            Theme.FillRound(g, bg, r, r.Height / 2);
            Theme.StrokeRound(g, Recording ? Theme.Rec : Theme.Border, r, r.Height / 2, Recording ? 1.5f : 1f);
            float d = r.Height * 0.46f;
            float cx = r.X + r.Height / 2, cy = r.Y + r.Height / 2;
            float a = Recording ? (float)(0.6 + 0.4 * Math.Sin(_pulse)) : 1f;
            var dot = Recording ? Theme.Mix(Color.FromArgb(120, 30, 30), Theme.Rec, a) : Theme.Rec;
            if (Recording)
                using (var b = new SolidBrush(dot))
                    g.FillRectangle(b, cx - d * 0.38f, cy - d * 0.38f, d * 0.76f, d * 0.76f);
            else
                using (var b = new SolidBrush(dot))
                    g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
            var tr = new Rectangle((int)(r.X + r.Height), 0, (int)(r.Width - r.Height - Theme.S(8)), Height);
            int mid = Height / 2;
            TextRenderer.DrawText(g, Recording ? "STOP" : "RECORD", Theme.UIBold,
                new Rectangle(tr.X, 0, tr.Width, mid + Theme.S(1)), Theme.Text,
                TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Bottom);
            TextRenderer.DrawText(g, Sub, Theme.Small, new Rectangle(tr.X, mid, tr.Width, Height - mid),
                Recording ? Theme.Rec : Theme.TextFaint,
                TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
        }
    }

    public sealed class DarkCheck : DarkControl
    {
        /// <summary>Extra left indent (unscaled px), for options that belong to the check above.</summary>
        public int Indent;

        bool _checked;

        public DarkCheck(string text, bool on = false)
        {
            Text = text;
            _checked = on;
            Height = Theme.S(22);
            Cursor = Cursors.Hand;
        }

        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler CheckedChanged;

        protected override void OnClick(EventArgs e)
        {
            if (Enabled) Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            Invalidate();
            base.OnTextChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Panel);
            float s = Theme.Sf(14), x0 = 1 + Theme.S(Indent);
            var box = new RectangleF(x0, (Height - s) / 2, s, s);
            Theme.FillRound(g,
                _checked ? Enabled ? Theme.AccentDim : Theme.Mix(Theme.AccentDim, Theme.Panel, 0.5f) : Theme.Panel2,
                box, 3);
            Theme.StrokeRound(g, _checked && Enabled ? Theme.Accent : Theme.Border, box, 3);
            if (_checked)
                using (var p = new Pen(Enabled ? Theme.Text : Theme.TextFaint, Theme.Sf(1.8f)))
                    g.DrawLines(p,
                        new[]
                        {
                            new PointF(box.X + s * 0.22f, box.Y + s * 0.52f),
                            new PointF(box.X + s * 0.42f, box.Y + s * 0.72f),
                            new PointF(box.X + s * 0.78f, box.Y + s * 0.3f)
                        });
            int tx = (int)(x0 + s + Theme.S(7));
            TextRenderer.DrawText(g, Text, Font, new Rectangle(tx, 0, Width - tx, Height),
                Enabled ? Theme.Text : Theme.TextFaint,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.Left |
                TextFormatFlags.EndEllipsis);
        }
    }

    public sealed class DarkSlider : DarkControl
    {
        public string Format = "0.00";
        public float Min, Max, Value, Step;
        public string Suffix = "";
        bool _drag;

        public DarkSlider(string label, float min, float max, float val, float step = 0)
        {
            Text = label;
            Min = min;
            Max = max;
            Value = val;
            Step = step;
            Height = Theme.S(38);
            Cursor = Cursors.Hand;
        }

        Rectangle Track => new(Theme.S(6), Height - Theme.S(12), Width - Theme.S(12), Theme.S(4));
        public event EventHandler ValueChanged;

        void SetFromX(int x)
        {
            var tr = Track;
            float a = MathX.Clamp01((x - tr.X) / (float)Math.Max(1, tr.Width));
            float v = Min + (Max - Min) * a;
            if (Step > 0) v = (float)Math.Round(v / Step) * Step;
            if (v != Value)
            {
                Value = v;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _drag = true;
                SetFromX(e.X);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_drag) SetFromX(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _drag = false;
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Panel);
            var head = new Rectangle(0, 0, Width, Height - Theme.S(16));
            TextRenderer.DrawText(g, Text, Font, head, Enabled ? Theme.TextDim : Theme.TextFaint,
                TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Value.ToString(Format) + Suffix, Theme.Mono, head,
                Enabled ? Theme.Text : Theme.TextFaint,
                TextFormatFlags.NoPrefix | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            var tr = Track;
            Theme.FillRound(g, Theme.Panel2, tr, 2);
            float a = MathX.Clamp01((Value - Min) / Math.Max(1e-6f, Max - Min));
            // bipolar ranges (-x..+x) fill from the centre
            float from = Min < 0 && Max > 0 ? -Min / (Max - Min) : 0;
            float fa = Math.Min(a, from), fb = Math.Max(a, from);
            if (tr.Width * (fb - fa) >= 1)
                Theme.FillRound(g, Theme.AccentDim,
                    new RectangleF(tr.X + tr.Width * fa, tr.Y, tr.Width * (fb - fa), tr.Height), 2);
            float kx = tr.X + tr.Width * a;
            using (var b = new SolidBrush(Enabled ? Theme.Accent : Theme.TextFaint))
                g.FillEllipse(b, kx - Theme.S(6), tr.Y + tr.Height / 2f - Theme.S(6), Theme.S(12), Theme.S(12));
        }
    }

    public sealed class SectionLabel : DarkControl
    {
        public SectionLabel(string t)
        {
            Text = t;
            Height = Theme.S(26);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            Invalidate();
            base.OnTextChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Panel);
            TextRenderer.DrawText(g, Text.ToUpperInvariant(), Theme.Small, new Rectangle(0, 0, Width, Height - 2),
                Theme.TextFaint,
                TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis);
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }
    }

    public sealed class InfoLabel : DarkControl
    {
        const TextFormatFlags WrapFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Left |
                                          TextFormatFlags.Top;

        public Color Color = Theme.TextDim;
        public bool Wrap = true;

        public InfoLabel(string t = "")
        {
            Text = t;
            Height = Theme.S(20);
            Font = Theme.UI;
        }

        protected override void OnTextChanged(EventArgs e)
        {
            Invalidate();
            base.OnTextChanged(e);
            // a wrapped label whose height changes: lay its page out again
            if (Wrap && Width > 10 && (string.IsNullOrEmpty(Text) ? 0 : MeasureH()) != Height) Layouts.Relayout(this);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Panel);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, Color,
                Wrap
                    ? WrapFlags
                    : TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                      TextFormatFlags.Left | TextFormatFlags.Top);
        }

        int MeasureH()
        {
            if (string.IsNullOrEmpty(Text)) return Theme.S(18);
            var sz = TextRenderer.MeasureText(Text, Font, new Size(Math.Max(10, Width), 10000), WrapFlags);
            return Math.Max(Theme.S(18), sz.Height + 2);
        }

        public void AutoHeight()
        {
            Height = MeasureH();
        }
    }

    /// <summary>Layout helpers shared by the stacking panels.</summary>
    public static class Layouts
    {
        static readonly MethodInfo GetState = typeof(Control).GetMethod("GetState",
            BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(int) }, null);

        static readonly HashSet<Control> _pending = new();

        /// <summary>The control's own Visible flag (Control.Visible is false for every child of a hidden page).</summary>
        public static bool OwnVisible(Control c)
        {
            if (c.Visible) return true;
            try { return GetState != null && (bool)GetState.Invoke(c, new object[] { 0x2 }); }
            catch { return false; }
        }

        /// <summary>Not stacked: hidden, or an empty wrapped label (takes no room until it has text).</summary>
        public static bool Skip(Control c)
        {
            if (!OwnVisible(c)) return true;
            if (c is InfoLabel il && il.Wrap && string.IsNullOrEmpty(il.Text))
            {
                if (il.Height != 0) il.Height = 0;
                return true;
            }

            return false;
        }

        /// <summary>Puts a stacked child at (x, y) with the given width, sizing self-sizing children (sections, wrapped labels).</summary>
        public static void Place(Control c, int x, int y, int w)
        {
            if (c is FoldSection fs) fs.Arrange(w);
            c.SetBounds(x, y, w, c.Height);
            if (c is InfoLabel il && il.Wrap) il.AutoHeight();
        }

        /// <summary>Lays out the scrolling page a control is on (after it changed size).</summary>
        public static void Relayout(Control c)
        {
            Control p = c.Parent, page = null;
            while (p != null)
            {
                if (p is StackPanel) page = p;
                p = p.Parent;
            }

            if (page != null && !_pending.Contains(page))
            {
                _pending.Add(page);
                try
                {
                    if (page.IsHandleCreated)
                        page.BeginInvoke((Action)(() =>
                        {
                            _pending.Remove(page);
                            page.PerformLayout();
                        }));
                    else
                    {
                        _pending.Remove(page);
                        page.PerformLayout();
                    }
                }
                catch { _pending.Remove(page); }
            }
        }
    }

    /// <summary>Vertical stacking panel with padding; children take full width.</summary>
    public class StackPanel : Panel
    {
        public int Pad = 10, Gap = 6;

        public StackPanel()
        {
            BackColor = Theme.Panel;
            AutoScroll = true;
            DoubleBuffered = true;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int y = Theme.S(Pad) + AutoScrollPosition.Y;
            int w = ClientSize.Width - Theme.S(Pad) * 2;
            foreach (Control c in Controls)
            {
                if (Layouts.Skip(c)) continue;
                Layouts.Place(c, Theme.S(Pad), y, w);
                y += c.Height + Theme.S(Gap);
            }

            if (AutoScroll) AdjustFormScrollbars(true);
        }

        public T Add<T>(T c) where T : Control
        {
            Controls.Add(c);
            return c;
        }

        public Panel Row(params Control[] cs)
        {
            var p = new RowPanel();
            foreach (var c in cs) p.Controls.Add(c);
            p.Height = Theme.S(28);
            Controls.Add(p);
            return p;
        }

        /// <summary>A label on the left, the editor on the right.</summary>
        public FieldRow Field(string label, Control editor, string tip = null)
        {
            return Add(FieldRow.Make(label, editor, tip));
        }

        /// <summary>A collapsible section (title bar with a chevron); add its content with its own Add / Row / Field.</summary>
        public FoldSection Fold(string title, bool expanded = true, string key = null)
        {
            return Add(new FoldSection(title, expanded, FoldSection.Kind.Section, key));
        }
    }

    public sealed class RowPanel : Panel
    {
        public int Gap = 6;
        public RowPanel() { BackColor = Theme.Panel; }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int n = Controls.Count;
            if (n == 0) return;
            int g = Theme.S(Gap);
            int w = (ClientSize.Width - g * (n - 1)) / n;
            int x = 0;
            foreach (Control c in Controls)
            {
                c.SetBounds(x, 0, w, ClientSize.Height);
                x += w + g;
            }
        }

        protected override void OnParentBackColorChanged(EventArgs e)
        {
            BackColor = Parent.BackColor;
            base.OnParentBackColorChanged(e);
        }
    }

    /// <summary>Children in a grid of equal cells (Columns per row); the panel is as tall as its rows.</summary>
    public sealed class GridPanel : Panel
    {
        public int Columns = 1, RowH = 26, Gap = 4;
        public GridPanel() { BackColor = Theme.Panel; }
        public int Rows => (Controls.Count + Math.Max(1, Columns) - 1) / Math.Max(1, Columns);

        public void FitHeight()
        {
            int h = Rows == 0 ? 0 : Rows * Theme.S(RowH) + (Rows - 1) * Theme.S(Gap);
            if (Height != h) Height = h;
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            FitHeight();
        }

        protected override void OnControlRemoved(ControlEventArgs e)
        {
            base.OnControlRemoved(e);
            FitHeight();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int cols = Math.Max(1, Columns), g = Theme.S(Gap), w = (ClientSize.Width - g * (cols - 1)) / cols, i = 0;
            foreach (Control c in Controls)
            {
                c.SetBounds(i % cols * (w + g), i / cols * (Theme.S(RowH) + g), w, Theme.S(RowH));
                i++;
            }

            FitHeight();
        }

        protected override void OnParentBackColorChanged(EventArgs e)
        {
            BackColor = Parent.BackColor;
            base.OnParentBackColorChanged(e);
        }
    }

    /// <summary>One line: a dim label on the left, an editor (list, text box) on the right.</summary>
    public sealed class FieldRow : Panel
    {
        public readonly Control Editor;
        public Color LabelColor = Theme.TextDim;

        /// <summary>Label column width (unscaled px); 0 = about a third of the row.</summary>
        public int LabelWidth;

        string _label;

        public FieldRow(string label, Control editor)
        {
            _label = label;
            Editor = editor;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint, true);
            BackColor = Theme.Panel;
            Controls.Add(editor);
            Height = Math.Max(Theme.S(26), editor.Height + Theme.S(2));
            editor.SizeChanged += (o, e) =>
            {
                int h = Math.Max(Theme.S(26), Editor.Height + Theme.S(2));
                if (Height != h)
                {
                    Height = h;
                    Layouts.Relayout(this);
                }
                else PerformLayout();
            };
        }

        public string Label
        {
            get => _label;
            set
            {
                if (_label == value) return;
                _label = value;
                Invalidate();
            }
        }

        int LabelW => LabelWidth > 0
            ? Theme.S(LabelWidth)
            : Math.Max(Theme.S(90), Math.Min(Theme.S(112), (int)(Width * 0.3)));

        public static FieldRow Make(string label, Control editor, string tip)
        {
            var f = new FieldRow(label, editor);
            if (tip != null)
            {
                Theme.Tip(f, tip);
                Theme.Tip(editor, tip);
            }

            return f;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int lw = LabelW, eh = Editor.Height;
            Editor.SetBounds(lw, Math.Max(0, (Height - eh) / 2), Math.Max(10, Width - lw), eh);
        }

        protected override void OnParentBackColorChanged(EventArgs e)
        {
            BackColor = Parent.BackColor;
            base.OnParentBackColorChanged(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Editor.Focus();
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            TextRenderer.DrawText(g, _label, Theme.UI, new Rectangle(0, 0, LabelW - Theme.S(8), Height),
                Enabled ? LabelColor : Theme.TextFaint,
                TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>
    ///     A collapsible group. Section: a title bar (chevron, title, a short summary on the right) over its content.
    ///     Sub: a quieter "› Advanced" / "› Details" line whose content is indented with a guide line.
    ///     The open state is remembered under <c>key</c>.
    /// </summary>
    public sealed class FoldSection : Panel
    {
        public enum Kind { Section, Sub }

        public readonly Kind Style;
        readonly Panel _body;
        readonly string _key;
        public int Gap = 6;
        public Color SummaryColor = Theme.TextFaint;
        bool _expanded, _hover;
        string _title, _summary = "";

        public FoldSection(string title, bool expanded = true, Kind style = Kind.Section, string key = null)
        {
            _title = title;
            Style = style;
            _key = key;
            _expanded = key != null ? Theme.UiState.Get("fold." + key, expanded) : expanded;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint, true);
            BackColor = Theme.Panel;
            _body = new Panel { BackColor = Theme.Panel, Visible = _expanded };
            Controls.Add(_body);
            Height = HeaderH;
        }

        public int HeaderH => Style == Kind.Section ? Theme.S(32) : Theme.S(24);
        int Indent => Style == Kind.Sub ? Theme.S(12) : 0;
        public ControlCollection Items => _body.Controls;

        public string Title
        {
            get => _title;
            set
            {
                if (_title == value) return;
                _title = value;
                Invalidate(new Rectangle(0, 0, Width, HeaderH));
            }
        }

        /// <summary>Short text on the right of the title bar (shown open or closed).</summary>
        public string Summary
        {
            get => _summary;
            set
            {
                value = value ?? "";
                if (_summary == value) return;
                _summary = value;
                Invalidate(new Rectangle(0, 0, Width, HeaderH));
            }
        }

        public bool Expanded
        {
            get => _expanded;
            set
            {
                if (_expanded == value) return;
                _expanded = value;
                if (_key != null) Theme.UiState.Set("fold." + _key, value);
                _body.Visible = value;
                Invalidate();
                Layouts.Relayout(this);
                if (!(FindPage() is StackPanel)) { Arrange(Width); }

                ExpandedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler ExpandedChanged;

        Control FindPage()
        {
            var p = Parent;
            while (p != null && !(p is StackPanel)) p = p.Parent;
            return p;
        }

        public T Add<T>(T c) where T : Control
        {
            _body.Controls.Add(c);
            return c;
        }

        public Panel Row(params Control[] cs)
        {
            var p = new RowPanel();
            foreach (var c in cs) p.Controls.Add(c);
            p.Height = Theme.S(28);
            _body.Controls.Add(p);
            return p;
        }

        public FieldRow Field(string label, Control editor, string tip = null)
        {
            return Add(FieldRow.Make(label, editor, tip));
        }

        /// <summary>A nested quiet group ("Advanced", "Details"), closed by default.</summary>
        public FoldSection Fold(string title, bool expanded = false, string key = null)
        {
            return Add(new FoldSection(title, expanded, Kind.Sub, key));
        }

        /// <summary>Lays out the content for this width and sets the height (called by the page's layout).</summary>
        public int Arrange(int width)
        {
            if (!_expanded)
            {
                _body.Visible = false;
                Height = HeaderH;
                return Height;
            }

            _body.Visible = true;
            int x = Indent, w = Math.Max(10, width - Indent), y = 0;
            foreach (Control c in _body.Controls)
            {
                if (Layouts.Skip(c)) continue;
                Layouts.Place(c, 0, y, w);
                y += c.Height + Theme.S(Gap);
            }

            if (y > 0) y -= Theme.S(Gap);
            int top = HeaderH + (Style == Kind.Section ? Theme.S(2) : Theme.S(2));
            _body.SetBounds(x, top, w, Math.Max(0, y));
            Height = top + y + (Style == Kind.Section ? Theme.S(10) : Theme.S(6));
            return Height;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = e.Y < HeaderH;
            if (h != _hover)
            {
                _hover = h;
                Cursor = h ? Cursors.Hand : Cursors.Default;
                Invalidate(new Rectangle(0, 0, Width, HeaderH));
            }

            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hover)
            {
                _hover = false;
                Cursor = Cursors.Default;
                Invalidate(new Rectangle(0, 0, Width, HeaderH));
            }

            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && e.Y < HeaderH) Expanded = !Expanded;
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            int hh = HeaderH;
            bool sec = Style == Kind.Section;
            int isz = Theme.S(sec ? 10 : 9);
            var ic = new RectangleF(0, (hh - isz) / 2f + (sec ? Theme.S(1) : 0), isz, isz);
            var tc = sec ? Theme.Text : _hover ? Theme.Text : Theme.TextDim;
            if (_hover && sec) tc = Color.White;
            Theme.DrawIcon(g, _expanded ? Theme.Icon.ChevronDown : Theme.Icon.ChevronRight, ic,
                _hover ? Theme.TextDim : Theme.TextFaint, Theme.Sf(1.4f));
            int tx = isz + Theme.S(7);
            var font = sec ? Theme.UIBold : Theme.UI;
            int titleW = TextRenderer.MeasureText(_title, font).Width;
            int sumW = 0;
            if (_summary.Length > 0)
            {
                int avail = Width - tx - titleW - Theme.S(14);
                if (avail > Theme.S(30))
                {
                    var r = new Rectangle(tx + titleW + Theme.S(10), 0, avail, hh);
                    TextRenderer.DrawText(g, _summary, Theme.Small, r, SummaryColor,
                        TextFormatFlags.NoPrefix | TextFormatFlags.Right | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                    sumW = avail;
                }
            }

            TextRenderer.DrawText(g, _title, font, new Rectangle(tx, 0, Width - tx - sumW, hh), tc,
                TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis);
            if (sec)
                using (var p = new Pen(Theme.Border))
                    g.DrawLine(p, 0, hh - 1, Width, hh - 1);
            else if (_expanded && Height > hh + Theme.S(4))
                using (var p = new Pen(Theme.Border))
                    g.DrawLine(p, isz / 2, hh, isz / 2, Height - Theme.S(4));
        }
    }

    /// <summary>Tab strip with content pages. Pages can be hidden from the strip (SetTabVisible).</summary>
    public sealed class DarkTabs : DarkControl
    {
        readonly List<string> _names = new();
        readonly List<Control> _pages = new();
        readonly List<bool> _shown = new();
        int _hot = -1;
        public DarkTabs() { BackColor = Theme.Panel; }
        public int Selected { get; private set; }
        int HeaderH => Theme.S(34);
        public event EventHandler SelectedChanged;

        public T AddPage<T>(string name, T page) where T : Control
        {
            _names.Add(name);
            _pages.Add(page);
            _shown.Add(true);
            page.Visible = _pages.Count == 1;
            Controls.Add(page);
            PerformLayout();
            return page;
        }

        public void Select(int i)
        {
            if (i < 0 || i >= _pages.Count) return;
            if (!_shown[i]) { _shown[i] = true; } // selecting a hidden page shows its tab

            Selected = i;
            for (int k = 0; k < _pages.Count; k++) _pages[k].Visible = k == i;
            _pages[i].PerformLayout();
            Invalidate();
            SelectedChanged?.Invoke(this, EventArgs.Empty);
        }

        public bool TabVisible(int i)
        {
            return i >= 0 && i < _shown.Count && _shown[i];
        }

        /// <summary>Shows or hides a tab; hiding the open one opens the first visible tab.</summary>
        public void SetTabVisible(int i, bool on)
        {
            if (i < 0 || i >= _shown.Count || _shown[i] == on) return;
            _shown[i] = on;
            if (!on && Selected == i)
            {
                int f = _shown.IndexOf(true);
                if (f >= 0) Select(f);
            }

            Invalidate();
        }

        List<int> VisibleTabs()
        {
            var l = new List<int>();
            for (int i = 0; i < _names.Count; i++)
                if (_shown[i])
                    l.Add(i);
            return l;
        }

        int TabAt(int x)
        {
            var v = VisibleTabs();
            if (v.Count == 0) return -1;
            int w = Width / v.Count;
            return v[Math.Max(0, Math.Min(v.Count - 1, x / Math.Max(1, w)))];
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            foreach (var p in _pages) p.SetBounds(0, HeaderH, Width, Math.Max(0, Height - HeaderH));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Y > HeaderH || _names.Count == 0) return;
            int i = TabAt(e.X);
            if (i >= 0) Select(i);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = e.Y <= HeaderH ? TabAt(e.X) : -1;
            if (h != _hot)
            {
                _hot = h;
                Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate(new Rectangle(0, 0, Width, HeaderH));
            }

            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hot >= 0)
            {
                _hot = -1;
                Invalidate(new Rectangle(0, 0, Width, HeaderH));
            }

            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Panel);
            var v = VisibleTabs();
            if (v.Count == 0) return;
            int w = Width / v.Count;
            for (int k = 0; k < v.Count; k++)
            {
                int i = v[k];
                var r = new Rectangle(k * w, 0, w, HeaderH);
                bool sel = i == Selected;
                TextRenderer.DrawText(g, _names[i], sel ? Theme.UIBold : Theme.UI, r,
                    sel ? Theme.Text : i == _hot ? Theme.Text : Theme.TextDim,
                    TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                if (sel)
                    using (var b = new SolidBrush(Theme.Accent))
                        g.FillRectangle(b, r.X + Theme.S(12), HeaderH - Theme.S(2), r.Width - Theme.S(24), Theme.S(2));
            }

            using (var p = new Pen(Theme.Border)) g.DrawLine(p, 0, HeaderH - 1, Width, HeaderH - 1);
        }
    }

    /// <summary>A plain list in the theme's colours (no system-blue selection).</summary>
    public sealed class DarkListBox : ListBox
    {
        public DarkListBox()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            BorderStyle = BorderStyle.None;
            IntegralHeight = false;
            BackColor = Theme.Panel2;
            ForeColor = Theme.Text;
            Font = Theme.UI;
            ItemHeight = Theme.S(22);
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count)
            {
                using (var b = new SolidBrush(BackColor)) e.Graphics.FillRectangle(b, e.Bounds);
                return;
            }

            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Theme.AccentDim : BackColor)) e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font,
                new Rectangle(e.Bounds.X + Theme.S(6), e.Bounds.Y, e.Bounds.Width - Theme.S(8), e.Bounds.Height),
                Theme.Text,
                TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
    }

    public sealed class DarkCombo : ComboBox
    {
        const int WM_MOUSEWHEEL = 0x020A, WM_PAINT = 0x000F, WM_PRINT = 0x0317, WM_PRINTCLIENT = 0x0318;
        bool _hover;

        public DarkCombo()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            DropDownStyle = ComboBoxStyle.DropDownList;
            FlatStyle = FlatStyle.Flat;
            BackColor = Theme.Panel2;
            ForeColor = Theme.Text;
            Font = Theme.UI;
            ItemHeight = Theme.S(18);
        }

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        /// <summary>
        ///     The mouse wheel never changes the selection (it would change a setting while scrolling the page):
        ///     with the list closed the wheel goes to the page under it.
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEWHEEL && !DroppedDown)
            {
                if (Parent != null && Parent.IsHandleCreated) SendMessage(Parent.Handle, m.Msg, m.WParam, m.LParam);
                return;
            }

            base.WndProc(ref m);
            if (m.Msg == WM_PAINT) PaintFrame(IntPtr.Zero);
            else if ((m.Msg == WM_PRINT || m.Msg == WM_PRINTCLIENT) && m.WParam != IntPtr.Zero)
                PaintFrame(m.WParam); // DrawToBitmap
        }

        /// <summary>Theme frame and arrow over the system's flat frame (which is drawn in light colours).</summary>
        void PaintFrame(IntPtr hdc)
        {
            try
            {
                using (var g = hdc != IntPtr.Zero ? Graphics.FromHdc(hdc) : Graphics.FromHwnd(Handle))
                {
                    var r = ClientRectangle;
                    if (r.Width < 4 || r.Height < 4) return;
                    int aw = Theme.S(20);
                    using (var p = new Pen(Theme.Panel2, 2)) g.DrawRectangle(p, 1, 1, r.Width - 2, r.Height - 2);
                    using (var b = new SolidBrush(Theme.Panel2))
                        g.FillRectangle(b, r.Right - aw - 1, 1, aw, r.Height - 2);
                    var ic = new RectangleF(r.Right - aw + Theme.S(5), (r.Height - Theme.S(9)) / 2f, Theme.S(9),
                        Theme.S(9));
                    Theme.DrawIcon(g, Theme.Icon.ChevronDown, ic,
                        Enabled ? _hover ? Theme.Text : Theme.TextDim : Theme.TextFaint, Theme.Sf(1.4f));
                    var bc = Focused || DroppedDown ? Theme.AccentDim :
                        _hover && Enabled ? Theme.Mix(Theme.Border, Color.White, 0.12f) : Theme.Border;
                    using (var p = new Pen(bc)) g.DrawRectangle(p, 0, 0, r.Width - 1, r.Height - 1);
                }
            }
            catch { }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            Invalidate();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            Invalidate();
            base.OnLostFocus(e);
        }

        /// <summary>The open list is as wide as its longest item (the field itself may be narrow).</summary>
        protected override void OnDropDown(EventArgs e)
        {
            int w = Width;
            foreach (object it in Items)
                w = Math.Max(w, TextRenderer.MeasureText(GetItemText(it), Font).Width + Theme.S(24));
            DropDownWidth = Math.Min(w, Theme.S(560));
            base.OnDropDown(e);
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
            bool sel = !edit && (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Theme.AccentDim : Theme.Panel2)) e.Graphics.FillRectangle(b, e.Bounds);
            var r = new Rectangle(e.Bounds.X + Theme.S(edit ? 2 : 4), e.Bounds.Y,
                e.Bounds.Width - Theme.S(edit ? 5 : 6), e.Bounds.Height);
            if (e.Index >= 0)
                TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, r,
                    Enabled ? Theme.Text : Theme.TextFaint,
                    TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis);
        }
    }
}
