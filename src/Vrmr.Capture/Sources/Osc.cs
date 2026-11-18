using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace VRMR
{
    /// <summary>One decoded OSC message. Instances are reused by the receive thread: copy what you need.</summary>
    public sealed class OscMsg
    {
        public string Address;
        public int Argc;
        public readonly char[] Tag = new char[32];
        public readonly float[] F = new float[32];
        public readonly int[] I = new int[32];
        public string S;
        public double Time;

        public float Float(int i) => Tag[i] == 'f' ? F[i] : Tag[i] == 'i' ? I[i] : Tag[i] == 'T' ? 1f : 0f;
    }

    /// <summary>Low-allocation UDP OSC receiver (messages + bundles) running on its own thread.</summary>
    public sealed class OscReceiver : IDisposable
    {
        readonly UdpClient _udp;
        readonly Thread _thread;
        volatile bool _stop;
        readonly OscMsg _msg = new OscMsg();
        readonly Dictionary<ulong, string> _addrCache = new Dictionary<ulong, string>();
        public int Port { get; }
        public long Packets, Messages, Errors;
        public double LastPacketTime = -1;
        public Action<OscMsg> OnMessage;
        public string Name;

        public OscReceiver(string name, int port, bool loopbackOnly = true)
        {
            Name = name;
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.ExclusiveAddressUse = true;
            _udp.Client.ReceiveBufferSize = 1 << 20;
            try { const int SIO_UDP_CONNRESET = -1744830452; _udp.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); } catch { }
            _udp.Client.Bind(new IPEndPoint(loopbackOnly ? IPAddress.Loopback : IPAddress.Any, port));
            Port = ((IPEndPoint)_udp.Client.LocalEndPoint).Port;
            _thread = new Thread(Loop) { IsBackground = true, Name = "OSC rx " + name, Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        public static bool IsPortFree(int port)
        {
            try
            {
                using (var u = new UdpClient(AddressFamily.InterNetwork))
                {
                    u.Client.ExclusiveAddressUse = true;
                    u.Client.Bind(new IPEndPoint(IPAddress.Loopback, port));
                }
                using (var u = new UdpClient(AddressFamily.InterNetwork))
                {
                    u.Client.ExclusiveAddressUse = true;
                    u.Client.Bind(new IPEndPoint(IPAddress.Any, port));
                }
                return true;
            }
            catch { return false; }
        }

        void Loop()
        {
            EfficiencyMode.ExemptCurrentThread();
            var buf = new byte[65536];
            EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
            while (!_stop)
            {
                int n;
                try { n = _udp.Client.ReceiveFrom(buf, ref ep); }
                catch (SocketException) { if (_stop) break; continue; }
                catch (ObjectDisposedException) { break; }
                double t = Clock.Now;
                LastPacketTime = t;
                Packets++;
                try { Parse(buf, 0, n, t); }
                catch { Errors++; }
            }
        }

        void Parse(byte[] b, int off, int len, double t)
        {
            if (len >= 16 && b[off] == '#' && b[off + 1] == 'b' && b[off + 2] == 'u')
            {
                int i = off + 16; // "#bundle\0" + timetag
                int end = off + len;
                while (i + 4 <= end)
                {
                    int sz = (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
                    i += 4;
                    if (sz <= 0 || i + sz > end) break;
                    Parse(b, i, sz, t);
                    i += sz;
                }
                return;
            }
            ParseMessage(b, off, len, t);
        }

        void ParseMessage(byte[] b, int off, int len, double t)
        {
            int end = off + len;
            int a0 = off, a1 = off;
            while (a1 < end && b[a1] != 0) a1++;
            if (a1 >= end || a1 == a0) return;
            string addr = Intern(b, a0, a1 - a0);
            int i = Align(a1 + 1, off);
            if (i >= end || b[i] != ',') { Deliver(addr, 0, t); return; }
            int ts = i + 1, te = ts;
            while (te < end && b[te] != 0) te++;
            int argc = Math.Min(te - ts, 32);
            i = Align(te + 1, off);
            _msg.S = null;
            for (int k = 0; k < argc; k++)
            {
                char c = (char)b[ts + k];
                _msg.Tag[k] = c;
                switch (c)
                {
                    case 'f':
                        if (i + 4 > end) return;
                        _msg.F[k] = BitsToFloat((b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3]); i += 4; break;
                    case 'i':
                        if (i + 4 > end) return;
                        _msg.I[k] = (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3]; i += 4; break;
                    case 'd':
                        if (i + 8 > end) return;
                        long l = ((long)b[i] << 56) | ((long)b[i + 1] << 48) | ((long)b[i + 2] << 40) | ((long)b[i + 3] << 32) | ((long)b[i + 4] << 24) | ((long)b[i + 5] << 16) | ((long)b[i + 6] << 8) | b[i + 7];
                        _msg.F[k] = (float)BitConverter.Int64BitsToDouble(l); _msg.Tag[k] = 'f'; i += 8; break;
                    case 's':
                        {
                            int s0 = i; while (i < end && b[i] != 0) i++;
                            _msg.S = Encoding.UTF8.GetString(b, s0, i - s0);
                            i = Align(i + 1, off);
                            break;
                        }
                    case 'T': case 'F': case 'N': case 'I': break;
                    default: return; // unsupported (blob etc.)
                }
            }
            Deliver(addr, argc, t);
        }

        void Deliver(string addr, int argc, double t)
        {
            Messages++;
            _msg.Address = addr; _msg.Argc = argc; _msg.Time = t;
            try { OnMessage?.Invoke(_msg); } catch (Exception ex) { Errors++; if (Errors < 5) AppLog.Warn("OSC handler: " + ex.Message); }
        }

        static int Align(int i, int off) => off + (((i - off) + 3) & ~3);

        static unsafe float BitsToFloat(int bits) => *(float*)&bits;

        string Intern(byte[] b, int s, int n)
        {
            ulong h = 14695981039346656037UL;
            for (int k = 0; k < n; k++) { h ^= b[s + k]; h *= 1099511628211UL; }
            h ^= (ulong)n << 56;
            if (_addrCache.TryGetValue(h, out var str) && str.Length == n) return str;
            str = Encoding.UTF8.GetString(b, s, n);
            if (_addrCache.Count < 100000) _addrCache[h] = str;
            return str;
        }

        public void Dispose()
        {
            _stop = true;
            try { _udp.Close(); } catch { }
            _thread.Join(500);
        }
    }

    /// <summary>Minimal OSC sender (used to replay face tracking / parameters back into VRChat).</summary>
    public sealed class OscSender : IDisposable
    {
        readonly UdpClient _udp;
        readonly IPEndPoint _ep;
        readonly byte[] _buf = new byte[1024];

        public OscSender(string host, int port)
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            try { const int SIO_UDP_CONNRESET = -1744830452; _udp.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); } catch { }
            _ep = new IPEndPoint(IPAddress.Parse(host), port);
        }

        int WriteStr(int i, string s)
        {
            int n = Encoding.UTF8.GetBytes(s, 0, s.Length, _buf, i);
            i += n;
            _buf[i++] = 0;
            while ((i & 3) != 0) _buf[i++] = 0;
            return i;
        }

        void WriteInt(ref int i, int v) { _buf[i++] = (byte)(v >> 24); _buf[i++] = (byte)(v >> 16); _buf[i++] = (byte)(v >> 8); _buf[i++] = (byte)v; }
        static unsafe int FloatBits(float f) => *(int*)&f;

        public void SendFloat(string addr, float v) { int i = WriteStr(0, addr); i = WriteStr(i, ",f"); WriteInt(ref i, FloatBits(v)); Send(i); }
        public void SendInt(string addr, int v) { int i = WriteStr(0, addr); i = WriteStr(i, ",i"); WriteInt(ref i, v); Send(i); }
        public void SendBool(string addr, bool v) { int i = WriteStr(0, addr); i = WriteStr(i, v ? ",T" : ",F"); Send(i); }

        public void SendParam(string name, ParamType type, float v)
        {
            string addr = "/avatar/parameters/" + name;
            switch (type)
            {
                case ParamType.Bool: SendBool(addr, v > 0.5f); break;
                case ParamType.Int: SendInt(addr, (int)Math.Round(v)); break;
                default: SendFloat(addr, v); break;
            }
        }

        void Send(int len) { try { _udp.Send(_buf, len, _ep); } catch { } }
        public void Dispose() { try { _udp.Close(); } catch { } }
    }
}
