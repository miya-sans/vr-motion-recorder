using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VRMR.App.Services;
using VRMR.App.ViewModels;

namespace VRMR.App.Controls
{
    /// <summary>Theme brushes for custom-drawn controls, looked up by token key and cached until the theme changes.</summary>
    public sealed class ThemeBrushes
    {
        readonly Control _owner;
        readonly Dictionary<string, IBrush> _cache = new Dictionary<string, IBrush>();

        public ThemeBrushes(Control owner)
        {
            _owner = owner;
            owner.ActualThemeVariantChanged += (o, e) => { _cache.Clear(); owner.InvalidateVisual(); };
        }

        public IBrush this[string key]
        {
            get
            {
                if (_cache.TryGetValue(key, out var b)) return b;
                b = _owner.TryFindResource(key, _owner.ActualThemeVariant, out var v) && v is IBrush found ? found : Brushes.Gray;
                _cache[key] = b;
                return b;
            }
        }

        /// <summary>A token colour with another opacity.</summary>
        public IBrush Faded(string key, double opacity)
        {
            string k = key + "@" + opacity.ToString(CultureInfo.InvariantCulture);
            if (_cache.TryGetValue(k, out var b)) return b;
            b = this[key] is ISolidColorBrush s ? new SolidColorBrush(s.Color, opacity) : this[key];
            _cache[k] = b;
            return b;
        }
    }

    /// <summary>
    /// The take timeline in source time: the whole recording with its trims (darkened), cuts (hatched), a selection,
    /// the playhead, lanes showing where each kind of data exists, tracker losses, face activity and flags for the
    /// moments worth knowing about. Custom-drawn; it redraws only when its model says something changed.
    /// </summary>
    public sealed class TimelineControl : Control
    {
        public static readonly StyledProperty<TimelineViewModel> ModelProperty = AvaloniaProperty.Register<TimelineControl, TimelineViewModel>(nameof(Model));

        const double LabelWidth = 96, RightPad = 10, RulerHeight = 22, LaneHeight = 14, HandleGrab = 6;
        static readonly Typeface Face = new Typeface("Inter");
        static readonly double[] TickSteps = { 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };

        enum Drag { None, Scrub, Select, TrimIn, TrimOut, Pan }
        readonly ThemeBrushes _brushes;
        Drag _drag;
        double _lastX, _selectStart;
        TimelineFlag _hoverFlag;

        static TimelineControl()
        {
            ModelProperty.Changed.AddClassHandler<TimelineControl>((c, e) => c.OnModelChanged(e.OldValue as TimelineViewModel, e.NewValue as TimelineViewModel));
        }

        public TimelineControl()
        {
            _brushes = new ThemeBrushes(this);
            Focusable = true;
            ClipToBounds = true;
            MinHeight = 96;
        }

        public TimelineViewModel Model { get => GetValue(ModelProperty); set => SetValue(ModelProperty, value); }

        /// <summary>Number of times the control drew (tests and the idle measurement).</summary>
        public long FramesDrawn { get; private set; }

        void OnModelChanged(TimelineViewModel old, TimelineViewModel now)
        {
            if (old != null) old.Changed -= InvalidateVisual;
            if (now != null) now.Changed += InvalidateVisual;
            InvalidateVisual();
        }

        double Left => LabelWidth;
        double Right => Math.Max(LabelWidth + 10, Bounds.Width - RightPad);
        double X(double t) { var m = Model; return Left + (t - m.ViewStart) / Math.Max(1e-6, m.ViewEnd - m.ViewStart) * (Right - Left); }
        double T(double x) { var m = Model; return m.ViewStart + (x - Left) / Math.Max(1, Right - Left) * (m.ViewEnd - m.ViewStart); }
        double LossLaneY(TimelineViewModel m) => RulerHeight + 6 + (m.Data?.Lanes.Count ?? 3) * LaneHeight;

