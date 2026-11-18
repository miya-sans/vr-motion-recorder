// Generated from the measurements described in docs/dev/unity-humanoid.md (do not edit by hand).

namespace VRMR
{
    /// <summary>
    ///     Constants of Unity's Humanoid muscle space, per HumanBodyBones index (measured, see
    ///     docs/dev/unity-humanoid.md).
    /// </summary>
    static class HumanoidTables
    {
        public const int MuscleCount = 95;
        public const float TotalMass = 82.5f;

        /// <summary>Curve attribute of each muscle in an AnimationClip (HumanTrait order).</summary>
        public static readonly string[] MuscleAttribute =
        {
            "Spine Front-Back", "Spine Left-Right", "Spine Twist Left-Right", "Chest Front-Back",
            "Chest Left-Right", "Chest Twist Left-Right", "UpperChest Front-Back", "UpperChest Left-Right",
            "UpperChest Twist Left-Right", "Neck Nod Down-Up", "Neck Tilt Left-Right", "Neck Turn Left-Right",
            "Head Nod Down-Up", "Head Tilt Left-Right", "Head Turn Left-Right", "Left Eye Down-Up",
            "Left Eye In-Out", "Right Eye Down-Up", "Right Eye In-Out", "Jaw Close",
            "Jaw Left-Right", "Left Upper Leg Front-Back", "Left Upper Leg In-Out", "Left Upper Leg Twist In-Out",
            "Left Lower Leg Stretch", "Left Lower Leg Twist In-Out", "Left Foot Up-Down", "Left Foot Twist In-Out",
            "Left Toes Up-Down", "Right Upper Leg Front-Back", "Right Upper Leg In-Out", "Right Upper Leg Twist In-Out",
            "Right Lower Leg Stretch", "Right Lower Leg Twist In-Out", "Right Foot Up-Down", "Right Foot Twist In-Out",
            "Right Toes Up-Down", "Left Shoulder Down-Up", "Left Shoulder Front-Back", "Left Arm Down-Up",
            "Left Arm Front-Back", "Left Arm Twist In-Out", "Left Forearm Stretch", "Left Forearm Twist In-Out",
            "Left Hand Down-Up", "Left Hand In-Out", "Right Shoulder Down-Up", "Right Shoulder Front-Back",
            "Right Arm Down-Up", "Right Arm Front-Back", "Right Arm Twist In-Out", "Right Forearm Stretch",
            "Right Forearm Twist In-Out", "Right Hand Down-Up", "Right Hand In-Out", "LeftHand.Thumb.1 Stretched",
            "LeftHand.Thumb.Spread", "LeftHand.Thumb.2 Stretched", "LeftHand.Thumb.3 Stretched",
            "LeftHand.Index.1 Stretched",
            "LeftHand.Index.Spread", "LeftHand.Index.2 Stretched", "LeftHand.Index.3 Stretched",
            "LeftHand.Middle.1 Stretched",
            "LeftHand.Middle.Spread", "LeftHand.Middle.2 Stretched", "LeftHand.Middle.3 Stretched",
            "LeftHand.Ring.1 Stretched",
            "LeftHand.Ring.Spread", "LeftHand.Ring.2 Stretched", "LeftHand.Ring.3 Stretched",
            "LeftHand.Little.1 Stretched",
            "LeftHand.Little.Spread", "LeftHand.Little.2 Stretched", "LeftHand.Little.3 Stretched",
            "RightHand.Thumb.1 Stretched",
            "RightHand.Thumb.Spread", "RightHand.Thumb.2 Stretched", "RightHand.Thumb.3 Stretched",
            "RightHand.Index.1 Stretched",
            "RightHand.Index.Spread", "RightHand.Index.2 Stretched", "RightHand.Index.3 Stretched",
            "RightHand.Middle.1 Stretched",
            "RightHand.Middle.Spread", "RightHand.Middle.2 Stretched", "RightHand.Middle.3 Stretched",
            "RightHand.Ring.1 Stretched",
            "RightHand.Ring.Spread", "RightHand.Ring.2 Stretched", "RightHand.Ring.3 Stretched",
            "RightHand.Little.1 Stretched",
            "RightHand.Little.Spread", "RightHand.Little.2 Stretched", "RightHand.Little.3 Stretched"
        };

