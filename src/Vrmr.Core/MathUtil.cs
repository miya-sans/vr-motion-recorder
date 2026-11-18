using System;

namespace VRMR
{
    /// <summary>3D vector (Unity convention when used for scene data: left-handed, Y up, +Z forward).</summary>
    public struct Vec3
    {
        public float x, y, z;
        public Vec3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);
        public static readonly Vec3 One = new Vec3(1, 1, 1);
        public static readonly Vec3 Up = new Vec3(0, 1, 0);
        public static readonly Vec3 Down = new Vec3(0, -1, 0);
        public static readonly Vec3 Forward = new Vec3(0, 0, 1);
        public static readonly Vec3 Back = new Vec3(0, 0, -1);
        public static readonly Vec3 Right = new Vec3(1, 0, 0);
        public static readonly Vec3 Left = new Vec3(-1, 0, 0);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.x, -a.y, -a.z);
        public static Vec3 operator *(Vec3 a, float s) => new Vec3(a.x * s, a.y * s, a.z * s);
        public static Vec3 operator *(float s, Vec3 a) => new Vec3(a.x * s, a.y * s, a.z * s);
        public static Vec3 operator /(Vec3 a, float s) => new Vec3(a.x / s, a.y / s, a.z / s);

        public float Length => (float)Math.Sqrt(x * x + y * y + z * z);
        public float SqrLength => x * x + y * y + z * z;
        public Vec3 Normalized { get { float l = Length; return l > 1e-9f ? this / l : Zero; } }
        public static float Dot(Vec3 a, Vec3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        public static Vec3 Lerp(Vec3 a, Vec3 b, float t) => new Vec3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);
        public static float Distance(Vec3 a, Vec3 b) => (a - b).Length;
        public static Vec3 Scale(Vec3 a, Vec3 b) => new Vec3(a.x * b.x, a.y * b.y, a.z * b.z);
        public Vec3 WithY(float ny) => new Vec3(x, ny, z);
        public static Vec3 ProjectOnPlane(Vec3 v, Vec3 n) => v - n * Dot(v, n);
        public static float Angle(Vec3 a, Vec3 b)
        {
            float d = Dot(a.Normalized, b.Normalized);
            return (float)(Math.Acos(Math.Max(-1f, Math.Min(1f, d))) * 180.0 / Math.PI);
        }
        public bool IsFinite => !(float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z) || float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z));
        public override string ToString() => $"({x:F4}, {y:F4}, {z:F4})";
    }

    /// <summary>Quaternion (x,y,z,w), Hamilton product, same convention as UnityEngine.Quaternion.</summary>
    public struct Quat
    {
        public float x, y, z, w;
        public Quat(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static readonly Quat Identity = new Quat(0, 0, 0, 1);

        public static Quat operator *(Quat a, Quat b) => new Quat(
            a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
            a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
            a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);

        public static Vec3 operator *(Quat q, Vec3 v)
        {
            float tx = 2f * (q.y * v.z - q.z * v.y);
            float ty = 2f * (q.z * v.x - q.x * v.z);
            float tz = 2f * (q.x * v.y - q.y * v.x);
            return new Vec3(
                v.x + q.w * tx + (q.y * tz - q.z * ty),
                v.y + q.w * ty + (q.z * tx - q.x * tz),
                v.z + q.w * tz + (q.x * ty - q.y * tx));
        }

        public Quat Conjugate => new Quat(-x, -y, -z, w);
        public Quat Inverse => Conjugate; // assumes unit
        public Quat Negated => new Quat(-x, -y, -z, -w);
        public float Norm => (float)Math.Sqrt(x * x + y * y + z * z + w * w);
        public Quat Normalized
        {
            get
            {
                float n = Norm;
                if (n < 1e-9f || float.IsNaN(n)) return Identity;
                return new Quat(x / n, y / n, z / n, w / n);
            }
        }
        public bool IsFinite => !(float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z) || float.IsNaN(w));
        public static float Dot(Quat a, Quat b) => a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;

        public static Quat AngleAxis(float degrees, Vec3 axis)
        {
            axis = axis.Normalized;
            double h = degrees * Math.PI / 360.0;
            float s = (float)Math.Sin(h);
            return new Quat(axis.x * s, axis.y * s, axis.z * s, (float)Math.Cos(h));
        }

        public static Quat AngleAxisRad(float rad, Vec3 axis)
        {
            axis = axis.Normalized;
            float s = (float)Math.Sin(rad * 0.5);
            return new Quat(axis.x * s, axis.y * s, axis.z * s, (float)Math.Cos(rad * 0.5));
        }

        public void ToAngleAxis(out float degrees, out Vec3 axis)
        {
            Quat q = w < 0 ? Negated : this;
            float s = (float)Math.Sqrt(Math.Max(0, 1 - q.w * q.w));
            degrees = (float)(2 * Math.Acos(Math.Min(1f, q.w)) * 180 / Math.PI);
            axis = s < 1e-6f ? Vec3.Up : new Vec3(q.x / s, q.y / s, q.z / s);
        }

        /// <summary>Shortest rotation taking direction a onto direction b.</summary>
        public static Quat FromToRotation(Vec3 a, Vec3 b)
        {
            a = a.Normalized; b = b.Normalized;
            float d = Vec3.Dot(a, b);
            if (d >= 1f - 1e-7f) return Identity;
            if (d <= -1f + 1e-7f)
            {
                Vec3 ax = Vec3.Cross(Vec3.Right, a);
                if (ax.SqrLength < 1e-6f) ax = Vec3.Cross(Vec3.Up, a);
                return AngleAxis(180f, ax);
            }
            Vec3 c = Vec3.Cross(a, b);
            return new Quat(c.x, c.y, c.z, 1f + d).Normalized;
        }

        /// <summary>Unity's Quaternion.LookRotation (forward = +Z, up = +Y).</summary>
        public static Quat LookRotation(Vec3 forward, Vec3 up)
        {
            Vec3 f = forward.Normalized;
            if (f.SqrLength < 1e-12f) return Identity;
            Vec3 r = Vec3.Cross(up, f);
            if (r.SqrLength < 1e-12f) r = Vec3.Cross(Math.Abs(f.y) < 0.9f ? Vec3.Up : Vec3.Forward, f);
            r = r.Normalized;
            Vec3 u = Vec3.Cross(f, r);
            return FromBasis(r, u, f);
        }

        /// <summary>Quaternion from orthonormal basis columns (X axis, Y axis, Z axis).</summary>
        public static Quat FromBasis(Vec3 xa, Vec3 ya, Vec3 za)
        {
            float m00 = xa.x, m01 = ya.x, m02 = za.x;
            float m10 = xa.y, m11 = ya.y, m12 = za.y;
            float m20 = xa.z, m21 = ya.z, m22 = za.z;
            return FromMatrix(m00, m01, m02, m10, m11, m12, m20, m21, m22);
        }

        /// <summary>Quaternion from a row-major 3x3 rotation matrix (column-vector convention).</summary>
        public static Quat FromMatrix(float m00, float m01, float m02, float m10, float m11, float m12, float m20, float m21, float m22)
        {
            float tr = m00 + m11 + m22;
            Quat q;
            if (tr > 0)
            {
                float s = (float)Math.Sqrt(tr + 1.0) * 2;
                q = new Quat((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s);
            }
            else if (m00 > m11 && m00 > m22)
            {
                float s = (float)Math.Sqrt(1.0 + m00 - m11 - m22) * 2;
                q = new Quat(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
            }
            else if (m11 > m22)
            {
                float s = (float)Math.Sqrt(1.0 + m11 - m00 - m22) * 2;
                q = new Quat((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s);
            }
            else
            {
                float s = (float)Math.Sqrt(1.0 + m22 - m00 - m11) * 2;
                q = new Quat((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s);
            }
            return q.Normalized;
        }

        public static Quat Slerp(Quat a, Quat b, float t)
        {
            float d = Dot(a, b);
            if (d < 0) { b = b.Negated; d = -d; }
            if (d > 0.9995f)
            {
                return new Quat(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t, a.w + (b.w - a.w) * t).Normalized;
            }
            double th = Math.Acos(d);
            double sn = Math.Sin(th);
            float wa = (float)(Math.Sin((1 - t) * th) / sn), wb = (float)(Math.Sin(t * th) / sn);
            return new Quat(a.x * wa + b.x * wb, a.y * wa + b.y * wb, a.z * wa + b.z * wb, a.w * wa + b.w * wb);
        }

        public static Quat Nlerp(Quat a, Quat b, float t)
        {
            if (Dot(a, b) < 0) b = b.Negated;
            return new Quat(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t, a.w + (b.w - a.w) * t).Normalized;
        }

        public static float Angle(Quat a, Quat b)
        {
            float d = Math.Abs(Dot(a, b));
            return (float)(2 * Math.Acos(Math.Min(1f, d)) * 180 / Math.PI);
        }

        /// <summary>Unity Quaternion.Euler: rotates Z, then X, then Y (degrees).</summary>
        public static Quat EulerUnity(float xDeg, float yDeg, float zDeg)
        {
            return AngleAxis(yDeg, Vec3.Up) * AngleAxis(xDeg, Vec3.Right) * AngleAxis(zDeg, Vec3.Forward);
        }

        /// <summary>Inverse of EulerUnity (ZXY order), degrees.</summary>
        public Vec3 ToEulerUnity()
        {
            // R = Ry * Rx * Rz ; m12 = -sin(x)
            float m12 = 2 * (y * z - w * x);
            float sx = -m12;
            float ex, ey, ez;
            if (Math.Abs(sx) < 0.99999f)
            {
                ex = (float)Math.Asin(Clamp(sx, -1, 1));
                float m02 = 2 * (x * z + w * y), m22 = 1 - 2 * (x * x + y * y);
                float m10 = 2 * (x * y + w * z), m11 = 1 - 2 * (x * x + z * z);
                ey = (float)Math.Atan2(m02, m22);
                ez = (float)Math.Atan2(m10, m11);
            }
            else
            {
                ex = (float)(Math.PI / 2 * Math.Sign(sx));
                float m01 = 2 * (x * y - w * z), m00 = 1 - 2 * (y * y + z * z);
                ey = 0;
                ez = (float)Math.Atan2(-m01, m00);
            }
            const float R2D = (float)(180.0 / Math.PI);
            return new Vec3(ex * R2D, ey * R2D, ez * R2D);
        }

        /// <summary>FBX eEulerXYZ: matrix R = Rz * Ry * Rx (X applied first), degrees. Input must be in FBX (right-handed) space.</summary>
        public Vec3 ToEulerXYZ()
        {
            float m20 = 2 * (x * z - w * y);
            float sy = -m20;
            double ex, ey, ez;
            if (Math.Abs(sy) < 0.999999f)
            {
                ey = Math.Asin(Clamp(sy, -1, 1));
                float m21 = 2 * (y * z + w * x), m22 = 1 - 2 * (x * x + y * y);
                float m10 = 2 * (x * y + w * z), m00 = 1 - 2 * (y * y + z * z);
                ex = Math.Atan2(m21, m22);
                ez = Math.Atan2(m10, m00);
            }
            else
            {
                ey = Math.PI / 2 * Math.Sign(sy);
                float m01 = 2 * (x * y - w * z), m11 = 1 - 2 * (x * x + z * z);
                ex = 0;
                ez = Math.Atan2(-m01, m11);
            }
            const double R2D = 180.0 / Math.PI;
            return new Vec3((float)(ex * R2D), (float)(ey * R2D), (float)(ez * R2D));
        }

        public static Quat FromEulerXYZ(float xDeg, float yDeg, float zDeg)
        {
            return AngleAxis(zDeg, Vec3.Forward) * AngleAxis(yDeg, Vec3.Up) * AngleAxis(xDeg, Vec3.Right);
        }

        /// <summary>Yaw (degrees around +Y) of the rotated forward axis.</summary>
        public float Yaw
        {
            get
            {
                Vec3 f = this * Vec3.Forward;
                if (Math.Abs(f.x) + Math.Abs(f.z) < 1e-4f)
                {
                    Vec3 u = this * Vec3.Up; // looking straight up/down: use up vector
                    f = f.y > 0 ? -u : u;
                }
                return (float)(Math.Atan2(f.x, f.z) * 180 / Math.PI);
            }
        }

        public static Quat YawRotation(float deg) => AngleAxis(deg, Vec3.Up);

        /// <summary>Decompose q = swing * twist, twist around the given axis.</summary>
        public static void SwingTwist(Quat q, Vec3 axis, out Quat swing, out Quat twist)
        {
            axis = axis.Normalized;
            Vec3 r = new Vec3(q.x, q.y, q.z);
            Vec3 p = axis * Vec3.Dot(r, axis);
            twist = new Quat(p.x, p.y, p.z, q.w);
            if (twist.Norm < 1e-9f) twist = Identity; else twist = twist.Normalized;
            swing = q * twist.Conjugate;
        }

        static float Clamp(float v, float a, float b) => v < a ? a : (v > b ? b : v);
        public override string ToString() => $"({x:F4}, {y:F4}, {z:F4}, {w:F4})";
    }

    public static class MathX
    {
        public const float Deg2Rad = (float)(Math.PI / 180.0);
        public const float Rad2Deg = (float)(180.0 / Math.PI);
        public static float Clamp(float v, float a, float b) => v < a ? a : (v > b ? b : v);
        public static double Clamp(double v, double a, double b) => v < a ? a : (v > b ? b : v);
        public static int Clamp(int v, int a, int b) => v < a ? a : (v > b ? b : v);
        public static float Clamp01(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);
        public static float Lerp(float a, float b, float t) => a + (b - a) * t;
        public static float InverseLerp(float a, float b, float v) => Math.Abs(b - a) < 1e-9f ? 0 : Clamp01((v - a) / (b - a));
        public static float SmoothStep(float t) { t = Clamp01(t); return t * t * (3 - 2 * t); }
        public static float DeltaAngle(float a, float b)
        {
            float d = (b - a) % 360f;
            if (d > 180f) d -= 360f;
            if (d < -180f) d += 360f;
            return d;
        }

        /// <summary>Convert an OpenVR (right-handed) 3x4 row-major pose matrix to Unity-space position + rotation.</summary>
        public static void OpenVRToUnity(float[] m, int o, out Vec3 pos, out Quat rot)
        {
            pos = new Vec3(m[o + 3], m[o + 7], -m[o + 11]);
            Quat q = Quat.FromMatrix(m[o + 0], m[o + 1], m[o + 2], m[o + 4], m[o + 5], m[o + 6], m[o + 8], m[o + 9], m[o + 10]);
            rot = new Quat(-q.x, -q.y, q.z, q.w);
        }

        /// <summary>Unity (left-handed) -> FBX (right-handed) by mirroring X.</summary>
        public static Vec3 UnityToFbxPos(Vec3 p) => new Vec3(-p.x, p.y, p.z);
        public static Quat UnityToFbxRot(Quat q) => new Quat(q.x, -q.y, -q.z, q.w);

        /// <summary>Choose the Euler triple equivalent to e that is closest to prev (avoids 360 flips).</summary>
        public static Vec3 UnwrapEuler(Vec3 e, Vec3 prev)
        {
            Vec3 a = new Vec3(Near(e.x, prev.x), Near(e.y, prev.y), Near(e.z, prev.z));
            // alternate solution for XYZ order: (x+180, 180-y, z+180)
            Vec3 alt = new Vec3(Near(e.x + 180f, prev.x), Near(180f - e.y, prev.y), Near(e.z + 180f, prev.z));
            float da = Math.Abs(a.x - prev.x) + Math.Abs(a.y - prev.y) + Math.Abs(a.z - prev.z);
            float db = Math.Abs(alt.x - prev.x) + Math.Abs(alt.y - prev.y) + Math.Abs(alt.z - prev.z);
            return db < da ? alt : a;
        }

        static float Near(float v, float target)
        {
            while (v - target > 180f) v -= 360f;
            while (v - target < -180f) v += 360f;
            return v;
        }
    }

    /// <summary>Rigid transform (rotation + translation).</summary>
    public struct Pose
    {
        public Vec3 pos;
        public Quat rot;
        public Pose(Vec3 p, Quat r) { pos = p; rot = r; }
        public static readonly Pose Identity = new Pose(Vec3.Zero, Quat.Identity);
        public static Pose operator *(Pose a, Pose b) => new Pose(a.pos + a.rot * b.pos, a.rot * b.rot);
        public Pose Inverse { get { Quat ir = rot.Conjugate; return new Pose(ir * -pos, ir); } }
        public Vec3 TransformPoint(Vec3 p) => pos + rot * p;
        public Vec3 InverseTransformPoint(Vec3 p) => rot.Conjugate * (p - pos);
        public static Pose Lerp(Pose a, Pose b, float t) => new Pose(Vec3.Lerp(a.pos, b.pos, t), Quat.Slerp(a.rot, b.rot, t));
    }
}
