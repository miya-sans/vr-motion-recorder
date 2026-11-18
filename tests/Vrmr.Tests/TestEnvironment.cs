using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace VRMR.Tests
{
    /// <summary>
    ///     Puts the whole test run into an isolated data folder before any test starts: settings, calibrations and the
    ///     sandboxed machine all live in one temp folder, so no test reads or writes the real profile, VRChat's folders or
    ///     the registry, whatever code path it reaches.
    /// </summary>
    static class TestEnvironment
    {
        /// <summary>The data folder of this test run.</summary>
        public static string Root { get; private set; }

        [ModuleInitializer]
        internal static void Isolate()
        {
            Root = Path.Combine(Path.GetTempPath(), "vrmr-tests-" + Guid.NewGuid().ToString("N"));
            AppPaths.Use(AppPaths.ForDataRoot(Root));
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                if (Environment.GetEnvironmentVariable("VRMR_PRINT_GOLDEN") == "1")
                    return; // the printed rows are in there
                try { Directory.Delete(Root, true); }
                catch { }
            };
        }
    }
}
