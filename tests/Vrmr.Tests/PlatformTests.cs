using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace VRMR.Tests
{
    public class AppPathsTests
    {
        [Fact]
        public void ADataRootHoldsEverything()
        {
            using (var tmp = new TempDir())
            {
                var p = AppPaths.ForDataRoot(tmp.Path);
                Assert.Equal(tmp.Path, p.DataRoot);
                Assert.True(p.Isolated);
                foreach (string path in new[]
                         {
                             p.AppDataDir, p.SettingsFile, p.UiStateFile, p.LogFile, p.CalibrationPresetsDir,
                             p.CalibrationSnapshotsDir, p.UnityProfilesDir,
                             p.PunchInFile, p.RoomFile, p.DocumentsDir, p.RecordingsDir, p.AvatarLibraryDir,
                             p.ScenesDir, p.ExportsDir, p.WritableSteamVRManifestDir(),
                             new VrcFolders(p.Machine).DataDir, SteamVRLocator.PathsFile(p.Machine)
                         })
                    Assert.StartsWith(tmp.Path, path, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void AnIsolatedDataRootSeesNoOtherPrograms()
        {
            using (var tmp = new TempDir())
            {
                var m = AppPaths.ForDataRoot(tmp.Path).Machine;
                Assert.IsType<SandboxMachine>(m);
                Assert.Null(SteamLocator.InstallDir(m));
                Assert.Null(SteamVRLocator.RuntimeDir(m));
                Assert.False(new VrcFolders(m).Exists);
                Assert.Empty(BlenderInstalls.Find(m));
                Assert.Empty(UnityProjects.Find(m));
                Assert.False(VrcSettings.ReadFrom(m).Found);
            }
        }

        [Fact]
        public void TheCommandLinePicksTheDataFolder()
        {
            using (var tmp = new TempDir())
            {
                Assert.Equal(tmp.Path, AppPaths.FromCommandLine(new[] { "--no-capture", "--data", tmp.Path }).DataRoot);
                Assert.Equal(tmp.Path, AppPaths.FromCommandLine(new[] { "--data=" + tmp.Path }).DataRoot);
                Assert.Null(AppPaths.DataArgument(new[] { "--no-capture" }));
                Assert.Null(AppPaths.DataArgument(new[] { "--data" }));
            }
        }

        [Fact]
        public void NormalRunsUseTheUsualFolderNames()
        {
            using (var tmp = new TempDir())
            {
                var machine = new SandboxMachine(tmp.Path);
                var p = AppPaths.ForMachine(machine);
                Assert.Equal(Path.Combine(machine.Folder(KnownFolder.AppData), "VRMotionRecorder"), p.AppDataDir);
                Assert.Equal(Path.Combine(machine.Folder(KnownFolder.Documents), "VR Motion Recorder"), p.DocumentsDir);
                Assert.Equal(Path.Combine(p.DocumentsDir, "Avatars"), p.AvatarLibraryDir);
                Assert.Null(p.DataRoot);
            }
        }

        [Fact]
        public void TheTestRunItselfIsIsolated()
        {
            Assert.Equal(TestEnvironment.Root, AppPaths.Current.DataRoot);
            Assert.StartsWith(TestEnvironment.Root, Settings.AppDataDir, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(TestEnvironment.Root, VrcPaths.LogDir, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void WritableIsJudgedWithoutLeavingAnythingBehind()
        {
            using (var tmp = new TempDir())
            {
                string missing = Path.Combine(tmp.Path, "not", "there", "yet");
                Assert.True(FolderAccess.CanWrite(missing));
                Assert.False(Directory.Exists(Path.Combine(tmp.Path, "not")));
                Assert.Empty(Directory.GetFileSystemEntries(tmp.Path));
            }
        }
    }

    /// <summary>A sandboxed machine filled with what the look-ups search for.</summary>
    sealed class FakeMachine : IDisposable
    {
        readonly TempDir _tmp = new();
        public FakeMachine() { Machine = new SandboxMachine(_tmp.Path); }
        public SandboxMachine Machine { get; }
        public string Root => _tmp.Path;

        public void Dispose()
        {
            _tmp.Dispose();
        }

        public string Dir(params string[] parts)
        {
            string d = Path.Combine(new[] { Root }.Concat(parts).ToArray());
            Directory.CreateDirectory(d);
            return d;
        }

        public string File(string dir, string name, string text = "")
        {
            Directory.CreateDirectory(dir);
            string f = Path.Combine(dir, name);
            System.IO.File.WriteAllText(f, text);
            return f;
        }

        /// <summary>Steam with one extra library; returns (steam folder, library folder).</summary>
        public (string steam, string library) AddSteam()
        {
            string steam = Dir("Apps", "Steam"), lib = Dir("Games", "SteamLibrary");
            // Steam stores this lowercased with forward slashes; only a case-insensitive file system can follow that
            string steamPath = steam.Replace('\\', '/');
            if (OperatingSystem.IsWindows()) steamPath = steamPath.ToLowerInvariant();
            Machine.SetRegistry(RegistryRoot.CurrentUser, @"Software\Valve\Steam", "SteamPath", steamPath);
            File(Path.Combine(steam, "steamapps"), "libraryfolders.vdf",
                "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + steam.Replace("\\", "\\\\") +
                "\"\n\t\t\"apps\"\n\t\t{\n\t\t\t\"228980\"\t\t\"421212\"\n\t\t}\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"" +
                lib.Replace("\\", "\\\\") + "\"\n\t}\n}\n");
            return (steam, lib);
        }

        public string AddSteamApp(string library, int appId, string folder)
        {
            File(Path.Combine(library, "steamapps"), "appmanifest_" + appId + ".acf",
                "\"AppState\"\n{\n\t\"appid\"\t\t\"" + appId + "\"\n\t\"installdir\"\t\t\"" + folder + "\"\n}\n");
            return Dir(Path.GetRelativePath(Root, Path.Combine(library, "steamapps", "common", folder)));
        }

        public string AddVrChat(string logName = "output_log_2026-01-01_10-00-00.txt")
        {
            string dir = new VrcFolders(Machine).DataDir;
            if (logName != null) File(dir, logName, "log\n");
            else Directory.CreateDirectory(dir);
            return dir;
        }

        public void SetVrChatPref(string name, object value)
        {
            Machine.SetRegistry(RegistryRoot.CurrentUser, @"Software\VRChat\VRChat", name + "_h123456789", value);
        }
    }

    public class LocatorTests
    {
        [Fact]
        public void SteamLibrariesComeFromSteamsOwnList()
        {
            using (var f = new FakeMachine())
            {
                (string steam, string lib) = f.AddSteam();
                var libs = SteamLocator.Libraries(f.Machine);
                Assert.Equal(new[] { steam, lib }, libs, StringComparer.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void SteamVRIsFoundInWhicheverLibraryHoldsIt()
        {
            using (var f = new FakeMachine())
            {
                (_, string lib) = f.AddSteam();
                string dir = f.AddSteamApp(lib, SteamLocator.SteamVRAppId, "SteamVR");
                Assert.Equal(dir, SteamVRLocator.RuntimeDir(f.Machine), true);
                Assert.False(SteamVRLocator.IsRunning(f.Machine));
                f.Machine.SetRunning("vrserver");
                Assert.True(SteamVRLocator.IsRunning(f.Machine));
            }
        }

        [Fact]
        public void OpenVRsPathFileWinsAndNamesTheSettingsFile()
        {
            using (var f = new FakeMachine())
            {
                string runtime = f.Dir("Elsewhere", "SteamVR"), config = f.Dir("Elsewhere", "config");
                string settings = f.File(config, "steamvr.vrsettings", "{}");
                var json = new Dictionary<string, object>
                    { ["runtime"] = new List<object> { runtime }, ["config"] = new List<object> { config } };
                f.File(Path.GetDirectoryName(SteamVRLocator.PathsFile(f.Machine)), "openvrpaths.vrpath",
                    Json.Serialize(json));
                Assert.Equal(runtime, SteamVRLocator.RuntimeDir(f.Machine));
                Assert.Equal(settings, SteamVRLocator.SettingsFile(f.Machine));
            }
        }

        [Fact]
        public void VrChatsFolderIsUnderLocalLow()
        {
            using (var f = new FakeMachine())
            {
                var folders = new VrcFolders(f.Machine);
                Assert.Equal(Path.Combine(f.Machine.Folder(KnownFolder.LocalAppDataLow), "VRChat", "VRChat"),
                    folders.DataDir);
                Assert.False(folders.Exists);
                Assert.Null(folders.NewestLog());
                f.AddVrChat();
                string newer = f.File(folders.LogDir, "output_log_2026-01-02_10-00-00.txt", "x");
                File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddMinutes(5));
                Assert.Equal(newer, folders.NewestLog());
            }
        }

        [Fact]
        public void BlenderIsFoundInInstallFoldersTheRegistrySteamAndAUserPath()
        {
            using (var f = new FakeMachine())
            {
                string exe = OperatingSystem.IsWindows() ? "blender.exe" : "blender";
                string vendor = Path.Combine(f.Machine.Folder(KnownFolder.ProgramFiles), "Blender Foundation");
                string installed = f.File(Path.Combine(vendor, "Blender 5.2"), exe);
                string daily = f.File(Path.Combine(vendor, "daily", "blender-5.4.0-alpha+main.abc"), exe);
                string stable = f.File(Path.Combine(vendor, "stable", "blender-5.3.1-stable.def"), exe);
                string assoc = f.File(f.Dir("Tools", "Blender 4.5"), exe);
                f.Machine.SetRegistry(RegistryRoot.ClassesRoot, ".blend", "", "blendfile");
                f.Machine.SetRegistry(RegistryRoot.ClassesRoot, @"blendfile\shell\open\command", "",
                    "\"" + Path.Combine(Path.GetDirectoryName(assoc), "blender-launcher.exe") + "\" \"%1\"");
                (_, string lib) = f.AddSteam();
                string steam = f.File(f.AddSteamApp(lib, SteamLocator.BlenderAppId, "Blender"), exe);
                string own = f.File(f.Dir("Mine", "blender-3.6.0"), exe);

                var found = BlenderInstalls.Find(f.Machine);
                Assert.Equal(new[] { stable, installed, assoc, steam, daily }, found.Select(b => b.Exe).ToArray(),
                    StringComparer.OrdinalIgnoreCase);
                Assert.Equal(new Version(5, 3, 1), found[0].Version);
                Assert.Equal(BlenderSource.Launcher, found[0].Source);
                Assert.Equal(BlenderSource.Registry, found[2].Source);
                Assert.Equal(BlenderChannel.Daily, found[4].Channel);

                // the user's own choice comes first, whatever its version; a folder is as good as the file
                var withUser = BlenderInstalls.Find(f.Machine, Path.GetDirectoryName(own));
                Assert.Equal(own, withUser[0].Exe, true);
                Assert.Equal(BlenderSource.User, withUser[0].Source);
                Assert.Equal(6, withUser.Count);
            }
        }

        [Fact]
        public void UnityProjectsComeFromTheHubListWithTheBridgeNoted()
        {
            using (var f = new FakeMachine())
            {
                string Project(string name, string version)
                {
                    string dir = f.Dir("Projects", name);
                    Directory.CreateDirectory(Path.Combine(dir, "Assets"));
                    f.File(Path.Combine(dir, "ProjectSettings"), "ProjectVersion.txt",
                        "m_EditorVersion: " + version + "\nm_EditorVersionWithRevision: " + version + " (abc)\n");
                    return dir;
                }

                string plain = Project("Plain", "2022.3.22f1"),
                    copied = Project("Copied", "2022.3.22f1"),
                    byUrl = Project("ByUrl", "6000.0.1f1"),
                    mine = Project("Mine", "2022.3.6f1");
                f.File(Path.Combine(copied, "Assets", "VRMotionBridge", "Editor"), UnityProjects.BridgeAssembly, "{}");
                f.File(Path.Combine(byUrl, "Packages"), "manifest.json",
                    "{ \"dependencies\": { \"com.example.vrmotionbridge\": \"https://example.invalid/bridge.git\" } }");
                string gone = Path.Combine(f.Root, "Projects", "Gone");

                string Entry(string path, string title, double modified)
                {
                    return "\"" + path.Replace("\\", "\\\\") + "\": { \"title\": \"" + title + "\", \"path\": \"" +
                           path.Replace("\\", "\\\\") + "\", \"version\": \"2022.3.22f1\", \"lastModified\": " +
                           modified + " }";
                }

                f.File(Path.GetDirectoryName(UnityProjects.HubProjectsFile(f.Machine)), "projects-v1.json",
                    "{ \"schema_version\": \"v1\", \"data\": { " + Entry(plain, "Plain", 100) + ", " +
                    Entry(copied, "Copied", 300) + ", " + Entry(byUrl, "ByUrl", 200) + ", " + Entry(gone, "Gone", 50) +
                    " } }");

                var found = UnityProjects.Find(f.Machine, new[] { mine });
                Assert.Equal(new[] { "Mine", "Copied", "ByUrl", "Plain", "Gone" }, found.Select(p => p.Name).ToArray());
                Assert.False(found[0].FromHub);
                Assert.Equal("2022.3.6f1", found[0].EditorVersion);
                Assert.True(found[1].HasBridge);
                Assert.True(found[2].HasBridge);
                Assert.Equal("6000.0.1f1", found[2].EditorVersion);
                Assert.False(found[3].HasBridge);
                Assert.False(found[4].Exists);
            }
        }

        [Fact]
        public void VrChatsSettingsAreDecodedFromItsPlayerPrefs()
        {
            using (var f = new FakeMachine())
            {
                f.SetVrChatPref("PlayerHeight", BitConverter.DoubleToInt64Bits(1.72));
                f.SetVrChatPref("VRC_IK_FBT_SPINE_MODE", 1);
                f.SetVrChatPref("VRC_IK_ONE_HANDED_CALIBRATION", 1);
                var v = VrcSettings.ReadFrom(f.Machine);
                Assert.True(v.Found);
                Assert.Equal(1.72f, v.PlayerHeight, 4);
                Assert.Equal(1, v.SpineMode);
                Assert.Equal(-1, v.MeasurementType);
                Assert.True(v.OneHandedCalibration);
            }
        }
    }

    public class EnvironmentProbeTests
    {
        static EnvironmentReport Probe(FakeMachine f, ProbeOptions options = null)
        {
            return EnvironmentProbe.Run(f.Machine, AppPaths.ForMachine(f.Machine), options);
        }

        [Fact]
        public void AnEmptyMachineReportsWhatIsMissingAndWhatToDo()
        {
            using (var f = new FakeMachine())
            {
                var r = Probe(f);
                Assert.Equal(ProbeState.Missing, r[EnvironmentProbe.SteamVR].State);
                Assert.True(r[EnvironmentProbe.SteamVR].Optional);
                Assert.Equal(ProbeState.Missing, r[EnvironmentProbe.VrChat].State);
                Assert.False(r[EnvironmentProbe.VrChat].Optional);
                Assert.Equal(ProbeState.Unknown, r[EnvironmentProbe.VrChatOsc].State);
                Assert.Equal(ProbeState.Missing, r[EnvironmentProbe.Blender].State);
                Assert.Equal(ProbeState.Missing, r[EnvironmentProbe.Unity].State);
                Assert.Equal(ProbeState.Ok, r[EnvironmentProbe.RecordingsFolder].State); // can be created
                Assert.False(r.CanRecord);
                Assert.Equal(9, r.Items.Count);
                foreach (var item in r.Items)
                {
                    Assert.False(string.IsNullOrWhiteSpace(item.Summary), item.Id);
                    if (item.State == ProbeState.Missing) Assert.False(string.IsNullOrWhiteSpace(item.Action), item.Id);
                }

                Assert.Null(r["no-such-item"]);
                // probing created nothing
                Assert.Empty(Directory.GetFileSystemEntries(f.Root));
            }
        }

        [Fact]
        public void AReadyMachineReportsEverythingOk()
        {
            using (var f = new FakeMachine())
            {
                (_, string lib) = f.AddSteam();
                f.AddSteamApp(lib, SteamLocator.SteamVRAppId, "SteamVR");
                f.Machine.SetRunning("vrserver");
                f.AddVrChat();
                f.SetVrChatPref("VRC_INPUT_OSC", 1);
                f.SetVrChatPref("PlayerHeight", BitConverter.DoubleToInt64Bits(1.8));
                f.File(Path.Combine(f.Machine.Folder(KnownFolder.ProgramFiles), "Blender Foundation", "Blender 5.3"),
                    OperatingSystem.IsWindows() ? "blender.exe" : "blender");

                var r = Probe(f);
                Assert.Equal(ProbeState.Ok, r[EnvironmentProbe.SteamVR].State);
                Assert.True(r.SteamVR.Installed && r.SteamVR.Running);
                Assert.Contains("running", r[EnvironmentProbe.SteamVR].Summary);
                Assert.Equal(ProbeState.Ok, r[EnvironmentProbe.VrChat].State);
                Assert.Equal(ProbeState.Ok, r[EnvironmentProbe.VrChatOsc].State);
                Assert.Equal(ProbeState.Ok, r[EnvironmentProbe.VrChatLog].State);
                Assert.Equal(true, r.VRChat.OscEnabled);
                Assert.Equal(1.8f, r.VRChat.UserHeight, 4);
                Assert.NotNull(r.VRChat.NewestLog);
                Assert.Equal(ProbeState.Ok, r[EnvironmentProbe.Blender].State);
                Assert.Single(r.Blender);
                Assert.True(r.CanRecord);
            }
        }

        [Fact]
        public void OscSwitchedOffIsReportedWithTheWayToTurnItOn()
        {
            using (var f = new FakeMachine())
            {
                f.AddVrChat(null);
                f.SetVrChatPref("VRC_INPUT_OSC", 0);
                var r = Probe(f);
                Assert.Equal(ProbeState.Missing, r[EnvironmentProbe.VrChatOsc].State);
                Assert.Contains("OSC", r[EnvironmentProbe.VrChatOsc].Action);
                Assert.Equal(ProbeState.Missing, r[EnvironmentProbe.VrChatLog].State);
                Assert.False(r.CanRecord);
            }
        }

        [Fact]
        public void AWrongBlenderPathIsFlagged()
        {
            using (var f = new FakeMachine())
            {
                f.File(Path.Combine(f.Machine.Folder(KnownFolder.ProgramFiles), "Blender Foundation", "Blender 5.3"),
                    OperatingSystem.IsWindows() ? "blender.exe" : "blender");
                var r = Probe(f, new ProbeOptions { BlenderPath = Path.Combine(f.Root, "nowhere") });
                Assert.Equal(ProbeState.Attention, r[EnvironmentProbe.Blender].State);
                Assert.NotNull(r[EnvironmentProbe.Blender].Action);
            }
        }

        [Fact]
        public void UnityProjectsWithoutTheBridgeNeedAttention()
        {
            using (var f = new FakeMachine())
            {
                string dir = f.Dir("Projects", "Avatar");
                Directory.CreateDirectory(Path.Combine(dir, "Assets"));
                Directory.CreateDirectory(Path.Combine(dir, "ProjectSettings"));
                var r = Probe(f, new ProbeOptions { UnityProjects = { dir } });
                Assert.Equal(ProbeState.Attention, r[EnvironmentProbe.Unity].State);
                f.File(Path.Combine(dir, "Assets", "VRMotionBridge", "Editor"), UnityProjects.BridgeAssembly, "{}");
                Assert.Equal(ProbeState.Ok,
                    Probe(f, new ProbeOptions { UnityProjects = { dir } })[EnvironmentProbe.Unity].State);
            }
        }
    }

    public class PlatformHelperTests
    {
        [Fact]
        public void EfficiencyModeFollowsTheWindowAndTheRecording()
        {
            Assert.True(EfficiencyMode.ShouldBeOn(false, false, false));
            Assert.True(EfficiencyMode.ShouldBeOn(true, true, false));
            Assert.False(EfficiencyMode.ShouldBeOn(true, false, false));
            Assert.False(EfficiencyMode.ShouldBeOn(false, true, true));
        }

        [Fact]
        public void EfficiencyModeCanBeTurnedOnAndOff()
        {
            if (!OperatingSystem.IsWindows() || !EfficiencyMode.Set(true)) return; // not supported here
            try
            {
                Assert.True(EfficiencyMode.IsOn);
                Assert.Equal(true, EfficiencyMode.ReadBack());
                EfficiencyMode.ExemptCurrentThread();
            }
            finally { EfficiencyMode.Set(false); }

            Assert.False(EfficiencyMode.IsOn);
            Assert.Equal(false, EfficiencyMode.ReadBack());
        }

        [Fact]
        public void TheRecycleBinTakesFilesAndFolders()
        {
            // moves two small temp items into the real Recycle Bin, so it only runs when asked for
            if (Environment.GetEnvironmentVariable("VRMR_TEST_RECYCLE") != "1") return;
            using (var tmp = new TempDir())
            {
                string file = tmp.File("vrmr-test-file.txt");
                File.WriteAllText(file, "x");
                string dir = Path.Combine(tmp.Path, "vrmr-test-folder");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "a.txt"), "x");
                RecycleBin.Delete(file);
                RecycleBin.Delete(dir);
                Assert.False(File.Exists(file));
                Assert.False(Directory.Exists(dir));
                RecycleBin.Delete(file); // already gone: nothing happens
            }
        }
    }
}
