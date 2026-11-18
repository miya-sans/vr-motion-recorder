using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRMR.App.Composition;
using VRMR.App.Services;
using Xunit;

namespace VRMR.App.Tests
{
    /// <summary>Stand-in avatars for the tests. Everything here is made up: no real avatar is needed or used.</summary>
    static class TestAvatars
    {
        /// <summary>Saves a base model with a plain humanoid skeleton and no model file into the host's library.</summary>
        public static AvatarEntry Save(AppHost host, string name, float height = 1.6f)
        {
            var skeleton = Mannequin.Profile(height);
            skeleton.Name = name;
            skeleton.SourcePath = "";
            skeleton.LibraryId = "";
            var entry = new AvatarEntry { Id = AvatarLibrary.Slug(name), Name = name, Skeleton = skeleton };
            host.Avatars.Save(entry);
            return entry;
        }

        /// <summary>
        ///     Writes a Unity model the way an avatar ships it: a .fbx with a humanoid .meta next to it. The .fbx is a
        ///     placeholder (nothing reads its mesh in these tests); the .meta holds a small skeleton. Returns the .fbx.
        /// </summary>
        public static string WriteUnityModel(string dir, string name)
        {
            Directory.CreateDirectory(dir);
            string fbx = Path.Combine(dir, name + ".fbx");
            File.WriteAllBytes(fbx, Encoding.ASCII.GetBytes("placeholder, not a model"));
            var meta = new StringBuilder(
                "fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdef\nModelImporter:\n  humanDescription:\n    serializedVersion: 3\n    human:\n");
            foreach (string bone in new[] { "Hips", "Spine", "Chest", "Neck", "Head" })
                meta.Append("    - boneName: ").Append(bone).Append("\n      humanName: ").Append(bone)
                    .Append("\n      limit:\n        useDefaultValues: 1\n");
            meta.Append("    skeleton:\n");

            void Bone(string bone, string parent, float y)
            {
                meta
                    .Append("    - name: ").Append(bone).Append("\n      parentName: ").Append(parent)
                    .Append("\n      position: {x: 0, y: ")
                    .Append(y.ToString(CultureInfo.InvariantCulture)).Append(", z: 0}")
                    .Append("\n      rotation: {x: 0, y: 0, z: 0, w: 1}\n      scale: {x: 1, y: 1, z: 1}\n");
            }

            Bone(name + "(Clone)", "", 0);
            Bone("Hips", name + "(Clone)", 0.9f);
            Bone("Spine", "Hips", 0.1f);
            Bone("Chest", "Spine", 0.15f);
            Bone("Neck", "Chest", 0.2f);
            Bone("Head", "Neck", 0.08f);
            meta.Append("  globalScale: 1\n  useFileScale: 1\n");
            File.WriteAllText(fbx + ".meta", meta.ToString(), new UTF8Encoding(false));
            return fbx;
        }
    }

    /// <summary>
    ///     The avatar library as the app uses it: the list, the default, names, adding, removing, Unity projects and
    ///     links.
    /// </summary>
    [Collection("app")]
    public class AvatarServiceTests
    {
        static (ScratchData data, ManualDispatcher ui, AppHost host) Start()
        {
            var data = new ScratchData();
            var ui = new ManualDispatcher();
            return (data, ui, data.NewHost(ui));
        }

        [Fact]
        public void AnEmptyLibraryStillHasTheBuiltInMannequin()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                var avatars = host.AvatarService;
                Assert.False(avatars.HasOwnEntries);
                var only = Assert.Single(avatars.Entries);
                Assert.True(only.BuiltIn);
                Assert.Same(only, avatars.Default);
                Assert.True(AvatarService.HasFbx(only));
                Assert.StartsWith(Path.GetFullPath(data.Root), avatars.Folder, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void EntriesAreListedByNameWithTheMannequinLast()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                int changes = 0;
                host.AvatarService.Changed += () => changes++;
                TestAvatars.Save(host, "Second avatar");
                TestAvatars.Save(host, "First avatar", 1.4f);
                Assert.True(changes >= 2, "saving an entry must tell the listeners");
                var entries = host.AvatarService.Entries;
                Assert.Equal(new[] { "First avatar", "Second avatar", Mannequin.Name },
                    entries.Select(e => e.Name).ToArray());
                Assert.True(host.AvatarService.HasOwnEntries);
                // no model file was saved with these: the list flags that
                Assert.False(AvatarService.HasFbx(entries[0]));
                Assert.Contains("eye height", host.AvatarService.Summary(entries[0]));
                Assert.Contains("humanoid bones", host.AvatarService.Details(entries[0]));
            }
        }

