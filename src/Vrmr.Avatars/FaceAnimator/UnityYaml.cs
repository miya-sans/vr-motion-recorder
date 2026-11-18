using System;
using System.Collections.Generic;
using System.Globalization;

namespace VRMR
{
    /// <summary>
    /// Reads the YAML Unity writes for its assets (controllers, animation clips, scriptable objects): a file of
    /// documents, each "--- !u!&lt;class&gt; &amp;&lt;fileID&gt;" followed by one object of nested maps, lists and flow maps such as
    /// {fileID: 1, guid: abc, type: 2}. Values are kept as strings; maps are Dictionary&lt;string, object&gt;, lists List&lt;object&gt;.
    /// Only that subset is understood; anything else in a line is kept as a plain string.
    /// </summary>
    public static class UnityYaml
    {
        /// <summary>One object of a Unity asset file.</summary>
        public sealed class Document
        {
            /// <summary>The class id (91 AnimatorController, 206 BlendTree, 1102 AnimatorState, 74 AnimationClip, …).</summary>
            public int ClassId;
            /// <summary>The object's file id within its file.</summary>
            public long FileId;
            /// <summary>The object's type name (the document's single top-level key).</summary>
            public string Type = "";
            /// <summary>The object's fields.</summary>
            public Dictionary<string, object> Body = new Dictionary<string, object>();
        }

        /// <summary>A reference to an object: in the same file when Guid is empty, else in the asset with that guid.</summary>
        public readonly struct Ref
        {
            public readonly long FileId;
            public readonly string Guid;
            public Ref(long fileId, string guid) { FileId = fileId; Guid = guid ?? ""; }
            public bool IsNull => FileId == 0;
            public override string ToString() => Guid.Length == 0 ? FileId.ToString(CultureInfo.InvariantCulture) : Guid + ":" + FileId;
        }

        /// <summary>Splits a file into its documents and parses each.</summary>
        public static List<Document> Parse(string text)
        {
            var docs = new List<Document>();
            var lines = text.Replace("\r\n", "\n").Split('\n');
            Document cur = null;
            var body = new List<string>();
            void Flush()
            {
                if (cur == null) return;
                int i = 0;
                var root = ParseBlock(body, ref i, 0) as Dictionary<string, object>;
                if (root != null && root.Count > 0)
                {
                    foreach (var kv in root) { cur.Type = kv.Key; cur.Body = kv.Value as Dictionary<string, object> ?? new Dictionary<string, object>(); break; }
                }
                docs.Add(cur);
                body.Clear();
            }
            foreach (var raw in lines)
            {
                if (raw.StartsWith("--- ", StringComparison.Ordinal))
                {
                    Flush();
                    cur = new Document();
                    foreach (var part in raw.Substring(4).Split(' '))
                    {
                        if (part.StartsWith("!u!", StringComparison.Ordinal)) int.TryParse(part.Substring(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out cur.ClassId);
                        else if (part.StartsWith("&", StringComparison.Ordinal)) long.TryParse(part.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out cur.FileId);
                    }
                    continue;
                }
                if (cur == null || raw.StartsWith("%", StringComparison.Ordinal)) continue;
                if (raw.Trim().Length == 0) continue;
                body.Add(raw);
            }
            Flush();
            return docs;
        }

        static int Indent(string line) { int n = 0; while (n < line.Length && line[n] == ' ') n++; return n; }

        /// <summary>Parses lines from i at the given indentation into a map or a list.</summary>
        static object ParseBlock(List<string> lines, ref int i, int indent)
        {
            if (i >= lines.Count) return null;
            string first = lines[i].Substring(Math.Min(indent, lines[i].Length));
            if (first.StartsWith("- ", StringComparison.Ordinal) || first == "-") return ParseList(lines, ref i, indent);
            return ParseMap(lines, ref i, indent);
        }

        static Dictionary<string, object> ParseMap(List<string> lines, ref int i, int indent)
        {
            var map = new Dictionary<string, object>(StringComparer.Ordinal);
            while (i < lines.Count)
            {
                string line = lines[i];
                int ind = Indent(line);
                if (ind < indent) break;
                if (ind > indent) { i++; continue; } // a continuation of a long scalar: ignored
                string content = line.Substring(ind);
                if (content.StartsWith("- ", StringComparison.Ordinal) || content == "-") break; // a list at this level belongs to the caller
                int colon = KeyColon(content);
                if (colon < 0) { i++; continue; }
                string key = content.Substring(0, colon).Trim();
                string rest = content.Substring(colon + 1).Trim();
                i++;
                if (rest.Length > 0) { map[key] = Scalar(rest); continue; }
                // a nested block: a map indented deeper, or a list at the same or a deeper indentation
                if (i < lines.Count)
                {
                    int next = Indent(lines[i]);
                    string nc = lines[i].Substring(next);
                    if (next > indent || (next == indent && (nc.StartsWith("- ", StringComparison.Ordinal) || nc == "-")))
                    {
                        map[key] = ParseBlock(lines, ref i, next);
                        continue;
                    }
                }
                map[key] = "";
            }
            return map;
        }

        static List<object> ParseList(List<string> lines, ref int i, int indent)
        {
            var list = new List<object>();
            while (i < lines.Count)
            {
                string line = lines[i];
                int ind = Indent(line);
                if (ind != indent) { if (ind < indent) break; i++; continue; }
                string content = line.Substring(ind);
                if (!(content.StartsWith("- ", StringComparison.Ordinal) || content == "-")) break;
                string item = content.Length > 2 ? content.Substring(2) : "";
                int colon = KeyColon(item);
                if (colon < 0 || item.StartsWith("{", StringComparison.Ordinal))
                {
                    list.Add(Scalar(item.Trim()));
                    i++;
                    continue;
                }
                // "- key: value" starts a map whose further keys are indented two more than the dash
                lines[i] = new string(' ', indent + 2) + item;
                list.Add(ParseMap(lines, ref i, indent + 2));
            }
            return list;
        }

        /// <summary>The colon that ends a key ("key: value" or "key:"), outside braces; -1 for none.</summary>
        static int KeyColon(string s)
        {
            int depth = 0;
            for (int k = 0; k < s.Length; k++)
            {
                char c = s[k];
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
                else if (c == ':' && depth == 0 && (k + 1 == s.Length || s[k + 1] == ' ')) return k;
            }
            return -1;
        }

        static object Scalar(string s)
        {
            if (s.StartsWith("{", StringComparison.Ordinal) && s.EndsWith("}", StringComparison.Ordinal))
            {
                var map = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var part in SplitTop(s.Substring(1, s.Length - 2)))
                {
                    int c = part.IndexOf(':');
                    if (c > 0) map[part.Substring(0, c).Trim()] = Scalar(part.Substring(c + 1).Trim());
                }
                return map;
            }
            if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[s.Length - 1] == s[0]) return s.Substring(1, s.Length - 2);
            return s;
        }

