using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using VRMR.App.Composition;
using VRMR.App.Platform;
using VRMR.App.Services;
using VRMR.App.ViewModels;
using VRMR.App.Viewport;
using VRMR.App.Views;
using Xunit;

namespace VRMR.App.Tests
{
    /// <summary>
    ///     Everything that depends on SteamVR and VRChat, without either: the recorder runs with its real sources, fed by
    ///     generated data. SteamVR devices come through <see cref="OpenVRSource.Feed" />, VRChat's OSC as real UDP packets
    ///     to a private port on this computer, VRChat's log as a file in the sandboxed machine folder. Nothing here touches
    ///     VRChat's or SteamVR's own ports, files or processes.
    /// </summary>
    [Collection("app")]
    public class LiveSourceTests
    {
        // ------------------------------------------------------------------------------------------ status pills

        [Fact]
        public void ThePillsFollowWhatTheSourcesReport()
        {
            using var rig = new Rig();
            var steam = rig.Pill(SourceStatus.SteamVr);
            Assert.Equal("off", steam.StateText);
            Assert.Contains("Settings → Sources", steam.Fix);
            Assert.Equal("waiting", rig.Pill(SourceStatus.VrChat).StateText);
            Assert.Equal("no data", rig.Pill(SourceStatus.Face).StateText);
            Assert.Null(rig.Pill(SourceStatus.Avatar));

            // SteamVR comes up without devices, then with all of them, then one loses tracking
            rig.AttachSteamVr();
            Assert.Equal("waiting", rig.Pill(SourceStatus.SteamVr).StateText);
            rig.Feed();
            steam = rig.Pill(SourceStatus.SteamVr);
            Assert.Equal("8/8 tracked", steam.StateText);
            Assert.Equal(SourceLevel.Ok, steam.Level);
            Assert.Contains("Headset · 2 controllers · 3 trackers · 2 base stations", steam.Detail);
            Assert.Contains("Play area: 3.0 × 2.0 m", steam.Detail);
            Assert.False(steam.HasFix);
            rig.Feed(4);
            steam = rig.Pill(SourceStatus.SteamVr);
            Assert.Equal("7/8 tracked", steam.StateText);
            Assert.Equal(SourceLevel.Warning, steam.Level);
            Assert.Contains("Devices panel", steam.Fix);

            // VRChat's OSC arrives on the private port
            rig.SendFloats("/avatar/parameters/GestureLeftWeight", 0.5f);
            Assert.True(rig.WaitFor(() => rig.Host.Recorder.OscMessages > 0), "the OSC packet did not arrive");
            var vrc = rig.Pill(SourceStatus.VrChat);
            Assert.Equal("receiving", vrc.StateText);
            Assert.Contains("UDP " + rig.OscPort, vrc.Detail);
            Assert.Contains("World: Test World", vrc.Detail);

            // face tracking parameters, and the avatar worn
            rig.SendFloats("/avatar/parameters/FT/v2/JawOpen", 0.7f);
            rig.SendString("/avatar/change", "avtr_test-1");
            rig.SendFloats("/avatar/parameters/EyeHeightAsMeters", 1.5f);
            Assert.True(rig.WaitFor(() =>
                rig.Host.Recorder.AvatarId == "avtr_test-1" && rig.Host.Recorder.AvatarEyeHeight > 1.4f &&
                rig.Host.Recorder.LastFaceTime > 0));
            Assert.Equal("active", rig.Pill(SourceStatus.Face).StateText);
            var avatar = rig.Pill(SourceStatus.Avatar);
            Assert.NotNull(avatar);
            Assert.Contains("Wearing: avtr_test-1", avatar.Detail);
        }

        // ------------------------------------------------------------------------------------------ live view and Devices panel

        [Fact]
        public void TheLiveAvatarFollowsTheHeadsetInRestPoseThenSolvesAfterCalibration()
        {
            using var rig = new Rig();
            rig.AttachSteamVr();
            rig.Feed();
            rig.Host.LivePresenter.Show(rig.View);
            var scene = rig.View.Scene;
            var profile = scene.Profile;
            Assert.NotNull(profile);
            int head = profile.Human[HumanBones.Head];
            var headset = Assert.Single(scene.Markers, m => m.Kind == MarkerKind.Headset).Pose;
            Assert.True((scene.NodePos[head] + profile.ViewPoint - profile.RestWorldPos[head] - headset.pos)
                .Length < 0.001f);

            rig.Host.Recorder.Calib.CaptureNow("test T-pose");
            var devices = Rig.Devices(0);
            devices[0].Pos += new Vec3(0.25f, -0.25f, 0);
            rig.SteamVr.Feed(devices, Rig.PlayArea, Clock.Now);
            Assert.True(rig.WaitFor(() =>
            {
                rig.Host.LivePresenter.Show(rig.View);
                return scene.Overlay.Contains("IK solve");
            }));
            Assert.True(scene.NodePos[head].x > 0.1f);
        }

