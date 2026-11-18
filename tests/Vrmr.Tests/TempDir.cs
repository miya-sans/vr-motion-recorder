using System;
using System.IO;

namespace VRMR.Tests
{
    /// <summary>A scratch folder under the system temp directory, deleted when the test is done.</summary>
    public sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vrmr-tests-" + Guid.NewGuid().ToString("N"));
        public TempDir() { Directory.CreateDirectory(Path); }
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
