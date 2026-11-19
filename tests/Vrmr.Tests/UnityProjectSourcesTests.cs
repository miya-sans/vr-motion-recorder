using System.IO;
using System.Linq;
using LiteDB;
using Microsoft.Data.Sqlite;
using Xunit;

namespace VRMR.Tests
{
    using JsonSerializer = System.Text.Json.JsonSerializer;

    public class UnityProjectSourcesTests
    {
        static string Project(FakeMachine fixture, string name)
        {
            string path = fixture.Dir("Projects", name);
            fixture.Dir("Projects", name, "Assets");
            fixture.File(Path.Combine(path, "ProjectSettings"), "ProjectVersion.txt", "m_EditorVersion: 2022.3.22f1");
            return path;
        }

        static void Legacy(FakeMachine fixture, string folder, params string[] paths)
        {
            fixture.File(folder, "settings.json", JsonSerializer.Serialize(new { userProjects = paths }));
        }

        [Theory]
        [InlineData(UnityProjectSource.Alcom)]
        [InlineData(UnityProjectSource.CreatorCompanion)]
        public void LegacyListsKeepBridgeFreeAndMissingProjectsAndNormalizeDuplicates(UnityProjectSource source)
        {
            using var fixture = new FakeMachine();
            string project = Project(fixture, "Avatar");
            string missing = Path.Combine(fixture.Root, "Deleted");
            string folder = UnityProjectSources.DefaultFolder(fixture.Machine, source);
            Legacy(fixture, folder, project, project + Path.DirectorySeparatorChar, missing);
            byte[] before = File.ReadAllBytes(Path.Combine(folder, "settings.json"));

            var result = UnityProjectSources.Discover(fixture.Machine, source);

            Assert.Empty(result.Errors);
            Assert.Equal(2, result.Projects.Count);
            Assert.True(result.Projects.Single(p => p.Path == project).Exists);
            Assert.False(result.Projects.Single(p => p.Path == project).HasBridge);
            Assert.False(result.Projects.Single(p => p.Path == missing).Exists);
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(folder, "settings.json")));
            Assert.Single(Directory.GetFiles(folder));
        }

        [Fact]
        public void CreatorCompanionDatabaseIsReadWithoutChangingTheStore()
        {
            using var fixture = new FakeMachine();
            string project = Project(fixture, "Avatar");
            string folder = UnityProjectSources.DefaultFolder(fixture.Machine, UnityProjectSource.CreatorCompanion);
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "vcc.liteDb");
            using (var database = new LiteDatabase(file))
                database.GetCollection("projects").Insert(new BsonDocument { ["Path"] = project });
            byte[] before = File.ReadAllBytes(file);

            var result = UnityProjectSources.Discover(fixture.Machine, UnityProjectSource.CreatorCompanion);

            Assert.Empty(result.Errors);
            Assert.Equal(project, Assert.Single(result.Projects).Path);
            Assert.Equal(before, File.ReadAllBytes(file));
            Assert.Single(Directory.GetFiles(folder));
        }

        [Fact]
        public void AlcomReadsSqliteAndDeduplicatesLegacyAndVccPaths()
        {
            using var fixture = new FakeMachine();
            string project = Project(fixture, "Avatar");
            string other = Project(fixture, "World");
            string folder = UnityProjectSources.DefaultFolder(fixture.Machine, UnityProjectSource.Alcom);
            Directory.CreateDirectory(Path.Combine(folder, "vrc-get"));
            string file = Path.Combine(folder, "vrc-get", "vrc-get.db");
            using (var connection = new SqliteConnection("Data Source=" + file))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE projects (path TEXT); INSERT INTO projects VALUES ($path)";
                command.Parameters.AddWithValue("$path", project);
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();
            Legacy(fixture, folder, project, other);
            using (var database = new LiteDatabase(Path.Combine(folder, "vcc.liteDb")))
                database.GetCollection("projects").Insert(new BsonDocument { ["Path"] = other });
            byte[] before = File.ReadAllBytes(file);

            var result = UnityProjectSources.Discover(fixture.Machine, UnityProjectSource.Alcom);

            Assert.Empty(result.Errors);
            Assert.Equal(2, result.Projects.Count);
            Assert.Equal(before, File.ReadAllBytes(file));
        }

        [Fact]
        public void AFailedStoreDoesNotHideOtherResultsAndCustomFoldersAreSupported()
        {
            using var fixture = new FakeMachine();
            string project = Project(fixture, "Avatar");
            string folder = fixture.Dir("CustomManagerData");
            Legacy(fixture, folder, project);
            fixture.File(folder, "vcc.liteDb", "corrupt");

            var result = UnityProjectSources.Discover(fixture.Machine, UnityProjectSource.Alcom, folder);

            Assert.Equal(project, Assert.Single(result.Projects).Path);
            Assert.Contains("vcc.liteDb", Assert.Single(result.Errors));
        }

        [Fact]
        public void LockedDatabaseReportsAnError()
        {
            using var fixture = new FakeMachine();
            string folder = fixture.Dir("Manager");
            string file = fixture.File(folder, "vcc.liteDb", "locked");
            using var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var result = UnityProjectSources.Discover(fixture.Machine, UnityProjectSource.CreatorCompanion, folder);

            Assert.Empty(result.Projects);
            Assert.Contains("vcc.liteDb", Assert.Single(result.Errors));
        }

        [Fact]
        public void MissingAndUnsupportedSourcesReportErrorsWithoutCreatingFiles()
        {
            using var fixture = new FakeMachine();
            string folder = Path.Combine(fixture.Root, "Missing");
            Assert.NotEmpty(UnityProjectSources.Discover(fixture.Machine, UnityProjectSource.Alcom, folder).Errors);
            Assert.False(Directory.Exists(folder));
            Directory.CreateDirectory(folder);
            Assert.NotEmpty(UnityProjectSources.Discover(fixture.Machine, UnityProjectSource.Alcom, folder).Errors);
            Assert.Empty(Directory.GetFiles(folder));
            fixture.File(folder, "settings.json", "{\"unsupported\":true}");
            Assert.Contains("No supported project list", Assert.Single(UnityProjectSources.Discover(
                fixture.Machine, UnityProjectSource.Alcom, folder).Errors));
            fixture.File(folder, "projects-v1.json", "{");
            Assert.NotEmpty(UnityProjectSources.Discover(fixture.Machine, UnityProjectSource.UnityHub, folder).Errors);
        }

        [Theory]
        [InlineData("{\"dependencies\":{\"com.vrmr.motion-bridge\":\"1.4.0\"}}", true)]
        [InlineData(
            "{\"dependencies\":{\"bridge\":\"https://github.com/example/vr-motion-recorder.git?path=integrations/unity/com.vrmr.motion-bridge\"}}",
            true)]
        [InlineData("{\"description\":\"vr-motion-recorder\",\"dependencies\":{}}", false)]
        public void BridgeDetectionChecksPackageDependencies(string manifest, bool expected)
        {
            using var fixture = new FakeMachine();
            string project = Project(fixture, "Avatar");
            fixture.File(Path.Combine(project, "Packages"), "manifest.json", manifest);
            Assert.Equal(expected, UnityProjects.HasBridge(project));
        }

        [Fact]
        public void LinkedProjectCountIncludesMissingLinksAndExcludesHubProjects()
        {
            using var fixture = new FakeMachine();
            string project = Project(fixture, "Linked");
            string hub = Project(fixture, "Unlinked");
            string missing = Path.Combine(fixture.Root, "Missing");
            fixture.File(Path.Combine(project, "Assets", "VRMotionBridge", "Editor"), UnityProjects.BridgeAssembly);
            fixture.File(Path.GetDirectoryName(UnityProjects.HubProjectsFile(fixture.Machine)), "projects-v1.json",
                JsonSerializer.Serialize(new { data = new[] { new { path = hub } } }));
            var paths = AppPaths.ForMachine(fixture.Machine);

            var report = EnvironmentProbe.Run(paths, new ProbeOptions { UnityProjects = { project, missing } });

            Assert.Equal(2, report.UnityProjects.Count);
            Assert.StartsWith("1 of 2 linked projects", report[EnvironmentProbe.Unity].Summary);
            Assert.DoesNotContain(report.UnityProjects, p => p.Path == hub);
            Assert.StartsWith("1 of 1 linked project", EnvironmentProbe.Run(paths,
                new ProbeOptions { UnityProjects = { project } })[EnvironmentProbe.Unity].Summary);
            Assert.Equal("No projects added.", EnvironmentProbe.Run(paths)[EnvironmentProbe.Unity].Summary);
        }
    }
}
