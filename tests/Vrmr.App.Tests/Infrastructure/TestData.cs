using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VRMR.App.Composition;
using VRMR.App.Services;

namespace VRMR.App.Tests
{
    /// <summary>A UI thread stand-in for service tests: posted work and timers run when the test says so.</summary>
    public sealed class ManualDispatcher : IUiDispatcher
    {
        readonly object _lock = new();
        readonly List<Action> _posted = new();
        readonly List<Timer> _timers = new();

        /// <summary>The dispatcher's own time in seconds; advanced by <see cref="Advance" />.</summary>
        public double Now { get; private set; }

        public bool CheckAccess()
        {
            return true;
        }

        public void Post(Action action)
        {
            lock (_lock) _posted.Add(action);
        }

        public IDisposable After(TimeSpan delay, Action action)
        {
            var t = new Timer { Due = Now + delay.TotalSeconds, Action = action };
            lock (_lock) _timers.Add(t);
            return t;
        }

        public IDisposable Every(TimeSpan interval, Action action)
        {
            var t = new Timer
                { Due = Now + interval.TotalSeconds, Interval = interval.TotalSeconds, Action = action, Repeat = true };
            lock (_lock) _timers.Add(t);
            return t;
        }

        /// <summary>Runs everything posted so far (and what that posts).</summary>
        public void RunPosted()
        {
            while (true)
            {
                Action[] batch;
                lock (_lock)
                {
                    if (_posted.Count == 0) return;
                    batch = _posted.ToArray();
                    _posted.Clear();
                }

                foreach (var a in batch) a();
            }
        }

        /// <summary>Moves time forward, firing due timers and posted work.</summary>
        public void Advance(double seconds)
        {
            Now += seconds;
            RunPosted();
            Timer[] due;
            lock (_lock)
            {
                _timers.RemoveAll(t => t.Dead);
                due = _timers.Where(t => t.Due <= Now).ToArray();
            }

            foreach (var t in due)
            {
                if (t.Dead) continue;
                if (t.Repeat) t.Due = Now + t.Interval;
                else t.Dead = true;
                t.Action();
            }

            RunPosted();
        }

        /// <summary>Lets background threads finish and deliver: runs posted work until the condition holds.</summary>
        public bool WaitFor(Func<bool> condition, int timeoutMs = 20000)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                RunPosted();
                if (condition()) return true;
                if (sw.ElapsedMilliseconds > timeoutMs) return false;
                Thread.Sleep(5);
            }
        }

        /// <summary>Drains posted work while asynchronously waiting for a background result.</summary>
        public async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 20000)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                RunPosted();
                if (condition()) return true;
                if (sw.ElapsedMilliseconds > timeoutMs) return false;
                await Task.Delay(5);
            }
        }

        sealed class Timer : IDisposable
        {
            public Action Action;
            public double Due, Interval;
            public bool Repeat, Dead;

            public void Dispose()
            {
                Dead = true;
            }
        }
    }

    /// <summary>
    ///     Copies of real takes for the tests that need them. Takes are personal data and never part of the repository:
    ///     set VRMR_PRIVATE_DATA to a folder holding copies of your own takes (and an Avatars folder) to run these tests;
    ///     without it they are skipped.
    /// </summary>
    public static class PrivateData
    {
        public const string Variable = "VRMR_PRIVATE_DATA";

        public const string SkipReason = "Needs copies of real takes: set " + Variable +
                                         " to a folder with .vrmr files (at the root or under Recordings).";

        static readonly Lazy<string> Root = new(Prepare);

        /// <summary>The private fixture folder, with the Windows user setting as a fallback.</summary>
        public static string Source
        {
            get
            {
                string dir = Environment.GetEnvironmentVariable(Variable);
                if (IsFixture(dir)) return dir;
                if (!OperatingSystem.IsWindows()) return null;

                dir = Environment.GetEnvironmentVariable(Variable, EnvironmentVariableTarget.User);
                return IsFixture(dir) ? dir : null;
            }
        }

        public static bool Available => Source != null;

        /// <summary>A data root (the app's --data layout) with a fresh copy of the takes and avatars; made once per test run.</summary>
        public static string DataRoot => Root.Value;

        /// <summary>The takes folder inside <see cref="DataRoot" />.</summary>
        public static string TakesDir => Path.Combine(DataRoot, "Documents");

        /// <summary>The copied takes, smallest first (the small ones solve fastest).</summary>
        public static string[] Takes()
        {
            return new DirectoryInfo(TakesDir).GetFiles("*.vrmr").OrderBy(f => f.Length).Select(f => f.FullName)
                .ToArray();
        }

        static bool IsFixture(string dir)
        {
            return Directory.Exists(dir) && (HasTakes(dir) || HasTakes(Path.Combine(dir, "Recordings")));
        }

        static bool HasTakes(string dir)
        {
            return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.vrmr").Any();
        }

        static string Prepare()
        {
            string src = Source ?? throw new InvalidOperationException(SkipReason);
            string root = Path.Combine(Path.GetTempPath(), "vrmr-app-tests-" + Guid.NewGuid().ToString("N"));
            string documents = Path.Combine(root, "Documents");
            CopyAll(src, documents);
            if (!HasTakes(documents))
                foreach (string take in Directory.GetFiles(Path.Combine(documents, "Recordings"), "*.vrmr"))
                    File.Move(take, Path.Combine(documents, Path.GetFileName(take)));

            AppDomain.CurrentDomain.ProcessExit += (o, e) =>
            {
                try { Directory.Delete(root, true); }
                catch { }
            };
            return root;
        }

        static void CopyAll(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (string d in Directory.GetDirectories(from)) CopyAll(d, Path.Combine(to, Path.GetFileName(d)));
        }

        /// <summary>The app's services on the private data root, without live sources.</summary>
        public static AppHost NewHost(IUiDispatcher ui)
        {
            return new AppHost(AppOptions.Parse(new[] { "--data", DataRoot, "--no-capture" }), ui, null);
        }
    }

    /// <summary>A fresh, empty data root for tests that need no real takes.</summary>
    public sealed class ScratchData : IDisposable
    {
        public ScratchData() { Directory.CreateDirectory(Root); }

        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "vrmr-app-tests-" + Guid.NewGuid().ToString("N"));

        public string TakesDir => Path.Combine(Root, "Documents");
        public AppOptions Options => AppOptions.Parse(new[] { "--data", Root, "--no-capture" });

        public void Dispose()
        {
            try { Directory.Delete(Root, true); }
            catch { }
        }

        public AppHost NewHost(IUiDispatcher ui)
        {
            return new AppHost(Options, ui, null);
        }
    }
}
