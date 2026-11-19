using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace VRMR.Tests
{
    public class UnityProjectIndexTests
    {
        static async Task WaitForScan(AvatarLibrary library)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (library.Scanning && DateTime.UtcNow < deadline) await Task.Delay(5);
            Assert.False(library.Scanning);
        }

        [Fact]
        public async Task AddingOneProjectDoesNotIndexExistingProjectsAgain()
        {
            using var tmp = new TempDir();
            var settings = new Settings(AppPaths.ForDataRoot(tmp.Path));
            string existing = Path.Combine(tmp.Path, "Existing");
            string added = Path.Combine(tmp.Path, "Added");
            settings.UnityProjects.Add(existing);
            var calls = new ConcurrentQueue<string>();
            var library = new AvatarLibrary(settings, (project, progress) =>
            {
                calls.Enqueue(project);
                return new List<UnityProjectScanner.Result>();
            });
            library.ScanAsync(new[] { existing });
            await WaitForScan(library);
            settings.UnityProjects.Add(added);
            library.ScanAsync(new[] { added });
            await WaitForScan(library);

            Assert.Equal(new[] { existing, added }, calls.ToArray());
            Assert.Equal("Found avatars: 0 · 0 linked", library.ProjectScanInfo(existing));
            Assert.Equal("Found avatars: 0 · 0 linked", library.ProjectScanInfo(added));
        }

        [Fact]
        public async Task NewRequestsAreQueuedAndDuplicateRequestsAreCoalesced()
        {
            using var tmp = new TempDir();
            var settings = new Settings(AppPaths.ForDataRoot(tmp.Path));
            string first = Path.Combine(tmp.Path, "First");
            string second = Path.Combine(tmp.Path, "Second");
            var calls = new ConcurrentQueue<string>();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new BlockingCollection<bool>();
            var library = new AvatarLibrary(settings, (project, progress) =>
            {
                calls.Enqueue(project);
                if (project == first)
                {
                    progress("Searching avatars");
                    started.TrySetResult();
                    release.Take();
                }

                return new List<UnityProjectScanner.Result>();
            });
            library.ScanAsync(new[] { first });
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                library.ScanAsync(new[] { first, second, second + Path.DirectorySeparatorChar });
                Assert.Equal("Scanning… Searching avatars", library.ProjectScanInfo(first));
                Assert.Equal("Queued", library.ProjectScanInfo(second));
            }
            finally { release.TryAdd(true); }

            await WaitForScan(library);
            Assert.Equal(new[] { first, second }, calls.ToArray());
        }

        [Fact]
        public async Task RemovedQueuedProjectsAreSkippedAndFailuresDoNotStopOtherProjects()
        {
            using var tmp = new TempDir();
            var settings = new Settings(AppPaths.ForDataRoot(tmp.Path));
            string first = Path.Combine(tmp.Path, "Failing");
            string removed = Path.Combine(tmp.Path, "Removed");
            string last = Path.Combine(tmp.Path, "Last");
            var calls = new ConcurrentQueue<string>();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new BlockingCollection<bool>();
            var library = new AvatarLibrary(settings, (project, progress) =>
            {
                calls.Enqueue(project);
                if (project == first)
                {
                    started.TrySetResult();
                    release.Take();
                    throw new IOException("Unavailable");
                }

                return new List<UnityProjectScanner.Result>();
            });
            library.ScanAsync(new[] { first, removed, last });
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                library.ForgetProjectScan(removed);
            }
            finally { release.TryAdd(true); }

            await WaitForScan(library);
            Assert.Equal(new[] { first, last }, calls.ToArray());
            Assert.Equal("Scan failed: Unavailable", library.ProjectScanInfo(first));
            Assert.Equal("Found avatars: 0 · 0 linked", library.ProjectScanInfo(last));
        }

        [Fact]
        public async Task RemovingAnActiveProjectDiscardsItsResults()
        {
            using var tmp = new TempDir();
            var settings = new Settings(AppPaths.ForDataRoot(tmp.Path));
            string project = Path.Combine(tmp.Path, "Removed");
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new BlockingCollection<bool>();
            var library = new AvatarLibrary(settings, (path, progress) =>
            {
                started.TrySetResult();
                release.Take();
                return new List<UnityProjectScanner.Result>
                {
                    new() { AvatarId = "removed", ModelMetaPath = Path.Combine(project, "Avatar.fbx.meta") }
                };
            });
            library.ScanAsync(new[] { project });
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                library.ForgetProjectScan(project);
            }
            finally { release.TryAdd(true); }

            await WaitForScan(library);
            Assert.Empty(settings.AvatarModelMap);
            Assert.Equal("0 avatars linked", library.ProjectScanInfo(project));
        }

        [Fact]
        public async Task ResultsAndRestoredLinkCountsBelongToTheirOwnProject()
        {
            using var tmp = new TempDir();
            var settings = new Settings(AppPaths.ForDataRoot(tmp.Path));
            string first = Path.Combine(tmp.Path, "First");
            string other = Path.Combine(tmp.Path, "FirstOther");
            Directory.CreateDirectory(first);
            string model = Path.Combine(first, "Avatar.fbx.meta");
            File.WriteAllText(model, "model");
            settings.AvatarModelMap["existing"] = model;
            settings.AvatarModelMap["other"] = Path.Combine(other, "Other.fbx.meta");
            var library = new AvatarLibrary(settings, (project, progress) => new List<UnityProjectScanner.Result>
            {
                new() { AvatarId = "existing", ModelMetaPath = model },
                new() { AvatarId = "new", ModelMetaPath = model },
                new() { AvatarId = "new", ModelMetaPath = model }
            });
            Assert.Equal("1 avatar linked", library.ProjectScanInfo(first));
            Assert.Equal("1 avatar linked", library.ProjectScanInfo(other));
            library.ScanAsync(new[] { first });
            await WaitForScan(library);
            Assert.Equal("Found avatars: 2 · 2 linked", library.ProjectScanInfo(first));
            Assert.Equal("1 avatar linked", library.ProjectScanInfo(other));
        }
    }
}
