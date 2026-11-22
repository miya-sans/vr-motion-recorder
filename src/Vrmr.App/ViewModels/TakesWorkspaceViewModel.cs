using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VRMR.App.Composition;
using VRMR.App.Controls;
using VRMR.App.Services;
using VRMR.App.Viewport;

namespace VRMR.App.ViewModels
{
    /// <summary>
    ///     The Takes workspace: the transport, the timeline, what the 3D view shows (the open take or the live devices)
    ///     and the view chips. It owns no data: the take session, the playback clock and the presenters do the work.
    /// </summary>
    public partial class TakesWorkspaceViewModel : ObservableObject, IGestureSource
    {
        /// <summary>The inspector's tabs.</summary>
        public const int DevicesTab = 0, TakeTab = 1, ExportTab = 2;

        readonly PlaybackClock _clock;
        readonly AppHost _host;
        readonly TakeSession _session;
        readonly SettingsStore _settings;
        [ObservableProperty] bool _canPlay;
        [ObservableProperty] bool _canRedo;
        [ObservableProperty] bool _canUndo;
        [ObservableProperty] string _emptyText = "";
        [ObservableProperty] string _emptyTitle = "";
        bool _framed, _active = true;
        [ObservableProperty] bool _hasBaseStations;
        [ObservableProperty] string _frameText = "";
        [ObservableProperty] bool _hasHeadset;
        [ObservableProperty] bool _hasPlayArea;
        [ObservableProperty] bool _hasSelection;

        [ObservableProperty] bool _hasTake;

        /// <summary>
        ///     Which inspector tab shows. It follows what the view shows (Devices with Live, Take with a take) until the user
        ///     picks one.
        /// </summary>
        [ObservableProperty] int _inspectorTab;

        [ObservableProperty] bool _isPlaying;
        [ObservableProperty] bool _isSolving;
        string _lastTakePath;
        IDisposable _liveWatch;
        [ObservableProperty] bool _loop = true;
        List<double[]> _segments = new();
        [ObservableProperty] bool _showBaseStations;

        /// <summary>Moving a left or right tracker in the calibration view mirrors it onto its partner.</summary>
        [ObservableProperty] bool _mirrorTrackers = true;

        /// <summary>How the 3D view looks: perspective, front, side or top (<see cref="Render.ViewPreset" />).</summary>
        [ObservableProperty] int _viewIndex;

        /// <summary>The view shows the calibration pose of the segment at the playhead instead of the solved pose.</summary>
        [ObservableProperty] bool _showCalibrationPose;

        [ObservableProperty] bool _showDevices;
        [ObservableProperty] bool _showEmpty;
        [ObservableProperty] bool _showHeadset;
        [ObservableProperty] bool _showMesh;
        [ObservableProperty] bool _showPlayArea;
        [ObservableProperty] bool _showSkeleton;
        [ObservableProperty] bool _solveFailed;
        [ObservableProperty] double _solveProgress;
        [ObservableProperty] string _solveText = "";
        [ObservableProperty] int _speedIndex = 2;
        [ObservableProperty] string _timeText = "";
        [ObservableProperty] string _timeTip;
        IViewport _view;
        CalibrationPose _shownCalibration;

