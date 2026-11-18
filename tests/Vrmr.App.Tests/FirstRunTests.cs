using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using VRMR.App.Composition;
using VRMR.App.Platform;
using VRMR.App.ViewModels;
using VRMR.App.Views.FirstRun;
using Xunit;

namespace VRMR.App.Tests
{
    /// <summary>The first-run guide: when it opens, its three steps, and the guide drawn off screen.</summary>
    [Collection("app")]
    public class FirstRunTests
    {
        static (ScratchData data, ManualDispatcher ui, AppHost host, FirstRunViewModel guide) Start()
        {
            var data = new ScratchData();
            var ui = new ManualDispatcher();
            var host = data.NewHost(ui);
            return (data, ui, host, new FirstRunViewModel(host));
        }

        static void WaitForEnvironment(ManualDispatcher ui, AppHost host)
        {
            Assert.True(ui.WaitFor(() => host.Environment.Report != null && !host.Environment.Checking),
                "the environment check did not finish");
        }

        [Fact]
        public void TheGuideOpensByItselfOnlyUntilItHasBeenSeen()
        {
            var (data, ui, host, guide) = Start();
            using (data)
            using (host)
            {
                Assert.False(guide.IsOpen); // the shell decides when
                Assert.True(guide.IsFirstRun);
                Assert.True(guide.OpenIfFirstRun());
                Assert.True(guide.IsOpen);

                guide.SkipCommand.Execute(null);
                Assert.False(guide.IsOpen);
                Assert.True(host.UiState.GetBool(FirstRunViewModel.DoneKey, false));
                Assert.Contains("Settings → About", host.Status.Message); // where to find it again

                var next = new FirstRunViewModel(host);
                Assert.False(next.IsFirstRun);
                Assert.False(next.OpenIfFirstRun());
                Assert.False(next.IsOpen);
                // from Settings it opens whenever asked
                next.Open();
                Assert.True(next.IsOpen);
            }
        }

        [Fact]
        public void TheStepsGoForwardAndBackAndFinishCloses()
        {
            var (data, ui, host, guide) = Start();
            using (data)
            using (host)
            {
                guide.Open();
                Assert.True(guide.ShowsFound);
                Assert.False(guide.CanGoBack);
                Assert.Equal("Step 1 of 3", guide.StepText);

                guide.NextCommand.Execute(null);
                Assert.True(guide.ShowsFolder);
                Assert.True(guide.CanGoBack);
                guide.NextCommand.Execute(null);
                Assert.True(guide.ShowsAvatar);
                Assert.True(guide.IsLastStep);
                Assert.Equal("Step 3 of 3", guide.StepText);
                guide.NextCommand.Execute(null);
                Assert.Equal(2, guide.Step); // there is no step after the last

                guide.BackCommand.Execute(null);
                Assert.True(guide.ShowsFolder);
                guide.NextCommand.Execute(null);
                guide.FinishCommand.Execute(null);
                Assert.False(guide.IsOpen);
                Assert.False(guide.IsFirstRun);

                // opened again it starts at the beginning
                guide.Open();
                Assert.Equal(0, guide.Step);
            }
        }

        [Fact]
        public void WhatWasFoundComesFromTheEnvironmentAndCanBeCheckedAgain()
        {
            var (data, ui, host, guide) = Start();
            using (data)
            using (host)
            {
                Assert.Equal(new[] { "SteamVR", "VRChat", "VRChat's OSC switch", "Blender", "Unity projects" },
                    guide.Found.Select(r => r.Title).ToArray());
                guide.Open();
                WaitForEnvironment(ui, host);
                Assert.False(guide.IsChecking);
                Assert.All(guide.Found, row => Assert.NotEqual("", row.Summary));

                // an empty sandbox: nothing is installed, and recording needs VRChat
                var vrchat = guide.Found[1];
                Assert.True(vrchat.NeedsAttention);
                Assert.True(vrchat.HasAction);
                Assert.True(guide.FoundNeedsAttention);
                // what the app records without is not shown as a problem
                Assert.False(guide.Found[0].NeedsAttention);
                Assert.False(guide.Found[3].NeedsAttention);
                Assert.False(guide.Found[4].NeedsAttention);

                // VRChat has run meanwhile: "Check again" finds it
                Directory.CreateDirectory(new VrcFolders(host.Options.Paths.Machine).DataDir);
                var before = host.Environment.Report;
                guide.CheckAgainCommand.Execute(null);
                Assert.True(guide.IsChecking);
                WaitForEnvironment(ui, host);
                Assert.NotSame(before, host.Environment.Report);
                Assert.True(vrchat.IsOk);
                Assert.False(guide.IsChecking);
            }
        }

