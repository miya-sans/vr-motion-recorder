using System;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace VRMR.Render
{
    /// <summary>Decodes avatar textures with SkiaSharp (PNG, JPEG, WebP, BMP, GIF). Passed to the owning mesh loader.</summary>
    public static class SkiaTextureDecoder
    {
        /// <summary>Decodes an image file to 32-bit ARGB with straight alpha, scaled down so that neither side exceeds maxSize.</summary>
        public static TexturePixels Decode(string path, int maxSize)
        {
            using var codec = SKCodec.Create(path) ?? throw new InvalidDataException("not an image this decoder reads");
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var full = SKBitmap.Decode(codec, info) ??
                             throw new InvalidDataException("the image could not be decoded");
            float k = Math.Min(1f, (float)maxSize / Math.Max(info.Width, info.Height));
            int w = Math.Max(1, (int)(info.Width * k)), h = Math.Max(1, (int)(info.Height * k));
            if (w == info.Width && h == info.Height) return ToPixels(full);
            // Both sides unpremultiplied: Skia then filters each channel on its own, so the colour under transparent
            // texels survives (some materials are drawn opaque whatever their alpha says).
            using var small = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            if (!full.ScalePixels(small, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)))
                throw new InvalidDataException("the image could not be scaled");
            return ToPixels(small);
        }

        static TexturePixels ToPixels(SKBitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            int[] px = new int[w * h];
            // BGRA bytes are 0xAARRGGBB as little-endian ints
            IntPtr src = bmp.GetPixels();
            for (int y = 0; y < h; y++) Marshal.Copy(src + y * bmp.RowBytes, px, y * w, w);
            return new TexturePixels { Width = w, Height = h, Argb = px };
        }
    }
}
