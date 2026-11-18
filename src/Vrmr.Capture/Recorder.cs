using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace VRMR
{
    /// <summary>
    /// Owns all live sources (OSC via OSCQuery, SteamVR, VRChat log) and, while recording, routes their data into a
    /// Recording + streaming file. Everything is timestamped on the shared Clock, so face, body and HUMR stay in sync.
    /// </summary>
    public sealed class Recorder : IDisposable
    {
        readonly Settings _settings;
        public OscReceiver OscQueryRx, LegacyRx;
        public OscQueryService OscQuery;
        public OpenVRSource OpenVR;
        public VrcLogTailer Log;
        /// <summary>Watches for VRChat's calibrate-and-confirm (all the time) and saves calibration snapshots.</summary>
        public CalibrationWatcher Calib;

        // ---- live state (for UI) ----
        public readonly Dictionary<string, Tuple<ParamType, float, double>> LiveParams = new Dictionary<string, Tuple<ParamType, float, double>>();
        public readonly object LiveLock = new object();
        public string AvatarId = "", AvatarName = "";
        public float AvatarEyeHeight = 0;
        public Pose VrcHead, VrcLeftWrist, VrcRightWrist, VrcCamera;
        public double VrcHeadTime = -1, VrcCameraTime = -1;
        public double LastOscTime = -1, LastFaceTime = -1;
        public long OscMessages;
        public event Action<string> AvatarChanged;
        public event Action RecordTriggerToggled;

        // ---- recording ----
        public Recording Current { get; private set; }
        RecordingWriter _writer;
        double _recStart;
        public bool IsRecording => Current != null;
        public double RecordingTime => IsRecording ? Clock.Now - _recStart : 0;
        readonly Dictionary<string, PoseStream> _deviceStreams = new Dictionary<string, PoseStream>();
        readonly Dictionary<long, double> _humrOffset = new Dictionary<long, double>(); // take -> min(arrival - humrTime)
        readonly List<Tuple<long, int>> _humrFrameTake = new List<Tuple<long, int>>();
        readonly List<double> _humrArrival = new List<double>(); // per HUMR frame: arrival (log tailer) minus the HUMR clock
        long _lastHumrTake = -1;
        bool _humrTakeEnded;
        public event Action<string> HumrTakeStarted, HumrTakeEnded;

        public Recorder(Settings s)
        {
            _settings = s;
        }

        public void Start()
        {
            // OSC: our own OSCQuery-advertised port (coexists with VRCFT & co.)
            if (_settings.UseOscQuery)
            {
                try
                {
                    OscQueryRx = new OscReceiver("oscquery", 0) { OnMessage = OnOsc };
                    OscQuery = new OscQueryService(OscQueryRx.Port);
                }
                catch (Exception ex) { AppLog.Error("OSCQuery start failed: " + ex.Message); }
            }
            // legacy: VRChat's default output port, only if nobody else owns it (retried in the background)
            if (_settings.ListenLegacyPort)
            {
                if (!TryBindLegacy()) AppLog.Info("UDP " + _settings.LegacyPort + " is in use by another app; relying on OSCQuery (will retry)");
                new Thread(() =>
                {
                    while (!_disposed)
                    {
                        Thread.Sleep(5000);
                        if (LegacyRx == null && !_disposed) TryBindLegacy();
                    }
                }) { IsBackground = true, Name = "udp retry" }.Start();
            }
            if (_settings.EnableOpenVR) StartOpenVR();
            else AppLog.Info("SteamVR source is off (Settings → Sources)");
            if (_settings.EnableHumr)
            {
                Log = new VrcLogTailer { OnHumr = OnHumr, OnEvent = OnLogEvent };
            }
            if (_settings.WatchCalibrations)
            {
                Calib = new CalibrationWatcher(() => OpenVR, () => AvatarId, () => AvatarName, () => AvatarEyeHeight);
                Calib.Captured += s => AddEvent("calibration", s.Id);
            }
            new Thread(() => { Thread.Sleep(2500); TryRefreshSnapshot(); }) { IsBackground = true }.Start();
        }

        volatile bool _disposed;

        bool TryBindLegacy()
        {
            if (LegacyRx != null || !OscReceiver.IsPortFree(_settings.LegacyPort)) return LegacyRx != null;
            try
            {
                LegacyRx = new OscReceiver("udp" + _settings.LegacyPort, _settings.LegacyPort) { OnMessage = OnOscLegacy };
                AppLog.Info("Listening on UDP " + _settings.LegacyPort);
                return true;
            }
            catch (Exception ex) { AppLog.Warn("Port " + _settings.LegacyPort + ": " + ex.Message); return false; }
        }

        /// <summary>Which route VRChat's OSC output is arriving on.</summary>
        public string OscRoute
        {
            get
            {
                double now = Clock.Now;
                if (OscQueryRx != null && OscQueryRx.LastPacketTime > 0 && now - OscQueryRx.LastPacketTime < 5) return "OSCQuery";
                if (LegacyRx != null && LegacyRx.LastPacketTime > 0 && now - LegacyRx.LastPacketTime < 5) return "UDP " + LegacyRx.Port;
                return null;
            }
        }

        // ------------------------------------------------------------------ OSC

        // When both receivers get the same stream, ignore the legacy one once OSCQuery delivers.
        void OnOscLegacy(OscMsg m)
        {
            if (OscQueryRx != null && OscQueryRx.LastPacketTime > 0 && Clock.Now - OscQueryRx.LastPacketTime < 2.0) return;
            OnOsc(m);
        }

        void OnOsc(OscMsg m)
        {
            OscMessages++;
            LastOscTime = m.Time;
            string a = m.Address;
            if (a.StartsWith("/avatar/parameters/", StringComparison.Ordinal))
            {
                if (m.Argc < 1) return;
                string name = a.Substring(19);
                char tag = m.Tag[0];
                ParamType type = tag == 'f' ? ParamType.Float : tag == 'i' ? ParamType.Int : ParamType.Bool;
                float v = m.Float(0);
                lock (LiveLock)
                {
                    LiveParams[name] = Tuple.Create(type, v, m.Time);
                }
                if (name.StartsWith("FT/", StringComparison.Ordinal) || name.StartsWith("v2/", StringComparison.Ordinal)) LastFaceTime = m.Time;
                if (name == "EyeHeightAsMeters" && v > 0.05f) AvatarEyeHeight = v;
                if (name == "TrackingType") Calib?.OnTrackingType((int)Math.Round(v), m.Time);
                if (!string.IsNullOrEmpty(_settings.RecordTriggerParam) && name == _settings.RecordTriggerParam && type == ParamType.Bool)
                    RecordTriggerToggled?.Invoke();
                var rec = Current;
                if (rec != null)
                {
                    double t = m.Time - _recStart;
                    ParamTrack p;
                    lock (rec.Sync) { p = rec.GetOrAddParam(name, type); p.Add(t, v); }
                    _writer?.AppendParam(p, t, v);
                }
                return;
            }
            if (a.StartsWith("/tracking/vrsystem/", StringComparison.Ordinal) && m.Argc >= 6)
            {
                var pose = new Pose(new Vec3(m.Float(0), m.Float(1), m.Float(2)), Quat.EulerUnity(m.Float(3), m.Float(4), m.Float(5)));
                StreamKind kind;
                if (a.IndexOf("/head/", StringComparison.Ordinal) > 0) { kind = StreamKind.VrcHead; VrcHead = pose; VrcHeadTime = m.Time; }
                else if (a.IndexOf("/leftwrist/", StringComparison.Ordinal) > 0) { kind = StreamKind.VrcLeftWrist; VrcLeftWrist = pose; }
                else if (a.IndexOf("/rightwrist/", StringComparison.Ordinal) > 0) { kind = StreamKind.VrcRightWrist; VrcRightWrist = pose; }
                else return;
                RecordPose(kind, kind.ToString(), null, m.Time, pose);
                return;
            }
            if (a == "/usercamera/Pose" && m.Argc >= 6)
            {
                var pose = new Pose(new Vec3(m.Float(0), m.Float(1), m.Float(2)), Quat.EulerUnity(m.Float(3), m.Float(4), m.Float(5)));
                VrcCamera = pose; VrcCameraTime = m.Time;
                RecordPose(StreamKind.VrcCamera, "VrcCamera", null, m.Time, pose);
                return;
            }
            if (a == "/avatar/change" && m.S != null)
            {
                string id = m.S;
                // VRChat also re-sends the same avatar when it reloads it, e.g. after a full-body calibration
                Calib?.OnAvatarReload("OSC /avatar/change", m.Time);
                if (id == AvatarId) AddEvent("avatarReload", id);
                if (id != AvatarId)
                {
                    AvatarId = id;
                    AppLog.Info("Avatar changed: " + id);
                    lock (LiveLock) LiveParams.Clear();
                    AddEvent("avatar", id);
                    AvatarChanged?.Invoke(id);
                    new Thread(() => { Thread.Sleep(1500); TryRefreshSnapshot(); }) { IsBackground = true }.Start();
                }
                return;
            }
            if (a == "/avatar/eyeheight" && m.Argc >= 1) { float v = m.Float(0); if (v > 0.05f) AvatarEyeHeight = v; }
        }

        void RecordPose(StreamKind kind, string name, Dictionary<string, object> info, double time, Pose pose)
        {
            var rec = Current;
            if (rec == null) return;
            double t = time - _recStart;
            PoseStream st;
            bool created = false;
            lock (rec.Sync)
            {
                st = kind == StreamKind.OpenVRDevice ? (_deviceStreams.TryGetValue(name, out var s) ? s : null) : rec.Stream(kind);
                if (st == null)
                {
                    st = rec.AddStream(kind, name, info);
                    if (kind == StreamKind.OpenVRDevice) _deviceStreams[name] = st;
                    created = true;
                }
                st.Add(t, pose.pos, pose.rot);
            }
            if (created) _writer?.DefineStream(st);
            _writer?.AppendPose(st, t, pose.pos, pose.rot);
        }

        /// <summary>Fetch current avatar + all parameter values from VRChat's OSCQuery server.</summary>
        public VrcOscQueryClient.Snapshot TryRefreshSnapshot()
        {
            var peer = OscQuery?.VrcHttp;
            try
            {
                var ip = peer?.Address ?? System.Net.IPAddress.Loopback;
                if (peer == null) return null;
                var snap = VrcOscQueryClient.Fetch(ip, peer.Port);
                if (!string.IsNullOrEmpty(snap.AvatarId) && snap.AvatarId != AvatarId)
                {
                    AvatarId = snap.AvatarId;
                    AvatarChanged?.Invoke(AvatarId);
                }
                if (!string.IsNullOrEmpty(snap.AvatarName)) AvatarName = snap.AvatarName;
                if (snap.Params.TryGetValue("TrackingType", out var ttv)) Calib?.OnTrackingType((int)Math.Round(ttv.Item2), Clock.Now);
                if (snap.EyeHeight > 0.05f) AvatarEyeHeight = snap.EyeHeight;
                double now = Clock.Now;
                lock (LiveLock)
                    foreach (var kv in snap.Params)
                        if (!LiveParams.ContainsKey(kv.Key)) LiveParams[kv.Key] = Tuple.Create(kv.Value.Item1, kv.Value.Item2, now - 1000);
                return snap;
            }
            catch (Exception ex) { AppLog.Warn("VRChat OSCQuery snapshot failed: " + ex.Message); return null; }
        }

        // ------------------------------------------------------------------ SteamVR

        void StartOpenVR()
        {
            OpenVR = new OpenVRSource(_settings.OpenVRRate)
            {
                OnPose = OnOpenVRPose, OnDevice = OnOpenVRDevice, OnHand = OnOpenVRHand, OnTriggers = OnOpenVRTriggers, OnTracking = OnOpenVRTracking,
                CaptureFingers = _settings.CaptureFingers, UseUtilityApp = _settings.OpenVRUtility,
            };
            OpenVR.Start();
        }

        /// <summary>
        /// Uses a SteamVR source the caller feeds itself (<see cref="OpenVRSource.Feed"/>) instead of SteamVR: wired like
        /// the real one, never started. For tests and demos.
        /// </summary>
        public void AttachOpenVR(OpenVRSource source)
        {
            var old = OpenVR;
            source.OnPose = OnOpenVRPose; source.OnDevice = OnOpenVRDevice; source.OnHand = OnOpenVRHand; source.OnTriggers = OnOpenVRTriggers; source.OnTracking = OnOpenVRTracking;
            OpenVR = source;
            try { old?.Dispose(); } catch { }
        }

        /// <summary>Apply the SteamVR settings now (enable/disable, finger tracking) without restarting the app.</summary>
        public void RestartOpenVR()
        {
            var old = OpenVR;
            OpenVR = null;
            try { old?.Dispose(); } catch { }
            if (_settings.EnableOpenVR && !_disposed) StartOpenVR();
            AppLog.Info("SteamVR source " + (_settings.EnableOpenVR ? "restarted" : "turned off"));
        }

        void OnOpenVRDevice(OpenVRSource.Device d)
        {
            var rec = Current;
            if (rec == null) return;
            PoseStream st;
            lock (rec.Sync)
            {
                if (!_deviceStreams.TryGetValue(d.Serial, out st)) return;
                st.Info["role"] = d.Role;
            }
            _writer?.DefineStream(st);
        }

        static Dictionary<string, object> DeviceInfo(OpenVRSource.Device d) => new Dictionary<string, object>
        {
            ["serial"] = d.Serial, ["class"] = d.Class, ["role"] = d.Role, ["model"] = d.Model, ["ctype"] = d.ControllerType,
            ["system"] = d.TrackingSystem, ["manufacturer"] = d.Manufacturer, ["renderModel"] = d.RenderModel,
        };

        void OnOpenVRHand(int side, double t, float[] summary, short[] bones)
        {
            var rec = Current;
            if (rec == null) return;
            double rt = t - _recStart;
            // the source reuses its arrays: the take keeps copies
            summary = (float[])summary.Clone(); bones = (short[])bones.Clone();
            lock (rec.Sync) rec.Hands[side].Add(rt, summary, bones);
            _writer?.AppendHand(rt, side, summary, bones);
        }

        string _trigState = "none";

        /// <summary>Trigger edges (both / left / right / none, with hysteresis) go into the take as events: VRChat confirms its calibration with the triggers.</summary>
        float _trigLogL = -1, _trigLogR = -1;

        void OnOpenVRTriggers(double t, float l, float r)
        {
            // the analog values too ("triggerValues" events when either moves by 0.05): which pull level VRChat confirms at
            // is not settled yet (the full press fits best on an 11-point test take, TakeCalibrations.TriggerPullLead)
            if (Current != null && (Math.Abs(l - _trigLogL) >= 0.05f || Math.Abs(r - _trigLogR) >= 0.05f))
            {
                _trigLogL = l; _trigLogR = r;
                var cr = Current;
                if (cr != null) lock (cr.Sync) cr.Events.Add(new RecEvent { T = Math.Max(0, t - _recStart), Type = "triggerValues", Text = l.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " " + r.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) });
            }
            bool lo = _trigState == "both" || _trigState == "left" ? l > 0.3f : l > 0.7f;
            bool ro = _trigState == "both" || _trigState == "right" ? r > 0.3f : r > 0.7f;
            string st = lo && ro ? "both" : lo ? "left" : ro ? "right" : "none";
            if (st == _trigState) return;
            _trigState = st;
            var rec = Current;
            if (rec != null) lock (rec.Sync) rec.Events.Add(new RecEvent { T = t - _recStart, Type = "triggers", Text = st });
        }

        /// <summary>SteamVR tracking changes per device go into the take ("tracking" events "serial code": 200 = OK, 0 = invalid pose, -1 = disconnected), for the tracking-loss repair (TrackerGaps).</summary>
        void OnOpenVRTracking(OpenVRSource.Device d, double t, int code)
        {
            var rec = Current;
            if (rec == null || d.Class == 4) return;
            lock (rec.Sync) rec.Events.Add(new RecEvent { T = Math.Max(0, t - _recStart), Type = "tracking", Text = d.Serial + " " + code });
        }

        void OnOpenVRPose(OpenVRSource.Device d, double t)
        {
            if (Current == null) return;
            RecordPose(StreamKind.OpenVRDevice, d.Serial, DeviceInfo(d), t, new Pose(d.Pos, d.Rot));
        }

        // ------------------------------------------------------------------ VRChat log / HUMR

        void OnLogEvent(string type, string text)
        {
            if (type == "avatar" && string.IsNullOrEmpty(AvatarName)) AvatarName = text;
            if (type == "avatar") Calib?.OnAvatarReload("log: avatar " + text, Clock.Now);
            if (type == "world" || type == "joining") Calib?.OnWorldChange(Clock.Now);
            if (type == "ikInit") Calib?.OnIkInit(text, Clock.Now);
            if (type == "remeasure") Calib?.OnRemeasure(Clock.Now);
            AddEvent(type, text);
        }

        void OnHumr(VrcLogTailer.HumrFrame f)
        {
            if (Log != null && !string.IsNullOrEmpty(Log.LocalUser) && f.Target != Log.LocalUser) return; // only the local player
            if (f.Take != _lastHumrTake)
            {
                _lastHumrTake = f.Take;
                HumrTakeStarted?.Invoke(f.Target);
            }
            var rec = Current;
            if (rec == null) return;
            double off = f.Arrival - f.HumrTime;
            if (!_humrOffset.TryGetValue(f.Take, out double best) || off < best) _humrOffset[f.Take] = best = off;
            double t = f.HumrTime + best - _recStart;
            if (t < -0.5) return; // frames from before the recording started
            for (int b = 0; b < HumanBones.Count; b++) if (f.Rot[b].w == 0 && f.Rot[b].x == 0 && f.Rot[b].y == 0 && f.Rot[b].z == 0) f.Rot[b] = Quat.Identity;
            lock (rec.Sync)
            {
                rec.Humr.Target = f.Target;
                rec.Humr.Add(t, f.HumrTime, f.Hips, f.Rot);
                _humrFrameTake.Add(Tuple.Create(f.Take, rec.Humr.Count - 1));
                _humrArrival.Add(off);
            }
            _writer?.AppendHumr(t, f.HumrTime, f.Hips, f.Rot);
        }

        /// <summary>Called by the UI timer: detects the end of an in-world HUMR take.</summary>
        public void PollHumrTake()
        {
            if (Log == null) return;
            bool active = Log.HumrActive;
            if (!active && _lastHumrTake >= 0 && !_humrTakeEnded) { _humrTakeEnded = true; HumrTakeEnded?.Invoke(Log.LastHumrTarget); }
            if (active) _humrTakeEnded = false;
        }

        // ------------------------------------------------------------------ recording control

        System.Threading.Timer _vrcPoll;
        string _lastVrc = "";

        /// <summary>Records VRChat's IK settings when they change during a take (registry, read-only; VRChat saves them within ~1 s).</summary>
        void PollVrcSettings()
        {
            try
            {
                if (Current == null) return;
                var v = VrcSettings.Read(true);
                if (!v.Found) return;
                string js = Json.Serialize(v.ToJson());
                if (js == _lastVrc) return;
                _lastVrc = js;
                AddEvent("vrcSettings", js);
                AppLog.Info($"VRChat IK settings changed while recording: {VrcTimeline.SpineName(v.SpineMode)}, {VrcTimeline.MeasureName(v.MeasurementType)}");
            }
            catch (Exception ex) { AppLog.Warn("Polling VRChat settings: " + ex.Message); }
        }

        void AddEvent(string type, string text)
        {
            var rec = Current;
            if (rec == null) return;
            lock (rec.Sync) rec.Events.Add(new RecEvent { T = Clock.Now - _recStart, Type = type, Text = text });
        }

        public Recording StartRecording(AvatarProfile profile)
        {
            if (IsRecording) return Current;
            Directory.CreateDirectory(_settings.RecordingsDir);
            var now = DateTime.Now;
            var rec = new Recording
            {
                Name = $"Rec {now:yyyy-MM-dd HH.mm.ss}",
                CreatedUtc = DateTime.UtcNow,
                AvatarId = AvatarId, AvatarName = AvatarName, AvatarEyeHeight = AvatarEyeHeight,
                WorldName = Log?.WorldName ?? "", LocalUser = Log?.LocalUser ?? "",
                Profile = profile?.Clone(),
            };
            // context for re-using calibrations: SteamVR session (playspace) and VRChat's IK settings (read-only)
            rec.Extra["steamvrSession"] = CalibrationLibrary.CurrentSteamVRSession();
            var vrc = VrcSettings.Read(true);
            rec.Extra["vrchatSettings"] = vrc.ToJson();
            var room = RoomNow(0); // the play area and base stations for replays (read-only SteamVR state)
            if (room != null) rec.Extra[SteamVRRoom.Key] = room.ToJson();
            string path = Path.Combine(_settings.RecordingsDir, $"{now:yyyy-MM-dd_HH-mm-ss}.vrmr");
            // a second take started within the same second must not replace the first
            for (int k = 2; File.Exists(path); k++) path = Path.Combine(_settings.RecordingsDir, $"{now:yyyy-MM-dd_HH-mm-ss}_{k}.vrmr");
            _recStart = Clock.Now;
            _deviceStreams.Clear(); _humrOffset.Clear(); _humrFrameTake.Clear(); _humrArrival.Clear();
            // initial parameter state: every known value at t=0 so the clip starts complete
            lock (LiveLock)
                foreach (var kv in LiveParams)
                {
                    var p = rec.GetOrAddParam(kv.Key, kv.Value.Item1);
                    p.Add(0, kv.Value.Item2);
                }
            _writer = new RecordingWriter(rec, path);
            rec.FilePath = path;
            foreach (var p in rec.Params) _writer.AppendParam(p, 0, p.V[0]);
            Current = rec;
            // VRChat's IK settings (spine mode, avatar measurement) while recording: a change applies from that moment
            _lastVrc = Json.Serialize(vrc.ToJson());
            lock (rec.Sync) rec.Events.Add(new RecEvent { T = 0, Type = "vrcSettings", Text = _lastVrc });
            _vrcPoll?.Dispose();
            _vrcPoll = new System.Threading.Timer(_ => PollVrcSettings(), null, 500, 500);
            AppLog.Info("Recording started -> " + path);
            return rec;
        }

        /// <summary>The SteamVR play area and base stations right now (raw standing space, like the devices' poses), or null.</summary>
        public SteamVRRoom RoomNow(double t)
        {
            var ov = OpenVR;
            if (ov == null || !ov.Connected) return null;
            try
            {
                var r = new SteamVRRoom { T = t };
                var pa = ov.PlayArea;
                if (pa != null && pa.Length == 4) r.PlayArea = (Vec3[])pa.Clone();
                foreach (var d in ov.Devices()) // once per take, not per frame
                    if (d.IsBaseStation && d.HasPose)
                        r.BaseStations.Add(new SteamVRRoom.Station { Serial = d.Serial, Pose = new Pose(d.Pos, d.Rot), FovL = d.FovL, FovR = d.FovR, FovT = d.FovT, FovB = d.FovB });
                return r.Empty ? null : r;
            }
            catch (Exception ex) { AppLog.Warn("SteamVR room: " + ex.Message); return null; }
        }

        public Recording StopRecording()
        {
            var rec = Current;
            if (rec == null) return null;
            _vrcPoll?.Dispose(); _vrcPoll = null;
            Current = null;
            lock (rec.Sync)
            {
                rec.Duration = Clock.Now - _recStart;
                if (!rec.Extra.ContainsKey(SteamVRRoom.Key)) { var room = RoomNow(rec.Duration); if (room != null) rec.Extra[SteamVRRoom.Key] = room.ToJson(); } // SteamVR came up during the take
                // re-time HUMR frames: the lowest arrival latency within 4 s of each frame (HUMR take by HUMR take).
                // VRChat's clock (HUMR's) falls behind real time on long frames (avatar reloads), so one offset per HUMR
                // take drifted up to 0.8 s over a test take with height changes (HumrSync re-aligns such older takes)
                for (int k = 0; k < _humrFrameTake.Count; k++)
                {
                    var ft = _humrFrameTake[k];
                    if (ft.Item2 >= rec.Humr.Count) continue;
                    double src = rec.Humr.SrcT[ft.Item2], best = double.MaxValue;
                    for (int j = Math.Max(0, k - 400); j < Math.Min(_humrFrameTake.Count, k + 400); j++)
                        if (_humrFrameTake[j].Item1 == ft.Item1 && _humrFrameTake[j].Item2 < rec.Humr.Count && Math.Abs(rec.Humr.SrcT[_humrFrameTake[j].Item2] - src) <= 4.0)
                            best = Math.Min(best, _humrArrival[j]);
                    if (best == double.MaxValue && !_humrOffset.TryGetValue(ft.Item1, out best)) continue;
                    rec.Humr.T[ft.Item2] = src + best - _recStart;
                }
                for (int i = 1; i < rec.Humr.Count; i++) if (rec.Humr.T[i] <= rec.Humr.T[i - 1]) rec.Humr.T[i] = rec.Humr.T[i - 1] + 1e-6;
                rec.RecomputeDuration();
                if (rec.AvatarEyeHeight <= 0) rec.AvatarEyeHeight = AvatarEyeHeight;
            }
            try { _writer?.Dispose(); } catch (Exception ex) { AppLog.Error("Close recording: " + ex.Message); }
            _writer = null;
            // link the take to its calibrations, as VRChat applied them: the calibration in effect when recording started
            // (the latest matching snapshot from before the take: a VRChat calibration made without recording, a capture or
            // a take), then every VRChat calibrate-and-confirm during the take from its "Calibrate FBT" click on
            if (_settings.AutoLinkCalibration && rec.HasOpenVR)
            {
                try
                {
                    var atStart = CalibrationLibrary.BestFor(rec, before: rec.CreatedUtc.AddSeconds(0.5));
                    var inTake = TakeCalibrations.FindAllInTake(rec, out _);
                    if (atStart != null || inTake.Count > 0)
                    {
                        rec.Edits.Snapshot = atStart?.Clone();
                        rec.Edits.CalibrationTime = EditList.UseVrcTimeline;
                        AppLog.Info("Take follows VRChat's calibrations: " + (atStart != null ? "from the start " + atStart.Label : "no calibration before the take") +
                                    (inTake.Count > 0 ? $"; recalibrated in the take at {string.Join(", ", inTake.Select(c => c.Confirm.ToString("F1") + " s"))}" : ""));
                    }
                    else
                    {
                        var snap = CalibrationLibrary.BestFor(rec);
                        if (snap != null)
                        {
                            rec.Edits.Snapshot = snap.Clone();
                            rec.Edits.CalibrationTime = EditList.UseSnapshot;
                            AppLog.Info("Take linked to calibration " + snap.Label);
                        }
                    }
                }
                catch (Exception ex) { AppLog.Warn("Linking calibration: " + ex.Message); }
            }
            // rewrite compactly (sorted, with corrected HUMR times)
            try { RecordingIO.Save(rec, rec.FilePath); } catch (Exception ex) { AppLog.Error("Save: " + ex.Message); }
            AppLog.Info($"Recording stopped: {rec.Duration:F1}s, {rec.Params.Count} params, {rec.Streams.Count} pose streams, {rec.Humr.Count} HUMR frames");
            return rec;
        }

        public void Dispose()
        {
            _disposed = true;
            if (IsRecording) StopRecording();
            try { OscQuery?.Dispose(); } catch { }
            try { OscQueryRx?.Dispose(); } catch { }
            try { LegacyRx?.Dispose(); } catch { }
            try { OpenVR?.Dispose(); } catch { }
            try { Log?.Dispose(); } catch { }
            try { Calib?.Dispose(); } catch { }
        }
    }
}
