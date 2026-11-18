using System;
using System.Numerics;

namespace VRMR.Render
{
    /// <summary>Screen-space hit tests for tools. Points and distances are in device-independent pixels.</summary>
    public static class HitTest
    {
        /// <summary>Distance from p to the segment a-b.</summary>
        public static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var d = b - a;
            float l2 = d.LengthSquared();
            float t = l2 < 1e-6f ? 0 : MathX.Clamp01(Vector2.Dot(p - a, d) / l2);
            return Vector2.Distance(a + d * t, p);
        }

        /// <summary>Distance from p to the nearest segment of an open polyline; float.MaxValue for fewer than two points.</summary>
        public static float DistanceToPolyline(Vector2 p, ReadOnlySpan<Vector2> points)
        {
            float best = float.MaxValue;
            for (int i = 1; i < points.Length; i++)
                best = Math.Min(best, DistanceToSegment(p, points[i - 1], points[i]));
            return best;
        }

        /// <summary>True when p is inside the polygon (even-odd rule).</summary>
        public static bool InsidePolygon(Vector2 p, ReadOnlySpan<Vector2> polygon)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
                if (polygon[i].Y > p.Y != polygon[j].Y > p.Y && p.X <
                    (polygon[j].X - polygon[i].X) * (p.Y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X)
                    inside = !inside;
            return inside;
        }

        /// <summary>Index of the point nearest to p within maxDistance, or -1.</summary>
        public static int NearestPoint(Vector2 p, ReadOnlySpan<Vector2> points, float maxDistance)
        {
            int best = -1;
            float bestD = maxDistance * maxDistance;
            for (int i = 0; i < points.Length; i++)
            {
                float d = Vector2.DistanceSquared(p, points[i]);
                if (d <= bestD)
                {
                    bestD = d;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>
        ///     The outline of a world-space circle (radius in metres, around an axis) as screen points: the first point is
        ///     repeated at the end, so the span is a closed polyline. False when part of it is behind the camera.
        /// </summary>
        public static bool WorldCircle(ViewCamera camera, Vec3 center, Vec3 axis, float radius, Span<Vector2> points)
        {
            var u = Vec3.Cross(axis, Math.Abs(axis.y) < 0.9f ? Vec3.Up : Vec3.Right).Normalized;
            var w = Vec3.Cross(axis, u).Normalized;
            int n = points.Length - 1;
            for (int i = 0; i <= n; i++)
            {
                double a = i * Math.PI * 2 / n;
                if (!camera.Project(center + (u * (float)Math.Cos(a) + w * (float)Math.Sin(a)) * radius,
                        out points[i])) return false;
            }

            return true;
        }
    }
}
