using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace VRMR.UI
{
    /// <summary>Lightweight GDI+ 3D viewport: orbit camera, floor grid, avatar skeleton, devices and camera markers.</summary>
    public sealed class Viewport3D : DarkControl
    {
        public float Yaw = 22, Pitch = 12, Dist = 3.0f, Fov = 45;
        public Vec3 Target = new Vec3(0, 0.75f, 0);
        public bool Follow = true;

        public AvatarProfile Profile;
        public Vec3[] NodePos;
        /// <summary>World rotation per profile node (needed to skin the avatar mesh).</summary>
        public Quat[] NodeRot;
        /// <summary>The avatar's own mesh, skinned onto NodePos/NodeRot when ShowMesh is on.</summary>
        public SkinnedAvatar Model;
        public bool ShowMesh = true, ShowSkeleton = true;
        public IDictionary<string, float> FaceWeights;
        public float MeshScale = 1f;
        public string MeshError;
        readonly MeshRenderer _mesh = new MeshRenderer();
        public double MeshMs => _mesh.LastMs;
        public bool[] NodeIsBone, NodeIsHuman;
        public int[] NodeHuman;
        public readonly List<Marker> Markers = new List<Marker>();
        public string Overlay = "", OverlayRight = "";
        public bool Recording;
        public string Empty = "";

        /// <summary>Kind: 0 axes, 1 camera, 2 point, 3 HMD, 4 controller, 5 tracker, 6 base station (Fov* half-angles in degrees).
        /// Faded = last known pose of a device that isn't tracked right now.</summary>
        public struct Marker { public string Label; public Pose Pose; public Color Color; public int Kind; public bool Faded; public float FovL, FovR, FovT, FovB; /// <summary>Model scale (0 = 1): devices drawn at the tracking scale in replays.</summary>
            public float Size; public bool NoDropLine; }
        public const int KHmd = 3, KController = 4, KTracker = 5, KBaseStation = 6;
        /// <summary>SteamVR play area corners on the floor (live view), or null.</summary>
        public Vec3[] PlayArea;

        Point _last; MouseButtons _btn;
        Vec3 _eye, _r, _u, _f; float _focal, _cx, _cy;

        /// <summary>Draw the headset, controllers and trackers (toggle in the view's corner).</summary>
        public bool ShowDevices = true;
        public event Action ShowDevicesChanged;
        /// <summary>Draw the headset's model among the devices (off by default: it covers the avatar's face). Picking and
        /// framing still use the headset's position.</summary>
        public bool ShowHmd = false;
        /// <summary>The SteamVR play area's outline on the floor (<see cref="PlayArea"/>) and the base stations, each with its own chip.</summary>
        public bool ShowPlayArea = true, ShowBaseStations = true;

        bool MarkerShown(Marker m) => m.Kind < KHmd || (m.Kind == KBaseStation ? ShowBaseStations : ShowDevices && (m.Kind != KHmd || ShowHmd));

        /// <summary>Clickable on/off chips in the view's bottom-left corner (Devices first). Available: false draws the chip
        /// faded (nothing of that kind in this view, e.g. a take recorded without it); it still switches.</summary>
        public sealed class ViewToggle { public string Label; public Func<bool> Get; public Action<bool> Set; public string Tip; public Func<bool> Available; internal RectangleF Rect; }
        public readonly List<ViewToggle> Toggles = new List<ViewToggle>();

        /// <summary>
        /// An interactive tool (posing gizmo, ...): gets the mouse first and draws over the scene. Return true from
        /// MouseDown to take the drag (no orbit / pan).
        /// </summary>
        public interface ITool
        {
            bool MouseDown(Viewport3D v, MouseEventArgs e);
            void MouseMove(Viewport3D v, MouseEventArgs e);
            void MouseUp(Viewport3D v, MouseEventArgs e);
            bool KeyDown(Viewport3D v, Keys key);
            void Draw(Viewport3D v, Graphics g);
        }
        public ITool Tool;
        bool _toolDrag;
        /// <summary>Extra drawing over the scene (after the skeleton and markers).</summary>
        public event Action<Viewport3D, Graphics> PaintOverlay;

        // ------------------------------------------------------------------ draw on demand

        /// <summary>Overlays that change with time (a count-in, a running clock) answer true while they do: the view then
        /// repaints on every tick. Everything else repaints only when what is shown has changed.</summary>
        public event Func<bool> Animating;

        ulong _paintedSig; bool _painted;
        ulong _meshSig; Bitmap _meshBmp;
        /// <summary>Paints since start, and how many of them had to render the avatar mesh again (diagnostics).</summary>
        public int Paints, MeshRenders;

        /// <summary>For timer-driven callers: repaint only when the view would look different from the last paint.</summary>
        public void InvalidateIfChanged()
        {
            if (!_painted || IsAnimating() || FollowPending() || StateSignature() != _paintedSig) Invalidate();
        }

        bool IsAnimating()
        {
            var a = Animating;
            if (a == null) return false;
            foreach (Func<bool> f in a.GetInvocationList()) if (f()) return true;
            return false;
        }

        /// <summary>The follow camera eases toward the hips over several paints: keep painting until it has arrived.</summary>
        bool FollowPending()
        {
            if (!Follow || Profile == null || NodePos == null || !Profile.Has(HumanBones.Hips)) return false;
            var h = NodePos[Profile.Human[HumanBones.Hips]];
            return Math.Abs(h.x - Target.x) > 0.001f || Math.Abs(h.z - Target.z) > 0.001f;
        }

        /// <summary>FNV-1a over everything a paint reads.</summary>
        struct Sig
        {
            public ulong H;
            public static Sig New() => new Sig { H = 14695981039346656037UL };
            public void Add(int v) { H = (H ^ (uint)v) * 1099511628211UL; }
            public void Add(bool v) => Add(v ? 1 : 0);
            public void Add(float v) => Add(BitConverter.SingleToInt32Bits(v));
            public void Add(Vec3 v) { Add(v.x); Add(v.y); Add(v.z); }
            public void Add(Quat q) { Add(q.x); Add(q.y); Add(q.z); Add(q.w); }
            public void Add(string s) => Add(s == null ? 0 : s.GetHashCode());
            public void Ref(object o) => Add(o == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o));
            /// <summary>Millimetres: tracking noise on a resting device does not count as a change.</summary>
            public void AddMm(Vec3 v) { Add((int)Math.Round(v.x * 1000)); Add((int)Math.Round(v.y * 1000)); Add((int)Math.Round(v.z * 1000)); }
            public void AddCoarse(Quat q) { Add((int)Math.Round(q.x * 500)); Add((int)Math.Round(q.y * 500)); Add((int)Math.Round(q.z * 500)); Add((int)Math.Round(q.w * 500)); }
        }

        void AddPose(ref Sig s)
        {
            s.Ref(Profile); s.Ref(Model); s.Add(MeshScale);
            if (NodePos != null) foreach (var p in NodePos) s.Add(p);
            if (NodeRot != null) foreach (var q in NodeRot) s.Add(q);
            if (FaceWeights != null) foreach (var kv in FaceWeights) { s.Add(kv.Key); s.Add(kv.Value); }
            if (Model != null) foreach (var part in Model.Parts) s.Add(part.Visible);
        }

        /// <summary>Everything a paint depends on, the mouse-driven tool state aside (tools invalidate on their own events).</summary>
        ulong StateSignature()
        {
            var s = Sig.New();
            s.Add(Width); s.Add(Height); s.Add(Yaw); s.Add(Pitch); s.Add(Dist); s.Add(Fov); s.Add(Target);
            s.Add(ShowMesh); s.Add(ShowSkeleton); s.Add(ShowDevices); s.Add(ShowHmd); s.Add(ShowPlayArea); s.Add(ShowBaseStations);
            s.Add(Recording); s.Add(_hover); s.Add(Follow);
            s.Add(Overlay); s.Add(OverlayRight); s.Add(Empty); s.Add(MeshError);
            AddPose(ref s);
            s.Add(Markers.Count);
            foreach (var m in Markers)
            {
                s.Add(m.Kind); s.Add(m.Faded); s.Add(m.NoDropLine); s.Add(m.Color.ToArgb()); s.Add(m.Label); s.Add(m.Size);
                s.AddMm(m.Pose.pos); s.AddCoarse(m.Pose.rot); s.Add(m.FovL); s.Add(m.FovR); s.Add(m.FovT); s.Add(m.FovB);
            }
            if (PlayArea != null) foreach (var p in PlayArea) s.AddMm(p);
            s.Add(Figures.Count);
            foreach (var fig in Figures) { s.Ref(fig.Profile); s.Add(fig.Color.ToArgb()); if (fig.NodePos != null) foreach (var p in fig.NodePos) s.Add(p); }
            foreach (var t in Toggles) { s.Add(t.Get()); s.Add(t.Available == null || t.Available()); }
            s.Ref(Tool); s.Add(PoseState.Version);
            return s.H;
        }

        /// <summary>What the rendered avatar mesh depends on: pose, face, visible parts, camera and size.</summary>
        ulong MeshSignature()
        {
            var s = Sig.New();
            s.Add(Width); s.Add(Height); s.Add(_eye); s.Add(_r); s.Add(_u); s.Add(_f); s.Add(_focal);
            AddPose(ref s);
            return s.H;
        }

        /// <summary>More skeletons drawn as lines (other actors of a scene): profile, node positions, colour.</summary>
        public readonly List<(AvatarProfile Profile, Vec3[] NodePos, Color Color)> Figures = new List<(AvatarProfile, Vec3[], Color)>();

        // camera basis of the last paint, for tools
        public Vec3 Eye => _eye;
        public Vec3 CamRight => _r;
        public Vec3 CamUp => _u;
        public Vec3 CamForward => _f;
        public float Focal => _focal;

        /// <summary>Screen position of a world point (false when it is behind the camera).</summary>
        public bool Project(Vec3 p, out PointF s)
        {
            var c = ToCam(p);
            s = PointF.Empty;
            if (c.z < Near) return false;
            s = Proj(c);
            return true;
        }

        /// <summary>World-space ray through a screen point.</summary>
        public void Ray(PointF screen, out Vec3 origin, out Vec3 dir)
        {
            origin = _eye;
            dir = (_f * _focal + _r * (screen.X - _cx) - _u * (screen.Y - _cy)).Normalized;
        }

        public Viewport3D()
        {
            BackColor = Theme.Bg;
            Toggles.Add(new ViewToggle { Label = "Devices", Get = () => ShowDevices, Set = v => { ShowDevices = v; ShowDevicesChanged?.Invoke(); }, Tip = "The headset, controllers and trackers (the headset's model has its own chip)" });
        }

        public void SetProfile(AvatarProfile p)
        {
            Profile = p;
            NodeRot = null;
            if (p == null) { NodePos = null; return; }
            int n = p.Nodes.Count;
            NodePos = new Vec3[n];
            NodeIsBone = new bool[n]; NodeIsHuman = new bool[n]; NodeHuman = Enumerable.Repeat(-1, n).ToArray();
            foreach (int i in p.ArmatureNodes()) NodeIsBone[i] = true;
            for (int b = 0; b < HumanBones.Count; b++) if (p.Human[b] >= 0) { NodeIsHuman[p.Human[b]] = true; NodeHuman[p.Human[b]] = b; }
        }

        public void FrameAll()
        {
            if (Profile != null && NodePos != null && Profile.Has(HumanBones.Hips))
            {
                var h = NodePos[Profile.Human[HumanBones.Hips]];
                float eye = Profile.EyeHeight;
                Target = new Vec3(h.x, Math.Max(0.4f, eye * 0.55f), h.z);
                Dist = Math.Max(1.2f, eye * 2.6f);
            }
            else FrameDevices();
            Invalidate();
        }

        /// <summary>Frame every SteamVR device shown (the headset even with its model off) and the play area.</summary>
        public bool FrameDevices()
        {
            var pts = new List<Vec3>();
            foreach (var m in Markers) if (m.Kind >= KHmd && (MarkerShown(m) || m.Kind == KHmd)) pts.Add(m.Pose.pos);
            if (PlayArea != null && ShowPlayArea) pts.AddRange(PlayArea);
            if (pts.Count == 0) return false;
            Vec3 lo = pts[0], hi = pts[0];
            foreach (var p in pts) { lo = new Vec3(Math.Min(lo.x, p.x), Math.Min(lo.y, p.y), Math.Min(lo.z, p.z)); hi = new Vec3(Math.Max(hi.x, p.x), Math.Max(hi.y, p.y), Math.Max(hi.z, p.z)); }
            var c = (lo + hi) * 0.5f;
            float ext = Math.Max(Math.Max(hi.x - lo.x, hi.z - lo.z), hi.y);
            Target = new Vec3(c.x, Math.Max(0.6f, c.y * 0.8f), c.z);
            Dist = MathX.Clamp(ext * 1.5f + 1.2f, 2.5f, 25f);
            Pitch = Math.Max(Pitch, 20);
            Follow = false;
            Invalidate();
            return true;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Left)
                foreach (var t in Toggles)
                    if (t.Rect.Contains(e.Location)) { t.Set(!t.Get()); Invalidate(); return; }
            if (Tool != null && Tool.MouseDown(this, e)) { _toolDrag = true; Invalidate(); return; }
            _last = e.Location; _btn = e.Button;
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_toolDrag) { _toolDrag = false; Tool?.MouseUp(this, e); Invalidate(); return; }
            _btn = MouseButtons.None;
        }
        bool _hover;
        ViewToggle _tipToggle;
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDoubleClick(MouseEventArgs e) { foreach (var t in Toggles) if (t.Rect.Contains(e.Location)) return; FrameAll(); }
        protected override bool IsInputKey(Keys keyData) => Tool != null || base.IsInputKey(keyData);
        protected override void OnKeyDown(KeyEventArgs e) { if (Tool != null && Tool.KeyDown(this, e.KeyData)) { e.Handled = true; Invalidate(); return; } base.OnKeyDown(e); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_toolDrag || (_btn == MouseButtons.None && Tool != null)) { Tool?.MouseMove(this, e); if (_toolDrag) { Invalidate(); return; } }
            if (_btn == MouseButtons.None)
            {
                var over = Toggles.FirstOrDefault(t => t.Rect.Contains(e.Location));
                Cursor = over != null ? Cursors.Hand : Cursors.Default;
                if (over != _tipToggle) { _tipToggle = over; Theme.Tip(this, over?.Tip); }
                return;
            }
            int dx = e.X - _last.X, dy = e.Y - _last.Y;
            _last = e.Location;
            if (_btn == MouseButtons.Left)
            {
                Yaw += dx * 0.4f;
                Pitch = MathX.Clamp(Pitch + dy * 0.3f, -85, 85);
            }
            else
            {
                float k = Dist * 0.0016f;
                Target = Target - _r * (dx * k) + _u * (dy * k);
                Follow = false;
            }
            Invalidate();
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            Dist = MathX.Clamp(Dist * (float)Math.Pow(0.88, e.Delta / 120.0), 0.3f, 40f);
            Invalidate();
        }

        void SetupCamera()
        {
            float yr = Yaw * MathX.Deg2Rad, pr = Pitch * MathX.Deg2Rad;
            var dir = new Vec3((float)(Math.Sin(yr) * Math.Cos(pr)), (float)Math.Sin(pr), (float)(Math.Cos(yr) * Math.Cos(pr)));
            _eye = Target + dir * Dist;
            _f = (Target - _eye).Normalized;
            _r = Vec3.Cross(Vec3.Up, _f).Normalized;
            _u = Vec3.Cross(_f, _r);
            _cx = Width / 2f; _cy = Height / 2f;
            _focal = (Height / 2f) / (float)Math.Tan(Fov * 0.5f * MathX.Deg2Rad);
        }

        Vec3 ToCam(Vec3 p) { var d = p - _eye; return new Vec3(Vec3.Dot(d, _r), Vec3.Dot(d, _u), Vec3.Dot(d, _f)); }
        PointF Proj(Vec3 c) => new PointF(_cx + c.x / c.z * _focal, _cy - c.y / c.z * _focal);
        const float Near = 0.03f;

        bool Seg(Vec3 a, Vec3 b, out PointF pa, out PointF pb, out float depth)
        {
            var ca = ToCam(a); var cb = ToCam(b);
            depth = (ca.z + cb.z) * 0.5f;
            pa = pb = PointF.Empty;
            if (ca.z < Near && cb.z < Near) return false;
            if (ca.z < Near) ca = Vec3.Lerp(ca, cb, (Near - ca.z) / (cb.z - ca.z));
            if (cb.z < Near) cb = Vec3.Lerp(cb, ca, (Near - cb.z) / (ca.z - cb.z));
            pa = Proj(ca); pb = Proj(cb);
            return true;
        }

        struct Item { public float Depth; public PointF A, B; public Color C; public float W; public int Kind; public PointF[] Poly; } // Kind 0 line, 1 circle, 2 dot, 3 filled polygon
        readonly List<Item> _items = new List<Item>(512);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Follow && Profile != null && NodePos != null && Profile.Has(HumanBones.Hips))
            {
                var h = NodePos[Profile.Human[HumanBones.Hips]];
                Target = Vec3.Lerp(Target, new Vec3(h.x, Target.y, h.z), 0.15f);
            }
            SetupCamera();
            DrawGrid(g);
            DrawPlayArea(g);
            _items.Clear();
            bool meshDrawn = false;
            if (ShowMesh && Model != null && Profile != null && NodePos != null && NodeRot != null && Model.Profile.Nodes.Count == Profile.Nodes.Count && NodeRot.Length == NodePos.Length)
            {
                try
                {
                    // skin and rasterise only when the pose, the face, the camera or the size changed; a repaint for
                    // an overlay, a tool or a device marker reuses the image
                    ulong ms = MeshSignature();
                    if (_meshBmp == null || ms != _meshSig)
                    {
                        Model.Skin(NodePos, NodeRot, FaceWeights, MeshScale);
                        _meshBmp = _mesh.Render(Model, _eye, _r, _u, _f, _focal, _cx, _cy, Width, Height, Near);
                        _meshSig = ms; MeshRenders++;
                    }
                    if (_meshBmp != null) { g.DrawImageUnscaled(_meshBmp, 0, 0); meshDrawn = true; }
                }
                catch (Exception ex) { MeshError = ex.Message; Model = null; _meshBmp = null; AppLog.Error("Mesh preview: " + ex); }
            }
            if (Profile != null && NodePos != null && (!meshDrawn || ShowSkeleton))
            {
                float boneW = meshDrawn ? 0.45f : 1f;
                var exists = Profile.HumanExists;
                // humanoid bones: segment from the nearest humanoid ancestor (skips intermediate transforms)
                for (int hb = 0; hb < HumanBones.Count; hb++)
                {
                    if (!exists[hb] || hb == HumanBones.LeftEye || hb == HumanBones.RightEye || hb == HumanBones.Jaw) continue;
                    int ph = HumanBones.ResolveParent(hb, exists);
                    if (ph < 0) continue;
                    if (!Seg(NodePos[Profile.Human[ph]], NodePos[Profile.Human[hb]], out var a, out var b, out float d)) continue;
                    string n = HumanBones.Names[hb];
                    var c = n.StartsWith("Left") ? Theme.Left : n.StartsWith("Right") ? Theme.Right : Theme.Center;
                    bool finger = HumanBones.IsFinger(hb);
                    float w = boneW * MathX.Clamp((finger ? 0.012f : 0.03f) * _focal / Math.Max(0.1f, d), 1.2f, finger ? 4f : 11f);
                    _items.Add(new Item { Depth = d, A = a, B = b, C = c, W = w });
                }
                // secondary bones (hair, ears, tail, skirt...): thin lines between non-humanoid armature nodes
                for (int i = 0; i < Profile.Nodes.Count; i++)
                {
                    int p = Profile.Nodes[i].Parent;
                    if (p < 0 || NodeIsHuman[i] || !NodeIsBone[i] || !NodeIsBone[p]) continue;
                    if (!NodeIsHuman[p] && Profile.Nodes[p].Parent >= 0 && Profile.Nodes[Profile.Nodes[p].Parent].Parent < 0) continue; // under the armature root
                    if (!Seg(NodePos[p], NodePos[i], out var a, out var b, out float d)) continue;
                    _items.Add(new Item { Depth = d, A = a, B = b, C = Color.FromArgb(90, 150, 156, 170), W = MathX.Clamp(0.008f * _focal / Math.Max(0.1f, d), 1f, 3f) });
                }
                // head
                if (Profile.Has(HumanBones.Head) && !meshDrawn)
                {
                    var hp = NodePos[Profile.Human[HumanBones.Head]];
                    float rad = Math.Max(0.06f, (Profile.EyeHeight - Profile.HumanRestPos(HumanBones.Head).y) * 1.8f);
                    var ce = ToCam(hp + Vec3.Up * rad * 0.7f);
                    if (ce.z > Near) _items.Add(new Item { Depth = ce.z, A = Proj(ce), W = rad * _focal / ce.z, C = Theme.Center, Kind = 1 });
                }
            }
            // other actors (scenes): skeleton lines
            foreach (var fig in Figures)
            {
                if (fig.Profile == null || fig.NodePos == null) continue;
                var ex = fig.Profile.HumanExists;
                for (int hb = 0; hb < HumanBones.Count; hb++)
                {
                    if (!ex[hb] || HumanBones.IsFinger(hb) || hb == HumanBones.LeftEye || hb == HumanBones.RightEye || hb == HumanBones.Jaw) continue;
                    int ph = HumanBones.ResolveParent(hb, ex);
                    if (ph < 0) continue;
                    if (Seg(fig.NodePos[fig.Profile.Human[ph]], fig.NodePos[fig.Profile.Human[hb]], out var a, out var b, out float d))
                        _items.Add(new Item { Depth = d, A = a, B = b, C = fig.Color, W = MathX.Clamp(0.025f * _focal / Math.Max(0.1f, d), 1.2f, 9f) });
                }
            }
            foreach (var m in Markers) if (MarkerShown(m)) AddMarker(m);
            foreach (var it in _items.OrderByDescending(i => i.Depth))
            {
                float fog = MathX.Clamp01((it.Depth - 2f) / 12f);
                var c = Theme.Mix(it.C, Theme.Bg, fog * 0.6f);
                if (it.Kind == 1)
                {
                    using (var p = new Pen(c, Theme.Sf(1.6f))) g.DrawEllipse(p, it.A.X - it.W, it.A.Y - it.W, it.W * 2, it.W * 2);
                    continue;
                }
                if (it.Kind == 2)
                {
                    using (var b = new SolidBrush(c)) g.FillEllipse(b, it.A.X - it.W, it.A.Y - it.W, it.W * 2, it.W * 2);
                    continue;
                }
                if (it.Kind == 3)
                {
                    using (var b = new SolidBrush(c)) g.FillPolygon(b, it.Poly);
                    using (var p = new Pen(Theme.Mix(c, Color.Black, 0.35f), 1f)) g.DrawPolygon(p, it.Poly);
                    continue;
                }
                using (var p = new Pen(c, it.W) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(p, it.A, it.B);
            }
            foreach (var m in Markers) if (MarkerShown(m)) LabelMarker(g, m);
            try { PaintOverlay?.Invoke(this, g); Tool?.Draw(this, g); } catch (Exception ex) { AppLog.Error("Viewport overlay: " + ex.Message); }
            DrawToggles(g);

            if (Profile == null && !string.IsNullOrEmpty(Empty) && !Markers.Any(m => m.Kind >= KHmd))
                TextRenderer.DrawText(g, Empty, Theme.UI, ClientRectangle, Theme.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            if (!string.IsNullOrEmpty(Overlay))
                TextRenderer.DrawText(g, Overlay, Theme.Mono, new Rectangle(Theme.S(10), Theme.S(8), Width - Theme.S(20), Height), Theme.TextDim, TextFormatFlags.Left | TextFormatFlags.Top);
            if (!string.IsNullOrEmpty(OverlayRight))
                TextRenderer.DrawText(g, OverlayRight, Theme.Mono, new Rectangle(Theme.S(10), Theme.S(8), Width - Theme.S(20), Height), Theme.TextDim, TextFormatFlags.Right | TextFormatFlags.Top);
            if (Recording)
                using (var p = new Pen(Color.FromArgb(160, Theme.Rec), Theme.Sf(2))) g.DrawRectangle(p, 1, 1, Width - 3, Height - 3);
            if (_hover || (Profile == null && Markers.Count == 0))
                TextRenderer.DrawText(g, "drag: orbit   right-drag: pan   wheel: zoom   double-click: frame", Theme.Small, new Rectangle(0, 0, Width - Theme.S(10), Height - Theme.S(6)), Theme.TextFaint, TextFormatFlags.Right | TextFormatFlags.Bottom);
            _paintedSig = StateSignature(); _painted = true; Paints++;
        }

        void DrawToggles(Graphics g)
        {
            float x = Theme.S(10), h = Theme.S(22), y = Height - h - Theme.S(8);
            foreach (var t in Toggles)
            {
                bool on = t.Get(), avail = t.Available == null || t.Available();
                string label = (on ? "● " : "○ ") + t.Label;
                var sz = TextRenderer.MeasureText(label, Theme.Small);
                var r = new RectangleF(x, y, sz.Width + Theme.S(14), h);
                if (r.Right > Width - Theme.S(10) && x > Theme.S(10)) { x = Theme.S(10); y -= h + Theme.S(6); r = new RectangleF(x, y, r.Width, h); } // narrow view: another row above
                t.Rect = r;
                if (!avail) on = false; // nothing of the kind here: drawn like off, dimmer; the setting still switches
                Theme.FillRound(g, on ? Color.FromArgb(200, Theme.AccentDim) : Color.FromArgb(avail ? 160 : 90, Theme.Panel2), r, Theme.Sf(5));
                Theme.StrokeRound(g, on ? Theme.Accent : avail ? Theme.Border : Color.FromArgb(110, Theme.Border), r, Theme.Sf(5));
                TextRenderer.DrawText(g, label, Theme.Small, Rectangle.Round(r), on ? Theme.Text : avail ? Theme.TextDim : Theme.TextFaint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                x += r.Width + Theme.S(6);
            }
        }

        static readonly Vec3 Light = new Vec3(0.3f, 0.9f, 0.35f).Normalized;

        /// <summary>Solid shaded box in a device's local frame (Unity axes: +Z forward, +Y up).</summary>
        void AddBox(Pose pose, Vec3 c, Vec3 h, Color col)
        {
            var q = pose.rot; var o = pose.pos;
            int[] us = { -1, 1, 1, -1 }, vs = { -1, -1, 1, 1 };
            var sg = new int[3];
            var cs = new Vec3[4];
            for (int ax = 0; ax < 3; ax++)
                for (int s = -1; s <= 1; s += 2)
                {
                    var n = q * (ax == 0 ? new Vec3(s, 0, 0) : ax == 1 ? new Vec3(0, s, 0) : new Vec3(0, 0, s));
                    for (int k = 0; k < 4; k++)
                    {
                        sg[ax] = s; sg[(ax + 1) % 3] = us[k]; sg[(ax + 2) % 3] = vs[k];
                        cs[k] = o + q * new Vec3(c.x + sg[0] * h.x, c.y + sg[1] * h.y, c.z + sg[2] * h.z);
                    }
                    var fc = (cs[0] + cs[2]) * 0.5f;
                    if (Vec3.Dot(n, fc - _eye) >= 0) continue; // back face
                    var pts = new PointF[4]; float depth = 0; bool ok = true;
                    for (int k = 0; k < 4; k++)
                    {
                        var cc = ToCam(cs[k]);
                        if (cc.z < Near) { ok = false; break; }
                        pts[k] = Proj(cc); depth += cc.z;
                    }
                    if (!ok) continue;
                    float shade = 0.42f + 0.38f * Math.Max(0, Vec3.Dot(n, Light)) + 0.2f * Math.Max(0, -Vec3.Dot(n, _f));
                    _items.Add(new Item { Depth = depth / 4, Poly = pts, Kind = 3, C = Theme.Mix(col, Color.Black, 1 - MathX.Clamp01(shade)) });
                }
        }

        void AddLine(Vec3 a, Vec3 b, Color c, float w)
        {
            if (Seg(a, b, out var pa, out var pb, out float d)) _items.Add(new Item { Depth = d, A = pa, B = pb, C = c, W = w });
        }

        /// <summary>HMD, controllers, trackers and base stations as simple solid models (Brekel-style overview).</summary>
        void AddDevice(Marker m)
        {
            var col = m.Faded ? Theme.Mix(m.Color, Theme.Bg, 0.6f) : m.Color;
            var pose = m.Pose; var p = pose.pos;
            float sz = m.Size > 0 ? m.Size : 1f;
            var f = pose.rot * Vec3.Forward; var u = pose.rot * Vec3.Up; var r = pose.rot * Vec3.Right;
            // drop line to the floor: makes heights readable from any angle
            var floor = new Vec3(p.x, 0, p.z);
            var dropC = Color.FromArgb(m.Faded ? 30 : 60, col);
            if (!m.NoDropLine)
            {
                AddLine(p, floor, dropC, 1f);
                AddLine(floor - Vec3.Right * 0.04f, floor + Vec3.Right * 0.04f, dropC, 1f);
                AddLine(floor - Vec3.Forward * 0.04f, floor + Vec3.Forward * 0.04f, dropC, 1f);
            }
            switch (m.Kind)
            {
                case KHmd:
                    AddBox(pose, new Vec3(0, 0, 0.035f), new Vec3(0.085f, 0.048f, 0.04f), col);
                    AddBox(pose, new Vec3(0, 0, 0.077f), new Vec3(0.07f, 0.036f, 0.004f), Theme.Mix(col, Color.Black, 0.55f)); // visor
                    AddLine(p + f * 0.08f, p + f * 0.3f, Color.FromArgb(110, col), 1.4f); // view direction
                    break;
                case KController:
                    AddBox(pose, new Vec3(0, 0, 0) * sz, new Vec3(0.028f, 0.018f, 0.03f) * sz, col);                       // head / ring
                    AddBox(pose, new Vec3(0, -0.05f, -0.025f) * sz, new Vec3(0.017f, 0.04f, 0.018f) * sz, Theme.Mix(col, Color.Black, 0.25f)); // grip
                    AddLine(p + f * 0.03f * sz, p + f * 0.16f * sz, Color.FromArgb(140, col), 1.4f); // pointing direction
                    break;
                case KTracker:
                    AddBox(pose, Vec3.Zero, new Vec3(0.024f, 0.024f, 0.024f) * sz, col);
                    AddLine(p, p + r * 0.06f * sz, Color.FromArgb(230, 90, 90), 1.6f);
                    AddLine(p, p + u * 0.06f * sz, Color.FromArgb(110, 220, 120), 1.6f);
                    AddLine(p, p + f * 0.06f * sz, Color.FromArgb(100, 150, 255), 1.6f);
                    break;
                case KBaseStation:
                    AddBox(pose, new Vec3(0, 0, -0.02f), new Vec3(0.04f, 0.04f, 0.035f), col);
                    AddBox(pose, new Vec3(0, 0, 0.017f), new Vec3(0.033f, 0.033f, 0.003f), Theme.Mix(col, Color.Black, 0.6f)); // front glass
                    // tracking field of view: rays along the frustum edges
                    float tl = (float)Math.Tan(MathX.Clamp(m.FovL > 0 ? m.FovL : 75, 5, 85) * MathX.Deg2Rad), tr = (float)Math.Tan(MathX.Clamp(m.FovR > 0 ? m.FovR : 75, 5, 85) * MathX.Deg2Rad);
                    float tt = (float)Math.Tan(MathX.Clamp(m.FovT > 0 ? m.FovT : 55, 5, 85) * MathX.Deg2Rad), tb = (float)Math.Tan(MathX.Clamp(m.FovB > 0 ? m.FovB : 55, 5, 85) * MathX.Deg2Rad);
                    var o = p + f * 0.02f;
                    var corners = new[] { f - r * tl + u * tt, f + r * tr + u * tt, f + r * tr - u * tb, f - r * tl - u * tb };
                    var ends = new Vec3[4];
                    var fovC = Color.FromArgb(m.Faded ? 35 : 70, col);
                    for (int k = 0; k < 4; k++) { ends[k] = o + corners[k].Normalized * 1.2f; AddLine(o, ends[k], fovC, 1f); }
                    for (int k = 0; k < 4; k++) AddLine(ends[k], ends[(k + 1) % 4], fovC, 1f);
                    AddLine(o, o + f * 1.5f, Color.FromArgb(m.Faded ? 45 : 90, col), 1f); // optical axis
                    break;
            }
        }

        void AddMarker(Marker m)
        {
            float len = 0.07f;
            var p = m.Pose.pos;
            if (m.Kind >= KHmd) { AddDevice(m); return; }
            if (m.Kind == 2)
            {
                var c = ToCam(p);
                if (c.z > Near) _items.Add(new Item { Depth = c.z, A = Proj(c), W = Math.Max(2.5f, 0.02f * _focal / c.z), C = m.Color, Kind = 2 });
                return;
            }
            if (m.Kind == 1)
            {
                // camera frustum
                var f = m.Pose.rot * Vec3.Forward; var u = m.Pose.rot * Vec3.Up; var r = m.Pose.rot * Vec3.Right;
                float d = 0.18f, w = 0.1f, h = 0.06f;
                var c0 = p + f * d;
                var corners = new[] { c0 + r * w + u * h, c0 - r * w + u * h, c0 - r * w - u * h, c0 + r * w - u * h };
                foreach (var k in corners) if (Seg(p, k, out var a, out var b, out float dd)) _items.Add(new Item { Depth = dd, A = a, B = b, C = m.Color, W = 1.4f });
                for (int i = 0; i < 4; i++) if (Seg(corners[i], corners[(i + 1) % 4], out var a, out var b, out float dd)) _items.Add(new Item { Depth = dd, A = a, B = b, C = m.Color, W = 1.4f });
                if (Seg(c0 + u * h, c0 + u * (h * 1.8f), out var a2, out var b2, out float d2)) _items.Add(new Item { Depth = d2, A = a2, B = b2, C = m.Color, W = 1.4f });
                return;
            }
            var axes = new[] { Tuple.Create(Vec3.Right, Color.FromArgb(230, 90, 90)), Tuple.Create(Vec3.Up, Color.FromArgb(110, 220, 120)), Tuple.Create(Vec3.Forward, Color.FromArgb(100, 150, 255)) };
            foreach (var ax in axes)
                if (Seg(p, p + m.Pose.rot * ax.Item1 * len, out var a, out var b, out float d)) _items.Add(new Item { Depth = d, A = a, B = b, C = ax.Item2, W = 2f });
            var cc = ToCam(p);
            if (cc.z > Near) _items.Add(new Item { Depth = cc.z - 0.001f, A = Proj(cc), W = Math.Max(2.5f, 0.012f * _focal / cc.z), C = m.Color, Kind = 2 });
        }

        void LabelMarker(Graphics g, Marker m)
        {
            if (string.IsNullOrEmpty(m.Label)) return;
            var c = ToCam(m.Pose.pos);
            if (c.z < Near) return;
            var s = Proj(c);
            var col = Theme.Mix(m.Color, Theme.Text, 0.4f);
            if (m.Faded) col = Theme.Mix(col, Theme.Bg, 0.45f);
            int dy = m.Kind >= KHmd ? Theme.S(26) : Theme.S(16);
            TextRenderer.DrawText(g, m.Label, Theme.Small, new Point((int)s.X + Theme.S(8), (int)s.Y - dy), col);
        }

        void DrawPlayArea(Graphics g)
        {
            var pa = PlayArea;
            if (pa == null || pa.Length < 3 || !ShowPlayArea) return;
            var pts = new List<PointF>();
            foreach (var v in pa)
            {
                var c = ToCam(new Vec3(v.x, 0, v.z));
                if (c.z < Near) return;
                pts.Add(Proj(c));
            }
            using (var b = new SolidBrush(Color.FromArgb(22, 80, 200, 220))) g.FillPolygon(b, pts.ToArray());
            using (var p = new Pen(Color.FromArgb(150, 80, 200, 220), Theme.Sf(1.5f))) g.DrawPolygon(p, pts.ToArray());
        }

        void DrawGrid(Graphics g)
        {
            float ext = 6f, step = 0.5f;
            float cx = (float)Math.Round(Target.x / step) * step, cz = (float)Math.Round(Target.z / step) * step;
            using (var pen = new Pen(Color.FromArgb(34, 36, 44), 1f))
            using (var pen2 = new Pen(Color.FromArgb(44, 47, 58), 1f))
            {
                for (float k = -ext; k <= ext + 1e-3f; k += step)
                {
                    bool major = Math.Abs((cx + k) % 2f) < 1e-3f;
                    if (Seg(new Vec3(cx + k, 0, cz - ext), new Vec3(cx + k, 0, cz + ext), out var a, out var b, out _)) g.DrawLine(major ? pen2 : pen, a, b);
                    major = Math.Abs((cz + k) % 2f) < 1e-3f;
                    if (Seg(new Vec3(cx - ext, 0, cz + k), new Vec3(cx + ext, 0, cz + k), out a, out b, out _)) g.DrawLine(major ? pen2 : pen, a, b);
                }
            }
            // origin: the "center on me" point with forward arrow
            using (var px = new Pen(Color.FromArgb(150, 220, 90, 90), 1.5f))
            using (var pz = new Pen(Color.FromArgb(150, 90, 140, 240), 1.5f))
            {
                if (Seg(Vec3.Zero, new Vec3(0.3f, 0, 0), out var a, out var b, out _)) g.DrawLine(px, a, b);
                if (Seg(Vec3.Zero, new Vec3(0, 0, 0.5f), out a, out b, out _)) g.DrawLine(pz, a, b);
                if (Seg(new Vec3(0, 0, 0.5f), new Vec3(-0.06f, 0, 0.42f), out a, out b, out _)) g.DrawLine(pz, a, b);
                if (Seg(new Vec3(0, 0, 0.5f), new Vec3(0.06f, 0, 0.42f), out a, out b, out _)) g.DrawLine(pz, a, b);
            }
        }
    }
}
