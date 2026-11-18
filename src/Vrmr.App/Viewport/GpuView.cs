using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using VRMR.Render;

namespace VRMR.App.Viewport
{
    using Key = Avalonia.Input.Key;
    using RenderMarker = VRMR.Render.Marker;
    using RenderKind = VRMR.Render.MarkerKind;

    /// <summary>
    /// The app's 3D view: the render project's GPU control behind the app's <see cref="IViewport"/> seam. It copies the
    /// <see cref="ViewScene"/> into a render frame whenever the scene changed, loads the avatar's mesh in the background,
    /// maps the app's theme tokens to the view's colours and lends the placement tool the renderer's gizmo.
    /// </summary>
    public sealed class GpuView : ViewportControl, IViewport
    {
        /// <summary>How many avatar meshes stay loaded (the open take's and the one worn live).</summary>
        const int MeshesKept = 2;

        sealed class MeshEntry
        {
            public AvatarProfile Profile;
            public MeshModel Model;
            public bool Loading;
            public long Used;
        }

        readonly List<MeshEntry> _meshes = new List<MeshEntry>();
        readonly GizmoTool _gizmoTool;
        IViewportTool _appTool;
        IReadOnlyList<string> _hiddenNames;
        MeshModel _hiddenFor;
        bool[] _hidden;
        long _use;

        public GpuView()
        {
            _gizmoTool = new GizmoTool(this);
            ShowHint = false; // the help sheet (F1) lists the mouse; the corner belongs to the panel buttons
            ActualThemeVariantChanged += (o, e) => ApplyTheme();
        }

        public ViewScene Scene { get; } = new ViewScene();
        public ViewOptions Options { get; } = new ViewOptions();
        public long FramesDrawn => Stats.FramesDrawn;

        /// <summary>The names of the avatar's meshes the user hid (Settings → View); asked for every scene change.</summary>
        public Func<IReadOnlyList<string>> HiddenMeshes { get; set; }

        /// <summary>Raised on the UI thread when an avatar's mesh finished loading (the mesh list in the settings refreshes).</summary>
        public event Action MeshLoaded;

        IViewportTool IViewport.Tool
        {
            get => _appTool;
            set { _appTool = value; _gizmoTool.Tool = value; Tool = value == null ? null : _gizmoTool; }
        }

        public bool Project(Vec3 world, out Point screen)
        {
            bool ok = Camera.Project(world, out var s);
            screen = new Point(s.X, s.Y);
            return ok;
        }

        public void Unproject(Point screen, out Vec3 origin, out Vec3 direction) =>
            Camera.Ray(new Vector2((float)screen.X, (float)screen.Y), out origin, out direction);

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Camera.SetViewport((float)Math.Max(1, Bounds.Width), (float)Math.Max(1, Bounds.Height));
            ApplyTheme();
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            // tools and tests project points between draws: keep the camera's idea of the view's size current
            Camera.SetViewport((float)Math.Max(1, e.NewSize.Width), (float)Math.Max(1, e.NewSize.Height));
        }

        // ------------------------------------------------------------------------------------------ scene -> frame

        /// <summary>Copies the scene and the options into a frame and shows it.</summary>
        public void SceneChanged()
        {
            var s = Scene; var o = Options;
            ShowMesh = o.Mesh && !o.Recording;
            ShowSkeleton = o.Skeleton || o.Recording;
            ShowDevices = o.Devices; ShowHeadset = o.Headset; ShowPlayArea = o.PlayArea; ShowBaseStations = o.BaseStations;

            var f = RentFrame();
            var p = s.Profile;
            if (p != null && s.NodePos.Length == p.Nodes.Count)
            {
                var model = ShowMesh ? MeshFor(p) : null;
                var a = f.AddActor(p, model);
                Array.Copy(s.NodePos, a.NodePos, s.NodePos.Length);
                Array.Copy(s.NodeRot, a.NodeRot, s.NodeRot.Length);
                a.Scale = s.MeshScale;
                if (model != null)
                {
                    if (s.FaceWeights != null) foreach (var kv in s.FaceWeights) a.Face.Set(kv.Key, kv.Value);
                    a.HiddenParts = HiddenParts(model);
                }
            }
            var theme = ViewTheme;
            foreach (var m in s.Markers)
                f.Markers.Add(new RenderMarker
                {
                    Kind = KindOf(m.Kind), Pose = m.Pose, Color = TintColor(m.Tint, theme), Label = m.Label, Faded = m.Faded, Size = m.Size, NoDropLine = m.NoDropLine,
                    FovLeft = m.FovL, FovRight = m.FovR, FovTop = m.FovT, FovBottom = m.FovB,
                });
            if (s.PlayArea != null) f.PlayArea.AddRange(s.PlayArea);
            f.Overlay.Append(s.Overlay);
            f.OverlayRight.Append(s.OverlayRight);
            f.Recording = o.Recording;
            Submit(f);
        }