        /// <summary>Default range of each muscle in degrees: the angle at -1 and at +1.</summary>
        public static readonly float[] MuscleMin =
        {
            -40f, -40f, -40f, -40f, -40f, -40f, -20f, -20f, -20f, -40f, -40f, -40f,
            -40f, -40f, -40f, -10f, -20f, -10f, -20f, -10f, -10f, -90f, -60f, -60f,
            -80f, -90f, -50f, -30f, -50f, -90f, -60f, -60f, -80f, -90f, -50f, -30f,
            -50f, -15f, -15f, -60f, -100f, -90f, -80f, -90f, -80f, -40f, -15f, -15f,
            -60f, -100f, -90f, -80f, -90f, -80f, -40f, -20f, -25f, -40f, -40f, -50f,
            -20f, -45f, -45f, -50f, -7.5f, -45f, -45f, -50f, -7.5f, -45f, -45f, -50f,
            -20f, -45f, -45f, -20f, -25f, -40f, -40f, -50f, -20f, -45f, -45f, -50f,
            -7.5f, -45f, -45f, -50f, -7.5f, -45f, -45f, -50f, -20f, -45f, -45f
        };

        public static readonly float[] MuscleMax =
        {
            40f, 40f, 40f, 40f, 40f, 40f, 20f, 20f, 20f, 40f, 40f, 40f,
            40f, 40f, 40f, 15f, 20f, 15f, 20f, 10f, 10f, 50f, 60f, 60f,
            80f, 90f, 50f, 30f, 50f, 50f, 60f, 60f, 80f, 90f, 50f, 30f,
            50f, 30f, 15f, 100f, 100f, 90f, 80f, 90f, 80f, 40f, 30f, 15f,
            100f, 100f, 90f, 80f, 90f, 80f, 40f, 20f, 25f, 35f, 35f, 50f,
            20f, 45f, 45f, 50f, 7.5f, 45f, 45f, 50f, 7.5f, 45f, 45f, 50f,
            20f, 45f, 45f, 20f, 25f, 35f, 35f, 50f, 20f, 45f, 45f, 50f,
            7.5f, 45f, 45f, 50f, 7.5f, 45f, 45f, 50f, 20f, 45f, 45f
        };

        /// <summary>Muscle index of each bone's twist (X), second (Y) and third (Z) axis; -1 = the bone has no such muscle.</summary>
        public static readonly int[,] MuscleOfBone =
        {
            { -1, -1, -1 }, { 23, 22, 21 }, { 31, 30, 29 }, { 25, -1, 24 }, { 33, -1, 32 },
            { -1, 27, 26 }, { -1, 35, 34 }, { 2, 1, 0 }, { 5, 4, 3 }, { 11, 10, 9 },
            { 14, 13, 12 }, { -1, 38, 37 }, { -1, 47, 46 }, { 41, 40, 39 }, { 50, 49, 48 },
            { 43, -1, 42 }, { 52, -1, 51 }, { -1, 45, 44 }, { -1, 54, 53 }, { -1, -1, 28 },
            { -1, -1, 36 }, { -1, 16, 15 }, { -1, 18, 17 }, { -1, 20, 19 }, { -1, 56, 55 },
            { -1, -1, 57 }, { -1, -1, 58 }, { -1, 60, 59 }, { -1, -1, 61 }, { -1, -1, 62 },
            { -1, 64, 63 }, { -1, -1, 65 }, { -1, -1, 66 }, { -1, 68, 67 }, { -1, -1, 69 },
            { -1, -1, 70 }, { -1, 72, 71 }, { -1, -1, 73 }, { -1, -1, 74 }, { -1, 76, 75 },
            { -1, -1, 77 }, { -1, -1, 78 }, { -1, 80, 79 }, { -1, -1, 81 }, { -1, -1, 82 },
            { -1, 84, 83 }, { -1, -1, 85 }, { -1, -1, 86 }, { -1, 88, 87 }, { -1, -1, 89 },
            { -1, -1, 90 }, { -1, 92, 91 }, { -1, -1, 93 }, { -1, -1, 94 }, { 8, 7, 6 }
        };

