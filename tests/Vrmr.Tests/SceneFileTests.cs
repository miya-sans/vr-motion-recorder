using System.Linq;
using Xunit;

namespace VRMR.Tests
{
    public class SceneFileTests
    {
        [Fact]
        public void SaveThenLoadKeepsTracksStripsAndMarkers()
        {
            using (var tmp = new TempDir())
            {
                var s = Scene.CreateNew("Test scene");
                s.Fps = 60;
                var take = new TakeRef { Id = "t1", Path = tmp.File("a.vrmr"), Name = "a", Duration = 10 };
                s.Takes.Add(take);
                var strip = SceneOps.AddTakeStrip(s, s.Actors[0].Tracks[0], take, 1.5, 2, 6);
                strip.Mask = BodyMask.UpperBody;
                s.Markers.Add(new SceneMarker { T = 3, Name = "Mark" });
                string path = tmp.File("test.vrmscene");
                s.Save(path);

                var b = Scene.Load(path);
                Assert.Equal("Test scene", b.Name);
                Assert.Single(b.Takes);
                var st = b.Actors[0].Tracks[0].Strips.Single();
                Assert.Equal(1.5, st.Start, 6);
                Assert.Equal(2, st.SrcIn, 6);
                Assert.Equal(6, st.SrcOut, 6);
                Assert.Equal(BodyMask.UpperBody, st.Mask);
                Assert.Equal("Mark", b.Markers.Single().Name);
            }
        }
    }
}
