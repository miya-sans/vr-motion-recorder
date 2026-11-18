// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Plain C#: game-engine, Burst and job-system dependencies are replaced by the VRMR.Fbik shim.

namespace VRMR.Fbik
{
    public static class IkLegSolveCore
    {
        const float epsilon = 1e-5f, sqrEpsilon = 1e-8f, restBendAgreeCos = 0.94f;
        public const float MinKneeInteriorDeg = 20f, MaxKneeInteriorDeg = 176f, KneeAnteriorSoftDeg = 85f;
        public const float KneeAnteriorHardDeg = 89.5f, KneeAnteriorTaperStartDeg = 160f, TrackerShinRollMaxDeg = 45f;
        public const float KneeHintForward = 0.75f;
        public const float KneeHintFootFlatTrust = 0.3f;
        public const float KneeTrackerHintBias = 0.06f;

        /// <summary>
        ///     VRMR addition: knee direction without a knee tracker from the VRChat-style bend plane (true) or the base
        ///     solver's hips-forward push (false).
        /// </summary>
        public static bool VrcKnee = true;

        /// <summary>
        ///     VRMR addition: bend-to-target weight of the knee plane: 0 = knee plane locked to the hips, 1 = to the foot target
        ///     (reference default 0.5).
        ///     VRChat's IK matches 1 (HUMR test takes: knee position mean 1.1-3.0 cm at 1, 1.7-3.5 cm at 0.5, 2.0-5.8 cm at 0.25).
        /// </summary>
        public static float VrmrBendToTargetWeight = 1f;

        public static float ClampKneeSwivelDeg(float swivelDeg, float softDeg, float hardDeg)
        {
            float wrapped = swivelDeg - 360f * Mathf.Floor((swivelDeg + 180f) / 360f);
            float mag = wrapped < 0f ? -wrapped : wrapped;

            if (!(mag >= 0f))
            {
                return 0f;
            }

            if (!(mag > softDeg))
            {
                return wrapped;
            }

            float maxExcess = hardDeg - softDeg;
            if (!(maxExcess > 0f))
            {
                return wrapped < 0f ? -softDeg : softDeg;
            }

            float excess = mag - softDeg, compressed = softDeg + maxExcess * excess / (maxExcess + excess);
            float taperSpan = 180f - KneeAnteriorTaperStartDeg;
            if (mag > KneeAnteriorTaperStartDeg && taperSpan > 0f)
            {
                float u = (mag - KneeAnteriorTaperStartDeg) / taperSpan;
                if (u > 1f) u = 1f;
                compressed *= 1f - u * u * (3f - 2f * u);
            }

            return wrapped < 0f ? -compressed : compressed;
        }