        // ------------------------------------------------------------------------------------------ input

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var m = Model;
            if (m?.Take == null) return;
            Focus();
            var p = e.GetPosition(this);
            var props = e.GetCurrentPoint(this).Properties;
            _lastX = p.X;
            if (props.IsMiddleButtonPressed) _drag = Drag.Pan;
            else if (props.IsRightButtonPressed)
            {
                if (!m.RestoreCutAt(T(p.X))) _drag = Drag.Pan; // right-click a cut restores it; elsewhere it pans
            }
            else if (props.IsLeftButtonPressed)
            {
                if (Math.Abs(p.X - X(m.TrimIn)) < HandleGrab) { _drag = Drag.TrimIn; m.BeginTrimDrag(); }
                else if (Math.Abs(p.X - X(m.TrimOut)) < HandleGrab) { _drag = Drag.TrimOut; m.BeginTrimDrag(); }
                else if ((e.KeyModifiers & KeyModifiers.Shift) != 0 || p.Y > RulerHeight + 4)
                {
                    _drag = Drag.Select; _selectStart = T(p.X);
                    m.SetSelection(_selectStart, _selectStart);
                    if (p.Y <= RulerHeight + 4) m.Scrub(_selectStart);
                }
                else { _drag = Drag.Scrub; m.ClearSelection(); m.Scrub(T(p.X)); }
            }
            if (_drag != Drag.None) e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var m = Model;
            if (m?.Take == null) return;
            var p = e.GetPosition(this);
            if (_drag == Drag.None)
            {
                bool onHandle = Math.Abs(p.X - X(m.TrimIn)) < HandleGrab || Math.Abs(p.X - X(m.TrimOut)) < HandleGrab;
                Cursor = onHandle ? new Cursor(StandardCursorType.SizeWestEast) : Cursor.Default;
                var flag = FlagAt(m, p);
                if (flag != _hoverFlag) { _hoverFlag = flag; ToolTip.SetTip(this, flag?.Label); ToolTip.SetIsOpen(this, flag != null); }
                return;
            }
            double t = T(p.X);
            switch (_drag)
            {
                case Drag.Scrub: m.Scrub(t); break;
                case Drag.Select: m.SetSelection(_selectStart, t); break;
                case Drag.TrimIn: m.DragTrimIn(t); break;
                case Drag.TrimOut: m.DragTrimOut(t); break;
                case Drag.Pan: m.Pan((p.X - _lastX) / Math.Max(1, Right - Left) * (m.ViewEnd - m.ViewStart)); break;
            }
            _lastX = p.X;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            var m = Model;
            var drag = _drag; _drag = Drag.None;
            e.Pointer.Capture(null);
            if (m?.Take == null) return;
            var p = e.GetPosition(this);
            if (drag == Drag.TrimIn || drag == Drag.TrimOut) m.EndTrimDrag();
            else if (drag == Drag.Select && !m.HasSelection)
            {
                // a click on a shaded span (a tracking loss, VRChat's calibration mode) selects it, ready for Cut or Keep
                double t = T(p.X), lossY = LossLaneY(m);
                var span = m.SpanAt(t, p.Y >= lossY && p.Y < lossY + LaneHeight);
                if (span != null) { m.SetSelection(span[0], span[1]); m.Scrub(span[0]); }
                else { m.ClearSelection(); m.Scrub(t); }
            }
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            if (_drag == Drag.TrimIn || _drag == Drag.TrimOut) Model?.EndTrimDrag();
            _drag = Drag.None;
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            var m = Model;
            if (m?.Take == null) return;
            m.Zoom(T(e.GetPosition(this).X), Math.Pow(0.85, e.Delta.Y));
            e.Handled = true;
        }

        TimelineFlag FlagAt(TimelineViewModel m, Point p)
        {
            var d = m.Data;
            if (d == null || p.Y < RulerHeight - 2 || p.Y > RulerHeight + 12) return null;
            foreach (var f in d.Flags) if (p.X >= X(f.Time) - 2 && p.X <= X(f.Time) + 9) return f;
            return null;
        }

