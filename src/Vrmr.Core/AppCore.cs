using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace VRMR
{
    /// <summary>Process-wide monotonic clock (QPC) shared by every data source so streams stay in sync.</summary>
    public static class Clock
    {
        static readonly Stopwatch Sw = Stopwatch.StartNew();
        public static double Now => Sw.Elapsed.TotalSeconds;
    }

    /// <summary>The app's log: kept in memory for the UI and written to a file once <see cref="Init"/> has run.</summary>
    public static class AppLog
    {
        /// <summary>The log file's name inside the folder given to <see cref="Init"/>.</summary>
        public const string FileName = "vrmr.log";
        static readonly object Lock = new object();
        static readonly List<string> Lines = new List<string>();
        static StreamWriter _file;
        public static event Action<string> Line;

        public static void Init(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                _file = new StreamWriter(Path.Combine(dir, FileName), false, Encoding.UTF8) { AutoFlush = true };
            }
            catch { }
        }

        static void Write(string level, string msg)
        {
            string line = $"{DateTime.Now:HH:mm:ss.fff} {level} {msg}";
            lock (Lock)
            {
                Lines.Add(line);
                if (Lines.Count > 2000) Lines.RemoveRange(0, 500);
                try { _file?.WriteLine(line); } catch { }
            }
            try { Line?.Invoke(line); } catch { }
        }

        public static void Info(string msg) => Write("INF", msg);
        public static void Warn(string msg) => Write("WRN", msg);
        public static void Error(string msg) => Write("ERR", msg);
        public static string[] Snapshot() { lock (Lock) return Lines.ToArray(); }
    }

}
