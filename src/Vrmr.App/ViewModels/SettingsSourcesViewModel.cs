using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using VRMR.App.Composition;
using VRMR.App.Services;

namespace VRMR.App.ViewModels
{
    /// <summary>Settings → Sources: which sources the app listens to, each with what it finds right now and what to do when it finds nothing.</summary>
    public partial class SettingsSourcesViewModel : ObservableObject
    {
        readonly AppHost _host;
        IDisposable _timer;

        public SettingsSourcesViewModel(AppHost host)
        {
            _host = host;
            var s = host.Settings;
            _steamVrOn = s.EnableOpenVR; _fingerTracking = s.CaptureFingers; _legacyPort = s.ListenLegacyPort;
            host.Environment.Changed += Refresh;
            Refresh();
        }

        [ObservableProperty] bool _steamVrOn;
        [ObservableProperty] bool _fingerTracking;
        [ObservableProperty] bool _legacyPort;
        [ObservableProperty] bool _showFingerState;

        public StatusRowViewModel SteamVr { get; } = new StatusRowViewModel();
        public StatusRowViewModel Fingers { get; } = new StatusRowViewModel();
        public StatusRowViewModel VrChat { get; } = new StatusRowViewModel();
        public StatusRowViewModel LegacyPortState { get; } = new StatusRowViewModel();
        public StatusRowViewModel Face { get; } = new StatusRowViewModel();

        /// <summary>The app was started without live sources: the switches are saved, the states come from what is installed.</summary>
        public bool CaptureOff => !_host.CaptureOn;

        /// <summary>"Also listen on UDP 9001", with the port from the settings.</summary>
        public string LegacyPortLabel => $"Also listen on UDP {_host.Settings.LegacyPort}";

        partial void OnSteamVrOnChanged(bool value)
        {
            _host.SettingsStore.Update(nameof(Settings.EnableOpenVR), s => s.EnableOpenVR = value);
            RestartSteamVr();
        }

        partial void OnFingerTrackingChanged(bool value)
        {
            _host.SettingsStore.Update(nameof(Settings.CaptureFingers), s => s.CaptureFingers = value);
            if (SteamVrOn) RestartSteamVr();
            else Refresh();
        }

        partial void OnLegacyPortChanged(bool value)
        {
            _host.SettingsStore.Update(nameof(Settings.ListenLegacyPort), s => s.ListenLegacyPort = value);
            Refresh();
        }

        /// <summary>Applies the SteamVR switches now. Without live sources there is nothing to restart.</summary>
        void RestartSteamVr()
        {
            if (_host.CaptureOn) _host.Recorder.RestartOpenVR();
            Refresh();
        }

        /// <summary>While the page is on screen the live states are read once a second; nothing runs while it is closed.</summary>
        public void SetActive(bool active)
        {
            if (active && _host.CaptureOn) _timer ??= _host.Ui.Every(TimeSpan.FromSeconds(1), Refresh);
            else { _timer?.Dispose(); _timer = null; }
            if (active) Refresh();
        }

        /// <summary>Reads the states again.</summary>
        public void Refresh()
        {
            var live = _host.CaptureOn ? SourceStatus.Read(_host.Live) : null;
            var env = _host.Environment;
            var recorder = _host.Recorder;

            // SteamVR: live once connected, else what is installed
            var steam = live?.FirstOrDefault(x => x.Id == SourceStatus.SteamVr);
            bool connected = _host.CaptureOn && _host.Live.SteamVrConnected;
            if (!SteamVrOn) SteamVr.Set(SourceLevel.Off, "Switched off: takes hold face tracking and avatar parameters, and no body motion from SteamVR.");
            else if (connected && steam != null) SteamVr.Set(steam);
            else
            {
                var item = env.Item(EnvironmentProbe.SteamVR);
                if (item == null) SteamVr.Set((ProbeItem)null);
                else if (item.State == ProbeState.Ok && env.Report.SteamVR.Running && _host.CaptureOn) SteamVr.Set(SourceLevel.Waiting, "SteamVR is running; connecting…");
                else SteamVr.Set(item.State == ProbeState.Ok ? SourceLevel.Waiting : SourceLevel.Warning, item.Summary, item.Action);
            }

            ShowFingerState = SteamVrOn && FingerTracking && connected;
            if (ShowFingerState)
            {
                string input = recorder.OpenVR?.InputStatus ?? "";
                bool hands = input.StartsWith("fingers:", StringComparison.Ordinal);
                Fingers.Set(hands ? SourceLevel.Ok : SourceLevel.Waiting,
                    hands ? "Receiving finger tracking." : input == "waiting for hands" ? "Waiting for controllers that track fingers." : "Not receiving finger tracking: " + input + ".",
                    hands || input == "waiting for hands" ? "" : "Restart SteamVR, then switch finger tracking off and on here.");
            }

            // VRChat: live when something arrives, else VRChat's own OSC switch, else whether VRChat was ever run
            var vrc = live?.FirstOrDefault(x => x.Id == SourceStatus.VrChat);
            if (vrc != null && vrc.Level != SourceLevel.Waiting) VrChat.Set(vrc);
            else
            {
                var installed = env.Item(EnvironmentProbe.VrChat);
                var osc = env.Item(EnvironmentProbe.VrChatOsc);
                if (installed == null || osc == null) VrChat.Set((ProbeItem)null);
                else if (installed.State != ProbeState.Ok) VrChat.Set(installed);
                else if (osc.State == ProbeState.Ok) VrChat.Set(_host.CaptureOn ? SourceLevel.Waiting : SourceLevel.Ok, osc.Summary, _host.CaptureOn ? "Start VRChat: the app finds it by itself." : "");
                else VrChat.Set(osc.State == ProbeState.Missing ? SourceLevel.Warning : SourceLevel.Off, osc.Summary, osc.Action);
            }

            int port = _host.Settings.LegacyPort;
            if (!LegacyPort) LegacyPortState.Set(SourceLevel.Off, $"Not listening on UDP {port}: VRChat is found through OSCQuery only.");
            else if (!_host.CaptureOn) LegacyPortState.Set(SourceLevel.Off, $"UDP {port} is used from the next normal start.");
            else if (recorder.LegacyRx != null) LegacyPortState.Set(SourceLevel.Ok, $"Listening on UDP {port}.");
            else LegacyPortState.Set(SourceLevel.Waiting, $"UDP {port} is not open: another program uses it, or the app was started with this switch off.",
                "Nothing to do when VRChat shows as receiving: OSCQuery is enough. Otherwise close the other program, or restart the app after switching this on.");

            var face = live?.FirstOrDefault(x => x.Id == SourceStatus.Face);
            if (face != null) Face.Set(face);
            else Face.Set(SourceLevel.Off, "Face tracking arrives through VRChat's OSC with the avatar parameters.", "Nothing to set up here: run VRCFaceTracking with an avatar that supports it.");
        }
    }
}
