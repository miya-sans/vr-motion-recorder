using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Windows.Forms;
using Microsoft.VisualBasic;

namespace VRMR.UI
{
    public sealed partial class MainForm
    {
        static readonly string[] RoleNames =
        {
            "Auto", "None", "Hips", "Chest", "LeftFoot", "RightFoot", "LeftKnee", "RightKnee", "LeftElbow", "RightElbow"
        };

        // ---------------------------------------------------------------- calibration library (snapshots)

        readonly List<object> _calSnapItems = new(); // "auto" | "moment" | "intake" | CalibrationSnapshot

        // ---------------------------------------------------------------- tracking loss (TrackerGaps)

        readonly List<TrackerGap> _gapItems = new();
        readonly List<PoseStream> _gapTrackerItems = new();
        FieldRow _calValueRow;
        int _captureCount;

        Timer _captureTimer;
        InfoLabel _clipDetail, _bakeNotes;
        double _inTakeCal = -1;
        string _inTakeCalText = "";
        Recording _inTakeFor;

        string _lastWatchText = "";

        // Clip tab: everything that belongs to the selected take. Body / Calibration / Fingers & Face, the rarely used
        // options in "Advanced", measurements and solver notes in "Details" (all remembered open or closed).
        FoldSection _secBody,
            _secCal,
            _secFace,
            _secNotes,
            _foldClipDetails,
            _foldTrackers,
            _foldGaps,
            _foldCalAdv,
            _foldCalLib,
            _foldVrc,
            _foldCalDetails;

        // ---------------------------------------------------------------- calibration settings (per clip / defaults / presets)

        /// <summary>The settings the calibration controls show and edit: the clip's own, else the global defaults.</summary>
        CalibrationSettings ShownCal =>
            _edit != null ? _edit.Edits.Calib ?? S.CalibrationDefaults : S.CalibrationDefaults;

        string SelectedPreset => _calPreset.SelectedIndex > 0 ? (string)_calPreset.SelectedItem : null;

