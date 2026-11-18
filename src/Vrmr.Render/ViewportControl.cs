using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using Silk.NET.OpenGL;
using VRMR.Render.Gl;

namespace VRMR.Render
{
    /// <summary>A frame of the 3D view as pixels: RGBA, 8 bits per channel, top row first.</summary>
    public sealed class ViewportImage
    {
        public byte[] Rgba;
        public int Width, Height;
    }

    /// <summary>
    ///     The 3D view: avatars (GPU-skinned mesh and skeleton), devices, the floor and tool overlays, drawn with OpenGL ES
    ///     inside Avalonia's compositor. It draws only when something changed: a submitted frame, the camera, a toggle, a
    ///     tool, its size. Paused means no frames and no CPU. See docs/dev/render-api.md.
    /// </summary>
    public class ViewportControl : OpenGlControlBase, ICustomHitTest
    {
        /// <summary>
        ///     Degrees of orbit per device pixel of drag, and metres of pan per pixel and metre of distance: the previous
        ///     view's feel.
        /// </summary>
        const float OrbitYaw = 0.4f, OrbitPitch = 0.3f, PanRate = 0.0016f;

        const double ZoomStep = 0.88;

        public static readonly StyledProperty<bool> ShowMeshProperty =
            AvaloniaProperty.Register<ViewportControl, bool>(nameof(ShowMesh), true);

        public static readonly StyledProperty<bool> ShowSkeletonProperty =
            AvaloniaProperty.Register<ViewportControl, bool>(nameof(ShowSkeleton), true);

        public static readonly StyledProperty<bool> ShowDevicesProperty =
            AvaloniaProperty.Register<ViewportControl, bool>(nameof(ShowDevices), true);

        public static readonly StyledProperty<bool> ShowHeadsetProperty =
            AvaloniaProperty.Register<ViewportControl, bool>(nameof(ShowHeadset));

        public static readonly StyledProperty<bool> ShowPlayAreaProperty =
            AvaloniaProperty.Register<ViewportControl, bool>(nameof(ShowPlayArea), true);

        public static readonly StyledProperty<bool> ShowBaseStationsProperty =
            AvaloniaProperty.Register<ViewportControl, bool>(nameof(ShowBaseStations), true);

        public static readonly StyledProperty<bool> ShowHintProperty =
            AvaloniaProperty.Register<ViewportControl, bool>(nameof(ShowHint), true);

        public static readonly StyledProperty<bool> FollowProperty =
            AvaloniaProperty.Register<ViewportControl, bool>(nameof(Follow), true);

        public static readonly StyledProperty<int> MsaaProperty =
            AvaloniaProperty.Register<ViewportControl, int>(nameof(Msaa));

        public static readonly StyledProperty<double> FrameCapProperty =
            AvaloniaProperty.Register<ViewportControl, double>(nameof(FrameCap), 60.0);

        public static readonly StyledProperty<bool> AnimatingProperty =
            AvaloniaProperty.Register<ViewportControl, bool>(nameof(Animating));

        public static readonly StyledProperty<ViewportTheme> ViewThemeProperty =
            AvaloniaProperty.Register<ViewportControl, ViewportTheme>(nameof(ViewTheme));

        readonly SceneBuilder _builder = new();
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly ViewportTheme _defaultTheme = new();

        readonly SceneDraw _draw = new();
        readonly Action<TimeSpan> _onDisplayFrame;
        readonly OverlayList _overlay = new();
        readonly FramePacer _pacer = new();
        readonly Stack<ViewFrame> _pool = new();
        Action<ViewportImage> _capture;
        Drag _drag;
        Vector2 _last;
        double _lastDrawTime = double.NaN;
        bool _pointerOver, _displayFrameQueued, _settling, _tickFailed;
        SceneRenderer _renderer;
        IViewportTool _tool;
        TopLevel _top;

        public ViewportControl()
        {
            Focusable = true;
            ClipToBounds = true;
            _onDisplayFrame = OnDisplayFrame;
        }

        // ---------------------------------------------------------------- view state

        /// <summary>Draw the avatars' meshes.</summary>
        public bool ShowMesh { get => GetValue(ShowMeshProperty); set => SetValue(ShowMeshProperty, value); }

        /// <summary>Draw the bones over the mesh (without a mesh the bones are always drawn).</summary>
        public bool ShowSkeleton
        {
            get => GetValue(ShowSkeletonProperty);
            set => SetValue(ShowSkeletonProperty, value);
        }

