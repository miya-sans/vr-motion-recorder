using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VRMR.App.Composition;
using VRMR.App.Services;

namespace VRMR.App.ViewModels
{
    /// <summary>The two workspaces of the window.</summary>
    public enum Workspace { Takes, Scene }

    /// <summary>A source pill in the top bar.</summary>
    public partial class SourcePillViewModel : ObservableObject
    {
        [ObservableProperty] string _label = "";
        [ObservableProperty] string _stateText = "";
        [ObservableProperty] SourceLevel _level;
        [ObservableProperty] string _detail = "";
        [ObservableProperty] string _fix = "";
        public string Id { get; init; } = "";
        public bool HasFix => !string.IsNullOrEmpty(Fix);

        public void Update(SourceState s)
        {
            Label = s.Label; StateText = s.StateText; Level = s.Level; Detail = s.Detail; Fix = s.Fix;
            OnPropertyChanged(nameof(HasFix));
        }
    }

    /// <summary>A toast on screen.</summary>
    public partial class ToastViewModel : ObservableObject
    {
        readonly Action<ToastViewModel> _close;
        public ToastViewModel(Toast toast, Action<ToastViewModel> close) { Toast = toast; _close = close; }

        public Toast Toast { get; }
        public string Text => Toast.Text;
        public StatusKind Kind => Toast.Kind;
        public bool IsError => Toast.Kind == StatusKind.Error;
        public bool IsWarning => Toast.Kind == StatusKind.Warning;
        public bool IsSuccess => Toast.Kind == StatusKind.Success;
        public string ActionLabel => Toast.ActionLabel;
        public bool HasAction => Toast.ActionLabel != null && Toast.Action != null;

        [RelayCommand] void Run() { Toast.Action?.Invoke(); _close(this); }
        [RelayCommand] void Close() => _close(this);
    }

    /// <summary>
    /// The window around the workspaces: the record button, the workspace switch, the source pills, the status bar and
    /// toasts, which panels are folded, and the pages that open over the workspace (Settings, the first-run guide).
    /// </summary>
    public partial class ShellViewModel : ObservableObject
    {
        readonly AppHost _host;
        IDisposable _recordTimer, _sourceTimer;

        public ShellViewModel(AppHost host)
        {
            _host = host;
            Library = new LibraryViewModel(host);
            Takes = new TakesWorkspaceViewModel(host);
            SettingsPage = new SettingsViewModel(host);
            FirstRun = new FirstRunViewModel(host);
            CalibrationDefaults = new CalibrationSettingsViewModel(host.Calibration, () => null, defaultsOnly: true);
            SettingsPage.CloseRequested += () => SettingsOpen = false;
            SettingsPage.SetupGuideRequested += () => { SettingsOpen = false; FirstRun.Open(); };
            var ui = host.UiState;
            _libraryOpen = ui.GetBool("panel.library", true);
            _inspectorOpen = ui.GetBool("panel.inspector", true);
            _timelineOpen = ui.GetBool("panel.timeline", true);
            _libraryWidth = ui.GetNumber("panel.library.width", 260);
            _inspectorWidth = ui.GetNumber("panel.inspector.width", 320);
            _timelineHeight = ui.GetNumber("panel.timeline.height", 168);

            host.Recording.StateChanged += UpdateRecord;
            host.Status.Changed += UpdateStatus;
            host.Status.ToastRaised += ShowToast;
            host.SettingsStore.Changed += what => { if (what == nameof(Settings.HotkeyRecord) || what == "") UpdateRecord(); };
            UpdateRecord();
            UpdateStatus();
            UpdateSources();
            if (host.CaptureOn) _sourceTimer = host.Ui.Every(TimeSpan.FromSeconds(1), UpdateSources);
            host.Session.TakeChanged += () => host.UiState.Set("lastTake", host.Session.Take?.FilePath ?? "");
        }

