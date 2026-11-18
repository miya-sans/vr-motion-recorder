using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VRMR.App.Composition;
using VRMR.App.Services;

namespace VRMR.App.ViewModels
{
    /// <summary>
    /// The short setup guide shown over the workspace on first run: what was found on this computer, where takes are
    /// kept, and the avatar to solve onto. Nothing in it is required: every step can be passed, and the guide can be
    /// left at any time and opened again from Settings → About.
    /// </summary>
    public partial class FirstRunViewModel : ObservableObject
    {
        /// <summary>The <see cref="UiStateStore"/> flag that says the guide has been seen.</summary>
        public const string DoneKey = "firstRun.done";

        /// <summary>The steps: what was found, the recordings folder, the avatar.</summary>
        public const int StepCount = 3;

        readonly AppHost _host;

        public FirstRunViewModel(AppHost host)
        {
            _host = host;
            Found = new ObservableCollection<StatusRowViewModel>
            {
                new StatusRowViewModel { Id = EnvironmentProbe.SteamVR, Title = "SteamVR" },
                new StatusRowViewModel { Id = EnvironmentProbe.VrChat, Title = "VRChat" },
                new StatusRowViewModel { Id = EnvironmentProbe.VrChatOsc, Title = "VRChat's OSC switch" },
                new StatusRowViewModel { Id = EnvironmentProbe.Blender, Title = "Blender" },
                new StatusRowViewModel { Id = EnvironmentProbe.Unity, Title = "Unity projects" },
            };
            host.Environment.Changed += ShowFound;
            host.AvatarService.Changed += ShowAvatars;
            host.SettingsStore.Changed += what => { if (what == nameof(Settings.RecordingsDir) || what == "") ShowFolder(); };
            ShowFound();
            ShowFolder();
            ShowAvatars();
        }

