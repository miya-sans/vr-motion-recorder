using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VRMR.App.ViewModels;
using VRMR.App.Views;
using VRMR.Render;

namespace VRMR.App.Viewport
{
    sealed class ViewportBenchmark(MainWindow window, ShellViewModel shell)
    {
        static readonly TimeSpan Warmup = TimeSpan.FromSeconds(1.5);
        readonly TimeSpan _duration = TimeSpan.FromSeconds(shell.Host.Options.BenchmarkSeconds);
        readonly Process _process = Process.GetCurrentProcess();
        readonly int _uiThread = OperatingSystem.IsWindows() ? GetCurrentThreadId() : 0;
        readonly GpuView _view = window.RenderViewport;

        public async Task RunAsync(CancellationToken cancellation)
        {
            using var process = _process;
            var host = shell.Host;
            var takes = shell.Takes;
            var stats = _view.Stats;
            if (host.Options.BenchmarkLive)
            {
                takes.ShowMesh = true;
                takes.ShowDevices = true;
                takes.ShowHeadset = true;
                takes.ShowPlayArea = true;
                takes.ShowBaseStations = true;
                await WaitForAsync(() => host.Live.HasLiveMotion && _view.Animating && stats.FramesDrawn > 0,
                    "No live motion or viewport frames arrived. Check that the live sources and GPU view are working.",
                    cancellation);
                await MeasureAsync("Live preview", cancellation);
                return;
            }

            takes.ShowMesh = true;
            takes.ShowSkeleton = false;
            if (!await host.Session.OpenAsync(host.Options.BenchmarkTake).WaitAsync(cancellation))
                throw new InvalidOperationException("The benchmark take could not be opened.");
            if (!await host.Session.WhenSolved().WaitAsync(cancellation) || host.Session.Clip?.Profile == null)
                throw new InvalidOperationException("The benchmark take needs a solved avatar. " + host.Session.SolveError);

            takes.SeekStartCommand.Execute(null);
            if (_view.PendingSceneMesh is { } mesh)
                await mesh.WaitAsync(cancellation);
            await WaitForAsync(() => stats.Triangles > 0,
                "The benchmark avatar mesh did not draw. Check the avatar model and GPU view.", cancellation);

            await MeasureAsync("Paused, mesh", cancellation, expectFrames: false);
            takes.Loop = true;
            host.Clock.Speed = 1;
            takes.TogglePlayCommand.Execute(null);
            if (!host.Clock.Playing)
                throw new InvalidOperationException("The benchmark take has no playable segment.");
            await MeasureAsync("Playing, mesh", cancellation);
            takes.ShowSkeleton = true;
            takes.ShowMesh = false;
            await MeasureAsync("Playing, skeleton only", cancellation);

            takes.ShowSkeleton = false;
            takes.ShowMesh = true;
            takes.ShowDevices = true;
            takes.ShowHeadset = true;
            takes.ShowPlayArea = true;
            takes.ShowBaseStations = true;
            host.TakePresenter.BenchmarkDevices = new SimulatedBenchmarkDevices(Clock.Now);
            try
            {
                takes.Present();
                await MeasureAsync("Playing, mesh and 14 simulated devices", cancellation);
            }
            finally { host.TakePresenter.BenchmarkDevices = null; }
        }

        async Task MeasureAsync(string name, CancellationToken cancellation, bool expectFrames = true)
        {
            var stats = _view.Stats;
            int capacity = (int)(_duration.TotalSeconds * 400) + 1000;
            double[] gaps = new double[capacity], cpu = new double[capacity];
            long[] allocations = new long[capacity];
            int samples = 0;
            await Task.Delay(Warmup, cancellation);

            AppLog.Info(FormattableString.Invariant(
                $"Benchmark {name} at {_view.Bounds.Width * window.RenderScaling:0}x{_view.Bounds.Height * window.RenderScaling:0} px, cap {_view.FrameCap:0} FPS, MSAA {_view.Msaa}, {stats.Triangles} triangles, GPU {stats.Adapter}, GL {stats.GlVersion}"));
            _process.Refresh();
            TimeSpan processStart = _process.TotalProcessorTime, uiStart = UiThreadCpu();
            long framesStart = stats.FramesDrawn, timelineStart = window.TimelineView.FramesDrawn;
            long allocatedStart = GC.GetTotalAllocatedBytes(), start = Stopwatch.GetTimestamp();
            _view.DrawOverlay += SamplePreviousFrame;
            try { await Task.Delay(_duration, cancellation); }
            finally { _view.DrawOverlay -= SamplePreviousFrame; }

            double wall = Stopwatch.GetElapsedTime(start).TotalSeconds;
            long allocated = GC.GetTotalAllocatedBytes() - allocatedStart;
            long frames = stats.FramesDrawn - framesStart;
            long timeline = window.TimelineView.FramesDrawn - timelineStart;
            _process.Refresh();
            double cores = (_process.TotalProcessorTime - processStart).TotalSeconds / wall;
            string uiCpu = _uiThread == 0
                ? "n/a"
                : FormattableString.Invariant($"{(UiThreadCpu() - uiStart).TotalSeconds / wall:P1}");
            if (expectFrames && frames == 0)
                throw new InvalidOperationException("The viewport drew no frames during " + name + ".");

            AppLog.Info(FormattableString.Invariant(
                $"Benchmark {name}: {frames / wall:0.0} FPS ({frames} frames in {wall:0.00} s), timeline {timeline / wall:0.0} FPS, process {cores:0.000} CPU cores, UI thread {uiCpu}, process allocations {allocated / wall / 1024:0.0} KiB/s"));
            if (samples == 0) return;

            Array.Sort(gaps, 0, samples);
            Array.Sort(cpu, 0, samples);
            Array.Sort(allocations, 0, samples);
            int p50 = (samples - 1) / 2, p95 = (int)Math.Ceiling(samples * 0.95) - 1;
            AppLog.Info(FormattableString.Invariant(
                $"Benchmark {name}: frame gap p50/p95 {gaps[p50]:0.00}/{gaps[p95]:0.00} ms, frame CPU p50/p95 {cpu[p50]:0.00}/{cpu[p95]:0.00} ms, viewport allocations p50/max {allocations[p50]}/{allocations[samples - 1]} B per frame"));

            void SamplePreviousFrame(ViewportControl view, OverlayList overlay)
            {
                if (stats.FramesDrawn <= framesStart + 1 || samples == capacity) return;
                gaps[samples] = stats.LastFrameGapMs;
                cpu[samples] = stats.LastFrameCpuMs;
                allocations[samples] = stats.LastFrameAllocatedBytes;
                samples++;
            }
        }

        static async Task WaitForAsync(Func<bool> ready, string error, CancellationToken cancellation)
        {
            long start = Stopwatch.GetTimestamp();
            while (!ready())
            {
                if (Stopwatch.GetElapsedTime(start) >= TimeSpan.FromSeconds(15))
                    throw new InvalidOperationException(error);
                await Task.Delay(100, cancellation);
            }
        }

        TimeSpan UiThreadCpu()
        {
            if (_uiThread == 0) return TimeSpan.Zero;
            foreach (ProcessThread thread in _process.Threads)
            {
                using (thread)
                    if (thread.Id == _uiThread) return thread.TotalProcessorTime;
            }

            return TimeSpan.Zero;
        }

        [DllImport("kernel32.dll")]
        static extern int GetCurrentThreadId();
    }
}
