using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace VRMR.UI
{
    /// <summary>
    /// A move / rotate gizmo drawn over the 3D view: axis arrows, plane squares and a view-plane handle (move), axis rings
    /// and a view ring (rotate). Constant size on screen. Drags are measured from where they started.
    /// </summary>
    public sealed class PoseGizmo
    {
        public enum Part { None, X, Y, Z, XY, YZ, XZ, View, RX, RY, RZ, RView }

        public Vec3 Center;
        public Quat Axes = Quat.Identity;
        public bool Rotate;
        public float SizePx => Theme.Sf(78);

        static readonly Color CX = Color.FromArgb(232, 88, 88), CY = Color.FromArgb(110, 214, 110), CZ = Color.FromArgb(92, 146, 255), CView = Color.FromArgb(220, 222, 230);
        static readonly Color Hot = Color.FromArgb(255, 214, 90);

        public Vec3 AxisDir(int i) => Axes * (i == 0 ? Vec3.Right : i == 1 ? Vec3.Up : Vec3.Forward);

        public float WorldSize(Viewport3D v)
        {
            float depth = Math.Max(0.05f, Vec3.Dot(Center - v.Eye, v.CamForward));
            return SizePx * depth / Math.Max(1f, v.Focal);
        }

        static float SegDist(PointF p, PointF a, PointF b)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
            float t = l2 < 1e-6f ? 0 : MathX.Clamp01(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2);
            float x = a.X + dx * t - p.X, y = a.Y + dy * t - p.Y;
            return (float)Math.Sqrt(x * x + y * y);
        }

        List<PointF> Ring(Viewport3D v, Vec3 axis, float r)
        {
            var u = Vec3.Cross(axis, Math.Abs(axis.y) < 0.9f ? Vec3.Up : Vec3.Right).Normalized;
            var w = Vec3.Cross(axis, u).Normalized;
            var pts = new List<PointF>(65);
            for (int i = 0; i <= 64; i++)
            {
                double a = i * Math.PI * 2 / 64;
                if (v.Project(Center + (u * (float)Math.Cos(a) + w * (float)Math.Sin(a)) * r, out var s)) pts.Add(s); else return null;
            }
            return pts;
        }

        static float PolyDist(PointF p, List<PointF> pts)
        {
            if (pts == null) return float.MaxValue;
            float best = float.MaxValue;
            for (int i = 1; i < pts.Count; i++) best = Math.Min(best, SegDist(p, pts[i - 1], pts[i]));
            return best;
        }

        PointF[] Square(Viewport3D v, int a, int b, float L)
        {
            var da = AxisDir(a) * L; var db = AxisDir(b) * L;
            var c = new[] { Center + da * 0.28f + db * 0.28f, Center + da * 0.5f + db * 0.28f, Center + da * 0.5f + db * 0.5f, Center + da * 0.28f + db * 0.5f };
            var pts = new PointF[4];
            for (int i = 0; i < 4; i++) if (!v.Project(c[i], out pts[i])) return null;
            return pts;
        }

        static bool Inside(PointF p, PointF[] poly)
        {
            if (poly == null) return false;
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) inside = !inside;
            return inside;
        }

        /// <summary>The gizmo part under a screen point.</summary>
        public Part Hit(Viewport3D v, PointF m)
        {
            if (!v.Project(Center, out var c)) return Part.None;
            float L = WorldSize(v), tol = Theme.Sf(7);
            if (Rotate)
            {
                Part best = Part.None; float bd = tol;
                for (int i = 0; i < 3; i++)
                {
                    float d = PolyDist(m, Ring(v, AxisDir(i), L));
                    if (d < bd) { bd = d; best = i == 0 ? Part.RX : i == 1 ? Part.RY : Part.RZ; }
                }
                float rv = SizePx * 1.22f;
                float dv = Math.Abs((float)Math.Sqrt((m.X - c.X) * (m.X - c.X) + (m.Y - c.Y) * (m.Y - c.Y)) - rv);
                if (dv < bd) best = Part.RView;
                return best;
            }
            float dc = (float)Math.Sqrt((m.X - c.X) * (m.X - c.X) + (m.Y - c.Y) * (m.Y - c.Y));
            if (dc < Theme.Sf(9)) return Part.View;
            if (Inside(m, Square(v, 0, 1, L))) return Part.XY;
            if (Inside(m, Square(v, 1, 2, L))) return Part.YZ;
            if (Inside(m, Square(v, 0, 2, L))) return Part.XZ;
            Part bp = Part.None; float bdist = tol;
            for (int i = 0; i < 3; i++)
            {
                if (!v.Project(Center + AxisDir(i) * L, out var e)) continue;
                float d = SegDist(m, c, e);
                if (d < bdist) { bdist = d; bp = i == 0 ? Part.X : i == 1 ? Part.Y : Part.Z; }
            }
            return bp;
        }

        public void Draw(Viewport3D v, Graphics g, Part hot)
        {
            if (!v.Project(Center, out var c)) return;
            float L = WorldSize(v);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color Col(int i) => i == 0 ? CX : i == 1 ? CY : CZ;
            if (Rotate)
            {
                for (int i = 0; i < 3; i++)
                {
                    var pts = Ring(v, AxisDir(i), L);
                    if (pts == null) continue;
                    bool h = hot == (i == 0 ? Part.RX : i == 1 ? Part.RY : Part.RZ);
                    using (var p = new Pen(h ? Hot : Col(i), Theme.Sf(h ? 3f : 2f))) g.DrawLines(p, pts.ToArray());
                }
                float rv = SizePx * 1.22f;
                using (var p = new Pen(hot == Part.RView ? Hot : Color.FromArgb(170, CView), Theme.Sf(hot == Part.RView ? 2.6f : 1.5f))) g.DrawEllipse(p, c.X - rv, c.Y - rv, rv * 2, rv * 2);
                using (var b = new SolidBrush(Color.FromArgb(200, CView))) g.FillEllipse(b, c.X - 2.5f, c.Y - 2.5f, 5, 5);
                return;
            }
            // plane squares
            var planes = new[] { (Part.XY, 0, 1, 2), (Part.YZ, 1, 2, 0), (Part.XZ, 0, 2, 1) };
            foreach (var (part, a, b, n) in planes)
            {
                var sq = Square(v, a, b, L); if (sq == null) continue;
                bool h = hot == part;
                using (var br = new SolidBrush(Color.FromArgb(h ? 150 : 70, h ? Hot : Col(n)))) g.FillPolygon(br, sq);
                using (var p = new Pen(Color.FromArgb(h ? 255 : 160, h ? Hot : Col(n)), 1f)) g.DrawPolygon(p, sq);
            }
            for (int i = 0; i < 3; i++)
            {
                if (!v.Project(Center + AxisDir(i) * L, out var e)) continue;
                bool h = hot == (i == 0 ? Part.X : i == 1 ? Part.Y : Part.Z);
                using (var p = new Pen(h ? Hot : Col(i), Theme.Sf(h ? 3.4f : 2.4f)) { CustomEndCap = new AdjustableArrowCap(4, 5) }) g.DrawLine(p, c, e);
            }
            float r = Theme.Sf(7);
            using (var p = new Pen(hot == Part.View ? Hot : CView, Theme.Sf(hot == Part.View ? 2.4f : 1.5f))) g.DrawEllipse(p, c.X - r, c.Y - r, r * 2, r * 2);
        }

        // ---------------------------------------------------------------- dragging

        Part _part; Vec3 _c0, _hit0; float _ang0, _angAcc, _angPrev; PointF _m0;

        public Part Active => _part;

        public void Begin(Viewport3D v, Part p, PointF m)
        {
            _part = p; _c0 = Center; _m0 = m;
            _angAcc = 0; _angPrev = ScreenAngle(v, m); _ang0 = _angPrev;
            _hit0 = Point(v, m, out _);
        }

        public void End() => _part = Part.None;

        float ScreenAngle(Viewport3D v, PointF m)
        {
            v.Project(_c0, out var c);
            return (float)Math.Atan2(m.Y - c.Y, m.X - c.X);
        }

        /// <summary>The world point under the mouse for the active part's constraint (axis line, plane or view plane).</summary>
        Vec3 Point(Viewport3D v, PointF m, out bool ok)
        {
            ok = true;
            v.Ray(m, out var e, out var d);
            switch (_part)
            {
                case Part.X: case Part.Y: case Part.Z:
                {
                    var a = AxisDir(_part == Part.X ? 0 : _part == Part.Y ? 1 : 2);
                    var w0 = _c0 - e;
                    float b = Vec3.Dot(a, d), den = 1 - b * b;
                    if (Math.Abs(den) < 1e-4f) { ok = false; return _c0; }
                    float s = (b * Vec3.Dot(d, w0) - Vec3.Dot(a, w0)) / den;
                    return _c0 + a * s;
                }
                case Part.XY: return Plane(e, d, AxisDir(2), out ok);
                case Part.YZ: return Plane(e, d, AxisDir(0), out ok);
                case Part.XZ: return Plane(e, d, AxisDir(1), out ok);
                default: return Plane(e, d, v.CamForward, out ok);
            }
        }

        Vec3 Plane(Vec3 e, Vec3 d, Vec3 n, out bool ok)
        {
            float dn = Vec3.Dot(d, n);
            ok = Math.Abs(dn) > 1e-4f;
            if (!ok) return _c0;
            float u = Vec3.Dot(_c0 - e, n) / dn;
            if (u < 0) { ok = false; return _c0; }
            return e + d * u;
        }

        /// <summary>The drag so far: a move (world) or a rotation (world, about the start centre). fine: 1/10 speed.</summary>
        public void Delta(Viewport3D v, PointF m, bool fine, out Vec3 move, out Quat rot)
        {
            move = Vec3.Zero; rot = Quat.Identity;
            if (_part == Part.None) return;
            if (_part >= Part.RX)
            {
                float a = ScreenAngle(v, m);
                float da = a - _angPrev;
                if (da > Math.PI) da -= (float)(2 * Math.PI); else if (da < -Math.PI) da += (float)(2 * Math.PI);
                _angPrev = a;
                _angAcc += fine ? da * 0.1f : da;
                Vec3 axis = _part == Part.RView ? v.CamForward : AxisDir(_part == Part.RX ? 0 : _part == Part.RY ? 1 : 2);
                float sign = Vec3.Dot(axis, v.CamForward) < 0 ? -1 : 1;
                rot = Quat.AngleAxisRad(_angAcc * sign, axis);
                return;
            }
            var p = Point(v, m, out bool ok);
            if (!ok) return;
            move = p - _hit0;
            if (fine) move = move * 0.1f;
        }

        /// <summary>Degrees turned so far (for the status text).</summary>
        public float Degrees => _angAcc * MathX.Rad2Deg;
    }
}
