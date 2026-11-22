using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VRMR.App.Services;

namespace VRMR.App.ViewModels
{
    /// <summary>What a coverage lane shows.</summary>
    public enum LaneKind { VrcIk, SteamVr, VrcPoses }

    /// <summary>Where a kind of data was recorded: a named lane of time spans.</summary>
    public sealed class TimelineLane
    {
        public string Name { get; init; } = "";
        public LaneKind Kind { get; init; }
        public List<double[]> Spans { get; init; } = new();
    }

    /// <summary>What a flag on the timeline marks.</summary>
    public enum FlagKind
    {
        Calibration, VrcCalibration, VrcSetting, AvatarChange, WorldChange, PlayspaceJump, VrcJump, LegacyOrigin
    }

    /// <summary>A moment worth knowing about, drawn as a flag with a tooltip.</summary>
    public sealed class TimelineFlag
    {
        public double Time { get; init; }
        public FlagKind Kind { get; init; }
        public string Label { get; init; } = "";
    }

    /// <summary>Everything the timeline draws besides the edits: computed once per take (and again after a solve) on a worker.</summary>
    public sealed class TimelineData
    {
        public List<TimelineLane> Lanes { get; } = new();

        /// <summary>Face activity 0..1 per <see cref="FaceStep" /> seconds, or null without face tracking.</summary>
        public float[] FaceActivity { get; set; }

        public double FaceStep { get; } = 0.1;
        public List<TrackerGap> Gaps { get; set; } = new();

        /// <summary>Spans where VRChat was not tracking (calibration mode, avatar reloads).</summary>
        public List<double[]> NotTracking { get; set; } = new();

        /// <summary>Spans where VRChat's jump button lifted the avatar.</summary>
        public List<double[]> JumpSpans { get; set; } = new();

        public List<TimelineFlag> Flags { get; } = new();

        /// <summary>Spans of sample times with no gap longer than <paramref name="gap" /> seconds.</summary>
        public static List<double[]> Coverage(IReadOnlyList<double> t, double gap)
        {
            var res = new List<double[]>();
            if (t == null || t.Count == 0) return res;
            double a = t[0], last = t[0];
            for (int i = 1; i < t.Count; i++)
            {
                if (t[i] - last > gap)
                {
                    res.Add(new[] { a, last });
                    a = t[i];
                }

                last = t[i];
            }

            res.Add(new[] { a, last });
            return res;
        }

        /// <summary>How much the face moved per step, normalised to 0..1 (square root, so quiet parts stay visible).</summary>
        public static float[] FaceActivityOf(Recording r, double step)
        {
            var tracks = r.Params.Where(p =>
                p.Type == ParamType.Float && (p.Name.StartsWith("FT/v2/", StringComparison.Ordinal) ||
                                              p.Name.StartsWith("v2/", StringComparison.Ordinal))).ToList();
            if (tracks.Count == 0) return null;
            int n = (int)(r.Duration / step) + 1;
            float[] a = new float[n];
            foreach (var p in tracks)
                for (int i = 1; i < p.Count; i++)
                {
                    int k = (int)(p.T[i] / step);
                    if (k >= 0 && k < n) a[k] += Math.Abs(p.V[i] - p.V[i - 1]);
                }

            float max = a.Length > 0 ? a.Max() : 0;
            if (max > 0)
                for (int i = 0; i < n; i++)
                    a[i] = (float)Math.Sqrt(a[i] / max);
            return a;
        }

