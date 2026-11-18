using System;
using Avalonia.Input;
using VRMR.App.Services;

namespace VRMR.App.Viewport
{
    using Key = Avalonia.Input.Key;

    /// <summary>
    /// Move or turn the whole take in the 3D view: a gizmo on the avatar's hips at the playhead, on world axes.
    /// While dragging the view previews the placement; the solve follows when the drag ends, and the drag is one undo step.
    /// </summary>
    public sealed class PlacementTool : IViewportTool
    {
        readonly PlacementService _placement;
        readonly TakePresenter _presenter;
        readonly TakeSession _session;
        readonly PlaybackClock _clock;
        readonly ViewGizmo _gizmo = new ViewGizmo();
        bool _active, _turn, _dragging;
        Pose _start;
        Vec3 _pivot;
        IDisposable _gesture;

        const string Hint = "W move · E turn · Shift slow · Esc off";

        public PlacementTool(PlacementService placement, TakePresenter presenter, TakeSession session, PlaybackClock clock)
        {
            _placement = placement; _presenter = presenter; _session = session; _clock = clock;
        }

        /// <summary>The tool was switched, or its mode changed.</summary>
        public event Action Changed;

        /// <summary>The gizmo is shown in the view.</summary>
        public bool Active
        {
            get => _active;
            set { if (_active == value) return; _active = value; Changed?.Invoke(); }
        }

        /// <summary>Turn rings instead of move arrows.</summary>
        public bool Turn
        {
            get => _turn;
            set { if (_turn == value) return; _turn = value; Changed?.Invoke(); }
        }

        public ViewGizmo Gizmo
        {
            get
            {
                if (!_active || !_presenter.ShownPose(out var positions, out _, out _)) return null;
                if (!_dragging)
                {
                    _gizmo.Center = positions[_session.Clip.Profile.Human[HumanBones.Hips]];
                    _gizmo.Status = (_turn ? "Turn the take   " : "Move the take   ") + Hint;
                }
                _gizmo.Rotate = _turn;
                return _gizmo;
            }
        }

        public void DragStarted()
        {
            _dragging = true;
            _start = _placement.Current; _pivot = _gizmo.Center;
            _clock.Playing = false;
            _gesture = _session.Gesture();
        }

        public void Dragged(Vec3 move, Quat turn, float degrees)
        {
            if (!_dragging) return;
            var p = _turn ? new Pose(_pivot + turn * (_start.pos - _pivot), (turn * _start.rot).Normalized) : new Pose(_start.pos + move, _start.rot);
            _placement.Set(p, solve: false);
            _gizmo.Center = _turn ? _pivot : _pivot + move;
            _gizmo.Status = _turn
                ? FormattableString.Invariant($"Turned {degrees:0.0}°")
                : FormattableString.Invariant($"Moved {move.x * 100:+0.0;-0.0;0} / {move.y * 100:+0.0;-0.0;0} / {move.z * 100:+0.0;-0.0;0} cm");
        }

        public void DragEnded()
        {
            if (!_dragging) return;
            _dragging = false;
            _gesture?.Dispose(); _gesture = null;
            _session.RequestSolve(); // the solve with the new placement
        }

        public bool KeyDown(Key key)
        {
            switch (key)
            {
                case Key.W: case Key.G: Turn = false; return true;
                case Key.E: case Key.R: Turn = true; return true;
                case Key.Escape: Active = false; return true;
            }
            return false;
        }
    }
}