        public TakesWorkspaceViewModel(AppHost host)
        {
            _host = host;
            _session = host.Session;
            _clock = host.Clock;
            _settings = host.SettingsStore;
            Timeline = new TimelineViewModel(_session, _clock, host.Ui);
            PlacementTool = new PlacementTool(host.Placement, host.TakePresenter, _session, _clock);
            CalibrationTool = new CalibrationTool(host.TakePresenter, _session);
            CalibrationTool.Changed += () =>
            {
                Present();
                CalibrationViewChanged?.Invoke();
            };
            var s = _settings.Value;
            _showMesh = s.PreviewMesh;
            _showSkeleton = s.PreviewSkeleton;
            _showDevices = s.ShowDevices;
            _showHeadset = s.ShowHmdModel;
            _showPlayArea = s.ShowPlayArea;
            _showBaseStations = s.ShowBaseStations;

            _session.TakeChanged += OnTakeChanged;
            _session.ClipChanged += OnClipChanged;
            _session.EditsChanged += OnEditsChanged;
            _session.SolveChanged += OnSolveChanged;
            _session.DirtyChanged += () => OnPropertyChanged(nameof(TakeTitle));
            _session.History.Changed += () =>
            {
                CanUndo = _session.History.CanUndo;
                CanRedo = _session.History.CanRedo;
            };
            _clock.PlayheadChanged += OnPlayheadChanged;
            _clock.StateChanged += () =>
            {
                IsPlaying = _clock.Playing;
                if (IsPlaying) ShowCalibrationPose = false;
                UpdateAnimating();
            };
            Timeline.PreviewChanged += Present;
            _session.PreviewClipReady += Present;
            Timeline.SelectionChanged += () => HasSelection = Timeline.HasSelection;
            host.Recording.StateChanged += OnRecordingChanged;
            host.Recording.Stopped += rec =>
            {
                if (_active) _session.Show(rec);
            };
            PlacementTool.Changed += () =>
            {
                _view?.SceneChanged();
                OnPropertyChanged(nameof(GizmoOn));
                OnPropertyChanged(nameof(GizmoTurns));
            };
            Inspector = new TakeInspectorViewModel(host, this);
            Devices = new DevicesViewModel(host);
            _inspectorTab = host.CaptureOn ? DevicesTab : TakeTab;
            Devices.SetVisible(_inspectorTab == DevicesTab);
            UpdateTimeText();
        }

        /// <summary>
        ///     Live mode: no take is open and the view shows only what SteamVR and VRChat send now. Switching it on closes
        ///     the open take; switching it off opens the last take again. Opening a take leaves it.
        /// </summary>
        public bool IsLive
        {
            get => _session.Take == null;
            set
            {
                if (value != IsLive) _ = ToggleLive();
            }
        }

        public TimelineViewModel Timeline { get; }
        public PlacementTool PlacementTool { get; }

        /// <summary>Moves trackers against the avatar while the calibration pose shows.</summary>
        public CalibrationTool CalibrationTool { get; }

        /// <summary>The calibration view was switched, shows another calibration, or its pick changed.</summary>
        public event Action CalibrationViewChanged;

        /// <summary>The tool the view uses now: the tracker gizmo in the calibration view, else the placement gizmo when on.</summary>
        IViewportTool CurrentTool =>
            !_active ? null : ShowCalibrationPose ? CalibrationTool : PlacementTool.Active ? PlacementTool : null;
        public TakeInspectorViewModel Inspector { get; }
        public DevicesViewModel Devices { get; }
        public bool ShowTakeTab => InspectorTab == TakeTab;
        public bool ShowExportTab => InspectorTab == ExportTab;
        public bool ShowDevicesTab => InspectorTab == DevicesTab;

        /// <summary>Speed labels for the transport.</summary>
        public IReadOnlyList<string> SpeedLabels { get; } = new[] { "0.25×", "0.5×", "1×", "1.5×", "2×" };

        /// <summary>The open take's name with a dot while it has unsaved edits.</summary>
        public string TakeTitle => _session.Take == null ? "" : _session.Take.Name + (_session.Dirty ? " •" : "");

        public bool GizmoOn => PlacementTool.Active;
        public bool GizmoTurns => PlacementTool.Turn;

        public IDisposable BeginGesture()
        {
            return _session.Gesture();
        }

        partial void OnInspectorTabChanged(int value)
        {
            OnPropertyChanged(nameof(ShowTakeTab));
            OnPropertyChanged(nameof(ShowExportTab));
            OnPropertyChanged(nameof(ShowDevicesTab));
            Devices.SetVisible(value == DevicesTab && _active);
        }

