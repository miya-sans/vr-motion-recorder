using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VRMR.App.Composition;
using VRMR.App.Services;

namespace VRMR.App.ViewModels
{
    /// <summary>A linked Unity project in the list.</summary>
    public sealed class UnityProjectRowViewModel
    {
        public string Path { get; init; } = "";

        /// <summary>"Unity 2022.3.22f1 · bridge installed", or that the folder is gone.</summary>
        public string Info { get; init; } = "";

        public bool Missing { get; init; }
    }

    /// <summary>
    ///     Settings → Folders: where takes and avatars are kept, which Blender opens exports, and the Unity projects whose
    ///     avatars are matched by their VRChat id. In portable mode the two folders are fixed inside the data folder.
    /// </summary>
    public partial class SettingsFoldersViewModel : ObservableObject
    {
        readonly AppHost _host;

        [ObservableProperty] [NotifyPropertyChangedFor(nameof(AvatarsMoved))]
        string _avatarFolder;

        [ObservableProperty] string _blenderInUse = "";

        [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasBlenderPath))]
        string _blenderPath;

        [ObservableProperty] bool _hasProjects;

        [ObservableProperty] [NotifyPropertyChangedFor(nameof(RecordingsMoved))]
        string _recordingsFolder;

        [ObservableProperty] string _scanInfo = "";
        [ObservableProperty] UnityProjectRowViewModel _selectedProject;
        bool _syncing;

        public SettingsFoldersViewModel(AppHost host)
        {
            _host = host;
            var s = host.Settings;
            _recordingsFolder = s.RecordingsDir;
            _avatarFolder = s.AvatarLibraryDir;
            _blenderPath = s.BlenderPath ?? "";
            host.Environment.Changed += RefreshStates;
            host.AvatarService.Changed += RefreshProjects;
            host.SettingsStore.Changed += OnSettingChanged;
            RefreshStates();
        }

        public StatusRowViewModel Recordings { get; } = new();
        public StatusRowViewModel Avatars { get; } = new();
        public StatusRowViewModel Blender { get; } = new();
        public StatusRowViewModel Unity { get; } = new();
        public ObservableCollection<UnityProjectRowViewModel> Projects { get; } = new();

        /// <summary>Portable mode: everything stays under the data folder the app was started with.</summary>
        public bool IsPortable => _host.Options.DataDir != null;

        public bool CanMoveFolders => !IsPortable;
        public bool RecordingsMoved => CanMoveFolders && !SamePath(RecordingsFolder, _host.Options.Paths.RecordingsDir);
        public bool AvatarsMoved => CanMoveFolders && !SamePath(AvatarFolder, _host.Options.Paths.AvatarLibraryDir);
        public bool HasBlenderPath => !string.IsNullOrEmpty(BlenderPath);

        /// <summary>Asks the view for a folder: the picker's title and the folder to start in. Null when cancelled.</summary>
        public Func<string, string, Task<string>> PickFolder { get; set; }

        /// <summary>Asks the view for Blender's program file. Null when cancelled.</summary>
        public Func<Task<string>> PickBlender { get; set; }

        // ------------------------------------------------------------------------------------------ takes and avatars

        partial void OnRecordingsFolderChanged(string value)
        {
            if (_syncing) return;
            string problem = SetRecordingsFolder(_host, value);
            if (problem != null) _host.Status.Notify(problem, StatusKind.Error);
            Sync();
        }

        partial void OnAvatarFolderChanged(string value)
        {
            if (_syncing) return;
            string dir = null;
            string problem = PortableProblem(_host) ?? CheckFolder(value, out dir);
            if (problem != null) _host.Status.Notify(problem, StatusKind.Error);
            else if (!SamePath(dir, _host.Settings.AvatarLibraryDir))
            {
                _host.SettingsStore.Update(nameof(Settings.AvatarLibraryDir), s => s.AvatarLibraryDir = dir);
                _host.Status.Info($"The avatar library is now {dir}. Avatars in the old folder stay where they are.");
            }

            Sync();
        }

        /// <summary>
        ///     Keeps new takes in another folder and lists the takes found there. Takes in the old folder stay where they
        ///     are. Returns why the folder cannot be used (with what to do), or null.
        /// </summary>
        public static string SetRecordingsFolder(AppHost host, string folder)
        {
            string dir = null;
            string problem = PortableProblem(host) ?? CheckFolder(folder, out dir);
            if (problem != null) return problem;
            if (SamePath(dir, host.Settings.RecordingsDir)) return null;
            host.SettingsStore.Update(nameof(Settings.RecordingsDir), s => s.RecordingsDir = dir);
            host.Status.Info($"Takes are now kept in {dir}. Takes in the old folder stay where they are.");
            return null;
        }

        static string PortableProblem(AppHost host)
        {
            return host.Options.DataDir != null
                ? "In portable mode takes and avatars stay inside the data folder the app was started with."
                : null;
        }

        /// <summary>
        ///     Makes sure a folder the user typed or picked can be used: a whole path that exists (it is created when it
        ///     does not) and can be written. Returns what is wrong with it and what to do, or null with the folder in
        ///     <paramref name="dir" />.
        /// </summary>
        public static string CheckFolder(string folder, out string dir)
        {
            dir = null;
            string text = (folder ?? "").Trim().Trim('"');
            if (text.Length == 0) return "Type a folder, or pick one with Browse.";
            if (!Path.IsPathRooted(text))
                return
                    $"\"{text}\" is not a whole path. Start with the drive (for example D:\\Takes), or pick the folder with Browse.";
            try
            {
                dir = Path.GetFullPath(text).TrimEnd('\\', '/');
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex) { return $"{text} cannot be used: {ex.Message} Pick another folder."; }

            return FolderAccess.CanWrite(dir) ? null : $"{dir} cannot be written to. Pick another folder.";
        }

        [RelayCommand]
        async Task BrowseRecordings()
        {
            if (PickFolder == null || IsPortable) return;
            string dir = await PickFolder("Where to keep your takes", RecordingsFolder);
            if (dir != null) RecordingsFolder = dir;
        }

        [RelayCommand]
        async Task BrowseAvatars()
        {
            if (PickFolder == null || IsPortable) return;
            string dir = await PickFolder("Where to keep the avatar library", AvatarFolder);
            if (dir != null) AvatarFolder = dir;
        }

        [RelayCommand]
        void ResetRecordings()
        {
            RecordingsFolder = _host.Options.Paths.RecordingsDir;
        }

        [RelayCommand]
        void ResetAvatars()
        {
            AvatarFolder = _host.Options.Paths.AvatarLibraryDir;
        }

        [RelayCommand]
        void OpenRecordings()
        {
            _host.Library.OpenFolder();
        }

        [RelayCommand]
        void OpenAvatars()
        {
            _host.AvatarService.OpenFolder();
        }

        // ------------------------------------------------------------------------------------------ Blender

        partial void OnBlenderPathChanged(string value)
        {
            if (_syncing) return;
            string path = (value ?? "").Trim().Trim('"');
            if (path != (_host.Settings.BlenderPath ?? ""))
                _host.SettingsStore.Update(nameof(Settings.BlenderPath), s => s.BlenderPath = path);
            Sync();
        }

        [RelayCommand]
        async Task BrowseBlender()
        {
            if (PickBlender == null) return;
            string file = await PickBlender();
            if (file != null) BlenderPath = file;
        }

        [RelayCommand]
        void ClearBlender()
        {
            BlenderPath = "";
        }

        // ------------------------------------------------------------------------------------------ Unity projects

        [RelayCommand]
        async Task AddProject()
        {
            if (PickFolder == null) return;
            string dir = await PickFolder("Pick a Unity project folder (the one with Assets and ProjectSettings)",
                Projects.LastOrDefault()?.Path);
            if (dir == null) return;
            string problem = _host.AvatarService.AddUnityProject(dir);
            if (problem != null) _host.Status.Notify(problem, StatusKind.Warning);
        }

        [RelayCommand]
        void RemoveProject()
        {
            if (SelectedProject != null) _host.AvatarService.RemoveUnityProject(SelectedProject.Path);
        }

        [RelayCommand]
        void Rescan()
        {
            _host.AvatarService.Rescan();
        }

        // ------------------------------------------------------------------------------------------ state

        void OnSettingChanged(string what)
        {
            if (what == nameof(Settings.RecordingsDir) || what == nameof(Settings.AvatarLibraryDir) ||
                what == nameof(Settings.BlenderPath) || what == "") Sync();
            if (what == nameof(Settings.UnityProjects) || what == "") RefreshProjects();
        }

        /// <summary>Shows the folders as the settings have them (a rejected entry goes back to the folder in use).</summary>
        void Sync()
        {
            _syncing = true;
            try
            {
                RecordingsFolder = _host.Settings.RecordingsDir;
                AvatarFolder = _host.Settings.AvatarLibraryDir;
                BlenderPath = _host.Settings.BlenderPath ?? "";
            }
            finally { _syncing = false; }
        }

        void RefreshStates()
        {
            var env = _host.Environment;
            Recordings.Set(env.Item(EnvironmentProbe.RecordingsFolder));
            Avatars.Set(env.Item(EnvironmentProbe.AvatarsFolder));
            var blender = env.Item(EnvironmentProbe.Blender);
            Blender.Set(blender);
            BlenderInUse = blender?.Path ?? "";
            Unity.Set(env.Item(EnvironmentProbe.Unity));
            RefreshProjects();
        }

        void RefreshProjects()
        {
            var service = _host.AvatarService;
            var found = _host.Environment.Report?.UnityProjects;
            string selected = SelectedProject?.Path;
            Projects.Clear();
            foreach (string path in service.UnityProjects)
            {
                var info = found?.FirstOrDefault(p => SamePath(p.Path, path));
                bool missing = info != null ? !info.Exists : !UnityProjectScanner.IsUnityProject(path);
                string text = missing
                    ? "This folder is no longer a Unity project. Remove it, or add the project again from its new place."
                    : info == null
                        ? ""
                        : (info.EditorVersion.Length > 0 ? "Unity " + info.EditorVersion + " · " : "") +
                          (info.HasBridge ? "bridge installed" : "no bridge yet");
                Projects.Add(new UnityProjectRowViewModel { Path = path, Info = text, Missing = missing });
            }

            SelectedProject = Projects.FirstOrDefault(p => p.Path == selected) ?? Projects.FirstOrDefault();
            HasProjects = Projects.Count > 0;
            ScanInfo = service.ScanInfo;
        }

        static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }
    }
}