        /// <summary>Sign applied to each axis angle (mirrors left and right).</summary>
        public static readonly sbyte[,] AxisSign =
        {
            { 1, 1, 1 }, { 1, 1, 1 }, { -1, -1, 1 }, { 1, 1, -1 }, { -1, 1, -1 }, { 1, 1, 1 },
            { 1, -1, 1 }, { 1, 1, 1 }, { 1, 1, 1 }, { 1, 1, 1 }, { 1, 1, 1 }, { 1, 1, -1 },
            { 1, 1, 1 }, { 1, 1, -1 }, { -1, 1, 1 }, { 1, 1, -1 }, { -1, 1, 1 }, { 1, 1, -1 },
            { 1, 1, 1 }, { 1, 1, 1 }, { 1, 1, 1 }, { 1, 1, -1 }, { 1, -1, -1 }, { 1, 1, -1 },
            { -1, -1, 1 }, { -1, 1, 1 }, { -1, 1, 1 }, { -1, -1, -1 }, { -1, 1, -1 }, { -1, 1, -1 },
            { -1, -1, -1 }, { -1, 1, -1 }, { -1, 1, -1 }, { -1, 1, -1 }, { -1, 1, -1 }, { -1, 1, -1 },
            { -1, 1, -1 }, { -1, 1, -1 }, { -1, 1, -1 }, { 1, -1, -1 }, { 1, 1, -1 }, { 1, 1, -1 },
            { 1, -1, 1 }, { 1, 1, 1 }, { 1, 1, 1 }, { 1, -1, 1 }, { 1, 1, 1 }, { 1, 1, 1 },
            { 1, 1, 1 }, { 1, 1, 1 }, { 1, 1, 1 }, { 1, 1, 1 }, { 1, 1, 1 }, { 1, 1, 1 },
            { 1, 1, 1 }
        };

