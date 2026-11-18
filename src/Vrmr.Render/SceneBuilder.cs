using System;
using System.Numerics;
using System.Text;
using VRMR.Render.Gl;

namespace VRMR.Render
{
    /// <summary>What the view's toggles say right now.</summary>
    struct ViewToggles
    {
        public bool Mesh, Skeleton, Devices, Headset, PlayArea, BaseStations, Hint;

        /// <summary>True when a marker of this kind is drawn. Cameras, points and axes always are.</summary>
        public readonly bool Shows(MarkerKind kind)
        {
            return kind < MarkerKind.Headset || (kind == MarkerKind.BaseStation
                ? BaseStations
                : Devices && (kind != MarkerKind.Headset || Headset));
        }
    }

    /// <summary>
    ///     Turns a <see cref="ViewFrame" /> into what the renderer draws: the floor grid and play area (under the meshes),
    ///     then bones, figures and device models sorted far to near with a depth fade, then labels and the corner texts.
    ///     Everything but the avatar meshes is projected here and drawn as 2D shapes, so it looks the same at any distance
    ///     and stays visible through the mesh, as in the previous view. Reused every frame; a steady frame allocates nothing.
    /// </summary>
    sealed class SceneBuilder
    {
        /// <summary>
        ///     Line widths the previous view gave in raw pixels were tuned on a 150 % display: this turns them into
        ///     device-independent pixels.
        /// </summary>
        const float Px = 1f / 1.5f;

        const byte Line = 0, Circle = 1, Dot = 2, Quad = 3;

        static readonly Vec3 BoxLight = new Vec3(0.3f, 0.9f, 0.35f).Normalized;
        readonly Vector2[] _poly = new Vector2[16];
        ViewCamera _cam;
        char[] _chars = new char[256];
        int _count;

        Item[] _items = new Item[1024];
        double[] _keys = new double[1024];
        int[] _order = new int[1024];
        ViewportTheme _theme;

        /// <summary>
        ///     The scene itself. Afterwards the caller may add tool overlays to the batch, then calls
        ///     <see cref="BuildChrome" />.
        /// </summary>
        public void BuildScene(SceneDraw draw, ViewFrame frame, ViewCamera cam, ViewportTheme theme,
            in ViewToggles toggles)
        {
            _cam = cam;
            _theme = theme;
            var batch = draw.Batch;
            draw.Camera = cam;
            draw.Background = theme.Background;
            draw.Untextured = theme.MeshUntextured;
            draw.Meshes.Clear();
            Grid(batch);
            if (toggles.PlayArea) PlayArea(batch, frame);
            draw.UnderMeshVertices = batch.Count;

            _count = 0;
            for (int i = 0; i < frame.Actors.Count; i++)
            {
                var a = frame.Actors[i];
                if (a.Style == ActorStyle.Figure)
                {
                    Figure(a);
                    continue;
                }

                bool mesh = toggles.Mesh && a.MeshUsable;
                if (mesh) draw.Meshes.Add(a);
                if (!mesh || toggles.Skeleton) Skeleton(a, mesh);
            }

            for (int i = 0; i < frame.Markers.Count; i++)
                if (toggles.Shows(frame.Markers[i].Kind))
                    AddMarker(frame.Markers[i]);
            EmitSorted(batch);

            for (int i = 0; i < frame.Markers.Count; i++)
                if (toggles.Shows(frame.Markers[i].Kind))
                    Label(draw, frame.Markers[i]);
        }

        /// <summary>
        ///     The texts and frames that sit on top of everything: the empty message, the corner texts, the recording frame,
        ///     the hint.
        /// </summary>
        public void BuildChrome(SceneDraw draw, ViewFrame frame, ViewCamera cam, ViewportTheme theme,
            in ViewToggles toggles, bool pointerOver)
        {
            var batch = draw.Batch;
            var atlas = draw.Atlas;
            float w = cam.Width, h = cam.Height;
            bool noActor = frame.Actors.Count == 0;
            if (noActor && !string.IsNullOrEmpty(frame.EmptyText) && !frame.HasDevice())
            {
                var size = atlas.Measure(frame.EmptyText, theme.EmptySize, batch.Scale);
                atlas.Draw(batch, frame.EmptyText, new Vector2(w * 0.5f, (h - size.Y) * 0.5f), theme.EmptySize,
                    theme.TextDim, TextAlign.Center);
            }

            if (frame.Overlay.Length > 0)
                atlas.Draw(batch, Chars(frame.Overlay), new Vector2(10, 8), theme.OverlaySize, theme.TextDim,
                    TextAlign.Left, true);
            if (frame.OverlayRight.Length > 0)
                atlas.Draw(batch, Chars(frame.OverlayRight), new Vector2(w - 10, 8), theme.OverlaySize, theme.TextDim,
                    TextAlign.Right, true);
            if (frame.Recording)
                batch.RoundRect(new Vector2(2, 2), new Vector2(w - 2, h - 2), 0, Rgba.Transparent,
                    theme.Record.WithAlpha(160), 2f);
            if (toggles.Hint && !string.IsNullOrEmpty(theme.HintText) &&
                (pointerOver || (noActor && frame.Markers.Count == 0)))
            {
                var size = atlas.Measure(theme.HintText, theme.LabelSize, batch.Scale);
                atlas.Draw(batch, theme.HintText, new Vector2(w - 10, h - 6 - size.Y), theme.LabelSize, theme.TextFaint,
                    TextAlign.Right);
            }
        }

