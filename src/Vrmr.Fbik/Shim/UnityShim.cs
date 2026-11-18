// Unity-compatible math shim: the subset of UnityEngine math (Vector2/3/4, Quaternion, Mathf, Matrix4x4,
// HumanBodyBones) the solver uses, following Unity's documented/reference behaviour
// (UnityCsReference + the native Quaternion/Matrix algorithms Unity is known to use).
// No Unity dependency. Namespace VRMR.Fbik.

using System;
using System.Globalization;

namespace VRMR.Fbik
{
    /// <summary>Unity's HumanBodyBones with Unity's exact numbering.</summary>
    public enum HumanBodyBones
    {
        Hips = 0, LeftUpperLeg = 1, RightUpperLeg = 2, LeftLowerLeg = 3, RightLowerLeg = 4, LeftFoot = 5, RightFoot = 6,
        Spine = 7, Chest = 8, Neck = 9, Head = 10, LeftShoulder = 11, RightShoulder = 12, LeftUpperArm = 13,
        RightUpperArm = 14,
        LeftLowerArm = 15, RightLowerArm = 16, LeftHand = 17, RightHand = 18, LeftToes = 19, RightToes = 20,
        LeftEye = 21, RightEye = 22,
        Jaw = 23, LeftThumbProximal = 24, LeftThumbIntermediate = 25, LeftThumbDistal = 26, LeftIndexProximal = 27,
        LeftIndexIntermediate = 28, LeftIndexDistal = 29, LeftMiddleProximal = 30, LeftMiddleIntermediate = 31,
        LeftMiddleDistal = 32,
        LeftRingProximal = 33, LeftRingIntermediate = 34, LeftRingDistal = 35, LeftLittleProximal = 36,
        LeftLittleIntermediate = 37,
        LeftLittleDistal = 38, RightThumbProximal = 39, RightThumbIntermediate = 40, RightThumbDistal = 41,
        RightIndexProximal = 42,
        RightIndexIntermediate = 43, RightIndexDistal = 44, RightMiddleProximal = 45, RightMiddleIntermediate = 46,
        RightMiddleDistal = 47,
        RightRingProximal = 48, RightRingIntermediate = 49, RightRingDistal = 50, RightLittleProximal = 51,
        RightLittleIntermediate = 52,
        RightLittleDistal = 53, UpperChest = 54, LastBone = 55
    }

    public static class Mathf
    {
        public const float PI = (float)Math.PI;
        public const float Infinity = float.PositiveInfinity;
        public const float NegativeInfinity = float.NegativeInfinity;
        public const float Deg2Rad = PI * 2F / 360F;

        public const float Rad2Deg = 1F / Deg2Rad;

        // Unity: FloatMinDenormal when flush-to-zero is off (desktop).
        public static readonly float Epsilon = float.Epsilon;

        public static float Sin(float f)
        {
            return (float)Math.Sin(f);
        }

        public static float Cos(float f)
        {
            return (float)Math.Cos(f);
        }

        public static float Tan(float f)
        {
            return (float)Math.Tan(f);
        }

        public static float Asin(float f)
        {
            return (float)Math.Asin(f);
        }

        public static float Acos(float f)
        {
            return (float)Math.Acos(f);
        }

        public static float Atan(float f)
        {
            return (float)Math.Atan(f);
        }

        public static float Atan2(float y, float x)
        {
            return (float)Math.Atan2(y, x);
        }

        public static float Sqrt(float f)
        {
            return (float)Math.Sqrt(f);
        }

        public static float Abs(float f)
        {
            return Math.Abs(f);
        }

        public static int Abs(int value)
        {
            return Math.Abs(value);
        }

        public static float Min(float a, float b)
        {
            return a < b ? a : b;
        }

        public static float Min(params float[] values)
        {
            int len = values.Length;
            if (len == 0) return 0;
            float m = values[0];
            for (int i = 1; i < len; i++)
                if (values[i] < m)
                    m = values[i];
            return m;
        }

        public static int Min(int a, int b)
        {
            return a < b ? a : b;
        }

        public static float Max(float a, float b)
        {
            return a > b ? a : b;
        }

        public static float Max(params float[] values)
        {
            int len = values.Length;
            if (len == 0) return 0;
            float m = values[0];
            for (int i = 1; i < len; i++)
                if (values[i] > m)
                    m = values[i];
            return m;
        }

        public static int Max(int a, int b)
        {
            return a > b ? a : b;
        }

        public static float Pow(float f, float p)
        {
            return (float)Math.Pow(f, p);
        }

        public static float Exp(float power)
        {
            return (float)Math.Exp(power);
        }

        public static float Log(float f, float p)
        {
            return (float)Math.Log(f, p);
        }

        public static float Log(float f)
        {
            return (float)Math.Log(f);
        }

        public static float Log10(float f)
        {
            return (float)Math.Log10(f);
        }

        public static float Ceil(float f)
        {
            return (float)Math.Ceiling(f);
        }

        public static float Floor(float f)
        {
            return (float)Math.Floor(f);
        }

        public static float Round(float f)
        {
            return (float)Math.Round(f);
        }

        public static int CeilToInt(float f)
        {
            return (int)Math.Ceiling(f);
        }

        public static int FloorToInt(float f)
        {
            return (int)Math.Floor(f);
        }

        public static int RoundToInt(float f)
        {
            return (int)Math.Round(f);
        }

        public static float Sign(float f)
        {
            return f >= 0F ? 1F : -1F;
        }

        public static float Clamp(float value, float min, float max)
        {
            if (value < min) value = min;
            else if (value > max) value = max;
            return value;
        }

        public static int Clamp(int value, int min, int max)
        {
            if (value < min) value = min;
            else if (value > max) value = max;
            return value;
        }

        public static float Clamp01(float value)
        {
            if (value < 0F) return 0F;
            if (value > 1F) return 1F;
            return value;
        }

        public static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * Clamp01(t);
        }

