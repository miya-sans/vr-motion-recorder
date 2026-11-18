using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace VRMR.UI
{
    public sealed partial class MainForm
    {
        // ---------------------------------------------------------------- the placement

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static readonly int[] FeetBones =
            { HumanBones.LeftFoot, HumanBones.RightFoot, HumanBones.LeftToes, HumanBones.RightToes };

        DarkButton _gizmoBtn, _gizMoveBtn, _gizTurnBtn;
        TextBox _plX, _plY, _plZ, _plRX, _plRY, _plRZ;
        InfoLabel _placeInfo;

        PlacementTool _placeTool;

        // The take's placement: the solved take moved / turned as a whole (Edits.Placement, applied after the solve by
        // PoseBuilder.Place, so the solve itself doesn't change). The transport's Origin puts the avatar on the origin at the
        // playhead and its turn buttons turn the take 90° about the hips; Clip tab > Placement types exact values or drags the
        // placement gizmo in the 3D view. While the take re-solves with a new placement, the view shows the last solve moved
        // by the difference, so dragging is live.
        FoldSection _secPlace;

        void BuildPlacementSection(StackPanel p)
        {
            var s = _secPlace = p.Fold("Placement", true, "clip.place");
            Tip(s,
                "Moves and turns the whole take (body, VRChat camera, devices) after the solve: the solve itself doesn't change. Used by the 3D view and every export. Scenes place their strips themselves and don't use it.");
            s.Add(new InfoLabel(
                    "Origin and the two turn buttons under the 3D view put the avatar on the origin at the playhead and turn it 90°. Exact values and a gizmo:")
                { Color = Theme.TextDim });

            RowPanel Boxes(out TextBox a, out TextBox b, out TextBox c)
            {
                var r = new RowPanel { Height = Theme.S(24) };
                foreach (var t in new[] { a = Box("0"), b = Box("0"), c = Box("0") })
                {
                    r.Controls.Add(t);
                    t.Leave += (o, e) => PlacementFromBoxes();
                    t.KeyDown += (o, e) =>
                    {
                        if (e.KeyCode == Keys.Enter)
                        {
                            PlacementFromBoxes();
                            e.SuppressKeyPress = true;
                        }
                    };
                }

                return r;
            }

            s.Field("Move cm", Boxes(out _plX, out _plY, out _plZ),
                "X, Y, Z: where the take is moved to, in centimetres (Unity axes: Y up, Z forward)");
            s.Field("Turn °", Boxes(out _plRX, out _plRY, out _plRZ),
                "X, Y, Z: how the take is turned, in degrees (Unity Euler angles: X pitch, Y heading, Z roll)");
            _gizmoBtn = Btn("Gizmo", () => { },
                tip: "A move / turn gizmo on the avatar in the 3D view (W move, E turn, Shift slow, Esc off)");
            _gizmoBtn.Toggle = true;
            _gizmoBtn.CheckedChanged += (o, e) => SetPlacementGizmo(_gizmoBtn.Checked);
            _gizMoveBtn = Btn("Move", () => SetGizmoTurn(false), tip: "Gizmo: move (W)");
            _gizMoveBtn.Toggle = true;
            _gizMoveBtn.Checked = true;
            _gizTurnBtn = Btn("Turn", () => SetGizmoTurn(true), tip: "Gizmo: turn (E)");
            _gizTurnBtn.Toggle = true;
            s.Row(_gizmoBtn, _gizMoveBtn, _gizTurnBtn,
                Btn("Reset", () => SetPlacement(Pose.Identity),
                    tip: "Back to the take as solved (also undoes Origin and the turn buttons)"));
            _placeInfo = s.Add(new InfoLabel() { Color = Theme.TextFaint });
        }

        /// <summary>Sets the take's placement, shows it right away (the re-solve follows).</summary>
        void SetPlacement(Pose P, bool rebake = true)
        {
            if (_edit == null) return;
            _edit.Edits.Placement = P;
            SyncPlacementUI();
            if (rebake) MarkDirty(true);
            else
            {
                _dirty = true;
                _lib.Invalidate();
            }

            ShowFrameAt(Playhead);
            _view.Invalidate();
        }

        /// <summary>What the view adds to the last solve: the current placement relative to the one it was solved with.</summary>
        Pose PlacementDelta()
        {
            if (_edit == null || _baked == null) return Pose.Identity;
            return _edit.Edits.Placement * _baked.Placement.Inverse;
        }

        static bool IsIdentity(Pose p)
        {
            return p.pos.SqrLength < 1e-12f && Math.Abs(Math.Abs(p.rot.w) - 1) < 1e-7f;
        }

        /// <summary>ShowFrameAt's last step: the frame moved by a placement that isn't solved in yet.</summary>
        void ApplyPlacementPreview()
        {
            var D = PlacementDelta();
            if (IsIdentity(D) || _view.NodePos == null) return;
            for (int i = 0; i < _view.NodePos.Length; i++) _view.NodePos[i] = D.TransformPoint(_view.NodePos[i]);
            if (_view.NodeRot != null)
                for (int i = 0; i < _view.NodeRot.Length; i++)
                    _view.NodeRot[i] = (D.rot * _view.NodeRot[i]).Normalized;
            for (int i = 0; i < _view.Markers.Count; i++)
            {
                var m = _view.Markers[i];
                m.Pose = D * m.Pose;
                _view.Markers[i] = m;
            }

            var pa = _view.PlayArea; // the take's own (ShowRoom made it for this frame)
            if (pa != null)
                for (int i = 0; i < pa.Length; i++)
                    pa[i] = D.TransformPoint(pa[i]);
        }

        /// <summary>The avatar as the view shows it at the playhead (placement included): node world positions / rotations.</summary>
        bool ShownPose(out Vec3[] wp, out Quat[] wr, out int f)
        {
            wp = null;
            wr = null;
            f = 0;
            var c = _baked;
            var r = _edit;
            if (c == null || r == null || c.BodySource == "none") return false;
            f = c.FrameAt(r.Edits.SourceToEdited(Playhead, r.Duration));
            int n = c.Profile.Nodes.Count;
            wp = new Vec3[n];
            wr = new Quat[n];
            c.EvaluateNodes(f, wp, wr);
            var D = PlacementDelta();
            for (int i = 0; i < n; i++)
            {
                wp[i] = D.TransformPoint(wp[i]);
                wr[i] = (D.rot * wr[i]).Normalized;
            }

            return true;
        }

        /// <summary>
        ///     Transport "Origin": the avatar at the playhead onto the origin: hips at X = Z = 0, the lowest foot at its
        ///     rest height above the floor, facing +Z (the hips' heading). Replaces the take's placement turn; ↺ ↻ turn from
        ///     there.
        /// </summary>
        void OriginHere()
        {
            if (!ShownPose(out var wp, out var wr, out int f))
            {
                _placeInfo.Text = "Select a solved take first.";
                return;
            }

            var prof = _baked.Profile;
            int h = prof.Human[HumanBones.Hips];
            var piv = wp[h];
            float yaw = (wr[h] * prof.HumanRestRot(HumanBones.Hips).Conjugate).Normalized.Yaw;
            var R = Quat.YawRotation(-yaw);
            float k = _baked.ScaleAt(f) / Math.Max(1e-6f, prof.Scale), lift = float.MaxValue;
            foreach (int b in FeetBones)
                if (prof.Has(b))
                    lift = Math.Min(lift, wp[prof.Human[b]].y - prof.HumanRestPos(b).y * k);
            if (lift == float.MaxValue) lift = 0;
            // turn about the hips (a turn about the vertical leaves the heights alone), then the hips onto the origin
            var d = new Vec3(-piv.x, -lift, -piv.z);
            var P = _edit.Edits.Placement;
            SetPlacement(new Pose(piv + R * (P.pos - piv) + d, (R * P.rot).Normalized));
            _placeInfo.Text =
                $"Origin at {Theme.Time(Playhead)}: moved {d.x * 100:+0.0;-0.0;0} / {d.y * 100:+0.0;-0.0;0} / {d.z * 100:+0.0;-0.0;0} cm, turned {-yaw:+0.0;-0.0;0}° to face +Z";
        }

        /// <summary>Transport ↺ ↻: the take turned by deg (Unity: + = clockwise seen from above) about the hips at the playhead.</summary>
        void TurnTake(float deg)
        {
            if (!ShownPose(out var wp, out _, out _))
            {
                _placeInfo.Text = "Select a solved take first.";
                return;
            }

            var piv = wp[_baked.Profile.Human[HumanBones.Hips]];
            var R = Quat.YawRotation(deg);
            var P = _edit.Edits.Placement;
            SetPlacement(new Pose(piv + R * (P.pos - piv), (R * P.rot).Normalized));
            _placeInfo.Text = $"Turned {deg:+0;-0}° about the hips at {Theme.Time(Playhead)}";
        }

        void PlacementFromBoxes()
        {
            if (_edit == null || _syncing) return;
            if (!TryF(_plX.Text, out float x) || !TryF(_plY.Text, out float y) || !TryF(_plZ.Text, out float z) ||
                !TryF(_plRX.Text, out float rx) || !TryF(_plRY.Text, out float ry) || !TryF(_plRZ.Text, out float rz))
            {
                SyncPlacementUI();
                return;
            }

            var P = new Pose(new Vec3(x, y, z) * 0.01f, Quat.EulerUnity(rx, ry, rz));
            var cur = _edit.Edits.Placement;
            if ((P.pos - cur.pos).Length < 1e-5f && Quat.Angle(P.rot, cur.rot) < 1e-3f) return;
            SetPlacement(P);
        }

        void SyncPlacementUI()
        {
            if (_plX == null) return;
            bool was = _syncing;
            _syncing = true;
            try
            {
                var P = _edit?.Edits.Placement ?? Pose.Identity;
                var e = P.rot.ToEulerUnity();

                float W(float a)
                {
                    a %= 360;
                    if (a > 180) a -= 360;
                    if (a <= -180) a += 360;
                    return Math.Abs(a) < 0.005f ? 0 : a;
                }

                void Set(TextBox b, float v, string fmt)
                {
                    if (!b.Focused) b.Text = v.ToString(fmt, Inv);
                }

                Set(_plX, P.pos.x * 100, "0.0");
                Set(_plY, P.pos.y * 100, "0.0");
                Set(_plZ, P.pos.z * 100, "0.0");
                Set(_plRX, W(e.x), "0.0");
                Set(_plRY, W(e.y), "0.0");
                Set(_plRZ, W(e.z), "0.0");
                foreach (var b in new[] { _plX, _plY, _plZ, _plRX, _plRY, _plRZ }) b.Enabled = _edit != null;
                bool has = _edit != null && _edit.Edits.HasPlacement;
                _secPlace.Summary =
                    has
                        ? $"moved {P.pos.Length * 100:0} cm, turned {Quat.Angle(Quat.Identity, P.rot):0}°"
                        : "as solved";
                _secPlace.SummaryColor = has ? Theme.Accent : Theme.TextFaint;
            }
            finally { _syncing = was; }
        }

        // ---------------------------------------------------------------- the gizmo

        void SetPlacementGizmo(bool on)
        {
            if (_gizmoBtn.Checked != on)
            {
                _gizmoBtn.Checked = on;
                _gizmoBtn.Invalidate();
            }

            if (_placeTool == null) _placeTool = new PlacementTool(this);
            if (_sceneMode) return; // the scene editor owns the view's tool; back in Takes mode the gizmo returns
            _view.Tool = on ? _placeTool : _view.Tool == _placeTool ? null : _view.Tool;
            _view.Invalidate();
        }

        void SetGizmoTurn(bool turn)
        {
            if (_placeTool == null) _placeTool = new PlacementTool(this);
            _placeTool.Turn = turn;
            _gizMoveBtn.Checked = !turn;
            _gizTurnBtn.Checked = turn;
            _gizMoveBtn.Invalidate();
            _gizTurnBtn.Invalidate();
            if (!_gizmoBtn.Checked) SetPlacementGizmo(true);
            _view.Invalidate();
        }

        /// <summary>Move / turn the take in the 3D view: a gizmo on the avatar's hips at the playhead (world axes).</summary>
        sealed class PlacementTool : Viewport3D.ITool
        {
            readonly MainForm _f;
            readonly PoseGizmo _giz = new();
            public bool Turn;
            bool _drag;
            PoseGizmo.Part _hot;
            Pose _p0;
            Vec3 _pivot;
            string _status = "";

            public PlacementTool(MainForm f) { _f = f; }

            public bool MouseDown(Viewport3D v, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left || _f._edit == null || !UpdateCenter()) return false;
                var part = _giz.Hit(v, e.Location);
                if (part == PoseGizmo.Part.None) return false;
                _drag = true;
                _p0 = _f._edit.Edits.Placement;
                _pivot = _giz.Center;
                if (_f._playing)
                {
                    _f._playing = false;
                    _f.UpdatePlayBtn();
                }

                _giz.Begin(v, part, e.Location);
                return true;
            }

            public void MouseMove(Viewport3D v, MouseEventArgs e)
            {
                if (_drag)
                {
                    bool fine = (ModifierKeys & Keys.Shift) != 0;
                    _giz.Delta(v, e.Location, fine, out var move, out var rot);
                    var P = Turn
                        ? new Pose(_pivot + rot * (_p0.pos - _pivot), (rot * _p0.rot).Normalized)
                        : new Pose(_p0.pos + move, _p0.rot);
                    _f.SetPlacement(P, false);
                    _giz.Center = Turn ? _pivot : _pivot + move;
                    _status = Turn
                        ? $"turned {_giz.Degrees:0.0}°"
                        : $"moved {move.x * 100:+0.0;-0.0;0} / {move.y * 100:+0.0;-0.0;0} / {move.z * 100:+0.0;-0.0;0} cm";
                    v.Invalidate();
                    return;
                }

                if (!UpdateCenter()) return;
                var hot = _giz.Hit(v, e.Location);
                if (hot != _hot)
                {
                    _hot = hot;
                    v.Invalidate();
                }

                v.Cursor = hot != PoseGizmo.Part.None ? Cursors.Hand : Cursors.Default;
            }

            public void MouseUp(Viewport3D v, MouseEventArgs e)
            {
                if (!_drag) return;
                _drag = false;
                _giz.End();
                _f.SetPlacement(_f._edit.Edits.Placement); // the re-solve with the new placement
                _status = "";
                v.Invalidate();
            }

            public bool KeyDown(Viewport3D v, Keys key)
            {
                switch (key)
                {
                    case Keys.W:
                    case Keys.G:
                        _f.SetGizmoTurn(false);
                        return true;
                    case Keys.E:
                    case Keys.R:
                        _f.SetGizmoTurn(true);
                        return true;
                    case Keys.Escape:
                        _f.SetPlacementGizmo(false);
                        return true;
                }

                return false;
            }

            public void Draw(Viewport3D v, Graphics g)
            {
                if (!UpdateCenter()) return;
                _giz.Draw(v, g, _drag ? _giz.Active : _hot);
                string t = (Turn ? "Turn the take" : "Move the take") +
                           (_status.Length > 0 ? ": " + _status : "   (W move, E turn, Shift slow, Esc off)");
                if (v.Project(_giz.Center, out var c))
                    TextRenderer.DrawText(g, t, Theme.Small, new Point((int)c.X + Theme.S(14), (int)c.Y + Theme.S(46)),
                        Theme.Text);
            }

            bool UpdateCenter()
            {
                if (!_f.ShownPose(out var wp, out _, out _)) return false;
                if (!_drag) _giz.Center = wp[_f._baked.Profile.Human[HumanBones.Hips]];
                _giz.Axes = Quat.Identity;
                _giz.Rotate = Turn;
                return true;
            }
        }
    }
}