        static IEnumerable<string> SplitTop(string s)
        {
            int depth = 0, start = 0;
            for (int k = 0; k < s.Length; k++)
            {
                char c = s[k];
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
                else if (c == ',' && depth == 0) { yield return s.Substring(start, k - start); start = k + 1; }
            }
            if (start < s.Length) yield return s.Substring(start);
        }

        // ------------------------------------------------------------------ reading fields

        /// <summary>A string field, or "".</summary>
        public static string Str(this Dictionary<string, object> map, string key) => map != null && map.TryGetValue(key, out var v) && v is string s ? s : "";

        /// <summary>A number field, or the fallback.</summary>
        public static float Num(this Dictionary<string, object> map, string key, float fallback = 0f) =>
            float.TryParse(map.Str(key), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : fallback;

        /// <summary>A nested map, or an empty one.</summary>
        public static Dictionary<string, object> Map(this Dictionary<string, object> map, string key) =>
            map != null && map.TryGetValue(key, out var v) && v is Dictionary<string, object> d ? d : new Dictionary<string, object>();

        /// <summary>A list of maps, or an empty list.</summary>
        public static List<Dictionary<string, object>> Maps(this Dictionary<string, object> map, string key)
        {
            var res = new List<Dictionary<string, object>>();
            if (map != null && map.TryGetValue(key, out var v) && v is List<object> l)
                foreach (var o in l) if (o is Dictionary<string, object> d) res.Add(d);
            return res;
        }

        /// <summary>A reference field ({fileID: …, guid: …}).</summary>
        public static Ref RefOf(this Dictionary<string, object> map, string key)
        {
            var r = map.Map(key);
            long.TryParse(r.Str("fileID"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id);
            return new Ref(id, r.Str("guid"));
        }

        /// <summary>A position field ({x: …, y: …}).</summary>
        public static (float x, float y) Vec2(this Dictionary<string, object> map, string key)
        {
            var r = map.Map(key);
            return (r.Num("x"), r.Num("y"));
        }
    }
}
