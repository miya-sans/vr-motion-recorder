using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using SkiaSharp;

namespace VRMR.Render
{
    /// <summary>Where the anchor point of a text sits: the top-left, top-centre or top-right of the block.</summary>
    public enum TextAlign { Left, Center, Right }
}

namespace VRMR.Render.Gl
{
    /// <summary>
    /// Text for the 3D view: glyphs are rasterised once per size in device pixels (so they are sharp at any display
    /// scaling), kept in one 8-bit atlas texture, and drawn as pixel-aligned quads. Laying out a string that was drawn
    /// before allocates nothing.
    /// </summary>
    internal sealed class GlyphAtlas : IDisposable
    {
        public const int Size = 1024;

        struct Glyph
        {
            public short X, Y, W, H;
            /// <summary>From the pen position on the baseline to the bitmap's top-left, in pixels.</summary>
            public float Left, Top;
            public float Advance;
        }

        struct SizeInfo { public float Ascent, LineHeight, DigitAdvance; }

        /// <summary>The atlas pixels (coverage), <see cref="Size"/> squared.</summary>
        public readonly byte[] Pixels = new byte[Size * Size];
        /// <summary>True when <see cref="Pixels"/> changed since the renderer last uploaded them.</summary>
        public bool Dirty = true;

        readonly Dictionary<long, Glyph> _glyphs = new Dictionary<long, Glyph>();
        readonly Dictionary<int, SizeInfo> _sizes = new Dictionary<int, SizeInfo>();
        readonly Dictionary<int, SKTypeface> _fallback = new Dictionary<int, SKTypeface>();
        readonly SKPaint _paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        SKTypeface _face = SKTypeface.Default;
        string _family;
        SKBitmap _scratch;
        SKCanvas _canvas;
        int _penX = 1, _penY = 1, _rowHeight;
        bool _full, _resetLastFrame;

        /// <summary>Selects the font family; an unknown family falls back to the system's default.</summary>
        public void SetFamily(string family)
        {
            if (family == _family) return;
            _family = family;
            _face = ResolveFamily(family);
            Reset();
        }

        static SKTypeface ResolveFamily(string family)
        {
            if (string.IsNullOrEmpty(family)) return SKTypeface.Default;
            try
            {
                var installed = SKFontManager.Default.MatchFamily(family);
                if (installed != null) return installed;
                // a font the app embeds the Avalonia way (for example Avalonia.Fonts.Inter)
                var assets = new Uri("avares://Avalonia.Fonts." + family + "/Assets");
                Uri pick = null;
                foreach (var uri in Avalonia.Platform.AssetLoader.GetAssets(assets, null))
                {
                    string path = uri.AbsolutePath;
                    if (!path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)) continue;
                    if (pick == null || path.Contains("Regular", StringComparison.OrdinalIgnoreCase)) pick = uri;
                }
                if (pick != null)
                    using (var stream = Avalonia.Platform.AssetLoader.Open(pick, null))
                        return SKTypeface.FromStream(stream) ?? SKTypeface.Default;
            }
            catch (Exception ex) { AppLog.Warn("3D view font '" + family + "': " + ex.Message); }
            return SKTypeface.Default;
        }

        /// <summary>
        /// Call at the start of a frame. Returns true when the atlas ran full during the last one and was emptied: the
        /// glyphs come back as they are used, and the caller should draw once more.
        /// </summary>
        public bool BeginFrame()
        {
            bool reset = _full && !_resetLastFrame;
            if (reset) Reset();
            _resetLastFrame = reset;
            return reset;
        }

        void Reset()
        {
            _glyphs.Clear(); _sizes.Clear();
            Array.Clear(Pixels);
            _penX = 1; _penY = 1; _rowHeight = 0; _full = false; Dirty = true;
        }

        static int SizeKey(float pixels) => Math.Max(8, (int)MathF.Round(pixels * 2f)); // half-pixel steps, 4 px at least

        SKTypeface FaceFor(int codepoint)
        {
            if (_face.GetGlyph(codepoint) != 0) return _face;
            if (_fallback.TryGetValue(codepoint, out var f)) return f;
            f = SKFontManager.Default.MatchCharacter(codepoint) ?? _face;
            _fallback[codepoint] = f;
            return f;
        }

        SizeInfo InfoFor(int sizeKey)
        {
            if (_sizes.TryGetValue(sizeKey, out var info)) return info;
            using (var font = NewFont(_face, sizeKey))
            {
                var m = font.Metrics;
                info.Ascent = MathF.Round(-m.Ascent);
                info.LineHeight = MathF.Ceiling(m.Descent - m.Ascent + m.Leading);
                Span<ushort> ids = stackalloc ushort[10];
                Span<float> adv = stackalloc float[10];
                for (int d = 0; d < 10; d++) ids[d] = font.GetGlyph('0' + d);
                font.GetGlyphWidths(ids, adv, Span<SKRect>.Empty, _paint);
                foreach (float a in adv) info.DigitAdvance = Math.Max(info.DigitAdvance, a);
            }
            _sizes[sizeKey] = info;
            return info;
        }

        static SKFont NewFont(SKTypeface face, int sizeKey) =>
            new SKFont(face, sizeKey * 0.5f) { Edging = SKFontEdging.Antialias, Hinting = SKFontHinting.Normal, Subpixel = false };

        Glyph GlyphFor(int codepoint, int sizeKey)
        {
            long key = ((long)sizeKey << 32) | (uint)codepoint;
            if (_glyphs.TryGetValue(key, out var g)) return g;
            g = Rasterise(codepoint, sizeKey, out bool placed);
            if (placed) _glyphs[key] = g;
            return g;
        }

