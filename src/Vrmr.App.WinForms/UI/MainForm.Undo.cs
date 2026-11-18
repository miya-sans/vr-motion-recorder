using System;
using System.Collections.Generic;
using System.Linq;

namespace VRMR.UI
{
    public sealed partial class MainForm
    {
        // Takes mode undo / redo (Ctrl+Z, Ctrl+Shift+Z / Ctrl+Y, the transport's ↶ ↷), like the scene editor's: snapshots of
        // the take's edits (EditList JSON: trims, cuts, placement, calibration, body source, tracking-loss marks… and the
        // tracker roles) taken whenever an edit is committed (MarkDirty). A burst of changes to the same thing (a slider
        // drag) is one step. The name and notes boxes keep their own text undo. Opening a take starts a fresh history.
        readonly List<string> _clipUndo = new List<string>(), _clipRedo = new List<string>();
        string _clipState, _clipLastKeys = "";
        double _clipStateT;
        bool _restoringClip;
        DarkButton _undoBtn, _redoBtn;
        const int ClipUndoLimit = 200;
        const double ClipMergeWindow = 0.4; // s between changes to the same fields that still count as one step

        Dictionary<string, object> ClipStateJson()
        {
            if (_edit == null) return null;
            return new Dictionary<string, object> { ["edits"] = _edit.Edits.ToJson(), ["roles"] = _edit.Streams.Select(s => (object)s.BodyRole).ToList() };
        }

        string ClipSnapshot() { var d = ClipStateJson(); return d == null ? null : Json.Serialize(d); }

        /// <summary>A take was opened (or reverted): its history starts here.</summary>
        void ResetClipHistory()
        {
            _clipUndo.Clear(); _clipRedo.Clear();
            _clipState = ClipSnapshot(); _clipStateT = 0; _clipLastKeys = "";
            SyncUndoButtons();
        }

        /// <summary>MarkDirty's last step: the take's edits as they are now become an undo step (if anything changed).</summary>
        void CommitClipEdit()
        {
            if (_edit == null || _restoringClip) return;
            string now = ClipSnapshot();
            if (now == _clipState) return;
            string keys = ChangedKeys(_clipState, now);
            double t = Clock.Now;
            bool merge = _clipUndo.Count > 0 && _clipRedo.Count == 0 && keys == _clipLastKeys && t - _clipStateT < ClipMergeWindow;
            if (!merge && _clipState != null) { _clipUndo.Add(_clipState); if (_clipUndo.Count > ClipUndoLimit) _clipUndo.RemoveAt(0); }
            _clipRedo.Clear();
            _clipState = now; _clipStateT = t; _clipLastKeys = keys;
            SyncUndoButtons();
        }

        /// <summary>Which top-level edit fields differ (to tell a slider drag from separate edits).</summary>
        static string ChangedKeys(string a, string b)
        {
            if (a == null || b == null) return "*";
            var da = Json.ParseObject(a); var db = Json.ParseObject(b);
            var ea = da.GetObj("edits") ?? new Dictionary<string, object>(); var eb = db.GetObj("edits") ?? new Dictionary<string, object>();
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var k in ea.Keys.Union(eb.Keys))
                if (Json.Serialize(ea.TryGetValue(k, out var x) ? x : null) != Json.Serialize(eb.TryGetValue(k, out var y) ? y : null)) keys.Add(k);
            if (Json.Serialize(da.GetList("roles")) != Json.Serialize(db.GetList("roles"))) keys.Add("roles");
            return string.Join(",", keys);
        }

        void ClipUndo()
        {
            if (_edit == null) return;
            CommitClipEdit(); // an edit not committed yet becomes its own step first
            if (_clipUndo.Count == 0) return;
            var prev = _clipUndo[_clipUndo.Count - 1]; _clipUndo.RemoveAt(_clipUndo.Count - 1);
            _clipRedo.Add(_clipState);
            ApplyClipSnapshot(prev);
        }

        void ClipRedo()
        {
            if (_edit == null || _clipRedo.Count == 0) return;
            var next = _clipRedo[_clipRedo.Count - 1]; _clipRedo.RemoveAt(_clipRedo.Count - 1);
            _clipUndo.Add(_clipState);
            ApplyClipSnapshot(next);
        }

        void ApplyClipSnapshot(string json)
        {
            var d = Json.ParseObject(json);
            _restoringClip = true;
            try
            {
                _edit.Edits = EditList.FromJson(d.GetObj("edits"));
                var roles = d.GetList("roles");
                if (roles != null)
                    for (int i = 0; i < _edit.Streams.Count && i < roles.Count; i++)
                    {
                        string v = roles[i] as string ?? "";
                        if (_edit.Streams[i].BodyRole != v) _edit.Streams[i].BodyRole = v; // only real changes (no empty "bodyRole" keys)
                    }
                TrackerGaps.Invalidate(_edit);
                _clipState = json; _clipStateT = 0; _clipLastKeys = "";
                _timeline.SelA = _timeline.SelB = -1;
                UpdateClipUI();
                SyncPlacementUI();
                SyncGapsUI();
                MarkDirty(true); // re-solve (not a new undo step: _restoringClip)
                ShowFrameAt(Playhead);
                _view.Invalidate();
            }
            finally { _restoringClip = false; }
            SyncUndoButtons();
        }

        void SyncUndoButtons()
        {
            if (_undoBtn == null) return;
            bool u = _edit != null && (_clipUndo.Count > 0 || (_clipState != null && ClipSnapshot() != _clipState)), r = _edit != null && _clipRedo.Count > 0;
            if (_undoBtn.Enabled != u) _undoBtn.Enabled = u;
            if (_redoBtn.Enabled != r) _redoBtn.Enabled = r;
        }
    }
}
