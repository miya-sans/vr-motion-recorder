using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace VRMR
{
    /// <summary>
    /// Reads Unity packages (.unitypackage = tar.gz of GUID folders, each with "pathname", "asset" and "asset.meta"),
    /// directly or inside a .zip (how Booth add-ons ship). Nothing is written to disk and no path from the archive is
    /// ever used as a file path: callers get the Unity path as a string and the bytes in memory.
    /// </summary>
    public sealed class UnityPackage
    {
        public sealed class Item
        {
            public string Guid = "", Path = "";
            public long AssetSize = -1;
            public byte[] Meta;
            public string FileName => Leaf(Path);
            public string Ext { get { string f = FileName; int i = f.LastIndexOf('.'); return i >= 0 ? f.Substring(i).ToLowerInvariant() : ""; } }
            public string Stem { get { string f = FileName; int i = f.LastIndexOf('.'); return i > 0 ? f.Substring(0, i) : f; } }
            public string MetaText => Meta == null ? "" : Encoding.UTF8.GetString(Meta);
        }

        public string Source = "";      // file the package came from (a .unitypackage, or "x.zip > y.unitypackage")
        readonly Func<Stream> _open;     // a fresh gzip-compressed tar stream per pass
        public readonly Dictionary<string, Item> Items = new Dictionary<string, Item>();

        UnityPackage(string source, Func<Stream> open) { Source = source; _open = open; }

        /// <summary>Every Unity package in a file: the .unitypackage itself, or each one inside a .zip.</summary>
        public static List<UnityPackage> Open(string file)
        {
            var res = new List<UnityPackage>();
            string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".unitypackage") res.Add(new UnityPackage(file, () => File.OpenRead(file)));
            else if (ext == ".zip")
            {
                using (var z = ZipFile.OpenRead(file))
                    foreach (var e in z.Entries.Where(e => e.FullName.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase)))
                    {
                        string name = e.FullName;
                        res.Add(new UnityPackage(file + " > " + name, () =>
                        {
                            var za = ZipFile.OpenRead(file);
                            var s = za.GetEntry(name).Open();
                            return new OwnerStream(s, za);
                        }));
                    }
            }
            foreach (var p in res) p.Index();
            return res;
        }

        /// <summary>Pass 1: the Unity path, size and .meta of every asset.</summary>
        void Index()
        {
            Walk((guid, kind, size, read) =>
            {
                if (!Items.TryGetValue(guid, out var it)) Items[guid] = it = new Item { Guid = guid };
                if (kind == "pathname") it.Path = Encoding.UTF8.GetString(read()).Split('\n')[0].Trim().Replace('\\', '/');
                else if (kind == "asset.meta") it.Meta = read();
                else if (kind == "asset") it.AssetSize = size;
            });
            foreach (var k in Items.Where(kv => kv.Value.Path.Length == 0).Select(kv => kv.Key).ToList()) Items.Remove(k);
        }

        /// <summary>Pass 2: the asset bytes of the chosen items (by GUID).</summary>
        public Dictionary<string, byte[]> Read(IEnumerable<string> guids)
        {
            var want = new HashSet<string>(guids);
            var res = new Dictionary<string, byte[]>();
            if (want.Count == 0) return res;
            Walk((guid, kind, size, read) => { if (kind == "asset" && want.Contains(guid)) res[guid] = read(); });
            return res;
        }

        /// <summary>Last path segment of a Unity path or "a.zip > b.unitypackage" source (no System.IO path rules).</summary>
        public static string Leaf(string p) { if (string.IsNullOrEmpty(p)) return ""; int i = Math.Max(p.LastIndexOf('/'), Math.Max(p.LastIndexOf((char)92), p.LastIndexOf('>'))); return p.Substring(i + 1).Trim(); }

        public IEnumerable<Item> WithExt(params string[] exts) => Items.Values.Where(i => exts.Contains(i.Ext));

        // ------------------------------------------------------------------ tar

        void Walk(Action<string, string, long, Func<byte[]>> onFile)
        {
            using (var raw = _open())
            using (var gz = new GZipStream(raw, CompressionMode.Decompress))
            using (var bs = new BufferedStream(gz, 1 << 16))
            {
                var hdr = new byte[512];
                string longName = null;
                while (true)
                {
                    if (!ReadFull(bs, hdr, 512)) break;
                    if (hdr.All(b => b == 0)) break;
                    string name = Str(hdr, 0, 100);
                    long size = Octal(hdr, 124, 12);
                    char type = (char)hdr[156];
                    string prefix = Encoding.ASCII.GetString(hdr, 257, 5) == "ustar" ? Str(hdr, 345, 155) : "";
                    if (prefix.Length > 0) name = prefix + "/" + name;
                    long padded = (size + 511) / 512 * 512;
                    bool consumed = false;
                    byte[] data = null;
                    Func<byte[]> read = () =>
                    {
                        if (data != null) return data;
                        if (size > int.MaxValue - 1024) throw new InvalidDataException("asset too large");
                        data = new byte[size];
                        if (!ReadFull(bs, data, (int)size)) throw new EndOfStreamException();
                        consumed = true;
                        return data;
                    };
                    if (type == 'L') { longName = Encoding.UTF8.GetString(read()).TrimEnd('\0'); Skip(bs, padded - size); continue; }
                    if (type == 'x' || type == 'g')
                    {
                        var pax = Encoding.UTF8.GetString(read());
                        foreach (var line in pax.Split('\n'))
                        {
                            int sp = line.IndexOf(' '), eq = line.IndexOf('=');
                            if (sp > 0 && eq > sp && line.Substring(sp + 1, eq - sp - 1) == "path") longName = line.Substring(eq + 1);
                        }
                        Skip(bs, padded - size); continue;
                    }
                    if (longName != null) { name = longName; longName = null; }
                    if (type == '0' || type == '\0')
                    {
                        var parts = name.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).Where(x => x != ".").ToArray();
                        if (parts.Length == 2) onFile(parts[0], parts[1], size, read);
                    }
                    Skip(bs, (consumed ? 0 : size) + (padded - size));
                }
            }
        }

        static bool ReadFull(Stream s, byte[] b, int n)
        {
            int off = 0;
            while (off < n) { int r = s.Read(b, off, n - off); if (r <= 0) return false; off += r; }
            return true;
        }

        static readonly byte[] SkipBuf = new byte[1 << 16];
        static void Skip(Stream s, long n)
        {
            lock (SkipBuf)
                while (n > 0) { int r = s.Read(SkipBuf, 0, (int)Math.Min(SkipBuf.Length, n)); if (r <= 0) return; n -= r; }
        }

        static string Str(byte[] b, int o, int n) { int e = o; while (e < o + n && b[e] != 0) e++; return Encoding.UTF8.GetString(b, o, e - o); }

        static long Octal(byte[] b, int o, int n)
        {
            if ((b[o] & 0x80) != 0) { long v = 0; for (int i = 1; i < n; i++) v = (v << 8) | b[o + i]; return v; } // base-256 (large files)
            long r = 0;
            for (int i = o; i < o + n; i++) { byte c = b[i]; if (c == 0 || c == ' ') { if (r > 0) break; continue; } if (c < '0' || c > '7') break; r = r * 8 + (c - '0'); }
            return r;
        }

        /// <summary>A zip entry stream that also closes its archive.</summary>
        sealed class OwnerStream : Stream
        {
            readonly Stream _s; readonly IDisposable _owner;
            public OwnerStream(Stream s, IDisposable owner) { _s = s; _owner = owner; }
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => _s.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) { _s.Dispose(); _owner.Dispose(); } base.Dispose(disposing); }
        }
    }
}
