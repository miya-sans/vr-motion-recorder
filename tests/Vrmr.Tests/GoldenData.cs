using System.Collections.Generic;

namespace VRMR.Tests
{
    /// <summary>The recorded result of the synthetic solve (see SyntheticSolveTests.BonePositionsMatchTheRecordedOnes).</summary>
    public static class GoldenData
    {
        /// <summary>Standing, deepest in the squat, after the turn, waving.</summary>
        public static readonly double[] Times = { 1.0, 2.25, 4.6, 5.7 };
        public static readonly string[] Bones = { "Hips", "Head", "LeftHand", "RightHand", "LeftLowerLeg", "RightFoot" };

        public static IEnumerable<object[]> Rows => new[]
        {
            new object[] { 1.0, "Hips", 0.00058f, 0.91656f, -0.11283f },
            new object[] { 1.0, "Head", 0.00007f, 1.49704f, -0.10284f },
            new object[] { 1.0, "LeftHand", -0.67821f, 1.37648f, -0.08908f },
            new object[] { 1.0, "RightHand", 0.67861f, 1.37644f, -0.08966f },
            new object[] { 1.0, "LeftLowerLeg", -0.08435f, 0.47175f, -0.10088f },
            new object[] { 1.0, "RightFoot", 0.08512f, 0.07742f, -0.12801f },
            new object[] { 2.25, "Hips", 0.00053f, 0.56933f, -0.11288f },
            new object[] { 2.25, "Head", 0.00032f, 1.14947f, -0.10296f },
            new object[] { 2.25, "LeftHand", -0.54340f, 1.02822f, 0.23106f },
            new object[] { 2.25, "RightHand", 0.54434f, 1.02818f, 0.23075f },
            new object[] { 2.25, "LeftLowerLeg", -0.08415f, 0.28376f, 0.20915f },
            new object[] { 2.25, "RightFoot", 0.08525f, 0.07733f, -0.12805f },
            new object[] { 4.6, "Hips", -0.02769f, 0.91654f, -0.08468f },
            new object[] { 4.6, "Head", -0.01818f, 1.49698f, -0.08448f },
            new object[] { 4.6, "LeftHand", -0.00558f, 1.37624f, 0.58998f },
            new object[] { 4.6, "RightHand", 0.00484f, 1.43938f, -0.76025f },
            new object[] { 4.6, "LeftLowerLeg", -0.05541f, 0.47565f, -0.04993f },
            new object[] { 4.6, "RightFoot", 0.08414f, 0.08566f, -0.12833f },
            new object[] { 5.7, "Hips", -0.02769f, 0.91662f, -0.08455f },
            new object[] { 5.7, "Head", -0.01820f, 1.49700f, -0.08439f },
            new object[] { 5.7, "LeftHand", -0.00907f, 1.37580f, 0.57806f },
            new object[] { 5.7, "RightHand", 0.04991f, 1.94041f, -0.27319f },
            new object[] { 5.7, "LeftLowerLeg", -0.05536f, 0.47571f, -0.04979f },
            new object[] { 5.7, "RightFoot", 0.08402f, 0.08573f, -0.12842f },
        };
    }
}
