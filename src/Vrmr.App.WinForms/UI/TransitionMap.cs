using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Windows.Forms;

namespace VRMR.UI
{
    /// <summary>Colours for match scores (the same bands as the NLA badge: green from 80, amber from 50).</summary>
    public static class TransitionColors
    {
        public static Color Of(double s)
        {
            if (double.IsNaN(s)) return Theme.Panel;
            s = Math.Max(0, Math.Min(1, s));
            var low = Theme.Mix(Theme.Panel2, Theme.Rec, 0.55f);
            if (s < 0.5) return Theme.Mix(low, Theme.Warn, (float)(s / 0.5));
            if (s < 0.8) return Theme.Mix(Theme.Warn, Theme.Ok, (float)((s - 0.5) / 0.3));
            return Theme.Mix(Theme.Ok, Color.FromArgb(210, 255, 225), (float)((s - 0.8) / 0.2));
        }

        public static string Verdict(double s)
        {
            return s >= 0.8 ? "Seamless" : s >= 0.5 ? "Smooth blend" : s >= 0.25 ? "Visible blend" : "Hard change";
        }
    }

    /// <summary>The match score as a number, a bar and a word.</summary>
    public sealed class TransitionScoreView : DarkControl
    {
        public string Pending = "Scoring…";
        public double Score = -1;
        public TransitionScoreView() { Height = Theme.S(46); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Panel);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.FillRound(g, Theme.Panel2, r, Theme.S(5));
            if (Score < 0)
            {
                TextRenderer.DrawText(g, Pending, Theme.UI, new Rectangle(Theme.S(10), 0, Width - Theme.S(20), Height),
                    Theme.TextDim, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
                return;
            }

            var col = TransitionColors.Of(Score);
            string num = (Score * 100).ToString("0", CultureInfo.InvariantCulture);
            var big = Theme.Big ?? Theme.UIBold;
            var sz = TextRenderer.MeasureText(num, big);
            int x = Theme.S(10);
            TextRenderer.DrawText(g, num, big, new Rectangle(x, 0, sz.Width + 2, Height), col,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix |
                TextFormatFlags.NoPadding);
            x += sz.Width + Theme.S(8);
            TextRenderer.DrawText(g, "match  ·  " + TransitionColors.Verdict(Score), Theme.UI,
                new Rectangle(x, Theme.S(6), Width - x - Theme.S(8), Theme.S(18)), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            // bar 0..100 with the 50 / 80 bands marked
            var bar = new RectangleF(x, Height - Theme.S(16), Width - x - Theme.S(10), Theme.S(6));
            Theme.FillRound(g, Theme.Bg, bar, Theme.S(3));
            Theme.FillRound(g, col,
                new RectangleF(bar.X, bar.Y, Math.Max(Theme.S(6), bar.Width * (float)Math.Min(1, Score)), bar.Height),
                Theme.S(3));
            using (var p = new Pen(Theme.Mix(Theme.Panel2, Theme.Text, 0.35f)))
                foreach (float m in new[] { 0.5f, 0.8f })
                    g.DrawLine(p, bar.X + bar.Width * m, bar.Y - Theme.S(2), bar.X + bar.Width * m,
                        bar.Bottom + Theme.S(1));
        }
    }

    /// <summary>
    ///     A best-cut search as a map: for a cut search, the score of every (A out point, B in point) around where the cut
    ///     was;
    ///     for an entry search, the score along B. Marks where the cut was, where it is now and the best point; click to cut
    ///     there.
    /// </summary>
    public sealed class TransitionMap : DarkControl
    {
        /// <summary>Where the cut is now (offsets in the search's terms); NaN = unknown.</summary>
        public double CurDa = double.NaN, CurDb = double.NaN;

        public CutSearch Search;

        /// <summary>The strips / transition changed since the search (other than the cut): drawn dimmed.</summary>
        public bool Stale;

        Bitmap _img;
        CutSearch _imgOf;
        Point _mouse = new(-1, -1);

        public TransitionMap()
        {
            Height = Theme.S(170);
            Cursor = Cursors.Cross;
        }

        Rectangle Plot => new(Theme.S(40), Theme.S(20), Math.Max(10, Width - Theme.S(48)),
            Math.Max(10, Height - Theme.S(40)));

        double XMin => Search.Entry ? Search.TbOf(Search.Db[0]) : Search.Da[0];
        double XMax => Search.Entry ? Search.TbOf(Search.Db[Search.Db.Length - 1]) : Search.Da[Search.Da.Length - 1];
        double YMin => Search.Db[0];
        double YMax => Search.Db[Search.Db.Length - 1];

        /// <summary>A point was clicked (offsets of A's out point and B's in point, scene s).</summary>
        public event Action<double, double> Pick;

        protected override void Dispose(bool disposing)
        {
            if (disposing) _img?.Dispose();
            base.Dispose(disposing);
        }

        static string F(double v, string f = "0.0")
        {
            return v.ToString(f, CultureInfo.InvariantCulture);
        }

