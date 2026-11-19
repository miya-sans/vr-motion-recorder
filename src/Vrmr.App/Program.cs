using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;

namespace VRMR.App
{
    static class Program
    {
        /// <summary>The options this process was started with (read by <see cref="App" />).</summary>
        public static AppOptions Options { get; private set; } = AppOptions.Parse(Array.Empty<string>());

        /// <summary>Set by another launch of the app: this instance should come to the front.</summary>
        public static EventWaitHandle ActivateSignal { get; private set; }

        [STAThread]
        public static int Main(string[] args)
        {
            Options = AppOptions.Parse(args);
            if (Options.Error != null)
            {
                Console.Error.WriteLine(Options.Error);
                Console.Error.WriteLine(AppOptions.Usage);
                return 2;
            }

            // one instance per data folder: the default data shares its name with the previous app, so the two never
            // fight over the hotkey and the OSC ports
            string id = "VRMotionRecorder.SingleInstance" +
                        (Options.DataDir == null ? "" : "." + ShortHash(Options.DataDir));
            using var mutex = new Mutex(true, id, out bool first);
            using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, id + ".Activate");
            if (!first)
            {
                activate.Set(); // the running instance comes to the front
                return 1;
            }

            ActivateSignal = activate;

            UsePaths(Options);
            AppLog.Init(Options.Paths.LogDir);
            AppLog.Info("VR Motion Recorder starting" + (Options.DataDir != null ? " (portable data)" : "") +
                        (Options.NoCapture ? " (no capture)" : ""));
            AppDomain.CurrentDomain.UnhandledException += (s, e) => AppLog.Error("Fatal: " + e.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                AppLog.Error("Background task: " + e.Exception);
                e.SetObserved();
            };

            try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
            catch (Exception ex)
            {
                AppLog.Error("Fatal: " + ex);
                return 3;
            }
            finally { AppLog.Close(); }
        }

        /// <summary>The Avalonia app (also used by the XAML previewer).</summary>
        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont();
        }

        /// <summary>Points the whole process at the options' folders. Call before anything loads settings.</summary>
        public static void UsePaths(AppOptions options)
        {
            AppPaths.Use(options.Paths);
        }

        static string ShortHash(string text)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToLowerInvariant()));
            return Convert.ToHexString(hash, 0, 6);
        }
    }
}
