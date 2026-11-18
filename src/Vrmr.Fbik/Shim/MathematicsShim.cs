// Unity.Mathematics-compatible shim (float2/3/4, bool3, float3x3, float4x4, quaternion, math): 1.x reference semantics
// (e.g. math.sign(0) == 0, quaternion(float3x3) branch-free conversion, LookRotation via cross products).

using System;
using System.Globalization;

namespace VRMR.Fbik
{
    [Serializable]
    public struct float2
    {
        public float x, y;

        public float2(float x, float y)
        {
            this.x = x;
            this.y = y;
        }

        public static float2 operator +(float2 a, float2 b)
        {
            return new float2(a.x + b.x, a.y + b.y);
        }

        public static float2 operator -(float2 a, float2 b)
        {
            return new float2(a.x - b.x, a.y - b.y);
        }

        public static float2 operator *(float2 a, float b)
        {
            return new float2(a.x * b, a.y * b);
        }
    }

    public struct bool3
    {
        public bool x, y, z;

        public bool3(bool x, bool y, bool z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }
    }

    public struct bool4
    {
        public bool x, y, z, w;

        public bool4(bool x, bool y, bool z, bool w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }
    }

    [Serializable]
    public struct float3 : IEquatable<float3>
    {
        public float x, y, z;

        public float3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public float3(float v)
        {
            x = v;
            y = v;
            z = v;
        }

        public static readonly float3 zero = new(0f, 0f, 0f);

        public static float3 operator +(float3 a, float3 b)
        {
            return new float3(a.x + b.x, a.y + b.y, a.z + b.z);
        }

        public static float3 operator -(float3 a, float3 b)
        {
            return new float3(a.x - b.x, a.y - b.y, a.z - b.z);
        }

        public static float3 operator -(float3 a)
        {
            return new float3(-a.x, -a.y, -a.z);
        }

        public static float3 operator *(float3 a, float3 b)
        {
            return new float3(a.x * b.x, a.y * b.y, a.z * b.z);
        }

        public static float3 operator *(float3 a, float b)
        {
            return new float3(a.x * b, a.y * b, a.z * b);
        }

        public static float3 operator *(float a, float3 b)
        {
            return new float3(a * b.x, a * b.y, a * b.z);
        }

        public static float3 operator /(float3 a, float b)
        {
            return new float3(a.x / b, a.y / b, a.z / b);
        }

        public static float3 operator /(float3 a, float3 b)
        {
            return new float3(a.x / b.x, a.y / b.y, a.z / b.z);
        }

        public static implicit operator float3(Vector3 v)
        {
            return new float3(v.x, v.y, v.z);
        }

        public static implicit operator Vector3(float3 v)
        {
            return new Vector3(v.x, v.y, v.z);
        }

        public bool Equals(float3 o)
        {
            return x == o.x && y == o.y && z == o.z;
        }

        public override bool Equals(object o)
        {
            return o is float3 f && Equals(f);
        }

