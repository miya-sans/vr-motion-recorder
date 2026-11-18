using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using VRMR.App.Composition;
using VRMR.App.Controls;
using VRMR.App.Platform;
using VRMR.App.Services;
using VRMR.App.ViewModels;
using VRMR.App.Views.Takes;
using Xunit;
using VisualExtensions = Avalonia.VisualTree.VisualExtensions;

namespace VRMR.App.Tests
{
    /// <summary>
    ///     The Export tab: the catalogue of formats and where each keeps its state, the folder rules, what an export
    ///     refuses, the tab itself, and (with copies of real takes) exports that write files.
    /// </summary>
    [Collection("app")]
    public class ExportTests
    {
        /// <summary>A take with nothing in it: enough to have a take open.</summary>
        static Recording EmptyTake()
        {
            return new Recording { Name = "Empty take", Duration = 1 };
        }

        static IEnumerable<ExportChoice> Choices(IEnumerable<ExportFormat> formats)
        {
            return formats.SelectMany(f => new ExportChoice[] { f }.Concat(f.Options));
        }

        // ------------------------------------------------------------------------------------------ catalogue

        [Fact]
        public void EveryCatalogueEntryIsCompleteAndRemembersItsState()
        {
            using var rig = new Rig();
            var formats = ExportCatalog.Takes;
            var all = Choices(formats).ToList();
            Assert.Equal(all.Count, all.Select(c => c.Id).Distinct().Count());
            Assert.Contains(formats, f => f.Group == ExportGroup.Main);
            Assert.Contains(formats, f => f.Group == ExportGroup.More);
            foreach (var choice in all)
            {
                Assert.False(string.IsNullOrWhiteSpace(choice.Id));
                Assert.False(string.IsNullOrWhiteSpace(choice.Title), choice.Id);
                Assert.False(string.IsNullOrWhiteSpace(choice.Description), choice.Id);
                Assert.NotNull(choice.State);
                Assert.NotNull(choice.Apply);
                rig.Export.SetOn(choice, true);
                Assert.True(rig.Export.IsOn(choice), choice.Id);
                rig.Export.SetOn(choice, false);
                Assert.False(rig.Export.IsOn(choice), choice.Id);
            }

            foreach (var format in formats)
            foreach (var option in format.Options)
            {
                Assert.Same(format, option.Format);
                Assert.All(option.DependsOn, id => Assert.Contains(formats, f => f.Id == id));
            }
        }

        [Fact]
        public void FormatsReadAndWriteTheirSettings()
        {
            using var rig = new Rig();
            var fields = new Dictionary<string, (Func<Settings, bool> Get, Action<Settings, bool> Set)>
            {
                ["fbx"] = (s => s.ExportFbx, (s, v) => s.ExportFbx = v),
                ["fbx.face"] = (s => s.FbxIncludeFaceMesh, (s, v) => s.FbxIncludeFaceMesh = v),
                ["fbxArmature"] = (s => s.ExportFbxArmature, (s, v) => s.ExportFbxArmature = v),
                ["fbx.camera"] = (s => s.FbxIncludeCamera, (s, v) => s.FbxIncludeCamera = v),
                ["fbx.trackers"] = (s => s.FbxIncludeTrackers, (s, v) => s.FbxIncludeTrackers = v),
                ["anim"] = (s => s.ExportAnim && s.ExportFaceAnim, (s, v) => s.ExportAnim = s.ExportFaceAnim = v),
                ["paramsAnim"] = (s => s.ExportParamsAnim, (s, v) => s.ExportParamsAnim = v),
                ["oscJson"] = (s => s.ExportOscJson, (s, v) => s.ExportOscJson = v),
                ["humrLog"] = (s => s.ExportHumrLog, (s, v) => s.ExportHumrLog = v)
            };
            var all = Choices(rig.Export.Formats).ToDictionary(c => c.Id);
            foreach (var kv in fields)
            {
                var choice = all[kv.Key];
                foreach (bool on in new[] { true, false })
                {
                    kv.Value.Set(rig.Settings, !on);
                    Assert.Equal(!on, rig.Export.IsOn(choice));
                    rig.Export.SetOn(choice, on);
                    Assert.Equal(on, kv.Value.Get(rig.Settings));
                }
            }

            // every entry of the catalogue is covered here, or is the one without a setting
            Assert.Equal(new[] { "faceJson" }, all.Keys.Except(fields.Keys));

            // the Unity .anim is one choice over two settings: either one shows it as on
            rig.Settings.ExportAnim = false;
            rig.Settings.ExportFaceAnim = true;
            Assert.True(rig.Export.IsOn(all["anim"]));
        }

