using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using VRMR.App.Composition;
using VRMR.App.Controls;
using VRMR.App.Platform;
using VRMR.App.Services;
using VRMR.App.ViewModels;
using VRMR.App.Viewport;
using VRMR.App.Views;
using Xunit;
using VisualExtensions = Avalonia.VisualTree.VisualExtensions;

namespace VRMR.App.Tests
{
    using Key = Avalonia.Input.Key;

    /// <summary>
    ///     The main flows through the real window, drawn off screen: open, play, trim with the mouse, undo, change a
    ///     calibration setting.
    /// </summary>
    [Collection("app")]
    public class FlowTests
    {
        /// <summary>The window on a scratch data folder with one made-up take, opened and solved.</summary>
        static async Task<Session> OpenTake(double seconds = 4, double width = 1400, bool trackers = false,
            Action<Session> beforeOpen = null)
        {
            var s = new Session { Data = new ScratchData() };
            SyntheticTake.Save(s.Data.TakesDir, seconds, "Flow take", trackers);
            s.Host = s.Data.NewHost(new AvaloniaUiDispatcher());
            s.Shell = new ShellViewModel(s.Host);
            s.Window = new MainWindow(s.Shell) { Width = width, Height = 860 };
            s.Window.Show();
            Assert.True(await HeadlessUi.WaitForAsync(() => s.Shell.Library.Items.Count == 1),
                "the library did not list the take");
            beforeOpen?.Invoke(s);
            s.Shell.Library.Selected = s.Shell.Library.Items[0];
            await WaitSolved(s);
            return s;
        }

        static async Task WaitSolved(Session s)
        {
            Assert.True(
                await HeadlessUi.WaitForAsync(() => s.Host.Session.Solve == SolveState.Done && s.Host.Session.ClipIsCurrent,
                    30000), "the take did not solve: " + s.Host.Session.SolveError);
            await HeadlessUi.SettleAsync(50);
        }

