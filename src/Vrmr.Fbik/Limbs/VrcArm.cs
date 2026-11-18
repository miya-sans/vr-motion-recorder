// VR Motion Recorder addition (not upstream): a VRChat-style arm (shoulder, elbow, twist) as VRChat's IK 2.0 solves it,
// re-implemented here (no third-party source is included), with the bend-direction
// constants fitted to VRChat's own IK on HUMR recordings (see README, "Shoulders and elbows").
//
// Per frame, for an arm without an elbow tracker:
//   read pose  = the rest arm hanging from the solved chest (VRChat's IK reads the animated pose)
//   shoulder   = a yaw/pitch shoulder: the clavicle turns toward the hand target by a damped yaw and pitch
//   elbow      = a two-bone triangle (upper arm, forearm) to the hand target in the bend plane: a direction built from
//                the chest frame and the target direction, plus the clavicle direction, minus the hand's wrist->palm
//                and palm->thumb axes; then rotated by a swivel offset around the shoulder->hand line
//   twist      = a shoulder/upper-arm twist when the hand is raised, upper arm twist to the bend plane, forearm
//                twist following the upper arm
//   no stretch = out of reach the arm points at the target and the hand stays short (no stretch curve; VRChat
//                measured: the hand stays 2-3 cm short, the shoulder hardly moves)
namespace VRMR.Fbik
{
    public struct VrcArmInput
    {
        public bool IsLeft;
        public Vector3 HandPos; public Quaternion HandRot;   // hand bone target
        public Quaternion ChestFrame;                        // avatar frame carried by the chest
        public Vector3 WristToPalm, PalmToThumb;             // hand-local axes (guessed from the rest pose); zero = unknown
        public Vector3 UpperArmBendAxis;                     // upper-arm local axis of the bend normal
        public bool HasShoulder;
        public float ShoulderWeight;                          // 1 = full shoulder rotation, 0 = clavicle stays at rest
        public bool HasBendGoal; public Vector3 BendGoal;     // elbow tracker (bend goal): the elbow bends toward it
        public float BendGoalWeight;
    }

    public static class VrcArmCore
    {
        // reference defaults
        public static float ShoulderYawOffset = 45f;
        public static float ShoulderPitchOffset = -30f;
        // Fitted to VRChat (elbow position given VRChat's own chest and hands, 7 HUMR windows, both arms, grid over
        // swivel 0-6 deg, wrist->palm 0.25-1, palm->thumb 0.5-3): reference defaults 0 / 1 / 1 -> 2 / 0.5 / 1.
        // Elbow 1.67 -> 0.59 cm, swivel 5.6 -> 2.3 deg.
        /// <summary>Swivel offset (deg) of the left arm; the right arm uses the mirrored value.</summary>
        public static float SwivelOffset = 2f;
        /// <summary>Weight of the hand's wrist->palm axis in the bend direction (reference default 1).</summary>
        public static float WristToPalmWeight = 0.5f;
        /// <summary>Weight of the hand's palm->thumb axis in the bend direction (reference default 1, applied at half strength).</summary>
        public static float PalmToThumbWeight = 1f;

        /// <summary>
        /// Share of the hand's twist (about the forearm) given to the forearm bone, on top of the basic forearm twist (which
        /// only follows the upper arm). Measured: VRChat's forearm takes 0.44-0.76 (0.51-0.61 on the takes with wide wrist
        /// turns) of the wrist twist, like a twist relaxer.
        /// </summary>
        public static float ForearmTwistShare = 0.55f;

        public const int Shoulder = 0, Upper = 1, Fore = 2, Hand = 3, Count = 4;

        [System.ThreadStatic] static Vector3[] _axis;