        /// <summary>Draw the headset, controllers and trackers.</summary>
        public bool ShowDevices { get => GetValue(ShowDevicesProperty); set => SetValue(ShowDevicesProperty, value); }

        /// <summary>Draw the headset's model among the devices (off by default: it covers the avatar's face).</summary>
        public bool ShowHeadset { get => GetValue(ShowHeadsetProperty); set => SetValue(ShowHeadsetProperty, value); }

        /// <summary>Draw the play area's outline on the floor.</summary>
        public bool ShowPlayArea
        {
            get => GetValue(ShowPlayAreaProperty);
            set => SetValue(ShowPlayAreaProperty, value);
        }

        /// <summary>Draw the base stations with their field of view.</summary>
        public bool ShowBaseStations
        {
            get => GetValue(ShowBaseStationsProperty);
            set => SetValue(ShowBaseStationsProperty, value);
        }

        /// <summary>Show the mouse hint while the pointer is over the view or the view is empty.</summary>
        public bool ShowHint { get => GetValue(ShowHintProperty); set => SetValue(ShowHintProperty, value); }

        /// <summary>Keep the camera's target over the main actor's hips. Panning switches it off.</summary>
        public bool Follow { get => GetValue(FollowProperty); set => SetValue(FollowProperty, value); }

        /// <summary>Multisampling of the mesh: 0 (off, the default: the preview shares the GPU with VR), 2, 4 or 8.</summary>
        public int Msaa { get => GetValue(MsaaProperty); set => SetValue(MsaaProperty, value); }

        /// <summary>Frames per second while <see cref="Animating" /> (the app lowers it while recording).</summary>
        public double FrameCap { get => GetValue(FrameCapProperty); set => SetValue(FrameCapProperty, value); }

        /// <summary>
        ///     While true the view raises <see cref="FrameTick" /> once per display frame, limited by <see cref="FrameCap" />.
        ///     Set it while playing or showing live devices; clear it when paused. No tick happens while it is false.
        /// </summary>
        public bool Animating { get => GetValue(AnimatingProperty); set => SetValue(AnimatingProperty, value); }

        /// <summary>
        ///     Every colour and the font of the view; null for the built-in dark theme. After changing fields of the same
        ///     instance call <see cref="RequestFrame" />.
        /// </summary>
        public ViewportTheme ViewTheme
        {
            get => GetValue(ViewThemeProperty);
            set => SetValue(ViewThemeProperty, value);
        }

        /// <summary>The orbit camera. After changing it from code call <see cref="RequestFrame" />.</summary>
        public ViewCamera Camera { get; } = new();

        /// <summary>Frame counters.</summary>
        public ViewportStats Stats { get; } = new();

        /// <summary>The frame on screen. Read-only: tools use it for picking.</summary>
        public ViewFrame Frame { get; private set; } = new();

        /// <summary>The active tool: it gets pointer and key events before the camera and draws over the scene. Null for none.</summary>
        public IViewportTool Tool
        {
            get => _tool;
            set
            {
                if (_tool == value) return;
                _tool = value;
                if (_drag == Drag.Tool) _drag = Drag.None;
                RequestFrame();
            }
        }

        ViewportTheme ThemeNow => ViewTheme ?? _defaultTheme;

        // ---------------------------------------------------------------- input

        bool ICustomHitTest.HitTest(Point point)
        {
            return true;
            // the whole rectangle takes the pointer, though Avalonia draws nothing here itself
        }

        /// <summary>
        ///     Raised on the UI thread before a paced frame, with the time since the previous tick: advance the playhead and
        ///     submit here.
        /// </summary>
        public event Action<TimeSpan> FrameTick;

        /// <summary>Raised after the user or <see cref="FrameAll" /> moved the camera.</summary>
        public event Action CameraChanged;

        /// <summary>Draws over the scene for every frame, without taking input.</summary>
        public event Action<ViewportControl, OverlayList> DrawOverlay;

        // ---------------------------------------------------------------- frames

        /// <summary>A cleared frame to fill and <see cref="Submit" />. Frames are recycled, so steady playback allocates nothing.</summary>
        public ViewFrame RentFrame()
        {
            var f = _pool.Count > 0 ? _pool.Pop() : new ViewFrame();
            f.Clear();
            return f;
        }

        /// <summary>Shows a frame and requests a draw. The frame belongs to the view from here on.</summary>
        public void Submit(ViewFrame frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (frame != Frame)
            {
                if (_pool.Count < 4) _pool.Push(Frame);
                Frame = frame;
            }

            RequestFrame();
        }

