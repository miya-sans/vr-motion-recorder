using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace VRMR
{
    /// <summary>
    ///     OSCQuery service: a tiny HTTP server describing our OSC endpoint plus an mDNS responder that advertises it
    ///     (_oscjson._tcp and _osc._udp), exactly like VRCFaceTracking does. VRChat discovers it and starts sending
    ///     its OSC output to our UDP port, so we coexist with any other OSC app (no fight over port 9001).
    ///     The same mDNS socket also discovers VRChat's own OSCQuery server (for parameter snapshots / input port).
    /// </summary>
    public sealed class OscQueryService : IDisposable
    {
        static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
        static readonly IPEndPoint GroupEp = new(Group, 5353);
        public readonly string InstanceName;
        public readonly int OscPort;
        readonly Dictionary<string, IPAddress> _hostAddr = new(StringComparer.OrdinalIgnoreCase);
        readonly TcpListener _http;
        readonly Thread _httpThread, _mdnsThread, _announceThread;
        readonly List<IPAddress> _ifaces = new();
        readonly Socket _mdns;
        readonly Dictionary<string, Peer> _peers = new(StringComparer.OrdinalIgnoreCase);
        public long HttpHits, MdnsQueriesAnswered;
        public double LastVrcHttpHit = -1;
        volatile bool _stop;

        public OscQueryService(int oscPort)
        {
            OscPort = oscPort;
            InstanceName = "VRMotionRecorder-" + Guid.NewGuid().ToString("N").Substring(0, 6).ToUpperInvariant();
            _http = new TcpListener(IPAddress.Loopback, 0);
            _http.Start();
            HttpPort = ((IPEndPoint)_http.LocalEndpoint).Port;
            _httpThread = new Thread(HttpLoop) { IsBackground = true, Name = "OSCQuery HTTP" };
            _httpThread.Start();

            _mdns = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _mdns.ExclusiveAddressUse = false;
            _mdns.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _mdns.Bind(new IPEndPoint(IPAddress.Any, 5353));
            _mdns.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
            _mdns.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            foreach (var ip in LocalIPv4())
            {
                try
                {
                    _mdns.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(Group, ip));
                    _ifaces.Add(ip);
                }
                catch { }
            }

            _mdnsThread = new Thread(MdnsLoop) { IsBackground = true, Name = "mDNS" };
            _mdnsThread.Start();
            _announceThread = new Thread(AnnounceLoop) { IsBackground = true, Name = "mDNS announce" };
            _announceThread.Start();
            AppLog.Info($"OSCQuery: advertising {InstanceName} (OSC udp {OscPort}, http {HttpPort})");
        }

        public int HttpPort { get; }

        string HostName => InstanceName + ".local";
        string JsonSvc => InstanceName + "._oscjson._tcp.local";
        string UdpSvc => InstanceName + "._osc._udp.local";

        /// <summary>VRChat's OSCQuery HTTP endpoint if discovered.</summary>
        public Peer VrcHttp
        {
            get
            {
                lock (_peers)
                    return _peers.Values.Where(p =>
                            p.Name.StartsWith("VRChat-Client", StringComparison.OrdinalIgnoreCase) &&
                            p.Name.IndexOf("_oscjson", StringComparison.OrdinalIgnoreCase) >= 0)
                        .OrderByDescending(p => p.Seen).FirstOrDefault();
            }
        }

        /// <summary>VRChat's OSC input (UDP) endpoint if discovered.</summary>
        public Peer VrcOsc
        {
            get
            {
                lock (_peers)
                    return _peers.Values.Where(p =>
                            p.Name.StartsWith("VRChat-Client", StringComparison.OrdinalIgnoreCase) &&
                            p.Name.IndexOf("_osc._udp", StringComparison.OrdinalIgnoreCase) >= 0)
                        .OrderByDescending(p => p.Seen).FirstOrDefault();
            }
        }

        public void Dispose()
        {
            _stop = true;
            try { SendAnnouncement(0); }
            catch { } // goodbye

            try { _http.Stop(); }
            catch { }

            try { _mdns.Close(); }
            catch { }

            _mdnsThread.Join(300);
            _httpThread.Join(300);
            _announceThread.Join(300);
        }

        static IEnumerable<IPAddress> LocalIPv4()
        {
            var list = new List<IPAddress> { IPAddress.Loopback };
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || !ni.SupportsMulticast) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                            list.Add(ua.Address);
                }
            }
            catch { }

            return list.Distinct();
        }

        // ------------------------------------------------------------------ HTTP

        string HostInfoJson()
        {
            return Json.Serialize(new Dictionary<string, object>
            {
                ["NAME"] = InstanceName,
                ["EXTENSIONS"] = new Dictionary<string, object>
                    { ["ACCESS"] = true, ["CLIPMODE"] = false, ["RANGE"] = true, ["TYPE"] = true, ["VALUE"] = true },
                ["OSC_IP"] = "127.0.0.1", ["OSC_PORT"] = OscPort, ["OSC_TRANSPORT"] = "UDP"
            });
        }

        static Dictionary<string, object> Node(string path, int access, string type = null,
            Dictionary<string, object> contents = null)
        {
            var d = new Dictionary<string, object> { ["FULL_PATH"] = path, ["ACCESS"] = access };
            if (type != null) d["TYPE"] = type;
            if (contents != null) d["CONTENTS"] = contents;
            return d;
        }

        string TreeJson()
        {
            var root = Node("/", 0, null, new Dictionary<string, object>
            {
                ["avatar"] = Node("/avatar", 0, null, new Dictionary<string, object>
                {
                    ["change"] = Node("/avatar/change", 2, "s"),
                    ["parameters"] = Node("/avatar/parameters", 2)
                }),
                ["tracking"] = Node("/tracking", 0, null, new Dictionary<string, object>
                {
                    ["vrsystem"] = Node("/tracking/vrsystem", 0, null, new Dictionary<string, object>
                    {
                        ["head"] = Node("/tracking/vrsystem/head", 0, null,
                            new Dictionary<string, object>
                                { ["pose"] = Node("/tracking/vrsystem/head/pose", 2, "ffffff") }),
                        ["leftwrist"] = Node("/tracking/vrsystem/leftwrist", 0, null,
                            new Dictionary<string, object>
                                { ["pose"] = Node("/tracking/vrsystem/leftwrist/pose", 2, "ffffff") }),
                        ["rightwrist"] = Node("/tracking/vrsystem/rightwrist", 0, null,
                            new Dictionary<string, object>
                                { ["pose"] = Node("/tracking/vrsystem/rightwrist/pose", 2, "ffffff") })
                    })
                }),
                ["usercamera"] = Node("/usercamera", 0, null,
                    new Dictionary<string, object> { ["Pose"] = Node("/usercamera/Pose", 2, "ffffff") })
            });
            root["DESCRIPTION"] = "VR Motion Recorder";
            return Json.Serialize(root);
        }

        void HttpLoop()
        {
            while (!_stop)
            {
                TcpClient c;
                try { c = _http.AcceptTcpClient(); }
                catch
                {
                    if (_stop) break;
                    continue;
                }

                ThreadPool.QueueUserWorkItem(_ => Serve(c));
            }
        }

        void Serve(TcpClient c)
        {
            try
            {
                using (c)
                {
                    c.ReceiveTimeout = 2000;
                    c.SendTimeout = 2000;
                    var ns = c.GetStream();
                    byte[] buf = new byte[8192];
                    int n = 0;
                    while (n < buf.Length)
                    {
                        int r = ns.Read(buf, n, buf.Length - n);
                        if (r <= 0) break;
                        n += r;
                        if (Encoding.ASCII.GetString(buf, 0, n).Contains("\r\n\r\n")) break;
                    }

                    string req = Encoding.ASCII.GetString(buf, 0, n);
                    string first = req.Split('\n')[0];
                    string target = first.Split(' ').Length > 1 ? first.Split(' ')[1] : "/";
                    HttpHits++;
                    if (req.IndexOf("VRChat", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        req.IndexOf("UnityWebRequest", StringComparison.OrdinalIgnoreCase) >= 0)
                        LastVrcHttpHit = Clock.Now;
                    string body = target.Contains("HOST_INFO") ? HostInfoJson() : TreeJson();
                    byte[] bb = Encoding.UTF8.GetBytes(body);
                    byte[] head = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bb.Length}\r\nConnection: close\r\npragma: no-cache\r\n\r\n");
                    ns.Write(head, 0, head.Length);
                    ns.Write(bb, 0, bb.Length);
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------ mDNS

        void AnnounceLoop()
        {
            int i = 0;
            while (!_stop)
            {
                if (i < 3 || i % 30 == 0) SendAnnouncement(120);
                if (i < 3 || (VrcHttp == null && i % 5 == 0) || i % 60 == 0) SendBrowse();
                i++;
                for (int k = 0; k < 10 && !_stop; k++) Thread.Sleep(100);
            }
        }

        void SendBrowse()
        {
            var w = new DnsWriter();
            w.Header(0, 0x0000, 2, 0, 0, 0);
            w.Name("_oscjson._tcp.local");
            w.U16(12);
            w.U16(1);
            w.Name("_osc._udp.local");
            w.U16(12);
            w.U16(1);
            SendMulticast(w.ToArray());
        }

        void SendAnnouncement(uint ttl)
        {
            SendMulticast(ServicePacket(0, "_oscjson._tcp.local", JsonSvc, HttpPort, ttl));
            SendMulticast(ServicePacket(0, "_osc._udp.local", UdpSvc, OscPort, ttl));
        }

        /// <summary>
        ///     One service per packet: PTR in the answer section, SRV/TXT/A in the additional section.
        ///     vrc-oscquery-lib (VRChat, VRCFT) reads SRV and A records from the additional records only.
        /// </summary>
        byte[] ServicePacket(ushort id, string type, string inst, int port, uint ttl)
        {
            var w = new DnsWriter();
            w.Header(id, 0x8400, 0, 1, 0, 3);
            w.Name(type);
            w.U16(12);
            w.U16(1);
            w.U32(ttl);
            w.RData(() => w.Name(inst));
            w.Name(inst);
            w.U16(33);
            w.U16(0x8001);
            w.U32(ttl);
            w.RData(() =>
            {
                w.U16(0);
                w.U16(0);
                w.U16((ushort)port);
                w.Name(HostName);
            });
            w.Name(inst);
            w.U16(16);
            w.U16(0x8001);
            w.U32(ttl);
            w.RData(() =>
            {
                w.Byte(9);
                w.Raw(Encoding.ASCII.GetBytes("txtvers=1"));
            });
            w.Name(HostName);
            w.U16(1);
            w.U16(0x8001);
            w.U32(ttl);
            w.RData(() => w.Raw(new byte[] { 127, 0, 0, 1 }));
            return w.ToArray();
        }

        void SendMulticast(byte[] pkt)
        {
            foreach (var ip in _ifaces)
            {
                try
                {
                    _mdns.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                        ip.GetAddressBytes());
                    _mdns.SendTo(pkt, GroupEp);
                }
                catch { }
            }
        }

        void MdnsLoop()
        {
            byte[] buf = new byte[9000];
            EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
            while (!_stop)
            {
                int n;
                try { n = _mdns.ReceiveFrom(buf, ref ep); }
                catch (SocketException)
                {
                    if (_stop) break;
                    continue;
                }
                catch (ObjectDisposedException) { break; }

                try { Handle(buf, n, (IPEndPoint)ep); }
                catch { }
            }
        }

        void Handle(byte[] b, int n, IPEndPoint from)
        {
            var r = new DnsReader(b, n);
            ushort id = r.U16(), flags = r.U16(), qd = r.U16(), an = r.U16(), ns = r.U16(), ar = r.U16();
            bool isResponse = (flags & 0x8000) != 0;
            if (!isResponse)
            {
                bool wantJson = false, wantUdp = false, unicast = from.Port != 5353;
                for (int i = 0; i < qd; i++)
                {
                    string name = r.Name();
                    ushort type = r.U16(), cls = r.U16();
                    if ((cls & 0x8000) != 0) unicast = true;
                    bool any = type == 255;
                    if (Eq(name, "_oscjson._tcp.local") && (type == 12 || any)) wantJson = true;
                    else if (Eq(name, "_osc._udp.local") && (type == 12 || any)) wantUdp = true;
                    else if (Eq(name, JsonSvc)) wantJson = true;
                    else if (Eq(name, UdpSvc)) wantUdp = true;
                    else if (Eq(name, HostName)) { wantJson = true; }
                    else if (Eq(name, "_services._dns-sd._udp.local") && (type == 12 || any))
                    {
                        wantJson = wantUdp = true;
                    }
                }

                if (!wantJson && !wantUdp) return;
                MdnsQueriesAnswered++;
                foreach (byte[] pkt in new[]
                         {
                             wantJson
                                 ? ServicePacket(unicast ? id : (ushort)0, "_oscjson._tcp.local", JsonSvc, HttpPort,
                                     120)
                                 : null,
                             wantUdp
                                 ? ServicePacket(unicast ? id : (ushort)0, "_osc._udp.local", UdpSvc, OscPort, 120)
                                 : null
                         })
                {
                    if (pkt == null) continue;
                    if (unicast)
                    {
                        try { _mdns.SendTo(pkt, from); }
                        catch { }
                    }

                    SendMulticast(pkt);
                }

                return;
            }

            // response: learn other services (VRChat's own OSCQuery)
            for (int i = 0; i < qd; i++)
            {
                r.Name();
                r.U16();
                r.U16();
            }

            var srvs = new List<Tuple<string, string, int>>();
            int total = an + ns + ar;
            for (int i = 0; i < total && r.Pos < n; i++)
            {
                string name = r.Name();
                ushort type = r.U16();
                r.U16();
                r.U32();
                ushort rl = r.U16();
                int start = r.Pos;
                if (type == 33 && rl >= 6)
                {
                    r.U16();
                    r.U16();
                    int port = r.U16();
                    string target = r.Name();
                    srvs.Add(Tuple.Create(name, target, port));
                }
                else if (type == 1 && rl == 4)
                {
                    var ip = new IPAddress(new[] { b[start], b[start + 1], b[start + 2], b[start + 3] });
                    lock (_peers) _hostAddr[name] = ip;
                }

                r.Pos = start + rl;
            }

            foreach (var s in srvs)
            {
                if (s.Item1.StartsWith(InstanceName, StringComparison.OrdinalIgnoreCase)) continue;
                lock (_peers)
                {
                    _hostAddr.TryGetValue(s.Item2, out var ip);
                    _peers[s.Item1] = new Peer
                    {
                        Name = s.Item1, Host = s.Item2, Port = s.Item3, Address = ip ?? from.Address, Seen = Clock.Now
                    };
                }
            }
        }

        static bool Eq(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        public sealed class Peer
        {
            public IPAddress Address;
            public string Host;
            public string Name;
            public int Port;
            public double Seen;
        }

        // ------------------------------------------------------------------ DNS helpers

        sealed class DnsWriter
        {
            readonly MemoryStream _ms = new();

            public void Header(ushort id, ushort flags, ushort qd, ushort an, ushort ns, ushort ar)
            {
                U16(id);
                U16(flags);
                U16(qd);
                U16(an);
                U16(ns);
                U16(ar);
            }

            public void Byte(byte v)
            {
                _ms.WriteByte(v);
            }

            public void U16(ushort v)
            {
                _ms.WriteByte((byte)(v >> 8));
                _ms.WriteByte((byte)v);
            }

            public void U32(uint v)
            {
                U16((ushort)(v >> 16));
                U16((ushort)v);
            }

            public void Raw(byte[] b)
            {
                _ms.Write(b, 0, b.Length);
            }

            public void Name(string name)
            {
                foreach (string label in name.Split('.'))
                {
                    if (label.Length == 0) continue;
                    byte[] lb = Encoding.UTF8.GetBytes(label);
                    _ms.WriteByte((byte)lb.Length);
                    _ms.Write(lb, 0, lb.Length);
                }

                _ms.WriteByte(0);
            }

            public void RData(Action body)
            {
                long lenPos = _ms.Position;
                U16(0);
                long start = _ms.Position;
                body();
                long end = _ms.Position;
                _ms.Position = lenPos;
                U16((ushort)(end - start));
                _ms.Position = end;
            }

            public byte[] ToArray()
            {
                return _ms.ToArray();
            }
        }

        sealed class DnsReader
        {
            readonly byte[] _b;
            readonly int _n;
            public int Pos;

            public DnsReader(byte[] b, int n)
            {
                _b = b;
                _n = n;
            }

            public ushort U16()
            {
                ushort v = (ushort)((_b[Pos] << 8) | _b[Pos + 1]);
                Pos += 2;
                return v;
            }

            public uint U32()
            {
                uint v = ((uint)U16() << 16) | U16();
                return v;
            }

            public string Name()
            {
                var labels = new List<string>();
                int p = Pos;
                bool jumped = false;
                int guard = 0;
                while (p < _n && guard++ < 64)
                {
                    int l = _b[p];
                    if (l == 0)
                    {
                        p++;
                        break;
                    }

                    if ((l & 0xC0) == 0xC0)
                    {
                        int ptr = ((l & 0x3F) << 8) | _b[p + 1];
                        if (!jumped) Pos = p + 2;
                        jumped = true;
                        p = ptr;
                        continue;
                    }

                    labels.Add(Encoding.UTF8.GetString(_b, p + 1, l));
                    p += 1 + l;
                }

                if (!jumped) Pos = p;
                return string.Join(".", labels);
            }
        }
    }

    /// <summary>Reads VRChat's OSCQuery tree over HTTP (current avatar + all parameter values).</summary>
    public static class VrcOscQueryClient
    {
        public static Snapshot Fetch(IPAddress ip, int port, int timeoutMs = 1500)
        {
            var req = (HttpWebRequest)WebRequest.Create($"http://{ip}:{port}/avatar");
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.Proxy = null;
            string body;
            using (var resp = req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                body = sr.ReadToEnd();
            var root = Json.ParseObject(body);
            var snap = new Snapshot();
            var contents = root.GetObj("CONTENTS");
            if (contents == null) return snap;
            snap.AvatarId = FirstValue(contents.GetObj("change")) as string;
            snap.AvatarName = FirstValue(contents.GetObj("name")) as string;
            if (FirstValue(contents.GetObj("eyeheight")) is double eh) snap.EyeHeight = (float)eh;
            var pars = contents.GetObj("parameters");
            if (pars != null) Walk(pars, snap);
            return snap;
        }

        static object FirstValue(Dictionary<string, object> node)
        {
            var v = node?.GetList("VALUE");
            return v != null && v.Count > 0 ? v[0] : null;
        }

        static void Walk(Dictionary<string, object> node, Snapshot snap)
        {
            var c = node.GetObj("CONTENTS");
            if (c != null)
            {
                foreach (var kv in c)
                    if (kv.Value is Dictionary<string, object> d)
                        Walk(d, snap);
                return;
            }

            string path = node.GetStr("FULL_PATH", "");
            string type = node.GetStr("TYPE", "");
            object v = FirstValue(node);
            if (!path.StartsWith("/avatar/parameters/", StringComparison.Ordinal) || v == null) return;
            string name = path.Substring(19);
            if (type == "f" && v is double f) snap.Params[name] = Tuple.Create(ParamType.Float, (float)f);
            else if (type == "i" && v is double i) snap.Params[name] = Tuple.Create(ParamType.Int, (float)i);
            else if (type == "T" || type == "F")
                snap.Params[name] = Tuple.Create(ParamType.Bool, v is bool b && b ? 1f : 0f);
        }

        public sealed class Snapshot
        {
            public string AvatarId, AvatarName;
            public float EyeHeight;
            public Dictionary<string, Tuple<ParamType, float>> Params = new();
        }
    }
}
