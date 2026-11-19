using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Xunit;

namespace VRMR.Tests
{
    [CollectionDefinition("logging", DisableParallelization = true)]
    public class LoggingCollection;

    [Collection("logging")]
    public class AppLogTests
    {
        [Fact]
        public void LogsUseATimestampedFileInTheLogsSubfolderAndPreservePreviousFiles()
        {
            using var tmp = new TempDir();
            var paths = AppPaths.ForDataRoot(tmp.Path);
            Assert.Equal(Path.Combine(paths.AppDataDir, "logs"), paths.LogDir);
            Assert.Matches(@"^vrmr-\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}\.\d{3}Z-\d+\.log$", AppLog.FileName);
            Directory.CreateDirectory(paths.LogDir);
            string previous = Path.Combine(paths.LogDir, "vrmr-2000-01-01T00-00-00.000Z-1.log");
            File.WriteAllText(previous, "previous run");
            try
            {
                AppLog.Init(paths.LogDir);
                AppLog.Info("first entry");
                AppLog.Close();
                AppLog.Init(paths.LogDir);
                AppLog.Info("second entry");
            }
            finally { AppLog.Close(); }

            Assert.Equal("previous run", File.ReadAllText(previous));
            string[] lines = File.ReadAllLines(paths.LogFile);
            Assert.Equal(2, lines.Length);
            Assert.EndsWith("[INF] first entry", lines[0]);
            Assert.EndsWith("[INF] second entry", lines[1]);
        }

        [Fact]
        public void EveryPhysicalLineHasAnInvariantIsoUtcTimestampAndBracketedLevel()
        {
            using var tmp = new TempDir();
            var paths = AppPaths.ForDataRoot(tmp.Path);
            var previousCulture = CultureInfo.CurrentCulture;
            var events = new List<string>();
            AppLog.Line += events.Add;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fi-FI");
                AppLog.Init(paths.LogDir);
                AppLog.Info("information");
                AppLog.Warn("warning");
                AppLog.Error("error\r\nstack line\nlast line");
            }
            finally
            {
                AppLog.Close();
                AppLog.Line -= events.Add;
                CultureInfo.CurrentCulture = previousCulture;
            }

            string[] lines = File.ReadAllLines(paths.LogFile);
            Assert.Equal(5, lines.Length);
            Assert.Equal(lines, events);
            foreach (string line in lines)
            {
                Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z \[(INF|WRN|ERR)\] ", line);
                Assert.True(DateTimeOffset.TryParseExact(line.Substring(0, 24), "yyyy-MM-ddTHH:mm:ss.fff'Z'",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp));
                Assert.Equal(TimeSpan.Zero, timestamp.Offset);
                Assert.Contains(line, AppLog.Snapshot());
            }

            Assert.EndsWith("[INF] information", lines[0]);
            Assert.EndsWith("[WRN] warning", lines[1]);
            Assert.EndsWith("[ERR] error", lines[2]);
            Assert.EndsWith("[ERR] stack line", lines[3]);
            Assert.EndsWith("[ERR] last line", lines[4]);
        }

        [Fact]
        public void AnUnavailableLogFolderDoesNotPreventInMemoryLogging()
        {
            using var tmp = new TempDir();
            string file = Path.Combine(tmp.Path, "file");
            File.WriteAllText(file, "not a folder");
            string message = "logging failure " + Guid.NewGuid();
            try
            {
                AppLog.Init(file);
                AppLog.Warn(message);
                Assert.Contains(AppLog.Snapshot(), line => line.EndsWith("[WRN] " + message, StringComparison.Ordinal));
            }
            finally { AppLog.Close(); }
        }
    }
}