        [Fact]
        public void TheLiveViewAndTheDevicesPanelShowTheDevices()
        {
            using var rig = new Rig();
            var takes = rig.Shell.Takes;
            Assert.True(takes.IsLive);
            Assert.True(takes.ShowEmpty);
            Assert.Equal("Waiting for VRChat or SteamVR", takes.EmptyTitle);
            Assert.False(rig.View.Animating); // nothing arrives: no frames

            rig.AttachSteamVr();
            rig.Feed();
            rig.Ui.Advance(1.05); // the once-a-second look for a source that came up
            Assert.True(rig.View.Animating);
            Assert.Equal(60, rig.View.FrameCap);
            rig.View.Tick(1 / 60.0);
            var scene = rig.View.Scene;
            Assert.False(takes.ShowEmpty);
            Assert.Equal(1, scene.Markers.Count(m => m.Kind == MarkerKind.Headset));
            Assert.Equal(2, scene.Markers.Count(m => m.Kind == MarkerKind.Controller));
            Assert.Equal(3, scene.Markers.Count(m => m.Kind == MarkerKind.Tracker));
            Assert.Equal(2, scene.Markers.Count(m => m.Kind == MarkerKind.BaseStation));
            Assert.Contains(scene.Markers,
                m => m.Label != null && m.Label.StartsWith("Left hand") && m.Label.Contains("80%") &&
                     m.Tint == MarkerTint.Left);
            Assert.Equal(4, scene.PlayArea.Length);
            Assert.StartsWith("LIVE", scene.Overlay);
            Assert.Contains("SteamVR: Headset · 2 controllers · 3 trackers · 2 base stations", scene.Overlay);
            Assert.True(takes.HasPlayArea);
            Assert.True(takes.HasBaseStations);
            Assert.True(takes.HasHeadset);
            Assert.Equal(1, rig.View.Framed); // the first devices frame the tracking space once

            // a device that loses tracking stays where it was, faded, with the reason under its name
            rig.Feed(3);
            rig.View.Tick(1 / 60.0);
            var hip = Assert.Single(scene.Markers, m => m.Faded);
            Assert.Contains("out of range", hip.Label);
            Assert.Equal(1, rig.View.Framed);

            // VRChat's own head, wrists and camera arrive over OSC
            rig.SendFloats("/tracking/vrsystem/head/pose", 0, 1.5f, 0, 0, 0, 0);
            rig.SendFloats("/usercamera/Pose", 1, 1.2f, 2, 0, 180, 0);
            Assert.True(rig.WaitFor(() => rig.Host.Recorder.VrcHeadTime > 0 && rig.Host.Recorder.VrcCameraTime > 0));
            rig.View.Tick(1 / 60.0);
            Assert.Contains(scene.Markers, m => m.Kind == MarkerKind.VrcPose && m.Label == "VRChat head");
            Assert.Contains(scene.Markers, m => m.Kind == MarkerKind.Camera);

            // the Devices panel (it is the inspector's tab while Live is on)
            Assert.Equal(TakesWorkspaceViewModel.DevicesTab, takes.InspectorTab);
            rig.Feed();
            rig.SendFloats("/avatar/parameters/FT/v2/JawOpen", 0.7f);
            Assert.True(rig.WaitFor(() => rig.Host.Recorder.LastFaceTime > 0));
            rig.Ui.Advance(0.3);
            var panel = takes.Devices;
            Assert.Equal(8, panel.Devices.Count);
            Assert.Equal("8/8 tracked", panel.DevicesSummary);
            Assert.Equal("Play area: 3.0 × 2.0 m", panel.PlayArea);
            Assert.Equal("", panel.DeviceStatus);
            var right = Assert.Single(panel.Devices, d => d.Name == "Right hand");
            Assert.True(right.BatteryLow);
            Assert.Equal("15%", right.BatteryText);
            Assert.True(right.TrackingOk);
            Assert.StartsWith("Base station", panel.Devices[panel.Devices.Count - 1].Name); // base stations last
            Assert.False(panel.NoFace);
            Assert.Contains("UDP " + rig.OscPort, panel.Connections);
            rig.Feed(5);
            rig.Ui.Advance(0.3);
            Assert.Equal("7/8 tracked", panel.DevicesSummary);
            Assert.True(panel.Devices.Single(d => d.Detail.Contains("SIM-FOOT-R")).TrackingDegraded ||
                        panel.Devices.Single(d => d.Detail.Contains("SIM-FOOT-R")).TrackingLost);

            // without a take to go back to, Live stays on
            takes.IsLive = false;
            Assert.True(takes.IsLive);
            Assert.True(rig.View.Animating);
        }

