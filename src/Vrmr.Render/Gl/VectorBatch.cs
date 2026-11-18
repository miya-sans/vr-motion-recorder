using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace VRMR.Render
{
    /// <summary>How a line or ring is broken up.</summary>
    public enum LineDash { Solid, Dash, Dot }
}

namespace VRMR.Render.Gl
{
    /// <summary>One vertex of a 2D shape; the fragment shader works out coverage from the local coordinates. 40 bytes.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct VectorVertex
    {
        /// <summary>Position in device pixels from the top-left.</summary>
        public float X, Y;
        /// <summary>Shape-local coordinates in device pixels (text: atlas texels).</summary>
        public float U, V;
        public float P0, P1, P2, P3;
        public float Kind;
        public uint Color;
        public const int Size = 40;
    }

    /// <summary>
    /// The 2D shapes of one frame (lines, rings, discs, polygons, rounded rectangles, glyphs) as triangles for one
    /// draw call, anti-aliased in the fragment shader. Callers give device-independent pixels; the batch scales to
    /// device pixels. Reused every frame, so it allocates only while it grows.
    /// </summary>
    internal sealed class VectorBatch
    {
        public const float KindLine = 0, KindRing = 1, KindDisc = 2, KindSolid = 3, KindGlyph = 4, KindRect = 5;

        VectorVertex[] _v = new VectorVertex[6 * 2048];
        int _n;

        /// <summary>Device pixels per device-independent pixel.</summary>
        public float Scale = 1f;

        public int Count => _n;
        public ReadOnlySpan<VectorVertex> Vertices => _v.AsSpan(0, _n);
        public void Clear() => _n = 0;

        static uint Pack(Rgba c, float alphaScale = 1f)
        {
            uint a = alphaScale >= 1f ? c.A : (uint)(c.A * alphaScale + 0.5f);
            return c.R | ((uint)c.G << 8) | ((uint)c.B << 16) | (a << 24);
        }

        Span<VectorVertex> Take(int count)
        {
            if (_n + count > _v.Length) Array.Resize(ref _v, Math.Max(_v.Length * 2, _n + count));
            var s = _v.AsSpan(_n, count);
            _n += count;
            return s;
        }

        /// <summary>Two triangles over the corners a, b, c, d (in order around the quad), with their local coordinates.</summary>
        void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Vector2 la, Vector2 lb, Vector2 lc, Vector2 ld, float kind, float p0, float p1, float p2, float p3, uint color)
        {
            var s = Take(6);
            var va = new VectorVertex { X = a.X, Y = a.Y, U = la.X, V = la.Y, P0 = p0, P1 = p1, P2 = p2, P3 = p3, Kind = kind, Color = color };
            var vb = va; vb.X = b.X; vb.Y = b.Y; vb.U = lb.X; vb.V = lb.Y;
            var vc = va; vc.X = c.X; vc.Y = c.Y; vc.U = lc.X; vc.V = lc.Y;
            var vd = va; vd.X = d.X; vd.Y = d.Y; vd.U = ld.X; vd.V = ld.Y;
            s[0] = va; s[1] = vb; s[2] = vc; s[3] = va; s[4] = vc; s[5] = vd;
        }

        /// <summary>An axis-aligned quad centred on c (device pixels) whose local coordinates are the offset from c.</summary>
        void CenteredQuad(Vector2 c, float hx, float hy, float kind, float p0, float p1, float p2, float p3, uint color)
        {
            Quad(new Vector2(c.X - hx, c.Y - hy), new Vector2(c.X + hx, c.Y - hy), new Vector2(c.X + hx, c.Y + hy), new Vector2(c.X - hx, c.Y + hy),
                new Vector2(-hx, -hy), new Vector2(hx, -hy), new Vector2(hx, hy), new Vector2(-hx, hy), kind, p0, p1, p2, p3, color);
        }

        static void DashPattern(LineDash dash, float widthPx, out float on, out float period)
        {
            float unit = Math.Max(1f, widthPx);
            if (dash == LineDash.Dash) { on = unit * 3; period = unit * 4; }
            else if (dash == LineDash.Dot) { on = unit; period = unit * 2; }
            else { on = 0; period = 0; }
        }

