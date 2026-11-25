using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Xunit;

namespace VRMR.Tests
{
    /// <summary>End to end without private data: the scripted take solved onto the built-in mannequin.</summary>
    public class SyntheticSolveTests
    {
        static readonly Lazy<BakedClip> Solved = new(() =>
        {
            BodySolverSetup.Register();
            var cal = new CalibrationSettings
            {
                Height = CalHeightMode.RealHeight,
                HeightValue = SyntheticTake.Height,
                FollowVrcSpine = false,
                FollowVrcMeasure = false
            };
            return PoseBuilder.Bake(SyntheticTake.Create(), Mannequin.Profile(),
                new BakeOptions { Fps = 60, Calibration = cal });
        });

        static Vec3 Bone(BakedClip c, double t, int bone)
        {
            var pos = new Vec3[c.Profile.Nodes.Count];
            var rot = new Quat[c.Profile.Nodes.Count];
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
            var hip = Bone(c, deepest, HumanBones.LeftUpperLeg);
            var knee = Bone(c, deepest, HumanBones.LeftLowerLeg);
            var ankle = Bone(c, deepest, HumanBones.LeftFoot);
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
            Assert.True(Bone(c, t, HumanBones.RightHand).y > Bone(c, t, HumanBones.Head).y + 0.15f,
                "right hand above the head");
            Assert.True(Bone(c, t, HumanBones.LeftHand).y < Bone(c, t, HumanBones.Head).y, "left hand stays down");
        }

        /// <summary>
        ///     Bone positions recorded from this solve when the test was written. They pin the result: a change to the
        ///     solver, the mannequin or the calibration that moves a bone by more than a millimetre fails here and has to be
        ///     looked at (and the numbers re-recorded when the change is meant). The tolerance leaves room for the last-bit
        ///     differences between processors and runtimes.
        /// </summary>
        [Theory]
        [MemberData(nameof(GoldenData.Rows), MemberType = typeof(GoldenData))]
        public void BonePositionsMatchTheRecordedOnes(double time, string bone, float x, float y, float z)
        {
            var p = Bone(Solved.Value, time, HumanBones.FromName(bone));
            Assert.True((p - new Vec3(x, y, z)).Length < 0.001f, $"{bone} at {time} s is {Row(time, bone, p)}");
        }

        static string Row(double time, string bone, Vec3 p)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{{ {0:0.0#}, \"{1}\", {2:0.00000}f, {3:0.00000}f, {4:0.00000}f }},", time, bone, p.x, p.y, p.z);
        }

        /// <summary>
        ///     Prints the rows for <see cref="GoldenData" /> (set VRMR_PRINT_GOLDEN=1 after an intended change; they are
        ///     written to golden.txt in the test run's temp folder, which is then kept).
        /// </summary>
        [Fact]
        public void PrintGoldenRowsWhenAsked()
        {
            if (Environment.GetEnvironmentVariable("VRMR_PRINT_GOLDEN") != "1") return;
            var sb = new StringBuilder();
            foreach (double t in GoldenData.Times)
            foreach (string bone in GoldenData.Bones)
                sb.AppendLine("            new object[] " +
                              Row(t, bone, Bone(Solved.Value, t, HumanBones.FromName(bone))));
            Directory.CreateDirectory(TestEnvironment.Root);
            File.WriteAllText(Path.Combine(TestEnvironment.Root, "golden.txt"), sb.ToString());
        }

        static BakedClip SolveWithNudge(Vec3 leftFoot, string calibration)
        {
            BodySolverSetup.Register();
            var rec = SyntheticTake.Create();
            rec.Edits.TrackerNudges[calibration] = new Dictionary<string, Vec3> { ["LeftFoot"] = leftFoot };
            var cal = new CalibrationSettings
            {
                Height = CalHeightMode.RealHeight,
                HeightValue = SyntheticTake.Height,
                FollowVrcSpine = false,
                FollowVrcMeasure = false
            };
            return PoseBuilder.Bake(rec, Mannequin.Profile(), new BakeOptions { Fps = 60, Calibration = cal });
        }

        [Fact]
        public void ANudgedTrackerMovesItsBoneTheOtherWayAndLeavesTheOthers()
        {
            var plain = Solved.Value;
            string key = EditList.CalibrationKey(null, SyntheticTake.CalibrationTime);
            var nudged = SolveWithNudge(new Vec3(0, -3, 0), key);
            Assert.Equal(key, nudged.Calibration.Pose.Key);
            float left = Bone(nudged, 1.0, HumanBones.LeftFoot).y - Bone(plain, 1.0, HumanBones.LeftFoot).y;
            float right = Bone(nudged, 1.0, HumanBones.RightFoot).y - Bone(plain, 1.0, HumanBones.RightFoot).y;
            Assert.InRange(left, 0.02f, 0.04f);
            Assert.InRange(Math.Abs(right), 0f, 0.005f);
            var moved = nudged.Calibration.Pose.Devices["LeftFoot"].pos -
                        plain.Calibration.Pose.Devices["LeftFoot"].pos;
            Assert.InRange(moved.y, -0.031f, -0.029f);
            Assert.InRange(Math.Abs(moved.x) + Math.Abs(moved.z), 0f, 0.001f);
        }

        [Fact]
        public void AnotherCalibrationsTrackerOffsetsDoNotApply()
        {
            var plain = Solved.Value;
            var other = SolveWithNudge(new Vec3(0, -3, 0), EditList.CalibrationKey(null, 2.0));
            float left = Bone(other, 1.0, HumanBones.LeftFoot).y - Bone(plain, 1.0, HumanBones.LeftFoot).y;
            Assert.InRange(Math.Abs(left), 0f, 0.001f);
        }

        [Fact]
        public void ThumbstickLocomotionDefaultsToOff()
        {
            Assert.False(new Recording().Edits.IncludeLocomotion);
            Assert.False(EditList.FromJson(null).IncludeLocomotion);
            Assert.False(EditList.FromJson(Json.ParseObject("{}")).IncludeLocomotion);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ThumbstickLocomotionSurvivesSavingTheEditList(bool enabled)
        {
            var edits = new EditList { IncludeLocomotion = enabled };
            var loaded = EditList.FromJson(Json.ParseObject(Json.Serialize(edits.ToJson())));
            Assert.Equal(enabled, loaded.IncludeLocomotion);
        }

        [Fact]
        public void TrackerOffsetsSurviveSavingTheEditListPerCalibration()
        {
            var e = new EditList();
            e.TrackerNudges["snapshot:a"] = new Dictionary<string, Vec3> { ["Hips"] = new Vec3(1.5f, -2f, 0.5f) };
            var back = EditList.FromJson(Json.ParseObject(Json.Serialize(e.ToJson())));
            Assert.Equal(new Vec3(1.5f, -2f, 0.5f), back.NudgesFor("snapshot:a")["Hips"]);
            Assert.Null(back.NudgesFor("snapshot:b"));
        }

        [Fact]
        public void OlderTrackerOffsetsApplyToEveryCalibration()
        {
            var d = Json.ParseObject("{\"trackerNudges\": {\"LeftFoot\": [0, 2, 0]}}");
            var e = EditList.FromJson(d);
            Assert.Equal(new Vec3(0, 2, 0), e.NudgesFor("snapshot:any")["LeftFoot"]);
        }
    }
}
