using System;
using Xunit;

namespace VRMR.Render.Tests
{
    /// <summary>
    /// Draws the built-in mannequin with the real renderer into memory and looks at the pixels. Needs OpenGL ES through
    /// ANGLE (Windows); on a machine without a GPU it falls back to Direct3D's software rasteriser, and it is skipped
    /// where neither exists.
    /// </summary>
    public class OffscreenRenderTests
    {
        const int Width = 480, Height = 360;

        static OffscreenViewport Open()
        {
            try { return new OffscreenViewport(); }
            catch (Exception first) when (first is InvalidOperationException || first is DllNotFoundException || first is EntryPointNotFoundException)
            {
                try { return new OffscreenViewport(software: true); }
                catch (Exception ex) when (ex is InvalidOperationException || ex is DllNotFoundException || ex is EntryPointNotFoundException) { return null; }
            }
        }

        static ViewFrame RestPose(AvatarProfile profile, MeshModel model, Vec3 offset = default)
        {
            var frame = new ViewFrame();
            var actor = frame.AddActor(profile, model);
            for (int i = 0; i < actor.NodePos.Length; i++) { actor.NodePos[i] = profile.RestWorldPos[i] + offset; actor.NodeRot[i] = profile.RestWorldRot[i]; }
            return frame;
        }

        /// <summary>Pixels that differ from the background, and the box around them.</summary>
        static (int count, int left, int top, int right, int bottom) Drawn(ViewportImage image, Rgba background)
        {
            int count = 0, left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
            for (int y = 0; y < image.Height; y++)
                for (int x = 0; x < image.Width; x++)
                {
                    int o = (y * image.Width + x) * 4;
                    if (Math.Abs(image.Rgba[o] - background.R) + Math.Abs(image.Rgba[o + 1] - background.G) + Math.Abs(image.Rgba[o + 2] - background.B) <= 6) continue;
                    count++;
                    left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                }
            return (count, left, top, right, bottom);
        }

        static ViewportTheme Bare()
        {
            var t = new ViewportTheme();
            t.GridMinor = t.GridMajor = t.OriginX = t.OriginZ = Rgba.Transparent;
            return t;
        }

        [Fact]
        public void TheMannequinIsDrawnWhereTheCameraProjectsIt()
        {
            using var view = Open();
            if (view == null) Assert.Skip("No OpenGL ES 3 (ANGLE) on this machine.");
            var profile = Mannequin.Profile();
            var model = MeshModel.Create(MannequinMesh.For(profile));
            view.Theme = Bare(); view.ShowSkeleton = false;
            view.Camera.Target = new Vec3(0, 0.85f, 0); view.Camera.Distance = 3.2f; view.Camera.Yaw = 180; view.Camera.Pitch = 0;

            var image = view.Render(RestPose(profile, model), Width, Height);
            Assert.Equal(Width, image.Width); Assert.Equal(Height, image.Height);
            Assert.Equal(model.TriangleCount, view.Triangles);
            var (count, left, top, right, bottom) = Drawn(image, view.Theme.Background);
            Assert.True(count > 3000, "the mesh covers " + count + " pixels");

            // the top of the head and the feet are where the camera says they are (within a few pixels)
            view.Camera.Project(profile.RestWorldPos[profile.Human[HumanBones.Head]], out var head);
            view.Camera.Project(new Vec3(0, 0, 0), out var floor);
            Assert.InRange(top, head.Y - 60, head.Y);
            Assert.InRange(bottom, floor.Y - 12, floor.Y + 4);
            Assert.InRange((left + right) / 2f, Width / 2f - 4, Width / 2f + 4);

            // moved half a metre sideways, the picture moves with it
            var moved = Drawn(view.Render(RestPose(profile, model, new Vec3(0.5f, 0, 0)), Width, Height), view.Theme.Background);
            Assert.True(Math.Abs((moved.left + moved.right) / 2f - (left + right) / 2f) > 40);

            // with the mesh switched off the bones stand in, whatever the skeleton toggle says: never an empty view
            view.ShowMesh = false;
            int bones = Drawn(view.Render(RestPose(profile, model), Width, Height), view.Theme.Background).count;
            Assert.InRange(bones, 300, count - 1);
            Assert.Equal(0, view.Triangles);
        }

        [Fact]
        public void BonesFloorAndDevicesAreDrawnWithoutAMesh()
        {
            using var view = Open();
            if (view == null) Assert.Skip("No OpenGL ES 3 (ANGLE) on this machine.");
            var profile = Mannequin.Profile();
            view.Theme = Bare();
            view.Camera.Target = new Vec3(0, 0.85f, 0); view.Camera.Distance = 3.2f;

            var skeleton = Drawn(view.Render(RestPose(profile, null), Width, Height), view.Theme.Background);
            Assert.True(skeleton.count > 300, "the bones cover " + skeleton.count + " pixels");

            var frame = RestPose(profile, null);
            frame.Markers.Add(new Marker { Kind = MarkerKind.Tracker, Pose = new Pose(new Vec3(0.8f, 1.0f, 0), Quat.Identity), Color = new Rgba(240, 180, 60), Label = "tracker" });
            frame.PlayArea.AddRange(new[] { new Vec3(-1, 0, -1), new Vec3(1, 0, -1), new Vec3(1, 0, 1), new Vec3(-1, 0, 1) });
            frame.Overlay.Append("overlay text");
            var all = Drawn(view.Render(frame, Width, Height), view.Theme.Background);
            Assert.True(all.count > skeleton.count + 300);

            view.ShowDevices = false; view.ShowPlayArea = false;
            frame.Overlay.Clear();
            Assert.Equal(skeleton.count, Drawn(view.Render(frame, Width, Height), view.Theme.Background).count);

            view.Theme = new ViewportTheme(); // with the floor grid
            Assert.True(Drawn(view.Render(RestPose(profile, null), Width, Height), view.Theme.Background).count > skeleton.count + 1000);
        }
    }
}
