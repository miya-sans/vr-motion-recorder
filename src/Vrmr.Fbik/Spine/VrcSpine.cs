// VR Motion Recorder addition (not upstream): the VRChat-matched spine (pelvis, spine, chest, neck, head) as
// VRChat's IK 2.0 solves it, re-implemented here (no third-party source is included), with the changes
// measured against VRChat's own IK on HUMR recordings (see README, "Chest and spine").
//
// Used for a tracked hip (FBT), instead of the base solver's Raliv soft-FABRIK spine; a chest tracker is a chest goal plus a
// fading position pull (fitted, see ChestGoal*). Per frame:
//   read pose      = the rest spine hung from the hip target (VRChat's IK reads the animated pose; its spine animation is unknown)
//   chest target   = head target * (chest relative to head in the read pose), turned by the hands (AdjustChestByHands)
//   TranslatePelvis: the body follows the head by bodyPosStiffness (0.55), moved back when the head goes down
//   FABRIK pass    -> Bend(spine..chest toward the chest target, neckStiffness) -> translate to the head -> FABRIK pass
//   Bend(neck..head toward the head target)
//   SolvePelvis    : the body back onto the hip target, then the chest-neck-head triangle aims at the head target.
// Measured differences from the plain reference solve (VRChat Lock Hip / Head / Both, 7 HUMR windows):
//   - the pelvis keeps the tracker's rotation: FABRIK and Bend never swing the pelvis bone (VRChat's hips stay within
//     ~1 deg of the tracker; the plain reference solve swings them 5-20 deg),
//   - SolvePelvis skips the first (spine-chest-head, reference weight 0.9) triangle and solves only chest-neck-head,
//   - the head target of that triangle is pushed out to at least the spine's current pelvis-head distance, so the spine
//     doesn't fold when the head comes closer than its length (Lock Hip: VRChat's head stays up to 9 cm above the
//     headset in a deep bend instead of the spine folding),
//   - spine lock = how much of the remaining head gap moves the whole body (a final translation toward the head).
namespace VRMR.Fbik
{
    public struct VrcSpineInput
    {
        public Vector3 HeadPos; public Quaternion HeadRot;      // head bone target
        public Vector3 PelvisPos; public Quaternion PelvisRot;  // hip bone target (the read pose already hangs from it)
        public Quaternion Anchor;                               // avatar frame of the hip target
        public Vector3 Up;                                      // player up
        public bool Hands; public Vector3 LeftHand, RightHand;  // hand IK targets, for the chest turn
        public float HeadHeight;                                // rest head height above the feet
        public float HeadFollow;                                // 0 = hips stay on the tracker, 1 = the body moves until the head is reached
        public bool HasChestGoal;                               // chest tracker: its bound chest bone pose (the chest goal)
        public Vector3 ChestGoalPos; public Quaternion ChestGoalRot;
    }

    public static class VrcSpineCore
    {
        // reference defaults unless noted
        public static float BodyPosStiffness = 0.55f;
        public static float MaintainPelvisPosition = 0.2f;
        public static float NeckStiffness = 0.2f;
        public static float ChestClampWeight = 0.5f;
        public static float HeadClampWeight = 0.6f;
        public static float RotateChestByHands = 1f;
        public static float MoveBodyBackWhenCrouching = 0.5f;
        /// <summary>VRChat-measured: push the triangle's head target out to the spine's current length (see header).</summary>
        public static bool ExtendHeadTarget = true;
        /// <summary>VRChat-measured: weight of the first SolvePelvis triangle (spine-chest-head); reference default 0.9.</summary>
        public static float FirstTriangleWeight = 0f;
        // Chest tracker, fitted on VRChat's own IK (HUMR, an 11-point test take, 7 windows, Lock Hip / Head / Both): VRChat's
        // chest follows the tracker's bound chest point within 1.4-2.1 cm (mean) standing, but not in a deep crouch (4 cm).
        // Rotation goal only (plain chest goal): chest 1.3-3.3 cm; plus the full position pull: 0.8-1.3 cm standing but
        // 6 cm crouching; the pull fading beyond 9% of the spine length: 0.75-2.4 cm everywhere.
        /// <summary>Chest tracker (chest goal): weight of turning the chest's forward toward the goal.</summary>
        public static float ChestGoalWeight = 1f;
        /// <summary>Chest tracker: 0 = plain chest goal (forward toward a point in front of the tracker); 1 = bend toward the tracker's full rotation.</summary>
        public static float ChestGoalRotation = 0f;
        /// <summary>How far in front of the bound chest point the goal point lies (avatar metres per metre of pelvis-head distance).</summary>
        public static float ChestGoalForward = 1f;
        /// <summary>Chest tracker: how far the spine bone swings the chest toward the tracker's bound chest point after the pelvis is placed (0 = rotation goal only).</summary>
        public static float ChestGoalPositionWeight = 1f;
        /// <summary>Chest tracker: beyond this distance between the spine's chest and the tracker's chest point (a fraction of the pelvis-head distance) the pull fades (weight * reach / distance); 0 = no fade.</summary>
        public static float ChestGoalPositionReach = 0.09f;

