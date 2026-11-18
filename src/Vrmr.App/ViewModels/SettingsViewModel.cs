using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VRMR.App.Composition;
using VRMR.App.Services;

namespace VRMR.App.ViewModels
{
    /// <summary>The categories of the Settings page, in the order of its list.</summary>
    public enum SettingsCategory { Sources, Recording, Folders, Avatars, CalibrationDefaults, View, About }

    /// <summary>
    /// The Settings page that opens over the workspace: app-wide settings only, one category at a time. What belongs
    /// to a single take stays in the inspector.
    /// </summary>
    public partial class SettingsViewModel : ObservableObject
    {
        const string CategoryKey = "settings.category";

        readonly AppHost _host;
        bool _active;

        public SettingsViewModel(AppHost host)
        {
            _host = host;
            Sources = new SettingsSourcesViewModel(host);
            Recording = new SettingsRecordingViewModel(host);
            Folders = new SettingsFoldersViewModel(host);
            Avatars = new SettingsAvatarsViewModel(host);
            View = new SettingsViewOptionsViewModel(host);
            _categoryIndex = (int)Math.Max(0, Math.Min(Categories.Count - 1, host.UiState.GetNumber(CategoryKey, 0)));
            host.SettingsStore.Changed += what => { if (what == nameof(Settings.RecordingsDir) || what == "") OnPropertyChanged(nameof(RecordingsFolder)); };
        }

        public SettingsSourcesViewModel Sources { get; }
        public SettingsRecordingViewModel Recording { get; }
        public SettingsFoldersViewModel Folders { get; }
        public SettingsAvatarsViewModel Avatars { get; }
        public SettingsViewOptionsViewModel View { get; }

        /// <summary>The category names, in the order of <see cref="SettingsCategory"/>.</summary>
        public IReadOnlyList<string> Categories { get; } = new[] { "Sources", "Recording", "Folders", "Avatars", "Calibration defaults", "View", "About" };

        [ObservableProperty, NotifyPropertyChangedFor(nameof(Category), nameof(CategoryTitle), nameof(CategoryIntro))] int _categoryIndex;

        /// <summary>The category on screen.</summary>
        public SettingsCategory Category => (SettingsCategory)CategoryIndex;

        /// <summary>The heading over the category.</summary>
        public string CategoryTitle => Categories[CategoryIndex];

        /// <summary>One or two sentences under the heading that say what the category is for.</summary>
        public string CategoryIntro => Intros[CategoryIndex];

        static readonly string[] Intros =
        {
            "What the app listens to. Each source shows what it finds right now, and what to do when it finds nothing.",
            "What starts and stops a recording, and what is saved along with it.",
            "Where your takes and avatars are kept, and the programs your exports go to.",
            "The base models your takes are solved onto. The avatar you wear in VRChat is matched to one by a linked avatar id, by its name, or by its eye height.",
            "Used by every take that has no calibration settings of its own.",
            "How the window looks, and what the 3D view shows of the avatar.",
            "Records what VRChat and SteamVR send, and solves it onto your avatar.",
        };

        /// <summary>The Close button was pressed: the shell hides the page (Esc does the same through the shell).</summary>
        public event Action CloseRequested;

        /// <summary>"Run the setup guide again" was pressed: the shell closes the page and opens the guide.</summary>
        public event Action SetupGuideRequested;

        partial void OnCategoryIndexChanged(int value)
        {
            // a list may clear its selection for a moment: the page always shows a category
            if (value < 0 || value >= Categories.Count) { CategoryIndex = 0; return; }
            _host.UiState.Set(CategoryKey, (double)value);
            Activate();
        }

        /// <summary>Opens the page on a category (a message that says "Settings → Avatars" can take the user there).</summary>
        public void Show(SettingsCategory category) => CategoryIndex = (int)category;

        /// <summary>Tells the page whether it is on screen: live states are only read while it is.</summary>
        public void SetActive(bool active)
        {
            _active = active;
            Activate();
        }

        void Activate()
        {
            Sources.SetActive(_active && Category == SettingsCategory.Sources);
            Avatars.SetActive(_active && Category == SettingsCategory.Avatars);
            View.SetActive(_active && Category == SettingsCategory.View);
        }

        [RelayCommand] void Close() => CloseRequested?.Invoke();

        // ------------------------------------------------------------------------------------------ about

        /// <summary>The app's version, with the short commit id when the build carries one.</summary>
        public string Version { get; } = ReadVersion();

        public string AppDataFolder => _host.Options.Paths.AppDataDir;
        public string RecordingsFolder => _host.Settings.RecordingsDir;

        /// <summary>Portable mode: everything is inside the data folder the app was started with.</summary>
        public bool IsPortable => _host.Options.DataDir != null;

        static string ReadVersion()
        {
            var assembly = typeof(SettingsViewModel).Assembly;
            string text = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString(3) ?? "";
            int plus = text.IndexOf('+');
            if (plus < 0) return text;
            string commit = text.Substring(plus + 1);
            return text.Substring(0, plus) + (commit.Length > 0 ? " (" + commit.Substring(0, Math.Min(7, commit.Length)) + ")" : "");
        }

        [RelayCommand] void OpenAppData() => FileActions.OpenFolder(AppDataFolder);
        [RelayCommand] void OpenRecordings() => _host.Library.OpenFolder();

        [RelayCommand]
        void OpenLog()
        {
            string log = _host.Options.Paths.LogFile;
            if (File.Exists(log)) FileActions.OpenFile(log);
            else _host.Status.Info("There is no log yet: nothing has been written in this session.");
        }

        [RelayCommand] void RunSetupGuide() => SetupGuideRequested?.Invoke();
    }
}