        static string Signed(double v)
        {
            return (v > 1e-9 ? "+" : v < -1e-9 ? "−" : "") + F(Math.Abs(v), "0.00");
        }

        PointF ToPx(double da, double db)
        {
            var p = Plot;
            if (Search.Entry)
            {
                double x = Search.TbOf(db);
                return new PointF((float)(p.X + (x - XMin) / Math.Max(1e-9, XMax - XMin) * p.Width), p.Bottom);
            }

            return new PointF((float)(p.X + (da - XMin) / Math.Max(1e-9, XMax - XMin) * p.Width),
                (float)(p.Bottom - (db - YMin) / Math.Max(1e-9, YMax - YMin) * p.Height));
        }

        /// <summary>Nearest searched point under a pixel (indices), false outside the plot.</summary>
        bool Cell(Point m, out int i, out int j)
        {
            i = j = 0;
            var p = Plot;
            if (Search == null || !p.Contains(m)) return false;
            double fx = (m.X - p.X) / (double)Math.Max(1, p.Width);
            if (Search.Entry)
            {
                j = (int)Math.Round(fx * (Search.Db.Length - 1));
                i = 0;
            }
            else
            {
                double fy = (p.Bottom - m.Y) / (double)Math.Max(1, p.Height);
                i = (int)Math.Round(fx * (Search.Da.Length - 1));
                j = (int)Math.Round(fy * (Search.Db.Length - 1));
            }

            i = Math.Max(0, Math.Min(Search.Da.Length - 1, i));
            j = Math.Max(0, Math.Min(Search.Db.Length - 1, j));
            return true;
        }

        void BuildImage()
        {
            _img?.Dispose();
            _img = null;
            _imgOf = Search;
            if (Search == null || Search.Entry) return;
            int w = Search.Da.Length, h = Search.Db.Length;
            _img = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            for (int i = 0; i < w; i++)
            for (int j = 0; j < h; j++)
            {
                float s = Search.Score[i, j];
                _img.SetPixel(i, h - 1 - j, float.IsNaN(s) ? Theme.Bg : TransitionColors.Of(s));
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            _mouse = e.Location;
            Invalidate();
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _mouse = new Point(-1, -1);
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left || Search == null || !Cell(e.Location, out int i, out int j)) return;
            if (float.IsNaN(Search.Score[i, j])) return;
            Pick?.Invoke(Search.Da[i], Search.Db[j]);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Panel);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var p = Plot;
            var faint = Theme.TextFaint;
            var small = Theme.Small ?? Theme.UI;
            if (Search == null)
            {
                Theme.FillRound(g, Theme.Panel2, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.S(5));
                TextRenderer.DrawText(g, "Find best cut / entry to see the match map here.", Theme.UI, ClientRectangle,
                    faint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak |
                    TextFormatFlags.NoPrefix);
                return;
            }

            if (_imgOf != Search) BuildImage();
            using (var bg = new SolidBrush(Theme.Bg)) g.FillRectangle(bg, p);
            if (!Search.Entry)
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(_img, p);
                g.PixelOffsetMode = PixelOffsetMode.Default;
                // axes through the original cut
                using (var ax = new Pen(Color.FromArgb(90, 255, 255, 255)) { DashStyle = DashStyle.Dot })
                {
                    var o = ToPx(0, 0);
                    g.DrawLine(ax, o.X, p.Top, o.X, p.Bottom);
                    g.DrawLine(ax, p.Left, o.Y, p.Right, o.Y);
                }

                TextRenderer.DrawText(g, "A ends " + Signed(XMin) + " s", small,
                    new Rectangle(p.X, p.Bottom + 1, p.Width / 2, Theme.S(16)), faint,
                    TextFormatFlags.Left | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, Signed(XMax) + " s", small,
                    new Rectangle(p.X + p.Width / 2, p.Bottom + 1, p.Width / 2, Theme.S(16)), faint,
                    TextFormatFlags.Right | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, Signed(YMax), small, new Rectangle(0, p.Top - 2, p.X - 3, Theme.S(14)), faint,
                    TextFormatFlags.Right | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, "B\nstarts", small,
                    new Rectangle(0, p.Top + p.Height / 2 - Theme.S(14), p.X - 3, Theme.S(28)), faint,
                    TextFormatFlags.Right | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, Signed(YMin), small,
                    new Rectangle(0, p.Bottom - Theme.S(12), p.X - 3, Theme.S(14)), faint,
                    TextFormatFlags.Right | TextFormatFlags.NoPrefix);
            }
            else
            {
                // score along B (0..100 up), the 50 / 80 bands as guides
                using (var guide = new Pen(Color.FromArgb(50, 255, 255, 255)) { DashStyle = DashStyle.Dot })
                    foreach (float m in new[] { 0.5f, 0.8f })
                        g.DrawLine(guide, p.Left, p.Bottom - p.Height * m, p.Right, p.Bottom - p.Height * m);
                int n = Search.Db.Length;
                var prev = PointF.Empty;
                bool has = false;
                for (int j = 0; j < n; j++)
                {
                    float s = Search.Score[0, j];
                    if (float.IsNaN(s))
                    {
                        has = false;
                        continue;
                    }

                    float x = ToPx(0, Search.Db[j]).X;
                    float y = p.Bottom - p.Height * s;
                    var pt = new PointF(x, y);
                    if (has)
                        using (var pen = new Pen(TransitionColors.Of(s), Theme.S(2) / 1.5f))
                            g.DrawLine(pen, prev, pt);
                    prev = pt;
                    has = true;
                }

                TextRenderer.DrawText(g, "B from " + F(XMin, "0") + " s", small,
                    new Rectangle(p.X, p.Bottom + 1, p.Width / 2, Theme.S(16)), faint,
                    TextFormatFlags.Left | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, F(XMax, "0") + " s", small,
                    new Rectangle(p.X + p.Width / 2, p.Bottom + 1, p.Width / 2, Theme.S(16)), faint,
                    TextFormatFlags.Right | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, "100", small, new Rectangle(0, p.Top - 2, p.X - 3, Theme.S(14)), faint,
                    TextFormatFlags.Right | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, "0", small, new Rectangle(0, p.Bottom - Theme.S(12), p.X - 3, Theme.S(14)),
                    faint, TextFormatFlags.Right | TextFormatFlags.NoPrefix);
            }

