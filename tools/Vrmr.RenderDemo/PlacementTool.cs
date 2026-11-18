using System;
using System.Numerics;
using Avalonia.Input;
using VRMR.Render;

namespace VRMR.RenderDemo
{
    /// <summary>
    /// A tool on the render API: a move / turn gizmo on the avatar's hips that edits the demo's placement.
    /// W moves, E turns, Shift slows the drag down, Esc puts the tool away.
    /// </summary>
    internal sealed class PlacementTool : IViewportTool
    {
        readonly Gizmo _gizmo = new Gizmo();
        readonly TakeSource _source;
        readonly Action _changed, _close;
        readonly char[] _status = new char[64];
        GizmoPart _hot;
        bool _drag;
        Pose _start;
        Vec3 _pivot;
        int _statusLength;

        /// <summary>changed: the placement moved, show the frame again. close: Esc was pressed.</summary>
        public PlacementTool(TakeSource source, Action changed, Action close) { _source = source; _changed = changed; _close = close; }

        bool Place(ViewportControl view)
        {
            var frame = view.Frame;
            if (frame.Actors.Count == 0) return false;
            var actor = frame.Actors[frame.MainActor];
            if (!actor.Profile.Has(HumanBones.Hips)) return false;
            if (!_drag) _gizmo.Center = actor.NodePos[actor.Profile.Human[HumanBones.Hips]];
            return true;
        }

        public bool PointerPressed(ViewportControl view, in ViewPointer e)
        {
            if (e.Button != MouseButton.Left || !Place(view)) return false;
            var part = _gizmo.Hit(view.Camera, e.Position);
            if (part == GizmoPart.None) return false;
            _drag = true; _start = _source.Placement; _pivot = _gizmo.Center;
            _gizmo.Begin(view.Camera, part, e.Position);
            return true;
        }

        public void PointerMoved(ViewportControl view, in ViewPointer e)
        {
            if (!_drag)
            {
                if (!Place(view)) return;
                var hot = _gizmo.Hit(view.Camera, e.Position);
                if (hot != _hot) { _hot = hot; view.RequestFrame(); }
                return;
            }
            _gizmo.Delta(view.Camera, e.Position, e.Shift, out Vec3 move, out Quat turn);
            _source.Placement = _gizmo.Rotate
                ? new Pose(_pivot + turn * (_start.pos - _pivot), (turn * _start.rot).Normalized)
                : new Pose(_start.pos + move, _start.rot);
            _gizmo.Center = _gizmo.Rotate ? _pivot : _pivot + move;
            _statusLength = 0;
            if (_gizmo.Rotate) _status.AsSpan().TryWrite($"turned {_gizmo.Degrees:0.0}°", out _statusLength);
            else _status.AsSpan().TryWrite($"moved {move.x * 100:0.0} / {move.y * 100:0.0} / {move.z * 100:0.0} cm", out _statusLength);
            _changed();
        }

        public void PointerReleased(ViewportControl view, in ViewPointer e)
        {
            _drag = false; _statusLength = 0;
            _gizmo.End();
        }

        public bool KeyDown(ViewportControl view, Key key, KeyModifiers modifiers)
        {
            switch (key)
            {
                case Key.W: _gizmo.Rotate = false; return true;
                case Key.E: _gizmo.Rotate = true; return true;
                case Key.Escape: _close(); return true;
            }
            return false;
        }

        public void Draw(ViewportControl view, OverlayList overlay)
        {
            if (!Place(view)) return;
            var theme = view.ViewTheme ?? Defaults;
            _gizmo.Draw(view.Camera, overlay, _drag ? _gizmo.Active : _hot, theme);
            ReadOnlySpan<char> text = _statusLength > 0 ? _status.AsSpan(0, _statusLength) : (_gizmo.Rotate ? "Turn   (W move, E turn, Shift slow, Esc off)" : "Move   (W move, E turn, Shift slow, Esc off)");
            overlay.Text(_gizmo.Center, new Vector2(14, 46), text, theme.Text);
        }

        static readonly ViewportTheme Defaults = new ViewportTheme();
    }
}
