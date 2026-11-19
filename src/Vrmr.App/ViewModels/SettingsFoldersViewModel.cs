using System;
using System.Collections.Generic;
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
    public sealed class UnityProjectRowViewModel : ObservableObject
    {
        string _scanInfo = "";

        public UnityProjectRowViewModel(UnityProjectInfo project, bool alreadyAdded = false)
        {
            Path = project.Path;
            Name = project.Name;
            Missing = !project.Exists;
            HasBridge = project.HasBridge;
            AlreadyAdded = alreadyAdded;
            Info = Missing
                ? "Project folder unavailable"
                : (project.EditorVersion.Length > 0 ? "Unity " + project.EditorVersion + " · " : "") +
                  (HasBridge ? "Bridge detected" : "No bridge");
            if (AlreadyAdded) Info += " · Already added";
        }

        public string Path { get; init; } = "";
        public string Name { get; }
        public bool HasBridge { get; }
        public bool AlreadyAdded { get; }
        public bool CanAdd => !Missing && !AlreadyAdded;
        public string Indicator => Missing ? "⚠" : "●";

        public string BridgeStatus => Missing
            ? "Project folder unavailable"
            : HasBridge
                ? "Bridge files or package reference detected; Unity compilation is not checked"
                : "No bridge detected";

        /// <summary>"Unity 2022.3.22f1 · bridge installed", or that the folder is gone.</summary>
        public string Info { get; init; } = "";

        public bool Missing { get; init; }

        /// <summary>Indexing progress and results for this project.</summary>
        public string ScanInfo
        {
            get => _scanInfo;
            set
            {
                if (SetProperty(ref _scanInfo, value)) OnPropertyChanged(nameof(Details));
            }
        }

        /// <summary>The project's version, bridge status and indexing status in one line.</summary>
        public string Details => Info + (ScanInfo.Length > 0 ? " · " + ScanInfo : "");
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

        [ObservableProperty]
        string _projectFolder;

        [ObservableProperty] [NotifyPropertyChangedFor(nameof(RecordingsMoved))]
        string _recordingsFolder;

        [ObservableProperty] UnityProjectRowViewModel _selectedProject;
        bool _syncing;

        public SettingsFoldersViewModel(AppHost host)
        {
            _host = host;
            var s = host.Settings;
            _recordingsFolder = s.RecordingsPath;
            _avatarFolder = s.AvatarLibraryPath;
            _projectFolder = s.ProjectFolder;
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
        public bool RecordingsMoved => CanMoveFolders && RecordingsFolder != "Recordings";
        public bool AvatarsMoved => CanMoveFolders && AvatarFolder != "Avatars";
        public bool HasBlenderPath => !string.IsNullOrEmpty(BlenderPath);

        /// <summary>Asks the view for a folder: the picker's title and the folder to start in. Null when cancelled.</summary>
        public Func<string, string, Task<string>> PickFolder { get; set; }

        /// <summary>Asks the view for Blender's program file. Null when cancelled.</summary>
        public Func<Task<string>> PickBlender { get; set; }

        /// <summary>Opens a manager picker and returns only the explicitly selected project paths.</summary>
        public Func<UnityProjectSource, Task<string[]>> PickProjects { get; set; }

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
            string problem = PortableProblem(_host) ?? CheckLibraryFolder(_host, value, out dir);
            if (problem != null) _host.Status.Notify(problem, StatusKind.Error);
            else if (LibraryPath(_host, dir) != _host.Settings.AvatarLibraryPath)
            {
                _host.SettingsStore.Update(nameof(Settings.AvatarLibraryDir),
                    s => s.AvatarLibraryDir = LibraryPath(_host, dir));
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
            string problem = PortableProblem(host) ?? CheckLibraryFolder(host, folder, out dir);
            if (problem != null) return problem;
            if (LibraryPath(host, dir) == host.Settings.RecordingsPath) return null;
            host.SettingsStore.Update(nameof(Settings.RecordingsDir), s => s.RecordingsDir = LibraryPath(host, dir));
            host.Status.Info($"Takes are now kept in {dir}. Takes in the old folder stay where they are.");
            return null;
        }

        partial void OnProjectFolderChanged(string value)
        {
            if (_syncing) return;
            string problem = SetProjectFolder(_host, value);
            if (problem != null) _host.Status.Notify(problem, StatusKind.Error);
            Sync();
        }

        [RelayCommand]
        async Task BrowseProject()
        {
            if (PickFolder == null || IsPortable) return;
            string dir = await PickFolder("Project folder", ProjectFolder);
            if (dir != null) ProjectFolder = dir;
        }

        /// <summary>Validates the project and library folders before changing their shared root.</summary>
        public static string SetProjectFolder(AppHost host, string folder)
        {
            string root = null;
            string problem = PortableProblem(host) ?? CheckProjectFolder(host.Settings, folder, out root);
            if (problem != null) return problem;
            host.SettingsStore.Update("", s => s.ProjectFolder = root);
            return null;
        }

        /// <summary>Validates a project root and its relative libraries without accessing absolute overrides.</summary>
        public static string CheckProjectFolder(Settings settings, string folder, out string root)
        {
            string problem = CheckFolder(folder, out root);
            if (problem != null) return problem;
            foreach (string path in new[] { settings.RecordingsPath, settings.AvatarLibraryPath })
            {
                if (Path.IsPathFullyQualified(path)) continue;
                problem = CheckFolder(Path.Combine(root, path), out _);
                if (problem != null) return problem;
            }

            return null;
        }

        static string LibraryPath(AppHost host, string dir)
        {
            string relative = Path.GetRelativePath(host.Settings.ProjectFolder, dir);
            return relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relative)
                ? dir
                : relative;
        }

        static string CheckLibraryFolder(AppHost host, string folder, out string dir)
        {
            dir = null;
            if (string.IsNullOrWhiteSpace(folder)) return "Type a folder, or pick one with Browse.";
            try
            {
                return CheckFolder(host.Settings.ResolveFolder(folder.Trim().Trim('"')), out dir);
            }
            catch (Exception ex) { return ex.Message; }
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
            if (!Path.IsPathFullyQualified(text))
                return
                    $"\"{text}\" is not a whole path. Start with the drive (for example D:\\Takes), or pick the folder with Browse.";
            try
            {
                dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(text));
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex) { return $"{text} cannot be used: {ex.Message} Pick another folder."; }

            return FolderAccess.CanWrite(dir) ? null : $"{dir} cannot be written to. Pick another folder.";
        }

        [RelayCommand]
        async Task BrowseRecordings()
        {
            if (PickFolder == null || IsPortable) return;
            string dir = await PickFolder("Recordings folder", _host.Settings.RecordingsDir);
            if (dir != null) RecordingsFolder = dir;
        }

        [RelayCommand]
        async Task BrowseAvatars()
        {
            if (PickFolder == null || IsPortable) return;
            string dir = await PickFolder("Avatar library folder", _host.Settings.AvatarLibraryDir);
            if (dir != null) AvatarFolder = dir;
        }

        [RelayCommand]
        void ResetRecordings()
        {
            RecordingsFolder = "Recordings";
        }

        [RelayCommand]
        void ResetAvatars()
        {
            AvatarFolder = "Avatars";
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
        void RemoveProject(UnityProjectRowViewModel project)
        {
            if (project != null) _host.AvatarService.RemoveUnityProject(project.Path);
        }

        [RelayCommand]
        async Task AddFromManager(UnityProjectSource source)
        {
            if (PickProjects == null) return;
            string[] paths = await PickProjects(source);
            if (paths == null) return;
            foreach (string problem in _host.AvatarService.AddUnityProjects(paths))
                _host.Status.Notify(problem, StatusKind.Warning);
        }

        /// <summary>Creates a picker using this host's machine and linked projects.</summary>
        public UnityProjectPickerViewModel CreateProjectPicker(UnityProjectSource source)
        {
            return new UnityProjectPickerViewModel(_host.Options.Paths.Machine, source, _host.Settings.UnityProjects);
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
                ProjectFolder = _host.Settings.ProjectFolder;
                RecordingsFolder = _host.Settings.RecordingsPath;
                AvatarFolder = _host.Settings.AvatarLibraryPath;
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
            var refreshed = new List<UnityProjectRowViewModel>();
            foreach (string path in service.UnityProjects)
            {
                var info = found?.FirstOrDefault(p => SamePath(p.Path, path));
                info ??= new UnityProjectInfo(path, Path.GetFileName(path), "",
                    UnityProjectScanner.IsUnityProject(path), false, false);
                refreshed.Add(new UnityProjectRowViewModel(info) { ScanInfo = service.ProjectScanInfo(path) });
            }

            bool sameRows = Projects.Count == refreshed.Count && Projects.Zip(refreshed).All(pair =>
                pair.First.Path == pair.Second.Path && pair.First.Info == pair.Second.Info);
            if (sameRows)
            {
                for (int i = 0; i < Projects.Count; i++) Projects[i].ScanInfo = refreshed[i].ScanInfo;
            }
            else
            {
                Projects.Clear();
                foreach (var row in refreshed) Projects.Add(row);
            }

            SelectedProject = Projects.FirstOrDefault(p => p.Path == selected) ?? Projects.FirstOrDefault();
            HasProjects = Projects.Count > 0;
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
