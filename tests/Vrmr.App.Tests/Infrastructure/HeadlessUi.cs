using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace VRMR.App.Tests
{
    /// <summary>
    ///     The app as the UI tests start it: the real <see cref="App" /> (theme and styles) on the headless platform,
    ///     drawn with Skia so frames can be captured.
    /// </summary>
    public static class TestAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont();
        }
    }

    /// <summary>Runs test code on the one headless UI thread of the test process.</summary>
    public static class HeadlessUi
    {
        static readonly Lazy<HeadlessUnitTestSession> Session = new(() =>
            HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder)));

        /// <summary>Runs an action on the UI thread and waits for it.</summary>
        public static void Run(Action action)
        {
            Session.Value.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>On the UI thread: lets posted work, timers and layout run until the condition holds. Returns false on timeout.</summary>
        public static bool WaitFor(Func<bool> condition, int timeoutMs = 20000)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                Pump(5);
                if (condition()) return true;
                if (sw.ElapsedMilliseconds > timeoutMs) return false;
            }
        }

        /// <summary>Runs the dispatcher's own loop for a moment, so timers fire as they do in the running app.</summary>
        static void Pump(int ms)
        {
            var frame = new DispatcherFrame();
            var timer = new Timer(_ => Dispatcher.UIThread.Post(() => frame.Continue = false, DispatcherPriority.Send),
                null, ms, Timeout.Infinite);
            Dispatcher.UIThread.PushFrame(frame);
            timer.Dispose();
        }

        /// <summary>On the UI thread: lets everything that is due run for a while (debounce timers, background results).</summary>
        public static void Settle(int ms = 60)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) Pump(5);
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>
        ///     On the UI thread: draws the window and saves the frame as a PNG when a shots folder is set (VRMR_SHOTS).
        ///     Returns the frame.
        /// </summary>
        public static WriteableBitmap Capture(Window window, string name)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var frame = window.CaptureRenderedFrame();
            string dir = Environment.GetEnvironmentVariable("VRMR_SHOTS");
            if (!string.IsNullOrEmpty(dir) && frame != null)
            {
                Directory.CreateDirectory(dir);
                frame.Save(Path.Combine(dir, name + ".png"));
            }

            return frame;
        }
    }
}
