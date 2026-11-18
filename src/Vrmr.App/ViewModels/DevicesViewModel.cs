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
        [ObservableProperty] string _name = "";
        [ObservableProperty] string _detail = "";
        [ObservableProperty] bool _tracked;
        [ObservableProperty] string _trackingText = "";
        /// <summary>0 tracked, 1 degraded, 2 not tracked.</summary>
        [ObservableProperty] int _trackingLevel;
        [ObservableProperty] bool _hasBattery;
        [ObservableProperty] double _battery;
        [ObservableProperty] string _batteryText = "";
        /// <summary>0 fine, 1 getting low, 2 low, 3 charging.</summary>
        [ObservableProperty] int _batteryLevel;
        /// <summary>The device's colour role: 0 headset, 1 left, 2 right, 3 tracker, 4 base station, 5 other.</summary>
        [ObservableProperty] int _kind;

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
            Name = DeviceText.Name(d) + (d.IsBaseStation && d.HasPose ? FormattableString.Invariant($"   {d.Pos.y:0.00} m high") : "");
            string model = string.IsNullOrEmpty(d.Model) ? d.Manufacturer : d.Model;
            Detail = model + (string.IsNullOrEmpty(d.Serial) ? "" : "  ·  " + d.Serial);
            Tracked = d.Valid;
            TrackingText = d.TrackingText; TrackingLevel = d.TrackingLevel;
            HasBattery = d.Battery >= 0;
            Battery = Math.Max(0, Math.Min(1, d.Battery));
            BatteryText = d.Battery >= 0 ? $"{d.Battery * 100:0}%" + (d.Charging ? " charging" : "") : "";
            BatteryLevel = d.Charging ? 3 : d.Battery < 0.2f ? 2 : d.Battery < 0.4f ? 1 : 0;
            Kind = d.Class == 1 ? 0 : d.Class == 2 ? (d.Role == 1 ? 1 : d.Role == 2 ? 2 : 5) : d.Class == 3 ? 3 : d.Class == 4 ? 4 : 5;
            OnPropertyChanged(nameof(TrackingOk)); OnPropertyChanged(nameof(TrackingDegraded)); OnPropertyChanged(nameof(TrackingLost));
            OnPropertyChanged(nameof(BatteryLow)); OnPropertyChanged(nameof(BatteryMid)); OnPropertyChanged(nameof(BatteryCharging));
            OnPropertyChanged(nameof(IsLeft)); OnPropertyChanged(nameof(IsRight)); OnPropertyChanged(nameof(IsTracker));
        }
    }

    /// <summary>One face shape arriving now.</summary>
    public partial class FaceValueViewModel : ObservableObject
    {
        [ObservableProperty] string _name = "";
        [ObservableProperty] double _value;
    }

    /// <summary>
    /// The Devices panel shown with the live view: the avatar worn in VRChat and its base model, the SteamVR devices
    /// with battery and tracking state, the face values arriving, and the connection details. It refreshes a few times
    /// a second, and only while it is on screen.
    /// </summary>
    public partial class DevicesViewModel : ObservableObject
    {
        readonly AppHost _host;
        readonly LiveService _live;
        IDisposable _timer;
        List<AvatarEntry> _baseModels = new List<AvatarEntry>();
        bool _syncing;

        public DevicesViewModel(AppHost host)
        {
            _host = host; _live = host.Live;
            CaptureOn = host.CaptureOn;
            host.Avatars.Updated += () => host.Ui.Post(() => Refresh(rebuildAvatars: true));
            Refresh(rebuildAvatars: true);
        }

        public bool CaptureOn { get; }
        public ObservableCollection<DeviceRowViewModel> Devices { get; } = new ObservableCollection<DeviceRowViewModel>();
        public ObservableCollection<FaceValueViewModel> Face { get; } = new ObservableCollection<FaceValueViewModel>();
        public ObservableCollection<string> BaseModels { get; } = new ObservableCollection<string>();

        [ObservableProperty] string _wearing = "";
        [ObservableProperty] string _wearingNote = "";
        [ObservableProperty] bool _wearingWarning;
        [ObservableProperty] int _baseModelIndex = -1;
        [ObservableProperty] bool _hasBaseModels;
        [ObservableProperty] string _deviceStatus = "";
        [ObservableProperty] string _devicesSummary = "";
        [ObservableProperty] string _playArea = "";
        [ObservableProperty] bool _noFace = true;
        [ObservableProperty] string _connections = "";
        [ObservableProperty] string _connectionsSummary = "";

        /// <summary>The panel became visible or hidden.</summary>
        public void SetVisible(bool visible)
        {
            if (visible && _timer == null)
            {
                Refresh(rebuildAvatars: true);
                if (CaptureOn) _timer = _host.Ui.Every(TimeSpan.FromMilliseconds(250), () => Refresh(false));
            }
            else if (!visible) { _timer?.Dispose(); _timer = null; }
        }

        partial void OnBaseModelIndexChanged(int value)
        {
            if (_syncing || value < 0 || value >= _baseModels.Count) return;
            var rec = _live.Recorder;
            if (string.IsNullOrEmpty(rec.AvatarId))
            {
                _host.Status.Notify("VRChat has not reported an avatar yet. Turn OSC on in VRChat (Action Menu → Options → OSC → Enabled), then pick again.", StatusKind.Warning);
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
                BaseModelIndex = match?.Entry == null ? -1 : _baseModels.FindIndex(e => e.Id == match.Entry.Id);
            }
            finally { _syncing = false; }
            string worn = _live.WornAvatar;
            Wearing = !CaptureOn ? "Live sources are off in this session." : worn.Length > 0 ? worn : "VRChat has not reported an avatar yet";
            WearingNote = !CaptureOn ? "" : worn.Length == 0 ? "Turn OSC on in VRChat: Action Menu → Options → OSC → Enabled."
                : match?.Entry == null ? "No base model matches it. Add your avatar under Settings → Avatars."
                : match.Entry.BuiltIn && !match.Sure ? "None of your base models matches it, so the built-in mannequin stands in. Add your avatar under Settings → Avatars, or pick one below."
                : match.Sure ? $"Base model found {match.How}." : "The base model is a guess: pick the right one below. It is remembered for this avatar.";
            WearingWarning = CaptureOn && worn.Length > 0 && (match?.Entry == null || !match.Sure);

            RefreshDevices(rec);
            RefreshFace();
            RefreshConnections(rec);
        }

        void RefreshDevices(Recorder rec)
        {
            var ov = rec.OpenVR;
            var devices = _live.Devices().OrderBy(d => d.Class == 4 ? 9 : d.Class).ThenBy(d => d.Role).ThenBy(d => d.Serial).ToList();
            DeviceStatus = !CaptureOn ? "" : ov == null ? "SteamVR devices are switched off (Settings → Sources)."
                : !ov.Connected ? "SteamVR is not connected: " + (ov.Status ?? "").TrimEnd('.').ToLowerInvariant() + ". Start SteamVR; the app connects by itself."
                : devices.Count == 0 ? "SteamVR is connected, but no device is switched on." : "";
            DevicesSummary = ov == null ? "off" : !ov.Connected ? "not connected" : $"{devices.Count(d => d.Valid)}/{devices.Count} tracked";
            PlayArea = ov != null && ov.Connected ? "Play area: " + (DeviceText.PlayArea(ov.PlayArea) ?? "not set up in SteamVR") : "";
            // keep the rows of devices that are still there, so the list does not flicker
            for (int i = Devices.Count - 1; i >= 0; i--) if (!devices.Any(d => d.Serial + d.Index == Devices[i].Key)) Devices.RemoveAt(i);
            for (int i = 0; i < devices.Count; i++)
            {
                string key = devices[i].Serial + devices[i].Index;
                var row = Devices.FirstOrDefault(r => r.Key == key);
                if (row == null) { row = new DeviceRowViewModel { Key = key }; Devices.Insert(Math.Min(i, Devices.Count), row); }
                row.Update(devices[i]);
            }
        }

        void RefreshFace()
        {
            var values = CaptureOn ? _live.FaceValues() : null;
            var top = values == null ? new List<KeyValuePair<string, float>>() : values.Where(k => k.Value > 0.02f).OrderByDescending(k => k.Value).Take(16).ToList();
            NoFace = top.Count == 0;
            while (Face.Count > top.Count) Face.RemoveAt(Face.Count - 1);
            for (int i = 0; i < top.Count; i++)
            {
                if (i >= Face.Count) Face.Add(new FaceValueViewModel());
                Face[i].Name = top[i].Key; Face[i].Value = Math.Min(1, top[i].Value);
            }
        }

        void RefreshConnections(Recorder rec)
        {
            if (!CaptureOn) { Connections = ""; ConnectionsSummary = "off"; return; }
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine($"OSCQuery: {(rec.OscQuery != null ? rec.OscQuery.InstanceName : "off")}");
            if (rec.OscQueryRx != null) sb.AppendLine($"  UDP {rec.OscQueryRx.Port}: {rec.OscQueryRx.Messages} messages");
            if (rec.LegacyRx != null) sb.AppendLine($"  UDP {rec.LegacyRx.Port}: {rec.LegacyRx.Messages} messages");
            var http = rec.OscQuery?.VrcHttp;
            sb.AppendLine($"VRChat's OSCQuery: {(http != null ? http.Address + ":" + http.Port : "not found")}");
            sb.AppendLine($"Avatar: {rec.AvatarName} {rec.AvatarId}");
            sb.AppendLine(string.Format(inv, "Eye height: {0:0.000} m", rec.AvatarEyeHeight));
            lock (rec.LiveLock) sb.AppendLine($"Parameters known: {rec.LiveParams.Count}");
            var ov = rec.OpenVR;
            sb.AppendLine($"SteamVR: {(ov == null ? "off" : ov.Status)}");
            if (ov != null) sb.AppendLine($"  finger tracking: {ov.InputStatus}");
            var log = rec.Log;
            sb.AppendLine($"VRChat log: {(log?.CurrentFile != null ? Path.GetFileName(log.CurrentFile) : "none")}");
            if (log != null) sb.AppendLine($"  world {log.WorldName}\n  VRChat IK frames {log.HumrFrames}{(log.HumrActive ? " (arriving)" : "")}");
            string text = sb.ToString().TrimEnd();
            if (text != Connections) Connections = text;
            bool osc = rec.LastOscTime > 0 && Clock.Now - rec.LastOscTime < 5;
            ConnectionsSummary = (osc ? "receiving OSC" : http != null ? "VRChat found, no OSC" : "VRChat not found") + (log?.HumrActive == true ? " · VRChat IK" : "");
        }
    }
}