        public static void Solve(in IkLegSolveInput i, out IkLegSolveResult r)
        {
            r = default;

            r.MidPostRoll = Quaternion.identity;
            var hintRotation = i.HasHintRotation ? i.HintRotation : default;

            Vector3 aPosition = i.Root, bPosition = i.Mid, cPosition = i.Tip;
            Quaternion rootRot = i.RootRotation, midRot = i.MidRotation;
            var tPosition = i.TargetPosition;
            var tRotation = i.TargetRotation * i.TargetOffset;
            Vector3 ab = bPosition - aPosition, bc = cPosition - bPosition, ac = cPosition - aPosition;
            float abLen = ab.magnitude, bcLen = bc.magnitude, acLen = ac.magnitude, maxReach = abLen + bcLen;

            // If we don't have a knee hint (active knee tracker) then we make our own and position it accordingly.
            var hintPosition = i.HintPosition;
            float hintWeight = i.HintWeight;
            if (!i.HintIsTracker && i.HasBodyFrame)
            {
                // Everything is measured against the hip->foot line, so the hint stays in front of the kneecap in any body pose (standing, lying, on the side).
                var legAxis = tPosition - aPosition;
                float legLen = legAxis.magnitude;
                legAxis = legLen > epsilon ? legAxis / legLen : -i.Up;

                // Hips and foot forward with their along-the-leg part removed. The foot steers the knee only when it points
                // clearly out from the leg and not behind the hips (pointed toes while lying down fall back to the hips).
                Vector3 hipsPerp = Vector3.ProjectOnPlane(i.HipsForward, legAxis),
                    footPerp = Vector3.ProjectOnPlane(i.FootForward, legAxis);
                var pushDir = hipsPerp.sqrMagnitude > sqrEpsilon ? hipsPerp.normalized : i.HipsForward;
                float footLen = footPerp.magnitude;
                if (footLen > epsilon && Vector3.Dot(footPerp, pushDir) > 0f)
                {
                    float footTrust = Mathf.Clamp01(footLen / KneeHintFootFlatTrust);
                    pushDir = Vector3.Slerp(pushDir, footPerp / footLen, footTrust).normalized;
                }

                // VRMR: the VRChat-style knee plane: the knee bends along cross(hip->foot, bendNormal), where the
                // bend normal is the hips' right axis slerped toward the foot target's right axis (bendToTargetWeight). Unlike the
                // hips-forward push above, it stays defined when the torso leans into a deep squat (hips forward then lies along
                // the hip->foot line) and turned-out feet splay the knees, as VRChat's IK does.
                if (VrcKnee && i.HipsRight.sqrMagnitude > sqrEpsilon && i.FootRight.sqrMagnitude > sqrEpsilon)
                {
                    var n = Vector3.Slerp(i.HipsRight.normalized, i.FootRight.normalized, VrmrBendToTargetWeight);
                    var planeDir = Vector3.Cross(legAxis, n);
                    planeDir -= legAxis * Vector3.Dot(planeDir, legAxis);
                    if (planeDir.sqrMagnitude > 1e-4f) pushDir = planeDir.normalized;
                }

                // Start on the leg line where the knee sits, then push out in front of it.
                float kneeFrac = maxReach > epsilon ? abLen / maxReach : 0.5f;
                hintPosition = aPosition + legAxis * (legLen * kneeFrac) + pushDir * (maxReach * KneeHintForward);
                hintWeight = 1f;
            }

            // Near full extension a tracked knee sits on the hip/foot line, so the direction of its offset is mostly noise and the leg twists around.
            // Push the hint a fixed distance along the bend the tracker's rotation implies: the offset then never shrinks below the push, so small
            // noise can't flip it, and once the knee bends the real offset outgrows the push and takes over on its own. Same as a tracked elbow.
            if (i.HintIsTracker && hintWeight > 0f && TrackerBendDir(i, tPosition - aPosition, out var trackerBendDir))
            {
                hintPosition += trackerBendDir * (KneeTrackerHintBias * maxReach);
            }

            bool hasHint = hintWeight > 0f;
            float oldAbcAngle = IkMath.TriangleAngle(acLen, abLen, bcLen);
            var atCorrected = tPosition - aPosition;
            float atCorrectedLen = atCorrected.magnitude, minFlexReach = MinFlexionReach(abLen, bcLen);
            if (atCorrectedLen < minFlexReach) atCorrectedLen = minFlexReach;

            float maxExtReach = MaxExtensionReach(abLen, bcLen);
            if (atCorrectedLen > maxExtReach) atCorrectedLen = maxExtReach;

            float newAbcAngle = IkMath.TriangleAngle(atCorrectedLen, abLen, bcLen);
            byte axisSource = 0;
            Vector3 restAxis = Vector3.Cross(ab, bc), bendAxis = restAxis;
            Vector3 abN = abLen > epsilon ? ab / abLen : Vector3.zero,
                anatAxis = i.BendNormal - abN * Vector3.Dot(i.BendNormal, abN);
            bool restBend = restAxis.sqrMagnitude >= sqrEpsilon,
                anatomical = abLen > epsilon && anatAxis.sqrMagnitude > sqrEpsilon && (!restBend ||
                    Vector3.Dot(restAxis.normalized, anatAxis.normalized) < restBendAgreeCos);
            if (anatomical)
            {
                bendAxis = anatAxis;
                axisSource = 3;
            }
            else if (!restBend)
            {
                if (hasHint)
                {
                    bendAxis = Vector3.Cross(hintPosition - aPosition, bc);
                    axisSource = 1;
                }

                if (bendAxis.sqrMagnitude < sqrEpsilon)
                {
                    bendAxis = Vector3.Cross(atCorrected, bc);
                    axisSource = 2;
                }

                if (bendAxis.sqrMagnitude < sqrEpsilon)
                {
                    var bcN = bcLen > epsilon ? bc / bcLen : Vector3.zero;
                    bendAxis = i.BendNormal - bcN * Vector3.Dot(i.BendNormal, bcN);
                    axisSource = 3;

                    if (bendAxis.sqrMagnitude < sqrEpsilon)
                    {
                        bendAxis = i.BendNormal;
                    }
                }
            }

            bendAxis = Vector3.Normalize(bendAxis);

            float half = 0.5f * (oldAbcAngle - newAbcAngle), sinHalf = Mathf.Sin(half), cosHalf = Mathf.Cos(half);
            var deltaR = new Quaternion(bendAxis.x * sinHalf, bendAxis.y * sinHalf, bendAxis.z * sinHalf, cosHalf);
            if (anatomical)
            {
                deltaR = IkMath.AngleAxisRad(Mathf.PI - newAbcAngle, bendAxis);
                if (restBend)
                    deltaR = deltaR * IkMath.AngleAxisRad(oldAbcAngle - Mathf.PI, Vector3.Normalize(restAxis));
            }

            midRot = deltaR * midRot;
            cPosition = bPosition + deltaR * (cPosition - bPosition);
            ac = cPosition - aPosition;

            var rootDelta = Quaternion.identity;
            if (atCorrectedLen > epsilon)
            {
                rootDelta = IkQuaternionExt.FromToRotation(ac, atCorrected);
                rootRot = rootDelta * rootRot;
                bPosition = aPosition + rootDelta * (bPosition - aPosition);
                cPosition = aPosition + rootDelta * (cPosition - aPosition);
                midRot = rootDelta * midRot;
            }

            var hintR = Quaternion.identity;
            bool hintApplied = false;
            var acFinal = cPosition - aPosition;
            float acFinalSqr = acFinal.sqrMagnitude;
            if (acFinalSqr > sqrEpsilon)
            {
                var acNorm = acFinal / Mathf.Sqrt(acFinalSqr);
                var kneeDir = Vector3.Cross(acNorm, rootDelta * bendAxis);
                kneeDir -= acNorm * Vector3.Dot(kneeDir, acNorm);

                var bendPole = Vector3.Cross(acNorm, i.BendNormal);
                bendPole -= acNorm * Vector3.Dot(bendPole, acNorm);
                bool hasBendPole = bendPole.sqrMagnitude > sqrEpsilon;
                var anteriorPole = bendPole;
                bool hasAnteriorPole = hasBendPole;
                if (i.AnteriorNormal.sqrMagnitude > sqrEpsilon)
                {
                    var ap = Vector3.Cross(acNorm, i.AnteriorNormal);
                    ap -= acNorm * Vector3.Dot(ap, acNorm);
                    if (ap.sqrMagnitude > sqrEpsilon)
                    {
                        anteriorPole = ap;
                        hasAnteriorPole = true;
                    }
                }

                var pole = bendPole;
                if (hasHint)
                {
                    Vector3 ah = hintPosition - aPosition, ahProj = ah - acNorm * Vector3.Dot(ah, acNorm);
                    if (ahProj.sqrMagnitude > sqrEpsilon)
                    {
                        pole = ahProj;
                    }
                }

                pole -= acNorm * Vector3.Dot(pole, acNorm);

                pole = GuardPoleAnterior(pole, anteriorPole, acNorm, hasAnteriorPole, ref axisSource);
                if (pole.sqrMagnitude > sqrEpsilon) r.PoleDir = pole.normalized;

                float weight = hasHint ? hintWeight : 1f;

                if (weight > 0f && kneeDir.sqrMagnitude > sqrEpsilon && pole.sqrMagnitude > sqrEpsilon)
                {
                    float swivel = ScaleSwivel(IkMath.SignedAngleRad(kneeDir, pole, acNorm), weight);
                    hintR = IkMath.AngleAxisRad(swivel, acNorm);

                    rootRot = hintR * rootRot;
                    bPosition = aPosition + hintR * (bPosition - aPosition);
                    cPosition = aPosition + hintR * (cPosition - aPosition);
                    midRot = hintR * midRot;
                    hintApplied = hasHint;
                }
            }

            float hintRotSqr = hintRotation.x * hintRotation.x + hintRotation.y * hintRotation.y +
                               hintRotation.z * hintRotation.z + hintRotation.w * hintRotation.w;
            if (i.HintIsTracker && hintRotSqr > 0.5f)
            {
                var shinRoll = cPosition - bPosition;
                if (shinRoll.sqrMagnitude > sqrEpsilon)
                {
                    var shinRollN = shinRoll.normalized;
                    float roll = IkMath.TwistAngleRad(hintRotation * Quaternion.Inverse(midRot), shinRollN);
                    float rollAbs = Mathf.Abs(roll), rollCap = TrackerShinRollMaxDeg * Mathf.Deg2Rad;
                    if (rollAbs > rollCap) rollAbs = rollCap;
                    if (rollAbs > 1e-6f)
                    {
                        float rollSigned = roll < 0f ? -rollAbs : rollAbs;
                        r.MidPostRoll = IkMath.AngleAxisRad(rollSigned, shinRollN);
                        midRot = r.MidPostRoll * midRot;
                        r.ShinRollDeg = rollSigned * Mathf.Rad2Deg;
                    }
                }
            }

            r.HintPosition = hintPosition;
            r.MidDelta = deltaR;
            r.RootDelta = rootDelta;
            r.HintDelta = hintR;
            r.TipRotation = tRotation;
            r.HintApplied = hintApplied;

            r.KneeSolved = bPosition;
            r.FootSolved = cPosition;
            r.RootRotationSolved = rootRot;
            r.MidRotationSolved = midRot;

            r.UpperLength = abLen;
            r.LowerLength = bcLen;
            r.TargetDistance = atCorrectedLen;
            r.ReachRatio = maxReach > epsilon ? atCorrectedLen / maxReach : 0f;
            r.KneeAngleDeg = IkMath.AngleDeg(aPosition - bPosition, cPosition - bPosition);
            r.AxisSource = axisSource;
            r.FootError = (cPosition - tPosition).magnitude;
        }

