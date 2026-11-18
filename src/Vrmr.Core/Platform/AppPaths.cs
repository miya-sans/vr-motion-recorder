using System;
using System.IO;

namespace VRMR
{
    /// <summary>
    /// Where the app keeps its own files: settings and the calibration library (app data), and the user's takes,
    /// scenes, avatar library and exports (documents). One instance is in use per process (<see cref="Current"/>);
    /// a data folder (--data) moves everything under one root so a run cannot touch the user's real files.
    /// </summary>
    public sealed class AppPaths
    {
        /// <summary>The app's folder name under the roaming app data folder.</summary>
        public const string AppDataFolderName = "VRMotionRecorder";
        /// <summary>The app's folder name under the user's documents.</summary>
        public const string DocumentsFolderName = "VR Motion Recorder";

        static volatile AppPaths _current;
        static readonly object CurrentLock = new object();

        /// <summary>The paths this process uses: the per-user folders until <see cref="Use"/> is called.</summary>
        public static AppPaths Current
        {
            get
            {
                var c = _current;
                if (c != null) return c;
                lock (CurrentLock) return _current ?? (_current = ForUser());
            }
        }

        /// <summary>Sets the paths for this process. Call once at start-up, before anything loads settings.</summary>
        public static void Use(AppPaths paths)
        {
            _current = paths ?? throw new ArgumentNullException(nameof(paths));
        }

        /// <summary>Normal runs: the user's app data and documents folders on this computer.</summary>
        public static AppPaths ForUser() => ForMachine(VRMR.Machine.Real);

        /// <summary>The per-user folders of a given machine.</summary>
        public static AppPaths ForMachine(IMachine machine)
        {
            if (machine == null) throw new ArgumentNullException(nameof(machine));
            string beside = Path.Combine(AppContext.BaseDirectory, "data"); // a machine without the usual folders
            string appData = machine.Folder(KnownFolder.AppData), docs = machine.Folder(KnownFolder.Documents);
            return new AppPaths(
                appData != null ? Path.Combine(appData, AppDataFolderName) : Path.Combine(beside, "AppData"),
                docs != null ? Path.Combine(docs, DocumentsFolderName) : Path.Combine(beside, "Documents"),
                machine, null, false);
        }

        /// <summary>
        /// Everything under one folder: root\AppData and root\Documents. Isolated (the default), other programs are
        /// looked up in a <see cref="SandboxMachine"/> at root\Machine, so nothing outside the root is read; otherwise
        /// only the app's own files move and the real computer is used.
        /// </summary>
        public static AppPaths ForDataRoot(string root, bool isolated = true)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("The data folder is empty", nameof(root));
            root = Path.GetFullPath(root);
            IMachine machine = isolated ? new SandboxMachine(Path.Combine(root, "Machine")) : VRMR.Machine.Real;
            return new AppPaths(Path.Combine(root, "AppData"), Path.Combine(root, "Documents"), machine, root, isolated) { _redirected = true };
        }

        /// <summary>A layout of the caller's own: an app data folder and a documents folder, with look-ups on <paramref name="machine"/> (null = the real computer).</summary>
        public static AppPaths ForFolders(string appDataDir, string documentsDir, IMachine machine = null)
        {
            if (string.IsNullOrWhiteSpace(appDataDir)) throw new ArgumentException("The app data folder is empty", nameof(appDataDir));
            if (string.IsNullOrWhiteSpace(documentsDir)) throw new ArgumentException("The documents folder is empty", nameof(documentsDir));
            appDataDir = Path.GetFullPath(appDataDir);
            return new AppPaths(appDataDir, Path.GetFullPath(documentsDir), machine ?? VRMR.Machine.Real, null, machine is SandboxMachine) { _redirected = true };
        }

        /// <summary>The paths a command line asks for: "--data &lt;dir&gt;" (or "--data=&lt;dir&gt;") is an isolated data folder, anything else the per-user folders.</summary>
        public static AppPaths FromCommandLine(string[] args)
        {
            string root = DataArgument(args);
            return root != null ? ForDataRoot(root) : ForUser();
        }

