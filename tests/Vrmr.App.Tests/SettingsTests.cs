using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using VRMR.App.Composition;
using VRMR.App.Controls;
using VRMR.App.Platform;
using VRMR.App.Services;
using VRMR.App.ViewModels;
using VRMR.App.Views.Settings;
using Xunit;

namespace VRMR.App.Tests
{
    using Key = Avalonia.Input.Key;

    /// <summary>The Settings page: what each category stores and shows, and the page drawn off screen.</summary>
    [Collection("app")]
    public class SettingsTests
    {
        static (ScratchData data, ManualDispatcher ui, AppHost host, SettingsViewModel settings) Start()
        {
            var data = new ScratchData();
            var ui = new ManualDispatcher();
            var host = data.NewHost(ui);
            return (data, ui, host, new SettingsViewModel(host));
        }

        static void WaitForEnvironment(ManualDispatcher ui, AppHost host)
        {
            Assert.True(ui.WaitFor(() => host.Environment.Report != null && !host.Environment.Checking),
                "the environment check did not finish");
        }

        // ------------------------------------------------------------------------------------------ recording and sources

        [Fact]
        public void SwitchesGoThroughTheSettingsStore()
        {
            var (data, ui, host, settings) = Start();
            using (data)
            using (host)
            {
                var changed = new List<string>();
                host.SettingsStore.Changed += changed.Add;
                var s = host.Settings;

                settings.Recording.FollowHumr = !s.FollowHumrTakes;
                settings.Recording.PreviewWhileRecording = false;
                settings.Recording.SaveCalibrations = false;
                settings.Recording.UseLatestCalibration = false;
                settings.Recording.TriggerParameter = "  Menu/Record ";
                settings.Sources.FingerTracking = false;
                settings.Sources.LegacyPort = !s.ListenLegacyPort;
                settings.Sources.SteamVrOn = false;

                Assert.True(s.FollowHumrTakes);
                Assert.False(s.PreviewWhileRecording);
                Assert.False(s.WatchCalibrations);
                Assert.False(s.AutoLinkCalibration);
                Assert.Equal("Menu/Record", s.RecordTriggerParam);
                Assert.Equal("Menu/Record", settings.Recording.TriggerParameter);
                Assert.False(s.CaptureFingers);
                Assert.False(s.ListenLegacyPort);
                Assert.False(s.EnableOpenVR);
                foreach (string name in new[]
                         {
                             nameof(Settings.FollowHumrTakes), nameof(Settings.PreviewWhileRecording),
                             nameof(Settings.WatchCalibrations), nameof(Settings.AutoLinkCalibration),
                             nameof(Settings.RecordTriggerParam), nameof(Settings.CaptureFingers),
                             nameof(Settings.ListenLegacyPort), nameof(Settings.EnableOpenVR)
                         })
                    Assert.Contains(name, changed);
                // without live sources a switch never starts SteamVR
                Assert.Null(host.Recorder.OpenVR);

                // and the file has them a moment later
                ui.Advance(1);
                Assert.True(
                    ui.WaitFor(() =>
                        File.Exists(host.Options.Paths.SettingsFile) &&
                        !Settings.Load(host.Options.Paths).EnableOpenVR), "the settings were not saved");
            }
        }

        [Fact]
        public void TheHotkeyIsTrimmedCheckedAndCanGoBackToItsDefault()
        {
            var (data, ui, host, settings) = Start();
            using (data)
            using (host)
            {
                var rec = settings.Recording;
                Assert.Equal(host.Settings.HotkeyRecord, rec.Hotkey);
                Assert.False(rec.HasHotkeyProblem);

                rec.Hotkey = "  Ctrl+Shift+R ";
                Assert.Equal("Ctrl+Shift+R", rec.Hotkey);
                Assert.Equal("Ctrl+Shift+R", host.Settings.HotkeyRecord);
                Assert.True(rec.HotkeyChanged);
                Assert.False(rec.HasHotkeyProblem);

                rec.Hotkey = "Banana";
                Assert.True(rec.HasHotkeyProblem);
                Assert.Contains("not a key", rec.HotkeyProblem);
                Assert.Contains("F9", rec.HotkeyProblem); // says what would work

                rec.Hotkey = "";
                Assert.False(rec.HasHotkeyProblem); // no hotkey is a choice
                Assert.Equal("", host.Settings.HotkeyRecord);

                rec.ResetHotkeyCommand.Execute(null);
                Assert.Equal(SettingsRecordingViewModel.DefaultHotkey, host.Settings.HotkeyRecord);
                Assert.False(rec.HotkeyChanged);
            }
        }

