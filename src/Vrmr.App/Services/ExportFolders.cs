using System;
using System.IO;
using System.Linq;

namespace VRMR.App.Services
{
    /// <summary>
    /// Where exports go: the folder chosen, else the linked Unity project's Assets\VRMotionRecorder, else Exports in
    /// the recordings folder; with a folder per take inside it unless that is switched off.
    /// </summary>
    public sealed class ExportFolders
    {
        readonly SettingsStore _settings;

        public ExportFolders(SettingsStore settings) { _settings = settings; }

        Settings S => _settings.Value;

        /// <summary>The first linked Unity project that is still there, or null.</summary>
        public string UnityProjectRoot => S.UnityProjects.FirstOrDefault(UnityProjectScanner.IsUnityProject);

        /// <summary>Assets\VRMotionRecorder in the linked Unity project (the Unity bridge picks exports up there), or null without a project.</summary>
        public string UnityExportDir
        {
            get
            {
                string root = UnityProjectRoot;
                return root == null ? null : Path.Combine(root, "Assets", "VRMotionRecorder");
            }
        }

        /// <summary>The export folder: the one chosen, else the linked Unity project's, else Exports in the recordings folder.</summary>
        public string ExportDir => !string.IsNullOrWhiteSpace(S.LastExportDir) ? S.LastExportDir : UnityExportDir ?? Path.Combine(S.RecordingsDir, "Exports");

        /// <summary>Each take's files go into a folder named after the take inside the export folder.</summary>
        public bool TakeFolders
        {
            get => S.ExportTakeFolders;
            set { if (S.ExportTakeFolders != value) _settings.Update(nameof(Settings.ExportTakeFolders), s => s.ExportTakeFolders = value); }
        }

        /// <summary>The folder the last export of this session wrote into, or null.</summary>
        public string LastOutputDir { get; internal set; }

        /// <summary>The folder "Open" shows: the last export's when it is still there, else the export folder; null when neither exists yet.</summary>
        public string FolderToOpen
        {
            get
            {
                if (LastOutputDir != null && Directory.Exists(LastOutputDir)) return LastOutputDir;
                string dir = ExportDir;
                return Directory.Exists(dir) ? dir : null;
            }
        }

        /// <summary>A few words for the export folder: "Unity project" inside a linked project's Assets, else the folder's name.</summary>
        public string Summary
        {
            get
            {
                string dir = ExportDir;
                if (IsInUnityAssets(dir)) return "Unity project";
                string name = Path.GetFileName(dir.TrimEnd('\\', '/'));
                return name.Length > 0 ? name : dir;
            }
        }

        /// <summary>
        /// Makes a typed or picked path the export folder. Nothing typed keeps the folder as it is. Returns false with
        /// the reason in plain words when the path cannot be an export folder.
        /// </summary>
        public bool SetExportDir(string typed, out string error)
        {
            error = null;
            string dir = (typed ?? "").Trim().Trim('"').Trim();
            if (dir.Length == 0) return true;
            string full = Path.IsPathFullyQualified(dir) ? FullPath(dir) : null;
            if (full == null) { error = "The export folder must be a full path (like D:\\Exports)."; return false; }
            if (File.Exists(full)) { error = $"{full} is a file. Pick a folder instead."; return false; }
            if (!string.Equals(full, S.LastExportDir, StringComparison.Ordinal)) _settings.Update(nameof(Settings.LastExportDir), s => s.LastExportDir = full);
            return true;
        }

        /// <summary>True for a folder inside the Assets of a linked Unity project.</summary>
        public bool IsInUnityAssets(string dir)
        {
            string full = FullPath(dir);
            if (full == null) return false;
            full = WithSeparator(full);
            foreach (string project in S.UnityProjects)
            {
                if (string.IsNullOrEmpty(project)) continue;
                string assets = FullPath(Path.Combine(project, "Assets"));
                if (assets != null && full.StartsWith(WithSeparator(assets), StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Where a take's files go: its own folder inside the export folder, or the export folder itself.</summary>
        public string TakeDir(string takeName) => TakeFolders ? Path.Combine(ExportDir, SafeName(takeName)) : ExportDir;

        /// <summary>Where the textures of an avatar's model FBX are copied: once per avatar, shared by every take exported into this folder.</summary>
        public string TextureDir(string avatarName) => TextureDir(ExportDir, avatarName);

        /// <summary>The textures folder of an avatar inside a given export folder.</summary>
        public static string TextureDir(string exportDir, string avatarName) =>
            Path.Combine(exportDir, "textures", SafeName(string.IsNullOrWhiteSpace(avatarName) ? "avatar" : avatarName));

        /// <summary>A name that can be a file or folder name: characters the file system refuses become "_".</summary>
        public static string SafeName(string name)
        {
            name ??= "";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Trim();
            return name.Length == 0 ? "clip" : name;
        }

        static string WithSeparator(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        /// <summary>The full form of a path, or null when it is not a path at all.</summary>
        static string FullPath(string path)
        {
            try { return Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return null; }
        }
    }
}