        [Fact]
        public void TheFaceJsonChoiceLivesInTheWindowState()
        {
            using var rig = new Rig();
            var faceJson = rig.Format("faceJson");
            Assert.False(rig.Export.IsOn(faceJson));
            int changes = 0;
            rig.Export.Changed += () => changes++;
            rig.Export.SetOn(faceJson, true);
            Assert.True(rig.Host.UiState.GetBool("export.faceJson", false));
            Assert.True(rig.Export.IsOn(faceJson));
            Assert.Equal(1, changes);
        }

        [Fact]
        public void OptionsNeedOneOfTheirFormats()
        {
            using var rig = new Rig();
            rig.AllOff();
            var face = rig.Option("fbx.face");
            var camera = rig.Option("fbx.camera");
            var trackers = rig.Option("fbx.trackers");
            Assert.True(face.IsNested);
            Assert.False(camera.IsNested);
            Assert.False(rig.Export.IsAvailable(face));
            Assert.False(rig.Export.IsAvailable(camera));

            rig.Export.SetOn(rig.Format("fbxArmature"), true);
            Assert.False(rig.Export.IsAvailable(face));
            Assert.True(rig.Export.IsAvailable(camera));
            Assert.True(rig.Export.IsAvailable(trackers));

            rig.Export.SetOn(rig.Format("fbxArmature"), false);
            rig.Export.SetOn(rig.Format("fbx"), true);
            Assert.True(rig.Export.IsAvailable(face));
            Assert.True(rig.Export.IsAvailable(camera));
        }

        [Fact]
        public void TheJobHoldsWhatIsChosen()
        {
            using var rig = new Rig();
            rig.AllOff();
            var job = rig.Export.BuildJob();
            Assert.Empty(job.Formats);
            Assert.Empty(job.Writers);
            Assert.False(job.Clip.Fbx || job.Clip.FbxArmature || job.Clip.BodyAnim || job.Clip.FaceAnim ||
                         job.Clip.ParamsAnim || job.Clip.FaceJson);

            // an option without its format does nothing
            rig.Export.SetOn(rig.Option("fbx.face"), true);
            rig.Export.SetOn(rig.Option("fbx.camera"), true);
            job = rig.Export.BuildJob();
            Assert.False(job.Clip.FbxFace);
            Assert.False(job.Clip.FbxCamera);

            rig.Export.SetOn(rig.Format("fbxArmature"), true);
            job = rig.Export.BuildJob();
            Assert.Equal(new[] { "fbxArmature" }, job.Formats);
            Assert.True(job.Clip.FbxArmature);
            Assert.True(job.Clip.FbxCamera);
            Assert.False(job.Clip.Fbx);
            Assert.False(job.Clip.FbxFace);
            Assert.False(job.Clip.FbxTrackers);
            Assert.True(job.AnyFbx);

            rig.Export.SetOn(rig.Format("fbx"), true);
            rig.Export.SetOn(rig.Format("anim"), true);
            rig.Export.SetOn(rig.Format("paramsAnim"), true);
            rig.Export.SetOn(rig.Format("faceJson"), true);
            rig.Export.SetOn(rig.Format("oscJson"), true);
            rig.Export.SetOn(rig.Format("humrLog"), true);
            job = rig.Export.BuildJob();
            Assert.Equal(rig.Export.Formats.Count, job.Formats.Count);
            Assert.True(job.Clip.Fbx && job.Clip.FbxFace && job.Clip.BodyAnim && job.Clip.FaceAnim &&
                        job.Clip.ParamsAnim && job.Clip.FaceJson);
            Assert.Equal(2, job.Writers.Count);
        }

