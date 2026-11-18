using System;
using System.Threading.Tasks;

namespace VRMR.App.Services
{
    /// <summary>
    ///     What was found around the app (SteamVR, VRChat and its OSC switch, Blender, Unity projects, the app's own
    ///     folders), for the first-run guide and the status rows in Settings. The look runs on a worker: once at start,
    ///     again when a folder or path setting changes, and whenever asked.
    /// </summary>
    public sealed class EnvironmentService
    {
        readonly AppPaths _paths;
        readonly SettingsStore _settings;
        readonly IUiDispatcher _ui;
        int _run, _pending;

        public EnvironmentService(AppPaths paths, SettingsStore settings, IUiDispatcher ui)
        {
            _paths = paths;
            _settings = settings;
            _ui = ui;
            _settings.Changed += what =>
            {
                if (Affects(what)) _ = RefreshAsync();
            };
            _ = RefreshAsync();
        }

        /// <summary>What the last look found; null until the first one is done.</summary>
        public EnvironmentReport Report { get; private set; }

        /// <summary>True while a look is running.</summary>
        public bool Checking => _pending > 0;

        /// <summary>Raised on the UI thread when a look started or its report arrived.</summary>
        public event Action Changed;

        /// <summary>One line of the report by its id (<see cref="EnvironmentProbe" />'s constants), or null.</summary>
        public ProbeItem Item(string id)
        {
            return Report?[id];
        }

        /// <summary>Looks again in the background. The task completes on the UI thread once <see cref="Report" /> is up to date.</summary>
        public Task RefreshAsync()
        {
            int run = ++_run;
            _pending++;
            var options = _settings.Value.ProbeOptions();
            var done = new TaskCompletionSource<bool>();
            Changed?.Invoke();
            Task.Run(() =>
            {
                EnvironmentReport report = null;
                try { report = EnvironmentProbe.Run(_paths, options); }
                catch (Exception ex) { AppLog.Warn("Environment check: " + ex.Message); }

                _ui.Post(() =>
                {
                    _pending--;
                    // a look started later knows more: only the newest one is kept
                    if (report != null && run == _run) Report = report;
                    Changed?.Invoke();
                    done.SetResult(true);
                });
            });
            return done.Task;
        }

        static bool Affects(string what)
        {
            return what == "" || what == nameof(Settings.BlenderPath) || what == nameof(Settings.RecordingsDir) ||
                   what == nameof(Settings.AvatarLibraryDir) || what == nameof(Settings.UnityProjects);
        }
    }
}