        [Theory]
        [InlineData(Key.F9, KeyModifiers.None, "F9")]
        [InlineData(Key.R, KeyModifiers.Control | KeyModifiers.Shift, "Ctrl+Shift+R")]
        [InlineData(Key.D5, KeyModifiers.Alt, "Alt+5")]
        [InlineData(Key.NumPad3, KeyModifiers.None, "NumPad3")]
        [InlineData(Key.Space, KeyModifiers.Control | KeyModifiers.Meta, "Ctrl+Win+Space")]
        public void AKeyPressBecomesHotkeyText(Key key, KeyModifiers modifiers, string text)
        {
            Assert.Equal(text, HotkeyKeys.Text(key, modifiers));
        }

        [Fact]
        public void EveryKeyThatCanBePressedIsOneTheRecorderUnderstands()
        {
            // a modifier alone is not a hotkey yet
            Assert.Null(HotkeyKeys.Text(Key.LeftCtrl, KeyModifiers.Control));
            Assert.Null(HotkeyKeys.Text(Key.LeftShift, KeyModifiers.Shift));
            int keys = 0;
            foreach (Key key in Enum.GetValues(typeof(Key)))
            {
                string text = HotkeyKeys.Text(key, KeyModifiers.Control | KeyModifiers.Alt);
                if (text == null) continue;
                keys++;
                Assert.True(HotkeyText.TryParse(text, out uint modifiers, out uint virtualKey),
                    text + " is not understood");
                Assert.Equal(HotkeyText.Ctrl | HotkeyText.Alt, modifiers);
                Assert.NotEqual(0u, virtualKey);
            }

            Assert.True(keys > 80, "letters, digits, function keys and the number pad should all be there");
        }

        [Fact]
        public void SourcesSayWhatWasFoundAndWhatToDo()
        {
            var (data, ui, host, settings) = Start();
            using (data)
            using (host)
            {
                WaitForEnvironment(ui, host);
                var sources = settings.Sources;
                Assert.True(sources.CaptureOff);
                // an empty sandbox: SteamVR is on in the settings and not installed
                Assert.True(sources.SteamVr.NeedsAttention);
                Assert.Contains("not found", sources.SteamVr.Summary);
                Assert.True(sources.SteamVr.HasAction);
                // VRChat has never run here
                Assert.True(sources.VrChat.NeedsAttention);
                Assert.True(sources.VrChat.HasAction);
                Assert.False(sources.ShowFingerState);

                sources.SteamVrOn = false;
                Assert.False(sources.SteamVr.NeedsAttention);
                Assert.Contains("Switched off", sources.SteamVr.Summary);

                // once VRChat has run, its row stops asking for it
                Directory.CreateDirectory(new VrcFolders(host.Options.Paths.Machine).DataDir);
                var again = host.Environment.RefreshAsync();
                Assert.True(ui.WaitFor(() => again.IsCompleted));
                Assert.DoesNotContain("has not been run", sources.VrChat.Summary);
            }
        }

        // ------------------------------------------------------------------------------------------ folders

