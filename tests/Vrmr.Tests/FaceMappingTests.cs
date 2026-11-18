using System.Linq;
using Xunit;

namespace VRMR.Tests
{
    /// <summary>The face shapes a recording turns into, and finding them on an avatar's mesh.</summary>
    public class FaceMappingTests
    {
        [Fact]
        public void EveryShapeTheMappingProducesIsOneTheMeshIsSearchedFor()
        {
            // every parameter present: the mapping writes every shape it knows
            var produced = new FaceMapping { Mode = FaceMapping.Naming.UnifiedExpressions }.EvaluateUE(name => 0.4f).Keys;
            var searched = FaceMapping.AllShapeNames.ToHashSet();
            Assert.Empty(produced.Where(name => !searched.Contains(name)));
        }

        [Fact]
        public void ATongueMadeOfStepsIsFoundAndDriven()
        {
            // face-tracking add-ons that have no TongueOut shape move the tongue with two step shapes
            var mesh = new[] { "TongueUp", "TongueDown", "TongueOutStep1", "TongueOutStep2", "TongueUpLeftMorph" };
            var map = FaceMapping.MatchToMesh(FaceMapping.AllShapeNames, mesh);
            Assert.Equal("TongueOutStep1", map["TongueOutStep1"]);
            Assert.Equal("TongueOutStep2", map["TongueOutStep2"]);
            Assert.Equal("TongueUpLeftMorph", map["TongueUpLeftMorph"]);
            Assert.False(map.ContainsKey("TongueOut"));

            // the add-on's own blend tree: step 1 is fully out at 0.3, step 2 runs from 0.3 to 0.9
            var weights = Evaluate(("TongueOut", 0.6f));
            Assert.Equal(1f, weights["TongueOutStep1"], 4);
            Assert.Equal(0.5f, weights["TongueOutStep2"], 4);
            Assert.Equal(0.5f, Evaluate(("TongueOut", 0.15f))["TongueOutStep1"], 4);
        }

        static System.Collections.Generic.Dictionary<string, float> Evaluate(params (string name, float value)[] parameters) =>
            new FaceMapping { Mode = FaceMapping.Naming.UnifiedExpressions }.EvaluateUE(name => parameters.Where(p => p.name == name).Select(p => (float?)p.value).FirstOrDefault());

        [Fact]
        public void TheMouthMakesRoomForTheTongueUnlessItIsWideOpen()
        {
            // the shape that opens the lips around the tongue, found on the add-on's mesh despite the '*' in its name
            Assert.Equal("MouthTongueOutMorph*", FaceMapping.MatchToMesh(FaceMapping.AllShapeNames, new[] { "MouthTongueOutMorph*" })["MouthTongueOutMorph"]);

            Assert.Equal(0f, Evaluate(("TongueOut", 0.05f))["MouthTongueOutMorph"], 4);                // just a hint of tongue: nothing
            Assert.Equal(0.5f, Evaluate(("TongueOut", 0.2f))["MouthTongueOutMorph"], 4);               // fades in between 0.1 and 0.3
            var closed = Evaluate(("TongueOut", 0.9f), ("MouthLowerDown", 0.6f));
            Assert.Equal(1f, closed["MouthTongueOutMorph"], 4);
            Assert.Equal(0f, closed["MouthLowerDownLeft"], 4);                                          // the lower lip goes back up over the tongue
            var wide = Evaluate(("TongueOut", 0.9f), ("JawOpen", 1f), ("MouthLowerDown", 0.6f));
            Assert.Equal(0f, wide["MouthTongueOutMorph"], 4);                                            // a wide open mouth has room already
            Assert.Equal(0.6f, wide["MouthLowerDownLeft"], 4);
        }
    }
}