        ReadOnlySpan<char> Chars(StringBuilder text)
        {
            if (_chars.Length < text.Length) _chars = new char[Math.Max(text.Length, _chars.Length * 2)];
            text.CopyTo(0, _chars, 0, text.Length);
            return _chars.AsSpan(0, text.Length);
        }

        // ---------------------------------------------------------------- floor

        void Grid(VectorBatch batch)
        {
            const float ext = 6f, step = 0.5f;
            var target = _cam.Target;
            float cx = MathF.Round(target.x / step) * step, cz = MathF.Round(target.z / step) * step;
            for (float k = -ext; k <= ext + 1e-3f; k += step)
            {
                bool major = MathF.Abs(MathF.IEEERemainder(cx + k, 2f)) < 1e-3f;
                FloorLine(batch, new Vec3(cx + k, 0, cz - ext), new Vec3(cx + k, 0, cz + ext),
                    major ? _theme.GridMajor : _theme.GridMinor, Px);
                major = MathF.Abs(MathF.IEEERemainder(cz + k, 2f)) < 1e-3f;
                FloorLine(batch, new Vec3(cx - ext, 0, cz + k), new Vec3(cx + ext, 0, cz + k),
                    major ? _theme.GridMajor : _theme.GridMinor, Px);
            }

            // the origin, with an arrow along +Z (forward)
            FloorLine(batch, Vec3.Zero, new Vec3(0.3f, 0, 0), _theme.OriginX, 1.5f * Px);
            FloorLine(batch, Vec3.Zero, new Vec3(0, 0, 0.5f), _theme.OriginZ, 1.5f * Px);
            FloorLine(batch, new Vec3(0, 0, 0.5f), new Vec3(-0.06f, 0, 0.42f), _theme.OriginZ, 1.5f * Px);
            FloorLine(batch, new Vec3(0, 0, 0.5f), new Vec3(0.06f, 0, 0.42f), _theme.OriginZ, 1.5f * Px);
        }

        void FloorLine(VectorBatch batch, Vec3 a, Vec3 b, Rgba color, float width)
        {
            if (_cam.Segment(a, b, out var pa, out var pb, out _)) batch.Line(pa, pb, color, width);
        }

        void PlayArea(VectorBatch batch, ViewFrame frame)
        {
            int n = frame.PlayArea.Count;
            if (n < 3 || n > _poly.Length) return;
            for (int i = 0; i < n; i++)
            {
                var v = frame.PlayArea[i];
                if (!_cam.Project(new Vec3(v.x, 0, v.z), out _poly[i]))
                    return; // a corner behind the camera: no outline
            }

            batch.Polygon(_poly.AsSpan(0, n), _theme.PlayAreaFill);
            for (int i = 0; i < n; i++) batch.Line(_poly[i], _poly[(i + 1) % n], _theme.PlayAreaLine, 1.5f);
        }

        // ---------------------------------------------------------------- sorted items

        ref Item Add(byte kind, float depth, Rgba color, float width)
        {
            if (_count == _items.Length)
            {
                Array.Resize(ref _items, _count * 2);
                Array.Resize(ref _keys, _count * 2);
                Array.Resize(ref _order, _count * 2);
            }

            ref var it = ref _items[_count++];
            it.Kind = kind;
            it.Depth = depth;
            it.Color = color;
            it.Width = width;
            return ref it;
        }

        void AddLine(Vec3 a, Vec3 b, Rgba color, float width)
        {
            if (!_cam.Segment(a, b, out var pa, out var pb, out float depth)) return;
            ref var it = ref Add(Line, depth, color, width);
            it.A = pa;
            it.B = pb;
        }

