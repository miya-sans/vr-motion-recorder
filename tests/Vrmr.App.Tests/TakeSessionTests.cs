using System.IO;
using System.Threading.Tasks;
using VRMR.App.Composition;
using VRMR.App.Services;
using Xunit;

namespace VRMR.App.Tests
{
    /// <summary>The open take: edits, undo, the background solve and saving, on a made-up take.</summary>
    [Collection("app")]
    public class TakeSessionTests
    {
        static (ScratchData data, ManualDispatcher ui, AppHost host) Start()
        {
            var data = new ScratchData();
            var ui = new ManualDispatcher();
            return (data, ui, data.NewHost(ui));
        }

        static void WaitSolved(ManualDispatcher ui, TakeSession s)
        {
            Assert.True(ui.WaitFor(() =>
            {
                if (s.Solve == SolveState.Waiting)
                    ui.Advance(TakeSession.SolveDelay.TotalSeconds + 0.05); // let the debounce fire
                return s.Solve == SolveState.Done || s.Solve == SolveState.Failed;
            }, 30000), "the solve did not finish");
            Assert.True(s.Solve == SolveState.Done, "solve failed: " + s.SolveError);
        }

        [Fact]
        public void ShowingATakeSolvesItInTheBackground()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                var s = host.Session;
                var take = SyntheticTake.Create();
                take.Edits.TrimIn = 0.5;
                host.Clock.Playhead = take.Edits.TrimIn;
                BakedClip preview = null;
                s.PreviewClipReady += () => preview = s.PlayheadClip;
                s.Show(take);
                Assert.Equal(SolveState.Solving, s.Solve);
                Assert.Null(s.Clip);
                WaitSolved(ui, s);
                Assert.NotNull(preview);
                Assert.All(preview.SourceTime, time => Assert.Equal(0.5, time));
                Assert.Null(s.PlayheadClip);
                Assert.NotNull(s.Clip);
                Assert.True(s.ClipIsCurrent);
                Assert.True(s.Clip.Frames > 100);
                Assert.False(s.Dirty);
                Assert.False(s.History.CanUndo);
            }
        }

        [Fact]
        public void AnEditMarksDirtyAddsAnUndoStepAndSolvesAgainAfterAPause()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                var s = host.Session;
                s.Show(SyntheticTake.Create());
                WaitSolved(ui, s);
                int framesBefore = s.Clip.Frames;
                var listBefore = s.Take.Edits;

                s.Edit("trimIn", e => e.TrimIn = 1.0);
                Assert.True(s.Dirty);
                Assert.True(s.History.CanUndo);
                Assert.Equal(SolveState.Waiting, s.Solve);
                Assert.False(s.ClipIsCurrent);
                Assert.NotSame(listBefore, s.Take.Edits); // copy-on-write: a new list was swapped in
                Assert.Equal(0, listBefore.TrimIn); // the old one is untouched
                Assert.Equal(1.0, s.Take.Edits.TrimIn);

                WaitSolved(ui, s);
                Assert.True(s.ClipIsCurrent);
                Assert.True(s.Clip.Frames < framesBefore);
            }
        }

        [Fact]
        public void AnIkEditPublishesThePlayheadBeforeReplacingTheTimeline()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                var session = host.Session;
                session.Show(SyntheticTake.Create());
                WaitSolved(ui, session);
                var previous = session.Clip;
                host.Clock.Playhead = 1.25;
                BakedClip preview = null;
                BakedClip timelineAtPreview = null;
                session.PreviewClipReady += () =>
                {
                    preview = session.PlayheadClip;
                    timelineAtPreview = session.Clip;
                };

                session.Edit("bodySource", edits => edits.BodySource = "ik");
                WaitSolved(ui, session);

                Assert.NotNull(preview);
                Assert.Equal("ik", preview.BodySource);
                Assert.Equal(1.25, preview.SourceTime[0]);
                Assert.Same(previous, timelineAtPreview);
                Assert.NotSame(previous, session.Clip);
                Assert.Null(session.PlayheadClip);
            }
        }

        [Fact]
        public void SolvingAPreviewDuringTheFullBakePreservesTheFullMotion()
        {
            var (data, _, host) = Start();
            using (data)
            using (host)
            {
                var take = SyntheticTake.Create(trackers: true);
                var options = host.Session.MakeBakeOptions();
                var expected = PoseBuilder.Bake(take, take.Profile, options);
                var previewOptions = host.Session.MakeBakeOptions();
                previewOptions.SourceRange = [1.25, 1.25];
                BakedClip preview = null;
                int checkpoints = 0;
                options.SolveCheckpoint = () =>
                {
                    if (++checkpoints == 5)
                        preview = PoseBuilder.Bake(take, take.Profile, previewOptions);
                };

                var actual = PoseBuilder.Bake(take, take.Profile, options);

                Assert.NotNull(preview);
                Assert.Equal(expected.SourceTime, actual.SourceTime);
                Assert.Equal(expected.Hips, actual.Hips);
                for (int frame = 0; frame < expected.Frames; frame++)
                    Assert.Equal(expected.Rot[frame], actual.Rot[frame]);
            }
        }

        [Fact]
        public void UndoAndRedoRestoreTheEditsAndSolveAgain()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                var s = host.Session;
                s.Show(SyntheticTake.Create());
                WaitSolved(ui, s);
                s.Edit("trimIn", e => e.TrimIn = 1.0);
                ui.Advance(1);
                s.Edit("cuts", e => e.Cuts.Add(new[] { 1.5, 2.0 }));
                WaitSolved(ui, s);

                s.Undo();
                Assert.Empty(s.Take.Edits.Cuts);
                Assert.Equal(1.0, s.Take.Edits.TrimIn);
                s.Undo();
                Assert.Equal(0, s.Take.Edits.TrimIn);
                Assert.False(s.History.CanUndo);
                s.Redo();
                s.Redo();
                Assert.Single(s.Take.Edits.Cuts);
                WaitSolved(ui, s);
                Assert.True(s.ClipIsCurrent);
            }
        }

        [Fact]
        public void ADragIsOneUndoStepAndSolvesOnceAtItsEnd()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                var s = host.Session;
                s.Show(SyntheticTake.Create());
                WaitSolved(ui, s);
                int solves = 0;
                s.ClipChanged += () => solves++;
                using (s.Gesture())
                    for (int i = 1; i <= 20; i++)
                    {
                        float x = i * 0.01f;
                        s.Edit("placement", e => e.Placement = new Pose(new Vec3(x, 0, 0), Quat.Identity), false);
                        ui.Advance(0.5); // a slow drag
                    }

                Assert.Equal(0, solves);
                Assert.Equal(1, s.History.UndoCount);
                s.RequestSolve();
                WaitSolved(ui, s);
                Assert.Equal(1, solves);
                Assert.Equal(0.2f, s.Clip.Placement.pos.x, 3);
                s.Undo();
                Assert.False(s.Take.Edits.HasPlacement);
            }
        }

        [Fact]
        public void AnEditDuringASolveCancelsItAndTheNewestStateWins()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                var s = host.Session;
                s.Show(SyntheticTake.Create(20));
                Assert.Equal(SolveState.Solving, s.Solve);
                s.Edit("trimOut", e => e.TrimOut = 2.0); // while the first solve runs
                Assert.Equal(SolveState.Waiting, s.Solve);
                WaitSolved(ui, s);
                Assert.True(s.ClipIsCurrent);
                Assert.True(s.Clip.Duration < 2.2, "the clip should be the trimmed one, not the cancelled full solve");
            }
        }

        [Fact]
        public void ASolveAfterACancelledOneGivesTheSameResultAsACleanSolve()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                var s = host.Session;
                s.Show(SyntheticTake.Create(10));
                WaitSolved(ui, s);
                var clean = s.Clip;
                s.RequestSolve(true);
                s.RequestSolve(true); // cancels the one just started
                WaitSolved(ui, s);
                var again = s.Clip;
                Assert.NotSame(clean, again);
                Assert.Equal(clean.Frames, again.Frames);
                for (int f = 0; f < clean.Frames; f += 37)
                {
                    Assert.Equal(clean.Hips[f].x, again.Hips[f].x);
                    Assert.Equal(clean.Hips[f].y, again.Hips[f].y);
                    Assert.Equal(clean.Rot[f][HumanBones.Hips].w, again.Rot[f][HumanBones.Hips].w);
                    Assert.Equal(clean.Rot[f][HumanBones.LeftUpperArm].x, again.Rot[f][HumanBones.LeftUpperArm].x);
                }
            }
        }

        [Fact]
        public async Task WhenSolvedCompletesOnceTheClipIsUpToDate()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                var s = host.Session;
                s.Show(SyntheticTake.Create());
                WaitSolved(ui, s);
                s.Edit("placement", e => e.Placement = new Pose(new Vec3(0, 0, 1), Quat.Identity), false);
                var task = s.WhenSolved();
                Assert.False(task.IsCompleted);
                WaitSolved(ui, s);
                Assert.True(await task);
                Assert.True(await s.WhenSolved()); // already current: at once
            }
        }

        [Fact]
        public async Task SavedEditsComeBackWhenTheTakeIsOpenedAgain()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                var s = host.Session;
                string path = SyntheticTake.Save(data.TakesDir);
                Assert.True(await s.OpenAsync(path));
                WaitSolved(ui, s);
                s.Edit("trimIn", e => e.TrimIn = 0.5);
                s.SetName("Renamed take");
                Assert.True(s.Dirty);
                Assert.True(await s.SaveAsync());
                Assert.False(s.Dirty);

                s.Close();
                Assert.Null(s.Take);
                Assert.True(await s.OpenAsync(path));
                Assert.Equal("Renamed take", s.Take.Name);
                Assert.Equal(0.5, s.Take.Edits.TrimIn, 6);
                Assert.False(s.Dirty);
            }
        }

        [Fact]
        public async Task RevertDropsUnsavedEdits()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                var s = host.Session;
                string path = SyntheticTake.Save(data.TakesDir);
                await s.OpenAsync(path);
                s.Edit("trimIn", e => e.TrimIn = 0.5);
                Assert.True(await s.RevertAsync());
                Assert.Equal(0, s.Take.Edits.TrimIn);
                Assert.False(s.Dirty);
            }
        }

        [Fact]
        public async Task OpeningAnotherTakeSavesTheEditsOfTheFirst()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                var s = host.Session;
                string a = SyntheticTake.Save(data.TakesDir, 2, "First"),
                    b = SyntheticTake.Save(data.TakesDir, 2, "Second");
                await s.OpenAsync(a);
                s.Edit("trimIn", e => e.TrimIn = 0.7);
                await s.OpenAsync(b);
                Assert.Equal("Second", s.Take.Name);
                Assert.Equal(0.7, RecordingIO.Load(a).Edits.TrimIn, 6);
            }
        }

        [Fact]
        public async Task AFileThatIsNotATakeIsReportedNotThrown()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                Directory.CreateDirectory(data.TakesDir);
                string bad = Path.Combine(data.TakesDir, "broken.vrmr");
                File.WriteAllText(bad, "this is not a take");
                Toast toast = null;
                host.Status.ToastRaised += t => toast = t;
                Assert.False(await host.Session.OpenAsync(bad));
                ui.RunPosted();
                Assert.Null(host.Session.Take);
                Assert.NotNull(toast);
                Assert.Equal(StatusKind.Error, toast.Kind);
            }
        }

        [Fact]
        public void ChangingTheFrameRateSolvesAgain()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                var s = host.Session;
                s.Show(SyntheticTake.Create());
                WaitSolved(ui, s);
                int frames60 = s.Clip.Frames;
                host.SettingsStore.Update(nameof(Settings.ExportFps), x => x.ExportFps = 30);
                Assert.False(s.ClipIsCurrent);
                WaitSolved(ui, s);
                Assert.True(s.Clip.Frames < frames60 * 0.6);
            }
        }

        [Fact]
        public void TheLibraryListsSearchesAndDeletesTakes()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                SyntheticTake.Save(data.TakesDir, 1, "Alpha");
                string beta = SyntheticTake.Save(data.TakesDir, 1, "Beta");
                host.Library.Refresh();
                Assert.True(ui.WaitFor(() => host.Library.Items.Count == 2));
                Assert.Contains(host.Library.Items,
                    i => i.Name == "Alpha" && i.HasDevices && i.HasFace && !i.Unreadable);
                Assert.Single(TakeLibrary.Filter(host.Library.Items, "beta"));
                Assert.Equal(2, TakeLibrary.Scan(data.TakesDir).Count);
            }
        }
    }
}
