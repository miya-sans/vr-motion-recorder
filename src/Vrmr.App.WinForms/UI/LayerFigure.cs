using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VRMR.UI
{
    /// <summary>
    ///     A clickable body figure for choosing what a layer drives: the devices (headset, controllers, each tracker), finger
    ///     tracking per hand, face tracking and parameters of an input layer (<see cref="InputPart" />), or the body parts of
    ///     a pose layer (<see cref="BodyPart" />). Seen from behind, so the body's left is on the left. Spots the take doesn't
    ///     have are drawn dashed. Click toggles one; the value is a flags int (InputPart or BodyPart).
    /// </summary>
    public sealed class LayerFigure : DarkControl
    {
        public enum Mode { Inputs, BodyParts }

        public readonly Mode Kind;
        readonly List<Spot> _spots = new();
        readonly ToolTip _tip = new();

        /// <summary>Flags the source has (others are drawn dashed); -1 = unknown (all solid).</summary>
        public int Available = -1;

        // ---------------------------------------------------------------- layout (separate x / y scales: a schematic)

        float _sx, _sy, _ox, _oy;
        string _tipText;
        int _value, _hover = -1;

        public LayerFigure(Mode mode)
        {
            Kind = mode;
            Height = Theme.S(mode == Mode.Inputs ? 262 : 236);
            Cursor = Cursors.Hand;
            if (mode == Mode.Inputs) BuildInputs();
            else BuildParts();
        }

        public int Value
        {
            get => _value;
            set
            {
                if (_value == value) return;
                _value = value;
                Invalidate();
            }
        }

        float SpotR => Theme.S(8);
        public event Action<int> Toggled;

        static PointF P(float x, float y)
        {
            return new PointF(x, y);
        }

        void Dev(InputPart p, string label, float x, float y, int side, string tip)
        {
            _spots.Add(new Spot { Flag = (int)p, Label = label, A = P(x, y), Device = true, Side = side, Tip = tip });
        }

        void Pill(int flag, string label, float x, float y, string tip)
        {
            _spots.Add(new Spot { Flag = flag, Label = label, A = P(x, y), Pill = true, Tip = tip });
        }

        void Seg(BodyPart p, float x0, float y0, float x1, float y1, float r, string tip)
        {
            _spots.Add(new Spot
                { Flag = (int)p, Label = p.ToString(), A = P(x0, y0), B = P(x1, y1), R = r, Tip = tip });
        }

        void BuildInputs()
        {
            Dev(InputPart.Head, "Headset", 50, 9, 1, "Headset (HMD)");
            Dev(InputPart.Chest, "Chest", 50, 29, 1, "Chest tracker");
            Dev(InputPart.Hips, "Hips", 50, 52, 1, "Hip tracker");
            Dev(InputPart.LeftElbow, "Elbow", 27, 38, -1, "Left elbow tracker");
            Dev(InputPart.RightElbow, "Elbow", 73, 38, 1, "Right elbow tracker");
            Dev(InputPart.LeftHand, "L hand", 14, 59, 0, "Left controller");
            Dev(InputPart.RightHand, "R hand", 86, 59, 0, "Right controller");
            Dev(InputPart.LeftKnee, "Knee", 41, 80, -1, "Left knee tracker");
            Dev(InputPart.RightKnee, "Knee", 59, 80, 1, "Right knee tracker");
            Dev(InputPart.LeftFoot, "L foot", 40, 105, -1, "Left foot tracker");
            Dev(InputPart.RightFoot, "R foot", 60, 105, 1, "Right foot tracker");
            Pill((int)InputPart.Face, "Face", 87, 9, "Face tracking (FT parameters / face shapes)");
            Pill((int)InputPart.LeftFingers, "Fingers", 14, 71, "Left hand finger tracking (SteamVR skeletal input)");
            Pill((int)InputPart.RightFingers, "Fingers", 86, 71, "Right hand finger tracking (SteamVR skeletal input)");
            Pill((int)InputPart.Params, "Params", 88, 115, "Other avatar parameters (gestures, toggles, ...)");
        }

        void BuildParts()
        {
            Seg(BodyPart.Head, 50, 6, 50, 13, 6f, "Head: neck, head, eyes, jaw");
            Seg(BodyPart.Spine, 50, 23, 50, 45, 4.6f, "Spine: spine, chest, upper chest");
            Seg(BodyPart.Hips, 43, 53, 57, 53, 4f, "Hips (rotation)");
            Seg(BodyPart.LeftArm, 38, 24, 15, 57, 3.2f, "Left arm: shoulder, arm, hand");
            Seg(BodyPart.RightArm, 62, 24, 85, 57, 3.2f, "Right arm: shoulder, arm, hand");
            Seg(BodyPart.LeftLeg, 44, 60, 40, 105, 3.8f, "Left leg: leg, foot, toes");
            Seg(BodyPart.RightLeg, 56, 60, 60, 105, 3.8f, "Right leg: leg, foot, toes");
            Pill((int)BodyPart.Root, "Root", 50, 116, "Root: the hips' position (where the body is)");
            Pill((int)BodyPart.LeftFingers, "Fingers", 13, 69, "Left fingers");
            Pill((int)BodyPart.RightFingers, "Fingers", 87, 69, "Right fingers");
            Pill((int)BodyPart.Face, "Face", 85, 8, "Face shapes");
            Pill((int)BodyPart.Params, "Params", 88, 115, "Avatar parameters");
        }

        void Fit()
        {
            float m = Theme.S(6);
            _sx = (Width - 2 * m) / 100f;
            _sy = (Height - 2 * m) / 122f;
            _ox = m;
            _oy = m + _sy;
        }

        PointF Map(PointF p)
        {
            return new PointF(_ox + p.X * _sx, _oy + p.Y * _sy);
        }

        RectangleF PillRect(Spot s)
        {
            var c = Map(s.A);
            float w = TextRenderer.MeasureText(s.Label, Theme.Small).Width + Theme.S(12), h = Theme.S(18);
            return new RectangleF(c.X - w / 2, c.Y - h / 2, w, h);
        }

        // ---------------------------------------------------------------- painting

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Panel);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Fit();
            if (Kind == Mode.Inputs) DrawBody(g);
            for (int i = 0; i < _spots.Count; i++) DrawSpot(g, _spots[i], i == _hover);
            var l = Map(P(2, 117));
            var r = Map(P(70, 117));
            TextRenderer.DrawText(g, "L", Theme.Small, Point.Round(l), Theme.TextFaint);
            TextRenderer.DrawText(g, "R", Theme.Small, Point.Round(r), Theme.TextFaint);
        }

        void DrawBody(Graphics g)
        {
            // a faint figure (seen from behind) under the device spots
            using (var pen = new Pen(Color.FromArgb(90, Theme.TextFaint), Theme.Sf(3.5f))
                       { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                void L(params float[] xy)
                {
                    var pts = new PointF[xy.Length / 2];
                    for (int i = 0; i < pts.Length; i++) pts[i] = Map(P(xy[2 * i], xy[2 * i + 1]));
                    g.DrawLines(pen, pts);
                }

                L(50, 17, 50, 53);
                L(37, 24, 63, 24);
                L(37, 24, 27, 38, 14, 59);
                L(63, 24, 73, 38, 86, 59);
                L(42, 54, 58, 54);
                L(43, 54, 41, 80, 40, 105);
                L(57, 54, 59, 80, 60, 105);
            }
        }

        bool On(Spot s)
        {
            return (_value & s.Flag) == s.Flag;
        }

        bool Has(Spot s)
        {
            return Available < 0 || (Available & s.Flag) != 0;
        }

        void DrawSpot(Graphics g, Spot s, bool hover)
        {
            bool on = On(s), has = Has(s);
            var edge = hover ? Theme.Text : on ? Theme.Accent : Theme.TextFaint;
            if (s.Pill)
            {
                var r = PillRect(s);
                s.Hit = RectangleF.Inflate(r, Theme.S(2), Theme.S(2));
                if (on)
                    Theme.FillRound(g, has ? Theme.AccentDim : Color.FromArgb(70, Theme.AccentDim), r, r.Height / 2);
                using (var pen = new Pen(edge, 1.2f))
                {
                    if (!has) pen.DashStyle = DashStyle.Dash;
                    using (var path = Theme.Round(r, r.Height / 2)) g.DrawPath(pen, path);
                }

                TextRenderer.DrawText(g, s.Label, Theme.Small, Rectangle.Round(r), on ? Theme.Text : Theme.TextDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return;
            }

            if (s.Device)
            {
                var c = Map(s.A);
                float rr = SpotR;
                s.Hit = new RectangleF(c.X - rr - 3, c.Y - rr - 3, rr * 2 + 6, rr * 2 + 6);
                using (var b = new SolidBrush(on
                           ? has ? Theme.Accent : Color.FromArgb(110, Theme.Accent)
                           : Theme.Panel)) g.FillEllipse(b, c.X - rr, c.Y - rr, rr * 2, rr * 2);
                using (var pen = new Pen(edge, hover ? 2f : 1.4f))
                {
                    if (!has) pen.DashStyle = DashStyle.Dash;
                    g.DrawEllipse(pen, c.X - rr, c.Y - rr, rr * 2, rr * 2);
                }

                var size = TextRenderer.MeasureText(s.Label, Theme.Small);
                var pt = s.Side < 0 ? new PointF(c.X - rr - Theme.S(4) - size.Width, c.Y - size.Height / 2f)
                    : s.Side > 0 ? new PointF(c.X + rr + Theme.S(4), c.Y - size.Height / 2f)
                    : new PointF(c.X - size.Width / 2f, c.Y - rr - size.Height - Theme.S(1));
                TextRenderer.DrawText(g, s.Label, Theme.Small, Point.Round(pt),
                    on ? Theme.Text : has ? Theme.TextDim : Theme.TextFaint, TextFormatFlags.NoPrefix);
                // the hit area covers the label too
                s.Hit = RectangleF.Union(s.Hit, new RectangleF(pt, size));
                return;
            }

            // a body segment (capsule)
            {
                var a = Map(s.A);
                var b = Map(s.B);
                float w = s.R * 2 * Math.Min(_sx, _sy);
                s.Hit = RectangleF.FromLTRB(Math.Min(a.X, b.X) - w / 2, Math.Min(a.Y, b.Y) - w / 2,
                    Math.Max(a.X, b.X) + w / 2, Math.Max(a.Y, b.Y) + w / 2);
                var fill = on ? hover ? Theme.Mix(Theme.Accent, Theme.Text, 0.25f) : Theme.Accent :
                    hover ? Theme.Border : Theme.Panel2;
                using (var pen = new Pen(fill, w) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(pen, a, b);
                if (!on)
                    using (var path = new GraphicsPath())
                    using (var widen = new Pen(Color.Black, w) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    using (var pen = new Pen(hover ? Theme.TextDim : Theme.Border, 1.2f))
                    {
                        if (!has) pen.DashStyle = DashStyle.Dash;
                        path.AddLine(a, b);
                        path.Widen(widen);
                        g.DrawPath(pen, path);
                    }
            }
        }

        // ---------------------------------------------------------------- input

        int HitTest(Point p)
        {
            // pills and devices first (they sit on top), then the nearest segment
            for (int i = _spots.Count - 1; i >= 0; i--)
            {
                var s = _spots[i];
                if ((s.Pill || s.Device) && s.Hit.Contains(p)) return i;
            }

            int best = -1;
            float bd = float.MaxValue;
            for (int i = 0; i < _spots.Count; i++)
            {
                var s = _spots[i];
                if (s.Pill || s.Device) continue;
                float d = DistToSeg(p, Map(s.A), Map(s.B)) - s.R * Math.Min(_sx, _sy);
                if (d < Theme.S(5) && d < bd)
                {
                    bd = d;
                    best = i;
                }
            }

            return best;
        }

        static float DistToSeg(PointF p, PointF a, PointF b)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
            float t = l2 > 0 ? Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2)) : 0;
            float x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
            return (float)Math.Sqrt(x * x + y * y);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitTest(e.Location);
            if (h != _hover)
            {
                _hover = h;
                Invalidate();
                string t = h >= 0 ? _spots[h].Tip + (Has(_spots[h]) ? "" : " (not in this take)") : null;
                if (t != _tipText)
                {
                    _tipText = t;
                    _tip.SetToolTip(this, t ?? "");
                }
            }

            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover >= 0)
            {
                _hover = -1;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int h = HitTest(e.Location);
            if (h < 0) return;
            _value ^= _spots[h].Flag;
            Invalidate();
            Toggled?.Invoke(_value);
        }

        /// <summary>Clicks the spot with this flag (tests).</summary>
        public void ClickFlag(int flag)
        {
            int i = _spots.FindIndex(s => s.Flag == flag);
            if (i < 0) return;
            _value ^= flag;
            Invalidate();
            Toggled?.Invoke(_value);
        }

        /// <summary>Plain-language list of what a value drives.</summary>
        public static string Describe(InputPart m)
        {
            if (m == InputPart.None) return "nothing";
            if (m == InputPart.All) return "everything";
            var names = new List<string>();

            void Pair(InputPart l, InputPart r, string both, string left, string right)
            {
                if ((m & l) != 0 && (m & r) != 0) names.Add(both);
                else if ((m & l) != 0) names.Add(left);
                else if ((m & r) != 0) names.Add(right);
            }

            if ((m & InputPart.Head) != 0) names.Add("headset");
            Pair(InputPart.LeftHand, InputPart.RightHand, "both controllers", "left controller", "right controller");
            if ((m & InputPart.Chest) != 0) names.Add("chest");
            if ((m & InputPart.Hips) != 0) names.Add("hips");
            Pair(InputPart.LeftElbow, InputPart.RightElbow, "both elbows", "left elbow", "right elbow");
            Pair(InputPart.LeftKnee, InputPart.RightKnee, "both knees", "left knee", "right knee");
            Pair(InputPart.LeftFoot, InputPart.RightFoot, "both feet", "left foot", "right foot");
            Pair(InputPart.LeftFingers, InputPart.RightFingers, "finger tracking", "left-hand finger tracking",
                "right-hand finger tracking");
            if ((m & InputPart.Face) != 0) names.Add("face tracking");
            if ((m & InputPart.Params) != 0) names.Add("parameters");
            return string.Join(", ", names);
        }

        sealed class Spot
        {
            /// <summary>Figure units (x 0..100, y 0..120): a device circle at A, a body segment A-B (R = half width), or a pill at A.</summary>
            public PointF A, B;

            public int Flag;
            public RectangleF Hit;
            public string Label, Tip;
            public bool Pill, Device;
            public float R;

            /// <summary>Device label: -1 left of the spot, +1 right, 0 above.</summary>
            public int Side = 1;
        }
    }
}