        /// <summary>Builds the data for a take (worker thread).</summary>
        public static TimelineData Build(Recording r)
        {
            var d = new TimelineData();
            d.Lanes.Add(new TimelineLane
                { Name = "VRChat IK", Kind = LaneKind.VrcIk, Spans = Coverage(r.Humr.T, 0.5) });
            var hmd = r.Devices.Where(s => s.DeviceClass == 1).OrderByDescending(s => s.Count).FirstOrDefault();
            d.Lanes.Add(new TimelineLane
            {
                Name = "SteamVR", Kind = LaneKind.SteamVr,
                Spans = hmd != null ? Coverage(hmd.T, 0.3) : new List<double[]>()
            });
            var head = r.Stream(StreamKind.VrcHead);
            d.Lanes.Add(new TimelineLane
            {
                Name = "VRChat poses", Kind = LaneKind.VrcPoses,
                Spans = head != null ? Coverage(head.T, 0.5) : new List<double[]>()
            });
            d.FaceActivity = FaceActivityOf(r, d.FaceStep);
            d.Gaps = TrackerGaps.For(r).ToList();
            var tl = VrcTimeline.For(r);
            d.NotTracking = tl.NotTracking().Select(s => new[] { s[0] - PoseBuilder.HoldLead, s[1] }).ToList();
            var e = r.Edits;
            if (e.OriginTime > 1e-6)
                d.Flags.Add(new TimelineFlag
                    { Time = e.OriginTime, Kind = FlagKind.LegacyOrigin, Label = "Origin set by an older version" });
            if (e.CalibrationTime >= 0)
                d.Flags.Add(new TimelineFlag
                    { Time = e.CalibrationTime, Kind = FlagKind.Calibration, Label = "Calibration moment" });
            foreach (var ev in r.Events)
            {
                if (ev.Type == "avatar")
                    d.Flags.Add(new TimelineFlag
                    {
                        Time = ev.T, Kind = FlagKind.AvatarChange,
                        Label = "Avatar changed" + (string.IsNullOrEmpty(ev.Text) ? "" : ": " + ev.Text)
                    });
                else if (ev.Type == "world")
                    d.Flags.Add(new TimelineFlag
                    {
                        Time = ev.T, Kind = FlagKind.WorldChange,
                        Label = "World changed" + (string.IsNullOrEmpty(ev.Text) ? "" : ": " + ev.Text)
                    });
            }

            foreach (var c in tl.Calibrations)
                d.Flags.Add(new TimelineFlag
                    { Time = c.Confirm, Kind = FlagKind.VrcCalibration, Label = "VRChat calibration" });
            foreach (var c in tl.Changes)
                d.Flags.Add(new TimelineFlag
                    { Time = c.T, Kind = FlagKind.VrcSetting, Label = "VRChat setting changed: " + c });
            foreach (var j in PlayspaceJumps.For(r).Jumps)
                d.Flags.Add(new TimelineFlag
                {
                    Time = j.After, Kind = FlagKind.PlayspaceJump, Label = $"Playspace jump ({j.Moved * 100:0} cm)"
                });
            foreach (var j in VrcJumps.For(r).Jumps)
            {
                d.JumpSpans.Add(new[] { j.Start, j.End });
                d.Flags.Add(new TimelineFlag
                    { Time = j.Start, Kind = FlagKind.VrcJump, Label = $"VRChat jump ({j.Height * 100:0} cm)" });
            }

            return d;
        }
    }

    /// <summary>
    ///     The take timeline's state and editing: the visible range, the selection, trims and cuts. The control only draws
    ///     this and forwards the pointer; every edit goes through the take session, a drag being one undo step.
    /// </summary>
    public sealed class TimelineViewModel
    {
        readonly PlaybackClock _clock;
        readonly TakeSession _session;
        readonly IUiDispatcher _ui;
        int _dataRun;
        IDisposable _gesture;

        public TimelineViewModel(TakeSession session, PlaybackClock clock, IUiDispatcher ui)
        {
            _session = session;
            _clock = clock;
            _ui = ui;
            _session.TakeChanged += OnTakeChanged;
            _session.ClipChanged += RebuildData;
            _session.EditsChanged += what =>
            {
                if (what == "trackerGaps" || what == "*" || what == "calibration" || what == "vrcChanges")
                    RebuildData();
                Changed?.Invoke();
            };
            _clock.PlayheadChanged += () => Changed?.Invoke();
        }