        [Fact]
        public void ANewFormatIsOneCatalogueEntry()
        {
            using var rig = new Rig();
            var catalogue = new[]
            {
                new ExportFormat
                {
                    Id = "notes", Title = "Notes", Description = "A text file with the take's notes.",
                    Group = ExportGroup.Main,
                    State = ExportState.InUiState("export.test.notes"),
                    Apply = job => job.Writers.Add(part => part.PathFor("_notes.txt"))
                }
            };
            var export = new ExportService(rig.Host.SettingsStore, rig.Host.UiState, rig.Host.Session, rig.Host.Avatars,
                rig.Host.Status, rig.Ui, catalogue);
            Assert.Equal(0, export.CountOn());
            Assert.Empty(export.BuildJob().Formats);
            export.SetOn(catalogue[0], true);
            Assert.Equal(1, export.CountOn(ExportGroup.Main));
            var built = export.BuildJob();
            Assert.Equal(new[] { "notes" }, built.Formats);
            var part = new ExportPart { Dir = rig.Root, Name = "take" };
            Assert.Equal(Path.Combine(rig.Root, "take_notes.txt"), built.Writers.Single()(part));
        }

        // ------------------------------------------------------------------------------------------ folders

        [Fact]
        public void TheExportFolderFallsBackFromChosenToUnityToRecordings()
        {
            using var rig = new Rig();
            var folders = rig.Export.Folders;
            Assert.Equal(Path.Combine(rig.Settings.RecordingsDir, "Exports"), folders.ExportDir);
            Assert.Null(folders.UnityExportDir);
            Assert.False(folders.IsInUnityAssets(folders.ExportDir));
            Assert.Equal("Exports", folders.Summary);
            Assert.Null(folders.FolderToOpen);

            string project = rig.LinkUnityProject();
            string inUnity = Path.Combine(project, "Assets", "VRMotionRecorder");
            Assert.Equal(inUnity, folders.UnityExportDir);
            Assert.Equal(inUnity, folders.ExportDir);
            Assert.True(folders.IsInUnityAssets(folders.ExportDir));
            Assert.False(folders.IsInUnityAssets(project));
            Assert.Equal("Unity project", folders.Summary);

            string chosen = Path.Combine(rig.Root, "Chosen");
            Assert.True(folders.SetExportDir(chosen, out string error));
            Assert.Null(error);
            Assert.Equal(chosen, folders.ExportDir);
            Assert.Equal(chosen, rig.Settings.LastExportDir);
            Assert.Equal("Chosen", folders.Summary);

            Directory.CreateDirectory(chosen);
            Assert.Equal(chosen, folders.FolderToOpen);
        }

        [Fact]
        public void OnlyAFullPathBecomesTheExportFolder()
        {
            using var rig = new Rig();
            var folders = rig.Export.Folders;
            string before = folders.ExportDir;

            Assert.False(folders.SetExportDir("Exports", out string error));
            Assert.Contains("full path", error);
            Assert.Equal(before, folders.ExportDir);
            Assert.Equal("", rig.Settings.LastExportDir);

            // nothing typed keeps the folder
            Assert.True(folders.SetExportDir("   ", out error));
            Assert.Null(error);
            Assert.Equal(before, folders.ExportDir);

            // a file is not a folder
            string file = Path.Combine(rig.Root, "file.txt");
            File.WriteAllText(file, "");
            Assert.False(folders.SetExportDir(file, out error));
            Assert.Contains("is a file", error);

            // quotes (a path copied from the file manager) and a trailing separator are fine
            string chosen = Path.Combine(rig.Root, "My exports");
            Assert.True(folders.SetExportDir("  \"" + chosen + Path.DirectorySeparatorChar + "\" ", out error));
            Assert.Equal(Path.GetFullPath(chosen + Path.DirectorySeparatorChar), folders.ExportDir);
        }

