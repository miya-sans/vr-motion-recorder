using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VRMR.App.Composition;
using VRMR.App.Controls;
using VRMR.App.Services;

namespace VRMR.App.ViewModels
{
    /// <summary>One tracker of the take with its role picker.</summary>
    public partial class TrackerRowViewModel : ObservableObject
    {
        readonly TrackerRoleInfo _info;
        readonly bool _ready;
        readonly TrackerService _trackers;

        [ObservableProperty] int _roleIndex;

        public TrackerRowViewModel(TrackerService trackers, TrackerRoleInfo info)
        {
            _trackers = trackers;
            _info = info;
            var labels = TrackerService.RoleLabels.ToList();
            if (info.AutoRole.Length > 0) labels[0] = "Automatic: " + TrackerService.RoleLabel(info.AutoRole);
            RoleChoices = labels;
            int i = Array.IndexOf(TrackerService.Roles, info.Role.Length == 0 ? "Auto" : info.Role);
            _roleIndex = i < 0 ? 0 : i;
            _ready = true;
        }

        public string Title => string.IsNullOrEmpty(_info.Serial) ? _info.Model : _info.Serial;

        public string Tip =>
            $"{_info.Model} {_info.Serial}\nAutomatic uses VRChat's own assignment when the take has it, else the role is found from how the tracker moves (known after the first solve).";

        public IReadOnlyList<string> RoleChoices { get; }

        partial void OnRoleIndexChanged(int value)
        {
            if (_ready) _trackers.SetRole(_info.Stream, value);
        }
    }

    /// <summary>
    ///     The Take tab of the inspector: everything that belongs to the open take. Name and notes, the body and the avatar
    ///     it is solved onto, tracker roles and tracking-loss repairs, placement, calibration, fingers and face.
    /// </summary>
    public partial class TakeInspectorViewModel : ObservableObject
    {
        static readonly string[] BodySourceIds = { "auto", "humr", "ik" };
        static readonly string[] FingerSourceIds = { "auto", "steamvr", "gestures", "none" };
        readonly TakeAvatarService _avatars;
        readonly CalibrationService _calibration;
        readonly AppHost _host;
        readonly TakeSession _session;
        readonly TrackerService _trackers;
        readonly TakesWorkspaceViewModel _workspace;
        List<AvatarEntry> _avatarEntries = new();

        [ObservableProperty] int _avatarIndex;
        [ObservableProperty] string _avatarNote = "";
        [ObservableProperty] string _avatarTip = "";
        [ObservableProperty] bool _avatarWarning;
        [ObservableProperty] int _bodySourceIndex;
        [ObservableProperty] string _calibrationPose = "";
        [ObservableProperty] bool _calibrationPoseWarning;
        [ObservableProperty] string _calibrationResult = "";
        [ObservableProperty] string _calibrationSummary = "";
        [ObservableProperty] Tone _calibrationTone;
        [ObservableProperty] bool _canDeleteSource;
        [ObservableProperty] bool _canMarkLost;
        [ObservableProperty] string _captureLabel = "Capture now";
        [ObservableProperty] string _detailText = "";
        [ObservableProperty] string _detailsSummary = "";
        [ObservableProperty] double _faceOffset;
        [ObservableProperty] bool _faceOffsetChanged;

        [ObservableProperty] int _fingerSourceIndex;
        [ObservableProperty] bool _hasLosses;
        [ObservableProperty] bool _hasMarks;
        [ObservableProperty] bool _hasPlacement;

        // ------------------------------------------------------------------------------------------ take

        [ObservableProperty] bool _hasTake;
        [ObservableProperty] bool _hasVrcChanges;
        [ObservableProperty] string _infoLine = "";
        [ObservableProperty] bool _isDirty;
        [ObservableProperty] bool _jumps;
        [ObservableProperty] string _jumpsLabel = "VRChat jumps";
        [ObservableProperty] bool _locomotion;
        [ObservableProperty] string _lossInfo = "";
        [ObservableProperty] string _lossSummary = "";
        [ObservableProperty] string _lossToggleLabel = "Keep as recorded";
        [ObservableProperty] Tone _lossTone;
        [ObservableProperty] int _markTrackerIndex;
        List<(PoseStream stream, string name)> _markable = new();

