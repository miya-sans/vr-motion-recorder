using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace VRMR
{
    /// <summary>
    /// Follows VRChat's output_log in real time. Extracts HUMR bone frames (VRChat's own IK, in worlds with a
    /// HUMR recorder and Logging=Full), plus session context: local user name, world, avatar switches.
    /// The log is opened once and shared for reading, writing and deleting, so VRChat is never blocked; new bytes are
    /// read where the last read stopped and split into lines in place, and only the few lines that matter become strings.
    /// </summary>
    public sealed class VrcLogTailer : IDisposable
    {
        public sealed class HumrFrame
        {
            public string Target;
            public long Take;
            public double HumrTime;   // seconds since the in-world take started
            public double Arrival;    // Clock.Now when the line was read
            public Vec3 Hips;
            public Quat[] Rot;
        }

        /// <summary>VRChat's log folder on the machine in use.</summary>
        public static string LogDir => VrcPaths.LogDir;

        public string CurrentFile { get; private set; }
        public string LocalUser = "", WorldName = "", WorldId = "", LocalAvatarName = "";
        public double LastHumrArrival = -1;
        public long HumrFrames;
        public string LastHumrTarget = "";
        public Action<HumrFrame> OnHumr;
        public Action<string, string> OnEvent; // (type, text)

        /// <summary>Head and tail of a log that are read for context (user, world, avatar) when it is opened.</summary>
        const long ContextBytes = 4L << 20;
        /// <summary>A line longer than this is not a line the tailer needs; it is skipped.</summary>
        const int MaxLineBytes = 8 << 20;
        /// <summary>While the log was written this recently, it is checked 25 times a second: a calibration confirm can follow minutes of silence and must be seen promptly.</summary>
        const double ActiveSeconds = 180;

        readonly string _dir;
        readonly Thread _thread;
        readonly AutoResetEvent _wake = new AutoResetEvent(false);
        volatile bool _stop, _newFileHint;
        FileSystemWatcher _watcher;
        FileStream _file;
        long _offset;
        byte[] _buf = new byte[64 * 1024];
        int _carry;          // bytes of an unfinished line at the start of _buf
        bool _skipLine;      // inside a line that outgrew the buffer
        double _lastData, _lastScan = double.NegativeInfinity, _lastIdentityCheck;
        byte[] _targetBytes = Array.Empty<byte>(); string _targetText = "";

        /// <summary>Follows VRChat's log folder on a thread of its own.</summary>
        public VrcLogTailer() : this(VrcPaths.LogDir, true) { }

        /// <summary>Follows the logs in a folder. Without a thread (<paramref name="startThread"/> false) the caller drives it with <see cref="Poll"/>.</summary>
        public VrcLogTailer(string logDir, bool startThread)
        {
            _dir = logDir;
            if (!startThread) return;
            _thread = new Thread(Loop) { IsBackground = true, Name = "VRChat log" };
            _thread.Start();
        }

        public bool HumrActive => LastHumrArrival > 0 && Clock.Now - LastHumrArrival < 1.5;

        // ------------------------------------------------------------------ following

        void Loop()
        {
            EfficiencyMode.ExemptCurrentThread();
            StartWatcher();
            while (!_stop)
            {
                bool read = false;
                try { read = Poll(); }
                catch (IOException) { CloseFile(); }
                catch (Exception ex) { AppLog.Warn("Log tail: " + ex.Message); CloseFile(); _wake.WaitOne(500); }
                _wake.WaitOne(DelayMs(read));
            }
        }

        /// <summary>How long to wait before looking again: short while data flows, longer the longer the log has been silent.</summary>
        int DelayMs(bool read)
        {
            if (HumrActive) return 8;
            if (_file == null) return 2000; // no log yet: the folder watcher wakes the loop when one appears
            double silent = Clock.Now - _lastData;
            return read || silent < ActiveSeconds ? 40 : silent < 600 ? 100 : 500;
        }

        /// <summary>Wakes the loop when VRChat starts a new log, so a quiet tailer does not have to list the folder often.</summary>
        void StartWatcher()
        {
            try
            {
                if (!Directory.Exists(_dir)) return;
                _watcher = new FileSystemWatcher(_dir, "output_log_*.txt") { NotifyFilter = NotifyFilters.FileName };
                _watcher.Created += (s, e) => { _newFileHint = true; _wake.Set(); };
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { AppLog.Warn("Log folder watch: " + ex.Message); _watcher = null; }
        }

        string Newest()
        {
            try
            {
                var d = new DirectoryInfo(_dir);
                if (!d.Exists) return null;
                return d.EnumerateFiles("output_log_*.txt").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
            }
            catch { return null; }
        }

        /// <summary>
        /// Reads whatever VRChat has written since the last call and raises the events for it. Returns true when
        /// something was read. <paramref name="lookForNewLog"/> forces the check for a newer log file, which otherwise
        /// happens when there is no log yet, when the folder watcher saw one, and now and then while the log is quiet.
        /// </summary>
        public bool Poll(bool lookForNewLog = false)
        {
            double now = Clock.Now;
            bool quiet = now - _lastData > 2.0;
            if (lookForNewLog || _newFileHint || ((_file == null || quiet) && now - _lastScan > (_watcher != null && _file != null ? 10.0 : 2.0)))
            {
                _newFileHint = false; _lastScan = now;
                string newest = Newest();
                if (newest != null && newest != CurrentFile) Open(newest, CurrentFile == null);
                else if (_file != null && (lookForNewLog || now - _lastIdentityCheck > 5.0)) { _lastIdentityCheck = now; ReopenIfReplaced(); }
            }
            return _file != null && ReadNew();
        }

        void Open(string file, bool first)
        {
            CloseFile();
            try { _file = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            CurrentFile = file;
            _carry = 0; _skipLine = false;
            long len = _file.Length;
            ScanContext(len);
            _offset = first ? len : 0; // at start skip the history; a log that appears later is read from its beginning
            _lastData = Clock.Now;
            AppLog.Info("Following VRChat log " + Path.GetFileName(file));
        }

        void CloseFile()
        {
            try { _file?.Dispose(); } catch { }
            _file = null;
        }

        /// <summary>
        /// The open handle keeps reading a log that was deleted and written anew under the same name. A second handle
        /// by name shows it: two handles to one file always agree on its length.
        /// </summary>
        void ReopenIfReplaced()
        {
            try
            {
                if (!File.Exists(CurrentFile)) { CloseFile(); CurrentFile = null; return; }
                using (var again = new FileStream(CurrentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1))
                    if (again.Length == _file.Length) return;
                string file = CurrentFile;
                CurrentFile = null;
                Open(file, false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>The head (sign-in) and the tail (latest world and avatar) of a log, read for context without raising events.</summary>
        void ScanContext(long length)
        {
            try
            {
                if (length <= 2 * ContextBytes) ReadRange(0, length, false);
                else { ReadRange(0, ContextBytes, false); ReadRange(length - ContextBytes, length, false); }
            }
            catch (IOException) { }
            _carry = 0; _skipLine = false;
        }

        bool ReadNew()
        {
            long len = _file.Length;
            if (len < _offset) { _offset = 0; _carry = 0; _skipLine = false; } // truncated
            if (len == _offset) return false;
            long before = _offset;
            _offset = ReadRange(_offset, len, true);
            if (_offset == before) return false;
            _lastData = Clock.Now;
            return true;
        }

        /// <summary>Reads [from, to) of the open log and handles every complete line; an unfinished last line stays in the buffer. Returns where it stopped.</summary>
        long ReadRange(long from, long to, bool live)
        {
            if (!live) { _carry = 0; _skipLine = false; }
            long pos = from;
            while (pos < to)
            {
                if (_carry == _buf.Length)
                {
                    if (_buf.Length < MaxLineBytes) Array.Resize(ref _buf, _buf.Length * 2);
                    else { _carry = 0; _skipLine = true; } // a line nobody needs: drop it up to its end
                }
                int n = RandomAccess.Read(_file.SafeFileHandle, _buf.AsSpan(_carry, (int)Math.Min(_buf.Length - _carry, to - pos)), pos);
                if (n <= 0) break;
                pos += n;
                double t = Clock.Now;
                var data = _buf.AsSpan(0, _carry + n);
                int start = 0;
                while (true)
                {
                    int nl = data.Slice(start).IndexOf((byte)'\n');
                    if (nl < 0) break;
                    if (_skipLine) _skipLine = false; else ProcessLine(data.Slice(start, nl), t, live);
                    start += nl + 1;
                }
                _carry = data.Length - start;
                if (_carry > 0 && start > 0) data.Slice(start).CopyTo(_buf);
            }
            return pos;
        }

        // ------------------------------------------------------------------ lines

        static ReadOnlySpan<byte> HumrMark => "[HUMR] RECORDING;"u8;
        static ReadOnlySpan<byte> BehaviourMark => "[Behaviour] "u8;

        void ProcessLine(ReadOnlySpan<byte> line, double arrival, bool live)
        {
            if (live)
            {
                int h = line.IndexOf(HumrMark);
                if (h >= 0) { ParseHumr(line.Slice(h + HumrMark.Length), arrival); return; }
            }
            else if (line.IndexOf(HumrMark) >= 0) return;
            if (IsContextLine(line)) ParseContext(Encoding.UTF8.GetString(line), live);
        }

        /// <summary>Whether a line is one of the few <see cref="ParseContext"/> acts on (so that all the others never become strings).</summary>
        static bool IsContextLine(ReadOnlySpan<byte> line)
        {
            int b = line.IndexOf(BehaviourMark);
            if (b >= 0)
            {
                var rest = line.Slice(b + BehaviourMark.Length);
                if (rest.StartsWith("Initialize"u8) || rest.StartsWith("Entering Room: "u8) || rest.StartsWith("Joining wrld_"u8) || rest.StartsWith("Switching "u8)) return true;
            }
            return line.IndexOf("User Authenticated: "u8) >= 0 || line.IndexOf("post login checks for "u8) >= 0 || line.IndexOf("Measure Human Avatar Avatar isRemeasure:True"u8) >= 0;
        }

        void ParseContext(string line, bool live)
        {
            int i;
            if ((i = line.IndexOf("User Authenticated: ", StringComparison.Ordinal)) >= 0)
            {
                string rest = line.Substring(i + 20).Trim();
                int p = rest.LastIndexOf(" (usr_", StringComparison.Ordinal);
                LocalUser = p > 0 ? rest.Substring(0, p) : rest;
            }
            else if ((i = line.IndexOf("post login checks for ", StringComparison.Ordinal)) >= 0)
            {
                string rest = line.Substring(i + 22).Trim();
                int p = rest.LastIndexOf(" (usr_", StringComparison.Ordinal);
                if (p > 0) LocalUser = rest.Substring(0, p);
            }
            else if ((i = line.IndexOf("[Behaviour] Initialized PlayerAPI \"", StringComparison.Ordinal)) >= 0)
            {
                int s0 = i + 35, s1 = line.LastIndexOf("\" is local", StringComparison.Ordinal);
                if (s1 > s0) LocalUser = line.Substring(s0, s1 - s0);
            }
            else if ((i = line.IndexOf("[Behaviour] Entering Room: ", StringComparison.Ordinal)) >= 0)
            {
                WorldName = line.Substring(i + 27).Trim();
                if (live) OnEvent?.Invoke("world", WorldName);
            }
            else if ((i = line.IndexOf("[Behaviour] Joining wrld_", StringComparison.Ordinal)) >= 0)
            {
                string rest = line.Substring(i + 20).Trim();
                int c = rest.IndexOf(':');
                WorldId = c > 0 ? rest.Substring(0, c) : rest;
                if (live) OnEvent?.Invoke("joining", WorldId);
            }
            else if (line.IndexOf("Measure Human Avatar Avatar isRemeasure:True", StringComparison.Ordinal) >= 0)
            {
                // VRChat re-measures the avatar: right after each IK set-up of an avatar load (same second), and on its own
                // when a full-body calibration is confirmed (both triggers), seconds after the "Calibrate FBT" reload.
                if (live) OnEvent?.Invoke("remeasure", "");
            }
            else if ((i = line.IndexOf("[Behaviour] Initialize ", StringComparison.Ordinal)) >= 0 && line.IndexOf(" Avatar VRCPlayer[Local]", StringComparison.Ordinal) > i)
            {
                // VRChat's IK set-up for the local avatar: Limb while loading, then ThreePoint / FourPoint / SixPoint ... (tracking points)
                string rest = line.Substring(i + 23);
                int k = rest.IndexOf(' ');
                if (k > 0 && live) OnEvent?.Invoke("ikInit", rest.Substring(0, k));
            }
            else if ((i = line.IndexOf("[Behaviour] Switching ", StringComparison.Ordinal)) >= 0)
            {
                string rest = line.Substring(i + 22);
                int k = rest.IndexOf(" to avatar ", StringComparison.Ordinal);
                if (k > 0)
                {
                    string who = rest.Substring(0, k), av = rest.Substring(k + 11).Trim();
                    if (who == LocalUser)
                    {
                        LocalAvatarName = av;
                        if (live) OnEvent?.Invoke("avatar", av);
                    }
                }
            }
        }

        /// <summary>The next ';'-separated field of a HUMR line; false when the line has ended.</summary>
        static bool NextField(ref ReadOnlySpan<byte> rest, out ReadOnlySpan<byte> field, ref bool ended)
        {
            if (ended) { field = default; return false; }
            int i = rest.IndexOf((byte)';');
            if (i < 0) { field = rest; rest = default; ended = true; }
            else { field = rest.Slice(0, i); rest = rest.Slice(i + 1); }
            return true;
        }

        void ParseHumr(ReadOnlySpan<byte> rest, double arrival)
        {
            // BoneRotations;<target>;<takeTimestamp>;<time>;<hips x,y,z>;<55 x quaternion>
            if (rest.Length > 0 && rest[rest.Length - 1] == (byte)'\r') rest = rest.Slice(0, rest.Length - 1);
            int fields = rest.Count((byte)';') + 1;
            if (fields < 5 + 22) return; // fewer than the 22 body bones
            bool ended = false;
            NextField(ref rest, out var kind, ref ended);
            if (!kind.SequenceEqual("BoneRotations"u8)) return;
            NextField(ref rest, out var target, ref ended);
            NextField(ref rest, out var take, ref ended);
            NextField(ref rest, out var time, ref ended);
            NextField(ref rest, out var hips, ref ended);
            var f = new HumrFrame { Target = TargetText(target), Arrival = arrival, Rot = new Quat[HumanBones.Count] };
            long.TryParse(take, NumberStyles.Integer, CultureInfo.InvariantCulture, out f.Take);
            if (!double.TryParse(time, NumberStyles.Float, CultureInfo.InvariantCulture, out f.HumrTime)) return;
            Span<float> v = stackalloc float[4];
            if (!ParseFloats(hips, v, 3)) return;
            f.Hips = new Vec3(v[0], v[1], v[2]);
            for (int b = 0; b < HumanBones.Count; b++)
            {
                if (NextField(ref rest, out var q, ref ended) && ParseFloats(q, v, 4)) f.Rot[b] = new Quat(v[0], v[1], v[2], v[3]);
                else f.Rot[b] = new Quat(0, 0, 0, 0); // missing bone marker
            }
            HumrFrames++;
            LastHumrArrival = arrival;
            LastHumrTarget = f.Target;
            OnHumr?.Invoke(f);
        }

        /// <summary>The player name of a HUMR line; the same name as last time costs no new string.</summary>
        string TargetText(ReadOnlySpan<byte> target)
        {
            if (!target.SequenceEqual(_targetBytes)) { _targetBytes = target.ToArray(); _targetText = Encoding.UTF8.GetString(target); }
            return _targetText;
        }

        /// <summary>Exactly <paramref name="count"/> comma-separated numbers.</summary>
        static bool ParseFloats(ReadOnlySpan<byte> s, Span<float> into, int count)
        {
            for (int k = 0; k < count; k++)
            {
                int c = s.IndexOf((byte)',');
                bool last = k == count - 1;
                if (last != (c < 0)) return false;
                if (!float.TryParse(last ? s : s.Slice(0, c), NumberStyles.Float, CultureInfo.InvariantCulture, out into[k])) return false;
                if (!last) s = s.Slice(c + 1);
            }
            return true;
        }

        public void Dispose()
        {
            _stop = true;
            _wake.Set();
            _thread?.Join(500);
            try { _watcher?.Dispose(); } catch { }
            CloseFile();
        }
    }
}
