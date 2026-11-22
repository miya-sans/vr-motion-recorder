using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using VRMR.App.Services;

namespace VRMR.App.Viewport
{
    using Key = Avalonia.Input.Key;

    /// <summary>
    ///     Moves body trackers against the avatar in the calibration view: click a tracker to pick it, drag the arrows on
    ///     world axes (right, up, forward of the calibration pose). A drag is one edit of that calibration's tracker offsets.
    ///     With <see cref="Mirror" /> the left/right partner is put where the moved tracker's mirror image is.
    /// </summary>
    public sealed class CalibrationTool : IViewportTool
    {
        const float PickRadius = 24f;
        readonly ViewGizmo _gizmo = new();
        readonly TakePresenter _presenter;
        readonly TakeSession _session;
        bool _dragging, _mirror = true;
        Vec3 _move;
        IDisposable _gesture;

        public CalibrationTool(TakePresenter presenter, TakeSession session)
        {
            _presenter = presenter;
            _session = session;
            session.ClipChanged += () =>
            {
                if (!_dragging) _presenter.PreviewOffsets.Clear(); // the new solve has the offsets in it
            };
        }

        /// <summary>The picked tracker (BodyRole name), or "".</summary>
        public string Selected => _presenter.SelectedTracker;

        /// <summary>Moving a left or right tracker puts its partner at the mirror image (across the avatar's middle).</summary>
        public bool Mirror
        {
            get => _mirror;
            set
            {
                if (_mirror == value) return;
                _mirror = value;
                Changed?.Invoke();
            }
        }

        public ViewGizmo Gizmo
        {
            get
            {
                if (Selected.Length == 0 || !_presenter.CalibrationDevice(Selected, out var pos)) return null;
                _gizmo.Center = pos;
                _gizmo.Rotate = false;
                var m = _move * 100;
                _gizmo.Status = _dragging
                    ? FormattableString.Invariant($"{m.x:+0.0;-0.0;0} / {m.y:+0.0;-0.0;0} / {m.z:+0.0;-0.0;0} cm")
                    : TrackerService.RoleLabel(Selected) + "   Shift slow · Esc done";
                return _gizmo;
            }
        }

        public void DragStarted()
        {
            _dragging = true;
            _move = Vec3.Zero;
            _presenter.PreviewOffsets.Clear();
            _gesture = _session.Gesture();
        }

        public void Dragged(Vec3 move, Quat turn, float degrees)
        {
            if (!_dragging) return;
            _move = move;
            var offsets = Offsets(Selected, Nudge(Selected) + move * 100f);
            _presenter.PreviewOffsets.Clear();
            foreach (var kv in offsets)
                if (Shown(kv.Key, out var shown) && Raw(kv.Key, out var raw))
                    _presenter.PreviewOffsets[kv.Key] = raw + kv.Value * 0.01f - shown;
            Changed?.Invoke();
        }

        public void DragEnded()
        {
            if (!_dragging) return;
            _dragging = false;
            if (_move.SqrLength > 1e-10f) SetNudge(Selected, Nudge(Selected) + _move * 100f);
            else _presenter.PreviewOffsets.Clear();
            _move = Vec3.Zero;
            _gesture?.Dispose();
            _gesture = null;
            Changed?.Invoke();
        }

        public bool KeyDown(Key key)
        {
            if (key != Key.Escape || Selected.Length == 0) return false;
            Select("");
            return true;
        }

        public bool Clicked(IViewport view, Point screen)
        {
            var cal = _presenter.ShownCalibration;
            if (cal == null) return false;
            string best = "";
            double bestD = PickRadius;
            foreach (string role in cal.Devices.Keys)
            {
                if (!TakePresenter.IsBodyTracker(cal, role) || !_presenter.CalibrationDevice(role, out var pos) ||
                    !view.Project(pos, out var s)) continue;
                double d = Math.Sqrt((s.X - screen.X) * (s.X - screen.X) + (s.Y - screen.Y) * (s.Y - screen.Y));
                if (d < bestD)
                {
                    bestD = d;
                    best = role;
                }
            }

            if (best.Length == 0) return false;
            Select(best);
            return true;
        }