        /// <summary>p/r: shoulder (clavicle), upper arm, forearm, hand in the read pose (world); solved in place.</summary>
        public static void Solve(in VrcArmInput i, Vector3[] p, Quaternion[] r)
        {
            if (_axis == null) _axis = new Vector3[Count];
            bool left = i.IsLeft;
            float mag = 0f;
            int first = i.HasShoulder ? Shoulder : Upper;
            for (int k = first; k < Hand; k++)
            {
                Vector3 v = p[k + 1] - p[k];
                mag += v.magnitude;
                _axis[k] = Quaternion.Inverse(r[k]) * v;
            }
            Quaternion forearmRelToUpper = Quaternion.Inverse(r[Upper]) * r[Fore];
            Quaternion handRelToForearm = Quaternion.Inverse(r[Fore]) * r[Hand];
            Quaternion chest = i.ChestFrame;
            Vector3 chestForward = chest * Vector3.forward, chestUp = chest * Vector3.up;
            Vector3 position = i.HandPos;

            if (i.HasShoulder && i.ShoulderWeight > 0f)
            {
                Vector3 shoulderAxis = _axis[Shoulder].normalized;
                // ---- shoulder yaw
                Vector3 sDir = (position - p[Shoulder]).normalized;
                float yOA = left ? ShoulderYawOffset : -ShoulderYawOffset;
                Quaternion working = Quaternion.AngleAxis((left ? -90f : 90f) + yOA, chestUp) * chest;
                Vector3 sw = Quaternion.Inverse(working) * sDir;
                float yaw = Mathf.Atan2(sw.x, sw.z) * Mathf.Rad2Deg;
                yaw *= 1f - Mathf.Abs(Vector3.Dot(sw, Vector3.up));
                yaw -= yOA;
                float yawMin = left ? -20f : -50f, yawMax = left ? 50f : 20f;
                yaw = Damper(yaw, yawMin - yOA, yawMax - yOA, 0.7f);
                Vector3 f = r[Shoulder] * shoulderAxis;
                Vector3 t = working * (Quaternion.AngleAxis(yaw, Vector3.up) * Vector3.forward);
                Quaternion yawRotation = IkQuaternionExt.FromToRotation(f, t);
                // ---- shoulder pitch
                working = Quaternion.AngleAxis(left ? -90f : 90f, chestUp) * chest;
                working = Quaternion.AngleAxis(left ? ShoulderPitchOffset : -ShoulderPitchOffset, chestForward) * working;
                sDir = position - (p[Shoulder] + chest * (left ? Vector3.right : Vector3.left) * mag);
                sw = Quaternion.Inverse(working) * sDir;
                float pitch = Mathf.Atan2(sw.y, sw.z) * Mathf.Rad2Deg;
                pitch -= ShoulderPitchOffset;
                pitch = Damper(pitch, -45f - ShoulderPitchOffset, 45f - ShoulderPitchOffset, 1f);
                Quaternion pitchRotation = Quaternion.AngleAxis(-pitch, working * Vector3.right);
                Quaternion sR = pitchRotation * yawRotation;
                if (i.ShoulderWeight < 1f) sR = Quaternion.Lerp(Quaternion.identity, sR, i.ShoulderWeight);
                RotateAround(p, r, Shoulder, p[Shoulder], sR);

                Vector3 bendNormal = BendNormalFrom(in i, r[Shoulder] * shoulderAxis, position - p[Upper], chest, p[Upper]);
                Triangle(p, r, Upper, Fore, Hand, position, bendNormal);
                // twist the shoulder and upper arm when the hand is raised
                float tw = Mathf.Clamp(pitch * i.ShoulderWeight * 2f, 0f, 180f);
                Vector3 ua = _axis[Upper].normalized;
                r[Shoulder] = Normalize(Quaternion.AngleAxis(tw, r[Shoulder] * (left ? shoulderAxis : -shoulderAxis)) * r[Shoulder]);
                r[Upper] = Normalize(Quaternion.AngleAxis(tw, r[Upper] * (left ? ua : -ua)) * r[Upper]);
                FixTwist(in i, p, r, bendNormal, forearmRelToUpper);
            }
            else
            {
                // the arm's first bone: the clavicle, or the upper arm on a rig without one
                Vector3 armDir = i.HasShoulder ? r[Shoulder] * _axis[Shoulder].normalized : r[Upper] * _axis[Upper].normalized;
                Vector3 bendNormal = BendNormalFrom(in i, armDir, position - p[Upper], chest, p[Upper]);
                Triangle(p, r, Upper, Fore, Hand, position, bendNormal);
                FixTwist(in i, p, r, bendNormal, forearmRelToUpper);
            }
            // the forearm takes a share of the wrist's twist (VRChat-measured)
            Vector3 foreAxis = p[Hand] - p[Fore];
            if (ForearmTwistShare > 0f && foreAxis.sqrMagnitude > 1e-10f)
            {
                foreAxis = foreAxis.normalized;
                Quaternion d = i.HandRot * Quaternion.Inverse(r[Fore] * handRelToForearm);
                float proj = d.x * foreAxis.x + d.y * foreAxis.y + d.z * foreAxis.z;
                float twist = 2f * Mathf.Atan2(proj, d.w) * Mathf.Rad2Deg;
                if (twist > 180f) twist -= 360f; else if (twist < -180f) twist += 360f;
                r[Fore] = Normalize(Quaternion.AngleAxis(twist * ForearmTwistShare, foreAxis) * r[Fore]);
            }
            r[Hand] = i.HandRot;
        }

