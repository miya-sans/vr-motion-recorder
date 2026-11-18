using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VRMR
{
    /// <summary>
    ///     Works out the other names of a base model from the names its package uses for it: the model file, the prefabs
    ///     that use the model, the package file. A name is taken only when exactly one meaningful word is left after
    ///     dropping version numbers and generic words ("avatar", "PC", "Quest", ...): a shop's name, an accessory or a
    ///     folder such as "FBX" must not become an alias, because any avatar whose name contains an alias is matched to
    ///     the model.
    /// </summary>
    public static class AvatarAliases
    {
        /// <summary>Words that say nothing about which model it is.</summary>
        static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
        {
            "avatar", "avatars", "model", "models", "prefab", "prefabs", "fbx", "unitypackage", "package", "unity",
            "vrchat", "vrc", "sdk", "sdk3",
            "pc", "quest", "android", "mobile", "ios", "lite", "light", "full", "base", "body", "default", "original",
            "standard", "normal",
            "ver", "version", "new", "old", "final", "fix", "fixed", "update", "updated", "release", "test", "sample",
            "demo", "copy",
            "physbone", "physbones", "pb", "dynamicbone", "db", "ft", "facetracking", "face", "tracking", "mmd", "nsfw",
            "sfw", "kisekae", "nude",
            "booth", "set", "setup", "import", "export", "data", "file", "files", "main", "variant", "type", "edition",
            "for", "the", "and", "with"
        };

        /// <summary>The fewest characters an alias may have: two for names in ideographs or kana, three otherwise.</summary>
        public static int MinLength(string alias)
        {
            return alias.Any(IsWide) ? 2 : 3;
        }

        /// <summary>
        ///     The aliases for a new library entry: its name and the model file's name, then the single-word names among
        ///     <paramref name="packageNames" /> (prefabs that use the model, the package file). Lower case, no duplicates.
        /// </summary>
        public static List<string> From(string entryName, string modelName, IEnumerable<string> packageNames)
        {
            var list = new List<string>();

            void Add(string alias)
            {
                alias = (alias ?? "").Trim().ToLowerInvariant();
                if (alias.Length >= MinLength(alias) && !list.Contains(alias)) list.Add(alias);
            }

            Add(entryName);
            Add(modelName);
            foreach (string name in packageNames ?? Enumerable.Empty<string>()) Add(SingleName(name));
            return list;
        }

        /// <summary>The one meaningful word of a file or prefab name, or null when there is none or more than one.</summary>
        public static string SingleName(string name)
        {
            string only = null;
            foreach (string word in Words(name ?? ""))
            {
                if (Generic.Contains(word) || IsVersion(word)) continue;
                if (only != null && !only.Equals(word, StringComparison.OrdinalIgnoreCase)) return null;
                only = word;
            }

            return only != null && only.Length >= MinLength(only) ? only : null;
        }

        /// <summary>
        ///     Splits at separators, and where the script changes between wide characters and the rest ("Name名前v2" has three
        ///     words).
        /// </summary>
        static IEnumerable<string> Words(string s)
        {
            var sb = new StringBuilder();
            int kind = 0; // 1 wide, 2 letter, 3 digit (digits stay with letters before them: "v2", "mk2")
            foreach (char c in s)
            {
                int k = IsWide(c) ? 1 :
                    char.IsLetter(c) ? 2 :
                    char.IsDigit(c) || (c == '.' && sb.Length > 0 && kind == 3) ? 3 : 0;
                bool joins = k != 0 && (k == kind || (kind == 2 && k == 3) || (kind == 3 && k == 3));
                if (!joins && sb.Length > 0)
                {
                    yield return sb.ToString().TrimEnd('.');
                    sb.Clear();
                }

                if (k != 0) sb.Append(c);
                kind = k;
            }

            if (sb.Length > 0) yield return sb.ToString().TrimEnd('.');
        }

        /// <summary>"2", "1.02", "v3", "ver2", "2024": numbers and version tags.</summary>
        static bool IsVersion(string word)
        {
            if (word.Length == 0) return true;
            string w = word.ToLowerInvariant();
            if (w.All(c => char.IsDigit(c) || c == '.')) return true;
            foreach (string prefix in new[] { "version", "ver", "v" })
                if (w.StartsWith(prefix, StringComparison.Ordinal) && w.Length > prefix.Length &&
                    w.Substring(prefix.Length).All(c => char.IsDigit(c) || c == '.'))
                    return true;
            return false;
        }

        /// <summary>Ideographs, kana and Hangul: scripts written without spaces between words.</summary>
        static bool IsWide(char c)
        {
            return c > 0x2E80;
        }
    }
}