        /// <summary>Redraws once.</summary>
        public void RequestFrame()
        {
            RequestNextFrameRendering();
        }

        /// <summary>Captures the next drawn frame (and requests one). The callback runs on the UI thread.</summary>
        public void Capture(Action<ViewportImage> done)
        {
            _capture = done ?? throw new ArgumentNullException(nameof(done));
            RequestFrame();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            var p = change.Property;
            if (p == AnimatingProperty)
            {
                if (Animating)
                {
                    _pacer.Reset();
                    QueueDisplayFrame();
                }
            }
            else if (p == ShowMeshProperty || p == ShowSkeletonProperty || p == ShowDevicesProperty ||
                     p == ShowHeadsetProperty || p == ShowPlayAreaProperty
                     || p == ShowBaseStationsProperty || p == ShowHintProperty || p == FollowProperty ||
                     p == MsaaProperty || p == ViewThemeProperty)
                RequestFrame();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _top = TopLevel.GetTopLevel(this);
            if (Animating || _settling) QueueDisplayFrame();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _top = null;
            _displayFrameQueued = false;
            base.OnDetachedFromVisualTree(e);
        }

        // ---------------------------------------------------------------- pacing

        void QueueDisplayFrame()
        {
            if (_displayFrameQueued || _top == null) return;
            _displayFrameQueued = true;
            _top.RequestAnimationFrame(_onDisplayFrame);
        }

        void OnDisplayFrame(TimeSpan _)
        {
            _displayFrameQueued = false;
            if (_top == null) return;
            if (Animating)
            {
                if (_pacer.Tick(_clock.Elapsed.TotalSeconds, FrameCap, out double elapsed))
                {
                    try { FrameTick?.Invoke(TimeSpan.FromSeconds(elapsed)); }
                    catch (Exception ex)
                    {
                        if (!_tickFailed)
                        {
                            _tickFailed = true;
                            AppLog.Error("3D view frame tick: " + ex);
                        }
                    }

                    if (_settling) RequestFrame();
                }
            }
            else if (_settling) RequestFrame();

            if (Animating || _settling) QueueDisplayFrame();
        }

        // ---------------------------------------------------------------- drawing

        protected override void OnOpenGlInit(GlInterface gl)
        {
            try
            {
                _renderer = new SceneRenderer(GL.GetApi(gl.GetProcAddress));
                _draw.Atlas.Dirty = true;
                Stats.GlVersion = _renderer.Version;
                Stats.Adapter = _renderer.Adapter;
            }
            catch (Exception ex)
            {
                _renderer = null;
                AppLog.Error("3D view: OpenGL could not be set up: " + ex.Message);
            }
        }

        protected override void OnOpenGlDeinit(GlInterface gl)
        {
            try { _renderer?.Dispose(); }
            catch (Exception ex) { AppLog.Warn("3D view: freeing OpenGL objects: " + ex.Message); }

            _renderer = null;
        }

        protected override void OnOpenGlLost()
        {
            _renderer = null;
            // the context is gone and its objects with it
        }

        protected override void OnOpenGlRender(GlInterface gl, int fb)
        {
            if (_renderer == null) return;
            long allocated = GC.GetAllocatedBytesForCurrentThread(), started = Stopwatch.GetTimestamp();
            double scaling = _top?.RenderScaling ?? 1.0;
            int width = Math.Max(1, (int)(Bounds.Width * scaling)),
                height = Math.Max(1, (int)(Bounds.Height * scaling));
            double now = _clock.Elapsed.TotalSeconds;
            var theme = ThemeNow;
            var toggles = new ViewToggles
            {
                Mesh = ShowMesh, Skeleton = ShowSkeleton, Devices = ShowDevices, Headset = ShowHeadset,
                PlayArea = ShowPlayArea, BaseStations = ShowBaseStations, Hint = ShowHint
            };

            Camera.SetViewport((float)Bounds.Width, (float)Bounds.Height);
            bool more = FollowMainActor(now);
            _draw.Atlas.SetFamily(theme.FontFamily);
            more |= _draw.Atlas.BeginFrame();
            _draw.Batch.Clear();
            _draw.Batch.Scale = _draw.Scale = (float)scaling;
            _draw.Msaa = Msaa;
            _builder.BuildScene(_draw, Frame, Camera, theme, toggles);
            _overlay.Bind(_draw.Batch, _draw.Atlas, Camera);
            try
            {
                DrawOverlay?.Invoke(this, _overlay);
                _tool?.Draw(this, _overlay);
            }
            catch (Exception ex) { AppLog.Error("3D view overlay: " + ex.Message); }

            _builder.BuildChrome(_draw, Frame, Camera, theme, toggles, _pointerOver);
            _renderer.Render(_draw, width, height, (uint)fb, false);

            if (_capture != null)
            {
                var done = _capture;
                _capture = null;
                var image = new ViewportImage { Width = width, Height = height, Rgba = new byte[width * height * 4] };
                _renderer.ReadPixels(image.Rgba);
                gl.BindFramebuffer((int)FramebufferTarget.Framebuffer, fb);
                done(image);
            }

            Stats.FramesDrawn++;
            Stats.Triangles = _renderer.LastTriangles;
            Stats.LastFrameGapMs = double.IsNaN(_lastDrawTime) ? 0 : (now - _lastDrawTime) * 1000.0;
            _lastDrawTime = now;
            Stats.LastFrameCpuMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Stats.LastFrameAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;

            _settling = more;
            if (more) QueueDisplayFrame();
        }

