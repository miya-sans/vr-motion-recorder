using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace VRMR
{
    /// <summary>
    /// Watches for VRChat's full-body calibration all the time (recording or not) and saves each one as a calibration
    /// snapshot in the library. Everything it uses is read-only. VRChat's sequence (output_log, checked against a
    /// SteamVR trigger probe and VRChat's own IK on test takes):
    ///  1. "Calibrate FBT" (a menu click) reloads the avatar: "[Behaviour] Switching &lt;local user&gt; to avatar ...",
    ///     "Initialize Limb Avatar", "Measure Human Avatar Avatar isRemeasure:True", "Initialize SixPoint Avatar" (or
    ///     FourPoint / ThreePoint), all within ~1 s. The avatar is now in calibration mode.
    ///  2. The confirm (both triggers; one with VRChat's one-handed option) logs a lone
    ///     "Measure Human Avatar Avatar isRemeasure:True" seconds later (3.4-15 s on the takes), and the avatar snaps
    ///     onto the controllers and trackers.
    ///  Changing the avatar measurement mode reloads the avatar the same way, but its re-measure follows in the same
    ///  second; spine mode changes don't reload. OSC TrackingType is not reliable here (it stayed 2 through whole
    ///  calibrations), so it isn't used.
    /// The triggers are read from SteamVR Input as a background utility app (it receives them while VRChat has focus);
    /// without trigger data the joint release of both index fingers (Index skeletal input) or the log line is used.
    /// The snapshot holds the raw SteamVR poses of every device just before the confirm.
    /// </summary>
    public sealed class CalibrationWatcher : IDisposable
    {
        /// <summary>One sample of the devices. The ring's frames and their device objects are reused, so sampling allocates nothing.</summary>
        sealed class Frame
        {
            public double T;
            public OpenVRSource.Device[] Devs = new OpenVRSource.Device[OpenVRSource.MaxDevices];
            public int Count;
            public float TrigL, TrigR, CurlL, CurlR;
            public bool HasTrig, HasCurl;
            public List<OpenVRSource.Device> Devices() { var l = new List<OpenVRSource.Device>(Count); for (int i = 0; i < Count; i++) l.Add(Devs[i].Clone()); return l; }
        }

        readonly Func<OpenVRSource> _ovr;
        readonly Func<string> _avatarId, _avatarName;
        readonly Func<float> _avatarEye;
        const double RingSeconds = 8;
        const int SampleMs = 33;
        /// <summary>The last <see cref="RingSeconds"/> of samples, oldest first from <see cref="_ringStart"/>; slots are reused.</summary>
        readonly Frame[] _ring = new Frame[(int)(RingSeconds * 1000 / SampleMs) + 16];
        int _ringStart, _ringCount;
        readonly object _lock = new object();
        readonly Thread _thread;
        volatile bool _stop;
        readonly ManualResetEventSlim _stopSignal = new ManualResetEventSlim(false);

        int _tt = -1; double _ttSince = -1, _lastTT3 = -1;
        double _reloadT = -1, _ikInitT = -1; string _reloadHow = "";
        double _worldT = -1;
        public bool Enabled = true;
        /// <summary>Raised (on the watcher thread) after a snapshot was saved.</summary>
        public event Action<CalibrationSnapshot> Captured;
        public CalibrationSnapshot Last { get; private set; }
        public string Status { get; private set; } = "waiting for VRChat";
        public int TrackingType => _tt;

        public CalibrationWatcher(Func<OpenVRSource> ovr, Func<string> avatarId, Func<string> avatarName, Func<float> avatarEye)
        {
            _ovr = ovr; _avatarId = avatarId; _avatarName = avatarName; _avatarEye = avatarEye;
            _thread = new Thread(Loop) { IsBackground = true, Name = "calibration watch" };
            _thread.Start();
        }

        // ------------------------------------------------------------------ inputs (from the Recorder)

        /// <summary>OSC TrackingType (kept for the snapshot's record; not used for detection).</summary>
        public void OnTrackingType(int v, double t)
        {
            lock (_lock)
            {
                if (v == _tt) return;
                if (_tt == 3) _lastTT3 = t;
                _tt = v; _ttSince = t;
                if (v == 3) _lastTT3 = t;
            }
        }

        /// <summary>VRChat reloaded the local avatar (log "Switching ... to avatar", or OSC /avatar/change): maybe "Calibrate FBT".</summary>
        public void OnAvatarReload(string how, double t)
        {
            lock (_lock)
            {
                if (_reloadT > 0 && t - _reloadT < 1.0) return; // log + OSC for the same reload
                if (_worldT > 0 && t - _worldT < 25) { Status = "avatar loaded after a world change (not a calibration)"; _reloadT = -1; return; }
                _reloadT = t; _reloadHow = how; _ikInitT = -1;
                Status = "VRChat avatar reloaded: waiting for a calibration confirm (both triggers)";
            }
        }

        /// <summary>VRChat set up the IK of the local avatar ("Initialize SixPoint Avatar" etc.; "Limb" while loading).</summary>
        public void OnIkInit(string kind, double t)
        {
            if (kind == "Limb") return;
            lock (_lock) _ikInitT = t;
        }

        /// <summary>"Measure Human Avatar Avatar isRemeasure:True": the confirm, unless it belongs to the IK set-up itself.</summary>
        public void OnRemeasure(double t)
        {
            double reload, ik;
            lock (_lock) { reload = _reloadT; ik = _ikInitT; }
            if (reload < 0 || t - reload > TakeCalibrations.MaxCalibrationSeconds) return;
            if (ik < 0) return; // the re-measure of the loading ("Limb") stage
            if (t - ik < TakeCalibrations.ConfirmAfterIkInit) { lock (_lock) { _reloadT = -1; _ikInitT = -1; } Status = "avatar re-measured right after loading (a setting change, not a calibration)"; return; }
            TryConfirm(t);
        }

        public void OnWorldChange(double t) { lock (_lock) { _worldT = t; _reloadT = -1; } }

        // ------------------------------------------------------------------ ring buffer

        void Loop()
        {
            EfficiencyMode.ExemptCurrentThread();
            while (!_stop)
            {
                // samples only while SteamVR is connected; otherwise it looks twice a second whether that changed
                var o = Enabled ? _ovr() : null;
                if (o == null || !o.Connected) { _stopSignal.Wait(500); continue; }
                if (_stopSignal.Wait(SampleMs)) break;
                try
                {
                    lock (_lock)
                    {
                        var f = NextFrame(Clock.Now);
                        f.Count = o.CopyDevices(f.Devs, OpenVRSource.DeviceFilter.TrackedBody);
                        f.HasTrig = o.TriggerTime[0] > 0 && f.T - o.TriggerTime[0] < 0.5;
                        if (f.HasTrig) { f.TrigL = o.Trigger[0]; f.TrigR = o.Trigger[1]; }
                        f.HasCurl = o.LiveFingerTime[0] > 0 && f.T - o.LiveFingerTime[0] < 0.5;
                        if (f.HasCurl) { f.CurlL = o.LiveFingers[0][1]; f.CurlR = o.LiveFingers[1][1]; }
                    }
                }
                catch (Exception ex) { Status = "error: " + ex.Message; _stopSignal.Wait(1000); }
            }
        }

        /// <summary>The ring's next slot, stamped with t; frames older than <see cref="RingSeconds"/> (or the oldest, when full) drop out. Call under the lock.</summary>
        Frame NextFrame(double t)
        {
            while (_ringCount > 0 && (t - _ring[_ringStart].T > RingSeconds || _ringCount == _ring.Length)) { _ringStart = (_ringStart + 1) % _ring.Length; _ringCount--; }
            int slot = (_ringStart + _ringCount) % _ring.Length;
            _ringCount++;
            var f = _ring[slot] ?? (_ring[slot] = new Frame());
            f.T = t; f.Count = 0; f.HasTrig = f.HasCurl = false; f.TrigL = f.TrigR = f.CurlL = f.CurlR = 0;
            return f;
        }

        /// <summary>The i-th frame of the ring, oldest first. Call under the lock.</summary>
        Frame At(int i) => _ring[(_ringStart + i) % _ring.Length];

        /// <summary>Tests only: adds a device frame to the ring buffer.</summary>
        public void InjectFrame(double t, List<OpenVRSource.Device> devs, float trigL, float trigR, bool hasTrig = true, float curlL = -1, float curlR = -1)
        {
            lock (_lock)
            {
                var f = NextFrame(t);
                for (int i = 0; i < devs.Count && i < f.Devs.Length; i++) (f.Devs[i] ?? (f.Devs[i] = new OpenVRSource.Device())).CopyFrom(devs[i]);
                f.Count = Math.Min(devs.Count, f.Devs.Length);
                f.TrigL = trigL; f.TrigR = trigR; f.HasTrig = hasTrig; f.CurlL = curlL; f.CurlR = curlR; f.HasCurl = curlL >= 0;
            }
        }

        // ------------------------------------------------------------------ detection

        void TryConfirm(double remeasure)
        {
            double reload; string how; int tt;
            lock (_lock) { reload = _reloadT; how = _reloadHow; tt = _tt; _reloadT = -1; _ikInitT = -1; }
            if (reload < 0) return;
            var vrc = VrcSettings.Read();
            if (!FindConfirm(remeasure, vrc.OneHandedCalibration, out double at, out string why))
            {
                Status = "VRChat calibration seen, but no SteamVR poses at that moment";
                return;
            }
            var snap = Build(at, "vrchat", $"VRChat calibrate-and-confirm: {why}; \"Calibrate FBT\" {remeasure - reload:0.0} s earlier ({how})", vrc);
            if (snap == null) { Status = "VRChat calibration seen, but no headset pose"; return; }
            snap.TrackingType = tt;
            Save(snap);
        }

        /// <summary>
        /// The confirm moment at VRChat's confirm re-measure: when both triggers were pulled (either one with one-handed
        /// calibration) just before it, else where both index fingers peaked before releasing together, else 0.3 s
        /// before the log line. The pose is taken just before the pull.
        /// </summary>
        bool FindConfirm(double remeasure, bool oneHanded, out double at, out string why)
        {
            at = remeasure - 0.3; why = "VRChat's confirm (log)";
            // the window's trigger and finger values, copied out: the ring's frames are reused while this runs
            var win = new List<(double T, float TrigL, float TrigR, float CurlL, float CurlR, bool HasTrig, bool HasCurl)>();
            lock (_lock)
                for (int i = 0; i < _ringCount; i++)
                {
                    var f = At(i);
                    if (f.T >= remeasure - 2.5 && f.T <= remeasure + 0.3) win.Add((f.T, f.TrigL, f.TrigR, f.CurlL, f.CurlR, f.HasTrig, f.HasCurl));
                }
            if (win.Count == 0) return false;
            if (win.Any(f => f.HasTrig))
            {
                // last frame where the confirm condition starts (rising edge), then the pose just before the pull
                for (int i = win.Count - 1; i > 0; i--)
                {
                    bool on = oneHanded ? Math.Max(win[i].TrigL, win[i].TrigR) > 0.7f : Math.Min(win[i].TrigL, win[i].TrigR) > 0.7f;
                    bool prev = oneHanded ? Math.Max(win[i - 1].TrigL, win[i - 1].TrigR) > 0.7f : Math.Min(win[i - 1].TrigL, win[i - 1].TrigR) > 0.7f;
                    if (on && !prev)
                    {
                        at = Math.Max(win[0].T, win[i].T - TakeCalibrations.TriggerPullLead); // the full press (see TriggerPullLead)
                        why = (oneHanded ? "trigger" : "both triggers") + $" pulled {remeasure - win[i].T:0.00} s before VRChat logged the confirm";
                        return true;
                    }
                }
            }
            if (win.Any(f => f.HasCurl))
            {
                var t = win.Where(f => f.HasCurl).Select(f => f.T).ToList();
                var l = win.Where(f => f.HasCurl).Select(f => f.CurlL).ToList();
                var r = win.Where(f => f.HasCurl).Select(f => f.CurlR).ToList();
                double peak = TakeCalibrations.BothIndexRelease(t, l, r, oneHanded);
                if (peak >= 0) { at = Math.Max(win[0].T, peak - 0.2); why = $"{(oneHanded ? "index finger" : "both index fingers")} pulled and released {remeasure - peak:0.00} s before VRChat logged the confirm"; return true; }
            }
            return true;
        }

        CalibrationSnapshot Build(double at, string source, string detail, VrcSettings.Values vrc)
        {
            List<OpenVRSource.Device> devs; double t;
            lock (_lock)
            {
                if (_ringCount == 0) return null;
                // the first frame at or after the moment, or the one before it when that is nearer; the last one when all are older
                int pick = _ringCount - 1;
                for (int i = 0; i < _ringCount; i++)
                    if (At(i).T >= at) { pick = i > 0 && Math.Abs(At(i - 1).T - at) < Math.Abs(At(i).T - at) ? i - 1 : i; break; }
                devs = At(pick).Devices(); t = At(pick).T;
            }
            return FromDevices(devs, DateTime.UtcNow.AddSeconds(t - Clock.Now), source, detail, vrc);
        }

        CalibrationSnapshot FromDevices(List<OpenVRSource.Device> devs, DateTime utc, string source, string detail, VrcSettings.Values vrc)
        {
            if (devs == null || !devs.Any(d => d.Class == 1 && d.HasPose)) return null;
            var o = _ovr();
            var s = new CalibrationSnapshot
            {
                CreatedUtc = utc, Source = source, Detail = detail, SteamVRSession = CalibrationLibrary.CurrentSteamVRSession(),
                AvatarId = _avatarId() ?? "", AvatarName = _avatarName() ?? "", AvatarEyeHeight = _avatarEye(), Vrc = vrc ?? VrcSettings.Read(),
            };
            foreach (var d in devs.Where(d => d.HasPose && !d.IsBaseStation && d.Class >= 1 && d.Class <= 3))
                s.Devices.Add(new CalibrationSnapshot.Device { Serial = d.Serial, Class = d.Class, Role = d.Role, Model = d.Model, ControllerType = d.ControllerType, TrackerRole = d.TrackerRole, Pos = d.Pos, Rot = d.Rot });
            if (o != null)
                for (int i = 0; i < 2; i++)
                    if (o.LiveWristTime[i] > 0 && Clock.Now - o.LiveWristTime[i] < 2) { s.WristPos[i] = o.LiveWristPos[i]; s.WristRot[i] = o.LiveWristRot[i]; s.HasWrist[i] = true; }
            s.Id = CalibrationLibrary.NewId(utc);
            return s;
        }

        void Save(CalibrationSnapshot s)
        {
            try
            {
                CalibrationLibrary.Save(s);
                Last = s;
                Status = "saved " + s.Label;
                AppLog.Info("Calibration snapshot saved: " + s.Label + " - " + s.Detail);
                Captured?.Invoke(s);
            }
            catch (Exception ex) { Status = "save failed: " + ex.Message; AppLog.Warn("Calibration snapshot: " + ex.Message); }
        }

        /// <summary>"Capture calibration now": the current SteamVR poses (stand straight, look ahead, arms out).</summary>
        public CalibrationSnapshot CaptureNow(string detail)
        {
            var o = _ovr();
            if (o == null || !o.Connected) throw new InvalidOperationException("SteamVR is not connected");
            var s = FromDevices(o.Snapshot(), DateTime.UtcNow, "manual", detail, VrcSettings.Read());
            if (s == null) throw new InvalidOperationException("The headset has no pose");
            Save(s);
            return s;
        }

        // ------------------------------------------------------------------ the same detection on a recorded take

        public void Dispose()
        {
            _stop = true;
            _stopSignal.Set();
            _thread.Join(300);
        }
    }
}
