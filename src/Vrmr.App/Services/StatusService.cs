using System;
using System.Collections.Generic;

namespace VRMR.App.Services
{
    /// <summary>How a message is shown: neutral, a success, a warning, or an error.</summary>
    public enum StatusKind { Info, Success, Warning, Error }

    /// <summary>A short message that appears over the view for a few seconds, with at most one action.</summary>
    public sealed class Toast
    {
        public string Text { get; init; } = "";
        public StatusKind Kind { get; init; }
        /// <summary>The action button's text (null = no button).</summary>
        public string ActionLabel { get; init; }
        public Action Action { get; init; }
        /// <summary>How long it stays; errors stay longer.</summary>
        public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(4);
    }

    /// <summary>Work in progress shown in the status bar; dispose it when the work ends.</summary>
    public sealed class StatusProgress : IDisposable
    {
        readonly StatusService _owner;
        internal StatusProgress(StatusService owner, string label) { _owner = owner; Label = label; }

        public string Label { get; private set; }
        /// <summary>0..1, or a negative value while the amount is unknown.</summary>
        public double Fraction { get; private set; } = -1;

        /// <summary>Updates the bar (any thread).</summary>
        public void Report(double fraction, string label = null)
        {
            Fraction = fraction;
            if (label != null) Label = label;
            _owner.ProgressTouched();
        }

        public void Dispose() => _owner.End(this);
    }

    /// <summary>
    /// Routine results go here instead of into dialogs: one line in the status bar (the last message stays readable),
    /// running work with its progress, and toasts for results that deserve a glance or offer an action.
    /// </summary>
    public sealed class StatusService
    {
        readonly IUiDispatcher _ui;
        readonly List<StatusProgress> _running = new List<StatusProgress>();

        public StatusService(IUiDispatcher ui) { _ui = ui; }

        /// <summary>The last message.</summary>
        public string Message { get; private set; } = "";
        public StatusKind Kind { get; private set; }

        /// <summary>The newest work in progress, or null.</summary>
        public StatusProgress Current { get { lock (_running) return _running.Count > 0 ? _running[_running.Count - 1] : null; } }

        /// <summary>Raised on the UI thread when the message or the progress changed.</summary>
        public event Action Changed;

        /// <summary>Raised on the UI thread for each toast.</summary>
        public event Action<Toast> ToastRaised;

        public void Info(string text) => Set(text, StatusKind.Info);
        public void Success(string text) => Set(text, StatusKind.Success);
        public void Warn(string text) => Set(text, StatusKind.Warning);

        /// <summary>An error in plain words; also written to the log.</summary>
        public void Error(string text)
        {
            AppLog.Error(text);
            Set(text, StatusKind.Error);
        }

        void Set(string text, StatusKind kind) => OnUi(() => { Message = text ?? ""; Kind = kind; Changed?.Invoke(); });

        /// <summary>Shows a toast and repeats its text in the status bar.</summary>
        public void Show(Toast toast)
        {
            OnUi(() =>
            {
                Message = toast.Text; Kind = toast.Kind;
                Changed?.Invoke();
                ToastRaised?.Invoke(toast);
            });
        }

        /// <summary>A toast without an action.</summary>
        public void Notify(string text, StatusKind kind = StatusKind.Info) =>
            Show(new Toast { Text = text, Kind = kind, Duration = TimeSpan.FromSeconds(kind == StatusKind.Error ? 8 : 4) });

        /// <summary>Starts a progress entry in the status bar.</summary>
        public StatusProgress Begin(string label)
        {
            var p = new StatusProgress(this, label);
            lock (_running) _running.Add(p);
            ProgressTouched();
            return p;
        }

        internal void End(StatusProgress p)
        {
            lock (_running) _running.Remove(p);
            ProgressTouched();
        }

        internal void ProgressTouched() => OnUi(() => Changed?.Invoke());

        void OnUi(Action a) { if (_ui.CheckAccess()) a(); else _ui.Post(a); }
    }
}