        static Point OnTimeline(Session s, double time, double y = 60)
        {
            var tl = s.Window.TimelineView;
            var m = tl.Model;
            double left = 96, right = tl.Bounds.Width - 10;
            double x = left + (time - m.ViewStart) / (m.ViewEnd - m.ViewStart) * (right - left);
            return tl.TranslatePoint(new Point(x, y), s.Window).Value;
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task SmoothingReleasesFocusOnBlankClickAndFinishesDragsWithKeys(bool releaseBeforeKey)
        {
            await HeadlessUi.RunAsync(async () =>
            {
                using var s = await OpenTake();
                var field = VisualExtensions.GetVisualDescendants(s.Window).OfType<NumberField>()
                    .First(f => f.IsEffectivelyVisible && f.Step == 0.05);
                var start = field.TranslatePoint(new Point(2, field.Bounds.Height / 2), s.Window).Value;
                var blank = start + new Point(field.Bounds.Width + 40, 0);
                var end = start + new Point(12, 0);

                s.Window.MouseDown(start, MouseButton.Left);
                s.Window.MouseUp(start, MouseButton.Left);
                s.Window.KeyTextInput("0.40");
                Assert.True(field.IsFocused);
                Assert.Equal("0.40", field.Text);
                s.Window.MouseDown(blank, MouseButton.Left);
                s.Window.MouseUp(blank, MouseButton.Left);
                Assert.False(field.IsFocused);
                Assert.Equal(0.40, field.Value);

                var left = start - new Point(6, 0);
                s.Window.MouseDown(start, MouseButton.Left);
                s.Window.MouseMove(left);
                s.Window.MouseUp(left, MouseButton.Left);
                Assert.True(field.IsFocused);
                Assert.Equal(0.30, field.Value);
                s.Window.MouseDown(blank, MouseButton.Left);
                s.Window.MouseUp(blank, MouseButton.Left);
                Assert.False(field.IsFocused);
                Assert.Equal(0.30, field.Value);

                foreach (var key in new[] { Key.Enter, Key.Escape })
                {
                    double before = field.Value;
                    s.Window.MouseDown(start, MouseButton.Left);
                    s.Window.MouseMove(end);
                    Assert.True(field.IsFocused);
                    Assert.True(field.Value > before);
                    double dragged = field.Value;
                    if (releaseBeforeKey) s.Window.MouseUp(end, MouseButton.Left);

                    s.Window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
                    Assert.False(field.IsFocused);
                    if (!releaseBeforeKey)
                    {
                        s.Window.MouseMove(end + new Point(12, 0));
                        s.Window.MouseUp(end, MouseButton.Left);
                    }

                    double expected = key == Key.Escape ? before : dragged;
                    Assert.Equal(expected, field.Value);
                    Assert.Equal(expected, s.Shell.Takes.Inspector.Smoothing);
                }
            });
        }

        [Fact]
        public async Task LiveClosesTheTakeAndClickingATakeOpensItAgain()
        {
            await HeadlessUi.RunAsync(async () =>
            {
                using var s = await OpenTake();
                var takes = s.Shell.Takes;
                var library = s.Shell.Library;
                var record = s.Window.FindControl<Button>("RecordButton");
                var recordDelay = s.Window.FindControl<ComboBox>("RecordDelay");
                var calibrationDelay = s.Window.FindControl<ComboBox>("CalibrationDelay");
                Assert.False(record.IsEffectivelyVisible);

                await takes.ToggleLiveCommand.ExecuteAsync(null);
                Assert.True(takes.IsLive);
                Assert.Null(s.Host.Session.Take);
                Assert.True(record.IsEffectivelyVisible);
                Assert.Equal(0, recordDelay.SelectedIndex);
                Assert.Equal(3, calibrationDelay.SelectedIndex);
                recordDelay.SelectedIndex = 2;
                calibrationDelay.SelectedIndex = 0;
                Assert.Equal(3, s.Host.Settings.RecordCountdownSeconds);
                Assert.Equal("Live sources off", s.Shell.LiveCalibrationText);
                Assert.False(takes.HasTake);
                Assert.Null(library.Selected);
                Assert.Null(s.Window.Viewport.Scene.Profile); // nothing of the take stays in the view

                library.Selected = library.Items[0];
                await WaitSolved(s);
                Assert.False(takes.IsLive);
                Assert.NotNull(s.Window.Viewport.Scene.Profile);

                // Live on and off again comes back to the last take
                await takes.ToggleLiveCommand.ExecuteAsync(null);
                Assert.True(takes.IsLive);
                await takes.ToggleLiveCommand.ExecuteAsync(null);
                Assert.False(takes.IsLive);
                Assert.Same(library.Items[0], library.Selected);
            });
        }

        [Fact]
        public async Task OpeningATakeShowsItAndLeavesLive()
        {
            await HeadlessUi.RunAsync(async () =>
            {
                bool previewShown = false, meshBeforeBakeProgress = false, sawBakeProgress = false;
                using var s = await OpenTake(beforeOpen: opening =>
                {
                    var session = opening.Host.Session;
                    var view = Assert.IsType<GpuView>(opening.Window.Viewport);
                    session.PreviewClipReady += () =>
                    {
                        previewShown = session.Clip == null && session.Solve == SolveState.Solving &&
                            view.Scene.Profile != null && !opening.Shell.Takes.ShowEmpty && !opening.Shell.Takes.CanPlay;
                    };
                    session.SolveChanged += () =>
                    {
                        if (sawBakeProgress || session.Solve != SolveState.Solving || session.SolveProgress <= 0)
                            return;
                        sawBakeProgress = true;
                        meshBeforeBakeProgress = view.Frame.Actors.Count == 1 && view.Frame.Actors[0].Model != null;
                    };
                });
                Assert.True(previewShown);
                Assert.True(meshBeforeBakeProgress);
                var takes = s.Shell.Takes;
                Assert.True(takes.HasTake);
                Assert.True(takes.CanPlay);
                Assert.False(takes.IsLive);
                Assert.False(takes.ShowEmpty);
                Assert.Equal(TakesWorkspaceViewModel.TakeTab, takes.InspectorTab);
                Assert.NotNull(s.Window.Viewport.Scene.Profile);
                Assert.Equal("Flow take", takes.Inspector.Name);
                Assert.Contains("IK solve", takes.SolveText);
                Assert.True(s.Shell.Library.Items[0].IsOpen);
                HeadlessUi.Capture(s.Window, "flow-open");
            });
        }

        [Fact]
        public async Task TheGpuViewShowsWhatTheSceneHolds()
        {
            await HeadlessUi.RunAsync(async () =>
            {
                using var s = await OpenTake();
                var view = Assert.IsType<GpuView>(s.Window.Viewport);
                var scene = view.Scene;
                Assert.Single(view.Frame.Actors);
                var actor = view.Frame.Actors[0];
                Assert.Same(scene.Profile, actor.Profile);
                Assert.Equal(scene.NodePos, actor.NodePos);
                Assert.Equal(scene.Markers.Count, view.Frame.Markers.Count);
                Assert.Equal(0, view.Frame.Overlay.Length);

                // the avatar's mesh arrives from a background load (here the built-in mannequin's) and the frame is refilled with it
                Assert.True(
                    await HeadlessUi.WaitForAsync(() => view.Frame.Actors.Count == 1 && view.Frame.Actors[0].Model != null, 10000),
                    "the mesh did not load");
                Assert.True(view.Frame.Actors[0].Model.TriangleCount > 100);
                Assert.True(view.ShowMesh);

                // the chips reach the view
                s.Shell.Takes.ShowMesh = false;
                Assert.False(view.ShowMesh);
                Assert.Null(view.Frame.Actors[0].Model);
                s.Shell.Takes.ShowMesh = true;
                Assert.NotNull(view.Frame.Actors[0].Model);
                s.Shell.Takes.ShowDevices = false;
                Assert.False(view.ShowDevices);

                // the placement tool gets the view's gizmo; the camera projects for it between draws
                s.Shell.Takes.SetGizmo(true, false);
                Assert.NotNull(view.Tool);
                Assert.True(view.Project(s.Shell.Takes.PlacementTool.Gizmo.Center, out var onScreen));
                Assert.InRange(onScreen.X, 0, view.Bounds.Width);
                Assert.InRange(onScreen.Y, 0, view.Bounds.Height);
                s.Shell.Takes.SetGizmo(false);
                Assert.Null(view.Tool);
            });
        }

        [Fact]
        public async Task TheCalibrationViewCentersTheTPoseAndItsGizmoMovesATracker()
        {
            await HeadlessUi.RunAsync(async () =>
            {
                using var s = await OpenTake(trackers: true);
                var takes = s.Shell.Takes;
                var view = Assert.IsType<GpuView>(s.Window.Viewport);
                var presenter = s.Host.TakePresenter;
                takes.ShowCalibrationPose = true;
                await HeadlessUi.SettleAsync(50);
                Assert.Same(takes.CalibrationTool, ((IViewport)view).Tool);
                Assert.Equal("Calibration pose", view.Scene.Overlay);
                var cal = presenter.ShownCalibration;
                Assert.NotNull(cal);

                // the T-pose stands at the origin, facing ahead
                var p = view.Scene.Profile;
                var hips = view.Scene.NodePos[p.Human[HumanBones.Hips]];
                Assert.InRange(Math.Abs(hips.x) + Math.Abs(hips.z), 0f, 0.05f);
                Assert.True(view.Scene.NodePos[p.Human[HumanBones.LeftHand]].x < -0.3f, "left hand on the left");
                var rows = takes.Inspector.NudgeRows.Select(r => r.Role).ToList();
                Assert.Equal(new[] { "Hips", "LeftFoot", "RightFoot" }, rows);

                // a click on the tracker picks it
                Assert.True(presenter.CalibrationDevice("LeftFoot", out var foot));
                Assert.True(view.Project(foot, out var onView));
                var at = view.TranslatePoint(onView, s.Window).Value;
                s.Window.MouseDown(at, MouseButton.Left);
                s.Window.MouseUp(at, MouseButton.Left);
                Assert.Equal("LeftFoot", takes.CalibrationTool.Selected);
                Assert.True(takes.Inspector.NudgeRows.Single(r => r.Role == "LeftFoot").Selected);
                Assert.NotNull(takes.CalibrationTool.Gizmo);

                // a drag is one edit of this calibration; mirroring puts the right foot at the left one's mirror image
                var tool = takes.CalibrationTool;
                Assert.True(tool.Mirror);
                tool.DragStarted();
                tool.Dragged(new Vec3(0.01f, 0.02f, 0.03f), Quat.Identity, 0);
                Assert.True(presenter.CalibrationDevice("RightFoot", out var preview));
                Assert.True(presenter.CalibrationDevice("LeftFoot", out var dragged));
                Assert.InRange((preview - CalibrationTool.Mirrored(dragged)).Length, 0f, 0.001f);
                tool.DragEnded();
                await WaitSolved(s);
                var nudges = s.Host.Session.Take.Edits.NudgesFor(presenter.ShownCalibration.Key);
                Assert.InRange(nudges["LeftFoot"].y, 1.99f, 2.01f);
                Assert.True(nudges.ContainsKey("RightFoot"));
                Assert.Equal(2.0, takes.Inspector.NudgeRows.Single(r => r.Role == "LeftFoot").Y, 1);
                Assert.True(presenter.CalibrationDevice("LeftFoot", out var moved));
                Assert.True(presenter.CalibrationDevice("RightFoot", out var mirrored));
                Assert.InRange(moved.y - foot.y, 0.019f, 0.021f);
                Assert.InRange((mirrored - CalibrationTool.Mirrored(moved)).Length, 0f, 0.002f);

                // parallel views: from the front the avatar's left is on screen right; orbiting goes back to 3D
                takes.SetView(VRMR.Render.ViewPreset.Front);
                Assert.True(view.Camera.Orthographic);
                Assert.True(view.Project(moved, out var left));
                Assert.True(view.Project(mirrored, out var right));
                Assert.True(left.X > right.X, "left foot on the right from the front");
                Assert.InRange(Math.Abs(left.Y - right.Y), 0, 1.0);
                var mid = view.TranslatePoint(new Point(view.Bounds.Width / 2, view.Bounds.Height * 0.15), s.Window)
                    .Value;
                s.Window.MouseDown(mid, MouseButton.Left);
                s.Window.MouseMove(mid + new Point(40, 0));
                s.Window.MouseUp(mid + new Point(40, 0), MouseButton.Left);
                Assert.Equal((int)VRMR.Render.ViewPreset.Perspective, takes.ViewIndex);
                Assert.False(view.Camera.Orthographic);

                takes.UndoCommand.Execute(null);
                await WaitSolved(s);
                Assert.Null(s.Host.Session.Take.Edits.NudgesFor(presenter.ShownCalibration.Key));

                takes.ShowCalibrationPose = false;
                Assert.Null(((IViewport)view).Tool);
                Assert.Equal("", takes.CalibrationTool.Selected);

                // leaving the take for Live leaves the calibration view
                takes.ShowCalibrationPose = true;
                s.Host.Session.Close();
                await HeadlessUi.SettleAsync(50);
                Assert.False(takes.ShowCalibrationPose);
                Assert.Null(presenter.ShownCalibration);
                Assert.Null(((IViewport)view).Tool);
            });
        }

        [Fact]
        public async Task TheWheelOverAFocusedDropDownDoesNotChangeItsValue()
        {
            await HeadlessUi.RunAsync(async () =>
            {
                using var s = await OpenTake();
                var takes = s.Shell.Takes;
                var combos = VisualExtensions.GetVisualDescendants(s.Window).OfType<ComboBox>()
                    .Where(c => c.IsEffectivelyVisible).ToList();
                var speed = combos.First(c => ReferenceEquals(c.ItemsSource, takes.SpeedLabels));
                Assert.Equal(2, speed.SelectedIndex);
                speed.Focus();
                var at = speed.TranslatePoint(new Point(speed.Bounds.Width / 2, speed.Bounds.Height / 2), s.Window)
                    .Value;
                s.Window.MouseWheel(at, new Vector(0, -1));
                s.Window.MouseWheel(at, new Vector(0, 1));
                s.Window.MouseWheel(at, new Vector(0, 1));
                Assert.Equal(2, speed.SelectedIndex);
                Assert.Equal(2, takes.SpeedIndex);

                // inside the inspector the wheel scrolls the panel instead
                var inPanel = combos.First(c => VisualExtensions.FindAncestorOfType<ScrollViewer>(c) != null);
                var scroller = VisualExtensions.FindAncestorOfType<ScrollViewer>(inPanel);
                int before = inPanel.SelectedIndex;
                inPanel.Focus();
                var there = inPanel
                    .TranslatePoint(new Point(inPanel.Bounds.Width / 2, inPanel.Bounds.Height / 2), s.Window).Value;
                s.Window.MouseWheel(there, new Vector(0, -2));
                await HeadlessUi.SettleAsync(30);
                Assert.Equal(before, inPanel.SelectedIndex);
                Assert.True(scroller.Offset.Y > 0, "the panel did not scroll");
            });
        }

        [Fact]
        public async Task PlaybackAndSeekingWorkBeforeTheFirstSolveFinishes()
        {
            await HeadlessUi.RunAsync(async () =>
            {
                var releaseBake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task playback = null;
                bool soughtFrameShown = false;
                using var s = await OpenTake(beforeOpen: opening =>
                {
                    var session = opening.Host.Session;
                    var pendingMesh = session.PendingPreviewMesh;
                    session.PendingPreviewMesh = () => Task.WhenAll(pendingMesh?.Invoke() ?? Task.CompletedTask,
                        releaseBake.Task);
                    session.PreviewClipReady += () =>
                    {
                        if (playback == null) playback = PlayThenSeek(opening);
                        else if (session.PlayheadClip.SourceTime[0] == 2)
                            soughtFrameShown = session.Clip == null && session.Solve == SolveState.Solving;
                    };
                });

                Assert.NotNull(playback);
                await playback;
                Assert.True(soughtFrameShown);
                Assert.Equal(2, s.Host.Clock.Playhead);

                async Task PlayThenSeek(Session opening)
                {
                    try
                    {
                        var play = VisualExtensions.GetVisualDescendants(opening.Window).OfType<IconButton>()
                            .Single(button => button.Name == "PlayButton");
                        Assert.True(play.IsEffectivelyEnabled);
                        play.Command.Execute(null);
                        Assert.True(opening.Shell.Takes.IsPlaying);
                        for (int i = 0; i < 10; i++)
                        {
                            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                            await HeadlessUi.SettleAsync(20);
                        }

                        var clock = opening.Host.Clock;
                        Assert.True(clock.Playhead > 0);
                        Assert.Null(opening.Host.Session.Clip);
                        clock.Seek(1, opening.Host.Session.Take.Duration);
                        clock.Seek(2, opening.Host.Session.Take.Duration);
                    }
                    finally { releaseBake.TrySetResult(); }
                }
            });
        }

        [Fact]
        public async Task SpacePlaysAndThePlayheadFollowsTheDisplayFrames()
        {
            await HeadlessUi.RunAsync(async () =>
            {
                using var s = await OpenTake();
                s.Window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
                Assert.True(s.Shell.Takes.IsPlaying);
                Assert.True(s.Window.Viewport.Animating);
                double start = s.Host.Clock.Playhead;
                for (int i = 0; i < 30; i++)
                {
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    await HeadlessUi.SettleAsync(20);
                }

                Assert.True(s.Host.Clock.Playhead > start + 0.2,
                    $"the playhead did not advance ({start} -> {s.Host.Clock.Playhead})");
                s.Window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
                Assert.False(s.Shell.Takes.IsPlaying);
                Assert.False(s.Window.Viewport.Animating);
            });
        }

        [Fact]
        public async Task APausedWindowDrawsNothing()
        {
            await HeadlessUi.RunAsync(async () =>
            {
                using var s = await OpenTake();
                HeadlessUi.Capture(s.Window, "flow-idle");
                await HeadlessUi.SettleAsync(300);
                long view = s.Window.Viewport.FramesDrawn, timeline = s.Window.TimelineView.FramesDrawn;
                for (int i = 0; i < 40; i++)
                {
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    await HeadlessUi.SettleAsync(25);
                }

                Assert.Equal(view, s.Window.Viewport.FramesDrawn);
                Assert.Equal(timeline, s.Window.TimelineView.FramesDrawn);
            });
        }

        [Fact]
        public async Task KeysStepSeekTrimAndUndo()
        {
            await HeadlessUi.RunAsync(async () =>
            {
                using var s = await OpenTake();
                var clock = s.Host.Clock;
                var take = s.Host.Session.Take;

                async Task Press(Key k, RawInputModifiers m = RawInputModifiers.None)
                {
                    s.Window.KeyPress(k, m, PhysicalKey.None, null);
                    await HeadlessUi.SettleAsync(20);
                }

                await Press(Key.Right);
                await Press(Key.Right);
                await Press(Key.Right);
                Assert.Equal(3 / 60.0, clock.Playhead, 4);
                await Press(Key.Left);
                Assert.Equal(2 / 60.0, clock.Playhead, 4);
                await Press(Key.End);
                Assert.Equal(take.Duration, clock.Playhead, 3);
                clock.Seek(1.0, take.Duration);
                await Press(Key.I);
                Assert.Equal(1.0, s.Host.Session.Take.Edits.TrimIn, 3);
                clock.Seek(3.0, take.Duration);
                await Press(Key.O);
                Assert.Equal(3.0, s.Host.Session.Take.Edits.TrimOut, 3);
                await Press(Key.Home);
                Assert.Equal(1.0, clock.Playhead, 3);
                await Press(Key.Z, RawInputModifiers.Control);
                Assert.True(s.Host.Session.Take.Edits.TrimOut < 0);
                await Press(Key.Z, RawInputModifiers.Control);
                Assert.Equal(0, s.Host.Session.Take.Edits.TrimIn);
                await Press(Key.Y, RawInputModifiers.Control);
                Assert.Equal(1.0, s.Host.Session.Take.Edits.TrimIn, 3);
                await Press(Key.Z, RawInputModifiers.Control | RawInputModifiers.Shift);
                Assert.Equal(3.0, s.Host.Session.Take.Edits.TrimOut, 3);
                await WaitSolved(s);
                Assert.Equal(2.0, s.Host.Session.Clip.Duration, 1);
            });
        }

        [Fact]
        public async Task DraggingATrimHandleIsOneUndoStepAndSolvesWhenReleased()
        {
            await HeadlessUi.RunAsync(async () =>
            {
                using var s = await OpenTake();
                var session = s.Host.Session;
                int solves = 0;
                session.ClipChanged += () => solves++;
                var from = OnTimeline(s, 0);
                s.Window.MouseDown(from, MouseButton.Left);
                for (int i = 1; i <= 10; i++)
                {
                    s.Window.MouseMove(OnTimeline(s, i * 0.1));
                    await HeadlessUi.SettleAsync(10);
                }

                Assert.Equal(1.0, session.Take.Edits.TrimIn, 1);
                Assert.Equal(0, solves); // nothing solves during the drag
                s.Window.MouseUp(OnTimeline(s, 1.0), MouseButton.Left);
                await WaitSolved(s);
                Assert.Equal(1, solves);
                Assert.Equal(1, session.History.UndoCount); // the whole drag is one step
                HeadlessUi.Capture(s.Window, "flow-trimmed");
                s.Shell.Takes.UndoCommand.Execute(null);
                Assert.Equal(0, session.Take.Edits.TrimIn);
            });
        }

        [Fact]
        public async Task DraggingInTheLanesSelectsAndDeleteCutsIt()
        {
            await HeadlessUi.RunAsync(async () =>
            {
                using var s = await OpenTake();
                var takes = s.Shell.Takes;
                Assert.False(takes.HasSelection);
                s.Window.MouseDown(OnTimeline(s, 1.0, 100), MouseButton.Left);
                s.Window.MouseMove(OnTimeline(s, 2.0, 100));
                s.Window.MouseUp(OnTimeline(s, 2.0, 100), MouseButton.Left);
                Assert.True(takes.HasSelection);
                Assert.Equal(1.0, takes.Timeline.SelectionStart, 1);
                Assert.Equal(2.0, takes.Timeline.SelectionEnd, 1);
                s.Window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
                Assert.Single(s.Host.Session.Take.Edits.Cuts);
                Assert.False(takes.HasSelection);
                await WaitSolved(s);
                Assert.Equal(3.0, s.Host.Session.Clip.Duration, 1);
                // right-click on the cut brings it back
                s.Window.MouseDown(OnTimeline(s, 1.5, 100), MouseButton.Right);
                s.Window.MouseUp(OnTimeline(s, 1.5, 100), MouseButton.Right);
                Assert.Empty(s.Host.Session.Take.Edits.Cuts);
            });
        }

        [Fact]
        public async Task ChangingACalibrationSettingGivesTheTakeItsOwnSettingsAndSolvesAgain()
        {
            await HeadlessUi.RunAsync(async () =>
            {
                using var s = await OpenTake();
                var session = s.Host.Session;
                var inspector = s.Shell.Takes.Inspector;
                Assert.Null(session.Take.Edits.Calib);
                Assert.Equal("defaults", inspector.CalibrationSummary);
                var view = s.Window.FindControl<UserControl>("TakesInspectorView").FindControl<UserControl>("TakeView");
                var sections = VisualExtensions.GetVisualDescendants(view).OfType<Section>();
                Assert.Single(sections, section => Equals(section.Header, "Calibration")).IsExpanded = true;
                await HeadlessUi.SettleAsync();
                var combos = VisualExtensions.GetVisualDescendants(view).OfType<ComboBox>();
                var spine = Assert.Single(combos, combo => ReferenceEquals(combo.ItemsSource, inspector.Settings.SpineModes));
                var measure = Assert.Single(combos, combo => ReferenceEquals(combo.ItemsSource, inspector.Settings.MeasureModes));
                Assert.Equal("VRChat: Recorded setting", spine.SelectedItem);

                session.Edit("vrcChanges", edits => edits.VrcChanges.Add(new VrcChange { Spine = 0, Measure = 0 }));
                await WaitSolved(s);
                Assert.Equal("VRChat: Lock hips", spine.SelectedItem);
                Assert.Equal("VRChat: By arm span", measure.SelectedItem);
                var before = session.Clip;
                spine.SelectedIndex = (int)CalLock.Both;
                Assert.NotNull(session.Take.Edits.Calib);
                Assert.Equal(CalLock.Both, session.Take.Edits.Calib.Lock);
                Assert.False(session.Take.Edits.Calib.FollowVrcSpine);
                Assert.True(session.Take.Edits.Calib.FollowVrcMeasure);
                Assert.Equal(CalLock.Head, s.Host.Settings.CalibrationDefaults.Lock); // the defaults are untouched
                Assert.Equal("this take's own", inspector.CalibrationSummary);
                await WaitSolved(s);
                Assert.NotSame(before, session.Clip);
                s.Shell.Takes.UndoCommand.Execute(null);
                Assert.Null(session.Take.Edits.Calib);
                Assert.Equal("VRChat: Lock hips", spine.SelectedItem);
            });
        }

        [Fact]
        public async Task AvatarScaleShowsTheUploadedMultiplierBesideTheNumericOverride()
        {
            await HeadlessUi.RunAsync(async () =>
            {
                using var s = await OpenTake();
                var settings = s.Shell.Takes.Inspector.Settings;
                Assert.Equal("At upload: 1.000", settings.AvatarScaleReadout);
                Assert.Equal("At upload: —", s.Shell.CalibrationDefaults.AvatarScaleReadout);

                var take = SyntheticTake.Create();
                take.Profile.LibraryId = "";
                take.AvatarEyeHeight = take.Profile.EyeHeight * 1.25f;
                s.Host.Session.Show(take);
                Assert.Equal("At upload: —", settings.AvatarScaleReadout);
                await WaitSolved(s);
                Assert.Equal("At upload: 1.250", settings.AvatarScaleReadout);

                var view = s.Window.FindControl<UserControl>("TakesInspectorView").FindControl<UserControl>("TakeView");
                var sections = VisualExtensions.GetVisualDescendants(view).OfType<Section>();
                Assert.Single(sections, section => Equals(section.Header, "Calibration")).IsExpanded = true;
                await HeadlessUi.SettleAsync();
                Assert.Single(sections, section => section.StateKey == "take.cal.advanced").IsExpanded = true;
                await HeadlessUi.SettleAsync();
                var controls = VisualExtensions.GetVisualDescendants(view);
                var field = Assert.Single(controls.OfType<NumberField>(), control => control.Maximum == 20);
                var label = Assert.Single(controls.OfType<TextBlock>(), control => control.Text == "At upload: 1.250");
                Assert.True(label.IsEffectivelyVisible);
                Assert.Equal("0.000", field.Text);

                field.SetCurrentValue(NumberField.ValueProperty, 1.5);
                settings.FollowAvatarScale = !settings.FollowAvatarScale;
                await WaitSolved(s);
                Assert.Equal("1.500", field.Text);
                Assert.Equal("At upload: 1.250", label.Text);

                settings.ResetAvatarScaleCommand.Execute(null);
                await WaitSolved(s);
                Assert.Equal("0.000", field.Text);
                Assert.Equal("At upload: 1.250", label.Text);
            });
        }

        [Fact]
        public async Task TakeInspectorKeepsLiveCalibrationAvailableAfterClosingTheTake()
        {
            await HeadlessUi.RunAsync(async () =>
            {
                using var s = await OpenTake();
                var takes = s.Shell.Takes;
                takes.InspectorTab = TakesWorkspaceViewModel.TakeTab;
                var inspector = s.Window.FindControl<UserControl>("TakesInspectorView");
                var view = inspector.FindControl<UserControl>("TakeView");
                var sections = VisualExtensions.GetVisualDescendants(view).OfType<Section>();
                Assert.Single(sections, section => Equals(section.Header, "Calibration")).IsExpanded = true;
                Assert.Single(sections, section => Equals(section.Header, "Calibration library")).IsExpanded = true;
                await HeadlessUi.SettleAsync();

                var buttons = VisualExtensions.GetVisualDescendants(view).OfType<Button>();
                var save = view.FindControl<Button>("SaveButton");
                var atPlayhead = Assert.Single(buttons, button => Equals(button.Content, "At playhead"));
                var capture = Assert.Single(buttons, button => Equals(button.Content, "Capture now"));
                Assert.True(atPlayhead.IsEffectivelyEnabled);

                s.Host.Session.Close();
                await HeadlessUi.SettleAsync();

                Assert.True(view.IsEffectivelyVisible);
                Assert.True(save.IsEffectivelyVisible);
                Assert.False(save.IsEffectivelyEnabled);
                Assert.True(atPlayhead.IsEffectivelyVisible);
                Assert.False(atPlayhead.IsEffectivelyEnabled);
                Assert.True(capture.IsEffectivelyVisible);
                Assert.True(capture.IsEffectivelyEnabled);
                var combos = VisualExtensions.GetVisualDescendants(view).OfType<ComboBox>();
                var spine = Assert.Single(combos, combo => ReferenceEquals(combo.ItemsSource, takes.Inspector.Settings.SpineModes));
                Assert.True(spine.IsEffectivelyEnabled);
                spine.SelectedIndex = (int)CalLock.Both;
                Assert.Equal(CalLock.Both, s.Host.Settings.CalibrationDefaults.Lock);

                await s.Host.Session.OpenAsync(s.Shell.Library.Items[0].Path);
                await WaitSolved(s);
                Assert.True(atPlayhead.IsEffectivelyEnabled);
                Assert.Null(s.Host.Session.Take.Edits.Calib);
            });
        }

        [Fact]
        public async Task OriginTurnsAndPlacementFieldsMoveTheTake()
        {
            await HeadlessUi.RunAsync(async () =>
            {
                using var s = await OpenTake();
                var session = s.Host.Session;
                var inspector = s.Shell.Takes.Inspector;
                s.Shell.Takes.TurnRightCommand.Execute(null);
                Assert.True(session.Take.Edits.HasPlacement);
                Assert.Equal(90, Math.Abs(inspector.TurnY), 0);
                inspector.MoveX = 25;
                Assert.Equal(0.25f, session.Take.Edits.Placement.pos.x, 3);
                await WaitSolved(s);
                Assert.Equal(0.25f, session.Clip.Placement.pos.x, 3);
                s.Shell.Takes.OriginHereCommand.Execute(null);
                await WaitSolved(s);
                var clip = session.Clip;
                var hips = clip.Hips[
                    clip.FrameAt(session.Take.Edits.SourceToEdited(s.Host.Clock.Playhead, session.Take.Duration))];
                Assert.True(Math.Abs(hips.x) < 0.01 && Math.Abs(hips.z) < 0.01,
                    $"the hips should be over the origin, were at {hips}");
                inspector.ResetPlacementCommand.Execute(null);
                Assert.False(session.Take.Edits.HasPlacement);
                Assert.Equal("as solved", inspector.PlacementSummary);
            });
        }

        [Fact]
        public async Task TheGizmoDragMovesTheTakeAsOneUndoStep()
        {
            await HeadlessUi.RunAsync(async () =>
            {
                using var s = await OpenTake();
                var takes = s.Shell.Takes;
                var session = s.Host.Session;
                takes.SetGizmo(true, false);
                var tool = takes.PlacementTool;
                Assert.NotNull(tool.Gizmo);
                tool.DragStarted();
                for (int i = 1; i <= 5; i++) tool.Dragged(new Vec3(i * 0.1f, 0, 0), Quat.Identity, 0);
                Assert.Equal(0.5f, session.Take.Edits.Placement.pos.x, 3);
                tool.DragEnded();
                await WaitSolved(s);
                Assert.Equal(1, session.History.UndoCount);
                Assert.Equal(0.5f, session.Clip.Placement.pos.x, 3);
                s.Window.KeyPress(Key.E, RawInputModifiers.None, PhysicalKey.E, "e");
                Assert.True(takes.GizmoTurns);
                s.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.False(takes.GizmoOn);
            });
        }

        [Fact]
        public async Task RenamingSavesAndTheLibraryShowsTheNewName()
        {
            await HeadlessUi.RunAsync(async () =>
            {
                using var s = await OpenTake();
                var inspector = s.Shell.Takes.Inspector;
                inspector.Name = "A better name";
                Assert.True(inspector.IsDirty);
                Assert.Equal("A better name •", s.Shell.Library.Items[0].Name);
                s.Window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, null);
                Assert.True(
                    await HeadlessUi.WaitForAsync(() =>
                        !s.Host.Session.Dirty && s.Shell.Library.Items.Count == 1 &&
                        s.Shell.Library.Items[0].Info.Name == "A better name"),
                    "the saved name did not reach the library");
                Assert.Equal("A better name", RecordingIO.ReadSummary(s.Shell.Library.Items[0].Path).Name);
            });
        }

