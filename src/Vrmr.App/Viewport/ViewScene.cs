using System;
using System.Collections.Generic;
using Avalonia;

namespace VRMR.App.Viewport
{
    /// <summary>What a marker in the 3D view stands for; the view picks the shape.</summary>
    public enum MarkerKind { Point, Camera, VrcPose, Headset, Controller, Tracker, BaseStation }

    /// <summary>The colour role of a marker; the view maps it to a theme colour.</summary>
    public enum MarkerTint { Neutral, Accent, Head, Left, Right, Tracker, Camera, VrcPose, Station }

    /// <summary>A device, camera or reference pose drawn in the 3D view.</summary>
    public struct ViewMarker
    {
        public Pose Pose;
        public MarkerKind Kind;
        public MarkerTint Tint;

        /// <summary>Model size (1 = real size; takes use their tracking scale).</summary>
        public float Size;

        /// <summary>Not tracked right now: drawn dimmed at its last pose.</summary>
        public bool Faded;

        /// <summary>No line down to the floor.</summary>
        public bool NoDropLine;

        public string Label;

        /// <summary>Base stations: field of view half-angles in degrees.</summary>
        public float FovL, FovR, FovT, FovB;
    }

    /// <summary>
    ///     Everything the 3D view shows for one frame: the avatar's pose, markers, the play area and the overlay text.
    ///     Presenters fill it (from a baked clip, the live devices or a scene) and then call
    ///     <see cref="IViewport.SceneChanged" />.
    /// </summary>
    public sealed class ViewScene
    {
        public readonly List<ViewMarker> Markers = new();

        /// <summary>Face shape weights (0..1) by name, or null.</summary>
        public IReadOnlyDictionary<string, float> FaceWeights;

        /// <summary>Uniform scale of the mesh on top of the skeleton.</summary>
        public float MeshScale = 1f;

        /// <summary>World position and rotation per node of <see cref="Profile" />.</summary>
        public Vec3[] NodePos = Array.Empty<Vec3>();

        public Quat[] NodeRot = Array.Empty<Quat>();

        /// <summary>Text in the view's top-left and top-right corners.</summary>
        public string Overlay = "", OverlayRight = "";

        /// <summary>The play area's four corners on the floor, or null.</summary>
        public Vec3[] PlayArea;

        /// <summary>The skeleton being shown, or null for no avatar.</summary>
        public AvatarProfile Profile;

        /// <summary>Makes the pose arrays fit the profile (reusing them when they already do).</summary>
        public void SetProfile(AvatarProfile profile)
        {
            Profile = profile;
            int n = profile?.Nodes.Count ?? 0;
            if (NodePos.Length != n)
            {
                NodePos = new Vec3[n];
                NodeRot = new Quat[n];
            }
        }

        /// <summary>Moves everything shown by a pose (a placement that is not solved in yet).</summary>
        public void Transform(Pose d)
        {
            for (int i = 0; i < NodePos.Length; i++)
            {
                NodePos[i] = d.TransformPoint(NodePos[i]);
                NodeRot[i] = (d.rot * NodeRot[i]).Normalized;
            }

            for (int i = 0; i < Markers.Count; i++)
            {
                var m = Markers[i];
                m.Pose = d * m.Pose;
                Markers[i] = m;
            }

            if (PlayArea != null)
                for (int i = 0; i < PlayArea.Length; i++)
                    PlayArea[i] = d.TransformPoint(PlayArea[i]);
        }
    }

    /// <summary>What the view chips switch on and off.</summary>
    public sealed class ViewOptions
    {
        public bool Mesh = true, Skeleton, Devices = true, Headset, PlayArea = true, BaseStations = true;

        /// <summary>While recording the view stays light: skeleton only.</summary>
        public bool Recording;
    }

    /// <summary>What a move / turn handle in the view is doing; the view draws it and reports drags on it.</summary>
    public sealed class ViewGizmo
    {
        /// <summary>World position of the handle.</summary>
        public Vec3 Center;

        /// <summary>Turn rings instead of move arrows.</summary>
        public bool Rotate;

        /// <summary>Text next to the handle (what the drag has done so far, or the key hints).</summary>
        public string Status = "";
    }

    /// <summary>A tool that owns a gizmo in the 3D view (the placement tool). The view calls it for drags on the gizmo.</summary>
    public interface IViewportTool
    {
        /// <summary>The handle to show, or null when the tool has nothing to grab right now. Asked before every draw and hit test.</summary>
        ViewGizmo Gizmo { get; }

        /// <summary>A drag on the gizmo started.</summary>
        void DragStarted();

        /// <summary>The drag so far: the movement and the turn since it started.</summary>
        void Dragged(Vec3 move, Quat turn, float degrees);

        void DragEnded();

        /// <summary>A key pressed while the view has the tool; true when the tool used it.</summary>
        bool KeyDown(Avalonia.Input.Key key);
    }

    /// <summary>
    ///     The 3D view as the app uses it. The control itself comes from the render project; until it does, the app's own
    ///     lightweight skeleton view implements this.
    /// </summary>
    public interface IViewport
    {
        /// <summary>The frame being shown. Change it, then call <see cref="SceneChanged" />.</summary>
        ViewScene Scene { get; }

        ViewOptions Options { get; }

        /// <summary>The active tool, or null for plain camera control.</summary>
        IViewportTool Tool { get; set; }

        /// <summary>
        ///     While true the view raises <see cref="FrameTick" /> once per display frame (limited by <see cref="FrameCap" />):
        ///     set it while playing or showing live devices, clear it when paused. No tick and no draw happen while it is false.
        /// </summary>
        bool Animating { get; set; }

        /// <summary>Frames per second while animating (60; lower while recording).</summary>
        double FrameCap { get; set; }

        /// <summary>Number of frames drawn since the view was created (for the idle measurement and tests).</summary>
        long FramesDrawn { get; }

        /// <summary>Asks for one redraw: the scene, an option or the tool changed.</summary>
        void SceneChanged();

        /// <summary>Moves the camera so the avatar (or everything shown) fits.</summary>
        void FrameAll();

        /// <summary>Moves the camera so every device fits; false when there is none.</summary>
        bool FrameDevices();

        /// <summary>World point to view pixels; false when it is behind the camera.</summary>
        bool Project(Vec3 world, out Point screen);

        /// <summary>The ray under a view pixel.</summary>
        void Unproject(Point screen, out Vec3 origin, out Vec3 direction);

        /// <summary>
        ///     Raised on the UI thread before a paced frame with the time since the last one: advance the playhead and refill
        ///     the scene here.
        /// </summary>
        event Action<TimeSpan> FrameTick;
    }
}