        public static float LerpUnclamped(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        public static float LerpAngle(float a, float b, float t)
        {
            float delta = Repeat(b - a, 360);
            if (delta > 180) delta -= 360;
            return a + delta * Clamp01(t);
        }

        public static float MoveTowards(float current, float target, float maxDelta)
        {
            if (Abs(target - current) <= maxDelta) return target;
            return current + Sign(target - current) * maxDelta;
        }

        public static float MoveTowardsAngle(float current, float target, float maxDelta)
        {
            float deltaAngle = DeltaAngle(current, target);
            if (-maxDelta < deltaAngle && deltaAngle < maxDelta) return target;
            target = current + deltaAngle;
            return MoveTowards(current, target, maxDelta);
        }

        public static float SmoothStep(float from, float to, float t)
        {
            t = Clamp01(t);
            t = -2.0F * t * t * t + 3.0F * t * t;
            return to * t + from * (1F - t);
        }

        public static bool Approximately(float a, float b)
        {
            return Abs(b - a) < Max(0.000001f * Max(Abs(a), Abs(b)), Epsilon * 8);
        }

        /// <summary>Unity's SmoothDamp (Game Programming Gems 4, 1.10). deltaTime must be supplied (no Time.deltaTime here).</summary>
        public static float SmoothDamp(float current, float target, ref float currentVelocity, float smoothTime,
            float maxSpeed, float deltaTime)
        {
            smoothTime = Max(0.0001F, smoothTime);
            float omega = 2F / smoothTime;
            float x = omega * deltaTime;
            float exp = 1F / (1F + x + 0.48F * x * x + 0.235F * x * x * x);
            float change = current - target;
            float originalTo = target;
            float maxChange = maxSpeed * smoothTime;
            change = Clamp(change, -maxChange, maxChange);
            target = current - change;
            float temp = (currentVelocity + omega * change) * deltaTime;
            currentVelocity = (currentVelocity - omega * temp) * exp;
            float output = target + (change + temp) * exp;
            if (originalTo - current > 0.0F == output > originalTo)
            {
                output = originalTo;
                currentVelocity = (output - originalTo) / deltaTime;
            }

            return output;
        }

        public static float SmoothDamp(float current, float target, ref float currentVelocity, float smoothTime,
            float deltaTime)
        {
            return SmoothDamp(current, target, ref currentVelocity, smoothTime, Infinity, deltaTime);
        }

        public static float SmoothDampAngle(float current, float target, ref float currentVelocity, float smoothTime,
            float maxSpeed, float deltaTime)
        {
            target = current + DeltaAngle(current, target);
            return SmoothDamp(current, target, ref currentVelocity, smoothTime, maxSpeed, deltaTime);
        }

        public static float Repeat(float t, float length)
        {
            return Clamp(t - Floor(t / length) * length, 0.0f, length);
        }

        public static float PingPong(float t, float length)
        {
            t = Repeat(t, length * 2F);
            return length - Abs(t - length);
        }

        public static float InverseLerp(float a, float b, float value)
        {
            if (a != b) return Clamp01((value - a) / (b - a));
            return 0.0f;
        }

        public static float DeltaAngle(float current, float target)
        {
            float delta = Repeat(target - current, 360.0F);
            if (delta > 180.0F) delta -= 360.0F;
            return delta;
        }
    }

    [Serializable]
    public struct Vector2 : IEquatable<Vector2>
    {
        public float x, y;

        public Vector2(float x, float y)
        {
            this.x = x;
            this.y = y;
        }

        public static Vector2 zero => new(0F, 0F);
        public static Vector2 one => new(1F, 1F);
        public float sqrMagnitude => x * x + y * y;
        public float magnitude => (float)Math.Sqrt(x * x + y * y);

        public static float Distance(Vector2 a, Vector2 b)
        {
            float dx = a.x - b.x, dy = a.y - b.y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        public static float Dot(Vector2 a, Vector2 b)
        {
            return a.x * b.x + a.y * b.y;
        }

        public static Vector2 operator +(Vector2 a, Vector2 b)
        {
            return new Vector2(a.x + b.x, a.y + b.y);
        }

        public static Vector2 operator -(Vector2 a, Vector2 b)
        {
            return new Vector2(a.x - b.x, a.y - b.y);
        }

        public static Vector2 operator -(Vector2 a)
        {
            return new Vector2(-a.x, -a.y);
        }

        public static Vector2 operator *(Vector2 a, float d)
        {
            return new Vector2(a.x * d, a.y * d);
        }

        public static Vector2 operator *(float d, Vector2 a)
        {
            return new Vector2(a.x * d, a.y * d);
        }

        public static Vector2 operator /(Vector2 a, float d)
        {
            return new Vector2(a.x / d, a.y / d);
        }

        public static bool operator ==(Vector2 l, Vector2 r)
        {
            float dx = l.x - r.x, dy = l.y - r.y;
            return dx * dx + dy * dy < 9.99999944E-11f;
        }

        public static bool operator !=(Vector2 l, Vector2 r)
        {
            return !(l == r);
        }

        public bool Equals(Vector2 o)
        {
            return x == o.x && y == o.y;
        }

        public override bool Equals(object o)
        {
            return o is Vector2 v && Equals(v);
        }

        public override int GetHashCode()
        {
            return x.GetHashCode() ^ (y.GetHashCode() << 2);
        }

        public static implicit operator Vector3(Vector2 v)
        {
            return new Vector3(v.x, v.y, 0F);
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "({0:F2}, {1:F2})", x, y);
        }
    }

    [Serializable]
    public struct Vector3 : IEquatable<Vector3>
    {
        public const float kEpsilon = 0.00001F;
        public const float kEpsilonNormalSqrt = 1e-15F;
        public float x, y, z;

        public Vector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public Vector3(float x, float y)
        {
            this.x = x;
            this.y = y;
            z = 0F;
        }

        public float this[int index]
        {
            get
            {
                switch (index)
                {
                    case 0: return x;
                    case 1: return y;
                    case 2: return z;
                    default: throw new IndexOutOfRangeException("Invalid Vector3 index!");
                }
            }
            set
            {
                switch (index)
                {
                    case 0: x = value; break;
                    case 1: y = value; break;
                    case 2: z = value; break;
                    default: throw new IndexOutOfRangeException("Invalid Vector3 index!");
                }
            }
        }

        public void Set(float newX, float newY, float newZ)
        {
            x = newX;
            y = newY;
            z = newZ;
        }

        public static Vector3 zero => new(0F, 0F, 0F);
        public static Vector3 one => new(1F, 1F, 1F);
        public static Vector3 up => new(0F, 1F, 0F);
        public static Vector3 down => new(0F, -1F, 0F);
        public static Vector3 left => new(-1F, 0F, 0F);
        public static Vector3 right => new(1F, 0F, 0F);
        public static Vector3 forward => new(0F, 0F, 1F);
        public static Vector3 back => new(0F, 0F, -1F);

        public static Vector3 positiveInfinity =>
            new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);

        public static Vector3 negativeInfinity =>
            new(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        public float magnitude => (float)Math.Sqrt(x * x + y * y + z * z);
        public float sqrMagnitude => x * x + y * y + z * z;
        public Vector3 normalized => Normalize(this);

        public void Normalize()
        {
            float mag = Magnitude(this);
            if (mag > kEpsilon) this = this / mag;
            else this = zero;
        }

        public static float Magnitude(Vector3 v)
        {
            return (float)Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
        }

        public static float SqrMagnitude(Vector3 v)
        {
            return v.x * v.x + v.y * v.y + v.z * v.z;
        }

        public static Vector3 Normalize(Vector3 value)
        {
            float mag = Magnitude(value);
            if (mag > kEpsilon) return value / mag;
            return zero;
        }

        public static float Dot(Vector3 lhs, Vector3 rhs)
        {
            return lhs.x * rhs.x + lhs.y * rhs.y + lhs.z * rhs.z;
        }

        public static Vector3 Cross(Vector3 lhs, Vector3 rhs)
        {
            return new Vector3(lhs.y * rhs.z - lhs.z * rhs.y, lhs.z * rhs.x - lhs.x * rhs.z,
                lhs.x * rhs.y - lhs.y * rhs.x);
        }

        public static float Distance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public static Vector3 Lerp(Vector3 a, Vector3 b, float t)
        {
            t = Mathf.Clamp01(t);
            return new Vector3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);
        }

