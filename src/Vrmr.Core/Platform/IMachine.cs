using System;
using System.Collections.Generic;
using System.IO;

namespace VRMR
{
    /// <summary>Per-user and system folders a look-up can start from.</summary>
    public enum KnownFolder { AppData, LocalAppData, LocalAppDataLow, Documents, ProgramFiles, ProgramFilesX86, ProgramData, Temp }

    /// <summary>The registry roots look-ups read. LocalMachine covers both the 64-bit and the 32-bit view.</summary>
    public enum RegistryRoot { CurrentUser, LocalMachine, ClassesRoot }

    /// <summary>
    /// The computer as the app looks at it, read-only: known folders, registry values and running processes. Every
    /// look-up of another program (VRChat, SteamVR, Steam, Blender, Unity Hub) goes through it, so tests and
    /// isolated runs can substitute a <see cref="SandboxMachine"/>. Files are read with System.IO: a substitute
    /// works because every path starts from one of its folders or registry values.
    /// </summary>
    public interface IMachine
    {
        /// <summary>The folder's full path, or null when this machine has none.</summary>
        string Folder(KnownFolder folder);
        /// <summary>A registry value (string, int, long or byte[]), or null when the key or the value is missing.</summary>
        object RegistryValue(RegistryRoot root, string key, string name);
        /// <summary>The value names under a key; empty when the key is missing.</summary>
        IReadOnlyList<string> RegistryValueNames(RegistryRoot root, string key);
        /// <summary>The names of a key's sub-keys; empty when the key is missing.</summary>
        IReadOnlyList<string> RegistrySubKeys(RegistryRoot root, string key);
        /// <summary>Whether a process with this executable name (without ".exe") is running.</summary>
        bool IsProcessRunning(string name);
    }

    /// <summary>The machine the process runs on.</summary>
    public static class Machine
    {
        /// <summary>The real computer.</summary>
        public static IMachine Real { get; } = new RealMachine();
    }

    /// <summary>
    /// A machine that exists only inside one folder: its known folders are sub-folders of <see cref="Root"/>, and its
    /// registry and process list hold only what was put there. Used by tests, by runs with a data folder (--data), and
    /// for UI previews of machine states.
    /// </summary>
    public sealed class SandboxMachine : IMachine
    {
        readonly Dictionary<string, Dictionary<string, object>> _keys = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The folder that stands in for the whole machine.</summary>
        public string Root { get; }

        public SandboxMachine(string root) { Root = Path.GetFullPath(root); }

        public string Folder(KnownFolder folder)
        {
            switch (folder)
            {
                case KnownFolder.AppData: return Path.Combine(Root, "Roaming");
                case KnownFolder.LocalAppData: return Path.Combine(Root, "Local");
                case KnownFolder.LocalAppDataLow: return Path.Combine(Root, "LocalLow");
                case KnownFolder.Documents: return Path.Combine(Root, "Documents");
                case KnownFolder.ProgramFiles: return Path.Combine(Root, "Programs");
                case KnownFolder.ProgramFilesX86: return Path.Combine(Root, "Programs32");
                case KnownFolder.ProgramData: return Path.Combine(Root, "ProgramData");
                default: return Path.Combine(Root, "Temp");
            }
        }

        /// <summary>Adds or replaces a registry value. Returns this machine, for chaining.</summary>
        public SandboxMachine SetRegistry(RegistryRoot root, string key, string name, object value)
        {
            lock (_keys)
            {
                string id = Id(root, key);
                if (!_keys.TryGetValue(id, out var values)) _keys[id] = values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                values[name ?? ""] = value;
            }
            return this;
        }

        /// <summary>Marks a process as running (or not). Returns this machine, for chaining.</summary>
        public SandboxMachine SetRunning(string process, bool running = true)
        {
            lock (_running) { if (running) _running.Add(process); else _running.Remove(process); }
            return this;
        }

        public object RegistryValue(RegistryRoot root, string key, string name)
        {
            lock (_keys) return _keys.TryGetValue(Id(root, key), out var values) && values.TryGetValue(name ?? "", out var v) ? v : null;
        }

        public IReadOnlyList<string> RegistryValueNames(RegistryRoot root, string key)
        {
            lock (_keys) return _keys.TryGetValue(Id(root, key), out var values) ? new List<string>(values.Keys) : new List<string>();
        }

        public IReadOnlyList<string> RegistrySubKeys(RegistryRoot root, string key)
        {
            string prefix = Id(root, key) + "\\";
            var names = new List<string>();
            lock (_keys)
                foreach (var id in _keys.Keys)
                {
                    if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    string rest = id.Substring(prefix.Length);
                    int cut = rest.IndexOf('\\');
                    string child = cut < 0 ? rest : rest.Substring(0, cut);
                    if (child.Length > 0 && !names.Contains(child)) names.Add(child);
                }
            return names;
        }

        public bool IsProcessRunning(string name) { lock (_running) return _running.Contains(name ?? ""); }

        static string Id(RegistryRoot root, string key) => root + ":" + (key ?? "").Trim('\\');
    }
}