        void BuildClipPage()
        {
            var p = _clipPage;
            _nameBox = p.Add(Box(""));
            Tip(_nameBox, "Take name");
            _nameBox.TextChanged += (o, e) =>
            {
                if (_edit != null && _nameBox.Focused)
                {
                    _edit.Name = _nameBox.Text;
                    _dirty = true;
                    _lib.Invalidate();
                }
            };
            _clipInfo = p.Add(new InfoLabel("No recording selected.") { Color = Theme.TextDim });
            _bakeInfo = p.Add(new InfoLabel() { Color = Theme.TextFaint });
            p.Row(
                Btn("Save  (Ctrl+S)", SaveEdits, DarkButton.Kind.Accent,
                    "Save the edits into the take (the raw data is never changed)"),
                Btn("Revert", () =>
                {
                    if (_edit?.FilePath != null) LoadRecording(_edit.FilePath, true);
                }, tip: "Reload the take as saved"));
            _foldClipDetails = p.Add(new FoldSection("Details", false, FoldSection.Kind.Sub, "clip.details"));
            _clipDetail = _foldClipDetails.Add(new InfoLabel() { Color = Theme.TextDim });
            _bakeNotes = _foldClipDetails.Add(new InfoLabel() { Color = Theme.TextFaint });

            // ---------------------------------------------------------------- body
            var b = _secBody = p.Fold("Body", true, "clip.body");
            _avatarPick = b.Field("Avatar", new DarkCombo(),
                    "The take is solved onto this avatar. Any humanoid in the library works; Auto = the base model of the avatar it was recorded with.")
                .Editor as DarkCombo;
            _avatarPick.SelectedIndexChanged += (o, e) => OnAvatarPicked();
            _avatarInfo = b.Add(new InfoLabel() { Color = Theme.TextFaint });
            b.Row(
                Btn("Link VRChat avatar", LinkTakeAvatar,
                    tip:
                    "The avatar this take was recorded with is the base model picked above: this take and every later take with the same VRChat avatar use it. For avatars whose name doesn't say which base model they are (public avatars, renamed edits)."),
                Btn("Unity model…", PickSkeletonForClip,
                    tip: "Use a humanoid model from a Unity project (its .fbx.meta) as this take's skeleton"));
            _bodySrc = b.Field("Body from", new DarkCombo(),
                    "Auto: the IK solve; VRChat's own IK (HUMR log) only for takes without SteamVR data.\nVRChat IK: the HUMR log, as a reference.\nIK solve: SteamVR devices + VRChat poses.")
                .Editor as DarkCombo;
            _bodySrc.Items.AddRange("Auto", "VRChat IK (HUMR)", "IK solve");
            _bodySrc.SelectedIndex = 0;
            _bodySrc.SelectedIndexChanged += (o, e) =>
            {
                if (_edit == null || _syncing) return;
                _edit.Edits.BodySource = new[] { "auto", "humr", "ik" }[_bodySrc.SelectedIndex];
                MarkDirty(true);
            };
            _locomotion = b.Add(Tip(new DarkCheck("Thumbstick locomotion and turning", true),
                "Moves and turns the body with VRChat's thumbstick locomotion, as in the world. Off: the body stays in the playspace."));
            _locomotion.CheckedChanged += (o, e) =>
            {
                if (_edit == null || _syncing) return;
                _edit.Edits.IncludeLocomotion = _locomotion.Checked;
                MarkDirty(true);
            };
            _jumps = b.Add(Tip(new DarkCheck("VRChat jumps"),
                "VRChat's jump button lifts the whole avatar in VRChat's world while you stand still. Found from the Grounded / VelocityY parameters (and VRChat's own IK); off takes the jump height out of the body, for VRChat's IK (HUMR) and the IK solve alike."));
            _jumps.CheckedChanged += (o, e) =>
            {
                if (_edit == null || _syncing) return;
                _edit.Edits.IncludeJumps = _jumps.Checked;
                MarkDirty(true);
            };
            _smooth = b.Add(Tip(new DarkSlider("Smoothing (all takes)", 0, 1, S.Smoothing, 0.05f),
                "Smooths the solved body. An app setting: it applies to every take."));
            _smooth.ValueChanged += (o, e) =>
            {
                S.Smoothing = _smooth.Value;
                MarkDirty(true);
            };

            _foldTrackers = b.Fold("Tracker roles", false, "clip.trackers");
            Tip(_foldTrackers,
                "Which body part each tracker is on. Auto: VRChat's own assignment when its IK is in the take, else found from how each tracker moves (shown after the solve).");
            _devInfo = _foldTrackers.Add(new InfoLabel() { Color = Theme.TextFaint });
            _devPanel = _foldTrackers.Add(new GridPanel { Columns = 1, RowH = 26 });

            _foldGaps = b.Fold("Tracking loss", false, "clip.gaps");
            Tip(_foldGaps,
                "Lying on a tracker, occlusion, a tracker that dies: found from SteamVR's tracking state and from missing, frozen or jumping poses, and repaired by carrying the tracker with its neighbour (knee with the foot, elbow with the hand, hips with the chest) into the recovered pose. Red on the timeline's Tracker loss lane; click one there to select it.");
            _gapInfo = _foldGaps.Add(new InfoLabel() { Color = Theme.TextDim });
            _gapList = _foldGaps.Add(new DarkCombo());
            _gapToggle = Btn("Keep as recorded", ToggleGap,
                tip: "Repair this stretch, or keep the tracker as it was recorded");
            _foldGaps.Row(Btn("Select on timeline", SelectGap), _gapToggle);
            _gapTracker = new DarkCombo();
            _foldGaps.Row(_gapTracker,
                Btn("Mark lost", MarkGapSelection,
                    tip:
                    "Mark the stretch selected on the timeline as lost for this tracker (repaired like a found loss)"),
                Btn("Clear marks", () =>
                {
                    if (_edit == null || _edit.Edits.TrackerGaps.Count == 0) return;
                    _edit.Edits.TrackerGaps.Clear();
                    TrackerGaps.Invalidate(_edit);
                    MarkDirty(true);
                    SyncGapsUI();
                }, tip: "Remove every mark you made in this take"));
            _gapList.SelectedIndexChanged += (o, e) =>
            {
                if (!_syncing) SyncGapToggle();
            };

            // ---------------------------------------------------------------- placement (MainForm.Placement.cs)
            BuildPlacementSection(p);

            // ---------------------------------------------------------------- calibration
            var c = _secCal = p.Fold("Calibration", true, "clip.cal");
            Tip(c,
                "Like VRChat: the headset and controllers need no calibration; the head and hands sit on them through fixed per-device offsets. Body trackers are bound to the avatar's T-pose at one calibration, hanging from the headset as in VRChat. Every VRChat calibration (Calibrate FBT, then the triggers) is saved to a library, and new takes use the latest matching one. Raw data is kept, so every change here re-solves without re-recording.");
            _calSnap = c.Field("From", new DarkCombo()).Editor as DarkCombo;
            _calSnap.SelectedIndexChanged += (o, e) => OnCalSnapPicked();
            _calSnapInfo = c.Add(new InfoLabel() { Color = Theme.TextFaint });
            _calMoment = c.Add(new InfoLabel() { Color = Theme.TextDim });
            c.Row(
                Btn("At playhead", SetCalibrationHere, tip: "Calibrate at the playhead (a T-pose moment in this take)"),
                Btn("Find T-pose", () =>
                {
                    if (_edit == null) return;
                    _edit.Edits.CalibrationTime = -1;
                    MarkDirty(true);
                    _timeline.Invalidate();
                    RefreshSnapList();
                }, tip: "Calibrate at the best T-pose found in this take (arms out, looking ahead, holding still)"));
            _calHeight = c.Field("Height", new DarkCombo(),
                "VRChat's \"User Real Height\". Measure: the headset height at the calibration moment, head tilt removed. My real height: eye height = height × " +
                CalibrationSettings.EyeToHeightRatio.ToString("0.00") +
                ". VRChat: read from VRChat's settings, stored with each calibration and take.").Editor as DarkCombo;
            _calHeight.Items.AddRange("Measure at calibration", "My real height", "My eye height", "Tracking scale",
                "VRChat's User Real Height");
            _calHeight.SelectedIndex = 0;
            _calHeight.SelectedIndexChanged += (o, e) => EditCal(x =>
            {
                x.Height = (CalHeightMode)_calHeight.SelectedIndex;
                if (x.Height != CalHeightMode.Measure && x.HeightValue <= 0)
                    x.HeightValue = SuggestedHeightValue(x.Height);
            });
            _calValue = Box("0");
            _calValueRow = c.Field("Value", _calValue);
            _calValue.Leave += (o, e) =>
            {
                if (TryF(_calValue.Text, out float v)) EditCal(x => x.HeightValue = Math.Max(0, v));
            };
            _calValue.KeyDown += (o, e) =>
            {
                if (e.KeyCode == Keys.Enter && TryF(_calValue.Text, out float v))
                {
                    EditCal(x => x.HeightValue = Math.Max(0, v));
                    e.SuppressKeyPress = true;
                }
            };
            _calLock = c.Field("Spine", new DarkCombo(),
                    "With a hip tracker. Lock head: head on the headset, hips may leave the tracker. Lock hips: hips on the tracker, head may leave the headset. Lock both: the spine bends to keep both.")
                .Editor as DarkCombo;
            _calLock.Items.AddRange("Lock head", "Lock hips", "Lock both");
            _calLock.SelectedIndex = 0;
            _calLock.SelectedIndexChanged += (o, e) => EditCal(x => x.Lock = (CalLock)_calLock.SelectedIndex);
            _calMeasure = c.Field("Measure", new DarkCombo(),
                    "VRChat's avatar measurement: by height (best for full-body tracking) or by arm span (controllers in the T-pose).")
                .Editor as DarkCombo;
            _calMeasure.Items.AddRange("By height", "By arm span");
            _calMeasure.SelectedIndex = 0;
            _calMeasure.SelectedIndexChanged +=
                (o, e) => EditCal(x => x.Measure = (CalMeasure)_calMeasure.SelectedIndex);
            _calFollow = c.Add(Tip(new DarkCheck("Follow VRChat's spine and measurement", true),
                "Use the spine mode (Lock Hip / Head / Both) and avatar measurement (by height / arm span) VRChat had while recording, and switch when they changed during the take, as VRChat did. Spine and Measure above then only apply to takes without that information."));
            _calFollow.CheckedChanged += (o, e) => EditCal(x => x.FollowVrc = _calFollow.Checked);
            _calHips = new DarkCheck("Hips", true);
            _calChest = new DarkCheck("Chest", true);
            _calFeet = new DarkCheck("Feet", true);
            _calKnees = new DarkCheck("Knees", true);
            _calElbows = new DarkCheck("Elbows", true);
            var used = new GridPanel { Columns = 3, RowH = 22, Gap = 2 };
            foreach (var ch in new[] { _calHips, _calChest, _calFeet, _calKnees, _calElbows }) used.Controls.Add(ch);
            c.Field("Trackers", used, "Trackers that drive the body (their roles stay as they are).");
            _calHips.CheckedChanged += (o, e) => EditCal(x => x.UseHips = _calHips.Checked);
            _calChest.CheckedChanged += (o, e) => EditCal(x => x.UseChest = _calChest.Checked);
            _calFeet.CheckedChanged += (o, e) => EditCal(x => x.UseFeet = _calFeet.Checked);
            _calKnees.CheckedChanged += (o, e) => EditCal(x => x.UseKnees = _calKnees.Checked);
            _calElbows.CheckedChanged += (o, e) => EditCal(x => x.UseElbows = _calElbows.Checked);

            var adv = _foldCalAdv = c.Fold("Advanced", false, "clip.cal.adv");
            _calFeetFirst = adv.Add(Tip(new DarkCheck("Keep feet on their trackers", true),
                "Lock head: when a leg can't reach its foot tracker (hip tracker slipped, standing taller than at calibration), the hips come down just enough instead of the foot leaving its tracker, as VRChat does (up to 3 cm). Lock hips and Lock both keep the hips on their tracker and let the foot lift, also as VRChat does."));
            _calFeetFirst.CheckedChanged += (o, e) => EditCal(x => x.KeepFeetPlanted = _calFeetFirst.Checked);
            _calUpright = adv.Add(Tip(new DarkCheck("Ignore head tilt at calibration", true),
                "Turns the head upright about the avatar's head pivot before binding (a confirm is often made while looking down at the menu)."));
            _calUpright.CheckedChanged += (o, e) => EditCal(x => x.UprightCalibration = _calUpright.Checked);
            _calHang = adv.Add(Tip(new DarkCheck("Hang from the headset (VRChat)", true),
                "VRChat binds the trackers with the avatar hanging from your headset: if your scaled headset is lower than the avatar's eyes, its feet end up below the floor (by height about 2 cm on one test avatar, by arm span about 10 cm), with straight legs. Off: the avatar is stood on the floor at calibration, and the knees bend instead."));
            _calHang.CheckedChanged += (o, e) => EditCal(x => x.HangFromHeadset = _calHang.Checked);
            _calVrcHands = adv.Add(Tip(new DarkCheck("VRChat head and hand offsets (Index)", true),
                "The hand bone sits where VRChat's own IK puts it relative to an Index controller (measured), and the head bone 4 cm behind the headset like VRChat. Off: SteamVR's skeletal wrist plus the base solver's controller offsets, and the avatar's eye bones at the headset."));
            _calVrcHands.CheckedChanged += (o, e) => EditCal(x => x.VrcHands = _calVrcHands.Checked);
            _calShoulders = adv.Add(Tip(new DarkCheck("Shoulder movement", true),
                "Shrug and reach (the base solver's shoulder solve; VRChat's --disable-shoulder-tracking turns it off)."));
            _calShoulders.CheckedChanged += (o, e) => EditCal(x => x.ShoulderTracking = _calShoulders.Checked);
            _calProcFeet = adv.Add(Tip(new DarkCheck("Procedural steps without foot trackers", true),
                "The base solver's foot IK for 3-point tracking. Tracked feet always follow their trackers."));
            _calProcFeet.CheckedChanged += (o, e) => EditCal(x => x.ProceduralFeet = _calProcFeet.Checked);
            _followScale = adv.Add(Tip(new DarkCheck("Follow VRChat's avatar scaling"),
                "Off (default): the avatar keeps its own size even when you resized it in VRChat during the take. On: the body follows VRChat's height changes and resets (ScaleFactor over OSC; measured on VRChat's own IK when the take has it)."));
            _followScale.CheckedChanged += (o, e) => EditCal(x => x.FollowAvatarScale = _followScale.Checked);
            _clipScaleBox = Box("0");
            adv.Field("Avatar scale", _clipScaleBox,
                "0 = the avatar's own size (as uploaded); any other value forces that size.");
            _clipScaleBox.Leave += (o, e) =>
            {
                if (TryF(_clipScaleBox.Text, out float v)) EditCal(x => x.AvatarScale = Math.Max(0, v));
            };
            _fixX = adv.Add(new DarkSlider("Hand rotation X", -60, 60, 0, 1) { Format = "+0;-0;0", Suffix = "°" });
            _fixY = adv.Add(new DarkSlider("Hand rotation Y", -60, 60, 0, 1) { Format = "+0;-0;0", Suffix = "°" });
            _fixZ = adv.Add(new DarkSlider("Hand rotation Z", -60, 60, 0, 1) { Format = "+0;-0;0", Suffix = "°" });
            foreach (var sl in new[] { _fixX, _fixY, _fixZ })
            {
                Tip(sl, "Per-take correction of the hand angle (mirrored for the right hand).");
                sl.ValueChanged += (o, e) =>
                    EditCal(x => x.HandRotFix = new Vec3(_fixX.Value, _fixY.Value, _fixZ.Value));
            }

            var vrc = _foldVrc = c.Fold("VRChat settings in this take", false, "clip.cal.vrc");
            Tip(vrc,
                "VRChat's spine mode and avatar measurement while recording, and where they changed. For takes recorded before the app logged them, add a change at the playhead.");
            _vrcTimeline = vrc.Add(new InfoLabel() { Color = Theme.TextDim });
            _vrcChangeKind = new DarkCombo();
            _vrcChangeKind.Items.AddRange("Lock Hip", "Lock Head", "Lock Both", "By arm span", "By height");
            _vrcChangeKind.SelectedIndex = 0;
            vrc.Row(_vrcChangeKind,
                Btn("Add here", AddVrcChangeHere, tip: "VRChat's setting changed to this at the playhead"),
                Btn("Clear added", () =>
                {
                    if (_edit == null || _edit.Edits.VrcChanges.Count == 0) return;
                    _edit.Edits.VrcChanges.Clear();
                    MarkDirty(true);
                    SyncCalibrationUI();
                }));

            var lib = _foldCalLib = c.Fold("Library and presets", false, "clip.cal.lib");
            _calSource = lib.Add(new InfoLabel() { Color = Theme.TextDim });
            lib.Row(
                Btn("Make default", MakeCalDefault,
                    tip: "These settings become the defaults for every take without its own settings"),
                Btn("Use defaults", UseCalDefaults, tip: "Drop this take's own settings"));
            _calPreset = lib.Field("Preset", new DarkCombo(),
                "Named calibration settings, saved in %APPDATA%\\VRMotionRecorder\\calibrations.").Editor as DarkCombo;
            RefreshPresetList(null);
            lib.Row(Btn("Load", LoadCalPreset), Btn("Save as…", SaveCalPreset),
                Btn("Delete", DeleteCalPreset, tip: "Delete the preset (Recycle Bin)"));
            _calCaptureBtn = Btn("Capture now", CaptureCalibrationNow,
                tip:
                "Saves the current SteamVR poses as a calibration after a 5 s countdown: stand straight, look ahead, arms out (T-pose). For when you don't calibrate in VRChat.");
            lib.Row(_calCaptureBtn,
                Btn("Save to library", SaveTakeCalibration,
                    tip:
                    "Saves the calibration this take uses (its T-pose, Calibrate moment or VRChat calibration) to the library, so other takes can use it."),
                Btn("Delete", DeleteSnapshot,
                    tip:
                    "Delete the calibration picked in the list from the library (Recycle Bin). Takes that use it keep their own copy."));

            var det = _foldCalDetails = c.Fold("Details", false, "clip.cal.details");
            _calResult = det.Add(new InfoLabel() { Color = Theme.TextDim });
            _calWatch = det.Add(new InfoLabel() { Color = Theme.TextFaint });

            // ---------------------------------------------------------------- fingers & face
            var f = _secFace = p.Fold("Fingers and face", true, "clip.face");
            _fingerSrc = f.Field("Fingers", new DarkCombo(),
                "Auto: SteamVR finger tracking when the take has it, else VRChat's gestures.").Editor as DarkCombo;
            _fingerSrc.Items.AddRange("Auto", "SteamVR finger tracking", "VRChat gestures", "None (rest pose)");
            _fingerSrc.SelectedIndex = 0;
            _fingerSrc.SelectedIndexChanged += (o, e) =>
            {
                if (_edit == null || _syncing) return;
                _edit.Edits.FingerSource = new[] { "auto", "steamvr", "gestures", "none" }[_fingerSrc.SelectedIndex];
                MarkDirty(true);
            };
            _faceOffset =
                f.Add(Tip(
                    new DarkSlider("Face sync offset", -0.5f, 0.5f, 0, 0.005f)
                        { Format = "+0.000;-0.000;0.000", Suffix = " s" },
                    "Shifts the face against the body (face tracking arrives a little late or early)."));
            _faceOffset.ValueChanged += (o, e) =>
            {
                if (_edit == null || _syncing) return;
                _edit.Edits.FaceOffset = _faceOffset.Value;
                MarkDirty(true);
            };
            _replayBtn = f.Add(Btn("Replay face into VRChat", () =>
                {
                    _replayBtn.Checked = !_replayBtn.Checked;
                    _replayBtn.Invalidate();
                    if (!_replayBtn.Checked)
                    {
                        _replay?.Dispose();
                        _replay = null;
                    }
                },
                tip:
                "While playing, sends the recorded face parameters to VRChat (OSC input), like the OSC_Recorder tool, so you can watch the replay on your avatar in game."));
            _replayBtn.Toggle = true;

            // ---------------------------------------------------------------- notes
            _secNotes = p.Fold("Notes", false, "clip.notes");
            _notesBox = _secNotes.Add(Box(""));
            _notesBox.Multiline = true;
            _notesBox.Height = Theme.S(64);
            _notesBox.TextChanged += (o, e) =>
            {
                if (_edit != null && _notesBox.Focused)
                {
                    _edit.Notes = _notesBox.Text;
                    _dirty = true;
                    _secNotes.Summary = NotesSummary(_notesBox.Text);
                }
            };
        }

