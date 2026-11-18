using System;
using System.Threading.Tasks;

namespace VRMR.App.Services
{
    /// <summary>The app settings plus saving them off the UI thread: changes are written a moment later, once.</summary>
    public sealed class SettingsStore : IDisposable
    {
        readonly Debouncer _save;
        readonly object _writeLock = new object();

        public SettingsStore(Settings settings, IUiDispatcher ui)
        {
            Value = settings;
            _save = new Debouncer(ui, TimeSpan.FromMilliseconds(400), () => Task.Run(Write));
        }

        /// <summary>The settings object the engine shares.</summary>
        public Settings Value { get; }

        /// <summary>Raised on the UI thread after a setting changed; the argument names what changed (a field name, or "" for several).</summary>
        public event Action<string> Changed;

        /// <summary>Applies a change, tells listeners and schedules the save.</summary>
        public void Update(string what, Action<Settings> change)
        {
            change(Value);
            NotifyChanged(what);
        }

        /// <summary>For changes made directly on <see cref="Value"/>: tells listeners and schedules the save.</summary>
        public void NotifyChanged(string what = "")
        {
            _save.Trigger();
            Changed?.Invoke(what ?? "");
        }

        /// <summary>Writes pending changes now (on exit).</summary>
        public void Flush()
        {
            if (!_save.Pending) return;
            _save.Cancel();
            Write();
        }

        void Write() { lock (_writeLock) Value.Save(); }

        public void Dispose() => Flush();
    }
}
