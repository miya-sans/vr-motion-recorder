// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Plain C#: game-engine, Burst and job-system dependencies are replaced by the VRMR.Fbik shim.

namespace VRMR.Fbik
{
    public static class IkSpineAnatomy
    {
        const float epsilon = 1e-5f, sqrEpsilon = 1e-8f;

        public static IkSpineRom Rom(IkSpineSegment segment)
        {
            switch (segment)
            {
                case IkSpineSegment.Lumbar: return new IkSpineRom(60f, 20f, 25f, 12f);
                case IkSpineSegment.LowerThoracic: return new IkSpineRom(25f, 15f, 20f, 20f);
                case IkSpineSegment.UpperThoracic: return new IkSpineRom(15f, 30f, 25f, 15f);
                case IkSpineSegment.Cervical: return new IkSpineRom(50f, 60f, 45f, 75f);
                default: return new IkSpineRom(60f, 20f, 25f, 12f);
            }
        }

        public static IkSpineRestFrame BuildRestFrame(Vector3 boneWorldPos, Vector3 childWorldPos,
            Quaternion boneWorldRot, Quaternion parentWorldRot, Vector3 hipsRightWorld)
        {
            IkSpineRestFrame f = default;
            f.Valid = false;

            var upW = childWorldPos - boneWorldPos;
            float upSqr = upW.sqrMagnitude;

            if (!(upSqr > sqrEpsilon) || !(hipsRightWorld.sqrMagnitude > sqrEpsilon))
            {
                return f;
            }

            upW /= Mathf.Sqrt(upSqr);

            var rightW = hipsRightWorld - upW * Vector3.Dot(hipsRightWorld, upW);
            float rSqr = rightW.sqrMagnitude;
            if (!(rSqr > sqrEpsilon))
            {
                return f;
            }

            rightW /= Mathf.Sqrt(rSqr);

            var fwdW = Vector3.Cross(rightW, upW);
            var invParent = IkSpineAnatomyCore.Conj(parentWorldRot);
            f.Right = invParent * rightW;
            f.Up = invParent * upW;
            f.Forward = invParent * fwdW;
            f.RestLocalRot = invParent * boneWorldRot;
            f.Valid = true;
            return f;
        }
    }

    public static class IkSpineAnatomyCore
    {
        const float epsilon = 1e-5f, sqrEpsilon = 1e-8f;
        public const float OvershootAsymptote = 1.25f;

        public static Quaternion Conj(Quaternion q)
        {
            return new Quaternion(-q.x, -q.y, -q.z, q.w);
        }

        public static Quaternion AxisAngle(float deg, Vector3 axis)
        {
            float h = deg * (0.5f * Mathf.Deg2Rad), s = Mathf.Sin(h);
            return new Quaternion(axis.x * s, axis.y * s, axis.z * s, Mathf.Cos(h));
        }

        public static Quaternion Clamp(Quaternion localRot, in IkSpineRestFrame frame, in IkSpineRom rom)
        {
            return Clamp(localRot, frame, rom, out _);
        }

        public static Quaternion Clamp(Quaternion localRot, in IkSpineRestFrame frame, in IkSpineRom rom,
            out IkSpineClampInfo info)
        {
            info = default;
            if (!frame.Valid)
            {
                return localRot;
            }

            var delta = localRot * Conj(frame.RestLocalRot);

            Decompose(delta, frame, out float flexDeg, out float latDeg, out float axialDeg);

            if (!(flexDeg * flexDeg + latDeg * latDeg + axialDeg * axialDeg >= 0f))
            {
                return localRot;
            }

            float axialLim = Mathf.Max(0f, rom.AxialDeg);
            float flexLim = Mathf.Max(0f, flexDeg >= 0f ? rom.FlexDeg : rom.ExtDeg), latLim = Mathf.Max(0f, rom.LatDeg);
            float fN = flexDeg / Mathf.Max(flexLim, epsilon), lN = latDeg / Mathf.Max(latLim, epsilon);
            float q = fN * fN + lN * lN, swingScale = 1f;
            if (q > 1f)
            {
                float rNow = Mathf.Sqrt(q), rGuard = Saturate(rNow, 1f, OvershootAsymptote);
                swingScale = rGuard / rNow;
                info.SwingClamped = true;
            }

            float axialGuard = axialDeg, axialAbs = axialDeg < 0f ? -axialDeg : axialDeg;
            if (axialAbs > axialLim)
            {
                float mag = Saturate(axialAbs, axialLim, axialLim * OvershootAsymptote);
                axialGuard = axialDeg < 0f ? -mag : mag;
                info.TwistClamped = true;
            }

            info.FlexDeg = flexDeg;
            info.LatDeg = latDeg;
            info.AxialDeg = axialDeg;

            if (!info.SwingClamped && !info.TwistClamped)
            {
                return localRot;
            }

            return Recompose(flexDeg * swingScale, latDeg * swingScale, axialGuard, frame);
        }

        public static void Decompose(Quaternion delta, in IkSpineRestFrame frame, out float flexDeg, out float latDeg,
            out float axialDeg)
        {
            if (delta.w < 0f)
            {
                delta = new Quaternion(-delta.x, -delta.y, -delta.z, -delta.w);
            }

            var v = new Vector3(delta.x, delta.y, delta.z);
            float proj = Vector3.Dot(v, frame.Up);
            var twist = new Quaternion(frame.Up.x * proj, frame.Up.y * proj, frame.Up.z * proj, delta.w);
            float twistNorm = Mathf.Sqrt(twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w);
            if (!(twistNorm > epsilon))
            {
                twist = Quaternion.identity;
                proj = 0f;
            }
            else
            {
                float inv = 1f / twistNorm;
                twist = new Quaternion(twist.x * inv, twist.y * inv, twist.z * inv, twist.w * inv);
                proj *= inv;
            }

            axialDeg = 2f * Mathf.Atan2(proj, twist.w) * Mathf.Rad2Deg;

            var swing = delta * Conj(twist);
            if (swing.w < 0f)
            {
                swing = new Quaternion(-swing.x, -swing.y, -swing.z, -swing.w);
            }

            var sv = new Vector3(swing.x, swing.y, swing.z);
            float svLen = sv.magnitude, swingDeg = 2f * Mathf.Atan2(svLen, swing.w) * Mathf.Rad2Deg;
            var swingVec = svLen > epsilon ? sv / svLen * swingDeg : Vector3.zero;

            flexDeg = Vector3.Dot(swingVec, frame.Right);
            latDeg = Vector3.Dot(swingVec, frame.Forward);
        }

        public static Quaternion Recompose(float flexDeg, float latDeg, float axialDeg, in IkSpineRestFrame frame)
        {
            var swingVec = frame.Right * flexDeg + frame.Forward * latDeg;
            float swingDeg = swingVec.magnitude;
            var swing = swingDeg > epsilon ? AxisAngle(swingDeg, swingVec / swingDeg) : Quaternion.identity;
            var twist = AxisAngle(axialDeg, frame.Up);

            return swing * twist * frame.RestLocalRot;
        }

        public static float Saturate(float x, float soft, float hard)
        {
            if (!(x > soft))
            {
                return x;
            }

            float m = hard - soft;
            if (!(m > epsilon))
            {
                return soft;
            }

            float e = x - soft;
            return soft + m * e / (m + e);
        }
    }
}