        // ------------------------------------------------------------------------------------------ the view

        /// <summary>Connects the 3D view (called once by the window).</summary>
        public void AttachView(IViewport view)
        {
            _view = view;
            ApplyViewOptions();
            view.FrameTick += OnFrameTick;
            view.ViewChanged += () => ViewIndex = (int)view.View;
            Present();
            UpdateAnimating();
        }

        /// <summary>The workspace became visible or hidden (the Scene workspace has its own way of filling the view).</summary>
        public void SetActive(bool active)
        {
            _active = active;
            if (!active)
            {
                Timeline.Preview(null);
                _clock.Playing = false;
                if (_view != null)
                {
                    _view.Tool = null;
                    _view.Animating = false;
                }
            }
            else
            {
                if (_view != null) _view.Tool = CurrentTool;
                Present();
                UpdateAnimating();
            }

            Devices.SetVisible(active && InspectorTab == DevicesTab);
        }

        void ApplyViewOptions()
        {
            if (_view == null) return;
            var o = _view.Options;
            o.Mesh = ShowMesh;
            o.Skeleton = ShowSkeleton;
            o.Devices = ShowDevices;
            o.Headset = ShowHeadset;
            o.PlayArea = ShowPlayArea;
            o.BaseStations = ShowBaseStations;
            o.Recording = _host.Recording.IsRecording;
            _view.SceneChanged();
        }

        partial void OnShowMeshChanged(bool value)
        {
            SaveChip(nameof(Settings.PreviewMesh), s => s.PreviewMesh = value);
        }

        partial void OnShowSkeletonChanged(bool value)
        {
            SaveChip(nameof(Settings.PreviewSkeleton), s => s.PreviewSkeleton = value);
        }

        partial void OnShowDevicesChanged(bool value)
        {
            SaveChip(nameof(Settings.ShowDevices), s => s.ShowDevices = value);
        }

        partial void OnShowHeadsetChanged(bool value)
        {
            SaveChip(nameof(Settings.ShowHmdModel), s => s.ShowHmdModel = value);
        }

        partial void OnShowPlayAreaChanged(bool value)
        {
            SaveChip(nameof(Settings.ShowPlayArea), s => s.ShowPlayArea = value);
            Present();
        }

        partial void OnShowBaseStationsChanged(bool value)
        {
            SaveChip(nameof(Settings.ShowBaseStations), s => s.ShowBaseStations = value);
            Present();
        }

        partial void OnMirrorTrackersChanged(bool value)
        {
            CalibrationTool.Mirror = value;
        }

        partial void OnViewIndexChanged(int value)
        {
            _view?.SetView((Render.ViewPreset)value);
        }

        /// <summary>Numpad keys as in 3D tools: 1 front, 3 side, 7 top, 5 perspective.</summary>
        public void SetView(Render.ViewPreset view)
        {
            ViewIndex = (int)view;
        }

        partial void OnShowCalibrationPoseChanged(bool value)
        {
            _host.TakePresenter.CalibrationPose = value;
            if (!value) CalibrationTool.Select("");
            if (value && PlacementTool.Active) SetGizmo(false);
            if (_view != null) _view.Tool = CurrentTool;
            Present();
            if (value) _view?.FrameAll();
            CalibrationViewChanged?.Invoke();
        }

        void SaveChip(string what, Action<Settings> change)
        {
            _settings.Update(what, change);
            ApplyViewOptions();
        }

        /// <summary>Refills the 3D view with the take at the playhead, or with the live state.</summary>
        public void Present()
        {
            if (_view == null || !_active) return;
            bool recording = _host.Recording.IsRecording;
            bool shown = false;
            if (!IsLive && !recording) shown = _host.TakePresenter.Show(_view, Timeline.PreviewTime);
            var cal = _host.TakePresenter.ShownCalibration;
            if (!ReferenceEquals(cal, _shownCalibration))
            {
                _shownCalibration = cal;
                CalibrationViewChanged?.Invoke();
            }

            FrameText = shown ? $"frame {_host.TakePresenter.Frame}/{_host.TakePresenter.LastFrame}" : "";
            if (!shown && (IsLive || recording))
            {
                _host.LivePresenter.Show(_view);
                shown = true;
            }

            if (!shown)
            {
                _view.Scene.Overlay = "";
                _view.Scene.OverlayRight = "";
                _view.SceneChanged();
            }

            UpdateChipAvailability();
            UpdateEmptyState();
        }

