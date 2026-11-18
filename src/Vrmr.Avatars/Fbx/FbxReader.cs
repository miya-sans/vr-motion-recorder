using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace VRMR
{
    /// <summary>Reads a binary FBX (7.x, 32- and 64-bit node headers) into the FbxNode tree used by the writer.</summary>
    public static class FbxReader
    {
        public static List<FbxNode> Read(string path)
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < 27 || Encoding.ASCII.GetString(data, 0, 18) != "Kaydara FBX Binary") throw new InvalidDataException("Not a binary FBX: " + path);
            uint version = BitConverter.ToUInt32(data, 23);
            bool wide = version >= 7500;
            var top = new List<FbxNode>();
            long pos = 27;
            while (pos < data.Length)
            {
                var n = ReadNode(data, ref pos, wide);
                if (n == null) break;
                top.Add(n);
            }
            return top;
        }

        static FbxNode ReadNode(byte[] d, ref long pos, bool wide)
        {
            long end, numProps;
            if (wide) { end = (long)BitConverter.ToUInt64(d, (int)pos); numProps = (long)BitConverter.ToUInt64(d, (int)pos + 8); pos += 24; }
            else { end = BitConverter.ToUInt32(d, (int)pos); numProps = BitConverter.ToUInt32(d, (int)pos + 4); pos += 12; }
            int nameLen = d[pos++];
            if (end == 0) return null; // null record
            string name = Encoding.ASCII.GetString(d, (int)pos, nameLen);
            pos += nameLen;
            var node = new FbxNode(name);
            for (long i = 0; i < numProps; i++) node.Props.Add(ReadProp(d, ref pos));
            while (pos < end)
            {
                var c = ReadNode(d, ref pos, wide);
                if (c == null) break;
                node.Children.Add(c);
            }
            pos = end;
            return node;
        }

        static object ReadProp(byte[] d, ref long pos)
        {
            char t = (char)d[pos++];
            int p = (int)pos;
            switch (t)
            {
                case 'Y': pos += 2; return BitConverter.ToInt16(d, p);
                case 'C': pos += 1; return d[p] != 0;
                case 'I': pos += 4; return BitConverter.ToInt32(d, p);
                case 'F': pos += 4; return BitConverter.ToSingle(d, p);
                case 'D': pos += 8; return BitConverter.ToDouble(d, p);
                case 'L': pos += 8; return BitConverter.ToInt64(d, p);
                case 'S': { int len = BitConverter.ToInt32(d, p); pos += 4 + len; return Encoding.UTF8.GetString(d, p + 4, len); }
                case 'R': { int len = BitConverter.ToInt32(d, p); pos += 4 + len; var b = new byte[len]; Buffer.BlockCopy(d, p + 4, b, 0, len); return new FbxRaw(b); }
                case 'f': case 'd': case 'l': case 'i': case 'b':
                {
                    int count = BitConverter.ToInt32(d, p), enc = BitConverter.ToInt32(d, p + 4), clen = BitConverter.ToInt32(d, p + 8);
                    pos += 12 + clen;
                    int elem = t == 'd' || t == 'l' ? 8 : t == 'b' ? 1 : 4;
                    byte[] raw;
                    if (enc == 1)
                    {
                        raw = new byte[count * elem];
                        using (var ms = new MemoryStream(d, p + 12 + 2, clen - 2)) // skip the 2-byte zlib header
                        using (var z = new DeflateStream(ms, CompressionMode.Decompress))
                        {
                            int read = 0;
                            while (read < raw.Length) { int r = z.Read(raw, read, raw.Length - read); if (r <= 0) break; read += r; }
                        }
                    }
                    else { raw = new byte[count * elem]; Buffer.BlockCopy(d, p + 12, raw, 0, raw.Length); }
                    switch (t)
                    {
                        case 'f': { var a = new float[count]; Buffer.BlockCopy(raw, 0, a, 0, raw.Length); return a; }
                        case 'd': { var a = new double[count]; Buffer.BlockCopy(raw, 0, a, 0, raw.Length); return a; }
                        case 'l': { var a = new long[count]; Buffer.BlockCopy(raw, 0, a, 0, raw.Length); return a; }
                        case 'i': { var a = new int[count]; Buffer.BlockCopy(raw, 0, a, 0, raw.Length); return a; }
                        default: { var a = new bool[count]; for (int k = 0; k < count; k++) a[k] = raw[k] != 0; return a; }
                    }
                }
                default: throw new InvalidDataException($"Unknown FBX property type '{t}' at {pos - 1}");
            }
        }

        // ---------------------------------------------------------------- helpers

        public static FbxNode Child(this FbxNode n, string name) => n?.Children.FirstOrDefault(c => c.Name == name);
        public static IEnumerable<FbxNode> ChildrenNamed(this FbxNode n, string name) => n == null ? Enumerable.Empty<FbxNode>() : n.Children.Where(c => c.Name == name);
        public static T Prop<T>(this FbxNode n, int i = 0) where T : class => n != null && n.Props.Count > i ? n.Props[i] as T : null;
        public static long Id(this FbxNode n) => n.Props.Count > 0 && n.Props[0] is long l ? l : 0;
        public static string ObjName(this FbxNode n) { var s = n.Props.Count > 1 ? n.Props[1] as string : null; if (s == null) return ""; int i = s.IndexOf('\0'); return i >= 0 ? s.Substring(0, i) : s; }
        public static string ObjClass(this FbxNode n) => n.Props.Count > 2 ? n.Props[2] as string ?? "" : "";

        public static double[] Doubles(this FbxNode n) => n?.Props.Count > 0 ? n.Props[0] as double[] ?? (n.Props[0] is float[] f ? f.Select(x => (double)x).ToArray() : null) : null;
        public static int[] Ints(this FbxNode n) => n?.Props.Count > 0 ? n.Props[0] as int[] : null;

        /// <summary>Properties70 lookup: returns the numeric values of P "name".</summary>
        public static double[] P70(this FbxNode obj, string name)
        {
            var p70 = obj.Child("Properties70");
            if (p70 == null) return null;
            foreach (var p in p70.Children)
            {
                if (p.Props.Count < 5 || (p.Props[0] as string) != name) continue;
                var vals = new List<double>();
                for (int i = 4; i < p.Props.Count; i++)
                {
                    var v = p.Props[i];
                    if (v is double dd) vals.Add(dd); else if (v is float ff) vals.Add(ff); else if (v is int ii) vals.Add(ii); else if (v is long ll) vals.Add(ll); else if (v is bool bb) vals.Add(bb ? 1 : 0);
                }
                return vals.ToArray();
            }
            return null;
        }

        public static string P70Str(this FbxNode obj, string name)
        {
            var p70 = obj.Child("Properties70");
            if (p70 == null) return null;
            foreach (var p in p70.Children)
                if (p.Props.Count >= 5 && (p.Props[0] as string) == name) return p.Props[4] as string;
            return null;
        }
    }
}
