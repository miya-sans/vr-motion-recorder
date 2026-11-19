using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.VisualTree;
using VRMR.App.Platform;
using VRMR.App.Services;
using VRMR.App.ViewModels;
using VRMR.App.Views.Settings;
using Xunit;

namespace VRMR.App.Tests
{
    [Collection("app")]
    public class UnityProjectPickerTests
    {
        static string Project(string root, string name, bool bridge = false)
        {
            string path = Path.Combine(root, name);
            Directory.CreateDirectory(Path.Combine(path, "Assets"));
            Directory.CreateDirectory(Path.Combine(path, "ProjectSettings"));
            File.WriteAllText(Path.Combine(path, "ProjectSettings", "ProjectVersion.txt"),
                "m_EditorVersion: 2022.3.22f1");
            if (bridge)
            {
                Directory.CreateDirectory(Path.Combine(path, "Packages"));
                File.WriteAllText(Path.Combine(path, "Packages", "manifest.json"),
                    "{\"dependencies\":{\"com.vrmr.motion-bridge\":\"1.4.0\"}}");
            }

            return path;
        }

        static void List(string folder, params string[] projects)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "settings.json"),
                JsonSerializer.Serialize(new { userProjects = projects }));
        }

        [Fact]
        public async Task AddingAProjectIndexesOnlyThatProject()
        {
            using var data = new ScratchData();
            await HeadlessUi.RunAsync(async () =>
            {
                using var host = data.NewHost(new AvaloniaUiDispatcher());
                var calls = new ConcurrentQueue<string>();
                var library = new AvatarLibrary(host.Settings, (project, progress) =>
                {
                    calls.Enqueue(project);
                    return new List<UnityProjectScanner.Result>();
                });
                var service = new AvatarService(library, host.SettingsStore, host.Live, host.Status, host.Ui,
                    host.Meshes);
                string first = Project(data.Root, "First");
                string second = Project(data.Root, "Second");
                service.AddUnityProject(first);
                Assert.True(await HeadlessUi.WaitForAsync(() => !service.Scanning));
                service.AddUnityProject(second);
                Assert.True(await HeadlessUi.WaitForAsync(() => !service.Scanning));
                Assert.Equal(new[] { first, second }, calls.ToArray());
                Assert.Contains("Found avatars: 0", service.ProjectScanInfo(first));
                Assert.Contains("Found avatars: 0", service.ProjectScanInfo(second));
                await HeadlessUi.SettleAsync(300);
            });
        }

        static double Contrast(Color foreground, Color background)
        {
            double Linear(byte channel)
            {
                double value = channel / 255.0;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }

            double Luminance(Color color)
            {
                return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
            }

            double a = Luminance(foreground), b = Luminance(background);
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }

        [Theory]
        [InlineData("dark")]
        [InlineData("light")]
        public async Task SelectedProjectDetailsHaveReadableContrast(string theme)
        {
            using var data = new ScratchData();
            await HeadlessUi.RunAsync(async () =>
            {
                using var host = data.NewHost(new AvaloniaUiDispatcher());
                App.ApplyTheme(Application.Current, theme);
                var dialog = new UnityProjectPicker();
                try
                {
                    string project = Project(data.Root, "Selected");
                    string folder =
                        UnityProjectSources.DefaultFolder(host.Options.Paths.Machine, UnityProjectSource.Alcom);
                    List(folder, project);
                    var vm = new UnityProjectPickerViewModel(host.Options.Paths.Machine, UnityProjectSource.Alcom,
                        Array.Empty<string>());
                    dialog.DataContext = vm;
                    dialog.Show();
                    Assert.True(await HeadlessUi.WaitForAsync(() => !vm.Loading && vm.Projects.Count == 1));
                    var list = dialog.FindControl<ListBox>("ProjectList");
                    list.SelectedIndex = 0;
                    await HeadlessUi.SettleAsync(80);
                    var row = list.ContainerFromIndex(0);
                    var presenter = row.GetVisualDescendants().OfType<ContentPresenter>()
                        .First(control => control.Name == "PART_ContentPresenter");
                    var background = Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background);
                    foreach (var detail in row.GetVisualDescendants().OfType<TextBlock>()
                                 .Where(text => text.Classes.Contains("project-detail")))
                    {
                        var foreground = Assert.IsAssignableFrom<ISolidColorBrush>(detail.Foreground);
                        Assert.True(Contrast(foreground.Color, background.Color) >= 4.5,
                            $"{theme}: {foreground.Color} on {background.Color}");
                    }

                    Assert.NotNull(HeadlessUi.Capture(dialog, "unity-project-picker-selected-" + theme));
                }
                finally
                {
                    dialog.Close();
                    App.ApplyTheme(Application.Current, "dark");
                }
            });
        }

        [Fact]
        public async Task PickerStartsUnselectedAndOnlyAcceptsAvailableUnlinkedProjects()
        {
            using var data = new ScratchData();
            await HeadlessUi.RunAsync(async () =>
            {
                using var host = data.NewHost(new AvaloniaUiDispatcher());
                string linked = Project(data.Root, "Linked", true);
                string available = Project(data.Root, "Available");
                string missing = Path.Combine(data.Root, "Missing");
                string folder = UnityProjectSources.DefaultFolder(host.Options.Paths.Machine, UnityProjectSource.Alcom);
                List(folder, linked, available, missing);
                var vm = new UnityProjectPickerViewModel(host.Options.Paths.Machine, UnityProjectSource.Alcom,
                    new[] { linked });
                await vm.LoadAsync();

                Assert.Empty(vm.SelectedPaths());
                Assert.False(vm.CanAdd);
                Assert.True(vm.Projects.Single(p => p.Path == linked).HasBridge);
                Assert.False(vm.Projects.Single(p => p.Path == available).HasBridge);
                Assert.False(vm.Projects.Single(p => p.Path == missing).CanAdd);
                Assert.Contains("Already added", vm.Projects.Single(p => p.Path == linked).Info);
                vm.Select(vm.Projects);
                Assert.Equal(new[] { available }, vm.SelectedPaths());

                string custom = Path.Combine(data.Root, "CustomData");
                List(custom, available);
                vm.PickFolder = start => Task.FromResult(custom);
                await vm.LocateDataFolderCommand.ExecuteAsync(null);
                Assert.Equal(custom, vm.Folder);
                Assert.Single(vm.Projects);
                Assert.Empty(vm.SelectedPaths());
                Assert.Empty(host.Settings.UnityProjects);
            });
        }

        [Theory]
        [InlineData(UnityProjectSource.UnityHub)]
        [InlineData(UnityProjectSource.Alcom)]
        [InlineData(UnityProjectSource.CreatorCompanion)]
        public async Task ManagerCommandsSaveOnlyReturnedSelectionsAndCancellationDoesNothing(UnityProjectSource source)
        {
            using var data = new ScratchData();
            await HeadlessUi.RunAsync(async () =>
            {
                using var host = data.NewHost(new AvaloniaUiDispatcher());
                var vm = new SettingsFoldersViewModel(host);
                string project = Project(data.Root, "Selected");
                string other = Project(data.Root, "AlsoSelected");
                vm.PickProjects = selected =>
                {
                    Assert.Equal(source, selected);
                    return Task.FromResult<string[]>(null);
                };
                await vm.AddFromManagerCommand.ExecuteAsync(source);
                Assert.Empty(host.Settings.UnityProjects);
                vm.PickProjects = selected =>
                    Task.FromResult(new[] { project, other, project + Path.DirectorySeparatorChar });
                await vm.AddFromManagerCommand.ExecuteAsync(source);
                Assert.Equal(new[] { project, other }, host.Settings.UnityProjects);
                Assert.Equal(2, vm.Projects.Count);
                Assert.True(await HeadlessUi.WaitForAsync(() => !host.AvatarService.Scanning &&
                                                                vm.Projects.All(row =>
                                                                    row.ScanInfo.Contains("Found avatars: 0"))));
                vm.RemoveProjectCommand.Execute(vm.Projects[0]);
                vm.RemoveProjectCommand.Execute(vm.Projects[0]);
                await host.Environment.RefreshAsync();
                Assert.Empty(host.Settings.UnityProjects);
                Assert.Empty(host.Environment.Report.UnityProjects);
            });
        }

        [Fact]
        public async Task PickerAndSettingsRenderTheirLabelsAndDisabledRows()
        {
            using var data = new ScratchData();
            await HeadlessUi.RunAsync(async () =>
            {
                using var host = data.NewHost(new AvaloniaUiDispatcher());
                string available = Project(data.Root, "Available");
                string linked = Project(data.Root, "Linked", true);
                string folder = UnityProjectSources.DefaultFolder(host.Options.Paths.Machine, UnityProjectSource.Alcom);
                List(folder, linked, available, Path.Combine(data.Root, "Missing"));
                var vm = new UnityProjectPickerViewModel(host.Options.Paths.Machine, UnityProjectSource.Alcom,
                    new[] { linked });
                var dialog = new UnityProjectPicker { DataContext = vm };
                dialog.Show();
                Assert.True(await HeadlessUi.WaitForAsync(() => !vm.Loading && vm.Projects.Count == 3));
                await HeadlessUi.SettleAsync(80);
                var list = dialog.FindControl<ListBox>("ProjectList");
                Assert.Equal(-1, list.SelectedIndex);
                Assert.False(list.ContainerFromIndex(vm.Projects.IndexOf(vm.Projects.Single(p => p.Path == linked)))
                    .IsEnabled);
                Assert.False(list.ContainerFromIndex(vm.Projects.IndexOf(vm.Projects.Single(p => p.Missing)))
                    .IsEnabled);
                Assert.NotNull(HeadlessUi.Capture(dialog, "unity-project-picker"));
                dialog.Close();

                host.AvatarService.AddUnityProjects(new[] { linked, available });
                Assert.True(await HeadlessUi.WaitForAsync(() => !host.AvatarService.Scanning));
                await host.Environment.RefreshAsync();
                var settingsModel = new SettingsFoldersViewModel(host);
                var settings = new SettingsFoldersView { DataContext = settingsModel };
                var window = new Window { Content = settings, Width = 900, Height = 750 };
                window.Show();
                await HeadlessUi.SettleAsync(80);
                string[] labels = settings.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string)
                    .ToArray();
                foreach (string label in new[]
                         {
                             "Folder", "Unity Hub", "ALCOM", "VRChat Creator Companion", "Remove"
                         })
                    Assert.Contains(label, labels);
                Assert.DoesNotContain("Scan again", labels);
                foreach (var row in settingsModel.Projects)
                {
                    Assert.Contains("Unity 2022.3.22f1", row.Details);
                    Assert.Contains("Found avatars: 0", row.Details);
                    Assert.Single(settings.GetVisualDescendants().OfType<TextBlock>(),
                        text => text.Text == row.Details);
                }

                Assert.NotNull(HeadlessUi.Capture(window, "unity-project-settings"));
                var removeButtons = settings.GetVisualDescendants().OfType<Button>()
                    .Where(button => button.Content as string == "Remove").ToArray();
                Assert.Equal(2, removeButtons.Length);
                settingsModel.SelectedProject = settingsModel.Projects.Single(p => p.Path == available);
                var removeLinked = removeButtons.Single(button =>
                    button.CommandParameter is UnityProjectRowViewModel row && row.Path == linked);
                removeLinked.Command.Execute(removeLinked.CommandParameter);
                Assert.Equal(available, Assert.Single(host.Settings.UnityProjects));
                Assert.True(Directory.Exists(linked));
                window.Close();
            });
        }
    }
}