        void AddDot(Vec3 at, float depthBias, float minRadius, float worldRadius, Rgba color)
        {
            var c = _cam.ToCamera(at);
            if (c.z <= ViewCamera.Near) return;
            ref var it = ref Add(Dot, c.z + depthBias, color, Math.Max(minRadius, worldRadius * _cam.Focal / c.z));
            it.A = _cam.FromCamera(c);
        }

        void EmitSorted(VectorBatch batch)
        {
            // far to near; equal depths keep the order they were added in
            for (int i = 0; i < _count; i++)
            {
                _keys[i] = -(double)_items[i].Depth + i * 1e-12;
                _order[i] = i;
            }

            Array.Sort(_keys, _order, 0, _count);
            var bg = _theme.Background;
            for (int k = 0; k < _count; k++)
            {
                ref var it = ref _items[_order[k]];
                float fog = MathX.Clamp01((it.Depth - 2f) / 12f);
                var c = Rgba.Mix(it.Color, bg, fog * 0.6f);
                switch (it.Kind)
                {
                    case Circle: batch.Ring(it.A, it.Width, c, 1.6f); break;
                    case Dot: batch.Disc(it.A, it.Width, c); break;
                    case Quad:
                        _poly[0] = it.A;
                        _poly[1] = it.B;
                        _poly[2] = it.C;
                        _poly[3] = it.D;
                        batch.Polygon(_poly.AsSpan(0, 4), c);
                        var edge = Rgba.Mix(c, Rgba.Black, 0.35f);
                        batch.Line(it.A, it.B, edge, Px);
                        batch.Line(it.B, it.C, edge, Px);
                        batch.Line(it.C, it.D, edge, Px);
                        batch.Line(it.D, it.A, edge, Px);
                        break;
                    default: batch.Line(it.A, it.B, c, it.Width); break;
                }
            }
        }

        // ---------------------------------------------------------------- skeletons

        void Skeleton(ActorView a, bool overMesh)
        {
            var layout = SkeletonLayout.For(a.Profile);
            var pos = a.NodePos;
            float thin = overMesh ? 0.45f : 1f, focal = _cam.Focal;
            foreach (var s in layout.Human)
            {
                if (!_cam.Segment(pos[s.From], pos[s.To], out var pa, out var pb, out float d)) continue;
                var color = s.Side == BoneSide.Left ? _theme.BoneLeft :
                    s.Side == BoneSide.Right ? _theme.BoneRight : _theme.BoneCenter;
                float w = thin * MathX.Clamp((s.Finger ? 0.012f : 0.03f) * focal / Math.Max(0.1f, d), 1.2f * Px,
                    (s.Finger ? 4f : 11f) * Px);
                ref var it = ref Add(Line, d, color, w);
                it.A = pa;
                it.B = pb;
            }

            foreach (var s in layout.Secondary)
            {
                if (!_cam.Segment(pos[s.From], pos[s.To], out var pa, out var pb, out float d)) continue;
                ref var it = ref Add(Line, d, _theme.BoneSecondary,
                    MathX.Clamp(0.008f * focal / Math.Max(0.1f, d), Px, 3f * Px));
                it.A = pa;
                it.B = pb;
            }

            if (layout.Head >= 0 && !overMesh)
            {
                var c = _cam.ToCamera(pos[layout.Head] + Vec3.Up * layout.HeadRadius * 0.7f);
                if (c.z > ViewCamera.Near)
                {
                    ref var it = ref Add(Circle, c.z, _theme.BoneCenter, layout.HeadRadius * focal / c.z);
                    it.A = _cam.FromCamera(c);
                }
            }
        }

        void Figure(ActorView a)
        {
            var layout = SkeletonLayout.For(a.Profile);
            var pos = a.NodePos;
            float focal = _cam.Focal;
            foreach (var s in layout.Human)
            {
                if (s.Finger || !_cam.Segment(pos[s.From], pos[s.To], out var pa, out var pb, out float d)) continue;
                ref var it = ref Add(Line, d, a.FigureColor,
                    MathX.Clamp(0.025f * focal / Math.Max(0.1f, d), 1.2f * Px, 9f * Px));
                it.A = pa;
                it.B = pb;
            }
        }

        // ---------------------------------------------------------------- markers