        public Recording Take => _session.Take;
        public TimelineData Data { get; private set; }
        public double Duration => Take != null ? Math.Max(0.01, Take.Duration) : 1;
        public double Playhead => _clock.Playhead;
        public double? PreviewTime { get; private set; }
        public event Action PreviewChanged;

        public void Preview(double? time)
        {
            double? next = time.HasValue ? Clamp(time.Value) : null;
            if (PreviewTime == next) return;
            PreviewTime = next;
            if (!next.HasValue) _session.ClearPreviewClip();
            PreviewChanged?.Invoke();
        }
        public double TrimIn => Take?.Edits.TrimIn ?? 0;

        /// <summary>The out point in seconds (the take's end when it is not trimmed).</summary>
        public double TrimOut => Take == null ? 1 : Take.Edits.TrimOut < 0 ? Duration : Take.Edits.TrimOut;

        public IReadOnlyList<double[]> Cuts => (IReadOnlyList<double[]>)Take?.Edits.Cuts ?? Array.Empty<double[]>();

        /// <summary>The selected range in source seconds, or -1 for none.</summary>
        public double SelectionStart { get; private set; } = -1;

        public double SelectionEnd { get; private set; } = -1;
        public bool HasSelection => SelectionStart >= 0 && SelectionEnd - SelectionStart >= 0.02;

        /// <summary>The visible range.</summary>
        public double ViewStart { get; private set; }

        public double ViewEnd { get; private set; } = 10;

        /// <summary>True when the take has trims or cuts.</summary>
        public bool HasEdits =>
            Take != null && (Take.Edits.TrimIn > 0 || Take.Edits.TrimOut >= 0 || Take.Edits.Cuts.Count > 0);

        /// <summary>Raised when anything the control draws changed.</summary>
        public event Action Changed;

        /// <summary>The selection changed (the transport enables Cut and Keep by it).</summary>
        public event Action SelectionChanged;

        /// <summary>The user moved the playhead on the timeline.</summary>
        public event Action Scrubbed;

        void OnTakeChanged()
        {
            SelectionStart = SelectionEnd = -1;
            PreviewTime = null;
            Data = null;
            _clock.Playing = false;
            _clock.Playhead = Take?.Edits.TrimIn ?? 0;
            FitAll();
            RebuildData();
            SelectionChanged?.Invoke();
        }

        void RebuildData()
        {
            var take = Take;
            int run = ++_dataRun;
            if (take == null)
            {
                Data = null;
                Changed?.Invoke();
                return;
            }

            Task.Run(() =>
            {
                TimelineData data = null;
                try { data = TimelineData.Build(take); }
                catch (Exception ex) { AppLog.Warn("Timeline: " + ex.Message); }

                _ui.Post(() =>
                {
                    if (run == _dataRun && Take == take && data != null)
                    {
                        Data = data;
                        Changed?.Invoke();
                    }
                });
            });
        }

        double Clamp(double t)
        {
            return Math.Max(0, Math.Min(Duration, t));
        }

        public void FitAll()
        {
            ViewStart = 0;
            ViewEnd = Duration;
            Changed?.Invoke();
        }

        /// <summary>Zooms about a time; factor below 1 zooms in.</summary>
        public void Zoom(double about, double factor)
        {
            double ns = about - (about - ViewStart) * factor, ne = about + (ViewEnd - about) * factor;
            if (ne - ns < 0.2 || ne - ns > Duration * 4 + 10) return;
            ViewStart = ns;
            ViewEnd = ne;
            Changed?.Invoke();
        }

        public void Pan(double seconds)
        {
            ViewStart -= seconds;
            ViewEnd -= seconds;
            Changed?.Invoke();
        }

        /// <summary>Moves the playhead (pauses playback).</summary>
        public void Scrub(double t)
        {
            if (Take == null) return;
            _clock.Seek(Clamp(t), Duration);
            Scrubbed?.Invoke();
        }

        public void SetSelection(double a, double b)
        {
            if (a < 0 || b < 0) { SelectionStart = SelectionEnd = -1; }
            else
            {
                SelectionStart = Clamp(Math.Min(a, b));
                SelectionEnd = Clamp(Math.Max(a, b));
            }

            Changed?.Invoke();
            SelectionChanged?.Invoke();
        }