        [Fact]
        public void ATakeGetsItsOwnFolderUnlessSwitchedOff()
        {
            using var rig = new Rig();
            var folders = rig.Export.Folders;
            Assert.True(folders.TakeFolders);
            Assert.Equal(Path.Combine(folders.ExportDir, "Take 1"), folders.TakeDir("Take 1"));
            Assert.Equal(Path.Combine(folders.ExportDir, "a_b_c"), folders.TakeDir("a/b:c"));

            folders.TakeFolders = false;
            Assert.False(rig.Settings.ExportTakeFolders);
            Assert.Equal(folders.ExportDir, folders.TakeDir("Take 1"));

            Assert.Equal("clip", ExportFolders.SafeName("  "));
            Assert.Equal("clip", ExportFolders.SafeName(null));
            Assert.Equal("a_b", ExportFolders.SafeName(" a?b "));
            Assert.Equal(Path.Combine(folders.ExportDir, "textures", "avatar"), folders.TextureDir(null));
            Assert.Equal(Path.Combine(folders.ExportDir, "textures", "My_Avatar"), folders.TextureDir("My*Avatar"));
        }

        // ------------------------------------------------------------------------------------------ options

        [Fact]
        public void OptionsAreKeptWhereTheyBelong()
        {
            using var rig = new Rig();
            var changed = new List<string>();
            rig.Host.SettingsStore.Changed += changed.Add;

            Assert.Contains(rig.Export.FrameRate, ExportService.FrameRates);
            rig.Export.FrameRate = 30;
            rig.Export.FrameRate = 0; // not a frame rate: ignored
            Assert.Equal(30, rig.Settings.ExportFps);
            rig.Export.FaceNaming = "ARKit";
            Assert.Equal("ARKit", rig.Settings.FaceNaming);
            rig.Export.FaceMeshPath = "  Body/Face ";
            Assert.Equal("Body/Face", rig.Settings.FaceMeshPath);
            // each change is announced under its field's name: the take session solves again on the first two
            Assert.Equal(
                new[] { nameof(Settings.ExportFps), nameof(Settings.FaceNaming), nameof(Settings.FaceMeshPath) },
                changed);

            Assert.False(rig.Export.SplitAtAvatarChanges);
            rig.Export.SplitAtAvatarChanges = true;
            Assert.True(rig.Host.UiState.GetBool("export.splitAvatars", false));
        }

        // ------------------------------------------------------------------------------------------ what an export refuses

        [Fact]
        public void WithoutATakeNothingIsExported()
        {
            using var rig = new Rig();
            var result = rig.Export.ExportAsync().Result;
            Assert.False(result.Exported);
            Assert.Equal(StatusKind.Warning, result.Kind);
            Assert.Contains("take", result.Message);
            Assert.False(rig.Export.IsExporting);
        }

        [Fact]
        public void WithoutAFormatTheExportAsksForOne()
        {
            using var rig = new Rig();
            rig.Host.Session.Show(EmptyTake());
            rig.AllOff();
            // options alone are not a format
            rig.Export.SetOn(rig.Option("fbx.camera"), true);
            var result = rig.Export.ExportAsync().Result;
            Assert.False(result.Exported);
            Assert.Equal(StatusKind.Warning, result.Kind);
            Assert.Equal("Choose at least one format.", result.Message);
            Assert.Empty(result.Files);
            Assert.False(rig.Export.IsExporting);
            Assert.False(Directory.Exists(rig.Export.Folders.ExportDir));
        }

        // ------------------------------------------------------------------------------------------ the tab