        void UpdateChipAvailability()
        {
            var scene = _view.Scene;
            bool stations = false, headset = false;
            foreach (var m in scene.Markers)
            {
                stations |= m.Kind == MarkerKind.BaseStation;
                headset |= m.Kind == MarkerKind.Headset;
            }

            HasPlayArea = scene.PlayArea != null;
            HasBaseStations = stations;
            HasHeadset = headset;
        }

        void UpdateEmptyState()
        {
            var take = _session.Take;
            bool nothing = _view.Scene.Profile == null && _view.Scene.Markers.Count == 0;
            if (take != null && !IsLive)
            {
                var clip = _session.Clip;
                if (_session.Solve == SolveState.Failed)
                {
                    ShowEmpty = true;
                    EmptyTitle = "This take could not be solved";
                    EmptyText = FriendlySolveError(_session.SolveError);
                }
                else if (clip == null)
                {
                    ShowEmpty = true;
                    EmptyTitle = _session.Opening ? "Opening…" : "Solving…";
                    EmptyText = "The body appears here as soon as the solve is done.";
                }
                else if (clip.Profile == null)
                {
                    ShowEmpty = true;
                    EmptyTitle = "No avatar to solve onto";
                    EmptyText =
                        "Add your avatar's base model under Settings → Avatars, or pick one for this take in the inspector.";
                }
                else ShowEmpty = false;
            }
            else if (nothing)
            {
                ShowEmpty = true;
                if (!_host.CaptureOn)
                {
                    EmptyTitle = "Pick a take";
                    EmptyText = "Choose a take in the library to see it here. Live sources are off in this session.";
                }
                else
                {
                    EmptyTitle = "Waiting for VRChat or SteamVR";
                    EmptyText =
                        "Join VRChat with OSC on, or start SteamVR: your devices appear here. Or pick a take in the library.";
                }
            }
            else ShowEmpty = false;
        }

        static string FriendlySolveError(string error)
        {
            return (string.IsNullOrEmpty(error) ? "The solver stopped with an error." : error.TrimEnd('.') + ".") +
                   " Try another calibration source or avatar in the inspector; the details are in the log (Settings → About).";
        }

        // ------------------------------------------------------------------------------------------ pacing

        void UpdateAnimating()
        {
            if (_view == null) return;
            bool recording = _host.Recording.IsRecording;
            bool live = (IsLive || recording) && _host.Live.HasLiveMotion;
            _view.FrameCap = recording ? _settings.Value.PreviewWhileRecording ? 15 : 4 : 60;
            _view.Animating = _active && (_clock.Playing || live);
            // while Live is on but nothing arrives yet, look once a second whether a source came up (no redraws meanwhile)
            bool watch = _active && _host.CaptureOn && (IsLive || recording) && !live;
            if (watch && _liveWatch == null)
                _liveWatch = _host.Ui.Every(TimeSpan.FromSeconds(1), () =>
                {
                    if (_host.Live.HasLiveMotion)
                    {
                        Present();
                        UpdateAnimating();
                    }
                });
            else if (!watch)
            {
                _liveWatch?.Dispose();
                _liveWatch = null;
            }
        }

