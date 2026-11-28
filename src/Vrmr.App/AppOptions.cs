using System;
using System.Globalization;
using System.IO;

namespace VRMR.App
{
    /// <summary>The command line: <c>--data &lt;dir&gt;</c> (portable mode) and <c>--no-capture</c> (no live sources).</summary>
    public sealed class AppOptions
    {
        public const string Usage = "Usage: VRMotionRecorder [--data <folder>] [--no-capture] [--background] " +
                                    "[--bench <take> | --bench-live] [--bench-seconds <seconds>]";
        AppPaths _paths;

        /// <summary>Portable mode: settings, the library and recordings all live under this folder. Null = the user's own folders.</summary>
        public string DataDir { get; private set; }

        /// <summary>Where the app's files are for these options: an isolated data folder, or the user's own folders.</summary>
        public AppPaths Paths => _paths ??= DataDir != null ? AppPaths.ForDataRoot(DataDir, !BenchmarkLive) : AppPaths.ForUser();

        /// <summary>
        ///     The recorder's sources are not started: no OSC ports, no OSCQuery, no SteamVR, no log following, no global
        ///     hotkey.
        /// </summary>
        public bool NoCapture { get; private set; }

        /// <summary>The window opens without taking the keyboard focus (test launches next to other work).</summary>
        public bool Background { get; private set; }

        public string BenchmarkTake { get; private set; }
        public bool BenchmarkLive { get; private set; }
        public double BenchmarkSeconds { get; private set; } = 10;
        public bool Benchmark => BenchmarkTake != null || BenchmarkLive;

        /// <summary>Set when the command line could not be understood (the app exits with this message).</summary>
        public string Error { get; private set; }

        /// <summary>Reads the options; unknown arguments are an error so a typo never silently opens the real data.</summary>
        public static AppOptions Parse(string[] args)
        {
            var o = new AppOptions();
            bool benchmarkSecondsSet = false;
            for (int i = 0; i < (args?.Length ?? 0); i++)
            {
                string a = args[i];
                if (a == "--no-capture") o.NoCapture = true;
                else if (a == "--background") o.Background = true;
                else if (a == "--bench-live") o.BenchmarkLive = true;
                else if (a == "--bench")
                {
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        o.Error = "--bench needs a .vrmr take.";
                        return o;
                    }

                    try { o.BenchmarkTake = Path.GetFullPath(args[++i]); }
                    catch (Exception ex)
                    {
                        o.Error = "--bench: " + ex.Message;
                        return o;
                    }
                }
                else if (a == "--bench-seconds")
                {
                    if (i + 1 >= args.Length ||
                        !double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) ||
                        !double.IsFinite(seconds) || seconds < 0.1 || seconds > 300)
                    {
                        o.Error = "--bench-seconds must be between 0.1 and 300.";
                        return o;
                    }

                    benchmarkSecondsSet = true;
                    o.BenchmarkSeconds = seconds;
                }
                else if (a.StartsWith("--data=", StringComparison.Ordinal) && a.Length > 7)
                {
                    try { o.DataDir = Path.GetFullPath(a.Substring(7).Trim('"')); }
                    catch (Exception ex)
                    {
                        o.Error = "--data: " + ex.Message;
                        return o;
                    }
                }
                else if (a == "--data")
                {
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        o.Error = "--data needs a folder.";
                        return o;
                    }

                    try { o.DataDir = Path.GetFullPath(args[++i]); }
                    catch (Exception ex)
                    {
                        o.Error = "--data: " + ex.Message;
                        return o;
                    }
                }
                else
                {
                    o.Error = "Unknown option: " + a;
                    return o;
                }
            }

            if (o.BenchmarkLive && (o.BenchmarkTake != null || o.NoCapture))
                o.Error = "--bench-live cannot be combined with --bench or --no-capture.";
            else if (benchmarkSecondsSet && !o.Benchmark)
                o.Error = "--bench-seconds needs --bench or --bench-live.";
            o.NoCapture |= o.BenchmarkTake != null;
            return o;
        }
    }
}
