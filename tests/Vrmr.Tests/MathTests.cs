using Xunit;

namespace VRMR.Tests
{
    public class MathTests
    {
        static void Near(Vec3 a, Vec3 b, float eps = 1e-5f) => Assert.True((a - b).Length < eps, $"{a.x},{a.y},{a.z} vs {b.x},{b.y},{b.z}");

        [Fact]
        public void YawRotationTurnsForwardToTheRight()
        {
            // Unity space: +Z forward, +X right; a positive yaw turns clockwise seen from above
            Near(Quat.YawRotation(90) * new Vec3(0, 0, 1), new Vec3(1, 0, 0));
        }

        [Fact]
        public void QuaternionTimesItsConjugateIsIdentity()
        {
            var q = (Quat.YawRotation(37) * Quat.AngleAxis(21, Vec3.Right)).Normalized;
            var v = new Vec3(0.3f, -1.2f, 2.5f);
            Near(q.Conjugate * (q * v), v);
        }

        [Fact]
        public void PoseTransformPointAppliesRotationThenTranslation()
        {
            var p = new Pose(new Vec3(1, 2, 3), Quat.YawRotation(180));
            Near(p.TransformPoint(new Vec3(0, 0, 1)), new Vec3(1, 2, 2));
        }
    }
}
