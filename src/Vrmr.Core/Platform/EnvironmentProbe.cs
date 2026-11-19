using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VRMR
{
    /// <summary>How one part of the environment looks.</summary>
    public enum ProbeState
    {
        /// <summary>Found and usable.</summary>
        Ok,

        /// <summary>Usable, but something deserves a look.</summary>
        Attention,

        /// <summary>Not found, or not usable.</summary>
        Missing,

        /// <summary>Could not be determined.</summary>
        Unknown
    }

    /// <summary>One line of the environment report: what was looked at, how it is, and what to do about it.</summary>
    public sealed class ProbeItem
    {
        public ProbeItem(string id, string title, ProbeState state, string summary, string action = null,
            string path = null, bool optional = false)
        {
            Id = id;
            Title = title;
            State = state;
            Summary = summary;
            Action = action;
            Path = path;
            Optional = optional;
        }

        /// <summary>Stable identifier ("steamvr", "vrchat.osc", "folder.recordings", ...).</summary>
        public string Id { get; }

        /// <summary>Short name for a list row.</summary>
        public string Title { get; }

        public ProbeState State { get; }

        /// <summary>One sentence saying what was found.</summary>
        public string Summary { get; }

        /// <summary>What the user should do, or null when nothing is needed.</summary>
        public string Action { get; }

        /// <summary>The folder or file the item is about, or null.</summary>
        public string Path { get; }

        /// <summary>True when the app records without it.</summary>
        public bool Optional { get; }

        public override string ToString()
        {
            return $"{Id}: {State}: {Summary}" + (Action != null ? " -> " + Action : "");
        }
    }

    /// <summary>What the user has configured, as far as the probe needs it.</summary>
    public sealed class ProbeOptions
    {
        /// <summary>The avatar library folder in use (null = the default).</summary>
        public string AvatarLibraryDir;

        /// <summary>The user's own Blender path ("" = none).</summary>
        public string BlenderPath = "";

        /// <summary>The takes folder in use (null = the default).</summary>
        public string RecordingsDir;

        /// <summary>Unity projects the user added by hand.</summary>
        public List<string> UnityProjects = new();
    }

    /// <summary>SteamVR as found on the machine.</summary>
    public sealed class SteamVRStatus
    {
        public bool Installed { get; internal set; }
        public bool Running { get; internal set; }

        /// <summary>The runtime folder, or null.</summary>
        public string RuntimeDir { get; internal set; }

        /// <summary>steamvr.vrsettings, or null.</summary>
        public string SettingsFile { get; internal set; }
    }

    /// <summary>VRChat as found on the machine.</summary>
    public sealed class VrChatStatus
    {
        public string DataDir { get; internal set; }

        /// <summary>The data folder exists: VRChat has run for this user.</summary>
        public bool Exists { get; internal set; }

        /// <summary>The newest output log, or null.</summary>
        public string NewestLog { get; internal set; }

        public DateTime? NewestLogTimeUtc { get; internal set; }

        /// <summary>VRChat's own OSC switch; null when it cannot be read.</summary>
        public bool? OscEnabled { get; internal set; }

        /// <summary>Whether VRChat sends the headset's and wrists' poses over OSC; null when it cannot be read.</summary>
        public bool? SendsHeadAndWrists { get; internal set; }

        /// <summary>VRChat's "User Real Height" in metres, 0 when it is not set.</summary>
        public float UserHeight { get; internal set; }
    }

    /// <summary>Everything <see cref="EnvironmentProbe" /> found.</summary>
    public sealed class EnvironmentReport
    {
        readonly List<ProbeItem> _items = new();

        public IReadOnlyList<ProbeItem> Items => _items;

        /// <summary>The item with this id, or null.</summary>
        public ProbeItem this[string id] => _items.FirstOrDefault(i => i.Id == id);

        public SteamVRStatus SteamVR { get; } = new();
        public VrChatStatus VRChat { get; } = new();
        public IReadOnlyList<BlenderInstall> Blender { get; internal set; } = new List<BlenderInstall>();
        public IReadOnlyList<UnityProjectInfo> UnityProjects { get; internal set; } = new List<UnityProjectInfo>();

        /// <summary>
        ///     The least that recording needs: VRChat's folder, OSC not known to be off, and a takes folder that can be
        ///     written.
        /// </summary>
        public bool CanRecord => this[EnvironmentProbe.VrChat]?.State == ProbeState.Ok &&
                                 this[EnvironmentProbe.VrChatOsc]?.State != ProbeState.Missing &&
                                 this[EnvironmentProbe.RecordingsFolder]?.State == ProbeState.Ok;

        internal void Add(ProbeItem item)
        {
            _items.Add(item);
        }
    }

    /// <summary>
    ///     Looks at what the app needs around it (SteamVR, VRChat, Blender, Unity projects, its own folders) and reports
    ///     it for the first-run guide and the Settings pages. It changes nothing and never throws. It reads the registry
    ///     and lists a few folders, so run it on a worker thread.
    /// </summary>
    public static class EnvironmentProbe
    {
        public const string SteamVR = "steamvr",
            VrChat = "vrchat",
            VrChatOsc = "vrchat.osc",
            VrChatLog = "vrchat.log",
            Blender = "blender",
            Unity = "unity",
            RecordingsFolder = "folder.recordings",
            AvatarsFolder = "folder.avatars",
            AppDataFolder = "folder.appdata";

        const string OscHint =
            "In VRChat, open the Action Menu, then Options, then OSC, and switch Enabled on. The app never changes VRChat's settings.";

        /// <summary>Probes the machine the given paths belong to.</summary>
        public static EnvironmentReport Run(AppPaths paths, ProbeOptions options = null)
        {
            return Run(paths.Machine, paths, options);
        }

        /// <summary>Probes a machine, with the app's folders taken from <paramref name="paths" />.</summary>
        public static EnvironmentReport Run(IMachine machine, AppPaths paths, ProbeOptions options = null)
        {
            options = options ?? new ProbeOptions();
            var report = new EnvironmentReport();
            Guard(report, SteamVR, "SteamVR", () => ProbeSteamVR(machine, report));
            Guard(report, VrChat, "VRChat", () => ProbeVrChat(machine, report));
            Guard(report, Blender, "Blender", () => ProbeBlender(machine, options, report));
            Guard(report, Unity, "Unity projects", () => ProbeUnity(machine, options, report));
            Guard(report, RecordingsFolder, "Takes folder",
                () => report.Add(Folder(RecordingsFolder, "Takes folder", "Takes are", "Takes will be",
                    string.IsNullOrEmpty(options.RecordingsDir) ? paths.RecordingsDir : options.RecordingsDir)));
            Guard(report, AvatarsFolder, "Avatar library",
                () => report.Add(Folder(AvatarsFolder, "Avatar library", "Avatars are", "Avatars will be",
                    string.IsNullOrEmpty(options.AvatarLibraryDir)
                        ? paths.AvatarLibraryDir
                        : options.AvatarLibraryDir)));
            Guard(report, AppDataFolder, "Settings folder",
                () => report.Add(Folder(AppDataFolder, "Settings folder", "Settings and calibrations are",
                    "Settings and calibrations will be", paths.AppDataDir)));
            return report;
        }

        static void Guard(EnvironmentReport report, string id, string title, Action probe)
        {
            try { probe(); }
            catch (Exception ex)
            {
                if (report[id] == null)
                    report.Add(new ProbeItem(id, title, ProbeState.Unknown,
                        "This could not be checked (" + ex.Message + ")."));
            }
        }

        static void ProbeSteamVR(IMachine machine, EnvironmentReport report)
        {
            var s = report.SteamVR;
            s.RuntimeDir = SteamVRLocator.RuntimeDir(machine);
            s.Installed = s.RuntimeDir != null;
            s.SettingsFile = SteamVRLocator.SettingsFile(machine);
            s.Running = s.Installed && SteamVRLocator.IsRunning(machine);
            if (!s.Installed)
                report.Add(new ProbeItem(SteamVR, "SteamVR", ProbeState.Missing, "SteamVR was not found.",
                    "Install SteamVR from Steam to record the headset, controllers, trackers and fingers. Face tracking and avatar parameters record without it.",
                    optional: true));
            else if (s.Running)
                report.Add(new ProbeItem(SteamVR, "SteamVR", ProbeState.Ok, "SteamVR is installed and running.",
                    path: s.RuntimeDir, optional: true));
            else
                report.Add(new ProbeItem(SteamVR, "SteamVR", ProbeState.Ok,
                    "SteamVR is installed and not running at the moment.",
                    "Start SteamVR before recording body motion; the app connects on its own.", s.RuntimeDir, true));
        }

        static void ProbeVrChat(IMachine machine, EnvironmentReport report)
        {
            var v = report.VRChat;
            var folders = new VrcFolders(machine);
            v.DataDir = folders.DataDir;
            v.Exists = folders.Exists;
            if (!v.Exists)
            {
                report.Add(new ProbeItem(VrChat, "VRChat", ProbeState.Missing,
                    "VRChat has not been run by this user on this computer.", "Start VRChat once, then check again."));
                report.Add(new ProbeItem(VrChatOsc, "VRChat OSC", ProbeState.Unknown,
                    "VRChat's OSC setting is not there yet.", OscHint));
                report.Add(new ProbeItem(VrChatLog, "VRChat log", ProbeState.Missing,
                    "There is no VRChat log to follow yet.", "It appears when VRChat has been started once.",
                    optional: true));
                return;
            }

            report.Add(new ProbeItem(VrChat, "VRChat", ProbeState.Ok, "VRChat's data folder was found.",
                path: v.DataDir));

            var prefs = new UnityPlayerPrefs(machine, "VRChat", "VRChat");
            int? osc = prefs.Int("VRC_INPUT_OSC") ?? prefs.Int("UI.Settings.Osc");
            v.OscEnabled = osc.HasValue ? osc.Value != 0 : null;
            int? poses = prefs.Int("VRC_TRACKING_SEND_VR_SYSTEM_HEAD_AND_WRIST_OSC_DATA");
            v.SendsHeadAndWrists = poses.HasValue ? poses.Value != 0 : null;
            float height = prefs.Float("PlayerHeight") ?? 0;
            v.UserHeight = height >= 0.5f && height <= 3f ? height : 0;
            if (v.OscEnabled == true)
                report.Add(new ProbeItem(VrChatOsc, "VRChat OSC", ProbeState.Ok, "OSC is turned on in VRChat."));
            else if (v.OscEnabled == false)
                report.Add(new ProbeItem(VrChatOsc, "VRChat OSC", ProbeState.Missing,
                    "OSC is turned off in VRChat, so VRChat sends nothing to record.", OscHint));
            else
                report.Add(new ProbeItem(VrChatOsc, "VRChat OSC", ProbeState.Unknown,
                    "VRChat's OSC setting could not be read.", OscHint));

            v.NewestLog = folders.NewestLog();
            if (v.NewestLog == null)
                report.Add(new ProbeItem(VrChatLog, "VRChat log", ProbeState.Missing, "VRChat has written no log.",
                    "Calibrations, world and avatar names are read from VRChat's log: leave VRChat's logging on.",
                    v.DataDir, true));
            else
            {
                try { v.NewestLogTimeUtc = File.GetLastWriteTimeUtc(v.NewestLog); }
                catch { }

                report.Add(new ProbeItem(VrChatLog, "VRChat log", ProbeState.Ok,
                    "VRChat's newest log is " + Path.GetFileName(v.NewestLog) + ".", path: v.NewestLog,
                    optional: true));
            }
        }

        static void ProbeBlender(IMachine machine, ProbeOptions options, EnvironmentReport report)
        {
            var found = BlenderInstalls.Find(machine, options.BlenderPath);
            report.Blender = found;
            bool userSet = !string.IsNullOrWhiteSpace(options.BlenderPath);
            bool userOk = userSet && found.Count > 0 && found[0].Source == BlenderSource.User;
            if (found.Count == 0)
                report.Add(new ProbeItem(Blender, "Blender", ProbeState.Missing,
                    userSet
                        ? "The Blender path in Settings does not point to Blender, and no other Blender was found."
                        : "Blender was not found.",
                    "Install Blender, or set its path in Settings, to open exports in Blender with one click.",
                    optional: true));
            else if (userSet && !userOk)
                report.Add(new ProbeItem(Blender, "Blender", ProbeState.Attention,
                    $"The Blender path in Settings does not point to Blender; {found[0].Label} is used instead.",
                    "Correct the path in Settings, or clear it.", found[0].Exe, true));
            else
                report.Add(new ProbeItem(Blender, "Blender", ProbeState.Ok,
                    found[0].Label + " was found" + (found.Count > 1 ? $", and {found.Count - 1} more." : "."),
                    path: found[0].Exe, optional: true));
        }

        static void ProbeUnity(IMachine machine, ProbeOptions options, EnvironmentReport report)
        {
            var all = UnityProjects.Find(machine, options.UnityProjects);
            report.UnityProjects = all;
            var there = all.Where(p => p.Exists).ToList();
            int withBridge = there.Count(p => p.HasBridge);
            if (all.Count == 0)
                report.Add(new ProbeItem(Unity, "Unity projects", ProbeState.Missing, "No projects added.",
                    "Add projects in Settings → Folders → Unity projects.",
                    optional: true));
            else if (withBridge == 0)
                report.Add(new ProbeItem(Unity, "Unity projects", ProbeState.Attention,
                    $"0 of {Count(all.Count, "linked project")} have the bridge.",
                    "Add the bridge package to the project you export to (see the README).", all[0].Path, true));
            else
                report.Add(new ProbeItem(Unity, "Unity projects", ProbeState.Ok,
                    $"{withBridge} of {Count(all.Count, "linked project")} " + (withBridge == 1 ? "has" : "have") +
                    " the bridge.",
                    path: there.First(p => p.HasBridge).Path, optional: true));
        }

        static string Count(int n, string noun)
        {
            return n + " " + noun + (n == 1 ? "" : "s");
        }

        static ProbeItem Folder(string id, string title, string are, string willBe, string dir)
        {
            bool exists = Directory.Exists(dir);
            if (!FolderAccess.CanWrite(dir))
                return new ProbeItem(id, title, ProbeState.Missing,
                    exists ? $"{dir} cannot be written to." : $"{dir} does not exist and cannot be created.",
                    "Pick another folder in Settings.", dir);
            return new ProbeItem(id, title, ProbeState.Ok,
                exists ? $"{are} kept in {dir}." : $"{willBe} kept in {dir} (created on first use).", path: dir);
        }
    }
}
