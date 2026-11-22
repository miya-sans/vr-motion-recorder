using System;
using System.Numerics;
using Xunit;

namespace VRMR.Render.Tests
{
    /// <summary>The parts of the 3D view that are plain arithmetic: pacing, the camera, hit tests, the gizmo, mesh packing.</summary>
    public class RenderLogicTests
    {
        static int Ticks(double displayHz, double cap, double seconds)
        {
            var pacer = new FramePacer();
            int ticks = 0;
            for (int i = 0; i < (int)(displayHz * seconds); i++)
                if (pacer.Tick(i / displayHz, cap, out _))
                    ticks++;
            return ticks;
        }

        [Fact]
        public void ThePacerTicksEveryDisplayFrameAtTheDisplayRate()
        {
            Assert.Equal(600, Ticks(60, 60, 10));
        }

        [Theory]
        [InlineData(60, 15)]
        [InlineData(60, 4)]
        [InlineData(144, 60)]
        [InlineData(144, 15)]
        public void ThePacerHoldsACapBelowTheDisplayRate(double displayHz, double cap)
        {
            int ticks = Ticks(displayHz, cap, 10);
            Assert.InRange(ticks, cap * 10 * 0.9, cap * 10 * 1.1);
        }

        [Fact]
        public void ThePacerReportsTheTimeSinceTheLastTick()
        {
            var pacer = new FramePacer();
            double sum = 0;
            for (int i = 0; i < 600; i++)
                if (pacer.Tick(i / 60.0, 15, out double elapsed) && i > 0)
                    sum += elapsed;
            Assert.InRange(sum, 9.8, 10.0);
        }

        static ViewCamera Camera()
        {
            var c = new ViewCamera { Yaw = 35, Pitch = 20, Distance = 3, Target = new Vec3(0.2f, 0.9f, -0.1f) };
            c.SetViewport(800, 500);
            return c;
        }

        [Fact]
        public void TheCameraLooksAtItsTarget()
        {
            var c = Camera();
            Assert.True(c.Project(c.Target, out var s));
            Assert.Equal(400, s.X, 2);
            Assert.Equal(250, s.Y, 2);
            Assert.Equal(3f, c.Depth(c.Target), 4);
        }

        [Fact]
        public void TheRayUnderAProjectedPointPassesThroughIt()
        {
            var c = Camera();
            var p = new Vec3(0.6f, 1.4f, 0.3f);
            Assert.True(c.Project(p, out var s));
            c.Ray(s, out var origin, out var dir);
            var toPoint = p - origin;
            float along = Vec3.Dot(toPoint, dir);
            Assert.True((toPoint - dir * along).Length < 1e-3f);
        }

        [Fact]
        public void AParallelViewKeepsSizesAndMatchesTheGpuMatrix()
        {
            var c = Camera();
            c.Orthographic = true;
            c.LookFrom(0, 0);
            // the target keeps its size on screen; a metre is as wide near as far
            Assert.True(c.Project(c.Target, out var t));
            Assert.Equal(400, t.X, 2);
            Assert.True(c.Project(c.Target + Vec3.Right, out var near));
            Assert.True(c.Project(c.Target + Vec3.Right + Vec3.Forward * 2, out var far));
            Assert.Equal(near.X, far.X, 2);
            Assert.Equal(c.Focal / 3f, Math.Abs(near.X - t.X), 1);

            // rays run along the view, through the point they were cast from
            var p = new Vec3(0.6f, 1.4f, 0.3f);
            Assert.True(c.Project(p, out var s));
            c.Ray(s, out var origin, out var dir);
            Assert.True((dir - c.Forward).Length < 1e-5f);
            var toPoint = p - origin;
            Assert.True((toPoint - dir * Vec3.Dot(toPoint, dir)).Length < 1e-3f);

            // the GPU's matrix lands on the same pixel
            Span<float> m = stackalloc float[16];
            c.ViewProjection(m, 800, 500, 1);
            float cx = m[0] * p.x + m[4] * p.y + m[8] * p.z + m[12];
            float cy = m[1] * p.x + m[5] * p.y + m[9] * p.z + m[13];
            float cz = m[2] * p.x + m[6] * p.y + m[10] * p.z + m[14];
            float w = m[3] * p.x + m[7] * p.y + m[11] * p.z + m[15];
            Assert.Equal(s.X, (cx / w + 1) * 400, 1);
            Assert.Equal(s.Y, (1 - cy / w) * 250, 1);
            Assert.InRange(cz / w, -1f, 1f);
        }

