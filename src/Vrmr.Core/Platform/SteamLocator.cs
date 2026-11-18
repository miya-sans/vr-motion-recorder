using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace VRMR
{
    /// <summary>Finds Steam, its library folders and installed apps from Steam's own records. Never writes.</summary>
    public static class SteamLocator
    {
        /// <summary>Steam's app id of SteamVR.</summary>
        public const int SteamVRAppId = 250820;

        /// <summary>Steam's app id of Blender.</summary>
        public const int BlenderAppId = 365670;

        static readonly Regex QuotedPair =
            new("\"(?<k>[^\"]*)\"\\s+\"(?<v>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

        /// <summary>The folder Steam is installed in, or null.</summary>
        public static string InstallDir(IMachine machine)
        {
            foreach (string candidate in new[]
                     {
                         machine.RegistryValue(RegistryRoot.CurrentUser, @"Software\Valve\Steam",
                             "SteamPath") as string,
                         machine.RegistryValue(RegistryRoot.LocalMachine, @"SOFTWARE\Valve\Steam",
                             "InstallPath") as string,
                         Under(machine.Folder(KnownFolder.LocalAppData),
                             "Steam") // where Steam lives without a registry
                     })
            {
                string dir = Clean(candidate);
                if (dir != null && Directory.Exists(dir)) return dir;
            }

            return null;
        }

        /// <summary>Every Steam library folder: Steam's own, then the ones listed in libraryfolders.vdf.</summary>
        public static List<string> Libraries(IMachine machine)
        {
            var list = new List<string>();
            string steam = InstallDir(machine);
            if (steam == null) return list;
            list.Add(steam);
            foreach (string vdf in new[]
                     {
                         Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
                         Path.Combine(steam, "config", "libraryfolders.vdf")
                     })
            {
                string text = ReadText(vdf);
                if (text == null) continue;
                foreach (Match m in QuotedPair.Matches(text))
                {
                    // current format: "path" "<folder>"; older format: "1" "<folder>"
                    string key = m.Groups["k"].Value;
                    if (!key.Equals("path", StringComparison.OrdinalIgnoreCase) && !IsNumber(key)) continue;
                    string dir = Clean(Unescape(m.Groups["v"].Value));
                    if (dir == null || !Path.IsPathRooted(dir) || !Directory.Exists(dir)) continue;
                    if (!list.Exists(x => SamePath(x, dir))) list.Add(dir);
                }
            }

            return list;
        }

        /// <summary>The folder an app is installed in (from its appmanifest in whichever library has it), or null.</summary>
        public static string AppDir(IMachine machine, int appId)
        {
            foreach (string lib in Libraries(machine))
            {
                string text = ReadText(Path.Combine(lib, "steamapps", "appmanifest_" + appId + ".acf"));
                if (text == null) continue;
                foreach (Match m in QuotedPair.Matches(text))
                {
                    if (!m.Groups["k"].Value.Equals("installdir", StringComparison.OrdinalIgnoreCase)) continue;
                    string dir = Path.Combine(lib, "steamapps", "common", Unescape(m.Groups["v"].Value));
                    if (Directory.Exists(dir)) return dir;
                }
            }

            return null;
        }

        static string Under(string dir, string name)
        {
            return dir == null ? null : Path.Combine(dir, name);
        }

        static string Clean(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return null;
            try { return Path.GetFullPath(dir.Trim().Replace('/', Path.DirectorySeparatorChar)); }
            catch { return null; }
        }

        static string Unescape(string s)
        {
            return s.Replace("\\\\", "\\").Replace("\\\"", "\"");
        }

        static bool IsNumber(string s)
        {
            if (s.Length == 0) return false;
            foreach (char c in s)
                if (c < '0' || c > '9')
                    return false;
            return true;
        }

        static bool SamePath(string a, string b)
        {
            return string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        static string ReadText(string file)
        {
            try { return File.Exists(file) ? File.ReadAllText(file) : null; }
            catch { return null; }
        }
    }

    /// <summary>Finds the SteamVR runtime and its settings file, and tells whether SteamVR is running. Never writes.</summary>
    public static class SteamVRLocator
    {
        /// <summary>OpenVR's own record of where the runtime is (written by SteamVR).</summary>
        public static string PathsFile(IMachine machine)
        {
            string local = machine.Folder(KnownFolder.LocalAppData);
            return local == null ? null : Path.Combine(local, "openvr", "openvrpaths.vrpath");
        }

        /// <summary>
        ///     The SteamVR runtime folder: from OpenVR's path file, else from Steam's libraries. Null when SteamVR is not
        ///     installed.
        /// </summary>
        public static string RuntimeDir(IMachine machine)
        {
            foreach (string dir in PathList(machine, "runtime"))
                if (Directory.Exists(dir))
                    return dir;
            return SteamLocator.AppDir(machine, SteamLocator.SteamVRAppId);
        }

        /// <summary>openvr_api.dll of the installed runtime, or null.</summary>
        public static string ApiLibrary(IMachine machine)
        {
            string rt = RuntimeDir(machine);
            if (rt == null) return null;
            string dll = Path.Combine(rt, "bin", "win64", "openvr_api.dll");
            return File.Exists(dll) ? dll : null;
        }

        /// <summary>SteamVR's settings file (tracker roles and the like; read-only), or null when it is not there.</summary>
        public static string SettingsFile(IMachine machine)
        {
            var dirs = PathList(machine, "config");
            string steam = SteamLocator.InstallDir(machine);
            if (steam != null) dirs.Add(Path.Combine(steam, "config"));
            foreach (string dir in dirs)
            {
                string f = Path.Combine(dir, "steamvr.vrsettings");
                if (File.Exists(f)) return f;
            }

            return null;
        }

        /// <summary>Whether SteamVR's server process is running.</summary>
        public static bool IsRunning(IMachine machine)
        {
            return machine.IsProcessRunning("vrserver");
        }

        static List<string> PathList(IMachine machine, string key)
        {
            var list = new List<string>();
            try
            {
                string file = PathsFile(machine);
                if (file == null || !File.Exists(file)) return list;
                var entries = Json.ParseObject(File.ReadAllText(file))?.GetList(key);
                if (entries != null)
                    foreach (object e in entries)
                        if (e is string s && s.Length > 0)
                            list.Add(s);
            }
            catch { }

            return list;
        }
    }
}