        // ------------------------------------------------------------------------------------------ placement

        [ObservableProperty] double _moveX;
        [ObservableProperty] double _moveY;
        [ObservableProperty] double _moveZ;
        [ObservableProperty] string _name = "";
        [ObservableProperty] bool _noTrackers;
        [ObservableProperty] string _notes = "";
        [ObservableProperty] string _notesSummary = "";
        [ObservableProperty] string _placementSummary = "as solved";
        [ObservableProperty] Tone _placementTone;
        [ObservableProperty] string _presetName = "";
        [ObservableProperty] bool _replayFace;

        [ObservableProperty] string _rolesSummary = "";
        [ObservableProperty] TrackerLossInfo _selectedLoss;
        [ObservableProperty] string _selectedPreset;
        [ObservableProperty] string _settingsOwner = "";
        [ObservableProperty] double _smoothing;
        [ObservableProperty] bool _smoothingChanged;
        [ObservableProperty] string _solveLine = "";
        [ObservableProperty] bool _solveWarning;
        [ObservableProperty] string _solverNotes = "";

        [ObservableProperty] int _sourceIndex;
        [ObservableProperty] string _sourceNote = "";
        [ObservableProperty] string _sourceTip = "";
        [ObservableProperty] bool _sourceWarning;
        List<CalibrationChoice> _sources = new();
        bool _syncing;
        [ObservableProperty] bool _takeHasOwnSettings;
        [ObservableProperty] double _turnX;
        [ObservableProperty] double _turnY;
        [ObservableProperty] double _turnZ;
        [ObservableProperty] int _vrcChangeKind;
        [ObservableProperty] string _vrcSettingsSummary = "";
        [ObservableProperty] string _vrcSettingsText = "";
        [ObservableProperty] string _watchText = "";
        IDisposable _watchTimer;

        public TakeInspectorViewModel(AppHost host, TakesWorkspaceViewModel workspace)
        {
            _host = host;
            _session = host.Session;
            _workspace = workspace;
            _calibration = host.Calibration;
            _trackers = host.Trackers;
            _avatars = host.TakeAvatars;
            Settings = new CalibrationSettingsViewModel(_calibration, () => _session.Clip);
            _session.TakeChanged += SyncAll;
            _session.EditsChanged += what => SyncEdits();
            _session.ClipChanged += SyncSolve;
            _session.SolveChanged += SyncSolveState;
            _session.DirtyChanged += () => { IsDirty = _session.Dirty; };
            _calibration.Changed += SyncCalibration;
            host.Avatars.Updated += () => host.Ui.Post(SyncAvatar);
            host.SettingsStore.Changed += what =>
            {
                if (what == nameof(VRMR.Settings.Smoothing) || what == "") SyncBody();
            };
            workspace.PlacementTool.Changed += () =>
            {
                OnPropertyChanged(nameof(GizmoOn));
                OnPropertyChanged(nameof(GizmoModeIndex));
            };
            workspace.Timeline.SelectionChanged += () => CanMarkLost = workspace.Timeline.HasSelection;
            SyncAll();
        }

        public IReadOnlyList<CalibrationSnapshot> SavedCalibrations => _calibration.SavedCalibrations;

        public CalibrationSettingsViewModel Settings { get; }

        /// <summary>Asks the view for a model file inside a Unity project.</summary>
        public Func<Task<string>> PickUnityModel { get; set; }

        // ------------------------------------------------------------------------------------------ body

        public ObservableCollection<string> AvatarChoices { get; } = new();
        public IReadOnlyList<string> BodySources { get; } = new[] { "Automatic", "VRChat's own IK", "IK solve" };

        // ------------------------------------------------------------------------------------------ trackers and tracking loss

        public ObservableCollection<TrackerRowViewModel> TrackerRows { get; } = new();
        public ObservableCollection<TrackerLossInfo> Losses { get; } = new();
        public ObservableCollection<string> MarkTrackers { get; } = new();

