using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace VRMR.UI
{
    public sealed partial class MainForm
    {
        FoldSection _secLiveDevices, _secLiveSources, _secSetRec, _secSources, _secAvLib, _secPreview, _secUnity;
        InfoLabel _avLibDetail;

        // Live tab: what VRChat and SteamVR send right now; connection diagnostics closed by default.
        void BuildLivePage()
        {
            var p = _livePage;
            var av = p.Fold("Avatar in VRChat", true, "live.avatar");
            _liveAvatarInfo = av.Add(new InfoLabel("") { Color = Theme.TextDim });
            _liveBase = av.Field("Base model", new DarkCombo(), "The base model for the live view and new takes; remembered for this VRChat avatar.").Editor as DarkCombo;
            _liveBase.SelectedIndexChanged += (o, e) => OnLiveBasePicked();
            _secLiveDevices = p.Fold("SteamVR devices", true, "live.devices");
            _devList = _secLiveDevices.Add(new DeviceList());
            p.Fold("Face tracking", true, "live.face").Add(new FaceBars(() => LiveFaceValues()));
            _secLiveSources = p.Fold("Connections", false, "live.sources");
            _liveInfo = _secLiveSources.Add(new InfoLabel("") { Font = Theme.Mono, Color = Theme.TextDim });
        }

        // Settings tab: app-wide settings only (per-take settings are on the Clip tab, view options in the 3D view).
        void BuildSettingsPage()
        {
            var p = _setPage;
            var rec = _secSetRec = p.Fold("Recording", true, "set.rec");
            _dirBox = Box(S.RecordingsDir);
            rec.Field("Folder", _dirBox, "Where takes are saved (scenes go into its Scenes folder)");
            _dirBox.Leave += (o, e) => { S.RecordingsDir = _dirBox.Text.Trim(); S.Save(); RefreshLibrary(); };
            _hotkeyBox = Box(S.HotkeyRecord);
            rec.Field("Hotkey", _hotkeyBox, "Global record hotkey, e.g. F9 or Ctrl+Shift+R (works while VRChat has the focus)");
            _hotkeyBox.Leave += (o, e) => { S.HotkeyRecord = _hotkeyBox.Text.Trim(); RegisterRecordHotkey(); _recBtn.Sub = "Hotkey " + S.HotkeyRecord; _recBtn.Invalidate(); Theme.Tip(_recBtn, "Start / stop recording (global hotkey " + S.HotkeyRecord + ")"); S.Save(); SyncSettingsSummaries(); };
            _triggerBox = Box(S.RecordTriggerParam);
            rec.Field("Avatar toggle", _triggerBox, "An avatar bool parameter that starts and stops recording (optional)");
            _triggerBox.Leave += (o, e) => { S.RecordTriggerParam = _triggerBox.Text.Trim(); S.Save(); };
            _sHumrFollow = rec.Add(Tip(new DarkCheck("Follow the world's HUMR recorder", S.FollowHumrTakes), "Start and stop with the in-world HUMR recorder button"));
            _sHumrFollow.CheckedChanged += (o, e) => { S.FollowHumrTakes = _sHumrFollow.Checked; S.Save(); };
            _sPreview = rec.Add(Tip(new DarkCheck("3D preview while recording", S.PreviewWhileRecording), "Off keeps the app lighter on VRChat while recording (the view updates 4 times a second)."));
            _sPreview.CheckedChanged += (o, e) => { S.PreviewWhileRecording = _sPreview.Checked; S.Save(); };
            var watchCal = rec.Add(Tip(new DarkCheck("Save every VRChat calibration", S.WatchCalibrations), "Every VRChat calibration (Calibrate FBT + the triggers) goes into the calibration library. The calibration settings themselves are per take on the Clip tab; with no take selected they edit the defaults."));
            watchCal.CheckedChanged += (o, e) => { S.WatchCalibrations = watchCal.Checked; if (Rec.Calib != null) Rec.Calib.Enabled = watchCal.Checked; S.Save(); };
            var autoLink = rec.Add(Tip(new DarkCheck("New takes use the latest calibration", S.AutoLinkCalibration), "New takes link the latest matching calibration from the library"));
            autoLink.CheckedChanged += (o, e) => { S.AutoLinkCalibration = autoLink.Checked; S.Save(); };

            var src = _secSources = p.Fold("Sources", true, "set.sources");
            _sOpenVR = src.Add(Tip(new DarkCheck("SteamVR devices", S.EnableOpenVR), "Runs as a background SteamVR app; never starts SteamVR."));
            _sOpenVR.CheckedChanged += (o, e) => { S.EnableOpenVR = _sOpenVR.Checked; S.Save(); Rec.RestartOpenVR(); SyncSettingsSummaries(); };
            var sFingers = src.Add(Tip(new DarkCheck("SteamVR finger tracking", S.CaptureFingers) { Indent = 20 }, "Index skeletal input, read-only"));
            sFingers.CheckedChanged += (o, e) => { S.CaptureFingers = sFingers.Checked; S.Save(); if (S.EnableOpenVR) Rec.RestartOpenVR(); };
            _sOpenVR.CheckedChanged += (o, e) => sFingers.Enabled = _sOpenVR.Checked;
            sFingers.Enabled = S.EnableOpenVR;
            _sLegacy = src.Add(Tip(new DarkCheck("Also listen on UDP 9001", S.ListenLegacyPort), "VRChat's default OSC port, when no other app uses it (restart to apply)"));
            _sLegacy.CheckedChanged += (o, e) => { S.ListenLegacyPort = _sLegacy.Checked; S.Save(); };
            src.Row(Btn("Open log", () => { var f = Path.Combine(Settings.AppDataDir, "vrmr.log"); if (File.Exists(f)) Process.Start("notepad.exe", f); }));

            var al = _secAvLib = p.Fold("Avatar library", true, "set.avatars");
            Tip(al, "Base models the takes are solved onto. Add a Booth avatar's .unitypackage (or the .zip it came in) together with its face-tracking add-on: the app reads the humanoid rig from the package, merges the add-on's face-tracking shapes (BlendShare) into an FBX and copies the textures. No Unity project needed. The avatar you wear in VRChat is matched by a linked avatar id, by name, or by eye height; pick it by hand on the Live tab or per take on the Clip tab.");
            _avList = al.Add(new DarkListBox { Height = Theme.S(112) });
            _avList.SelectedIndexChanged += (o, e) => SyncAvatarEntryUI();
            al.Row(Btn("Add…", AddAvatar, DarkButton.Kind.Accent, "Add a base model: the avatar's .unitypackage (or .zip) and its face-tracking add-on, or a Unity model (.fbx with its .meta)"),
                   Btn("Remove", RemoveAvatar, tip: "Remove from the library (its files go to the Recycle Bin; takes keep their own copy of the skeleton)"),
                   Btn("Default", () => { if (_avList.SelectedItem is AvatarEntry ae) { S.DefaultAvatar = ae.Id; S.Save(); _liveMatchKey = ""; RefreshAvatarLists(); } }, tip: "Use for avatars that match nothing"));
            _avPatterns = Box("");
            al.Field("VRChat names", _avPatterns, "VRChat avatar names that mean this base model, comma separated (e.g. the model name, also in another script)");
            _avPatterns.Leave += (o, e) => SaveAvatarPatterns();
            _avLibInfo = al.Add(new InfoLabel("") { Color = Theme.TextFaint });
            var ad = al.Fold("Details", false, "set.avatars.details");
            _avLibDetail = ad.Add(new InfoLabel("") { Color = Theme.TextFaint });
            ad.Row(Btn("Open folder", () => { Directory.CreateDirectory(Avatars.Dir); Process.Start("explorer.exe", Avatars.Dir); }), Btn("Reload", () => { Avatars.Reload(); _liveMatchKey = ""; RefreshAvatarLists(); }));

            var pv = _secPreview = p.Fold("Avatar preview", false, "set.preview");
            Tip(pv, "The 3D view's Mesh / Skeleton / Devices chips turn those on and off. Here: hide single meshes (an outfit piece, say).");
            _meshInfo = pv.Add(new InfoLabel("") { Color = Theme.TextFaint });
            _meshList = pv.Add(new GridPanel { Columns = 2, RowH = 22, Gap = 2 });
            pv.Row(Btn("Export FBX with face shapes…", ExportAvatarFbx, tip: "Writes a copy of the shown avatar's FBX with the shapes that BlendShare adds in Unity (e.g. a face-tracking add-on), plus its textures, for Blender or other tools."));

            var un = _secUnity = p.Fold("Unity projects", false, "set.unity");
            Tip(un, "Optional: link your Unity project(s). The app reads the humanoid rig from your avatar's .fbx.meta and matches it to the avatar you wear in VRChat (blueprint ID). Unity does not need to be running.");
            _projList = un.Add(new DarkListBox { Height = Theme.S(66) });
            foreach (var pr in S.UnityProjects) _projList.Items.Add(pr);
            un.Row(Btn("Add…", AddProject, tip: "Link a Unity project folder"), Btn("Remove", () => { if (_projList.SelectedItem is string s) { S.UnityProjects.Remove(s); _projList.Items.Remove(s); S.Save(); SyncSettingsSummaries(); } }), Btn("Rescan", () => Avatars.ScanAsync()));
            _scanInfo = un.Add(new InfoLabel("") { Color = Theme.TextFaint });
            un.Row(Btn("Link current avatar to FBX…", LinkCurrentAvatar, tip: "Use a model from a Unity project for the avatar you wear in VRChat now"));
            SyncSettingsSummaries();
        }

        void SyncSettingsSummaries()
        {
            if (_secSetRec == null) return;
            _secSetRec.Summary = "hotkey " + S.HotkeyRecord;
            _secSources.Summary = S.EnableOpenVR ? "SteamVR on" : "SteamVR off";
            _secSources.SummaryColor = S.EnableOpenVR ? Theme.TextFaint : Theme.Warn;
            int n = Avatars.All().Count;
            _secAvLib.Summary = n == 0 ? "empty" : n + (n == 1 ? " avatar" : " avatars");
            _secAvLib.SummaryColor = n == 0 ? Theme.Warn : Theme.TextFaint;
            _secUnity.Summary = S.UnityProjects.Count == 0 ? "none" : S.UnityProjects.Count + (S.UnityProjects.Count == 1 ? " project" : " projects");
            _secPreview.Summary = !S.PreviewMesh ? "mesh off" : _view?.Model != null ? $"{_view.Model.Parts.Count(pt => pt.Visible)}/{_view.Model.Parts.Count} meshes" : "";
        }

        /// <summary>The 3D view's Mesh chip.</summary>
        void SetPreviewMesh(bool on)
        {
            S.PreviewMesh = on; S.Save();
            _meshFor = null;
            if (!on) _view.Model = null;
            UpdateMeshList();
            _view.Invalidate();
        }

        void LinkCurrentAvatar()
        {
            if (string.IsNullOrEmpty(Rec.AvatarId)) { MessageBox.Show(this, "VRChat hasn't reported an avatar yet (enable OSC in VRChat's Action Menu)."); return; }
            var p = PickModelMeta();
            if (p != null) { Avatars.Link(Rec.AvatarId, p.SourcePath); RefreshAvatarInfo(); }
        }

        void AddProject()
        {
            using (var d = new FolderBrowserDialog { Description = "Select a Unity project folder (contains Assets and ProjectSettings)" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (!UnityProjectScanner.IsUnityProject(d.SelectedPath)) { MessageBox.Show(this, "That folder is not a Unity project."); return; }
                if (!S.UnityProjects.Contains(d.SelectedPath)) { S.UnityProjects.Add(d.SelectedPath); _projList.Items.Add(d.SelectedPath); S.Save(); SyncSettingsSummaries(); }
                Avatars.ScanAsync();
            }
        }

        // ---------------------------------------------------------------- avatar library (Settings), base model per take (Clip) and live (Live)

        void RefreshAvatarLists()
        {
            bool was = _syncing; _syncing = true;
            try
            {
                var all = Avatars.All();
                var sel = (_avList?.SelectedItem as AvatarEntry)?.Id;
                if (_avList != null)
                {
                    _avList.BeginUpdate(); _avList.Items.Clear();
                    foreach (var e in all) _avList.Items.Add(e);
                    _avList.EndUpdate();
                    int i = all.FindIndex(e => e.Id == sel);
                    if (i >= 0) _avList.SelectedIndex = i; else if (all.Count > 0) _avList.SelectedIndex = 0;
                }
            }
            finally { _syncing = was; }
            SyncAvatarEntryUI();
            SyncAvatarPick();
            SyncLiveBase(true);
        }

        void SyncAvatarEntryUI()
        {
            if (_avList == null) return;
            var e = _avList.SelectedItem as AvatarEntry;
            bool was = _syncing; _syncing = true;
            try
            {
                _avPatterns.Text = e == null ? "" : string.Join(", ", e.Match);
                var def = Avatars.Default;
                bool fbxOk = e != null && File.Exists(e.FbxPath);
                _avLibInfo.Text = Avatars.All().Count == 0 ? "The library is empty: Add… a Booth avatar package."
                    : e == null ? "" : $"{(def == e ? "Default · " : "")}eye height {e.EyeHeight:0.000} m" + (e.AvatarIds.Count > 0 ? $" · {e.AvatarIds.Count} linked" : "") + (fbxOk ? "" : " · FBX missing");
                _avLibInfo.Color = e != null && !fbxOk ? Theme.Warn : Theme.TextFaint;
                _avLibDetail.Text = e == null ? $"Folder: {Avatars.Dir}" : $"{e.Label}: {e.Skeleton.Human.Count(h => h >= 0)} humanoid bones, eye height {e.EyeHeight:0.000} m\n" +
                      $"FBX: {(fbxOk ? Path.GetFileName(e.FbxPath) : "missing (" + e.FbxPath + ")")}\n" +
                      (e.AvatarIds.Count > 0 ? $"Linked VRChat avatars: {e.AvatarIds.Count}\n" : "") + (e.Notes.Length > 0 ? e.Notes + "\n" : "") + "From: " + e.Source;
            }
            finally { _syncing = was; }
            SyncSettingsSummaries();
            _setPage?.PerformLayout();
        }

        void SaveAvatarPatterns()
        {
            if (_syncing || !(_avList?.SelectedItem is AvatarEntry e)) return;
            var pats = _avPatterns.Text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
            if (pats.SequenceEqual(e.Match)) return;
            e.Match = pats;
            try { Avatars.Save(e); } catch (Exception ex) { MessageBox.Show(this, "Could not save: " + ex.Message); }
            _liveMatchKey = "";
        }

        void AddAvatar()
        {
            string[] files;
            using (var d = new OpenFileDialog
            {
                Multiselect = true, Title = "Pick the avatar's .unitypackage (or .zip) and its face-tracking add-on (BlendShare); or a Unity model (.fbx with its .meta)",
                Filter = "Avatar files (*.unitypackage;*.zip;*.fbx;*.meta)|*.unitypackage;*.zip;*.fbx;*.FBX;*.meta|All files (*.*)|*.*",
            })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                files = d.FileNames;
            }
            string name = Microsoft.VisualBasic.Interaction.InputBox("Name of this base model (leave empty to use the model's file name):", "Add avatar", "");
            _avLibInfo.Text = "Adding…"; _avLibInfo.Color = Theme.Accent;
            new Thread(() =>
            {
                AvatarImport.Result res = null; string err = null;
                try { res = AvatarImport.Import(files, Avatars.Dir, string.IsNullOrWhiteSpace(name) ? null : name, msg => BeginInvokeSafe(() => _avLibInfo.Text = msg)); }
                catch (Exception ex) { err = ex.Message; AppLog.Error("Avatar import: " + ex); }
                BeginInvokeSafe(() =>
                {
                    _avLibInfo.Color = Theme.TextDim;
                    Avatars.Reload();
                    _liveMatchKey = "";
                    RefreshAvatarLists();
                    if (res?.Entry != null) { int i = Avatars.All().FindIndex(x => x.Id == res.Entry.Id); if (i >= 0) _avList.SelectedIndex = i; }
                    MessageBox.Show(this, err != null ? "Adding the avatar failed: " + err : res.Summary, "Add avatar");
                });
            }) { IsBackground = true, Name = "avatar import" }.Start();
        }

        void RemoveAvatar()
        {
            if (!(_avList?.SelectedItem is AvatarEntry e)) return;
            if (MessageBox.Show(this, $"Remove {e.Label} from the library? Its files go to the Recycle Bin. Takes keep their own copy of the skeleton.", "Remove avatar", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            Avatars.Remove(e);
            _liveMatchKey = "";
            RefreshAvatarLists();
        }

        /// <summary>The Live tab: the avatar worn in VRChat now and its base model (pick one to link this avatar id).</summary>
        void SyncLiveBase(bool rebuild)
        {
            if (_liveBase == null) return;
            var m = CurrentMatch();
            bool was = _syncing; _syncing = true;
            try
            {
                if (rebuild || _liveBase.Items.Count != Avatars.All().Count)
                {
                    _liveBaseItems.Clear(); _liveBase.Items.Clear();
                    foreach (var e in Avatars.All()) { _liveBaseItems.Add(e); _liveBase.Items.Add(e.Label); }
                }
                int idx = m?.Entry == null ? -1 : _liveBaseItems.FindIndex(e => e.Id == m.Entry.Id);
                if (_liveBase.SelectedIndex != idx) _liveBase.SelectedIndex = idx;
                string worn = string.IsNullOrEmpty(Rec.AvatarName) ? (string.IsNullOrEmpty(Rec.AvatarId) ? "none reported yet (turn OSC on in VRChat)" : Rec.AvatarId) : Rec.AvatarName;
                string t = $"Wearing: {worn}" + (m?.Entry == null ? "\nNo base model: add avatars on the Settings tab." : m.Sure ? "" : "\nBase model guessed: pick the right one below.");
                if (_liveAvatarInfo.Text != t) _liveAvatarInfo.Text = t;
                _liveAvatarInfo.Color = m == null || !m.Sure ? Theme.Warn : Theme.TextDim;
                Theme.Tip(_liveAvatarInfo, m?.Entry != null ? $"Base model: {m.Entry.Label} ({m.How})" : null);
            }
            finally { _syncing = was; }
        }

        void OnLiveBasePicked()
        {
            if (_syncing) return;
            int i = _liveBase.SelectedIndex;
            if (i < 0 || i >= _liveBaseItems.Count) return;
            if (string.IsNullOrEmpty(Rec.AvatarId)) { MessageBox.Show(this, "VRChat hasn't reported an avatar yet (enable OSC in VRChat's Action Menu)."); SyncLiveBase(false); return; }
            Avatars.LinkId(Rec.AvatarId, _liveBaseItems[i]);
            _liveMatchKey = "";
            SyncLiveBase(false);
        }

        void RefreshAvatarInfo()
        {
            if (_scanInfo != null) _scanInfo.Text = Avatars.Scanning ? "Scanning… " + Avatars.LastScanInfo : Avatars.LastScanInfo + (S.AvatarModelMap.Count > 0 ? $"\n{S.AvatarModelMap.Count} avatar(s) linked." : "");
            SyncSettingsSummaries();
            _setPage?.PerformLayout();
        }

        void ExportAvatarFbx()
        {
            var prof = _view.Profile ?? _edit?.Profile ?? CurrentProfile();
            string fbx = SkinnedAvatar.FbxFor(prof);
            if (fbx == null) { MessageBox.Show(this, "Load a recording (or link your avatar) first: the FBX is found next to the avatar's .fbx.meta.", "VR Motion Recorder"); return; }
            var found = BlendShareAsset.FindFor(fbx);
            if (found.Count == 0) { MessageBox.Show(this, "No BlendShare asset in the Unity project uses " + Path.GetFileName(fbx) + ".", "VR Motion Recorder"); return; }
            string def = null;
            try { def = BlendShareAsset.Load(found[0]).DefaultName; } catch { }
            using (var dlg = new SaveFileDialog { Filter = "FBX (*.fbx)|*.fbx", FileName = (string.IsNullOrEmpty(def) ? Path.GetFileNameWithoutExtension(fbx) + "_FT" : def) + ".fbx" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string outFbx = dlg.FileName;
                if (string.Equals(Path.GetFullPath(outFbx), Path.GetFullPath(fbx), StringComparison.OrdinalIgnoreCase)) { MessageBox.Show(this, "Choose a different file than the original FBX.", "VR Motion Recorder"); return; }
                var model = _view.Model;
                _meshInfo.Text = "Exporting " + Path.GetFileName(outFbx) + "…";
                new Thread(() =>
                {
                    string msg;
                    try
                    {
                        var report = BlendShareAsset.MergeIntoFbx(fbx, found.Select(BlendShareAsset.Load).ToList(), outFbx);
                        int copied = 0;
                        // textures next to the FBX, so Blender finds them by their relative file names
                        if (model != null && string.Equals(model.SourcePath, fbx, StringComparison.OrdinalIgnoreCase))
                            foreach (var t in model.Textures)
                                if (!string.IsNullOrEmpty(t.File) && File.Exists(t.File))
                                {
                                    string dst = Path.Combine(Path.GetDirectoryName(outFbx), Path.GetFileName(t.File));
                                    if (!string.Equals(Path.GetFullPath(dst), Path.GetFullPath(t.File), StringComparison.OrdinalIgnoreCase)) { File.Copy(t.File, dst, true); copied++; }
                                }
                        msg = $"Exported {Path.GetFileName(outFbx)}: {report.Replace(Environment.NewLine, "; ")}" + (copied > 0 ? $"; {copied} textures copied" : "");
                        AppLog.Info(msg);
                    }
                    catch (Exception ex) { msg = "Export failed: " + ex.Message; AppLog.Error("Avatar FBX export: " + ex); }
                    BeginInvokeSafe(() => { _meshInfo.Text = msg; _setPage.PerformLayout(); });
                }) { IsBackground = true, Name = "avatar fbx export" }.Start();
            }
        }

        void UpdateMeshList()
        {
            var m = _view.Model;
            var old = _meshList.Controls.Cast<Control>().ToList();
            _meshList.Controls.Clear();
            foreach (var c in old) c.Dispose();
            if (m != null)
            {
                foreach (var part in m.Parts)
                {
                    part.Visible = !S.PreviewHiddenMeshes.Contains(part.Name);
                    var cb = Tip(new DarkCheck(part.Name, part.Visible), part.Name);
                    var pt = part;
                    cb.CheckedChanged += (o, e) =>
                    {
                        pt.Visible = cb.Checked;
                        S.PreviewHiddenMeshes.Remove(pt.Name);
                        if (!cb.Checked) S.PreviewHiddenMeshes.Add(pt.Name);
                        S.Save(); _view.Invalidate(); SyncSettingsSummaries();
                    };
                    _meshList.Controls.Add(cb);
                }
                _meshInfo.Text = $"{Path.GetFileName(m.SourcePath)}: {m.Parts.Count} meshes, {m.TriangleCount:N0} triangles";
                Theme.Tip(_meshInfo, $"{m.Textures.Count(t => t.Px != null)} textures. Face preview: {(m.FaceShapes.Count > 0 ? m.FaceShapes.Count + " shapes (" + string.Join(", ", m.FaceShapes.Keys.Take(12)) + (m.FaceShapes.Count > 12 ? ", …" : "") + ")" : "none")}.");
            }
            else { _meshInfo.Text = !S.PreviewMesh ? "The mesh is off (Mesh chip in the 3D view)." : _view.Profile == null ? "Shown when a take or live VRChat IK is on screen." : "No FBX found for this avatar."; Theme.Tip(_meshInfo, null); }
            ((GridPanel)_meshList).FitHeight();
            SyncSettingsSummaries();
            _setPage.PerformLayout();
        }

        /// <summary>"SteamVR: HMD · 2 controllers · 3 trackers · 2 base stations (1 not tracked)".</summary>
        public static string SteamVRSummary(OpenVRSource ov)
        {
            if (ov == null) return "SteamVR: off (Settings → Sources)";
            if (!ov.Connected) return "SteamVR: " + ov.Status.ToLowerInvariant();
            var devs = ov.Devices();
            if (devs.Count == 0) return "SteamVR: connected, no devices";
            string N(int c, string one, string many) => c == 0 ? null : c == 1 ? "1 " + one : c + " " + many;
            var parts = new[]
            {
                devs.Any(d => d.Class == 1) ? "HMD" : null,
                N(devs.Count(d => d.Class == 2), "controller", "controllers"),
                N(devs.Count(d => d.Class == 3), "tracker", "trackers"),
                N(devs.Count(d => d.Class == 4), "base station", "base stations"),
            }.Where(s => s != null);
            int lost = devs.Count(d => !d.Valid);
            return "SteamVR: " + string.Join(" · ", parts) + (lost > 0 ? $"  ({lost} not tracked)" : "");
        }

        public static string DeviceName(OpenVRSource.Device d)
        {
            switch (d.Class)
            {
                case 1: return "HMD";
                case 2: return d.Role == 1 ? "Left hand" : d.Role == 2 ? "Right hand" : "Controller";
                case 3: return string.IsNullOrEmpty(d.TrackerRole) ? "Tracker" : d.TrackerRole;
                case 4: return "Base station" + (string.IsNullOrEmpty(d.ModeLabel) ? "" : " " + d.ModeLabel.ToUpperInvariant());
                default: return "Device";
            }
        }

        public static Color DeviceColor(OpenVRSource.Device d) =>
            d.Class == 1 ? Theme.Center : d.Class == 2 ? (d.Role == 1 ? Theme.Left : d.Role == 2 ? Theme.Right : Theme.Accent) :
            d.Class == 4 ? Color.FromArgb(150, 160, 185) : Theme.Warn;

        void UpdateLiveInfo()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"OSCQuery: {(Rec.OscQuery != null ? Rec.OscQuery.InstanceName : "off")}");
            if (Rec.OscQueryRx != null) sb.AppendLine($"  udp {Rec.OscQueryRx.Port}: {Rec.OscQueryRx.Messages} msgs");
            if (Rec.LegacyRx != null) sb.AppendLine($"  udp {Rec.LegacyRx.Port}: {Rec.LegacyRx.Messages} msgs");
            var vh = Rec.OscQuery?.VrcHttp;
            sb.AppendLine($"VRChat OSCQuery: {(vh != null ? vh.Address + ":" + vh.Port : "not found")}");
            sb.AppendLine($"Avatar: {Rec.AvatarName} {Rec.AvatarId}");
            sb.AppendLine($"Eye height: {Rec.AvatarEyeHeight:F3} m");
            lock (Rec.LiveLock) sb.AppendLine($"Params known: {Rec.LiveParams.Count}");
            var ov = Rec.OpenVR;
            _devList.UpdateFrom(ov);
            sb.AppendLine($"SteamVR: {(ov == null ? "disabled" : ov.Status)}");
            if (ov != null) sb.AppendLine($"  finger tracking: {ov.InputStatus}");
            if (ov != null) for (int sd = 0; sd < 2; sd++) if (ov.LiveFingerTime[sd] > 0 && Clock.Now - ov.LiveFingerTime[sd] < 1) sb.AppendLine($"  {(sd == 0 ? "L" : "R")} curl " + string.Join(" ", ov.LiveFingers[sd].Take(5).Select(v => v.ToString("0.00"))));
            var lg = Rec.Log;
            sb.AppendLine($"VRChat log: {(lg?.CurrentFile != null ? Path.GetFileName(lg.CurrentFile) : "-")}");
            if (lg != null) sb.AppendLine($"  user {lg.LocalUser}  world {lg.WorldName}\n  HUMR frames {lg.HumrFrames}{(lg.HumrActive ? " (active)" : "")}");
            string t = sb.ToString().TrimEnd();
            if (_liveInfo.Text != t) _liveInfo.Text = t; // a height change lays the page out again
            string ds = ov == null ? "off" : !ov.Connected ? (ov.Status ?? "").ToLowerInvariant() : $"{ov.Devices().Count(d => d.Valid)}/{ov.Devices().Count} tracked";
            _secLiveDevices.Summary = ds;
            _secLiveSources.Summary = (Rec.OscQuery?.VrcHttp != null ? "VRChat found" : "VRChat not found") + (lg?.HumrActive == true ? " · HUMR" : "");
        }

    }
}
