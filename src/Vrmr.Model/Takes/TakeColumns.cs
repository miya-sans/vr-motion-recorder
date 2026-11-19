using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace VRMR
{
    /// <summary>
    ///     Compressed sample chunks of a saved take. Each chunk holds one block of one track: a header
    ///     (u16 id, i32 count, f64 last sample time, i32 raw length) and a Brotli body of integer columns. Every column is
    ///     delta coded and stored byte plane by byte plane. Times are kept in microseconds and positions in micrometres.
    ///     A rotation keeps the index and sign of its largest component and the other three in 16 bits; the largest is
    ///     rebuilt so the rotation stays unit length (a repeated pose still reads as exactly repeated). Parameter values,
    ///     finger summaries and finger bones are kept exactly. Writing replaces the in-memory samples with the stored
    ///     values, so an open take solves as it will after loading.
    /// </summary>
    static class TakeColumns
    {
        public const int HeaderLength = 18;
        const int HandBlock = 16384, HumrBlock = 8192, MaxRawLength = 32 << 20, ParamBlock = 65536, PoseBlock = 65536,
            Quality = 5, Window = 22;
        const double TimeScale = 1e6, PosScale = 1e6, RotScale = 46340;

        public static readonly uint PCOL = Tag("PCOL"), SCOL = Tag("SCOL"), HCOL = Tag("HCOL"), FCOL = Tag("FCOL");

        static uint Tag(string s)
        {
            return (uint)(s[0] | (s[1] << 8) | (s[2] << 16) | (s[3] << 24));
        }

        static long Time(double t)
        {
            return (long)Math.Round(t * TimeScale);
        }

        static long Pos(float v)
        {
            return float.IsFinite(v) ? (long)Math.Round(v * PosScale) : 0;
        }

        static float PosOf(long k)
        {
            return (float)(k / PosScale);
        }

        static int Largest(Quat q)
        {
            int m = 0;
            float best = -1;
            for (int axis = 0; axis < 4; axis++)
            {
                float a = Math.Abs(At(ref q, axis));
                if (!(a > best)) continue;
                best = a;
                m = axis;
            }

            return At(ref q, m) < 0 ? m | 4 : m;
        }

        static long Small(Quat q, int largest, int j)
        {
            int axis = j < (largest & 3) ? j : j + 1;
            float v = At(ref q, axis);
            return float.IsFinite(v) ? (long)Math.Clamp(Math.Round(v * RotScale), -32767, 32767) : 0;
        }

        static Quat Rotation(long largest, long a, long b, long c)
        {
            double x = a / RotScale, y = b / RotScale, z = c / RotScale;
            double big = Math.Sqrt(Math.Max(0, 1 - x * x - y * y - z * z)) * ((largest & 4) != 0 ? -1 : 1);
            var q = new Quat();
            int m = (int)(largest & 3), j = 0;
            for (int axis = 0; axis < 4; axis++)
            {
                if (axis == m)
                {
                    At(ref q, axis) = (float)big;
                    continue;
                }

                At(ref q, axis) = (float)(j == 0 ? x : j == 1 ? y : z);
                j++;
            }

            return q;
        }

        /// <summary>
        ///     The stored form of a rotation. A component tied with the largest could outrank the rebuilt one after
        ///     rounding; it is moved one step towards zero, so storing the result again gives the same value.
        /// </summary>
        static Quat Stored(Quat q)
        {
            int m = Largest(q);
            long a = Small(q, m, 0), b = Small(q, m, 1), c = Small(q, m, 2);
            var r = Rotation(m, a, b, c);
            while (Largest(r) != m)
            {
                if (Math.Abs(a) >= Math.Abs(b) && Math.Abs(a) >= Math.Abs(c)) a -= Math.Sign(a);
                else if (Math.Abs(b) >= Math.Abs(c)) b -= Math.Sign(b);
                else c -= Math.Sign(c);
                r = Rotation(m, a, b, c);
            }

            return r;
        }

        static void WriteRotations(Columns c, Span<Quat> q)
        {
            long[] col = c.Scratch;
            for (int k = 0; k < q.Length; k++) q[k] = Stored(q[k]);
            for (int k = 0; k < q.Length; k++) col[k] = Largest(q[k]);
            c.Add(1);
            for (int j = 0; j < 3; j++)
            {
                for (int k = 0; k < q.Length; k++) col[k] = Small(q[k], Largest(q[k]), j);
                c.Add(2);
            }
        }

        static void ReadRotations(Columns r, Quat[] q)
        {
            int n = q.Length;
            long[] largest = (long[])r.Next(1).Clone(), a = (long[])r.Next(2).Clone(), b = (long[])r.Next(2).Clone();
            long[] c = r.Next(2);
            for (int k = 0; k < n; k++) q[k] = Rotation(largest[k], a[k], b[k], c[k]);
        }

        static ref float At(ref Vec3 v, int axis)
        {
            if (axis == 0) return ref v.x;
            if (axis == 1) return ref v.y;
            return ref v.z;
        }

        static ref float At(ref Quat q, int axis)
        {
            if (axis == 0) return ref q.x;
            if (axis == 1) return ref q.y;
            if (axis == 2) return ref q.z;
            return ref q.w;
        }

        /// <summary>Last sample time stored in a column chunk's header.</summary>
        public static double LastTime(byte[] all, int pos)
        {
            return BitConverter.ToDouble(all, pos + 6);
        }

        // ------------------------------------------------------------ write

        /// <summary>
        ///     Writes the samples of a take as column chunks: parameters, poses, VRChat IK frames, then fingers. Blocks are
        ///     compressed in parallel; the file is the same as if they were written one after another.
        /// </summary>
        public static void Write(BinaryWriter w, Recording rec)
        {
            var blocks = new List<Action<BinaryWriter, Columns>>();
            for (int i = 0; i < rec.Params.Count; i++)
            {
                var p = rec.Params[i];
                ushort id = (ushort)i;
                for (int s = 0; s < p.Count; s += ParamBlock)
                {
                    int start = s, n = Math.Min(ParamBlock, p.Count - s);
                    blocks.Add((bw, c) => WriteParam(bw, c, id, p, start, n));
                }
            }

            foreach (var st in rec.Streams)
                for (int s = 0; s < st.Count; s += PoseBlock)
                {
                    int start = s, n = Math.Min(PoseBlock, st.Count - s);
                    blocks.Add((bw, c) => WritePoses(bw, c, st, start, n));
                }

            var h = rec.Humr;
            for (int s = 0; s < h.Count; s += HumrBlock)
            {
                int start = s, n = Math.Min(HumrBlock, h.Count - s);
                blocks.Add((bw, c) => WriteHumr(bw, c, h, start, n));
            }

            foreach (var hs in rec.Hands)
                for (int s = 0; s < hs.Count; s += HandBlock)
                {
                    int start = s, n = Math.Min(HandBlock, hs.Count - s);
                    blocks.Add((bw, c) => WriteHand(bw, c, hs, start, n));
                }

            byte[][] chunks = new byte[blocks.Count][];
            Parallel.For(0, blocks.Count, () => new Columns(), (i, _, c) =>
            {
                var ms = new MemoryStream();
                using (var bw = new BinaryWriter(ms))
                {
                    blocks[i](bw, c);
                    bw.Flush();
                    chunks[i] = ms.ToArray();
                }

                return c;
            }, _ => { });
            foreach (byte[] chunk in chunks) w.Write(chunk);
        }

        static void WriteParam(BinaryWriter w, Columns c, ushort id, ParamTrack p, int s, int n)
        {
            c.Begin(n);
            long[] col = c.Scratch;
            WriteTimes(c, p.T, s, n);
            for (int k = 0; k < n; k++) col[k] = BitConverter.SingleToInt32Bits(p.V[s + k]);
            c.Add(4);
            c.Write(w, PCOL, id, p.T[s + n - 1]);
        }

        static void WritePoses(BinaryWriter w, Columns c, PoseStream st, int s, int n)
        {
            var p = CollectionsMarshal.AsSpan(st.P);
            c.Begin(n);
            long[] col = c.Scratch;
            WriteTimes(c, st.T, s, n);
            for (int axis = 0; axis < 3; axis++)
            {
                for (int k = 0; k < n; k++) col[k] = Pos(At(ref p[s + k], axis));
                c.Add(8);
                for (int k = 0; k < n; k++) At(ref p[s + k], axis) = PosOf(col[k]);
            }

            WriteRotations(c, CollectionsMarshal.AsSpan(st.R).Slice(s, n));
            c.Write(w, SCOL, (ushort)st.Id, st.T[s + n - 1]);
        }

        static void WriteHumr(BinaryWriter w, Columns c, HumrStream h, int s, int n)
        {
            var hips = CollectionsMarshal.AsSpan(h.Hips);
            c.Begin(n);
            long[] col = c.Scratch;
            WriteTimes(c, h.T, s, n);
            WriteTimes(c, h.SrcT, s, n);
            for (int axis = 0; axis < 3; axis++)
            {
                for (int k = 0; k < n; k++) col[k] = Pos(At(ref hips[s + k], axis));
                c.Add(8);
                for (int k = 0; k < n; k++) At(ref hips[s + k], axis) = PosOf(col[k]);
            }

            var bone = new Quat[n];
            for (int b = 0; b < HumanBones.Count; b++)
            {
                for (int k = 0; k < n; k++) bone[k] = h.Rot[s + k][b];
                WriteRotations(c, bone);
                for (int k = 0; k < n; k++) h.Rot[s + k][b] = bone[k];
            }

            c.Write(w, HCOL, 0, h.T[s + n - 1]);
        }

        static void WriteHand(BinaryWriter w, Columns c, HandStream hs, int s, int n)
        {
            c.Begin(n);
            long[] col = c.Scratch;
            WriteTimes(c, hs.T, s, n);
            for (int j = 0; j < 9; j++)
            {
                for (int k = 0; k < n; k++) col[k] = BitConverter.SingleToInt32Bits(hs.Summary[s + k][j]);
                c.Add(4);
            }

            for (int j = 0; j < HandStream.PackedLength; j++)
            {
                for (int k = 0; k < n; k++) col[k] = hs.Bones[s + k][j];
                c.Add(2);
            }

            c.Write(w, FCOL, (ushort)hs.Side, hs.T[s + n - 1]);
        }

        static void WriteTimes(Columns c, List<double> t, int s, int n)
        {
            long[] col = c.Scratch;
            for (int k = 0; k < n; k++) col[k] = Time(t[s + k]);
            c.Add(8);
            for (int k = 0; k < n; k++) t[s + k] = col[k] / TimeScale;
        }

        // ------------------------------------------------------------ read
        // A chunk is decoded completely before its samples are added, so a damaged chunk adds nothing.

        public static void ReadParam(byte[] all, int pos, int len, Dictionary<int, ParamTrack> byId)
        {
            var r = Columns.Open(all, pos, len, out int id, out int n);
            double[] t = ReadTimes(r, n);
            long[] col = r.Next(4);
            var v = new float[n];
            for (int k = 0; k < n; k++) v[k] = BitConverter.Int32BitsToSingle((int)col[k]);
            r.End();
            if (!byId.TryGetValue(id, out var p)) return;
            p.T.AddRange(t);
            p.V.AddRange(v);
        }

        public static void ReadPoses(byte[] all, int pos, int len, Dictionary<int, PoseStream> byId)
        {
            var r = Columns.Open(all, pos, len, out int id, out int n);
            double[] t = ReadTimes(r, n);
            var p = new Vec3[n];
            var q = new Quat[n];
            for (int axis = 0; axis < 3; axis++)
            {
                long[] col = r.Next(8);
                for (int k = 0; k < n; k++) At(ref p[k], axis) = PosOf(col[k]);
            }

            ReadRotations(r, q);
            r.End();
            if (!byId.TryGetValue(id, out var st)) return;
            st.T.AddRange(t);
            st.P.AddRange(p);
            st.R.AddRange(q);
        }

        public static void ReadHumr(byte[] all, int pos, int len, HumrStream h)
        {
            var r = Columns.Open(all, pos, len, out _, out int n);
            double[] t = ReadTimes(r, n), src = ReadTimes(r, n);
            var hips = new Vec3[n];
            for (int axis = 0; axis < 3; axis++)
            {
                long[] col = r.Next(8);
                for (int k = 0; k < n; k++) At(ref hips[k], axis) = PosOf(col[k]);
            }

            var rot = new Quat[n][];
            for (int k = 0; k < n; k++) rot[k] = new Quat[HumanBones.Count];
            var bone = new Quat[n];
            for (int b = 0; b < HumanBones.Count; b++)
            {
                ReadRotations(r, bone);
                for (int k = 0; k < n; k++) rot[k][b] = bone[k];
            }

            r.End();
            h.T.AddRange(t);
            h.SrcT.AddRange(src);
            h.Hips.AddRange(hips);
            h.Rot.AddRange(rot);
        }

        public static void ReadHand(byte[] all, int pos, int len, HandStream[] hands)
        {
            var r = Columns.Open(all, pos, len, out int side, out int n);
            double[] t = ReadTimes(r, n);
            var sum = new float[n][];
            var bones = new short[n][];
            for (int k = 0; k < n; k++)
            {
                sum[k] = new float[9];
                bones[k] = new short[HandStream.PackedLength];
            }

            for (int j = 0; j < 9; j++)
            {
                long[] col = r.Next(4);
                for (int k = 0; k < n; k++) sum[k][j] = BitConverter.Int32BitsToSingle((int)col[k]);
            }

            for (int j = 0; j < HandStream.PackedLength; j++)
            {
                long[] col = r.Next(2);
                for (int k = 0; k < n; k++) bones[k][j] = (short)col[k];
            }

            r.End();
            if (side >= hands.Length) return;
            var hs = hands[side];
            hs.T.AddRange(t);
            hs.Summary.AddRange(sum);
            hs.Bones.AddRange(bones);
        }

        /// <summary>A time column; times are stored in order, so a column out of order is damage.</summary>
        static double[] ReadTimes(Columns r, int n)
        {
            long[] col = r.Next(8);
            var t = new double[n];
            for (int k = 0; k < n; k++)
            {
                if (k > 0 && col[k] < col[k - 1]) throw new InvalidDataException("Sample times out of order");
                t[k] = col[k] / TimeScale;
            }

            return t;
        }

        /// <summary>Builds and reads the delta-coded, byte-plane column body of one chunk.</summary>
        public sealed class Columns
        {
            readonly byte[] _raw;
            byte[] _buf = new byte[1 << 16], _packed = Array.Empty<byte>();
            int _len, _n, _pos;
            long[] _values = Array.Empty<long>();

            public Columns() { }

            Columns(byte[] raw, int n)
            {
                _raw = raw;
                _n = n;
                _values = new long[n];
            }

            /// <summary>Values of the next column to add; valid up to the count given to <see cref="Begin" />.</summary>
            public long[] Scratch => _values;

            public void Begin(int n)
            {
                _n = n;
                _len = 0;
                if (_values.Length < n) _values = new long[n];
            }

            /// <summary>Appends the first <c>n</c> values of <see cref="Scratch" /> as a column of the given byte width.</summary>
            public void Add(int width)
            {
                int need = _len + _n * width;
                if (_buf.Length < need) Array.Resize(ref _buf, Math.Max(need, _buf.Length * 2));
                long prev = 0;
                for (int k = 0; k < _n; k++)
                {
                    long d = _values[k] - prev;
                    prev = _values[k];
                    for (int b = 0; b < width; b++) _buf[_len + b * _n + k] = (byte)(d >> (8 * b));
                }

                _len = need;
            }

            public void Write(BinaryWriter w, uint tag, ushort id, double lastTime)
            {
                int max = BrotliEncoder.GetMaxCompressedLength(_len);
                if (_packed.Length < max) _packed = new byte[max];
                if (!BrotliEncoder.TryCompress(_buf.AsSpan(0, _len), _packed, out int written, Quality, Window))
                    throw new IOException("Could not compress take samples");
                w.Write(tag);
                w.Write(HeaderLength + written);
                w.Write(id);
                w.Write(_n);
                w.Write(lastTime);
                w.Write(_len);
                w.Write(_packed, 0, written);
            }

            public static Columns Open(byte[] all, int pos, int len, out int id, out int n)
            {
                if (len < HeaderLength) throw new InvalidDataException("Bad sample chunk");
                id = BitConverter.ToUInt16(all, pos);
                n = BitConverter.ToInt32(all, pos + 2);
                int rawLen = BitConverter.ToInt32(all, pos + 14);
                // every track stores at least 12 bytes per sample (a time and a value)
                if (n < 0 || rawLen < 0 || rawLen > MaxRawLength || n * 12L > rawLen)
                    throw new InvalidDataException("Bad sample chunk");
                byte[] raw = new byte[rawLen];
                if (!BrotliDecoder.TryDecompress(all.AsSpan(pos + HeaderLength, len - HeaderLength), raw,
                        out int written) || written != rawLen)
                    throw new InvalidDataException("Bad sample chunk");
                return new Columns(raw, n);
            }

            /// <summary>Decodes the next column; the returned array is reused by the following call.</summary>
            public long[] Next(int width)
            {
                if (_pos + _n * width > _raw.Length) throw new InvalidDataException("Bad sample chunk");
                int shift = 64 - 8 * width;
                long acc = 0;
                for (int k = 0; k < _n; k++)
                {
                    long d = 0;
                    for (int b = 0; b < width; b++) d |= (long)_raw[_pos + b * _n + k] << (8 * b);
                    acc = (acc + d) << shift >> shift;
                    _values[k] = acc;
                }

                _pos += _n * width;
                return _values;
            }

            /// <summary>Checks that every column was read: a body of another length is damaged.</summary>
            public void End()
            {
                if (_pos != _raw.Length) throw new InvalidDataException("Bad sample chunk");
            }
        }
    }
}