        [Fact]
        public async Task TheWindowShowsTheLiveState()
        {
            await HeadlessUi.RunAsync(async () =>
            {
                using var rig = new Rig(realUi: new AvaloniaUiDispatcher());
                var window = new MainWindow(rig.Shell) { Width = 1400, Height = 860 };
                window.Show();
                try
                {
                    rig.AttachSteamVr();
                    rig.Feed();
                    rig.Feed(4); // tracked once, then lost: it stays in the view, faded
                    rig.SendFloats("/avatar/parameters/FT/v2/JawOpen", 0.7f);
                    rig.SendFloats("/avatar/parameters/FT/v2/MouthSmileLeft", 0.4f);
                    rig.SendString("/avatar/change", "avtr_test-1");
                    Assert.True(await HeadlessUi.WaitForAsync(() =>
                            rig.Shell.Sources.Any(p => p.Id == SourceStatus.SteamVr && p.StateText == "7/8 tracked")
                            && rig.Shell.Sources.Any(p => p.Id == SourceStatus.VrChat && p.StateText == "receiving")
                            && rig.Shell.Sources.Any(p => p.Id == SourceStatus.Avatar), 5000),
                        "the pills did not follow the sources");
                    Assert.True(await HeadlessUi.WaitForAsync(() => rig.Shell.Takes.Devices.Devices.Count == 8, 3000),
                        "the Devices panel did not list the devices");
                    Assert.True(await HeadlessUi.WaitForAsync(() => window.Viewport.Animating, 3000),
                        "the live view did not start when the devices came up"); // looked for once a second
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    await HeadlessUi.SettleAsync(50);
                    Assert.Contains(window.Viewport.Scene.Markers, m => m.Faded);
                    HeadlessUi.Capture(window, "live-devices");

                    rig.Shell.ToggleRecordCommand.Execute(null);
                    for (int i = 0; i < 40; i++)
                    {
                        rig.Feed();
                        await HeadlessUi.SettleAsync(10);
                    }

                    Assert.True(rig.Shell.IsRecording);
                    Assert.StartsWith("Stop", rig.Shell.RecordText);
                    HeadlessUi.Capture(window, "live-recording");
                    rig.Shell.ToggleRecordCommand.Execute(null);
                    Assert.True(
                        await HeadlessUi.WaitForAsync(() => rig.Shell.Library.Items.Count == 1 && rig.Shell.Takes.HasTake, 8000),
                        "the new take did not appear and open");
                    HeadlessUi.Capture(window, "live-recorded");
                }
                finally { window.Close(); }
            });
        }

        // ------------------------------------------------------------------------------------------ recording

        [Fact]
        public void RecordingWritesATakeThatAppearsInTheLibraryOpensAndSolves()
        {
            using var rig = new Rig();
            rig.AttachSteamVr();
            rig.Feed();
            rig.SendFloats("/avatar/parameters/FT/v2/JawOpen", 0.2f);
            Assert.True(rig.WaitFor(() => rig.Host.Recorder.LastFaceTime > 0));
            Assert.True(rig.WaitFor(() => !rig.Host.Library.Loading));
            Assert.Empty(rig.Host.Library.Items);

            rig.Shell.ToggleRecordCommand.Execute(null);
            Assert.True(rig.Shell.IsRecording);
            Assert.StartsWith("Stop", rig.Shell.RecordText);
            Assert.True(rig.View.Options.Recording); // the view goes light: bones only
            Assert.Equal(15, rig.View.FrameCap);
            Assert.True(rig.View.Animating);
            int n = 0;
            rig.FeedFor(1.3, () =>
            {
                if (++n % 6 == 0) rig.SendFloats("/avatar/parameters/FT/v2/JawOpen", n % 60 / 60f);
            });
            rig.View.Tick(1 / 15.0);
            Assert.StartsWith("● REC", rig.View.Scene.Overlay);
            // the file being written is on disk but not offered in the library yet
            Assert.Single(Directory.GetFiles(rig.Data.TakesDir, "*.vrmr"));
            rig.Host.Library.Refresh();
            Thread.Sleep(200);
            rig.Ui.Advance(0.4);
            Assert.Empty(rig.Host.Library.Items);
            rig.Ui.Advance(0.3);
            Assert.NotEqual("Stop  00:00", rig.Shell.RecordText);

            rig.Shell.ToggleRecordCommand.Execute(null);
            Assert.False(rig.Shell.IsRecording);
            Assert.Equal("Record", rig.Shell.RecordText);
            Assert.False(rig.View.Options.Recording);
            Assert.Contains("Recorded Rec ", rig.Host.Status.Message);

            // on disk, in the library, and open in the Takes workspace
            string file = Assert.Single(Directory.GetFiles(rig.Data.TakesDir, "*.vrmr"));
            Assert.True(rig.WaitFor(() => rig.Host.Library.Items.Count == 1), "the library did not list the new take");
            var info = rig.Host.Library.Items[0];
            Assert.Equal(file, info.Path);
            Assert.True(info.HasDevices);
            Assert.True(info.HasFace);
            Assert.InRange(info.Duration, 1.2, 3.0);
            Assert.True(rig.WaitFor(() => rig.Host.Session.Take != null));
            var take = rig.Host.Session.Take;
            Assert.Equal(file, take.FilePath);
            Assert.Equal(6,
                take.Streams.Count(s => s.Kind == StreamKind.OpenVRDevice)); // base stations are not recorded
            Assert.Contains(take.Params, p => p.Name == "FT/v2/JawOpen" && p.Count > 3);
            Assert.True(take.Extra.ContainsKey(SteamVRRoom.Key)); // the play area and base stations for replays
            Assert.False(rig.Shell.Takes.IsLive);

            // it solves onto the built-in mannequin (no avatar was worn)
            Assert.True(rig.WaitFor(
                () => rig.Host.Session.Solve == SolveState.Done || rig.Host.Session.Solve == SolveState.Failed, 30000));
            Assert.True(rig.Host.Session.Solve == SolveState.Done, "solve failed: " + rig.Host.Session.SolveError);
            var clip = rig.Host.Session.Clip;
            Assert.Equal("ik", clip.BodySource);
            Assert.True(clip.Frames > 60);
            Assert.NotNull(rig.View.Scene.Profile);
            Assert.Contains("IK solve", rig.View.Scene.Overlay);
        }

