using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualBasic.FileIO;
using Timer = System.Windows.Forms.Timer;

namespace VRMR.UI
{
    public sealed partial class MainForm : Form
    {
        readonly AvatarLibrary Avatars;
        readonly Recorder Rec;
        readonly Settings S;
        readonly List<AvatarEntry> _avatarItems = new();
        readonly List<AvatarEntry> _liveBaseItems = new();
        readonly LiveHumrMap _liveMap = new();
        readonly Timer _rebakeTimer;
        readonly Dictionary<string, float> _replaySent = new();
        readonly Dictionary<Recording, Tuple<SteamVRRoom, float>> _roomFit = new();

        readonly List<int> _transportSeps = new();
        readonly Timer _ui;
        bool _autoProfile;
        InfoLabel _avLibInfo, _liveAvatarInfo;
        ListBox _avList;

        TextBox _avPatterns;

        // avatar library
        DarkCombo _avatarPick, _liveBase;
        bool _bakeAgain;
        BakedClip _baked;
        volatile bool _baking;
        DarkCombo _bodySrc, _fps, _faceNaming;
        DarkButton _calCaptureBtn;

        DarkCheck _calHips,
            _calChest,
            _calFeet,
            _calKnees,
            _calElbows,
            _calUpright,
            _calShoulders,
            _calProcFeet,
            _calFeetFirst,
            _calVrcHands,
            _calHang,
            _calFollow;

        InfoLabel _calMoment, _calSource, _calResult, _calSnapInfo, _calWatch;
        StatusChips _chips;
        StackPanel _clipPage, _exportPage, _livePage, _setPage;
        TextBox _clipScaleBox, _calValue;
        DeviceList _devList;
        Panel _devPanel;
        bool _dirty;

        Recording _edit;
        DarkSlider _faceOffset, _smooth, _fixX, _fixY, _fixZ;
        Dictionary<string, float> _faceW;
        DarkCombo _fingerSrc, _calHeight, _calMeasure, _calLock, _calPreset, _calSnap;
        DarkCheck _followScale, _splitAvatars;

        bool _framedDevices;
        InfoLabel _gapInfo;
        DarkCombo _gapList, _gapTracker;
        DarkButton _gapToggle;

        DarkCheck _jumps,
            _locomotion,
            _cFbx,
            _cAnim,
            _cParams,
            _cOsc,
            _cHumr,
            _cFaceJson,
            _cFbxFace,
            _cFbxCam,
            _cFbxTrk;

        // ---- the SteamVR room as last seen: older takes are shown with it
        SteamVRRoom _knownRoom;
        double _knownRoomCheck = -100;
        bool _knownRoomLoaded;
        double _lastTick;
        ListBox _lib;

        int _libTipIndex = -1;

        double _liveBaseSync;
        AvatarMatch _liveMatch;
        string _liveMatchKey = "";

        AvatarProfile _liveProfile;
        string _liveProfileId;
        float _liveUserEye;
        double _liveVrcRead = -100;

        // ---- avatar mesh preview: the avatar's own FBX, loaded in the background whenever the shown skeleton changes
        AvatarProfile _meshFor;
        InfoLabel _meshInfo;
        Panel _meshList;
        string _meshLoading;
        TextBox _nameBox, _notesBox, _meshBox, _hotkeyBox, _dirBox, _triggerBox;
        DarkButton _playBtn, _loopBtn, _liveBtn, _replayBtn;
        bool _playing, _loop = true, _liveView = true;
        ListBox _projList;

        RecordButton _recBtn;
        OscSender _replay;

        SteamVRRoom _room;
        Recording _roomOf;
        DarkCheck _sOpenVR, _sHumrFollow, _sPreview, _sLegacy;
        DarkCombo _speed;
        double _speedMul = 1;

        bool _syncing;
        DarkTabs _tabs;
        string _timeFull;
        InfoLabel _timeLbl, _clipInfo, _bakeInfo, _liveInfo, _exportInfo, _avatarInfo, _scanInfo, _devInfo;
        string _timeTip;
        Timeline _timeline;
        Panel _transportPanel;
        int _transportRight, _transportY, _timeForW;
        Viewport3D _view;
        bool _viewFramed;
        DarkCombo _vrcChangeKind;
        InfoLabel _vrcTimeline;

        public MainForm(Settings s, Recorder rec)
        {
            S = s;
            Rec = rec;
            Avatars = new AvatarLibrary(s);
            // takes without a skeleton of their own: the base model their avatar matches in the library
            PoseBuilder.SourceResolver = r => Avatars.MatchTake(r).Entry?.Profile();
            Text = "VR Motion Recorder";
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Theme.Init(this);
            Font = Theme.UI;
            KeyPreview = true;
            MinimumSize = new Size(Theme.S(1000), Theme.S(640));
            StartPosition = FormStartPosition.Manual;
            var wa = Screen.PrimaryScreen.WorkingArea;
            int w = Math.Min(Theme.S(S.WindowW), wa.Width), h = Math.Min(Theme.S(S.WindowH), wa.Height);
            Bounds = S.WindowX >= 0 &&
                     Screen.AllScreens.Any(sc => sc.WorkingArea.Contains(S.WindowX + 50, S.WindowY + 50))
                ? new Rectangle(S.WindowX, S.WindowY, w, h)
                : new Rectangle(wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2, w, h);
            if (S.WindowMax) WindowState = FormWindowState.Maximized;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }

            BuildUI();
            Avatars.Updated += () => BeginInvokeSafe(() =>
            {
                _liveMatchKey = "";
                RefreshAvatarInfo();
                RefreshAvatarLists();
            });
            Rec.AvatarChanged += id => BeginInvokeSafe(RefreshAvatarInfo);
            Rec.RecordTriggerToggled += () => BeginInvokeSafe(ToggleRecord);
            Rec.HumrTakeStarted += t => BeginInvokeSafe(() =>
            {
                if (S.FollowHumrTakes && !Rec.IsRecording) StartRecording();
            });
            Rec.HumrTakeEnded += t => BeginInvokeSafe(() =>
            {
                if (S.FollowHumrTakes && Rec.IsRecording) StopRecording();
            });
            if (Rec.Calib != null) Rec.Calib.Captured += sn => BeginInvokeSafe(RefreshSnapList);
            _ui = new Timer { Interval = 16 };
            _ui.Tick += (o, e) => Tick();
            _ui.Start();
            _rebakeTimer = new Timer { Interval = 350 };
            _rebakeTimer.Tick += (o, e) =>
            {
                _rebakeTimer.Stop();
                Rebake();
            };
            RefreshLibrary();
            RefreshAvatarInfo();
            RefreshAvatarLists();
            if (S.UnityProjects.Count > 0 && S.AvatarModelMap.Count == 0) Avatars.ScanAsync();
        }

        double Playhead => _timeline.Playhead;
        static string KnownRoomFile => Path.Combine(Settings.AppDataDir, "steamvr_room.json");