        /// <summary>Picks a tracker ("" for none).</summary>
        public void Select(string role)
        {
            role ??= "";
            if (role == Selected) return;
            _presenter.SelectedTracker = role;
            Changed?.Invoke();
        }

        /// <summary>The left/right partner of a tracker role, or "" for one in the middle.</summary>
        public static string Partner(string role)
        {
            return role.StartsWith("Left", StringComparison.Ordinal) ? "Right" + role[4..] :
                role.StartsWith("Right", StringComparison.Ordinal) ? "Left" + role[5..] : "";
        }

        /// <summary>The mirror image across the avatar's middle (x = 0 in the calibration view).</summary>
        public static Vec3 Mirrored(Vec3 v)
        {
            return new Vec3(-v.x, v.y, v.z);
        }

        /// <summary>The shown calibration's offset of a tracker in the edits (avatar cm).</summary>
        public Vec3 Nudge(string role)
        {
            var cal = _presenter.ShownCalibration;
            var nudges = cal == null ? null : _session.Take?.Edits.NudgesFor(cal.Key);
            return nudges != null && nudges.TryGetValue(role, out var v) ? v : Vec3.Zero;
        }

        /// <summary>Sets a tracker's offset (avatar cm) as one edit of the shown calibration, its mirror partner included.</summary>
        public void SetNudge(string role, Vec3 cm)
        {
            var cal = _presenter.ShownCalibration;
            if (cal == null || _session.Take == null || role.Length == 0) return;
            string key = cal.Key;
            var offsets = Offsets(role, cm);
            _session.Edit("trackerNudge", e =>
            {
                if (!e.TrackerNudges.TryGetValue(key, out var own))
                {
                    var inherited = e.NudgesFor(key);
                    e.TrackerNudges[key] = own = inherited == null
                        ? new Dictionary<string, Vec3>()
                        : inherited.ToDictionary(k => k.Key, k => k.Value);
                }

                foreach (var kv in offsets)
                    if (kv.Value.SqrLength < 1e-6f) own.Remove(kv.Key);
                    else own[kv.Key] = kv.Value;
                if (own.Count == 0 && !e.TrackerNudges.ContainsKey(EditList.AnyCalibration)) e.TrackerNudges.Remove(key);
            });
            // show the new places until the solve with them arrives
            _presenter.PreviewOffsets.Clear();
            foreach (var kv in offsets)
                if (Shown(kv.Key, out var shown) && Raw(kv.Key, out var raw))
                    _presenter.PreviewOffsets[kv.Key] = raw + kv.Value * 0.01f - shown;
            Changed?.Invoke();
        }

        /// <summary>
        ///     The offsets (avatar cm) that put a tracker at its own offset and, mirroring, its partner at the mirror image of
        ///     where the tracker ends up.
        /// </summary>
        Dictionary<string, Vec3> Offsets(string role, Vec3 cm)
        {
            var res = new Dictionary<string, Vec3> { [role] = cm };
            string partner = Partner(role);
            var cal = _presenter.ShownCalibration;
            if (!_mirror || partner.Length == 0 || cal == null || !TakePresenter.IsBodyTracker(cal, partner) ||
                !Raw(role, out var raw) || !Raw(partner, out var partnerRaw)) return res;
            res[partner] = (Mirrored(raw + cm * 0.01f) - partnerRaw) * 100f;
            return res;
        }

        /// <summary>Where a tracker is shown by the last solve (view space, without previews).</summary>
        bool Shown(string role, out Vec3 pos)
        {
            pos = Vec3.Zero;
            var cal = _presenter.ShownCalibration;
            if (cal == null || !cal.Devices.TryGetValue(role, out var p)) return false;
            pos = cal.Root.Inverse.TransformPoint(p.pos);
            return true;
        }

        /// <summary>Where a tracker was before any offset (view space).</summary>
        bool Raw(string role, out Vec3 pos)
        {
            if (!Shown(role, out pos)) return false;
            if (_presenter.ShownCalibration.Nudges.TryGetValue(role, out var applied)) pos -= applied * 0.01f;
            return true;
        }

        /// <summary>The pick, a drag or an offset changed.</summary>
        public event Action Changed;
    }
}
