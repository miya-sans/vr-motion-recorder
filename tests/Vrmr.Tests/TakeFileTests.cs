using System;
using System.IO;
using System.Linq;
using System.Text;
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

        /// <summary>The synthetic body take with VRChat IK frames and finger tracking added.</summary>
        static Recording FullSample()
        {
            var r = SyntheticTake.Create();
            var rnd = new Random(7);
            for (int i = 0; i < 180; i++)
            {
                double t = i / 30.0 + 0.0123456;
                var rot = new Quat[HumanBones.Count];
                for (int b = 0; b < rot.Length; b++) rot[b] = Quat.YawRotation(b * 7 + i * 0.5f);
                rot[1] = new Quat(-0.4994f, 0.50058597f, 0.50058597f, 0.4994f).Normalized;
                r.Humr.Add(t, 1000 + t, new Vec3(120.5f + i * 0.001f, 1.02f, -340.25f), rot);
            }

            for (int side = 0; side < 2; side++)
            for (int i = 0; i < 270; i++)
            {
                float[] sum = new float[9];
                for (int k = 0; k < 9; k++) sum[k] = (float)rnd.NextDouble();
                short[] bones = new short[HandStream.PackedLength];
                for (int k = 0; k < bones.Length; k++) bones[k] = (short)rnd.Next(short.MinValue, short.MaxValue);
                r.Hands[side].Add(i / 45.0, sum, bones);
            }

            var p = r.GetOrAddParam("FT/v2/JawOpen", ParamType.Float);
            for (int i = 0; i <= 600; i++) p.Add(i * 0.01, (float)rnd.NextDouble());
            return r;
        }

        static double AngleDeg(Quat a, Quat b)
        {
            double d = (double)a.x * b.x + (double)a.y * b.y + (double)a.z * b.z + (double)a.w * b.w;
            double na = Math.Sqrt((double)a.x * a.x + (double)a.y * a.y + (double)a.z * a.z + (double)a.w * a.w);
            double nb = Math.Sqrt((double)b.x * b.x + (double)b.y * b.y + (double)b.z * b.z + (double)b.w * b.w);
            return 2 * Math.Acos(Math.Min(1, Math.Abs(d) / (na * nb))) * 180 / Math.PI;
        }

        /// <summary>Every track's lists hold one entry per sample.</summary>
        static void AssertConsistent(Recording r)
        {
            foreach (var s in r.Streams)
            {
                Assert.Equal(s.T.Count, s.P.Count);
                Assert.Equal(s.T.Count, s.R.Count);
            }

            foreach (var p in r.Params) Assert.Equal(p.T.Count, p.V.Count);
            Assert.Equal(r.Humr.T.Count, r.Humr.SrcT.Count);
            Assert.Equal(r.Humr.T.Count, r.Humr.Hips.Count);
            Assert.Equal(r.Humr.T.Count, r.Humr.Rot.Count);
            foreach (var h in r.Hands)
            {
                Assert.Equal(h.T.Count, h.Summary.Count);
                Assert.Equal(h.T.Count, h.Bones.Count);
            }
        }

        static void AssertSameSamples(Recording a, Recording b)
        {
            Assert.Equal(a.Streams.Count, b.Streams.Count);
            for (int s = 0; s < a.Streams.Count; s++)
            {
                PoseStream x = a.Streams[s], y = b.Streams[s];
                Assert.Equal(x.T, y.T);
                Assert.Equal(x.P, y.P);
                Assert.Equal(x.R, y.R);
            }

            Assert.Equal(a.Humr.T, b.Humr.T);
            Assert.Equal(a.Humr.SrcT, b.Humr.SrcT);
            Assert.Equal(a.Humr.Hips, b.Humr.Hips);
            for (int i = 0; i < a.Humr.Count; i++) Assert.Equal(a.Humr.Rot[i], b.Humr.Rot[i]);
            for (int side = 0; side < 2; side++)
            {
                Assert.Equal(a.Hands[side].T, b.Hands[side].T);
                for (int i = 0; i < a.Hands[side].Count; i++)
                {
                    Assert.Equal(a.Hands[side].Summary[i], b.Hands[side].Summary[i]);
                    Assert.Equal(a.Hands[side].Bones[i], b.Hands[side].Bones[i]);
                }
            }

            Assert.Equal(a.Params.Count, b.Params.Count);
            for (int i = 0; i < a.Params.Count; i++)
            {
                Assert.Equal(a.Params[i].T, b.Params[i].T);
                Assert.Equal(a.Params[i].V, b.Params[i].V);
            }
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
        public void SaveKeepsSamplesWithinStoredPrecision()
        {
            using (var tmp = new TempDir())
            {
                var original = FullSample();
                var saved = FullSample();
                RecordingIO.Save(saved, tmp.File("take.vrmr"));
                for (int s = 0; s < original.Streams.Count; s++)
                {
                    PoseStream x = original.Streams[s], y = saved.Streams[s];
                    Assert.Equal(x.Count, y.Count);
                    for (int i = 0; i < x.Count; i++)
                    {
                        Assert.InRange(Math.Abs(x.T[i] - y.T[i]), 0, 0.5e-6);
                        Assert.InRange((x.P[i] - y.P[i]).Length, 0, 1e-6f);
                        Assert.InRange(AngleDeg(x.R[i], y.R[i]), 0, 0.005);
                    }
                }

                for (int i = 0; i < original.Humr.Count; i++)
                {
                    Assert.InRange((original.Humr.Hips[i] - saved.Humr.Hips[i]).Length, 0, 1e-5f);
                    for (int b = 0; b < HumanBones.Count; b++)
                        Assert.InRange(AngleDeg(original.Humr.Rot[i][b], saved.Humr.Rot[i][b]), 0, 0.005);
                }

                AssertSameSamples(saved, RecordingIO.Load(tmp.File("take.vrmr")));
            }
        }

        [Fact]
        public void SavingALoadedTakeAgainWritesTheSameFile()
        {
            using (var tmp = new TempDir())
            {
                string a = tmp.File("a.vrmr"), b = tmp.File("b.vrmr");
                RecordingIO.Save(FullSample(), a);
                RecordingIO.Save(RecordingIO.Load(a), b);
                Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
            }
        }

        [Fact]
        public void RepeatedPoseStaysExactlyRepeated()
        {
            using (var tmp = new TempDir())
            {
                var rec = new Recording { Name = "still", Duration = 1 };
                var st = rec.AddStream(StreamKind.OpenVRDevice, "TRK");
                var q = new Quat(0.70710677f, 0, 0.70710677f, 0);
                for (int i = 0; i < 10; i++) st.Add(i / 90.0, new Vec3(1.2345678f, 0.5f, -2.25f), q);
                string path = tmp.File("still.vrmr");
                RecordingIO.Save(rec, path);
                var r = RecordingIO.Load(path).Streams[0].R;
                for (int i = 1; i < r.Count; i++)
                {
                    Assert.Equal(r[0], r[i]);
                    Assert.True(Quat.Dot(r[i], r[i - 1]) > 0.9999999f);
                }
            }
        }

        [Fact]
        public void FarPositionsKeepTheirValueAndInvalidValuesBecomeZero()
        {
            using (var tmp = new TempDir())
            {
                var rec = new Recording { Name = "far", Duration = 1 };
                var st = rec.AddStream(StreamKind.OpenVRDevice, "TRK");
                st.Add(0, new Vec3(5000.25f, -3000.5f, 1), Quat.Identity);
                st.Add(0.01, new Vec3(float.NaN, 0, 0), new Quat(float.NaN, float.NaN, float.NaN, float.NaN));
                string path = tmp.File("far.vrmr");
                RecordingIO.Save(rec, path);
                var r = RecordingIO.Load(path).Streams[0];
                Assert.Equal(new Vec3(5000.25f, -3000.5f, 1), r.P[0]);
                Assert.Equal(Vec3.Zero, r.P[1]);
                Assert.Equal(Quat.Identity, r.R[0]);
                Assert.True(float.IsFinite(r.R[1].x) && float.IsFinite(r.R[1].w));
            }
        }

        [Fact]
        public void StreamedTakeLoadsAndCompacts()
        {
            using (var tmp = new TempDir())
            {
                var source = FullSample();
                var rec = new Recording { Name = "streamed", Duration = source.Duration };
                string path = tmp.File("streamed.vrmr");
                using (var w = new RecordingWriter(rec, path))
                {
                    foreach (var s in source.Streams)
                    {
                        var st = rec.AddStream(s.Kind, s.Name, s.Info);
                        w.DefineStream(st);
                        for (int i = 0; i < s.Count; i++)
                        {
                            st.Add(s.T[i], s.P[i], s.R[i]);
                            w.AppendPose(st, s.T[i], s.P[i], s.R[i]);
                        }
                    }

                    var p = source.Params[0];
                    var param = rec.GetOrAddParam(p.Name, p.Type);
                    for (int i = 0; i < p.Count; i++)
                    {
                        param.Add(p.T[i], p.V[i]);
                        w.AppendParam(param, p.T[i], p.V[i]);
                    }
                }

                long streamed = new FileInfo(path).Length;
                var loaded = RecordingIO.Load(path);
                AssertSameSamples(rec, loaded);
                RecordingIO.Save(loaded, path);
                Assert.True(new FileInfo(path).Length < streamed / 2);
                AssertSameSamples(loaded, RecordingIO.Load(path));
            }
        }

        [Fact]
        public void DamagedTakesLoadWhatIsIntact()
        {
            using (var tmp = new TempDir())
            {
                string saved = tmp.File("saved.vrmr"), streamed = tmp.File("streamed.vrmr");
                var source = FullSample();
                FaceKeys.Reduce(source);
                RecordingIO.Save(source, saved);
                var rec = new Recording { Name = "streamed", Duration = source.Duration };
                using (var w = new RecordingWriter(rec, streamed))
                {
                    var st = rec.AddStream(StreamKind.OpenVRDevice, "TRK");
                    w.DefineStream(st);
                    var p = rec.GetOrAddParam("FT/v2/JawOpen", ParamType.Float);
                    for (int i = 0; i < 2000; i++)
                    {
                        w.AppendPose(st, i / 90.0, new Vec3(i * 0.001f, 1, 0), Quat.YawRotation(i));
                        w.AppendParam(p, i / 90.0, i % 100 / 100f);
                        w.AppendHumr(i / 90.0, i / 90.0, Vec3.Zero, new Quat[HumanBones.Count]);
                        w.AppendHand(i / 90.0, i % 2, new float[9], new short[HandStream.PackedLength]);
                    }
                }

                var rnd = new Random(11);
                string damaged = tmp.File("damaged.vrmr");
                foreach (string path in new[] { saved, streamed })
                {
                    byte[] good = File.ReadAllBytes(path);
                    for (int trial = 0; trial < 300; trial++)
                    {
                        byte[] bad;
                        if (trial % 3 == 0)
                        {
                            bad = new byte[8 + rnd.Next(good.Length - 8)];
                            Array.Copy(good, bad, bad.Length);
                        }
                        else
                        {
                            bad = (byte[])good.Clone();
                            for (int k = 0; k < 1 + trial % 5; k++) bad[8 + rnd.Next(bad.Length - 8)] ^= (byte)(1 + rnd.Next(255));
                        }

                        File.WriteAllBytes(damaged, bad);
                        Recording loaded = null;
                        var ex = Record.Exception(() => loaded = RecordingIO.Load(damaged));
                        Assert.True(ex == null, $"trial {trial} of {Path.GetFileName(path)}: {ex}");
                        AssertConsistent(loaded);
                        ex = Record.Exception(() => RecordingIO.ReadSummary(damaged));
                        Assert.True(ex == null, $"summary, trial {trial} of {Path.GetFileName(path)}: {ex}");
                    }
                }
            }
        }

        [Fact]
        public void ADamagedChunkIsSkippedAndCounted()
        {
            using (var tmp = new TempDir())
            {
                string path = tmp.File("take.vrmr");
                var source = FullSample();
                RecordingIO.Save(source, path);
                byte[] b = File.ReadAllBytes(path);
                int pos = 8, damaged = -1;
                while (pos + 8 <= b.Length)
                {
                    string tag = Encoding.ASCII.GetString(b, pos, 4);
                    int len = BitConverter.ToInt32(b, pos + 4);
                    if (tag == "SCOL" && damaged < 0)
                    {
                        // chunk header: u16 id, i32 count, f64 last time, i32 raw length
                        damaged = BitConverter.ToUInt16(b, pos + 8);
                        b[pos + 8 + 14] ^= 1;
                    }

                    pos += 8 + len;
                }

                File.WriteAllBytes(path, b);
                var loaded = RecordingIO.Load(path);
                Assert.Equal(1, loaded.SkippedChunks);
                AssertConsistent(loaded);
                foreach (var st in loaded.Streams)
                    Assert.Equal(st.Id == damaged ? 0 : source.Streams.First(x => x.Name == st.Name).Count, st.Count);
                Assert.Equal(source.Humr.Count, loaded.Humr.Count);
            }
        }

        [Fact]
        public void EmptyAndTinyTracksSaveAndLoad()
        {
            using (var tmp = new TempDir())
            {
                var rec = new Recording { Name = "tiny", Duration = 0.1 };
                rec.AddStream(StreamKind.OpenVRDevice, "EMPTY");
                rec.AddStream(StreamKind.OpenVRDevice, "ONE").Add(0.05, new Vec3(1, 2, 3), Quat.Identity);
                rec.GetOrAddParam("FT/v2/JawOpen", ParamType.Float).Add(0.01, 0.3f);
                var two = rec.GetOrAddParam("FT/v2/EyeLeftX", ParamType.Float);
                two.Add(0.01, 0.1f);
                two.Add(0.02, 0.2f);
                rec.GetOrAddParam("FT/v2/Empty", ParamType.Float);
                rec.Humr.Add(0.03, 1, Vec3.Zero, new Quat[HumanBones.Count]);
                rec.Hands[1].Add(0.04, new float[9], new short[HandStream.PackedLength]);
                FaceKeys.Reduce(rec);
                string path = tmp.File("tiny.vrmr");
                RecordingIO.Save(rec, path);
                var loaded = RecordingIO.Load(path);
                AssertSameSamples(rec, loaded);
                Assert.Equal(0, loaded.Streams[0].Count);
                Assert.Equal(2, loaded.Param("FT/v2/EyeLeftX").Count);
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

        [Fact]
        public void SummaryFindsCompressedTracksAndTheirLength()
        {
            using (var tmp = new TempDir())
            {
                string path = tmp.File("full.vrmr");
                var rec = FullSample();
                rec.Duration = 0;
                RecordingIO.Save(rec, path);
                var s = RecordingIO.ReadSummary(path);
                Assert.True(s.HasDevices);
                Assert.True(s.HasHumr);
                Assert.True(s.HasFingers);
                Assert.True(s.HasFace);
                Assert.Equal(SyntheticTake.Duration, s.Duration, 5);
            }
        }
    }
}
