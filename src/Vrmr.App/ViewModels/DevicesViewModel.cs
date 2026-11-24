using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using VRMR.App.Composition;
using VRMR.App.Services;

namespace VRMR.App.ViewModels
{
    /// <summary>One SteamVR device in the Devices panel.</summary>
    public partial class DeviceRowViewModel : ObservableObject
    {
        [ObservableProperty] double _battery;

        /// <summary>0 fine, 1 getting low, 2 low, 3 charging.</summary>
        [ObservableProperty] int _batteryLevel;

        [ObservableProperty] string _batteryText = "";
        [ObservableProperty] string _detail = "";
        [ObservableProperty] bool _hasBattery;

        /// <summary>The device's colour role: 0 headset, 1 left, 2 right, 3 tracker, 4 base station, 5 other.</summary>
        [ObservableProperty] int _kind;

        [ObservableProperty] string _name = "";
        [ObservableProperty] bool _tracked;

        /// <summary>0 tracked, 1 degraded, 2 not tracked.</summary>
        [ObservableProperty] int _trackingLevel;

        [ObservableProperty] string _trackingText = "";

        public string Key { get; init; } = "";
        public bool TrackingOk => TrackingLevel == 0;
        public bool TrackingDegraded => TrackingLevel == 1;
        public bool TrackingLost => TrackingLevel == 2;
        public bool BatteryLow => BatteryLevel == 2;
        public bool BatteryMid => BatteryLevel == 1;
        public bool BatteryCharging => BatteryLevel == 3;
        public bool IsLeft => Kind == 1;
        public bool IsRight => Kind == 2;
        public bool IsTracker => Kind == 3;

        public void Update(OpenVRSource.Device d)
        {
            Name = DeviceText.Name(d) + (d.IsBaseStation && d.HasPose
                ? FormattableString.Invariant($"   {d.Pos.y:0.00} m high")
                : "");
            string model = string.IsNullOrEmpty(d.Model) ? d.Manufacturer : d.Model;
            Detail = model + (string.IsNullOrEmpty(d.Serial) ? "" : "  ·  " + d.Serial);
            Tracked = d.Valid;
            TrackingText = d.TrackingText;
            TrackingLevel = d.TrackingLevel;
            HasBattery = d.Battery >= 0;
            Battery = Math.Max(0, Math.Min(1, d.Battery));
            BatteryText = d.Battery >= 0 ? $"{d.Battery * 100:0}%" + (d.Charging ? " charging" : "") : "";
            BatteryLevel = d.Charging ? 3 : d.Battery < 0.2f ? 2 : d.Battery < 0.4f ? 1 : 0;
            Kind = d.Class == 1 ? 0 :
                d.Class == 2 ? d.Role == 1 ? 1 : d.Role == 2 ? 2 : 5 :
                d.Class == 3 ? 3 :
                d.Class == 4 ? 4 : 5;
            OnPropertyChanged(nameof(TrackingOk));
            OnPropertyChanged(nameof(TrackingDegraded));
            OnPropertyChanged(nameof(TrackingLost));
            OnPropertyChanged(nameof(BatteryLow));
            OnPropertyChanged(nameof(BatteryMid));
            OnPropertyChanged(nameof(BatteryCharging));
            OnPropertyChanged(nameof(IsLeft));
            OnPropertyChanged(nameof(IsRight));
            OnPropertyChanged(nameof(IsTracker));
        }
    }

    /// <summary>One face shape arriving now.</summary>
    public partial class FaceValueViewModel : ObservableObject
    {
        [ObservableProperty] string _name = "";
        [ObservableProperty] double _value;
    }

    /// <summary>
    ///     The Devices panel shown with the live view: the avatar worn in VRChat and its base model, the SteamVR devices
    ///     with battery and tracking state, the face values arriving, and the connection details. It refreshes a few times
    ///     a second, and only while it is on screen.
    /// </summary>
    public partial class DevicesViewModel : ObservableObject
    {
        readonly AppHost _host;
        readonly LiveService _live;
        [ObservableProperty] int _baseModelIndex = -1;
        List<AvatarEntry> _baseModels = new();
        [ObservableProperty] string _connections = "";
        [ObservableProperty] string _connectionsSummary = "";
        [ObservableProperty] string _devicesSummary = "";
        [ObservableProperty] string _faceDiagnostics = "";
        [ObservableProperty] bool _fingerTracking;
        [ObservableProperty] bool _hasBaseModels;
        [ObservableProperty] bool _legacyPort;
        [ObservableProperty] string _steamVrDiagnostics = "";
        [ObservableProperty] bool _steamVrOn;
        bool _syncing;
        IDisposable _timer;

