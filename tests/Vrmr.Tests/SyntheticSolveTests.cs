using System;
using System.Globalization;
using System.Text;
using Xunit;

namespace VRMR.Tests
{
    /// <summary>End to end without private data: the scripted take solved onto the built-in mannequin.</summary>
    public class SyntheticSolveTests
    {
        static readonly Lazy<BakedClip> Solved = new Lazy<BakedClip>(() =>
        {
            BodySolverSetup.Register();
            var cal = new CalibrationSettings { Height = CalHeightMode.RealHeight, HeightValue = SyntheticTake.Height, FollowVrc = false };
            return PoseBuilder.Bake(SyntheticTake.Create(), Mannequin.Profile(), new BakeOptions { Fps = 60, Calibration = cal });
        });

        static Vec3 Bone(BakedClip c, double t, int bone)
        {
            var pos = new Vec3[c.Profile.Nodes.Count]; var rot = new Quat[c.Profile.Nodes.Count];
            c.EvaluateNodes(c.FrameAt(t), pos, rot);
            return pos[c.Profile.Human[bone]];
        }

        [Fact]
        public void TheTakeIsSolvedWithTheIkOnTheMannequin()
        {
            var c = Solved.Value;
            Assert.Equal("ik", c.BodySource);
            Assert.Equal(Mannequin.Name, c.Profile.Name);
            Assert.Equal((int)(SyntheticTake.Duration * 60) + 1, c.Frames);
            for (int f = 0; f < c.Frames; f++)
            {
                Assert.True(c.Hips[f].IsFinite, "hips at frame " + f);
                foreach (var q in c.Rot[f]) Assert.InRange(Math.Abs(q.Norm - 1f), 0f, 1e-3f);
            }
        }

        [Fact]
        public void StandingTheHeadIsAtTheHeadsetAndTheFeetAreOnTheFloor()
        {
            var c = Solved.Value;
            var head = Bone(c, 1.0, HumanBones.Head);
            Assert.InRange(head.y, 1.40f, 1.60f);
            Assert.InRange(Math.Abs(head.x), 0f, 0.05f);
            foreach (int foot in new[] { HumanBones.LeftFoot, HumanBones.RightFoot })
                Assert.InRange(Bone(c, 1.0, foot).y, 0.0f, 0.20f);
            Assert.True(Bone(c, 1.0, HumanBones.LeftHand).x < -0.5f, "left hand out to the left");
            Assert.True(Bone(c, 1.0, HumanBones.RightHand).x > 0.5f, "right hand out to the right");
        }

        [Fact]
        public void TheSquatLowersTheHipsAndBendsTheKnees()
        {
            var c = Solved.Value;
            double deepest = (SyntheticTake.SquatStart + SyntheticTake.SquatEnd) / 2;
            float standing = Bone(c, 1.0, HumanBones.Hips).y, squatting = Bone(c, deepest, HumanBones.Hips).y;
            Assert.InRange(standing - squatting, SyntheticTake.SquatDepth - 0.10f, SyntheticTake.SquatDepth + 0.10f);
            // the feet stay put, so the legs must fold: the knee leaves the straight line from hip joint to ankle
            var hip = Bone(c, deepest, HumanBones.LeftUpperLeg); var knee = Bone(c, deepest, HumanBones.LeftLowerLeg); var ankle = Bone(c, deepest, HumanBones.LeftFoot);
            var line = (ankle - hip).Normalized;
            Assert.True(Vec3.ProjectOnPlane(knee - hip, line).Length > 0.10f, "knee bent in the squat");
            Assert.InRange(Bone(c, deepest, HumanBones.LeftFoot).y, 0.0f, 0.20f);
        }

        [Fact]
        public void TheTurnFacesTheChestToTheRight()
        {
            var c = Solved.Value;
            var before = c.Rot[c.FrameAt(1.0)][HumanBones.Chest] * Vec3.Forward;
            var after = c.Rot[c.FrameAt(SyntheticTake.TurnEnd + 0.1)][HumanBones.Chest] * Vec3.Forward;
            Assert.True(before.z > 0.9f, "facing forward before the turn");
            Assert.True(after.x > 0.7f, "facing right after the turn");
        }

        [Fact]
        public void TheWaveRaisesTheRightHandAboveTheHead()
        {
            var c = Solved.Value;
            double t = SyntheticTake.Duration - 0.3;
            Assert.True(Bone(c, t, HumanBones.RightHand).y > Bone(c, t, HumanBones.Head).y + 0.15f, "right hand above the head");
            Assert.True(Bone(c, t, HumanBones.LeftHand).y < Bone(c, t, HumanBones.Head).y, "left hand stays down");
        }

        /// <summary>
        /// Bone positions recorded from this solve when the test was written. They pin the result: a change to the
        /// solver, the mannequin or the calibration that moves a bone by more than a millimetre fails here and has to be
        /// looked at (and the numbers re-recorded when the change is meant). The tolerance leaves room for the last-bit
        /// differences between processors and runtimes.
        /// </summary>
        [Theory]
        [MemberData(nameof(GoldenData.Rows), MemberType = typeof(GoldenData))]
        public void BonePositionsMatchTheRecordedOnes(double time, string bone, float x, float y, float z)
        {
            var p = Bone(Solved.Value, time, HumanBones.FromName(bone));
            Assert.True((p - new Vec3(x, y, z)).Length < 0.001f, $"{bone} at {time} s is {Row(time, bone, p)}");
        }

        static string Row(double time, string bone, Vec3 p) =>
            string.Format(CultureInfo.InvariantCulture, "{{ {0:0.0#}, \"{1}\", {2:0.00000}f, {3:0.00000}f, {4:0.00000}f }},", time, bone, p.x, p.y, p.z);

        /// <summary>Prints the rows for <see cref="GoldenData"/> (set VRMR_PRINT_GOLDEN=1 after an intended change; they are written to golden.txt in the test run's temp folder, which is then kept).</summary>
        [Fact]
        public void PrintGoldenRowsWhenAsked()
        {
            if (Environment.GetEnvironmentVariable("VRMR_PRINT_GOLDEN") != "1") return;
            var sb = new StringBuilder();
            foreach (double t in GoldenData.Times)
                foreach (string bone in GoldenData.Bones)
                    sb.AppendLine("            new object[] " + Row(t, bone, Bone(Solved.Value, t, HumanBones.FromName(bone))));
            System.IO.Directory.CreateDirectory(TestEnvironment.Root);
            System.IO.File.WriteAllText(System.IO.Path.Combine(TestEnvironment.Root, "golden.txt"), sb.ToString());
        }
    }
}