        // virtual bones: 0 pelvis, 1 spine, 2 chest, 3 neck, 4 head (world)
        public const int Pelvis = 0, Spine = 1, Chest = 2, Neck = 3, Head = 4, Count = 5;

        public static void Solve(in VrcSpineInput i, Vector3[] p, Quaternion[] r)
        {
            Vector3 up = i.Up.sqrMagnitude > 1e-8f ? i.Up.normalized : Vector3.up;
            // ---- Read (relative rotations from the read pose)
            Quaternion chestRelative = Quaternion.Inverse(r[Head]) * r[Chest];
            float sizeMlp = Vector3.Distance(p[Pelvis], p[Head]) / 0.7f;
            Vector3 fwd = Vector3.ProjectOnPlane(i.Anchor * Vector3.forward, up);
            Quaternion root = fwd.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(fwd, up) : Quaternion.identity;
            Vector3 headPos = i.HeadPos;
            Quaternion headRot = i.HeadRot;
            Vector3 chestFwdLocal = Quaternion.Inverse(r[Chest]) * (root * Vector3.forward); // chest forward (read pose)

            // ---- chest target (CalculateChestTargetRotation + AdjustChestByHands)
            Quaternion chestTarget = headRot * chestRelative;
            if (i.Hands && RotateChestByHands > 0f && sizeMlp > 1e-5f)
            {
                Quaternion h = Quaternion.Inverse(i.Anchor);
                Vector3 pl = h * (i.LeftHand - headPos) / sizeMlp, pr = h * (i.RightHand - headPos) / sizeMlp;
                float cx = pl.x * Mathf.Abs(pl.x) + pl.z * Mathf.Abs(pl.z) + pr.x * Mathf.Abs(pr.x) - pr.z * Mathf.Abs(pr.z);
                cx *= 5f * RotateChestByHands;
                chestTarget = Quaternion.AngleAxis(Mathf.Atan2(cx, 1f) * Mathf.Rad2Deg, root * Vector3.up) * chestTarget;
                float tx = (pl.y - pr.y) * 0.5f * RotateChestByHands;
                chestTarget = Quaternion.AngleAxis(Mathf.Atan2(tx, 1f) * Mathf.Rad2Deg, root * Vector3.back) * chestTarget;
            }
            Vector3 animatedPelvis = p[Pelvis];

            // ---- TranslatePelvis (the pelvis already has the target rotation: pelvis rotation weight 1)
            Vector3 delta = headPos - p[Head];
            float deltaY = Mathf.Abs(Vector3.Dot(delta, up));
            delta += (root * Vector3.forward) * (deltaY * -MoveBodyBackWhenCrouching * i.HeadHeight);
            Move(p, delta * BodyPosStiffness);

            FabrikPass(p, r, animatedPelvis, headPos);
            BendChest(p, r, chestTarget);
            if (i.HasChestGoal && ChestGoalWeight > 0f)
            {
                // chest goal: Bend(pelvis..chest) so the chest's forward points at the goal; the goal is a point in front
                // of the tracker-bound chest (its forward carried by the tracker), or the tracker's full rotation
                Vector3 goal = i.ChestGoalPos + (i.ChestGoalRot * chestFwdLocal) * (sizeMlp * 0.7f * ChestGoalForward);
                Quaternion c = IkQuaternionExt.FromToRotation(r[Chest] * chestFwdLocal, goal - p[Chest]) * r[Chest];
                if (ChestGoalRotation > 0f) c = Quaternion.Slerp(c, i.ChestGoalRot, ChestGoalRotation);
                BendChestGoal(p, r, c, ChestGoalWeight);
            }
            Move(p, headPos - p[Head]);           // InverseTranslateToHead
            FabrikPass(p, r, animatedPelvis, headPos);
            BendHead(p, r, headRot);

            // ---- SolvePelvis (pelvisPositionWeight 1)
            Quaternion headSolved = r[Head];
            Move(p, i.PelvisPos - p[Pelvis]);
            Vector3 bendNormal = i.Anchor * Vector3.right;
            Vector3 target = headPos;
            if (ExtendHeadTarget)
            {
                Vector3 d = headPos - p[Pelvis];
                float len = d.magnitude, cur = Vector3.Distance(p[Head], p[Pelvis]);
                if (len > 1e-6f && cur > len) target = p[Pelvis] + d * (cur / len);
            }
            if (FirstTriangleWeight > 0f) Triangle(p, r, Spine, Chest, Head, target, bendNormal, FirstTriangleWeight);
            if (i.HasChestGoal && ChestGoalPositionWeight > 0f)
            {
                // swing the spine bone so the chest moves toward the tracker's chest point (lengths kept)
                float w = ChestGoalPositionWeight, dist = Vector3.Distance(p[Chest], i.ChestGoalPos), reach = ChestGoalPositionReach * sizeMlp * 0.7f;
                if (reach > 0f && dist > reach) w *= reach / dist;
                Vector3 from = p[Chest] - p[Spine], to = Vector3.Lerp(p[Chest], i.ChestGoalPos, w) - p[Spine];
                if (from.sqrMagnitude > 1e-10f && to.sqrMagnitude > 1e-10f) RotateAround(p, r, Spine, p[Spine], IkQuaternionExt.FromToRotation(from, to));
            }
            Triangle(p, r, Chest, Neck, Head, target, bendNormal, 1f);
            r[Head] = headSolved;
            r[Pelvis] = i.PelvisRot;

            // ---- spine lock: move the body toward the head (a translation toward the head after the spine)
            if (i.HeadFollow > 0f) Move(p, (headPos - p[Head]) * Mathf.Clamp01(i.HeadFollow));
        }

