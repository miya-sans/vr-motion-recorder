using System;
using System.Collections.Generic;
using System.Linq;

namespace VRMR.App.Services
{
    /// <summary>Words for SteamVR devices, shared by the view, the Devices panel and the status pills.</summary>
    public static class DeviceText
    {
        /// <summary>"Headset", "Left hand", a tracker's SteamVR role, "Base station B".</summary>
        public static string Name(OpenVRSource.Device d)
        {
            switch (d.Class)
            {
                case 1: return "Headset";
                case 2: return d.Role == 1 ? "Left hand" : d.Role == 2 ? "Right hand" : "Controller";
                case 3: return string.IsNullOrEmpty(d.TrackerRole) ? "Tracker" : d.TrackerRole;
                case 4: return "Base station" + (string.IsNullOrEmpty(d.ModeLabel) ? "" : " " + d.ModeLabel.ToUpperInvariant());
                default: return "Device";
            }
        }

        /// <summary>"Headset · 2 controllers · 3 trackers · 2 base stations (1 not tracked)".</summary>
        public static string Summary(IReadOnlyList<OpenVRSource.Device> devices)
        {
            if (devices == null || devices.Count == 0) return "No devices are on";
            static string N(int c, string one, string many) => c == 0 ? null : c == 1 ? "1 " + one : c + " " + many;
            var parts = new[]
            {
                devices.Any(d => d.Class == 1) ? "Headset" : null,
                N(devices.Count(d => d.Class == 2), "controller", "controllers"),
                N(devices.Count(d => d.Class == 3), "tracker", "trackers"),
                N(devices.Count(d => d.Class == 4), "base station", "base stations"),
            }.Where(s => s != null);
            int lost = devices.Count(d => !d.Valid);
            return string.Join(" · ", parts) + (lost > 0 ? $" ({lost} not tracked)" : "");
        }

        /// <summary>"3.2 × 2.4 m", or null when Room Setup has no play area.</summary>
        public static string PlayArea(Vec3[] corners)
        {
            if (corners == null || corners.Length != 4) return null;
            float a = (corners[1] - corners[0]).Length, b = (corners[2] - corners[1]).Length;
            return FormattableString.Invariant($"{Math.Max(a, b):0.0} × {Math.Min(a, b):0.0} m");
        }
    }

    /// <summary>
    /// What the recorder's sources report right now, read by the live view and the Devices panel: the avatar worn in
    /// VRChat and its base model, the SteamVR devices, the face values. Without capture everything is empty.
    /// </summary>
    public sealed class LiveService
    {
        readonly Recorder _recorder;
        readonly AvatarLibrary _avatars;
        AvatarMatch _match;
        string _matchKey = "";

        public LiveService(Recorder recorder, AvatarLibrary avatars, bool captureOn)
        {
            _recorder = recorder; _avatars = avatars; CaptureOn = captureOn;
            _avatars.Updated += () => _matchKey = "";
        }

        /// <summary>False when the app was started without live sources.</summary>
        public bool CaptureOn { get; }

        public Recorder Recorder => _recorder;

        /// <summary>The base model of the avatar worn now (cached; the library matches by linked id, name, then eye height).</summary>
        public AvatarMatch CurrentMatch()
        {
            float scale = 0;
            lock (_recorder.LiveLock) if (_recorder.LiveParams.TryGetValue("ScaleFactor", out var v)) scale = v.Item2;
            string key = _recorder.AvatarId + "|" + _recorder.AvatarName + "|" + Math.Round(_recorder.AvatarEyeHeight, 3) + "|" + Math.Round(scale, 3);
            if (key != _matchKey || _match == null) { _matchKey = key; _match = _avatars.Match(_recorder.AvatarId, _recorder.AvatarName, _recorder.AvatarEyeHeight, scale); }
            return _match;
        }

        /// <summary>Forget the cached match (a link or the library changed).</summary>
        public void InvalidateMatch() => _matchKey = "";

        /// <summary>The avatar worn in VRChat, in words; "" when VRChat has not reported one.</summary>
        public string WornAvatar => !string.IsNullOrEmpty(_recorder.AvatarName) ? _recorder.AvatarName : _recorder.AvatarId ?? "";

        /// <summary>The SteamVR devices as they are now (empty when SteamVR is off or not connected).</summary>
        public List<OpenVRSource.Device> Devices()
        {
            var ov = _recorder.OpenVR;
            return ov != null && ov.Connected ? ov.Devices() : new List<OpenVRSource.Device>();
        }

        /// <summary>True when SteamVR is connected.</summary>
        public bool SteamVrConnected => _recorder.OpenVR != null && _recorder.OpenVR.Connected;

        /// <summary>The face shape weights from the parameters arriving now.</summary>
        public Dictionary<string, float> FaceValues()
        {
            var mapping = new FaceMapping { Mode = FaceMapping.Naming.UnifiedExpressions };
            Dictionary<string, Tuple<ParamType, float, double>> snap;
            lock (_recorder.LiveLock) snap = new Dictionary<string, Tuple<ParamType, float, double>>(_recorder.LiveParams);
            return mapping.Evaluate(n => snap.TryGetValue(n, out var v) ? v.Item2 : (float?)null);
        }

        /// <summary>Anything arriving that makes the live view worth redrawing (devices, VRChat poses, a recording in progress).</summary>
        public bool HasLiveMotion
        {
            get
            {
                if (!CaptureOn) return false;
                double now = Clock.Now;
                return SteamVrConnected || _recorder.IsRecording
                    || (_recorder.VrcHeadTime > 0 && now - _recorder.VrcHeadTime < 1)
                    || (_recorder.VrcCameraTime > 0 && now - _recorder.VrcCameraTime < 2);
            }
        }
    }
}
