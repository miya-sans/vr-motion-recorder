using System.Linq;
using VRMR.App.Viewport;
using Xunit;

namespace VRMR.App.Tests
{
    public class SimulatedBenchmarkDevicesTests
    {
        [Fact]
        public void PlaybackReceivesFourteenSimulatedDevicesAndAPlayArea()
        {
            var scene = new ViewScene();
            scene.Markers.Add(new ViewMarker { Kind = MarkerKind.Camera });
            var devices = new SimulatedBenchmarkDevices(0);

            devices.Fill(scene, 0);
            var headset = scene.Markers[0].Pose.pos;
            var station = scene.Markers[^1].Pose.pos;
            devices.Fill(scene, 1);

            Assert.Equal(
                Enumerable.Repeat(MarkerKind.Controller, 2).Prepend(MarkerKind.Headset)
                    .Concat(Enumerable.Repeat(MarkerKind.Tracker, 8))
                    .Concat(Enumerable.Repeat(MarkerKind.BaseStation, 3)),
                scene.Markers.Select(marker => marker.Kind));
            Assert.NotEqual(headset, scene.Markers[0].Pose.pos);
            Assert.Equal(station, scene.Markers[^1].Pose.pos);
            Vec3[] expectedArea =
                [new(-1.5f, 0, -1.2f), new(1.5f, 0, -1.2f), new(1.5f, 0, 1.2f), new(-1.5f, 0, 1.2f)];
            Assert.Equal(expectedArea, scene.PlayArea);
        }
    }
}
