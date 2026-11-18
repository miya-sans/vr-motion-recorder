using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace VRMR.UI
{
    /// <summary>
    ///     Blender-style NLA editor for a scene: per actor its tracks (top = highest priority), strips of takes (blue) and
    ///     keyframe actions (orange), transitions on the cuts with their match score, a ruler with the playhead and markers.
    ///     Edits go through the host (BeforeEdit -> change -> Edited) so they can be undone.
    /// </summary>
    public sealed class NlaEditor : DarkControl
    {
        // =====================================================================================================  drag & drop of takes

        public const string TakeFormat = "VRMR.TakePath";

        static readonly Color TakeCol = Color.FromArgb(52, 88, 140), TakeSel = Color.FromArgb(70, 120, 190);
        static readonly Color ActCol = Color.FromArgb(138, 94, 44), ActSel = Color.FromArgb(186, 128, 58);
        static readonly Color MuteCol = Color.FromArgb(62, 64, 72);
        static readonly Color InTakeCol = Color.FromArgb(34, 112, 104), InTakeSel = Color.FromArgb(44, 150, 138);
        static readonly Color InActCol = Color.FromArgb(104, 74, 142), InActSel = Color.FromArgb(140, 100, 190);

        // selection (ids survive undo, which swaps the scene object)
        public readonly HashSet<string> SelStrips = new();
        readonly List<Moving> _moving = new();
        readonly List<Row> _rows = new();

        /// <summary>Key times of an action strip in its own (source) time.</summary>
        public Func<Strip, IEnumerable<double>> KeyTimes = s => Enumerable.Empty<double>();

        public double Playhead;
        public Scene Scene;
        public string SelTrack, SelActor, SelTransition;
        public bool Snap = true;

        // =====================================================================================================  paint

        /// <summary>Display name of a strip's source (take name / action name).</summary>
        public Func<Strip, string> SourceName = s => s.Name;

        /// <summary>Status line drawn in the ruler's corner (e.g. "solving 2 takes…").</summary>
        public string Status = "";

        public double ViewStart = -0.5, ViewEnd = 20;
        Point _down, _last;
        Drag _drag;
        Transition _dragTr;
        double _dragTrDur, _dragCutA, _dragCutB;
        Row _dropRow;
        double _dropT = double.NaN;

        bool _invalid;

        // key retiming
        KeyAction _keyAct;
        Strip _keyStrip;
        double _keyT0, _keyApplied;
        List<double> _keyTimes;
        Actor _moveActor;
        bool _moved;
        int _rowDelta;
        int _scrollY;
        double _snapLine = double.NaN;

        public NlaEditor()
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            AllowDrop = true;
            BackColor = Theme.Panel;
        }

        int HeaderW => Theme.S(176);
        int RulerH => Theme.S(24);
        int ActorH => Theme.S(24);
        int TrackH => Theme.S(38);
        int L => HeaderW;
        int R => Width - Theme.S(8);

        float Fps => Scene?.Fps ?? 60;

        int ContentH => _rows.Count == 0 ? 0 : _rows[_rows.Count - 1].Y + _rows[_rows.Count - 1].H + _scrollY - RulerH;

        /// <summary>The playhead was moved (scene seconds).</summary>
        public event Action<double> Scrub;

        public event Action SelectionChanged;

        /// <summary>About to change the scene (take an undo checkpoint).</summary>
        public event Action<string> BeforeEdit;

        /// <summary>The last BeforeEdit changed nothing (drop its checkpoint).</summary>
        public event Action EditCancelled;

        /// <summary>The scene changed while dragging (re-evaluate, throttled).</summary>
        public event Action Changing;

        /// <summary>An edit finished.</summary>
        public event Action Edited;

        /// <summary>A take was dropped from the list: actor, track (null = a new track), scene time, file path.</summary>
        public event Action<Actor, NlaTrack, double, string> DropTake;

        /// <summary>Double-click on a strip.</summary>
        public event Action<Strip> StripActivated;

        /// <summary>Extra context-menu items for a strip (added by the host / feature modules).</summary>
        public event Action<ContextMenuStrip, Strip> StripMenu;

        /// <summary>The right-click menu of a transition is about to open (features add items).</summary>
        public event Action<ContextMenuStrip, Transition> TransitionMenu;

        float X(double t)
        {
            return (float)(L + (t - ViewStart) / Math.Max(1e-6, ViewEnd - ViewStart) * (R - L));
        }

        double T(float x)
        {
            return ViewStart + (x - L) / (double)Math.Max(1, R - L) * (ViewEnd - ViewStart);
        }

        double PxToT(float px)
        {
            return px / (double)Math.Max(1, R - L) * (ViewEnd - ViewStart);
        }

        void BuildRows()
        {
            _rows.Clear();
            if (Scene == null) return;
            int y = RulerH - _scrollY;
            foreach (var a in Scene.Actors)
            {
                _rows.Add(new Row { Actor = a, Y = y, H = ActorH });
                y += ActorH;
                for (int i = a.Tracks.Count - 1; i >= 0; i--)
                {
                    _rows.Add(new Row { Actor = a, Track = a.Tracks[i], Y = y, H = TrackH });
                    y += TrackH;
                }
            }
        }

        Row RowAt(int y)
        {
            return _rows.FirstOrDefault(r => y >= r.Y && y < r.Y + r.H);
        }

        Row RowOf(NlaTrack t)
        {
            return _rows.FirstOrDefault(r => r.Track == t);
        }

        public void FrameAll()
        {
            if (Scene == null) return;
            double a = Math.Min(Scene.Start, Scene.AllStrips().Select(x => x.Strip.Start).DefaultIfEmpty(0).Min());
            double b = Math.Max(Scene.RangeEnd, 1);
            double pad = (b - a) * 0.04 + 0.2;
            ViewStart = a - pad;
            ViewEnd = b + pad;
            Invalidate();
        }

        RectangleF StripRect(Row r, Strip s)
        {
            return new RectangleF(X(s.Start), r.Y + Theme.S(3), Math.Max(2, X(s.End) - X(s.Start)), r.H - Theme.S(6));
        }

        RectangleF TransRect(Row r, Transition tr, out Strip a, out Strip b)
        {
            a = r.Track.Strips.FirstOrDefault(x => x.Id == tr.From);
            b = r.Track.Strips.FirstOrDefault(x => x.Id == tr.To);
            if (a == null || b == null) return RectangleF.Empty;
            double c = b.Start, h = tr.Duration / 2;
            float x0 = X(c - h), x1 = X(c + h);
            if (x1 - x0 < Theme.S(10))
            {
                float m = (x0 + x1) / 2;
                x0 = m - Theme.S(5);
                x1 = m + Theme.S(5);
            }

            return new RectangleF(x0, r.Y + Theme.S(3), x1 - x0, r.H - Theme.S(6));
        }

        Rectangle ToggleRect(Row r, int i)
        {
            int w = Theme.S(18);
            return new Rectangle(L - Theme.S(8) - (3 - i) * (w + Theme.S(3)), r.Y + (r.H - w) / 2, w, w);
        }

        Rectangle AddRect(Row r)
        {
            int w = Theme.S(18);
            return new Rectangle(L - Theme.S(8) - w, r.Y + (r.H - w) / 2, w, w);
        }

        Hit HitTest(Point p)
        {
            var h = new Hit { T = T(p.X) };
            if (p.Y < RulerH)
            {
                h.Kind = p.X >= L ? HitKind.Ruler : HitKind.None;
                return h;
            }

            var r = RowAt(p.Y);
            if (r == null) return h;
            h.Row = r;
            if (p.X < L)
            {
                if (r.Track == null)
                {
                    h.Kind = AddRect(r).Contains(p) ? HitKind.ActorAdd : HitKind.Actor;
                    return h;
                }

                for (int i = 0; i < 3; i++)
                    if (ToggleRect(r, i).Contains(p))
                    {
                        h.Kind = HitKind.Toggle;
                        h.Toggle = i;
                        return h;
                    }

                h.Kind = HitKind.TrackHeader;
                return h;
            }

            if (r.Track == null)
            {
                h.Kind = HitKind.Actor;
                return h;
            }

            int edge = Theme.S(6);
            // transitions first (they sit on top of the cut)
            foreach (var tr in r.Track.Transitions)
            {
                var rc = TransRect(r, tr, out _, out _);
                if (rc.IsEmpty || p.X < rc.Left - 2 || p.X > rc.Right + 2) continue;
                h.Tr = tr;
                h.Kind = Math.Abs(p.X - rc.Left) <= 3 && rc.Width > Theme.S(14) ? HitKind.TransitionL :
                    Math.Abs(p.X - rc.Right) <= 3 && rc.Width > Theme.S(14) ? HitKind.TransitionR : HitKind.Transition;
                return h;
            }

            foreach (var s in r.Track.Strips)
            {
                var rc = StripRect(r, s);
                if (p.X < rc.Left - 2 || p.X > rc.Right + 2) continue;
                h.Strip = s;
                if (s.Kind == StripKind.Action && p.Y >= rc.Bottom - Theme.S(12))
                {
                    // a key diamond: drag to retime (Alt: it and every key after it)
                    double best = double.NaN;
                    float bd = Theme.S(5);
                    foreach (double kt in KeyTimes(s))
                    {
                        float kx = X(s.ToScene(kt));
                        if (Math.Abs(kx - p.X) <= bd)
                        {
                            bd = Math.Abs(kx - p.X);
                            best = kt;
                        }
                    }

                    if (!double.IsNaN(best))
                    {
                        h.Kind = HitKind.Key;
                        h.KeyT = best;
                        return h;
                    }
                }

                h.Kind = p.X - rc.Left <= edge && rc.Width > edge * 3 ? HitKind.StripIn :
                    rc.Right - p.X <= edge && rc.Width > edge * 3 ? HitKind.StripOut : HitKind.Strip;
                return h;
            }

            h.Kind = HitKind.Lane;
            return h;
        }

        void SelectOnly(Strip s, Row r)
        {
            SelStrips.Clear();
            if (s != null) SelStrips.Add(s.Id);
            SelTransition = null;
            SelTrack = r?.Track?.Id;
            SelActor = r?.Actor?.Id;
            SelectionChanged?.Invoke();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (Scene == null) return;
            BuildRows();
            _down = _last = e.Location;
            _moved = false;
            var h = HitTest(e.Location);
            if (e.Button == MouseButtons.Middle)
            {
                _drag = Drag.Pan;
                return;
            }

            if (e.Button == MouseButtons.Right)
            {
                if (h.Kind == HitKind.Strip || h.Kind == HitKind.StripIn || h.Kind == HitKind.StripOut)
                {
                    if (!SelStrips.Contains(h.Strip.Id)) SelectOnly(h.Strip, h.Row);
                    ShowStripMenu(h, e.Location);
                }
                else if (h.Kind == HitKind.Transition || h.Kind == HitKind.TransitionL || h.Kind == HitKind.TransitionR)
                {
                    SelTransition = h.Tr.Id;
                    SelStrips.Clear();
                    SelectionChanged?.Invoke();
                    ShowTransitionMenu(h, e.Location);
                }
                else if (h.Kind == HitKind.TrackHeader || h.Kind == HitKind.Lane)
                {
                    SelTrack = h.Row.Track.Id;
                    SelActor = h.Row.Actor.Id;
                    SelectionChanged?.Invoke();
                    ShowTrackMenu(h, e.Location);
                }
                else if (h.Kind == HitKind.Actor)
                {
                    SelActor = h.Row.Actor.Id;
                    SelTrack = null;
                    SelStrips.Clear();
                    SelectionChanged?.Invoke();
                    ShowActorMenu(h, e.Location);
                }
                else if (h.Kind == HitKind.Ruler) ShowRulerMenu(h, e.Location);
                else _drag = Drag.Pan;

                Invalidate();
                return;
            }

            if (e.Button != MouseButtons.Left) return;
            switch (h.Kind)
            {
                case HitKind.Ruler:
                    _drag = Drag.Scrub;
                    SetPlayhead(SnapTime(h.T, true));
                    break;
                case HitKind.ActorAdd:
                    Edit("Add track", () =>
                    {
                        var t = SceneOps.AddTrack(h.Row.Actor, h.Row.Actor.Tracks.Count);
                        SelTrack = t.Id;
                    });
                    break;
                case HitKind.Actor:
                    SelActor = h.Row.Actor.Id;
                    SelTrack = null;
                    SelStrips.Clear();
                    SelTransition = null;
                    SelectionChanged?.Invoke();
                    break;
                case HitKind.Toggle:
                {
                    var t = h.Row.Track;
                    Edit(new[] { "Mute track", "Solo track", "Lock track" }[h.Toggle], () =>
                    {
                        if (h.Toggle == 0) t.Mute = !t.Mute;
                        else if (h.Toggle == 1) t.Solo = !t.Solo;
                        else t.Lock = !t.Lock;
                    });
                    break;
                }
                case HitKind.TrackHeader:
                    SelTrack = h.Row.Track.Id;
                    SelActor = h.Row.Actor.Id;
                    SelStrips.Clear();
                    SelTransition = null;
                    SelectionChanged?.Invoke();
                    if (e.Clicks == 2) RenameTrack(h.Row.Track);
                    break;
                case HitKind.Lane:
                    SelStrips.Clear();
                    SelTransition = null;
                    SelTrack = h.Row.Track.Id;
                    SelActor = h.Row.Actor.Id;
                    SelectionChanged?.Invoke();
                    _drag = Drag.Scrub;
                    SetPlayhead(SnapTime(h.T, true));
                    break;
                case HitKind.Transition:
                case HitKind.TransitionL:
                case HitKind.TransitionR:
                {
                    SelTransition = h.Tr.Id;
                    SelStrips.Clear();
                    SelTrack = h.Row.Track.Id;
                    SelActor = h.Row.Actor.Id;
                    SelectionChanged?.Invoke();
                    if (h.Row.Track.Lock) break;
                    _dragTr = h.Tr;
                    _dragTrDur = h.Tr.Duration;
                    var a = h.Row.Track.Strips.FirstOrDefault(x => x.Id == h.Tr.From);
                    var b = h.Row.Track.Strips.FirstOrDefault(x => x.Id == h.Tr.To);
                    if (a == null || b == null) break;
                    _dragCutA = a.SrcOut;
                    _dragCutB = b.SrcIn;
                    _drag = h.Kind == HitKind.Transition ? Drag.Roll : Drag.TransDur;
                    BeforeEdit?.Invoke(h.Kind == HitKind.Transition ? "Move cut" : "Transition length");
                    break;
                }
                case HitKind.Key:
                {
                    if (!SelStrips.Contains(h.Strip.Id)) SelectOnly(h.Strip, h.Row);
                    Playhead = h.Strip.ToScene(h.KeyT);
                    Scrub?.Invoke(Playhead);
                    if (h.Row.Track.Lock) break;
                    _keyStrip = h.Strip;
                    _keyAct = Scene.Action(h.Strip.Source);
                    if (_keyAct == null) break;
                    _keyT0 = h.KeyT;
                    _keyApplied = 0;
                    _keyTimes = (ModifierKeys & Keys.Alt) != 0
                        ? KeyTimes(h.Strip).Where(x => x >= h.KeyT - 1e-6).ToList()
                        : new List<double> { h.KeyT };
                    _drag = Drag.KeyMove;
                    BeforeEdit?.Invoke(_keyTimes.Count > 1 ? "Move keys" : "Move key");
                    break;
                }
                case HitKind.Strip:
                case HitKind.StripIn:
                case HitKind.StripOut:
                {
                    var s = h.Strip;
                    bool shift = (ModifierKeys & Keys.Shift) != 0, ctrl = (ModifierKeys & Keys.Control) != 0;
                    if (shift || ctrl)
                    {
                        if (!SelStrips.Remove(s.Id)) SelStrips.Add(s.Id);
                        SelTransition = null;
                        SelTrack = h.Row.Track.Id;
                        SelActor = h.Row.Actor.Id;
                        SelectionChanged?.Invoke();
                    }
                    else if (!SelStrips.Contains(s.Id)) SelectOnly(s, h.Row);
                    else
                    {
                        SelTrack = h.Row.Track.Id;
                        SelActor = h.Row.Actor.Id;
                        SelTransition = null;
                        SelectionChanged?.Invoke();
                    }

                    if (e.Clicks == 2)
                    {
                        StripActivated?.Invoke(s);
                        break;
                    }

                    if (h.Row.Track.Lock || shift || ctrl) break;
                    if (h.Kind == HitKind.Strip)
                    {
                        bool slip = (ModifierKeys & Keys.Alt) != 0;
                        BeginMove(slip);
                        _drag = slip ? Drag.Slip : Drag.Move;
                        BeforeEdit?.Invoke(slip ? "Slip strip" : "Move strips");
                    }
                    else
                    {
                        BeginMove(false, s);
                        _drag = h.Kind == HitKind.StripIn ? Drag.TrimIn : Drag.TrimOut;
                        BeforeEdit?.Invoke("Trim strip");
                    }

                    break;
                }
            }

            Invalidate();
        }

        void BeginMove(bool slip, Strip only = null)
        {
            _moving.Clear();
            _rowDelta = 0;
            _invalid = false;
            foreach (var x in Scene.AllStrips())
            {
                if (only != null ? x.Strip != only : !SelStrips.Contains(x.Strip.Id)) continue;
                _moving.Add(new Moving
                {
                    Strip = x.Strip, From = x.Track, Start = x.Strip.Start, SrcIn = x.Strip.SrcIn,
                    SrcOut = x.Strip.SrcOut, RowIndex = _rows.FindIndex(r => r.Track == x.Track)
                });
                _moveActor = x.Actor;
            }

            // moving strips of more than one actor: no track changes
            if (_moving.Select(m => Scene.AllStrips().First(x => x.Strip == m.Strip).Actor).Distinct().Count() > 1)
                _moveActor = null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (Scene == null) return;
            if (_drag == Drag.None)
            {
                UpdateCursor(e.Location);
                return;
            }

            if (Math.Abs(e.X - _down.X) + Math.Abs(e.Y - _down.Y) > 3) _moved = true;
            double dt = PxToT(e.X - _down.X);
            switch (_drag)
            {
                case Drag.Pan:
                {
                    double d = PxToT(e.X - _last.X);
                    ViewStart -= d;
                    ViewEnd -= d;
                    _scrollY = Math.Max(0,
                        Math.Min(Math.Max(0, ContentH - (Height - RulerH)), _scrollY - (e.Y - _last.Y)));
                    break;
                }
                case Drag.Scrub: SetPlayhead(SnapTime(T(e.X), true)); break;
                case Drag.KeyMove:
                {
                    if (!_moved || _keyAct == null) break;
                    double dsrc = PxToT(e.X - _down.X) * _keyStrip.Speed;
                    double target = Math.Round((_keyT0 + dsrc) * Fps) / Fps - _keyT0; // keys land on frames
                    double delta = target - _keyApplied;
                    if (Math.Abs(delta) < 1e-9) break;
                    KeyframeOps.MoveKeys(_keyAct, _keyTimes, delta);
                    _keyTimes = _keyTimes.Select(x => x + delta).ToList();
                    _keyApplied = target;
                    Playhead = _keyStrip.ToScene(_keyT0 + target);
                    Scrub?.Invoke(Playhead);
                    Changing?.Invoke();
                    break;
                }
                case Drag.Move: DoMove(dt, e.Y); break;
                case Drag.Slip:
                {
                    if (!_moved) break;
                    foreach (var m in _moving)
                    {
                        SceneOps.SourceLimits(Scene, m.Strip, out double lo, out double hi);
                        double d = -dt * m.Strip.Speed;
                        d = MathX.Clamp(d, lo - m.SrcIn, hi - m.SrcOut);
                        m.Strip.SrcIn = m.SrcIn + d;
                        m.Strip.SrcOut = m.SrcOut + d;
                    }

                    Changing?.Invoke();
                    break;
                }
                case Drag.TrimIn:
                case Drag.TrimOut:
                    if (_moved) DoTrim(e.X);
                    break;
                case Drag.TransDur:
                {
                    var r = _rows.FirstOrDefault(x => x.Track != null && x.Track.Transitions.Contains(_dragTr));
                    var b = r?.Track.Strips.FirstOrDefault(x => x.Id == _dragTr.To);
                    var a = r?.Track.Strips.FirstOrDefault(x => x.Id == _dragTr.From);
                    if (a == null || b == null) break;
                    double half = Math.Abs(T(e.X) - b.Start);
                    _dragTr.Duration = MathX.Clamp(Quant(half * 2), 0, Math.Min(a.Length, b.Length));
                    _dragTr.Score = -1;
                    Changing?.Invoke();
                    break;
                }
                case Drag.Roll:
                {
                    if (!_moved) break;
                    var r = _rows.FirstOrDefault(x => x.Track != null && x.Track.Transitions.Contains(_dragTr));
                    var a = r?.Track.Strips.FirstOrDefault(x => x.Id == _dragTr.From);
                    if (a == null) break;
                    double d = Quant(dt);
                    SceneOps.SetCut(Scene, r.Track, _dragTr, _dragCutA + d * a.Speed, _dragCutB + d * a.Speed);
                    Changing?.Invoke();
                    break;
                }
            }

            _last = e.Location;
            Invalidate();
        }

        void DoMove(double dt, int mouseY)
        {
            if (!_moved || _moving.Count == 0) return;
            // vertical: whole rows of the same actor
            if (_moveActor != null)
            {
                var r = RowAt(mouseY);
                int idx0 = _rows.FindIndex(x => x.Track == _moving[0].From);
                int idx = r != null && r.Track != null && r.Actor == _moveActor ? _rows.IndexOf(r) : -1;
                if (idx >= 0) _rowDelta = idx - idx0;
            }

            // snap the moving block's start or end
            double a0 = _moving.Min(m => m.Start),
                b0 = _moving.Max(m => m.Start + (m.SrcOut - m.SrcIn) / m.Strip.Speed);
            double d = dt;
            _snapLine = double.NaN;
            if (Snap && (ModifierKeys & Keys.Control) == 0)
            {
                double best = PxToT(Theme.S(8));
                double sa = SnapTarget(a0 + d, best), sb = SnapTarget(b0 + d, best);
                if (!double.IsNaN(sa) && (double.IsNaN(sb) || Math.Abs(sa - (a0 + d)) <= Math.Abs(sb - (b0 + d))))
                {
                    d = sa - a0;
                    _snapLine = sa;
                }
                else if (!double.IsNaN(sb))
                {
                    d = sb - b0;
                    _snapLine = sb;
                }
                else d = Quant(a0 + d) - a0;
            }

            _invalid = false;
            foreach (var m in _moving)
            {
                m.Strip.Start = m.Start + d;
                var target = TargetTrack(m);
                if (target == null || target.Lock)
                {
                    _invalid = true;
                    continue;
                }

                if (!SceneOps.IsFree(target, m.Strip.Start, m.Strip.End, _moving.Select(x => x.Strip).ToArray()))
                    _invalid = true;
            }

            Changing?.Invoke();
        }

        NlaTrack TargetTrack(Moving m)
        {
            if (_rowDelta == 0 || _moveActor == null) return m.From;
            int i = m.RowIndex + _rowDelta;
            if (i < 0 || i >= _rows.Count) return null;
            var r = _rows[i];
            return r.Actor == _moveActor ? r.Track : null;
        }

        void DoTrim(int mouseX)
        {
            var m = _moving.FirstOrDefault();
            if (m == null) return;
            var s = m.Strip;
            SceneOps.SourceLimits(Scene, s, out double lo, out double hi);
            double t = T(mouseX);
            double best = PxToT(Theme.S(8));
            _snapLine = double.NaN;
            if (Snap && (ModifierKeys & Keys.Control) == 0)
            {
                double st = SnapTarget(t, best, s);
                if (!double.IsNaN(st))
                {
                    t = st;
                    _snapLine = st;
                }
                else t = Quant(t);
            }

            double minLen = 1.0 / Fps;
            // neighbours on the track limit the trim (strips don't overlap)
            var tr = Scene.AllStrips().First(x => x.Strip == s).Track;
            if (_drag == Drag.TrimIn)
            {
                double prevEnd = tr.Strips.Where(x => x != s && x.End <= m.Start + 1e-6).Select(x => x.End)
                    .DefaultIfEmpty(double.NegativeInfinity).Max();
                double end = m.Start + (m.SrcOut - m.SrcIn) / s.Speed;
                t = Math.Max(t, prevEnd);
                double srcIn = m.SrcIn + (t - m.Start) * s.Speed;
                srcIn = MathX.Clamp(srcIn, Math.Max(lo, -1e9), m.SrcOut - minLen * s.Speed);
                s.SrcIn = srcIn;
                s.Start = end - (s.SrcOut - s.SrcIn) / s.Speed;
            }
            else
            {
                double nextStart = tr.Strips
                    .Where(x => x != s && x.Start >= m.Start + (m.SrcOut - m.SrcIn) / s.Speed - 1e-6)
                    .Select(x => x.Start).DefaultIfEmpty(double.PositiveInfinity).Min();
                t = Math.Min(t, nextStart);
                double srcOut = m.SrcIn + (t - m.Start) * s.Speed;
                s.SrcOut = MathX.Clamp(srcOut, m.SrcIn + minLen * s.Speed, Math.Min(hi, 1e9));
            }

            Changing?.Invoke();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            var d = _drag;
            _drag = Drag.None;
            _snapLine = double.NaN;
            if (Scene == null) return;
            switch (d)
            {
                case Drag.Move:
                    if (!_moved)
                    {
                        EditCancelled?.Invoke();
                        break;
                    }

                    if (_invalid)
                    {
                        foreach (var m in _moving) m.Strip.Start = m.Start;
                        EditCancelled?.Invoke();
                        Changing?.Invoke();
                        break;
                    }

                    foreach (var m in _moving)
                    {
                        var to = TargetTrack(m);
                        if (to != null && to != m.From) SceneOps.Move(m.From, to, m.Strip, m.Strip.Start);
                    }

                    foreach (var t in _moving.Select(m => TargetTrack(m) ?? m.From).Concat(_moving.Select(m => m.From))
                                 .Distinct()) SceneOps.Normalize(t);
                    Edited?.Invoke();
                    break;
                case Drag.KeyMove:
                    if (!_moved || Math.Abs(_keyApplied) < 1e-9)
                    {
                        EditCancelled?.Invoke();
                        break;
                    }

                    Edited?.Invoke();
                    break;
                case Drag.Slip:
                case Drag.TrimIn:
                case Drag.TrimOut:
                case Drag.TransDur:
                case Drag.Roll:
                    if (!_moved && d != Drag.TransDur)
                    {
                        EditCancelled?.Invoke();
                        break;
                    }

                    foreach (var a in Scene.Actors)
                    foreach (var t in a.Tracks)
                        SceneOps.Normalize(t);
                    Edited?.Invoke();
                    break;
            }

            _moving.Clear();
            _rowDelta = 0;
            _invalid = false;
            Invalidate();
        }

        void UpdateCursor(Point p)
        {
            BuildRows();
            var h = HitTest(p);
            Cursor = h.Kind == HitKind.StripIn || h.Kind == HitKind.StripOut || h.Kind == HitKind.TransitionL ||
                     h.Kind == HitKind.TransitionR || h.Kind == HitKind.Key ? Cursors.SizeWE
                : h.Kind == HitKind.Transition ? Cursors.VSplit
                : h.Kind == HitKind.Toggle || h.Kind == HitKind.ActorAdd ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if ((ModifierKeys & (Keys.Control | Keys.Shift)) != 0 || e.X < L)
            {
                _scrollY = Math.Max(0, Math.Min(Math.Max(0, ContentH - (Height - RulerH)), _scrollY - e.Delta / 3));
                Invalidate();
                return;
            }

            double t = T(e.X);
            double k = Math.Pow(0.85, e.Delta / 120.0);
            double ns = t - (t - ViewStart) * k, ne = t + (ViewEnd - t) * k;
            if (ne - ns < 0.1 || ne - ns > 36000) return;
            ViewStart = ns;
            ViewEnd = ne;
            Invalidate();
        }

        // =====================================================================================================  snapping

        double Quant(double t)
        {
            return Math.Round(t * Fps) / Fps;
        }

        /// <summary>The nearest snap point (strip edges, playhead, scene start/end, markers) within maxDist, else NaN.</summary>
        double SnapTarget(double t, double maxDist, Strip exclude = null)
        {
            double best = double.NaN, bd = maxDist;

            void Try(double c)
            {
                double d = Math.Abs(c - t);
                if (d < bd)
                {
                    bd = d;
                    best = c;
                }
            }

            Try(Playhead);
            Try(Scene.Start);
            if (Scene.End > Scene.Start) Try(Scene.End);
            foreach (var m in Scene.Markers) Try(m.T);
            foreach (var x in Scene.AllStrips())
            {
                if (x.Strip == exclude || _moving.Any(m => m.Strip == x.Strip)) continue;
                Try(x.Strip.Start);
                Try(x.Strip.End);
            }

            return best;
        }

        double SnapTime(double t, bool toFrames)
        {
            return Snap && toFrames ? Quant(t) : t;
        }

        void SetPlayhead(double t)
        {
            Playhead = t;
            Scrub?.Invoke(t);
            Invalidate();
        }

        // =====================================================================================================  keys / commands

        protected override bool IsInputKey(Keys keyData)
        {
            var k = keyData & Keys.KeyCode;
            return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (Scene == null) return;
            switch (e.KeyCode)
            {
                case Keys.Delete:
                case Keys.Back: DeleteSelected(); break;
                case Keys.K: SplitAtPlayhead(); break;
                case Keys.T: TransitionSelected(); break;
                case Keys.M:
                    if (e.Shift) AddMarker(Playhead);
                    else MuteSelected();
                    break;
                case Keys.D:
                    if (e.Shift || e.Control) DuplicateSelected();
                    else return;
                    break;
                case Keys.F:
                case Keys.Home when e.Control: FrameAll(); break;
                case Keys.A:
                    if (e.Control)
                    {
                        SelStrips.Clear();
                        foreach (var x in Scene.AllStrips()) SelStrips.Add(x.Strip.Id);
                        SelectionChanged?.Invoke();
                        Invalidate();
                    }
                    else return;

                    break;
                default: return;
            }

            e.Handled = true;
        }

        /// <summary>Runs an edit as one undo step.</summary>
        public void Edit(string label, Action change)
        {
            if (Scene == null) return;
            BeforeEdit?.Invoke(label);
            change();
            foreach (var a in Scene.Actors)
            foreach (var t in a.Tracks)
                SceneOps.Normalize(t);
            Edited?.Invoke();
            Invalidate();
        }

        IEnumerable<(Actor Actor, NlaTrack Track, Strip Strip)> Selected()
        {
            return Scene.AllStrips().Where(x => SelStrips.Contains(x.Strip.Id)).ToList();
        }

        public void DeleteSelected()
        {
            if (SelTransition != null)
            {
                var x = Scene.FindTransition(SelTransition);
                if (x.Track != null && !x.Track.Lock)
                    Edit("Remove transition", () => x.Track.Transitions.Remove(x.Transition));
                SelTransition = null;
                SelectionChanged?.Invoke();
                return;
            }

            var sel = Selected().Where(x => !x.Track.Lock).ToList();
            if (sel.Count == 0) return;
            Edit(sel.Count == 1 ? "Delete strip" : "Delete strips", () =>
            {
                foreach (var x in sel) SceneOps.DeleteStrip(Scene, x.Track, x.Strip);
            });
            SelStrips.Clear();
            SelectionChanged?.Invoke();
        }

        /// <summary>Cuts the selected strips (or the strips under the playhead on the selected track) at the playhead.</summary>
        public void SplitAtPlayhead()
        {
            var targets = Selected().Where(x => x.Strip.Start < Playhead && x.Strip.End > Playhead).ToList();
            if (targets.Count == 0 && SelTrack != null)
            {
                var (a, t) = Scene.FindTrack(SelTrack);
                if (t != null)
                    targets = t.Strips.Where(s => s.Start < Playhead && s.End > Playhead).Select(s => (a, t, s))
                        .ToList();
            }

            targets = targets.Where(x => !x.Track.Lock).ToList();
            if (targets.Count == 0) return;
            var right = new List<string>();
            Edit("Cut at playhead", () =>
            {
                foreach (var x in targets)
                {
                    var r = SceneOps.Split(Scene, x.Track, x.Strip, Playhead);
                    if (r != null) right.Add(r.Id);
                }
            });
            SelStrips.Clear();
            foreach (string id in right) SelStrips.Add(id);
            SelectionChanged?.Invoke();
        }

        /// <summary>A transition between two selected strips of a track (or the selected strip and the next one).</summary>
        public void TransitionSelected()
        {
            var sel = Selected().ToList();
            NlaTrack track = null;
            Strip a = null, b = null;
            if (sel.Count == 2 && sel[0].Track == sel[1].Track)
            {
                track = sel[0].Track;
                a = sel[0].Strip;
                b = sel[1].Strip;
            }
            else if (sel.Count == 1)
            {
                track = sel[0].Track;
                a = sel[0].Strip;
                b = track.Strips.Where(x => x.Start >= a.End - SceneOps.Adjacent && x != a).OrderBy(x => x.Start)
                    .FirstOrDefault();
            }

            if (track == null || a == null || b == null || track.Lock) return;
            Transition made = null;
            Edit("Add transition", () => made = SceneOps.AddTransition(track, a, b));
            if (made != null)
            {
                SelTransition = made.Id;
                SelStrips.Clear();
                SelectionChanged?.Invoke();
            }
        }

        public void MuteSelected()
        {
            var sel = Selected().ToList();
            if (sel.Count == 0) return;
            bool mute = !sel.All(x => x.Strip.Mute);
            Edit(mute ? "Mute strips" : "Unmute strips", () =>
            {
                foreach (var x in sel) x.Strip.Mute = mute;
            });
        }

        public void DuplicateSelected()
        {
            var sel = Selected().ToList();
            if (sel.Count == 0) return;
            var made = new List<string>();
            Edit("Duplicate strips", () =>
            {
                foreach (var x in sel) made.Add(SceneOps.Duplicate(Scene, x.Actor, x.Track, x.Strip).Id);
            });
            SelStrips.Clear();
            foreach (string id in made) SelStrips.Add(id);
            SelectionChanged?.Invoke();
        }

        public void AddMarker(double t)
        {
            Edit("Add marker",
                () => Scene.Markers.Add(new SceneMarker { T = Quant(t), Name = "M" + (Scene.Markers.Count + 1) }));
        }

        void RenameTrack(NlaTrack t)
        {
            string n = Prompt("Track name:", t.Name);
            if (!string.IsNullOrWhiteSpace(n) && n != t.Name) Edit("Rename track", () => t.Name = n.Trim());
        }

        public static string Prompt(string label, string value)
        {
            using (var f = new Form
                   {
                       Text = "VR Motion Recorder", FormBorderStyle = FormBorderStyle.FixedDialog,
                       StartPosition = FormStartPosition.CenterParent, Width = Theme.S(360), Height = Theme.S(140),
                       MinimizeBox = false, MaximizeBox = false, BackColor = Theme.Panel, ForeColor = Theme.Text,
                       Font = Theme.UI
                   })
            {
                var l = new Label
                    { Text = label, Left = Theme.S(12), Top = Theme.S(12), AutoSize = true, ForeColor = Theme.TextDim };
                var b = new TextBox { Text = value, Left = Theme.S(12), Top = Theme.S(34), Width = Theme.S(320) };
                Theme.StyleInput(b);
                var ok = new Button
                {
                    Text = "OK", DialogResult = DialogResult.OK, Left = Theme.S(172), Top = Theme.S(66),
                    Width = Theme.S(76)
                };
                var cancel = new Button
                {
                    Text = "Cancel", DialogResult = DialogResult.Cancel, Left = Theme.S(256), Top = Theme.S(66),
                    Width = Theme.S(76)
                };
                f.Controls.AddRange(l, b, ok, cancel);
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                return f.ShowDialog() == DialogResult.OK ? b.Text : null;
            }
        }

        // =====================================================================================================  menus

        ToolStripMenuItem Item(string text, Action a, string keys = null, bool enabled = true)
        {
            var i = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = keys, Enabled = enabled };
            i.Click += (o, e) => a();
            return i;
        }

        ContextMenuStrip NewMenu()
        {
            return new ContextMenuStrip
            {
                BackColor = Theme.Panel2, ForeColor = Theme.Text, Font = Theme.UI, ShowImageMargin = false,
                Renderer = new ToolStripProfessionalRenderer(new DarkColors())
            };
        }

        void ShowStripMenu(Hit h, Point p)
        {
            var m = NewMenu();
            var sel = Selected().ToList();
            m.Items.Add(Item("Cut at playhead", SplitAtPlayhead, "K"));
            m.Items.Add(Item("Transition to next strip", TransitionSelected, "T"));
            m.Items.Add(Item("Duplicate", DuplicateSelected, "Shift+D"));
            m.Items.Add(Item(sel.All(x => x.Strip.Mute) ? "Unmute" : "Mute", MuteSelected, "M"));
            m.Items.Add(new ToolStripSeparator());
            if (h.Row.Track.Kind == TrackKind.Input)
            {
                var inputs = new ToolStripMenuItem("Inputs (what this strip drives)");
                foreach (var pr in InputMask.Presets)
                {
                    var preset = pr;
                    var it = Item(preset.Name, () => Edit("Inputs: " + preset.Name, () =>
                    {
                        foreach (var x in sel) x.Strip.Inputs = preset.Mask;
                    }));
                    it.Checked = sel.Count > 0 && sel.All(x => x.Strip.Inputs == preset.Mask);
                    inputs.DropDownItems.Add(it);
                }

                m.Items.Add(inputs);
                if (h.Strip.Kind == StripKind.Take)
                {
                    var place = new ToolStripMenuItem("Placement");
                    foreach ((string name, var al) in new[]
                             {
                                 ("Match the headset at the strip start", InputAlign.Head),
                                 ("As recorded (same spot in the playspace)", InputAlign.None)
                             })
                    {
                        var a2 = al;
                        var it = Item(name, () => Edit("Input placement", () =>
                        {
                            foreach (var x in sel) x.Strip.InputAlign = a2;
                        }));
                        it.Checked = sel.All(x => x.Strip.InputAlign == a2);
                        place.DropDownItems.Add(it);
                    }

                    m.Items.Add(place);
                }
            }
            else
            {
                var mask = new ToolStripMenuItem("Body parts");
                foreach (var pr in BodyMask.Presets)
                {
                    var preset = pr;
                    var it = Item(preset.Name, () => Edit("Body parts: " + preset.Name, () =>
                    {
                        foreach (var x in sel) x.Strip.Mask = preset.Mask;
                    }));
                    it.Checked = sel.Count > 0 && sel.All(x => x.Strip.Mask == preset.Mask);
                    mask.DropDownItems.Add(it);
                }

                m.Items.Add(mask);
            }

            var blend = new ToolStripMenuItem("Blend");
            foreach (BlendMode bm in Enum.GetValues(typeof(BlendMode)))
            {
                var b = bm;
                var it = Item(b == BlendMode.Replace ? "Replace" : "Add (on top of the tracks below)", () =>
                    Edit("Blend " + b, () =>
                    {
                        foreach (var x in sel) x.Strip.Blend = b;
                    }));
                it.Checked = sel.Count > 0 && sel.All(x => x.Strip.Blend == b);
                blend.DropDownItems.Add(it);
            }

            m.Items.Add(blend);
            StripMenu?.Invoke(m, h.Strip);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item("Delete (the take is kept)", DeleteSelected, "Del"));
            m.Show(this, p);
        }

        void ShowTransitionMenu(Hit h, Point p)
        {
            var m = NewMenu();
            var tr = h.Tr;
            var track = h.Row.Track;
            foreach (TransitionAlign al in Enum.GetValues(typeof(TransitionAlign)))
            {
                var a = al;
                var it = Item(
                    a == TransitionAlign.None ? "Don't move the next strip" :
                    a == TransitionAlign.Com ? "Align centre of mass and facing" :
                    a == TransitionAlign.Hips ? "Align hips and facing" : "Align feet and facing",
                    () => Edit("Transition alignment", () => tr.Align = a));
                it.Checked = tr.Align == a;
                m.Items.Add(it);
            }

            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item("Remove transition", () => Edit("Remove transition", () => track.Transitions.Remove(tr))));
            TransitionMenu?.Invoke(m, tr);
            m.Show(this, p);
        }

        void ShowTrackMenu(Hit h, Point p)
        {
            var m = NewMenu();
            var a = h.Row.Actor;
            var t = h.Row.Track;
            m.Items.Add(Item("Rename…", () => RenameTrack(t)));
            m.Items.Add(Item("Add track above",
                () => Edit("Add track", () => SelTrack = SceneOps.AddTrack(a, a.Tracks.IndexOf(t) + 1).Id)));
            m.Items.Add(Item("Add track below",
                () => Edit("Add track", () => SelTrack = SceneOps.AddTrack(a, a.Tracks.IndexOf(t)).Id)));
            m.Items.Add(Item("Add input track above (devices, face, fingers → IK)", () => Edit("Add input track", () =>
            {
                var nt = SceneOps.AddTrack(a, a.Tracks.IndexOf(t) + 1, "Inputs");
                nt.Kind = TrackKind.Input;
                SelTrack = nt.Id;
            })));
            m.Items.Add(Item("Move up", () => Edit("Move track", () =>
            {
                int i = a.Tracks.IndexOf(t);
                if (i < a.Tracks.Count - 1)
                {
                    a.Tracks.RemoveAt(i);
                    a.Tracks.Insert(i + 1, t);
                }
            }), null, a.Tracks.IndexOf(t) < a.Tracks.Count - 1));
            m.Items.Add(Item("Move down", () => Edit("Move track", () =>
            {
                int i = a.Tracks.IndexOf(t);
                if (i > 0)
                {
                    a.Tracks.RemoveAt(i);
                    a.Tracks.Insert(i - 1, t);
                }
            }), null, a.Tracks.IndexOf(t) > 0));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item("Cut strips at playhead", () =>
            {
                SelTrack = t.Id;
                SelStrips.Clear();
                SplitAtPlayhead();
            }, "K"));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item("Delete track", () => Edit("Delete track", () => a.Tracks.Remove(t)), null,
                a.Tracks.Count > 1 || t.Strips.Count == 0));
            m.Show(this, p);
        }

        void ShowActorMenu(Hit h, Point p)
        {
            var m = NewMenu();
            var a = h.Row.Actor;
            m.Items.Add(Item("Rename…", () =>
            {
                string n = Prompt("Actor name:", a.Name);
                if (!string.IsNullOrWhiteSpace(n)) Edit("Rename actor", () => a.Name = n.Trim());
            }));
            m.Items.Add(Item("Add track",
                () => Edit("Add track", () => SelTrack = SceneOps.AddTrack(a, a.Tracks.Count).Id)));
            m.Items.Add(Item("Add input track (devices, face, fingers → IK)", () => Edit("Add input track", () =>
            {
                var nt = SceneOps.AddTrack(a, a.Tracks.Count, "Inputs");
                nt.Kind = TrackKind.Input;
                SelTrack = nt.Id;
            })));
            m.Items.Add(Item(a.Hidden ? "Show in the 3D view" : "Hide in the 3D view",
                () => Edit("Hide actor", () => a.Hidden = !a.Hidden)));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item("Add actor", () => Edit("Add actor", () =>
            {
                var na = new Actor { Id = Scene.NewId("a"), Name = "Actor " + (Scene.Actors.Count + 1) };
                na.Tracks.Add(new NlaTrack { Id = Scene.NewId("k"), Name = "Base" });
                Scene.Actors.Add(na);
                SelActor = na.Id;
            })));
            m.Items.Add(Item("Delete actor", () => Edit("Delete actor", () => Scene.Actors.Remove(a)), null,
                Scene.Actors.Count > 1));
            m.Show(this, p);
        }

        void ShowRulerMenu(Hit h, Point p)
        {
            var m = NewMenu();
            var near = Scene.Markers.OrderBy(x => Math.Abs(x.T - h.T))
                .FirstOrDefault(x => Math.Abs(X(x.T) - p.X) < Theme.S(8));
            m.Items.Add(Item("Add marker here", () => AddMarker(h.T), "Shift+M"));
            if (near != null)
            {
                m.Items.Add(Item($"Rename marker \"{near.Name}\"…", () =>
                {
                    string n = Prompt("Marker name:", near.Name);
                    if (!string.IsNullOrWhiteSpace(n)) Edit("Rename marker", () => near.Name = n.Trim());
                }));
                m.Items.Add(Item($"Delete marker \"{near.Name}\"",
                    () => Edit("Delete marker", () => Scene.Markers.Remove(near))));
            }

            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item("Scene starts here",
                () => Edit("Scene start", () => Scene.Start = Math.Min(Quant(h.T), Scene.RangeEnd - 0.1))));
            m.Items.Add(Item("Scene ends here",
                () => Edit("Scene end", () => Scene.End = Math.Max(Quant(h.T), Scene.Start + 0.1))));
            m.Items.Add(Item("Scene end: last strip", () => Edit("Scene end", () => Scene.End = -1)));
            m.Show(this, p);
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            if (Scene == null || !e.Data.GetDataPresent(TakeFormat))
            {
                e.Effect = DragDropEffects.None;
                return;
            }

            e.Effect = DragDropEffects.Copy;
            var p = PointToClient(new Point(e.X, e.Y));
            BuildRows();
            _dropRow = RowAt(p.Y);
            _dropT = Math.Max(0, SnapTime(T(p.X), true));
            double sn = SnapTarget(_dropT, PxToT(Theme.S(8)));
            if (!double.IsNaN(sn)) _dropT = sn;
            Invalidate();
        }

        protected override void OnDragLeave(EventArgs e)
        {
            _dropRow = null;
            _dropT = double.NaN;
            Invalidate();
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            string path = e.Data.GetData(TakeFormat) as string;
            var r = _dropRow;
            double t = _dropT;
            _dropRow = null;
            _dropT = double.NaN;
            Invalidate();
            if (Scene == null || string.IsNullOrEmpty(path) || double.IsNaN(t)) return;
            var actor = r?.Actor ?? Scene.Actors.FirstOrDefault();
            DropTake?.Invoke(actor, r?.Track, t, path);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Panel);
            if (Scene == null)
            {
                TextRenderer.DrawText(g, "No scene open", Theme.UI, ClientRectangle, Theme.TextFaint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            BuildRows();
            int top = RulerH, bottom = Height;
            using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, L, top, R - L, bottom - top);
            g.SetClip(new Rectangle(L, 0, R - L + 1, Height));
            // ruler grid
            double span = ViewEnd - ViewStart;
            double[] steps = { 1 / 60.0, 1 / 30.0, 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
            double step = steps.FirstOrDefault(s => (R - L) / (span / s) > Theme.S(64));
            if (step == 0) step = 600;
            using (var p = new Pen(Color.FromArgb(40, 255, 255, 255)))
                for (double t = Math.Floor(ViewStart / step) * step; t <= ViewEnd; t += step)
                {
                    float x = X(t);
                    g.DrawLine(p, x, top - Theme.S(6), x, bottom);
                    TextRenderer.DrawText(g, Theme.Time(t, step < 1), Theme.Small, new Point((int)x + 3, Theme.S(4)),
                        Theme.TextFaint);
                }

            // rows
            foreach (var r in _rows)
            {
                if (r.Y > bottom || r.Y + r.H < top) continue;
                if (r.Track == null)
                {
                    using (var b = new SolidBrush(Color.FromArgb(30, 34, 42))) g.FillRectangle(b, L, r.Y, R - L, r.H);
                    continue;
                }

                bool selT = r.Track.Id == SelTrack;
                using (var b = new SolidBrush(selT ? Color.FromArgb(28, 32, 40) :
                           _rows.IndexOf(r) % 2 == 0 ? Theme.Bg : Color.FromArgb(22, 24, 30)))
                    g.FillRectangle(b, L, r.Y, R - L, r.H);
                using (var p = new Pen(Color.FromArgb(36, 255, 255, 255)))
                    g.DrawLine(p, L, r.Y + r.H - 1, R, r.Y + r.H - 1);
                foreach (var s in r.Track.Strips) DrawStrip(g, r, s);
                foreach (var tr in r.Track.Transitions) DrawTransition(g, r, tr);
                if (r.Track.Mute)
                    using (var b = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(40, 0, 0, 0),
                               Color.Transparent))
                        g.FillRectangle(b, L, r.Y, R - L, r.H);
            }

            // outside the scene range
            using (var dark = new SolidBrush(Color.FromArgb(110, 8, 8, 10)))
            {
                float xs = X(Scene.Start), xe = X(Scene.RangeEnd);
                if (xs > L) g.FillRectangle(dark, L, top, xs - L, bottom - top);
                if (xe < R) g.FillRectangle(dark, xe, top, R - xe, bottom - top);
            }

            // drop indicator
            if (!double.IsNaN(_dropT))
                using (var p = new Pen(Theme.Accent, Theme.Sf(2)))
                {
                    float x = X(_dropT);
                    int y0 = _dropRow?.Y ?? top, y1 = _dropRow != null ? _dropRow.Y + _dropRow.H : bottom;
                    g.DrawLine(p, x, y0, x, y1);
                }

            if (!double.IsNaN(_snapLine))
                using (var p = new Pen(Color.FromArgb(160, Theme.Warn)) { DashStyle = DashStyle.Dash })
                    g.DrawLine(p, X(_snapLine), top, X(_snapLine), bottom);
            // markers
            foreach (var m in Scene.Markers)
            {
                float x = X(m.T);
                using (var p = new Pen(Color.FromArgb(90, Theme.Warn)) { DashStyle = DashStyle.Dot })
                    g.DrawLine(p, x, top, x, bottom);
                using (var b = new SolidBrush(Theme.Warn))
                    g.FillPolygon(b,
                        new[]
                        {
                            new PointF(x - Theme.S(4), top - Theme.S(8)), new PointF(x + Theme.S(4), top - Theme.S(8)),
                            new PointF(x, top)
                        });
                TextRenderer.DrawText(g, m.Name, Theme.Small, new Point((int)x + Theme.S(5), top - Theme.S(14)),
                    Theme.Warn);
            }

            // playhead
            using (var p = new Pen(Theme.Rec, Theme.Sf(1.5f))) g.DrawLine(p, X(Playhead), 0, X(Playhead), bottom);
            using (var b = new SolidBrush(Theme.Rec))
                g.FillRectangle(b, X(Playhead) - Theme.S(4), 0, Theme.S(8), Theme.S(6));
            g.ResetClip();
            // header column
            using (var b = new SolidBrush(Theme.Panel)) g.FillRectangle(b, 0, top, L, bottom - top);
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, L - 1, top, L - 1, bottom);
            foreach (var r in _rows)
            {
                if (r.Y > bottom || r.Y + r.H < top) continue;
                if (r.Track == null)
                {
                    bool selA = r.Actor.Id == SelActor && SelTrack == null && SelStrips.Count == 0;
                    TextRenderer.DrawText(g, (r.Actor.Hidden ? "◌ " : "● ") + r.Actor.Name, Theme.UIBold,
                        new Rectangle(Theme.S(8), r.Y, L - Theme.S(36), r.H), selA ? Theme.Accent : Theme.Text,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                        TextFormatFlags.NoPrefix);
                    var ar = AddRect(r);
                    Theme.StrokeRound(g, Theme.Border, ar, Theme.Sf(4));
                    TextRenderer.DrawText(g, "+", Theme.UIBold, ar, Theme.TextDim,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    continue;
                }

                bool sel = r.Track.Id == SelTrack;
                if (sel)
                    using (var b = new SolidBrush(Theme.Panel2))
                        g.FillRectangle(b, 0, r.Y, L - 1, r.H);
                if (r.Track.Kind == TrackKind.Input)
                    using (var b = new SolidBrush(Color.FromArgb(70, 44, 150, 138)))
                        g.FillRectangle(b, Theme.S(4), r.Y + Theme.S(4), Theme.S(4), r.H - Theme.S(8));
                TextRenderer.DrawText(g, r.Track.Name, Theme.UI,
                    new Rectangle(Theme.S(16), r.Y, L - Theme.S(16) - 3 * Theme.S(21) - Theme.S(8),
                        r.H / (r.Track.Kind == TrackKind.Input ? 2 : 1) +
                        (r.Track.Kind == TrackKind.Input ? Theme.S(6) : 0)),
                    r.Track.Mute ? Theme.TextFaint : Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPrefix);
                if (r.Track.Kind == TrackKind.Input)
                    TextRenderer.DrawText(g, "inputs → IK", Theme.Small,
                        new Rectangle(Theme.S(16), r.Y + r.H / 2, L - Theme.S(16) - 3 * Theme.S(21) - Theme.S(8),
                            r.H / 2 - Theme.S(2)), Color.FromArgb(120, 200, 190),
                        TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
                string[] tg = { "M", "S", "L" };
                bool[] on = { r.Track.Mute, r.Track.Solo, r.Track.Lock };
                string[] tip = { "Mute", "Solo", "Lock" };
                for (int i = 0; i < 3; i++)
                {
                    var tr = ToggleRect(r, i);
                    Theme.FillRound(g,
                        on[i]
                            ? i == 0 ? Color.FromArgb(120, 60, 60) :
                            i == 1 ? Color.FromArgb(120, 110, 40) : Theme.AccentDim
                            : Theme.Panel2, tr, Theme.Sf(4));
                    TextRenderer.DrawText(g, tg[i], Theme.Small, tr, on[i] ? Theme.Text : Theme.TextFaint,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }

                using (var p = new Pen(Color.FromArgb(36, 255, 255, 255)))
                    g.DrawLine(p, 0, r.Y + r.H - 1, L, r.Y + r.H - 1);
            }

            // ruler band over everything
            using (var b = new SolidBrush(Theme.Panel)) g.FillRectangle(b, 0, 0, L, RulerH);
            TextRenderer.DrawText(g, Theme.Time(Playhead) + $"  f{Math.Round(Playhead * Fps)}", Theme.Mono,
                new Rectangle(Theme.S(8), 0, L - Theme.S(8), RulerH), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, 0, RulerH - 1, Width, RulerH - 1);
            if (!string.IsNullOrEmpty(Status))
                TextRenderer.DrawText(g, Status, Theme.Small, new Rectangle(L, 0, R - L - Theme.S(4), RulerH),
                    Theme.Warn, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            if (_rows.Count > 0 && !Scene.AllStrips().Any())
                TextRenderer.DrawText(g, "Drag takes here from the list, or use + Take / + Keyframes", Theme.UI,
                    new Rectangle(L, RulerH, R - L, Math.Min(Height - RulerH, ContentH + Theme.S(40))), Theme.TextFaint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom);
        }

        void DrawStrip(Graphics g, Row r, Strip s)
        {
            var rc = StripRect(r, s);
            if (rc.Right < L || rc.Left > R) return;
            bool sel = SelStrips.Contains(s.Id);
            bool moving = _drag == Drag.Move && _moving.Any(m => m.Strip == s);
            if (moving && _rowDelta != 0)
            {
                // drawn on its target row
                var m = _moving.First(x => x.Strip == s);
                var to = TargetTrack(m);
                var tr = to != null ? RowOf(to) : null;
                if (tr != null) rc = StripRect(tr, s);
            }

            bool input = r.Track.Kind == TrackKind.Input;
            var fill = s.Mute ? MuteCol :
                input ? s.Kind == StripKind.Take ? sel ? InTakeSel : InTakeCol : sel ? InActSel : InActCol :
                s.Kind == StripKind.Take ? sel ? TakeSel : TakeCol :
                sel ? ActSel : ActCol;
            Theme.FillRound(g, fill, rc, Theme.Sf(4));
            if (s.Blend == BlendMode.Add)
                using (var hb = new HatchBrush(HatchStyle.DarkUpwardDiagonal, Color.FromArgb(40, 255, 255, 255),
                           Color.Transparent))
                    g.FillRectangle(hb, rc);
            // influence ramps
            if (s.BlendIn > 1e-6)
                using (var b = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                    g.FillPolygon(b,
                        new[]
                        {
                            new PointF(rc.Left, rc.Top), new PointF(X(s.Start + s.BlendIn), rc.Top),
                            new PointF(rc.Left, rc.Bottom)
                        });
            if (s.BlendOut > 1e-6)
                using (var b = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                    g.FillPolygon(b,
                        new[]
                        {
                            new PointF(rc.Right, rc.Top), new PointF(X(s.End - s.BlendOut), rc.Top),
                            new PointF(rc.Right, rc.Bottom)
                        });
            Theme.StrokeRound(g,
                moving && _invalid ? Theme.Rec :
                sel ? Color.FromArgb(230, 235, 245) : Theme.Mix(fill, Color.Black, 0.35f), rc, Theme.Sf(4),
                sel ? 1.5f : 1f);
            // text
            var tr2 = Rectangle.Round(new RectangleF(rc.X + Theme.S(6), rc.Y + Theme.S(1),
                Math.Max(0, rc.Width - Theme.S(10)), rc.Height / 2f));
            if (tr2.Width > Theme.S(14))
            {
                string name = string.IsNullOrEmpty(s.Name) ? SourceName(s) : s.Name;
                TextRenderer.DrawText(g, name, Theme.Small, tr2, s.Mute ? Theme.TextFaint : Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPrefix);
                var tags = new List<string>();
                if (input) tags.Add("inputs: " + InputMask.Describe(s.Inputs));
                else if (s.Mask != BodyPart.All) tags.Add(BodyMask.Describe(s.Mask));
                if (s.Blend == BlendMode.Add) tags.Add("Add");
                if (Math.Abs(s.Speed - 1) > 1e-6) tags.Add($"×{s.Speed:0.##}");
                if (s.Influence < 0.999f) tags.Add($"{s.Influence * 100:0}%");
                if (s.Modifiers.Count > 0)
                    tags.Add(string.Join(", ",
                        s.Modifiers.Where(m => m.Enabled).Select(m =>
                            SceneModifiers.Strip.TryGetValue(m.Type, out var md) ? md.Label : m.Type)));
                if (tags.Count > 0)
                    TextRenderer.DrawText(g, string.Join(" · ", tags), Theme.Small,
                        new Rectangle(tr2.X, tr2.Bottom - Theme.S(1), tr2.Width, tr2.Height),
                        Color.FromArgb(190, 210, 220, 235),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                        TextFormatFlags.NoPrefix);
            }

            // keyframes (actions)
            if (s.Kind == StripKind.Action)
            {
                float ky = rc.Bottom - Theme.S(6), kr = Theme.Sf(3.5f);
                using (var b = new SolidBrush(Color.FromArgb(240, 235, 220, 200)))
                    foreach (double kt in KeyTimes(s))
                    {
                        double t = s.ToScene(kt);
                        if (t < s.Start - 1e-6 || t > s.End + 1e-6) continue;
                        float x = X(t);
                        g.FillPolygon(b,
                            new[]
                            {
                                new PointF(x, ky - kr), new PointF(x + kr, ky), new PointF(x, ky + kr),
                                new PointF(x - kr, ky)
                            });
                    }
            }
        }

        void DrawTransition(Graphics g, Row r, Transition tr)
        {
            var rc = TransRect(r, tr, out var a, out var b);
            if (rc.IsEmpty || rc.Right < L || rc.Left > R) return;
            bool sel = tr.Id == SelTransition;
            using (var br = new SolidBrush(Color.FromArgb(sel ? 120 : 80, 240, 240, 250))) g.FillRectangle(br, rc);
            using (var p = new Pen(sel ? Color.White : Color.FromArgb(160, 230, 230, 240), sel ? 1.6f : 1f))
            {
                g.DrawLine(p, rc.Left, rc.Bottom, rc.Right, rc.Top);
                g.DrawLine(p, rc.Left, rc.Top, rc.Right, rc.Bottom);
                g.DrawRectangle(p, rc.X, rc.Y, rc.Width, rc.Height);
            }

            // match score badge
            if (tr.Score >= 0)
            {
                string txt = $"{tr.Score * 100:0}";
                var col = tr.Score >= 0.8 ? Theme.Ok : tr.Score >= 0.5 ? Theme.Warn : Theme.Rec;
                var sz = TextRenderer.MeasureText(txt, Theme.Small);
                var br = new RectangleF((rc.Left + rc.Right) / 2 - sz.Width / 2f - Theme.S(3), rc.Top - Theme.S(1),
                    sz.Width + Theme.S(6), Theme.S(14));
                Theme.FillRound(g, Theme.Mix(col, Color.Black, 0.45f), br, Theme.Sf(3));
                TextRenderer.DrawText(g, txt, Theme.Small, Rectangle.Round(br), Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        // =====================================================================================================  rows

        sealed class Row
        {
            public Actor Actor;
            public NlaTrack Track;
            public int Y, H;
        }

        // =====================================================================================================  hit test

        enum HitKind
        {
            None, Ruler, Actor, ActorAdd, TrackHeader, Toggle, Lane, Strip, StripIn, StripOut, Transition, TransitionL,
            TransitionR, Key
        }

        struct Hit
        {
            public HitKind Kind;
            public Row Row;
            public Strip Strip;
            public Transition Tr;
            public int Toggle;
            public double T;
            public double KeyT;
        }

        // =====================================================================================================  mouse

        enum Drag { None, Scrub, Move, TrimIn, TrimOut, Slip, TransDur, Roll, Pan, KeyMove }

        // move state
        sealed class Moving
        {
            public NlaTrack From;
            public int RowIndex;
            public double Start, SrcIn, SrcOut;
            public Strip Strip;
        }

        sealed class DarkColors : ProfessionalColorTable
        {
            public override Color MenuItemSelected => Theme.AccentDim;
            public override Color MenuItemBorder => Theme.Accent;
            public override Color ToolStripDropDownBackground => Theme.Panel2;
            public override Color ImageMarginGradientBegin => Theme.Panel2;
            public override Color ImageMarginGradientMiddle => Theme.Panel2;
            public override Color ImageMarginGradientEnd => Theme.Panel2;
            public override Color MenuBorder => Theme.Border;
            public override Color SeparatorDark => Theme.Border;
            public override Color MenuItemSelectedGradientBegin => Theme.AccentDim;
            public override Color MenuItemSelectedGradientEnd => Theme.AccentDim;
        }
    }
}
