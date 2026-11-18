using System;
using System.Numerics;
using VRMR.Render.Gl;

namespace VRMR.Render
{
    /// <summary>
    /// What tools draw over the scene: lines, circles, polygons and text, batched into the frame's GPU pass in the
    /// order they were added. Positions are world points (<see cref="Vec3"/>, projected with the view's camera) or
    /// screen points (<see cref="Vector2"/>, device-independent pixels from the top-left); sizes are in
    /// device-independent pixels. Only valid inside <see cref="IViewportTool.Draw"/> and the view's DrawOverlay event.
    /// </summary>
    public sealed class OverlayList
    {
        VectorBatch _batch;
        GlyphAtlas _atlas;
        ViewCamera _camera;
        Vector2[] _ring = new Vector2[65];

        internal void Bind(VectorBatch batch, GlyphAtlas atlas, ViewCamera camera) { _batch = batch; _atlas = atlas; _camera = camera; }

        /// <summary>The camera of the frame being drawn.</summary>
        public ViewCamera Camera => _camera;

        /// <summary>A line with round caps between two screen points.</summary>
        public void Line(Vector2 a, Vector2 b, Rgba color, float width = 1f, LineDash dash = LineDash.Solid) => _batch.Line(a, b, color, width, dash);

        /// <summary>A line between two world points, cut where it passes behind the camera.</summary>
        public void Line(Vec3 a, Vec3 b, Rgba color, float width = 1f, LineDash dash = LineDash.Solid)
        {
            if (_camera.Segment(a, b, out var pa, out var pb, out _)) _batch.Line(pa, pb, color, width, dash);
        }

        /// <summary>Connected lines through screen points.</summary>
        public void Polyline(ReadOnlySpan<Vector2> points, Rgba color, float width = 1f, bool closed = false)
        {
            for (int i = 1; i < points.Length; i++) _batch.Line(points[i - 1], points[i], color, width);
            if (closed && points.Length > 2) _batch.Line(points[points.Length - 1], points[0], color, width);
        }

        /// <summary>A ring around a screen point; the radius is in pixels.</summary>
        public void Circle(Vector2 center, float radius, Rgba color, float width = 1f, LineDash dash = LineDash.Solid) => _batch.Ring(center, radius, color, width, dash);

        /// <summary>A ring around a world point; the radius is in pixels.</summary>
        public void Circle(Vec3 center, float radius, Rgba color, float width = 1f, LineDash dash = LineDash.Solid)
        {
            if (_camera.Project(center, out var c)) _batch.Ring(c, radius, color, width, dash);
        }

        /// <summary>A circle in the world (radius in metres) around an axis, as the camera sees it. Returns false when part of it is behind the camera.</summary>
        public bool WorldCircle(Vec3 center, Vec3 axis, float radius, Rgba color, float width = 1f)
        {
            if (!HitTest.WorldCircle(_camera, center, axis, radius, _ring)) return false;
            Polyline(_ring, color, width);
            return true;
        }

        /// <summary>A filled circle around a screen point.</summary>
        public void Disc(Vector2 center, float radius, Rgba fill) => _batch.Disc(center, radius, fill);

        /// <summary>A filled circle around a world point; the radius is in pixels.</summary>
        public void Disc(Vec3 center, float radius, Rgba fill)
        {
            if (_camera.Project(center, out var c)) _batch.Disc(c, radius, fill);
        }

        /// <summary>A filled convex polygon through screen points.</summary>
        public void Polygon(ReadOnlySpan<Vector2> points, Rgba fill) => _batch.Polygon(points, fill);

        /// <summary>A rectangle with rounded corners: filled, outlined, or both (a transparent colour leaves that part out).</summary>
        public void Rect(Avalonia.Rect rect, float radius, Rgba fill, Rgba stroke, float strokeWidth = 1f) =>
            _batch.RoundRect(new Vector2((float)rect.X, (float)rect.Y), new Vector2((float)rect.Right, (float)rect.Bottom), radius, fill, stroke, strokeWidth);

        /// <summary>Text with its top-left (or top-centre, top-right) at a screen point. A line feed starts a new line.</summary>
        public void Text(Vector2 at, ReadOnlySpan<char> text, Rgba color, float size = 11f, TextAlign align = TextAlign.Left) => _atlas.Draw(_batch, text, at, size, color, align);

        /// <summary>Text anchored at a world point, moved by an offset in pixels.</summary>
        public void Text(Vec3 at, Vector2 offset, ReadOnlySpan<char> text, Rgba color, float size = 11f, TextAlign align = TextAlign.Left)
        {
            if (_camera.Project(at, out var s)) _atlas.Draw(_batch, text, s + offset, size, color, align);
        }

        /// <summary>Width and height a text takes.</summary>
        public Vector2 MeasureText(ReadOnlySpan<char> text, float size = 11f) => _atlas.Measure(text, size, _batch.Scale);

        /// <summary>A line with an arrow head at b.</summary>
        public void Arrow(Vector2 a, Vector2 b, Rgba color, float width = 1f)
        {
            var d = b - a;
            float len = d.Length();
            if (len < 1e-3f) return;
            d /= len;
            float head = Math.Min(len, width * 5f), half = width * 2f;
            var neck = b - d * head;
            var n = new Vector2(-d.Y, d.X);
            _batch.Line(a, neck, color, width);
            Span<Vector2> tri = stackalloc Vector2[3] { b, neck + n * half, neck - n * half };
            _batch.Polygon(tri, color);
        }

        /// <summary>An arrow between two world points.</summary>
        public void Arrow(Vec3 a, Vec3 b, Rgba color, float width = 1f)
        {
            if (_camera.Project(a, out var pa) && _camera.Project(b, out var pb)) Arrow(pa, pb, color, width);
        }
    }
}