        // Knee direction (perpendicular to the hip/foot axis) implied by the knee tracker's rotation, else by the hips.
        // HintRotation is the tracker mapped onto the lower leg bone, so carrying the rest-pose hinge (BendNormal, hips-right) through it gives the live hinge.
        static bool TrackerBendDir(in IkLegSolveInput i, Vector3 axis, out Vector3 dir)
        {
            dir = default;
            float axisSqr = axis.sqrMagnitude;
            if (axisSqr < sqrEpsilon)
            {
                return false;
            }

            axis /= Mathf.Sqrt(axisSqr);
            var hinge = i.HasHintRotation
                ? i.HintRotation * (Quaternion.Inverse(i.MidRotation) * i.BendNormal)
                : i.BendNormal;
            dir = Vector3.Cross(axis, hinge);
            dir -= axis * Vector3.Dot(dir, axis);
            float dirSqr = dir.sqrMagnitude;
            if (dirSqr < sqrEpsilon)
            {
                return false;
            }

            dir /= Mathf.Sqrt(dirSqr);
            return true;
        }

        static Vector3 GuardPoleAnterior(Vector3 pole, Vector3 anteriorPole, Vector3 acNorm, bool hasAnteriorPole,
            ref byte axisSource)
        {
            if (!hasAnteriorPole || !(pole.sqrMagnitude > sqrEpsilon))
            {
                return pole;
            }

            var anterior = anteriorPole.normalized;
            float poleDeg = IkMath.SignedAngleRad(anterior, pole, acNorm) * Mathf.Rad2Deg;
            float guardedDeg = ClampKneeSwivelDeg(poleDeg, KneeAnteriorSoftDeg, KneeAnteriorHardDeg);

            if (guardedDeg != poleDeg)
            {
                pole = IkMath.AngleAxisRad(guardedDeg * Mathf.Deg2Rad, acNorm) * anterior;
                axisSource = 5;
            }

            return pole;
        }

        static float ScaleSwivel(float radians, float weight)
        {
            if (weight >= 1f)
            {
                return radians;
            }

            if (!(weight > 0f))
            {
                return 0f;
            }

            return 2f * Mathf.Atan(weight * Mathf.Tan(0.5f * radians));
        }

        static float MinFlexionReach(float upper, float lower)
        {
            float c = Mathf.Cos(MinKneeInteriorDeg * Mathf.Deg2Rad);
            float d2 = upper * upper + lower * lower - 2f * upper * lower * c;
            return d2 > 0f ? Mathf.Sqrt(d2) : 0f;
        }

        static float MaxExtensionReach(float upper, float lower)
        {
            float c = Mathf.Cos(MaxKneeInteriorDeg * Mathf.Deg2Rad);
            float d2 = upper * upper + lower * lower - 2f * upper * lower * c;
            return d2 > 0f ? Mathf.Sqrt(d2) : 0f;
        }
    }
}
