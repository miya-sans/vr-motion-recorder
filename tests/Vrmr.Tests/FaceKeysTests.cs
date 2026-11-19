using System;
using Xunit;

namespace VRMR.Tests
{
    public class FaceKeysTests
    {
        const double Bound = FaceKeys.Tolerance + FaceKeys.Step / 2 + 1e-6;

        /// <summary>A face value as VRCFT sends it: about 145 updates a second with jitter, slow motion, noise and blinks.</summary>
        static ParamTrack Face(Recording rec, string name, int seed, double seconds = 20)
        {
            var p = rec.GetOrAddParam(name, ParamType.Float);
            var rnd = new Random(seed);
            for (double t = 0; t < seconds; t += 0.002 + rnd.NextDouble() * 0.0115)
            {
                double v = 0.4 + 0.3 * Math.Sin(t * 1.7) + 0.05 * Math.Sin(t * 13) + (rnd.NextDouble() - 0.5) * 0.002;
                if (t % 4 < 0.15) v = 1; // a blink: an instant step up and back
                p.Add(t, (float)v);
            }

            return p;
        }

        static ParamTrack Copy(ParamTrack p)
        {
            var c = new ParamTrack { Name = p.Name, Type = p.Type };
            c.T.AddRange(p.T);
            c.V.AddRange(p.V);
            return c;
        }

        static double MaxError(ParamTrack original, ParamTrack reduced, double seconds)
        {
            double worst = 0;
            for (double t = -0.1; t < seconds + 0.1; t += 0.0007)
                worst = Math.Max(worst, Math.Abs(original.Sample(t) - reduced.Sample(t)));
            return worst;
        }

        [Fact]
        public void ReducedFaceFollowsTheOriginalWithinToleranceAtAnyTime()
        {
            var rec = new Recording();
            var p = Face(rec, "FT/v2/JawOpen", 1);
            var original = Copy(p);
            int removed = FaceKeys.Reduce(rec);
            Assert.True(removed > original.Count / 2, $"only {removed} of {original.Count} samples removed");
            Assert.InRange(MaxError(original, p, 20), 0, Bound);
            Assert.Equal(original.T[0], p.T[0]);
            Assert.Equal(original.T[original.Count - 1], p.T[p.Count - 1]);
            for (int i = 1; i < p.Count; i++) Assert.InRange(p.T[i] - p.T[i - 1], 1e-9, FaceKeys.MaxGap);
        }

        [Fact]
        public void GapsLongerThanInterpolationStillHold()
        {
            var rec = new Recording();
            var p = rec.GetOrAddParam("FT/v2/EyeLeftX", ParamType.Float);
            for (int i = 0; i < 50; i++) p.Add(i * 0.01, 0.2f);
            p.Add(0.49, 0.25f);
            for (int i = 0; i < 50; i++) p.Add(1.5 + i * 0.01, 0.8f + i * 0.001f);
            var original = Copy(p);
            FaceKeys.Reduce(rec);
            Assert.InRange(MaxError(original, p, 2), 0, Bound);
            Assert.Equal(original.Sample(1.0), p.Sample(1.0), 3);
        }

        [Fact]
        public void OnlyFaceFloatsAreReducedAndOnlyOnce()
        {
            using (var tmp = new TempDir())
            {
                var rec = new Recording { Name = "face", Duration = 20 };
                var face = Face(rec, "FT/v2/MouthClosed", 2);
                var other = Face(rec, "VelocityX", 3);
                var bit = rec.GetOrAddParam("FT/v2/JawOpen4", ParamType.Bool);
                for (int i = 0; i < 200; i++) bit.Add(i * 0.01, i / 7 % 2);
                var otherBefore = Copy(other);
                int bitCount = bit.Count;
                Assert.True(FaceKeys.Reduce(rec) > 0);
                Assert.Equal(otherBefore.V, other.V);
                Assert.Equal(bitCount, bit.Count);
                int kept = face.Count;
                Assert.Equal(0, FaceKeys.Reduce(rec));
                string path = tmp.File("face.vrmr");
                RecordingIO.Save(rec, path);
                var loaded = RecordingIO.Load(path);
                Assert.Equal(FaceKeys.Tolerance, loaded.FaceKeyTolerance);
                Assert.Equal(0, FaceKeys.Reduce(loaded));
                Assert.Equal(kept, loaded.Param("FT/v2/MouthClosed").Count);
            }
        }

        [Fact]
        public void InvalidValuesAndRepeatedTimesAreKept()
        {
            var rec = new Recording();
            var p = rec.GetOrAddParam("FT/v2/CheekPuffLeft", ParamType.Float);
            for (int i = 0; i < 20; i++) p.Add(i * 0.01, 0.1f);
            p.Add(0.2, float.NaN);
            for (int i = 21; i < 40; i++) p.Add(i * 0.01, 0.1f);
            p.Add(0.39, 0.9f);
            p.Add(0.39, 0.1f);
            for (int i = 40; i < 60; i++) p.Add(i * 0.01, 0.1f);
            FaceKeys.Reduce(rec);
            Assert.Contains(p.V, float.IsNaN);
            Assert.Contains(p.V, v => Math.Abs(v - 0.9f) <= FaceKeys.Step);
            for (int i = 1; i < p.Count; i++) Assert.True(p.T[i] >= p.T[i - 1]);
        }
    }
}
