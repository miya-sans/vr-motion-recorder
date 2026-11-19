using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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

    /// <summary>The app's log: kept in memory for the UI and written to a file once <see cref="Init" /> has run.</summary>
    public static class AppLog
    {
        /// <summary>The log file's name inside the folder given to <see cref="Init" />.</summary>
        public static readonly string FileName = "vrmr-" + Timestamp().Replace(':', '-') +
                                                 "-" + Environment.ProcessId + ".log";

        static readonly object Lock = new();
        static readonly List<string> Lines = new();
        static StreamWriter _file;
        public static event Action<string> Line;

        public static void Init(string dir)
        {
            lock (Lock)
            {
                Close();
                try
                {
                    Directory.CreateDirectory(dir);
                    _file = new StreamWriter(Path.Combine(dir, FileName), true, Encoding.UTF8) { AutoFlush = true };
                }
                catch { }
            }
        }

        /// <summary>Flushes and closes the current log file.</summary>
        public static void Close()
        {
            lock (Lock)
            {
                try { _file?.Dispose(); }
                catch { }

                _file = null;
            }
        }

        static string Timestamp()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        static void Write(string level, string msg)
        {
            string prefix = Timestamp() + " [" + level + "] ";
            string[] lines = (msg ?? "").ReplaceLineEndings("\n").Split('\n');
            lock (Lock)
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    lines[i] = prefix + lines[i];
                    Lines.Add(lines[i]);
                    if (Lines.Count > 2000) Lines.RemoveRange(0, 500);
                    try { _file?.WriteLine(lines[i]); }
                    catch { }
                }
            }

            foreach (string line in lines)
            {
                try { Line?.Invoke(line); }
                catch { }
            }
        }

        public static void Info(string msg)
        {
            Write("INF", msg);
        }

        public static void Warn(string msg)
        {
            Write("WRN", msg);
        }

        public static void Error(string msg)
        {
            Write("ERR", msg);
        }

        public static string[] Snapshot()
        {
            lock (Lock) return Lines.ToArray();
        }
    }
}
