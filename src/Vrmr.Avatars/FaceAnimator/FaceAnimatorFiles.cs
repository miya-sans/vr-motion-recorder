using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VRMR
{
    /// <summary>
    /// The files of an avatar's face-tracking animator, kept next to its base model in the avatar library (a
    /// "face-animator" folder): the add-on's controllers and override controllers, the animation clips they play and the
    /// add-on's parameter links, each saved under its Unity guid, with a list of which controllers the avatar plays.
    /// They are gathered from the add-on's package and the face-tracking framework it builds on (packages, or folders of
    /// a Unity project); nothing in them is run.
    /// </summary>
    public static class FaceAnimatorFiles
    {
        /// <summary>The folder name inside a library entry.</summary>
        public const string FolderName = "face-animator";
        const string IndexFile = "files.json";

        static readonly string[] Kinds = { ".controller", ".overrideController", ".anim", ".asset", ".prefab" };

        sealed class Found { public string Guid = "", Path = "", Text = ""; }

        /// <summary>What <see cref="Gather"/> collected.</summary>
        public sealed class Result
        {
            public readonly Dictionary<string, string> Texts = new Dictionary<string, string>();
            public readonly Dictionary<string, string> Names = new Dictionary<string, string>();
            /// <summary>The controllers the avatar plays (by guid).</summary>
            public readonly List<string> Roots = new List<string>();
            public readonly List<string> Log = new List<string>();
        }

        /// <summary>
        /// Collects the animator files from add-on packages (.unitypackage, .zip) and folders (a Unity project's
        /// Assets or Packages folder). The controllers played are the add-on's: its override controllers, else its own
        /// FX controllers; the framework's controllers are only bases and libraries. When an override controller's base
        /// is not among the sources, the packages of <paramref name="unityProjects"/> are searched for it (an add-on's
        /// framework is usually installed there).
        /// </summary>
        public static Result Gather(IEnumerable<string> sources, IEnumerable<string> unityProjects = null)
        {
            var res = new Result();
            var all = new Dictionary<string, Found>();
            var list = sources.ToList();
            Collect(list, all, res);
            var missing = MissingBases(all);
            if (missing.Count > 0 && unityProjects != null)
            {
                var extra = new List<string>();
                foreach (var project in unityProjects)
                {
                    string pkgs = Path.Combine(project, "Packages");
                    if (!Directory.Exists(pkgs)) continue;
                    foreach (var dir in Directory.GetDirectories(pkgs))
                        if (missing.Any(g => HasGuid(dir, g))) extra.Add(dir);
                    if (extra.Count > 0) break;
                }
                if (extra.Count > 0) { res.Log.Add("The add-on's framework was found in a Unity project: " + string.Join(", ", extra.Select(d => Path.GetFileName(d)))); Collect(extra, all, res); }
            }
            Finish(all, res);
            return res;
        }

        /// <summary>The bases of override controllers that are not among the files.</summary>
        static List<string> MissingBases(Dictionary<string, Found> all) =>
            all.Values.Where(f => f.Text.Contains("AnimatorOverrideController:"))
               .Select(f => Regex.Match(f.Text, @"m_Controller: \{fileID: \d+, guid: ([0-9a-f]{32})"))
               .Where(m => m.Success && !all.ContainsKey(m.Groups[1].Value)).Select(m => m.Groups[1].Value).Distinct().ToList();

        static bool HasGuid(string dir, string guid)
        {
            try
            {
                foreach (var meta in Directory.EnumerateFiles(dir, "*.controller.meta", SearchOption.AllDirectories))
                    if (File.ReadAllText(meta).Contains("guid: " + guid)) return true;
            }
            catch (Exception ex) { AppLog.Warn("Face animator search in " + dir + ": " + ex.Message); }
            return false;
        }

        static void Collect(List<string> sources, Dictionary<string, Found> all, Result res)
        {
            foreach (var src in sources)
            {
                int before = all.Count;
                if (Directory.Exists(src)) FromFolder(src, all);
                else if (File.Exists(src))
                {
                    try { foreach (var pkg in UnityPackage.Open(src)) FromPackage(pkg, all); }
                    catch (Exception ex) { res.Log.Add($"{Path.GetFileName(src)}: not readable ({ex.Message})"); continue; }
                    // an .fbx or other loose file: nothing for the animator
                }
                else { res.Log.Add(src + ": not found"); continue; }
                res.Log.Add($"{Path.GetFileName(src.TrimEnd('\\', '/'))}: {all.Count - before} animator file(s)");
            }
        }

        static void Finish(Dictionary<string, Found> all, Result res)
        {
            foreach (var f in all.Values) { res.Texts[f.Guid] = f.Text; res.Names[f.Guid] = f.Path; }

            var overrides = all.Values.Where(f => f.Path.EndsWith(".overrideController", StringComparison.OrdinalIgnoreCase)).ToList();
            if (overrides.Count > 0) res.Roots.AddRange(overrides.Select(f => f.Guid));
            else
                // an add-on without override controllers ships its own FX controllers (in a "Template" folder or by name)
                res.Roots.AddRange(all.Values.Where(f => f.Path.EndsWith(".controller", StringComparison.OrdinalIgnoreCase)
                                                         && Regex.IsMatch(Path.GetFileName(f.Path), @"^FX\b", RegexOptions.IgnoreCase)
                                                         && f.Path.IndexOf("_FT/", StringComparison.OrdinalIgnoreCase) >= 0).Select(f => f.Guid));
            foreach (var g in res.Roots)
            {
                var m = Regex.Match(res.Texts[g], @"m_Controller: \{fileID: \d+, guid: ([0-9a-f]{32})");
                if (res.Texts[g].Contains("AnimatorOverrideController:") && m.Success && !res.Texts.ContainsKey(m.Groups[1].Value))
                    res.Log.Add($"{Path.GetFileName(res.Names[g])}: its base controller is missing: add the face-tracking framework the add-on needs (a package, or its folder in the Unity project)");
            }
            res.Log.Add($"{res.Roots.Count} controller(s) to play: {string.Join(", ", res.Roots.Select(g => Path.GetFileNameWithoutExtension(res.Names[g])))}");
        }

        /// <summary>Forgets a loaded animator so the next use reads the files again (after <see cref="Save"/> replaced them).</summary>
        public static void Forget(string entryDir) => Cache.TryRemove(Path.GetFullPath(Path.Combine(entryDir, FolderName)), out _);

        // assets: the add-on's parameter links; prefabs: the ones that declare parameters with defaults (Modular Avatar)
        static bool Wanted(string path, string text) =>
            path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) ? text.Contains("m_LinkedParameters:")
            : path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ? text.Contains("nameOrPrefix:") && text.Contains("defaultValue:")
            : true;

        static void FromPackage(UnityPackage pkg, Dictionary<string, Found> all)
        {
            var items = pkg.Items.Values.Where(i => Kinds.Any(k => i.Path.EndsWith(k, StringComparison.OrdinalIgnoreCase)) && i.AssetSize > 0 && i.AssetSize < 16L * 1024 * 1024).ToList();
            var data = pkg.Read(items.Select(i => i.Guid));
            foreach (var it in items)
            {
                if (!data.TryGetValue(it.Guid, out var b)) continue;
                string text = Encoding.UTF8.GetString(b);
                if (!text.StartsWith("%YAML", StringComparison.Ordinal) || !Wanted(it.Path, text)) continue;
                all[it.Guid] = new Found { Guid = it.Guid, Path = it.Path, Text = text };
            }
        }

        static void FromFolder(string dir, Dictionary<string, Found> all)
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (!Kinds.Any(k => f.EndsWith(k, StringComparison.OrdinalIgnoreCase)) || !File.Exists(f + ".meta")) continue;
                if (new FileInfo(f).Length > 16L * 1024 * 1024) continue;
                var m = Regex.Match(File.ReadAllText(f + ".meta"), @"guid: ([0-9a-f]{32})");
                if (!m.Success) continue;
                string text = File.ReadAllText(f, Encoding.UTF8);
                if (!text.StartsWith("%YAML", StringComparison.Ordinal) || !Wanted(f, text)) continue;
                all[m.Groups[1].Value] = new Found { Guid = m.Groups[1].Value, Path = Path.GetRelativePath(dir, f).Replace('\\', '/'), Text = text };
            }
        }

        // ------------------------------------------------------------------ the library entry's copy

        /// <summary>Saves gathered files into a library entry's folder (replacing an earlier set).</summary>
        public static string Save(Result files, string entryDir)
        {
            string dir = Path.Combine(entryDir, FolderName);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var index = new Dictionary<string, object>
            {
                ["roots"] = files.Roots.Cast<object>().ToList(),
                ["files"] = files.Texts.Keys.ToDictionary(g => g, g => (object)files.Names[g]),
            };
            foreach (var kv in files.Texts) File.WriteAllText(Path.Combine(dir, kv.Key + ".yaml"), kv.Value, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, IndexFile), Json.Serialize(index, true), new UTF8Encoding(false));
            Cache.TryRemove(Path.GetFullPath(dir), out _);
            return dir;
        }

        static readonly ConcurrentDictionary<string, Lazy<FaceAnimator>> Cache = new ConcurrentDictionary<string, Lazy<FaceAnimator>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The face animator kept with an avatar (next to its base model), or null when it has none. Loaded once.</summary>
        public static FaceAnimator ForProfile(AvatarProfile profile)
        {
            string fbx = SkinnedAvatar.FbxFor(profile);
            if (fbx == null) return null;
            string dir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fbx), FolderName));
            if (!File.Exists(Path.Combine(dir, IndexFile))) return null;
            return Cache.GetOrAdd(dir, d => new Lazy<FaceAnimator>(() =>
            {
                try
                {
                    var a = Load(d);
                    AppLog.Info($"Avatar {profile.Name}: {a}" + (a.Notes.Count > 0 ? " (" + string.Join("; ", a.Notes) + ")" : ""));
                    return a.HasFace ? a : null;
                }
                catch (Exception ex) { AppLog.Warn("Face animator of " + profile.Name + ": " + ex); return null; }
            })).Value;
        }

        /// <summary>Loads a saved set.</summary>
        public static FaceAnimator Load(string dir)
        {
            var index = Json.ParseObject(File.ReadAllText(Path.Combine(dir, IndexFile), Encoding.UTF8));
            var texts = new Dictionary<string, string>();
            var names = new Dictionary<string, string>();
            foreach (var kv in index.GetObj("files"))
            {
                string file = Path.Combine(dir, kv.Key + ".yaml");
                if (!File.Exists(file)) continue;
                texts[kv.Key] = File.ReadAllText(file, Encoding.UTF8);
                names[kv.Key] = kv.Value as string ?? kv.Key;
            }
            var roots = (index.GetList("roots") ?? new List<object>()).Select(o => o as string).Where(s => s != null).ToList();
            return FaceAnimator.Load(texts, names, roots);
        }
    }
}
