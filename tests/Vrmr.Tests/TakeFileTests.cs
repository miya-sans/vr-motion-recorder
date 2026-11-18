using System;
using Xunit;

namespace VRMR.Tests
{
    public class TakeFileTests
    {
        static Recording Sample()
        {
            var r = new Recording { Name = "synthetic", Duration = 2.0, AvatarName = "Mannequin" };
            var p = r.GetOrAddParam("FT/v2/JawOpen", ParamType.Float);
            for (int i = 0; i <= 20; i++) p.Add(i * 0.1, (float)Math.Sin(i * 0.3) * 0.5f + 0.5f);
            return r;
        }

        [Fact]
        public void SaveThenLoadKeepsParametersAndMetadata()
        {
            using (var tmp = new TempDir())
            {
                string path = tmp.File("synthetic.vrmr");
                var a = Sample();
                RecordingIO.Save(a, path);
                var b = RecordingIO.Load(path);
                Assert.Equal(a.Name, b.Name);
                Assert.Equal(a.AvatarName, b.AvatarName);
                Assert.Equal(a.Duration, b.Duration, 6);
                var pa = a.Param("FT/v2/JawOpen");
                var pb = b.Param("FT/v2/JawOpen");
                Assert.NotNull(pb);
                Assert.Equal(pa.Count, pb.Count);
                for (int i = 0; i < pa.Count; i++)
                {
                    Assert.Equal(pa.T[i], pb.T[i], 9);
                    Assert.Equal(pa.V[i], pb.V[i]);
                }
            }
        }

        [Fact]
        public void SummaryReadsNameAndDuration()
        {
            using (var tmp = new TempDir())
            {
                string path = tmp.File("synthetic.vrmr");
                RecordingIO.Save(Sample(), path);
                var s = RecordingIO.ReadSummary(path);
                Assert.Equal("synthetic", s.Name);
                Assert.Equal(2.0, s.Duration, 6);
            }
        }
    }
}