        [Fact]
        public void TheTabListsTheCatalogueAndSaysWhatIsChosen()
        {
            using var rig = new Rig();
            rig.AllOff();
            var vm = new ExportViewModel(rig.Host);
            Assert.False(vm.HasTake);
            int main = rig.Export.Formats.Where(f => f.Group == ExportGroup.Main).Sum(f => 1 + f.Options.Count);
            int more = rig.Export.Formats.Where(f => f.Group == ExportGroup.More).Sum(f => 1 + f.Options.Count);
            Assert.Equal(main, vm.MainFormats.Count);
            Assert.Equal(more, vm.MoreFormats.Count);
            Assert.Equal("none chosen", vm.FormatsSummary);
            Assert.Equal(Tone.Warning, vm.FormatsTone);
            Assert.Equal("", vm.MoreSummary);

            var fbx = vm.MainFormats.First(c => c.Title == rig.Format("fbx").Title);
            var face = vm.MainFormats.First(c => c.Title == rig.Option("fbx.face").Title);
            Assert.True(face.IsNested);
            Assert.False(face.IsAvailable);
            fbx.IsOn = true;
            Assert.True(rig.Settings.ExportFbx);
            Assert.True(face.IsAvailable);
            Assert.Equal("1 format", vm.FormatsSummary);
            Assert.Equal(Tone.Neutral, vm.FormatsTone);

            vm.MoreFormats[0].IsOn = true;
            vm.MoreFormats[1].IsOn = true;
            Assert.Equal("3 formats", vm.FormatsSummary);
            Assert.Equal("2 on", vm.MoreSummary);

            // a change made elsewhere (the settings page) shows up
            rig.Host.SettingsStore.Update(nameof(Settings.ExportFbx), s => s.ExportFbx = false);
            Assert.False(fbx.IsOn);
            Assert.False(face.IsAvailable);

            vm.FrameRateIndex = ExportService.FrameRates.ToList().IndexOf(90);
            Assert.Equal(90, rig.Settings.ExportFps);
            Assert.Equal("90 fps", vm.OptionsSummary);
            rig.Host.SettingsStore.Update(nameof(Settings.ExportFps),
                s => s.ExportFps = 50); // set by hand in the settings file
            Assert.Equal(-1, vm.FrameRateIndex);
            Assert.Equal("50 fps", vm.OptionsSummary);
            Assert.Equal(50, rig.Settings.ExportFps);

            rig.Host.Session.Show(EmptyTake());
            Assert.True(vm.HasTake);
        }

        [Fact]
        public void TheTabKeepsTheFolderWhenThePathIsNoFolder()
        {
            using var rig = new Rig();
            var vm = new ExportViewModel(rig.Host);
            string before = rig.Export.Folders.ExportDir;
            Assert.Equal(before, vm.FolderText);
            Assert.Equal("Exports", vm.FolderSummary);
            Assert.False(vm.HasUnityProject);

            vm.FolderText = "not a full path";
            vm.CommitFolderCommand.Execute(null);
            Assert.Contains("full path", vm.FolderError);
            Assert.Equal(before, vm.FolderText);

            string chosen = Path.Combine(rig.Root, "Chosen");
            vm.FolderText = chosen;
            vm.CommitFolderCommand.Execute(null);
            Assert.Equal("", vm.FolderError);
            Assert.Equal(chosen, rig.Settings.LastExportDir);
            Assert.Equal("Chosen", vm.FolderSummary);

            rig.LinkUnityProject();
            Assert.True(vm.HasUnityProject);
            vm.UseUnityProjectCommand.Execute(null);
            Assert.Equal(rig.Export.Folders.UnityExportDir, vm.FolderText);
            Assert.Equal("Unity project", vm.FolderSummary);

            // a path typed but not confirmed is checked by Export too
            rig.Host.Session.Show(EmptyTake());
            vm.FolderText = "still not a path";
            vm.ExportCommand.Execute(null);
            Assert.True(vm.HasResult);
            Assert.True(vm.ResultIsWarning);
            Assert.Contains("full path", vm.ResultText);
            Assert.False(rig.Export.IsExporting);
        }

        [Fact]
        public void TheTabDraws()
        {
            using var data = new ScratchData();
            HeadlessUi.Run(() =>
            {
                var host = data.NewHost(new AvaloniaUiDispatcher());
                Section.StateStore = host.UiState;
                host.Session.Show(EmptyTake());
                var vm = new ExportViewModel(host);
                var view = new ExportView { DataContext = vm };
                var window = new Window { Width = 320, Height = 760, Content = view };
                window.Show();
                HeadlessUi.Settle(150);

                Assert.NotNull(vm.PickFolder);
                var boxes = Descendants<CheckBox>(view).Where(c => c.IsEffectivelyVisible)
                    .Select(c => c.Content as string).ToList();
                foreach (var format in host.Export.Formats.Where(f => f.Group == ExportGroup.Main))
                    Assert.Contains(format.Title, boxes);
                // "More formats" stays closed until wanted
                foreach (var format in host.Export.Formats.Where(f => f.Group == ExportGroup.More))
                    Assert.DoesNotContain(format.Title, boxes);
                Assert.False(view.FindControl<Control>("ProgressPanel").IsVisible);
                Assert.False(view.FindControl<Control>("ResultPanel").IsVisible);
                Assert.NotNull(HeadlessUi.Capture(window, "export-tab"));

                // nothing chosen: the summary warns, and Export says what to do right under the button
                foreach (var format in host.Export.Formats) host.Export.SetOn(format, false);
                vm.ExportCommand.Execute(null);
                HeadlessUi.Settle();
                Assert.Equal("none chosen", view.FindControl<Section>("FormatsSection").Summary);
                Assert.True(view.FindControl<Control>("ResultPanel").IsVisible);
                Assert.Equal("Choose at least one format.", view.FindControl<TextBlock>("ResultMessage").Text);
                Assert.Contains("warning", view.FindControl<TextBlock>("ResultMessage").Classes);
                Assert.NotNull(HeadlessUi.Capture(window, "export-tab-none-chosen"));

                window.Close();
                host.Dispose();
            });
        }