        /// <summary>
        ///     Eases the camera's target towards the main actor's hips (on the floor plane), as the previous view did on each
        ///     of its paints. Returns true while it has not arrived, so a paused view keeps drawing until it has.
        /// </summary>
        bool FollowMainActor(double now)
        {
            if (!Follow) return false;
            var actor = Frame.Main;
            if (actor == null) return false;
            int hips = SkeletonLayout.For(actor.Profile).Hips;
            if (hips < 0) return false;
            var h = actor.NodePos[hips];
            var target = Camera.Target;
            var goal = new Vec3(h.x, target.y, h.z);
            float off = (goal - target).Length;
            if (off < 1e-4f) return false;
            // 15 % of the way per 60 Hz frame, whatever the real frame time
            double dt = double.IsNaN(_lastDrawTime) ? 1.0 / 60 : Math.Min(0.1, Math.Max(0, now - _lastDrawTime));
            float k = 1f - (float)Math.Pow(0.85, dt * 60.0);
            if (off * (1f - k) < 0.001f)
            {
                Camera.Target = goal;
                return false;
            }

            Camera.Target = Vec3.Lerp(target, goal, k);
            return true;
        }

        // ---------------------------------------------------------------- camera

        /// <summary>Frames the main actor, or every device when there is none.</summary>
        public void FrameAll()
        {
            var actor = Frame.Main;
            var layout = actor == null ? null : SkeletonLayout.For(actor.Profile);
            if (layout != null && layout.Hips >= 0)
            {
                var h = actor.NodePos[layout.Hips];
                Camera.Target = new Vec3(h.x, Math.Max(0.4f, layout.EyeHeight * 0.55f), h.z);
                Camera.Distance = Math.Max(1.2f, layout.EyeHeight * 2.6f);
                OnCameraChanged();
            }
            else FrameDevices();
        }

        /// <summary>
        ///     Frames every SteamVR device shown (the headset even with its model off) and the play area. False when there is
        ///     nothing to frame.
        /// </summary>
        public bool FrameDevices()
        {
            var toggles = new ViewToggles
                { Devices = ShowDevices, Headset = ShowHeadset, BaseStations = ShowBaseStations };
            bool any = false;
            Vec3 lo = default, hi = default;

            void Add(Vec3 p)
            {
                if (!any)
                {
                    lo = hi = p;
                    any = true;
                    return;
                }

                lo = new Vec3(Math.Min(lo.x, p.x), Math.Min(lo.y, p.y), Math.Min(lo.z, p.z));
                hi = new Vec3(Math.Max(hi.x, p.x), Math.Max(hi.y, p.y), Math.Max(hi.z, p.z));
            }

            foreach (var m in Frame.Markers)
                if (m.IsDevice && (toggles.Shows(m.Kind) || m.Kind == MarkerKind.Headset))
                    Add(m.Pose.pos);
            if (ShowPlayArea)
                foreach (var p in Frame.PlayArea)
                    Add(p);
            if (!any) return false;
            var c = (lo + hi) * 0.5f;
            float extent = Math.Max(Math.Max(hi.x - lo.x, hi.z - lo.z), hi.y);
            Camera.Target = new Vec3(c.x, Math.Max(0.6f, c.y * 0.8f), c.z);
            Camera.Distance = MathX.Clamp(extent * 1.5f + 1.2f, 2.5f, 25f);
            Camera.Pitch = Math.Max(Camera.Pitch, 20);
            Follow = false;
            OnCameraChanged();
            return true;
        }

