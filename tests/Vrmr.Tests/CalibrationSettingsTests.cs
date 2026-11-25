using Xunit;

namespace VRMR.Tests
{
    public class CalibrationSettingsTests
    {
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