        /// <summary>World rotation of the zero-muscle pose relative to the T-pose, with the parent in its T-pose orientation.</summary>
        public static readonly Quat[] ZeroPose =
        {
            new(0f, 0f, 0f, 1f), // Hips
            new(-0.25886488f, 0f, 0f, 0.96591365f), // LeftUpperLeg
            new(-0.25886488f, 0f, 0f, 0.96591365f), // RightUpperLeg
            new(0.6427429f, 0f, 0f, 0.76608205f), // LeftLowerLeg
            new(0.6427429f, 0f, 0f, 0.76608205f), // RightLowerLeg
            new(0f, 0f, 0f, 1f), // LeftFoot
            new(0f, 0f, 0f, 1f), // RightFoot
            new(0f, 0f, 0f, 1f), // Spine
            new(0f, 0f, 0f, 1f), // Chest
            new(0f, 0f, 0f, 1f), // Neck
            new(0f, 0f, 0f, 1f), // Head
            new(0f, 0f, 0f, 1f), // LeftShoulder
            new(0f, 0f, 0f, 1f), // RightShoulder
            new(0f, 0.24421026f, 0.33168855f, 0.9112323f), // LeftUpperArm
            new(0f, -0.24421026f, -0.33168855f, 0.9112323f), // RightUpperArm
            new(0f, 0.6427429f, 0f, 0.76608217f), // LeftLowerArm
            new(0f, -0.6427429f, 0f, 0.76608217f), // RightLowerArm
            new(0f, 0f, 0f, 1f), // LeftHand
            new(0f, 0f, 0f, 1f), // RightHand
            new(0f, 0f, 0f, 1.0000001f), // LeftToes
            new(0f, 0f, 0f, 1.0000001f), // RightToes
            new(0f, 0f, 0f, 1.0000001f), // LeftEye
            new(0f, 0f, 0f, 1.0000001f), // RightEye
            new(0.08963773f, 0f, 0f, 0.99597466f), // Jaw
            new(0f, 0.1230915f, 0.12309146f, 0.9847321f), // LeftThumbProximal
            new(0f, -0.1961162f, 0f, 0.9805809f), // LeftThumbIntermediate
            new(0f, -0.19611621f, 0f, 0.9805809f), // LeftThumbDistal
            new(0f, 0.076402135f, 0.28650802f, 0.9550267f), // LeftIndexProximal
            new(0f, 0f, 0.3133775f, 0.9496287f), // LeftIndexIntermediate
            new(0f, 0f, 0.3133775f, 0.9496287f), // LeftIndexDistal
            new(0f, 0.038284965f, 0.28713727f, 0.9571242f), // LeftMiddleProximal
            new(0f, 0f, 0.3133775f, 0.9496287f), // LeftMiddleIntermediate
            new(0f, 0f, 0.3133775f, 0.9496287f), // LeftMiddleDistal
            new(0f, -0.038284965f, 0.28713727f, 0.9571242f), // LeftRingProximal
            new(0f, 0f, 0.3133775f, 0.9496287f), // LeftRingIntermediate
            new(0f, 0f, 0.3133775f, 0.9496287f), // LeftRingDistal
            new(0f, -0.076402135f, 0.28650802f, 0.9550267f), // LeftLittleProximal
            new(0f, 0f, 0.3133775f, 0.9496287f), // LeftLittleIntermediate
            new(0f, 0f, 0.3133775f, 0.9496287f), // LeftLittleDistal
            new(0f, -0.12309149f, -0.12309149f, 0.9847321f), // RightThumbProximal
            new(0f, 0.1961162f, 0f, 0.9805809f), // RightThumbIntermediate
            new(0f, 0.19611621f, 0f, 0.9805809f), // RightThumbDistal
            new(0f, -0.076402135f, -0.28650802f, 0.9550267f), // RightIndexProximal
            new(0f, 0f, -0.3133775f, 0.9496287f), // RightIndexIntermediate
            new(0f, 0f, -0.3133775f, 0.9496287f), // RightIndexDistal
            new(0f, -0.03828497f, -0.28713727f, 0.95712423f), // RightMiddleProximal
            new(0f, 0f, -0.3133775f, 0.9496287f), // RightMiddleIntermediate
            new(0f, 0f, -0.3133775f, 0.9496287f), // RightMiddleDistal
            new(0f, 0.03828497f, -0.28713727f, 0.95712423f), // RightRingProximal
            new(0f, 0f, -0.3133775f, 0.9496287f), // RightRingIntermediate
            new(0f, 0f, -0.3133775f, 0.9496287f), // RightRingDistal
            new(0f, 0.076402135f, -0.28650802f, 0.9550267f), // RightLittleProximal
            new(0f, 0f, -0.3133775f, 0.9496287f), // RightLittleIntermediate
            new(0f, 0f, -0.3133775f, 0.9496287f), // RightLittleDistal
            new(0f, 0f, 0f, 1f) // UpperChest
        };