        static RenderKind KindOf(MarkerKind kind) => kind switch
        {
            MarkerKind.Camera => RenderKind.Camera,
            MarkerKind.VrcPose => RenderKind.Axes,
            MarkerKind.Headset => RenderKind.Headset,
            MarkerKind.Controller => RenderKind.Controller,
            MarkerKind.Tracker => RenderKind.Tracker,
            MarkerKind.BaseStation => RenderKind.BaseStation,
            _ => RenderKind.Point,
        };

        Rgba TintColor(MarkerTint tint, ViewportTheme theme) => tint switch
        {
            MarkerTint.Accent => Token("AccentBrush", theme.Text),
            MarkerTint.Head => theme.Text,
            MarkerTint.Left => theme.BoneLeft,
            MarkerTint.Right => theme.BoneRight,
            MarkerTint.Tracker or MarkerTint.Camera => Token("WarningBrush", theme.Text),
            MarkerTint.VrcPose => Token("VrcPoseBrush", theme.Text),
            MarkerTint.Station => Token("StationBrush", theme.TextDim),
            _ => theme.TextDim,
        };

        // ------------------------------------------------------------------------------------------ meshes

        /// <summary>The avatar's mesh when it is loaded; otherwise starts loading it and returns null (the bones show meanwhile).</summary>
        MeshModel MeshFor(AvatarProfile profile)
        {
            MeshEntry entry = null;
            foreach (var e in _meshes) if (e.Profile == profile) { entry = e; break; }
            if (entry == null)
            {
                entry = new MeshEntry { Profile = profile, Loading = true };
                _meshes.Add(entry);
                var loading = entry;
                Task.Run(() =>
                {
                    MeshModel model = null;
                    try
                    {
                        // the built-in mannequin has a mesh of its own; every other avatar's comes from its model file
                        var mannequin = MannequinMesh.For(profile);
                        model = mannequin != null ? MeshModel.Create(mannequin) : MeshModel.Create(SkinnedAvatar.ForProfile(profile));
                    }
                    catch (Exception ex) { AppLog.Warn("Avatar mesh for the 3D view: " + ex.Message); }
                    Dispatcher.UIThread.Post(() =>
                    {
                        loading.Model = model; loading.Loading = false;
                        if (!_meshes.Contains(loading)) { model?.Dispose(); return; }
                        if (Scene.Profile == profile) SceneChanged();
                        MeshLoaded?.Invoke();
                    });
                });
            }
            entry.Used = ++_use;
            // forget the meshes of avatars that are not shown any more
            while (_meshes.Count > MeshesKept)
            {
                MeshEntry oldest = null;
                foreach (var e in _meshes) if (e != entry && (oldest == null || e.Used < oldest.Used)) oldest = e;
                _meshes.Remove(oldest);
                oldest.Model?.Dispose();
            }
            return entry.Model;
        }

        bool[] HiddenParts(MeshModel model)
        {
            var names = HiddenMeshes?.Invoke();
            if (names == null || names.Count == 0) return null;
            if (!ReferenceEquals(names, _hiddenNames) || _hiddenFor != model) { _hiddenNames = names; _hiddenFor = model; _hidden = model.HiddenParts(names); }
            return _hidden;
        }

        // ------------------------------------------------------------------------------------------ theme

        /// <summary>
        /// A theme colour by its brush key. The colour token behind the brush ("TextBrush" -> "TextColor") is read for
        /// the current theme variant: a shared brush object does not tell which variant its colour was resolved for.
        /// </summary>
        Rgba Token(string key, Rgba fallback)
        {
            string colorKey = key.EndsWith("Brush", StringComparison.Ordinal) ? key.Substring(0, key.Length - 5) + "Color" : key;
            if (this.TryFindResource(colorKey, ActualThemeVariant, out var v) && v is Color c) return new Rgba(c.R, c.G, c.B, c.A);
            if (this.TryFindResource(key, ActualThemeVariant, out v) && v is ISolidColorBrush solid) { c = solid.Color; return new Rgba(c.R, c.G, c.B, c.A); }
            return fallback;
        }

