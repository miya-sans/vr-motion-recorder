using System.Collections.Generic;
using VRMR.App.Services;
using Xunit;

namespace VRMR.App.Tests
{
    [Collection("app")]
    public class PlaybackClockTests
    {
        static List<double[]> Segments(params double[] bounds)
        {
            var list = new List<double[]>();
            for (int i = 0; i + 1 < bounds.Length; i += 2) list.Add(new[] { bounds[i], bounds[i + 1] });
            return list;
        }

        [Fact]
        public void ItDoesNotMoveUnlessItIsPlaying()
        {
            var c = new PlaybackClock { Playhead = 1 };
            c.Advance(0.5, Segments(0, 10));
            Assert.Equal(1, c.Playhead);
        }

        [Fact]
        public void PlayingAdvancesByWallTimeTimesSpeed()
        {
            var c = new PlaybackClock { Speed = 2 };
            c.Play(Segments(0, 10));
            c.Advance(0.25, Segments(0, 10));
            Assert.Equal(0.5, c.Playhead, 6);
        }

        [Fact]
        public void PlayingSkipsACut()
        {
            var segs = Segments(0, 2, 5, 10);
            var c = new PlaybackClock { Playhead = 1.95 };
            c.Play(segs);
            c.Advance(0.1, segs);
            Assert.Equal(5, c.Playhead, 6);
            Assert.True(c.Playing);
        }

        [Fact]
        public void AtTheEndItLoopsToTheFirstKeptPart()
        {
            var segs = Segments(1, 2, 5, 6);
            var c = new PlaybackClock { Playhead = 5.95, Loop = true };
            c.Play(segs);
            c.Advance(0.1, segs);
            Assert.Equal(1, c.Playhead, 6);
            Assert.True(c.Playing);
        }

        [Fact]
        public void WithoutLoopItStopsAtTheEnd()
        {
            var segs = Segments(1, 2, 5, 6);
            var c = new PlaybackClock { Playhead = 5.95, Loop = false };
            c.Play(segs);
            c.Advance(0.1, segs);
            Assert.Equal(6, c.Playhead, 6);
            Assert.False(c.Playing);
        }

        [Fact]
        public void PlayFromOutsideTheKeptPartsStartsAtTheFirst()
        {
            var segs = Segments(3, 8);
            var c = new PlaybackClock { Playhead = 0.5 };
            c.Play(segs);
            Assert.Equal(3, c.Playhead, 6);
            c.Playhead = 8; c.Playing = false;
            c.Play(segs);
            Assert.Equal(3, c.Playhead, 6);
        }

        [Fact]
        public void NothingKeptMeansNothingPlays()
        {
            var c = new PlaybackClock();
            c.Play(Segments());
            Assert.False(c.Playing);
            c.Playing = true;
            c.Advance(0.1, Segments());
            Assert.False(c.Playing);
        }

        [Fact]
        public void StepMovesByFramesPausesAndStaysInsideTheTake()
        {
            var c = new PlaybackClock { Playhead = 1, Playing = true };
            c.Step(3, 60, 10);
            Assert.False(c.Playing);
            Assert.Equal(1.05, c.Playhead, 6);
            c.Step(-1000, 60, 10);
            Assert.Equal(0, c.Playhead, 6);
            c.Step(100000, 60, 10);
            Assert.Equal(10, c.Playhead, 6);
        }

        [Fact]
        public void PlayheadAndStateChangesAreAnnounced()
        {
            var c = new PlaybackClock();
            int moved = 0, state = 0;
            c.PlayheadChanged += () => moved++;
            c.StateChanged += () => state++;
            c.Playhead = 2; c.Playhead = 2;
            c.Playing = true; c.Playing = true; c.Pause();
            Assert.Equal(1, moved);
            Assert.Equal(2, state);
        }
    }
}