        static void Move(Vector3[] p, Vector3 d)
        {
            for (int k = 0; k < Count; k++) p[k] += d;
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

        // FABRIK pass with the pelvis held: one FABRIK iteration over spine..head, rooted where the pelvis carries the
        // spine bone; the target is pushed out to at least the chain's current spine-head distance (minNormalizedTargetDistance 1).
        [System.ThreadStatic] static float[] _len;
        [System.ThreadStatic] static Vector3[] _axis;
        static void FabrikPass(Vector3[] p, Quaternion[] r, Vector3 animatedPelvis, Vector3 headPos)
        {
            if (_len == null) { _len = new float[Count]; _axis = new Vector3[Count]; }
            Vector3 start = Vector3.Lerp(p[Pelvis], animatedPelvis, MaintainPelvisPosition);
            Vector3 offset = start - p[Pelvis];
            for (int k = Spine; k < Head; k++)
            {
                Vector3 v = p[k + 1] - p[k];
                _len[k] = v.magnitude;
                _axis[k] = Quaternion.Inverse(r[k]) * v;
            }
            Vector3 root = p[Spine] + offset;
            float length = Vector3.Distance(p[Spine], p[Head]);
            Vector3 td = headPos - root;
            float tl = td.magnitude;
            Vector3 target = tl > 1e-6f ? root + td / tl * Mathf.Max(length, tl) : headPos;
            p[Head] = target;
            for (int k = Head - 1; k >= Spine; k--) p[k] = Joint(p[k], p[k + 1], _len[k]);
            p[Pelvis] += offset;
            p[Spine] = root;
            for (int k = Spine + 1; k <= Head; k++) p[k] = Joint(p[k], p[k - 1], _len[k - 1]);
            for (int k = Spine; k < Head; k++)
            {
                Quaternion q = IkQuaternionExt.FromToRotation(r[k] * _axis[k], p[k + 1] - p[k]);
                for (int j = k; j < Count; j++) r[j] = Normalize(q * r[j]);
            }
        }

        static Vector3 Joint(Vector3 pos1, Vector3 pos2, float length)
        {
            Vector3 d = pos1 - pos2;
            float m = d.magnitude;
            return m > 1e-8f ? pos2 + d * (length / m) : pos2;
        }

        // Bend(pelvis..chest, chestTarget, chestClampWeight, neckStiffness) with the pelvis left out (steps 0.4 / 0.45)
        static void BendChest(Vector3[] p, Quaternion[] r, Quaternion chestTarget)
        {
            if (NeckStiffness <= 0f) return;
            Quaternion q = Clamp(chestTarget * Quaternion.Inverse(r[Chest]), ChestClampWeight);
            RotateAround(p, r, Spine, p[Spine], Quaternion.Slerp(Quaternion.identity, q, 0.4f * NeckStiffness));
            RotateAround(p, r, Chest, p[Chest], Quaternion.Slerp(Quaternion.identity, q, 0.45f * NeckStiffness));
        }

        // Bend(pelvis..chest, chest goal rotation, chestClampWeight, chestGoalWeight) with the pelvis left out (steps 0.4 / 0.45)
        static void BendChestGoal(Vector3[] p, Quaternion[] r, Quaternion target, float w)
        {
            Quaternion q = Clamp(target * Quaternion.Inverse(r[Chest]), ChestClampWeight);
            RotateAround(p, r, Spine, p[Spine], Quaternion.Slerp(Quaternion.identity, q, 0.4f * w));
            RotateAround(p, r, Chest, p[Chest], Quaternion.Slerp(Quaternion.identity, q, 0.45f * w));
        }

        // Bend(neck..head, headRotation, headClampWeight, uniform)
        static void BendHead(Vector3[] p, Quaternion[] r, Quaternion headRot)
        {
            Quaternion q = Clamp(headRot * Quaternion.Inverse(r[Head]), HeadClampWeight);
            Quaternion half = Quaternion.Slerp(Quaternion.identity, q, 0.5f);
            RotateAround(p, r, Neck, p[Neck], half);
            RotateAround(p, r, Head, p[Head], half);
        }

        // Soft clamp of a rotation, with 2 sine smoothing steps
        static Quaternion Clamp(Quaternion q, float clampWeight)
        {
            if (clampWeight >= 1f) return Quaternion.identity;
            if (clampWeight <= 0f) return q;
            float angle = Quaternion.Angle(Quaternion.identity, q);
            float dot = 1f - angle / 180f;
            float targetClamp = dot < 1f ? Mathf.Clamp(1f - (clampWeight - dot) / (1f - dot), 0f, 1f) : 1f;
            float clamp = Mathf.Clamp(dot / clampWeight, 0f, 1f);
            for (int k = 0; k < 2; k++) clamp = Mathf.Sin(clamp * Mathf.PI * 0.5f);
            return Quaternion.Slerp(Quaternion.identity, q, clamp * targetClamp);
        }

        // Two-bone solve in the bend plane
        static void Triangle(Vector3[] p, Quaternion[] r, int a, int b, int c, Vector3 target, Vector3 bendNormal, float w)
        {
            if (w <= 0f) return;
            target = Vector3.Lerp(p[c], target, w);
            Vector3 dir = target - p[a];
            float sq = dir.sqrMagnitude;
            if (sq < 1e-12f) return;
            float len = Mathf.Sqrt(sq);
            float sq1 = (p[b] - p[a]).sqrMagnitude, sq2 = (p[c] - p[b]).sqrMagnitude;
            Vector3 bendDir = Vector3.Cross(dir, bendNormal);
            float x = (len * len + (sq1 - sq2)) / 2f / len;
            float y = Mathf.Sqrt(Mathf.Max(sq1 - x * x, 0f));
            Vector3 toBend = Quaternion.LookRotation(dir, bendDir) * new Vector3(0f, y, x);
            Quaternion q1 = IkQuaternionExt.FromToRotation(p[b] - p[a], toBend);
            if (w < 1f) q1 = Quaternion.Lerp(Quaternion.identity, q1, w);
            RotateAround(p, r, a, p[a], q1);
            Quaternion q2 = IkQuaternionExt.FromToRotation(p[c] - p[b], target - p[b]);
            if (w < 1f) q2 = Quaternion.Lerp(Quaternion.identity, q2, w);
            RotateAround(p, r, b, p[b], q2);
        }
    }
}
