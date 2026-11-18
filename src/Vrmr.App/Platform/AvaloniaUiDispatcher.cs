using System;
using Avalonia.Threading;
using VRMR.App.Services;

namespace VRMR.App.Platform
{
    /// <summary>The UI thread of the running Avalonia app.</summary>
    public sealed class AvaloniaUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess()
        {
            return Dispatcher.UIThread.CheckAccess();
        }

        public void Post(Action action)
        {
            Dispatcher.UIThread.Post(action);
        }

        public IDisposable After(TimeSpan delay, Action action)
        {
            var timer = new DispatcherTimer { Interval = delay };
            timer.Tick += (o, e) =>
            {
                timer.Stop();
                action();
            };
            timer.Start();
            return new Stopper(timer);
        }

        public IDisposable Every(TimeSpan interval, Action action)
        {
            var timer = new DispatcherTimer { Interval = interval };
            timer.Tick += (o, e) => action();
            timer.Start();
            return new Stopper(timer);
        }

        sealed class Stopper : IDisposable
        {
            readonly DispatcherTimer _timer;
            public Stopper(DispatcherTimer timer) { _timer = timer; }

            public void Dispose()
            {
                _timer.Stop();
            }
        }
    }
}
