using Xunit;

namespace VRMR.Tests
{
    public class CalibrationSettingsTests
    {
        [Fact]
        public void OlderCalibrationKeepsItsHandRotationAndUsesLegacyOffsets()
        {
            var settings = CalibrationSettings.FromJson(
                Json.ParseObject("{\"vrcHands\":false,\"handRotFix\":[1,2,3]}"));
            Assert.True(settings.UseLegacyIndexHandOffset);
            Assert.Equal((0f, 0f), settings.HeadOffset);
            Assert.Null(settings.HandOffset);
            Assert.Equal(new Vec3(1, 2, 3), settings.HandRotFix);
            Assert.True(settings.SameAs(CalibrationSettings.FromJson(settings.ToJson())));
        }

        [Theory]
        [InlineData("\"vrcHands\":true,", false)]
        [InlineData("\"vrcHands\":false,", true)]
        [InlineData("", true)]
        public void ReadsOnlyOffsetsThatWereInUse(string toggle, bool hasOffsets)
        {
            var settings = CalibrationSettings.FromJson(Json.ParseObject(
                "{" + toggle + "\"headOffset\":{\"y\":0.01,\"z\":-0.02},\"handOffset\":[1,2,3]}"));
            Assert.Equal(hasOffsets ? (0.01f, -0.02f) : ((float, float)?)null, settings.HeadOffset);
            Assert.Equal(hasOffsets ? new Vec3(1, 2, 3) : (Vec3?)null, settings.HandOffset);
            Assert.False(settings.UseLegacyIndexHandOffset);
            Assert.True(settings.SameAs(CalibrationSettings.FromJson(settings.ToJson())));
        }

        [Theory]
        [InlineData("{}", true)]
        [InlineData("{\"followVrc\":true}", true)]
        [InlineData("{\"followVrc\":false}", false)]
        public void LegacyFollowSettingAppliesToBothDropdowns(string json, bool follows)
        {
            var settings = CalibrationSettings.FromJson(Json.ParseObject(json));
            Assert.Equal(follows, settings.FollowVrcSpine);
            Assert.Equal(follows, settings.FollowVrcMeasure);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void OnlyTheSelectedSettingFollowsRecordedChanges(bool followSpine)
        {
            var saved = new CalibrationSettings { FollowVrcSpine = followSpine, FollowVrcMeasure = !followSpine };
            var settings = CalibrationSettings.FromJson(Json.ParseObject(Json.Serialize(saved.ToJson())));
            Assert.True(settings.SameAs(saved));

            var take = SyntheticTake.Create();
            take.Edits.VrcChanges.Add(new VrcChange { T = 1, Spine = 0 });
            take.Edits.VrcChanges.Add(new VrcChange { T = 2, Measure = 0 });

            var plans = PoseBuilder.PlanSegments(take, settings, out _);

            Assert.Collection(plans,
                before =>
                {
                    Assert.Equal(CalLock.Head, before.Settings.Lock);
                    Assert.Equal(CalMeasure.Height, before.Settings.Measure);
                },
                after =>
                {
                    Assert.Equal(followSpine ? 1d : 2d, after.Start);
                    Assert.Equal(followSpine ? CalLock.Hips : CalLock.Head, after.Settings.Lock);
                    Assert.Equal(followSpine ? CalMeasure.Height : CalMeasure.ArmSpan, after.Settings.Measure);
                });
        }
    }
}
