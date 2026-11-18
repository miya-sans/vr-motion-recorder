using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace VRMR
{
    /// <summary>
    ///     .vrmr container: "VRMR" u16 version u16 flags, then chunks [u32 tag][u32 length][payload].
    ///     Chunks are appended while recording (crash safe: a truncated tail is ignored on load).
    /// </summary>
    public static class RecordingIO
    {
        public const ushort Version = 1;

        public static readonly uint META = Tag("META"),
            PROF = Tag("PROF"),
            PDEF = Tag("PDEF"),
            PSMP = Tag("PSMP"),
            SDEF = Tag("SDEF"),
            SSMP = Tag("SSMP"),
            HUMR = Tag("HUMR"),
            HAND = Tag("HAND"),
            EVNT = Tag("EVNT"),
            END = Tag("END ");

        static uint Tag(string s)
        {
            return (uint)(s[0] | (s[1] << 8) | (s[2] << 16) | (s[3] << 24));
        }

        public static void WriteHeader(BinaryWriter w)
        {
            w.Write(Encoding.ASCII.GetBytes("VRMR"));
            w.Write(Version);
            w.Write((ushort)0);
        }

        public static void WriteChunk(BinaryWriter w, uint tag, byte[] payload, int len)
        {
            w.Write(tag);
            w.Write(len);
            w.Write(payload, 0, len);
        }

        public static void WriteJsonChunk(BinaryWriter w, uint tag, object json)
        {
            byte[] b = Encoding.UTF8.GetBytes(Json.Serialize(json));
            WriteChunk(w, tag, b, b.Length);
        }

        // ------------------------------------------------------------ full save (after edits)

        public static void Save(Recording rec, string path)
        {
            string tmp = path + ".tmp";
            lock (rec.Sync)
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                using (var w = new BinaryWriter(fs))
                {
                    WriteHeader(w);
                    WriteJsonChunk(w, META, rec.MetaJson());
                    if (rec.Profile != null) WriteJsonChunk(w, PROF, rec.Profile.ToJson());
                    // params
                    var ms = new MemoryStream();
                    var bw = new BinaryWriter(ms);
                    bw.Write(rec.Params.Count);
                    for (int i = 0; i < rec.Params.Count; i++) WriteParamDef(bw, (ushort)i, rec.Params[i]);
                    Flush(w, PDEF, ms);
                    for (int i = 0; i < rec.Params.Count; i++)
                    {
                        var p = rec.Params[i];
                        for (int s = 0; s < p.Count; s += 100000)
                        {
                            int n = Math.Min(100000, p.Count - s);
                            ms.SetLength(0);
                            bw.Write(n);
                            for (int k = s; k < s + n; k++)
                            {
                                bw.Write(p.T[k]);
                                bw.Write((ushort)i);
                                bw.Write(p.V[k]);
                            }

                            Flush(w, PSMP, ms);
                        }
                    }

                    // pose streams
                    ms.SetLength(0);
                    bw.Write(rec.Streams.Count);
                    foreach (var st in rec.Streams) WriteStreamDef(bw, st);
                    Flush(w, SDEF, ms);
                    foreach (var st in rec.Streams)
                    {
                        for (int s = 0; s < st.Count; s += 50000)
                        {
                            int n = Math.Min(50000, st.Count - s);
                            ms.SetLength(0);
                            bw.Write(n);
                            for (int k = s; k < s + n; k++) WritePose(bw, st.T[k], (ushort)st.Id, st.P[k], st.R[k]);
                            Flush(w, SSMP, ms);
                        }
                    }

                    // HUMR frames
                    var h = rec.Humr;
                    for (int s = 0; s < h.Count; s += 5000)
                    {
                        int n = Math.Min(5000, h.Count - s);
                        ms.SetLength(0);
                        bw.Write(n);
                        for (int k = s; k < s + n; k++) WriteHumr(bw, h.T[k], h.SrcT[k], h.Hips[k], h.Rot[k]);
                        Flush(w, HUMR, ms);
                    }

                    foreach (var hs in rec.Hands)
                        for (int s0 = 0; s0 < hs.Count; s0 += 10000)
                        {
                            int n = Math.Min(10000, hs.Count - s0);
                            ms.SetLength(0);
                            bw.Write(n);
                            for (int k = s0; k < s0 + n; k++)
                                WriteHand(bw, hs.T[k], hs.Side, hs.Summary[k], hs.Bones[k]);
                            Flush(w, HAND, ms);
                        }

                    WriteJsonChunk(w, EVNT,
                        rec.Events.Select(e => (object)new Dictionary<string, object>
                            { ["t"] = e.T, ["type"] = e.Type, ["text"] = e.Text }).ToList());
                    w.Write(END);
                    w.Write(0);
                }
            }

            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            rec.FilePath = path;
        }

        static void Flush(BinaryWriter w, uint tag, MemoryStream ms)
        {
            WriteChunk(w, tag, ms.GetBuffer(), (int)ms.Length);
        }

        internal static void WriteParamDef(BinaryWriter bw, ushort id, ParamTrack p)
        {
            bw.Write(id);
            bw.Write((byte)p.Type);
            byte[] nb = Encoding.UTF8.GetBytes(p.Name);
            bw.Write((ushort)nb.Length);
            bw.Write(nb);
        }

        internal static void WriteStreamDef(BinaryWriter bw, PoseStream st)
        {
            bw.Write((ushort)st.Id);
            bw.Write((byte)st.Kind);
            byte[] nb = Encoding.UTF8.GetBytes(st.Name);
            bw.Write((ushort)nb.Length);
            bw.Write(nb);
            byte[] ib = Encoding.UTF8.GetBytes(Json.Serialize(st.Info));
            bw.Write((ushort)ib.Length);
            bw.Write(ib);
        }

        internal static void WritePose(BinaryWriter bw, double t, ushort id, Vec3 p, Quat r)
        {
            bw.Write(t);
            bw.Write(id);
            bw.Write(p.x);
            bw.Write(p.y);
            bw.Write(p.z);
            bw.Write(r.x);
            bw.Write(r.y);
            bw.Write(r.z);
            bw.Write(r.w);
        }

        internal static void WriteHand(BinaryWriter bw, double t, int side, float[] summary, short[] bones)
        {
            bw.Write(t);
            bw.Write((byte)side);
            for (int k = 0; k < 9; k++) bw.Write(summary[k]);
            bw.Write((ushort)bones.Length);
            foreach (short v in bones) bw.Write(v);
        }

        internal static void WriteHumr(BinaryWriter bw, double t, double srcT, Vec3 hips, Quat[] rot)
        {
            bw.Write(t);
            bw.Write(srcT);
            bw.Write(hips.x);
            bw.Write(hips.y);
            bw.Write(hips.z);
            for (int b = 0; b < HumanBones.Count; b++)
            {
                var q = rot[b];
                bw.Write(q.x);
                bw.Write(q.y);
                bw.Write(q.z);
                bw.Write(q.w);
            }
        }

        public static Summary ReadSummary(string path)
        {
            var s = new Summary { Name = Path.GetFileNameWithoutExtension(path) };
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var r = new BinaryReader(fs))
            {
                if (fs.Length < 8 || Encoding.ASCII.GetString(r.ReadBytes(4)) != "VRMR")
                    throw new InvalidDataException();
                fs.Position = 8;
                Dictionary<string, object> meta = null;
                double maxT = 0;
                while (fs.Position + 8 <= fs.Length)
                {
                    uint tag = r.ReadUInt32();
                    int len = r.ReadInt32();
                    long start = fs.Position;
                    if (len < 0 || start + len > fs.Length || tag == END) break;
                    if (tag == META) meta = Json.ParseObject(Encoding.UTF8.GetString(r.ReadBytes(len)));
                    else if (tag == HUMR) s.HasHumr = true;
                    else if (tag == HAND) s.HasFingers = true;
                    else if (tag == SSMP) { s.HasDevices = true; }
                    else if (tag == PDEF && !s.HasFace)
                    {
                        byte[] b = r.ReadBytes(len);
                        if (Encoding.UTF8.GetString(b).Contains("FT/")) s.HasFace = true;
                    }

                    if ((tag == PSMP || tag == SSMP || tag == HUMR) && len >= 12)
                    {
                        // last sample time of the chunk (samples are time-first records)
                        fs.Position = start;
                        int n = r.ReadInt32();
                        int rec = n > 0 ? (len - 4) / n : 0;
                        if (rec >= 8)
                        {
                            fs.Position = start + 4 + (long)(n - 1) * rec;
                            maxT = Math.Max(maxT, r.ReadDouble());
                        }
                    }

                    fs.Position = start + len;
                }

                if (meta != null)
                {
                    s.Name = meta.GetStr("name", s.Name);
                    s.AvatarName = meta.GetStr("avatarName", "");
                    if (string.IsNullOrEmpty(s.AvatarName)) s.AvatarName = meta.GetStr("avatarId", "");
                    s.World = meta.GetStr("world", "");
                    s.Created = meta.GetStr("created", "");
                    s.Duration = meta.GetNum("duration");
                }

                if (s.Duration <= 0) s.Duration = maxT;
            }

            return s;
        }

        // ------------------------------------------------------------ load

        public static Recording Load(string path)
        {
            var rec = new Recording { FilePath = path };
            byte[] all;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                all = new byte[fs.Length];
                int off = 0;
                while (off < all.Length)
                {
                    int n = fs.Read(all, off, all.Length - off);
                    if (n <= 0) break;
                    off += n;
                }
            }

            if (all.Length < 8 || Encoding.ASCII.GetString(all, 0, 4) != "VRMR")
                throw new InvalidDataException("Not a VR Motion Recorder file");
            int pos = 8;
            var paramById = new Dictionary<int, ParamTrack>();
            var streamById = new Dictionary<int, PoseStream>();
            Dictionary<string, object> meta = null;
            while (pos + 8 <= all.Length)
            {
                uint tag = BitConverter.ToUInt32(all, pos);
                int len = BitConverter.ToInt32(all, pos + 4);
                pos += 8;
                if (len < 0 || pos + len > all.Length) break; // truncated tail (crash) -> stop
                if (tag == END) break;
                using (var ms = new MemoryStream(all, pos, len, false))
                using (var r = new BinaryReader(ms))
                {
                    try
                    {
                        if (tag == META) meta = Json.ParseObject(Encoding.UTF8.GetString(all, pos, len));
                        else if (tag == PROF)
                            rec.Profile =
                                AvatarProfile.FromJson(Json.ParseObject(Encoding.UTF8.GetString(all, pos, len)));
                        else if (tag == PDEF)
                        {
                            int n = r.ReadInt32();
                            for (int i = 0; i < n; i++)
                            {
                                int id = r.ReadUInt16();
                                var type = (ParamType)r.ReadByte();
                                string name = Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16()));
                                paramById[id] = rec.GetOrAddParam(name, type);
                            }
                        }
                        else if (tag == PSMP)
                        {
                            int n = r.ReadInt32();
                            for (int i = 0; i < n; i++)
                            {
                                double t = r.ReadDouble();
                                int id = r.ReadUInt16();
                                float v = r.ReadSingle();
                                if (paramById.TryGetValue(id, out var p))
                                {
                                    p.T.Add(t);
                                    p.V.Add(v);
                                }
                            }
                        }
                        else if (tag == SDEF)
                        {
                            int n = r.ReadInt32();
                            for (int i = 0; i < n; i++)
                            {
                                int id = r.ReadUInt16();
                                var kind = (StreamKind)r.ReadByte();
                                string name = Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16()));
                                var info = Json.ParseObject(Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16()))) ??
                                           new Dictionary<string, object>();
                                if (!streamById.TryGetValue(id, out var st))
                                {
                                    st = new PoseStream { Id = id, Kind = kind, Name = name, Info = info };
                                    streamById[id] = st;
                                    rec.Streams.Add(st);
                                }
                                else st.Info = info; // later definitions update info (e.g. role assignment)
                            }
                        }
                        else if (tag == SSMP)
                        {
                            int n = r.ReadInt32();
                            for (int i = 0; i < n; i++)
                            {
                                double t = r.ReadDouble();
                                int id = r.ReadUInt16();
                                var p = new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                                var q = new Quat(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                                if (streamById.TryGetValue(id, out var st))
                                {
                                    st.T.Add(t);
                                    st.P.Add(p);
                                    st.R.Add(q);
                                }
                            }
                        }
                        else if (tag == HUMR)
                        {
                            int n = r.ReadInt32();
                            for (int i = 0; i < n; i++)
                            {
                                double t = r.ReadDouble(), st = r.ReadDouble();
                                var hips = new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                                var rot = new Quat[HumanBones.Count];
                                for (int b = 0; b < HumanBones.Count; b++)
                                    rot[b] = new Quat(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                                rec.Humr.T.Add(t);
                                rec.Humr.SrcT.Add(st);
                                rec.Humr.Hips.Add(hips);
                                rec.Humr.Rot.Add(rot);
                            }
                        }
                        else if (tag == HAND)
                        {
                            int n = r.ReadInt32();
                            for (int i = 0; i < n; i++)
                            {
                                double t = r.ReadDouble();
                                int side = r.ReadByte();
                                float[] sum = new float[9];
                                for (int k = 0; k < 9; k++) sum[k] = r.ReadSingle();
                                int nb = r.ReadUInt16();
                                short[] bones = new short[nb];
                                for (int k = 0; k < nb; k++) bones[k] = r.ReadInt16();
                                if (side < 2 && nb == HandStream.PackedLength) rec.Hands[side].Add(t, sum, bones);
                            }
                        }
                        else if (tag == EVNT)
                        {
                            var list = Json.Parse(Encoding.UTF8.GetString(all, pos, len)) as List<object>;
                            if (list != null)
                                foreach (object o in list)
                                    if (o is Dictionary<string, object> d)
                                        rec.Events.Add(new RecEvent
                                        {
                                            T = d.GetNum("t"), Type = d.GetStr("type", ""), Text = d.GetStr("text", "")
                                        });
                        }
                    }
                    catch (EndOfStreamException) { }
                }

                pos += len;
            }

            if (meta != null) rec.ApplyMeta(meta);
            rec.ReapplyExportFlags();
            // streaming files write samples in arrival order; make sure tracks are sorted
            foreach (var p in rec.Params) SortTrack(p.T, p.V);
            rec.RecomputeDuration();
            return rec;
        }

        static void SortTrack(List<double> t, List<float> v)
        {
            for (int i = 1; i < t.Count; i++)
                if (t[i] < t[i - 1])
                {
                    int[] idx = Enumerable.Range(0, t.Count).OrderBy(k => t[k]).ToArray();
                    var tt = idx.Select(k => t[k]).ToList();
                    var vv = idx.Select(k => v[k]).ToList();
                    t.Clear();
                    t.AddRange(tt);
                    v.Clear();
                    v.AddRange(vv);
                    return;
                }
        }

        // ------------------------------------------------------------ quick summary (library list)

        public sealed class Summary
        {
            public double Duration;
            public bool HasHumr, HasDevices, HasFace, HasFingers;
            public string Name = "", AvatarName = "", World = "", Created = "";
        }
    }

    /// <summary>
    ///     Streams a recording to disk while it is being captured. Producers call the Append methods from any thread;
    ///     a background thread batches samples into chunks every 200 ms. The in-memory Recording is filled at the same time.
    /// </summary>
    public sealed class RecordingWriter : IDisposable
    {
        readonly FileStream _fs;
        readonly object _lock = new();
        readonly Dictionary<ParamTrack, ushort> _paramIds = new();
        readonly Recording _rec;
        readonly Thread _thread;
        readonly BinaryWriter _w;
        MemoryStream _pdef = new(), _psmp = new(), _sdef = new(), _ssmp = new(), _humr = new(), _hand = new();
        int _pdefN, _psmpN, _sdefN, _ssmpN, _humrN, _handN;
        BinaryWriter _pdefW, _psmpW, _sdefW, _ssmpW, _humrW, _handW;
        volatile bool _stop;

        public RecordingWriter(Recording rec, string path)
        {
            _rec = rec;
            Path = path;
            _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            _w = new BinaryWriter(_fs);
            RecordingIO.WriteHeader(_w);
            RecordingIO.WriteJsonChunk(_w, RecordingIO.META, rec.MetaJson());
            if (rec.Profile != null) RecordingIO.WriteJsonChunk(_w, RecordingIO.PROF, rec.Profile.ToJson());
            _w.Flush();
            ResetBuffers();
            _thread = new Thread(Loop)
                { IsBackground = true, Name = "VRMR writer", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public long BytesWritten => _fs.CanSeek ? _fs.Position : 0;
        public string Path { get; }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(2000);
            FlushPending();
            try
            {
                RecordingIO.WriteJsonChunk(_w, RecordingIO.META, _rec.MetaJson());
                if (_rec.Profile != null) RecordingIO.WriteJsonChunk(_w, RecordingIO.PROF, _rec.Profile.ToJson());
                RecordingIO.WriteJsonChunk(_w, RecordingIO.EVNT,
                    _rec.Events.Select(e => (object)new Dictionary<string, object>
                        { ["t"] = e.T, ["type"] = e.Type, ["text"] = e.Text }).ToList());
                _w.Write(RecordingIO.END);
                _w.Write(0);
                _w.Flush();
            }
            catch { }

            _w.Dispose();
            _fs.Dispose();
        }

        void ResetBuffers()
        {
            _pdef.SetLength(0);
            _psmp.SetLength(0);
            _sdef.SetLength(0);
            _ssmp.SetLength(0);
            _humr.SetLength(0);
            _hand.SetLength(0);
            _pdefW = new BinaryWriter(_pdef);
            _psmpW = new BinaryWriter(_psmp);
            _sdefW = new BinaryWriter(_sdef);
            _ssmpW = new BinaryWriter(_ssmp);
            _humrW = new BinaryWriter(_humr);
            _handW = new BinaryWriter(_hand);
            _pdefN = _psmpN = _sdefN = _ssmpN = _humrN = _handN = 0;
        }

        public void AppendParam(ParamTrack p, double t, float v)
        {
            lock (_lock)
            {
                if (!_paramIds.TryGetValue(p, out ushort id))
                {
                    id = (ushort)_paramIds.Count;
                    _paramIds[p] = id;
                    RecordingIO.WriteParamDef(_pdefW, id, p);
                    _pdefN++;
                }

                _psmpW.Write(t);
                _psmpW.Write(id);
                _psmpW.Write(v);
                _psmpN++;
            }
        }

        public void DefineStream(PoseStream st)
        {
            lock (_lock)
            {
                RecordingIO.WriteStreamDef(_sdefW, st);
                _sdefN++;
            }
        }

        public void AppendPose(PoseStream st, double t, Vec3 p, Quat r)
        {
            lock (_lock)
            {
                RecordingIO.WritePose(_ssmpW, t, (ushort)st.Id, p, r);
                _ssmpN++;
            }
        }

        public void AppendHumr(double t, double srcT, Vec3 hips, Quat[] rot)
        {
            lock (_lock)
            {
                RecordingIO.WriteHumr(_humrW, t, srcT, hips, rot);
                _humrN++;
            }
        }

        public void AppendHand(double t, int side, float[] summary, short[] bones)
        {
            lock (_lock)
            {
                RecordingIO.WriteHand(_handW, t, side, summary, bones);
                _handN++;
            }
        }

        void Loop()
        {
            while (!_stop)
            {
                Thread.Sleep(200);
                FlushPending();
            }
        }

        void FlushPending()
        {
            MemoryStream pdef, psmp, sdef, ssmp, humr, hand;
            int pdefN, psmpN, sdefN, ssmpN, humrN, handN;
            lock (_lock)
            {
                if (_pdefN + _psmpN + _sdefN + _ssmpN + _humrN + _handN == 0) return;
                pdef = _pdef;
                psmp = _psmp;
                sdef = _sdef;
                ssmp = _ssmp;
                humr = _humr;
                hand = _hand;
                pdefN = _pdefN;
                psmpN = _psmpN;
                sdefN = _sdefN;
                ssmpN = _ssmpN;
                humrN = _humrN;
                handN = _handN;
                _pdef = new MemoryStream();
                _psmp = new MemoryStream();
                _sdef = new MemoryStream();
                _ssmp = new MemoryStream();
                _humr = new MemoryStream();
                _hand = new MemoryStream();
                ResetBuffers();
            }

            try
            {
                // definitions first so samples can always be resolved
                WriteCounted(RecordingIO.PDEF, pdef, pdefN);
                WriteCounted(RecordingIO.SDEF, sdef, sdefN);
                WriteCounted(RecordingIO.PSMP, psmp, psmpN);
                WriteCounted(RecordingIO.SSMP, ssmp, ssmpN);
                WriteCounted(RecordingIO.HUMR, humr, humrN);
                WriteCounted(RecordingIO.HAND, hand, handN);
                _w.Flush();
            }
            catch (Exception ex) { AppLog.Error("Writer: " + ex.Message); }
        }

        void WriteCounted(uint tag, MemoryStream body, int n)
        {
            if (n == 0) return;
            _w.Write(tag);
            _w.Write((int)body.Length + 4);
            _w.Write(n);
            _w.Write(body.GetBuffer(), 0, (int)body.Length);
        }
    }
}
