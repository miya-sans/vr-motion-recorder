using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using VRMR.App.Composition;
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
        static Session OpenTake(double seconds = 4, double width = 1400)
        {
            var s = new Session { Data = new ScratchData() };
            SyntheticTake.Save(s.Data.TakesDir, seconds, "Flow take");
            s.Host = s.Data.NewHost(new AvaloniaUiDispatcher());
            s.Shell = new ShellViewModel(s.Host);
            s.Window = new MainWindow(s.Shell) { Width = width, Height = 860 };
            s.Window.Show();
            Assert.True(HeadlessUi.WaitFor(() => s.Shell.Library.Items.Count == 1),
                "the library did not list the take");
            s.Shell.Library.Selected = s.Shell.Library.Items[0];
            WaitSolved(s);
            return s;
        }

        static void WaitSolved(Session s)
        {
            Assert.True(
                HeadlessUi.WaitFor(() => s.Host.Session.Solve == SolveState.Done && s.Host.Session.ClipIsCurrent,
                    30000), "the take did not solve: " + s.Host.Session.SolveError);
            HeadlessUi.Settle(50);
        }

        static Point OnTimeline(Session s, double time, double y = 60)
        {
            var tl = s.Window.TimelineView;
            var m = tl.Model;
            double left = 96, right = tl.Bounds.Width - 10;
            double x = left + (time - m.ViewStart) / (m.ViewEnd - m.ViewStart) * (right - left);
            return tl.TranslatePoint(new Point(x, y), s.Window).Value;
        }

        [Fact]
        public void OpeningATakeShowsItAndLeavesLive()
        {
            HeadlessUi.Run(() =>
            {
                using var s = OpenTake();
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
        public void TheGpuViewShowsWhatTheSceneHolds()
        {
            HeadlessUi.Run(() =>
            {
                using var s = OpenTake();
                var view = Assert.IsType<GpuView>(s.Window.Viewport);
                var scene = view.Scene;
                Assert.Single(view.Frame.Actors);
                var actor = view.Frame.Actors[0];
                Assert.Same(scene.Profile, actor.Profile);
                Assert.Equal(scene.NodePos, actor.NodePos);
                Assert.Equal(scene.Markers.Count, view.Frame.Markers.Count);
                Assert.Contains("frame", view.Frame.Overlay.ToString());

                // the avatar's mesh arrives from a background load (here the built-in mannequin's) and the frame is refilled with it
                Assert.True(
                    HeadlessUi.WaitFor(() => view.Frame.Actors.Count == 1 && view.Frame.Actors[0].Model != null, 10000),
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
        public void TheWheelOverAFocusedDropDownDoesNotChangeItsValue()
        {
            HeadlessUi.Run(() =>
            {
                using var s = OpenTake();
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
                HeadlessUi.Settle(30);
                Assert.Equal(before, inPanel.SelectedIndex);
                Assert.True(scroller.Offset.Y > 0, "the panel did not scroll");
            });
        }

        [Fact]
        public void SpacePlaysAndThePlayheadFollowsTheDisplayFrames()
        {
            HeadlessUi.Run(() =>
            {
                using var s = OpenTake();
                s.Window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
                Assert.True(s.Shell.Takes.IsPlaying);
                Assert.True(s.Window.Viewport.Animating);
                double start = s.Host.Clock.Playhead;
                for (int i = 0; i < 30; i++)
                {
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    HeadlessUi.Settle(20);
                }

                Assert.True(s.Host.Clock.Playhead > start + 0.2,
                    $"the playhead did not advance ({start} -> {s.Host.Clock.Playhead})");
                s.Window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
                Assert.False(s.Shell.Takes.IsPlaying);
                Assert.False(s.Window.Viewport.Animating);
            });
        }

        [Fact]
        public void APausedWindowDrawsNothing()
        {
            HeadlessUi.Run(() =>
            {
                using var s = OpenTake();
                HeadlessUi.Capture(s.Window, "flow-idle");
                HeadlessUi.Settle(300);
                long view = s.Window.Viewport.FramesDrawn, timeline = s.Window.TimelineView.FramesDrawn;
                for (int i = 0; i < 40; i++)
                {
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    HeadlessUi.Settle(25);
                }

                Assert.Equal(view, s.Window.Viewport.FramesDrawn);
                Assert.Equal(timeline, s.Window.TimelineView.FramesDrawn);
            });
        }

        [Fact]
        public void KeysStepSeekTrimAndUndo()
        {
            HeadlessUi.Run(() =>
            {
                using var s = OpenTake();
                var clock = s.Host.Clock;
                var take = s.Host.Session.Take;

                void Press(Key k, RawInputModifiers m = RawInputModifiers.None)
                {
                    s.Window.KeyPress(k, m, PhysicalKey.None, null);
                    HeadlessUi.Settle(20);
                }

                Press(Key.Right);
                Press(Key.Right);
                Press(Key.Right);
                Assert.Equal(3 / 60.0, clock.Playhead, 4);
                Press(Key.Left);
                Assert.Equal(2 / 60.0, clock.Playhead, 4);
                Press(Key.End);
                Assert.Equal(take.Duration, clock.Playhead, 3);
                clock.Seek(1.0, take.Duration);
                Press(Key.I);
                Assert.Equal(1.0, s.Host.Session.Take.Edits.TrimIn, 3);
                clock.Seek(3.0, take.Duration);
                Press(Key.O);
                Assert.Equal(3.0, s.Host.Session.Take.Edits.TrimOut, 3);
                Press(Key.Home);
                Assert.Equal(1.0, clock.Playhead, 3);
                Press(Key.Z, RawInputModifiers.Control);
                Assert.True(s.Host.Session.Take.Edits.TrimOut < 0);
                Press(Key.Z, RawInputModifiers.Control);
                Assert.Equal(0, s.Host.Session.Take.Edits.TrimIn);
                Press(Key.Y, RawInputModifiers.Control);
                Assert.Equal(1.0, s.Host.Session.Take.Edits.TrimIn, 3);
                Press(Key.Z, RawInputModifiers.Control | RawInputModifiers.Shift);
                Assert.Equal(3.0, s.Host.Session.Take.Edits.TrimOut, 3);
                WaitSolved(s);
                Assert.Equal(2.0, s.Host.Session.Clip.Duration, 1);
            });
        }

        [Fact]
        public void DraggingATrimHandleIsOneUndoStepAndSolvesWhenReleased()
        {
            HeadlessUi.Run(() =>
            {
                using var s = OpenTake();
                var session = s.Host.Session;
                int solves = 0;
                session.ClipChanged += () => solves++;
                var from = OnTimeline(s, 0);
                s.Window.MouseDown(from, MouseButton.Left);
                for (int i = 1; i <= 10; i++)
                {
                    s.Window.MouseMove(OnTimeline(s, i * 0.1));
                    HeadlessUi.Settle(10);
                }

                Assert.Equal(1.0, session.Take.Edits.TrimIn, 1);
                Assert.Equal(0, solves); // nothing solves during the drag
                s.Window.MouseUp(OnTimeline(s, 1.0), MouseButton.Left);
                WaitSolved(s);
                Assert.Equal(1, solves);
                Assert.Equal(1, session.History.UndoCount); // the whole drag is one step
                HeadlessUi.Capture(s.Window, "flow-trimmed");
                s.Shell.Takes.UndoCommand.Execute(null);
                Assert.Equal(0, session.Take.Edits.TrimIn);
            });
        }

        [Fact]
        public void DraggingInTheLanesSelectsAndDeleteCutsIt()
        {
            HeadlessUi.Run(() =>
            {
                using var s = OpenTake();
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
                WaitSolved(s);
                Assert.Equal(3.0, s.Host.Session.Clip.Duration, 1);
                // right-click on the cut brings it back
                s.Window.MouseDown(OnTimeline(s, 1.5, 100), MouseButton.Right);
                s.Window.MouseUp(OnTimeline(s, 1.5, 100), MouseButton.Right);
                Assert.Empty(s.Host.Session.Take.Edits.Cuts);
            });
        }

        [Fact]
        public void ChangingACalibrationSettingGivesTheTakeItsOwnSettingsAndSolvesAgain()
        {
            HeadlessUi.Run(() =>
            {
                using var s = OpenTake();
                var session = s.Host.Session;
                var inspector = s.Shell.Takes.Inspector;
                Assert.Null(session.Take.Edits.Calib);
                Assert.Equal("defaults", inspector.CalibrationSummary);
                var before = session.Clip;
                inspector.Settings.SpineIndex = (int)CalLock.Both;
                Assert.NotNull(session.Take.Edits.Calib);
                Assert.Equal(CalLock.Both, session.Take.Edits.Calib.Lock);
                Assert.Equal(CalLock.Head, s.Host.Settings.CalibrationDefaults.Lock); // the defaults are untouched
                Assert.Equal("this take's own", inspector.CalibrationSummary);
                WaitSolved(s);
                Assert.NotSame(before, session.Clip);
                s.Shell.Takes.UndoCommand.Execute(null);
                Assert.Null(session.Take.Edits.Calib);
                Assert.Equal((int)CalLock.Head, inspector.Settings.SpineIndex);
            });
        }

        [Fact]
        public void OriginTurnsAndPlacementFieldsMoveTheTake()
        {
            HeadlessUi.Run(() =>
            {
                using var s = OpenTake();
                var session = s.Host.Session;
                var inspector = s.Shell.Takes.Inspector;
                s.Shell.Takes.TurnRightCommand.Execute(null);
                Assert.True(session.Take.Edits.HasPlacement);
                Assert.Equal(90, Math.Abs(inspector.TurnY), 0);
                inspector.MoveX = 25;
                Assert.Equal(0.25f, session.Take.Edits.Placement.pos.x, 3);
                WaitSolved(s);
                Assert.Equal(0.25f, session.Clip.Placement.pos.x, 3);
                s.Shell.Takes.OriginHereCommand.Execute(null);
                WaitSolved(s);
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
        public void TheGizmoDragMovesTheTakeAsOneUndoStep()
        {
            HeadlessUi.Run(() =>
            {
                using var s = OpenTake();
                var takes = s.Shell.Takes;
                var session = s.Host.Session;
                takes.SetGizmo(true, false);
                var tool = takes.PlacementTool;
                Assert.NotNull(tool.Gizmo);
                tool.DragStarted();
                for (int i = 1; i <= 5; i++) tool.Dragged(new Vec3(i * 0.1f, 0, 0), Quat.Identity, 0);
                Assert.Equal(0.5f, session.Take.Edits.Placement.pos.x, 3);
                tool.DragEnded();
                WaitSolved(s);
                Assert.Equal(1, session.History.UndoCount);
                Assert.Equal(0.5f, session.Clip.Placement.pos.x, 3);
                s.Window.KeyPress(Key.E, RawInputModifiers.None, PhysicalKey.E, "e");
                Assert.True(takes.GizmoTurns);
                s.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.False(takes.GizmoOn);
            });
        }

        [Fact]
        public void RenamingSavesAndTheLibraryShowsTheNewName()
        {
            HeadlessUi.Run(() =>
            {
                using var s = OpenTake();
                var inspector = s.Shell.Takes.Inspector;
                inspector.Name = "A better name";
                Assert.True(inspector.IsDirty);
                Assert.Equal("A better name •", s.Shell.Library.Items[0].Name);
                s.Window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, null);
                Assert.True(
                    HeadlessUi.WaitFor(() =>
                        !s.Host.Session.Dirty && s.Shell.Library.Items.Count == 1 &&
                        s.Shell.Library.Items[0].Info.Name == "A better name"),
                    "the saved name did not reach the library");
                Assert.Equal("A better name", RecordingIO.ReadSummary(s.Shell.Library.Items[0].Path).Name);
            });
        }

        [Fact]
        public void SearchFiltersTheLibraryAndSaysWhenNothingMatches()
        {
            HeadlessUi.Run(() =>
            {
                using var s = OpenTake();
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
        public void PanelsFoldTheWorkspaceSwitchesAndTheHelpSheetOpens()
        {
            HeadlessUi.Run(() =>
            {
                using var s = OpenTake();
                s.Window.KeyPress(Key.D1, RawInputModifiers.Control, PhysicalKey.Digit1, null);
                s.Window.KeyPress(Key.D2, RawInputModifiers.Control, PhysicalKey.Digit2, null);
                Assert.False(s.Shell.LibraryOpen);
                Assert.False(s.Shell.InspectorOpen);
                HeadlessUi.Capture(s.Window, "flow-folded");
                s.Shell.LibraryOpen = s.Shell.InspectorOpen = true;

                // the Scene workspace is switched off: no switch in the top bar, and nothing can select it
                var workspaceSwitch = ControlExtensions.FindControl<Control>(s.Window, "WorkspaceSwitch");
                Assert.False(AppFeatures.SceneWorkspace);
                Assert.False(workspaceSwitch.IsVisible);
                s.Shell.WorkspaceIndex = 1;
                Assert.True(s.Shell.IsTakes);
                Assert.Equal(0, s.Shell.WorkspaceIndex);

                // switched on (as the flag would), the switch is back and the Takes workspace lets go of the view
                s.Shell.SceneAvailable = true;
                HeadlessUi.Settle(20);
                Assert.True(workspaceSwitch.IsVisible);
                s.Shell.WorkspaceIndex = 1;
                Assert.True(s.Shell.IsScene);
                Assert.False(s.Window.Viewport.Animating);
                s.Shell.SceneAvailable = false;
                Assert.True(s.Shell.IsTakes);
                HeadlessUi.Settle(20);
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
