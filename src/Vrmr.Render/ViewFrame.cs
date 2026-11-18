using System;
using System.Collections.Generic;
using System.Text;

namespace VRMR.Render
{
    /// <summary>What a marker stands for; the view picks the shape.</summary>
    public enum MarkerKind { Axes, Camera, Point, Headset, Controller, Tracker, BaseStation }

    /// <summary>A device, the VRChat camera, a point or a set of axes in the 3D view.</summary>
    public struct Marker
    {
        public MarkerKind Kind;
        public Pose Pose;
        public Rgba Color;

        /// <summary>Text next to the marker (may have two lines), or null.</summary>
        public string Label;

        /// <summary>The last known pose of a device that is not tracked right now: drawn dimmed.</summary>
        public bool Faded;

        /// <summary>Model scale (0 = 1): takes draw devices at their tracking scale.</summary>
        public float Size;

        /// <summary>No line down to the floor.</summary>
        public bool NoDropLine;

        /// <summary>Base stations: half angles of the field of view in degrees (0 = the default).</summary>
        public float FovLeft, FovRight, FovTop, FovBottom;

        /// <summary>True for the SteamVR devices (headset, controllers, trackers, base stations).</summary>
        public readonly bool IsDevice => Kind >= MarkerKind.Headset;
    }

    /// <summary>How an actor is drawn.</summary>
    public enum ActorStyle
    {
        /// <summary>Mesh and bones as the view's Mesh and Skeleton toggles say.</summary>
        Full,

        /// <summary>A plain skeleton in one colour (the other actors of a scene); the toggles do not affect it.</summary>
        Figure
    }

    /// <summary>
    ///     Face shape weights of one actor for one frame. Names are the recording's (Unified Expressions or ARKit); shapes
    ///     the actor's mesh does not have are ignored.
    /// </summary>
    public sealed class FaceWeights
    {
        MeshModel _model;
        float[] _w = Array.Empty<float>();

        internal void Bind(MeshModel model)
        {
            _model = model;
            int n = model == null ? 0 : model.FaceChannelCount * 2;
            if (_w.Length != n) _w = new float[n];
            Clear();
        }

        /// <summary>Forgets every weight.</summary>
        public void Clear()
        {
            Array.Fill(_w, float.NaN);
        }

        /// <summary>Sets a shape by name.</summary>
        public void Set(string name, float weight)
        {
            Set(_model == null ? -1 : _model.FaceChannel(name), weight);
        }

        /// <summary>Sets a shape by the index <see cref="MeshModel.FaceChannel" /> returned (negative: ignored).</summary>
        public void Set(int channel, float weight)
        {
            if ((uint)channel < (uint)_w.Length) _w[channel] = weight;
        }

        /// <summary>The weight of a mesh channel: its own name's value, else the value given under its other name, else 0.</summary>
        internal float Resolve(int channel)
        {
            int n = _w.Length / 2;
            if ((uint)channel >= (uint)n) return 0;
            float w = _w[channel];
            if (float.IsNaN(w)) w = _w[channel + n];
            return float.IsNaN(w) ? 0 : w;
        }
    }

    /// <summary>One avatar in a frame: its skeleton, its pose and optionally its mesh.</summary>
    public sealed class ActorView
    {
        public readonly FaceWeights Face = new();

        /// <summary>Colour of a <see cref="ActorStyle.Figure" />.</summary>
        public Rgba FigureColor;

        /// <summary>Per <see cref="MeshModel.Parts" /> index, true hides that part; null shows all.</summary>
        public bool[] HiddenParts;

        /// <summary>Avatar scale of this pose.</summary>
        public float Scale = 1f;

        public ActorStyle Style;

        /// <summary>The skeleton.</summary>
        public AvatarProfile Profile { get; private set; }

        /// <summary>The mesh, or null for a skeleton only.</summary>
        public MeshModel Model { get; private set; }

        /// <summary>World position per profile node.</summary>
        public Vec3[] NodePos { get; private set; } = Array.Empty<Vec3>();

        /// <summary>World rotation per profile node.</summary>
        public Quat[] NodeRot { get; private set; } = Array.Empty<Quat>();

        /// <summary>True when the mesh can be skinned onto this pose (it was built for a skeleton with the same nodes).</summary>
        internal bool MeshUsable => Model != null && !Model.IsDisposed && Model.NodeCount == NodePos.Length;

        internal void Reset(AvatarProfile profile, MeshModel model)
        {
            Profile = profile;
            Model = model;
            int n = profile.Nodes.Count;
            if (NodePos.Length != n)
            {
                NodePos = new Vec3[n];
                NodeRot = new Quat[n];
            }

            Scale = 1f;
            Style = ActorStyle.Full;
            FigureColor = default;
            HiddenParts = null;
            Face.Bind(model);
        }
    }

    /// <summary>
    ///     Everything the 3D view shows at one moment, as plain data. Rent one from the view, fill it, submit it; after
    ///     that it belongs to the view.
    /// </summary>
    public sealed class ViewFrame
    {
        /// <summary>Devices, the VRChat camera, points and axes.</summary>
        public readonly List<Marker> Markers = new();

        /// <summary>Text in the top-left corner; a line feed starts a new line.</summary>
        public readonly StringBuilder Overlay = new();

        /// <summary>Text in the top-right corner.</summary>
        public readonly StringBuilder OverlayRight = new();

        /// <summary>Corners of the play area on the floor; empty for none.</summary>
        public readonly List<Vec3> PlayArea = new();

        readonly List<ActorView> _actors = new();
        readonly List<ActorView> _pool = new();

        /// <summary>Shown in the centre when the frame has no actor and no device.</summary>
        public string EmptyText;

        /// <summary>Index of the actor the camera follows and frames.</summary>
        public int MainActor;

        /// <summary>Draws the recording frame around the view.</summary>
        public bool Recording;

        /// <summary>The actors added so far.</summary>
        public IReadOnlyList<ActorView> Actors => _actors;

        /// <summary>The actor the camera follows, or null.</summary>
        internal ActorView Main => _actors.Count == 0 ? null : _actors[MathX.Clamp(MainActor, 0, _actors.Count - 1)];

        /// <summary>Adds an actor and returns it with its pose arrays sized for the profile. model may be null.</summary>
        public ActorView AddActor(AvatarProfile profile, MeshModel model)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (_actors.Count == _pool.Count) _pool.Add(new ActorView());
            var a = _pool[_actors.Count];
            a.Reset(profile, model);
            _actors.Add(a);
            return a;
        }

        /// <summary>True when the frame holds a SteamVR device.</summary>
        internal bool HasDevice()
        {
            for (int i = 0; i < Markers.Count; i++)
                if (Markers[i].IsDevice)
                    return true;
            return false;
        }

        /// <summary>Empties the frame for reuse; the arrays and lists keep their capacity.</summary>
        internal void Clear()
        {
            _actors.Clear();
            MainActor = 0;
            Markers.Clear();
            PlayArea.Clear();
            Overlay.Clear();
            OverlayRight.Clear();
            EmptyText = null;
            Recording = false;
        }
    }
}