        /// <summary>A line with round caps.</summary>
        public void Line(Vector2 a, Vector2 b, Rgba color, float width, LineDash dash = LineDash.Solid)
        {
            if (color.A == 0) return;
            a *= Scale; b *= Scale;
            float w = width * Scale, alpha = 1f;
            if (w < 1f) { alpha = Math.Max(0f, w); w = 1f; } // thinner than a pixel: one pixel wide, fainter
            float hw = w * 0.5f, pad = hw + 1f;
            var d = b - a;
            float len = d.Length();
            d = len > 1e-4f ? d / len : new Vector2(1, 0);
            var n = new Vector2(-d.Y, d.X);
            DashPattern(dash, w, out float on, out float period);
            Vector2 a0 = a - d * pad, b0 = b + d * pad;
            Quad(a0 - n * pad, b0 - n * pad, b0 + n * pad, a0 + n * pad,
                new Vector2(-pad, -pad), new Vector2(len + pad, -pad), new Vector2(len + pad, pad), new Vector2(-pad, pad),
                KindLine, len, hw, on, period, Pack(color, alpha));
        }

        /// <summary>A circle outline.</summary>
        public void Ring(Vector2 center, float radius, Rgba color, float width, LineDash dash = LineDash.Solid)
        {
            if (color.A == 0 || radius <= 0) return;
            center *= Scale;
            float r = radius * Scale, w = Math.Max(1f, width * Scale), hw = w * 0.5f, ext = r + hw + 1f;
            DashPattern(dash, w, out float on, out float period);
            if (period > 0)
            {
                // a whole number of dashes around, so there is no seam
                float around = 2f * MathF.PI * r, count = Math.Max(1f, MathF.Round(around / period)), k = around / count / period;
                on *= k; period *= k;
            }
            CenteredQuad(center, ext, ext, KindRing, r, hw, on, period, Pack(color));
        }

        /// <summary>A filled circle.</summary>
        public void Disc(Vector2 center, float radius, Rgba color)
        {
            if (color.A == 0 || radius <= 0) return;
            float r = radius * Scale, ext = r + 1f;
            CenteredQuad(center * Scale, ext, ext, KindDisc, r, 0, 0, 0, Pack(color));
        }

        /// <summary>A filled convex polygon.</summary>
        public void Polygon(ReadOnlySpan<Vector2> points, Rgba color)
        {
            if (color.A == 0 || points.Length < 3) return;
            uint c = Pack(color);
            var s = Take((points.Length - 2) * 3);
            var v0 = new VectorVertex { X = points[0].X * Scale, Y = points[0].Y * Scale, Kind = KindSolid, Color = c };
            for (int i = 1, o = 0; i + 1 < points.Length; i++)
            {
                var v1 = v0; v1.X = points[i].X * Scale; v1.Y = points[i].Y * Scale;
                var v2 = v0; v2.X = points[i + 1].X * Scale; v2.Y = points[i + 1].Y * Scale;
                s[o++] = v0; s[o++] = v1; s[o++] = v2;
            }
        }

        /// <summary>A rounded rectangle: filled, outlined, or both (a transparent colour skips that part).</summary>
        public void RoundRect(Vector2 min, Vector2 max, float radius, Rgba fill, Rgba stroke, float strokeWidth = 1f)
        {
            min *= Scale; max *= Scale;
            var c = (min + max) * 0.5f;
            float hx = (max.X - min.X) * 0.5f, hy = (max.Y - min.Y) * 0.5f;
            if (hx <= 0 || hy <= 0) return;
            float r = Math.Min(radius * Scale, Math.Min(hx, hy));
            if (fill.A != 0) CenteredQuad(c, hx + 1f, hy + 1f, KindRect, hx, hy, r, 0, Pack(fill));
            if (stroke.A != 0)
            {
                float hw = Math.Max(1f, strokeWidth * Scale) * 0.5f;
                CenteredQuad(c, hx + hw + 1f, hy + hw + 1f, KindRect, hx, hy, r, hw, Pack(stroke));
            }
        }

        /// <summary>One glyph from the atlas: x, y is its top-left in device pixels, atlasX, atlasY its place in the atlas.</summary>
        public void Glyph(float x, float y, int atlasX, int atlasY, int w, int h, Rgba color)
        {
            Quad(new Vector2(x, y), new Vector2(x + w, y), new Vector2(x + w, y + h), new Vector2(x, y + h),
                new Vector2(atlasX, atlasY), new Vector2(atlasX + w, atlasY), new Vector2(atlasX + w, atlasY + h), new Vector2(atlasX, atlasY + h),
                KindGlyph, 0, 0, 0, 0, Pack(color));
        }
    }
}
