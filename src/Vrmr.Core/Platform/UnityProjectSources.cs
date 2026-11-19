using System;
using System.Collections.Generic;
using System.IO;
using LiteDB;
using Microsoft.Data.Sqlite;

namespace VRMR
{
    /// <summary>A manager whose saved project list can be explicitly imported.</summary>
    public enum UnityProjectSource { UnityHub, Alcom, CreatorCompanion }

    /// <summary>Projects and source errors from a read-only manager lookup.</summary>
    public sealed class UnityProjectDiscovery
    {
        public List<UnityProjectInfo> Projects { get; } = new();
        public List<string> Errors { get; } = new();
    }

    /// <summary>Reads manager project lists only when explicitly requested.</summary>
    public static class UnityProjectSources
    {
        public static string Name(UnityProjectSource source)
        {
            return source switch
            {
                UnityProjectSource.UnityHub => "Unity Hub",
                UnityProjectSource.Alcom => "ALCOM",
                _ => "VRChat Creator Companion"
            };
        }

        public static string DefaultFolder(IMachine machine, UnityProjectSource source)
        {
            string root = machine.Folder(source == UnityProjectSource.UnityHub
                ? KnownFolder.AppData
                : KnownFolder.LocalAppData);
            return root == null
                ? null
                : Path.Combine(root,
                    source == UnityProjectSource.UnityHub ? "UnityHub" : "VRChatCreatorCompanion");
        }

        /// <summary>Reads known stores in one manager data folder; never searches project directories.</summary>
        public static UnityProjectDiscovery Discover(IMachine machine, UnityProjectSource source, string folder = null)
        {
            var result = new UnityProjectDiscovery();
            folder ??= DefaultFolder(machine, source);
            if (folder == null || !Directory.Exists(folder))
            {
                result.Errors.Add("Data folder not found. Use Locate data folder…");
                return result;
            }

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string path, string title = null, string version = null)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                string full = UnityProjects.NormalizePath(path);
                if (full == null || !paths.Add(full)) return;
                bool exists = UnityProjects.IsProject(full);
                result.Projects.Add(new UnityProjectInfo(full,
                    string.IsNullOrWhiteSpace(title) ? Path.GetFileName(full) : title,
                    exists ? UnityProjects.EditorVersionOf(full) : version, exists,
                    exists && UnityProjects.HasBridge(full), source == UnityProjectSource.UnityHub));
            }

            bool foundStore = false;
            bool supportedStore = false;

            void Read(string relative, Action<string> read)
            {
                string file = Path.Combine(folder, relative);
                if (!File.Exists(file)) return;
                foundStore = true;
                try { read(file); }
                catch (Exception ex) { result.Errors.Add(relative + ": " + ex.Message); }
            }

            if (source == UnityProjectSource.UnityHub)
                Read("projects-v1.json", file =>
                {
                    var root = Json.ParseObject(File.ReadAllText(file).TrimStart('\uFEFF'));
                    if (root == null) throw new InvalidDataException("Invalid project list.");
                    var data = root.GetObj("data") ?? root;
                    supportedStore = true;
                    foreach (var entry in data)
                        if (entry.Value is Dictionary<string, object> project)
                            Add(project.GetStr("path") ?? entry.Key, project.GetStr("title"),
                                project.GetStr("version"));
                });
            else
            {
                if (source == UnityProjectSource.Alcom)
                    Read(Path.Combine("vrc-get", "vrc-get.db"), file =>
                    {
                        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                        {
                            DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1
                        }.ToString());
                        connection.Open();
                        using var command = connection.CreateCommand();
                        command.CommandText = "SELECT path FROM projects ORDER BY path";
                        using var rows = command.ExecuteReader();
                        supportedStore = true;
                        while (rows.Read()) Add(rows.GetString(0));
                    });

                Read("vcc.liteDb", file =>
                {
                    using var database = new LiteDatabase(new ConnectionString
                        { Filename = file, ReadOnly = true, Connection = ConnectionType.Direct });
                    if (!database.CollectionExists("projects"))
                        throw new InvalidDataException("Project collection not found.");
                    supportedStore = true;
                    foreach (var project in database.GetCollection("projects").FindAll())
                        if (project["Path"].IsString)
                            Add(project["Path"].AsString);
                });

                Read("settings.json", file =>
                {
                    var root = Json.ParseObject(File.ReadAllText(file).TrimStart('\uFEFF'));
                    if (root == null) throw new InvalidDataException("Invalid settings file.");
                    var projects = root.GetList("userProjects");
                    if (projects == null) return;
                    supportedStore = true;
                    foreach (object project in projects)
                        if (project is string path)
                            Add(path);
                });
            }

            if (!foundStore || !supportedStore && result.Errors.Count == 0)
                result.Errors.Add("No supported project list found. Use Locate data folder…");
            result.Projects.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            return result;
        }
    }
}