        [Fact]
        public void PortableModeKeepsTheFoldersWhereTheyAre()
        {
            var (data, ui, host, settings) = Start();
            using (data)
            using (host)
            {
                var folders = settings.Folders;
                Assert.True(folders.IsPortable);
                Assert.False(folders.CanMoveFolders);
                Assert.False(folders.RecordingsMoved);
                string takes = host.Settings.RecordingsDir, avatars = host.Settings.AvatarLibraryDir;

                folders.RecordingsFolder = Path.Combine(data.Root, "elsewhere");
                folders.AvatarFolder = Path.Combine(data.Root, "elsewhere");
                Assert.Equal(takes, host.Settings.RecordingsDir);
                Assert.Equal(avatars, host.Settings.AvatarLibraryDir);
                Assert.Equal(takes, folders.RecordingsFolder); // the field shows the folder in use again
                Assert.Equal(avatars, folders.AvatarFolder);
                Assert.False(Directory.Exists(Path.Combine(data.Root, "elsewhere")));
                Assert.Equal(StatusKind.Error, host.Status.Kind);
                Assert.Contains("portable", host.Status.Message, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void AFolderIsCheckedBeforeItIsUsed()
        {
            using var data = new ScratchData();
            Assert.Contains("Browse", SettingsFoldersViewModel.CheckFolder("  ", out _));
            Assert.Contains("whole path", SettingsFoldersViewModel.CheckFolder("takes\\new", out _));

            string wanted = Path.Combine(data.Root, "New takes") + Path.DirectorySeparatorChar;
            Assert.Null(SettingsFoldersViewModel.CheckFolder(" \"" + wanted + "\" ", out string dir));
            Assert.Equal(Path.Combine(data.Root, "New takes"), dir);
            Assert.True(Directory.Exists(dir)); // made, so the library can watch it

            // a file is in the way
            string file = Path.Combine(data.Root, "a file");
            File.WriteAllText(file, "");
            Assert.Contains("Pick another folder", SettingsFoldersViewModel.CheckFolder(file, out _));
        }

        [Fact]
        public void TheBlenderPathIsStoredAndCheckedAgain()
        {
            var (data, ui, host, settings) = Start();
            using (data)
            using (host)
            {
                WaitForEnvironment(ui, host);
                var folders = settings.Folders;
                Assert.Contains("not found", folders.Blender.Summary);
                Assert.False(folders.Blender.NeedsAttention); // Blender is optional
                Assert.Equal("", folders.BlenderInUse);

                string wrong = Path.Combine(data.Root, "tools", "blender.exe");
                folders.BlenderPath = " \"" + wrong + "\" ";
                Assert.Equal(wrong, host.Settings.BlenderPath);
                Assert.Equal(wrong, folders.BlenderPath);
                Assert.True(folders.HasBlenderPath);
                WaitForEnvironment(ui, host);
                Assert.Contains("does not point to Blender", folders.Blender.Summary);

                folders.ClearBlenderCommand.Execute(null);
                Assert.Equal("", host.Settings.BlenderPath);
                Assert.False(folders.HasBlenderPath);
            }
        }

        [Fact]
        public async Task UnityProjectsAreAddedThroughTheFolderPicker()
        {
            using var data = new ScratchData();
            await HeadlessUi.RunAsync(async () =>
            {
                using var host = data.NewHost(new AvaloniaUiDispatcher());
                var folders = new SettingsViewModel(host).Folders;
                Assert.False(folders.HasProjects);

                string project = Path.Combine(data.Root, "UnityProject");
                Directory.CreateDirectory(project);
                folders.PickFolder = (title, start) => Task.FromResult(project);
                folders.AddProjectCommand.Execute(null);
                Assert.False(folders.HasProjects);
                Assert.Contains("not a Unity project", host.Status.Message);

                Directory.CreateDirectory(Path.Combine(project, "Assets"));
                Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
                folders.AddProjectCommand.Execute(null);
                Assert.True(folders.HasProjects);
                Assert.Equal(project, Assert.Single(folders.Projects).Path);
                Assert.Same(folders.Projects[0], folders.SelectedProject);
                Assert.True(
                    await HeadlessUi.WaitForAsync(() => !host.AvatarService.Scanning && folders.ScanInfo.Contains("1 project")),
                    "the scan did not report: " + folders.ScanInfo);
                Assert.False(folders.Projects[0].Missing);

                folders.RemoveProjectCommand.Execute(null);
                Assert.False(folders.HasProjects);
                Assert.Empty(host.Settings.UnityProjects);
            });
        }

        // ------------------------------------------------------------------------------------------ avatars

        [Fact]
        public void AnEmptyLibrarySaysWhatToAdd()
        {
            var (data, ui, host, settings) = Start();
            using (data)
            using (host)
            {
                Assert.True(settings.Avatars.IsEmpty);
                Assert.True(settings.Avatars.AddCommand.CanExecute(null));
                TestAvatars.Save(host, "First of my own");
                Assert.False(settings.Avatars.IsEmpty);
                Assert.Equal(2, settings.Avatars.Items.Count);
            }
        }

        [Fact]
        public void TheAvatarListShowsTheDefaultTheBuiltInOneAndMissingModels()
        {
            var (data, ui, host, settings) = Start();
            using (data)
            using (host)
            {
                TestAvatars.Save(host, "Avatar A");
                TestAvatars.Save(host, "Avatar B");
                var avatars = settings.Avatars;
                Assert.Equal(new[] { "Avatar A", "Avatar B", Mannequin.Name },
                    avatars.Items.Select(i => i.Name).ToArray());
                avatars.Selected = avatars.Items[0];
                Assert.Equal("Avatar A", avatars.Selected.Name);
                Assert.True(avatars.CanEdit);
                Assert.True(avatars.Items[0].FbxMissing);
                Assert.True(avatars.Items[2].IsBuiltIn);
                Assert.False(avatars.Items[2].FbxMissing);
                Assert.Contains("eye height", avatars.Items[0].Summary);

                avatars.Selected = avatars.Items[1];
                Assert.True(avatars.CanMakeDefault);
                avatars.MakeDefaultCommand.Execute(null);
                Assert.Equal("Avatar B", avatars.Selected.Name); // the selection stays through the rebuild
                Assert.True(avatars.Selected.IsDefault);
                Assert.False(avatars.CanMakeDefault);
                Assert.Single(avatars.Items, i => i.IsDefault);

                // the mannequin can be the default, and nothing else about it can change
                avatars.Selected = avatars.Items[2];
                Assert.False(avatars.CanEdit);
                Assert.True(avatars.CanMakeDefault);
            }
        }

        [Fact]
        public void NameAndVrChatNamesOfTheSelectedAvatarAreEdited()
        {
            var (data, ui, host, settings) = Start();
            using (data)
            using (host)
            {
                TestAvatars.Save(host, "Working title");
                var avatars = settings.Avatars;
                avatars.Selected = avatars.Items[0];
                Assert.Equal("Working title", avatars.Name);
                Assert.Equal("", avatars.VrChatNames);

                avatars.VrChatNames = "red panda ,  panda";
                Assert.Equal("red panda, panda", avatars.VrChatNames); // shown as stored
                Assert.Equal(new[] { "red panda", "panda" }, avatars.Selected.Entry.Match.ToArray());

                avatars.Name = "Final name";
                Assert.Equal("Final name", avatars.Selected.Name);
                Assert.Equal("Final name", avatars.Name);
                Assert.Equal("red panda, panda", avatars.VrChatNames);
                Assert.Contains(host.AvatarService.Entries, e => e.Name == "Final name" && e.Id == "Working_title");

                avatars.Name = "   ";
                Assert.Equal("Final name", avatars.Name); // an empty name is not taken
                Assert.Contains("humanoid bones", avatars.Details);
            }
        }

        [Fact]
        public void RemovingAsksFirst()
        {
            var (data, ui, host, settings) = Start();
            using (data)
            using (host)
            {
                var entry = TestAvatars.Save(host, "To remove");
                var avatars = settings.Avatars;
                avatars.Selected = avatars.Items[0];
                string asked = null;
                bool answer = false;
                avatars.ConfirmRemove = name =>
                {
                    asked = name;
                    return Task.FromResult(answer);
                };

                avatars.RemoveCommand.Execute(null);
                Assert.Equal("To remove", asked);
                Assert.Equal(2, avatars.Items.Count); // "Cancel" keeps it

                answer = true;
                File.Delete(entry.FilePath); // so the test leaves nothing in the Recycle Bin
                avatars.RemoveCommand.Execute(null);
                Assert.True(avatars.IsEmpty);
                Assert.True(Assert.Single(avatars.Items).IsBuiltIn);
            }
        }

        [Fact]
        public void AddingShowsItsProgressAndSelectsTheNewAvatar()
        {
            var (data, ui, host, settings) = Start();
            using (data)
            using (host)
            {
                var avatars = settings.Avatars;
                string fbx = TestAvatars.WriteUnityModel(Path.Combine(data.Root, "source"), "Fresh avatar");
                avatars.PickFiles = () => Task.FromResult<IReadOnlyList<string>>(new[] { fbx });
                avatars.AddCommand.Execute(null);
                Assert.True(avatars.IsImporting);
                Assert.False(avatars.AddCommand.CanExecute(null));
                Assert.NotEqual("", avatars.ImportMessage);
                Assert.True(ui.WaitFor(() => !avatars.IsImporting && avatars.Selected?.Name == "Fresh avatar"),
                    "the import did not finish");
                Assert.False(avatars.IsEmpty);
                Assert.True(avatars.AddCommand.CanExecute(null));
                Assert.True(avatars.HasImportReport);
                Assert.Contains("Added Fresh avatar", avatars.ImportReport);
                Assert.False(avatars.Selected.FbxMissing);

                // a cancelled picker adds nothing
                avatars.PickFiles = () => Task.FromResult<IReadOnlyList<string>>(null);
                avatars.AddCommand.Execute(null);
                Assert.False(avatars.IsImporting);
            }
        }

        [Fact]
        public void LinkingAndTheFaceFbxSayWhatIsMissing()
        {
            var (data, ui, host, settings) = Start();
            using (data)
            using (host)
            {
                var avatars = settings.Avatars;
                bool asked = false;
                avatars.PickUnityModel = () =>
                {
                    asked = true;
                    return Task.FromResult<string>(null);
                };
                avatars.PickSaveFbx = (name, start) =>
                {
                    asked = true;
                    return Task.FromResult<string>(null);
                };

                avatars.LinkWornCommand.Execute(null);
                Assert.False(asked); // no picker before VRChat has said which avatar is worn
                Assert.Equal(StatusKind.Warning, host.Status.Kind);
                Assert.Contains("OSC", host.Status.Message);

                avatars.ExportFaceFbxCommand.Execute(null);
                Assert.False(asked);
                Assert.Contains("no avatar on screen", host.Status.Message);
            }
        }

        // ------------------------------------------------------------------------------------------ view, about, the page itself

        [Fact]
        public void TheThemeIsRememberedAndApplied()
        {
            var (data, ui, host, settings) = Start();
            using (data)
            using (host)
            {
                var applied = new List<string>();
                settings.View.ApplyTheme = applied.Add;
                Assert.Equal(0, settings.View.ThemeIndex); // dark until chosen otherwise
                settings.View.ThemeIndex = 2;
                settings.View.ThemeIndex = 1;
                Assert.Equal(new[] { "system", "light" }, applied.ToArray());
                Assert.Equal("light", host.UiState.GetString("theme", ""));
                Assert.Equal(1, new SettingsViewModel(host).View.ThemeIndex);
            }
        }

        [Fact]
        public void TheMeshListFollowsTheAvatarOnScreen()
        {
            var (data, ui, host, settings) = Start();
            using (data)
            using (host)
            {
                var view = settings.View;
                settings.Show(SettingsCategory.View);
                Assert.Equal("", view.MeshInfo); // nothing is read while the page is closed
                settings.SetActive(true);
                Assert.Contains("Open a take", view.MeshInfo);
                Assert.False(view.HasMeshes);

                host.Session.Show(SyntheticTake.Create());
                Assert.True(ui.WaitFor(() =>
                {
                    if (host.Session.Solve == SolveState.Waiting)
                        ui.Advance(TakeSession.SolveDelay.TotalSeconds + 0.05);
                    return host.Session.Solve == SolveState.Done && view.MeshInfo.Contains("mannequin");
                }, 30000), "the mesh note did not follow the take: " + view.MeshInfo);
                Assert.False(view.AnyHidden);
            }
        }

        [Fact]
        public async Task HiddenMeshesOfARealAvatarFollowTheTicks()
        {
            Assert.SkipUnless(PrivateData.Available, PrivateData.SkipReason);
            await HeadlessUi.RunAsync(async () =>
            {
                using var host = PrivateData.NewHost(new AvaloniaUiDispatcher());
                var settings = new SettingsViewModel(host);
                var view = settings.View;
                await OpenATake(host);
                settings.Show(SettingsCategory.View);
                settings.SetActive(true);
                Assert.True(await HeadlessUi.WaitForAsync(() => view.HasMeshes, 120000),
                    "no meshes were listed: " + view.MeshInfo);
                var before = host.Settings.PreviewHiddenMeshes.ToList();
                try
                {
                    var row = view.Meshes.First(m => m.Shown);
                    row.Shown = false;
                    Assert.Contains(row.Name, host.Settings.PreviewHiddenMeshes);
                    Assert.True(view.AnyHidden);
                    Assert.Contains($"of {view.Meshes.Count} meshes shown", view.MeshInfo);
                    view.ShowAllMeshesCommand.Execute(null);
                    Assert.DoesNotContain(row.Name, host.Settings.PreviewHiddenMeshes);
                    Assert.All(view.Meshes, m => Assert.True(m.Shown));
                }
                finally
                {
                    host.SettingsStore.Update(nameof(Settings.PreviewHiddenMeshes),
                        s => s.PreviewHiddenMeshes = before);
                }
            });
        }

        [Fact]
        public void TheCategoryIsRememberedAndAlwaysAValidOne()
        {
            var (data, ui, host, settings) = Start();
            using (data)
            using (host)
            {
                Assert.Equal(SettingsCategory.Sources, settings.Category);
                Assert.Equal(7, settings.Categories.Count);
                settings.Show(SettingsCategory.Avatars);
                Assert.Equal("Avatars", settings.CategoryTitle);
                Assert.NotEqual("", settings.CategoryIntro);
                Assert.Equal(SettingsCategory.Avatars, new SettingsViewModel(host).Category);

                settings.CategoryIndex = -1; // a list losing its selection for a moment
                Assert.Equal(SettingsCategory.Sources, settings.Category);
                settings.CategoryIndex = 99;
                Assert.Equal(SettingsCategory.Sources, settings.Category);
            }
        }

        [Fact]
        public void AboutNamesTheVersionAndTheFoldersAndHandsCloseAndTheGuideToTheShell()
        {
            var (data, ui, host, settings) = Start();
            using (data)
            using (host)
            {
                Assert.Matches(@"^\d+\.\d+", settings.Version);
                Assert.True(settings.IsPortable);
                Assert.Equal(host.Options.Paths.AppDataDir, settings.AppDataFolder);
                Assert.Equal(host.Settings.RecordingsDir, settings.RecordingsFolder);

                int closed = 0, guide = 0;
                settings.CloseRequested += () => closed++;
                settings.SetupGuideRequested += () => guide++;
                settings.CloseCommand.Execute(null);
                settings.RunSetupGuideCommand.Execute(null);
                Assert.Equal(1, closed);
                Assert.Equal(1, guide);

                // no log has been written into a fresh data folder: the status bar says so instead of opening nothing
                if (!File.Exists(host.Options.Paths.LogFile))
                {
                    settings.OpenLogCommand.Execute(null);
                    Assert.Contains("no log yet", host.Status.Message);
                }
            }
        }

        [Fact]
        public void LiveStatesAreOnlyReadWhileThePageIsOpen()
        {
            var (data, ui, host, settings) = Start();
            using (data)
            using (host)
            {
                // without live sources there is nothing to poll, open or not
                settings.SetActive(true);
                settings.Show(SettingsCategory.Avatars);
                Assert.Contains("Live sources are off", settings.Avatars.WornAvatar);
                settings.SetActive(false);
            }
        }

        // ------------------------------------------------------------------------------------------ drawn off screen

        /// <summary>On the UI thread: opens the smallest of the private takes and waits for its solve.</summary>
        static async Task OpenATake(AppHost host)
        {
            var opening = host.Session.OpenAsync(PrivateData.Takes()[0]);
            Assert.True(await HeadlessUi.WaitForAsync(() => opening.IsCompleted, 60000), "the take did not open");
            Assert.True(
                await HeadlessUi.WaitForAsync(
                    () => host.Session.Solve == SolveState.Done || host.Session.Solve == SolveState.Failed, 120000),
                "the take did not solve");
            Assert.True(host.Session.Solve == SolveState.Done, "the solve failed: " + host.Session.SolveError);
        }

        static async Task<Window> Open(SettingsViewModel settings, AppHost host, double scale = 1)
        {
            Section.StateStore = host.UiState;
            var window = new Window
                { Width = 1400, Height = 860, Content = new SettingsPage { DataContext = settings } };
            window.Show();
            if (scale != 1) window.SetRenderScaling(scale);
            await HeadlessUi.SettleAsync(150);
            return window;
        }

        [Theory]
        [InlineData(1.0)]
        [InlineData(1.5)]
        public async Task EveryCategoryDraws(double scale)
        {
            using var data = new ScratchData();
            await HeadlessUi.RunAsync(async () =>
            {
                using var host = data.NewHost(new AvaloniaUiDispatcher());
                TestAvatars.Save(host, "Test avatar one", 1.45f);
                TestAvatars.Save(host, "Test avatar two");
                host.AvatarService.SetDefault(host.AvatarService.Entries[1]);
                var settings = new SettingsViewModel(host);
                settings.Show(SettingsCategory.Sources);
                var window = await Open(settings, host, scale);
                var page = (SettingsPage)window.Content;
                Assert.True(await HeadlessUi.WaitForAsync(() => host.Environment.Report != null));
                string suffix = scale == 1 ? "" : "-150";
                foreach (SettingsCategory category in Enum.GetValues(typeof(SettingsCategory)))
                {
                    settings.Show(category);
                    await HeadlessUi.SettleAsync(80);
                    Assert.Equal((int)category, page.FindControl<ListBox>("CategoryList").SelectedIndex);
                    Assert.NotNull(HeadlessUi.Capture(window,
                        $"settings-{(int)category + 1}-{category.ToString().ToLowerInvariant()}{suffix}"));
                }

                Assert.NotNull(page.FindControl<ContentControl>("CalibrationDefaultsHost"));
                window.Close();
            });
        }

        [Fact]
        public async Task TheEmptyAvatarLibraryAndTheFoldedPartsDraw()
        {
            using var data = new ScratchData();
            await HeadlessUi.RunAsync(async () =>
            {
                using var host = data.NewHost(new AvaloniaUiDispatcher());
                var settings = new SettingsViewModel(host);
                settings.Show(SettingsCategory.Avatars);
                var window = await Open(settings, host);
                Assert.True(settings.Avatars.IsEmpty);
                Assert.NotNull(HeadlessUi.Capture(window, "settings-avatars-empty"));

                // with avatars, an import report and every folded part open
                foreach (string key in new[]
                             { "settings.avatars.details", "settings.avatars.import", "settings.avatars.unity" })
                    host.UiState.Set("fold." + key, true);
                window.Close();
                window = await Open(settings, host);
                // the import of a made-up model is over in a blink: its progress is drawn from the state the service would report
                settings.Avatars.ImportMessage = "Reading Imported avatar.unitypackage…";
                settings.Avatars.IsImporting = true;
                await HeadlessUi.SettleAsync(80);
                Assert.NotNull(HeadlessUi.Capture(window, "settings-avatars-importing"));
                string fbx = TestAvatars.WriteUnityModel(Path.Combine(data.Root, "source"), "Imported avatar");
                var adding = host.AvatarService.AddAsync(new[] { fbx });
                Assert.True(await HeadlessUi.WaitForAsync(() => adding.IsCompleted));
                await HeadlessUi.SettleAsync(80);
                Assert.False(settings.Avatars.IsImporting);
                Assert.False(settings.Avatars.IsEmpty);
                Assert.NotNull(HeadlessUi.Capture(window, "settings-avatars-unfolded"));
                window.Close();
            });
        }

        [Fact]
        public async Task TheLightThemeDraws()
        {
            using var data = new ScratchData();
            await HeadlessUi.RunAsync(async () =>
            {
                using var host = data.NewHost(new AvaloniaUiDispatcher());
                TestAvatars.Save(host, "Test avatar");
                var settings = new SettingsViewModel(host);
                settings.Show(SettingsCategory.View);
                var window = await Open(settings, host);
                try
                {
                    settings.View.ThemeIndex = 1; // through the view, as a click on the switch would
                    await HeadlessUi.SettleAsync(80);
                    Assert.Equal(ThemeVariant.Light, Application.Current.ActualThemeVariant);
                    Assert.NotNull(HeadlessUi.Capture(window, "settings-view-light"));
                    settings.Show(SettingsCategory.Recording);
                    await HeadlessUi.SettleAsync(80);
                    Assert.NotNull(HeadlessUi.Capture(window, "settings-recording-light"));
                }
                finally
                {
                    // the other UI tests expect the dark theme
                    App.ApplyTheme(Application.Current, "dark");
                    window.Close();
                }
            });
        }

        [Fact]
        public async Task RealAvatarsAndTheirMeshesDraw()
        {
            Assert.SkipUnless(PrivateData.Available, PrivateData.SkipReason);
            await HeadlessUi.RunAsync(async () =>
            {
                using var host = PrivateData.NewHost(new AvaloniaUiDispatcher());
                var settings = new SettingsViewModel(host);
                settings.Show(SettingsCategory.Avatars);
                var window = await Open(settings, host);
                Assert.False(settings.Avatars.IsEmpty);
                Assert.NotNull(HeadlessUi.Capture(window, "settings-avatars-real"));

                await OpenATake(host);
                settings.Show(SettingsCategory.View);
                Assert.True(await HeadlessUi.WaitForAsync(() => settings.View.HasMeshes, 120000),
                    "no meshes were listed: " + settings.View.MeshInfo);
                await HeadlessUi.SettleAsync(80);
                Assert.NotNull(HeadlessUi.Capture(window, "settings-view-real"));
                window.Close();
            });
        }
    }
}
