using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace VRMR.UI
{
    public sealed partial class MainForm
    {
        DarkCheck _cFbxArm, _cTakeFolder;

        /// <summary>An export waiting for the solve with the latest edits; Rebake runs it.</summary>
        bool _exportAfterSolve;

        TextBox _exportDirBox;

        bool _exporting;

        string _lastExportOut;
        FoldSection _secFolder, _secFormats, _foldMoreFormats, _secExportOpts;

        // Export tab: where (a folder of your choice, a folder per take inside it by default), what (FBX with the avatar model,
        // FBX with the armature only, Unity .anim, more formats), how (frame rate, face names, split per avatar); the export
        // button and its result at the bottom.
        void BuildExportPage()
        {
            var p = _exportPage;
            var fd = _secFolder = p.Fold("Folder", true, "export.folder");
            _exportDirBox = Tip(Box(ExportDir()), "Where the files go. Type a path or Browse.");
            _exportDirBox.Leave += (o, e) => SetExportDir(_exportDirBox.Text);
            _exportDirBox.KeyDown += (o, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    SetExportDir(_exportDirBox.Text);
                    e.SuppressKeyPress = true;
                }
            };
            fd.Add(_exportDirBox);
            var btns = new List<Control> { Btn("Browse…", BrowseExportDir, tip: "Pick the export folder") };
            if (UnityProjectRoot() != null)
                btns.Add(Btn("Unity project", () => SetExportDir(UnityExportDir()),
                    tip:
                    "Assets\\VRMotionRecorder in the linked Unity project: the VR Motion Bridge sets the model FBX up as your base model's Humanoid and writes <take>_humanoid.anim, which plays on any Humanoid avatar"));
            btns.Add(Btn("Open", () =>
            {
                string d = _lastExportOut != null && Directory.Exists(_lastExportOut) ? _lastExportOut : ExportDir();
                if (Directory.Exists(d)) Process.Start("explorer.exe", d);
            }, tip: "Open the folder of the last export (else the export folder) in Explorer"));
            fd.Row(btns.ToArray());
            _cTakeFolder = fd.Add(Tip(new DarkCheck("A folder per take", S.ExportTakeFolders),
                "Each take's files go into <folder>\\<take name>. Off: straight into the export folder (every file name starts with the take's name)."));

            var fm = _secFormats = p.Fold("Formats", true, "export.formats");
            _cFbx = fm.Add(Tip(new DarkCheck("FBX with the avatar model", S.ExportFbx),
                "<take>.fbx: your avatar's own FBX (meshes, materials, face-tracking shapes) with the take as its animation. Its textures (each material's color texture) go into <folder>\\textures\\<avatar>, once per avatar, and the FBX points there, so Blender and Unity show them; textures already in the same Unity project are used where they are. When no FBX of the avatar is found (avatar library or the linked Unity project), the armature only."));
            _cFbxFace = fm.Add(Tip(new DarkCheck("Face shapes", S.FbxIncludeFaceMesh) { Indent = 20 },
                "Key the face mesh's own shape keys with the recorded face tracking"));
            _cFbxArm = fm.Add(Tip(new DarkCheck("FBX, armature only", S.ExportFbxArmature),
                "<take>_armature.fbx: the avatar's armature (exact bone names and rest pose) with the take's animation, no meshes: small, for retargeting onto any rig. No face: that is in the model FBX and the Unity .anim."));
            _cFbxCam = fm.Add(Tip(new DarkCheck("VRChat camera in the FBX", S.FbxIncludeCamera),
                "VRChat's camera (photo / stream camera) as an animated camera, in either FBX"));
            _cFbxTrk = fm.Add(Tip(new DarkCheck("Trackers in the FBX", S.FbxIncludeTrackers),
                "The headset, controllers and trackers as animated empties, in either FBX"));
            _cAnim = fm.Add(Tip(new DarkCheck("Unity .anim", S.ExportAnim || S.ExportFaceAnim),
                "<take>_body.anim: the body (and face) on the avatar's transform paths, exact on a Generic Animator; in Unity, Tools > VR Motion Bridge > Make Humanoid Clip turns it into a Humanoid clip. <take>_face.anim: the face blendshapes alone (a face layer on any avatar with the face mesh)."));
            var more = _foldMoreFormats = fm.Fold("More formats", false, "export.more");
            _cParams = more.Add(Tip(new DarkCheck("Unity .anim: avatar parameters", S.ExportParamsAnim),
                "The raw avatar parameters as animator curves (FX layer replay)"));
            _cOsc = more.Add(Tip(new DarkCheck("OSC_Recorder .json", S.ExportOscJson),
                "Parameter replay with the OSC_Recorder tool"));
            _cHumr = more.Add(Tip(new DarkCheck("HUMR log", S.ExportHumrLog),
                "A HUMR-compatible log for the HUMR loader"));
            _cFaceJson = more.Add(Tip(new DarkCheck("Face .json (Blender add-on)"),
                "Always written next to an FBX; this also writes it without one"));
            foreach (var c in new[]
                     {
                         _cTakeFolder, _cFbx, _cFbxFace, _cFbxArm, _cFbxCam, _cFbxTrk, _cAnim, _cParams, _cOsc, _cHumr,
                         _cFaceJson
                     })
                c.CheckedChanged += (o, e) =>
                {
                    SaveExportSettings();
                    SyncExportUI();
                };

            var op = _secExportOpts = p.Fold("Options", true, "export.options");
            _fps = op.Field("Frame rate", new DarkCombo(),
                    "Frames per second of the solved clip (the take re-solves when this changes). An app setting.")
                .Editor as DarkCombo;
            foreach (int f in new[] { 24, 30, 60, 90, 120 }) _fps.Items.Add(f + " fps");
            _fps.SelectedItem = S.ExportFps + " fps";
            if (_fps.SelectedIndex < 0) _fps.SelectedIndex = 2;
            _fps.SelectedIndexChanged += (o, e) =>
            {
                S.ExportFps = int.Parse(((string)_fps.SelectedItem).Split(' ')[0]);
                MarkDirty(true);
            };
            _faceNaming = op.Field("Face shapes", new DarkCombo(),
                    "Names of the exported face shapes. Matched to your avatar's own shapes when the face mesh has them.")
                .Editor as DarkCombo;
            _faceNaming.Items.AddRange("Unified Expressions", "ARKit", "Unified Expressions (raw names)");
            _faceNaming.SelectedIndex = S.FaceNaming == "ARKit" ? 1 : 0;
            _faceNaming.SelectedIndexChanged += (o, e) =>
            {
                S.FaceNaming = _faceNaming.SelectedIndex == 1 ? "ARKit" : "UnifiedExpressions";
                MarkDirty(true);
            };
            _meshBox = Box(S.FaceMeshPath);
            op.Field("Face mesh", _meshBox, "Path of the face mesh, relative to the avatar root (e.g. Body)");
            _meshBox.TextChanged += (o, e) => S.FaceMeshPath = _meshBox.Text.Trim();
            _splitAvatars = op.Add(Tip(new DarkCheck("Split at avatar changes"),
                "When you switched avatars in VRChat during the take, export one clip per avatar, each solved onto that avatar's base model (name suffix _1, _2...)."));
            _fps.SelectedIndexChanged += (o, e) => SyncExportUI();

            p.Row(Btn("Export", () => Export(), DarkButton.Kind.Accent,
                "Write the chosen formats for the selected take into the export folder"));
            _exportInfo = p.Add(new InfoLabel() { Color = Theme.TextDim });
            SyncExportUI();
        }

        static bool SamePose(Pose a, Pose b)
        {
            return (a.pos - b.pos).Length < 1e-6f && Quat.Angle(a.rot, b.rot) < 1e-4f;
        }

        /// <summary>Rebake's end: the export that waited for this solve.</summary>
        void RunPendingExport(bool solved)
        {
            if (!_exportAfterSolve || _bakeAgain || _rebakeTimer.Enabled) return; // more edits queued: after those
            _exportAfterSolve = false;
            if (!solved)
            {
                _exportInfo.Text = "Export skipped: the solve failed.";
                _exportInfo.Color = Theme.Rec;
                return;
            }

            Export();
        }

        void SyncExportUI()
        {
            _cFbxFace.Enabled = _cFbx.Checked;
            _cFbxCam.Enabled = _cFbxTrk.Enabled = _cFbx.Checked || _cFbxArm.Checked;
            int n = new[] { _cParams, _cOsc, _cHumr, _cFaceJson }.Count(c => c.Checked);
            _foldMoreFormats.Summary = n == 0 ? "" : n + " on";
            int all = new[] { _cFbx, _cFbxArm, _cAnim }.Count(c => c.Checked) + n;
            _secFormats.Summary = all == 0 ? "none chosen" : all + (all == 1 ? " format" : " formats");
            _secFormats.SummaryColor = all == 0 ? Theme.Warn : Theme.TextFaint;
            _secExportOpts.Summary = _fps.SelectedItem as string ?? "";
            string d = ExportDir();
            _secFolder.Summary = IsInUnityAssets(d) ? "Unity project" : Path.GetFileName(d.TrimEnd('\\', '/'));
        }

        // =====================================================================================================  export folder

        string UnityProjectRoot()
        {
            return S.UnityProjects.FirstOrDefault(UnityProjectScanner.IsUnityProject);
        }

        string UnityExportDir()
        {
            string u = UnityProjectRoot();
            return u == null ? null : Path.Combine(u, "Assets", "VRMotionRecorder");
        }

        /// <summary>
        ///     The export folder: the one chosen (LastExportDir), else the linked Unity project's Assets\VRMotionRecorder,
        ///     else Documents\VR Motion Recorder\Exports.
        /// </summary>
        string ExportDir()
        {
            return !string.IsNullOrWhiteSpace(S.LastExportDir)
                ? S.LastExportDir
                : UnityExportDir() ?? Path.Combine(S.RecordingsDir, "Exports");
        }

        bool IsInUnityAssets(string d)
        {
            foreach (string u in S.UnityProjects)
            {
                if (string.IsNullOrEmpty(u)) continue;
                string a = Path.GetFullPath(Path.Combine(u, "Assets")).TrimEnd('\\') + "\\";
                try
                {
                    if ((Path.GetFullPath(d).TrimEnd('\\') + "\\").StartsWith(a, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { }
            }

            return false;
        }

        void SetExportDir(string d)
        {
            d = (d ?? "").Trim().Trim('"');
            if (d.Length == 0)
            {
                _exportDirBox.Text = ExportDir();
                return;
            }

            try
            {
                if (!Path.IsPathRooted(d)) throw new ArgumentException("not a full path");
                d = Path.GetFullPath(d);
            }
            catch
            {
                _exportInfo.Text = "The export folder must be a full path (like D:\\Exports).";
                _exportInfo.Color = Theme.Warn;
                _exportDirBox.Text = ExportDir();
                return;
            }

            S.LastExportDir = d;
            S.Save();
            if (_exportDirBox.Text != d) _exportDirBox.Text = d;
            SyncExportUI();
        }

        void BrowseExportDir()
        {
            using (var dlg = new FolderBrowserDialog
                   {
                       Description = "Export folder",
                       SelectedPath = Directory.Exists(ExportDir()) ? ExportDir() : S.RecordingsDir,
                       ShowNewFolderButton = true
                   })
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    SetExportDir(dlg.SelectedPath);
        }

        // =====================================================================================================  export

        void SaveExportSettings()
        {
            S.ExportFbx = _cFbx.Checked;
            S.FbxIncludeFaceMesh = _cFbxFace.Checked;
            S.ExportFbxArmature = _cFbxArm.Checked;
            S.FbxIncludeCamera = _cFbxCam.Checked;
            S.FbxIncludeTrackers = _cFbxTrk.Checked;
            S.ExportAnim = S.ExportFaceAnim = _cAnim.Checked;
            S.ExportParamsAnim = _cParams.Checked;
            S.ExportOscJson = _cOsc.Checked;
            S.ExportHumrLog = _cHumr.Checked;
            S.ExportTakeFolders = _cTakeFolder.Checked;
            S.Save();
        }

        static string SafeName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim().Length == 0 ? "clip" : s.Trim();
        }

        void Export()
        {
            var c = _baked;
            var r = _edit;
            _exportInfo.Color = Theme.TextDim;
            if (c == null || r == null)
            {
                _exportInfo.Text = "Select a take first.";
                return;
            }

            if (_exporting)
            {
                _exportInfo.Text = "An export is already running…";
                return;
            }

            // the last edit (Origin, a turn, a gizmo drag, a trim…) not solved in yet: solve now, export right after
            if (_baking || _bakeAgain || _rebakeTimer.Enabled || !SamePose(c.Placement, r.Edits.Placement))
            {
                _exportAfterSolve = true;
                _exportInfo.Text = "Solving the latest edit, then exporting…";
                if (!_baking)
                {
                    _rebakeTimer.Stop();
                    Rebake();
                }

                return;
            }

            if (_exportDirBox.Focused || _exportDirBox.Text.Trim() != ExportDir()) SetExportDir(_exportDirBox.Text);
            SaveExportSettings();
            string name = SafeName(r.Name);
            string root = ExportDir();
            string dir = S.ExportTakeFolders ? Path.Combine(root, name) : root;
            var job = new TakeExportJob
            {
                Fbx = _cFbx.Checked, FbxArmature = _cFbxArm.Checked, FaceShapes = _cFbxFace.Checked,
                Camera = _cFbxCam.Checked, Trackers = _cFbxTrk.Checked,
                Anim = _cAnim.Checked, Params = _cParams.Checked, Osc = _cOsc.Checked, Humr = _cHumr.Checked,
                FaceJson = _cFaceJson.Checked,
                FaceMesh = string.IsNullOrEmpty(S.FaceMeshPath) ? "Body" : S.FaceMeshPath, FaceNaming = S.FaceNaming,
                Dir = dir, Root = root, Rec = r
            };
            if (!(job.Fbx || job.FbxArmature || job.Anim || job.Params || job.Osc || job.Humr || job.FaceJson))
            {
                _exportInfo.Text = "Choose at least one format.";
                _exportInfo.Color = Theme.Warn;
                return;
            }

            // one clip, or one per avatar worn during the take (each solved on its own base model)
            var parts = new List<Tuple<BakedClip, string>>();
            try
            {
                var segs = AvatarLibrary.AvatarSegments(r);
                if (_splitAvatars.Checked && segs.Count > 1)
                {
                    var orig = r.Edits;
                    try
                    {
                        for (int k = 0; k < segs.Count; k++)
                        {
                            var sg = segs[k];
                            var e = EditList.FromJson(Json.ParseObject(Json.Serialize(orig.ToJson())));
                            e.Snapshot = orig.Snapshot; // FromJson re-reads it; keep the same object
                            e.TrimIn = Math.Max(orig.TrimIn, sg.Start);
                            e.TrimOut = Math.Min(orig.TrimOut < 0 ? r.Duration : orig.TrimOut, sg.End);
                            if (e.TrimOut - e.TrimIn < 0.2) continue;
                            var m = Avatars.Match(sg.AvatarId, sg.AvatarName);
                            e.TargetAvatar = m.Entry?.Id ?? "";
                            _exportInfo.Text =
                                $"Solving part {k + 1}/{segs.Count} ({sg.AvatarName} → {m.Entry?.Label})…";
                            _exportInfo.Refresh();
                            r.Edits = e;
                            BakedClip pc;
                            lock (r.Sync) pc = PoseBuilder.Bake(r, m.Entry?.Profile(), MakeBakeOptions());
                            parts.Add(Tuple.Create(pc, name + "_" + (k + 1)));
                        }
                    }
                    finally { r.Edits = orig; }
                }
                else parts.Add(Tuple.Create(c, name));
            }
            catch (Exception ex)
            {
                _exportInfo.Text = "Export failed: " + ex.Message;
                _exportInfo.Color = Theme.Rec;
                AppLog.Error("Export: " + ex);
                return;
            }

            _exporting = true;
            _exportInfo.Text = "Exporting…";
            _exportPage.PerformLayout();
            new Thread(() =>
            {
                string err = null;
                try
                {
                    Directory.CreateDirectory(dir);
                    foreach (var part in parts)
                        ExportOne(part.Item1, part.Item2, job,
                            msg => BeginInvokeSafe(() => { _exportInfo.Text = msg; }));
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                    AppLog.Error("Export: " + ex);
                }

                BeginInvokeSafe(() =>
                {
                    _exporting = false;
                    if (err != null)
                    {
                        _exportInfo.Text = "Export failed: " + err;
                        _exportInfo.Color = Theme.Rec;
                        _exportPage.PerformLayout();
                        return;
                    }

                    _lastExportOut = dir;
                    string notes = job.Notes.Count > 0
                        ? Environment.NewLine + string.Join(Environment.NewLine, job.Notes.Distinct().Take(3))
                        : "";
                    _exportInfo.Text = $"Exported {job.Written.Count} file(s)" +
                                       (parts.Count > 1 ? $" in {parts.Count} parts (one per avatar)" : "") +
                                       $" to{Environment.NewLine}{dir}" + notes;
                    _exportInfo.Color = job.Notes.Count > 0 ? Theme.Warn : Theme.Ok;
                    _exportPage.PerformLayout();
                });
            }) { IsBackground = true, Name = "take export" }.Start();
        }

        /// <summary>
        ///     Writes one solved clip in every chosen format (the clip files shared with the scene export, plus the
        ///     take-only formats and the take's manifest).
        /// </summary>
        void ExportOne(BakedClip c, string name, TakeExportJob job, Action<string> progress)
        {
            var r = job.Rec;
            bool anyFbx = job.Fbx || job.FbxArmature;
            var cr = ClipExport.Write(c, job.Dir, name, new ClipExportOptions
            {
                Fbx = job.Fbx, FbxFace = job.FaceShapes, FbxAvatarMesh = true, FbxArmature = job.FbxArmature,
                FbxCamera = job.Camera, FbxTrackers = job.Trackers,
                BodyAnim = job.Anim, FaceAnim = job.Anim, ParamsAnim = job.Params, FaceJson = job.FaceJson || anyFbx,
                UnitySidecar = true,
                FaceMeshPath = job.FaceMesh, FaceNaming = job.FaceNaming, AvatarId = r.AvatarId ?? "",
                AvatarName = r.AvatarName ?? "", Kind = "take",
                TextureDir =
                    Path.Combine(job.Root, "textures",
                        SafeName(c.Profile?.Name ?? "avatar")), // one copy per avatar in the export folder
                Extra = new Dictionary<string, object> { ["take"] = r.Name ?? "" },
                Progress = progress
            });
            job.Written.AddRange(cr.Paths);
            job.Notes.AddRange(cr.Warnings);
            if (job.Osc)
            {
                string p = Path.Combine(job.Dir, name + "_osc.json");
                MiscExporters.ExportOscRecorderJson(r, p, name);
                job.Written.Add(p);
            }

            if (job.Humr && c.BodySource != "none")
            {
                string p = Path.Combine(job.Dir, "output_log_" + name + ".txt");
                MiscExporters.ExportHumrLog(c, p, string.IsNullOrEmpty(r.LocalUser) ? "Recorder" : r.LocalUser);
                job.Written.Add(p);
            }

            if (anyFbx)
                job.Written.Add(SceneExport.WriteTakeManifest(job.Dir, name, c, cr, c.Profile?.Name, job.FaceMesh,
                    r.AvatarId, r.AvatarName, job.Camera, cr.Warnings));
        }

        /// <summary>What one export writes (read from the UI on the UI thread, used by the writer thread).</summary>
        sealed class TakeExportJob
        {
            public readonly List<string> Notes = new();
            public readonly List<string> Written = new();
            public string FaceMesh, FaceNaming, Dir, Root;
            public bool Fbx, FbxArmature, FaceShapes, Camera, Trackers, Anim, Params, Osc, Humr, FaceJson;
            public Recording Rec;
        }
    }
}
