using System;
using System.Numerics;

namespace VRMR.Render
{
    /// <summary>
    ///     A handle of the <see cref="Gizmo" />: move along an axis, in a plane or in the view plane; turn about an axis
    ///     or the view direction.
    /// </summary>
    public enum GizmoPart { None, X, Y, Z, XY, YZ, XZ, View, RX, RY, RZ, RView }

    /// <summary>
    ///     A move / turn handle drawn over the 3D view: axis arrows, plane squares and a view-plane ring (move); axis rings
    ///     and a view ring (turn). It keeps a constant size on screen. Drags are measured from where they started, so a
    ///     tool applies <see cref="Delta" /> to the state it had at <see cref="Begin" />. What the drag edits is the tool's
    ///     business.
    /// </summary>
    public sealed class Gizmo
    {
        const float Tolerance = 7f, CentreRadius = 9f, ViewRingScale = 1.22f;
        const int RingPoints = 65;

        /// <summary>Orientation of the handle's axes (identity: world axes).</summary>
        public Quat Axes = Quat.Identity;

        /// <summary>World position of the handle.</summary>
        public Vec3 Center;

        /// <summary>Turn rings instead of move arrows.</summary>
        public bool Rotate;

        /// <summary>Length of the arrows and radius of the rings, in device-independent pixels.</summary>
        public float Size = 78f;

        float _angleAcc, _anglePrev;
        Vec3 _c0, _hit0;

        /// <summary>The part being dragged, or None.</summary>
        public GizmoPart Active { get; private set; }

        /// <summary>Degrees turned since <see cref="Begin" /> (for a status text).</summary>
        public float Degrees => _angleAcc * MathX.Rad2Deg;

        /// <summary>World direction of an axis: 0 = X, 1 = Y, 2 = Z.</summary>
        public Vec3 AxisDir(int i)
        {
            return Axes * (i == 0 ? Vec3.Right : i == 1 ? Vec3.Up : Vec3.Forward);
        }

        /// <summary>Length in metres that covers <see cref="Size" /> pixels at the handle's depth.</summary>
        public float WorldSize(ViewCamera camera)
        {
            return Size * Math.Max(0.05f, camera.Depth(Center)) / Math.Max(1f, camera.Focal);
        }

        bool Square(ViewCamera camera, int a, int b, float length, Span<Vector2> points)
        {
            var da = AxisDir(a) * length;
            var db = AxisDir(b) * length;
            return camera.Project(Center + da * 0.28f + db * 0.28f, out points[0]) &&
                   camera.Project(Center + da * 0.5f + db * 0.28f, out points[1])
                   && camera.Project(Center + da * 0.5f + db * 0.5f, out points[2]) &&
                   camera.Project(Center + da * 0.28f + db * 0.5f, out points[3]);
        }

        /// <summary>The part under a screen point, or None.</summary>
        public GizmoPart Hit(ViewCamera camera, Vector2 point)
        {
            if (!camera.Project(Center, out var c)) return GizmoPart.None;
            float length = WorldSize(camera);
            if (Rotate)
            {
                var best = GizmoPart.None;
                float bestD = Tolerance;
                Span<Vector2> ring = stackalloc Vector2[RingPoints];
                for (int i = 0; i < 3; i++)
                {
                    if (!HitTest.WorldCircle(camera, Center, AxisDir(i), length, ring)) continue;
                    float d = HitTest.DistanceToPolyline(point, ring);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = GizmoPart.RX + i;
                    }
                }

                if (Math.Abs(Vector2.Distance(point, c) - Size * ViewRingScale) < bestD) best = GizmoPart.RView;
                return best;
            }

            if (Vector2.Distance(point, c) < CentreRadius) return GizmoPart.View;
            Span<Vector2> square = stackalloc Vector2[4];
            if (Square(camera, 0, 1, length, square) && HitTest.InsidePolygon(point, square)) return GizmoPart.XY;
            if (Square(camera, 1, 2, length, square) && HitTest.InsidePolygon(point, square)) return GizmoPart.YZ;
            if (Square(camera, 0, 2, length, square) && HitTest.InsidePolygon(point, square)) return GizmoPart.XZ;
            var part = GizmoPart.None;
            float dist = Tolerance;
            for (int i = 0; i < 3; i++)
            {
                if (!camera.Project(Center + AxisDir(i) * length, out var e)) continue;
                float d = HitTest.DistanceToSegment(point, c, e);
                if (d < dist)
                {
                    dist = d;
                    part = GizmoPart.X + i;
                }
            }

            return part;
        }

