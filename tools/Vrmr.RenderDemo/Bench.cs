using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Threading;
using VRMR.Render;

namespace VRMR.RenderDemo
{
    /// <summary>
    /// Measures the 3D view in the states the old view was measured in: paused with the mesh, playing with the mesh,
    /// playing with the skeleton only, and a live view with fourteen moving devices plus the mesh. Per state: frames per
    /// second, the gaps between frames, CPU cores used by the whole process, the UI thread's share, and allocations.
    /// </summary>
    internal sealed class Bench
    {
        sealed class State
        {
            public string Name;
            public Action Enter;
        }

        readonly DemoWindow _window;
        readonly double _seconds;
        readonly Action<string> _print;
        readonly Action _done;
        readonly List<State> _states = new List<State>();
        readonly double[] _gaps, _cpu;
        readonly long[] _alloc;
        readonly Process _process = Process.GetCurrentProcess();
        readonly int _uiThread = OperatingSystem.IsWindows() ? GetCurrentThreadId() : 0;
        readonly StringBuilder _report = new StringBuilder();
        int _samples, _state = -1;
        long _lastFrameTicks, _frames0, _gcBytes0, _wall0;
        TimeSpan _processCpu0, _uiCpu0;
        bool _measuring;

        const double WarmUpSeconds = 1.5;

        [DllImport("kernel32.dll")] static extern int GetCurrentThreadId();

        public Bench(DemoWindow window, double seconds, Action<string> print, Action done)
        {
            _window = window; _seconds = seconds; _print = print; _done = done;
            int capacity = (int)(seconds * 400) + 1000;
            _gaps = new double[capacity]; _cpu = new double[capacity]; _alloc = new long[capacity];
            var view = window.View;
            view.DrawOverlay += OnFrameDrawn;
            _states.Add(new State { Name = "(a) take open, paused, mesh", Enter = () => { view.ShowMesh = true; window.LiveDevices = false; window.Playing = false; window.Seek(120); } });
            _states.Add(new State { Name = "(b) playing, mesh", Enter = () => { view.ShowMesh = true; window.LiveDevices = false; window.Playing = true; } });
            _states.Add(new State { Name = "(c) playing, skeleton only", Enter = () => { view.ShowMesh = false; window.LiveDevices = false; window.Playing = true; } });
            _states.Add(new State { Name = "(d) live: 14 moving devices + mesh", Enter = () => { view.ShowMesh = true; window.Playing = true; window.LiveDevices = true; } });
        }

        public void Start()
        {
            var view = _window.View;
            _print($"view {(int)(view.Bounds.Width * _window.RenderScaling)}x{(int)(view.Bounds.Height * _window.RenderScaling)} px (scaling {_window.RenderScaling:0.##}), display {DisplayHz()} Hz, frame cap {view.FrameCap:0}, MSAA {view.Msaa}");
            _print($"GPU: {view.Stats.Adapter}");
            _print($"GL: {view.Stats.GlVersion}");
            _print($"mesh: {view.Stats.Triangles} triangles drawn in the last frame; {Environment.ProcessorCount} logical CPUs; {_seconds:0} s per state");
            _print("");
            _print("| State | Frames/s | Frame gap p50 / p95 | CPU cores | UI thread | Alloc (process) | Frame CPU p50 / p95 | Alloc in the view per frame |");
            _print("|---|---|---|---|---|---|---|---|");
            Next();
        }

        void Next()
        {
            if (++_state >= _states.Count) { _window.Playing = false; _window.LiveDevices = false; _done(); return; }
            _states[_state].Enter();
            DispatcherTimer.RunOnce(Begin, TimeSpan.FromSeconds(WarmUpSeconds));
        }

        void Begin()
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            _samples = 0; _lastFrameTicks = 0;
            _frames0 = _window.View.Stats.FramesDrawn;
            _gcBytes0 = GC.GetTotalAllocatedBytes(false);
            _process.Refresh();
            _processCpu0 = _process.TotalProcessorTime;
            _uiCpu0 = UiThreadCpu();
            _wall0 = Stopwatch.GetTimestamp();
            _measuring = true;
            DispatcherTimer.RunOnce(End, TimeSpan.FromSeconds(_seconds));
        }

        /// <summary>Runs inside every drawn frame: notes when it came, and what the previous one cost.</summary>
        void OnFrameDrawn(ViewportControl view, OverlayList overlay)
        {
            if (!_measuring) return;
            long now = Stopwatch.GetTimestamp();
            if (_lastFrameTicks != 0 && _samples < _gaps.Length)
            {
                _gaps[_samples] = (now - _lastFrameTicks) * 1000.0 / Stopwatch.Frequency;
                _cpu[_samples] = view.Stats.LastFrameCpuMs;
                _alloc[_samples] = view.Stats.LastFrameAllocatedBytes;
                _samples++;
            }
            _lastFrameTicks = now;
        }

        void End()
        {
            _measuring = false;
            double wall = Stopwatch.GetElapsedTime(_wall0).TotalSeconds;
            long gcBytes = GC.GetTotalAllocatedBytes(false) - _gcBytes0;
            _process.Refresh();
            double cores = (_process.TotalProcessorTime - _processCpu0).TotalSeconds / wall;
            double ui = (UiThreadCpu() - _uiCpu0).TotalSeconds / wall;
            long frames = _window.View.Stats.FramesDrawn - _frames0;
            Array.Sort(_gaps, 0, _samples); Array.Sort(_cpu, 0, _samples); Array.Sort(_alloc, 0, _samples);
            string gap = _samples == 0 ? "–" : $"{P(_gaps, 0.5):0.0} / {P(_gaps, 0.95):0.0} ms";
            string cpu = _samples == 0 ? "–" : $"{P(_cpu, 0.5):0.00} / {P(_cpu, 0.95):0.00} ms";
            string alloc = _samples == 0 ? "–" : $"p50 {_alloc[_samples / 2]} B, max {_alloc[_samples - 1]} B";
            _print($"| {_states[_state].Name} | {frames / wall:0.0} | {gap} | {cores:0.000} | {ui * 100:0.0} % | {gcBytes / wall / 1024:0.0} KB/s | {cpu} | {alloc} |");
            Next();
        }

        double P(double[] sorted, double q) => sorted[Math.Min(_samples - 1, (int)(_samples * q))];

        TimeSpan UiThreadCpu()
        {
            if (_uiThread == 0) return TimeSpan.Zero;
            foreach (ProcessThread t in _process.Threads)
                if (t.Id == _uiThread) { try { return t.TotalProcessorTime; } catch (InvalidOperationException) { return TimeSpan.Zero; } }
            return TimeSpan.Zero;
        }

        // ---- the display's refresh rate (Windows)

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public short SpecVersion, DriverVersion, Size, DriverExtra;
            public int Fields, PositionX, PositionY, Orientation, FixedOutput;
            public short Color, Duplex, YResolution, TTOption, Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
            public short LogPixels;
            public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettingsW(string device, int mode, ref DevMode devMode);

        static string DisplayHz()
        {
            if (!OperatingSystem.IsWindows()) return "?";
            var mode = new DevMode(); mode.Size = (short)Marshal.SizeOf<DevMode>();
            return EnumDisplaySettingsW(null, -1, ref mode) ? mode.DisplayFrequency.ToString() : "?";
        }
    }
}
