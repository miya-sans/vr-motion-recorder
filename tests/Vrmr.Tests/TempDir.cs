using System;
using System.IO;

namespace VRMR.Tests
{
    /// <summary>A scratch folder under the system temp directory, deleted when the test is done.</summary>
    public sealed class TempDir : IDisposable
    {
        public TempDir() { Directory.CreateDirectory(Path); }

        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vrmr-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(Path, true); }
            catch { }
        }

        public string File(string name)
        {
            return System.IO.Path.Combine(Path, name);
        }
    }
}