        [ObservableProperty] bool _isOpen;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowsFound), nameof(ShowsFolder), nameof(ShowsAvatar), nameof(CanGoBack), nameof(IsLastStep), nameof(StepText), nameof(StepTitle))]
        int _step;

        // what was found
        [ObservableProperty] bool _isChecking;
        [ObservableProperty] string _foundSummary = "";
        [ObservableProperty] bool _foundNeedsAttention;

        // the recordings folder
        [ObservableProperty] string _recordingsFolder = "";
        [ObservableProperty] bool _folderMoved;

        // the avatar
        [ObservableProperty] bool _isImporting;
        [ObservableProperty] string _importMessage = "";
        [ObservableProperty] string _avatarState = "";
        [ObservableProperty] bool _hasOwnAvatar;

        /// <summary>SteamVR, VRChat, VRChat's OSC switch, Blender and Unity projects, as found.</summary>
        public ObservableCollection<StatusRowViewModel> Found { get; }

        /// <summary>Whether the recordings folder can be used.</summary>
        public StatusRowViewModel FolderState { get; } = new StatusRowViewModel();

        public bool ShowsFound => Step == 0;
        public bool ShowsFolder => Step == 1;
        public bool ShowsAvatar => Step == 2;
        public bool CanGoBack => Step > 0;
        public bool IsLastStep => Step == StepCount - 1;
        public string StepText => $"Step {Step + 1} of {StepCount}";
        public string StepTitle => Step == 0 ? "What was found on this computer" : Step == 1 ? "Where to keep your takes" : "The avatar to solve onto";

        /// <summary>Portable mode: takes stay inside the data folder the app was started with.</summary>
        public bool IsPortable => _host.Options.DataDir != null;
        public bool CanMoveFolder => !IsPortable;

        /// <summary>Asks the view for a folder: the picker's title and the folder to start in. Null when cancelled.</summary>
        public Func<string, string, Task<string>> PickFolder { get; set; }
        /// <summary>Asks the view for the files of an avatar. Null or empty when cancelled.</summary>
        public Func<Task<IReadOnlyList<string>>> PickFiles { get; set; }

        // ------------------------------------------------------------------------------------------ open and close

        /// <summary>True until the guide has been finished or skipped once.</summary>
        public bool IsFirstRun => !_host.UiState.GetBool(DoneKey, false);

        /// <summary>Opens the guide when it has never been seen. Returns whether it opened.</summary>
        public bool OpenIfFirstRun()
        {
            if (!IsFirstRun) return false;
            Open();
            return true;
        }

        /// <summary>Opens the guide at its first step and looks at the computer again.</summary>
        public void Open()
        {
            Step = 0;
            IsOpen = true;
            _ = _host.Environment.RefreshAsync();
        }

        /// <summary>Closes the guide and remembers that it has been seen.</summary>
        public void Close()
        {
            IsOpen = false;
            _host.UiState.Set(DoneKey, true);
        }

        [RelayCommand] void Next() { if (!IsLastStep) Step++; }
        [RelayCommand] void Back() { if (CanGoBack) Step--; }

        [RelayCommand]
        void Skip()
        {
            Close();
            _host.Status.Info("Setup skipped. You can run the guide again under Settings → About.");
        }

        [RelayCommand]
        void Finish()
        {
            Close();
            _host.Status.Info(_host.CaptureOn ? "Setup done. Press Record while VRChat runs to make your first take." : "Setup done.");
        }

        // ------------------------------------------------------------------------------------------ what was found

        [RelayCommand] Task CheckAgain() => _host.Environment.RefreshAsync();

        void ShowFound()
        {
            var env = _host.Environment;
            IsChecking = env.Checking;
            foreach (var row in Found) row.Set(env.Item(row.Id));
            FolderState.Set(env.Item(EnvironmentProbe.RecordingsFolder));
            var report = env.Report;
            FoundNeedsAttention = report != null && !report.CanRecord;
            FoundSummary = report == null ? "Looking at this computer…"
                : report.CanRecord ? "Everything needed to record is here. What is marked grey is optional."
                : "Recording needs VRChat with OSC on: see what to do below. You can go on and come back to it later.";
        }

        // ------------------------------------------------------------------------------------------ the recordings folder

        void ShowFolder()
        {
            RecordingsFolder = _host.Settings.RecordingsDir;
            FolderMoved = CanMoveFolder && !string.Equals(RecordingsFolder.TrimEnd('\\', '/'), _host.Options.Paths.RecordingsDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        [RelayCommand]
        async Task BrowseFolder()
        {
            if (PickFolder == null || IsPortable) return;
            string dir = await PickFolder("Where to keep your takes", RecordingsFolder);
            if (dir != null) UseFolder(dir);
        }

        [RelayCommand] void UseDefaultFolder() => UseFolder(_host.Options.Paths.RecordingsDir);

        void UseFolder(string dir)
        {
            string problem = SettingsFoldersViewModel.SetRecordingsFolder(_host, dir);
            if (problem != null) _host.Status.Notify(problem, StatusKind.Error);
        }

        // ------------------------------------------------------------------------------------------ the avatar

        void ShowAvatars()
        {
            var service = _host.AvatarService;
            IsImporting = service.Importing;
            ImportMessage = service.ImportMessage;
            var own = service.Entries.Where(e => !e.BuiltIn).Select(e => e.Label).ToList();
            HasOwnAvatar = own.Count > 0;
            AvatarState = own.Count == 0 ? "No avatar of your own yet: takes are solved onto the built-in mannequin until you add one."
                : own.Count == 1 ? $"In your library: {own[0]}."
                : $"In your library: {string.Join(", ", own)}. Avatars that match none of them use {service.Default.Label}.";
        }

        [RelayCommand]
        async Task AddAvatar()
        {
            if (PickFiles == null) return;
            var files = await PickFiles();
            if (files != null && files.Count > 0) await _host.AvatarService.AddAsync(files);
        }
    }
}
