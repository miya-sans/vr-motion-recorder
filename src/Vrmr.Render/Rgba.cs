using System;
using Avalonia.Media;

namespace VRMR.Render
{
    /// <summary>A colour with straight (not premultiplied) alpha, 8 bits per channel.</summary>
    public readonly struct Rgba : IEquatable<Rgba>
    {
        public readonly byte R, G, B, A;

        public Rgba(byte r, byte g, byte b, byte a = 255)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        /// <summary>The same colour with another alpha.</summary>
        public Rgba WithAlpha(byte a)
        {
            return new Rgba(R, G, B, a);
        }

        /// <summary>The same colour with its alpha multiplied by k (0..1).</summary>
        public Rgba Fade(float k)
        {
            return new Rgba(R, G, B, (byte)(A * MathX.Clamp01(k) + 0.5f));
        }

        /// <summary>Linear blend from a (t = 0) to b (t = 1), alpha included.</summary>
        public static Rgba Mix(Rgba a, Rgba b, float t)
        {
            t = MathX.Clamp01(t);
            return new Rgba((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t),
                (byte)(a.A + (b.A - a.A) * t));
        }

        public static readonly Rgba Black = new(0, 0, 0);
        public static readonly Rgba White = new(255, 255, 255);
        public static readonly Rgba Transparent = new(0, 0, 0, 0);

        public static implicit operator Rgba(Color c)
        {
            return new Rgba(c.R, c.G, c.B, c.A);
        }

        public bool Equals(Rgba o)
        {
            return R == o.R && G == o.G && B == o.B && A == o.A;
        }

        public override bool Equals(object obj)
        {
            return obj is Rgba o && Equals(o);
        }

        public override int GetHashCode()
        {
            return (R << 24) | (G << 16) | (B << 8) | A;
        }

        public override string ToString()
        {
            return $"#{A:X2}{R:X2}{G:X2}{B:X2}";
        }
    }
}