        void OnFrameTick(TimeSpan elapsed)
        {
            if (!_active) return;
            if (_clock.Playing && _session.Take != null && _session.Clip != null)
            {
                _clock.Advance(Math.Min(0.1, elapsed.TotalSeconds), _segments);
                if (_host.FaceReplay.Enabled) _host.FaceReplay.Send(_session.Take, _clock.Playhead);
            }
            else if (IsLive || _host.Recording.IsRecording)
            {
                _host.LivePresenter.Show(_view);
                UpdateChipAvailability();
                if (!_host.Live.HasLiveMotion) UpdateAnimating();
            }
        }

        // ------------------------------------------------------------------------------------------ session events

        void OnTakeChanged()
        {
            var take = _session.Take;
            HasTake = take != null;
            if (take?.FilePath != null) _lastTakePath = take.FilePath;
            OnPropertyChanged(nameof(IsLive));
            if (take == null)
            {
                _clock.Playing = false;
                if (PlacementTool.Active) SetGizmo(false);
                if (_host.CaptureOn) InspectorTab = DevicesTab;
            }
            else if (InspectorTab == DevicesTab) InspectorTab = TakeTab;
            _framed = false;
            RecomputeSegments();
            OnPropertyChanged(nameof(TakeTitle));
            UpdateTimeText();
            Present();
            UpdateAnimating();
        }

        void OnClipChanged()
        {
            CanPlay = _session.Clip != null;
            Present();
            if (_session.Clip?.Profile != null && !_framed && _view != null && !IsLive)
            {
                _view.FrameAll();
                _framed = true;
            }
        }

        void OnEditsChanged(string what)
        {
            RecomputeSegments();
            UpdateTimeText();
            if (what == "placement" || what == "*") Present();
        }

        void OnSolveChanged()
        {
            var st = _session.Solve;
            IsSolving = st == SolveState.Solving || st == SolveState.Waiting || _session.Opening;
            SolveProgress = st == SolveState.Solving ? _session.SolveProgress : 0;
            SolveFailed = st == SolveState.Failed;
            var clip = _session.Clip;
            SolveText = _session.Opening
                ? "Opening…"
                : st == SolveState.Solving
                    ? $"Solving… {_session.SolveProgress * 100:0}%"
                    : st == SolveState.Waiting
                        ? "Solving…"
                        : st == SolveState.Failed
                            ? "Solve failed"
                            : st == SolveState.Done && clip != null
                                ? $"{TakePresenter.BodySourceText(clip.BodySource)} · {clip.Frames} frames at {clip.Fps:0} fps · {_session.SolveMilliseconds} ms"
                                : "";
            if (_view != null && (st == SolveState.Failed || _session.Opening)) UpdateEmptyState();
        }

        void OnPlayheadChanged()
        {
            UpdateTimeText();
            if (!IsLive) Present();
        }

        void OnRecordingChanged()
        {
            // recording is live: the open take is saved and closed
            if (_host.Recording.IsRecording && _session.Take != null)
            {
                _session.SaveIfDirtyNow();
                _session.Close();
            }

            ApplyViewOptions();
            Present();
            UpdateAnimating();
        }

        partial void OnLoopChanged(bool value)
        {
            _clock.Loop = value;
        }

        partial void OnSpeedIndexChanged(int value)
        {
            _clock.Speed = PlaybackClock.Speeds[Math.Max(0, Math.Min(PlaybackClock.Speeds.Length - 1, value))];
        }

        void RecomputeSegments()
        {
            var take = _session.Take;
            _segments = take != null ? take.Edits.Segments(take.Duration) : new List<double[]>();
        }

        void UpdateTimeText()
        {
            var take = _session.Take;
            TimeText = take == null
                ? ""
                : $"{Services.TimeText.Format(_clock.Playhead)} / {Services.TimeText.Format(take.Duration)}";
            TimeTip = take != null && Math.Abs(take.EditedDuration - take.Duration) > 0.005
                ? $"Length after trims and cuts: {Services.TimeText.Format(take.EditedDuration)}"
                : "Playhead / length of the take";
        }

        // ------------------------------------------------------------------------------------------ commands