        public DevicesViewModel(AppHost host)
        {
            _host = host;
            _live = host.Live;
            CaptureOn = host.CaptureOn;
            _steamVrOn = host.Settings.EnableOpenVR;
            _fingerTracking = host.Settings.CaptureFingers;
            _legacyPort = host.Settings.ListenLegacyPort;
            host.Avatars.Updated += () => host.Ui.Post(() => Refresh(true));
            Refresh(true);
        }

        public StatusRowViewModel AvatarStatus { get; } = new();
        public StatusRowViewModel SteamVr { get; } = new();
        public StatusRowViewModel Tracking { get; } = new();
        public StatusRowViewModel PlayArea { get; } = new();
        public StatusRowViewModel Fingers { get; } = new();
        public StatusRowViewModel FaceStatus { get; } = new();
        public StatusRowViewModel Discovery { get; } = new();
        public StatusRowViewModel Osc { get; } = new();
        public StatusRowViewModel LegacyPortStatus { get; } = new();
        public string LegacyPortLabel => $"Receive fallback OSC on UDP {_host.Settings.LegacyPort}";
        public bool CaptureOn { get; }
        public ObservableCollection<DeviceRowViewModel> Devices { get; } = new();
        public ObservableCollection<FaceValueViewModel> Face { get; } = new();
        public ObservableCollection<string> BaseModels { get; } = new();

        /// <summary>The panel became visible or hidden.</summary>
        public void SetVisible(bool visible)
        {
            if (visible && _timer == null)
            {
                Refresh(true);
                if (CaptureOn) _timer = _host.Ui.Every(TimeSpan.FromMilliseconds(250), () => Refresh(false));
            }
            else if (!visible)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }

        partial void OnSteamVrOnChanged(bool value)
        {
            _host.SettingsStore.Update(nameof(Settings.EnableOpenVR), s => s.EnableOpenVR = value);
            _host.Recorder.ApplyTrackingSettings();
            Refresh(false);
        }

        partial void OnFingerTrackingChanged(bool value)
        {
            _host.SettingsStore.Update(nameof(Settings.CaptureFingers), s => s.CaptureFingers = value);
            _host.Recorder.ApplyTrackingSettings();
            Refresh(false);
        }

        partial void OnLegacyPortChanged(bool value)
        {
            _host.SettingsStore.Update(nameof(Settings.ListenLegacyPort), s => s.ListenLegacyPort = value);
            if (CaptureOn) _host.Recorder.ApplyOscFallbackSetting();
            RefreshConnections(_host.Recorder);
        }

        partial void OnBaseModelIndexChanged(int value)
        {
            if (_syncing || value < 0 || value >= _baseModels.Count) return;
            var rec = _live.Recorder;
            if (string.IsNullOrEmpty(rec.AvatarId))
            {
                _host.Status.Notify(
                    "VRChat has not reported an avatar yet. Turn OSC on in VRChat (Action Menu → Options → OSC → Enabled), then pick again.",
                    StatusKind.Warning);
                Refresh(false);
                return;
            }

            _host.Avatars.LinkId(rec.AvatarId, _baseModels[value]);
            _live.InvalidateMatch();
            _host.Status.Info($"{_live.WornAvatar} is now linked to {_baseModels[value].Label}");
            Refresh(false);
        }

        /// <summary>Reads the live state into the panel (only what changed raises notifications).</summary>
        public void Refresh(bool rebuildAvatars)
        {
            var rec = _live.Recorder;
            var match = _live.CurrentMatch();
            _syncing = true;
            try
            {
                var all = _host.Avatars.All();
                if (rebuildAvatars || all.Count != _baseModels.Count)
                {
                    _baseModels = all;
                    BaseModels.Clear();
                    foreach (var e in all) BaseModels.Add(e.Label);
                }

                HasBaseModels = _baseModels.Count > 0;
                BaseModelIndex = match?.Entry == null || !match.Sure && !string.IsNullOrEmpty(rec.AvatarId) ? -1 : _baseModels.FindIndex(e => e.Id == match.Entry.Id);
            }
            finally { _syncing = false; }

            string worn = _live.WornAvatar;
            bool running = rec.VrChatRunning || IsRecent(rec.LastOscTime) || rec.Log?.HumrActive == true;
            if (!CaptureOn) AvatarStatus.Set(SourceLevel.Off, "Live capture off");
            else if (!running) AvatarStatus.Set(SourceLevel.Error, "VRChat off");
            else if (worn.Length == 0) AvatarStatus.Set(SourceLevel.Waiting, "VRChat running · waiting for avatar");
            else if (match?.Sure == true) AvatarStatus.Set(SourceLevel.Ok, $"VRChat avatar linked: {worn}");
            else AvatarStatus.Set(SourceLevel.Warning, $"Unknown avatar · {worn}");

            RefreshDevices(rec);
            RefreshFace();
            RefreshConnections(rec);
        }