        public override int GetHashCode()
        {
            return x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2);
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "float3({0}f, {1}f, {2}f)", x, y,
                z);
        }
    }

    [Serializable]
    public struct float4 : IEquatable<float4>
    {
        public float x, y, z, w;

        public float4(float x, float y, float z, float w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        public float4(float3 xyz, float w)
        {
            x = xyz.x;
            y = xyz.y;
            z = xyz.z;
            this.w = w;
        }

        public float4(float v)
        {
            x = v;
            y = v;
            z = v;
            w = v;
        }

        public float3 xyz => new(x, y, z);

        public static float4 operator +(float4 a, float4 b)
        {
            return new float4(a.x + b.x, a.y + b.y, a.z + b.z, a.w + b.w);
        }

        public static float4 operator -(float4 a, float4 b)
        {
            return new float4(a.x - b.x, a.y - b.y, a.z - b.z, a.w - b.w);
        }

        public static float4 operator -(float4 a)
        {
            return new float4(-a.x, -a.y, -a.z, -a.w);
        }

        public static float4 operator *(float4 a, float4 b)
        {
            return new float4(a.x * b.x, a.y * b.y, a.z * b.z, a.w * b.w);
        }

        public static float4 operator *(float4 a, float b)
        {
            return new float4(a.x * b, a.y * b, a.z * b, a.w * b);
        }

        public static float4 operator *(float a, float4 b)
        {
            return new float4(a * b.x, a * b.y, a * b.z, a * b.w);
        }

        public static float4 operator /(float4 a, float b)
        {
            return new float4(a.x / b, a.y / b, a.z / b, a.w / b);
        }

        public bool Equals(float4 o)
        {
            return x == o.x && y == o.y && z == o.z && w == o.w;
        }

        public override bool Equals(object o)
        {
            return o is float4 f && Equals(f);
        }

        public override int GetHashCode()
        {
            return x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2) ^ (w.GetHashCode() >> 1);
        }
    }

    /// <summary>Column-major 3x3 (c0, c1, c2 are columns), as Unity.Mathematics.</summary>
    [Serializable]
    public struct float3x3
    {
        public float3 c0, c1, c2;
        public static readonly float3x3 zero = new(float3.zero, float3.zero, float3.zero);
        public static readonly float3x3 identity = new(new float3(1, 0, 0), new float3(0, 1, 0), new float3(0, 0, 1));

        public float3x3(float3 c0, float3 c1, float3 c2)
        {
            this.c0 = c0;
            this.c1 = c1;
            this.c2 = c2;
        }

        /// <summary>Rotation matrix of a unit quaternion.</summary>
        public float3x3(quaternion q)
        {
            var v = q.value;
            float x2 = v.x + v.x, y2 = v.y + v.y, z2 = v.z + v.z;
            float xx = v.x * x2,
                yy = v.y * y2,
                zz = v.z * z2,
                xy = v.x * y2,
                xz = v.x * z2,
                yz = v.y * z2,
                wx = v.w * x2,
                wy = v.w * y2,
                wz = v.w * z2;
            c0 = new float3(1f - (yy + zz), xy + wz, xz - wy);
            c1 = new float3(xy - wz, 1f - (xx + zz), yz + wx);
            c2 = new float3(xz + wy, yz - wx, 1f - (xx + yy));
        }

        public static float3x3 operator +(float3x3 a, float3x3 b)
        {
            return new float3x3(a.c0 + b.c0, a.c1 + b.c1, a.c2 + b.c2);
        }

        public static float3x3 operator -(float3x3 a, float3x3 b)
        {
            return new float3x3(a.c0 - b.c0, a.c1 - b.c1, a.c2 - b.c2);
        }

        public static float3x3 operator *(float3x3 a, float b)
        {
            return new float3x3(a.c0 * b, a.c1 * b, a.c2 * b);
        }
    }

    /// <summary>Column-major 4x4 (c0..c3 columns), as Unity.Mathematics.</summary>
    [Serializable]
    public struct float4x4
    {
        public float4 c0, c1, c2, c3;

        public float4x4(float4 c0, float4 c1, float4 c2, float4 c3)
        {
            this.c0 = c0;
            this.c1 = c1;
            this.c2 = c2;
            this.c3 = c3;
        }

        public static readonly float4x4 identity = new(new float4(1, 0, 0, 0), new float4(0, 1, 0, 0),
            new float4(0, 0, 1, 0), new float4(0, 0, 0, 1));
    }

    [Serializable]
    public struct quaternion : IEquatable<quaternion>
    {
        public float4 value;
        public static readonly quaternion identity = new(0f, 0f, 0f, 1f);
        public quaternion(float x, float y, float z, float w) { value = new float4(x, y, z, w); }
        public quaternion(float4 value) { this.value = value; }

        /// <summary>Unity.Mathematics quaternion(float3x3): branch-free conversion from an orthonormal matrix (normalized result).</summary>
        public unsafe quaternion(float3x3 m)
        {
            float3 u = m.c0, v = m.c1, w = m.c2;
            uint uBits = *(uint*)&u.x;
            bool uNeg = (uBits & 0x80000000u) != 0;
            float t = v.y + (uNeg ? Neg(w.z) : w.z);
            bool tNeg = (*(uint*)&t & 0x80000000u) != 0;
            float tr = 1.0f + Math.Abs(u.x);
            // sign_flips = (0,1,1,1) ^ (uNeg & (0,1,0,1)) ^ (tNeg & (1,1,1,0))
            bool s0 = false ^ false ^ tNeg;
            bool s1 = true ^ uNeg ^ tNeg;
            bool s2 = true ^ false ^ tNeg;
            bool s3 = true ^ uNeg ^ false;
            var val = new float4(
                tr + (s0 ? Neg(t) : t),
                u.y + (s1 ? Neg(v.x) : v.x),
                w.x + (s2 ? Neg(u.z) : u.z),
                v.z + (s3 ? Neg(w.y) : w.y));
            if (uNeg) val = new float4(val.z, val.w, val.x, val.y);
            if (!tNeg) val = new float4(val.w, val.z, val.y, val.x);
            float rs = 1.0f / (float)Math.Sqrt(val.x * val.x + val.y * val.y + val.z * val.z + val.w * val.w);
            value = val * rs;
        }

        static unsafe float Neg(float f)
        {
            uint b = *(uint*)&f ^ 0x80000000u;
            return *(float*)&b;
        }

        public static quaternion AxisAngle(float3 axis, float angle)
        {
            float s = (float)Math.Sin(0.5f * angle), c = (float)Math.Cos(0.5f * angle);
            return new quaternion(new float4(axis * s, c));
        }

        /// <summary>quaternion.EulerZXY: rotate z, then x, then y (mul(rotY, mul(rotX, rotZ))).</summary>
        public static quaternion EulerZXY(float3 xyz)
        {
            float sx = (float)Math.Sin(0.5f * xyz.x), cx = (float)Math.Cos(0.5f * xyz.x);
            float sy = (float)Math.Sin(0.5f * xyz.y), cy = (float)Math.Cos(0.5f * xyz.y);
            float sz = (float)Math.Sin(0.5f * xyz.z), cz = (float)Math.Cos(0.5f * xyz.z);
            return new quaternion(
                sx * cy * cz + sy * sz * cx,
                sy * cx * cz - sx * sz * cy,
                sz * cx * cy - sx * sy * cz,
                cx * cy * cz + sy * sz * sx);
        }

        public static quaternion EulerZXY(float x, float y, float z)
        {
            return EulerZXY(new float3(x, y, z));
        }

        public static quaternion RotateX(float angle)
        {
            float s = (float)Math.Sin(0.5f * angle), c = (float)Math.Cos(0.5f * angle);
            return new quaternion(s, 0f, 0f, c);
        }

        public static quaternion RotateY(float angle)
        {
            float s = (float)Math.Sin(0.5f * angle), c = (float)Math.Cos(0.5f * angle);
            return new quaternion(0f, s, 0f, c);
        }

        public static quaternion RotateZ(float angle)
        {
            float s = (float)Math.Sin(0.5f * angle), c = (float)Math.Cos(0.5f * angle);
            return new quaternion(0f, 0f, s, c);
        }

        /// <summary>Unity.Mathematics LookRotation: assumes forward normalized and up not parallel.</summary>
        public static quaternion LookRotation(float3 forward, float3 up)
        {
            var t = math.normalize(math.cross(up, forward));
            return new quaternion(new float3x3(t, math.cross(forward, t), forward));
        }

        public static implicit operator quaternion(Quaternion q)
        {
            return new quaternion(q.x, q.y, q.z, q.w);
        }

        public static implicit operator Quaternion(quaternion q)
        {
            return new Quaternion(q.value.x, q.value.y, q.value.z, q.value.w);
        }

        public bool Equals(quaternion o)
        {
            return value.Equals(o.value);
        }

        public override bool Equals(object o)
        {
            return o is quaternion q && Equals(q);
        }

        public override int GetHashCode()
        {
            return value.GetHashCode();
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "quaternion({0}f, {1}f, {2}f, {3}f)", value.x, value.y, value.z, value.w);
        }
    }

    public static class math
    {
        public const float PI = 3.14159265358979323846f;
        public const float EPSILON = 1.1920928955078125e-7f;
        public const float FLT_MIN_NORMAL = 1.175494351e-38F;
        const float TODEGREES = 57.29578f;
        const float TORADIANS = 0.0174532924f;

        // scalar
        public static float abs(float x)
        {
            return Math.Abs(x);
        }

        public static float sqrt(float x)
        {
            return (float)Math.Sqrt(x);
        }

        public static float rsqrt(float x)
        {
            return 1.0f / (float)Math.Sqrt(x);
        }

        public static float rcp(float x)
        {
            return 1.0f / x;
        }

        public static float sin(float x)
        {
            return (float)Math.Sin(x);
        }

        public static float cos(float x)
        {
            return (float)Math.Cos(x);
        }

        public static float tan(float x)
        {
            return (float)Math.Tan(x);
        }

        public static float asin(float x)
        {
            return (float)Math.Asin(x);
        }

        public static float acos(float x)
        {
            return (float)Math.Acos(x);
        }

        public static float atan(float x)
        {
            return (float)Math.Atan(x);
        }

        public static float atan2(float y, float x)
        {
            return (float)Math.Atan2(y, x);
        }

        public static float exp(float x)
        {
            return (float)Math.Exp(x);
        }

        public static float log(float x)
        {
            return (float)Math.Log(x);
        }

        public static float pow(float x, float y)
        {
            return (float)Math.Pow(x, y);
        }

        public static float floor(float x)
        {
            return (float)Math.Floor(x);
        }

        public static float ceil(float x)
        {
            return (float)Math.Ceiling(x);
        }

        public static float degrees(float x)
        {
            return x * TODEGREES;
        }

        public static float radians(float x)
        {
            return x * TORADIANS;
        }

        public static float min(float x, float y)
        {
            return float.IsNaN(y) || x < y ? x : y;
        }

        public static float max(float x, float y)
        {
            return float.IsNaN(y) || x > y ? x : y;
        }

        public static int min(int x, int y)
        {
            return x < y ? x : y;
        }

        public static int max(int x, int y)
        {
            return x > y ? x : y;
        }

        public static float clamp(float x, float a, float b)
        {
            return max(a, min(b, x));
        }

        public static float saturate(float x)
        {
            return clamp(x, 0.0f, 1.0f);
        }

        public static float lerp(float x, float y, float s)
        {
            return x + s * (y - x);
        }

        public static float sign(float x)
        {
            return (x > 0.0f ? 1.0f : 0.0f) - (x < 0.0f ? 1.0f : 0.0f);
        }

        public static bool isfinite(float x)
        {
            return Math.Abs(x) < float.PositiveInfinity;
        }

        public static bool isnan(float x)
        {
            return float.IsNaN(x);
        }

        public static float select(float a, float b, bool c)
        {
            return c ? b : a;
        }

        public static void sincos(float x, out float s, out float c)
        {
            s = (float)Math.Sin(x);
            c = (float)Math.Cos(x);
        }

        // float2
        public static float dot(float2 x, float2 y)
        {
            return x.x * y.x + x.y * y.y;
        }

        public static float lengthsq(float2 x)
        {
            return dot(x, x);
        }

        public static float length(float2 x)
        {
            return sqrt(dot(x, x));
        }

        // float3
        public static float3 radians(float3 x)
        {
            return x * TORADIANS;
        }

        public static float3 degrees(float3 x)
        {
            return x * TODEGREES;
        }

        public static float dot(float3 x, float3 y)
        {
            return x.x * y.x + x.y * y.y + x.z * y.z;
        }

        public static float3 cross(float3 x, float3 y)
        {
            return new float3(x.y * y.z - x.z * y.y, x.z * y.x - x.x * y.z, x.x * y.y - x.y * y.x);
        }

        public static float lengthsq(float3 x)
        {
            return dot(x, x);
        }

        public static float length(float3 x)
        {
            return sqrt(dot(x, x));
        }

        public static float distance(float3 x, float3 y)
        {
            return length(y - x);
        }

        public static float distancesq(float3 x, float3 y)
        {
            return lengthsq(y - x);
        }

        public static float3 normalize(float3 x)
        {
            return rsqrt(dot(x, x)) * x;
        }

        public static float3 normalizesafe(float3 x, float3 defaultvalue = default)
        {
            float len = dot(x, x);
            return len > FLT_MIN_NORMAL ? x * rsqrt(len) : defaultvalue;
        }

        public static float3 lerp(float3 x, float3 y, float s)
        {
            return x + s * (y - x);
        }

        public static float3 abs(float3 x)
        {
            return new float3(Math.Abs(x.x), Math.Abs(x.y), Math.Abs(x.z));
        }

        public static float3 min(float3 x, float3 y)
        {
            return new float3(min(x.x, y.x), min(x.y, y.y), min(x.z, y.z));
        }

        public static float3 max(float3 x, float3 y)
        {
            return new float3(max(x.x, y.x), max(x.y, y.y), max(x.z, y.z));
        }

        public static float3 clamp(float3 x, float3 a, float3 b)
        {
            return max(a, min(b, x));
        }

        public static float3 saturate(float3 x)
        {
            return clamp(x, new float3(0f), new float3(1f));
        }

        public static float cmax(float3 x)
        {
            return max(max(x.x, x.y), x.z);
        }

        public static float cmin(float3 x)
        {
            return min(min(x.x, x.y), x.z);
        }

        public static bool3 isfinite(float3 x)
        {
            return new bool3(isfinite(x.x), isfinite(x.y), isfinite(x.z));
        }

        public static bool all(bool3 x)
        {
            return x.x && x.y && x.z;
        }

        public static bool any(bool3 x)
        {
            return x.x || x.y || x.z;
        }

        public static float3 select(float3 a, float3 b, bool c)
        {
            return c ? b : a;
        }

        // float4
        public static float dot(float4 x, float4 y)
        {
            return x.x * y.x + x.y * y.y + x.z * y.z + x.w * y.w;
        }

        public static float lengthsq(float4 x)
        {
            return dot(x, x);
        }

        public static float length(float4 x)
        {
            return sqrt(dot(x, x));
        }

        public static float4 normalize(float4 x)
        {
            return rsqrt(dot(x, x)) * x;
        }

        public static float4 lerp(float4 x, float4 y, float s)
        {
            return x + s * (y - x);
        }

        public static float4 abs(float4 x)
        {
            return new float4(Math.Abs(x.x), Math.Abs(x.y), Math.Abs(x.z), Math.Abs(x.w));
        }

        public static float cmax(float4 x)
        {
            return max(max(x.x, x.y), max(x.z, x.w));
        }

        public static bool4 isfinite(float4 x)
        {
            return new bool4(isfinite(x.x), isfinite(x.y), isfinite(x.z), isfinite(x.w));
        }

        public static bool all(bool4 x)
        {
            return x.x && x.y && x.z && x.w;
        }

        // float3x3
        public static float3 mul(float3x3 a, float3 b)
        {
            return a.c0 * b.x + a.c1 * b.y + a.c2 * b.z;
        }

        public static float3x3 mul(float3x3 a, float3x3 b)
        {
            return new float3x3(mul(a, b.c0), mul(a, b.c1), mul(a, b.c2));
        }

        public static float3x3 transpose(float3x3 v)
        {
            return new float3x3(new float3(v.c0.x, v.c1.x, v.c2.x), new float3(v.c0.y, v.c1.y, v.c2.y),
                new float3(v.c0.z, v.c1.z, v.c2.z));
        }

        // float4x4
        public static float4 mul(float4x4 a, float4 b)
        {
            return a.c0 * b.x + a.c1 * b.y + a.c2 * b.z + a.c3 * b.w;
        }

        public static float3 transform(float4x4 a, float3 b)
        {
            return (a.c0 * b.x + a.c1 * b.y + a.c2 * b.z + a.c3).xyz;
        }

        public static float3 rotate(float4x4 a, float3 b)
        {
            return (a.c0 * b.x + a.c1 * b.y + a.c2 * b.z).xyz;
        }

        // quaternion
        public static float dot(quaternion a, quaternion b)
        {
            return dot(a.value, b.value);
        }

        public static quaternion conjugate(quaternion q)
        {
            return new quaternion(q.value * new float4(-1.0f, -1.0f, -1.0f, 1.0f));
        }

        public static quaternion inverse(quaternion q)
        {
            var x = q.value;
            return new quaternion(rcp(dot(x, x)) * x * new float4(-1.0f, -1.0f, -1.0f, 1.0f));
        }

        public static quaternion normalize(quaternion q)
        {
            var x = q.value;
            return new quaternion(rsqrt(dot(x, x)) * x);
        }

        public static quaternion normalizesafe(quaternion q)
        {
            var x = q.value;
            float len = dot(x, x);
            return len > FLT_MIN_NORMAL ? new quaternion(x * rsqrt(len)) : quaternion.identity;
        }

        public static float length(quaternion q)
        {
            return sqrt(dot(q.value, q.value));
        }

        public static float lengthsq(quaternion q)
        {
            return dot(q.value, q.value);
        }

        /// <summary>Hamilton product.</summary>
        public static quaternion mul(quaternion a, quaternion b)
        {
            float4 av = a.value, bv = b.value;
            return new quaternion(
                av.w * bv.x + av.x * bv.w + av.y * bv.z - av.z * bv.y,
                av.w * bv.y + av.y * bv.w + av.z * bv.x - av.x * bv.z,
                av.w * bv.z + av.z * bv.w + av.x * bv.y - av.y * bv.x,
                av.w * bv.w - av.x * bv.x - av.y * bv.y - av.z * bv.z);
        }

        public static float3 mul(quaternion q, float3 v)
        {
            var qv = q.value.xyz;
            var t = 2 * cross(qv, v);
            return v + q.value.w * t + cross(qv, t);
        }

        public static float3 rotate(quaternion q, float3 v)
        {
            return mul(q, v);
        }

        public static quaternion nlerp(quaternion q1, quaternion q2, float t)
        {
            float dt = dot(q1, q2);
            var q2v = dt < 0f ? -q2.value : q2.value;
            return normalize(new quaternion(q1.value + t * (q2v - q1.value)));
        }

        /// <summary>Unity.Mathematics slerp: shortest arc, nlerp above cos 0.9995.</summary>
        public static quaternion slerp(quaternion q1, quaternion q2, float t)
        {
            float dt = dot(q1, q2);
            if (dt < 0.0f)
            {
                dt = -dt;
                q2.value = -q2.value;
            }

            if (dt < 0.9995f)
            {
                float angle = acos(dt);
                float s = rsqrt(1.0f - dt * dt);
                float w1 = sin(angle * (1.0f - t)) * s;
                float w2 = sin(angle * t) * s;
                return new quaternion(q1.value * w1 + q2.value * w2);
            }

            return nlerp(q1, q2, t);
        }
    }
}