        void OnCameraChanged()
        {
            RequestFrame();
            CameraChanged?.Invoke();
        }

        /// <summary>
        ///     Index in <see cref="ViewFrame.Markers" /> of the shown marker nearest to a screen point within radius pixels,
        ///     or -1.
        /// </summary>
        public int PickMarker(Vector2 screen, float radius)
        {
            var toggles = new ViewToggles
                { Devices = ShowDevices, Headset = ShowHeadset, BaseStations = ShowBaseStations };
            int best = -1;
            float bestD = radius * radius;
            for (int i = 0; i < Frame.Markers.Count; i++)
            {
                var m = Frame.Markers[i];
                if (!toggles.Shows(m.Kind) || !Camera.Project(m.Pose.pos, out var s)) continue;
                float d = Vector2.DistanceSquared(s, screen);
                if (d <= bestD)
                {
                    bestD = d;
                    best = i;
                }
            }

            return best;
        }

        ViewPointer Pointer(PointerEventArgs e, MouseButton button, int clicks = 0)
        {
            var p = e.GetPosition(this);
            return new ViewPointer(new Vector2((float)p.X, (float)p.Y), button, e.KeyModifiers, clicks);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            if (_drag != Drag.None) return; // a second button during a drag
            var button = e.GetCurrentPoint(this).Properties.PointerUpdateKind.GetMouseButton();
            var ev = Pointer(e, button, e.ClickCount);
            if (_tool != null && _tool.PointerPressed(this, ev))
            {
                _drag = Drag.Tool;
                e.Pointer.Capture(this);
                e.Handled = true;
                RequestFrame();
                return;
            }

            if (button == MouseButton.Left && e.ClickCount == 2)
            {
                FrameAll();
                e.Handled = true;
                return;
            }

            if (button != MouseButton.Left && button != MouseButton.Right && button != MouseButton.Middle) return;
            _drag = button == MouseButton.Left ? Drag.Orbit : Drag.Pan;
            _last = ev.Position;
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var ev = Pointer(e, MouseButton.None);
            if (_drag == Drag.Tool || (_drag == Drag.None && _tool != null))
            {
                _tool?.PointerMoved(this, ev);
                if (_drag == Drag.Tool) RequestFrame();
                return;
            }

            if (_drag == Drag.None) return;
            // the previous view measured drags in device pixels: keep its feel at any display scaling
            float scaling = (float)(_top?.RenderScaling ?? 1.0);
            float dx = (ev.Position.X - _last.X) * scaling, dy = (ev.Position.Y - _last.Y) * scaling;
            _last = ev.Position;
            if (_drag == Drag.Orbit)
            {
                Camera.Yaw += dx * OrbitYaw;
                Camera.Pitch += dy * OrbitPitch;
            }
            else
            {
                float k = Camera.Distance * PanRate;
                Camera.Target = Camera.Target - Camera.Right * (dx * k) + Camera.Up * (dy * k);
                Follow = false;
            }

            OnCameraChanged();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            EndDrag(e, e.InitialPressMouseButton);
            e.Pointer.Capture(null);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            if (_drag == Drag.Tool)
            {
                _drag = Drag.None;
                _tool?.PointerReleased(this, new ViewPointer(_last, MouseButton.None, KeyModifiers.None));
                RequestFrame();
            }

            _drag = Drag.None;
        }

        void EndDrag(PointerEventArgs e, MouseButton button)
        {
            var drag = _drag;
            _drag = Drag.None;
            if (drag != Drag.Tool) return;
            _tool?.PointerReleased(this, Pointer(e, button));
            RequestFrame();
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            if (e.Delta.Y == 0) return;
            Camera.Distance *= (float)Math.Pow(ZoomStep, e.Delta.Y);
            e.Handled = true;
            OnCameraChanged();
        }

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            base.OnPointerEntered(e);
            _pointerOver = true;
            if (ShowHint) RequestFrame();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _pointerOver = false;
            if (ShowHint) RequestFrame();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (_tool != null && _tool.KeyDown(this, e.Key, e.KeyModifiers))
            {
                e.Handled = true;
                RequestFrame();
                return;
            }

            base.OnKeyDown(e);
        }

        enum Drag { None, Orbit, Pan, Tool }
    }
}