        // Bend normal of the VRChat-style arm, with the fitted swivel / palm / thumb constants
        static Vector3 BendNormalFrom(in VrcArmInput i, Vector3 armDir, Vector3 dir, Quaternion chest, Vector3 upperPos)
        {
            Quaternion ic = Quaternion.Inverse(chest);
            Vector3 f = Vector3.down;
            Vector3 t = ic * dir.normalized + Vector3.forward;
            Vector3 b = IkQuaternionExt.FromToRotation(f, t) * Vector3.back;
            f = ic * armDir;
            t = ic * dir;
            b = IkQuaternionExt.FromToRotation(f, t) * b;
            b = chest * b;
            b += armDir;
            b -= i.HandRot * i.WristToPalm * WristToPalmWeight;
            b -= i.HandRot * i.PalmToThumb * (0.5f * PalmToThumbWeight);
            if (i.HasBendGoal && i.BendGoalWeight > 0f) b = Vector3.Slerp(b, i.BendGoal - upperPos, i.BendGoalWeight); // bend goal (elbow tracker)
            float swivel = i.IsLeft ? SwivelOffset : -SwivelOffset;
            if (swivel != 0f) b = Quaternion.AngleAxis(swivel, -dir) * b;
            return Vector3.Cross(b, dir);
        }

        static void FixTwist(in VrcArmInput i, Vector3[] p, Quaternion[] r, Vector3 bendNormal, Quaternion forearmRelToUpper)
        {
            // upper arm twist relative to the bend normal
            Vector3 upperDir = p[Fore] - p[Upper];
            Vector3 bendAxisWorld = r[Upper] * i.UpperArmBendAxis;
            if (upperDir.sqrMagnitude > 1e-10f && bendAxisWorld.sqrMagnitude > 1e-10f && Vector3.Cross(bendAxisWorld, upperDir).sqrMagnitude > 1e-12f)
            {
                Quaternion space = Quaternion.LookRotation(bendAxisWorld, upperDir);
                Vector3 twist = Quaternion.Inverse(space) * bendNormal;
                float angle = Mathf.Atan2(twist.x, twist.z) * Mathf.Rad2Deg;
                r[Upper] = Normalize(Quaternion.AngleAxis(angle, upperDir) * r[Upper]);
            }
            // forearm twist relative to the upper arm
            Quaternion fixedFore = r[Upper] * forearmRelToUpper;
            Quaternion fromTo = IkQuaternionExt.FromToRotation(fixedFore * _axis[Fore], p[Hand] - p[Fore]);
            Quaternion target = fromTo * fixedFore;
            Quaternion q = target * Quaternion.Inverse(r[Fore]);
            RotateAround(p, r, Fore, p[Fore], q);
        }

        static void RotateAround(Vector3[] p, Quaternion[] r, int from, Vector3 point, Quaternion q)
        {
            for (int k = from; k < Count; k++)
            {
                p[k] = point + q * (p[k] - point);
                r[k] = Normalize(q * r[k]);
            }
        }

        static Quaternion Normalize(Quaternion q)
        {
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return m > 1e-8f ? new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m) : Quaternion.identity;
        }