        [Fact]
        public async Task SearchFiltersTheLibraryAndSaysWhenNothingMatches()
        {
            await HeadlessUi.RunAsync(async () =>
            {
                using var s = await OpenTake();
                s.Shell.Library.Search = "flow";
                Assert.Single(s.Shell.Library.Items);
                s.Shell.Library.Search = "nothing like this";
                Assert.Empty(s.Shell.Library.Items);
                Assert.True(s.Shell.Library.NoMatches);
                HeadlessUi.Capture(s.Window, "flow-no-matches");
                s.Shell.Library.ClearSearchCommand.Execute(null);
                Assert.Single(s.Shell.Library.Items);
            });
        }

        [Fact]
        public async Task ReviewShowsFramesBesideTheTimecodeAndKeepsStatusClearOfTheFoldout()
        {
            await HeadlessUi.RunAsync(async () =>
            {
                using var s = await OpenTake(width: 1100);
                var live = s.Window.FindControl<VRMR.App.Controls.IconToggle>("LiveButton");
                var library = s.Window.FindControl<Border>("LibraryHost");
                var libraryButtons = VisualExtensions.GetVisualDescendants(library).OfType<Button>();
                var hide = Assert.Single(libraryButtons, b => Equals(ToolTip.GetTip(b), "Hide library (Ctrl+1)"));
                double liveRight = live.TranslatePoint(new Point(live.Bounds.Width, 0), library).Value.X;
                var hideStart = hide.TranslatePoint(default, library).Value;
                Assert.Equal(live.TranslatePoint(default, library).Value.Y, hideStart.Y);
                Assert.True(liveRight <= hideStart.X);
                Assert.True(hideStart.X + hide.Bounds.Width <= library.Bounds.Width);
                Assert.True(live.TryFindResource("AccentBrush", live.ActualThemeVariant, out object accent));
                Assert.Equal(accent, live.BorderBrush);

                var take = s.Host.Session.Take;
                var clip = s.Host.Session.Clip;
                var counter = s.Window.FindControl<TextBlock>("FrameCounter");
                s.Host.Clock.Seek(1, take.Duration);
                int frame = clip.FrameAt(take.Edits.SourceToEdited(1, take.Duration));
                Assert.Equal($"frame {frame}/{clip.Frames - 1}", counter.Text);
                Assert.Empty(s.Window.Viewport.Scene.OverlayRight);
                var status = s.Window.FindControl<TextBlock>("ViewStatus");
                Assert.Contains("IK solve", status.Text);
                Assert.DoesNotContain("frame ", status.Text);

                s.Shell.LibraryOpen = false;
                await HeadlessUi.SettleAsync(20);
                var buttons = VisualExtensions.GetVisualDescendants(s.Window).OfType<Button>();
                var reopen = Assert.Single(buttons, b => Equals(ToolTip.GetTip(b), "Show library (Ctrl+1)"));
                var right = reopen.TranslatePoint(new Point(reopen.Bounds.Width, 0), s.Window).Value;
                Assert.True(status.TranslatePoint(default, s.Window).Value.X > right.X);
            });
        }

