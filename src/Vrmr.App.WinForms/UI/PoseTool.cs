using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace VRMR.UI
{
    /// <summary>The Pose toggle on the scene toolbar.</summary>
    public sealed class PoseToolEntry : ISceneTool
    {
        public string Name => "Pose";

        public string Tip =>
            "Pose the actor in the 3D view.\nDevices: move / turn the headset, controllers and trackers; the IK solves the body from them. H switches a tracker off / on.\n" +
            "Bones: grab hands, feet, head, chest, hips (IK) or turn any bone.\nKeys go into a keyframe strip at the playhead (auto-key). G move, R rotate, Tab devices / bones, I key, Alt+I delete key.";

        public int Order => 10;

        public Viewport3D.ITool Create(SceneEditor ed)
        {
            return new PoseTool(ed);
        }
    }

    /// <summary>
    ///     The posing tool: picks devices (or bones) in the 3D view and moves / turns them with a gizmo, writing keys into an
    ///     action strip at the playhead. Device keys go on an input track (the body re-solves through the IK); bone keys on a
    ///     pose track (IK here: two-bone limbs, spine, head). One undo step per drag.
    /// </summary>
    public sealed class PoseTool : Viewport3D.ITool
    {
        // =====================================================================================================  bones at the playhead

        /// <summary>The bones the tool offers: IK handles (hands, feet, head, chest, hips) and the bones to turn.</summary>
        static readonly int[] PickBones =
        {
            HumanBones.Hips, HumanBones.Spine, HumanBones.Chest, HumanBones.UpperChest, HumanBones.Neck,
            HumanBones.Head,
            HumanBones.LeftShoulder, HumanBones.RightShoulder, HumanBones.LeftUpperArm, HumanBones.RightUpperArm,
            HumanBones.LeftLowerArm, HumanBones.RightLowerArm,
            HumanBones.LeftHand, HumanBones.RightHand, HumanBones.LeftUpperLeg, HumanBones.RightUpperLeg,
            HumanBones.LeftLowerLeg, HumanBones.RightLowerLeg,
            HumanBones.LeftFoot, HumanBones.RightFoot, HumanBones.LeftToes, HumanBones.RightToes
        };

        // =====================================================================================================  drawing

        static readonly Color HotC = Color.FromArgb(255, 214, 90);

        // ---------------------------------------------------------------- HUD (mode chips, target, status)

        readonly List<(RectangleF Rect, Action Click)> _chips = new();
        readonly SceneEditor _ed;
        readonly PoseGizmo _giz = new();

        int _activeBone = -1;
        string _cacheActor;

        // =====================================================================================================  devices at the playhead

        int _cacheVer = -1, _cacheFrame = -1;
        DeviceFrame _dev;
        bool _devReady;
        Drag _drag;
        PoseGizmo.Part _hot;
        int _hoverDev = -1, _hoverBone = -1;
        object _pending;
        HashSet<int> _pendingBones;

        // ---------------------------------------------------------------- pending (auto-key off)

        Dictionary<BodyRole, Pose> _pendingDev;
        bool _pendingDevices;
        int _pendingFrame = -1;
        ScenePose _pendingPose;
        bool _pendingRoot;
        string _status = "";

        public PoseTool(SceneEditor ed) { _ed = ed; }

        Scene S => _ed.Scene;
        Actor A => _ed.ViewActor;
        bool Devices => PoseState.Current == PoseState.Mode.Devices;

        public void Draw(Viewport3D v, Graphics g)
        {
            if (S == null || A == null) return;
            if (_pending != null && _pendingFrame != Frame()) ClearPending();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Devices) DrawDevices(v, g);
            else DrawBones(v, g);
            UpdateGizmo(v);
            if (HasSelection() && !_ed.Playing) _giz.Draw(v, g, _drag != null ? _giz.Active : _hot);
            DrawHud(v, g);
        }

        public bool MouseDown(Viewport3D v, MouseEventArgs e)
        {
            if (S == null || A == null || e.Button != MouseButtons.Left) return false;
            foreach (var c in _chips)
                if (c.Rect.Contains(e.Location))
                {
                    c.Click();
                    v.Invalidate();
                    return true;
                }

            if (_ed.Playing) return false;
            bool shift = (Control.ModifierKeys & Keys.Shift) != 0;
            // the gizmo first
            if (HasSelection())
            {
                UpdateGizmo(v);
                var part = _giz.Hit(v, e.Location);
                if (part != PoseGizmo.Part.None)
                {
                    StartDrag(v, e.Location, part);
                    return true;
                }
            }

            if (Devices)
            {
                int r = PickDevice(v, e.Location);
                if (r < 0) return false;
                var role = (BodyRole)r;
                if (shift)
                {
                    if (!PoseState.Devices.Remove(role)) PoseState.Devices.Add(role);
                }
                else if (!PoseState.Devices.Contains(role))
                {
                    PoseState.Devices.Clear();
                    PoseState.Devices.Add(role);
                }

                PoseState.Notify();
                if (PoseState.Devices.Contains(role) && PoseState.Handle == PoseState.Gizmo.Move)
                {
                    UpdateGizmo(v);
                    StartDrag(v, e.Location, PoseGizmo.Part.View);
                }

                return true;
            }

            int b = PickBone(v, e.Location);
            if (b < 0) return false;
            if (shift)
            {
                if (!PoseState.Bones.Remove(b)) PoseState.Bones.Add(b);
            }
            else if (!PoseState.Bones.Contains(b))
            {
                PoseState.Bones.Clear();
                PoseState.Bones.Add(b);
            }

            if (PoseState.Bones.Contains(b)) _activeBone = b;
            PoseState.Notify();
            if (PoseState.Bones.Contains(b) && PoseState.Handle == PoseState.Gizmo.Move && CanMove(b))
            {
                UpdateGizmo(v);
                StartDrag(v, e.Location, PoseGizmo.Part.View);
            }

            return true;
        }

        public void MouseMove(Viewport3D v, MouseEventArgs e)
        {
            if (_drag != null)
            {
                if (!_drag.Started)
                {
                    if (Math.Abs(e.X - _drag.Down.X) + Math.Abs(e.Y - _drag.Down.Y) < Theme.Sf(3)) return;
                    if (!BeginEdit(v))
                    {
                        _drag = null;
                        _giz.End();
                        return;
                    }
                }

                bool fine = (Control.ModifierKeys & Keys.Shift) != 0;
                _giz.Delta(v, e.Location, fine, out var move, out var rot);
                if (_drag.Devices) DragDevices(move, rot);
                else DragBones(move, rot);
                return;
            }

            // hover
            var hot = HasSelection() && !_ed.Playing ? _giz.Hit(v, e.Location) : PoseGizmo.Part.None;
            int hd = hot == PoseGizmo.Part.None && Devices ? PickDevice(v, e.Location) : -1;
            int hb = hot == PoseGizmo.Part.None && !Devices ? PickBone(v, e.Location) : -1;
            bool chip = _chips.Any(c => c.Rect.Contains(e.Location));
            if (hot != _hot || hd != _hoverDev || hb != _hoverBone)
            {
                _hot = hot;
                _hoverDev = hd;
                _hoverBone = hb;
                v.Invalidate();
            }

            v.Cursor = hot != PoseGizmo.Part.None || hd >= 0 || hb >= 0 || chip ? Cursors.Hand : Cursors.Default;
        }

        public void MouseUp(Viewport3D v, MouseEventArgs e)
        {
            if (_drag == null) return;
            var d = _drag;
            _drag = null;
            _giz.End();
            if (d.Started)
            {
                if (PoseState.AutoKey) _ed.EndEdit();
                PoseState.Notify();
            }

            _status = "";
            v.Invalidate();
        }

        // =====================================================================================================  keys

        public bool KeyDown(Viewport3D v, Keys key)
        {
            if (S == null || A == null) return false;
            switch (key)
            {
                case Keys.Escape:
                    if (_drag != null)
                    {
                        CancelDrag();
                        return true;
                    }

                    if (Devices) PoseState.Devices.Clear();
                    else PoseState.Bones.Clear();
                    ClearPending();
                    PoseState.Notify();
                    return true;
                case Keys.Tab:
                    SetMode(Devices ? PoseState.Mode.Bones : PoseState.Mode.Devices);
                    return true;
                case Keys.G:
                    PoseState.Handle = PoseState.Gizmo.Move;
                    return true;
                case Keys.R:
                    PoseState.Handle = PoseState.Gizmo.Rotate;
                    return true;
                case Keys.L:
                    PoseState.Local = !PoseState.Local;
                    return true;
                case Keys.A:
                    if (Devices)
                    {
                        var d = DevicesNow();
                        foreach (var r in KeyDevices.Roles)
                            if (d.Has(r))
                                PoseState.Devices.Add(r);
                    }
                    else
                        foreach (int b in PickBones)
                            if (_ed.View.Profile?.Has(b) == true)
                                PoseState.Bones.Add(b);

                    PoseState.Notify();
                    return true;
                case Keys.I:
                    Run("Insert key", () => NeedSelection(false), () => InsertKeys(false));
                    return true;
                case Keys.Shift | Keys.I:
                    Run("Insert keys (all)", null, () => InsertKeys(true));
                    return true;
                case Keys.Alt | Keys.I:
                    Run("Delete key", NeedKeysHere, DeleteKeys);
                    return true;
                case Keys.H:
                    if (Devices)
                        Run("Switch device on / off",
                            () => PoseState.Devices.Count == 0 ? "select a device first" : null, ToggleInfluence);
                    return true;
                case Keys.M:
                    Run("Mirror keys", NeedKeysHere, Mirror);
                    return true;
                case Keys.Control | Keys.C:
                    Copy();
                    return true;
                case Keys.Control | Keys.V:
                    Run("Paste keys", () => KeyframeOps.Clipboard?.Count > 0 ? null : "nothing copied",
                        () => Paste(false));
                    return true;
                case Keys.Control | Keys.Shift | Keys.V:
                    Run("Paste keys mirrored", () => KeyframeOps.Clipboard?.Count > 0 ? null : "nothing copied",
                        () => Paste(true));
                    return true;
                case Keys.P:
                    if (!Devices) { TogglePins(); }

                    return true;
                case Keys.Oemcomma:
                    JumpKey(-1);
                    return true;
                case Keys.OemPeriod:
                    JumpKey(1);
                    return true;
                case Keys.Left:
                    _ed.Step(-1);
                    return true;
                case Keys.Right:
                    _ed.Step(1);
                    return true;
                case Keys.F:
                    v.FrameAll();
                    return true;
            }

            return false;
        }

        int Frame()
        {
            if (S == null) return 0;
            int f = KeyTarget.FrameOf(S, _ed.Playhead);
            return Math.Min(f, Math.Max(0, SceneEvaluator.FrameCount(S) - 1));
        }

        (NlaTrack Track, Strip Strip) Target()
        {
            return S == null || A == null
                ? (null, null)
                : KeyTarget.Find(S, A, _ed.SelectedStrip, Devices, KeyTarget.TimeOf(S, Frame()));
        }

        DeviceFrame DevicesNow()
        {
            if (_drag != null && _drag.Devices) return _drag.Shown;
            int f = Frame();
            if (_dev != null && _cacheVer == PoseState.Version && _cacheFrame == f && _cacheActor == A?.Id) return _dev;
            var d = new DeviceFrame();
            var clip = _ed.Playing ? _ed.ClipOf(A) : null;
            if (clip != null && f < clip.Frames)
            {
                // while playing: what the evaluation fed the IK (scene space), no re-planning per frame
                foreach (var kv in clip.Trackers)
                {
                    if (!Enum.TryParse(kv.Key, out BodyRole r)) continue;
                    d.Pose[r] = kv.Value[f];
                    d.Valid[r] = clip.TrackerValid[kv.Key][f];
                    d.Influence[r] =
                        clip.TrackerInfluence != null && clip.TrackerInfluence.TryGetValue(kv.Key, out float[] inf)
                            ? inf[f]
                            : 1f;
                }

                _dev = d;
                _cacheVer = -1;
                _devReady = true;
                return d;
            }

            _devReady = S != null && A != null && KeyDevices.Below(_ed.Eval, S, A, A.Tracks.Count, f, d);
            if (_pending != null && _pendingFrame == f && _pendingDevices)
                foreach (var kv in _pendingDev)
                {
                    d.Pose[kv.Key] = kv.Value;
                    d.Valid[kv.Key] = true;
                }

            _dev = d;
            _cacheVer = PoseState.Version;
            _cacheFrame = f;
            _cacheActor = A?.Id;
            return d;
        }

        /// <summary>
        ///     Devices in the take are shown; a device take also shows the ones it doesn't use yet (faint: drag one to add
        ///     it).
        /// </summary>
        static bool Shown(DeviceFrame d, BodyRole r)
        {
            return d.Pose.ContainsKey(r) && (d.Has(r) || d.KeyframeOnly);
        }

        static bool IsIkHandle(int b)
        {
            return b == HumanBones.Hips || b == HumanBones.Head || b == HumanBones.LeftHand ||
                   b == HumanBones.RightHand || b == HumanBones.LeftFoot || b == HumanBones.RightFoot
                   || b == HumanBones.Chest || b == HumanBones.UpperChest || b == HumanBones.LeftLowerArm ||
                   b == HumanBones.RightLowerArm || b == HumanBones.LeftLowerLeg || b == HumanBones.RightLowerLeg;
        }

        bool BonePos(Viewport3D v, int b, out Vec3 p, out Quat w)
        {
            p = Vec3.Zero;
            w = Quat.Identity;
            if (_drag != null && !_drag.Devices && _drag.Ik != null)
            {
                if (!_drag.Ik.Has(b)) return false;
                p = _drag.Ik.Pos[b];
                w = _drag.Ik.World[b];
                return true;
            }

            var prof = v.Profile;
            if (prof == null || v.NodePos == null || !prof.Has(b)) return false;
            int n = prof.Human[b];
            p = v.NodePos[n];
            w = v.NodeRot != null && n < v.NodeRot.Length ? v.NodeRot[n] : Quat.Identity;
            return true;
        }

        Color DevColor(BodyRole r)
        {
            return r == BodyRole.Head ? Color.FromArgb(214, 216, 226) :
                r == BodyRole.LeftHand || r == BodyRole.RightHand ? Theme.Accent : Color.FromArgb(176, 182, 200);
        }

        void DrawDevices(Viewport3D v, Graphics g)
        {
            var d = DevicesNow();
            int i = 0;
            foreach (var r in KeyDevices.Roles)
            {
                i++;
                if (!Shown(d, r)) continue;
                var p = d.Scene(r);
                if (!v.Project(p.pos, out var s)) continue;
                float inf = d.InfluenceOf(r);
                bool sel = PoseState.Devices.Contains(r), hover = _hoverDev == (int)r;
                var col = DevColor(r);
                if (!d.Has(r))
                {
                    inf = 0;
                    col = Theme.Mix(col, Theme.Bg, 0.45f);
                } // not used yet (device take): a ghost to drag in

                int alpha = (int)(70 + 185 * MathX.Clamp01(inf));
                // link to the bone it drives: how far the solved body is from the device
                int bone = KeyRest.BoneOf(r);
                if (BonePos(v, bone, out var bp, out _) && v.Project(bp, out var bs))
                    using (var pen = new Pen(Color.FromArgb(inf < 0.05f ? 60 : 110, col), 1f)
                               { DashStyle = DashStyle.Dot })
                        g.DrawLine(pen, s, bs);
                // forward direction
                var fwd = p.rot * Vec3.Forward;
                if (v.Project(p.pos + fwd * (r == BodyRole.Head ? 0.16f : 0.1f), out var fs))
                    using (var pen = new Pen(Color.FromArgb(alpha, col), Theme.Sf(1.4f)))
                        g.DrawLine(pen, s, fs);
                if (r == BodyRole.Head) DrawHmd(v, g, p, Color.FromArgb(alpha, sel ? HotC : col));
                float rad = Theme.Sf(r == BodyRole.Head ? 9 :
                    r == BodyRole.LeftHand || r == BodyRole.RightHand ? 8 : 7);
                using (var b = new SolidBrush(Color.FromArgb(sel ? 120 : (int)(40 * inf + 20), sel ? HotC : col)))
                    g.FillEllipse(b, s.X - rad, s.Y - rad, rad * 2, rad * 2);
                using (var pen = new Pen(
                           Color.FromArgb(sel || hover ? 255 : alpha, sel ? HotC : hover ? Theme.Text : col),
                           Theme.Sf(sel ? 2.2f : 1.5f)))
                {
                    if (inf < 0.05f) pen.DashStyle = DashStyle.Dash;
                    g.DrawEllipse(pen, s.X - rad, s.Y - rad, rad * 2, rad * 2);
                }

                if (sel || hover)
                {
                    string label = PoseState.RoleLabel(r) + (!d.Has(r) ? "  (not used: drag to add)" :
                        inf < 0.999f ? $"  {inf * 100:0}%" + (inf < 0.05f ? " (off)" : "") : "");
                    TextRenderer.DrawText(g, label, Theme.Small,
                        new Point((int)(s.X + rad + 3), (int)(s.Y - rad - Theme.S(12))),
                        sel ? HotC : Theme.Mix(col, Theme.Text, 0.4f));
                }

                // keyed at the playhead: a small diamond
                if (KeyedAt(r))
                    DrawDiamond(g, new PointF(s.X - rad - Theme.Sf(6), s.Y - rad), Theme.Sf(4),
                        Color.FromArgb(240, 200, 90));
            }
        }

        static void DrawDiamond(Graphics g, PointF c, float r, Color col)
        {
            var pts = new[]
            {
                new PointF(c.X, c.Y - r), new PointF(c.X + r, c.Y), new PointF(c.X, c.Y + r), new PointF(c.X - r, c.Y)
            };
            using (var b = new SolidBrush(col)) g.FillPolygon(b, pts);
        }

        void DrawHmd(Viewport3D v, Graphics g, Pose p, Color col)
        {
            var h = new Vec3(0.085f, 0.048f, 0.045f);
            var c = new Vec3[8];
            for (int k = 0; k < 8; k++)
                c[k] = p.pos + p.rot * new Vec3((k & 1) == 0 ? -h.x : h.x, (k & 2) == 0 ? -h.y : h.y,
                    ((k & 4) == 0 ? -h.z : h.z) + 0.03f);
            var s = new PointF[8];
            for (int k = 0; k < 8; k++)
                if (!v.Project(c[k], out s[k]))
                    return;
            int[,] e =
            {
                { 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 }, { 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 }, { 0, 4 }, { 1, 5 },
                { 2, 6 }, { 3, 7 }
            };
            using (var pen = new Pen(col, Theme.Sf(1.3f)))
                for (int k = 0; k < 12; k++)
                    g.DrawLine(pen, s[e[k, 0]], s[e[k, 1]]);
        }

        bool KeyedAt(BodyRole r)
        {
            var (t, st) = Target();
            var act = st != null ? S.Action(st.Source) : null;
            if (act == null) return false;
            double at = KeyTarget.ActionTime(S, st, Frame());
            return KeyframeOps.HasKey(act, at, p => Channel.DeviceOf(p, out var rr, out _) && rr == r);
        }

        void DrawBones(Viewport3D v, Graphics g)
        {
            var (t, st) = Target();
            var act = st != null ? S.Action(st.Source) : null;
            double at = st != null ? KeyTarget.ActionTime(S, st, Frame()) : 0;
            foreach (int b in PickBones)
            {
                if (!BonePos(v, b, out var p, out _)) continue;
                if (!v.Project(p, out var s)) continue;
                bool sel = PoseState.Bones.Contains(b), hover = _hoverBone == b, ik = IsIkHandle(b);
                float rad = Theme.Sf(ik ? 7 : 4.5f);
                var col = HumanBones.IsLeft(b) ? Theme.Left :
                    HumanBones.Names[b].StartsWith("Right") ? Theme.Right : Theme.Center;
                // a dark ring under the coloured one keeps the handles readable over a light mesh
                using (var pen = new Pen(Color.FromArgb(150, 10, 12, 16), Theme.Sf(sel ? 4.4f : 3.2f)))
                    g.DrawEllipse(pen, s.X - rad, s.Y - rad, rad * 2, rad * 2);
                using (var br = new SolidBrush(Color.FromArgb(sel ? 200 : 120, sel ? HotC : col)))
                    g.FillEllipse(br, s.X - rad, s.Y - rad, rad * 2, rad * 2);
                using (var pen = new Pen(sel ? HotC : hover ? Theme.Text : col, Theme.Sf(sel ? 2.2f : 1.4f)))
                    g.DrawEllipse(pen, s.X - rad, s.Y - rad, rad * 2, rad * 2);
                if (PoseState.Pins.Contains(b))
                    using (var pen = new Pen(Color.FromArgb(220, 120, 200, 255), Theme.Sf(1.5f)))
                    {
                        g.DrawLine(pen, s.X, s.Y + rad, s.X, s.Y + rad + Theme.Sf(8));
                        g.DrawLine(pen, s.X - Theme.Sf(4), s.Y + rad + Theme.Sf(8), s.X + Theme.Sf(4),
                            s.Y + rad + Theme.Sf(8));
                    }

                if (sel || hover)
                    TextRenderer.DrawText(g, PoseState.BoneLabel(b) + (PoseState.Pins.Contains(b) ? " (pinned)" : ""),
                        Theme.Small, new Point((int)(s.X + rad + 3), (int)(s.Y - rad - Theme.S(12))),
                        sel ? HotC : Theme.Text);
                if (act != null && KeyframeOps.HasKey(act, at,
                        x => Channel.BoneOf(x) == b || (b == HumanBones.Hips && KeyframeOps.IsRoot(x))))
                    DrawDiamond(g, new PointF(s.X - rad - Theme.Sf(5), s.Y - rad), Theme.Sf(3.5f),
                        Color.FromArgb(240, 200, 90));
            }
        }

        bool HasSelection()
        {
            return Devices ? PoseState.Devices.Count > 0 : PoseState.Bones.Count > 0;
        }

        /// <summary>Places the gizmo on the selection (the active device / bone; the selection's centre for several devices).</summary>
        void UpdateGizmo(Viewport3D v)
        {
            _giz.Rotate = PoseState.Handle == PoseState.Gizmo.Rotate;
            if (_drag != null) return;
            if (Devices)
            {
                var d = DevicesNow();
                var sel = PoseState.Devices.Where(r => d.Pose.ContainsKey(r)).ToList();
                if (sel.Count == 0) return;
                var c = Vec3.Zero;
                foreach (var r in sel) c += d.Scene(r).pos;
                _giz.Center = c / sel.Count;
                _giz.Axes = PoseState.Local && sel.Count == 1 ? d.Scene(sel[0]).rot : Quat.Identity;
            }
            else
            {
                int b = ActiveBone();
                if (b < 0 || !BonePos(v, b, out var p, out var w)) return;
                _giz.Center = p;
                _giz.Axes = PoseState.Local ? w : Quat.Identity;
            }
        }

        int ActiveBone()
        {
            return _activeBone >= 0 && PoseState.Bones.Contains(_activeBone) ? _activeBone :
                PoseState.Bones.Count > 0 ? PoseState.Bones.First() : -1;
        }

        void DrawHud(Viewport3D v, Graphics g)
        {
            _chips.Clear();
            var items = new List<(string Text, bool On, Action Click)>
            {
                ("Devices", Devices, () => SetMode(PoseState.Mode.Devices)),
                ("Bones", !Devices, () => SetMode(PoseState.Mode.Bones)),
                (null, false, null),
                ("Move  G", PoseState.Handle == PoseState.Gizmo.Move, () => PoseState.Handle = PoseState.Gizmo.Move),
                ("Rotate  R", PoseState.Handle == PoseState.Gizmo.Rotate,
                    () => PoseState.Handle = PoseState.Gizmo.Rotate),
                ("Local  L", PoseState.Local, () => PoseState.Local = !PoseState.Local),
                (null, false, null),
                ("Auto-key", PoseState.AutoKey, () =>
                {
                    PoseState.AutoKey = !PoseState.AutoKey;
                    PoseState.Notify();
                })
            };
            float h = Theme.Sf(22), gap = Theme.Sf(5), x0, y = Theme.Sf(30);
            float total = 0;
            foreach (var it in items)
                total += it.Text == null
                    ? Theme.Sf(10)
                    : TextRenderer.MeasureText(it.Text, Theme.Small).Width + Theme.Sf(14) + gap;
            x0 = Math.Max(Theme.Sf(8), (v.Width - total) / 2);
            float x = x0;
            foreach (var it in items)
            {
                if (it.Text == null)
                {
                    x += Theme.Sf(10);
                    continue;
                }

                var sz = TextRenderer.MeasureText(it.Text, Theme.Small);
                var r = new RectangleF(x, y, sz.Width + Theme.Sf(14), h);
                Theme.FillRound(g, it.On ? Color.FromArgb(210, Theme.AccentDim) : Color.FromArgb(170, Theme.Panel2), r,
                    Theme.Sf(5));
                Theme.StrokeRound(g, it.On ? Theme.Accent : Theme.Border, r, Theme.Sf(5));
                TextRenderer.DrawText(g, it.Text, Theme.Small, Rectangle.Round(r), it.On ? Theme.Text : Theme.TextDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                _chips.Add((r, it.Click));
                x += r.Width + gap;
            }

            // what gets keyed, and the status / hints
            var (track, st) = Target();
            string target = st != null
                ? $"keys → {st.Name} ({(st.Blend == BlendMode.Add ? "Add" : "Replace")})"
                : $"keys → a new \"{(Devices ? "Device keys" : "Pose keys")}\" strip on the first edit";
            if (!PoseState.AutoKey) target += "   auto-key off: I keys the change";
            string line2 = _status.Length > 0 ? _status : Hint();
            TextRenderer.DrawText(g, target + "\n" + line2, Theme.Small,
                new Rectangle(0, (int)(y + h + Theme.Sf(4)), v.Width, Theme.S(40)), Theme.TextDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
        }

        string Hint()
        {
            if (_ed.Playing) return "pause to pose";
            if (Devices)
            {
                if (!_devReady && S != null && A != null) return "takes still solving…";
                if (PoseState.Devices.Count == 0)
                    return "click a headset / controller / tracker (Shift: several)" +
                           (DevicesNow().KeyframeOnly ? "; faint ones aren't used yet: drag one to add it" : "") +
                           "   Tab: bones";
                return
                    "drag the gizmo   H: switch off / on   I: key   Alt+I: delete key   M: mirror   , .: prev / next key";
            }

            return PoseState.Bones.Count == 0
                ? "click a hand, foot, head, chest or hips (big dots: IK; small: turn)   Tab: devices"
                : "drag the gizmo   P: pin hand / foot   I: key   Alt+I: delete key   M: mirror   , .: prev / next key";
        }

        void SetMode(PoseState.Mode m)
        {
            if (PoseState.Current == m) return;
            PoseState.Current = m;
            _status = "";
            PoseState.Notify();
        }

        // =====================================================================================================  picking

        int PickDevice(Viewport3D v, PointF m)
        {
            var d = DevicesNow();
            int best = -1;
            float bd = Theme.Sf(14);
            foreach (var r in KeyDevices.Roles)
            {
                if (!Shown(d, r) || !v.Project(d.Scene(r).pos, out var s)) continue;
                float dist = (float)Math.Sqrt((s.X - m.X) * (s.X - m.X) + (s.Y - m.Y) * (s.Y - m.Y));
                if (dist < bd)
                {
                    bd = dist;
                    best = (int)r;
                }
            }

            return best;
        }

        int PickBone(Viewport3D v, PointF m)
        {
            int best = -1;
            float bd = Theme.Sf(12);
            foreach (int b in PickBones)
            {
                if (!BonePos(v, b, out var p, out _) || !v.Project(p, out var s)) continue;
                float dist = (float)Math.Sqrt((s.X - m.X) * (s.X - m.X) + (s.Y - m.Y) * (s.Y - m.Y)) -
                             (IsIkHandle(b) ? Theme.Sf(3) : 0);
                if (dist < bd)
                {
                    bd = dist;
                    best = b;
                }
            }

            return best;
        }

        static bool CanMove(int b)
        {
            return IsIkHandle(b) || b == HumanBones.Spine || b == HumanBones.Neck;
        }

        void StartDrag(Viewport3D v, PointF m, PoseGizmo.Part part)
        {
            _drag = new Drag { Devices = Devices, Down = m, Part = part, ViewMove = part == PoseGizmo.Part.View };
            _giz.Begin(v, part, m);
        }

        /// <summary>The first real movement of a drag: finds / creates the strip, opens the undo step, captures the start state.</summary>
        bool BeginEdit(Viewport3D v)
        {
            var d = _drag;
            int f = Frame();
            var (track, st) = Target();
            int ti = track != null ? A.Tracks.IndexOf(track) : A.Tracks.Count;
            // start state below the layer (before anything changes)
            if (d.Devices)
            {
                d.Below = new DeviceFrame();
                if (!KeyDevices.Below(_ed.Eval, S, A, ti, f, d.Below))
                {
                    _status = "takes still solving: try again in a moment";
                    return false;
                }
            }
            else
            {
                d.BelowPose = new ScenePose();
                if (!_ed.Eval.EvaluateFrame(S, A, f, d.BelowPose, ti))
                {
                    _status = "takes still solving: try again in a moment";
                    return false;
                }

                (var rig, float scale) = _ed.Eval.RigOf(S, A);
                d.Ik = new PoseIK(rig, scale);
            }

            string label = d.Devices
                ? (_giz.Rotate ? "Turn " : "Move ") + string.Join(", ", PoseState.Devices.Select(PoseState.RoleLabel))
                : (_giz.Rotate ? "Turn " : "Pose ") + PoseState.BoneLabel(ActiveBone());
            if (PoseState.AutoKey)
            {
                _ed.BeginEdit(label);
                if (st == null)
                {
                    (track, st) = KeyTarget.Create(S, A, Devices);
                    d.Created = true;
                }
            }

            d.Track = track;
            d.Strip = st;
            d.Act = st != null ? S.Action(st.Source) : null;
            d.Backup = d.Act != null ? KeyAction.FromJson(Json.ParseObject(Json.Serialize(d.Act.ToJson()))) : null;
            d.Frame = f;
            d.T = st != null ? KeyTarget.ActionTime(S, st, f) : 0;
            if (d.Devices)
            {
                // a device take (keys on a pose track) is placed by its own strip offset
                d.DeviceTake = d.Track != null && d.Track.Kind == TrackKind.Pose;
                if (d.DeviceTake && d.Strip != null) d.Below.ToScene = d.Strip.Offset;
                d.Shown = d.Below.Clone();
                if (d.Act != null) KeyDevices.Apply(d.Act, d.Strip, d.T, 1f, d.Shown, d.DeviceTake);
                if (_pending != null && _pendingDevices && _pendingFrame == f)
                    foreach (var kv in _pendingDev)
                        d.Shown.Pose[kv.Key] = kv.Value;
                d.StartScene = PoseState.Devices.Where(r => d.Shown.Pose.ContainsKey(r))
                    .ToDictionary(r => r, r => d.Shown.Scene(r));
                if (d.StartScene.Count == 0)
                {
                    if (PoseState.AutoKey) { _ed.EndEdit(); }

                    return false;
                }

                d.Pivot = _giz.Center;
            }
            else
            {
                d.StartPose = new ScenePose();
                if (d.Act != null) KeyframeOps.ApplyStripPose(d.Act, d.Strip, d.T, d.BelowPose, d.StartPose);
                else d.StartPose.CopyFrom(d.BelowPose);
                if (_pending != null && !_pendingDevices && _pendingFrame == f) d.StartPose.CopyFrom(_pendingPose);
                d.Work = new ScenePose();
                d.Work.CopyFrom(d.StartPose);
                d.Bone = ActiveBone();
                d.Ik.Fk(d.StartPose);
                d.StartJoint = d.Ik.Pos[d.Bone];
                var lim = PoseIK.LimbOf(d.Bone);
                if (lim != null)
                {
                    int end = PoseIK.Bones(lim.Value).End;
                    d.StartEnd = d.Ik.Pos[end];
                    d.StartEndRot = d.Ik.World[end];
                }

                d.Tmp = new BakedClip
                {
                    Profile = d.Ik.Rig.Profile, Fps = S.Fps, Frames = 1, Hips = new Vec3[1],
                    Rot = new[] { new Quat[HumanBones.Count] }, BoneExists = d.Ik.Rig.Exists, Scale = d.Ik.Scale
                };
            }

            d.Started = true;
            return true;
        }

        // ---------------------------------------------------------------- device drags

        void DragDevices(Vec3 move, Quat rot)
        {
            var d = _drag;
            var target = new Dictionary<BodyRole, Pose>();
            var below = new Dictionary<BodyRole, Pose>();
            var toSolve = d.Below.ToScene.Inverse;
            foreach (var kv in d.StartScene)
            {
                var p = kv.Value;
                var np = _giz.Rotate
                    ? new Pose(d.Pivot + rot * (p.pos - d.Pivot), (rot * p.rot).Normalized)
                    : new Pose(p.pos + move, p.rot);
                target[kv.Key] = toSolve * np;
                below[kv.Key] = d.Below.Pose.TryGetValue(kv.Key, out var bp) ? bp : target[kv.Key];
            }

            if (PoseState.AutoKey && d.Act != null)
            {
                KeyframeOps.KeyDevices(d.Act, d.Strip, d.T, target, below, target.Keys, absolute: d.DeviceTake);
                d.Shown = d.Below.Clone();
                KeyDevices.Apply(d.Act, d.Strip, d.T, 1f, d.Shown, d.DeviceTake);
                _ed.Changing();
            }
            else
            {
                SetPendingDevices(target);
                d.Shown = d.Below.Clone();
                if (d.Act != null) KeyDevices.Apply(d.Act, d.Strip, d.T, 1f, d.Shown, d.DeviceTake);
                foreach (var kv in target)
                {
                    d.Shown.Pose[kv.Key] = kv.Value;
                    d.Shown.Valid[kv.Key] = true;
                }
            }

            _giz.Center = _giz.Rotate ? d.Pivot : d.Pivot + move;
            _status = _giz.Rotate
                ? $"turned {_giz.Degrees:0.0}°"
                : $"moved {move.Length * 100:0.0} cm  (x {move.x * 100:0.0}, y {move.y * 100:0.0}, z {move.z * 100:0.0})";
            _ed.View.Invalidate();
        }

        // ---------------------------------------------------------------- bone drags

        void DragBones(Vec3 move, Quat rot)
        {
            var d = _drag;
            var ik = d.Ik;
            int b = d.Bone;
            d.Work.CopyFrom(d.StartPose);
            var pins = PoseState.Pins.Where(x => x != b).ToList();
            if (_giz.Rotate)
            {
                bool keepHead = b == HumanBones.Spine || b == HumanBones.Chest || b == HumanBones.UpperChest;
                ik.RotatePinned(d.Work, b, rot, pins, keepHead);
            }
            else
            {
                var lim = PoseIK.LimbOf(b);
                var bones = lim != null ? PoseIK.Bones(lim.Value) : default;
                if (b == HumanBones.Hips) ik.MoveHips(d.Work, d.StartPose.Hips + move, null, pins);
                else if (lim != null && b == bones.End)
                    ik.SolveLimb(d.Work, lim.Value, d.StartJoint + move, d.StartEndRot);
                else if (lim != null && b == bones.Lower)
                    ik.SolveLimb(d.Work, lim.Value, d.StartEnd, d.StartEndRot,
                        d.StartJoint + move * 3f); // the knee / elbow points where it is dragged
                else if (b == HumanBones.Head) ik.SolveSpine(d.Work, HumanBones.Head, d.StartJoint + move, true, pins);
                else if (b == HumanBones.Spine || b == HumanBones.Chest || b == HumanBones.UpperChest ||
                         b == HumanBones.Neck)
                {
                    int tip = b == HumanBones.Neck ? HumanBones.Head :
                        ik.Has(HumanBones.Neck) ? HumanBones.Neck : HumanBones.Head;
                    ik.Fk(d.StartPose);
                    var tip0 = ik.Pos[tip];
                    ik.SolveSpine(d.Work, tip, tip0 + move, true, pins);
                }
            }

            ik.Fk(d.Work);
            // which bones changed (and stay keyed for the rest of the drag)
            foreach (int x in ik.Rig.Order)
                if (Quat.Angle(d.Work.Local[x], d.StartPose.Local[x]) > 0.01f)
                    d.Keyed.Add(x);
            if ((d.Work.Hips - d.StartPose.Hips).Length > 1e-6f) d.RootKeyed = true;
            if (PoseState.AutoKey && d.Act != null)
            {
                KeyframeOps.KeyPose(d.Act, d.Strip, d.T, d.Work, d.BelowPose, d.Keyed, d.RootKeyed);
                _ed.Changing();
            }
            else SetPendingPose(d.Work, d.Keyed, d.RootKeyed);

            ShowPose(d);
            _giz.Center = ik.Pos[b];
            _status = _giz.Rotate ? $"turned {_giz.Degrees:0.0}°" : $"moved {move.Length * 100:0.0} cm";
        }

        /// <summary>Shows the posed skeleton right away (the scene's evaluation follows).</summary>
        void ShowPose(Drag d)
        {
            var v = _ed.View;
            if (d.Tmp == null || v.Profile != d.Tmp.Profile || v.NodePos == null) return;
            d.Tmp.Hips[0] = d.Work.Hips;
            d.Ik.Rig.ToWorld(d.Work.Local, d.Tmp.Rot[0]);
            var wr = v.NodeRot != null && v.NodeRot.Length == v.NodePos.Length ? v.NodeRot : new Quat[v.NodePos.Length];
            d.Tmp.EvaluateNodes(0, v.NodePos, wr);
            v.NodeRot = wr;
        }

        void SetPendingDevices(Dictionary<BodyRole, Pose> solve)
        {
            if (_pendingDev == null || !_pendingDevices || _pendingFrame != Frame())
                _pendingDev = new Dictionary<BodyRole, Pose>();
            foreach (var kv in solve) _pendingDev[kv.Key] = kv.Value;
            _pending = _pendingDev;
            _pendingDevices = true;
            _pendingFrame = Frame();
        }

        void SetPendingPose(ScenePose p, HashSet<int> bones, bool root)
        {
            _pendingPose = _pendingPose ?? new ScenePose();
            _pendingPose.CopyFrom(p);
            _pendingBones = new HashSet<int>(bones);
            _pendingRoot = root;
            _pending = _pendingPose;
            _pendingDevices = false;
            _pendingFrame = Frame();
        }

        void ClearPending()
        {
            _pending = null;
            _pendingDev = null;
            _pendingFrame = -1;
            PoseState.Version++;
        }

        void CancelDrag()
        {
            var d = _drag;
            _drag = null;
            _giz.End();
            if (d != null && d.Started && PoseState.AutoKey)
            {
                if (d.Act != null && d.Backup != null && !d.Created)
                {
                    d.Act.Channels.Clear();
                    foreach (var kv in d.Backup.Channels) d.Act.Channels[kv.Key] = kv.Value;
                }

                _ed.EndEdit();
                if (d.Created) _ed.Undo();
            }

            ClearPending();
            _status = "cancelled";
            PoseState.Notify();
        }

        /// <summary>Runs a key operation as one undo step on the target strip (check: an error text when it can't run).</summary>
        void Run(string label, Func<string> check, Action op)
        {
            if (_ed.Playing) return;
            string err = check?.Invoke();
            if (err != null)
            {
                _status = err;
                _ed.View.Invalidate();
                return;
            }

            _ed.Edit(label, () =>
            {
                try { op(); }
                catch (Exception ex)
                {
                    AppLog.Error("Pose tool: " + ex);
                    _status = "error: " + ex.Message;
                }
            });
            PoseState.Notify();
            _ed.View.Invalidate();
        }

        (NlaTrack Track, Strip Strip, KeyAction Act, double T, int Frame, int TrackIndex) EnsureTarget(bool create)
        {
            var (t, st) = Target();
            if (st == null && create) (t, st) = KeyTarget.Create(S, A, Devices);
            if (st == null) return (null, null, null, 0, 0, 0);
            int f = Frame();
            return (t, st, S.Action(st.Source), KeyTarget.ActionTime(S, st, f), f, A.Tracks.IndexOf(t));
        }

        Func<string, bool> SelectionFilter(bool all)
        {
            if (all) return Devices ? p => Channel.DeviceOf(p, out _, out _) : p => KeyframeOps.IsBody(p);
            if (Devices) return KeyframeOps.Filter(PoseState.Devices, null);
            var bones = new HashSet<int>(PoseState.Bones);
            return KeyframeOps.Filter(null, bones, bones.Contains(HumanBones.Hips));
        }

        Func<string, bool> OpFilter()
        {
            return HasSelection() ? SelectionFilter(false) : SelectionFilter(true);
        }

        string NeedKeysHere()
        {
            var x = EnsureTarget(false);
            if (x.Act == null) return "no keyframe strip here";
            return KeyframeOps.HasKey(x.Act, x.T, OpFilter())
                ? null
                : "no keys at the playhead" + (HasSelection() ? " for the selection" : "");
        }

        string NeedSelection(bool all)
        {
            if (all || HasSelection() || _pending != null) return null;
            return Devices
                ? "select a device first (A: all, Shift+I: key all)"
                : "select a bone first (A: all, Shift+I: key all)";
        }

        void InsertKeys(bool all)
        {
            var x = EnsureTarget(true);
            if (x.Act == null) return;
            if (Devices)
            {
                var below = new DeviceFrame();
                KeyDevices.Below(_ed.Eval, S, A, x.TrackIndex, x.Frame, below);
                var roles = all
                    ? KeyDevices.Roles.Where(r => below.Has(r) || KeyframeOps.KeysDevice(x.Act, r)).ToList()
                    : PoseState.Devices.ToList();
                int pend = 0;
                // moves not keyed yet (auto-key off) are keyed as they are; the rest hold their current values
                if (_pending != null && _pendingDevices && _pendingFrame == x.Frame)
                {
                    KeyframeOps.KeyDevices(x.Act, x.Strip, x.T, _pendingDev, below.Pose, _pendingDev.Keys,
                        absolute: x.Track.Kind == TrackKind.Pose);
                    pend = _pendingDev.Count;
                    roles = roles.Where(r => !_pendingDev.ContainsKey(r)).ToList();
                }

                var paths = roles.SelectMany(r => new[] { Channel.DevicePosition(r), Channel.DeviceRotation(r) });
                KeyframeOps.InsertCurrent(x.Act, x.Strip, x.T, paths, p =>
                {
                    if (!Channel.DeviceOf(p, out var r, out string k) || !below.Pose.TryGetValue(r, out var bp))
                        return null;
                    return k == "position" ? KeyframeOps.V(bp.pos) : KeyframeOps.V(bp.rot);
                });
                _status = $"keyed {roles.Count + pend} device(s)";
            }
            else
            {
                var below = new ScenePose();
                if (!_ed.Eval.EvaluateFrame(S, A, x.Frame, below, x.TrackIndex))
                {
                    _status = "takes still solving";
                    return;
                }

                var (rig, _) = _ed.Eval.RigOf(S, A);
                var bones = all
                    ? rig.Order.Where(b =>
                        !HumanBones.IsFinger(b) && b != HumanBones.LeftEye && b != HumanBones.RightEye &&
                        b != HumanBones.Jaw).ToList()
                    : PoseState.Bones.Where(b => rig.Exists[b]).ToList();
                var cur = new ScenePose();
                KeyframeOps.ApplyStripPose(x.Act, x.Strip, x.T, below, cur);
                bool root = bones.Contains(HumanBones.Hips);
                if (_pending != null && !_pendingDevices && _pendingFrame == x.Frame)
                {
                    // the posed bones (auto-key off) as posed, the other selected ones as they are now
                    foreach (int b in _pendingBones) cur.Local[b] = _pendingPose.Local[b];
                    if (_pendingRoot) cur.Hips = _pendingPose.Hips;
                    bones = bones.Union(_pendingBones).ToList();
                    root |= _pendingRoot;
                }

                KeyframeOps.KeyPose(x.Act, x.Strip, x.T, cur, below, bones, root);
                _status = $"keyed {bones.Count} bone(s)";
            }

            ClearPending();
        }

        void DeleteKeys()
        {
            var x = EnsureTarget(false);
            int n = KeyframeOps.DeleteKeys(x.Act, x.T, OpFilter());
            _status = $"deleted {n} key(s)";
        }

        void ToggleInfluence()
        {
            var x = EnsureTarget(true);
            if (x.Act == null) return;
            var cur = new DeviceFrame();
            KeyDevices.With(_ed.Eval, S, A, x.TrackIndex, x.Strip, x.Frame, cur);
            bool anyOn = PoseState.Devices.Any(r => cur.InfluenceOf(r) >= 0.5f);
            foreach (var r in PoseState.Devices)
                KeyframeOps.RampInfluence(x.Act, r, x.T, anyOn ? 0f : 1f, PoseState.InfluenceRamp);
            _status = (anyOn ? "switched off: " : "switched on: ") +
                      string.Join(", ", PoseState.Devices.Select(PoseState.RoleLabel)) +
                      $" (ramp {PoseState.InfluenceRamp:0.0#} s)";
        }

        KeyframeOps.MirrorFrame MirrorFrameAt(int f, Strip st, KeyAction act, double t)
        {
            (var rig, float scale) = _ed.Eval.RigOf(S, A);
            if (Devices)
            {
                var d = new DeviceFrame();
                KeyDevices.Below(_ed.Eval, S, A, A.Tracks.Count, f, d);
                var head = d.Pose.TryGetValue(BodyRole.Head, out var hp) ? hp : Pose.Identity;
                return new KeyframeOps.MirrorFrame { Origin = head.pos, Yaw = head.rot.Yaw, Rig = rig };
            }

            var p = new ScenePose();
            _ed.Eval.EvaluateFrame(S, A, f, p);
            var origin = KeyframeOps.Value(act, Channel.RootPosition, t) is float[] rv && st.Blend == BlendMode.Replace
                ? KeyframeOps.P(rv)
                : p.Hips;
            return new KeyframeOps.MirrorFrame
                { Origin = origin, Yaw = rig.Facing(p.Local[HumanBones.Hips]), Rig = rig };
        }

        void Mirror()
        {
            var x = EnsureTarget(false);
            KeyframeOps.MirrorKeys(x.Act, x.Strip, x.T, MirrorFrameAt(x.Frame, x.Strip, x.Act, x.T), OpFilter());
            _status = "mirrored onto the other side";
        }

        void Copy()
        {
            var x = EnsureTarget(false);
            if (x.Act == null)
            {
                _status = "nothing to copy";
                return;
            }

            KeyframeOps.Clipboard = KeyframeOps.Copy(x.Act, x.Strip, x.T, OpFilter());
            _status = $"copied {KeyframeOps.Clipboard.Count} channel(s)";
        }

        void Paste(bool mirror)
        {
            var c = KeyframeOps.Clipboard;
            var x = EnsureTarget(true);
            if (x.Act == null) return;
            KeyframeOps.Paste(x.Act, x.T, c, mirror ? MirrorFrameAt(x.Frame, x.Strip, x.Act, x.T) : null);
            _status = $"pasted {c.Count} channel(s)" + (mirror ? " mirrored" : "") +
                      (c.Delta != (x.Strip.Blend == BlendMode.Add)
                          ? "  (copied from a strip with the other blend mode!)"
                          : "");
        }

        void TogglePins()
        {
            foreach (int b in PoseState.Bones.Where(b => PoseIK.Effectors.Contains(b)).ToList())
                if (!PoseState.Pins.Remove(b))
                    PoseState.Pins.Add(b);
            PoseState.Notify();
        }

        void JumpKey(int dir)
        {
            var (t, st) = Target();
            var act = st != null ? S.Action(st.Source) : null;
            if (act == null) return;
            double at = KeyTarget.ActionTime(S, st, Frame());
            var filter = HasSelection() ? SelectionFilter(false) : null;
            double? k = dir < 0 ? KeyframeOps.PrevKey(act, at, filter) : KeyframeOps.NextKey(act, at, filter);
            if (k.HasValue) _ed.Seek(st.ToScene(k.Value));
        }

        // =====================================================================================================  mouse

        sealed class Drag
        {
            public readonly HashSet<int> Keyed = new();
            public KeyAction Act;

            public KeyAction Backup;

            // devices
            public DeviceFrame Below, Shown;
            public ScenePose BelowPose, StartPose, Work;
            public int Bone;
            public bool Created;
            public bool Devices, Started, ViewMove, DeviceTake;
            public PointF Down;

            public int Frame;

            // bones
            public PoseIK Ik;
            public PoseGizmo.Part Part;
            public Vec3 Pivot;
            public bool RootKeyed;
            public Quat StartEndRot;
            public Vec3 StartJoint, StartEnd;
            public Dictionary<BodyRole, Pose> StartScene;
            public Strip Strip;
            public double T;
            public BakedClip Tmp;
            public NlaTrack Track;
        }
    }
}
