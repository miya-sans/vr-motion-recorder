using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VRMR
{
    /// <summary>
    ///     The computer the process runs on. Registry and shell look-ups exist on Windows only; elsewhere they find
    ///     nothing.
    /// </summary>
    sealed class RealMachine : IMachine
    {
        static readonly Guid LocalAppDataLowId = new("A520A1A4-1780-4FF6-BD18-167343C5AF16");
        readonly List<uint> _gone = new();

        /// <summary>Process id to executable name (no folder, no ".exe"), kept between calls.</summary>
        readonly Dictionary<uint, KnownProcess> _processes = new();

        uint[] _ids = new uint[2048];
        long _lastFullPass;
        int _pass;

        public string Folder(KnownFolder folder)
        {
            try
            {
                switch (folder)
                {
                    case KnownFolder.AppData: return Special(Environment.SpecialFolder.ApplicationData);
                    case KnownFolder.LocalAppData: return Special(Environment.SpecialFolder.LocalApplicationData);
                    case KnownFolder.LocalAppDataLow:
                        return OperatingSystem.IsWindows() ? ShellFolder(LocalAppDataLowId) : null;
                    case KnownFolder.Documents: return Special(Environment.SpecialFolder.MyDocuments);
                    case KnownFolder.ProgramFiles:
                        // a 32-bit process is told the x86 folder; the environment names the real one
                        return NullIfEmpty(Environment.GetEnvironmentVariable("ProgramW6432")) ??
                               Special(Environment.SpecialFolder.ProgramFiles);
                    case KnownFolder.ProgramFilesX86: return Special(Environment.SpecialFolder.ProgramFilesX86);
                    case KnownFolder.ProgramData: return Special(Environment.SpecialFolder.CommonApplicationData);
                    default: return NullIfEmpty(Path.GetTempPath());
                }
            }
            catch { return null; }
        }

        public object RegistryValue(RegistryRoot root, string key, string name)
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                object found = null;
                foreach (var hive in Open(root))
                    using (hive)
                    {
                        if (found != null) continue;
                        using (var k = hive.OpenSubKey(key)) found = k?.GetValue(name);
                    }

                return found;
            }
            catch { return null; }
        }

        public IReadOnlyList<string> RegistryValueNames(RegistryRoot root, string key)
        {
            return Names(root, key, k => k.GetValueNames());
        }

        public IReadOnlyList<string> RegistrySubKeys(RegistryRoot root, string key)
        {
            return Names(root, key, k => k.GetSubKeyNames());
        }

        /// <summary>
        ///     Whether a process with this executable name is running. Listing the process ids is cheap; what costs is
        ///     asking each process for its name, so names are remembered and only processes that appeared since the last
        ///     call are asked. Everything is asked again once a minute, in case an id was reused within one interval.
        ///     That makes the check cheap enough to repeat every few seconds while waiting for a program to start.
        /// </summary>
        public unsafe bool IsProcessRunning(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    var found = Process.GetProcessesByName(name);
                    foreach (var p in found) p.Dispose();
                    return found.Length > 0;
                }
                catch { return false; }
            }

            lock (_processes)
            {
                int count;
                while (true)
                {
                    uint bytes;
                    fixed (uint* ids = _ids)
                        if (EnumProcesses(ids, (uint)(_ids.Length * sizeof(uint)), out bytes) == 0)
                            return false;
                    count = (int)(bytes / sizeof(uint));
                    if (count < _ids.Length) break;
                    _ids = new uint[_ids.Length * 2]; // the list was cut short: ask again with more room
                }

                long now = Environment.TickCount64;
                if (now - _lastFullPass > 60000)
                {
                    _processes.Clear();
                    _lastFullPass = now;
                }

                _pass++;
                bool running = false;
                for (int i = 0; i < count; i++)
                {
                    uint id = _ids[i];
                    if (id == 0) continue;
                    if (!_processes.TryGetValue(id, out var known))
                        _processes[id] = known = new KnownProcess { Name = NameOf(id) };
                    known.Seen = _pass;
                    if (!running && string.Equals(known.Name, name, StringComparison.OrdinalIgnoreCase)) running = true;
                }

                // ids that are no longer listed are forgotten, so a new process that gets one of them is asked for its name
                if (_processes.Count > count)
                {
                    _gone.Clear();
                    foreach (var kv in _processes)
                        if (kv.Value.Seen != _pass)
                            _gone.Add(kv.Key);
                    foreach (uint id in _gone) _processes.Remove(id);
                }

                return running;
            }
        }

        static string Special(Environment.SpecialFolder f)
        {
            return NullIfEmpty(Environment.GetFolderPath(f));
        }

        static string NullIfEmpty(string s)
        {
            return string.IsNullOrEmpty(s) ? null : s;
        }

        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out IntPtr path);

        static string ShellFolder(Guid id)
        {
            IntPtr p = IntPtr.Zero;
            try
            {
                return SHGetKnownFolderPath(in id, 0, IntPtr.Zero, out p) == 0
                    ? NullIfEmpty(Marshal.PtrToStringUni(p))
                    : null;
            }
            finally
            {
                if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
            }
        }

        // ------------------------------------------------------------------ registry

        static RegistryKey[] Open(RegistryRoot root)
        {
            switch (root)
            {
                case RegistryRoot.CurrentUser:
                    return new[] { RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default) };
                case RegistryRoot.ClassesRoot:
                    return new[] { RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Default) };
                default:
                    return new[]
                    {
                        RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
                        RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                    };
            }
        }

        static IReadOnlyList<string> Names(RegistryRoot root, string key, Func<RegistryKey, string[]> read)
        {
            var names = new List<string>();
            if (!OperatingSystem.IsWindows()) return names;
            try
            {
                foreach (var hive in Open(root))
                    using (hive)
                    using (var k = hive.OpenSubKey(key))
                        if (k != null)
                            foreach (string n in read(k))
                                if (!names.Contains(n))
                                    names.Add(n);
            }
            catch { }

            return names;
        }

        // ------------------------------------------------------------------ processes

        [DllImport("kernel32", EntryPoint = "K32EnumProcesses")]
        static unsafe extern int EnumProcesses(uint* ids, uint bytes, out uint bytesReturned);

        [DllImport("kernel32")]
        static extern IntPtr OpenProcess(uint access, int inherit, uint processId);

        [DllImport("kernel32", EntryPoint = "QueryFullProcessImageNameW")]
        static unsafe extern int QueryFullProcessImageName(IntPtr process, uint flags, char* path, ref uint size);

        [DllImport("kernel32")]
        static extern int CloseHandle(IntPtr handle);

        /// <summary>A process's executable name without folder and ".exe"; "" when the process cannot be asked (system processes).</summary>
        static unsafe string NameOf(uint processId)
        {
            IntPtr h = OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, 0, processId);
            if (h == IntPtr.Zero) return "";
            try
            {
                char* path = stackalloc char[1024];
                uint size = 1024;
                if (QueryFullProcessImageName(h, 0, path, ref size) == 0 || size == 0) return "";
                var full = new ReadOnlySpan<char>(path, (int)size);
                var file = full.Slice(full.LastIndexOf('\\') + 1);
                if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) file = file.Slice(0, file.Length - 4);
                return file.ToString();
            }
            finally { CloseHandle(h); }
        }

        sealed class KnownProcess
        {
            public string Name;
            public int Seen;
        }
    }
}