        public static Vector3 LerpUnclamped(Vector3 a, Vector3 b, float t)
        {
            return new Vector3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);
        }

        public static Vector3 MoveTowards(Vector3 current, Vector3 target, float maxDistanceDelta)
        {
            float toX = target.x - current.x, toY = target.y - current.y, toZ = target.z - current.z;
            float sqdist = toX * toX + toY * toY + toZ * toZ;
            if (sqdist == 0 || (maxDistanceDelta >= 0 && sqdist <= maxDistanceDelta * maxDistanceDelta)) return target;
            float dist = (float)Math.Sqrt(sqdist);
            return new Vector3(current.x + toX / dist * maxDistanceDelta, current.y + toY / dist * maxDistanceDelta,
                current.z + toZ / dist * maxDistanceDelta);
        }

        public static Vector3 SmoothDamp(Vector3 current, Vector3 target, ref Vector3 currentVelocity, float smoothTime,
            float maxSpeed, float deltaTime)
        {
            smoothTime = Mathf.Max(0.0001F, smoothTime);
            float omega = 2F / smoothTime;
            float x = omega * deltaTime;
            float exp = 1F / (1F + x + 0.48F * x * x + 0.235F * x * x * x);
            float change_x = current.x - target.x, change_y = current.y - target.y, change_z = current.z - target.z;
            var originalTo = target;
            float maxChange = maxSpeed * smoothTime;
            float maxChangeSq = maxChange * maxChange;
            float sqrmag = change_x * change_x + change_y * change_y + change_z * change_z;
            if (sqrmag > maxChangeSq)
            {
                float mag = (float)Math.Sqrt(sqrmag);
                change_x = change_x / mag * maxChange;
                change_y = change_y / mag * maxChange;
                change_z = change_z / mag * maxChange;
            }

            target.x = current.x - change_x;
            target.y = current.y - change_y;
            target.z = current.z - change_z;
            float temp_x = (currentVelocity.x + omega * change_x) * deltaTime;
            float temp_y = (currentVelocity.y + omega * change_y) * deltaTime;
            float temp_z = (currentVelocity.z + omega * change_z) * deltaTime;
            currentVelocity.x = (currentVelocity.x - omega * temp_x) * exp;
            currentVelocity.y = (currentVelocity.y - omega * temp_y) * exp;
            currentVelocity.z = (currentVelocity.z - omega * temp_z) * exp;
            float output_x = target.x + (change_x + temp_x) * exp;
            float output_y = target.y + (change_y + temp_y) * exp;
            float output_z = target.z + (change_z + temp_z) * exp;
            float origMinusCurrent_x = originalTo.x - current.x,
                origMinusCurrent_y = originalTo.y - current.y,
                origMinusCurrent_z = originalTo.z - current.z;
            float outMinusOrig_x = output_x - originalTo.x,
                outMinusOrig_y = output_y - originalTo.y,
                outMinusOrig_z = output_z - originalTo.z;
            if (origMinusCurrent_x * outMinusOrig_x + origMinusCurrent_y * outMinusOrig_y +
                origMinusCurrent_z * outMinusOrig_z > 0)
            {
                output_x = originalTo.x;
                output_y = originalTo.y;
                output_z = originalTo.z;
                currentVelocity.x = (output_x - originalTo.x) / deltaTime;
                currentVelocity.y = (output_y - originalTo.y) / deltaTime;
                currentVelocity.z = (output_z - originalTo.z) / deltaTime;
            }

            return new Vector3(output_x, output_y, output_z);
        }

        public static Vector3 Scale(Vector3 a, Vector3 b)
        {
            return new Vector3(a.x * b.x, a.y * b.y, a.z * b.z);
        }

        public static Vector3 Reflect(Vector3 inDirection, Vector3 inNormal)
        {
            float factor = -2F * Dot(inNormal, inDirection);
            return new Vector3(factor * inNormal.x + inDirection.x, factor * inNormal.y + inDirection.y,
                factor * inNormal.z + inDirection.z);
        }

        public static Vector3 Project(Vector3 vector, Vector3 onNormal)
        {
            float sqrMag = Dot(onNormal, onNormal);
            if (sqrMag < Mathf.Epsilon) return zero;
            float dot = Dot(vector, onNormal);
            return new Vector3(onNormal.x * dot / sqrMag, onNormal.y * dot / sqrMag, onNormal.z * dot / sqrMag);
        }

        public static Vector3 ProjectOnPlane(Vector3 vector, Vector3 planeNormal)
        {
            float sqrMag = Dot(planeNormal, planeNormal);
            if (sqrMag < Mathf.Epsilon) return vector;
            float dot = Dot(vector, planeNormal);
            return new Vector3(vector.x - planeNormal.x * dot / sqrMag, vector.y - planeNormal.y * dot / sqrMag,
                vector.z - planeNormal.z * dot / sqrMag);
        }

        public static float Angle(Vector3 from, Vector3 to)
        {
            float denominator = (float)Math.Sqrt(from.sqrMagnitude * to.sqrMagnitude);
            if (denominator < kEpsilonNormalSqrt) return 0F;
            float dot = Mathf.Clamp(Dot(from, to) / denominator, -1F, 1F);
            return (float)Math.Acos(dot) * Mathf.Rad2Deg;
        }

        public static float SignedAngle(Vector3 from, Vector3 to, Vector3 axis)
        {
            float unsignedAngle = Angle(from, to);
            float cross_x = from.y * to.z - from.z * to.y;
            float cross_y = from.z * to.x - from.x * to.z;
            float cross_z = from.x * to.y - from.y * to.x;
            float sign = Mathf.Sign(axis.x * cross_x + axis.y * cross_y + axis.z * cross_z);
            return unsignedAngle * sign;
        }

        public static Vector3 ClampMagnitude(Vector3 vector, float maxLength)
        {
            float sqrmag = vector.sqrMagnitude;
            if (sqrmag > maxLength * maxLength)
            {
                float mag = (float)Math.Sqrt(sqrmag);
                float normalized_x = vector.x / mag, normalized_y = vector.y / mag, normalized_z = vector.z / mag;
                return new Vector3(normalized_x * maxLength, normalized_y * maxLength, normalized_z * maxLength);
            }

            return vector;
        }

        public static Vector3 Min(Vector3 a, Vector3 b)
        {
            return new Vector3(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Min(a.z, b.z));
        }

        public static Vector3 Max(Vector3 a, Vector3 b)
        {
            return new Vector3(Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y), Mathf.Max(a.z, b.z));
        }

        /// <summary>Unity native Vector3.Slerp (t clamped to [0,1]).</summary>
        public static Vector3 Slerp(Vector3 a, Vector3 b, float t)
        {
            return SlerpUnclamped(a, b, Mathf.Clamp01(t));
        }

        public static Vector3 SlerpUnclamped(Vector3 lhs, Vector3 rhs, float t)
        {
            float lhsMag = Magnitude(lhs), rhsMag = Magnitude(rhs);
            if (lhsMag < kEpsilon || rhsMag < kEpsilon) return LerpUnclamped(lhs, rhs, t);
            float lerpedMagnitude = lhsMag + (rhsMag - lhsMag) * t;
            float dot = Dot(lhs, rhs) / (lhsMag * rhsMag);
            if (dot > 1.0F - kEpsilon)
            {
                return LerpUnclamped(lhs, rhs, t);
            }

            if (dot < -1.0F + kEpsilon)
            {
                var lhsNorm = lhs / lhsMag;
                var axis = OrthoNormalVectorFast(lhsNorm);
                var slerped = RotateAxisAngle(lhsNorm, axis, Mathf.PI * t);
                return slerped * lerpedMagnitude;
            }
            else
            {
                var axis = Normalize(Cross(lhs, rhs));
                var lhsNorm = lhs / lhsMag;
                float angle = (float)Math.Acos(dot) * t;
                var slerped = RotateAxisAngle(lhsNorm, axis, angle);
                return slerped * lerpedMagnitude;
            }
        }

        // Matrix3x3f::SetAxisAngle + MultiplyPoint3 (Rodrigues), unit axis.
        static Vector3 RotateAxisAngle(Vector3 v, Vector3 axis, float angle)
        {
            float s = (float)Math.Sin(angle), c = (float)Math.Cos(angle);
            float xx = axis.x * axis.x, yy = axis.y * axis.y, zz = axis.z * axis.z;
            float xy = axis.x * axis.y, yz = axis.y * axis.z, zx = axis.z * axis.x;
            float xs = axis.x * s, ys = axis.y * s, zs = axis.z * s;
            float oneMinusCos = 1.0f - c;
            float m00 = oneMinusCos * xx + c, m01 = oneMinusCos * xy - zs, m02 = oneMinusCos * zx + ys;
            float m10 = oneMinusCos * xy + zs, m11 = oneMinusCos * yy + c, m12 = oneMinusCos * yz - xs;
            float m20 = oneMinusCos * zx - ys, m21 = oneMinusCos * yz + xs, m22 = oneMinusCos * zz + c;
            return new Vector3(m00 * v.x + m01 * v.y + m02 * v.z, m10 * v.x + m11 * v.y + m12 * v.z,
                m20 * v.x + m21 * v.y + m22 * v.z);
        }

        internal static Vector3 OrthoNormalVectorFast(Vector3 n)
        {
            const float k1OverSqrt2 = 0.7071067811865475244008443621048490f;
            Vector3 res;
            if (Math.Abs(n.z) > k1OverSqrt2)
            {
                float a = n.y * n.y + n.z * n.z;
                float k = 1.0F / (float)Math.Sqrt(a);
                res.x = 0;
                res.y = -n.z * k;
                res.z = n.y * k;
            }
            else
            {
                float a = n.x * n.x + n.y * n.y;
                float k = 1.0F / (float)Math.Sqrt(a);
                res.x = -n.y * k;
                res.y = n.x * k;
                res.z = 0;
            }

            return res;
        }

        public static Vector3 operator +(Vector3 a, Vector3 b)
        {
            return new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        }

        public static Vector3 operator -(Vector3 a, Vector3 b)
        {
            return new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        }

        public static Vector3 operator -(Vector3 a)
        {
            return new Vector3(-a.x, -a.y, -a.z);
        }

        public static Vector3 operator *(Vector3 a, float d)
        {
            return new Vector3(a.x * d, a.y * d, a.z * d);
        }

        public static Vector3 operator *(float d, Vector3 a)
        {
            return new Vector3(a.x * d, a.y * d, a.z * d);
        }

        public static Vector3 operator /(Vector3 a, float d)
        {
            return new Vector3(a.x / d, a.y / d, a.z / d);
        }

        public static bool operator ==(Vector3 lhs, Vector3 rhs)
        {
            float diff_x = lhs.x - rhs.x, diff_y = lhs.y - rhs.y, diff_z = lhs.z - rhs.z;
            float sqrmag = diff_x * diff_x + diff_y * diff_y + diff_z * diff_z;
            return sqrmag < kEpsilon * kEpsilon;
        }

        public static bool operator !=(Vector3 lhs, Vector3 rhs)
        {
            return !(lhs == rhs);
        }

        public bool Equals(Vector3 other)
        {
            return x == other.x && y == other.y && z == other.z;
        }

        public override bool Equals(object other)
        {
            return other is Vector3 v && Equals(v);
        }

        public override int GetHashCode()
        {
            return x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2);
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "({0:F5}, {1:F5}, {2:F5})", x, y, z);
        }

        public string ToString(string format)
        {
            return string.Format(CultureInfo.InvariantCulture, "({0}, {1}, {2})",
                x.ToString(format, CultureInfo.InvariantCulture), y.ToString(format, CultureInfo.InvariantCulture),
                z.ToString(format, CultureInfo.InvariantCulture));
        }
    }

    [Serializable]
    public struct Vector4
    {
        public float x, y, z, w;

        public Vector4(float x, float y, float z, float w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        public static Vector4 zero => new(0, 0, 0, 0);
    }

    [Serializable]
    public struct Quaternion : IEquatable<Quaternion>
    {
        public const float kEpsilon = 0.000001F;
        public float x, y, z, w;

        public Quaternion(float x, float y, float z, float w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        public float this[int index]
        {
            get
            {
                switch (index)
                {
                    case 0: return x;
                    case 1: return y;
                    case 2: return z;
                    case 3: return w;
                    default: throw new IndexOutOfRangeException("Invalid Quaternion index!");
                }
            }
            set
            {
                switch (index)
                {
                    case 0: x = value; break;
                    case 1: y = value; break;
                    case 2: z = value; break;
                    case 3: w = value; break;
                    default: throw new IndexOutOfRangeException("Invalid Quaternion index!");
                }
            }
        }

        public void Set(float newX, float newY, float newZ, float newW)
        {
            x = newX;
            y = newY;
            z = newZ;
            w = newW;
        }

        public static Quaternion identity => new(0F, 0F, 0F, 1F);

        public static Quaternion operator *(Quaternion lhs, Quaternion rhs)
        {
            return new Quaternion(
                lhs.w * rhs.x + lhs.x * rhs.w + lhs.y * rhs.z - lhs.z * rhs.y,
                lhs.w * rhs.y + lhs.y * rhs.w + lhs.z * rhs.x - lhs.x * rhs.z,
                lhs.w * rhs.z + lhs.z * rhs.w + lhs.x * rhs.y - lhs.y * rhs.x,
                lhs.w * rhs.w - lhs.x * rhs.x - lhs.y * rhs.y - lhs.z * rhs.z);
        }

        public static Vector3 operator *(Quaternion rotation, Vector3 point)
        {
            float x = rotation.x * 2F, y = rotation.y * 2F, z = rotation.z * 2F;
            float xx = rotation.x * x, yy = rotation.y * y, zz = rotation.z * z;
            float xy = rotation.x * y, xz = rotation.x * z, yz = rotation.y * z;
            float wx = rotation.w * x, wy = rotation.w * y, wz = rotation.w * z;
            Vector3 res;
            res.x = (1F - (yy + zz)) * point.x + (xy - wz) * point.y + (xz + wy) * point.z;
            res.y = (xy + wz) * point.x + (1F - (xx + zz)) * point.y + (yz - wx) * point.z;
            res.z = (xz - wy) * point.x + (yz + wx) * point.y + (1F - (xx + yy)) * point.z;
            return res;
        }

        static bool IsEqualUsingDot(float dot)
        {
            return dot > 1.0f - kEpsilon;
        }

        public static bool operator ==(Quaternion lhs, Quaternion rhs)
        {
            return IsEqualUsingDot(Dot(lhs, rhs));
        }

        public static bool operator !=(Quaternion lhs, Quaternion rhs)
        {
            return !(lhs == rhs);
        }

        public static float Dot(Quaternion a, Quaternion b)
        {
            return a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
        }

        public static float Angle(Quaternion a, Quaternion b)
        {
            float dot = Mathf.Min(Mathf.Abs(Dot(a, b)), 1.0F);
            return IsEqualUsingDot(dot) ? 0.0f : Mathf.Acos(dot) * 2.0F * Mathf.Rad2Deg;
        }

        /// <summary>Unity's Quaternion.Inverse: the conjugate.</summary>
        public static Quaternion Inverse(Quaternion rotation)
        {
            return new Quaternion(-rotation.x, -rotation.y, -rotation.z, rotation.w);
        }

        public static Quaternion Normalize(Quaternion q)
        {
            float mag = (float)Math.Sqrt(Dot(q, q));
            if (mag < Mathf.Epsilon) return identity;
            return new Quaternion(q.x / mag, q.y / mag, q.z / mag, q.w / mag);
        }

        public void Normalize() { this = Normalize(this); }
        public Quaternion normalized => Normalize(this);

        /// <summary>AxisAngleToQuaternionSafe: axis need not be unit; zero axis gives identity.</summary>
        public static Quaternion AngleAxis(float angle, Vector3 axis)
        {
            float mag = (float)Math.Sqrt(axis.x * axis.x + axis.y * axis.y + axis.z * axis.z);
            if (mag > 0.000001F)
            {
                float halfAngle = angle * Mathf.Deg2Rad * 0.5F;
                float s = (float)Math.Sin(halfAngle) / mag;
                return new Quaternion(s * axis.x, s * axis.y, s * axis.z, (float)Math.Cos(halfAngle));
            }

            return identity;
        }

        /// <summary>Unity Euler order: Z, then X, then Y (q = qy * qx * qz).</summary>
        public static Quaternion Euler(float x, float y, float z)
        {
            return EulerRad(new Vector3(x * Mathf.Deg2Rad, y * Mathf.Deg2Rad, z * Mathf.Deg2Rad));
        }

        public static Quaternion Euler(Vector3 euler)
        {
            return Euler(euler.x, euler.y, euler.z);
        }

        static Quaternion EulerRad(Vector3 r)
        {
            float cX = (float)Math.Cos(r.x * 0.5f), sX = (float)Math.Sin(r.x * 0.5f);
            float cY = (float)Math.Cos(r.y * 0.5f), sY = (float)Math.Sin(r.y * 0.5f);
            float cZ = (float)Math.Cos(r.z * 0.5f), sZ = (float)Math.Sin(r.z * 0.5f);
            var qX = new Quaternion(sX, 0F, 0F, cX);
            var qY = new Quaternion(0F, sY, 0F, cY);
            var qZ = new Quaternion(0F, 0F, sZ, cZ);
            return qY * qX * qZ;
        }

        public Vector3 eulerAngles
        {
            get
            {
                var m = Matrix3.FromQuaternion(this);
                Vector3 v;
                if (m.Get(1, 2) < 0.999F)
                {
                    if (m.Get(1, 2) > -0.999F)
                    {
                        v.x = (float)Math.Asin(-m.Get(1, 2));
                        v.y = (float)Math.Atan2(m.Get(0, 2), m.Get(2, 2));
                        v.z = (float)Math.Atan2(m.Get(1, 0), m.Get(1, 1));
                    }
                    else
                    {
                        v.x = Mathf.PI * 0.5F;
                        v.y = (float)Math.Atan2(m.Get(0, 1), m.Get(0, 0));
                        v.z = 0.0F;
                    }
                }
                else
                {
                    v.x = -Mathf.PI * 0.5F;
                    v.y = (float)Math.Atan2(-m.Get(0, 1), m.Get(0, 0));
                    v.z = 0.0F;
                }

                v = new Vector3(MakePositive(v.x), MakePositive(v.y), MakePositive(v.z));
                return v * Mathf.Rad2Deg;
            }
            set => this = Euler(value);
        }

        static float MakePositive(float a)
        {
            const float negativeFlip = -0.0001F;
            const float positiveFlip = Mathf.PI * 2.0F - 0.0001F;
            if (a < negativeFlip) a += 2.0f * Mathf.PI;
            else if (a > positiveFlip) a -= 2.0f * Mathf.PI;
            return a;
        }

        public static Quaternion LookRotation(Vector3 forward)
        {
            return LookRotation(forward, Vector3.up);
        }

        /// <summary>
        ///     Unity native LookRotation: ortho-normal basis (x = up×z, y = z×x) -> quaternion; falls back to FromTo(z,
        ///     forward) when up is parallel; identity for zero forward.
        /// </summary>
        public static Quaternion LookRotation(Vector3 forward, Vector3 upwards)
        {
            if (LookRotationToMatrix(forward, upwards, out var m))
            {
                return m.ToQuaternion();
            }

            float mag = Vector3.Magnitude(forward);
            if (mag > Vector3.kEpsilon)
            {
                var ft = Matrix3.FromToRotation(Vector3.forward, forward / mag);
                return ft.ToQuaternion();
            }

            return identity;
        }

        static bool LookRotationToMatrix(Vector3 viewVec, Vector3 upVec, out Matrix3 m)
        {
            m = Matrix3.Identity;
            var z = viewVec;
            float mag = Vector3.Magnitude(z);
            if (mag < Vector3.kEpsilon) return false;
            z /= mag;
            var x = Vector3.Cross(upVec, z);
            mag = Vector3.Magnitude(x);
            if (mag < Vector3.kEpsilon) return false;
            x /= mag;
            var y = Vector3.Cross(z, x);
            if (!(Math.Abs(Vector3.SqrMagnitude(y) - 1.0F) <= 0.000001F)) return false;
            m = Matrix3.FromColumns(x, y, z);
            return true;
        }

        /// <summary>Unity native FromToRotation (Möller-Hughes matrix -> quaternion).</summary>
        public static Quaternion FromToRotation(Vector3 fromDirection, Vector3 toDirection)
        {
            float lhsMag = Vector3.Magnitude(fromDirection), rhsMag = Vector3.Magnitude(toDirection);
            if (lhsMag < Vector3.kEpsilon || rhsMag < Vector3.kEpsilon) return identity;
            return Matrix3.FromToRotation(fromDirection / lhsMag, toDirection / rhsMag).ToQuaternion();
        }

        public void SetFromToRotation(Vector3 fromDirection, Vector3 toDirection)
        {
            this = FromToRotation(fromDirection, toDirection);
        }

        public void SetLookRotation(Vector3 view, Vector3 up) { this = LookRotation(view, up); }

        public static Quaternion Slerp(Quaternion a, Quaternion b, float t)
        {
            return SlerpUnclamped(a, b, Mathf.Clamp01(t));
        }

        /// <summary>Unity native quaternion slerp (shortest arc, nlerp above cos 0.95).</summary>
        public static Quaternion SlerpUnclamped(Quaternion q1, Quaternion q2, float t)
        {
            float dot = Dot(q1, q2);
            Quaternion tmpQuat;
            if (dot < 0.0f)
            {
                dot = -dot;
                tmpQuat = new Quaternion(-q2.x, -q2.y, -q2.z, -q2.w);
            }
            else tmpQuat = q2;

            if (dot < 0.95f)
            {
                float angle = (float)Math.Acos(dot);
                float sinadiv = 1.0f / (float)Math.Sin(angle);
                float sinat = (float)Math.Sin(angle * t);
                float sinaomt = (float)Math.Sin(angle * (1.0f - t));
                return new Quaternion((q1.x * sinaomt + tmpQuat.x * sinat) * sinadiv,
                    (q1.y * sinaomt + tmpQuat.y * sinat) * sinadiv,
                    (q1.z * sinaomt + tmpQuat.z * sinat) * sinadiv,
                    (q1.w * sinaomt + tmpQuat.w * sinat) * sinadiv);
            }

            return LerpUnclamped(q1, tmpQuat, t);
        }

        public static Quaternion Lerp(Quaternion a, Quaternion b, float t)
        {
            return LerpUnclamped(a, b, Mathf.Clamp01(t));
        }

        public static Quaternion LerpUnclamped(Quaternion q1, Quaternion q2, float t)
        {
            Quaternion tmp;
            if (Dot(q1, q2) < 0.0F)
                tmp = new Quaternion(q1.x + t * (-q2.x - q1.x), q1.y + t * (-q2.y - q1.y), q1.z + t * (-q2.z - q1.z),
                    q1.w + t * (-q2.w - q1.w));
            else
                tmp = new Quaternion(q1.x + t * (q2.x - q1.x), q1.y + t * (q2.y - q1.y), q1.z + t * (q2.z - q1.z),
                    q1.w + t * (q2.w - q1.w));
            return NormalizeNative(tmp);
        }

        static Quaternion NormalizeNative(Quaternion q)
        {
            float mag = (float)Math.Sqrt(Dot(q, q));
            return new Quaternion(q.x / mag, q.y / mag, q.z / mag, q.w / mag);
        }

        public static Quaternion RotateTowards(Quaternion from, Quaternion to, float maxDegreesDelta)
        {
            float angle = Angle(from, to);
            if (angle == 0.0f) return to;
            return SlerpUnclamped(from, to, Mathf.Min(1.0f, maxDegreesDelta / angle));
        }

        /// <summary>Quaternion.ToAngleAxis (degrees). Normalizes first; w clamped to [-1,1] so acos cannot NaN.</summary>
        public void ToAngleAxis(out float angle, out Vector3 axis)
        {
            var q = this;
            float mag = (float)Math.Sqrt(Dot(q, q));
            if (mag > 1e-6f) q = new Quaternion(q.x / mag, q.y / mag, q.z / mag, q.w / mag);
            else q = identity;
            float w = q.w > 1f ? 1f : q.w < -1f ? -1f : q.w;
            float a = 2.0f * (float)Math.Acos(w);
            if (Math.Abs(a) <= 0.000001F)
            {
                axis = Vector3.right;
                angle = a * Mathf.Rad2Deg;
                return;
            }

            float div = 1.0f / (float)Math.Sqrt(Math.Max(0f, 1.0f - w * w));
            axis = new Vector3(q.x * div, q.y * div, q.z * div);
            angle = a * Mathf.Rad2Deg;
        }

        public bool Equals(Quaternion other)
        {
            return x == other.x && y == other.y && z == other.z && w == other.w;
        }

        public override bool Equals(object other)
        {
            return other is Quaternion q && Equals(q);
        }

        public override int GetHashCode()
        {
            return x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2) ^ (w.GetHashCode() >> 1);
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "({0:F5}, {1:F5}, {2:F5}, {3:F5})", x, y, z, w);
        }
    }

    /// <summary>Internal 3x3 matrix used to mirror Unity's native Quaternion algorithms. Get(row, col).</summary>
    struct Matrix3
    {
        public float m00, m01, m02, m10, m11, m12, m20, m21, m22;
        public static Matrix3 Identity => new() { m00 = 1, m11 = 1, m22 = 1 };

        public float Get(int r, int c)
        {
            switch (r * 3 + c)
            {
                case 0: return m00;
                case 1: return m01;
                case 2: return m02;
                case 3: return m10;
                case 4: return m11;
                case 5: return m12;
                case 6: return m20;
                case 7: return m21;
                default: return m22;
            }
        }

        public static Matrix3 FromColumns(Vector3 x, Vector3 y, Vector3 z)
        {
            return new Matrix3
                { m00 = x.x, m10 = x.y, m20 = x.z, m01 = y.x, m11 = y.y, m21 = y.z, m02 = z.x, m12 = z.y, m22 = z.z };
        }

        public static Matrix3 FromQuaternion(Quaternion q)
        {
            float x = q.x * 2.0F, y = q.y * 2.0F, z = q.z * 2.0F;
            float xx = q.x * x,
                yy = q.y * y,
                zz = q.z * z,
                xy = q.x * y,
                xz = q.x * z,
                yz = q.y * z,
                wx = q.w * x,
                wy = q.w * y,
                wz = q.w * z;
            return new Matrix3
            {
                m00 = 1.0f - (yy + zz), m10 = xy + wz, m20 = xz - wy,
                m01 = xy - wz, m11 = 1.0f - (xx + zz), m21 = yz + wx,
                m02 = xz + wy, m12 = yz - wx, m22 = 1.0f - (xx + yy)
            };
        }

        // Shoemake's algorithm (Unity MatrixToQuaternion), result normalized.
        public Quaternion ToQuaternion()
        {
            Quaternion q;
            float fTrace = m00 + m11 + m22;
            float fRoot;
            if (fTrace > 0.0f)
            {
                fRoot = (float)Math.Sqrt(fTrace + 1.0f);
                q.w = 0.5f * fRoot;
                fRoot = 0.5f / fRoot;
                q.x = (m21 - m12) * fRoot;
                q.y = (m02 - m20) * fRoot;
                q.z = (m10 - m01) * fRoot;
            }
            else
            {
                int i = 0;
                if (m11 > m00) i = 1;
                if (m22 > Get(i, i)) i = 2;
                int j = (i + 1) % 3, k = (j + 1) % 3;
                fRoot = (float)Math.Sqrt(Get(i, i) - Get(j, j) - Get(k, k) + 1.0f);
                float qi = 0.5f * fRoot;
                fRoot = 0.5f / fRoot;
                q.w = (Get(k, j) - Get(j, k)) * fRoot;
                float qj = (Get(j, i) + Get(i, j)) * fRoot;
                float qk = (Get(k, i) + Get(i, k)) * fRoot;
                q.x = i == 0 ? qi : j == 0 ? qj : qk;
                q.y = i == 1 ? qi : j == 1 ? qj : qk;
                q.z = i == 2 ? qi : j == 2 ? qj : qk;
            }

            float mag = (float)Math.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return new Quaternion(q.x / mag, q.y / mag, q.z / mag, q.w / mag);
        }

        // Möller & Hughes "Efficiently building a matrix to rotate one vector to another" (Unity SetFromToRotation). Unit inputs.
        public static Matrix3 FromToRotation(Vector3 from, Vector3 to)
        {
            const float EPSILON = 0.000001f;
            var v = Vector3.Cross(from, to);
            float e = Vector3.Dot(from, to);
            var m = Identity;
            if (e > 1.0f - EPSILON)
            {
                return m;
            }

            if (e < -1.0f + EPSILON)
            {
                var left = new Vector3(0.0f, from.z, -from.y);
                if (Vector3.Dot(left, left) < EPSILON) left = new Vector3(-from.z, 0.0f, from.x);
                float invlen = 1.0f / (float)Math.Sqrt(Vector3.Dot(left, left));
                left *= invlen;
                var up = Vector3.Cross(left, from);
                float fxx = -from.x * from.x, fyy = -from.y * from.y, fzz = -from.z * from.z;
                float fxy = -from.x * from.y, fxz = -from.x * from.z, fyz = -from.y * from.z;
                float uxx = up.x * up.x, uyy = up.y * up.y, uzz = up.z * up.z;
                float uxy = up.x * up.y, uxz = up.x * up.z, uyz = up.y * up.z;
                float lxx = -left.x * left.x, lyy = -left.y * left.y, lzz = -left.z * left.z;
                float lxy = -left.x * left.y, lxz = -left.x * left.z, lyz = -left.y * left.z;
                m.m00 = fxx + uxx + lxx;
                m.m01 = fxy + uxy + lxy;
                m.m02 = fxz + uxz + lxz;
                m.m10 = m.m01;
                m.m11 = fyy + uyy + lyy;
                m.m12 = fyz + uyz + lyz;
                m.m20 = m.m02;
                m.m21 = m.m12;
                m.m22 = fzz + uzz + lzz;
                return m;
            }

            float h = (1.0f - e) / Vector3.Dot(v, v);
            float hvx = h * v.x, hvz = h * v.z, hvxy = hvx * v.y, hvxz = hvx * v.z, hvyz = hvz * v.y;
            m.m00 = e + hvx * v.x;
            m.m01 = hvxy - v.z;
            m.m02 = hvxz + v.y;
            m.m10 = hvxy + v.z;
            m.m11 = e + h * v.y * v.y;
            m.m12 = hvyz - v.x;
            m.m20 = hvxz - v.y;
            m.m21 = hvyz + v.x;
            m.m22 = e + hvz * v.z;
            return m;
        }
    }

    /// <summary>Column-major 4x4 matrix with Unity's field layout (mRowCol). Only what the port uses.</summary>
    [Serializable]
    public struct Matrix4x4
    {
        public float m00, m10, m20, m30, m01, m11, m21, m31, m02, m12, m22, m32, m03, m13, m23, m33;
        public static Matrix4x4 identity => new() { m00 = 1, m11 = 1, m22 = 1, m33 = 1 };

        public static Matrix4x4 TRS(Vector3 pos, Quaternion q, Vector3 s)
        {
            var r = Matrix3.FromQuaternion(q);
            return new Matrix4x4
            {
                m00 = r.m00 * s.x, m10 = r.m10 * s.x, m20 = r.m20 * s.x,
                m01 = r.m01 * s.y, m11 = r.m11 * s.y, m21 = r.m21 * s.y,
                m02 = r.m02 * s.z, m12 = r.m12 * s.z, m22 = r.m22 * s.z,
                m03 = pos.x, m13 = pos.y, m23 = pos.z, m33 = 1f
            };
        }

        public Vector4 GetColumn(int i)
        {
            switch (i)
            {
                case 0: return new Vector4(m00, m10, m20, m30);
                case 1: return new Vector4(m01, m11, m21, m31);
                case 2: return new Vector4(m02, m12, m22, m32);
                default: return new Vector4(m03, m13, m23, m33);
            }
        }

        public Vector3 MultiplyPoint3x4(Vector3 p)
        {
            return new Vector3(m00 * p.x + m01 * p.y + m02 * p.z + m03, m10 * p.x + m11 * p.y + m12 * p.z + m13,
                m20 * p.x + m21 * p.y + m22 * p.z + m23);
        }

        public Vector3 MultiplyVector(Vector3 v)
        {
            return new Vector3(m00 * v.x + m01 * v.y + m02 * v.z, m10 * v.x + m11 * v.y + m12 * v.z,
                m20 * v.x + m21 * v.y + m22 * v.z);
        }

        /// <summary>Rotation of a TRS matrix (scale removed per column).</summary>
        public Quaternion rotation
        {
            get
            {
                Vector3 c1 = new(m01, m11, m21), c2 = new(m02, m12, m22);
                return Quaternion.LookRotation(c2, c1);
            }
        }

        public static Matrix4x4 operator *(Matrix4x4 lhs, Matrix4x4 rhs)
        {
            Matrix4x4 res;
            res.m00 = lhs.m00 * rhs.m00 + lhs.m01 * rhs.m10 + lhs.m02 * rhs.m20 + lhs.m03 * rhs.m30;
            res.m01 = lhs.m00 * rhs.m01 + lhs.m01 * rhs.m11 + lhs.m02 * rhs.m21 + lhs.m03 * rhs.m31;
            res.m02 = lhs.m00 * rhs.m02 + lhs.m01 * rhs.m12 + lhs.m02 * rhs.m22 + lhs.m03 * rhs.m32;
            res.m03 = lhs.m00 * rhs.m03 + lhs.m01 * rhs.m13 + lhs.m02 * rhs.m23 + lhs.m03 * rhs.m33;
            res.m10 = lhs.m10 * rhs.m00 + lhs.m11 * rhs.m10 + lhs.m12 * rhs.m20 + lhs.m13 * rhs.m30;
            res.m11 = lhs.m10 * rhs.m01 + lhs.m11 * rhs.m11 + lhs.m12 * rhs.m21 + lhs.m13 * rhs.m31;
            res.m12 = lhs.m10 * rhs.m02 + lhs.m11 * rhs.m12 + lhs.m12 * rhs.m22 + lhs.m13 * rhs.m32;
            res.m13 = lhs.m10 * rhs.m03 + lhs.m11 * rhs.m13 + lhs.m12 * rhs.m23 + lhs.m13 * rhs.m33;
            res.m20 = lhs.m20 * rhs.m00 + lhs.m21 * rhs.m10 + lhs.m22 * rhs.m20 + lhs.m23 * rhs.m30;
            res.m21 = lhs.m20 * rhs.m01 + lhs.m21 * rhs.m11 + lhs.m22 * rhs.m21 + lhs.m23 * rhs.m31;
            res.m22 = lhs.m20 * rhs.m02 + lhs.m21 * rhs.m12 + lhs.m22 * rhs.m22 + lhs.m23 * rhs.m32;
            res.m23 = lhs.m20 * rhs.m03 + lhs.m21 * rhs.m13 + lhs.m22 * rhs.m23 + lhs.m23 * rhs.m33;
            res.m30 = lhs.m30 * rhs.m00 + lhs.m31 * rhs.m10 + lhs.m32 * rhs.m20 + lhs.m33 * rhs.m30;
            res.m31 = lhs.m30 * rhs.m01 + lhs.m31 * rhs.m11 + lhs.m32 * rhs.m21 + lhs.m33 * rhs.m31;
            res.m32 = lhs.m30 * rhs.m02 + lhs.m31 * rhs.m12 + lhs.m32 * rhs.m22 + lhs.m33 * rhs.m32;
            res.m33 = lhs.m30 * rhs.m03 + lhs.m31 * rhs.m13 + lhs.m32 * rhs.m23 + lhs.m33 * rhs.m33;
            return res;
        }

        /// <summary>General 4x4 inverse (cofactor expansion).</summary>
        public unsafe Matrix4x4 inverse
        {
            get
            {
                float* m = stackalloc float[16];
                float* inv = stackalloc float[16];
                m[0] = m00;
                m[1] = m01;
                m[2] = m02;
                m[3] = m03;
                m[4] = m10;
                m[5] = m11;
                m[6] = m12;
                m[7] = m13;
                m[8] = m20;
                m[9] = m21;
                m[10] = m22;
                m[11] = m23;
                m[12] = m30;
                m[13] = m31;
                m[14] = m32;
                m[15] = m33;
                inv[0] = m[5] * m[10] * m[15] - m[5] * m[11] * m[14] - m[9] * m[6] * m[15] + m[9] * m[7] * m[14] +
                    m[13] * m[6] * m[11] - m[13] * m[7] * m[10];
                inv[4] = -m[4] * m[10] * m[15] + m[4] * m[11] * m[14] + m[8] * m[6] * m[15] - m[8] * m[7] * m[14] -
                    m[12] * m[6] * m[11] + m[12] * m[7] * m[10];
                inv[8] = m[4] * m[9] * m[15] - m[4] * m[11] * m[13] - m[8] * m[5] * m[15] + m[8] * m[7] * m[13] +
                    m[12] * m[5] * m[11] - m[12] * m[7] * m[9];
                inv[12] = -m[4] * m[9] * m[14] + m[4] * m[10] * m[13] + m[8] * m[5] * m[14] - m[8] * m[6] * m[13] -
                    m[12] * m[5] * m[10] + m[12] * m[6] * m[9];
                inv[1] = -m[1] * m[10] * m[15] + m[1] * m[11] * m[14] + m[9] * m[2] * m[15] - m[9] * m[3] * m[14] -
                    m[13] * m[2] * m[11] + m[13] * m[3] * m[10];
                inv[5] = m[0] * m[10] * m[15] - m[0] * m[11] * m[14] - m[8] * m[2] * m[15] + m[8] * m[3] * m[14] +
                    m[12] * m[2] * m[11] - m[12] * m[3] * m[10];
                inv[9] = -m[0] * m[9] * m[15] + m[0] * m[11] * m[13] + m[8] * m[1] * m[15] - m[8] * m[3] * m[13] -
                    m[12] * m[1] * m[11] + m[12] * m[3] * m[9];
                inv[13] = m[0] * m[9] * m[14] - m[0] * m[10] * m[13] - m[8] * m[1] * m[14] + m[8] * m[2] * m[13] +
                    m[12] * m[1] * m[10] - m[12] * m[2] * m[9];
                inv[2] = m[1] * m[6] * m[15] - m[1] * m[7] * m[14] - m[5] * m[2] * m[15] + m[5] * m[3] * m[14] +
                    m[13] * m[2] * m[7] - m[13] * m[3] * m[6];
                inv[6] = -m[0] * m[6] * m[15] + m[0] * m[7] * m[14] + m[4] * m[2] * m[15] - m[4] * m[3] * m[14] -
                    m[12] * m[2] * m[7] + m[12] * m[3] * m[6];
                inv[10] = m[0] * m[5] * m[15] - m[0] * m[7] * m[13] - m[4] * m[1] * m[15] + m[4] * m[3] * m[13] +
                    m[12] * m[1] * m[7] - m[12] * m[3] * m[5];
                inv[14] = -m[0] * m[5] * m[14] + m[0] * m[6] * m[13] + m[4] * m[1] * m[14] - m[4] * m[2] * m[13] -
                    m[12] * m[1] * m[6] + m[12] * m[2] * m[5];
                inv[3] = -m[1] * m[6] * m[11] + m[1] * m[7] * m[10] + m[5] * m[2] * m[11] - m[5] * m[3] * m[10] -
                    m[9] * m[2] * m[7] + m[9] * m[3] * m[6];
                inv[7] = m[0] * m[6] * m[11] - m[0] * m[7] * m[10] - m[4] * m[2] * m[11] + m[4] * m[3] * m[10] +
                    m[8] * m[2] * m[7] - m[8] * m[3] * m[6];
                inv[11] = -m[0] * m[5] * m[11] + m[0] * m[7] * m[9] + m[4] * m[1] * m[11] - m[4] * m[3] * m[9] -
                    m[8] * m[1] * m[7] + m[8] * m[3] * m[5];
                inv[15] = m[0] * m[5] * m[10] - m[0] * m[6] * m[9] - m[4] * m[1] * m[10] + m[4] * m[2] * m[9] +
                    m[8] * m[1] * m[6] - m[8] * m[2] * m[5];
                float det = m[0] * inv[0] + m[1] * inv[4] + m[2] * inv[8] + m[3] * inv[12];
                if (det == 0) return new Matrix4x4();
                det = 1.0f / det;
                return new Matrix4x4
                {
                    m00 = inv[0] * det, m01 = inv[1] * det, m02 = inv[2] * det, m03 = inv[3] * det,
                    m10 = inv[4] * det, m11 = inv[5] * det, m12 = inv[6] * det, m13 = inv[7] * det,
                    m20 = inv[8] * det, m21 = inv[9] * det, m22 = inv[10] * det, m23 = inv[11] * det,
                    m30 = inv[12] * det, m31 = inv[13] * det, m32 = inv[14] * det, m33 = inv[15] * det
                };
            }
        }

        public static implicit operator float4x4(Matrix4x4 m)
        {
            return new float4x4(new float4(m.m00, m.m10, m.m20, m.m30), new float4(m.m01, m.m11, m.m21, m.m31),
                new float4(m.m02, m.m12, m.m22, m.m32), new float4(m.m03, m.m13, m.m23, m.m33));
        }
    }
}
