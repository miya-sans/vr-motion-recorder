using System;
using System.Collections.Generic;
using System.Linq;

namespace VRMR.App.Services
{
    /// <summary>A system-wide hotkey (works while another program has the focus).</summary>
    public interface IGlobalHotkey : IDisposable
    {
        /// <summary>Registers the one hotkey (replacing the previous one). False when the keys are taken by another program.</summary>
        bool Register(uint modifiers, uint virtualKey, Action pressed);
        void Unregister();
    }

    /// <summary>Reads hotkey text such as "F9" or "Ctrl+Shift+R".</summary>
    public static class HotkeyText
    {
        public const uint Alt = 1, Ctrl = 2, Shift = 4, Win = 8;

        static readonly Dictionary<string, uint> Named = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["Space"] = 0x20, ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24, ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
            ["PrintScreen"] = 0x2C, ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Pause"] = 0x13, ["Scroll"] = 0x91, ["ScrollLock"] = 0x91, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Back"] = 0x08,
            ["Multiply"] = 0x6A, ["Add"] = 0x6B, ["Subtract"] = 0x6D, ["Decimal"] = 0x6E, ["Divide"] = 0x6F,
        };

        /// <summary>Parses the text into Win32 modifier flags and a virtual key code. False for text that names no key.</summary>
        public static bool TryParse(string text, out uint modifiers, out uint virtualKey)
        {
            modifiers = 0; virtualKey = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (var part in text.Split('+').Select(x => x.Trim()))
            {
                switch (part.ToLowerInvariant())
                {
                    case "ctrl": case "control": modifiers |= Ctrl; break;
                    case "alt": modifiers |= Alt; break;
                    case "shift": modifiers |= Shift; break;
                    case "win": modifiers |= Win; break;
                    default:
                        if (virtualKey != 0 || !TryKey(part, out virtualKey)) return false;
                        break;
                }
            }
            return virtualKey != 0;
        }

        static bool TryKey(string name, out uint vk)
        {
            vk = 0;
            if (name.Length == 1)
            {
                char c = char.ToUpperInvariant(name[0]);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) { vk = c; return true; }
                return false;
            }
            if ((name[0] == 'F' || name[0] == 'f') && int.TryParse(name.Substring(1), out int f) && f >= 1 && f <= 24) { vk = (uint)(0x70 + f - 1); return true; }
            if (name.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) && name.Length == 7 && char.IsDigit(name[6])) { vk = (uint)(0x60 + (name[6] - '0')); return true; }
            if (name.Length == 2 && (name[0] == 'D' || name[0] == 'd') && char.IsDigit(name[1])) { vk = name[1]; return true; } // "D1" as some tools write digits
            return Named.TryGetValue(name, out vk);
        }
    }

    /// <summary>
    /// Starts and stops recording: from the record button, the global hotkey, an avatar parameter toggled in VRChat,
    /// or the in-world HUMR recorder when following it is on.
    /// </summary>
    public sealed class RecordingController : IDisposable
    {
        readonly Recorder _recorder;
        readonly SettingsStore _settings;
        readonly LiveService _live;
        readonly TakeSession _session;
        readonly TakeLibrary _library;
        readonly StatusService _status;
        readonly IUiDispatcher _ui;
        readonly IGlobalHotkey _hotkey;
        IDisposable _humrPoll;

        public RecordingController(Recorder recorder, SettingsStore settings, LiveService live, TakeSession session, TakeLibrary library,
                                   StatusService status, IUiDispatcher ui, IGlobalHotkey hotkey)
        {
            _recorder = recorder; _settings = settings; _live = live; _session = session; _library = library; _status = status; _ui = ui; _hotkey = hotkey;
            if (!_live.CaptureOn) return;
            _recorder.RecordTriggerToggled += () => _ui.Post(Toggle);
            _recorder.HumrTakeStarted += t => _ui.Post(() =>
            {
                WatchInWorldTake();
                if (_settings.Value.FollowHumrTakes && !IsRecording) Start();
            });
            _recorder.HumrTakeEnded += t => _ui.Post(() => { if (_settings.Value.FollowHumrTakes && IsRecording) Stop(); });
            _settings.Changed += what => { if (what == nameof(Settings.HotkeyRecord)) RegisterHotkey(); };
            RegisterHotkey();
        }

        public bool IsRecording => _recorder.IsRecording;

        /// <summary>Seconds recorded so far.</summary>
        public double Elapsed => _recorder.RecordingTime;

        /// <summary>Frames of VRChat's own IK captured so far (a HUMR world), for the status line.</summary>
        public int VrcIkFrames => _recorder.Current?.Humr.Count ?? 0;

        /// <summary>True while the end of an in-world take is being watched for (a timer runs only then).</summary>
        public bool WatchingInWorldTake => _humrPoll != null;

        /// <summary>Why the hotkey does not work, or null.</summary>
        public string HotkeyProblem { get; private set; }

        /// <summary>Recording started or stopped.</summary>
        public event Action StateChanged;

        /// <summary>A recording started (the take being written).</summary>
        public event Action<Recording> Started;

        /// <summary>A recording finished and is on disk.</summary>
        public event Action<Recording> Stopped;

        /// <summary>
        /// The end of an in-world take is noticed by asking the recorder a few times a second. That only runs while such
        /// a take is arriving or a recording is on, not for as long as the log is followed.
        /// </summary>
        void WatchInWorldTake()
        {
            if (_humrPoll != null || _recorder.Log == null) return;
            _humrPoll = _ui.Every(TimeSpan.FromMilliseconds(250), () =>
            {
                _recorder.PollHumrTake();
                if (IsRecording || _recorder.Log?.HumrActive == true) return;
                _humrPoll?.Dispose(); _humrPoll = null;
            });
        }

        public void Toggle() { if (IsRecording) Stop(); else Start(); }

        public void Start()
        {
            if (IsRecording) return;
            if (!_live.CaptureOn)
            {
                _status.Notify("Recording is off in this session: the app was started with --no-capture.", StatusKind.Warning);
                return;
            }
            _ = _session.SaveIfDirtyAsync();
            var match = _live.CurrentMatch();
            Recording rec;
            try { rec = _recorder.StartRecording(match?.Entry?.Profile()); }
            catch (Exception ex)
            {
                AppLog.Error("Start recording: " + ex);
                _status.Show(new Toast { Text = $"Recording could not start: {ex.Message}. Check that the recordings folder exists and can be written (Settings → Folders).", Kind = StatusKind.Error, Duration = TimeSpan.FromSeconds(8) });
                return;
            }
            if (rec != null && match?.Entry != null)
                lock (rec.Sync) rec.Extra["avatarBase"] = new Dictionary<string, object> { ["id"] = match.Entry.Id, ["name"] = match.Entry.Label, ["how"] = match.How };
            _library.BeingRecorded = rec?.FilePath;
            WatchInWorldTake();
            StateChanged?.Invoke();
            if (rec != null) Started?.Invoke(rec);
        }

        public void Stop()
        {
            if (!IsRecording) return;
            Recording rec = null;
            try { rec = _recorder.StopRecording(); }
            catch (Exception ex) { AppLog.Error("Stop recording: " + ex); _status.Notify("The recording could not be finished: " + ex.Message, StatusKind.Error); }
            _library.BeingRecorded = null;
            StateChanged?.Invoke();
            _library.Refresh();
            if (rec == null) return;
            _status.Info($"Recorded {rec.Name} ({TimeText.Format(rec.Duration, false)})");
            Stopped?.Invoke(rec);
        }

        void RegisterHotkey()
        {
            if (_hotkey == null) return;
            string text = _settings.Value.HotkeyRecord;
            _hotkey.Unregister();
            HotkeyProblem = null;
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!HotkeyText.TryParse(text, out uint mods, out uint vk)) HotkeyProblem = $"\"{text}\" is not a key. Use a key name such as F9, or a combination such as Ctrl+Shift+R.";
            else if (!_hotkey.Register(mods, vk, () => _ui.Post(Toggle))) HotkeyProblem = $"{text} is already used by another program. Pick another hotkey.";
            if (HotkeyProblem != null) AppLog.Warn("Record hotkey: " + HotkeyProblem);
            StateChanged?.Invoke();
        }

        public void Dispose()
        {
            _humrPoll?.Dispose();
            _hotkey?.Dispose();
        }
    }
}