        static string NotesSummary(string t)
        {
            t = (t ?? "").Trim();
            int nl = t.IndexOfAny(new[] { '\r', '\n' });
            if (nl >= 0) t = t.Substring(0, nl) + "…";
            return t.Length > 40 ? t.Substring(0, 39) + "…" : t;
        }

        /// <summary>After a solve: one status line, the solver's notes in Details.</summary>
        void ShowBakeResult(BakedClip c, long ms)
        {
            string src = c.BodySource == "humr" ? "VRChat IK (HUMR)" :
                c.BodySource == "ik" ? "IK solve" : "no body data";
            _bakeInfo.Text = $"{src} · {c.Frames} frames at {c.Fps:F0} fps · {ms} ms";
            _bakeInfo.Color = c.BodySource == "none" ? Theme.Warn : Theme.TextFaint;
            _bakeNotes.Text = $"{c.Face.Count} face shapes" +
                              (c.Warnings.Count > 0 ? "\n" + string.Join("\n", c.Warnings) : "");
            _foldClipDetails.Summary = c.Warnings.Count > 0
                ? c.Warnings.Count + (c.Warnings.Count == 1 ? " solver note" : " solver notes")
                : "";
        }

        void UpdateClipUI()
        {
            _syncing = true;
            try { UpdateClipUIInner(); }
            finally { _syncing = false; }
        }

