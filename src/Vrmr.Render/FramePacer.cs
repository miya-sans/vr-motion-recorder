using System;

namespace VRMR.Render
{
    /// <summary>
    ///     Decides on which display frames an animating view ticks, so that it runs at the frame cap: every display frame
    ///     when the cap is at or above the display's rate, every second one for half of it, and so on. The callbacks need
    ///     not be evenly spaced. Pure arithmetic on the times it is given, so it can be tested without a display.
    /// </summary>
    sealed class FramePacer
    {
        const int Window = 32;
        readonly double[] _gaps = new double[Window];
        int _gapCount, _gapNext;
        double _lastDisplay = double.NaN, _lastTick = double.NaN, _nextDue = double.NaN;

        /// <summary>The display's frame interval in seconds as measured from the callbacks so far (1/60 until there are enough).</summary>
        public double DisplayInterval { get; private set; } = 1.0 / 60.0;

        /// <summary>Forgets the phase: the next display frame ticks. Call when animation starts.</summary>
        public void Reset() { _lastDisplay = _lastTick = _nextDue = double.NaN; }

        /// <summary>
        ///     Call once per display frame with the current time in seconds. Returns true when the view should tick now;
        ///     elapsed is then the time since the previous tick (one cap interval for the first).
        /// </summary>
        public bool Tick(double now, double cap, out double elapsed)
        {
            cap = Math.Max(1.0, cap);
            double interval = 1.0 / cap;
            if (!double.IsNaN(_lastDisplay)) Observe(now - _lastDisplay);
            _lastDisplay = now;
            elapsed = double.IsNaN(_lastTick) ? interval : now - _lastTick;

            bool due;
            if (double.IsNaN(_nextDue) || interval <= DisplayInterval * 1.1)
            {
                // the cap is not below what the display does anyway: every frame, no accumulated phase to drift
                due = true;
                _nextDue = now + interval;
            }
            else
            {
                // tick on the display frame nearest to the due time
                due = now + DisplayInterval * 0.5 >= _nextDue;
                if (due)
                {
                    // a late tick is followed by the next callback, so unevenly spaced callbacks still reach the cap,
                    // but lateness beyond that is dropped: no catching up in a burst
                    _nextDue = Math.Max(_nextDue + interval, now);
                }
            }

            if (due) _lastTick = now;
            return due;
        }

        /// <summary>
        ///     Tracks the display interval as the mean gap of the recent callbacks: callbacks that arrive in short and long
        ///     pairs still average to the rate they come at, where a median would pick one of the two.
        /// </summary>
        void Observe(double gap)
        {
            if (gap < 0.002 || gap > 0.1) return; // a hitch or a pause tells nothing about the display
            _gaps[_gapNext] = gap;
            _gapNext = (_gapNext + 1) % Window;
            if (_gapCount < Window) _gapCount++;
            if (_gapCount < 8) return;
            double sum = 0;
            for (int i = 0; i < _gapCount; i++) sum += _gaps[i];
            DisplayInterval = sum / _gapCount;
        }
    }

    /// <summary>Counters of the 3D view, for the idle check, benchmarks and a debug overlay.</summary>
    public sealed class ViewportStats
    {
        /// <summary>Frames drawn since the view was created.</summary>
        public long FramesDrawn { get; internal set; }

        /// <summary>CPU time the last frame took on the UI thread, in milliseconds (building it and issuing the GL calls).</summary>
        public double LastFrameCpuMs { get; internal set; }

        /// <summary>Managed memory the last frame allocated, in bytes. Zero in steady state.</summary>
        public long LastFrameAllocatedBytes { get; internal set; }

        /// <summary>Time between the last two frames, in milliseconds.</summary>
        public double LastFrameGapMs { get; internal set; }

        /// <summary>Avatar mesh triangles in the last frame.</summary>
        public int Triangles { get; internal set; }

        /// <summary>The OpenGL implementation and the graphics adapter, once the first frame was drawn.</summary>
        public string GlVersion { get; internal set; } = "";

        public string Adapter { get; internal set; } = "";
    }
}
