using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VRMR.App.Composition;
using VRMR.App.Services;

namespace VRMR.App.ViewModels
{
    /// <summary>Settings → Recording: what starts and stops a recording, and what happens around one.</summary>
    public partial class SettingsRecordingViewModel : ObservableObject
    {
        /// <summary>The hotkey of a fresh install.</summary>
        public const string DefaultHotkey = "F9";

        readonly AppHost _host;
        [ObservableProperty] bool _followHumr;

        /// <summary>The global hotkey as text ("F9", "Ctrl+Shift+R"); empty for none.</summary>
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(HotkeyChanged))]
        string _hotkey;

        [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasHotkeyProblem))]
        string _hotkeyProblem = "";

        [ObservableProperty] bool _previewWhileRecording;
        [ObservableProperty] bool _saveCalibrations;
        [ObservableProperty] string _triggerParameter;
        [ObservableProperty] bool _useLatestCalibration;

        public SettingsRecordingViewModel(AppHost host)
        {
            _host = host;
            var s = host.Settings;
            _hotkey = s.HotkeyRecord ?? "";
            _triggerParameter = s.RecordTriggerParam ?? "";
            _followHumr = s.FollowHumrTakes;
            _previewWhileRecording = s.PreviewWhileRecording;
            _saveCalibrations = s.WatchCalibrations;
            _useLatestCalibration = s.AutoLinkCalibration;
            // registering the hotkey reports through the controller's state event
            host.Recording.StateChanged += UpdateHotkeyProblem;
            UpdateHotkeyProblem();
        }

        public bool HotkeyChanged => Hotkey != DefaultHotkey;
        public bool HasHotkeyProblem => !string.IsNullOrEmpty(HotkeyProblem);

        /// <summary>When the hotkey works, for the line under the field.</summary>
        public string HotkeyNote => _host.CaptureOn
            ? "Works while VRChat or any other program has the focus. Leave it empty to record with the button only."
            : "The hotkey is off in this session (the app was started with --no-capture); it is saved for the next normal start.";

        partial void OnHotkeyChanged(string value)
        {
            string text = (value ?? "").Trim();
            if (text != value)
            {
                Hotkey = text;
                return;
            }

            _host.SettingsStore.Update(nameof(Settings.HotkeyRecord), s => s.HotkeyRecord = text);
            UpdateHotkeyProblem();
        }

        partial void OnTriggerParameterChanged(string value)
        {
            string text = (value ?? "").Trim();
            if (text != value)
            {
                TriggerParameter = text;
                return;
            }

            _host.SettingsStore.Update(nameof(Settings.RecordTriggerParam), s => s.RecordTriggerParam = text);
        }

        partial void OnFollowHumrChanged(bool value)
        {
            _host.SettingsStore.Update(nameof(Settings.FollowHumrTakes), s => s.FollowHumrTakes = value);
        }

        partial void OnPreviewWhileRecordingChanged(bool value)
        {
            _host.SettingsStore.Update(nameof(Settings.PreviewWhileRecording), s => s.PreviewWhileRecording = value);
        }

        partial void OnUseLatestCalibrationChanged(bool value)
        {
            _host.SettingsStore.Update(nameof(Settings.AutoLinkCalibration), s => s.AutoLinkCalibration = value);
        }

        partial void OnSaveCalibrationsChanged(bool value)
        {
            _host.SettingsStore.Update(nameof(Settings.WatchCalibrations), s => s.WatchCalibrations = value);
            if (_host.Recorder.Calib != null) _host.Recorder.Calib.Enabled = value;
        }

        [RelayCommand]
        void ResetHotkey()
        {
            Hotkey = DefaultHotkey;
        }

        /// <summary>Why the hotkey does not work: taken by another program (the controller knows), or not a key at all.</summary>
        void UpdateHotkeyProblem()
        {
            string problem = _host.Recording.HotkeyProblem;
            if (problem == null && Hotkey.Length > 0 && !HotkeyText.TryParse(Hotkey, out _, out _))
                problem =
                    $"\"{Hotkey}\" is not a key. Use a key name such as F9, or a combination such as Ctrl+Shift+R.";
            HotkeyProblem = problem ?? "";
        }
    }
}