        [RelayCommand]
        void TogglePlay()
        {
            if (_session.Take == null || _session.Clip == null) return;
            if (_clock.Playing) _clock.Pause();
            else
            {
                _host.FaceReplay.Restart();
                _clock.Play(_segments);
            }
        }

        [RelayCommand]
        void SeekStart()
        {
            if (_session.Take == null) return;
            _clock.Seek(_session.Take.Edits.TrimIn, _session.Take.Duration);
        }

        [RelayCommand]
        void SeekEnd()
        {
            if (_session.Take == null) return;
            _clock.Seek(Timeline.TrimOut, _session.Take.Duration);
        }

        [RelayCommand]
        void StepBack()
        {
            Step(-1);
        }

        [RelayCommand]
        void StepForward()
        {
            Step(1);
        }

        void Step(int frames)
        {
            if (_session.Take == null || _session.Clip == null) return;
            _clock.Step(frames, _session.Clip.Fps, _session.Take.Duration);
        }

        [RelayCommand]
        void SetIn()
        {
            Timeline.SetIn();
        }

        [RelayCommand]
        void SetOut()
        {
            Timeline.SetOut();
        }

        [RelayCommand]
        void Cut()
        {
            if (_session.Take == null) return;
            if (!Timeline.CutSelection())
                _host.Status.Info("Select a range first: drag in the lanes of the timeline, then cut.");
        }

        [RelayCommand]
        void Keep()
        {
            if (_session.Take == null) return;
            if (!Timeline.KeepSelection())
                _host.Status.Info("Select a range first: drag in the lanes of the timeline, then keep only that part.");
        }

        [RelayCommand]
        void ResetEdits()
        {
            Timeline.ResetEdits();
        }

        [RelayCommand]
        void Undo()
        {
            _session.Undo();
        }

        [RelayCommand]
        void Redo()
        {
            _session.Redo();
        }

        [RelayCommand]
        void OriginHere()
        {
            _host.Placement.OriginHere();
            _host.Status.Info(_host.Placement.LastAction);
        }

        [RelayCommand]
        void TurnLeft()
        {
            _host.Placement.Turn(-90);
            _host.Status.Info(_host.Placement.LastAction);
        }

        [RelayCommand]
        void TurnRight()
        {
            _host.Placement.Turn(90);
            _host.Status.Info(_host.Placement.LastAction);
        }

        [RelayCommand]
        void CalibrateHere()
        {
            if (_session.Take == null) return;
            double t = _clock.Playhead;
            _session.Edit("calibration", e => e.CalibrationTime = t);
            _host.Status.Info($"Calibrating at {Services.TimeText.Format(t)}");
        }

        /// <summary>Enters Live (the open take is saved and closed) or leaves it for the last take.</summary>
        [RelayCommand]
        async Task ToggleLive()
        {
            if (_session.Take != null)
            {
                await _session.SaveIfDirtyAsync();
                _session.Close();
                return;
            }

            if (!_host.Recording.IsRecording && File.Exists(_lastTakePath) && await _session.OpenAsync(_lastTakePath))
                return;
            _host.Ui.Post(() => OnPropertyChanged(nameof(IsLive))); // nothing to leave for: the button stays on
        }

        [RelayCommand]
        void FrameAll()
        {
            _view?.FrameAll();
        }

        [RelayCommand]
        Task Save()
        {
            return _session.SaveAsync();
        }

        [RelayCommand]
        Task Revert()
        {
            return _session.RevertAsync();
        }

        /// <summary>Switches the placement gizmo on or off, in move or turn mode.</summary>
        public void SetGizmo(bool on, bool? turn = null)
        {
            if (turn.HasValue) PlacementTool.Turn = turn.Value;
            PlacementTool.Active = on;
            if (_view != null)
            {
                _view.Tool = CurrentTool;
                _view.SceneChanged();
            }

            OnPropertyChanged(nameof(GizmoOn));
            OnPropertyChanged(nameof(GizmoTurns));
        }
    }
}
