using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VRMR
{
    /// <summary>A Unity project the app can export into.</summary>
    public sealed class UnityProjectInfo
    {
        public UnityProjectInfo(string path, string name, string editorVersion, bool exists, bool hasBridge,
            bool fromHub)
        {
            Path = path;
            Name = name;
            EditorVersion = editorVersion ?? "";
            Exists = exists;
            HasBridge = hasBridge;
            FromHub = fromHub;
        }

        /// <summary>The project folder (the one holding Assets and ProjectSettings).</summary>
        public string Path { get; }

        public string Name { get; }

        /// <summary>The editor version the project was last opened with ("2022.3.22f1"), or "".</summary>
        public string EditorVersion { get; }

        /// <summary>The folder is there and is a Unity project.</summary>
        public bool Exists { get; }

        /// <summary>The app's Unity bridge is in the project.</summary>
        public bool HasBridge { get; }

        /// <summary>Listed by Unity Hub (false: only named by the caller).</summary>
        public bool FromHub { get; }

        public override string ToString()
        {
            return $"{Name} ({EditorVersion}){(HasBridge ? ", bridge" : "")}: {Path}";
        }
    }

    /// <summary>Inspects explicitly supplied Unity projects. Never writes.</summary>
    public static class UnityProjects
    {
        /// <summary>The assembly definition every copy of the Unity bridge ships, however it was installed.</summary>
        public const string BridgeAssembly = "VRMotionBridge.Editor.asmdef";

        /// <summary>Parts of a package name or Git URL in Packages/manifest.json that mean the bridge.</summary>
        static readonly string[] BridgeMarkers =
            { "vrmotionbridge", "vrmotionrecorder", "vr-motion-recorder", "vrc-motion-recorder" };

        /// <summary>Unity Hub's project list, or null when Unity Hub is not installed for this user.</summary>
        public static string HubProjectsFile(IMachine machine)
        {
            string appData = machine.Folder(KnownFolder.AppData);
            return appData == null ? null : Path.Combine(appData, "UnityHub", "projects-v1.json");
        }

        /// <summary>A canonical project path, or null for an invalid path.</summary>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim())); }
            catch { return null; }
        }

        /// <summary>
        ///     The projects the caller names (<paramref name="extraPaths" />), in their order.
        ///     Projects whose folder is gone are kept with Exists = false. Manager lists are not read.
        /// </summary>
        public static List<UnityProjectInfo> Find(IMachine machine, IEnumerable<string> extraPaths = null)
        {
            var list = new List<UnityProjectInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string path, string title, string version, bool fromHub)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                string full = NormalizePath(path);
                if (full == null) return;
                if (!seen.Add(full)) return;
                bool exists = IsProject(full);
                string v = exists ? EditorVersionOf(full) : "";
                list.Add(new UnityProjectInfo(full, string.IsNullOrEmpty(title) ? Path.GetFileName(full) : title,
                    v.Length > 0 ? v : version, exists, exists && HasBridge(full), fromHub));
            }

            if (extraPaths != null)
                foreach (string p in extraPaths)
                    Add(p, null, null, false);
            return list;
        }

        /// <summary>Whether a folder is a Unity project.</summary>
        public static bool IsProject(string dir)
        {
            try
            {
                return !string.IsNullOrEmpty(dir) && Directory.Exists(Path.Combine(dir, "Assets")) &&
                       Directory.Exists(Path.Combine(dir, "ProjectSettings"));
            }
            catch { return false; }
        }

        /// <summary>The editor version a project was last saved with, or "".</summary>
        public static string EditorVersionOf(string projectDir)
        {
            try
            {
                string file = Path.Combine(projectDir, "ProjectSettings", "ProjectVersion.txt");
                if (!File.Exists(file)) return "";
                foreach (string line in File.ReadLines(file))
                    if (line.StartsWith("m_EditorVersion:", StringComparison.Ordinal))
                        return line.Substring(16).Trim();
            }
            catch { }

            return "";
        }

        /// <summary>
        ///     Whether the Unity bridge is in a project: named in Packages/manifest.json (installed by Git URL or name), or
        ///     its assembly definition found under Packages or Assets (copied in). Looks a few folders deep only.
        /// </summary>
        public static bool HasBridge(string projectDir)
        {
            try
            {
                string manifest = Path.Combine(projectDir, "Packages", "manifest.json");
                if (File.Exists(manifest))
                {
                    var dependencies = Json.ParseObject(File.ReadAllText(manifest))?.GetObj("dependencies");
                    if (dependencies != null)
                        foreach (var dependency in dependencies)
                            if (dependency.Key.Equals("com.vrmr.motion-bridge", StringComparison.OrdinalIgnoreCase) ||
                                BridgeMarkers.Any(marker =>
                                    dependency.Key.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
                                    dependency.Value is string value &&
                                    value.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                                return true;
                }

                // the usual place first, then a shallow search (a project's Assets can hold many thousands of folders)
                if (File.Exists(Path.Combine(projectDir, "Assets", "VRMotionBridge", "Editor", BridgeAssembly)))
                    return true;
                return ContainsFile(Path.Combine(projectDir, "Packages"), BridgeAssembly, 3) ||
                       ContainsFile(Path.Combine(projectDir, "Assets"), BridgeAssembly, 3);
            }
            catch { return false; }
        }

        static bool ContainsFile(string dir, string fileName, int depth)
        {
            try
            {
                if (!Directory.Exists(dir)) return false;
                if (File.Exists(Path.Combine(dir, fileName))) return true;
                if (depth <= 0) return false;
                foreach (string sub in Directory.GetDirectories(dir))
                    if (ContainsFile(sub, fileName, depth - 1))
                        return true;
            }
            catch { }

            return false;
        }
    }
}
