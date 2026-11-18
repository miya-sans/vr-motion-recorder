using System;
using System.IO;

namespace VRMR.App
{
    /// <summary>The command line: <c>--data &lt;dir&gt;</c> (portable mode) and <c>--no-capture</c> (no live sources).</summary>
    public sealed class AppOptions
    {
        /// <summary>Portable mode: settings, the library and recordings all live under this folder. Null = the user's own folders.</summary>
        public string DataDir { get; private set; }

        /// <summary>Where the app's files are for these options: an isolated data folder, or the user's own folders.</summary>
        public AppPaths Paths => _paths ??= DataDir != null ? AppPaths.ForDataRoot(DataDir) : AppPaths.ForUser();
        AppPaths _paths;

        /// <summary>The recorder's sources are not started: no OSC ports, no OSCQuery, no SteamVR, no log following, no global hotkey.</summary>
        public bool NoCapture { get; private set; }

        /// <summary>The window opens without taking the keyboard focus (test launches next to other work).</summary>
        public bool Background { get; private set; }

        /// <summary>Set when the command line could not be understood (the app exits with this message).</summary>
        public string Error { get; private set; }

        public const string Usage = "Usage: VRMotionRecorder [--data <folder>] [--no-capture] [--background]";

        /// <summary>Reads the options; unknown arguments are an error so a typo never silently opens the real data.</summary>
        public static AppOptions Parse(string[] args)
        {
            var o = new AppOptions();
            for (int i = 0; i < (args?.Length ?? 0); i++)
            {
                string a = args[i];
                if (a == "--no-capture") o.NoCapture = true;
                else if (a == "--background") o.Background = true;
                else if (a.StartsWith("--data=", StringComparison.Ordinal) && a.Length > 7)
                {
                    try { o.DataDir = Path.GetFullPath(a.Substring(7).Trim('"')); }
                    catch (Exception ex) { o.Error = "--data: " + ex.Message; return o; }
                }
                else if (a == "--data")
                {
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) { o.Error = "--data needs a folder."; return o; }
                    try { o.DataDir = Path.GetFullPath(args[++i]); }
                    catch (Exception ex) { o.Error = "--data: " + ex.Message; return o; }
                }
                else { o.Error = "Unknown option: " + a; return o; }
            }
            return o;
        }
    }
}