        Glyph Rasterise(int codepoint, int sizeKey, out bool placed)
        {
            placed = true;
            using var font = NewFont(FaceFor(codepoint), sizeKey);
            Span<ushort> id = stackalloc ushort[1];
            Span<float> adv = stackalloc float[1];
            Span<SKRect> box = stackalloc SKRect[1];
            id[0] = font.GetGlyph(codepoint);
            font.GetGlyphWidths(id, adv, box, _paint);
            var g = new Glyph { Advance = adv[0] };
            if (box[0].Width <= 0 || box[0].Height <= 0) return g; // a space
            int left = (int)MathF.Floor(box[0].Left) - 1, top = (int)MathF.Floor(box[0].Top) - 1;
            int w = (int)MathF.Ceiling(box[0].Right) + 1 - left, h = (int)MathF.Ceiling(box[0].Bottom) + 1 - top;
            if (_penX + w + 1 > Size) { _penX = 1; _penY += _rowHeight + 1; _rowHeight = 0; }
            if (w + 2 > Size || _penY + h + 1 > Size) { _full = true; placed = false; return g; }

            if (_scratch == null || _scratch.Width < w || _scratch.Height < h)
            {
                _canvas?.Dispose(); _scratch?.Dispose();
                _scratch = new SKBitmap(new SKImageInfo(Math.Max(64, w), Math.Max(64, h), SKColorType.Alpha8, SKAlphaType.Premul));
                _canvas = new SKCanvas(_scratch);
            }
            _canvas.Clear(SKColors.Transparent);
            _canvas.DrawText(char.ConvertFromUtf32(codepoint), -left, -top, SKTextAlign.Left, font, _paint);
            var src = _scratch.GetPixelSpan();
            int stride = _scratch.RowBytes;
            for (int y = 0; y < h; y++) src.Slice(y * stride, w).CopyTo(Pixels.AsSpan((_penY + y) * Size + _penX, w));

            g.X = (short)_penX; g.Y = (short)_penY; g.W = (short)w; g.H = (short)h; g.Left = left; g.Top = top;
            _penX += w + 1; _rowHeight = Math.Max(_rowHeight, h);
            Dirty = true;
            return g;
        }

        static bool NextRune(ReadOnlySpan<char> text, ref int i, out int codepoint)
        {
            if (i >= text.Length) { codepoint = 0; return false; }
            if (Rune.DecodeFromUtf16(text.Slice(i), out Rune r, out int used) != System.Buffers.OperationStatus.Done) { r = Rune.ReplacementChar; used = 1; }
            i += used; codepoint = r.Value;
            return true;
        }

        float LineWidth(ReadOnlySpan<char> line, int sizeKey, bool tabular, float digitAdvance)
        {
            float w = 0;
            for (int i = 0; NextRune(line, ref i, out int cp);)
                w += tabular && cp >= '0' && cp <= '9' ? digitAdvance : GlyphFor(cp, sizeKey).Advance;
            return w;
        }

        /// <summary>Size of a text block in device-independent pixels.</summary>
        public Vector2 Measure(ReadOnlySpan<char> text, float size, float scale, bool tabular = false)
        {
            int sizeKey = SizeKey(size * scale);
            var info = InfoFor(sizeKey);
            float width = 0; int lines = 0;
            for (int start = 0; start <= text.Length;)
            {
                int n = text.Slice(start).IndexOf('\n');
                int end = n < 0 ? text.Length : start + n;
                width = Math.Max(width, LineWidth(text.Slice(start, end - start), sizeKey, tabular, info.DigitAdvance));
                lines++; start = end + 1;
            }
            return new Vector2(width, lines * info.LineHeight) / scale;
        }

        /// <summary>
        /// Adds a text block to the batch. at is the block's top-left, top-centre or top-right in device-independent
        /// pixels; size its font size. tabular gives every digit the same width, so counters do not jitter.
        /// </summary>
        public void Draw(VectorBatch batch, ReadOnlySpan<char> text, Vector2 at, float size, Rgba color, TextAlign align = TextAlign.Left, bool tabular = false)
        {
            if (color.A == 0 || text.Length == 0) return;
            float scale = batch.Scale;
            int sizeKey = SizeKey(size * scale);
            var info = InfoFor(sizeKey);
            float y = MathF.Round(at.Y * scale);
            for (int start = 0; start <= text.Length;)
            {
                int n = text.Slice(start).IndexOf('\n');
                int end = n < 0 ? text.Length : start + n;
                var line = text.Slice(start, end - start);
                float x = at.X * scale;
                if (align != TextAlign.Left)
                {
                    float w = LineWidth(line, sizeKey, tabular, info.DigitAdvance);
                    x -= align == TextAlign.Center ? w * 0.5f : w;
                }
                x = MathF.Round(x);
                float baseline = y + info.Ascent;
                for (int i = 0; NextRune(line, ref i, out int cp);)
                {
                    var g = GlyphFor(cp, sizeKey);
                    float advance = g.Advance, shift = 0;
                    if (tabular && cp >= '0' && cp <= '9') { shift = (info.DigitAdvance - advance) * 0.5f; advance = info.DigitAdvance; }
                    if (g.W > 0) batch.Glyph(MathF.Round(x + shift) + g.Left, baseline + g.Top, g.X, g.Y, g.W, g.H, color);
                    x += advance;
                }
                y += info.LineHeight; start = end + 1;
            }
        }

        public void Dispose()
        {
            _canvas?.Dispose(); _scratch?.Dispose(); _paint.Dispose();
            _canvas = null; _scratch = null;
        }
    }
}