        [Fact]
        public void TheDefaultIsStoredInTheSettings()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                var a = TestAvatars.Save(host, "Avatar A");
                var b = TestAvatars.Save(host, "Avatar B");
                string changed = null;
                host.SettingsStore.Changed += what => changed = what;
                host.AvatarService.SetDefault(b);
                Assert.Equal(b.Id, host.Settings.DefaultAvatar);
                Assert.Equal(nameof(Settings.DefaultAvatar), changed);
                Assert.Same(b, host.AvatarService.Default);
                // an avatar nobody knows is now solved onto it
                Assert.Same(b, host.Avatars.Match("", "someone else's avatar").Entry);
                host.AvatarService.SetDefault(host.Avatars.BuiltIn);
                Assert.True(host.AvatarService.Default.BuiltIn);
                Assert.NotSame(a, host.AvatarService.Default);
            }
        }

        [Fact]
        public void VrChatNamesAreSplitTrimmedAndSaved()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                var entry = TestAvatars.Save(host, "Named avatar");
                Assert.True(host.AvatarService.SetNames(entry, "  blue fox , fox;fox ,, 青い狐 "));
                Assert.Equal(new[] { "blue fox", "fox", "青い狐" }, entry.Match.ToArray());
                Assert.Equal("blue fox, fox, 青い狐", AvatarService.NamesText(entry));
                // the same names again change nothing
                Assert.False(host.AvatarService.SetNames(entry, "blue fox, fox, 青い狐"));
                // they are in the file, and matching uses them
                host.AvatarService.Reload();
                var reloaded = host.AvatarService.Entries.First(e => e.Id == entry.Id);
                Assert.Equal(3, reloaded.Match.Count);
                var match = host.Avatars.Match("", "My Blue Fox v2");
                Assert.Same(reloaded, match.Entry);
                Assert.True(match.Sure);
            }
        }

        [Fact]
        public void RenamingKeepsTheIdAndSortsTheList()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                var entry = TestAvatars.Save(host, "Alpha");
                TestAvatars.Save(host, "Middle");
                Assert.True(host.AvatarService.Rename(entry, "  Zulu "));
                var entries = host.AvatarService.Entries;
                Assert.Equal(new[] { "Middle", "Zulu", Mannequin.Name }, entries.Select(e => e.Name).ToArray());
                Assert.Equal("Alpha", entries[1].Id);
                // an empty name and the same name are not renames
                Assert.False(host.AvatarService.Rename(entries[1], "  "));
                Assert.False(host.AvatarService.Rename(entries[1], "Zulu"));
            }
        }

        [Fact]
        public void RemovingForgetsTheEntryAndItsDefault()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                var entry = TestAvatars.Save(host, "Short lived");
                host.AvatarService.SetDefault(entry);
                // the entry's file is taken away first, so the test leaves nothing in the Recycle Bin
                File.Delete(entry.FilePath);
                host.AvatarService.Remove(entry);
                Assert.False(host.AvatarService.HasOwnEntries);
                Assert.Equal("", host.Settings.DefaultAvatar);
                Assert.True(host.AvatarService.Default.BuiltIn);
                Assert.Contains("Recycle Bin", host.Status.Message);
            }
        }

        [Fact]
        public void TheBuiltInMannequinCannotBeChangedOrRemoved()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                var mannequin = host.Avatars.BuiltIn;
                Assert.False(host.AvatarService.Rename(mannequin, "Mine"));
                Assert.False(host.AvatarService.SetNames(mannequin, "anything"));
                host.AvatarService.Remove(mannequin);
                Assert.Single(host.AvatarService.Entries);
                Assert.Equal(Mannequin.Name, mannequin.Name);
            }
        }

        [Fact]
        public async Task AddingAUnityModelRunsInTheBackgroundAndReportsTheResult()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                var avatars = host.AvatarService;
                string fbx = TestAvatars.WriteUnityModel(Path.Combine(data.Root, "source"), "Paper doll");
                int changes = 0;
                avatars.Changed += () => changes++;

                var adding = avatars.AddAsync(new[] { fbx });
                Assert.True(avatars.Importing);
                Assert.NotNull(host.Status.Current); // the status bar shows the work
                Assert.True(changes > 0);
                Assert.True(await ui.WaitForAsync(() => adding.IsCompleted), "the import did not finish");

                var entry = await adding;
                Assert.NotNull(entry);
                Assert.False(avatars.Importing);
                Assert.Null(host.Status.Current);
                Assert.Equal("Paper doll", entry.Name);
                Assert.Contains(avatars.Entries, e => e.Id == entry.Id);
                Assert.True(AvatarService.HasFbx(entry));
                Assert.StartsWith(avatars.Folder, entry.FilePath, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(StatusKind.Success, host.Status.Kind);
                Assert.Contains("Added Paper doll", host.Status.Message);
                Assert.Contains("Added Paper doll", avatars.LastImportReport);
            }
        }

        [Fact]
        public async Task TheSameModelAddedTwiceGetsAnEntryOfItsOwn()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                var avatars = host.AvatarService;
                string fbx = TestAvatars.WriteUnityModel(Path.Combine(data.Root, "source"), "Twin");
                var first = avatars.AddAsync(new[] { fbx });
                Assert.True(await ui.WaitForAsync(() => first.IsCompleted));
                var second = avatars.AddAsync(new[] { fbx });
                Assert.True(await ui.WaitForAsync(() => second.IsCompleted));
                Assert.NotNull(await first);
                Assert.NotNull(await second);
                Assert.NotEqual((await first).Id, (await second).Id);
                Assert.Equal("Twin (2)", (await second).Name);
                Assert.Equal(3, avatars.Entries.Count);
            }
        }

        [Fact]
        public async Task FilesWithoutAnAvatarSayWhatToPickInstead()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                string notes = Path.Combine(data.Root, "notes.txt");
                File.WriteAllText(notes, "not an avatar");
                var adding = host.AvatarService.AddAsync(new[] { notes });
                Assert.True(await ui.WaitForAsync(() => adding.IsCompleted));
                Assert.Null(await adding);
                Assert.False(host.AvatarService.Importing);
                Assert.False(host.AvatarService.HasOwnEntries);
                Assert.Equal(StatusKind.Error, host.Status.Kind);
                Assert.Contains("No avatar was added", host.Status.Message);
                Assert.Contains("notes.txt", host.Status.Message);
                Assert.Contains(".unitypackage", host.Status.Message);
            }
        }

        [Fact]
        public async Task ASecondAddWaitsForTheFirst()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                string fbx = TestAvatars.WriteUnityModel(Path.Combine(data.Root, "source"), "One at a time");
                var first = host.AvatarService.AddAsync(new[] { fbx });
                var second = host.AvatarService.AddAsync(new[] { fbx });
                Assert.True(second.IsCompleted);
                Assert.Null(await second);
                Assert.Equal(StatusKind.Warning, host.Status.Kind);
                Assert.True(await ui.WaitForAsync(() => first.IsCompleted));
                Assert.NotNull(await first);
                Assert.Equal(2, host.AvatarService.Entries.Count);
            }
        }

        [Fact]
        public async Task AUnityProjectMustLookLikeOne()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                var avatars = host.AvatarService;
                string project = Path.Combine(data.Root, "UnityProject");
                Directory.CreateDirectory(project);
                Assert.Contains("not a Unity project", avatars.AddUnityProject(project));
                Assert.Empty(avatars.UnityProjects);

                Directory.CreateDirectory(Path.Combine(project, "Assets"));
                Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
                Assert.Null(avatars.AddUnityProject(project));
                Assert.Equal(new[] { project }, avatars.UnityProjects.ToArray());
                // adding it again does not list it twice
                Assert.True(await ui.WaitForAsync(() => !avatars.Scanning));
                Assert.Null(avatars.AddUnityProject(project + Path.DirectorySeparatorChar));
                Assert.Single(avatars.UnityProjects);
                Assert.True(await ui.WaitForAsync(() => !avatars.Scanning));
                ui.Advance(0.5); // the watch that follows a scan stops with it
                Assert.Contains("1 project", avatars.ScanInfo);

                avatars.RemoveUnityProject(project);
                Assert.Empty(avatars.UnityProjects);
                Assert.Empty(host.Settings.UnityProjects);
            }
        }

        [Fact]
        public void LinkingNeedsAnAvatarFromVrChatAndAHumanoidModel()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                var avatars = host.AvatarService;
                string assets = Path.Combine(data.Root, "UnityProject", "Assets");
                string fbx = TestAvatars.WriteUnityModel(assets, "Linked model");

                // VRChat has not said which avatar is worn
                Assert.Equal("", avatars.WornAvatarId);
                Assert.Contains("OSC", avatars.LinkWornAvatar(fbx));

                host.Recorder.AvatarId = "avtr_00000000-0000-0000-0000-000000000001";
                host.Recorder.AvatarName = "Test avatar";
                Assert.Contains(".meta", avatars.LinkWornAvatar(Path.Combine(assets, "missing.fbx")));
                string plain = Path.Combine(assets, "plain.fbx");
                File.WriteAllText(plain + ".meta", "fileFormatVersion: 2\nguid: 00000000000000000000000000000000\n");
                Assert.Contains("humanoid", avatars.LinkWornAvatar(plain));

                Assert.Null(avatars.LinkWornAvatar(fbx));
                var match = host.Live.CurrentMatch();
                Assert.Equal("Linked model", match.Entry.Name);
                Assert.True(match.Sure);
                Assert.Contains("Linked model", host.Status.Message);
            }
        }

        [Fact]
        public void HiddenMeshesAreKeptInTheSettings()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                var before = host.Settings.PreviewHiddenMeshes;
                host.AvatarService.SetMeshHidden("Jacket", true);
                host.AvatarService.SetMeshHidden("Jacket", true);
                host.AvatarService.SetMeshHidden("Hat", true);
                Assert.Equal(new[] { "Jacket", "Hat" }, host.AvatarService.HiddenMeshes.ToArray());
                Assert.NotSame(before,
                    host.Settings.PreviewHiddenMeshes); // a view reading the old list is not disturbed
                host.AvatarService.SetMeshHidden("Jacket", false);
                Assert.Equal(new[] { "Hat" }, host.AvatarService.HiddenMeshes.ToArray());
            }
        }

        [Fact]
        public async Task TheMeshListSaysWhyItIsEmpty()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                var avatars = host.AvatarService;
                var none = avatars.LoadMeshesAsync(null);
                Assert.True(none.IsCompleted);
                Assert.Contains("Open a take", (await none).Note);
                Assert.Contains("mannequin", (await avatars.LoadMeshesAsync(host.Avatars.BuiltIn.Profile())).Note);

                // no model file at all
                var bare = avatars.LoadMeshesAsync(TestAvatars.Save(host, "Bare").Profile());
                Assert.True(await ui.WaitForAsync(() => bare.IsCompleted));
                Assert.Empty((await bare).Parts);
                Assert.Contains("No model file", (await bare).Note);

                // a model file that is not a model
                string fbx = TestAvatars.WriteUnityModel(Path.Combine(data.Root, "source"), "Unreadable");
                var adding = avatars.AddAsync(new[] { fbx });
                Assert.True(await ui.WaitForAsync(() => adding.IsCompleted));
                var broken = avatars.LoadMeshesAsync((await adding).Profile());
                Assert.True(await ui.WaitForAsync(() => broken.IsCompleted));
                Assert.Empty((await broken).Parts);
                Assert.Contains("could not be read", (await broken).Note);
            }
        }

        [Fact]
        public async Task TheFaceFbxCopyNeedsAnAddOnInAUnityProject()
        {
            var (data, ui, host) = Start();
            using (data)
            using (host)
            {
                var avatars = host.AvatarService;
                var nothing = avatars.PlanFaceFbxAsync(null);
                Assert.Contains("no avatar on screen", (await nothing).Problem);

                string fbx = TestAvatars.WriteUnityModel(Path.Combine(data.Root, "source"), "No add-on");
                var adding = avatars.AddAsync(new[] { fbx });
                Assert.True(await ui.WaitForAsync(() => adding.IsCompleted));
                var plan = avatars.PlanFaceFbxAsync((await adding).Profile());
                Assert.True(await ui.WaitForAsync(() => plan.IsCompleted));
                Assert.Contains("No face-tracking add-on", (await plan).Problem);
                // a plan that cannot run writes nothing
                string output = Path.Combine(data.Root, "copy.fbx");
                Assert.False(await avatars.ExportFaceFbxAsync(await plan, output));
                Assert.False(File.Exists(output));
            }
        }

        [Fact]
        public async Task RealAvatarsListTheirMeshes()
        {
            Assert.SkipUnless(PrivateData.Available, PrivateData.SkipReason);
            var ui = new ManualDispatcher();
            using var host = PrivateData.NewHost(ui);
            var avatars = host.AvatarService;
            Assert.True(avatars.HasOwnEntries, "the private data has no avatar library");
            var entry = avatars.Entries.First(e => !e.BuiltIn && AvatarService.HasFbx(e));
            var loading = avatars.LoadMeshesAsync(entry.Profile());
            Assert.True(await ui.WaitForAsync(() => loading.IsCompleted, 120000), "the model did not load");
            var list = await loading;
            Assert.NotEmpty(list.Parts);
            Assert.True(list.Triangles > 0);
            Assert.Equal(list.Triangles, list.Parts.Sum(p => p.Triangles));
            Assert.All(list.Parts, p => Assert.False(string.IsNullOrEmpty(p.Name)));
            Assert.EndsWith(".fbx", list.File, StringComparison.OrdinalIgnoreCase);
        }
    }
}