        void RefreshDevices(Recorder rec)
        {
            var ov = rec.OpenVR;
            var devices = _live.Devices().OrderBy(d => d.Class == 4 ? 9 : d.Class).ThenBy(d => d.Role)
                .ThenBy(d => d.Serial).ToList();
            bool connected = CaptureOn && ov?.Connected == true;
            bool enabled = CaptureOn && (SteamVrOn || ov != null);
            string status = ov?.Status ?? "Waiting for SteamVR";
            bool waiting = status is "Waiting for SteamVR" or "Not started" or "SteamVR exited" or "SteamVR: headset not found";
            SteamVr.Set(!enabled ? SourceLevel.Off : connected ? SourceLevel.Ok : waiting ? SourceLevel.Waiting : SourceLevel.Error,
                !enabled ? "SteamVR off" : connected ? "SteamVR connected" : status);
            int tracked = devices.Count(d => d.Valid);
            DevicesSummary = !enabled ? "off" : !connected ? "not connected" : $"{tracked}/{devices.Count} tracked";
            var trackingLevel = !enabled ? SourceLevel.Off : devices.Count == 0 ? SourceLevel.Waiting :
                devices.Any(d => d.TrackingLevel == 2) ? SourceLevel.Error :
                tracked == devices.Count ? SourceLevel.Ok : SourceLevel.Waiting;
            Tracking.Set(trackingLevel,
                connected ? DevicesSummary : enabled ? "Waiting for devices" : "Device tracking off");
            string area = connected ? DeviceText.PlayArea(ov.PlayArea) : null;
            PlayArea.Set(area == null ? SourceLevel.Off : SourceLevel.Ok,
                "Play area: " + (area ?? (connected ? "not set up" : "unavailable")));
            string input = connected ? ov.InputStatus : "";
            if (!enabled || !SteamVrOn || !FingerTracking) Fingers.Set(SourceLevel.Off, "Finger tracking off");
            else if (!connected || input is "" or "off" or "waiting for hands")
                Fingers.Set(SourceLevel.Waiting, "Waiting for hand input");
            else if (input.StartsWith("fingers:", StringComparison.Ordinal))
                Fingers.Set(SourceLevel.Ok, input);
            else Fingers.Set(SourceLevel.Error, input);

            SteamVrDiagnostics = $"Runtime: {ov?.Status ?? "not started"}\n" +
                                 $"Recording devices: {(CaptureOn && SteamVrOn ? "on" : "off")}\n" +
                                 $"Tracking: {Tracking.Summary}\n{PlayArea.Summary}\n{Fingers.Summary}";

            // keep the rows of devices that are still there, so the list does not flicker
            for (int i = Devices.Count - 1; i >= 0; i--)
                if (!devices.Any(d => d.Serial + d.Index == Devices[i].Key))
                    Devices.RemoveAt(i);
            for (int i = 0; i < devices.Count; i++)
            {
                string key = devices[i].Serial + devices[i].Index;
                var row = Devices.FirstOrDefault(r => r.Key == key);
                if (row == null)
                {
                    row = new DeviceRowViewModel { Key = key };
                    Devices.Insert(Math.Min(i, Devices.Count), row);
                }

                row.Update(devices[i]);
            }
        }

        void RefreshFace()
        {
            bool recent = CaptureOn && IsRecent(_live.Recorder.LastFaceTime);
            FaceStatus.Set(recent ? SourceLevel.Ok : SourceLevel.Off,
                recent ? "Receiving face data" : CaptureOn ? "No recent face data" : "Face tracking off");
            var values = recent ? _live.FaceValues() : null;
            var top = values == null
                ? new List<KeyValuePair<string, float>>()
                : values.Where(k => k.Value > 0.02f).OrderByDescending(k => k.Value).Take(16).ToList();
            while (Face.Count > top.Count) Face.RemoveAt(Face.Count - 1);
            for (int i = 0; i < top.Count; i++)
            {
                if (i >= Face.Count) Face.Add(new FaceValueViewModel());
                Face[i].Name = top[i].Key;
                Face[i].Value = Math.Min(1, top[i].Value);
            }
        }

        static bool IsRecent(double time)
        {
            return time > 0 && Clock.Now - time < 5;
        }