        void UpdateClipUIInner()
        {
            var r = _edit;
            _nameBox.Text = r?.Name ?? "";
            _nameBox.Enabled = r != null;
            SyncPlacementUI();
            _notesBox.Text = r?.Notes ?? "";
            _secNotes.Summary = NotesSummary(r?.Notes);
            if (r == null)
            {
                _clipInfo.Text = "No take selected: the calibration settings below are the defaults for every take.";
                _bakeInfo.Text = "";
                _clipDetail.Text = "";
                _bakeNotes.Text = "";
                _foldClipDetails.Summary = "";
                _devInfo.Text = "";
                BuildDeviceRoles();
                SyncCalibrationUI();
                RefreshSnapList();
                SyncGapsUI();
                _clipPage.PerformLayout();
                return;
            }

            int ftCount = r.Params.Count(p => p.Name.StartsWith("FT/") || p.Name.StartsWith("v2/"));
            var hmd = r.Devices.FirstOrDefault(d => d.DeviceClass == 1);
            bool edited = Math.Abs(r.EditedDuration - r.Duration) > 0.005;
            _clipInfo.Text = $"{r.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {Theme.Time(r.Duration)}" +
                             (edited ? $" → {Theme.Time(r.EditedDuration)}" : "") +
                             (string.IsNullOrEmpty(r.WorldName) ? "" : " · " + r.WorldName);
            _clipDetail.Text =
                $"Recorded {r.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}, {Theme.Time(r.Duration)}" +
                (edited ? $" ({Theme.Time(r.EditedDuration)} edited)" : "") + "\n" +
                $"Avatar: {(string.IsNullOrEmpty(r.AvatarName) ? r.AvatarId : r.AvatarName)}   World: {r.WorldName}\n" +
                $"VRChat IK frames: {r.Humr.Count}   SteamVR devices: {r.Devices.Count()}" +
                (hmd != null ? $" at {hmd.Rate:F0} Hz" : "") + "\n" +
                $"VRChat poses: {(r.HasVrcPoses ? "yes" : "no")}   Parameters: {r.Params.Count} ({ftCount} face)\n" +
                $"Finger tracking: {(r.HasFingers ? $"SteamVR ({r.Hands[0].Count + r.Hands[1].Count} samples)" : "none (gestures)")}";
            _bodySrc.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { "auto", "humr", "ik" }, r.Edits.BodySource));
            _locomotion.Checked = r.Edits.IncludeLocomotion;
            _jumps.Checked = r.Edits.IncludeJumps;
            var vj = VrcJumps.For(r, r.Profile);
            _jumps.Text = vj.Any ? $"VRChat jumps ({vj.Jumps.Count} in this take)" : "VRChat jumps (none in this take)";
            _faceOffset.Value = (float)r.Edits.FaceOffset;
            _faceOffset.Invalidate();
            SyncCalibrationUI();
            RefreshSnapList();
            _fingerSrc.SelectedIndex = Math.Max(0,
                Array.IndexOf(new[] { "auto", "steamvr", "gestures", "none" }, r.Edits.FingerSource));
            SyncAvatarPick();
            BuildDeviceRoles();
            SyncGapsUI();
            _clipPage.PerformLayout();
        }

        static string RoleOf(PoseStream d)
        {
            string r = d.BodyRole;
            if (string.IsNullOrEmpty(r)) r = d.Info.GetStr("autoRole", "");
            return string.IsNullOrEmpty(r) ? d.Serial : r;
        }

        void SyncGapsUI()
        {
            bool was = _syncing;
            _syncing = true;
            try
            {
                _gapItems.Clear();
                _gapList.Items.Clear();
                _gapTrackerItems.Clear();
                _gapTracker.Items.Clear();
                if (_edit == null)
                {
                    _gapInfo.Text = "";
                    _foldGaps.Summary = "";
                    return;
                }

                var trackers = _edit.Devices.Where(d =>
                    d.Count > 2 && (d.DeviceClass == 3 ||
                                    (d.DeviceClass == 2 && d.ControllerRole != 1 && d.ControllerRole != 2))).ToList();
                foreach (var d in trackers)
                {
                    _gapTrackerItems.Add(d);
                    _gapTracker.Items.Add(RoleOf(d));
                }

                if (_gapTracker.Items.Count > 0) _gapTracker.SelectedIndex = 0;
                var gaps = TrackerGaps.For(_edit);
                foreach (var g in gaps)
                {
                    var d = trackers.FirstOrDefault(x => x.Serial == g.Serial);
                    string b = double.IsPositiveInfinity(g.B) ? "end" : g.B.ToString("F2");
                    _gapItems.Add(g);
                    _gapList.Items.Add(
                        $"{(d != null ? RoleOf(d) : g.Serial)}  {Math.Max(0, g.A):F2}-{b} s  {(g.Ignored ? "(kept as recorded) " : g.Manual ? "(marked) " : "")}{g.Why}");
                }

                if (_gapList.Items.Count > 0) _gapList.SelectedIndex = 0;
                double total = gaps.Where(g => !g.Ignored && !double.IsInfinity(g.Length)).Sum(g => g.Length);
                _gapInfo.Text = gaps.Count == 0
                    ? "No tracking loss found."
                    : $"{gaps.Count(g => !g.Ignored)} repaired ({total:0.0} s), {gaps.Count(g => g.Ignored)} kept as recorded.";
                _foldGaps.Summary =
                    gaps.Count == 0 ? "none" : $"{gaps.Count(g => !g.Ignored)} repaired · {total:0.0} s";
                _foldGaps.SummaryColor = gaps.Any(g => !g.Ignored) ? Theme.Warn : Theme.TextFaint;
                _gapList.Visible = gaps.Count > 0;
                SyncGapToggle();
            }
            finally { _syncing = was; }
        }

        void SyncGapToggle()
        {
            int i = _gapList.SelectedIndex;
            bool ok = i >= 0 && i < _gapItems.Count;
            _gapToggle.Text = ok && _gapItems[i].Ignored ? "Repair" :
                ok && _gapItems[i].Manual ? "Remove mark" : "Keep as recorded";
            _gapToggle.Invalidate();
        }

        void SelectGap()
        {
            int i = _gapList.SelectedIndex;
            if (_edit == null || i < 0 || i >= _gapItems.Count) return;
            var g = _gapItems[i];
            _timeline.SelA = Math.Max(0, g.A);
            _timeline.SelB = double.IsPositiveInfinity(g.B) ? _edit.Duration : g.B;
            _timeline.Playhead = Math.Max(0, g.A - 0.3);
            _liveView = false;
            ShowFrameAt(_timeline.Playhead);
            _timeline.Invalidate();
        }

        void ToggleGap()
        {
            int i = _gapList.SelectedIndex;
            if (_edit == null || i < 0 || i >= _gapItems.Count) return;
            var g = _gapItems[i];
            var marks = _edit.Edits.TrackerGaps;
            if (g.Manual) marks.RemoveAll(m => !m.Keep && m.Serial == g.Serial && Math.Abs(m.A - g.A) < 1e-6);
            else if (g.Ignored) marks.RemoveAll(m => m.Keep && m.Serial == g.Serial && m.A < g.B && m.B > g.A);
            else
                marks.Add(new TrackerGapMark
                {
                    Serial = g.Serial, A = g.A, B = double.IsPositiveInfinity(g.B) ? _edit.Duration : g.B, Keep = true
                });
            TrackerGaps.Invalidate(_edit);
            MarkDirty(true);
            SyncGapsUI();
        }

        void MarkGapSelection()
        {
            int k = _gapTracker.SelectedIndex;
            if (_edit == null || k < 0 || k >= _gapTrackerItems.Count || _timeline.SelA < 0 ||
                _timeline.SelB - _timeline.SelA < 0.02)
            {
                _gapInfo.Text = "Select a stretch on the timeline first (drag in the lanes), then pick the tracker.";
                return;
            }

            _edit.Edits.TrackerGaps.Add(new TrackerGapMark
                { Serial = _gapTrackerItems[k].Serial, A = _timeline.SelA, B = _timeline.SelB });
            TrackerGaps.Invalidate(_edit);
            MarkDirty(true);
            SyncGapsUI();
        }

        void BuildDeviceRoles()
        {
            var old = _devPanel.Controls.Cast<Control>().ToList();
            _devPanel.Controls.Clear();
            foreach (var c in old) c.Dispose();
            var r = _edit;
            var trackers =
                r?.Devices.Where(d =>
                        d.DeviceClass == 3 || (d.DeviceClass == 2 && d.ControllerRole != 1 && d.ControllerRole != 2))
                    .ToList() ?? new List<PoseStream>();
            _devInfo.Text = r == null ? "" : trackers.Count == 0 ? "No body trackers in this take." : "";
            _devInfo.Visible = _devInfo.Text.Length > 0;
            var roles = new List<string>();
            foreach (var d in trackers)
            {
                var cb = new DarkCombo();
                cb.Items.AddRange(RoleNames);
                string auto = d.Info.GetStr("autoRole", "");
                if (auto.Length > 0) cb.Items[0] = "Auto: " + auto;
                string cur = d.BodyRole;
                int idx = Array.IndexOf(RoleNames, string.IsNullOrEmpty(cur) ? "Auto" : cur);
                cb.SelectedIndex = idx < 0 ? 0 : idx;
                roles.Add(!string.IsNullOrEmpty(cur) ? cur : auto.Length > 0 ? auto : "?");
                var stream = d;
                cb.SelectedIndexChanged += (o, e) =>
                {
                    if (_syncing) return;
                    stream.BodyRole = cb.SelectedIndex == 0 ? "" : RoleNames[cb.SelectedIndex];
                    MarkDirty(true);
                };
                string model = d.ModelNumber.Length > 0 ? d.ModelNumber : "Tracker";
                {
                    var fr = FieldRow.Make(string.IsNullOrEmpty(d.Serial) ? model : d.Serial, cb,
                        $"{model} {d.Serial}\nAuto: VRChat's own assignment when its IK is in the take, else from how the tracker moves (shown after the solve).");
                    fr.LabelWidth = 112;
                    _devPanel.Controls.Add(fr);
                }
            }

            ((GridPanel)_devPanel).FitHeight();
            _foldTrackers.Summary = r == null ? "" : trackers.Count == 0 ? "none" : string.Join(", ", roles);
        }

        static string ShortPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            int i = p.IndexOf("Assets", StringComparison.OrdinalIgnoreCase);
            return i >= 0 ? p.Substring(i) : p;
        }

        /// <summary>
        ///     Adds a VRChat setting change (spine mode / measurement) at the playhead, for takes recorded without the
        ///     settings log.
        /// </summary>
        void AddVrcChangeHere()
        {
            if (_edit == null) return;
            int k = _vrcChangeKind.SelectedIndex;
            var c = new VrcChange { T = Playhead };
            if (k <= 2) c.Spine = k;
            else c.Measure = k == 3 ? 0 : 1;
            _edit.Edits.VrcChanges.Add(c);
            MarkDirty(true);
            SyncCalibrationUI();
        }

        /// <summary>
        ///     Fills the "Calibration used by this take" list: this take's own T-pose / moment / VRChat calibration, then the
        ///     library.
        /// </summary>
        void RefreshSnapList()
        {
            if (_calSnap == null) return;
            bool was = _syncing;
            _syncing = true;
            try
            {
                _calSnapItems.Clear();
                _calSnap.Items.Clear();

                void Add(string text, object tag)
                {
                    _calSnap.Items.Add(text);
                    _calSnapItems.Add(tag);
                }

                var r = _edit;
                if (r == null)
                {
                    Add("(select a take)", null);
                    _calSnap.SelectedIndex = 0;
                    _calSnapInfo.Text = "";
                    return;
                }

                if (_inTakeFor != r)
                {
                    _inTakeFor = r;
                    _inTakeCal = TakeCalibrations.FindInTake(r, out _inTakeCalText);
                }

                Add("Best T-pose in this take", "auto");
                if (r.Edits.CalibrationTime >= 0) Add($"Moment at {r.Edits.CalibrationTime:F2} s", "moment");
                var tl = VrcTimeline.For(r);
                if (tl.Calibrations.Count > 0)
                    Add(
                        $"VRChat's calibrations ({string.Join(", ", tl.Calibrations.Select(c => c.Confirm.ToString("F1") + " s"))})",
                        "timeline");
                if (_inTakeCal >= 0) Add($"VRChat calibration at {_inTakeCal:F2} s only", "intake");
                var emb = r.Edits.Snapshot;
                var lib = CalibrationLibrary.List();
                string session = r.Extra.GetStr("steamvrSession", "");
                var ordered = lib.Select(sn =>
                    {
                        sn.Compare(r, out var miss, out _);
                        return Tuple.Create(sn, miss.Count == 0);
                    })
                    .OrderByDescending(t => t.Item2).ThenByDescending(t => t.Item1.CreatedUtc).Take(40).ToList();
                foreach (var t in ordered)
                {
                    var sn = t.Item1;
                    string tags = (t.Item2 ? "" : "  ⚠ trackers differ") +
                                  (session.Length > 0 && sn.SteamVRSession.Length > 0 && sn.SteamVRSession != session
                                      ? "  (other SteamVR session)"
                                      : "");
                    Add((t.Item2 ? "✓ " : "") + sn.Label + tags, sn);
                }

                bool embInList = emb != null && lib.Any(x => x.Id == emb.Id);
                bool embInTake = emb != null && emb.FromTake.Length > 0 &&
                                 emb.FromTake == Path.GetFileName(r.FilePath ?? "");
                if (emb != null && !embInList && !(embInTake && _inTakeCal >= 0))
                    Add("Linked: " + emb.Label + " (copy kept in the take)", emb);
                int sel = 0;
                if (PoseBuilder.FollowsVrcCalibrations(r, tl))
                    sel = _calSnapItems.IndexOf("timeline");
                else if (r.Edits.CalibrationTime == EditList.UseVrcTimeline && emb != null)
                    sel = _calSnapItems.FindIndex(o => o is CalibrationSnapshot c && c.Id == emb.Id);
                else if (r.Edits.CalibrationTime == EditList.UseSnapshot && emb != null)
                {
                    sel = _calSnapItems.FindIndex(o => o is CalibrationSnapshot c && c.Id == emb.Id);
                    if (sel < 0 && embInTake) sel = _calSnapItems.IndexOf("intake");
                    if (sel < 0) sel = _calSnapItems.FindIndex(o => o == emb);
                }
                else if (r.Edits.CalibrationTime >= 0) sel = _calSnapItems.IndexOf("moment");

                _calSnap.SelectedIndex = Math.Max(0, sel);
                UpdateSnapInfo();
            }
            finally { _syncing = was; }
        }

        /// <summary>One short line under the calibration list (warnings in colour); the whole story in its tooltip.</summary>
        void UpdateSnapInfo()
        {
            var r = _edit;

            void Show(string shortText, string full, bool warn = false)
            {
                _calSnapInfo.Text = shortText;
                _calSnapInfo.Color = warn ? Theme.Warn : Theme.TextFaint;
                Theme.Tip(_calSnapInfo, full);
                Theme.Tip(_calSnap, full);
            }

            if (r == null || _calSnap.SelectedIndex < 0 || _calSnap.SelectedIndex >= _calSnapItems.Count)
            {
                Show("", null);
                return;
            }

            object it = _calSnapItems[_calSnap.SelectedIndex];
            var sn = it as CalibrationSnapshot ?? (it as string == "intake" ? r.Edits.Snapshot : null);
            if (it as string == "auto")
            {
                Show("", "Calibrates at the best T-pose in this take (arms out, looking ahead, holding still).");
                return;
            }

            if (it as string == "timeline")
            {
                var e0 = r.Edits.Snapshot;
                bool before = e0 != null && PoseBuilder.SnapshotTakeTime(r, e0) < 0;
                Show(
                    before
                        ? $"\"{e0.Label}\" until the first recalibration, then each VRChat calibration."
                        : "Each VRChat calibration in the take, as it happened.",
                    "Like VRChat: " +
                    (before
                        ? $"\"{e0.Label}\" (in effect when recording started) until the first recalibration, then"
                        : "the first calibration in the take is also used before it (no earlier one is linked), then") +
                    " each VRChat calibration from its \"Calibrate FBT\" click on. VRChat hides the avatar until the confirm; you stand in the calibration pose there, so the new calibration already fits.");
                return;
            }

            if (it as string == "moment")
            {
                Show("", "The moment picked with Calibrate (transport) or At playhead.");
                return;
            }

            if (it as string == "intake" && sn == null)
            {
                Show(_inTakeCalText, _inTakeCalText);
                return;
            }

            if (sn == null)
            {
                Show("", null);
                return;
            }

            sn.Compare(r, out var miss, out var extra);
            string session = r.Extra.GetStr("steamvrSession", "");
            var lines = new List<string> { sn.Detail.Length > 0 ? sn.Detail : sn.Source };
            var age = r.CreatedUtc - sn.CreatedUtc;
            string when = Math.Abs(age.TotalMinutes) < 1
                ? "at the start of this take"
                :
                age.TotalSeconds > 0
                    ?
                    age.TotalHours >= 1
                        ? $"{age.TotalHours:0.#} h before this take"
                        : $"{age.TotalMinutes:0} min before this take"
                    : $"{-age.TotalSeconds:0} s into this take";
            lines.Add($"{sn.Devices.Count} devices, {when}" +
                      (sn.Vrc.PlayerHeight > 0.5f ? $", VRChat height {sn.Vrc.PlayerHeight * 100:0} cm" : "") +
                      (string.IsNullOrEmpty(sn.AvatarName) ? "" : $", avatar {sn.AvatarName}"));
            var warns = new List<string>();
            if (miss.Count > 0)
                warns.Add(
                    $"⚠ Tracker(s) {string.Join(", ", miss)} are not in this calibration: they won't drive the body.");
            else lines.Add("Trackers match this take.");
            if (session.Length > 0 && sn.SteamVRSession.Length > 0 && sn.SteamVRSession != session)
                warns.Add(
                    "⚠ From another SteamVR session: if the room setup or tracker straps changed since, recalibrate.");
            else if ((session.Length == 0 || sn.SteamVRSession.Length == 0) && age.TotalHours > 2)
                warns.Add("⚠ Taken hours apart: tracker straps have probably moved since.");
            string shortText = $"{sn.Devices.Count} devices, {when}" +
                               (warns.Count > 0 ? "\n" + string.Join("\n", warns) : "");
            Show(shortText, string.Join("\n", lines.Concat(warns)), warns.Count > 0);
        }

        void OnCalSnapPicked()
        {
            if (_syncing || _edit == null) return;
            int i = _calSnap.SelectedIndex;
            if (i < 0 || i >= _calSnapItems.Count) return;
            object it = _calSnapItems[i];
            var e = _edit.Edits;
            if (it as string == "auto") e.CalibrationTime = -1;
            else if (it as string == "moment") { }
            else if (it as string == "timeline")
            {
                // keep a linked calibration from before the take (in effect when recording started); else the library's latest one before it
                if (e.Snapshot == null || PoseBuilder.SnapshotTakeTime(_edit, e.Snapshot) >= 0)
                    e.Snapshot = CalibrationLibrary.BestFor(_edit, before: _edit.CreatedUtc.AddSeconds(0.5))?.Clone();
                e.CalibrationTime = EditList.UseVrcTimeline;
            }
            else if (it as string == "intake")
            {
                var sn = CalibrationSnapshot.FromTakeMoment(_edit, _inTakeCal, _inTakeCalText);
                sn.Source = "vrchat";
                e.Snapshot = sn;
                e.CalibrationTime = EditList.UseSnapshot;
            }
            else if (it is CalibrationSnapshot sn)
            {
                e.Snapshot = sn.Clone();
                e.CalibrationTime = EditList.UseSnapshot;
            }

            UpdateSnapInfo();
            MarkDirty(true);
            _timeline.Invalidate();
        }

        /// <summary>5 s countdown (beeps), then the current SteamVR poses are saved as a calibration snapshot.</summary>
        void CaptureCalibrationNow()
        {
            if (Rec.Calib == null)
            {
                MessageBox.Show(this, "Calibration watching is off (Settings).", "Calibration");
                return;
            }

            if (Rec.OpenVR == null || !Rec.OpenVR.Connected)
            {
                MessageBox.Show(this, "SteamVR is not connected.", "Calibration");
                return;
            }

            if (_captureTimer != null) return;
            _captureCount = 5;
            _calCaptureBtn.Text = "Capturing in 5…";
            _captureTimer = new Timer { Interval = 1000 };
            _captureTimer.Tick += (o, ev) =>
            {
                _captureCount--;
                if (_captureCount > 0)
                {
                    SystemSounds.Beep.Play();
                    _calCaptureBtn.Text = $"Capturing in {_captureCount}…";
                    return;
                }

                _captureTimer.Stop();
                _captureTimer.Dispose();
                _captureTimer = null;
                _calCaptureBtn.Text = "Capture now";
                try
                {
                    var sn = Rec.Calib.CaptureNow("captured with the Capture calibration now button");
                    SystemSounds.Asterisk.Play();
                    RefreshSnapList();
                    _calWatch.Text = "Saved " + sn.Label;
                }
                catch (Exception ex) { MessageBox.Show(this, "Could not capture: " + ex.Message, "Calibration"); }
            };
            SystemSounds.Beep.Play();
            _captureTimer.Start();
        }

        /// <summary>
        ///     Saves the calibration this take uses (its T-pose / moment / VRChat calibration) to the library, so other takes
        ///     can use it.
        /// </summary>
        void SaveTakeCalibration()
        {
            if (_edit == null) return;
            try
            {
                CalibrationSnapshot sn;
                if (_edit.Edits.CalibrationTime == EditList.UseSnapshot && _edit.Edits.Snapshot != null)
                    sn = _edit.Edits.Snapshot.Clone();
                else
                {
                    double t = _baked?.Calibration != null && _baked.Calibration.Time >= 0
                        ? _baked.Calibration.Time
                        : _edit.Edits.CalibrationTime;
                    if (t < 0)
                    {
                        MessageBox.Show(this,
                            "This take has no calibration moment yet (no T-pose found). Pick one with Calibrate at playhead.",
                            "Calibration");
                        return;
                    }

                    sn = CalibrationSnapshot.FromTakeMoment(_edit, t, $"moment {t:F2} s of take {_edit.Name}");
                }

                if (CalibrationLibrary.Get(sn.Id) != null)
                {
                    MessageBox.Show(this, "This calibration is already in the library.", "Calibration");
                    return;
                }

                CalibrationLibrary.Save(sn);
                RefreshSnapList();
            }
            catch (Exception ex) { MessageBox.Show(this, "Could not save: " + ex.Message, "Calibration"); }
        }

        void DeleteSnapshot()
        {
            int i = _calSnap.SelectedIndex;
            if (i < 0 || i >= _calSnapItems.Count || !(_calSnapItems[i] is CalibrationSnapshot sn) ||
                CalibrationLibrary.Get(sn.Id) == null)
            {
                MessageBox.Show(this, "Pick a saved calibration in the list first.", "Calibration");
                return;
            }

            if (MessageBox.Show(this,
                    $"Delete \"{sn.Label}\" from the library? It goes to the Recycle Bin. Takes that use it keep their own copy.",
                    "Calibration", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            try { CalibrationLibrary.Delete(sn.Id); }
            catch (Exception ex) { MessageBox.Show(this, "Could not delete: " + ex.Message, "Calibration"); }

            RefreshSnapList();
        }

        void UpdateCalWatch()
        {
            if (_calWatch == null) return;
            string t;
            if (Rec.Calib == null) t = "Watching for VRChat calibrations is off (Settings).";
            else
            {
                var v = VrcSettings.Read();
                t = "Watching for VRChat calibrations: " + Rec.Calib.Status + (Rec.Calib.TrackingType >= 0
                    ? $" (TrackingType {Rec.Calib.TrackingType})"
                    : "");
                if (v.Found)
                    t +=
                        $"\nVRChat settings: User Real Height {v.PlayerHeight * 100:0} cm, Avatar Measurement {(v.MeasurementType == 0 ? "Arm Span" : v.MeasurementType == 1 ? "Height" : v.MeasurementType.ToString())}, FBT Spine Mode {(v.SpineMode == 0 ? "Lock Hip" : v.SpineMode == 1 ? "Lock Head" : v.SpineMode == 2 ? "Lock Both" : v.SpineMode.ToString())}" +
                        (v.OneHandedCalibration ? ", one-handed calibration" : "");
            }

            if (t != _lastWatchText)
            {
                _lastWatchText = t;
                _calWatch.Text = t;
            }
        }

        /// <summary>
        ///     Applies a change from a calibration control. With a clip open the clip gets its own copy (raw data stays
        ///     re-solvable); without one the global defaults change.
        /// </summary>
        void EditCal(Action<CalibrationSettings> change)
        {
            if (_syncing) return;
            if (_edit == null)
            {
                change(S.CalibrationDefaults);
                S.Save();
                SyncCalibrationUI();
                return;
            }

            if (_edit.Edits.Calib == null) _edit.Edits.Calib = S.CalibrationDefaults.Clone();
            change(_edit.Edits.Calib);
            SyncCalibrationUI();
            MarkDirty(true);
        }

        static float SuggestedHeightValue(CalHeightMode m)
        {
            return m == CalHeightMode.RealHeight ? 1.70f :
                m == CalHeightMode.EyeHeight ? 1.58f :
                m == CalHeightMode.Scale ? 1f : 0f;
        }

        void SyncCalibrationUI()
        {
            if (_calHeight == null) return;
            bool was = _syncing;
            _syncing = true;
            try
            {
                var c = ShownCal;
                var inv = CultureInfo.InvariantCulture;
                _calHeight.SelectedIndex = (int)c.Height;
                _calValue.Enabled = c.Height != CalHeightMode.Measure && c.Height != CalHeightMode.VRChat;
                var vrcNow = c.Height == CalHeightMode.VRChat ? VrcSettings.Read() : null;
                if (!_calValue.Focused)
                    _calValue.Text = c.Height == CalHeightMode.Measure
                        ? _baked?.Calibration != null
                            ? _baked.Calibration.RealEyeHeight.ToString("0.000", inv) + " m (measured eye height)"
                            : ""
                        : c.Height == CalHeightMode.VRChat
                            ? vrcNow.Found
                                ? vrcNow.PlayerHeight.ToString("0.00", inv) + " m (VRChat settings now)"
                                : "not found: VRChat isn't installed or never ran"
                            : c.HeightValue.ToString(c.Height == CalHeightMode.Scale ? "0.000" : "0.00", inv);
                _calValueRow.Label = c.Height == CalHeightMode.RealHeight ? "Height (m)" :
                    c.Height == CalHeightMode.EyeHeight ? "Eye height (m)" :
                    c.Height == CalHeightMode.Scale ? "Scale" :
                    c.Height == CalHeightMode.VRChat ? "VRChat (m)" : "Measured";
                Theme.Tip(_calValueRow, c.Height == CalHeightMode.RealHeight
                    ? $"Your height in metres (floor to top of head); eye height = height × {CalibrationSettings.EyeToHeightRatio:0.00}."
                    : c.Height == CalHeightMode.EyeHeight
                        ? "Your eye height in metres (floor to eyes)."
                        : c.Height == CalHeightMode.Scale
                            ? "Tracking scale (real metres → avatar metres)."
                            : c.Height == CalHeightMode.VRChat
                                ? $"VRChat's User Real Height (read from its settings, stored with each calibration and take); eye height = height × {CalibrationSettings.EyeToHeightRatio:0.00}."
                                : "Measured: the headset height at the calibration moment, head tilt removed.");
                _calMeasure.SelectedIndex = (int)c.Measure;
                if (!_clipScaleBox.Focused) _clipScaleBox.Text = c.AvatarScale.ToString("0.000", inv);
                _calLock.SelectedIndex = (int)c.Lock;
                _calHips.Checked = c.UseHips;
                _calChest.Checked = c.UseChest;
                _calFeet.Checked = c.UseFeet;
                _calKnees.Checked = c.UseKnees;
                _calElbows.Checked = c.UseElbows;
                _calFollow.Checked = c.FollowVrc;
                _followScale.Checked = c.FollowAvatarScale;
                string vd = _edit == null ? "" : VrcTimeline.For(_edit).Describe();
                _vrcTimeline.Text = _edit == null ? "Select a take." : "VRChat in this take: " + vd;
                _foldVrc.Summary = vd.Length > 34 ? vd.Substring(0, 33) + "…" : vd;
                _calUpright.Checked = c.UprightCalibration;
                _calHang.Checked = c.HangFromHeadset;
                _calVrcHands.Checked = c.VrcHands;
                _calFeetFirst.Checked = c.KeepFeetPlanted;
                _calShoulders.Checked = c.ShoulderTracking;
                _calProcFeet.Checked = c.ProceduralFeet;
                _fixX.Value = c.HandRotFix.x;
                _fixY.Value = c.HandRotFix.y;
                _fixZ.Value = c.HandRotFix.z;
                foreach (var sl in new[] { _fixX, _fixY, _fixZ }) sl.Invalidate();
                _calSource.Text = _edit == null
                    ? "Editing the global defaults (no take selected)."
                    : _edit.Edits.Calib == null
                        ? "This take uses the global defaults. Any change gives it its own settings."
                        : _edit.Edits.Calib.SameAs(S.CalibrationDefaults)
                            ? "This take has its own settings (same as the defaults)."
                            : "This take has its own settings.";
                _secCal.Summary = _edit == null ? "editing the defaults" :
                    _edit.Edits.Calib == null || _edit.Edits.Calib.SameAs(S.CalibrationDefaults) ? "defaults" :
                    "this take's own";
                _secCal.SummaryColor = _edit == null ? Theme.Accent : Theme.TextFaint;
                if (_edit == null) _calMoment.Text = "";
                else if (_baked?.Calibration == null)
                    _calMoment.Text = _edit.Edits.CalibrationTime == EditList.UseSnapshot
                        ?
                        "Calibration: " + (_edit.Edits.Snapshot?.Label ?? "")
                        : _edit.Edits.CalibrationTime >= 0
                            ? $"Calibration moment: {_edit.Edits.CalibrationTime:F2} s (set with Calib.)"
                            : "Calibration moment: auto (T-pose detection)";
            }
            finally { _syncing = was; }

            _clipPage.PerformLayout();
        }

        void ShowCalibrationResult(BakedClip c)
        {
            var res = c?.Calibration;
            if (res == null)
            {
                _calResult.Text = c != null && c.BodySource == "humr"
                    ? "Body from VRChat's own IK (HUMR): calibration settings don't apply."
                    : "";
                _calMoment.Text = "";
                return;
            }

            _calMoment.Text = "Calibration: " + res.PoseText;
            _calMoment.Color = res.PoseText.Contains("not a T-pose") || res.PoseText.Contains("no T-pose")
                ? Theme.Warn
                : Theme.TextDim;
            var warns = c.Warnings.Where(w => w.StartsWith("Calibration snapshot")).ToList();
            _calResult.Text = "Scale: " + res.ScaleText + "\n" + res.Summary() +
                              (warns.Count > 0 ? "\n⚠ " + string.Join("\n⚠ ", warns) : "");
            if (warns.Count > 0) _calMoment.Color = Theme.Warn;
            SyncCalibrationUI();
        }

        void RefreshPresetList(string select)
        {
            var names = CalibrationPresets.List();
            _calPreset.Items.Clear();
            _calPreset.Items.Add(names.Count == 0 ? "(no presets saved yet)" : "Preset…");
            foreach (string n in names) _calPreset.Items.Add(n);
            int i = select != null
                ? names.FindIndex(n => string.Equals(n, select, StringComparison.OrdinalIgnoreCase))
                : -1;
            _calPreset.SelectedIndex = i >= 0 ? i + 1 : 0;
        }

        void LoadCalPreset()
        {
            string name = SelectedPreset;
            if (name == null)
            {
                MessageBox.Show(this, "Pick a preset in the list first.", "Calibration presets");
                return;
            }

            try
            {
                var c = CalibrationPresets.Load(name);
                if (_edit == null)
                {
                    S.CalibrationDefaults = c;
                    S.Save();
                    SyncCalibrationUI();
                    return;
                }

                _edit.Edits.Calib = c;
                SyncCalibrationUI();
                MarkDirty(true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not load preset: " + ex.Message, "Calibration presets");
            }
        }

        void SaveCalPreset()
        {
            string name = Interaction.InputBox("Name for these calibration settings (" + ShownCal.Describe() + "):",
                "Save calibration preset", SelectedPreset ?? "My calibration");
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                if (CalibrationPresets.List()
                        .Any(n => string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase)) &&
                    MessageBox.Show(this, $"Replace the preset \"{name.Trim()}\"?", "Save calibration preset",
                        MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                CalibrationPresets.Save(name, ShownCal);
                RefreshPresetList(name.Trim());
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save preset: " + ex.Message, "Calibration presets");
            }
        }

        void DeleteCalPreset()
        {
            string name = SelectedPreset;
            if (name == null) return;
            if (MessageBox.Show(this, $"Delete the preset \"{name}\"? (It goes to the Recycle Bin.)",
                    "Calibration presets", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            try { CalibrationPresets.Delete(name); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not delete preset: " + ex.Message, "Calibration presets");
            }

            RefreshPresetList(null);
        }

        void MakeCalDefault()
        {
            S.CalibrationDefaults = ShownCal.Clone();
            S.Save();
            if (_edit != null && _edit.Edits.Calib != null)
            {
                _edit.Edits.Calib = null;
                MarkDirty(false);
            } // identical to the defaults now

            SyncCalibrationUI();
        }

        void UseCalDefaults()
        {
            if (_edit == null || _edit.Edits.Calib == null) return;
            _edit.Edits.Calib = null;
            SyncCalibrationUI();
            MarkDirty(true);
        }

        void PickSkeletonForClip()
        {
            if (_edit == null) return;
            var p = PickModelMeta();
            if (p == null) return;
            _edit.Profile = p.Clone();
            if (!string.IsNullOrEmpty(_edit.AvatarId) && MessageBox.Show(this,
                    $"Also use this skeleton whenever you wear avatar {_edit.AvatarName} ({_edit.AvatarId})?",
                    "Link avatar", MessageBoxButtons.YesNo) == DialogResult.Yes)
                Avatars.Link(_edit.AvatarId, p.SourcePath);
            UpdateClipUI();
            MarkDirty(true);
        }

        AvatarProfile PickModelMeta()
        {
            using (var d = new OpenFileDialog
                   {
                       Filter = "Humanoid model (*.fbx;*.fbx.meta;*.blend)|*.fbx;*.meta;*.blend;*.FBX",
                       Title = "Pick your avatar's model inside a Unity project (rig must be Humanoid)"
                   })
            {
                if (S.UnityProjects.Count > 0) d.InitialDirectory = Path.Combine(S.UnityProjects[0], "Assets");
                if (d.ShowDialog(this) != DialogResult.OK) return null;
                string meta = d.FileName.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                    ? d.FileName
                    : d.FileName + ".meta";
                if (!File.Exists(meta))
                {
                    MessageBox.Show(this,
                        "No .meta next to that file. Pick a model inside a Unity project's Assets folder.");
                    return null;
                }

                var p = Avatars.LoadModel(meta);
                if (p == null) MessageBox.Show(this, "That model has no humanoid rig in its import settings.");
                return p;
            }
        }

        /// <summary>The Clip tab's avatar list: "Auto" (the avatar the take was recorded with) and every library entry.</summary>
        void SyncAvatarPick()
        {
            if (_avatarPick == null) return;
            bool was = _syncing;
            _syncing = true;
            try
            {
                _avatarItems.Clear();
                _avatarPick.Items.Clear();
                var r = _edit;
                var m = r == null ? null : Avatars.MatchTake(r);
                string recorded = r?.Profile != null
                    ? Avatars.Get(r.Profile.LibraryId)?.Label ?? r.Profile.Name
                    : m?.Entry?.Label ?? "none";
                _avatarItems.Add(null);
                _avatarPick.Items.Add($"Auto ({recorded})");
                foreach (var e in Avatars.All())
                {
                    _avatarItems.Add(e);
                    _avatarPick.Items.Add(e.Label);
                }

                int idx = r == null
                    ? 0
                    : Math.Max(0,
                        _avatarItems.FindIndex(e =>
                            e != null && e.Id.Equals(r.Edits.TargetAvatar, StringComparison.OrdinalIgnoreCase)));
                _avatarPick.SelectedIndex = idx;
                if (r == null)
                {
                    _avatarInfo.Text = "";
                    Theme.Tip(_avatarInfo, null);
                    return;
                }

                string worn = string.IsNullOrEmpty(r.AvatarName) ? r.AvatarId : r.AvatarName;
                string how = _autoProfile
                    ? m?.How ?? ""
                    : r.Extra.GetObj("avatarBase")?.GetStr("how") ?? "skeleton saved with the take";
                var target = Avatars.TargetFor(r, out _);
                bool guess = _autoProfile && m != null && !m.Sure;
                _avatarInfo.Text = target == null
                    ? "Nothing to solve onto: add an avatar on the Settings tab."
                    : $"Recorded as {worn}" + (guess ? " (base model guessed: link it below)" : "");
                Theme.Tip(_avatarInfo, $"Recorded with: {worn} → base model {recorded}" +
                                       (how.Length > 0 ? $" ({how})" : "") +
                                       $"\nSolved onto: {target?.Name ?? "nothing"}" +
                                       (target != null ? $", eye height {target.EyeHeight:0.000} m" : ""));
                _avatarInfo.Color = guess || target == null ? Theme.Warn : Theme.TextFaint;
            }
            finally { _syncing = was; }
        }

        void OnAvatarPicked()
        {
            if (_syncing || _edit == null) return;
            int i = _avatarPick.SelectedIndex;
            var e = i >= 0 && i < _avatarItems.Count ? _avatarItems[i] : null;
            _edit.Edits.TargetAvatar = e?.Id ?? "";
            SyncAvatarPick();
            MarkDirty(true);
        }

        /// <summary>The take's avatar (and this VRChat avatar id from now on) is the base model picked in the list.</summary>
        void LinkTakeAvatar()
        {
            if (_edit == null) return;
            int i = _avatarPick.SelectedIndex;
            var e = i > 0 && i < _avatarItems.Count ? _avatarItems[i] : null;
            if (e == null)
            {
                MessageBox.Show(this, "Pick the base model in the list above first.");
                return;
            }

            _edit.Profile = e.Profile();
            _edit.Edits.TargetAvatar = "";
            if (!string.IsNullOrEmpty(_edit.AvatarId)) Avatars.LinkId(_edit.AvatarId, e);
            TrackerRoleFinder.Invalidate(_edit);
            VrcAvatarScale.Invalidate(_edit);
            SyncAvatarPick();
            MarkDirty(true);
        }
    }
}
