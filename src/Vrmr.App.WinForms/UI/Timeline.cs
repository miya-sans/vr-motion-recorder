using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace VRMR.UI
{
    /// <summary>
    /// Source-time timeline: shows the whole recording, trims (darkened), cuts (red), a selection, the playhead,
    /// data coverage lanes (VRChat IK / SteamVR / VRChat poses / face) and markers (origin, calibration, events).
    /// </summary>
    public sealed class Timeline : DarkControl
    {
        public Recording Rec;
        public double Playhead;
        public double SelA = -1, SelB = -1;
        public double ViewStart = 0, ViewEnd = 10;
        public event Action<double> Scrub;
        public event Action EditsChanged;
        public event Action<double, double> RestoreCut;
        public float[] FaceActivity; public double FaceActivityStep = 0.1;
        public List<Tuple<string, Color, List<double[]>>> Lanes = new List<Tuple<string, Color, List<double[]>>>();

        enum Drag { None, Scrub, Select, TrimIn, TrimOut, Pan }
        Drag _drag;
        int _lastX;
        double _selStart;

        public Timeline() { Height = Theme.S(150); BackColor = Theme.Panel; }

        double Dur => Rec != null ? Math.Max(0.01, Rec.Duration) : 1;
        int L => Theme.S(88);
        int R => Width - Theme.S(10);
        float X(double t) => (float)(L + (t - ViewStart) / Math.Max(1e-6, ViewEnd - ViewStart) * (R - L));
        double T(int x) => ViewStart + (x - L) / (double)Math.Max(1, R - L) * (ViewEnd - ViewStart);
        int RulerH => Theme.S(22);

        public void FitAll() { ViewStart = 0; ViewEnd = Dur; Invalidate(); }

        public void SetRecording(Recording r)
        {
            Rec = r; SelA = SelB = -1; Playhead = r?.Edits.TrimIn ?? 0;
            Lanes.Clear(); FaceActivity = null;
            if (r != null)
            {
                Lanes.Add(Tuple.Create("VRChat IK", Theme.Ok, Coverage(r.Humr.T, 0.5)));
                var hmd = r.Devices.Where(d => d.DeviceClass == 1).OrderByDescending(d => d.Count).FirstOrDefault();
                Lanes.Add(Tuple.Create("SteamVR", Theme.Accent, hmd != null ? Coverage(hmd.T, 0.3) : new List<double[]>()));
                var vh = r.Stream(StreamKind.VrcHead);
                Lanes.Add(Tuple.Create("VRC poses", Color.FromArgb(180, 140, 255), vh != null ? Coverage(vh.T, 0.5) : new List<double[]>()));
                ComputeFaceActivity(r);
            }
            FitAll();
        }

        static List<double[]> Coverage(List<double> t, double gap)
        {
            var res = new List<double[]>();
            if (t.Count == 0) return res;
            double a = t[0], last = t[0];
            for (int i = 1; i < t.Count; i++)
            {
                if (t[i] - last > gap) { res.Add(new[] { a, last }); a = t[i]; }
                last = t[i];
            }
            res.Add(new[] { a, last });
            return res;
        }

        void ComputeFaceActivity(Recording r)
        {
            var ft = r.Params.Where(p => p.Type == ParamType.Float && (p.Name.StartsWith("FT/v2/") || p.Name.StartsWith("v2/"))).ToList();
            if (ft.Count == 0) return;
            int n = (int)(r.Duration / FaceActivityStep) + 1;
            var a = new float[n];
            foreach (var p in ft)
                for (int i = 1; i < p.Count; i++)
                {
                    int k = (int)(p.T[i] / FaceActivityStep);
                    if (k >= 0 && k < n) a[k] += Math.Abs(p.V[i] - p.V[i - 1]);
                }
            float mx = a.Length > 0 ? a.Max() : 0;
            if (mx > 0) for (int i = 0; i < n; i++) a[i] = (float)Math.Sqrt(a[i] / mx);
            FaceActivity = a;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (Rec == null) return;
            Focus();
            _lastX = e.X;
            var ed = Rec.Edits;
            double tin = ed.TrimIn, tout = ed.TrimOut < 0 ? Dur : ed.TrimOut;
            if (e.Button == MouseButtons.Middle) { _drag = Drag.Pan; return; }
            if (e.Button == MouseButtons.Right)
            {
                double t = T(e.X);
                foreach (var c in ed.Cuts) if (t >= c[0] && t <= c[1]) { RestoreCut?.Invoke(c[0], c[1]); return; }
                _drag = Drag.Pan; return;
            }
            if (Math.Abs(e.X - X(tin)) < Theme.S(6)) { _drag = Drag.TrimIn; return; }
            if (Math.Abs(e.X - X(tout)) < Theme.S(6)) { _drag = Drag.TrimOut; return; }
            if ((ModifierKeys & Keys.Shift) != 0 || e.Y > RulerH + Theme.S(4))
            {
                _drag = Drag.Select; _selStart = Clamp(T(e.X)); SelA = SelB = _selStart;
                if (e.Y <= RulerH + Theme.S(4)) { Playhead = _selStart; Scrub?.Invoke(Playhead); }
                Invalidate(); return;
            }
            _drag = Drag.Scrub; Playhead = Clamp(T(e.X)); SelA = SelB = -1; Scrub?.Invoke(Playhead); Invalidate();
        }

        double Clamp(double t) => Math.Max(0, Math.Min(Dur, t));

        /// <summary>A shaded span was clicked and selected (start, end in source seconds).</summary>
        public event Action<double, double> SpanClicked;
        int GapLaneY => RulerH + Theme.S(6) + Lanes.Count * Theme.S(14);

        /// <summary>The shaded span under a click: a tracker loss on its lane, else VRChat not tracking (calibration mode, reload).</summary>
        double[] SpanAt(double t, int y)
        {
            if (Rec == null) return null;
            if (y >= GapLaneY && y < GapLaneY + Theme.S(14))
                foreach (var g in TrackerGaps.For(Rec)) if (t >= g.A - 0.05 && t <= g.B + 0.05) return new[] { Math.Max(0, g.A), double.IsPositiveInfinity(g.B) ? Dur : g.B };
            foreach (var sp in VrcTimeline.For(Rec).NotTracking()) if (t >= sp[0] - PoseBuilder.HoldLead && t <= sp[1]) return new[] { sp[0] - PoseBuilder.HoldLead, sp[1] };
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (Rec == null) return;
            var ed = Rec.Edits;
            double tin = ed.TrimIn, tout = ed.TrimOut < 0 ? Dur : ed.TrimOut;
            if (_drag == Drag.None)
            {
                Cursor = Math.Abs(e.X - X(tin)) < Theme.S(6) || Math.Abs(e.X - X(tout)) < Theme.S(6) ? Cursors.SizeWE : Cursors.Default;
                return;
            }
            double t = Clamp(T(e.X));
            switch (_drag)
            {
                case Drag.Scrub: Playhead = t; Scrub?.Invoke(t); break;
                case Drag.Select: SelA = Math.Min(_selStart, t); SelB = Math.Max(_selStart, t); break;
                case Drag.TrimIn: ed.TrimIn = Math.Min(t, tout - 0.05); break;
                case Drag.TrimOut: ed.TrimOut = Math.Max(t, tin + 0.05); if (ed.TrimOut >= Dur - 1e-3) ed.TrimOut = -1; break;
                case Drag.Pan:
                    double dt = (e.X - _lastX) / (double)Math.Max(1, R - L) * (ViewEnd - ViewStart);
                    ViewStart -= dt; ViewEnd -= dt; break;
            }
            _lastX = e.X;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_drag == Drag.TrimIn || _drag == Drag.TrimOut) EditsChanged?.Invoke();
            if (_drag == Drag.Select && SelB - SelA < 0.02)
            {
                // a click on a shaded span (VRChat's calibration mode / avatar reload, a tracker's tracking loss) selects it,
                // ready for Cut / Keep
                double t = Clamp(T(e.X));
                var span = SpanAt(t, e.Y);
                if (span != null) { SelA = Math.Max(0, span[0]); SelB = Math.Min(Dur, span[1]); Playhead = SelA; SpanClicked?.Invoke(span[0], span[1]); }
                else SelA = SelB = -1;
                Playhead = span != null ? SelA : t; Scrub?.Invoke(Playhead);
            }
            _drag = Drag.None;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            double t = T(e.X);
            double k = Math.Pow(0.85, e.Delta / 120.0);
            double ns = t - (t - ViewStart) * k, ne = t + (ViewEnd - t) * k;
            if (ne - ns < 0.2) return;
            ViewStart = ns; ViewEnd = ne;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Panel);
            if (Rec == null)
            {
                TextRenderer.DrawText(g, "No recording loaded", Theme.UI, ClientRectangle, Theme.TextFaint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            g.SmoothingMode = SmoothingMode.None;
            var ed = Rec.Edits;
            double tin = ed.TrimIn, tout = ed.TrimOut < 0 ? Dur : ed.TrimOut;
            int top = RulerH, bottom = Height - Theme.S(4);
            var area = new Rectangle(L, top, R - L, bottom - top);
            using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, area);
            g.SetClip(new Rectangle(L, 0, R - L + 1, Height));
            // ruler ticks
            double span = ViewEnd - ViewStart;
            double[] steps = { 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
            double step = steps.FirstOrDefault(s => (R - L) / (span / s) > Theme.S(70));
            if (step == 0) step = 600;
            using (var p = new Pen(Theme.Border))
                for (double t = Math.Floor(ViewStart / step) * step; t <= ViewEnd; t += step)
                {
                    float x = X(t);
                    g.DrawLine(p, x, top - Theme.S(5), x, bottom);
                    TextRenderer.DrawText(g, Theme.Time(t, step < 1), Theme.Small, new Point((int)x + 3, Theme.S(3)), Theme.TextFaint);
                }
            // lanes
            int laneH = Theme.S(14), y = top + Theme.S(6);
            foreach (var lane in Lanes)
            {
                using (var b = new SolidBrush(Color.FromArgb(150, lane.Item2)))
                    foreach (var c in lane.Item3) g.FillRectangle(b, X(c[0]), y + 2, Math.Max(1, X(c[1]) - X(c[0])), laneH - 4);
                y += laneH;
            }
            // tracking loss per tracker (repaired: red; kept as recorded: grey)
            {
                var gaps = TrackerGaps.For(Rec);
                using (var bRep = new SolidBrush(Color.FromArgb(190, Theme.Rec)))
                using (var bKeep = new SolidBrush(Color.FromArgb(90, Theme.TextDim)))
                    foreach (var gp in gaps)
                    {
                        float x0 = X(Math.Max(0, gp.A)), x1 = X(double.IsPositiveInfinity(gp.B) ? Dur : gp.B);
                        g.FillRectangle(gp.Ignored ? bKeep : bRep, x0, y + 2, Math.Max(2, x1 - x0), laneH - 4);
                    }
                y += laneH;
            }
            // face activity sparkline
            if (FaceActivity != null)
            {
                int h = Math.Max(Theme.S(16), bottom - y - Theme.S(4));
                using (var b = new SolidBrush(Color.FromArgb(150, 240, 186, 80)))
                    for (int i = 0; i < FaceActivity.Length; i++)
                    {
                        float x0 = X(i * FaceActivityStep), x1 = X((i + 1) * FaceActivityStep);
                        if (x1 < L || x0 > R) continue;
                        float hh = FaceActivity[i] * h;
                        g.FillRectangle(b, x0, bottom - hh, Math.Max(1, x1 - x0 - 0.5f), hh);
                    }
            }
            // trims, cuts
            using (var dark = new SolidBrush(Color.FromArgb(170, 8, 8, 10)))
            {
                g.FillRectangle(dark, X(0), top, X(tin) - X(0), bottom - top);
                g.FillRectangle(dark, X(tout), top, X(Dur) - X(tout), bottom - top);
            }
            using (var cut = new HatchBrush(HatchStyle.BackwardDiagonal, Color.FromArgb(150, Theme.Rec), Color.FromArgb(110, Theme.Cut)))
                foreach (var c in ed.Cuts) g.FillRectangle(cut, X(c[0]), top, Math.Max(1, X(c[1]) - X(c[0])), bottom - top);
            // VRChat: where it isn't tracking (calibration mode, avatar reloads)
            var vtl = VrcTimeline.For(Rec);
            using (var b = new SolidBrush(Color.FromArgb(40, Theme.Ok)))
            using (var bo = new Pen(Color.FromArgb(120, Theme.Ok)) { DashStyle = DashStyle.Dash })
                foreach (var sp in vtl.NotTracking())
                {
                    float x0 = X(sp[0] - PoseBuilder.HoldLead), x1 = X(sp[1]);
                    g.FillRectangle(b, x0, top, Math.Max(1, x1 - x0), bottom - top);
                    g.DrawRectangle(bo, x0, top, Math.Max(1, x1 - x0), bottom - top - 1);
                    if (x1 - x0 > Theme.S(70)) TextRenderer.DrawText(g, "VRChat calibration", Theme.Small, new Rectangle((int)x0 + 2, bottom - Theme.S(16), (int)(x1 - x0) - 4, Theme.S(14)), Theme.Ok, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                }
            // selection
            if (SelA >= 0 && SelB > SelA)
                using (var b = new SolidBrush(Color.FromArgb(60, Theme.Accent)))
                using (var p = new Pen(Theme.Accent))
                {
                    g.FillRectangle(b, X(SelA), top, X(SelB) - X(SelA), bottom - top);
                    g.DrawRectangle(p, X(SelA), top, X(SelB) - X(SelA), bottom - top - 1);
                }
            // trim handles
            using (var b = new SolidBrush(Theme.Warn))
            {
                g.FillRectangle(b, X(tin) - Theme.S(2), top, Theme.S(4), bottom - top);
                g.FillRectangle(b, X(tout) - Theme.S(2), top, Theme.S(4), bottom - top);
            }
            // markers
            if (ed.OriginTime > 1e-6) Flag(g, ed.OriginTime, Theme.Accent, "origin"); // the pre-solve origin older takes may have (Origin no longer moves it)
            if (ed.CalibrationTime >= 0) Flag(g, ed.CalibrationTime, Theme.Ok, "calib");
            foreach (var ev in Rec.Events.Where(ev => ev.Type == "avatar" || ev.Type == "world")) Flag(g, ev.T, Theme.TextDim, ev.Type);
            foreach (var c in vtl.Calibrations) Flag(g, c.Confirm, Theme.Ok, "VRChat calibration");
            foreach (var c in vtl.Changes) Flag(g, c.T, Theme.Warn, "VRChat setting");
            foreach (var j in PlayspaceJumps.For(Rec).Jumps) Flag(g, j.After, Theme.Rec, "playspace jump");
            using (var b = new SolidBrush(Color.FromArgb(50, Theme.Warn)))
                foreach (var j in VrcJumps.For(Rec).Jumps) { g.FillRectangle(b, X(j.Start), top, Math.Max(1, X(j.End) - X(j.Start)), Theme.S(6)); Flag(g, j.Start, Theme.Warn, "VRChat jump"); }
            // playhead
            using (var p = new Pen(Theme.Text, Theme.Sf(1.5f))) g.DrawLine(p, X(Playhead), 0, X(Playhead), bottom);
            g.ResetClip();
            // lane labels
            y = top + Theme.S(6);
            foreach (var lane in Lanes) { TextRenderer.DrawText(g, lane.Item1, Theme.Small, new Rectangle(Theme.S(8), y, L - Theme.S(12), laneH), Theme.TextDim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter); y += laneH; }
            TextRenderer.DrawText(g, "Tracker loss", Theme.Small, new Rectangle(Theme.S(8), y, L - Theme.S(12), laneH), Theme.TextDim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter); y += laneH;
            if (FaceActivity != null) TextRenderer.DrawText(g, "Face", Theme.Small, new Rectangle(Theme.S(8), y, L - Theme.S(12), laneH), Theme.TextDim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, Theme.Time(Playhead), Theme.Mono, new Rectangle(Theme.S(8), 0, L, RulerH), Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        void Flag(Graphics g, double t, Color c, string label)
        {
            float x = X(t);
            using (var p = new Pen(c) { DashStyle = DashStyle.Dot }) g.DrawLine(p, x, RulerH, x, Height);
            using (var b = new SolidBrush(c)) g.FillPolygon(b, new[] { new PointF(x, RulerH), new PointF(x + Theme.S(7), RulerH + Theme.S(4)), new PointF(x, RulerH + Theme.S(8)) });
        }
    }
}