        /// <summary>
        ///     Muscle axes in the canonical T-pose (world): the frame itself for bones with fixed axes, else its X is
        ///     replaced by the bone direction.
        /// </summary>
        public static readonly Quat[] CanonicalAxes =
        {
            new(-0.5f, -0.5f, 0.5f, 0.5f), // Hips
            new(-0.5f, 0.5f, -0.5f, 0.5f), // LeftUpperLeg
            new(-0.5f, 0.5f, -0.5f, 0.5f), // RightUpperLeg
            new(-0.5f, 0.5f, -0.5f, 0.5f), // LeftLowerLeg
            new(-0.5f, 0.5f, -0.5f, 0.5f), // RightLowerLeg
            new(-0.5f, 0.5f, -0.5f, 0.5f), // LeftFoot
            new(-0.5f, 0.5f, -0.5f, 0.5f), // RightFoot
            new(-0.5f, -0.5f, 0.5f, 0.5f), // Spine
            new(-0.5f, -0.5f, 0.5f, 0.5f), // Chest
            new(-0.5f, -0.5f, 0.5f, 0.5f), // Neck
            new(-0.5f, -0.5f, 0.5f, 0.5f), // Head
            new(0f, 0f, 1f, 0f), // LeftShoulder
            new(0f, 0f, 0f, 1f), // RightShoulder
            new(0f, 0f, 1f, 0f), // LeftUpperArm
            new(0f, 0f, 0f, 1f), // RightUpperArm
            new(0f, 0.7071068f, 0.7071068f, 0f), // LeftLowerArm
            new(-0.7071068f, 0f, 0f, 0.7071068f), // RightLowerArm
            new(0f, 0f, 1f, 0f), // LeftHand
            new(0f, 0f, 0f, 1f), // RightHand
            new(0.7071068f, 0f, 0.7071068f, 0f), // LeftToes
            new(0.7071068f, 0f, 0.7071068f, 0f), // RightToes
            new(0.7071068f, 0f, 0.7071068f, 0f), // LeftEye
            new(0.7071068f, 0f, 0.7071068f, 0f), // RightEye
            new(0.7071068f, 0f, 0.7071068f, 0f), // Jaw
            new(0.20490849f, 0.6767663f, 0.6767662f, -0.20490849f), // LeftThumbProximal
            new(0.20490819f, 0.6767664f, 0.6767664f, -0.20490819f), // LeftThumbIntermediate
            new(0.20490819f, 0.6767664f, 0.67676634f, -0.20490819f), // LeftThumbDistal
            new(0f, 0f, 1f, 0f), // LeftIndexProximal
            new(0f, 0f, 1f, 0f), // LeftIndexIntermediate
            new(0f, 0f, 1f, 0f), // LeftIndexDistal
            new(0f, 0f, 1f, 0f), // LeftMiddleProximal
            new(0f, 0f, 1f, 0f), // LeftMiddleIntermediate
            new(0f, 0f, 1f, 0f), // LeftMiddleDistal
            new(0f, 0f, 1f, 0f), // LeftRingProximal
            new(0f, 0f, 1f, 0f), // LeftRingIntermediate
            new(0f, 0f, 1f, 0f), // LeftRingDistal
            new(0f, 0f, 1f, 0f), // LeftLittleProximal
            new(0f, 0f, 1f, 0f), // LeftLittleIntermediate
            new(0f, 0f, 1f, 0f), // LeftLittleDistal
            new(-0.6767662f, -0.20490849f, -0.20490849f, 0.6767663f), // RightThumbProximal
            new(-0.6767664f, -0.20490819f, -0.20490819f, 0.6767664f), // RightThumbIntermediate
            new(-0.67676634f, -0.20490819f, -0.20490819f, 0.6767664f), // RightThumbDistal
            new(0f, 0f, 0f, 1f), // RightIndexProximal
            new(0f, 0f, 0f, 1f), // RightIndexIntermediate
            new(0f, 0f, 0f, 1f), // RightIndexDistal
            new(0f, 0f, 0f, 1f), // RightMiddleProximal
            new(0f, 0f, 0f, 1f), // RightMiddleIntermediate
            new(0f, 0f, 0f, 1f), // RightMiddleDistal
            new(0f, 0f, 0f, 1f), // RightRingProximal
            new(0f, 0f, 0f, 1f), // RightRingIntermediate
            new(0f, 0f, 0f, 1f), // RightRingDistal
            new(0f, 0f, 0f, 1f), // RightLittleProximal
            new(0f, 0f, 0f, 1f), // RightLittleIntermediate
            new(0f, 0f, 0f, 1f), // RightLittleDistal
            new(-0.5f, -0.5f, 0.5f, 0.5f) // UpperChest
        };

        /// <summary>Mass of each bone's segment in the centre of mass (parts of 82.5; a missing bone's mass goes to its parent).</summary>
        public static readonly float[] Mass =
        {
            12f, 10f, 10f, 4f, 4f, 0.8f, 0.8f, 2.5f, 12f, 1f, 4f, 0.5f, 0.5f, 2f, 2f, 1.5f, 1.5f, 0.5f, 0.5f, 0.2f,
            0.2f, 0f, 0f, 0f,
            0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f,
            0f, 0f, 0f, 12f
        };
    }
}