        /// <summary>Draws the handle; hot is the part to highlight (the one under the pointer, or the one being dragged).</summary>
        public void Draw(ViewCamera camera, OverlayList overlay, GizmoPart hot, ViewportTheme theme)
        {
            if (!camera.Project(Center, out var c)) return;
            float length = WorldSize(camera);
            if (Rotate)
            {
                for (int i = 0; i < 3; i++)
                {
                    bool h = hot == GizmoPart.RX + i;
                    overlay.WorldCircle(Center, AxisDir(i), length, h ? theme.GizmoHot : AxisColor(theme, i),
                        h ? 3f : 2f);
                }

                bool hv = hot == GizmoPart.RView;
                overlay.Circle(c, Size * ViewRingScale, hv ? theme.GizmoHot : theme.GizmoView.WithAlpha(170),
                    hv ? 2.6f : 1.5f);
                overlay.Disc(c, 2.5f / 1.5f, theme.GizmoView.WithAlpha(200));
                return;
            }

            Span<Vector2> square = stackalloc Vector2[4];
            for (int k = 0; k < 3; k++)
            {
                // the square between two axes is coloured like the third one
                (var part, int a, int b, int normal) = k == 0 ? (GizmoPart.XY, 0, 1, 2) :
                    k == 1 ? (GizmoPart.YZ, 1, 2, 0) : (GizmoPart.XZ, 0, 2, 1);
                if (!Square(camera, a, b, length, square)) continue;
                bool h = hot == part;
                var col = h ? theme.GizmoHot : AxisColor(theme, normal);
                overlay.Polygon(square, col.WithAlpha((byte)(h ? 150 : 70)));
                overlay.Polyline(square, col.WithAlpha((byte)(h ? 255 : 160)), 1f / 1.5f, true);
            }

            for (int i = 0; i < 3; i++)
            {
                if (!camera.Project(Center + AxisDir(i) * length, out var e)) continue;
                bool h = hot == GizmoPart.X + i;
                overlay.Arrow(c, e, h ? theme.GizmoHot : AxisColor(theme, i), h ? 3.4f : 2.4f);
            }

            bool hc = hot == GizmoPart.View;
            overlay.Circle(c, 7f, hc ? theme.GizmoHot : theme.GizmoView, hc ? 2.4f : 1.5f);
        }

        static Rgba AxisColor(ViewportTheme theme, int axis)
        {
            return axis == 0 ? theme.GizmoX : axis == 1 ? theme.GizmoY : theme.GizmoZ;
        }

        // ---------------------------------------------------------------- dragging

        /// <summary>Starts a drag of a part at a screen point.</summary>
        public void Begin(ViewCamera camera, GizmoPart part, Vector2 point)
        {
            Active = part;
            _c0 = Center;
            _angleAcc = 0;
            _anglePrev = ScreenAngle(camera, point);
            _hit0 = ConstraintPoint(camera, point, out _);
        }

        /// <summary>Ends the drag.</summary>
        public void End()
        {
            Active = GizmoPart.None;
        }

        float ScreenAngle(ViewCamera camera, Vector2 point)
        {
            camera.Project(_c0, out var c);
            return MathF.Atan2(point.Y - c.Y, point.X - c.X);
        }

        /// <summary>The world point under the pointer on the active part's constraint: the axis line, the plane or the view plane.</summary>
        Vec3 ConstraintPoint(ViewCamera camera, Vector2 point, out bool ok)
        {
            ok = true;
            camera.Ray(point, out var e, out var d);
            switch (Active)
            {
                case GizmoPart.X:
                case GizmoPart.Y:
                case GizmoPart.Z:
                {
                    // the point on the axis line nearest to the pointer's ray
                    var a = AxisDir(Active - GizmoPart.X);
                    var w0 = _c0 - e;
                    float b = Vec3.Dot(a, d), den = 1 - b * b;
                    if (Math.Abs(den) < 1e-4f)
                    {
                        ok = false;
                        return _c0;
                    }

                    return _c0 + a * ((b * Vec3.Dot(d, w0) - Vec3.Dot(a, w0)) / den);
                }
                case GizmoPart.XY: return OnPlane(e, d, AxisDir(2), out ok);
                case GizmoPart.YZ: return OnPlane(e, d, AxisDir(0), out ok);
                case GizmoPart.XZ: return OnPlane(e, d, AxisDir(1), out ok);
                default: return OnPlane(e, d, camera.Forward, out ok);
            }
        }

        Vec3 OnPlane(Vec3 origin, Vec3 dir, Vec3 normal, out bool ok)
        {
            float dn = Vec3.Dot(dir, normal);
            ok = Math.Abs(dn) > 1e-4f;
            if (!ok) return _c0;
            float t = Vec3.Dot(_c0 - origin, normal) / dn;
            if (t < 0)
            {
                ok = false;
                return _c0;
            }

            return origin + dir * t;
        }

        /// <summary>
        ///     The drag so far: a move in world space, or a rotation in world space about the centre the drag started at.
        ///     fine: a tenth of the speed.
        /// </summary>
        public void Delta(ViewCamera camera, Vector2 point, bool fine, out Vec3 move, out Quat rot)
        {
            move = Vec3.Zero;
            rot = Quat.Identity;
            if (Active == GizmoPart.None) return;
            if (Active >= GizmoPart.RX)
            {
                float a = ScreenAngle(camera, point);
                float da = a - _anglePrev;
                if (da > MathF.PI) da -= 2 * MathF.PI;
                else if (da < -MathF.PI) da += 2 * MathF.PI;
                _anglePrev = a;
                _angleAcc += fine ? da * 0.1f : da;
                var axis = Active == GizmoPart.RView ? camera.Forward : AxisDir(Active - GizmoPart.RX);
                float sign = Vec3.Dot(axis, camera.Forward) < 0 ? -1 : 1;
                rot = Quat.AngleAxisRad(_angleAcc * sign, axis);
                return;
            }

            var p = ConstraintPoint(camera, point, out bool ok);
            if (!ok) return;
            move = p - _hit0;
            if (fine) move = move * 0.1f;
        }
    }
}
