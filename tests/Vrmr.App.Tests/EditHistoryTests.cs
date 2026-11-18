using VRMR.App.Services;
using Xunit;

namespace VRMR.App.Tests
{
    [Collection("app")]
    public class EditHistoryTests
    {
        double _now;

        EditHistory New(string start = "a")
        {
            var h = new EditHistory(() => _now);
            h.Reset(start);
            return h;
        }

        [Fact]
        public void UndoReturnsThePreviousStateAndRedoTheNext()
        {
            var h = New();
            _now = 1;
            h.Commit("b", "x");
            _now = 2;
            h.Commit("c", "y");
            Assert.Equal("b", h.Undo("c"));
            Assert.Equal("a", h.Undo("b"));
            Assert.Null(h.Undo("a"));
            Assert.Equal("b", h.Redo());
            Assert.Equal("c", h.Redo());
            Assert.Null(h.Redo());
        }

        [Fact]
        public void QuickChangesToTheSameThingAreOneStep()
        {
            var h = New();
            _now = 1.0;
            h.Commit("b", "smoothing");
            _now = 1.1;
            h.Commit("c", "smoothing");
            _now = 1.2;
            h.Commit("d", "smoothing");
            Assert.Equal(1, h.UndoCount);
            Assert.Equal("a", h.Undo("d"));
        }

        [Fact]
        public void ChangesToDifferentThingsAreSeparateSteps()
        {
            var h = New();
            _now = 1.0;
            h.Commit("b", "trimIn");
            _now = 1.1;
            h.Commit("c", "trimOut");
            Assert.Equal(2, h.UndoCount);
        }

        [Fact]
        public void SlowChangesToTheSameThingAreSeparateSteps()
        {
            var h = New();
            _now = 1.0;
            h.Commit("b", "smoothing");
            _now = 1.0 + EditHistory.MergeWindow + 0.1;
            h.Commit("c", "smoothing");
            Assert.Equal(2, h.UndoCount);
        }

        [Fact]
        public void AWholeDragIsOneStepHoweverLongItTakes()
        {
            var h = New();
            using (h.Gesture())
            {
                _now = 1;
                h.Commit("b", "placement");
                _now = 5;
                h.Commit("c", "placement");
                _now = 9;
                h.Commit("d", "trimIn"); // even across fields
            }

            Assert.Equal(1, h.UndoCount);
            Assert.Equal("a", h.Undo("d"));
        }

        [Fact]
        public void TheChangeAfterADragIsItsOwnStep()
        {
            var h = New();
            using (h.Gesture())
            {
                _now = 1;
                h.Commit("b", "placement");
            }

            _now = 1.05;
            h.Commit("c", "placement");
            Assert.Equal(2, h.UndoCount);
        }

        [Fact]
        public void ADragThatChangesNothingAddsNoStep()
        {
            var h = New();
            using (h.Gesture()) { h.Commit("a", "placement"); }

            Assert.False(h.CanUndo);
        }

        [Fact]
        public void ANewEditClearsRedo()
        {
            var h = New();
            _now = 1;
            h.Commit("b", "x");
            h.Undo("b");
            Assert.True(h.CanRedo);
            _now = 2;
            h.Commit("c", "y");
            Assert.False(h.CanRedo);
        }

        [Fact]
        public void UndoFirstKeepsAnEditThatWasNotCommittedYet()
        {
            var h = New();
            _now = 1;
            h.Commit("b", "x");
            // the state moved on to "c" without a commit: undo makes it a step, then steps back to "b"
            Assert.Equal("b", h.Undo("c"));
            Assert.Equal("c", h.Redo());
        }

        [Fact]
        public void TheHistoryIsCapped()
        {
            var h = New("0");
            for (int i = 1; i <= EditHistory.Limit + 50; i++)
            {
                _now = i;
                h.Commit(i.ToString(), "k" + i);
            }

            Assert.Equal(EditHistory.Limit, h.UndoCount);
        }

        [Fact]
        public void ResetStartsAFreshHistory()
        {
            var h = New();
            _now = 1;
            h.Commit("b", "x");
            h.Reset("z");
            Assert.False(h.CanUndo);
            Assert.False(h.CanRedo);
        }
    }
}
