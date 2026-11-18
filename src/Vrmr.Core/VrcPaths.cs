using System;
using System.IO;
using System.Linq;

namespace VRMR
{
    /// <summary>Where VRChat keeps its files for the current user on a machine (read-only for this app).</summary>
    public sealed class VrcFolders
    {
        /// <summary>Steam's app id of VRChat.</summary>
        public const int SteamAppId = 438100;

        /// <summary>VRChat's folders on a machine.</summary>
        public VrcFolders(IMachine machine)
        {
            if (machine == null) throw new ArgumentNullException(nameof(machine));
            DataDir = Locate(machine);
        }

        /// <summary>VRChat's data folder (LocalLow\VRChat\VRChat). When it cannot be located this is a folder that does not exist.</summary>
        public string DataDir { get; }

        /// <summary>The folder with VRChat's output_log_*.txt files.</summary>
        public string LogDir => DataDir;

        /// <summary>VRChat's OSC folder (per-user avatar parameter configs).</summary>
        public string OscDir => Path.Combine(DataDir, "OSC");

        /// <summary>Whether VRChat has run for this user (its data folder exists).</summary>
        public bool Exists => Directory.Exists(DataDir);

        /// <summary>The most recently written VRChat log, or null.</summary>
        public string NewestLog()
        {
            try
            {
                var dir = new DirectoryInfo(LogDir);
                if (!dir.Exists) return null;
                return dir.GetFiles("output_log_*.txt").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
                    ?.FullName;
            }
            catch { return null; }
        }

        static string Locate(IMachine machine)
        {
            // Windows: the LocalLow known folder (it can be redirected, so it is asked for, not built from the profile path)
            string low = machine.Folder(KnownFolder.LocalAppDataLow);
            if (low != null) return Path.Combine(low, "VRChat", "VRChat");
            // elsewhere VRChat runs under Steam's compatibility layer, with its own Windows profile per app
            foreach (string lib in SteamLocator.Libraries(machine))
            {
                string prefix = Path.Combine(lib, "steamapps", "compatdata", SteamAppId.ToString(), "pfx", "drive_c",
                    "users", "steamuser", "AppData", "LocalLow", "VRChat", "VRChat");
                if (Directory.Exists(prefix)) return prefix;
            }

            return Path.Combine(machine.Folder(KnownFolder.Temp) ?? Path.GetTempPath(), AppPaths.AppDataFolderName,
                "vrchat-not-found");
        }
    }

    /// <summary>VRChat's folders on the machine this process looks at (<see cref="AppPaths.Current" />).</summary>
    public static class VrcPaths
    {
        static VrcFolders _folders;
        static IMachine _machine;
        static readonly object Lock = new();

        /// <summary>VRChat's folders for the current machine.</summary>
        public static VrcFolders Current
        {
            get
            {
                var machine = AppPaths.Current.Machine;
                lock (Lock)
                {
                    if (_folders == null || !ReferenceEquals(_machine, machine))
                    {
                        _folders = new VrcFolders(machine);
                        _machine = machine;
                    }

                    return _folders;
                }
            }
        }

        /// <summary>VRChat's data folder.</summary>
        public static string DataDir => Current.DataDir;

        /// <summary>The folder with VRChat's output_log_*.txt files.</summary>
        public static string LogDir => Current.LogDir;

        /// <summary>VRChat's OSC folder (per-user avatar parameter configs).</summary>
        public static string OscDir => Current.OscDir;
    }
}