        [Fact]
        public void TheHotkeyAndTheVrchatParameterStartAndStopRecording()
        {
            using var rig = new Rig();
            rig.AttachSteamVr();
            rig.Feed();
            Assert.NotNull(rig.Hotkey.Pressed);
            Assert.Equal(0x78u, rig.Hotkey.Key); // F9, the default
            Assert.Contains("F9", rig.Shell.RecordTip);

            rig.Hotkey.Pressed(); // arrives on the hotkey's own thread in the app: handled on the UI thread
            Assert.False(rig.Shell.IsRecording);
            rig.Ui.RunPosted();
            Assert.True(rig.Shell.IsRecording);
            rig.FeedFor(0.3);
            rig.Hotkey.Pressed();
            rig.Ui.RunPosted();
            Assert.False(rig.Shell.IsRecording);
            Assert.Single(Directory.GetFiles(rig.Data.TakesDir, "*.vrmr"));

            // the avatar parameter set as the trigger (a toggle in the avatar's menu): every change of it toggles
            rig.SendBool("/avatar/parameters/Test/Record", true);
            Assert.True(rig.WaitFor(() => rig.Shell.IsRecording), "the parameter did not start a recording");
            rig.FeedFor(0.3); // started within the same second as the first take: it still gets a file of its own
            rig.SendBool("/avatar/parameters/Test/Record", false);
            Assert.True(rig.WaitFor(() => !rig.Shell.IsRecording), "the parameter did not stop the recording");
            Assert.Equal(2, Directory.GetFiles(rig.Data.TakesDir, "*.vrmr").Length);

            // other parameters, and the trigger sent as a number, do nothing
            rig.SendBool("/avatar/parameters/Something/Else", true);
            rig.SendFloats("/avatar/parameters/Test/Record", 1f);
            long seen = rig.Host.Recorder.OscMessages;
            Assert.True(rig.WaitFor(() => rig.Host.Recorder.OscMessages >= seen));
            Thread.Sleep(150);
            rig.Ui.RunPosted();
            Assert.False(rig.Shell.IsRecording);

            // a new hotkey replaces the old one
            rig.Host.SettingsStore.Update(nameof(Settings.HotkeyRecord), s => s.HotkeyRecord = "Ctrl+Shift+R");
            Assert.Equal('R', rig.Hotkey.Key);
            Assert.Equal(HotkeyText.Ctrl | HotkeyText.Shift, rig.Hotkey.Modifiers);
            Assert.Contains("Ctrl+Shift+R", rig.Shell.RecordTip);
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 3)]
        [InlineData(3, 5)]
        public void HotkeyAndButtonShareTheRecordingCountdown(int index, int seconds)
        {
            using var rig = new Rig();
            rig.Shell.RecordDelayIndex = index;
            rig.Shell.ToggleRecordCommand.Execute(null);
            Assert.Equal($"Record {seconds} s", rig.Shell.RecordText);
            rig.Hotkey.Pressed();
            rig.Ui.RunPosted();
            rig.Ui.Advance(seconds + 1);
            Assert.False(rig.Shell.IsRecording);
            Assert.Equal("Record", rig.Shell.RecordText);

            rig.Hotkey.Pressed();
            rig.Ui.RunPosted();
            for (int i = 0; i < seconds; i++)
            {
                Assert.False(rig.Shell.IsRecording);
                rig.Ui.Advance(1);
            }

            Assert.True(rig.Shell.IsRecording);
            rig.Shell.ToggleRecordCommand.Execute(null);
            Assert.False(rig.Shell.IsRecording);
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 3)]
        [InlineData(3, 5)]
        public void ManualCalibrationUsesItsOwnSavedDelay(int index, int seconds)
        {
            using var rig = new Rig(s => s["watchCalibrations"] = false);
            rig.Shell.RecordDelayIndex = 2;
            rig.Shell.CalibrationDelayIndex = 0;
            Assert.Equal("Watcher disabled", rig.Shell.LiveCalibrationText);
            Assert.False(rig.Shell.CanCalibrate);
            rig.Shell.CalibrationDelayIndex = index;
            rig.Host.SettingsStore.Flush();
            var settings = Settings.Load(rig.Host.Options.Paths);
            Assert.Equal(3, settings.RecordCountdownSeconds);
            Assert.Equal(seconds, settings.CalibrationCountdownSeconds);
            rig.Shell.CalibrateLiveCommand.Execute(null);
            Assert.Equal("—", rig.Shell.CalibrationAge);
            rig.AttachSteamVr();
            rig.Feed();
            rig.Shell.CalibrateLiveCommand.Execute(null);
            for (int i = seconds; i > 0; i--)
            {
                Assert.Equal($"Calibrate {i} s", rig.Shell.LiveCalibrationText);
                Assert.Equal("—", rig.Shell.CalibrationAge);
                rig.Ui.Advance(1);
            }

            Assert.True(rig.WaitFor(() => rig.Shell.CalibrationAge == "0 min"));
            var saved = Assert.Single(rig.Host.Recorder.Calibrations.List());
            Assert.Equal("0 min", ShellViewModel.CalibrationAgeText(saved.CreatedUtc, saved.CreatedUtc.AddSeconds(59)));
            Assert.Equal("1 min", ShellViewModel.CalibrationAgeText(saved.CreatedUtc, saved.CreatedUtc.AddMinutes(1)));
        }

        [Fact]
        public void LiveAgeRestoresTheLastVrchatCalibrationAndSurvivesFailedCapture()
        {
            using var data = new ScratchData();
            var ui = new ManualDispatcher();
            var library = new CalibrationLibrary(data.Options.Paths);
            var timestamp = DateTime.UtcNow.AddMinutes(-4);
            var snapshot = new CalibrationSnapshot { CreatedUtc = timestamp, Source = "vrchat" };
            snapshot.Devices.Add(new CalibrationSnapshot.Device { Class = 1, Serial = "SIM-HMD" });
            library.Save(snapshot);
            var fromTake = snapshot.Clone();
            fromTake.Id = "";
            fromTake.Source = "take";
            fromTake.CreatedUtc = DateTime.UtcNow;
            library.Save(fromTake);
            using var host = data.NewHost(ui);
            var shell = new ShellViewModel(host);
            Assert.True(ui.WaitFor(() => shell.CalibrationAge == "4 min"));
            shell.CalibrateLiveCommand.Execute(null);
            Assert.Equal("4 min", shell.CalibrationAge);
        }

        [Fact]
        public void SyncDisplaysTheWatcherStatusWithoutManualCapture()
        {
            using var rig = new Rig();
            rig.Shell.CalibrationDelayIndex = 0;
            Assert.Equal("SteamVR offline", rig.Shell.LiveCalibrationText);
            rig.AttachSteamVr();
            rig.Feed();
            rig.Ui.Advance(1);
            Assert.Equal(rig.Host.Recorder.Calib.Status, rig.Shell.LiveCalibrationText);
            Assert.False(rig.Shell.CanCalibrate);
            Assert.Equal(0, rig.Host.Calibration.CaptureCountdown);
        }

        [Fact]
        public void AHotkeyThatIsTakenIsSaidSoOnTheRecordButton()
        {
            using var rig = new Rig(hotkeyTaken: true);
            Assert.Contains("already used by another program", rig.Host.Recording.HotkeyProblem);
            Assert.Contains("already used by another program", rig.Shell.RecordTip);
            rig.Shell.ToggleRecordCommand.Execute(null); // the button still works
            Assert.True(rig.Shell.IsRecording);
            rig.Shell.ToggleRecordCommand.Execute(null);
            Assert.False(rig.Shell.IsRecording);
        }

        [Fact]
        public void AnInWorldRecorderStartsAndStopsTheRecordingWhenFollowingIsOn()
        {
            using var rig = new Rig(s => s["followHumrTakes"] = true);
            Assert.True(rig.WaitFor(() => rig.Host.Recorder.Log.LocalUser == Rig.User), "the log was not read");
            Assert.False(rig.Shell.IsRecording);
            Assert.False(rig.Host.Recording
                .WatchingInWorldTake); // following the log costs no timer until a take arrives

            // VRChat's own IK arrives in the log: the recording starts with it
            var sw = Stopwatch.StartNew();
            int frame = 0;
            bool started = false;
            while (sw.Elapsed.TotalSeconds < 6 && !(started && sw.Elapsed.TotalSeconds > 1.0))
            {
                rig.Log(Rig.HumrLine(7, frame++ / 60.0));
                Thread.Sleep(15);
                rig.Ui.Advance(0.015);
                started |= rig.Shell.IsRecording;
            }

            Assert.True(started, "the in-world recorder did not start a recording");
            Assert.True(rig.Host.Recording.VrcIkFrames > 5);
            // the log shows that VRChat runs although no OSC arrives: the pill says what to switch on
            var vrc = rig.Pill(SourceStatus.VrChat);
            Assert.Equal("no OSC", vrc.StateText);
            Assert.Contains("HUMR", vrc.Detail);
            Assert.Contains("OSC → Enabled", vrc.Fix);

            // the lines stop: the recording stops by itself and keeps VRChat's IK as its body
            var end = Stopwatch.StartNew();
            while (end.Elapsed.TotalSeconds < 6 && rig.Shell.IsRecording)
            {
                Thread.Sleep(50);
                rig.Ui.Advance(0.3);
            }

            Assert.False(rig.Shell.IsRecording);
            rig.Ui.Advance(0.3);
            Assert.False(rig.Host.Recording.WatchingInWorldTake);
            Assert.True(rig.WaitFor(() => rig.Host.Session.Take != null));
            Assert.True(rig.Host.Session.Take.Humr.Count > 5);
            Assert.Equal("Test World", rig.Host.Session.Take.WorldName);
        }

        [Fact]
        public void WithoutFollowingAnInWorldRecorderDoesNotStartARecording()
        {
            using var rig = new Rig();
            Assert.True(rig.WaitFor(() => rig.Host.Recorder.Log.LocalUser == Rig.User));
            for (int i = 0; i < 30; i++)
            {
                rig.Log(Rig.HumrLine(3, i / 60.0));
                Thread.Sleep(10);
                rig.Ui.Advance(0.3);
            }

            Assert.True(rig.WaitFor(() => rig.Host.Recorder.Log.HumrFrames > 0));
            rig.Ui.Advance(0.3);
            Assert.False(rig.Shell.IsRecording);
        }

        // ------------------------------------------------------------------------------------------ calibration capture

        [Fact]
        public void CaptureNowSavesTheDevicesAsACalibrationAfterACountdown()
        {
            using var rig = new Rig();
            var calibration = rig.Host.Calibration;
            Assert.Contains("SteamVR is not connected", calibration.CaptureProblem());
            calibration.CaptureNow();
            Assert.Equal(0, calibration.CaptureCountdown);
            Assert.Contains("SteamVR is not connected", rig.Host.Status.Message);

            rig.AttachSteamVr();
            rig.Feed();
            Assert.Null(calibration.CaptureProblem());
            int before = rig.Host.Recorder.Calibrations.List().Count;
            calibration.CaptureNow();
            Assert.Equal(5, calibration.CaptureCountdown);
            for (int i = 4; i >= 1; i--)
            {
                rig.Feed();
                rig.Ui.Advance(1);
                Assert.Equal(i, calibration.CaptureCountdown);
            }

            rig.Feed();
            rig.Ui.Advance(1);
            Assert.Equal(0, calibration.CaptureCountdown);
            Assert.Contains("Saved the calibration", rig.Host.Status.Message);
            var saved = rig.Host.Recorder.Calibrations.List();
            Assert.Equal(before + 1, saved.Count);
            Assert.StartsWith(Path.GetFullPath(rig.Data.Root),
                Path.GetFullPath(rig.Host.Recorder.Calibrations.Dir)); // in the scratch folder, not the user's
        }

        // ------------------------------------------------------------------------------------------ face replay

        [Fact]
        public void FaceReplaySendsTheTakesFaceValuesWhilePlayingAndOnlyWhenSwitchedOn()
        {
            using var rig = new Rig();
            SyntheticTake.Save(rig.Data.TakesDir, 3, "Face take");
            rig.Host.Library.Refresh();
            Assert.True(rig.WaitFor(() => rig.Host.Library.Items.Count == 1));
            _ = rig.Host.Session.OpenAsync(rig.Host.Library.Items[0].Path);
            Assert.True(rig.WaitFor(() => rig.Host.Session.Solve == SolveState.Done, 30000));
            var takes = rig.Shell.Takes;

            takes.TogglePlayCommand.Execute(null);
            for (int i = 0; i < 20; i++) rig.View.Tick(1 / 60.0);
            Assert.Empty(rig.Sender.Sent); // off unless asked for

            Assert.True(takes.Inspector.ReplayAvailable);
            takes.Inspector.ReplayFace = true;
            for (int i = 0; i < 30; i++) rig.View.Tick(1 / 60.0);
            Assert.NotEmpty(rig.Sender.Sent);
            Assert.All(rig.Sender.Sent, s => Assert.Equal("FT/v2/JawOpen", s.name)); // face parameters only
            Assert.True(rig.Sender.Sent.Count <= 30); // one value per frame at most
            Assert.True(rig.Sender.Sent.Select(s => s.value).Distinct().Count() > 3);

            // paused: nothing more; switched off: the sender is closed
            takes.TogglePlayCommand.Execute(null);
            int sent = rig.Sender.Sent.Count;
            rig.View.Tick(1 / 60.0);
            Assert.Equal(sent, rig.Sender.Sent.Count);
            takes.Inspector.ReplayFace = false;
            Assert.True(rig.Sender.Disposed);
        }
        // ------------------------------------------------------------------------------------------ the rig

        sealed class FakeHotkey : IGlobalHotkey
        {
            public uint Modifiers, Key;
            public Action Pressed;
            public bool Taken;

            public bool Register(uint modifiers, uint virtualKey, Action pressed)
            {
                if (Taken) return false;
                Modifiers = modifiers;
                Key = virtualKey;
                Pressed = pressed;
                return true;
            }

            public void Unregister()
            {
                Pressed = null;
            }

            public void Dispose()
            {
                Pressed = null;
            }
        }

        sealed class FakeSender : IParameterSender
        {
            public readonly List<(string name, float value)> Sent = new();
            public bool Disposed;

            public void Send(string name, ParamType type, float value)
            {
                Sent.Add((name, value));
            }

            public void Dispose()
            {
                Disposed = true;
            }
        }

        /// <summary>A 3D view that only keeps what it is given.</summary>
        sealed class FakeView : IViewport
        {
            public int Changes, Framed;
            public ViewScene Scene { get; } = new();
            public ViewOptions Options { get; } = new();

            public void SceneChanged()
            {
                Changes++;
            }

            public void FrameAll() { }

            public Render.ViewPreset View => Render.ViewPreset.Perspective;

            public void SetView(Render.ViewPreset view) { }

            public event Action ViewChanged
            {
                add { }
                remove { }
            }

            public bool FrameDevices()
            {
                Framed++;
                return true;
            }

            public IViewportTool Tool { get; set; }

            public bool Project(Vec3 world, out Point screen)
            {
                screen = default;
                return false;
            }

            public void Unproject(Point screen, out Vec3 origin, out Vec3 direction)
            {
                origin = default;
                direction = Vec3.Forward;
            }

            public bool Animating { get; set; }
            public double FrameCap { get; set; } = 60;
            public event Action<TimeSpan> FrameTick;
            public long FramesDrawn => 0;

            public void Tick(double seconds)
            {
                FrameTick?.Invoke(TimeSpan.FromSeconds(seconds));
            }
        }

        sealed class Rig : IDisposable
        {
            public const string User = "Tester";
            const string Stamp = "2026.01.01 10:00:00 Debug      -  ";

            public static readonly Vec3[] PlayArea =
                { new(-1, 0, -1.5f), new(1, 0, -1.5f), new(1, 0, 1.5f), new(-1, 0, 1.5f) };

            public readonly ScratchData Data = new();
            public readonly AppHost Host;
            public readonly FakeHotkey Hotkey = new();
            public readonly string LogFile;
            public readonly int OscPort;
            public readonly FakeSender Sender = new();
            public readonly ShellViewModel Shell;
            public readonly OpenVRSource SteamVr = new(90);
            public readonly ManualDispatcher Ui = new();
            public readonly FakeView View = new();
            readonly UdpClient _udp = new();
            double _feedTime;

            /// <param name="realUi">
            ///     The window's own dispatcher (headless window tests) instead of the manual one; the caller attaches
            ///     the view.
            /// </param>
            public Rig(Action<Dictionary<string, object>> settings = null, bool hotkeyTaken = false,
                IUiDispatcher realUi = null)
            {
                Hotkey.Taken = hotkeyTaken;
                using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                    OscPort = ((IPEndPoint)probe.Client.LocalEndPoint).Port;
                // the sources as a test may run them: OSC on a private port only, no OSCQuery advertising, no SteamVR, the log in the sandbox
                var s = new Dictionary<string, object>
                {
                    ["useOscQuery"] = false, ["udpFallback"] = true, ["legacyPort"] = OscPort,
                    ["enableOpenVR"] = false,
                    ["enableHumr"] = true, ["watchCalibrations"] = true, ["recordTriggerParam"] = "Test/Record"
                };
                settings?.Invoke(s);
                var paths = AppPaths.ForDataRoot(Data.Root);
                Directory.CreateDirectory(paths.AppDataDir);
                File.WriteAllText(paths.SettingsFile, Json.Serialize(s, true));
                string logDir = Path.Combine(paths.Machine.Folder(KnownFolder.LocalAppDataLow), "VRChat", "VRChat");
                Directory.CreateDirectory(logDir);
                LogFile = Path.Combine(logDir, "output_log_2026-01-01_10-00-00.txt");
                File.WriteAllText(LogFile,
                    Stamp + "User Authenticated: " + User + " (usr_1)\n" + Stamp +
                    "[Behaviour] Entering Room: Test World\n");

                Host = new AppHost(AppOptions.Parse(new[] { "--data", Data.Root }), realUi ?? Ui, Hotkey, () => Sender);
                Assert.Equal(logDir, VrcPaths.LogDir);
                Assert.Null(Host.Recorder.OpenVR); // SteamVR itself is never asked
                Assert.Null(Host.Recorder.OscQuery); // nothing is advertised on the network
                Shell = new ShellViewModel(Host);
                if (realUi == null) Shell.Takes.AttachView(View);
            }

            public void Dispose()
            {
                _udp.Dispose();
                Host.Dispose();
                SteamVr.Dispose();
                Data.Dispose();
            }

            /// <summary>Connects the generated SteamVR devices.</summary>
            public void AttachSteamVr()
            {
                Host.Recorder.AttachOpenVR(SteamVr);
            }

            /// <summary>A headset, two controllers, three trackers and two base stations around a person standing with the arms out.</summary>
            public static List<OpenVRSource.Device> Devices(double t, int lost = -1)
            {
                float sway = (float)Math.Sin(t * 2) * 0.03f;

                OpenVRSource.Device D(int index, int cls, int role, string serial, Vec3 pos, float battery = -1)
                {
                    return new OpenVRSource.Device
                    {
                        Index = index, Class = cls, Role = role, Serial = serial, Model = "Test device",
                        Pos = pos + new Vec3(sway, 0, 0), Rot = Quat.Identity,
                        Valid = index != lost, Result = index == lost ? 201 : 200, Battery = battery
                    };
                }

                var list = new List<OpenVRSource.Device>
                {
                    D(0, 1, 0, "SIM-HMD", new Vec3(0, 1.60f, 0)),
                    D(1, 2, 1, "SIM-LEFT", new Vec3(-0.70f, 1.40f, 0), 0.8f),
                    D(2, 2, 2, "SIM-RIGHT", new Vec3(0.70f, 1.40f, 0), 0.15f),
                    D(3, 3, 0, "SIM-HIP", new Vec3(0, 0.95f, -0.08f), 0.6f),
                    D(4, 3, 0, "SIM-FOOT-L", new Vec3(-0.10f, 0.08f, 0), 0.5f),
                    D(5, 3, 0, "SIM-FOOT-R", new Vec3(0.10f, 0.08f, 0), 0.5f),
                    D(6, 4, 0, "SIM-BASE-1", new Vec3(-2, 2.2f, -2)),
                    D(7, 4, 0, "SIM-BASE-2", new Vec3(2, 2.2f, 2))
                };
                list[6].Pos = new Vec3(-2, 2.2f, -2);
                list[7].Pos = new Vec3(2, 2.2f, 2); // base stations do not sway
                return list;
            }

            /// <summary>One poll of the generated devices.</summary>
            public void Feed(int lost = -1)
            {
                _feedTime += 1.0 / 90;
                SteamVr.Feed(Devices(_feedTime, lost), PlayArea, Clock.Now);
            }

            /// <summary>Feeds devices in real time for a while (a recording's clock is the real one).</summary>
            public void FeedFor(double seconds, Action everyPoll = null)
            {
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    Feed();
                    everyPoll?.Invoke();
                    Thread.Sleep(8);
                }
            }

            // ---- OSC as VRChat sends it

            static void Pad(List<byte> b)
            {
                do b.Add(0);
                while (b.Count % 4 != 0);
            }

            static void Str(List<byte> b, string s)
            {
                b.AddRange(Encoding.ASCII.GetBytes(s));
                Pad(b);
            }

            static void Float(List<byte> b, float v)
            {
                byte[] x = BitConverter.GetBytes(v);
                if (BitConverter.IsLittleEndian) Array.Reverse(x);
                b.AddRange(x);
            }

            void Send(List<byte> packet)
            {
                _udp.Send(packet.ToArray(), packet.Count, new IPEndPoint(IPAddress.Loopback, OscPort));
            }

            public void SendFloats(string address, params float[] values)
            {
                var b = new List<byte>();
                Str(b, address);
                Str(b, "," + new string('f', values.Length));
                foreach (float v in values) Float(b, v);
                Send(b);
            }

            public void SendBool(string address, bool value)
            {
                var b = new List<byte>();
                Str(b, address);
                Str(b, value ? ",T" : ",F");
                Send(b);
            }

            public void SendString(string address, string value)
            {
                var b = new List<byte>();
                Str(b, address);
                Str(b, ",s");
                Str(b, value);
                Send(b);
            }

            /// <summary>Appends lines to VRChat's log as VRChat does.</summary>
            public void Log(string text)
            {
                using var fs = new FileStream(LogFile, FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                fs.Write(bytes, 0, bytes.Length);
            }

            public static string HumrLine(long take, double time)
            {
                var sb = new StringBuilder(Stamp + "[HUMR] RECORDING;BoneRotations;" + User + ";" + take + ";" +
                                           time.ToString("R", CultureInfo.InvariantCulture) + ";0,0.95,0");
                for (int b = 0; b < HumanBones.Count; b++) sb.Append(";0,0,0,1");
                return sb.Append("\r\n").ToString();
            }

            public SourcePillViewModel Pill(string id)
            {
                Ui.Advance(1.05); // the pills refresh once a second
                return Shell.Sources.FirstOrDefault(p => p.Id == id);
            }

            public bool WaitFor(Func<bool> condition, int ms = 10000)
            {
                return Ui.WaitFor(condition, ms);
            }
        }
    }
}