        [Fact]
        public async Task SettingsToggleOpensAndClosesThePageAndReflectsEscape()
        {
            await HeadlessUi.RunAsync(async () =>
            {
                using var s = await OpenTake();
                var toggle = s.Window.FindControl<VRMR.App.Controls.IconToggle>("SettingsToggle");
                var page = s.Window.FindControl<ContentControl>("PageHost");
                var centre = new Point(toggle.Bounds.Width / 2, toggle.Bounds.Height / 2);
                var point = toggle.TranslatePoint(centre, s.Window).Value;

                void Click()
                {
                    s.Window.MouseDown(point, MouseButton.Left);
                    s.Window.MouseUp(point, MouseButton.Left);
                }

                Assert.False(toggle.IsChecked);
                Click();
                Assert.True(page.IsVisible);
                Assert.True(toggle.IsChecked);
                Assert.True(toggle.TryFindResource("AccentBrush", toggle.ActualThemeVariant, out object accent));
                Assert.Equal(accent, toggle.BorderBrush);
                Click();
                Assert.False(page.IsVisible);
                Assert.False(toggle.IsChecked);
                Assert.NotEqual(accent, toggle.BorderBrush);

                s.Shell.OpenSettingsCommand.Execute(null);
                Assert.True(toggle.IsChecked);
                s.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.False(toggle.IsChecked);
                Assert.False(page.IsVisible);
            });
        }