        [DllImport("user32.dll")]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);

        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        void BeginInvokeSafe(Action a)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke(a);
            }
            catch { }
        }

        // =====================================================================================================  layout

        void BuildUI()
        {
            SuspendLayout();
            // ---- top bar: record, Takes | Scene, status chips
            var top = new Panel { Dock = DockStyle.Top, Height = Theme.S(52), BackColor = Theme.Panel };
            _recBtn = new RecordButton
            {
                Bounds = new Rectangle(Theme.S(12), Theme.S(8), Theme.S(164), Theme.S(36)),
                Sub = "Hotkey " + S.HotkeyRecord
            };
            _recBtn.Click += (o, e) => ToggleRecord();
            Theme.Tip(_recBtn,
                "Start / stop recording (global hotkey " + S.HotkeyRecord + ", set on the Settings tab)");
            int modeX = Theme.S(12 + 164 + 14);
            _chips = new StatusChips
            {
                Bounds = new Rectangle(modeX + Theme.S(74 * 2 + 4 + 18), Theme.S(11), Theme.S(900), Theme.S(30)),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
            };
            top.Controls.Add(_recBtn);
            top.Controls.Add(_chips);
            BuildModeSwitch(top, modeX);
            top.Resize += (o, e) => _chips.Width = Math.Max(0, top.Width - _chips.Left - Theme.S(12));
            top.Paint += (o, e) =>
            {
                using (var p = new Pen(Theme.Border))
                    e.Graphics.DrawLine(p, 0, top.Height - 1, top.Width, top.Height - 1);
            };

            // ---- library (left)
            var left = new Panel
            {
                Dock = DockStyle.Left, Width = Theme.S(270), BackColor = Theme.Panel,
                Padding = new Padding(Theme.S(10), Theme.S(6), Theme.S(10), Theme.S(10))
            };
            var libHead = new SectionLabel("Recordings") { Dock = DockStyle.Top };
            _lib = new ListBox
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.Panel, ForeColor = Theme.Text,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Theme.S(44), IntegralHeight = false, Font = Theme.UI
            };
            _lib.DrawItem += DrawLibItem;
            _lib.SelectedIndexChanged += (o, e) =>
            {
                if (!_sceneMode && _lib.SelectedItem is LibItem li && (_edit == null || _edit.FilePath != li.Path))
                    LoadRecording(li.Path);
            };
            _lib.MouseMove += (o, e) => LibTip(_lib.IndexFromPoint(e.Location));
            _lib.MouseLeave += (o, e) => LibTip(-1);
            HookLibraryDrag();
            var libBtns = new RowPanel { Dock = DockStyle.Bottom, Height = Theme.S(28) };
            var bOpen = new DarkButton("Folder");
            bOpen.Click += (o, e) =>
            {
                Directory.CreateDirectory(S.RecordingsDir);
                Process.Start("explorer.exe", S.RecordingsDir);
            };
            var bImport = new DarkButton("Open…");
            bImport.Click += (o, e) => OpenFile();
            var bDel = new DarkButton("Delete", DarkButton.Kind.Danger);
            bDel.Click += (o, e) => DeleteSelected();
            Theme.Tip(bOpen, "Open the recordings folder");
            Theme.Tip(bImport, "Open a .vrmr file from anywhere");
            Theme.Tip(bDel, "Move the selected take to the Recycle Bin");
            libBtns.Controls.Add(bOpen);
            libBtns.Controls.Add(bImport);
            libBtns.Controls.Add(bDel);
            var libSpacer = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(8), BackColor = Theme.Panel };
            left.Controls.Add(_lib);
            left.Controls.Add(libHead);
            left.Controls.Add(libSpacer);
            left.Controls.Add(libBtns);
            _libHead = libHead;
            _libBtns = libBtns;
            BuildScenesPanel(left);

            // ---- right tabs
            _tabs = new DarkTabs { Dock = DockStyle.Right, Width = Theme.S(330) };
            BuildSceneTab();
            _clipPage = _tabs.AddPage("Clip", new StackPanel());
            _exportPage = _tabs.AddPage("Export", new StackPanel());
            _livePage = _tabs.AddPage("Live", new StackPanel());
            _setPage = _tabs.AddPage("Settings", new StackPanel());
            BuildClipPage();
            BuildExportPage();
            BuildLivePage();
            BuildSettingsPage();

            // ---- bottom: transport + timeline
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(196), BackColor = Theme.Panel };
            var transport = new Panel { Dock = DockStyle.Top, Height = Theme.S(40), BackColor = Theme.Panel };
            typeof(Panel).GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(transport, true);
            _timeline = new Timeline { Dock = DockStyle.Fill };
            _timeline.Scrub += t =>
            {
                _liveView = false;
                ShowFrameAt(t);
            };
            _timeline.EditsChanged += () => { MarkDirty(true); };
            _timeline.RestoreCut += (a, b) =>
            {
                _edit?.Edits.Cuts.RemoveAll(c => c[0] == a && c[1] == b);
                MarkDirty(true);
            };
            BuildTransport(transport);
            bottom.Controls.Add(_timeline);
            bottom.Controls.Add(transport);
            _bottom = bottom;
            _transport = transport;

            // ---- center viewport
            _view = new Viewport3D
                { Dock = DockStyle.Fill, Empty = "Waiting for data…\n\nJoin VRChat with OSC on, or start SteamVR." };
            _view.ShowDevices = S.ShowDevices;
            _view.ShowDevicesChanged += () =>
            {
                S.ShowDevices = _view.ShowDevices;
                S.Save();
            };
            _view.ShowHmd = S.ShowHmdModel;
            _view.Toggles.Add(new Viewport3D.ViewToggle
            {
                Label = "Headset", Get = () => _view.ShowHmd, Set = v =>
                {
                    _view.ShowHmd = S.ShowHmdModel = v;
                    S.Save();
                    _view.Invalidate();
                },
                Tip = "The headset's model among the devices (off by default: it covers the avatar's face)",
                Available = () => _view.Markers.Any(m => m.Kind == Viewport3D.KHmd)
            });
            _view.ShowPlayArea = S.ShowPlayArea;
            _view.ShowBaseStations = S.ShowBaseStations;
            _view.Toggles.Add(new Viewport3D.ViewToggle
            {
                Label = "Playspace", Get = () => _view.ShowPlayArea, Set = v =>
                {
                    _view.ShowPlayArea = S.ShowPlayArea = v;
                    S.Save();
                    _view.Invalidate();
                },
                Tip =
                    "The SteamVR play area (Room Setup) as an outline on the floor. Live, and in takes: their own (recorded from 2026-11-17 on), else SteamVR's room as it is now when it fits the take. Faded: this view has none.",
                Available = () => _view.PlayArea != null
            });
            _view.Toggles.Add(new Viewport3D.ViewToggle
            {
                Label = "Base stations", Get = () => _view.ShowBaseStations, Set = v =>
                {
                    _view.ShowBaseStations = S.ShowBaseStations = v;
                    S.Save();
                    _view.Invalidate();
                },
                Tip =
                    "The SteamVR base stations with their field of view. Live, and in takes: their own (recorded from 2026-11-17 on), else SteamVR's room as it is now when it fits the take. Faded: this view has none.",
                Available = () => _view.Markers.Any(m => m.Kind == Viewport3D.KBaseStation)
            });
            // view options as chips in the view's corner (next to Devices)
            _view.Toggles.Insert(0,
                new Viewport3D.ViewToggle
                {
                    Label = "Mesh", Get = () => S.PreviewMesh, Set = SetPreviewMesh,
                    Tip =
                        "The avatar's own mesh (from its FBX). Single meshes can be hidden under Settings > Avatar preview."
                });
            _view.Toggles.Insert(1, new Viewport3D.ViewToggle
            {
                Label = "Skeleton", Get = () => S.PreviewSkeleton, Set = v =>
                {
                    S.PreviewSkeleton = v;
                    S.Save();
                    _view.Invalidate();
                },
                Tip = "The bones over the mesh"
            });
            _bottomSplit = new Splitter
            {
                Dock = DockStyle.Bottom, Height = Theme.S(5), BackColor = Theme.Border, MinExtra = Theme.S(160),
                MinSize = Theme.S(120)
            };
            _bottomSplit.SplitterMoved += (o, e) =>
            {
                if (_sceneMode) { S.NlaHeight = (int)(_bottom.Height / Theme.Dpi); }
            };

            Controls.Add(_view);
            Controls.Add(_bottomSplit);
            Controls.Add(bottom);
            Controls.Add(_tabs);
            Controls.Add(left);
            Controls.Add(top);
            ResumeLayout();
            CreateSceneEditor();
        }

        /// <summary>
        ///     The time readout, right-aligned after the buttons: "playhead / length", only the length when that is all
        ///     that fits (the timeline shows the playhead too), nothing in a narrow window.
        /// </summary>
        void PlaceTimeLabel()
        {
            if (_timeLbl == null || _transportPanel == null) return;
            string full = _edit != null ? $"{Theme.Time(Playhead)} / {Theme.Time(_edit.Duration)}" : "";
            if (full == _timeFull && _transportPanel.Width == _timeForW) return;
            _timeFull = full;
            _timeForW = _transportPanel.Width;
            int avail = _transportPanel.Width - _transportRight - Theme.S(12);
            int wf = TextRenderer.MeasureText(full, Theme.Mono).Width,
                ws = TextRenderer.MeasureText(_edit != null ? Theme.Time(_edit.Duration) : "", Theme.Mono).Width;
            string t = full.Length == 0 ? "" : wf <= avail ? full : ws <= avail ? Theme.Time(_edit.Duration) : "";
            if (_timeLbl.Text != t) _timeLbl.Text = t;
            int w = TextRenderer.MeasureText(t, Theme.Mono).Width + Theme.S(4);
            _timeLbl.SetBounds(_transportPanel.Width - w - Theme.S(8), _transportY, w, _timeLbl.Height);
            _timeLbl.Visible = t.Length > 0;
        }

        /// <summary>Take transport: playback | trim and cut | origin and calibration | live, the time on the right.</summary>
        void BuildTransport(Panel p)
        {
            int x = Theme.S(10), y = Theme.S(6), h = Theme.S(28);

            DarkButton B(string t, int w, Action a, string tip = null)
            {
                var b = new DarkButton(t) { Bounds = new Rectangle(x, y, Theme.S(w), h) };
                b.Click += (o, e) => a();
                if (tip != null) Theme.Tip(b, tip);
                p.Controls.Add(b);
                x += Theme.S(w) + Theme.S(3);
                return b;
            }

            void Sep()
            {
                x += Theme.S(3);
                _transportSeps.Add(x);
                x += Theme.S(7);
            }

            B("⏮", 32, () => SeekStart(), "Go to the start (Home)");
            _playBtn = B("▶", 40, TogglePlay, "Play / pause (Space)");
            B("⏭", 32, () => SeekEnd(), "Go to the end (End)");
            x += Theme.S(4);
            _loopBtn = B("Loop", 46, () =>
            {
                _loop = !_loop;
                _loopBtn.Checked = _loop;
                _loopBtn.Invalidate();
            }, "Loop playback");
            _loopBtn.Toggle = true;
            _loopBtn.Checked = true;
            _speed = new DarkCombo { Width = Theme.S(54) };
            _speed.Location = new Point(x, y + (h - _speed.Height) / 2);
            _speed.Items.AddRange("0.25x", "0.5x", "1x", "1.5x", "2x");
            _speed.SelectedIndex = 2;
            _speed.SelectedIndexChanged += (o, e) => _speedMul = new[] { 0.25, 0.5, 1, 1.5, 2 }[_speed.SelectedIndex];
            Theme.Tip(_speed, "Playback speed");
            p.Controls.Add(_speed);
            x += _speed.Width;
            Sep();
            B("In", 34, SetIn, "Trim the start at the playhead (I)");
            B("Out", 38, SetOut, "Trim the end at the playhead (O)");
            B("✂ Cut", 56, CutSelection,
                "Remove the selected range (Del). Drag in the lanes to select; right-click a cut to restore it.");
            B("Keep", 46, KeepSelection, "Trim to the selected range");
            B("Reset", 48, ResetEdits, "Remove all trims and cuts");
            _undoBtn = B("↶", 30, ClipUndo,
                "Undo the last edit of this take (Ctrl+Z): trims, cuts, Origin and turns, placement, calibration, tracker roles…");
            _redoBtn = B("↷", 30, ClipRedo, "Redo (Ctrl+Shift+Z or Ctrl+Y)");
            _undoBtn.Enabled = _redoBtn.Enabled = false;
            Sep();
            B("Origin", 52, OriginHere,
                "Put the avatar on the origin at the playhead: hips at X = Z = 0, feet on the floor, facing +Z. Moves the whole take (body, camera, devices) after the solve; Clip tab > Placement has exact values and a gizmo.");
            B("↺", 30, () => TurnTake(-90),
                "Turn the take 90° to the left (anticlockwise seen from above) about the hips at the playhead");
            B("↻", 30, () => TurnTake(90),
                "Turn the take 90° to the right (clockwise seen from above) about the hips at the playhead");
            B("Calibrate", 68, SetCalibrationHere, "Use this moment as the T-pose calibration (IK solve)");
            Sep();
            _liveBtn = B("Live", 42, () =>
                {
                    _liveView = !_liveView;
                    _playing = false;
                    UpdatePlayBtn();
                },
                "Live view: the 3D view shows what SteamVR and VRChat send right now (devices, play area, base stations, VRChat's head and camera; VRChat's IK avatar while recording in a HUMR world) instead of the take. Doesn't record. Playing, scrubbing or picking a take switches it off; with no take it is on.");
            _transportRight = x;
            _timeLbl = new InfoLabel() { Height = Theme.S(18), Wrap = false, Font = Theme.Mono, Color = Theme.TextDim };
            p.Controls.Add(_timeLbl);
            _transportPanel = p;
            _transportY = y + (h - _timeLbl.Height) / 2 + Theme.S(1);
            p.Resize += (o, e) => PlaceTimeLabel();
            PlaceTimeLabel();
            p.Paint += (o, e) =>
            {
                using (var pen = new Pen(Theme.Border))
                    foreach (int sx in _transportSeps)
                        e.Graphics.DrawLine(pen, sx, y + Theme.S(5), sx, y + h - Theme.S(5));
            };
        }

        static bool TryF(string t, out float v)
        {
            return float.TryParse((t ?? "").Trim().Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out v);
        }

        DarkButton Btn(string t, Action a, DarkButton.Kind k = DarkButton.Kind.Normal, string tip = null)
        {
            var b = new DarkButton(t, k);
            b.Click += (o, e) => a();
            if (tip != null) Theme.Tip(b, tip);
            return b;
        }

        TextBox Box(string t)
        {
            var b = new TextBox { Text = t };
            Theme.StyleInput(b);
            b.Height = Theme.S(24);
            return b;
        }

        static T Tip<T>(T c, string tip) where T : Control
        {
            return Theme.Tip(c, tip);
        }

        /// <summary>The Scene tab only shows in Scene mode (SetSceneMode lays the form out after switching).</summary>
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_tabs != null && _tabs.TabVisible(0) != _sceneMode) _tabs.SetTabVisible(0, _sceneMode);
        }

        void RefreshLibrary()
        {
            var items = new List<LibItem>();
            try
            {
                if (Directory.Exists(S.RecordingsDir))
                    foreach (string f in Directory.GetFiles(S.RecordingsDir, "*.vrmr")
                                 .OrderByDescending(File.GetLastWriteTimeUtc))
                    {
                        var li = new LibItem
                            { Path = f, Name = Path.GetFileNameWithoutExtension(f), Time = File.GetLastWriteTime(f) };
                        try
                        {
                            var m = RecordingIO.ReadSummary(f);
                            li.Name = m.Name;
                            li.Dur = Theme.Time(m.Duration, false);
                            li.Avatar = m.AvatarName ?? "";
                            li.World = m.World ?? "";
                            li.Sub = li.Avatar + (string.IsNullOrEmpty(li.World) ? "" : "  ·  " + li.World);
                            li.Humr = m.HasHumr;
                            li.Ik = m.HasDevices;
                            li.Face = m.HasFace;
                            li.Fingers = m.HasFingers;
                        }
                        catch { li.Sub = "unreadable"; }

                        items.Add(li);
                    }
            }
            catch (Exception ex) { AppLog.Warn("Library: " + ex.Message); }

            string sel = (_lib.SelectedItem as LibItem)?.Path ?? _edit?.FilePath;
            _lib.BeginUpdate();
            _lib.Items.Clear();
            foreach (var i in items) _lib.Items.Add(i);
            _lib.EndUpdate();
            for (int i = 0; i < _lib.Items.Count; i++)
                if (((LibItem)_lib.Items[i]).Path == sel)
                {
                    _lib.SelectedIndex = i;
                    break;
                }
        }

        void DrawLibItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var li = (LibItem)_lib.Items[e.Index];
            var g = e.Graphics;
            var bd = e.Bounds;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Theme.Panel2 : Theme.Panel)) g.FillRectangle(b, bd);
            if (sel)
                using (var b = new SolidBrush(Theme.Accent))
                    g.FillRectangle(b, bd.X, bd.Y + Theme.S(6), Theme.S(3), bd.Height - Theme.S(12));
            const TextFormatFlags tf = TextFormatFlags.NoPrefix | TextFormatFlags.Left |
                                       TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                                       TextFormatFlags.SingleLine;
            int x0 = bd.X + Theme.S(11), right = bd.Right - Theme.S(6);
            // line 1: name, length on the right
            int l1 = bd.Y + Theme.S(4), lh = Theme.S(18);
            int dw = TextRenderer.MeasureText(li.Dur, Theme.Small).Width;
            TextRenderer.DrawText(g, li.Dur, Theme.Small, new Rectangle(right - dw, l1, dw, lh), Theme.TextDim, tf);
            string name = li.Path == _edit?.FilePath && _edit != null ? _edit.Name + (_dirty ? " •" : "") : li.Name;
            TextRenderer.DrawText(g, name, Theme.UIBold, new Rectangle(x0, l1, right - dw - Theme.S(8) - x0, lh),
                Theme.Text, tf);
            // line 2: badges on the right, avatar · world in what is left (never under the badges)
            var sub = LibLine2(li, bd, out var badges);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var b in badges)
            {
                Theme.FillRound(g, Color.FromArgb(40, b.Item2), b.Item3, Theme.Sf(3));
                TextRenderer.DrawText(g, b.Item1, Theme.Tiny, Rectangle.Round(b.Item3), b.Item2,
                    TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine);
            }

            TextRenderer.DrawText(g, li.Sub ?? "", Theme.Small, sub, Theme.TextFaint, tf);
        }

        /// <summary>Second line of a takes-list item: the text's rectangle and the badges (text, colour, rectangle).</summary>
        Rectangle LibLine2(LibItem li, Rectangle bd, out List<Tuple<string, Color, RectangleF>> badges)
        {
            int x0 = bd.X + Theme.S(11),
                right = bd.Right - Theme.S(6),
                l2 = bd.Y + Theme.S(23),
                lh = Theme.S(18),
                bh = Theme.S(14);
            badges = new List<Tuple<string, Color, RectangleF>>();
            int bx = right;
            foreach (var badge in LibBadges(li))
            {
                int w = TextRenderer.MeasureText(badge.Item1, Theme.Tiny).Width + Theme.S(4);
                bx -= w;
                badges.Add(Tuple.Create(badge.Item1, badge.Item2, new RectangleF(bx, l2 + (lh - bh) / 2f, w, bh)));
                bx -= Theme.S(3);
            }

            return new Rectangle(x0, l2, Math.Max(0, bx - Theme.S(5) - x0), lh);
        }

        /// <summary>What a take has beyond SteamVR devices: VRChat's own IK (HUMR), face tracking, finger tracking.</summary>
        static IEnumerable<Tuple<string, Color>> LibBadges(LibItem li)
        {
            // right to left
            if (li.Face) yield return Tuple.Create("FACE", Theme.Warn);
            if (li.Fingers) yield return Tuple.Create("HANDS", Theme.Left);
            if (li.Humr) yield return Tuple.Create("HUMR", Theme.Ok);
            if (!li.Ik) yield return Tuple.Create("NO VR", Theme.TextDim);
        }

        void LibTip(int i)
        {
            if (i == _libTipIndex) return;
            _libTipIndex = i;
            if (i < 0 || i >= _lib.Items.Count)
            {
                Theme.Tip(_lib, null);
                return;
            }

            var li = (LibItem)_lib.Items[i];
            var data = new List<string>();
            if (li.Ik) data.Add("SteamVR devices");
            if (li.Humr) data.Add("VRChat IK (HUMR)");
            if (li.Face) data.Add("face tracking");
            if (li.Fingers) data.Add("finger tracking");
            Theme.Tip(_lib, $"{li.Name}\n{li.Time:yyyy-MM-dd HH:mm} · {li.Dur}" +
                            (li.Avatar.Length > 0 ? "\nAvatar: " + li.Avatar : "") +
                            (li.World.Length > 0 ? "\nWorld: " + li.World : "") +
                            (data.Count > 0 ? "\nData: " + string.Join(", ", data) : "") + (_sceneMode
                                ? "\n\nDrag onto a track (or double-click) to add it to the scene."
                                : ""));
        }

        void OpenFile()
        {
            using (var d = new OpenFileDialog
                       { Filter = "VR Motion Recording (*.vrmr)|*.vrmr", InitialDirectory = S.RecordingsDir })
                if (d.ShowDialog(this) == DialogResult.OK)
                    LoadRecording(d.FileName);
        }

        void DeleteSelected()
        {
            if (!(_lib.SelectedItem is LibItem li)) return;
            if (MessageBox.Show(this, $"Move \"{li.Name}\" to the Recycle Bin?", "Delete recording",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                FileSystem.DeleteFile(li.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                if (_edit?.FilePath == li.Path)
                {
                    _edit = null;
                    _baked = null;
                    _timeline.SetRecording(null);
                    _liveView = true;
                    UpdateClipUI();
                }
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Delete failed"); }

            RefreshLibrary();
        }

        // =====================================================================================================  recording

        AvatarProfile CurrentProfile()
        {
            return CurrentMatch()?.Entry?.Profile();
        }

        /// <summary>The base model of the avatar worn now (cached; the library matches by id, name, eye height).</summary>
        AvatarMatch CurrentMatch()
        {
            float sf = 0;
            lock (Rec.LiveLock)
                if (Rec.LiveParams.TryGetValue("ScaleFactor", out var v))
                    sf = v.Item2;
            string key = Rec.AvatarId + "|" + Rec.AvatarName + "|" + Math.Round(Rec.AvatarEyeHeight, 3) + "|" +
                         Math.Round(sf, 3);
            if (key != _liveMatchKey || _liveMatch == null)
            {
                _liveMatchKey = key;
                _liveMatch = Avatars.Match(Rec.AvatarId, Rec.AvatarName, Rec.AvatarEyeHeight, sf);
            }

            return _liveMatch;
        }

        void ToggleRecord()
        {
            if (Rec.IsRecording) StopRecording();
            else StartRecording();
        }

        void StartRecording()
        {
            if (Rec.IsRecording) return;
            SaveEditsIfDirty();
            _playing = false;
            UpdatePlayBtn();
            var m = CurrentMatch();
            var prof = m?.Entry?.Profile();
            var rec = Rec.StartRecording(prof);
            if (rec != null && m?.Entry != null)
                lock (rec.Sync)
                    rec.Extra["avatarBase"] = new Dictionary<string, object>
                        { ["id"] = m.Entry.Id, ["name"] = m.Entry.Label, ["how"] = m.How };
            if (rec != null && _sceneMode) _scene.NotifyRecordingStarted(rec);
            _recBtn.Recording = true;
            _liveView = true;
            _view.Recording = true;
            _recBtn.Invalidate();
        }

        void StopRecording()
        {
            var rec = Rec.StopRecording();
            _recBtn.Recording = false;
            _recBtn.Sub = "Hotkey " + S.HotkeyRecord;
            _recBtn.Invalidate();
            _view.Recording = false;
            if (rec == null) return;
            RefreshLibrary();
            if (_sceneMode)
            {
                _scene.NotifyRecordingStopped(rec);
                return;
            }

            SetRecording(rec);
        }

        // =====================================================================================================  editing

        void LoadRecording(string path, bool force = false)
        {
            if (Rec.IsRecording) return;
            if (!force && _edit?.FilePath == path) return;
            SaveEditsIfDirty();
            try { SetRecording(RecordingIO.Load(path)); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not open recording:\n" + ex.Message, "VR Motion Recorder");
            }
        }

        void SetRecording(Recording r)
        {
            _edit = r;
            _dirty = false;
            _baked = null;
            _playing = false;
            _exportAfterSolve = false;
            _autoProfile = r != null && r.Profile == null;
            if (_autoProfile)
                r.Profile = Avatars.MatchTake(r).Entry?.Profile(); // the base model the take's avatar matches
            _timeline.SetRecording(r);
            _liveView = r == null;
            UpdateClipUI();
            UpdatePlayBtn();
            Rebake();
            ResetClipHistory();
            for (int i = 0; i < _lib.Items.Count; i++)
                if (((LibItem)_lib.Items[i]).Path == r?.FilePath)
                {
                    _lib.SelectedIndex = i;
                    break;
                }
        }

        void MarkDirty(bool rebake)
        {
            if (_edit == null) return;
            _dirty = true;
            _lib.Invalidate();
            _clipInfo.Text = _clipInfo.Text; // keep
            if (rebake)
            {
                _rebakeTimer.Stop();
                _rebakeTimer.Start();
            }

            _timeline.Invalidate();
            CommitClipEdit(); // an undo step (Takes mode undo / redo)
        }

        BakeOptions MakeBakeOptions()
        {
            return new BakeOptions
            {
                Fps = S.ExportFps,
                FaceNaming = S.FaceNaming == "ARKit" ? FaceMapping.Naming.ARKit : FaceMapping.Naming.UnifiedExpressions,
                EyeYaw = S.EyeYawRange, EyePitch = S.EyePitchRange, Smoothing = S.Smoothing,
                Calibration = (_edit?.Edits.Calib ?? S.CalibrationDefaults).Clone()
            };
        }

        void Rebake()
        {
            var r = _edit;
            if (r == null)
            {
                _baked = null;
                return;
            }

            if (_baking)
            {
                _bakeAgain = true;
                return;
            }

            _baking = true;
            var opt = MakeBakeOptions();
            _bakeInfo.Text = "Solving…";
            _bakeInfo.Color = Theme.TextFaint;
            var sw = Stopwatch.StartNew();
            new Thread(() =>
            {
                BakedClip c = null;
                string err = null;
                try
                {
                    var target = Avatars.TargetFor(r, out _);
                    lock (TakeCache.SolveLock)
                    lock (r.Sync)
                        c = PoseBuilder.Bake(r, target, opt,
                            f => BeginInvokeSafe(() => _bakeInfo.Text = $"Solving… {f * 100:F0}%"));
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                    AppLog.Error("Bake: " + ex);
                }

                BeginInvokeSafe(() =>
                {
                    _baking = false;
                    if (_edit != r)
                    {
                        if (_bakeAgain)
                        {
                            _bakeAgain = false;
                            Rebake();
                        }

                        return;
                    }

                    _baked = c;
                    if (c != null)
                    {
                        _view.SetProfile(c.Profile);
                        ShowBakeResult(c, sw.ElapsedMilliseconds);
                        ShowCalibrationResult(c);
                        bool was = _syncing;
                        _syncing = true;
                        try { BuildDeviceRoles(); }
                        finally { _syncing = was; }

                        SyncGapsUI();
                        ShowFrameAt(_timeline.Playhead);
                        if (_view.Profile != null && !_viewFramed)
                        {
                            _view.FrameAll();
                            _viewFramed = true;
                        }
                    }
                    else
                    {
                        _bakeInfo.Text = "Solve failed: " + err;
                        _bakeInfo.Color = Theme.Rec;
                    }

                    _clipPage.PerformLayout();
                    if (_bakeAgain)
                    {
                        _bakeAgain = false;
                        Rebake();
                    }
                    else RunPendingExport(c != null); // an export that waited for this solve
                });
            }) { IsBackground = true, Name = "bake" }.Start();
        }

        void SaveEdits()
        {
            if (_edit == null || Rec.IsRecording) return;
            try
            {
                string path = _edit.FilePath ??
                              Path.Combine(S.RecordingsDir, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".vrmr");
                RecordingIO.Save(_edit, path);
                _dirty = false;
                RefreshLibrary();
            }
            catch (Exception ex) { MessageBox.Show(this, "Save failed: " + ex.Message); }
        }

        void SaveEditsIfDirty()
        {
            if (_dirty) SaveEdits();
        }

        void SetIn()
        {
            if (_edit == null) return;
            var e = _edit.Edits;
            e.TrimIn = Math.Min(Playhead, (e.TrimOut < 0 ? _edit.Duration : e.TrimOut) - 0.05);
            MarkDirty(true);
        }

        void SetOut()
        {
            if (_edit == null) return;
            var e = _edit.Edits;
            e.TrimOut = Math.Max(Playhead, e.TrimIn + 0.05);
            MarkDirty(true);
        }

        void CutSelection()
        {
            if (_edit == null || _timeline.SelA < 0 || _timeline.SelB - _timeline.SelA < 0.02) return;
            _edit.Edits.Cuts.Add(new[] { _timeline.SelA, _timeline.SelB });
            _timeline.SelA = _timeline.SelB = -1;
            MarkDirty(true);
        }

        void KeepSelection()
        {
            if (_edit == null || _timeline.SelA < 0) return;
            _edit.Edits.TrimIn = _timeline.SelA;
            _edit.Edits.TrimOut = _timeline.SelB;
            _timeline.SelA = _timeline.SelB = -1;
            MarkDirty(true);
        }

        void ResetEdits()
        {
            if (_edit == null) return;
            var e = _edit.Edits;
            e.TrimIn = 0;
            e.TrimOut = -1;
            e.Cuts.Clear();
            MarkDirty(true);
        }

        // Origin / ↺ ↻: MainForm.Placement.cs (the take's placement, after the solve). Older takes' originTime / originYaw
        // (the pre-solve origin the buttons used to set) still solve as they were.
        void SetCalibrationHere()
        {
            if (_edit == null) return;
            _edit.Edits.CalibrationTime = Playhead;
            MarkDirty(true);
            RefreshSnapList();
        }

        // =====================================================================================================  playback / tick

        void TogglePlay()
        {
            if (_edit == null || _baked == null) return;
            _playing = !_playing;
            _liveView = false;
            if (_playing)
            {
                var e = _edit.Edits;
                double tout = e.TrimOut < 0 ? _edit.Duration : e.TrimOut;
                if (Playhead >= tout - 1e-3 || Playhead < e.TrimIn) _timeline.Playhead = e.TrimIn;
                _replaySent.Clear();
            }

            UpdatePlayBtn();
        }

        void UpdatePlayBtn()
        {
            _playBtn.IconKind = _playing ? Theme.Icon.Pause : Theme.Icon.Play;
            _playBtn.Text = "";
            _playBtn.Invalidate();
            _liveBtn.Checked = _liveView;
            _liveBtn.Toggle = true;
            _liveBtn.Invalidate();
        }

        void SeekStart()
        {
            if (_edit == null) return;
            _timeline.Playhead = _edit.Edits.TrimIn;
            _liveView = false;
            ShowFrameAt(Playhead);
            _timeline.Invalidate();
        }

        void SeekEnd()
        {
            if (_edit == null) return;
            var e = _edit.Edits;
            _timeline.Playhead = e.TrimOut < 0 ? _edit.Duration : e.TrimOut;
            _liveView = false;
            ShowFrameAt(Playhead);
            _timeline.Invalidate();
        }

        void Tick()
        {
            double now = Clock.Now;
            double dt = _lastTick > 0 ? Math.Min(0.1, now - _lastTick) : 0;
            _lastTick = now;
            Rec.PollHumrTake();
            bool recording = Rec.IsRecording;
            if (recording)
                _recBtn.Sub = Theme.Time(Rec.RecordingTime) +
                              (Rec.Current != null ? $"  ·  {Rec.Current.Humr.Count} IK" : "");
            _chips.Update(Rec, CurrentMatch(), S);
            UpdateCalWatch();
            if (_livePage.Visible)
            {
                UpdateLiveInfo();
                if (now - _liveBaseSync > 0.5)
                {
                    _liveBaseSync = now;
                    SyncLiveBase(false);
                }
            }

            UpdateMeshPreview();
            UpdateKnownRoom();

            // throttle rendering while recording to stay light on VRChat
            _ui.Interval = recording ? S.PreviewWhileRecording ? 66 : 250 : _playing || _liveView ? 16 : 50;

            if (_sceneMode)
            {
                _ui.Interval = recording ? S.PreviewWhileRecording ? 66 : 250 : _scene.Playing ? 16 : 50;
                if (recording && !_scene.Playing) ShowLive();
                else _scene.Tick(dt);
                _view.InvalidateIfChanged(); // draw on demand: nothing changed, nothing painted
                return;
            }

            if (_playing && _edit != null && _baked != null)
            {
                double t = Playhead + dt * _speedMul;
                var segs = _edit.Edits.Segments(_edit.Duration);
                if (segs.Count == 0)
                {
                    _playing = false;
                    UpdatePlayBtn();
                }
                else
                {
                    double[] inSeg = segs.FirstOrDefault(s => t >= s[0] && t < s[1]);
                    if (inSeg == null)
                    {
                        double[] next = segs.FirstOrDefault(s => s[0] > Playhead - 1e-6 && s[0] >= t - 1e-6) ??
                                        segs.FirstOrDefault(s => s[0] > Playhead);
                        if (next != null && next[0] > Playhead) t = next[0];
                        else if (_loop) t = segs[0][0];
                        else
                        {
                            t = segs[segs.Count - 1][1];
                            _playing = false;
                            UpdatePlayBtn();
                        }
                    }

                    _timeline.Playhead = t;
                    _timeline.Invalidate();
                    ShowFrameAt(t);
                    if (_replayBtn.Checked) ReplayToVrc(t);
                }
            }
            else if (_liveView || recording) ShowLive();

            PlaceTimeLabel();
            string tt = _edit != null && Math.Abs(_edit.EditedDuration - _edit.Duration) > 0.005
                ? $"Edited length {Theme.Time(_edit.EditedDuration)} (trims and cuts)"
                : null;
            if (tt != _timeTip)
            {
                _timeTip = tt;
                Theme.Tip(_timeLbl, tt);
            }

            _view.InvalidateIfChanged(); // draw on demand: nothing changed, nothing painted
        }

        void ShowFrameAt(double sourceTime)
        {
            var c = _baked;
            var r = _edit;
            if (c == null || r == null) return;
            if (_view.Profile != c.Profile) _view.SetProfile(c.Profile);
            _view.PlayArea =
                null; // the live play area doesn't apply to a replay; the take's own (if recorded) follows below
            double te = r.Edits.SourceToEdited(sourceTime, r.Duration);
            int f = c.FrameAt(te);
            var wr = _view.NodeRot != null && _view.NodeRot.Length == c.Profile.Nodes.Count
                ? _view.NodeRot
                : new Quat[c.Profile.Nodes.Count];
            c.EvaluateNodes(f, _view.NodePos, wr);
            _view.NodeRot = wr;
            _view.MeshScale = c.ScaleAt(f);
            var fw = _faceW ?? (_faceW = new Dictionary<string, float>());
            fw.Clear();
            foreach (var kv in c.Face) fw[kv.Key] = kv.Value[f];
            _view.FaceWeights = fw;
            _view.Markers.Clear();
            if (c.Camera != null && c.CameraValid[f])
                _view.Markers.Add(new Viewport3D.Marker
                    { Pose = c.Camera[f], Color = Theme.Warn, Kind = 1, Label = "camera" });
            // devices as small models at the tracking scale (like VRChat, where the controllers shrink with the avatar)
            float devSize = c.Calibration != null && c.Calibration.TrackScale > 0.05f ? c.Calibration.TrackScale : 1f;
            // the headset too (as in the scene view): its model shows with the view's Headset chip
            foreach (var kv in c.Trackers)
                if (c.TrackerValid[kv.Key][f])
                {
                    bool hand = kv.Key == "LeftHand" || kv.Key == "RightHand", head = kv.Key == "Head";
                    _view.Markers.Add(new Viewport3D.Marker
                    {
                        Pose = kv.Value[f], Color = head ? Theme.Center : hand ? Theme.Accent : Theme.TextDim,
                        Kind = head ? Viewport3D.KHmd : hand ? Viewport3D.KController : Viewport3D.KTracker,
                        Size = devSize, NoDropLine = true
                    });
                }

            string roomNote = ShowRoom(c, r, f);
            _view.Overlay =
                $"{(c.BodySource == "humr" ? "VRChat IK" : c.BodySource == "ik" ? "IK solve" : "no body")}   frame {f}/{c.Frames - 1}" +
                (roomNote.Length > 0 ? "\n" + roomNote : "");
            _view.OverlayRight = r.Name;
            ApplyPlacementPreview(); // a placement the take isn't re-solved with yet (gizmo drags)
        }

        /// <summary>
        ///     The take's play area and base stations, mapped like its devices at frame f. Takes recorded from 2026-11-17 on carry
        ///     their own (SteamVRRoom); older ones are shown with the SteamVR room as last seen (live, else saved), which fits as
        ///     long as the room setup hasn't changed: it is only drawn when the take's headset stayed mostly inside that play
        ///     area.
        ///     Returns a note for the view's overlay ("" for the take's own room).
        /// </summary>
        string ShowRoom(BakedClip c, Recording r, int f)
        {
            if (_roomOf != r)
            {
                _roomOf = r;
                lock (r.Sync) _room = SteamVRRoom.From(r);
            }

            var room = _room;
            bool approx = false;
            if (room == null)
            {
                room = _knownRoom;
                approx = room != null;
            }

            if (room == null || c.Room == null || f < 0 || f >= c.Room.Length || c.RoomScale[f] <= 0) return "";
            bool wanted = _view.ShowPlayArea || _view.ShowBaseStations;
            if (approx && room.PlayArea != null)
            {
                float fit = RoomFit(r, room);
                if (fit < 0.6f)
                    return wanted
                        ? $"room: SteamVR's room now doesn't fit this take (its headset was inside the play area {fit * 100:0}% of the time), not shown"
                        : "";
            }

            var M = c.Room[f];
            float k = c.RoomScale[f];
            if (room.PlayArea != null) _view.PlayArea = room.PlayArea.Select(p => M.TransformPoint(p * k)).ToArray();
            foreach (var s in room.BaseStations)
                _view.Markers.Add(new Viewport3D.Marker
                {
                    Pose = new Pose(M.TransformPoint(s.Pose.pos * k), (M.rot * s.Pose.rot).Normalized),
                    Color = Color.FromArgb(150, 160, 185), Kind = Viewport3D.KBaseStation,
                    Size = k, FovL = s.FovL, FovR = s.FovR, FovT = s.FovT, FovB = s.FovB, NoDropLine = true
                });
            return approx && wanted ? "room: SteamVR's room as it is now (this take didn't record its own)" : "";
        }

        /// <summary>
        ///     Tick: every 5 s, SteamVR's room now (read-only); saved (under 1 KB) when it changed, so replays have it
        ///     without SteamVR running.
        /// </summary>
        void UpdateKnownRoom()
        {
            if (!_knownRoomLoaded)
            {
                _knownRoomLoaded = true;
                try
                {
                    if (File.Exists(KnownRoomFile))
                    {
                        var d = Json.ParseObject(File.ReadAllText(KnownRoomFile));
                        var rr = new Recording();
                        rr.Extra[SteamVRRoom.Key] = d.GetObj("room");
                        _knownRoom = SteamVRRoom.From(rr);
                    }
                }
                catch (Exception ex) { AppLog.Warn("SteamVR room file: " + ex.Message); }
            }

            double now = Clock.Now;
            if (now - _knownRoomCheck < 5) return;
            _knownRoomCheck = now;
            var live = Rec.OpenVR != null && Rec.OpenVR.Connected ? Rec.RoomNow(0) : null;
            if (live == null || SameRoom(live, _knownRoom)) return;
            _knownRoom = live;
            _roomFit.Clear();
            try
            {
                Directory.CreateDirectory(Settings.AppDataDir);
                File.WriteAllText(KnownRoomFile,
                    Json.Serialize(new Dictionary<string, object>
                        { ["seen"] = DateTime.UtcNow.ToString("o"), ["room"] = live.ToJson() }));
            }
            catch (Exception ex) { AppLog.Warn("SteamVR room file: " + ex.Message); }
        }

        /// <summary>The same room within tracking noise (base stations 2 cm, play area 1 cm).</summary>
        static bool SameRoom(SteamVRRoom a, SteamVRRoom b)
        {
            if (a == null || b == null) return a == b;
            if (a.PlayArea == null != (b.PlayArea == null) || a.BaseStations.Count != b.BaseStations.Count)
                return false;
            if (a.PlayArea != null)
                for (int i = 0; i < 4; i++)
                    if ((a.PlayArea[i] - b.PlayArea[i]).Length > 0.01f)
                        return false;
            foreach (var s in a.BaseStations)
            {
                var o = b.BaseStations.FirstOrDefault(x => x.Serial == s.Serial);
                if (o.Serial == null || (o.Pose.pos - s.Pose.pos).Length > 0.02f) return false;
            }

            return true;
        }

        /// <summary>
        ///     How well a room fits a take: the share of its headset's floor positions (raw SteamVR) inside the play area
        ///     grown by 0.5 m. 1 when there is no headset to check.
        /// </summary>
        float RoomFit(Recording r, SteamVRRoom room)
        {
            if (_roomFit.TryGetValue(r, out var cached) && cached.Item1 == room) return cached.Item2;
            float fit = 1;
            PoseStream hmd;
            lock (r.Sync)
                hmd = r.Devices.Where(d => d.DeviceClass == 1).OrderByDescending(d => d.Count).FirstOrDefault();
            var pa = room.PlayArea;
            if (hmd != null && hmd.Count > 10 && pa != null)
            {
                Vec3 u = pa[1] - pa[0], v = pa[3] - pa[0];
                float lu = u.Length, lv = v.Length;
                u = u * (1 / Math.Max(1e-4f, lu));
                v = v * (1 / Math.Max(1e-4f, lv));
                int inside = 0, n = 0;
                lock (r.Sync)
                    for (int i = 0; i < hmd.Count; i += 10)
                    {
                        var d = hmd.P[i] - pa[0];
                        d.y = 0;
                        float a = Vec3.Dot(d, u), b = Vec3.Dot(d, v);
                        n++;
                        if (a > -0.5f && a < lu + 0.5f && b > -0.5f && b < lv + 0.5f) inside++;
                    }

                fit = n > 0 ? inside / (float)n : 1;
            }

            _roomFit[r] = Tuple.Create(room, fit);
            return fit;
        }

        void ShowLive()
        {
            // live skeleton from VRChat IK (HUMR) when available
            var lm = CurrentMatch();
            string lkey = Rec.AvatarId + "|" + lm?.Entry?.Id;
            if (lkey != _liveProfileId)
            {
                _liveProfileId = lkey;
                _liveProfile = lm?.Entry?.Profile();
            }

            var prof = _liveProfile;
            _view.Markers.Clear();
            var cur = Rec.Current;
            string info = "";
            var ov = Rec.OpenVR;
            _view.PlayArea = ov != null && ov.Connected ? ov.PlayArea : null;
            int shown = 0;
            if (ov != null && ov.Connected)
                foreach (var d in ov.Devices())
                {
                    if (!d.HasPose) continue; // never tracked yet: listed in the Live tab only
                    var m = new Viewport3D.Marker
                    {
                        Pose = new Pose(d.Pos, d.Rot), Color = DeviceColor(d), Faded = !d.Valid,
                        Kind = d.Class == 1 ? Viewport3D.KHmd :
                            d.Class == 2 ? Viewport3D.KController :
                            d.Class == 4 ? Viewport3D.KBaseStation : Viewport3D.KTracker,
                        Label = DeviceName(d) + (d.Battery >= 0 ? $"  {d.Battery * 100:0}%" : "") +
                                (d.Valid ? "" : "\n" + d.TrackingText),
                        FovL = d.FovL, FovR = d.FovR, FovT = d.FovT, FovB = d.FovB
                    };
                    _view.Markers.Add(m);
                    shown++;
                }

            // first time devices show up without an avatar on screen: frame the whole tracking space
            if (shown > 0 && !_framedDevices && _view.Profile == null) _framedDevices = _view.FrameDevices();
            if (shown == 0) _framedDevices = false;
            if (Rec.VrcHeadTime > 0 && Clock.Now - Rec.VrcHeadTime < 1)
            {
                _view.Markers.Add(new Viewport3D.Marker
                    { Pose = Rec.VrcHead, Color = Color.FromArgb(180, 140, 255), Kind = 2, Label = "VRC head" });
                _view.Markers.Add(new Viewport3D.Marker
                    { Pose = Rec.VrcLeftWrist, Color = Color.FromArgb(180, 140, 255), Kind = 2 });
                _view.Markers.Add(new Viewport3D.Marker
                    { Pose = Rec.VrcRightWrist, Color = Color.FromArgb(180, 140, 255), Kind = 2 });
            }

            if (Rec.VrcCameraTime > 0 && Clock.Now - Rec.VrcCameraTime < 2)
                _view.Markers.Add(new Viewport3D.Marker
                    { Pose = Rec.VrcCamera, Color = Theme.Warn, Kind = 1, Label = "camera" });
            if (cur != null && cur.Humr.Count > 0 && (prof ?? cur.Profile) != null)
            {
                var p = prof ?? cur.Profile;
                if (_view.Profile != p) _view.SetProfile(p);
                var h = cur.Humr;
                Vec3 hips;
                Quat[] rot;
                lock (cur.Sync)
                {
                    int n = h.Count - 1;
                    hips = h.Hips[n];
                    rot = (Quat[])h.Rot[n].Clone();
                }

                var tmp = new BakedClip
                {
                    Profile = p, Frames = 1, Fps = 1, Hips = new[] { hips }, Rot = new[] { rot },
                    BoneExists = p.HumanExists
                };
                for (int b = 0; b < HumanBones.Count; b++)
                    if (rot[b].Norm < 0.5f)
                        rot[b] = p.HumanRestRot(b);
                var lwr = _view.NodeRot != null && _view.NodeRot.Length == p.Nodes.Count
                    ? _view.NodeRot
                    : new Quat[p.Nodes.Count];
                tmp.EvaluateNodes(0, _view.NodePos, lwr);
                // VRChat's world -> the SteamVR playspace the devices are drawn in (thumbstick moves, spawn point, tracking scale)
                if (Clock.Now - _liveVrcRead > 5)
                {
                    _liveVrcRead = Clock.Now;
                    var vs = VrcSettings.Read();
                    if (vs.PlayerHeight > 0.5f) _liveUserEye = vs.PlayerHeight * CalibrationSettings.EyeToHeightRatio;
                }

                _liveMap.Update(cur, p, Rec.AvatarEyeHeight > 0.2f ? Rec.AvatarEyeHeight : p.EyeHeight, _liveUserEye);
                if (_liveMap.Valid)
                {
                    for (int k = 0; k < p.Nodes.Count; k++)
                    {
                        _view.NodePos[k] = _liveMap.ToRaw(_view.NodePos[k]);
                        lwr[k] = _liveMap.ToRaw(lwr[k]);
                    }

                    info = $"live: VRChat IK (HUMR) over the devices (scale {_liveMap.Scale:0.000})";
                }

                _view.NodeRot = lwr;
                _view.MeshScale = _liveMap.Valid ? 1f / _liveMap.Scale : 1f;
                _view.FaceWeights = _view.Model != null ? LiveFaceValues() : null;
                if (!_liveMap.Valid) info = "live: VRChat IK (HUMR, VRChat's world)";
            }
            else if (_view.Profile != null && _edit == null) _view.SetProfile(null);

            _view.Overlay = (Rec.IsRecording ? "● REC  " + Theme.Time(Rec.RecordingTime) + "\n" : "LIVE\n") + info +
                            (info.Length > 0 ? "\n" : "") + SteamVRSummary(ov);
            _view.OverlayRight = Rec.AvatarName;
        }

        void UpdateMeshPreview()
        {
            var p = _view.Profile;
            _view.ShowMesh = S.PreviewMesh && !Rec.IsRecording; // keep recording light: skeleton only while recording
            _view.ShowSkeleton = S.PreviewSkeleton;
            if (p == _meshFor) return;
            _meshFor = p;
            string fbx = S.PreviewMesh ? SkinnedAvatar.FbxFor(p) : null;
            if (fbx == null)
            {
                _view.Model = null;
                UpdateMeshList();
                return;
            }

            if (_view.Model != null && string.Equals(_view.Model.SourcePath, fbx, StringComparison.OrdinalIgnoreCase) &&
                _view.Model.Profile.Nodes.Count == p.Nodes.Count) return;
            _view.Model = null;
            if (_meshLoading == fbx) return;
            _meshLoading = fbx;
            _meshInfo.Text = "Loading " + Path.GetFileName(fbx) + "…";
            new Thread(() =>
            {
                var m = SkinnedAvatar.ForProfile(p);
                BeginInvokeSafe(() =>
                {
                    _meshLoading = null;
                    if (_view.Profile == null || SkinnedAvatar.FbxFor(_view.Profile) != fbx ||
                        _view.Profile.Nodes.Count != p.Nodes.Count)
                    {
                        _meshFor = null;
                        return;
                    }

                    _view.Model = m;
                    UpdateMeshList();
                    _view.Invalidate();
                });
            }) { IsBackground = true, Name = "avatar mesh" }.Start();
        }

        void ReplayToVrc(double t)
        {
            var r = _edit;
            if (r == null) return;
            if (_replay == null)
            {
                var peer = Rec.OscQuery?.VrcOsc;
                _replay = new OscSender("127.0.0.1", peer?.Port ?? S.VrcInputPort);
            }

            double off = r.Edits.FaceOffset;
            foreach (var p in r.Params)
            {
                if (!(p.Name.StartsWith("FT/") || p.Name.StartsWith("v2/"))) continue;
                float v = p.Type == ParamType.Float ? p.Sample(t - off) : p.SampleStep(t - off);
                if (_replaySent.TryGetValue(p.Name, out float prev) && Math.Abs(prev - v) < 1e-4f) continue;
                _replaySent[p.Name] = v;
                _replay.SendParam(p.Name, p.Type, v);
            }
        }

        Dictionary<string, float> LiveFaceValues()
        {
            var fm = new FaceMapping { Mode = FaceMapping.Naming.UnifiedExpressions };
            Dictionary<string, Tuple<ParamType, float, double>> snap;
            lock (Rec.LiveLock) snap = new Dictionary<string, Tuple<ParamType, float, double>>(Rec.LiveParams);
            return fm.Evaluate(n => snap.TryGetValue(n, out var v) ? v.Item2 : null);
        }

        // =====================================================================================================  keys / hotkey / close

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterRecordHotkey();
        }

        void RegisterRecordHotkey()
        {
            UnregisterHotKey(Handle, 1);
            if (!ParseHotkey(S.HotkeyRecord, out uint mods, out uint vk)) return;
            if (!RegisterHotKey(Handle, 1, mods | 0x4000 /*NOREPEAT*/, vk))
                AppLog.Warn("Hotkey " + S.HotkeyRecord + " is taken by another app");
        }

        static bool ParseHotkey(string s, out uint mods, out uint vk)
        {
            mods = 0;
            vk = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            foreach (string part in s.Split('+').Select(x => x.Trim()))
            {
                switch (part.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control": mods |= 2; break;
                    case "alt": mods |= 1; break;
                    case "shift": mods |= 4; break;
                    case "win": mods |= 8; break;
                    default:
                        if (Enum.TryParse(part, true, out Keys k)) vk = (uint)k;
                        else return false;
                        break;
                }
            }

            return vk != 0;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == 1)
            {
                ToggleRecord();
                return;
            }

            base.WndProc(ref m);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (ActiveControl is TextBox) return base.ProcessCmdKey(ref msg, keyData);
            if (_sceneMode) return SceneKey(keyData) || base.ProcessCmdKey(ref msg, keyData);
            if (_placeTool != null && _view.Tool == _placeTool && _placeTool.KeyDown(_view, keyData))
            {
                _view.Invalidate();
                return true;
            }

            switch (keyData)
            {
                case Keys.Space:
                    TogglePlay();
                    return true;
                case Keys.I:
                    SetIn();
                    return true;
                case Keys.O:
                    SetOut();
                    return true;
                case Keys.Delete:
                    CutSelection();
                    return true;
                case Keys.Home:
                    SeekStart();
                    return true;
                case Keys.End:
                    SeekEnd();
                    return true;
                case Keys.Control | Keys.S:
                    SaveEdits();
                    return true;
                case Keys.Control | Keys.Z:
                    ClipUndo();
                    return true;
                case Keys.Control | Keys.Y:
                case Keys.Control | Keys.Shift | Keys.Z:
                    ClipRedo();
                    return true;
                case Keys.F:
                    _view.FrameAll();
                    return true;
                case Keys.Left:
                    Step(-1);
                    return true;
                case Keys.Right:
                    Step(1);
                    return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        void Step(int frames)
        {
            if (_edit == null || _baked == null) return;
            _playing = false;
            UpdatePlayBtn();
            _liveView = false;
            _timeline.Playhead = Math.Max(0, Math.Min(_edit.Duration, Playhead + frames / (double)_baked.Fps));
            ShowFrameAt(Playhead);
            _timeline.Invalidate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_scene != null && !_scene.ConfirmDiscard())
            {
                e.Cancel = true;
                return;
            }

            if (Rec.IsRecording) StopRecording();
            SaveEditsIfDirty();
            _scene?.Dispose();
            S.WindowMax = WindowState == FormWindowState.Maximized;
            if (WindowState == FormWindowState.Normal)
            {
                S.WindowX = Left;
                S.WindowY = Top;
                S.WindowW = (int)(Width / Theme.Dpi);
                S.WindowH = (int)(Height / Theme.Dpi);
            }

            S.Save();
            UnregisterHotKey(Handle, 1);
            _replay?.Dispose();
            base.OnFormClosing(e);
        }

        // =====================================================================================================  library

        sealed class LibItem
        {
            public bool Humr, Ik, Face, Fingers;

            /// <summary>Sub: avatar · world (second line); Dur: the length.</summary>
            public string Path, Name, Sub, Dur = "", Avatar = "", World = "";

            public DateTime Time;

            public override string ToString()
            {
                return Name;
            }
        }
    }
}
