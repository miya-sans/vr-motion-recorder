using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VRMR
{
    /// <summary>A Unity project the app can export into.</summary>
    public sealed class UnityProjectInfo
    {
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

        public UnityProjectInfo(string path, string name, string editorVersion, bool exists, bool hasBridge, bool fromHub)
        {
            Path = path; Name = name; EditorVersion = editorVersion ?? ""; Exists = exists; HasBridge = hasBridge; FromHub = fromHub;
        }

        public override string ToString() => $"{Name} ({EditorVersion}){(HasBridge ? ", bridge" : "")}: {Path}";
    }

    /// <summary>Finds Unity projects from Unity Hub's project list, and checks them for the app's bridge. Never writes.</summary>
    public static class UnityProjects
    {
        /// <summary>The assembly definition every copy of the Unity bridge ships, however it was installed.</summary>
        public const string BridgeAssembly = "VRMotionBridge.Editor.asmdef";
        /// <summary>Parts of a package name or Git URL in Packages/manifest.json that mean the bridge.</summary>
        static readonly string[] BridgeMarkers = { "vrmotionbridge", "vrmotionrecorder", "vr-motion-recorder", "vrc-motion-recorder" };

        /// <summary>Unity Hub's project list, or null when Unity Hub is not installed for this user.</summary>
        public static string HubProjectsFile(IMachine machine)
        {
            string appData = machine.Folder(KnownFolder.AppData);
            return appData == null ? null : System.IO.Path.Combine(appData, "UnityHub", "projects-v1.json");
        }

        /// <summary>
        /// The projects the caller names (<paramref name="extraPaths"/>, in their order), then the ones Unity Hub
        /// lists, most recently used first. Projects whose folder is gone are kept with Exists = false.
        /// </summary>
        public static List<UnityProjectInfo> Find(IMachine machine, IEnumerable<string> extraPaths = null)
        {
            var list = new List<UnityProjectInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string path, string title, string version, bool fromHub)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                string full;
                try { full = System.IO.Path.GetFullPath(path.Trim()).TrimEnd('\\', '/'); } catch { return; }
                if (!seen.Add(full)) return;
                bool exists = IsProject(full);
                string v = exists ? EditorVersionOf(full) : "";
                list.Add(new UnityProjectInfo(full, string.IsNullOrEmpty(title) ? System.IO.Path.GetFileName(full) : title, v.Length > 0 ? v : version, exists, exists && HasBridge(full), fromHub));
            }

            if (extraPaths != null) foreach (var p in extraPaths) Add(p, null, null, false);
            foreach (var p in HubProjects(machine)) Add(p.Path, p.Title, p.Version, true);
            return list;
        }

        sealed class HubEntry { public string Path, Title, Version; public double LastModified; }

        static IEnumerable<HubEntry> HubProjects(IMachine machine)
        {
            var entries = new List<HubEntry>();
            try
            {
                string file = HubProjectsFile(machine);
                if (file == null || !File.Exists(file)) return entries;
                var root = Json.ParseObject(File.ReadAllText(file).TrimStart('﻿'));
                var data = root?.GetObj("data") ?? root; // newer files wrap the list in "data"
                if (data == null) return entries;
                foreach (var kv in data)
                {
                    if (!(kv.Value is Dictionary<string, object> p)) continue;
                    string path = p.GetStr("path", null) ?? kv.Key;
                    entries.Add(new HubEntry { Path = path, Title = p.GetStr("title", ""), Version = p.GetStr("version", ""), LastModified = p.GetNum("lastModified", 0) });
                }
            }
            catch (Exception ex) { AppLog.Warn("Unity Hub project list: " + ex.Message); }
            return entries.OrderByDescending(e => e.LastModified);
        }

        /// <summary>Whether a folder is a Unity project.</summary>
        public static bool IsProject(string dir)
        {
            try { return !string.IsNullOrEmpty(dir) && Directory.Exists(System.IO.Path.Combine(dir, "Assets")) && Directory.Exists(System.IO.Path.Combine(dir, "ProjectSettings")); }
            catch { return false; }
        }

        /// <summary>The editor version a project was last saved with, or "".</summary>
        public static string EditorVersionOf(string projectDir)
        {
            try
            {
                string file = System.IO.Path.Combine(projectDir, "ProjectSettings", "ProjectVersion.txt");
                if (!File.Exists(file)) return "";
                foreach (var line in File.ReadLines(file))
                    if (line.StartsWith("m_EditorVersion:", StringComparison.Ordinal)) return line.Substring(16).Trim();
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Whether the Unity bridge is in a project: named in Packages/manifest.json (installed by Git URL or name), or
        /// its assembly definition found under Packages or Assets (copied in). Looks a few folders deep only.
        /// </summary>
        public static bool HasBridge(string projectDir)
        {
            try
            {
                string manifest = System.IO.Path.Combine(projectDir, "Packages", "manifest.json");
                if (File.Exists(manifest))
                {
                    string text = File.ReadAllText(manifest);
                    foreach (var marker in BridgeMarkers) if (text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
                // the usual place first, then a shallow search (a project's Assets can hold many thousands of folders)
                if (File.Exists(System.IO.Path.Combine(projectDir, "Assets", "VRMotionBridge", "Editor", BridgeAssembly))) return true;
                return ContainsFile(System.IO.Path.Combine(projectDir, "Packages"), BridgeAssembly, 3) || ContainsFile(System.IO.Path.Combine(projectDir, "Assets"), BridgeAssembly, 3);
            }
            catch { return false; }
        }

        static bool ContainsFile(string dir, string fileName, int depth)
        {
            try
            {
                if (!Directory.Exists(dir)) return false;
                if (File.Exists(System.IO.Path.Combine(dir, fileName))) return true;
                if (depth <= 0) return false;
                foreach (var sub in Directory.GetDirectories(dir))
                    if (ContainsFile(sub, fileName, depth - 1)) return true;
            }
            catch { }
            return false;
        }
    }
}