        public bool GizmoOn
        {
            get => _workspace.GizmoOn;
            set
            {
                if (value != _workspace.GizmoOn) _workspace.SetGizmo(value);
            }
        }

        /// <summary>0 = move, 1 = turn.</summary>
        public int GizmoModeIndex
        {
            get => _workspace.GizmoTurns ? 1 : 0;
            set
            {
                if (value >= 0 && value == 1 != _workspace.GizmoTurns) _workspace.SetGizmo(true, value == 1);
            }
        }

        // ------------------------------------------------------------------------------------------ calibration

        public ObservableCollection<CalibrationChoice> Sources { get; } = new();
        public ObservableCollection<string> Presets { get; } = new();
        public IReadOnlyList<string> VrcChangeKinds { get; } = CalibrationService.VrcChangeKinds;

        // ------------------------------------------------------------------------------------------ fingers and face

        public IReadOnlyList<string> FingerSources { get; } = new[]
            { "Automatic", "SteamVR finger tracking", "VRChat gestures", "None (rest pose)" };

        public bool ReplayAvailable => _host.FaceReplay.Available;

        partial void OnNameChanged(string value)
        {
            if (!_syncing) _session.SetName(value);
        }

        partial void OnNotesChanged(string value)
        {
            if (!_syncing)
            {
                _session.SetNotes(value);
                NotesSummary = Summarise(value);
            }
        }

