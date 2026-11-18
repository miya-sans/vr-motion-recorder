using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace VRMR.App.Services
{
    /// <summary>What a take can be calibrated from.</summary>
    public enum CalibrationChoiceKind
    {
        /// <summary>The best T-pose found in the take.</summary>
        BestTPose,

        /// <summary>A moment the user picked.</summary>
        Moment,

        /// <summary>Every VRChat calibration in the take, as it happened.</summary>
        VrcCalibrations,

        /// <summary>Only the one VRChat calibration found in the take.</summary>
        VrcCalibrationInTake,

        /// <summary>A calibration from the library.</summary>
        Library,

        /// <summary>A calibration the take carries a copy of that is no longer in the library.</summary>
        Linked
    }

    /// <summary>One entry of the calibration source list.</summary>
    public sealed class CalibrationChoice
    {
        public CalibrationChoiceKind Kind { get; init; }
        public string Label { get; init; } = "";
        public CalibrationSnapshot Snapshot { get; init; }

        /// <summary>Library entries: every tracker of the take is in the calibration.</summary>
        public bool Matches { get; init; }

        public override string ToString()
        {
            return Label;
        }
    }

    /// <summary>A line about the chosen calibration source: a short text, the whole story, and whether it warns.</summary>
    public readonly record struct CalibrationNote(string Short, string Full, bool Warning);

    /// <summary>
    ///     Calibration as the inspector needs it: where a take's calibration comes from (its own T-pose, a moment, VRChat's
    ///     calibrations, the library), the settings it is solved with (the take's own or the defaults), presets, capturing
    ///     a calibration from the devices, and VRChat setting changes added by hand.
    /// </summary>
    public sealed class CalibrationService
    {
        // ------------------------------------------------------------------------------------------ VRChat's settings in the take

        /// <summary>The kinds of VRChat setting change that can be added by hand.</summary>
        public static readonly string[] VrcChangeKinds =
            { "Lock Hip", "Lock Head", "Lock Both", "By arm span", "By height" };

        readonly PlaybackClock _clock;
        readonly LiveService _live;
        readonly TakeSession _session;
        readonly SettingsStore _settings;
        readonly CalibrationLibrary _snapshots;
        readonly StatusService _status;
        readonly IUiDispatcher _ui;
        IDisposable _countdown;
        double _inTake = -1;
        Recording _inTakeFor;
        string _inTakeText = "";
        List<CalibrationSnapshot> _library = new();
        int _libraryRun;

        public CalibrationService(TakeSession session, SettingsStore settings, LiveService live, StatusService status,
            PlaybackClock clock, IUiDispatcher ui)
        {
            _snapshots = live.Recorder.Calibrations;
            _session = session;
            _settings = settings;
            _live = live;
            _status = status;
            _clock = clock;
            _ui = ui;
            _session.TakeChanged += RefreshLibrary;
            _snapshots.Changed += () => _ui.Post(RefreshLibrary);
            var watcher = _live.Recorder.Calib;
            if (watcher != null) watcher.Captured += s => _ui.Post(RefreshLibrary);
            RefreshLibrary();
        }

        /// <summary>Seconds left of a capture countdown, or 0.</summary>
        public int CaptureCountdown { get; private set; }

        // ------------------------------------------------------------------------------------------ settings

        /// <summary>The settings the controls show and edit: the take's own, else the defaults.</summary>
        public CalibrationSettings Shown => _session.Take?.Edits.Calib ?? _settings.Value.CalibrationDefaults;

        /// <summary>The defaults: used by every take without settings of its own.</summary>
        public CalibrationSettings Defaults => _settings.Value.CalibrationDefaults;

        /// <summary>True when the open take has settings of its own.</summary>
        public bool TakeHasOwnSettings => _session.Take?.Edits.Calib != null;

        /// <summary>The source list or the settings shown may have changed.</summary>
        public event Action Changed;

        /// <summary>Changes the defaults (the Settings page), whatever take is open.</summary>
        public void EditDefaults(Action<CalibrationSettings> change)
        {
            _settings.Update(nameof(Settings.CalibrationDefaults), s => change(s.CalibrationDefaults));
            Changed?.Invoke();
        }

        /// <summary>
        ///     Applies a change from a calibration control. With a take open the take gets its own copy of the settings
        ///     (the raw data stays solvable with any settings); without one the defaults change.
        /// </summary>
        public void EditSettings(Action<CalibrationSettings> change)
        {
            if (_session.Take == null)
            {
                _settings.Update(nameof(Settings.CalibrationDefaults), s => change(s.CalibrationDefaults));
                Changed?.Invoke();
                return;
            }

            var defaults = _settings.Value.CalibrationDefaults;
            _session.Edit("calibration", e =>
            {
                e.Calib = (e.Calib ?? defaults).Clone();
                change(e.Calib);
            });
            Changed?.Invoke();
        }

        /// <summary>A starting value when a height mode that needs one is chosen for the first time.</summary>
        public static float SuggestedHeightValue(CalHeightMode mode)
        {
            return mode == CalHeightMode.RealHeight ? 1.70f :
                mode == CalHeightMode.EyeHeight ? 1.58f :
                mode == CalHeightMode.Scale ? 1f : 0f;
        }

        /// <summary>The shown settings become the defaults for every take without its own.</summary>
        public void MakeDefault()
        {
            var copy = Shown.Clone();
            _settings.Update(nameof(Settings.CalibrationDefaults), s => s.CalibrationDefaults = copy);
            if (_session.Take?.Edits.Calib != null)
                _session.Edit("calibration", e => e.Calib = null, false); // identical to the defaults now
            _status.Info("These calibration settings are now the defaults");
            Changed?.Invoke();
        }

        /// <summary>Drops the take's own settings: it follows the defaults again.</summary>
        public void UseDefaults()
        {
            if (_session.Take?.Edits.Calib == null) return;
            _session.Edit("calibration", e => e.Calib = null);
            Changed?.Invoke();
        }

        /// <summary>Whose settings are shown, in one line.</summary>
        public string SettingsOwnerText()
        {
            var take = _session.Take;
            if (take == null) return "No take is open: these are the defaults for every take.";
            if (take.Edits.Calib == null)
                return "This take uses the defaults. Changing anything gives it settings of its own.";
            return take.Edits.Calib.SameAs(_settings.Value.CalibrationDefaults)
                ? "This take has its own settings (the same as the defaults)."
                : "This take has its own settings.";
        }

        // ------------------------------------------------------------------------------------------ presets

        public IReadOnlyList<string> Presets()
        {
            try { return CalibrationPresets.List(); }
            catch { return Array.Empty<string>(); }
        }

        public bool LoadPreset(string name)
        {
            try
            {
                var c = CalibrationPresets.Load(name);
                if (_session.Take == null)
                    _settings.Update(nameof(Settings.CalibrationDefaults), s => s.CalibrationDefaults = c);
                else _session.Edit("calibration", e => e.Calib = c);
                Changed?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                _status.Error($"Could not load the preset \"{name}\": {ex.Message}");
                return false;
            }
        }

        /// <summary>Saves the shown settings under a name (replacing a preset of that name).</summary>
        public bool SavePreset(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return false;
            try
            {
                CalibrationPresets.Save(name, Shown);
                _status.Info($"Saved the preset \"{name}\"");
                Changed?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                _status.Error(
                    $"Could not save the preset \"{name}\": {ex.Message}. Names cannot contain characters such as \\ / : * ? \" < > |.");
                return false;
            }
        }

        public void DeletePreset(string name)
        {
            try
            {
                CalibrationPresets.Delete(name);
                _status.Info($"Moved the preset \"{name}\" to the Recycle Bin");
                Changed?.Invoke();
            }
            catch (Exception ex) { _status.Error($"Could not delete the preset \"{name}\": {ex.Message}"); }
        }

        // ------------------------------------------------------------------------------------------ sources

        /// <summary>Reads the calibration library again in the background.</summary>
        public void RefreshLibrary()
        {
            int run = ++_libraryRun;
            Task.Run(() =>
            {
                List<CalibrationSnapshot> list;
                try { list = _snapshots.List(); }
                catch (Exception ex)
                {
                    AppLog.Warn("Calibration library: " + ex.Message);
                    list = new List<CalibrationSnapshot>();
                }

                _ui.Post(() =>
                {
                    if (run != _libraryRun) return;
                    _library = list;
                    Changed?.Invoke();
                });
            });
        }

        void EnsureInTake(Recording r)
        {
            if (_inTakeFor == r) return;
            _inTakeFor = r;
            _inTake = TakeCalibrations.FindInTake(r, out _inTakeText);
        }

        /// <summary>What the open take can be calibrated from, and which entry it uses now.</summary>
        public List<CalibrationChoice> Sources(out int selected)
        {
            var list = new List<CalibrationChoice>();
            selected = 0;
            var r = _session.Take;
            if (r == null) return list;
            EnsureInTake(r);
            var inv = CultureInfo.InvariantCulture;
            list.Add(new CalibrationChoice
                { Kind = CalibrationChoiceKind.BestTPose, Label = "Best T-pose in this take" });
            if (r.Edits.CalibrationTime >= 0)
                list.Add(new CalibrationChoice
                {
                    Kind = CalibrationChoiceKind.Moment, Label = $"Moment at {TimeText.Format(r.Edits.CalibrationTime)}"
                });
            var tl = VrcTimeline.For(r);
            if (tl.Calibrations.Count > 0)
                list.Add(new CalibrationChoice
                {
                    Kind = CalibrationChoiceKind.VrcCalibrations,
                    Label = tl.Calibrations.Count == 1
                        ? "VRChat's calibration in this take"
                        : $"VRChat's {tl.Calibrations.Count} calibrations in this take"
                });
            if (_inTake >= 0)
                list.Add(new CalibrationChoice
                {
                    Kind = CalibrationChoiceKind.VrcCalibrationInTake,
                    Label = $"Only VRChat's calibration at {TimeText.Format(_inTake)}"
                });
            var emb = r.Edits.Snapshot;
            string session = r.Extra.GetStr("steamvrSession", "");
            var ordered = _library.Select(sn =>
                {
                    sn.Compare(r, out var miss, out _);
                    return (sn, ok: miss.Count == 0);
                })
                .OrderByDescending(t => t.ok).ThenByDescending(t => t.sn.CreatedUtc).Take(40);
            foreach ((var sn, bool ok) in ordered)
            {
                string tags = (ok ? "" : "  (trackers differ)") +
                              (session.Length > 0 && sn.SteamVRSession.Length > 0 && sn.SteamVRSession != session
                                  ? "  (other SteamVR session)"
                                  : "");
                list.Add(new CalibrationChoice
                    { Kind = CalibrationChoiceKind.Library, Label = sn.Label + tags, Snapshot = sn, Matches = ok });
            }

            bool inLibrary = emb != null && _library.Any(x => x.Id == emb.Id);
            bool fromThisTake = emb != null && emb.FromTake.Length > 0 &&
                                emb.FromTake == Path.GetFileName(r.FilePath ?? "");
            if (emb != null && !inLibrary && !(fromThisTake && _inTake >= 0))
                list.Add(new CalibrationChoice
                {
                    Kind = CalibrationChoiceKind.Linked, Label = emb.Label + "  (copy kept in the take)", Snapshot = emb
                });

            int Find(Func<CalibrationChoice, bool> f)
            {
                return list.FindIndex(s => f(s));
            }

            if (PoseBuilder.FollowsVrcCalibrations(r, tl))
                selected = Find(s => s.Kind == CalibrationChoiceKind.VrcCalibrations);
            else if (r.Edits.CalibrationTime == EditList.UseVrcTimeline && emb != null)
                selected = Find(s => s.Snapshot != null && s.Snapshot.Id == emb.Id);
            else if (r.Edits.CalibrationTime == EditList.UseSnapshot && emb != null)
            {
                selected = Find(s => s.Kind == CalibrationChoiceKind.Library && s.Snapshot.Id == emb.Id);
                if (selected < 0 && fromThisTake)
                    selected = Find(s => s.Kind == CalibrationChoiceKind.VrcCalibrationInTake);
                if (selected < 0) selected = Find(s => s.Snapshot == emb);
            }
            else if (r.Edits.CalibrationTime >= 0) selected = Find(s => s.Kind == CalibrationChoiceKind.Moment);

            if (selected < 0) selected = 0;
            return list;
        }

        /// <summary>Makes the take use a calibration source.</summary>
        public void Pick(CalibrationChoice source)
        {
            var r = _session.Take;
            if (r == null || source == null || source.Kind == CalibrationChoiceKind.Moment) return;
            EnsureInTake(r);
            double inTake = _inTake;
            string inTakeText = _inTakeText;
            _session.Edit("calibration", e =>
            {
                switch (source.Kind)
                {
                    case CalibrationChoiceKind.BestTPose: e.CalibrationTime = -1; break;
                    case CalibrationChoiceKind.VrcCalibrations:
                        // keep a linked calibration from before the take (in effect when recording started); else the library's latest one before it
                        if (e.Snapshot == null || PoseBuilder.SnapshotTakeTime(r, e.Snapshot) >= 0)
                            e.Snapshot = _snapshots.BestFor(r, _library, before: r.CreatedUtc.AddSeconds(0.5))
                                ?.Clone();
                        e.CalibrationTime = EditList.UseVrcTimeline;
                        break;
                    case CalibrationChoiceKind.VrcCalibrationInTake:
                        var sn = CalibrationSnapshot.FromTakeMoment(r, inTake, inTakeText);
                        sn.Source = "vrchat";
                        e.Snapshot = sn;
                        e.CalibrationTime = EditList.UseSnapshot;
                        break;
                    default:
                        e.Snapshot = source.Snapshot.Clone();
                        e.CalibrationTime = EditList.UseSnapshot;
                        break;
                }
            });
            Changed?.Invoke();
        }

        /// <summary>Calibrate at the playhead (a T-pose moment in this take).</summary>
        public void UseMomentAtPlayhead()
        {
            if (_session.Take == null) return;
            double t = _clock.Playhead;
            _session.Edit("calibration", e => e.CalibrationTime = t);
            Changed?.Invoke();
        }

        /// <summary>Calibrate at the best T-pose found in the take.</summary>
        public void FindTPose()
        {
            if (_session.Take == null) return;
            _session.Edit("calibration", e => e.CalibrationTime = -1);
            Changed?.Invoke();
        }

        /// <summary>What to tell about the chosen source.</summary>
        public CalibrationNote Describe(CalibrationChoice source)
        {
            var r = _session.Take;
            if (r == null || source == null) return new CalibrationNote("", "", false);
            switch (source.Kind)
            {
                case CalibrationChoiceKind.BestTPose:
                    return new CalibrationNote("",
                        "Calibrates at the best T-pose in this take: arms out, looking ahead, holding still.", false);
                case CalibrationChoiceKind.Moment:
                    return new CalibrationNote("", "The moment picked with Calibrate in the transport or At playhead.",
                        false);
                case CalibrationChoiceKind.VrcCalibrations:
                {
                    var linked = r.Edits.Snapshot;
                    bool before = linked != null && PoseBuilder.SnapshotTakeTime(r, linked) < 0;
                    string s = before
                        ? $"\"{linked.Label}\" until the first recalibration, then each VRChat calibration."
                        : "Each VRChat calibration in the take, as it happened.";
                    string full =
                        "Like VRChat: " + (before
                                            ? $"\"{linked.Label}\" (in effect when recording started) until the first recalibration, then"
                                            : "the first calibration in the take is also used before it, then")
                                        + " each calibration from its \"Calibrate FBT\" click on. VRChat hides the avatar until you confirm; you stand in the calibration pose there, so the new calibration already fits.";
                    return new CalibrationNote(s, full, false);
                }
            }

            var sn = source.Snapshot ??
                     (source.Kind == CalibrationChoiceKind.VrcCalibrationInTake ? r.Edits.Snapshot : null);
            if (sn == null) return new CalibrationNote(_inTakeText, _inTakeText, false);
            sn.Compare(r, out var missing, out _);
            string session = r.Extra.GetStr("steamvrSession", "");
            var age = r.CreatedUtc - sn.CreatedUtc;
            string when = Math.Abs(age.TotalMinutes) < 1 ? "at the start of this take"
                : age.TotalSeconds > 0 ? age.TotalHours >= 1
                    ? $"{age.TotalHours:0.#} h before this take"
                    : $"{age.TotalMinutes:0} min before this take"
                : $"{-age.TotalSeconds:0} s into this take";
            var lines = new List<string> { sn.Detail.Length > 0 ? sn.Detail : sn.Source };
            lines.Add($"{sn.Devices.Count} devices, {when}" +
                      (sn.Vrc.PlayerHeight > 0.5f ? $", VRChat height {sn.Vrc.PlayerHeight * 100:0} cm" : "") +
                      (string.IsNullOrEmpty(sn.AvatarName) ? "" : $", avatar {sn.AvatarName}"));
            var warnings = new List<string>();
            if (missing.Count > 0)
                warnings.Add(
                    $"{(missing.Count == 1 ? "Tracker" : "Trackers")} {string.Join(", ", missing)} {(missing.Count == 1 ? "is" : "are")} not in this calibration and will not drive the body.");
            else lines.Add("Its trackers match this take.");
            if (session.Length > 0 && sn.SteamVRSession.Length > 0 && sn.SteamVRSession != session)
                warnings.Add(
                    "From another SteamVR session: if the room setup or the tracker straps changed since, calibrate again.");
            else if ((session.Length == 0 || sn.SteamVRSession.Length == 0) && age.TotalHours > 2)
                warnings.Add("Taken hours apart: the tracker straps have probably moved since.");
            string shortText = $"{sn.Devices.Count} devices, {when}" +
                               (warnings.Count > 0 ? "\n" + string.Join("\n", warnings) : "");
            return new CalibrationNote(shortText, string.Join("\n", lines.Concat(warnings)), warnings.Count > 0);
        }

        // ------------------------------------------------------------------------------------------ library actions

        /// <summary>Saves the calibration this take uses to the library, so other takes can use it.</summary>
        public void SaveTakeCalibration()
        {
            var r = _session.Take;
            if (r == null) return;
            try
            {
                CalibrationSnapshot sn;
                if (r.Edits.CalibrationTime == EditList.UseSnapshot && r.Edits.Snapshot != null)
                    sn = r.Edits.Snapshot.Clone();
                else
                {
                    var clip = _session.Clip;
                    double t = clip?.Calibration != null && clip.Calibration.Time >= 0
                        ? clip.Calibration.Time
                        : r.Edits.CalibrationTime;
                    if (t < 0)
                    {
                        _status.Notify(
                            "This take has no calibration moment yet (no T-pose was found). Move the playhead to a T-pose and choose At playhead.",
                            StatusKind.Warning);
                        return;
                    }

                    sn = CalibrationSnapshot.FromTakeMoment(r, t, $"moment {t:F2} s of take {r.Name}");
                }

                if (_library.Any(x => x.Id == sn.Id))
                {
                    _status.Info("This calibration is already in the library");
                    return;
                }

                _snapshots.Save(sn);
                _status.Info($"Saved \"{sn.Label}\" to the calibration library");
                RefreshLibrary();
            }
            catch (Exception ex) { _status.Error("Could not save the calibration: " + ex.Message); }
        }

        /// <summary>Moves a library calibration to the Recycle Bin. Takes that use it keep their own copy.</summary>
        public void DeleteFromLibrary(CalibrationSnapshot snapshot)
        {
            if (snapshot == null) return;
            try
            {
                _snapshots.Delete(snapshot.Id);
                _status.Info($"Moved \"{snapshot.Label}\" to the Recycle Bin");
            }
            catch (Exception ex) { _status.Error("Could not delete the calibration: " + ex.Message); }

            RefreshLibrary();
        }

        /// <summary>Why a calibration cannot be captured from the devices right now, or null when it can.</summary>
        public string CaptureProblem()
        {
            if (!_live.CaptureOn) return "Live sources are off in this session.";
            if (_live.Recorder.Calib == null)
                return
                    "Saving calibrations is switched off. Turn on \"Save every VRChat calibration\" under Settings → Recording.";
            if (!_live.SteamVrConnected)
                return "SteamVR is not connected. Start SteamVR and switch on your trackers first.";
            return null;
        }

        /// <summary>
        ///     After a five second countdown, saves the devices' poses as a calibration: stand straight, look ahead, arms
        ///     out.
        /// </summary>
        public void CaptureNow()
        {
            if (_countdown != null) return;
            string problem = CaptureProblem();
            if (problem != null)
            {
                _status.Notify(problem, StatusKind.Warning);
                return;
            }

            CaptureCountdown = 5;
            Changed?.Invoke();
            _countdown = _ui.Every(TimeSpan.FromSeconds(1), () =>
            {
                CaptureCountdown--;
                if (CaptureCountdown > 0)
                {
                    Changed?.Invoke();
                    return;
                }

                _countdown?.Dispose();
                _countdown = null;
                try
                {
                    var sn = _live.Recorder.Calib.CaptureNow("captured with Capture now");
                    _status.Notify($"Saved the calibration \"{sn.Label}\"", StatusKind.Success);
                    RefreshLibrary();
                }
                catch (Exception ex) { _status.Error("Could not capture a calibration: " + ex.Message); }

                Changed?.Invoke();
            });
        }

        /// <summary>VRChat's spine mode and measurement over the take, in words.</summary>
        public string VrcSettingsText()
        {
            return _session.Take == null ? "" : VrcTimeline.For(_session.Take).Describe();
        }

        /// <summary>Records that a VRChat setting changed at the playhead (for takes recorded before the app logged them).</summary>
        public void AddVrcChange(int kind)
        {
            if (_session.Take == null || kind < 0 || kind >= VrcChangeKinds.Length) return;
            var change = new VrcChange { T = _clock.Playhead };
            if (kind <= 2) change.Spine = kind;
            else change.Measure = kind == 3 ? 0 : 1;
            _session.Edit("vrcChanges", e => e.VrcChanges.Add(change));
            Changed?.Invoke();
        }

        public void ClearVrcChanges()
        {
            if (_session.Take == null || _session.Take.Edits.VrcChanges.Count == 0) return;
            _session.Edit("vrcChanges", e => e.VrcChanges.Clear());
            Changed?.Invoke();
        }

        /// <summary>Whether VRChat calibrations are being watched for, and VRChat's own settings as they are now.</summary>
        public string WatchText()
        {
            var watcher = _live.Recorder.Calib;
            if (!_live.CaptureOn) return "";
            if (watcher == null) return "VRChat calibrations are not being saved (Settings → Recording).";
            string t = "Watching for VRChat calibrations: " + watcher.Status;
            var v = VrcSettings.Read();
            if (v.Found)
                t +=
                    $"\nVRChat now: height {v.PlayerHeight * 100:0} cm, measured {VrcTimeline.MeasureName(v.MeasurementType)}, spine {VrcTimeline.SpineName(v.SpineMode)}" +
                    (v.OneHandedCalibration ? ", one-handed calibration" : "");
            return t;
        }
    }
}
