using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VRMR.UI
{
    public static class Theme
    {
        // =====================================================================================================  icons

        /// <summary>Small vector icons (font glyphs like ⏮ ⏭ ⟲ render as boxes in Segoe UI).</summary>
        public enum Icon
        {
            None, Play, Pause, Prev, Next, RotLeft, RotRight, Cut, ChevronRight, ChevronDown, Plus, Stop, Undo, Redo
        }

        public static readonly Color Bg = Color.FromArgb(18, 19, 23);
        public static readonly Color Panel = Color.FromArgb(26, 27, 33);
        public static readonly Color Panel2 = Color.FromArgb(33, 35, 42);
        public static readonly Color Border = Color.FromArgb(48, 50, 60);
        public static readonly Color Text = Color.FromArgb(228, 230, 236);
        public static readonly Color TextDim = Color.FromArgb(148, 152, 166);
        public static readonly Color TextFaint = Color.FromArgb(100, 104, 118);
        public static readonly Color Accent = Color.FromArgb(110, 168, 255);
        public static readonly Color AccentDim = Color.FromArgb(52, 80, 130);
        public static readonly Color Rec = Color.FromArgb(240, 72, 72);
        public static readonly Color Ok = Color.FromArgb(86, 206, 132);
        public static readonly Color Warn = Color.FromArgb(240, 186, 80);
        public static readonly Color Left = Color.FromArgb(96, 160, 255);
        public static readonly Color Right = Color.FromArgb(255, 116, 116);
        public static readonly Color Center = Color.FromArgb(210, 214, 224);
        public static readonly Color Cut = Color.FromArgb(120, 52, 58);

        public static float Dpi = 1f;

        public static Font UI, UIBold, Small, Mono, Big, Title, Tiny;

        // =====================================================================================================  tooltips

        static DarkToolTip _tip;

        static readonly (string Glyph, Icon Icon)[] GlyphIcons =
        {
            ("⏮", Icon.Prev), ("⏭", Icon.Next), ("|◀", Icon.Prev), ("▶|", Icon.Next), ("▶", Icon.Play),
            ("►", Icon.Play), ("❚❚", Icon.Pause), ("⏸", Icon.Pause),
            ("↶", Icon.Undo), ("↷", Icon.Redo), ("⟲", Icon.RotLeft), ("↺", Icon.RotLeft), ("⟳", Icon.RotRight),
            ("↻", Icon.RotRight), ("✂", Icon.Cut), ("■", Icon.Stop), ("⏹", Icon.Stop)
        };

        /// <summary>The app's one tooltip (dark, wraps long text). Null or empty text removes it.</summary>
        public static ToolTip Tips => _tip ?? (_tip = new DarkToolTip());

        public static int S(int px)
        {
            return (int)Math.Round(px * Dpi);
        }

        public static float Sf(float px)
        {
            return px * Dpi;
        }

        public static void Init(Control c)
        {
            using (var g = c.CreateGraphics()) Dpi = g.DpiX / 96f;
            string family = "Segoe UI";
            UI = new Font(family, 9f);
            UIBold = new Font(family, 9f, FontStyle.Bold);
            Small = new Font(family, 8f);
            Mono = new Font("Consolas", 8.5f);
            Big = new Font(family, 14f, FontStyle.Bold);
            Title = new Font(family, 11f, FontStyle.Bold);
            Tiny = new Font(family, 7f, FontStyle.Bold);
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            d = Math.Min(d, Math.Min(r.Width, r.Height));
            if (d <= 0.5f)
            {
                if (r.Width > 0 && r.Height > 0) p.AddRectangle(r);
                return p;
            }

            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float rad)
        {
            using (var b = new SolidBrush(c))
            using (var p = Round(r, rad))
                g.FillPath(b, p);
        }

        public static void StrokeRound(Graphics g, Color c, RectangleF r, float rad, float w = 1f)
        {
            using (var pen = new Pen(c, w))
            using (var p = Round(r, rad))
                g.DrawPath(pen, p);
        }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        /// <summary>Dark input colours; text boxes also get a themed 1 px frame (accent while focused) and inner margins.</summary>
        public static void StyleInput(Control c)
        {
            c.BackColor = Panel2;
            c.ForeColor = Text;
            c.Font = UI;
            if (c is TextBox tb)
            {
                tb.BorderStyle = BorderStyle.FixedSingle;
                InputFrame.Attach(tb);
            }
        }

        public static string Time(double s, bool ms = true)
        {
            if (s < 0) s = 0;
            int m = (int)(s / 60);
            double sec = s - m * 60;
            return ms ? $"{m:00}:{sec:00.00}" : $"{m:00}:{(int)sec:00}";
        }

        public static T Tip<T>(T c, string text) where T : Control
        {
            Tips.SetToolTip(c, string.IsNullOrEmpty(text) ? null : text);
            return c;
        }

        /// <summary>A leading glyph that is drawn as a vector icon instead ("⏮", "▶  Play", "✂ Cut"); rest = the label.</summary>
        public static Icon IconOf(string text, out string rest)
        {
            rest = text ?? "";
            if (string.IsNullOrEmpty(text)) return Icon.None;
            string t = text.TrimStart();
            foreach (var gi in GlyphIcons)
                if (t.StartsWith(gi.Glyph, StringComparison.Ordinal))
                {
                    rest = t.Substring(gi.Glyph.Length).Trim();
                    return gi.Icon;
                }

            return Icon.None;
        }

        public static void DrawIcon(Graphics g, Icon icon, RectangleF r, Color c, float stroke = 0)
        {
            if (icon == Icon.None) return;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Math.Min(r.Width, r.Height), cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, h = s / 2;
            if (stroke <= 0) stroke = Math.Max(1.2f, s / 8f);
            using (var b = new SolidBrush(c))
            using (var p = new Pen(c, stroke)
                       { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                switch (icon)
                {
                    case Icon.Play:
                        g.FillPolygon(b,
                            new[]
                            {
                                new PointF(cx - h * 0.62f, cy - h * 0.8f), new PointF(cx + h * 0.8f, cy),
                                new PointF(cx - h * 0.62f, cy + h * 0.8f)
                            }); break;
                    case Icon.Pause:
                        g.FillRectangle(b, cx - h * 0.62f, cy - h * 0.75f, h * 0.42f, h * 1.5f);
                        g.FillRectangle(b, cx + h * 0.2f, cy - h * 0.75f, h * 0.42f, h * 1.5f);
                        break;
                    case Icon.Stop: g.FillRectangle(b, cx - h * 0.62f, cy - h * 0.62f, h * 1.24f, h * 1.24f); break;
                    case Icon.Prev:
                        g.FillRectangle(b, cx - h * 0.8f, cy - h * 0.7f, h * 0.24f, h * 1.4f);
                        g.FillPolygon(b,
                            new[]
                            {
                                new PointF(cx + h * 0.75f, cy - h * 0.7f), new PointF(cx - h * 0.45f, cy),
                                new PointF(cx + h * 0.75f, cy + h * 0.7f)
                            });
                        break;
                    case Icon.Next:
                        g.FillRectangle(b, cx + h * 0.56f, cy - h * 0.7f, h * 0.24f, h * 1.4f);
                        g.FillPolygon(b,
                            new[]
                            {
                                new PointF(cx - h * 0.75f, cy - h * 0.7f), new PointF(cx + h * 0.45f, cy),
                                new PointF(cx - h * 0.75f, cy + h * 0.7f)
                            });
                        break;
                    case Icon.RotLeft:
                    case Icon.RotRight:
                    {
                        // an arch over the top with the arrow head on the side the motion ends on, pointing down: ↻ (right,
                        // clockwise) ends on the right, ↺ (left, anticlockwise) on the left. Mirror images that read apart
                        // even at 16 px (the earlier full circles with the gap at the top looked the same).
                        bool left = icon == Icon.RotLeft;
                        float rr = h * 0.8f, ccy = cy + h * 0.14f;
                        var arc = new RectangleF(cx - rr, ccy - rr, rr * 2, rr * 2);
                        g.DrawArc(p, arc, left ? 15 : 165, left ? -195 : 195); // GDI angles run clockwise on screen
                        var end = new PointF(left ? cx - rr : cx + rr, ccy);
                        float d = h * 0.72f;
                        g.FillPolygon(b,
                            new[]
                            {
                                new PointF(end.X, end.Y + d * 0.8f), new PointF(end.X - d * 0.62f, end.Y - d * 0.1f),
                                new PointF(end.X + d * 0.62f, end.Y - d * 0.1f)
                            });
                        break;
                    }
                    case Icon.Undo:
                    case Icon.Redo:
                    {
                        // a U-turn arrow: the head on top pointing back (undo: left, redo: right), the tail curling under
                        float m = icon == Icon.Undo ? 1 : -1; // undo drawn as is, redo mirrored

                        float X(float v)
                        {
                            return cx + m * v;
                        }

                        float top = cy - h * 0.32f, bot = cy + h * 0.62f, rr = (bot - top) / 2, ax = h * 0.18f;
                        using (var path = new GraphicsPath())
                        {
                            path.AddLine(X(-h * 0.38f), top, X(ax), top);
                            path.AddArc(new RectangleF(X(ax) - rr, top, rr * 2, rr * 2), -90, m * 180);
                            path.AddLine(X(ax), bot, X(-h * 0.3f), bot);
                            g.DrawPath(p, path);
                        }

                        float tx = X(-h * 0.82f), d = h * 0.42f;
                        g.FillPolygon(b,
                            new[]
                            {
                                new PointF(tx, top), new PointF(X(-h * 0.82f + d * 1.05f), top - d * 0.85f),
                                new PointF(X(-h * 0.82f + d * 1.05f), top + d * 0.85f)
                            });
                        break;
                    }
                    case Icon.Cut:
                    {
                        float rr = h * 0.3f;
                        using (var p2 = new Pen(c, stroke * 0.9f))
                        {
                            g.DrawEllipse(p2, cx - h * 0.72f, cy + h * 0.25f, rr * 2, rr * 2);
                            g.DrawEllipse(p2, cx + h * 0.72f - rr * 2, cy + h * 0.25f, rr * 2, rr * 2);
                        }

                        g.DrawLine(p, cx - h * 0.3f, cy + h * 0.3f, cx + h * 0.5f, cy - h * 0.85f);
                        g.DrawLine(p, cx + h * 0.3f, cy + h * 0.3f, cx - h * 0.5f, cy - h * 0.85f);
                        break;
                    }
                    case Icon.ChevronRight:
                        g.DrawLines(p,
                            new[]
                            {
                                new PointF(cx - h * 0.25f, cy - h * 0.55f), new PointF(cx + h * 0.3f, cy),
                                new PointF(cx - h * 0.25f, cy + h * 0.55f)
                            }); break;
                    case Icon.ChevronDown:
                        g.DrawLines(p,
                            new[]
                            {
                                new PointF(cx - h * 0.55f, cy - h * 0.25f), new PointF(cx, cy + h * 0.3f),
                                new PointF(cx + h * 0.55f, cy - h * 0.25f)
                            }); break;
                    case Icon.Plus:
                        g.DrawLine(p, cx - h * 0.6f, cy, cx + h * 0.6f, cy);
                        g.DrawLine(p, cx, cy - h * 0.6f, cx, cy + h * 0.6f);
                        break;
                }
            }

            g.SmoothingMode = old;
        }

        sealed class DarkToolTip : ToolTip
        {
            const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Left |
                                          TextFormatFlags.Top;

            public DarkToolTip()
            {
                OwnerDraw = true;
                AutoPopDelay = 30000;
                InitialDelay = 450;
                ReshowDelay = 120;
                ShowAlways = true;
                Popup += (o, e) =>
                {
                    string t = GetToolTip(e.AssociatedControl);
                    if (string.IsNullOrEmpty(t))
                    {
                        e.Cancel = true;
                        return;
                    }

                    var sz = TextRenderer.MeasureText(t, UI ?? SystemFonts.DefaultFont, new Size(S(340), int.MaxValue),
                        Flags);
                    e.ToolTipSize = new Size(sz.Width + S(18), sz.Height + S(12));
                };
                Draw += (o, e) =>
                {
                    var g = e.Graphics;
                    using (var b = new SolidBrush(Panel2)) g.FillRectangle(b, e.Bounds);
                    using (var p = new Pen(Border)) g.DrawRectangle(p, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
                    var r = new Rectangle(S(9), S(6), e.Bounds.Width - S(16), e.Bounds.Height - S(10));
                    TextRenderer.DrawText(g, e.ToolTipText, UI ?? SystemFonts.DefaultFont, r, Text, Flags);
                };
            }
        }

        // =====================================================================================================  UI state

        /// <summary>Remembered UI state (which sections are open), in %APPDATA%\VRMotionRecorder\ui.json.</summary>
        public static class UiState
        {
            static Dictionary<string, object> _d;
            static string FilePath => Path.Combine(Settings.AppDataDir, "ui.json");

            static Dictionary<string, object> D
            {
                get
                {
                    if (_d != null) return _d;
                    try { _d = File.Exists(FilePath) ? Json.ParseObject(File.ReadAllText(FilePath)) : null; }
                    catch { _d = null; }

                    return _d ?? (_d = new Dictionary<string, object>());
                }
            }

            public static bool Get(string key, bool def)
            {
                return !string.IsNullOrEmpty(key) && D.TryGetValue(key, out object v) && v is bool b ? b : def;
            }

            public static void Set(string key, bool value)
            {
                if (string.IsNullOrEmpty(key)) return;
                D[key] = value;
                try
                {
                    Directory.CreateDirectory(Settings.AppDataDir);
                    File.WriteAllText(FilePath, Json.Serialize(D, true));
                }
                catch { }
            }
        }
    }

    /// <summary>Paints a text box's 1 px frame in the theme's colours (accent while focused) and gives it inner margins.</summary>
    sealed class InputFrame : NativeWindow
    {
        const int WM_NCPAINT = 0x85, WM_SETFOCUS = 0x07, WM_KILLFOCUS = 0x08, WM_ENABLE = 0x0A, EM_SETMARGINS = 0xD3;

        static readonly ConditionalWeakTable<TextBox, InputFrame> Frames = new();
        readonly TextBox _tb;

        InputFrame(TextBox tb) { _tb = tb; }

        [DllImport("user32.dll")]
        static extern IntPtr GetWindowDC(IntPtr h);

        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr h, IntPtr dc);

        [DllImport("user32.dll")]
        static extern bool RedrawWindow(IntPtr h, IntPtr r, IntPtr rgn, uint flags);

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        public static void Attach(TextBox tb)
        {
            if (Frames.TryGetValue(tb, out _)) return;
            var f = new InputFrame(tb);
            Frames.Add(tb, f);
            if (tb.IsHandleCreated) f.Hook();
            tb.HandleCreated += (o, e) => f.Hook();
            tb.HandleDestroyed += (o, e) => f.ReleaseHandle();
        }

        void Hook()
        {
            if (Handle != IntPtr.Zero) ReleaseHandle();
            AssignHandle(_tb.Handle);
            int m = Theme.S(4);
            SendMessage(_tb.Handle, EM_SETMARGINS, 3, (m << 16) | m);
            RedrawWindow(_tb.Handle, IntPtr.Zero, IntPtr.Zero, 0x0400 | 0x0001); // frame + invalidate
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_NCPAINT) Paint(IntPtr.Zero);
            else if (m.Msg == 0x0317 && m.WParam != IntPtr.Zero && ((long)m.LParam & 0x2) != 0)
                Paint(m.WParam); // WM_PRINT with PRF_NONCLIENT (DrawToBitmap)
            else if (m.Msg == WM_SETFOCUS || m.Msg == WM_KILLFOCUS || m.Msg == WM_ENABLE)
                RedrawWindow(m.HWnd, IntPtr.Zero, IntPtr.Zero, 0x0400 | 0x0001 | 0x0100);
        }

        void Paint(IntPtr hdc)
        {
            IntPtr dc = hdc != IntPtr.Zero ? hdc : GetWindowDC(Handle);
            if (dc == IntPtr.Zero) return;
            try
            {
                using (var g = Graphics.FromHdc(dc))
                using (var p = new Pen(_tb.Focused && !_tb.ReadOnly ? Theme.AccentDim : Theme.Border))
                    g.DrawRectangle(p, 0, 0, _tb.Width - 1, _tb.Height - 1);
            }
            finally
            {
                if (hdc == IntPtr.Zero) ReleaseDC(Handle, dc);
            }
        }
    }
}