        static IEnumerable<T> Descendants<T>(Visual root) where T : class
        {
            return VisualExtensions.GetVisualDescendants(root).OfType<T>();
        }

        // ------------------------------------------------------------------------------------------ real takes

        /// <summary>Opens the smallest take copy (changed by <paramref name="prepare" /> in memory only) and waits for its solve.</summary>
        static Recording OpenSmallestTake(AppHost host, ManualDispatcher ui, Action<Recording> prepare = null)
        {
            var take = RecordingIO.Load(PrivateData.Takes()[0]);
            prepare?.Invoke(take);
            host.Session.Show(take);
            Assert.True(
                ui.WaitFor(() => host.Session.Solve == SolveState.Done || host.Session.Solve == SolveState.Failed,
                    120000), "the solve did not end");
            Assert.True(host.Session.ClipIsCurrent, "the take did not solve: " + host.Session.SolveError);
            return take;
        }

        static string NewExportFolder()
        {
            return Path.Combine(Path.GetTempPath(), "vrmr-app-tests-export-" + Guid.NewGuid().ToString("N"));
        }

        /// <summary>After a test on the take copies: closes the host, restores the settings and removes what was exported.</summary>
        static void CleanUp(AppHost host, KeptSettings kept, string folder)
        {
            host?.Dispose();
            kept?.Dispose();
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch (IOException) { }
        }