        static Rgba Mix(Rgba a, Rgba b) => new Rgba((byte)((a.R + b.R) / 2), (byte)((a.G + b.G) / 2), (byte)((a.B + b.B) / 2));

        /// <summary>Fills the view's colours from the app's theme tokens (again when the theme switches).</summary>
        void ApplyTheme()
        {
            var d = new ViewportTheme();
            var t = new ViewportTheme
            {
                Background = Mix(Token("ViewTopColor", d.Background), Token("ViewBottomColor", d.Background)),
                GridMinor = Token("GridBrush", d.GridMinor),
                GridMajor = Token("BorderBrush", d.GridMajor),
                BoneLeft = Token("LimbLeftBrush", d.BoneLeft),
                BoneRight = Token("LimbRightBrush", d.BoneRight),
                BoneCenter = Token("TextBrush", d.BoneCenter),
                Text = Token("TextBrush", d.Text),
                TextDim = Token("TextSecondaryBrush", d.TextDim),
                TextFaint = Token("TextFaintBrush", d.TextFaint),
                Record = Token("RecordBrush", d.Record),
                GizmoHot = Token("WarningBrush", d.GizmoHot),
            };
            t.OriginX = t.BoneRight.WithAlpha(150); t.OriginZ = t.BoneLeft.WithAlpha(150);
            var area = Token("SuccessBrush", d.PlayAreaLine);
            t.PlayAreaLine = area.WithAlpha(150); t.PlayAreaFill = area.WithAlpha(22);
            // untextured mesh parts and the secondary bones keep their neutral greys in both themes
            ViewTheme = t;
            if (Scene.Markers.Count > 0) SceneChanged(); // marker colours come from the theme too
        }

        // ------------------------------------------------------------------------------------------ the placement gizmo

        /// <summary>Lends the app's tool (which only knows "a drag moved or turned by this much") the renderer's gizmo.</summary>
        sealed class GizmoTool : VRMR.Render.IViewportTool
        {
            readonly GpuView _view;
            readonly Gizmo _gizmo = new Gizmo();
            GizmoPart _hot;

            public GizmoTool(GpuView view) { _view = view; }

            public IViewportTool Tool;

            bool Sync()
            {
                var g = Tool?.Gizmo;
                if (g == null) return false;
                if (_gizmo.Active == GizmoPart.None) _gizmo.Center = g.Center;
                _gizmo.Rotate = g.Rotate;
                return true;
            }

            public bool PointerPressed(ViewportControl view, in ViewPointer e)
            {
                if (e.Button != MouseButton.Left || !Sync()) return false;
                var part = _gizmo.Hit(view.Camera, e.Position);
                if (part == GizmoPart.None) return false;
                _hot = part;
                _gizmo.Begin(view.Camera, part, e.Position);
                Tool.DragStarted();
                return true;
            }

            public void PointerMoved(ViewportControl view, in ViewPointer e)
            {
                if (_gizmo.Active != GizmoPart.None)
                {
                    _gizmo.Delta(view.Camera, e.Position, e.Shift, out var move, out var turn);
                    Tool?.Dragged(move, turn, _gizmo.Degrees);
                    return;
                }
                if (!Sync()) return;
                var hot = _gizmo.Hit(view.Camera, e.Position);
                if (hot == _hot) return;
                _hot = hot;
                _view.Cursor = hot == GizmoPart.None ? Cursor.Default : new Cursor(StandardCursorType.Hand);
                view.RequestFrame();
            }

            public void PointerReleased(ViewportControl view, in ViewPointer e)
            {
                if (_gizmo.Active == GizmoPart.None) return;
                _gizmo.End();
                Tool?.DragEnded();
            }

            public bool KeyDown(ViewportControl view, Key key, KeyModifiers modifiers) => Tool != null && Tool.KeyDown(key);

            public void Draw(ViewportControl view, OverlayList overlay)
            {
                var g = Tool?.Gizmo;
                if (g == null) return;
                _gizmo.Center = g.Center; // the tool moves the handle with the drag
                _gizmo.Rotate = g.Rotate;
                var theme = view.ViewTheme ?? new ViewportTheme();
                _gizmo.Draw(view.Camera, overlay, _gizmo.Active != GizmoPart.None ? _gizmo.Active : _hot, theme);
                if (!string.IsNullOrEmpty(g.Status) && view.Camera.Project(g.Center, out var c))
                    overlay.Text(new Vector2(c.X + 14, c.Y + _gizmo.Size * 0.6f), g.Status, theme.Text, theme.OverlaySize);
            }
        }
    }
}
