using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using VRMR.App.Composition;
using VRMR.App.Platform;
using VRMR.App.Services;
using VRMR.App.ViewModels;
using VRMR.App.Viewport;
using VRMR.App.Views;

namespace VRMR.App
{
    /// <summary>The Avalonia application: loads the theme, builds the host and the window, and shuts both down.</summary>
    public class App : Application
    {
        RegisteredWaitHandle _activateWait;
        AppHost _host;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            // UI exceptions are logged and shown as a toast; the app keeps running
            Dispatcher.UIThread.UnhandledException += (s, e) =>
            {
                AppLog.Error("UI: " + e.Exception);
                e.Handled = true;
                _host?.Status.Show(new Toast
                {
                    Text = "Something went wrong: " + e.Exception.Message +
                           " The details are in the log (Settings → About).",
                    Kind = StatusKind.Error, Duration = TimeSpan.FromSeconds(8)
                });
            };

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var ui = new AvaloniaUiDispatcher();
                IGlobalHotkey hotkey = OperatingSystem.IsWindows() ? new Win32GlobalHotkey() : null;
                _host = new AppHost(Program.Options, ui, hotkey);
                ApplyTheme(this, _host.UiState.GetString("theme", "dark"));
                var shell = new ShellViewModel(_host);
                var window = new MainWindow(shell) { ShowActivated = !Program.Options.Background };
                desktop.MainWindow = window;
                if (Program.Options.Benchmark)
                    window.Opened += async (_, _) => await RunBenchmarkAsync(desktop, window, shell);
                else if (!shell.FirstRun.OpenIfFirstRun()) shell.ReopenLastTake();
                _host.Session.SolveChanged += () =>
                {
                    if (_host.Session.Solve == SolveState.Done) LogDrawCounts(window, "after a solve");
                };
                window.Closing += (o, e) => LogDrawCounts(window, "at exit");
                desktop.ShutdownRequested += (o, e) => DisposeHost();
                desktop.Exit += (o, e) => DisposeHost();
                ListenForActivation(window);
            }

            base.OnFrameworkInitializationCompleted();
        }

        static async Task RunBenchmarkAsync(IClassicDesktopStyleApplicationLifetime desktop,
            MainWindow window, ShellViewModel shell)
        {
            using var cancellation = new CancellationTokenSource();
            window.Closing += Cancel;
            int exitCode = 0;
            try { await new ViewportBenchmark(window, shell).RunAsync(cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                AppLog.Warn("Benchmark cancelled.");
            }
            catch (Exception ex)
            {
                AppLog.Error("Benchmark: " + ex.Message);
                exitCode = 1;
            }
            finally { window.Closing -= Cancel; }

            if (!cancellation.IsCancellationRequested) desktop.Shutdown(exitCode);

            void Cancel(object sender, WindowClosingEventArgs e)
            {
                cancellation.Cancel();
            }
        }

        /// <summary>"dark", "light" or "system".</summary>
        public static void ApplyTheme(Application app, string theme)
        {
            app.RequestedThemeVariant = theme == "light" ? ThemeVariant.Light :
                theme == "system" ? ThemeVariant.Default : ThemeVariant.Dark;
        }

        /// <summary>A second launch signals the first: bring its window to the front.</summary>
        void ListenForActivation(Window window)
        {
            var signal = Program.ActivateSignal;
            if (signal == null) return;
            _activateWait = ThreadPool.RegisterWaitForSingleObject(signal, (state, timedOut) =>
                Dispatcher.UIThread.Post(() =>
                {
                    if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                    window.Activate();
                }), null, Timeout.Infinite, false);
        }

        /// <summary>Writes how often the view and the timeline drew so far (the idle check reads it from the log).</summary>
        static void LogDrawCounts(MainWindow window, string when)
        {
            AppLog.Info(
                $"Frames drawn {when}: view {window.Viewport.FramesDrawn}, timeline {window.TimelineView.FramesDrawn}");
        }

        void DisposeHost()
        {
            _activateWait?.Unregister(null);
            _activateWait = null;
            var host = _host;
            _host = null;
            host?.Dispose();
        }
    }
}