        public void ClearSelection()
        {
            SetSelection(-1, -1);
        }

        /// <summary>The shaded span under a time: a tracker loss (on its lane), else a stretch where VRChat was not tracking.</summary>
        public double[] SpanAt(double t, bool onLossLane)
        {
            var d = Data;
            if (d == null) return null;
            if (onLossLane)
                foreach (var g in d.Gaps)
                    if (t >= g.A - 0.05 && t <= g.B + 0.05)
                        return new[] { Math.Max(0, g.A), double.IsPositiveInfinity(g.B) ? Duration : g.B };
            foreach (double[] sp in d.NotTracking)
                if (t >= sp[0] && t <= sp[1])
                    return new[] { Math.Max(0, sp[0]), Math.Min(Duration, sp[1]) };
            return null;
        }

        // ---------------------------------------------------------------------------------------- trim drags

        /// <summary>A trim handle was grabbed: the drag is one undo step.</summary>
        public void BeginTrimDrag()
        {
            _gesture?.Dispose();
            _gesture = _session.Gesture();
        }

        public void DragTrimIn(double t)
        {
            double tout = TrimOut;
            _session.Edit("trimIn", e => e.TrimIn = Math.Min(Clamp(t), tout - 0.05), false);
            Preview(TrimIn);
        }

        public void DragTrimOut(double t)
        {
            double tin = TrimIn, dur = Duration;
            _session.Edit("trimOut", e =>
            {
                e.TrimOut = Math.Max(Clamp(t), tin + 0.05);
                if (e.TrimOut >= dur - 1e-3) e.TrimOut = -1;
            }, false);
            Preview(TrimOut);
        }

        public void EndTrimDrag()
        {
            _gesture?.Dispose();
            _gesture = null;
            _session.RequestSolve();
        }

        // ---------------------------------------------------------------------------------------- edit commands

        /// <summary>Trim the start at the playhead.</summary>
        public void SetIn()
        {
            if (Take == null) return;
            double t = Playhead, tout = TrimOut;
            _session.Edit("trimIn", e => e.TrimIn = Math.Max(0, Math.Min(t, tout - 0.05)));
        }

        /// <summary>Trim the end at the playhead.</summary>
        public void SetOut()
        {
            if (Take == null) return;
            double t = Playhead, tin = TrimIn;
            _session.Edit("trimOut", e => e.TrimOut = Math.Max(t, tin + 0.05));
        }

        /// <summary>Remove the selected range from the take.</summary>
        public bool CutSelection()
        {
            if (Take == null || !HasSelection) return false;
            double a = SelectionStart, b = SelectionEnd;
            _session.Edit("cuts", e => e.Cuts.Add(new[] { a, b }));
            ClearSelection();
            return true;
        }

        /// <summary>Trim to the selected range.</summary>
        public bool KeepSelection()
        {
            if (Take == null || !HasSelection) return false;
            double a = SelectionStart, b = SelectionEnd;
            _session.Edit("trim", e =>
            {
                e.TrimIn = a;
                e.TrimOut = b;
            });
            ClearSelection();
            return true;
        }

        /// <summary>Remove every trim and cut.</summary>
        public void ResetEdits()
        {
            if (Take == null) return;
            _session.Edit("trim", e =>
            {
                e.TrimIn = 0;
                e.TrimOut = -1;
                e.Cuts.Clear();
            });
        }

        /// <summary>Restores the cut under a time; false when there is none.</summary>
        public bool RestoreCutAt(double t)
        {
            if (Take == null) return false;
            double[] cut = Take.Edits.Cuts.FirstOrDefault(c => t >= c[0] && t <= c[1]);
            if (cut == null) return false;
            double a = cut[0], b = cut[1];
            _session.Edit("cuts", e => e.Cuts.RemoveAll(c => c[0] == a && c[1] == b));
            return true;
        }
    }
}