        static string Summarise(string t)
        {
            t = (t ?? "").Trim();
            int nl = t.IndexOfAny(new[] { '\r', '\n' });
            if (nl >= 0) t = t.Substring(0, nl) + "…";
            return t.Length > 36 ? t.Substring(0, 35) + "…" : t;
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

        partial void OnAvatarIndexChanged(int value)
        {
            if (_syncing || value < 0 || value >= _avatarEntries.Count) return;
            _avatars.Pick(_avatarEntries[value]);
        }

        partial void OnBodySourceIndexChanged(int value)
        {
            if (!_syncing && value >= 0) _session.Edit("bodySource", e => e.BodySource = BodySourceIds[value]);
        }

        partial void OnLocomotionChanged(bool value)
        {
            if (!_syncing) _session.Edit("locomotion", e => e.IncludeLocomotion = value);
        }

        partial void OnJumpsChanged(bool value)
        {
            if (!_syncing) _session.Edit("jumps", e => e.IncludeJumps = value);
        }

        partial void OnSmoothingChanged(double value)
        {
            if (!_syncing)
                _host.SettingsStore.Update(nameof(VRMR.Settings.Smoothing),
                    s => s.Smoothing = (float)Math.Max(0, Math.Min(1, value)));
        }

        [RelayCommand]
        void ResetSmoothing()
        {
            Smoothing = new Settings().Smoothing;
        }

        [RelayCommand]
        void LinkAvatar()
        {
            var entry = AvatarIndex > 0 && AvatarIndex < _avatarEntries.Count ? _avatarEntries[AvatarIndex] : null;
            _avatars.Link(entry);
        }

        [RelayCommand]
        async Task UseUnityModel()
        {
            if (PickUnityModel == null || _session.Take == null) return;
            string path = await PickUnityModel();
            if (path == null) return;
            string problem = _avatars.UseUnityModel(path, true);
            if (problem != null) _host.Status.Notify(problem, StatusKind.Warning);
            else _host.Status.Info("This take now uses the skeleton of that model");
        }

        partial void OnSelectedLossChanged(TrackerLossInfo value)
        {
            LossToggleLabel = value?.ToggleLabel ?? "Keep as recorded";
        }

        [RelayCommand]
        void ToggleLoss()
        {
            if (SelectedLoss != null) _trackers.Toggle(SelectedLoss.Gap);
        }

        [RelayCommand]
        void ShowLoss()
        {
            var g = SelectedLoss?.Gap;
            var take = _session.Take;
            if (g == null || take == null) return;
            _workspace.Timeline.SetSelection(Math.Max(0, g.A), double.IsPositiveInfinity(g.B) ? take.Duration : g.B);
            _workspace.Timeline.Scrub(Math.Max(0, g.A - 0.3));
        }

        [RelayCommand]
        void MarkLost()
        {
            var tl = _workspace.Timeline;
            if (MarkTrackerIndex < 0 || MarkTrackerIndex >= _markable.Count ||
                !_trackers.MarkLost(_markable[MarkTrackerIndex].stream, tl.SelectionStart, tl.SelectionEnd))
                _host.Status.Info("Select the stretch on the timeline first (drag in the lanes), then mark it.");
        }

        [RelayCommand]
        void ClearMarks()
        {
            _trackers.ClearMarks();
        }

        partial void OnMoveXChanged(double value)
        {
            PlacementFromFields();
        }

        partial void OnMoveYChanged(double value)
        {
            PlacementFromFields();
        }

        partial void OnMoveZChanged(double value)
        {
            PlacementFromFields();
        }

        partial void OnTurnXChanged(double value)
        {
            PlacementFromFields();
        }

        partial void OnTurnYChanged(double value)
        {
            PlacementFromFields();
        }

        partial void OnTurnZChanged(double value)
        {
            PlacementFromFields();
        }

        void PlacementFromFields()
        {
            if (_syncing || _session.Take == null) return;
            var p = PlacementService.FromValues((float)MoveX, (float)MoveY, (float)MoveZ, (float)TurnX, (float)TurnY,
                (float)TurnZ);
            if (PlacementService.Same(p, _host.Placement.Current)) return;
            _host.Placement.Set(p);
        }

        [RelayCommand]
        void ResetPlacement()
        {
            _host.Placement.Reset();
        }

        partial void OnSourceIndexChanged(int value)
        {
            if (value < 0 || value >= _sources.Count) return;
            if (!_syncing) _calibration.Pick(_sources[value]);
            DescribeSource();
        }

        partial void OnSelectedPresetChanged(string value)
        {
            if (!string.IsNullOrEmpty(value)) PresetName = value;
        }

        void DescribeSource()
        {
            var src = SourceIndex >= 0 && SourceIndex < _sources.Count ? _sources[SourceIndex] : null;
            var note = _calibration.Describe(src);
            SourceNote = note.Short ?? "";
            SourceTip = note.Full ?? "";
            SourceWarning = note.Warning;
            CanDeleteSource = src?.Kind == CalibrationChoiceKind.Library;
        }

        [RelayCommand]
        void CalibrateAtPlayhead()
        {
            _calibration.UseMomentAtPlayhead();
        }

        [RelayCommand]
        void FindTPose()
        {
            _calibration.FindTPose();
        }

        [RelayCommand]
        void MakeDefault()
        {
            _calibration.MakeDefault();
        }

        [RelayCommand]
        void UseDefaults()
        {
            _calibration.UseDefaults();
        }

        [RelayCommand]
        void AddVrcChange()
        {
            _calibration.AddVrcChange(VrcChangeKind);
        }

        [RelayCommand]
        void ClearVrcChanges()
        {
            _calibration.ClearVrcChanges();
        }

        [RelayCommand]
        void LoadPreset()
        {
            if (!string.IsNullOrEmpty(SelectedPreset)) _calibration.LoadPreset(SelectedPreset);
            else _host.Status.Info("Pick a preset in the list first.");
        }

        [RelayCommand]
        void SavePreset()
        {
            if (!_calibration.SavePreset(PresetName)) _host.Status.Info("Type a name for the preset first.");
        }

        [RelayCommand]
        void DeletePreset()
        {
            if (!string.IsNullOrEmpty(SelectedPreset)) _calibration.DeletePreset(SelectedPreset);
        }

        [RelayCommand]
        void CaptureNow()
        {
            _calibration.CaptureNow();
        }

        [RelayCommand]
        void SaveTakeCalibration()
        {
            _calibration.SaveTakeCalibration();
        }

        [RelayCommand]
        void DeleteSource()
        {
            var src = SourceIndex >= 0 && SourceIndex < _sources.Count ? _sources[SourceIndex] : null;
            if (src?.Kind == CalibrationChoiceKind.Library) _calibration.DeleteFromLibrary(src.Snapshot);
        }

        partial void OnFingerSourceIndexChanged(int value)
        {
            if (!_syncing && value >= 0) _session.Edit("fingerSource", e => e.FingerSource = FingerSourceIds[value]);
        }

        partial void OnFaceOffsetChanged(double value)
        {
            if (!_syncing) _session.Edit("faceOffset", e => e.FaceOffset = value);
        }

        partial void OnReplayFaceChanged(bool value)
        {
            _host.FaceReplay.Enabled = value;
            if (value != _host.FaceReplay.Enabled) ReplayFace = _host.FaceReplay.Enabled;
        }

        [RelayCommand]
        void ResetFaceOffset()
        {
            FaceOffset = 0;
        }

        // ------------------------------------------------------------------------------------------ sync

        void SyncAll()
        {
            var r = _session.Take;
            HasTake = r != null;
            IsDirty = _session.Dirty;
            _syncing = true;
            try
            {
                Name = r?.Name ?? "";
                Notes = r?.Notes ?? "";
                NotesSummary = Summarise(r?.Notes);
            }
            finally { _syncing = false; }

            SyncInfo();
            SyncEdits();
            SyncAvatar();
            SyncSolve();
            SyncSolveState();
        }

        void SyncInfo()
        {
            var r = _session.Take;
            if (r == null)
            {
                InfoLine = "";
                DetailText = "";
                return;
            }

            bool edited = Math.Abs(r.EditedDuration - r.Duration) > 0.005;
            InfoLine = $"{r.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {TimeText.Format(r.Duration)}" +
                       (edited ? $" → {TimeText.Format(r.EditedDuration)}" : "") +
                       (string.IsNullOrEmpty(r.WorldName) ? "" : " · " + r.WorldName);
            int face = r.Params.Count(p => FaceReplayService.IsFaceParameter(p.Name));
            var hmd = r.Devices.FirstOrDefault(d => d.DeviceClass == 1);
            DetailText =
                $"Recorded {r.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}, {TimeText.Format(r.Duration)}" +
                (edited ? $" ({TimeText.Format(r.EditedDuration)} after trims and cuts)" : "") + "\n" +
                $"Avatar: {(string.IsNullOrEmpty(r.AvatarName) ? r.AvatarId : r.AvatarName)}\n" +
                (string.IsNullOrEmpty(r.WorldName) ? "" : $"World: {r.WorldName}\n") +
                $"SteamVR devices: {r.Devices.Count()}" + (hmd != null ? $" at {hmd.Rate:0} Hz" : "") + "\n" +
                $"VRChat's own IK: {(r.Humr.Count > 0 ? r.Humr.Count + " frames" : "none")}\n" +
                $"VRChat poses: {(r.HasVrcPoses ? "yes" : "no")}\n" +
                $"Parameters: {r.Params.Count} ({face} face)\n" +
                $"Finger tracking: {(r.HasFingers ? "SteamVR" : "none (from gestures)")}";
        }

        /// <summary>Everything that follows the edit list.</summary>
        void SyncEdits()
        {
            var r = _session.Take;
            _syncing = true;
            try
            {
                if (r != null)
                {
                    BodySourceIndex = Math.Max(0, Array.IndexOf(BodySourceIds, r.Edits.BodySource));
                    Locomotion = r.Edits.IncludeLocomotion;
                    Jumps = r.Edits.IncludeJumps;
                    FingerSourceIndex = Math.Max(0, Array.IndexOf(FingerSourceIds, r.Edits.FingerSource));
                    FaceOffset = r.Edits.FaceOffset;
                    FaceOffsetChanged = Math.Abs(r.Edits.FaceOffset) > 1e-6;
                    HasMarks = r.Edits.TrackerGaps.Count > 0;
                    HasVrcChanges = r.Edits.VrcChanges.Count > 0;
                }

                var p = r?.Edits.Placement ?? Pose.Identity;
                var euler = p.rot.ToEulerUnity();
                MoveX = Math.Round(p.pos.x * 100, 1);
                MoveY = Math.Round(p.pos.y * 100, 1);
                MoveZ = Math.Round(p.pos.z * 100, 1);
                TurnX = Math.Round(PlacementService.WrapDegrees(euler.x), 1);
                TurnY = Math.Round(PlacementService.WrapDegrees(euler.y), 1);
                TurnZ = Math.Round(PlacementService.WrapDegrees(euler.z), 1);
                HasPlacement = r != null && r.Edits.HasPlacement;
                PlacementSummary = _host.Placement.Summary();
                PlacementTone = HasPlacement ? Tone.Accent : Tone.Neutral;
            }
            finally { _syncing = false; }

            SyncInfo();
            SyncBody();
            SyncAvatar();
            SyncTrackers();
            SyncCalibration();
        }

        void SyncBody()
        {
            _syncing = true;
            try
            {
                Smoothing = Math.Round(_host.Settings.Smoothing, 2);
                SmoothingChanged = Math.Abs(_host.Settings.Smoothing - new Settings().Smoothing) > 1e-4f;
            }
            finally { _syncing = false; }
        }

        void SyncAvatar()
        {
            var r = _session.Take;
            _syncing = true;
            try
            {
                _avatarEntries = _avatars.Choices();
                AvatarChoices.Clear();
                AvatarChoices.Add(r == null ? "Automatic" : $"Automatic ({_avatars.RecordedLabel()})");
                foreach (var e in _avatarEntries.Skip(1)) AvatarChoices.Add(e.Label);
                AvatarIndex = _avatars.SelectedIndex(_avatarEntries);
                var note = _avatars.Describe();
                AvatarNote = note.Short ?? "";
                AvatarTip = note.Full ?? "";
                AvatarWarning = note.Warning;
            }
            finally { _syncing = false; }
        }

        void SyncTrackers()
        {
            var r = _session.Take;
            TrackerRows.Clear();
            foreach (var t in _trackers.Trackers()) TrackerRows.Add(new TrackerRowViewModel(_trackers, t));
            NoTrackers = r != null && TrackerRows.Count == 0;
            RolesSummary = _trackers.RolesSummary();

            var selected = SelectedLoss?.Gap;
            Losses.Clear();
            foreach (var l in _trackers.Losses()) Losses.Add(l);
            HasLosses = Losses.Count > 0;
            SelectedLoss = Losses.FirstOrDefault(l =>
                               selected != null && l.Gap.Serial == selected.Serial &&
                               Math.Abs(l.Gap.A - selected.A) < 1e-6) ??
                           Losses.FirstOrDefault();
            LossSummary = _trackers.LossSummary(out bool repaired);
            LossTone = repaired ? Tone.Warning : Tone.Neutral;
            LossInfo = r == null ? "" : Losses.Count == 0 ? "No tracking loss was found in this take." : "";
            _markable = _trackers.MarkableTrackers();
            int keep = MarkTrackerIndex;
            MarkTrackers.Clear();
            foreach (var m in _markable) MarkTrackers.Add(m.name);
            MarkTrackerIndex = _markable.Count == 0 ? -1 : Math.Max(0, Math.Min(_markable.Count - 1, keep));
            CanMarkLost = _workspace.Timeline.HasSelection;
        }

        void SyncCalibration()
        {
            OnPropertyChanged(nameof(SavedCalibrations));
            var r = _session.Take;
            _syncing = true;
            try
            {
                _sources = _calibration.Sources(out int selected);
                Sources.Clear();
                foreach (var s in _sources) Sources.Add(s);
                SourceIndex = _sources.Count == 0 ? -1 : selected;
                Presets.Clear();
                foreach (string p in _calibration.Presets()) Presets.Add(p);
                if (SelectedPreset != null && !Presets.Contains(SelectedPreset)) SelectedPreset = null;
                SettingsOwner = _calibration.SettingsOwnerText();
                TakeHasOwnSettings = _calibration.TakeHasOwnSettings;
                bool own = r?.Edits.Calib != null && !r.Edits.Calib.SameAs(_host.Settings.CalibrationDefaults);
                CalibrationSummary = r == null ? "the defaults" : own ? "this take's own" : "defaults";
                CalibrationTone = r == null ? Tone.Accent : Tone.Neutral;
                string vrc = _calibration.VrcSettingsText();
                VrcSettingsText = vrc;
                VrcSettingsSummary = vrc.Length > 30 ? vrc.Substring(0, 29) + "…" : vrc;
                int count = _calibration.CaptureCountdown;
                CaptureLabel = count > 0 ? $"Capturing in {count}…" : "Capture now";
            }
            finally { _syncing = false; }

            DescribeSource();
            Settings.Sync();
            SyncCalibrationResult();
        }

        void SyncCalibrationResult()
        {
            var r = _session.Take;
            var clip = _session.Clip;
            var res = clip?.Calibration;
            if (r == null)
            {
                CalibrationPose = "";
                CalibrationResult = "";
                CalibrationPoseWarning = false;
                return;
            }

            if (res == null)
            {
                CalibrationResult = clip != null && clip.BodySource == "humr"
                    ? "The body comes from VRChat's own IK: calibration settings do not apply."
                    : "";
                CalibrationPose = clip != null
                    ? ""
                    : r.Edits.CalibrationTime == EditList.UseSnapshot
                        ? "Calibration: " + (r.Edits.Snapshot?.Label ?? "")
                        : r.Edits.CalibrationTime >= 0
                            ? $"Calibration moment: {TimeText.Format(r.Edits.CalibrationTime)}"
                            : "Calibration moment: found automatically";
                CalibrationPoseWarning = false;
                return;
            }

            var warnings = clip.Warnings.Where(w => w.StartsWith("Calibration snapshot", StringComparison.Ordinal))
                .ToList();
            CalibrationPose = "Calibration: " + res.PoseText;
            CalibrationPoseWarning = warnings.Count > 0 || res.PoseText.Contains("not a T-pose") ||
                                     res.PoseText.Contains("no T-pose");
            CalibrationResult = "Scale: " + res.ScaleText + "\n" + res.Summary() +
                                (warnings.Count > 0 ? "\n" + string.Join("\n", warnings) : "");
        }

        /// <summary>A new solve arrived: results, automatic roles and found losses may have changed.</summary>
        void SyncSolve()
        {
            var r = _session.Take;
            var clip = _session.Clip;
            if (r != null)
            {
                var jumps = VrcJumps.For(r, r.Profile);
                JumpsLabel = jumps.Any
                    ? $"VRChat jumps ({jumps.Jumps.Count} in this take)"
                    : "VRChat jumps (none in this take)";
            }

            SolverNotes = clip == null
                ? ""
                : $"{clip.Face.Count} face shapes" +
                  (clip.Warnings.Count > 0 ? "\n" + string.Join("\n", clip.Warnings) : "");
            DetailsSummary = clip != null && clip.Warnings.Count > 0
                ? clip.Warnings.Count + (clip.Warnings.Count == 1 ? " solver note" : " solver notes")
                : "";
            SyncTrackers();
            Settings.Sync();
            SyncCalibrationResult();
            SyncAvatar();
        }

        void SyncSolveState()
        {
            SolveLine = _workspace.SolveText;
            var st = _session.Solve;
            SolveWarning = st == SolveState.Failed || (st == SolveState.Done && _session.Clip?.BodySource == "none");
            if (st == SolveState.Failed) SolveLine = "Solve failed: " + _session.SolveError;
        }

        /// <summary>The inspector became visible or hidden: the calibration watch text only refreshes while it shows.</summary>
        public void SetVisible(bool visible)
        {
            if (visible && _watchTimer == null && _host.CaptureOn)
            {
                WatchText = _calibration.WatchText();
                _watchTimer = _host.Ui.Every(TimeSpan.FromSeconds(2), () => WatchText = _calibration.WatchText());
            }
            else if (!visible)
            {
                _watchTimer?.Dispose();
                _watchTimer = null;
            }
        }
    }
}
