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
    /// </summary>
    public sealed class CalibrationTool : IViewportTool
    {
        const float PickRadius = 24f;
        readonly ViewGizmo _gizmo = new();
        readonly TakePresenter _presenter;
        readonly TakeSession _session;
        bool _dragging;
        IDisposable _gesture;

        public CalibrationTool(TakePresenter presenter, TakeSession session)
        {
            _presenter = presenter;
            _session = session;
            session.ClipChanged += () =>
            {
                if (!_dragging) _presenter.DragOffset = Vec3.Zero; // the new solve has the offset in it
            };
        }

        /// <summary>The picked tracker (BodyRole name), or "".</summary>
        public string Selected => _presenter.SelectedTracker;

        public ViewGizmo Gizmo
        {
            get
            {
                if (Selected.Length == 0 || !_presenter.CalibrationDevice(Selected, out var pos)) return null;
                _gizmo.Center = pos;
                _gizmo.Rotate = false;
                var m = _presenter.DragOffset * 100;
                _gizmo.Status = _dragging
                    ? FormattableString.Invariant($"{m.x:+0.0;-0.0;0} / {m.y:+0.0;-0.0;0} / {m.z:+0.0;-0.0;0} cm")
                    : TrackerService.RoleLabel(Selected) + "   Shift slow · Esc done";
                return _gizmo;
            }
        }

        public void DragStarted()
        {
            _dragging = true;
            _presenter.DragOffset = Vec3.Zero;
            _gesture = _session.Gesture();
        }

        public void Dragged(Vec3 move, Quat turn, float degrees)
        {
            if (!_dragging) return;
            _presenter.DragOffset = move;
            Changed?.Invoke();
        }

        public void DragEnded()
        {
            if (!_dragging) return;
            _dragging = false;
            var move = _presenter.DragOffset;
            if (move.SqrLength > 1e-10f) SetNudge(Selected, Nudge(Selected) + move * 100f);
            else _presenter.DragOffset = Vec3.Zero;
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
            _presenter.DragOffset = Vec3.Zero;
            Changed?.Invoke();
        }

        /// <summary>The shown calibration's offset of a tracker (avatar cm).</summary>
        public Vec3 Nudge(string role)
        {
            var cal = _presenter.ShownCalibration;
            var nudges = cal == null ? null : _session.Take?.Edits.NudgesFor(cal.Key);
            return nudges != null && nudges.TryGetValue(role, out var v) ? v : Vec3.Zero;
        }

        /// <summary>Sets the shown calibration's offset of a tracker (avatar cm) as an edit of the take.</summary>
        public void SetNudge(string role, Vec3 cm)
        {
            var cal = _presenter.ShownCalibration;
            if (cal == null || _session.Take == null || role.Length == 0) return;
            string key = cal.Key;
            _session.Edit("trackerNudge", e =>
            {
                if (!e.TrackerNudges.TryGetValue(key, out var own))
                {
                    var inherited = e.NudgesFor(key);
                    e.TrackerNudges[key] = own = inherited == null
                        ? new Dictionary<string, Vec3>()
                        : inherited.ToDictionary(k => k.Key, k => k.Value);
                }

                if (cm.SqrLength < 1e-6f) own.Remove(role);
                else own[role] = cm;
                if (own.Count == 0 && !e.TrackerNudges.ContainsKey(EditList.AnyCalibration)) e.TrackerNudges.Remove(key);
            });
        }

        /// <summary>The pick, a drag or an offset changed.</summary>
        public event Action Changed;
    }
}
