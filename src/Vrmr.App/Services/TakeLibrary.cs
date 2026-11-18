using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace VRMR.App.Services
{
    /// <summary>One take in the recordings folder, as the library lists it (read from the file's summary, not the whole file).</summary>
    public sealed class TakeInfo
    {
        public string Path { get; init; } = "";
        public string Name { get; init; } = "";
        public string Avatar { get; init; } = "";
        public string World { get; init; } = "";
        public double Duration { get; init; }
        /// <summary>Last write time (local): the list is sorted by it, newest first.</summary>
        public DateTime Modified { get; init; }
        public long Length { get; init; }
        /// <summary>VRChat's own IK from a HUMR world is in the take (a reference body).</summary>
        public bool HasVrcIk { get; init; }
        /// <summary>SteamVR devices are in the take (the IK solve needs them).</summary>
        public bool HasDevices { get; init; }
        public bool HasFace { get; init; }
        public bool HasFingers { get; init; }
        /// <summary>The file could not be read as a take.</summary>
        public bool Unreadable { get; init; }
    }

    /// <summary>
    /// The takes in the recordings folder: listed in the background, kept up to date by watching the folder, searched,
    /// deleted to the Recycle Bin and revealed in the file manager.
    /// </summary>
    public sealed class TakeLibrary : IDisposable
    {
        readonly SettingsStore _settings;
        readonly IUiDispatcher _ui;
        readonly Debouncer _rescan;
        readonly Dictionary<string, TakeInfo> _cache = new Dictionary<string, TakeInfo>(StringComparer.OrdinalIgnoreCase);
        FileSystemWatcher _watcher;
        string _watched;
        int _scanVersion;

        public TakeLibrary(SettingsStore settings, IUiDispatcher ui)
        {
            _settings = settings; _ui = ui;
            _rescan = new Debouncer(ui, TimeSpan.FromMilliseconds(300), Refresh);
            _settings.Changed += what => { if (what == nameof(Settings.RecordingsDir) || what == "") Refresh(); };
        }

        /// <summary>The recordings folder.</summary>
        public string Folder => _settings.Value.RecordingsDir;

        /// <summary>All takes, newest first.</summary>
        public IReadOnlyList<TakeInfo> Items { get; private set; } = Array.Empty<TakeInfo>();

        /// <summary>True until the first listing has arrived.</summary>
        public bool Loading { get; private set; } = true;

        /// <summary>Raised on the UI thread when <see cref="Items"/> changed.</summary>
        public event Action Changed;

        /// <summary>
        /// The take being recorded right now, or null. Its file is still being written: it is left out of the listing
        /// (so it cannot be opened half-written) and its growth does not cause rescans. Set it to null, then
        /// <see cref="Refresh"/>, when the recording is finished.
        /// </summary>
        public string BeingRecorded
        {
            get => _beingRecorded;
            set => _beingRecorded = string.IsNullOrEmpty(value) ? null : System.IO.Path.GetFullPath(value);
        }
        volatile string _beingRecorded;

        bool IsBeingRecorded(string path) => _beingRecorded is string r && string.Equals(r, path, StringComparison.OrdinalIgnoreCase);

        /// <summary>Lists the folder again in the background.</summary>
        public void Refresh()
        {
            string dir = Folder;
            Watch(dir);
            int version = ++_scanVersion;
            Dictionary<string, TakeInfo> known;
            lock (_cache) known = new Dictionary<string, TakeInfo>(_cache, StringComparer.OrdinalIgnoreCase);
            Task.Run(() =>
            {
                var items = Scan(dir, known, BeingRecorded);
                _ui.Post(() =>
                {
                    if (version != _scanVersion) return;
                    lock (_cache) { _cache.Clear(); foreach (var i in items) _cache[i.Path] = i; }
                    Items = items; Loading = false;
                    Changed?.Invoke();
                });
            });
        }

        /// <summary>Reads the summaries of every take in a folder, newest first; unchanged files come from <paramref name="known"/>.</summary>
        /// <param name="skip">A file to leave out (the take being recorded), or null.</param>
        public static List<TakeInfo> Scan(string dir, IReadOnlyDictionary<string, TakeInfo> known = null, string skip = null)
        {
            var items = new List<TakeInfo>();
            try
            {
                if (!Directory.Exists(dir)) return items;
                foreach (var f in new DirectoryInfo(dir).GetFiles("*.vrmr"))
                {
                    if (skip != null && string.Equals(f.FullName, skip, StringComparison.OrdinalIgnoreCase)) continue;
                    if (known != null && known.TryGetValue(f.FullName, out var old) && old.Modified == f.LastWriteTime && old.Length == f.Length) { items.Add(old); continue; }
                    items.Add(Read(f));
                }
            }
            catch (Exception ex) { AppLog.Warn("Library: " + ex.Message); }
            items.Sort((a, b) => b.Modified.CompareTo(a.Modified));
            return items;
        }

        static TakeInfo Read(FileInfo f)
        {
            string fallback = System.IO.Path.GetFileNameWithoutExtension(f.Name);
            try
            {
                var m = RecordingIO.ReadSummary(f.FullName);
                return new TakeInfo
                {
                    Path = f.FullName, Name = string.IsNullOrEmpty(m.Name) ? fallback : m.Name, Avatar = m.AvatarName ?? "", World = m.World ?? "",
                    Duration = m.Duration, Modified = f.LastWriteTime, Length = f.Length,
                    HasVrcIk = m.HasHumr, HasDevices = m.HasDevices, HasFace = m.HasFace, HasFingers = m.HasFingers,
                };
            }
            catch { return new TakeInfo { Path = f.FullName, Name = fallback, Modified = f.LastWriteTime, Length = f.Length, Unreadable = true }; }
        }

        /// <summary>The takes whose name, avatar or world contain every word of the query.</summary>
        public static IEnumerable<TakeInfo> Filter(IEnumerable<TakeInfo> items, string query)
        {
            var words = (query ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return items;
            return items.Where(i => words.All(w =>
                i.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || i.Avatar.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                i.World.Contains(w, StringComparison.OrdinalIgnoreCase) || System.IO.Path.GetFileName(i.Path).Contains(w, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>Moves a take to the Recycle Bin.</summary>
        public void Delete(string path)
        {
            FileActions.Recycle(path);
            Refresh();
        }

        /// <summary>Opens the recordings folder in the file manager.</summary>
        public void OpenFolder()
        {
            Directory.CreateDirectory(Folder);
            FileActions.OpenFolder(Folder);
        }

        void Watch(string dir)
        {
            if (string.Equals(dir, _watched, StringComparison.OrdinalIgnoreCase) && _watcher != null) return;
            _watcher?.Dispose(); _watcher = null; _watched = dir;
            try
            {
                if (!Directory.Exists(dir)) return;
                var w = new FileSystemWatcher(dir, "*.vrmr") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                FileSystemEventHandler changed = (o, e) => { if (!IsBeingRecorded(e.FullPath)) _ui.Post(_rescan.Trigger); };
                w.Created += changed; w.Deleted += changed; w.Changed += changed;
                w.Renamed += (o, e) => _ui.Post(_rescan.Trigger);
                w.EnableRaisingEvents = true;
                _watcher = w;
            }
            catch (Exception ex) { AppLog.Warn("Watching the recordings folder: " + ex.Message); }
        }

        public void Dispose() { _watcher?.Dispose(); _rescan.Dispose(); }
    }

    /// <summary>File-manager actions shared by the services.</summary>
    public static class FileActions
    {
        /// <summary>Moves a file to the Recycle Bin (it can be restored from there).</summary>
        public static void Recycle(string path)
        {
            if (File.Exists(path)) RecycleBin.Delete(path);
        }

        /// <summary>Opens a folder in the file manager.</summary>
        public static void OpenFolder(string dir)
        {
            try { Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true }); }
            catch (Exception ex) { AppLog.Warn("Open folder: " + ex.Message); }
        }

        /// <summary>Opens the file manager with a file selected.</summary>
        public static void Reveal(string path)
        {
            try
            {
                if (OperatingSystem.IsWindows() && File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = false });
                else OpenFolder(System.IO.Path.GetDirectoryName(path));
            }
            catch (Exception ex) { AppLog.Warn("Reveal: " + ex.Message); }
        }

        /// <summary>Opens a file with the program registered for it.</summary>
        public static void OpenFile(string path)
        {
            try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
            catch (Exception ex) { AppLog.Warn("Open file: " + ex.Message); }
        }
    }
}
