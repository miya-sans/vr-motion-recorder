using System;

namespace VRMR.App.Services
{
    /// <summary>The UI thread as services see it: post work back to it and schedule timers on it. Tests supply their own.</summary>
    public interface IUiDispatcher
    {
        /// <summary>True on the UI thread.</summary>
        bool CheckAccess();

        /// <summary>Runs an action on the UI thread, after the current work.</summary>
        void Post(Action action);

        /// <summary>Runs an action once on the UI thread after a delay; disposing the result cancels it.</summary>
        IDisposable After(TimeSpan delay, Action action);

        /// <summary>Runs an action repeatedly on the UI thread; disposing the result stops it.</summary>
        IDisposable Every(TimeSpan interval, Action action);
    }

    /// <summary>Runs an action once things have been quiet for a while; each call restarts the wait.</summary>
    public sealed class Debouncer : IDisposable
    {
        readonly Action _action;
        readonly TimeSpan _delay;
        readonly IUiDispatcher _ui;
        IDisposable _pending;

        public Debouncer(IUiDispatcher ui, TimeSpan delay, Action action)
        {
            _ui = ui;
            _delay = delay;
            _action = action;
        }

        /// <summary>True while a run is waiting.</summary>
        public bool Pending => _pending != null;

        public void Dispose()
        {
            Cancel();
        }

        /// <summary>Starts (or restarts) the wait.</summary>
        public void Trigger()
        {
            _pending?.Dispose();
            _pending = _ui.After(_delay, () =>
            {
                _pending = null;
                _action();
            });
        }

        /// <summary>Runs now if a run is waiting.</summary>
        public void Flush()
        {
            if (_pending == null) return;
            Cancel();
            _action();
        }

        /// <summary>Drops a waiting run.</summary>
        public void Cancel()
        {
            _pending?.Dispose();
            _pending = null;
        }
    }
}
