using System;
using System.Collections.ObjectModel;
using System.IO;
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
        [ObservableProperty] string _detail = "";
        [ObservableProperty] string _fix = "";
        [ObservableProperty] string _label = "";
        [ObservableProperty] SourceLevel _level;
        [ObservableProperty] string _stateText = "";
        public string Id { get; init; } = "";
        public bool HasFix => !string.IsNullOrEmpty(Fix);

        public void Update(SourceState s)
        {
            Label = s.Label;
            StateText = s.StateText;
            Level = s.Level;
            Detail = s.Detail;
            Fix = s.Fix;
            OnPropertyChanged(nameof(HasFix));
        }
    }

    /// <summary>A toast on screen.</summary>
    public partial class ToastViewModel : ObservableObject
    {
        readonly Action<ToastViewModel> _close;

        public ToastViewModel(Toast toast, Action<ToastViewModel> close)
        {
            Toast = toast;
            _close = close;
        }

        public Toast Toast { get; }
        public string Text => Toast.Text;
        public StatusKind Kind => Toast.Kind;
        public bool IsError => Toast.Kind == StatusKind.Error;
        public bool IsWarning => Toast.Kind == StatusKind.Warning;
        public bool IsSuccess => Toast.Kind == StatusKind.Success;
        public string ActionLabel => Toast.ActionLabel;
        public bool HasAction => Toast.ActionLabel != null && Toast.Action != null;

        [RelayCommand]
        void Run()
        {
            Toast.Action?.Invoke();
            _close(this);
        }

        [RelayCommand]
        void Close()
        {
            _close(this);
        }
    }

    /// <summary>
    ///     The window around the workspaces: the record button, the workspace switch, the source pills, the status bar and
    ///     toasts, which panels are folded, and the pages that open over the workspace (Settings, the first-run guide).
    /// </summary>
    public partial class ShellViewModel : ObservableObject
    {
        [ObservableProperty] bool _hasProgress;
        [ObservableProperty] bool _helpOpen;
        [ObservableProperty] bool _inspectorOpen;
        [ObservableProperty] double _inspectorWidth;
        [ObservableProperty] bool _isRecording;
        [ObservableProperty] bool _libraryOpen;
        [ObservableProperty] double _libraryWidth;
        [ObservableProperty] bool _progressIndeterminate;
        [ObservableProperty] string _progressText = "";
        [ObservableProperty] double _progressValue;
        [ObservableProperty] string _recordText = "Record";
        IDisposable _recordTimer, _sourceTimer;
        [ObservableProperty] string _recordTip = "";
        bool _sceneAvailable = AppFeatures.SceneWorkspace;
        [ObservableProperty] bool _settingsOpen;
        [ObservableProperty] StatusKind _statusKind;
        [ObservableProperty] string _statusText = "";
        [ObservableProperty] double _timelineHeight;
        [ObservableProperty] bool _timelineOpen;

        [ObservableProperty] int _workspaceIndex;

        public ShellViewModel(AppHost host)
        {
            Host = host;
            Library = new LibraryViewModel(host);
            Takes = new TakesWorkspaceViewModel(host);
            Takes.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TakesWorkspaceViewModel.WorkflowIndex)) UpdatePanelVisibility();
            };
            SettingsPage = new SettingsViewModel(host);
            FirstRun = new FirstRunViewModel(host);
            CalibrationDefaults = new CalibrationSettingsViewModel(host.Calibration, () => null, true);
            SettingsPage.CloseRequested += () => SettingsOpen = false;
            SettingsPage.SetupGuideRequested += () =>
            {
                SettingsOpen = false;
                FirstRun.Open();
            };
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
            host.SettingsStore.Changed += what =>
            {
                if (what == nameof(Settings.HotkeyRecord) || what == "") UpdateRecord();
            };
            UpdateRecord();
            UpdateStatus();
            UpdateSources();
            if (host.CaptureOn) _sourceTimer = host.Ui.Every(TimeSpan.FromSeconds(1), UpdateSources);
            host.Session.TakeChanged += () => host.UiState.Set("lastTake", host.Session.Take?.FilePath ?? "");
        }

        public AppHost Host { get; }

        public LibraryViewModel Library { get; }
        public TakesWorkspaceViewModel Takes { get; }
        public SettingsViewModel SettingsPage { get; }
        public FirstRunViewModel FirstRun { get; }

        /// <summary>The calibration defaults editor shown on the Settings page.</summary>
        public CalibrationSettingsViewModel CalibrationDefaults { get; }

        public ObservableCollection<SourcePillViewModel> Sources { get; } = new();
        public ObservableCollection<ToastViewModel> Toasts { get; } = new();

        /// <summary>
        ///     The Scene workspace can be switched to (<see cref="AppFeatures.SceneWorkspace" />); without it the switch is
        ///     hidden.
        /// </summary>
        public bool SceneAvailable
        {
            get => _sceneAvailable;
            internal set
            {
                if (SetProperty(ref _sceneAvailable, value) && !value) WorkspaceIndex = 0;
            }
        }

        public Workspace Workspace => WorkspaceIndex == 1 ? Workspace.Scene : Workspace.Takes;
        public bool IsTakes => WorkspaceIndex == 0;
        public bool IsScene => WorkspaceIndex == 1;
        public bool TimelineVisible => TimelineOpen && !ShowCalibrations;
        public bool ShowCalibrations => IsTakes && Takes.ShowDevicesTab;

        /// <summary>Opens the take that was open when the app was closed, if it is still in the recordings folder.</summary>
        public void ReopenLastTake()
        {
            string last = Host.UiState.GetString("lastTake", "");
            if (last.Length == 0 || Host.Recording.IsRecording) return;
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(last));
                char[] slashes = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
                bool inLibrary = string.Equals(dir.TrimEnd(slashes),
                    Path.GetFullPath(Host.Settings.RecordingsDir).TrimEnd(slashes), StringComparison.OrdinalIgnoreCase);
                if (inLibrary && File.Exists(last)) _ = Host.Session.OpenAsync(last);
            }
            catch (Exception ex) { AppLog.Warn("Reopening the last take: " + ex.Message); }
        }

        /// <summary>Raised when the workspace switched (the Scene workspace hooks in here).</summary>
        public event Action<Workspace> WorkspaceChanged;

        partial void OnWorkspaceIndexChanged(int value)
        {
            if (value != 0 && !SceneAvailable)
            {
                WorkspaceIndex = 0;
                return;
            } // nothing leads to a workspace that is switched off

            OnPropertyChanged(nameof(IsTakes));
            OnPropertyChanged(nameof(IsScene));
            OnPropertyChanged(nameof(Workspace));
            UpdatePanelVisibility();
            Takes.SetActive(IsTakes);
            WorkspaceChanged?.Invoke(Workspace);
        }

        void UpdatePanelVisibility()
        {
            OnPropertyChanged(nameof(ShowCalibrations));
            OnPropertyChanged(nameof(TimelineVisible));
        }

        partial void OnLibraryOpenChanged(bool value)
        {
            Host.UiState.Set("panel.library", value);
        }

        partial void OnInspectorOpenChanged(bool value)
        {
            Host.UiState.Set("panel.inspector", value);
        }

        partial void OnTimelineOpenChanged(bool value)
        {
            OnPropertyChanged(nameof(TimelineVisible));
            Host.UiState.Set("panel.timeline", value);
        }

        partial void OnLibraryWidthChanged(double value)
        {
            Host.UiState.Set("panel.library.width", Math.Round(value));
        }

        partial void OnInspectorWidthChanged(double value)
        {
            Host.UiState.Set("panel.inspector.width", Math.Round(value));
        }

        partial void OnTimelineHeightChanged(double value)
        {
            Host.UiState.Set("panel.timeline.height", Math.Round(value));
        }

        // ------------------------------------------------------------------------------------------ record

        [RelayCommand]
        void ToggleRecord()
        {
            Host.Recording.Toggle();
        }

        void UpdateRecord()
        {
            var rec = Host.Recording;
            IsRecording = rec.IsRecording;
            string hotkey = Host.Settings.HotkeyRecord;
            RecordTip = !Host.CaptureOn
                ? "Recording is off in this session (started with --no-capture)."
                : rec.HotkeyProblem != null
                    ? "Start or stop recording. " + rec.HotkeyProblem
                    : string.IsNullOrWhiteSpace(hotkey)
                        ? "Start or stop recording. Set a global hotkey under Settings → Recording."
                        : $"Start or stop recording ({hotkey} works while VRChat has the focus).";
            UpdateRecordText();
            // the running time only ticks while recording
            if (IsRecording && _recordTimer == null)
                _recordTimer = Host.Ui.Every(TimeSpan.FromMilliseconds(250), UpdateRecordText);
            else if (!IsRecording)
            {
                _recordTimer?.Dispose();
                _recordTimer = null;
            }
        }

        void UpdateRecordText()
        {
            var rec = Host.Recording;
            string text = rec.IsRecording ? "Stop  " + TimeText.Format(rec.Elapsed, false) : "Record";
            if (text != RecordText) RecordText = text;
        }

        // ------------------------------------------------------------------------------------------ sources

        void UpdateSources()
        {
            var states = SourceStatus.Read(Host.Live);
            for (int i = Sources.Count - 1; i >= 0; i--)
                if (!states.Any(s => s.Id == Sources[i].Id))
                    Sources.RemoveAt(i);
            for (int i = 0; i < states.Count; i++)
            {
                var vm = Sources.FirstOrDefault(p => p.Id == states[i].Id);
                if (vm == null)
                {
                    vm = new SourcePillViewModel { Id = states[i].Id };
                    Sources.Insert(Math.Min(i, Sources.Count), vm);
                }

                vm.Update(states[i]);
            }
        }

        // ------------------------------------------------------------------------------------------ status and toasts

        void UpdateStatus()
        {
            var st = Host.Status;
            StatusText = st.Message;
            StatusKind = st.Kind;
            var p = st.Current;
            HasProgress = p != null;
            if (p != null)
            {
                ProgressText = p.Label;
                ProgressIndeterminate = p.Fraction < 0;
                ProgressValue = Math.Max(0, Math.Min(1, p.Fraction));
            }
        }

        void ShowToast(Toast toast)
        {
            var vm = new ToastViewModel(toast, t => Toasts.Remove(t));
            Toasts.Add(vm);
            while (Toasts.Count > 3) Toasts.RemoveAt(0);
            Host.Ui.After(toast.Duration, () => Toasts.Remove(vm));
        }

        // ------------------------------------------------------------------------------------------ pages and panels

        [RelayCommand]
        void ToggleLibrary()
        {
            LibraryOpen = !LibraryOpen;
        }

        [RelayCommand]
        void ToggleInspector()
        {
            InspectorOpen = !InspectorOpen;
        }

        [RelayCommand]
        void ToggleTimeline()
        {
            if (!ShowCalibrations) TimelineOpen = !TimelineOpen;
        }

        [RelayCommand]
        void OpenSettings()
        {
            SettingsOpen = true;
        }

        [RelayCommand]
        void CloseSettings()
        {
            SettingsOpen = false;
        }

        [RelayCommand]
        void ToggleHelp()
        {
            HelpOpen = !HelpOpen;
        }

        /// <summary>Closes the page on top (Esc); false when none was open.</summary>
        public bool CloseTopPage()
        {
            if (HelpOpen)
            {
                HelpOpen = false;
                return true;
            }

            if (FirstRun.IsOpen)
            {
                FirstRun.Close();
                return true;
            }

            if (SettingsOpen)
            {
                SettingsOpen = false;
                return true;
            }

            return false;
        }
    }
}