        /// <summary>The folder given with --data, or null.</summary>
        public static string DataArgument(string[] args)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i] ?? "";
                if (a.StartsWith("--data=", StringComparison.OrdinalIgnoreCase) && a.Length > 7) return a.Substring(7).Trim('"');
                if (a.Equals("--data", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1])) return args[i + 1];
            }
            return null;
        }

        AppPaths(string appData, string documents, IMachine machine, string dataRoot, bool isolated)
        {
            AppDataDir = appData; DocumentsDir = documents; Machine = machine; DataRoot = dataRoot; Isolated = isolated;
        }

        bool _redirected; // the folders were chosen by the caller: nothing is written outside them

        /// <summary>The data folder everything is under (--data), or null.</summary>
        public string DataRoot { get; }
        /// <summary>True when other programs are looked up in a sandbox, so nothing outside the app's own folders is read.</summary>
        public bool Isolated { get; }
        /// <summary>The machine other programs are looked up on.</summary>
        public IMachine Machine { get; }

        // ------------------------------------------------------------------ settings, calibration library, logs

        /// <summary>Settings, the calibration library and logs.</summary>
        public string AppDataDir { get; }
        /// <summary>The user's settings.</summary>
        public string SettingsFile => Path.Combine(AppDataDir, "settings.json");
        /// <summary>The app's own window and panel state (not part of the settings).</summary>
        public string UiStateFile => Path.Combine(AppDataDir, "ui-state.json");
        /// <summary>The folder the log file is written to.</summary>
        public string LogDir => AppDataDir;
        /// <summary>The log of the current run.</summary>
        public string LogFile => Path.Combine(LogDir, AppLog.FileName);
        /// <summary>Named calibration settings.</summary>
        public string CalibrationPresetsDir => Path.Combine(AppDataDir, "calibrations");
        /// <summary>Calibration snapshots (device poses at a calibration).</summary>
        public string CalibrationSnapshotsDir => Path.Combine(CalibrationPresetsDir, "snapshots");
        /// <summary>Skeletons the Unity bridge exports for the app.</summary>
        public string UnityProfilesDir => Path.Combine(AppDataDir, "profiles");
        /// <summary>The scene editor's punch-in settings.</summary>
        public string PunchInFile => Path.Combine(AppDataDir, "punchin.json");
        /// <summary>The last SteamVR room seen (play area and base stations).</summary>
        public string RoomFile => Path.Combine(AppDataDir, "steamvr_room.json");

        // ------------------------------------------------------------------ the user's work

        /// <summary>The user's work: takes, scenes, the avatar library and exports.</summary>
        public string DocumentsDir { get; }
        /// <summary>Where takes go unless the user picked another folder.</summary>
        public string RecordingsDir => DocumentsDir;
        /// <summary>Where the avatar library is unless the user picked another folder.</summary>
        public string AvatarLibraryDir => Path.Combine(DocumentsDir, "Avatars");
        /// <summary>Where scenes are saved by default.</summary>
        public string ScenesDir => Path.Combine(DocumentsDir, "Scenes");
        /// <summary>Where exports go by default.</summary>
        public string ExportsDir => Path.Combine(DocumentsDir, "Exports");

        /// <summary>
        /// A folder for the SteamVR input manifest that SteamVR can read. SteamVR does not load manifests from the
        /// per-user app data folders, so the first writable one of these is used: next to the program, the shared
        /// program data folder, then app data. With folders of the caller's choice (--data) it is always inside the
        /// app data folder. Creates the folder.
        /// </summary>
        public string WritableSteamVRManifestDir()
        {
            string own = Path.Combine(AppDataDir, "steamvr");
            if (_redirected) return own;
            string shared = Machine.Folder(KnownFolder.ProgramData);
            foreach (var root in new[] { AppContext.BaseDirectory, shared != null ? Path.Combine(shared, AppDataFolderName) : null })
            {
                if (root == null) continue;
                string dir = Path.Combine(root, "steamvr");
                if (FolderAccess.CanWrite(dir, create: true)) return dir;
            }
            return own;
        }
    }

    /// <summary>Checks whether a folder can be written to.</summary>
    public static class FolderAccess
    {
        /// <summary>
        /// True when a file can be created in the folder. The test file is removed by the system when its handle
        /// closes. A folder that does not exist is created when <paramref name="create"/> is set; otherwise its nearest
        /// existing parent is tested and nothing is created.
        /// </summary>
        public static bool CanWrite(string dir, bool create = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir)) return false;
                dir = Path.GetFullPath(dir);
                if (create) Directory.CreateDirectory(dir);
                while (!Directory.Exists(dir))
                {
                    dir = Path.GetDirectoryName(dir);
                    if (string.IsNullOrEmpty(dir)) return false;
                }
                string probe = Path.Combine(dir, ".vrmr-write-test-" + Guid.NewGuid().ToString("N"));
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch { return false; }
        }
    }
}
