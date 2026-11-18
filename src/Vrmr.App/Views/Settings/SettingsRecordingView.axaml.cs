using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VRMR.App.ViewModels;

namespace VRMR.App.Views.Settings
{
    using Key = Avalonia.Input.Key;

    /// <summary>Settings → Recording. The hotkey can be typed, or set by pressing it once "Press to set" is on.</summary>
    public partial class SettingsRecordingView : UserControl
    {
        const string CaptureIdle = "Press to set", CaptureArmed = "Press the keys now…";

        public SettingsRecordingView()
        {
            InitializeComponent();
            // before the button itself sees the key: Space and Enter are keys to capture, not clicks
            CaptureToggle.AddHandler(KeyDownEvent, OnCaptureKeyDown, RoutingStrategies.Tunnel);
            CaptureToggle.IsCheckedChanged += (o, e) => CaptureToggle.Content = CaptureToggle.IsChecked == true ? CaptureArmed : CaptureIdle;
            CaptureToggle.LostFocus += (o, e) => CaptureToggle.IsChecked = false;
        }

        void OnCaptureKeyDown(object sender, KeyEventArgs e)
        {
            if (CaptureToggle.IsChecked != true) return;
            // Tab moves on and Esc gives up: neither becomes the hotkey
            if (e.Key == Key.Tab || e.Key == Key.Escape) { CaptureToggle.IsChecked = false; return; }
            e.Handled = true;
            string text = HotkeyKeys.Text(e.Key, e.KeyModifiers);
            if (text == null) return; // Ctrl, Shift or Alt alone: the key is still to come
            CaptureToggle.IsChecked = false;
            if (DataContext is SettingsRecordingViewModel vm) vm.Hotkey = text;
        }
    }
}
