using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace VRMR.UI
{
    /// <summary>Scene mode: the NLA editor, the scenes list and the Scene tab, next to the take editor (Takes mode).</summary>
    public sealed partial class MainForm
    {
        bool _sceneMode;
        SceneEditor _scene;
        Panel _bottom, _transport, _scenesPanel, _sceneTabHost;
        Splitter _bottomSplit;
        SectionLabel _libHead;
        RowPanel _libBtns;
        ListBox _sceneList;
        DarkButton _modeTakes, _modeScene;
        int _takeBottomH;

        // ---------------------------------------------------------------- layout pieces (called from BuildUI)

        void BuildModeSwitch(Panel top, int x)
        {
            int h = Theme.S(32), y = (top.Height - h) / 2, w = Theme.S(74);
            _modeTakes = new DarkButton("Takes") { Bounds = new Rectangle(x, y, w, h), Toggle = true, Checked = true };
            _modeScene = new DarkButton("Scene") { Bounds = new Rectangle(x + w + Theme.S(4), y, w, h), Toggle = true };
            Theme.Tip(_modeTakes, "Takes: record, replay, calibrate and export single takes");
            Theme.Tip(_modeScene, "Scene: cut, layer, keyframe and re-record takes on NLA tracks (non-destructive)");
            _modeTakes.Click += (o, e) => SetSceneMode(false);
            _modeScene.Click += (o, e) => SetSceneMode(true);
            top.Controls.Add(_modeTakes); top.Controls.Add(_modeScene);
        }

        void BuildScenesPanel(Panel left)
        {
            _scenesPanel = new Panel { Dock = DockStyle.Top, Height = Theme.S(196), BackColor = Theme.Panel, Visible = false };
            var head = new SectionLabel("Scenes") { Dock = DockStyle.Top };
            _sceneList = new ListBox
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.Panel, ForeColor = Theme.Text,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Theme.S(26), IntegralHeight = false, Font = Theme.UI,
            };
            _sceneList.DrawItem += DrawSceneItem;
            _sceneList.SelectedIndexChanged += (o, e) =>
            {
                if (_syncingScenes || !(_sceneList.SelectedItem is SceneItem si)) return;
                if (_scene.Scene?.FilePath == null || !string.Equals(Path.GetFullPath(_scene.Scene.FilePath), si.Path, StringComparison.OrdinalIgnoreCase))
                    if (!_scene.Open(si.Path)) SyncSceneListSelection();
            };
            var btns = new RowPanel { Dock = DockStyle.Bottom, Height = Theme.S(30) };
            var bNew = new DarkButton("New"); bNew.Click += (o, e) => { _scene.NewScene(); RefreshSceneList(); };
            var bFolder = new DarkButton("Folder"); bFolder.Click += (o, e) => { Directory.CreateDirectory(_scene.ScenesDir); Process.Start("explorer.exe", _scene.ScenesDir); };
            var bDel = new DarkButton("Delete", DarkButton.Kind.Danger); bDel.Click += (o, e) => DeleteSelectedScene();
            Theme.Tip(bDel, "Moves the scene file to the Recycle Bin. Its takes are not touched.");
            btns.Controls.Add(bNew); btns.Controls.Add(bFolder); btns.Controls.Add(bDel);
            var gap = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(10), BackColor = Theme.Panel };
            _scenesPanel.Controls.Add(_sceneList); _scenesPanel.Controls.Add(head); _scenesPanel.Controls.Add(gap); _scenesPanel.Controls.Add(btns);
            left.Controls.Add(_scenesPanel); // added last = docked first: above the takes list's header
        }

        void BuildSceneTab()
        {
            _sceneTabHost = _tabs.AddPage("Scene", new Panel { BackColor = Theme.Panel });
        }

        void CreateSceneEditor()
        {
            var ctx = new SceneContext
            {
                AvatarById = id => Avatars.All().FirstOrDefault(e => e.Id == id)?.Profile(),
                TakeAvatar = r => Avatars.TargetFor(r, out _),
                BakeOptionsFor = r => new BakeOptions
                {
                    Fps = S.ExportFps, FaceNaming = S.FaceNaming == "ARKit" ? FaceMapping.Naming.ARKit : FaceMapping.Naming.UnifiedExpressions,
                    EyeYaw = S.EyeYawRange, EyePitch = S.EyePitchRange, Smoothing = S.Smoothing,
                    Calibration = (r.Edits.Calib ?? S.CalibrationDefaults).Clone(),
                },
            };
            _scene = new SceneEditor(S, ctx, _view, this);
            _scene.Inspector.Dock = DockStyle.Fill;
            _sceneTabHost.Controls.Add(_scene.Inspector);
            _scene.Panel.Dock = DockStyle.Fill;
            _scene.Panel.Visible = false;
            _bottom.Controls.Add(_scene.Panel);
            _scene.Panel.BringToFront();
            _scene.AvatarChoices = () => Avatars.All().Select(e => (e.Id, e.Label)).ToList();
            _scene.SelectedTakePath = () => (_lib.SelectedItem as LibItem)?.Path;
            _scene.OpenTake = path => { SetSceneMode(false); LoadRecording(path); _tabs.Select(1); };
            _scene.StartRecording = () => { if (!Rec.IsRecording) StartRecording(); };
            _scene.StopRecording = () => { if (Rec.IsRecording) StopRecording(); };
            _scene.IsRecording = () => Rec.IsRecording;
            _scene.RecordingTime = () => Rec.RecordingTime;
            _scene.SceneChanged += () => { SyncSceneListSelection(); _view.Invalidate(); };
            _scene.Saved += RefreshSceneList;
            Avatars.Updated += () => BeginInvokeSafe(() => _scene.RequestEvaluate());
            _tabs.Select(1); // Clip
        }

        // ---------------------------------------------------------------- mode

        void SetSceneMode(bool on)
        {
            _modeTakes.Checked = !on; _modeScene.Checked = on; _modeTakes.Invalidate(); _modeScene.Invalidate();
            if (on == _sceneMode) return;
            if (on && Rec.IsRecording && _scene.Scene == null) { /* recording keeps going; the scene opens alongside */ }
            _sceneMode = on;
            SuspendLayout();
            if (on)
            {
                SaveEditsIfDirty();
                _playing = false; UpdatePlayBtn();
                _takeBottomH = _bottom.Height;
                _timeline.Visible = false; _transport.Visible = false;
                _scene.Panel.Visible = true;
                _bottom.Height = Theme.S(Math.Max(160, S.NlaHeight));
                _scenesPanel.Visible = true;
                _libHead.Text = "Takes (drag onto a track)";
                _libHead.Invalidate();
                _tabs.Select(0);
                RefreshSceneList();
                if (_scene.Scene == null)
                {
                    string path = File.Exists(S.LastScene) ? S.LastScene : _sceneItems.FirstOrDefault()?.Path;
                    if (path != null) _scene.Open(path); else _scene.NewScene();
                    RefreshSceneList();
                }
                _liveView = false;
                _view.Tool = null;
                _scene.SetTool(_scene.ActiveTool);
                _scene.RequestEvaluate();
                _scene.ShowFrame();
            }
            else
            {
                _scene.Playing = false;
                _scene.Panel.Visible = false;
                _timeline.Visible = true; _transport.Visible = true;
                _bottom.Height = _takeBottomH > 0 ? _takeBottomH : Theme.S(196);
                _scenesPanel.Visible = false;
                _libHead.Text = "Recordings"; _libHead.Invalidate();
                _view.Tool = _gizmoBtn != null && _gizmoBtn.Checked ? _placeTool : null; _view.Figures.Clear(); _view.Markers.Clear();
                if (_tabs.Selected == 0) _tabs.Select(1);
                _view.SetProfile(null);
                if (_baked != null && _edit != null) { _view.SetProfile(_baked.Profile); ShowFrameAt(Playhead); } else _liveView = true;
                UpdatePlayBtn();
            }
            ResumeLayout();
            PerformLayout();
            _view.Invalidate();
        }

        // ---------------------------------------------------------------- scenes list

        sealed class SceneItem { public string Path, Name; public DateTime Time; public override string ToString() => Name; }
        readonly List<SceneItem> _sceneItems = new List<SceneItem>();
        bool _syncingScenes;

        void RefreshSceneList()
        {
            _sceneItems.Clear();
            try
            {
                string dir = _scene.ScenesDir;
                if (Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir, "*" + Scene.Extension).OrderByDescending(File.GetLastWriteTimeUtc))
                        _sceneItems.Add(new SceneItem { Path = Path.GetFullPath(f), Name = Path.GetFileNameWithoutExtension(f), Time = File.GetLastWriteTime(f) });
            }
            catch (Exception ex) { AppLog.Warn("Scenes: " + ex.Message); }
            _syncingScenes = true;
            _sceneList.BeginUpdate(); _sceneList.Items.Clear();
            foreach (var i in _sceneItems) _sceneList.Items.Add(i);
            _sceneList.EndUpdate();
            _syncingScenes = false;
            SyncSceneListSelection();
            // the list sizes to its scenes (2..8 rows)
            int rows = Math.Max(2, Math.Min(8, _sceneItems.Count));
            _scenesPanel.Height = Theme.S(26) + rows * _sceneList.ItemHeight + Theme.S(10) + Theme.S(30) + Theme.S(4);
        }

        void SyncSceneListSelection()
        {
            _syncingScenes = true;
            string cur = _scene.Scene?.FilePath != null ? Path.GetFullPath(_scene.Scene.FilePath) : null;
            int idx = _sceneItems.FindIndex(i => string.Equals(i.Path, cur, StringComparison.OrdinalIgnoreCase));
            _sceneList.SelectedIndex = idx;
            _sceneList.Invalidate();
            _syncingScenes = false;
        }

        void DrawSceneItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var it = (SceneItem)_sceneList.Items[e.Index];
            var g = e.Graphics;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Theme.Panel2 : Theme.Panel)) g.FillRectangle(b, e.Bounds);
            if (sel) using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, e.Bounds.X, e.Bounds.Y + Theme.S(4), Theme.S(3), e.Bounds.Height - Theme.S(8));
            bool open = _scene.Scene?.FilePath != null && string.Equals(Path.GetFullPath(_scene.Scene.FilePath), it.Path, StringComparison.OrdinalIgnoreCase);
            string name = open ? _scene.Scene.Name + (_scene.Dirty ? " •" : "") : it.Name;
            TextRenderer.DrawText(g, name, open ? Theme.UIBold : Theme.UI, new Rectangle(e.Bounds.X + Theme.S(10), e.Bounds.Y, e.Bounds.Width - Theme.S(70), e.Bounds.Height), Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, it.Time.ToString("MM-dd HH:mm"), Theme.Small, new Rectangle(e.Bounds.X, e.Bounds.Y, e.Bounds.Width - Theme.S(6), e.Bounds.Height), Theme.TextFaint, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        void DeleteSelectedScene()
        {
            if (!(_sceneList.SelectedItem is SceneItem si)) return;
            if (MessageBox.Show(this, $"Move the scene \"{si.Name}\" to the Recycle Bin?\nIts takes are not touched.", "Delete scene", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            bool open = _scene.Scene?.FilePath != null && string.Equals(Path.GetFullPath(_scene.Scene.FilePath), si.Path, StringComparison.OrdinalIgnoreCase);
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(si.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                if (open) { _scene.Close(); _view.SetProfile(null); }
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Delete failed"); }
            RefreshSceneList();
        }

        // ---------------------------------------------------------------- takes list -> NLA

        Point _libDown; int _libDownIndex = -1;

        void HookLibraryDrag()
        {
            _lib.MouseDown += (o, e) => { _libDown = e.Location; _libDownIndex = e.Button == MouseButtons.Left ? _lib.IndexFromPoint(e.Location) : -1; };
            _lib.MouseMove += (o, e) =>
            {
                if (!_sceneMode || _libDownIndex < 0 || e.Button != MouseButtons.Left) return;
                if (Math.Abs(e.X - _libDown.X) + Math.Abs(e.Y - _libDown.Y) < Theme.S(6)) return;
                if (_libDownIndex >= _lib.Items.Count) return;
                var li = (LibItem)_lib.Items[_libDownIndex];
                _libDownIndex = -1;
                var data = new DataObject();
                data.SetData(NlaEditor.TakeFormat, li.Path);
                _lib.DoDragDrop(data, DragDropEffects.Copy);
            };
            _lib.MouseUp += (o, e) => _libDownIndex = -1;
            _lib.DoubleClick += (o, e) =>
            {
                if (!_sceneMode || !(_lib.SelectedItem is LibItem li)) return;
                _scene.AddTakeAt(li.Path, _scene.SelectedActor, _scene.SelectedTrack, _scene.Playhead);
            };
        }

        // ---------------------------------------------------------------- keys

        bool SceneKey(Keys k)
        {
            var ed = _scene;
            bool viewTool = ActiveControl == _view && _view.Tool != null;
            switch (k)
            {
                case Keys.Space: ed.TogglePlay(); return true;
                case Keys.Home: ed.Seek(ed.Scene?.Start ?? 0); return true;
                case Keys.End: ed.Seek(ed.Scene?.RangeEnd ?? 0); return true;
                case Keys.Control | Keys.S: ed.Save(); RefreshSceneList(); return true;
                case Keys.Control | Keys.Z: ed.Undo(); return true;
                case Keys.Control | Keys.Y:
                case Keys.Control | Keys.Shift | Keys.Z: ed.Redo(); return true;
            }
            if (viewTool) return false; // the tool gets its keys
            switch (k)
            {
                case Keys.Left: ed.Step(-1); return true;
                case Keys.Right: ed.Step(1); return true;
                case Keys.Delete: ed.Nla.DeleteSelected(); return true;
                case Keys.K: ed.Nla.SplitAtPlayhead(); return true;
                case Keys.T: ed.Nla.TransitionSelected(); return true;
                case Keys.M: ed.Nla.MuteSelected(); return true;
                case Keys.Shift | Keys.M: ed.Nla.AddMarker(ed.Playhead); return true;
                case Keys.Shift | Keys.D: ed.Nla.DuplicateSelected(); return true;
                case Keys.F: if (ActiveControl == ed.Nla) ed.Nla.FrameAll(); else _view.FrameAll(); return true;
                case Keys.I: if (ed.Scene != null) ed.Edit("Scene start", () => ed.Scene.Start = Math.Min(ed.Playhead, ed.Scene.RangeEnd - 0.1)); return true;
                case Keys.O: if (ed.Scene != null) ed.Edit("Scene end", () => ed.Scene.End = Math.Max(ed.Playhead, ed.Scene.Start + 0.1)); return true;
            }
            return false;
        }
    }
}