        static void AssertWrittenInto(string folder, ExportResult result)
        {
            Assert.True(result.Exported, result.Message);
            Assert.NotEmpty(result.Files);
            Assert.All(result.Files,
                f => Assert.True(File.Exists(f) && new FileInfo(f).Length > 0,
                    "missing or empty: " + Path.GetExtension(f)));
            Assert.All(result.Files, f => Assert.StartsWith(folder, f, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void ATakeExportsIntoItsOwnFolder()
        {
            Assert.SkipUnless(PrivateData.Available, PrivateData.SkipReason);
            var ui = new ManualDispatcher();
            var host = PrivateData.NewHost(ui);
            var kept = new KeptSettings(host.Options.Paths);
            string folder = NewExportFolder();
            try
            {
                var take = OpenSmallestTake(host, ui);
                var export = host.Export;
                Assert.True(export.Folders.SetExportDir(folder, out _));
                foreach (var format in export.Formats) export.SetOn(format, format.Group == ExportGroup.Main);
                var stages = new List<string>();
                export.ProgressChanged += () =>
                {
                    if (export.IsExporting) stages.Add(export.ProgressText);
                };

                // a change that is not solved yet: the export solves it first
                host.SettingsStore.Update(nameof(Settings.Smoothing), s => s.Smoothing += 0.05f);
                Assert.False(host.Session.ClipIsCurrent);
                var running = export.ExportAsync();
                Assert.True(export.IsExporting);
                Assert.NotNull(host.Status.Current);
                Assert.Contains("Solving the latest edit, then exporting…", stages);

                // one export at a time
                var second = export.ExportAsync().Result;
                Assert.False(second.Exported);
                Assert.Contains("already running", second.Message);

                Assert.True(ui.WaitFor(() => running.IsCompleted, 300000), "the export did not end");
                var result = running.Result;
                AssertWrittenInto(folder, result);
                Assert.True(host.Session.ClipIsCurrent);
                Assert.Equal(1, result.Parts);
                Assert.Equal(export.Folders.TakeDir(take.Name), result.Folder);
                Assert.Equal(result.Folder, export.Folders.LastOutputDir);
                Assert.Equal(result.Folder, export.Folders.FolderToOpen);
                Assert.Contains(result.Files,
                    f => f.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) &&
                         !f.EndsWith("_armature.fbx", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(result.Files, f => f.EndsWith("_armature.fbx", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(result.Files,
                    f => f.EndsWith(SceneExport.ManifestSuffix, StringComparison.OrdinalIgnoreCase));
                Assert.Contains(result.Files, f => f.EndsWith(".face.json", StringComparison.OrdinalIgnoreCase));
                if (host.Session.Clip.BodySource != "none")
                    Assert.Contains(result.Files, f => f.EndsWith("_body.anim", StringComparison.OrdinalIgnoreCase));
                Assert.True(stages.Count > 1);
                Assert.False(export.IsExporting);
                Assert.Null(host.Status.Current);
                Assert.Equal("", export.ProgressText);
                Assert.Contains("Exported", host.Status.Message);
                Assert.False(host.Session.Dirty);
            }
            finally { CleanUp(host, kept, folder); }
        }

        [Fact]
        public void ATakeWithAnAvatarChangeExportsOnePartPerAvatar()
        {
            Assert.SkipUnless(PrivateData.Available, PrivateData.SkipReason);
            var ui = new ManualDispatcher();
            var host = PrivateData.NewHost(ui);
            var kept = new KeptSettings(host.Options.Paths);
            string folder = NewExportFolder();
            try
            {
                // the avatar changes to one nobody knows, halfway through what the take keeps after its trims
                var take = OpenSmallestTake(host, ui, t =>
                {
                    double end = t.Edits.TrimOut < 0 ? t.Duration : t.Edits.TrimOut;
                    t.Events.Add(new RecEvent
                    {
                        T = (t.Edits.TrimIn + end) / 2, Type = "avatar",
                        Text = "avtr_00000000-0000-0000-0000-000000000000"
                    });
                });
                var export = host.Export;
                Assert.True(export.Folders.SetExportDir(folder, out _));
                export.Folders.TakeFolders = false;
                foreach (var format in export.Formats)
                    export.SetOn(format, format.Id == "fbxArmature" || format.Group == ExportGroup.More);
                export.SplitAtAvatarChanges = true;
                var edits = take.Edits;

                var running = export.ExportAsync();
                Assert.True(ui.WaitFor(() => running.IsCompleted, 300000), "the export did not end");
                var result = running.Result;
                AssertWrittenInto(folder, result);
                Assert.True(result.Parts >= 2, "parts: " + result.Parts);
                Assert.Contains("one per avatar", result.Message);
                Assert.Equal(folder, result.Folder);
                // the take itself is as it was: its own edit list, nothing to save
                Assert.Same(edits, take.Edits);
                Assert.False(host.Session.Dirty);
                Assert.True(host.Session.ClipIsCurrent);

                string name = ExportFolders.SafeName(take.Name);
                var armatures = result.Files.Where(f => f.EndsWith("_armature.fbx", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                Assert.Equal(result.Parts, armatures.Count);
                Assert.Contains(Path.Combine(folder, name + "_1_armature.fbx"), armatures);
                foreach (string armature in armatures)
                {
                    string part = Path.GetFileName(armature)
                        .Substring(0, Path.GetFileName(armature).Length - "_armature.fbx".Length);
                    Assert.Matches("_[0-9]+$", part.Substring(name.Length));
                    Assert.Contains(Path.Combine(folder, part + "_osc.json"), result.Files);
                    Assert.Contains(Path.Combine(folder, part + ".face.json"), result.Files);
                    Assert.Contains(Path.Combine(folder, part + SceneExport.ManifestSuffix), result.Files);
                }
            }
            finally { CleanUp(host, kept, folder); }
        }

        [Fact]
        public void TheTabShowsWhereTheFilesWent()
        {
            Assert.SkipUnless(PrivateData.Available, PrivateData.SkipReason);
            string folder = NewExportFolder();
            AppHost host = null;
            KeptSettings kept = null;
            try
            {
                HeadlessUi.Run(() =>
                {
                    host = PrivateData.NewHost(new AvaloniaUiDispatcher());
                    kept = new KeptSettings(host.Options.Paths);
                    Section.StateStore = host.UiState;
                    var vm = new ExportViewModel(host);
                    var view = new ExportView { DataContext = vm };
                    var window = new Window { Width = 320, Height = 760, Content = view };
                    window.Show();
                    host.Session.Show(RecordingIO.Load(PrivateData.Takes()[0]));
                    foreach (var format in host.Export.Formats)
                        host.Export.SetOn(format, format.Id == "anim" || format.Id == "faceJson");
                    vm.FolderText = folder; // typed, not confirmed: Export takes it

                    vm.ExportCommand.Execute(null);
                    Assert.True(vm.IsExporting);
                    HeadlessUi.Settle(30);
                    Assert.True(view.FindControl<Control>("ProgressPanel").IsVisible);
                    Assert.False(view.FindControl<Button>("ExportButton").IsEffectivelyEnabled);
                    Assert.True(HeadlessUi.WaitFor(() => !vm.IsExporting && vm.HasResult, 300000),
                        "the export did not end");
                    HeadlessUi.Settle();

                    Assert.True(vm.ResultIsSuccess, vm.ResultText);
                    Assert.True(vm.CanOpenResult);
                    Assert.StartsWith("Exported", vm.ResultText);
                    Assert.Equal(folder, host.Settings.LastExportDir);
                    Assert.True(Directory.GetFiles(folder, "*.face.json", SearchOption.AllDirectories).Length == 1);
                    Assert.Contains("success", view.FindControl<TextBlock>("ResultMessage").Classes);
                    Assert.False(view.FindControl<Control>("ProgressPanel").IsVisible);
                    Assert.True(view.FindControl<Button>("ExportButton").IsEffectivelyEnabled);
                    Assert.NotNull(HeadlessUi.Capture(window, "export-tab-result"));
                    window.Close();
                });
            }
            finally { HeadlessUi.Run(() => CleanUp(host, kept, folder)); }
        }

        /// <summary>The app's services on an empty data folder, with a UI thread the test drives.</summary>
        sealed class Rig : IDisposable
        {
            readonly ScratchData _data = new();

            public Rig() { Host = _data.NewHost(Ui); }
            public ManualDispatcher Ui { get; } = new();
            public AppHost Host { get; }

            public string Root => _data.Root;
            public ExportService Export => Host.Export;
            public Settings Settings => Host.Settings;

            public void Dispose()
            {
                Host.Dispose();
                _data.Dispose();
            }

            public ExportFormat Format(string id)
            {
                return Export.Formats.First(f => f.Id == id);
            }

            public ExportOption Option(string id)
            {
                return Export.Formats.SelectMany(f => f.Options).First(o => o.Id == id);
            }

            public void AllOff()
            {
                foreach (var format in Export.Formats)
                {
                    Export.SetOn(format, false);
                    foreach (var option in format.Options) Export.SetOn(option, false);
                }
            }

            /// <summary>Makes a folder that looks like a Unity project and links it.</summary>
            public string LinkUnityProject()
            {
                string project = Path.Combine(Root, "UnityProject");
                Directory.CreateDirectory(Path.Combine(project, "Assets"));
                Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
                Host.SettingsStore.Update(nameof(Settings.UnityProjects), s => s.UnityProjects.Add(project));
                return project;
            }
        }

        /// <summary>Puts the settings files of the shared take copies back as they were, so other tests find them unchanged.</summary>
        sealed class KeptSettings : IDisposable
        {
            readonly List<(string Path, byte[] Content)> _files = new();

            public KeptSettings(AppPaths paths)
            {
                foreach (string path in new[] { paths.SettingsFile, paths.UiStateFile })
                    _files.Add((path, File.Exists(path) ? File.ReadAllBytes(path) : null));
            }

            public void Dispose()
            {
                foreach ((string path, byte[] content) in _files)
                    if (content != null) File.WriteAllBytes(path, content);
                    else File.Delete(path);
            }
        }
    }
}
