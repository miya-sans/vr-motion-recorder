using System;
using System.Collections.Generic;

namespace VRMR.App.Services
{
    /// <summary>
    ///     Undo and redo over whole-state snapshots (strings). A burst of changes to the same thing within a short time is
    ///     one step, and everything committed inside a <see cref="Gesture" /> (a drag) is one step however long it takes.
    /// </summary>
    public sealed class EditHistory
    {
        /// <summary>Steps kept.</summary>
        public const int Limit = 200;

        /// <summary>Seconds between changes to the same thing that still count as one step.</summary>
        public const double MergeWindow = 0.4;

        readonly Func<double> _now;
        readonly List<string> _undo = new(), _redo = new();
        int _gestureDepth;
        bool _gesturePushed;
        string _state, _lastKey = "";
        double _stateTime;

        public EditHistory(Func<double> now = null) { _now = now ?? (() => Clock.Now); }

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        /// <summary>Number of undo steps (for tests and the tooltip).</summary>
        public int UndoCount => _undo.Count;

        /// <summary>Raised when <see cref="CanUndo" /> or <see cref="CanRedo" /> may have changed.</summary>
        public event Action Changed;

        /// <summary>Starts a fresh history at this state (a take was opened or reverted).</summary>
        public void Reset(string state)
        {
            _undo.Clear();
            _redo.Clear();
            _state = state;
            _stateTime = 0;
            _lastKey = "";
            _gestureDepth = 0;
            _gesturePushed = false;
            Changed?.Invoke();
        }

        /// <summary>
        ///     The state as it is now becomes an undo step, if it differs from the last one. <paramref name="key" /> names
        ///     what changed, so a slider drag or repeated arrow presses merge into one step.
        /// </summary>
        public void Commit(string state, string key)
        {
            if (state == null || state == _state) return;
            double t = _now();
            bool merge = _gestureDepth > 0
                ? _gesturePushed
                : _undo.Count > 0 && _redo.Count == 0 && key == _lastKey && t - _stateTime < MergeWindow;
            if (!merge && _state != null)
            {
                _undo.Add(_state);
                if (_undo.Count > Limit) _undo.RemoveAt(0);
            }

            if (_gestureDepth > 0) _gesturePushed = true;
            _redo.Clear();
            _state = state;
            _stateTime = t;
            _lastKey = key ?? "";
            Changed?.Invoke();
        }

        /// <summary>Everything committed until the result is disposed is one undo step (one step per drag).</summary>
        public IDisposable Gesture()
        {
            if (_gestureDepth++ == 0) _gesturePushed = false;
            return new GestureScope(this);
        }

        void EndGesture()
        {
            if (_gestureDepth == 0) return;
            if (--_gestureDepth == 0)
            {
                _gesturePushed = false;
                _stateTime = 0;
                _lastKey = "";
            } // the next change is its own step
        }

        /// <summary>
        ///     Steps back: returns the state to restore, or null when there is nothing to undo. Pass the state as it
        ///     is now so an edit that was not committed yet becomes its own step first.
        /// </summary>
        public string Undo(string current)
        {
            if (current != null && current != _state)
            {
                _stateTime = 0;
                Commit(current, "*");
            }

            if (_undo.Count == 0) return null;
            string prev = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(_state);
            _state = prev;
            _stateTime = 0;
            _lastKey = "";
            Changed?.Invoke();
            return prev;
        }

        /// <summary>Steps forward again: returns the state to restore, or null.</summary>
        public string Redo()
        {
            if (_redo.Count == 0) return null;
            string next = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(_state);
            _state = next;
            _stateTime = 0;
            _lastKey = "";
            Changed?.Invoke();
            return next;
        }

        sealed class GestureScope : IDisposable
        {
            EditHistory _owner;
            public GestureScope(EditHistory owner) { _owner = owner; }

            public void Dispose()
            {
                _owner?.EndGesture();
                _owner = null;
            }
        }
    }
}