        /// <summary>Opens the take that was open when the app was closed, if it is still in the recordings folder.</summary>
        public void ReopenLastTake()
        {
            string last = _host.UiState.GetString("lastTake", "");
            if (last.Length == 0 || _host.Recording.IsRecording) return;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(last));
                char[] slashes = { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar };
                bool inLibrary = string.Equals(dir.TrimEnd(slashes), System.IO.Path.GetFullPath(_host.Settings.RecordingsDir).TrimEnd(slashes), StringComparison.OrdinalIgnoreCase);
                if (inLibrary && System.IO.File.Exists(last)) _ = _host.Session.OpenAsync(last);
            }
            catch (Exception ex) { AppLog.Warn("Reopening the last take: " + ex.Message); }
        }

        public AppHost Host => _host;
        public LibraryViewModel Library { get; }
        public TakesWorkspaceViewModel Takes { get; }
        public SettingsViewModel SettingsPage { get; }
        public FirstRunViewModel FirstRun { get; }
        /// <summary>The calibration defaults editor shown on the Settings page.</summary>
        public CalibrationSettingsViewModel CalibrationDefaults { get; }
        public ObservableCollection<SourcePillViewModel> Sources { get; } = new ObservableCollection<SourcePillViewModel>();
        public ObservableCollection<ToastViewModel> Toasts { get; } = new ObservableCollection<ToastViewModel>();

        [ObservableProperty] int _workspaceIndex;
        [ObservableProperty] bool _isRecording;
        [ObservableProperty] string _recordText = "Record";
        [ObservableProperty] string _recordTip = "";
        [ObservableProperty] bool _libraryOpen;
        [ObservableProperty] bool _inspectorOpen;
        [ObservableProperty] bool _timelineOpen;
        [ObservableProperty] double _libraryWidth;
        [ObservableProperty] double _inspectorWidth;
        [ObservableProperty] double _timelineHeight;
        [ObservableProperty] bool _settingsOpen;
        [ObservableProperty] bool _helpOpen;
        [ObservableProperty] string _statusText = "";
        [ObservableProperty] StatusKind _statusKind;
        [ObservableProperty] bool _hasProgress;
        [ObservableProperty] string _progressText = "";
        [ObservableProperty] double _progressValue;
        [ObservableProperty] bool _progressIndeterminate;

        /// <summary>The Scene workspace can be switched to (<see cref="AppFeatures.SceneWorkspace"/>); without it the switch is hidden.</summary>
        public bool SceneAvailable
        {
            get => _sceneAvailable;
            internal set { if (SetProperty(ref _sceneAvailable, value) && !value) WorkspaceIndex = 0; }
        }
        bool _sceneAvailable = AppFeatures.SceneWorkspace;

        public Workspace Workspace => WorkspaceIndex == 1 ? Workspace.Scene : Workspace.Takes;
        public bool IsTakes => WorkspaceIndex == 0;
        public bool IsScene => WorkspaceIndex == 1;

        /// <summary>Raised when the workspace switched (the Scene workspace hooks in here).</summary>
        public event Action<Workspace> WorkspaceChanged;

        partial void OnWorkspaceIndexChanged(int value)
        {
            if (value != 0 && !SceneAvailable) { WorkspaceIndex = 0; return; } // nothing leads to a workspace that is switched off
            OnPropertyChanged(nameof(IsTakes)); OnPropertyChanged(nameof(IsScene)); OnPropertyChanged(nameof(Workspace));
            Takes.SetActive(IsTakes);
            WorkspaceChanged?.Invoke(Workspace);
        }

        partial void OnLibraryOpenChanged(bool value) => _host.UiState.Set("panel.library", value);
        partial void OnInspectorOpenChanged(bool value) => _host.UiState.Set("panel.inspector", value);
        partial void OnTimelineOpenChanged(bool value) => _host.UiState.Set("panel.timeline", value);
        partial void OnLibraryWidthChanged(double value) => _host.UiState.Set("panel.library.width", Math.Round(value));
        partial void OnInspectorWidthChanged(double value) => _host.UiState.Set("panel.inspector.width", Math.Round(value));
        partial void OnTimelineHeightChanged(double value) => _host.UiState.Set("panel.timeline.height", Math.Round(value));

        // ------------------------------------------------------------------------------------------ record

        [RelayCommand] void ToggleRecord() => _host.Recording.Toggle();

        void UpdateRecord()
        {
            var rec = _host.Recording;
            IsRecording = rec.IsRecording;
            string hotkey = _host.Settings.HotkeyRecord;
            RecordTip = !_host.CaptureOn ? "Recording is off in this session (started with --no-capture)."
                : rec.HotkeyProblem != null ? "Start or stop recording. " + rec.HotkeyProblem
                : string.IsNullOrWhiteSpace(hotkey) ? "Start or stop recording. Set a global hotkey under Settings → Recording."
                : $"Start or stop recording ({hotkey} works while VRChat has the focus).";
            UpdateRecordText();
            // the running time only ticks while recording
            if (IsRecording && _recordTimer == null) _recordTimer = _host.Ui.Every(TimeSpan.FromMilliseconds(250), UpdateRecordText);
            else if (!IsRecording) { _recordTimer?.Dispose(); _recordTimer = null; }
        }

        void UpdateRecordText()
        {
            var rec = _host.Recording;
            string text = rec.IsRecording ? "Stop  " + TimeText.Format(rec.Elapsed, false) : "Record";
            if (text != RecordText) RecordText = text;
        }

        // ------------------------------------------------------------------------------------------ sources

        void UpdateSources()
        {
            List<SourceState> states = SourceStatus.Read(_host.Live);
            for (int i = Sources.Count - 1; i >= 0; i--) if (!states.Any(s => s.Id == Sources[i].Id)) Sources.RemoveAt(i);
            for (int i = 0; i < states.Count; i++)
            {
                var vm = Sources.FirstOrDefault(p => p.Id == states[i].Id);
                if (vm == null) { vm = new SourcePillViewModel { Id = states[i].Id }; Sources.Insert(Math.Min(i, Sources.Count), vm); }
                vm.Update(states[i]);
            }
        }

        // ------------------------------------------------------------------------------------------ status and toasts

        void UpdateStatus()
        {
            var st = _host.Status;
            StatusText = st.Message; StatusKind = st.Kind;
            var p = st.Current;
            HasProgress = p != null;
            if (p != null) { ProgressText = p.Label; ProgressIndeterminate = p.Fraction < 0; ProgressValue = Math.Max(0, Math.Min(1, p.Fraction)); }
        }

        void ShowToast(Toast toast)
        {
            var vm = new ToastViewModel(toast, t => Toasts.Remove(t));
            Toasts.Add(vm);
            while (Toasts.Count > 3) Toasts.RemoveAt(0);
            _host.Ui.After(toast.Duration, () => Toasts.Remove(vm));
        }

        // ------------------------------------------------------------------------------------------ pages and panels

        [RelayCommand] void ToggleLibrary() => LibraryOpen = !LibraryOpen;
        [RelayCommand] void ToggleInspector() => InspectorOpen = !InspectorOpen;
        [RelayCommand] void ToggleTimeline() => TimelineOpen = !TimelineOpen;
        [RelayCommand] void OpenSettings() => SettingsOpen = true;
        [RelayCommand] void CloseSettings() => SettingsOpen = false;
        [RelayCommand] void ToggleHelp() => HelpOpen = !HelpOpen;

        /// <summary>Closes the page on top (Esc); false when none was open.</summary>
        public bool CloseTopPage()
        {
            if (HelpOpen) { HelpOpen = false; return true; }
            if (FirstRun.IsOpen) { FirstRun.Close(); return true; }
            if (SettingsOpen) { SettingsOpen = false; return true; }
            return false;
        }
    }
}
