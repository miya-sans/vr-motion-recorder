using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace VRMR
{
    /// <summary>Generic binary FBX (7.4) node tree + writer. Compatible with Autodesk FBX SDK (Unity) and Blender's importer.</summary>
    public sealed class FbxNode
    {
        public readonly List<FbxNode> Children = new();
        public readonly string Name;
        public readonly List<object> Props = new();

        public FbxNode(string name, params object[] props)
        {
            Name = name;
            Props.AddRange(props);
        }

        public FbxNode Add(string name, params object[] props)
        {
            var n = new FbxNode(name, props);
            Children.Add(n);
            return n;
        }

        public FbxNode Add(FbxNode n)
        {
            Children.Add(n);
            return n;
        }

        // Properties70 helpers
        public FbxNode P(string name, string type, string label, string flags, params object[] values)
        {
            var n = new FbxNode("P", name, type, label, flags);
            n.Props.AddRange(values);
            Children.Add(n);
            return n;
        }
    }

    /// <summary>Marks a string property that should be written as raw bytes ('R').</summary>
    public sealed class FbxRaw
    {
        public byte[] Data;
        public FbxRaw(byte[] d) { Data = d; }
    }

    public static class FbxBinaryWriter
    {
        const uint Version = 7400;
        public const string CreationTime = "1970-01-01 10:00:00:000";
        const int Sentinel = 13;
        static readonly byte[] HeadMagic = Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\x1a\0");

        public static readonly byte[] FileId =
            { 0x28, 0xb3, 0x2a, 0xeb, 0xb6, 0x24, 0xcc, 0xc2, 0xbf, 0xc8, 0xb0, 0x2a, 0xa9, 0x2b, 0xfc, 0xf1 };

        static readonly byte[] FootId =
            { 0xfa, 0xbc, 0xab, 0x09, 0xd0, 0xc8, 0xd4, 0x66, 0xb1, 0x76, 0xfb, 0x83, 0x1c, 0xf7, 0x26, 0x7e };

        static readonly byte[] FootMagic =
            { 0xf8, 0x5a, 0x8c, 0x6a, 0xde, 0xf5, 0xd9, 0x7e, 0xec, 0xe9, 0x0c, 0xe3, 0x75, 0x8f, 0x29, 0x0b };

        public static void Write(string path, IList<FbxNode> topLevel)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(HeadMagic);
                w.Write(Version);
                for (int i = 0; i < topLevel.Count; i++) WriteNode(w, topLevel[i], i == topLevel.Count - 1);
                w.Write(new byte[Sentinel]);
                // footer
                w.Write(FootId);
                w.Write(new byte[4]);
                long ofs = fs.Position;
                long pad = ((ofs + 15) & ~15L) - ofs;
                if (pad == 0) pad = 16;
                w.Write(new byte[pad]);
                w.Write(Version);
                w.Write(new byte[120]);
                w.Write(FootMagic);
            }
        }

        // Same rule as Blender's encode_bin.py (known-good with the Autodesk FBX SDK used by Unity):
        // a nested-list terminator follows children; childless nodes get one when they have no properties or are
        // AnimationStack/AnimationLayer, unless they are the last node of their list.
        static bool AlwaysSentinel(string name)
        {
            return name == "AnimationStack" || name == "AnimationLayer";
        }

        static void WriteNode(BinaryWriter w, FbxNode n, bool isLast)
        {
            var fs = w.BaseStream;
            long start = fs.Position;
            w.Write(0u); // end offset placeholder
            w.Write((uint)n.Props.Count);
            w.Write(0u); // property list length placeholder
            byte[] nameBytes = Encoding.UTF8.GetBytes(n.Name);
            w.Write((byte)nameBytes.Length);
            w.Write(nameBytes);
            long propStart = fs.Position;
            foreach (object p in n.Props) WriteProp(w, p);
            long propEnd = fs.Position;
            for (int i = 0; i < n.Children.Count; i++) WriteNode(w, n.Children[i], i == n.Children.Count - 1);
            if (n.Children.Count > 0) w.Write(new byte[Sentinel]);
            else if ((n.Props.Count == 0 || AlwaysSentinel(n.Name)) && !isLast) w.Write(new byte[Sentinel]);
            long end = fs.Position;
            fs.Position = start;
            w.Write((uint)end);
            w.Write((uint)n.Props.Count);
            w.Write((uint)(propEnd - propStart));
            fs.Position = end;
        }

        static void WriteProp(BinaryWriter w, object p)
        {
            switch (p)
            {
                case short v:
                    w.Write((byte)'Y');
                    w.Write(v);
                    break;
                case bool v:
                    w.Write((byte)'C');
                    w.Write((byte)(v ? 1 : 0));
                    break;
                case int v:
                    w.Write((byte)'I');
                    w.Write(v);
                    break;
                case float v:
                    w.Write((byte)'F');
                    w.Write(v);
                    break;
                case double v:
                    w.Write((byte)'D');
                    w.Write(v);
                    break;
                case long v:
                    w.Write((byte)'L');
                    w.Write(v);
                    break;
                case string s:
                {
                    // "Name::Class" -> "Name\0\x01Class" convention handled by caller via FbxName()
                    byte[] b = Encoding.UTF8.GetBytes(s);
                    w.Write((byte)'S');
                    w.Write((uint)b.Length);
                    w.Write(b);
                    break;
                }
                case FbxRaw r:
                    w.Write((byte)'R');
                    w.Write((uint)r.Data.Length);
                    w.Write(r.Data);
                    break;
                case float[] a:
                    WriteArray(w, 'f', a.Length, bw =>
                    {
                        foreach (float x in a) bw.Write(x);
                    }); break;
                case double[] a:
                    WriteArray(w, 'd', a.Length, bw =>
                    {
                        foreach (double x in a) bw.Write(x);
                    }); break;
                case long[] a:
                    WriteArray(w, 'l', a.Length, bw =>
                    {
                        foreach (long x in a) bw.Write(x);
                    }); break;
                case int[] a:
                    WriteArray(w, 'i', a.Length, bw =>
                    {
                        foreach (int x in a) bw.Write(x);
                    }); break;
                case bool[] a:
                    WriteArray(w, 'b', a.Length, bw =>
                    {
                        foreach (bool x in a) bw.Write((byte)(x ? 1 : 0));
                    }); break;
                default: throw new NotSupportedException("FBX property type " + p?.GetType());
            }
        }

        static void WriteArray(BinaryWriter w, char type, int count, Action<BinaryWriter> body)
        {
            var raw = new MemoryStream();
            using (var bw = new BinaryWriter(raw, Encoding.UTF8, true)) body(bw);
            byte[] data = raw.ToArray();
            w.Write((byte)type);
            w.Write((uint)count);
            if (data.Length > 128)
            {
                byte[] z = Zlib(data);
                w.Write(1u);
                w.Write((uint)z.Length);
                w.Write(z);
            }
            else
            {
                w.Write(0u);
                w.Write((uint)data.Length);
                w.Write(data);
            }
        }

        /// <summary>zlib stream (header + raw deflate + adler32).</summary>
        static byte[] Zlib(byte[] data)
        {
            var ms = new MemoryStream();
            ms.WriteByte(0x78);
            ms.WriteByte(0x9C);
            using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true)) ds.Write(data, 0, data.Length);
            uint a = 1, b = 0;
            foreach (byte x in data)
            {
                a = (a + x) % 65521;
                b = (b + a) % 65521;
            }

            uint adler = (b << 16) | a;
            ms.WriteByte((byte)(adler >> 24));
            ms.WriteByte((byte)(adler >> 16));
            ms.WriteByte((byte)(adler >> 8));
            ms.WriteByte((byte)adler);
            return ms.ToArray();
        }

        /// <summary>Binary FBX object name: "Name\0\x01Class".</summary>
        public static string Name(string name, string cls)
        {
            return (name ?? "") + "\0\x01" + cls;
        }
    }
}
