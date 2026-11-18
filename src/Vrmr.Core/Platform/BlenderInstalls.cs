using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VRMR
{
    /// <summary>How a Blender installation was found.</summary>
    public enum BlenderSource { User, Registry, InstallFolder, Launcher, Steam }

    /// <summary>The kind of build, lowest first so that release builds sort above daily and experimental ones.</summary>
    public enum BlenderChannel { Experimental, Daily, Release }

    /// <summary>One Blender found on the machine.</summary>
    public sealed class BlenderInstall
    {
        /// <summary>Full path of blender.exe.</summary>
        public string Exe { get; }
        /// <summary>The build's version (0.0 when it cannot be read).</summary>
        public Version Version { get; }
        public BlenderSource Source { get; }
        public BlenderChannel Channel { get; }

        public BlenderInstall(string exe, Version version, BlenderSource source, BlenderChannel channel)
        {
            Exe = exe; Version = version; Source = source; Channel = channel;
        }

        /// <summary>"Blender 5.3" (plus the channel when it is not a release).</summary>
        public string Label => "Blender " + Version.ToString(2) + (Channel == BlenderChannel.Release ? "" : " " + Channel.ToString().ToLowerInvariant());
        public override string ToString() => $"{Label} ({Source}): {Exe}";
    }

    /// <summary>
    /// Finds Blender: a path the user set, the registry (the .blend file type and the installer's uninstall records),
    /// the standard install folders, a Blender Launcher library there, and Steam. Never writes.
    /// </summary>
    public static class BlenderInstalls
    {
        const string VendorFolder = "Blender Foundation";
        const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        static readonly string ExeName = OperatingSystem.IsWindows() ? "blender.exe" : "blender";

        /// <summary>
        /// Every Blender found, best first: the user's own choice, then release builds before daily and experimental
        /// ones, then the highest version. <paramref name="userPath"/> is a blender executable or the folder holding it.
        /// </summary>
        public static List<BlenderInstall> Find(IMachine machine, string userPath = null)
        {
            var found = new List<BlenderInstall>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string exe, BlenderSource source, BlenderChannel channel)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(exe)) return;
                    exe = Path.GetFullPath(exe);
                    if (!File.Exists(exe) || !seen.Add(exe)) return;
                    found.Add(new BlenderInstall(exe, VersionOf(exe), source, channel));
                }
                catch { }
            }
            void AddFolder(string dir, BlenderSource source, BlenderChannel channel) { if (!string.IsNullOrWhiteSpace(dir)) Add(Path.Combine(dir.Trim().Trim('"'), ExeName), source, channel); }

            // 1. the user's own path: a file, or the folder it is in
            if (!string.IsNullOrWhiteSpace(userPath))
            {
                string p = userPath.Trim().Trim('"');
                if (Directory.Exists(p)) AddFolder(p, BlenderSource.User, BlenderChannel.Release); else Add(p, BlenderSource.User, BlenderChannel.Release);
            }
            int userCount = found.Count;

            // 2. the registry: what opens .blend files, and what the installer recorded
            string progId = machine.RegistryValue(RegistryRoot.ClassesRoot, ".blend", "") as string;
            foreach (var id in new[] { progId, "blendfile" })
                if (!string.IsNullOrEmpty(id))
                    AddFolder(FolderOfCommand(machine.RegistryValue(RegistryRoot.ClassesRoot, id + @"\shell\open\command", "") as string), BlenderSource.Registry, BlenderChannel.Release);
            foreach (var sub in machine.RegistrySubKeys(RegistryRoot.LocalMachine, UninstallKey))
            {
                string key = UninstallKey + "\\" + sub;
                if (!(machine.RegistryValue(RegistryRoot.LocalMachine, key, "DisplayName") is string name) || !name.StartsWith("blender", StringComparison.OrdinalIgnoreCase)) continue;
                AddFolder(machine.RegistryValue(RegistryRoot.LocalMachine, key, "InstallLocation") as string, BlenderSource.Registry, BlenderChannel.Release);
            }

            // 3. the standard install folders (one sub-folder per version), also used by Blender Launcher for its library
            foreach (var root in new[] { machine.Folder(KnownFolder.ProgramFiles), machine.Folder(KnownFolder.ProgramFilesX86), Combine(machine.Folder(KnownFolder.LocalAppData), "Programs") })
            {
                string vendor = Combine(root, VendorFolder);
                foreach (var dir in SubFolders(vendor))
                {
                    var channel = LauncherChannel(Path.GetFileName(dir));
                    if (channel == null) AddFolder(dir, BlenderSource.InstallFolder, BlenderChannel.Release);
                    else foreach (var build in SubFolders(dir)) AddFolder(build, BlenderSource.Launcher, channel.Value);
                }
            }

            // 4. Steam
            AddFolder(SteamLocator.AppDir(machine, SteamLocator.BlenderAppId), BlenderSource.Steam, BlenderChannel.Release);

            var rest = found.Skip(userCount).OrderByDescending(b => b.Channel).ThenByDescending(b => b.Version);
            return found.Take(userCount).Concat(rest).ToList();
        }

        /// <summary>Full version from the build folder's name ("blender-5.3.0-stable.hash", "Blender 5.3"), else the file's own version.</summary>
        public static Version VersionOf(string exe)
        {
            string dir = Path.GetFileName(Path.GetDirectoryName(exe)) ?? "";
            var m = Regex.Match(dir, @"(\d+)\.(\d+)(?:\.(\d+))?");
            if (m.Success)
                return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
            try
            {
                var fv = FileVersionInfo.GetVersionInfo(exe);
                if (fv.FileMajorPart > 0) return new Version(fv.FileMajorPart, fv.FileMinorPart, Math.Max(0, fv.FileBuildPart));
            }
            catch { }
            return new Version(0, 0);
        }

        /// <summary>Blender Launcher keeps builds in folders named after their channel; null for any other folder.</summary>
        static BlenderChannel? LauncherChannel(string folder)
        {
            switch ((folder ?? "").ToLowerInvariant())
            {
                case "stable": return BlenderChannel.Release;
                case "daily": return BlenderChannel.Daily;
                case "experimental": case "custom": case "bforartists": return BlenderChannel.Experimental;
                default: return null;
            }
        }

        /// <summary>The folder of the program in a shell command such as "&lt;path&gt;\blender-launcher.exe" "%1".</summary>
        static string FolderOfCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            command = command.Trim();
            string exe;
            if (command[0] == '"') { int end = command.IndexOf('"', 1); exe = end > 1 ? command.Substring(1, end - 1) : null; }
            else { int end = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); exe = end > 0 ? command.Substring(0, end + 4) : command.Split(' ')[0]; }
            try { return string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe); } catch { return null; }
        }

        static string Combine(string root, string name) => root == null ? null : Path.Combine(root, name);

        static string[] SubFolders(string dir)
        {
            try { return dir != null && Directory.Exists(dir) ? Directory.GetDirectories(dir) : Array.Empty<string>(); }
            catch { return Array.Empty<string>(); }
        }
    }
}