            using (var border = new Pen(Theme.Border)) g.DrawRectangle(border, p);

            // markers: where the cut was (ring), the best point (diamond), where it is now (cross)
            PointF Mark(double da, double db, float score)
            {
                return Search.Entry
                    ? new PointF(ToPx(0, db).X, p.Bottom - p.Height * Math.Max(0, Math.Min(1, score)))
                    : ToPx(da, db);
            }

            float r = Theme.S(5);
            var start = Mark(0, 0, Search.StartScore);
            using (var pen = new Pen(Color.White, 1.5f)) g.DrawEllipse(pen, start.X - r, start.Y - r, 2 * r, 2 * r);
            var best = Mark(Search.BestDa, Search.BestDb, Search.BestScore);
            var dia = new[]
            {
                new PointF(best.X, best.Y - r - 1), new PointF(best.X + r + 1, best.Y),
                new PointF(best.X, best.Y + r + 1), new PointF(best.X - r - 1, best.Y)
            };
            using (var b = new SolidBrush(Theme.Accent)) g.FillPolygon(b, dia);
            using (var pen = new Pen(Color.Black, 1f)) g.DrawPolygon(pen, dia);
            if (!double.IsNaN(CurDa) && !double.IsNaN(CurDb))
            {
                bool inside = Search.Entry
                    ? Search.TbOf(CurDb) >= XMin - 1e-6 && Search.TbOf(CurDb) <= XMax + 1e-6
                    : CurDa >= XMin - 1e-6 && CurDa <= XMax + 1e-6 && CurDb >= YMin - 1e-6 && CurDb <= YMax + 1e-6;
                if (inside)
                {
                    float sc = float.NaN;
                    if (Search.Entry)
                    {
                        int j = (int)Math.Round((CurDb - Search.Db[0]) /
                                                Math.Max(1e-9, Search.Db.Length > 1 ? Search.Db[1] - Search.Db[0] : 1));
                        if (j >= 0 && j < Search.Db.Length) sc = Search.Score[0, j];
                    }

                    var c = Mark(CurDa, CurDb, float.IsNaN(sc) ? 0 : sc);
                    using (var pen = new Pen(Color.White, 2f))
                    {
                        g.DrawLine(pen, c.X - r, c.Y, c.X + r, c.Y);
                        g.DrawLine(pen, c.X, c.Y - r, c.X, c.Y + r);
                    }
                }
            }

            if (Stale)
                using (var veil = new SolidBrush(Color.FromArgb(150, Theme.Panel)))
                    g.FillRectangle(veil, p);

            // title line: the hovered point, else the legend
            string title;
            if (Cell(_mouse, out int hi, out int hj))
            {
                float s = Search.Score[hi, hj];
                string where = Search.Entry
                    ? "enter B at " + F(Search.TbOf(Search.Db[hj]), "0.00") + " s"
                    : "A ends " + Signed(Search.Da[hi]) + " s, B starts " + Signed(Search.Db[hj]) + " s";
                title = where + (float.IsNaN(s)
                    ? "  ·  not possible"
                    : "  ·  " + F(s * 100, "0") + "  (click to cut here)");
            }
            else
                title = Stale
                    ? "Out of date: the strips changed. Search again."
                    : "○ was " + F(Search.StartScore * 100, "0") + "    ◆ best " + F(Search.BestScore * 100, "0") +
                      "    + now";

            TextRenderer.DrawText(g, title, small, new Rectangle(p.X, 0, p.Width, p.Top - 2),
                Stale ? Theme.Warn : Theme.TextDim,
                TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }
}
