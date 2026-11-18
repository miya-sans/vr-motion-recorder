using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VRMR
{
    /// <summary>
    ///     Finds VRChat avatar blueprint IDs in a Unity project (scenes/prefabs with a PipelineManager)
    ///     and resolves each to the humanoid model .meta that holds the avatar's skeleton binding.
    ///     Pure file parsing; Unity does not need to be running.
    /// </summary>
    public static class UnityProjectScanner
    {
        static readonly Regex GuidLine = new(@"^guid:\s*([0-9a-f]{32})",
            RegexOptions.Multiline | RegexOptions.Compiled);

        static readonly Regex GuidRef = new(@"guid:\s*([0-9a-f]{32})", RegexOptions.Compiled);
        static readonly Regex AvtrRx = new(@"avtr_[0-9a-fA-F\-]{36}", RegexOptions.Compiled);

        public static bool IsUnityProject(string dir)
        {
            return !string.IsNullOrEmpty(dir) && Directory.Exists(Path.Combine(dir, "Assets")) &&
                   Directory.Exists(Path.Combine(dir, "ProjectSettings"));
        }

        public static List<Result> Scan(string projectRoot, Action<string> progress = null)
        {
            var results = new List<Result>();
            if (!IsUnityProject(projectRoot)) return results;
            progress?.Invoke("Indexing " + projectRoot);
            var guidToPath = new Dictionary<string, string>();
            foreach (string sub in new[] { "Assets", "Packages" })
            {
                string d = Path.Combine(projectRoot, sub);
                if (!Directory.Exists(d)) continue;
                foreach (string meta in SafeEnum(d, "*.meta"))
                {
                    try
                    {
                        using (var sr = new StreamReader(meta, Encoding.UTF8))
                        {
                            for (int l = 0; l < 4; l++)
                            {
                                string line = sr.ReadLine();
                                if (line == null) break;
                                if (line.StartsWith("guid:", StringComparison.Ordinal))
                                {
                                    guidToPath[line.Substring(5).Trim()] = meta.Substring(0, meta.Length - 5);
                                    break;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            progress?.Invoke("Searching avatars");
            var candidates = new List<string>();
            foreach (string pat in new[] { "*.unity", "*.prefab" })
                candidates.AddRange(SafeEnum(Path.Combine(projectRoot, "Assets"), pat));

            var seen = new HashSet<string>();
            foreach (string file in candidates)
            {
                string text;
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.Length > 64 * 1024 * 1024) continue;
                    text = File.ReadAllText(file, Encoding.UTF8);
                }
                catch { continue; }

                if (text.IndexOf("avtr_", StringComparison.Ordinal) < 0) continue;
                var docs = SplitDocs(text);
                foreach (string doc in docs)
                {
                    foreach (Match am in AvtrRx.Matches(doc))
                    {
                        string id = am.Value.ToLowerInvariant();
                        // only count blueprintId fields (not e.g. comments or fallback lists)
                        int lineStart = doc.LastIndexOf('\n', am.Index) + 1;
                        string ctx = doc.Substring(Math.Max(0, lineStart - 200),
                            Math.Min(doc.Length - Math.Max(0, lineStart - 200),
                                am.Index - Math.Max(0, lineStart - 200) + 60));
                        if (ctx.IndexOf("blueprintId", StringComparison.Ordinal) < 0) continue;
                        if (!seen.Add(id + "|" + file)) continue;

                        string model = null;
                        string display = null;
                        if (doc.StartsWith("--- !u!1001", StringComparison.Ordinal))
                        {
                            // scene-level prefab instance: use its avatar override or its source prefab
                            var src = Regex.Match(doc, @"m_SourcePrefab:\s*\{[^}]*guid:\s*([0-9a-f]{32})");
                            var avatarOverride = Regex.Match(doc,
                                @"propertyPath:\s*m_Avatar\s*\n\s*value:\s*\n?\s*objectReference:\s*\{[^}]*guid:\s*([0-9a-f]{32})");
                            var nameMod = Regex.Match(doc, @"propertyPath:\s*m_Name\s*\n\s*value:\s*(.*)\n");
                            if (nameMod.Success) display = AvatarProfile.YamlStr(nameMod.Groups[1].Value);
                            if (avatarOverride.Success)
                                model = ResolveModel(avatarOverride.Groups[1].Value, guidToPath, 0);
                            if (model == null && src.Success) model = ResolveModel(src.Groups[1].Value, guidToPath, 0);
                        }

                        if (model == null)
                        {
                            // component inside this file: look at every model referenced by the file
                            model = BestModelInText(text, guidToPath, 0);
                        }

                        if (display == null) display = Path.GetFileNameWithoutExtension(file);
                        if (model != null)
                            results.Add(new Result
                            {
                                AvatarId = id, ModelMetaPath = model + ".meta", FoundIn = file, DisplayName = display
                            });
                    }
                }
            }

            return results;
        }

        static IEnumerable<string> SafeEnum(string dir, string pattern)
        {
            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0)
            {
                string d = stack.Pop();
                string[] files = null, dirs = null;
                try
                {
                    files = Directory.GetFiles(d, pattern);
                    dirs = Directory.GetDirectories(d);
                }
                catch { }

                if (files != null)
                    foreach (string f in files)
                        yield return f;
                if (dirs != null)
                    foreach (string s in dirs)
                        stack.Push(s);
            }
        }

        static List<string> SplitDocs(string text)
        {
            var res = new List<string>();
            int i = text.IndexOf("--- !u!", StringComparison.Ordinal);
            while (i >= 0)
            {
                int j = text.IndexOf("\n--- !u!", i + 1, StringComparison.Ordinal);
                res.Add(j < 0 ? text.Substring(i) : text.Substring(i, j + 1 - i));
                i = j < 0 ? -1 : j + 1;
            }

            return res;
        }

        static bool IsModel(string path)
        {
            return path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".blend", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".obj", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".dae", StringComparison.OrdinalIgnoreCase);
        }

        static int HumanBoneCount(string modelPath)
        {
            try
            {
                string meta = File.ReadAllText(modelPath + ".meta");
                if (meta.IndexOf("animationType: 3", StringComparison.Ordinal) < 0) return 0;
                return Regex.Matches(meta, @"- boneName:").Count;
            }
            catch { return 0; }
        }

        static string ResolveModel(string guid, Dictionary<string, string> map, int depth)
        {
            if (depth > 5 || !map.TryGetValue(guid, out string path)) return null;
            if (IsModel(path)) return HumanBoneCount(path) > 0 ? path : null;
            if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                string text;
                try { text = File.ReadAllText(path); }
                catch { return null; }

                // prefab variant: root prefab instance source first
                var src = Regex.Match(text, @"m_SourcePrefab:\s*\{[^}]*guid:\s*([0-9a-f]{32})");
                if (src.Success)
                {
                    string r = ResolveModel(src.Groups[1].Value, map, depth + 1);
                    if (r != null) return r;
                }

                return BestModelInText(text, map, depth + 1);
            }

            return null;
        }

        static string BestModelInText(string text, Dictionary<string, string> map, int depth)
        {
            string best = null;
            int bestCount = 0;
            var visited = new HashSet<string>();
            // Animator avatar references are the most reliable signal
            foreach (Match m in Regex.Matches(text, @"m_Avatar:\s*\{fileID:\s*\d+,\s*guid:\s*([0-9a-f]{32})"))
            {
                if (map.TryGetValue(m.Groups[1].Value, out string p) && IsModel(p))
                {
                    int c = HumanBoneCount(p);
                    if (c > bestCount)
                    {
                        best = p;
                        bestCount = c;
                    }
                }
            }

            if (best != null) return best;
            foreach (Match m in GuidRef.Matches(text))
            {
                string g = m.Groups[1].Value;
                if (!visited.Add(g) || !map.TryGetValue(g, out string p)) continue;
                if (IsModel(p))
                {
                    int c = HumanBoneCount(p);
                    if (c > bestCount)
                    {
                        best = p;
                        bestCount = c;
                    }
                }
                else if (depth < 3 && p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    string r = ResolveModel(g, map, depth + 1);
                    if (r != null)
                    {
                        int c = HumanBoneCount(r);
                        if (c > bestCount)
                        {
                            best = r;
                            bestCount = c;
                        }
                    }
                }
            }

            return best;
        }

        public sealed class Result
        {
            public string AvatarId;
            public string DisplayName;
            public string FoundIn;
            public string ModelMetaPath;
        }
    }
}
