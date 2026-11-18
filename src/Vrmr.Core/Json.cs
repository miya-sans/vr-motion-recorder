using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VRMR
{
    /// <summary>Tiny dependency-free JSON reader/writer. Objects -> Dictionary&lt;string,object&gt;, arrays -> List&lt;object&gt;, numbers -> double.</summary>
    public static class Json
    {
        public static object Parse(string s)
        {
            int i = 0;
            object v = ParseValue(s, ref i);
            return v;
        }

        public static Dictionary<string, object> ParseObject(string s) => Parse(s) as Dictionary<string, object>;

        static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static object ParseValue(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>();
                i++; Ws(s, ref i);
                if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i);
                    string k = ParseString(s, ref i);
                    Ws(s, ref i);
                    if (s[i] != ':') throw new FormatException("Expected ':' at " + i);
                    i++;
                    d[k] = ParseValue(s, ref i);
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return d; }
                    throw new FormatException("Expected ',' or '}' at " + i);
                }
            }
            if (c == '[')
            {
                var l = new List<object>();
                i++; Ws(s, ref i);
                if (s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(ParseValue(s, ref i));
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return l; }
                    throw new FormatException("Expected ',' or ']' at " + i);
                }
            }
            if (c == '"') return ParseString(s, ref i);
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            if (s.Length - i >= 3 && string.CompareOrdinal(s, i, "NaN", 0, 3) == 0) { i += 3; return double.NaN; }
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (st == i) throw new FormatException("Unexpected char '" + c + "' at " + i);
            return double.Parse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static string ParseString(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("Expected string at " + i);
            i++;
            var sb = new StringBuilder();
            while (true)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c == '\\')
                {
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static string Serialize(object o, bool pretty = false)
        {
            var sb = new StringBuilder();
            Write(sb, o, pretty, 0);
            return sb.ToString();
        }

        static void Indent(StringBuilder sb, bool pretty, int lvl)
        {
            if (!pretty) return;
            sb.Append('\n');
            sb.Append(' ', lvl * 2);
        }

        static void Write(StringBuilder sb, object o, bool pretty, int lvl)
        {
            switch (o)
            {
                case null: sb.Append("null"); break;
                case string s: WriteString(sb, s); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case double d: WriteNum(sb, d); break;
                case float f: WriteNum(sb, f); break;
                case int n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                case long n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                case uint n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                case IDictionary<string, object> d:
                    {
                        sb.Append('{');
                        bool first = true;
                        foreach (var kv in d)
                        {
                            if (!first) sb.Append(',');
                            first = false;
                            Indent(sb, pretty, lvl + 1);
                            WriteString(sb, kv.Key);
                            sb.Append(pretty ? ": " : ":");
                            Write(sb, kv.Value, pretty, lvl + 1);
                        }
                        if (!first) Indent(sb, pretty, lvl);
                        sb.Append('}');
                        break;
                    }
                case System.Collections.IEnumerable e:
                    {
                        sb.Append('[');
                        bool first = true;
                        bool simple = true;
                        foreach (var x in e) { if (x is IDictionary<string, object> || (x is System.Collections.IEnumerable && !(x is string))) { simple = false; break; } }
                        foreach (var x in e)
                        {
                            if (!first) sb.Append(',');
                            first = false;
                            if (!simple) Indent(sb, pretty, lvl + 1);
                            Write(sb, x, pretty, lvl + 1);
                        }
                        if (!first && !simple) Indent(sb, pretty, lvl);
                        sb.Append(']');
                        break;
                    }
                default: WriteString(sb, o.ToString()); break;
            }
        }

        static void WriteNum(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
            sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---- typed accessors ----
        public static double GetNum(this Dictionary<string, object> d, string k, double def = 0)
        {
            if (d != null && d.TryGetValue(k, out var v))
            {
                if (v is double x) return x;
                if (v is int i) return i;
                if (v is long l) return l;
                if (v is float f) return f;
                if (v is bool b) return b ? 1 : 0;
                if (v is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return p;
            }
            return def;
        }
        public static float GetF(this Dictionary<string, object> d, string k, float def = 0) => (float)GetNum(d, k, def);
        public static int GetInt(this Dictionary<string, object> d, string k, int def = 0) => (int)Math.Round(GetNum(d, k, def));
        public static bool GetBool(this Dictionary<string, object> d, string k, bool def = false)
        {
            if (d != null && d.TryGetValue(k, out var v))
            {
                if (v is bool b) return b;
                if (v is double x) return x != 0;
            }
            return def;
        }
        public static string GetStr(this Dictionary<string, object> d, string k, string def = null)
        {
            if (d != null && d.TryGetValue(k, out var v) && v != null) return v is string s ? s : Convert.ToString(v, CultureInfo.InvariantCulture);
            return def;
        }
        public static Dictionary<string, object> GetObj(this Dictionary<string, object> d, string k)
        {
            if (d != null && d.TryGetValue(k, out var v)) return v as Dictionary<string, object>;
            return null;
        }
        public static List<object> GetList(this Dictionary<string, object> d, string k)
        {
            if (d != null && d.TryGetValue(k, out var v)) return v as List<object>;
            return null;
        }

        public static List<object> V3(Vec3 v) => new List<object> { (double)v.x, (double)v.y, (double)v.z };
        public static List<object> Q(Quat q) => new List<object> { (double)q.x, (double)q.y, (double)q.z, (double)q.w };
        public static Vec3 ToV3(object o, Vec3 def = default)
        {
            if (o is List<object> l && l.Count >= 3) return new Vec3(F(l[0]), F(l[1]), F(l[2]));
            return def;
        }
        public static Quat ToQ(object o)
        {
            if (o is List<object> l && l.Count >= 4) return new Quat(F(l[0]), F(l[1]), F(l[2]), F(l[3]));
            return Quat.Identity;
        }
        static float F(object o) => o is double d ? (float)d : 0f;
    }
}