        [Fact]
        public async Task SidePanelButtonsReopenBothPanelsAndDevicesIsTheFirstTab()
        {
            await HeadlessUi.RunAsync(async () =>
            {
                using var s = await OpenTake(width: 1100);
                async Task Click(string tip)
                {
                    var buttons = VisualExtensions.GetVisualDescendants(s.Window).OfType<Button>();
                    var button = Assert.Single(buttons, b => b.IsEffectivelyVisible && Equals(ToolTip.GetTip(b), tip));
                    var centre = new Point(button.Bounds.Width / 2, button.Bounds.Height / 2);
                    var point = button.TranslatePoint(centre, s.Window).Value;
                    s.Window.MouseDown(point, MouseButton.Left);
                    s.Window.MouseUp(point, MouseButton.Left);
                    await HeadlessUi.SettleAsync(20);
                }

                await Click("Hide library (Ctrl+1)");
                await Click("Hide inspector (Ctrl+2)");
                Assert.False(s.Shell.LibraryOpen);
                Assert.False(s.Shell.InspectorOpen);
                await Click("Show library (Ctrl+1)");
                await Click("Show inspector (Ctrl+2)");
                Assert.True(s.Shell.LibraryOpen);
                Assert.True(s.Shell.InspectorOpen);

                var inspector = s.Window.FindControl<UserControl>("TakesInspectorView");
                var tabs = inspector.FindControl<ListBox>("Tabs");
                Assert.Equal(["Devices", "Take", "Export"], tabs.Items.Cast<ListBoxItem>().Select(t => (string)t.Content));
                tabs.SelectedIndex = 0;
                Assert.True(s.Shell.Takes.ShowDevicesTab);
                tabs.SelectedIndex = 1;
                Assert.True(s.Shell.Takes.ShowTakeTab);
                tabs.SelectedIndex = 2;
                Assert.True(s.Shell.Takes.ShowExportTab);
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task TimelineButtonAndShortcutRestoreTheResizedTimeline(bool scene)
        {
            await HeadlessUi.RunAsync(async () =>
            {
                using var s = await OpenTake(width: 1100);
                s.Shell.SceneAvailable = true;
                s.Shell.WorkspaceIndex = scene ? 1 : 0;
                await HeadlessUi.SettleAsync(20);
                var splitter = s.Window.FindControl<GridSplitter>("TimelineSplitter");
                var button = s.Window.FindControl<Button>("TimelineToggle");
                var timeline = s.Window.FindControl<Panel>("TimelineHost");
                double original = timeline.Bounds.Height;
                var start = splitter.TranslatePoint(new Point(30, splitter.Bounds.Height / 2), s.Window).Value;
                s.Window.MouseDown(start, MouseButton.Left);
                s.Window.MouseMove(start.WithY(start.Y - 40));
                await HeadlessUi.SettleAsync(20);
                s.Window.MouseUp(start.WithY(start.Y - 40), MouseButton.Left);
                await HeadlessUi.SettleAsync(30);
                double resized = timeline.Bounds.Height;
                Assert.True(resized > original);

                async Task Click()
                {
                    var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), s.Window).Value;
                    s.Window.MouseDown(point, MouseButton.Left);
                    s.Window.MouseUp(point, MouseButton.Left);
                    await HeadlessUi.SettleAsync(20);
                }

                await Click();
                Assert.False(s.Shell.TimelineOpen);
                Assert.Equal("Show timeline (Ctrl+3)", ToolTip.GetTip(button));
                s.Window.KeyPress(Key.D3, RawInputModifiers.Control, PhysicalKey.Digit3, null);
                await HeadlessUi.SettleAsync(20);
                Assert.Equal(resized, timeline.Bounds.Height);
                s.Window.KeyPress(Key.D3, RawInputModifiers.Control, PhysicalKey.Digit3, null);
                await HeadlessUi.SettleAsync(20);
                await Click();
                Assert.True(s.Shell.TimelineOpen);
                Assert.Equal(resized, timeline.Bounds.Height);
                Assert.Equal("Hide timeline (Ctrl+3)", ToolTip.GetTip(button));
            });
        }

        [Fact]
        public async Task PanelsFoldTheWorkspaceSwitchesAndTheHelpSheetOpens()
        {
            await HeadlessUi.RunAsync(async () =>
            {
                using var s = await OpenTake();
                s.Window.KeyPress(Key.D1, RawInputModifiers.Control, PhysicalKey.Digit1, null);
                s.Window.KeyPress(Key.D2, RawInputModifiers.Control, PhysicalKey.Digit2, null);
                Assert.False(s.Shell.LibraryOpen);
                Assert.False(s.Shell.InspectorOpen);
                HeadlessUi.Capture(s.Window, "flow-folded");
                s.Shell.LibraryOpen = s.Shell.InspectorOpen = true;

                // the Scene workspace is switched off: no switch in the top bar, and nothing can select it
                var workspaceSwitch = s.Window.FindControl<Control>("WorkspaceSwitch");
                Assert.False(AppFeatures.SceneWorkspace);
                Assert.False(workspaceSwitch.IsVisible);
                s.Shell.WorkspaceIndex = 1;
                Assert.True(s.Shell.IsTakes);
                Assert.Equal(0, s.Shell.WorkspaceIndex);

                // switched on (as the flag would), the switch is back and the Takes workspace lets go of the view
                s.Shell.SceneAvailable = true;
                await HeadlessUi.SettleAsync(20);
                Assert.True(workspaceSwitch.IsVisible);
                s.Shell.WorkspaceIndex = 1;
                Assert.True(s.Shell.IsScene);
                Assert.False(s.Window.Viewport.Animating);
                s.Shell.SceneAvailable = false;
                Assert.True(s.Shell.IsTakes);
                await HeadlessUi.SettleAsync(20);
                Assert.False(workspaceSwitch.IsVisible);
                s.Window.KeyPress(Key.F1, RawInputModifiers.None, PhysicalKey.F1, null);
                Assert.True(s.Shell.HelpOpen);
                HeadlessUi.Capture(s.Window, "flow-help");
                s.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.False(s.Shell.HelpOpen);
            });
        }

        sealed class Session : IDisposable
        {
            public ScratchData Data;
            public AppHost Host;
            public ShellViewModel Shell;
            public MainWindow Window;

            public void Dispose()
            {
                Window.Close();
                Host.Dispose();
                Data.Dispose();
            }
        }
    }
}
