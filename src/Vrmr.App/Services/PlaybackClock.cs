using System;
using System.Collections.Generic;

namespace VRMR.App.Services
{
    /// <summary>
    ///     The playhead of the open take, in source seconds. It only moves when <see cref="Advance" /> is called (by the
    ///     display's frame callbacks while playing) or when the user seeks; nothing here owns a timer.
    /// </summary>
    public sealed class PlaybackClock
    {
        /// <summary>Playback speeds offered by the transport.</summary>
        public static readonly double[] Speeds = { 0.25, 0.5, 1, 1.5, 2 };

        double _playhead;
        bool _playing;

        /// <summary>Position in source seconds (before trims and cuts).</summary>
        public double Playhead
        {
            get => _playhead;
            set
            {
                if (_playhead == value) return;
                _playhead = value;
                PlayheadChanged?.Invoke();
            }
        }

        public bool Playing
        {
            get => _playing;
            set
            {
                if (_playing == value) return;
                _playing = value;
                StateChanged?.Invoke();
            }
        }

        /// <summary>Start again from the first kept part at the end.</summary>
        public bool Loop { get; set; } = true;

        /// <summary>Playback rate (1 = real time).</summary>
        public double Speed { get; set; } = 1;

        /// <summary>The playhead moved.</summary>
        public event Action PlayheadChanged;

        /// <summary>Playing started or stopped.</summary>
        public event Action StateChanged;

        /// <summary>Starts playing over the kept parts of the take (from the first one when the playhead is outside them).</summary>
        public void Play(IReadOnlyList<double[]> segments)
        {
            if (segments == null || segments.Count == 0) return;
            double first = segments[0][0], last = segments[segments.Count - 1][1];
            if (_playhead >= last - 1e-3 || _playhead < first) Playhead = first;
            Playing = true;
        }

        public void Pause()
        {
            Playing = false;
        }

        /// <summary>
        ///     Moves the playhead by <paramref name="dt" /> seconds of wall time, skipping cuts and trims; loops or stops at
        ///     the end.
        /// </summary>
        public void Advance(double dt, IReadOnlyList<double[]> segments)
        {
            if (!_playing) return;
            if (segments == null || segments.Count == 0)
            {
                Playing = false;
                return;
            }

            double t = _playhead + dt * Speed;
            if (!Inside(segments, t))
            {
                double[] next = null;
                foreach (double[] s in segments)
                    if (s[0] > _playhead - 1e-6 && s[0] >= t - 1e-6)
                    {
                        next = s;
                        break;
                    }

                if (next == null)
                    foreach (double[] s in segments)
                        if (s[0] > _playhead)
                        {
                            next = s;
                            break;
                        }

                if (next != null && next[0] > _playhead) t = next[0];
                else if (Loop) t = segments[0][0];
                else
                {
                    t = segments[segments.Count - 1][1];
                    Playhead = t;
                    Playing = false;
                    return;
                }
            }

            Playhead = t;
        }

        static bool Inside(IReadOnlyList<double[]> segments, double t)
        {
            foreach (double[] s in segments)
                if (t >= s[0] && t < s[1])
                    return true;
            return false;
        }

        /// <summary>Pauses and moves by whole frames.</summary>
        public void Step(int frames, double fps, double duration)
        {
            Playing = false;
            if (fps <= 0) return;
            Playhead = Math.Max(0, Math.Min(duration, _playhead + frames / fps));
        }

        /// <summary>Pauses and jumps.</summary>
        public void Seek(double t, double duration)
        {
            Playing = false;
            Playhead = Math.Max(0, Math.Min(duration, t));
        }
    }
}
