using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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

        /// <summary>Runs an action on the UI thread without blocking the test runner.</summary>
        public static Task RunAsync(Action action)
        {
            return Session.Value.Dispatch(action, CancellationToken.None);
        }

        /// <summary>Runs asynchronous work on the UI thread, retaining UI access after awaits.</summary>
        public static Task RunAsync(Func<Task> action)
        {
            return Session.Value.Dispatch(async () =>
            {
                await action();
                return true;
            }, CancellationToken.None);
        }

        /// <summary>On the UI thread: lets posted work, timers and layout run until the condition holds. Returns false on timeout.</summary>
        public static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 20000)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                Dispatcher.UIThread.VerifyAccess();
                if (condition()) return true;
                if (sw.ElapsedMilliseconds > timeoutMs) return false;
                await Task.Delay(5);
            }
        }

        /// <summary>On the UI thread: lets everything that is due run for a while (debounce timers, background results).</summary>
        public static async Task SettleAsync(int ms = 60)
        {
            Dispatcher.UIThread.VerifyAccess();
            await Task.Delay(ms);
            Dispatcher.UIThread.VerifyAccess();
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
                frame.Save(Path.Combine(dir, name + ".png"), PngBitmapEncoderOptions.Default);
            }

            return frame;
        }
    }
}
