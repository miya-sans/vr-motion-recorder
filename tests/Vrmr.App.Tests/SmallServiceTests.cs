using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VRMR.App.Controls;
using VRMR.App.Services;
using VRMR.App.ViewModels;
using Xunit;

namespace VRMR.App.Tests
{
    [Collection("app")]
    public class AppOptionsTests
    {
        [Fact]
        public void NoArgumentsMeansTheUsersFoldersWithCapture()
        {
            var o = AppOptions.Parse(Array.Empty<string>());
            Assert.Null(o.Error);
            Assert.Null(o.DataDir);
            Assert.False(o.NoCapture);
        }

        [Fact]
        public void DataAndNoCaptureAreRead()
        {
            using var tmp = new ScratchData();
            var o = AppOptions.Parse(new[] { "--data", tmp.Root, "--no-capture" });
            Assert.Null(o.Error);
            Assert.Equal(Path.GetFullPath(tmp.Root), o.DataDir);
            Assert.True(o.NoCapture);
            Assert.StartsWith(o.DataDir, o.Paths.AppDataDir, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(o.DataDir, o.Paths.RecordingsDir, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void DataWithEqualsSignIsRead()
        {
            using var tmp = new ScratchData();
            var o = AppOptions.Parse(new[] { "--data=" + tmp.Root });
            Assert.Equal(Path.GetFullPath(tmp.Root), o.DataDir);
        }

        [Fact]
        public void TakeBenchmarkDisablesCapture()
        {
            var o = AppOptions.Parse(["--bench", "take.vrmr", "--bench-seconds", "1.5", "--data", "scratch"]);
            Assert.Null(o.Error);
            Assert.Equal(Path.GetFullPath("take.vrmr"), o.BenchmarkTake);
            Assert.Equal(1.5, o.BenchmarkSeconds);
            Assert.True(o.NoCapture);
            Assert.True(o.Paths.Isolated);
        }

        [Fact]
        public void LiveBenchmarkUsesRealSourcesWithPortableFiles()
        {
            var o = AppOptions.Parse(["--bench-live", "--data", "scratch"]);
            Assert.Null(o.Error);
            Assert.True(o.BenchmarkLive);
            Assert.False(o.NoCapture);
            Assert.False(o.Paths.Isolated);
            Assert.Equal(Path.GetFullPath("scratch"), o.Paths.DataRoot);
        }

        [Theory]
        [InlineData("--data")]
        [InlineData("--data", "--no-capture")]
        [InlineData("--nocapture")]
        [InlineData("some.vrmr")]
        [InlineData("--bench")]
        [InlineData("--bench-live", "--no-capture")]
        [InlineData("--bench-live", "--bench", "take.vrmr")]
        [InlineData("--bench-seconds", "1")]
        [InlineData("--bench-live", "--bench-seconds", "NaN")]
        [InlineData("--bench-live", "--bench-seconds", "0")]
        public void AnythingElseIsAnErrorSoATypoNeverOpensRealData(params string[] args)
        {
            Assert.NotNull(AppOptions.Parse(args).Error);
        }

        [Fact]
        public void PortableModeKeepsEveryFolderUnderTheDataFolder()
        {
            using var tmp = new ScratchData();
            var ui = new ManualDispatcher();
            using var host = tmp.NewHost(ui);
            string root = Path.GetFullPath(tmp.Root);
            Assert.StartsWith(root, host.Settings.RecordingsDir, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(root, host.Settings.AvatarLibraryDir, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(root, Settings.AppDataDir, StringComparison.OrdinalIgnoreCase);
            Assert.False(host.CaptureOn);
            // no source was started
            Assert.Null(host.Recorder.OpenVR);
            Assert.Null(host.Recorder.OscQuery);
            Assert.Null(host.Recorder.OscQueryRx);
            Assert.Null(host.Recorder.LegacyRx);
            Assert.Null(host.Recorder.Log);
        }

        [Fact]
        public void RecordingIsRefusedWithoutLiveSources()
        {
            using var tmp = new ScratchData();
            var ui = new ManualDispatcher();
            using var host = tmp.NewHost(ui);
            host.Recording.Start();
            Assert.False(host.Recording.IsRecording);
            Assert.Empty(Directory.Exists(tmp.TakesDir)
                ? Directory.GetFiles(tmp.TakesDir, "*.vrmr")
                : Array.Empty<string>());
            Assert.Equal(StatusKind.Warning, host.Status.Kind);
        }
    }

    [Collection("app")]
    public class HotkeyTextTests
    {
        [Theory]
        [InlineData("F9", 0u, 0x78u)]
        [InlineData("f12", 0u, 0x7Bu)]
        [InlineData("Ctrl+Shift+R", HotkeyText.Ctrl | HotkeyText.Shift, (uint)'R')]
        [InlineData(" alt + 5 ", HotkeyText.Alt, (uint)'5')]
        [InlineData("Win+Space", HotkeyText.Win, 0x20u)]
        [InlineData("Control+NumPad3", HotkeyText.Ctrl, 0x63u)]
        [InlineData("Pause", 0u, 0x13u)]
        public void KeyNamesAreUnderstood(string text, uint mods, uint vk)
        {
            Assert.True(HotkeyText.TryParse(text, out uint m, out uint k));
            Assert.Equal(mods, m);
            Assert.Equal(vk, k);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("Ctrl")]
        [InlineData("Ctrl+Shift")]
        [InlineData("F25")]
        [InlineData("A+B")]
        [InlineData("Hyper+X")]
        public void TextThatNamesNoKeyIsRejected(string text)
        {
            Assert.False(HotkeyText.TryParse(text, out _, out _));
        }
    }

    [Collection("app")]
    public class TextAndFilterTests
    {
        [Theory]
        [InlineData(0, "00:00.00")]
        [InlineData(5.5, "00:05.50")]
        [InlineData(65.25, "01:05.25")]
        [InlineData(59.999, "01:00.00")]
        [InlineData(-3, "00:00.00")]
        public void TimeIsMinutesSecondsHundredths(double seconds, string expected)
        {
            Assert.Equal(expected, TimeText.Format(seconds));
        }

        [Fact]
        public void TimeWithoutFractionDropsIt()
        {
            Assert.Equal("02:07", TimeText.Format(127.9, false));
        }

        [Fact]
        public void SearchMatchesEveryWordInNameAvatarOrWorld()
        {
            var items = new[]
            {
                new TakeInfo { Path = "a.vrmr", Name = "Dance one", Avatar = "Robot", World = "Studio" },
                new TakeInfo { Path = "b.vrmr", Name = "Dance two", Avatar = "Cat", World = "Beach" },
                new TakeInfo { Path = "c.vrmr", Name = "Walk", Avatar = "Robot", World = "Beach" }
            };
            Assert.Equal(3, TakeLibrary.Filter(items, "").Count());
            Assert.Equal(new[] { "a.vrmr", "b.vrmr" }, TakeLibrary.Filter(items, "dance").Select(i => i.Path));
            Assert.Equal(new[] { "c.vrmr" }, TakeLibrary.Filter(items, "robot beach").Select(i => i.Path));
            Assert.Empty(TakeLibrary.Filter(items, "robot studio beach"));
            Assert.Equal(new[] { "b.vrmr" }, TakeLibrary.Filter(items, "b.vrmr").Select(i => i.Path));
        }

        [Fact]
        public void CoverageSplitsWhereSamplesStop()
        {
            double[] t = new[] { 0, 0.1, 0.2, 2.0, 2.1, 5.0 };
            var spans = TimelineData.Coverage(t, 0.5);
            Assert.Equal(3, spans.Count);
            Assert.Equal(new[] { 0, 0.2 }, spans[0]);
            Assert.Equal(new[] { 2.0, 2.1 }, spans[1]);
            Assert.Equal(new[] { 5.0, 5.0 }, spans[2]);
            Assert.Empty(TimelineData.Coverage(Array.Empty<double>(), 0.5));
        }

        [Theory]
        [InlineData("12.5", 12.5)]
        [InlineData("12,5", 12.5)]
        [InlineData(" -3 cm", -3)]
        [InlineData("+0.25 s", 0.25)]
        [InlineData("90°", 90)]
        public void NumberFieldsReadDecimalCommasAndUnits(string text, double expected)
        {
            Assert.True(NumberField.TryParse(text, out double v));
            Assert.Equal(expected, v, 9);
        }

        [Theory]
        [InlineData("")]
        [InlineData("abc")]
        [InlineData("cm 5")]
        public void NumberFieldsRejectTextWithoutANumber(string text)
        {
            Assert.False(NumberField.TryParse(text, out _));
        }

        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(190f, -170f)]
        [InlineData(-190f, 170f)]
        [InlineData(360.001f, 0f)]
        public void AnglesWrapToHalfTurns(float input, float expected)
        {
            Assert.Equal(expected, PlacementService.WrapDegrees(input), 2);
        }
    }

    [Collection("app")]
    public class StoreTests
    {
        [Fact]
        public void UiStateSurvivesARestart()
        {
            using var tmp = new ScratchData();
            string file = Path.Combine(tmp.Root, "ui-state.json");
            var ui = new ManualDispatcher();
            var a = new UiStateStore(file, ui);
            a.Set("fold.take.body", false);
            a.Set("panel.library.width", 300.0);
            a.Set("theme", "light");
            a.Flush();
            var b = new UiStateStore(file, ui);
            Assert.False(b.GetBool("fold.take.body", true));
            Assert.Equal(300.0, b.GetNumber("panel.library.width", 0));
            Assert.Equal("light", b.GetString("theme", "dark"));
            Assert.True(b.GetBool("missing", true));
        }

        [Fact]
        public void SettingsAreSavedOnceAfterABurstOfChanges()
        {
            using var tmp = new ScratchData();
            var ui = new ManualDispatcher();
            using var host = tmp.NewHost(ui);
            var seen = new List<string>();
            host.SettingsStore.Changed += seen.Add;
            host.SettingsStore.Update(nameof(Settings.Smoothing), s => s.Smoothing = 0.4f);
            host.SettingsStore.Update(nameof(Settings.Smoothing), s => s.Smoothing = 0.6f);
            Assert.Equal(new[] { nameof(Settings.Smoothing), nameof(Settings.Smoothing) }, seen);
            host.SettingsStore.Flush();
            Assert.Equal(0.6f, Settings.Load().Smoothing, 3);
        }

        [Fact]
        public void StatusKeepsTheLastMessageAndTracksProgress()
        {
            var ui = new ManualDispatcher();
            var status = new StatusService(ui);
            int changes = 0;
            Toast toast = null;
            status.Changed += () => changes++;
            status.ToastRaised += t => toast = t;
            status.Info("Saved");
            Assert.Equal("Saved", status.Message);
            using (var p = status.Begin("Exporting"))
            {
                Assert.Same(p, status.Current);
                p.Report(0.5, "Writing files");
                Assert.Equal("Writing files", status.Current.Label);
                Assert.Equal(0.5, status.Current.Fraction);
            }

            Assert.Null(status.Current);
            status.Notify("Done", StatusKind.Success);
            Assert.Equal("Done", toast.Text);
            Assert.Equal("Done", status.Message);
            Assert.True(changes >= 4);
        }

        [Fact]
        public void TheDebouncerRunsOnceAfterThingsGoQuiet()
        {
            var ui = new ManualDispatcher();
            int runs = 0;
            var d = new Debouncer(ui, TimeSpan.FromMilliseconds(300), () => runs++);
            d.Trigger();
            ui.Advance(0.2);
            d.Trigger();
            ui.Advance(0.2);
            Assert.Equal(0, runs);
            ui.Advance(0.2);
            Assert.Equal(1, runs);
            d.Trigger();
            d.Flush();
            Assert.Equal(2, runs);
            d.Trigger();
            d.Cancel();
            ui.Advance(1);
            Assert.Equal(2, runs);
        }
    }

    [Collection("app")] // a solve reads the process-wide paths: never next to a test that switches them
    public class TakeViewTests
    {
        [Fact]
        public void ATakeCanBeSolvedWithOtherEditsWithoutTouchingItsOwn()
        {
            // the export of one avatar's stretch solves a view of the take; an edit made meanwhile must stay on the take
            BodySolverSetup.Register();
            var take = SyntheticTake.Create();
            var own = take.Edits;
            var partEdits = TakeSession.CloneEdits(own);
            partEdits.TrimIn = 1;
            partEdits.TrimOut = 2;
            var part = take.WithEdits(partEdits);
            Assert.Same(take.Streams, part.Streams);
            Assert.Same(take.Sync, part.Sync);
            Assert.Same(partEdits, part.Edits);

            var options = new BakeOptions { Fps = 60, Calibration = new CalibrationSettings() };
            var whole = PoseBuilder.Bake(take, null, options);
            var userEdit = TakeSession.CloneEdits(own);
            userEdit.TrimIn = 0.5;
            take.Edits = userEdit; // what an edit in the window does while the part solves
            var clip = PoseBuilder.Bake(part, null, options);
            Assert.Same(userEdit, take.Edits);
            Assert.InRange(clip.Frames, 55, 65);
            Assert.True(whole.Frames > 170);
            Assert.Equal(1.0, clip.SourceTime[0], 3);
        }
    }
}