        void RefreshConnections(Recorder rec)
        {
            var http = rec.OscQuery?.VrcHttp;
            bool discovering = CaptureOn && _host.Settings.UseOscQuery;
            string route = CaptureOn ? rec.OscRoute : null;
            bool osc = route != null && IsRecent(rec.LastOscTime);
            Discovery.Set(!discovering ? SourceLevel.Off : http != null ? SourceLevel.Ok :
                    rec.OscQuery == null ? SourceLevel.Error : SourceLevel.Waiting,
                !discovering ? "Avatar discovery disabled" : http != null ? $"VRChat OSCQuery at {http.Address}:{http.Port}" :
                rec.OscQuery == null ? "OSCQuery unavailable" : "Looking for avatar data via VRChat OSCQuery");
            var fallback = rec.LegacyRx;
            bool listening = CaptureOn && fallback != null;
            bool standby = CaptureOn && LegacyPort;
            string bindError = standby ? rec.LegacyPortError : "";
            LegacyPortStatus.Set(listening ? SourceLevel.Ok : bindError.Length > 0 ? SourceLevel.Error :
                    standby ? SourceLevel.Waiting : SourceLevel.Off,
                listening ? $"Listening for OSC on UDP {fallback.Port}" :
                bindError.Length > 0 ? $"Cannot listen on UDP {_host.Settings.LegacyPort}" :
                standby ? "Fallback on standby" : "Fallback listener disabled");
            bool canReceive = CaptureOn && (rec.OscQueryRx != null || listening || standby && bindError.Length == 0);
            Osc.Set(osc ? SourceLevel.Ok : canReceive ? SourceLevel.Waiting : SourceLevel.Off,
                osc ? $"Receiving VRChat OSC via {route}" : canReceive ? "Waiting for VRChat OSC messages" : "VRChat OSC reception disabled");
            if (!CaptureOn)
            {
                Connections = "";
                ConnectionsSummary = "Live capture off";
                FaceDiagnostics = "Live capture off";
                return;
            }

            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine(Discovery.Summary);
            sb.AppendLine(Osc.Summary);
            sb.AppendLine($"Recorder OSCQuery service: {rec.OscQuery?.InstanceName ?? "not advertised"}");
            if (rec.OscQueryRx != null)
                sb.AppendLine($"Advertised OSC input · UDP {rec.OscQueryRx.Port}: {rec.OscQueryRx.Messages} messages, {rec.OscQueryRx.Errors} errors");
            sb.AppendLine(LegacyPortStatus.Summary);
            if (fallback != null)
                sb.AppendLine($"Fallback OSC input · UDP {fallback.Port}: {fallback.Messages} messages, {fallback.Errors} errors");
            if (bindError.Length > 0) sb.AppendLine(bindError);
            if (!string.IsNullOrEmpty(rec.AvatarId)) sb.AppendLine($"Reported avatar ID: {rec.AvatarId}");
            if (rec.AvatarEyeHeight > 0) sb.AppendLine(string.Format(inv, "Reported eye height: {0:0.000} m", rec.AvatarEyeHeight));
            RefreshFaceDiagnostics(rec, osc, sb);
            var log = rec.Log;
            sb.AppendLine($"World and HUMR log: {(log == null ? "reading disabled" : Path.GetFileName(log.CurrentFile) ?? "waiting for file")}");
            if (!string.IsNullOrEmpty(log?.WorldName)) sb.AppendLine($"Last world in log: {log.WorldName}");
            if (log != null) sb.AppendLine($"HUMR body frames read from log: {log.HumrFrames}");
            string text = sb.ToString().TrimEnd();
            if (text != Connections) Connections = text;
            ConnectionsSummary = (osc ? "receiving OSC" : canReceive ? "Waiting for OSC" : "OSC input off") +
                                 (log?.HumrActive == true ? " · VRChat IK" : "");
        }

        void RefreshFaceDiagnostics(Recorder rec, bool osc, StringBuilder vrChat)
        {
            int known = 0, recent = 0;
            lock (rec.LiveLock)
            {
                vrChat.AppendLine($"Avatar OSC parameters: {rec.LiveParams.Count}");
                foreach (var entry in rec.LiveParams)
                {
                    if (!FaceKeys.IsFace(entry.Key)) continue;
                    known++;
                    if (IsRecent(entry.Value.Item3)) recent++;
                }
            }

            string guidance = !osc ? "Waiting for VRChat OSC. Check that OSC is enabled in VRChat." :
                known == 0 ? "VRChat OSC is arriving. Check the avatar’s face tracking setup and the parameter count in VRCFaceTracking." :
                recent == 0 ? "Face parameters are known. Check whether moving your face changes the avatar in VRChat." : "";
            string counts = $"Face parameters (FT/ or v2/): {known}\nUpdated in the last 5 seconds: {recent}";
            FaceDiagnostics = guidance.Length == 0 ? counts : counts + "\n" + guidance;
        }
    }
}