        [Fact]
        public void InPortableModeTheRecordingsFolderIsShownAndFixed()
        {
            var (data, ui, host, guide) = Start();
            using (data)
            using (host)
            {
                Assert.True(guide.IsPortable);
                Assert.False(guide.CanMoveFolder);
                Assert.Equal(host.Settings.RecordingsDir, guide.RecordingsFolder);
                Assert.False(guide.FolderMoved);

                bool asked = false;
                guide.PickFolder = (title, start) =>
                {
                    asked = true;
                    return Task.FromResult(Path.Combine(data.Root, "elsewhere"));
                };
                guide.BrowseFolderCommand.Execute(null);
                Assert.False(asked);
                Assert.Equal(host.Settings.RecordingsDir, guide.RecordingsFolder);
            }
        }

        [Fact]
        public void AnAvatarCanBeAddedFromTheGuideOrLeftForLater()
        {
            var (data, ui, host, guide) = Start();
            using (data)
            using (host)
            {
                Assert.False(guide.HasOwnAvatar);
                Assert.Contains("mannequin", guide.AvatarState);

                // the picker was cancelled: nothing happens
                guide.PickFiles = () => Task.FromResult<IReadOnlyList<string>>(new string[0]);
                guide.AddAvatarCommand.Execute(null);
                Assert.False(guide.IsImporting);

                string fbx = TestAvatars.WriteUnityModel(Path.Combine(data.Root, "source"), "Guide avatar");
                guide.PickFiles = () => Task.FromResult<IReadOnlyList<string>>(new[] { fbx });
                guide.AddAvatarCommand.Execute(null);
                Assert.True(guide.IsImporting);
                Assert.True(ui.WaitFor(() => !guide.IsImporting && guide.HasOwnAvatar), "the import did not finish");
                Assert.Contains("Guide avatar", guide.AvatarState);
                Assert.True(host.AvatarService.HasOwnEntries);
            }
        }

        // ------------------------------------------------------------------------------------------ drawn off screen

        [Theory]
        [InlineData(1.0)]
        [InlineData(1.5)]
        public async Task EveryStepDraws(double scale)
        {
            using var data = new ScratchData();
            await HeadlessUi.RunAsync(async () =>
            {
                using var host = data.NewHost(new AvaloniaUiDispatcher());
                var guide = new FirstRunViewModel(host);
                var view = new FirstRunView { DataContext = guide };
                var window = new Window { Width = 1400, Height = 860, Content = view };
                window.Show();
                if (scale != 1) window.SetRenderScaling(scale);
                await HeadlessUi.SettleAsync(100);
                Assert.False(view.IsVisible); // closed until its view-model opens

                guide.Open();
                Assert.True(await HeadlessUi.WaitForAsync(() =>
                    view.IsVisible && host.Environment.Report != null && !guide.IsChecking));
                string suffix = scale == 1 ? "" : "-150";
                for (int step = 0; step < FirstRunViewModel.StepCount; step++)
                {
                    Assert.Equal(step, guide.Step);
                    await HeadlessUi.SettleAsync(80);
                    Assert.NotNull(HeadlessUi.Capture(window, $"firstrun-{step + 1}{suffix}"));
                    guide.NextCommand.Execute(null);
                }

                // the last step with an avatar in the library
                TestAvatars.Save(host, "Test avatar");
                await HeadlessUi.SettleAsync(80);
                Assert.True(guide.HasOwnAvatar);
                Assert.NotNull(HeadlessUi.Capture(window, $"firstrun-3-with-avatar{suffix}"));

                guide.FinishCommand.Execute(null);
                await HeadlessUi.SettleAsync(50);
                Assert.False(view.IsVisible);
                window.Close();
            });
        }
    }
}