        // Two-bone solve in the bend plane, weight 1
        static void Triangle(Vector3[] p, Quaternion[] r, int a, int b, int c, Vector3 target, Vector3 bendNormal)
        {
            Vector3 dir = target - p[a];
            float sq = dir.sqrMagnitude;
            if (sq < 1e-12f) return;
            float len = Mathf.Sqrt(sq);
            float sq1 = (p[b] - p[a]).sqrMagnitude, sq2 = (p[c] - p[b]).sqrMagnitude;
            Vector3 bendDir = Vector3.Cross(dir, bendNormal);
            float x = (len * len + (sq1 - sq2)) / 2f / len;
            float y = Mathf.Sqrt(Mathf.Max(sq1 - x * x, 0f));
            Vector3 toBend = bendDir.sqrMagnitude > 1e-12f ? Quaternion.LookRotation(dir, bendDir) * new Vector3(0f, y, x) : dir.normalized * Mathf.Sqrt(sq1);
            RotateAround(p, r, a, p[a], IkQuaternionExt.FromToRotation(p[b] - p[a], toBend));
            RotateAround(p, r, b, p[b], IkQuaternionExt.FromToRotation(p[c] - p[b], target - p[b]));
        }

        // Damped clamp of an angle to a range (in-out quintic easing)
        static float Damper(float value, float min, float max, float weight)
        {
            float range = max - min;
            if (weight < 1f)
            {
                float mid = max - range * 0.5f;
                value = mid + (value - mid) * 0.5f;
            }
            value -= min;
            float t = Mathf.Clamp(value / range, 0f, 1f);
            float ts = t * t, tc = ts * t;
            return Mathf.Lerp(min, max, 6f * tc * ts - 15f * ts * ts + 10f * tc);
        }

        /// <summary>Guess of the hand-local wrist-to-palm axis: the axis pointing from the wrist away from the forearm.</summary>
        public static Vector3 WristToPalmAxis(Quaternion handRot, Vector3 handPos, Vector3 forearmPos)
        {
            Vector3 toForearm = forearmPos - handPos;
            Vector3 axis = AxisToDirection(handRot, toForearm);
            return Vector3.Dot(toForearm, handRot * axis) > 0f ? -axis : axis;
        }

        /// <summary>Guess of the hand-local palm-to-thumb axis, from the hand's closest child (the thumb on most rigs).</summary>
        public static Vector3 PalmToThumbAxis(Quaternion handRot, Vector3 handPos, Vector3 forearmPos, Vector3 closestChildPos)
        {
            Vector3 handNormal = Vector3.Cross(handPos - forearmPos, closestChildPos - handPos);
            Vector3 toThumb = Vector3.Cross(handNormal, handPos - forearmPos);
            if (toThumb.sqrMagnitude < 1e-12f) return Vector3.zero;
            Vector3 axis = AxisToDirection(handRot, toThumb);
            return Vector3.Dot(toThumb, handRot * axis) < 0f ? -axis : axis;
        }

        /// <summary>Upper-arm bend axis: upper-arm-local cross of the bone direction and the local axis closest to the avatar's forward.</summary>
        public static Vector3 UpperArmBendAxis(Quaternion upperRot, Vector3 upperPos, Vector3 forearmPos, Vector3 avatarForward)
        {
            Vector3 fwd = AxisToDirection(upperRot, avatarForward);
            if (Vector3.Dot(upperRot * fwd, avatarForward) < 0f) fwd = -fwd;
            return Vector3.Cross(Quaternion.Inverse(upperRot) * (forearmPos - upperPos), fwd);
        }

        static Vector3 AxisToDirection(Quaternion r, Vector3 direction)
        {
            direction = direction.normalized;
            Vector3 axis = Vector3.right;
            float dx = Mathf.Abs(Vector3.Dot(r * Vector3.right, direction));
            float dy = Mathf.Abs(Vector3.Dot(r * Vector3.up, direction));
            if (dy > dx) axis = Vector3.up;
            float dz = Mathf.Abs(Vector3.Dot(r * Vector3.forward, direction));
            if (dz > dx && dz > dy) axis = Vector3.forward;
            return axis;
        }
    }
}