        [Fact]
        public void PointsBehindTheCameraDoNotProject()
        {
            var c = Camera();
            Assert.False(c.Project(c.Eye - c.Forward, out _));
        }

        [Fact]
        public void HitTestsMeasureInPixels()
        {
            Assert.Equal(5f, HitTest.DistanceToSegment(new Vector2(5, 5), new Vector2(0, 0), new Vector2(10, 0)), 4);
            Assert.Equal(5f, HitTest.DistanceToSegment(new Vector2(-3, 4), new Vector2(0, 0), new Vector2(10, 0)), 4);
            Span<Vector2> square = stackalloc Vector2[]
                { new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10), new Vector2(0, 10) };
            Assert.True(HitTest.InsidePolygon(new Vector2(5, 5), square));
            Assert.False(HitTest.InsidePolygon(new Vector2(15, 5), square));
            Assert.Equal(2, HitTest.NearestPoint(new Vector2(9, 9), square, 3));
            Assert.Equal(-1, HitTest.NearestPoint(new Vector2(5, 5), square, 3));
        }

        [Fact]
        public void AnAxisDragOfTheGizmoMovesAlongThatAxisOnly()
        {
            var c = Camera();
            var gizmo = new Gizmo { Center = c.Target };
            Assert.True(c.Project(gizmo.Center + gizmo.AxisDir(0) * (gizmo.WorldSize(c) * 0.8f), out var onArrow));
            Assert.Equal(GizmoPart.X, gizmo.Hit(c, onArrow));
            gizmo.Begin(c, GizmoPart.X, onArrow);
            Assert.True(c.Project(gizmo.Center + new Vec3(0.5f, 0.2f, 0), out var there));
            gizmo.Delta(c, there, false, out var move, out var turn);
            gizmo.End();
            Assert.True(Math.Abs(move.x) > 0.05f);
            Assert.Equal(0f, move.y, 5);
            Assert.Equal(0f, move.z, 5);
            Assert.Equal(1f, Math.Abs(turn.w), 5);
        }

        [Fact]
        public void ARingDragOfTheGizmoTurnsAboutItsAxis()
        {
            var c = Camera();
            var gizmo = new Gizmo { Center = c.Target, Rotate = true };
            c.Project(gizmo.Center, out var centre);
            gizmo.Begin(c, GizmoPart.RY, centre + new Vector2(60, 0));
            gizmo.Delta(c, centre + new Vector2(0, 60), false, out var move, out var turn);
            Assert.Equal(90f, Math.Abs(gizmo.Degrees), 1);
            Assert.Equal(0f, move.Length, 5);
            var turned = turn * Vec3.Forward;
            Assert.Equal(0f, turned.y, 4); // about the vertical axis
            Assert.Equal(1f, Math.Abs(turned.x), 3); // a quarter turn
        }

        [Fact]
        public void TheMannequinPacksIntoOneRigidPart()
        {
            var profile = Mannequin.Profile();
            var mesh = MannequinMesh.For(profile);
            var model = MeshModel.Create(mesh);
            Assert.Equal(profile.Nodes.Count, model.NodeCount);
            Assert.Equal(mesh.TriangleCount, model.TriangleCount);
            Assert.Equal(mesh.VertexCount, model.VertexCount);
            Assert.Single(model.Parts);
            Assert.Equal(0, model.FaceChannelCount);
            Assert.True(model.HiddenParts(new[] { Mannequin.Name })[0]);
            Assert.False(model.HiddenParts(new[] { "something else" })[0]);
            Assert.Null(MeshModel.Create((MannequinMesh)null));
        }

        [Fact]
        public void AFrameIsEmptyAgainAfterClear()
        {
            var profile = Mannequin.Profile();
            var frame = new ViewFrame();
            var actor = frame.AddActor(profile, null);
            Assert.Equal(profile.Nodes.Count, actor.NodePos.Length);
            frame.Markers.Add(new Marker { Kind = MarkerKind.Tracker });
            frame.Overlay.Append("text");
            frame.Recording = true;
            Assert.True(frame.HasDevice());
            frame.Clear();
            Assert.Empty(frame.Actors);
            Assert.Empty(frame.Markers);
            Assert.Equal(0, frame.Overlay.Length);
            Assert.False(frame.Recording);
            Assert.False(frame.HasDevice());
        }
    }
}