        // ------------------------------------------------------------------------------------------ drawing

        string FlagBrush(FlagKind k) => k switch
        {
            FlagKind.Calibration or FlagKind.VrcCalibration => "SuccessBrush",
            FlagKind.VrcSetting or FlagKind.VrcJump => "WarningBrush",
            FlagKind.PlayspaceJump => "RecordBrush",
            FlagKind.LegacyOrigin => "AccentBrush",
            _ => "TextSecondaryBrush",
        };

        string LaneBrush(LaneKind k) => k == LaneKind.VrcIk ? "SuccessBrush" : k == LaneKind.SteamVr ? "AccentBrush" : "VrcPoseBrush";

        void Text(DrawingContext c, string text, double x, double y, string brush, double size = 11)
        {
            c.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, _brushes[brush]), new Point(x, y));
        }

        public override void Render(DrawingContext c)
        {
            FramesDrawn++;
            var bounds = new Rect(Bounds.Size);
            c.FillRectangle(_brushes["SurfaceBrush"], bounds);
            var m = Model;
            if (m?.Take == null) return;
            double top = RulerHeight, bottom = bounds.Height - 4, left = Left, right = Right, dur = m.Duration;
            c.FillRectangle(_brushes["BgBrush"], new Rect(left, top, right - left, Math.Max(0, bottom - top)));
            var data = m.Data;

            using (c.PushClip(new Rect(left, 0, right - left + 1, bounds.Height)))
            {
                // ruler
                double span = m.ViewEnd - m.ViewStart, step = 600;
                foreach (double s in TickSteps) if ((right - left) / (span / s) > 70) { step = s; break; }
                var tick = new Pen(_brushes["BorderBrush"], 1);
                for (double t = Math.Floor(m.ViewStart / step) * step; t <= m.ViewEnd; t += step)
                {
                    double x = Math.Round(X(t)) + 0.5;
                    c.DrawLine(tick, new Point(x, top - 5), new Point(x, bottom));
                    Text(c, TimeText.Format(t, step < 1), x + 3, 4, "TextFaintBrush");
                }
                // coverage lanes
                double y = top + 6;
                if (data != null)
                {
                    foreach (var lane in data.Lanes)
                    {
                        var b = _brushes.Faded(LaneBrush(lane.Kind), 0.6);
                        foreach (var sp in lane.Spans) c.FillRectangle(b, new Rect(X(sp[0]), y + 2, Math.Max(1, X(sp[1]) - X(sp[0])), LaneHeight - 4), 2);
                        y += LaneHeight;
                    }
                    // tracking loss per tracker: repaired in red, kept as recorded in grey
                    foreach (var g in data.Gaps)
                    {
                        double x0 = X(Math.Max(0, g.A)), x1 = X(double.IsPositiveInfinity(g.B) ? dur : g.B);
                        c.FillRectangle(g.Ignored ? _brushes.Faded("TextSecondaryBrush", 0.35) : _brushes.Faded("RecordBrush", 0.75), new Rect(x0, y + 2, Math.Max(2, x1 - x0), LaneHeight - 4), 2);
                    }
                    y += LaneHeight;
                    // face activity
                    if (data.FaceActivity != null)
                    {
                        double h = Math.Max(16, bottom - y - 4);
                        var b = _brushes.Faded("WarningBrush", 0.6);
                        for (int i = 0; i < data.FaceActivity.Length; i++)
                        {
                            double x0 = X(i * data.FaceStep), x1 = X((i + 1) * data.FaceStep);
                            if (x1 < left || x0 > right) continue;
                            double hh = data.FaceActivity[i] * h;
                            if (hh > 0.5) c.FillRectangle(b, new Rect(x0, bottom - hh, Math.Max(1, x1 - x0 - 0.5), hh));
                        }
                    }
                }
                // trims and cuts
                var dark = _brushes["ScrimBrush"];
                double xin = X(m.TrimIn), xout = X(m.TrimOut);
                if (xin > X(0)) c.FillRectangle(dark, new Rect(X(0), top, xin - X(0), bottom - top));
                if (X(dur) > xout) c.FillRectangle(dark, new Rect(xout, top, X(dur) - xout, bottom - top));
                var cutFill = _brushes.Faded("RecordBrush", 0.28);
                var cutPen = new Pen(_brushes.Faded("RecordBrush", 0.8), 1);
                foreach (var cut in m.Cuts)
                {
                    var r = new Rect(X(cut[0]), top, Math.Max(1, X(cut[1]) - X(cut[0])), bottom - top);
                    c.FillRectangle(cutFill, r);
                    using (c.PushClip(r))
                        for (double hx = r.X - r.Height; hx < r.Right; hx += 7) c.DrawLine(cutPen, new Point(hx, r.Bottom), new Point(hx + r.Height, r.Y));
                }
                if (data != null)
                {
                    // where VRChat was not tracking (calibration mode, avatar reloads)
                    var fill = _brushes.Faded("SuccessBrush", 0.14);
                    var outline = new Pen(_brushes.Faded("SuccessBrush", 0.5), 1) { DashStyle = DashStyle.Dash };
                    foreach (var sp in data.NotTracking)
                    {
                        double x0 = X(sp[0]), x1 = X(sp[1]);
                        var r = new Rect(x0, top, Math.Max(1, x1 - x0), bottom - top - 1);
                        c.DrawRectangle(fill, outline, r);
                        if (x1 - x0 > 110) Text(c, "VRChat calibration", x0 + 4, bottom - 17, "SuccessBrush");
                    }
                    var jump = _brushes.Faded("WarningBrush", 0.25);
                    foreach (var j in data.JumpSpans) c.FillRectangle(jump, new Rect(X(j[0]), top, Math.Max(1, X(j[1]) - X(j[0])), 6));
                }
                // selection
                if (m.SelectionStart >= 0 && m.SelectionEnd > m.SelectionStart)
                    c.DrawRectangle(_brushes["AccentSoftBrush"], new Pen(_brushes["AccentBrush"], 1), new Rect(X(m.SelectionStart), top, X(m.SelectionEnd) - X(m.SelectionStart), bottom - top - 1));
                // trim handles
                var handle = _brushes["WarningBrush"];
                c.FillRectangle(handle, new Rect(xin - 2, top, 4, bottom - top), 2);
                c.FillRectangle(handle, new Rect(xout - 2, top, 4, bottom - top), 2);
                // flags
                if (data != null)
                    foreach (var f in data.Flags)
                    {
                        double x = Math.Round(X(f.Time)) + 0.5;
                        var b = _brushes[FlagBrush(f.Kind)];
                        c.DrawLine(new Pen(b, 1) { DashStyle = DashStyle.Dot }, new Point(x, top), new Point(x, bottom));
                        var flag = new StreamGeometry();
                        using (var g = flag.Open()) { g.BeginFigure(new Point(x, top), true); g.LineTo(new Point(x + 7, top + 4)); g.LineTo(new Point(x, top + 8)); g.EndFigure(true); }
                        c.DrawGeometry(b, null, flag);
                    }
                // playhead
                double px = X(m.Playhead);
                c.DrawLine(new Pen(_brushes["TextBrush"], 1.5), new Point(px, 0), new Point(px, bottom));
            }

            // lane labels
            double ly = top + 6;
            if (data != null)
            {
                foreach (var lane in data.Lanes) { Text(c, lane.Name, 12, ly, "TextSecondaryBrush"); ly += LaneHeight; }
                Text(c, "Tracking loss", 12, ly, "TextSecondaryBrush"); ly += LaneHeight;
                if (data.FaceActivity != null) Text(c, "Face", 12, ly, "TextSecondaryBrush");
            }
        }
    }
}