        void AddMarker(in Marker m)
        {
            var p = m.Pose.pos;
            if (m.IsDevice)
            {
                AddDevice(m);
                return;
            }

            if (m.Kind == MarkerKind.Point)
            {
                AddDot(p, 0, 2.5f * Px, 0.02f, m.Color);
                return;
            }

            if (m.Kind == MarkerKind.Camera)
            {
                // a small frustum with a tick on its top edge
                Vec3 f = m.Pose.rot * Vec3.Forward, u = m.Pose.rot * Vec3.Up, r = m.Pose.rot * Vec3.Right;
                const float d = 0.18f, w = 0.1f, h = 0.06f;
                var c0 = p + f * d;
                Vec3 k0 = c0 + r * w + u * h, k1 = c0 - r * w + u * h, k2 = c0 - r * w - u * h, k3 = c0 + r * w - u * h;
                float lw = 1.4f * Px;
                AddLine(p, k0, m.Color, lw);
                AddLine(p, k1, m.Color, lw);
                AddLine(p, k2, m.Color, lw);
                AddLine(p, k3, m.Color, lw);
                AddLine(k0, k1, m.Color, lw);
                AddLine(k1, k2, m.Color, lw);
                AddLine(k2, k3, m.Color, lw);
                AddLine(k3, k0, m.Color, lw);
                AddLine(c0 + u * h, c0 + u * (h * 1.8f), m.Color, lw);
                return;
            }

            const float len = 0.07f;
            AddLine(p, p + m.Pose.rot * Vec3.Right * len, _theme.AxisX, 2f * Px);
            AddLine(p, p + m.Pose.rot * Vec3.Up * len, _theme.AxisY, 2f * Px);
            AddLine(p, p + m.Pose.rot * Vec3.Forward * len, _theme.AxisZ, 2f * Px);
            AddDot(p, -0.001f, 2.5f * Px, 0.012f, m.Color);
        }

        /// <summary>Headset, controllers, trackers and base stations as simple solid models, each with a line down to the floor.</summary>
        void AddDevice(in Marker m)
        {
            var bg = _theme.Background;
            var col = m.Faded ? Rgba.Mix(m.Color, bg, 0.6f) : m.Color;
            var pose = m.Pose;
            var p = pose.pos;
            float sz = m.Size > 0 ? m.Size : 1f;
            Vec3 f = pose.rot * Vec3.Forward, u = pose.rot * Vec3.Up, r = pose.rot * Vec3.Right;
            if (!m.NoDropLine)
            {
                // makes heights readable from any angle
                var floor = new Vec3(p.x, 0, p.z);
                var drop = col.WithAlpha((byte)(m.Faded ? 30 : 60));
                AddLine(p, floor, drop, Px);
                AddLine(floor - Vec3.Right * 0.04f, floor + Vec3.Right * 0.04f, drop, Px);
                AddLine(floor - Vec3.Forward * 0.04f, floor + Vec3.Forward * 0.04f, drop, Px);
            }

            switch (m.Kind)
            {
                case MarkerKind.Headset:
                    AddBox(pose, new Vec3(0, 0, 0.035f), new Vec3(0.085f, 0.048f, 0.04f), col);
                    AddBox(pose, new Vec3(0, 0, 0.077f), new Vec3(0.07f, 0.036f, 0.004f),
                        Rgba.Mix(col, Rgba.Black, 0.55f)); // visor
                    AddLine(p + f * 0.08f, p + f * 0.3f, col.WithAlpha(110), 1.4f * Px); // view direction
                    break;
                case MarkerKind.Controller:
                    AddBox(pose, Vec3.Zero, new Vec3(0.028f, 0.018f, 0.03f) * sz, col); // head / ring
                    AddBox(pose, new Vec3(0, -0.05f, -0.025f) * sz, new Vec3(0.017f, 0.04f, 0.018f) * sz,
                        Rgba.Mix(col, Rgba.Black, 0.25f)); // grip
                    AddLine(p + f * 0.03f * sz, p + f * 0.16f * sz, col.WithAlpha(140),
                        1.4f * Px); // pointing direction
                    break;
                case MarkerKind.Tracker:
                    AddBox(pose, Vec3.Zero, new Vec3(0.024f, 0.024f, 0.024f) * sz, col);
                    AddLine(p, p + r * 0.06f * sz, _theme.AxisX, 1.6f * Px);
                    AddLine(p, p + u * 0.06f * sz, _theme.AxisY, 1.6f * Px);
                    AddLine(p, p + f * 0.06f * sz, _theme.AxisZ, 1.6f * Px);
                    break;
                case MarkerKind.BaseStation:
                    AddBox(pose, new Vec3(0, 0, -0.02f), new Vec3(0.04f, 0.04f, 0.035f), col);
                    AddBox(pose, new Vec3(0, 0, 0.017f), new Vec3(0.033f, 0.033f, 0.003f),
                        Rgba.Mix(col, Rgba.Black, 0.6f)); // front glass
                    // the tracking field of view: rays along the edges of the frustum
                    float tl = FovTan(m.FovLeft, 75),
                        tr = FovTan(m.FovRight, 75),
                        tt = FovTan(m.FovTop, 55),
                        tb = FovTan(m.FovBottom, 55);
                    var o = p + f * 0.02f;
                    var fov = col.WithAlpha((byte)(m.Faded ? 35 : 70));
                    Vec3 e0 = o + (f - r * tl + u * tt).Normalized * 1.2f,
                        e1 = o + (f + r * tr + u * tt).Normalized * 1.2f;
                    Vec3 e2 = o + (f + r * tr - u * tb).Normalized * 1.2f,
                        e3 = o + (f - r * tl - u * tb).Normalized * 1.2f;
                    AddLine(o, e0, fov, Px);
                    AddLine(o, e1, fov, Px);
                    AddLine(o, e2, fov, Px);
                    AddLine(o, e3, fov, Px);
                    AddLine(e0, e1, fov, Px);
                    AddLine(e1, e2, fov, Px);
                    AddLine(e2, e3, fov, Px);
                    AddLine(e3, e0, fov, Px);
                    AddLine(o, o + f * 1.5f, col.WithAlpha((byte)(m.Faded ? 45 : 90)), Px); // optical axis
                    break;
            }
        }

        static float FovTan(float degrees, float fallback)
        {
            return MathF.Tan(MathX.Clamp(degrees > 0 ? degrees : fallback, 5, 85) * MathX.Deg2Rad);
        }

        /// <summary>A solid shaded box in a device's own frame (+Z forward, +Y up): its faces that look at the camera.</summary>
        void AddBox(Pose pose, Vec3 centre, Vec3 half, Rgba color)
        {
            var q = pose.rot;
            var o = pose.pos;
            Vec3 eye = _cam.Eye, forward = _cam.Forward;
            Span<Vec3> corner = stackalloc Vec3[4];
            Span<float> sign = stackalloc float[3];
            for (int axis = 0; axis < 3; axis++)
            for (int s = -1; s <= 1; s += 2)
            {
                var n = q * (axis == 0 ? new Vec3(s, 0, 0) : axis == 1 ? new Vec3(0, s, 0) : new Vec3(0, 0, s));
                for (int k = 0; k < 4; k++)
                {
                    sign[axis] = s;
                    sign[(axis + 1) % 3] = k == 0 || k == 3 ? -1 : 1;
                    sign[(axis + 2) % 3] = k < 2 ? -1 : 1;
                    corner[k] = o + q * new Vec3(centre.x + sign[0] * half.x, centre.y + sign[1] * half.y,
                        centre.z + sign[2] * half.z);
                }

                var faceCentre = (corner[0] + corner[2]) * 0.5f;
                if (Vec3.Dot(n, faceCentre - eye) >= 0) continue; // faces away
                Vec3 c0 = _cam.ToCamera(corner[0]),
                    c1 = _cam.ToCamera(corner[1]),
                    c2 = _cam.ToCamera(corner[2]),
                    c3 = _cam.ToCamera(corner[3]);
                if (c0.z < ViewCamera.Near || c1.z < ViewCamera.Near || c2.z < ViewCamera.Near ||
                    c3.z < ViewCamera.Near) continue;
                float shade = 0.42f + 0.38f * Math.Max(0, Vec3.Dot(n, BoxLight)) +
                              0.2f * Math.Max(0, -Vec3.Dot(n, forward));
                ref var it = ref Add(Quad, (c0.z + c1.z + c2.z + c3.z) * 0.25f,
                    Rgba.Mix(color, Rgba.Black, 1 - MathX.Clamp01(shade)), 0);
                it.A = _cam.FromCamera(c0);
                it.B = _cam.FromCamera(c1);
                it.C = _cam.FromCamera(c2);
                it.D = _cam.FromCamera(c3);
            }
        }

        void Label(SceneDraw draw, in Marker m)
        {
            if (string.IsNullOrEmpty(m.Label) || !_cam.Project(m.Pose.pos, out var s)) return;
            var color = Rgba.Mix(m.Color, _theme.Text, 0.4f);
            if (m.Faded) color = Rgba.Mix(color, _theme.Background, 0.45f);
            draw.Atlas.Draw(draw.Batch, m.Label,
                new Vector2(MathF.Floor(s.X) + 8, MathF.Floor(s.Y) - (m.IsDevice ? 26 : 16)), _theme.LabelSize, color);
        }

        struct Item
        {
            public float Depth, Width;
            public Vector2 A, B, C, D;
            public Rgba Color;
            public byte Kind;
        }
    }
}
