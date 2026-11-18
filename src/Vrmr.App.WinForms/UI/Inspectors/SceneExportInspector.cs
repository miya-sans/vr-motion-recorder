using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace VRMR.UI
{
    /// <summary>Scene tab (scene selected): export every actor for Blender and Unity, plus the scene manifest.</summary>
    public sealed class SceneExportInspector : ISceneInspector
    {
        public int Order => 90;

        public bool Applies(SceneEditor ed)
        {
            return ed.SelectionKind == "scene";
        }

        public void Build(InspectorContext c)
        {
            var ed = c.Editor;
            var S = ed.S;
            c.Section("Export scene");
            Props.Check(c, "FBX — body + face carrier (Blender & Unity)", () => S.ExportFbx, v =>
                {
                    S.ExportFbx = v;
                    S.Save();
                },
                "Per actor: the avatar's armature with the evaluated scene (cuts, transitions, layers baked), the face as blendshapes on a carrier mesh");
            Props.Check(c, ".anim — body (avatar transform paths)", () => S.ExportAnim, v =>
            {
                S.ExportAnim = v;
                S.Save();
            });
            Props.Check(c, ".anim — face blendshapes", () => S.ExportFaceAnim, v =>
            {
                S.ExportFaceAnim = v;
                S.Save();
            });
            Props.Check(c, ".anim — raw avatar parameters (FX replay)", () => S.ExportParamsAnim, v =>
            {
                S.ExportParamsAnim = v;
                S.Save();
            });
            var unity = Props.Check(c, "Into the linked Unity project (Assets/VRMotionRecorder)",
                () => SceneExportRunner.ToUnity(S), v => SceneExportRunner.IntoUnity = v,
                "Off: Documents\\VR Motion Recorder\\Exports\\<scene>. Link a Unity project in Settings to export straight into it.");
            unity.Enabled = SceneExportRunner.UnityProject(S) != null;
            Props.Buttons(c, ("Export", () => SceneExportRunner.Run(ed, false)));
            var info = c.Note(SceneExportRunner.Status, false);
            info.Color = SceneExportRunner.StatusColor;
            Action upd = null;
            upd = () =>
            {
                if (info.IsDisposed)
                {
                    SceneExportRunner.StatusChanged -= upd;
                    return;
                }

                info.Color = SceneExportRunner.StatusColor;
                info.Text = SceneExportRunner.Status;
                info.Invalidate();
                c.Page.PerformLayout();
            };
            SceneExportRunner.StatusChanged += upd;
            info.Disposed += (o, e) => SceneExportRunner.StatusChanged -= upd;
            Props.Buttons(c, ("Open export folder", () => SceneExportRunner.OpenFolder(ed)));
            c.Note(
                "One FBX + face .json + .anim clips per actor, and <scene>.vrmrscene.json (frame range, fps, markers, actors). " +
                "Blender: VRMR tab → Import VRMR Scene. Unity: select the scene folder → Tools → VR Motion Bridge → Create Timeline From Scene.");
        }
    }

    /// <summary>Toolbar button 'Export…' (the export section is only shown when nothing is selected).</summary>
    public sealed class SceneExportModule : ISceneModule
    {
        public void Attach(SceneEditor ed)
        {
            DarkButton btn = null;
            btn = ed.AddToolbarButton("Export…", () =>
                {
                    if (ed.Scene == null) return;
                    var m = new ContextMenuStrip
                    {
                        BackColor = Theme.Panel2, ForeColor = Theme.Text, Font = Theme.UI, ShowImageMargin = false,
                        Renderer = new ToolStripProfessionalRenderer(new MenuColors())
                    };

                    ToolStripMenuItem Item(string text, Action a, bool enabled = true)
                    {
                        var it = new ToolStripMenuItem(text) { ForeColor = Theme.Text, Enabled = enabled };
                        it.Click += (o, e) => a();
                        m.Items.Add(it);
                        return it;
                    }

                    bool hasUnity = SceneExportRunner.UnityProject(ed.S) != null;
                    Item("Export scene", () => SceneExportRunner.Run(ed, false));
                    Item("Export + open in Blender", () => SceneExportRunner.Run(ed, true));
                    m.Items.Add(new ToolStripSeparator());
                    Item("Export into the Unity project", () => SceneExportRunner.Run(ed, false, true), hasUnity);
                    Item("Export to the exports folder", () => SceneExportRunner.Run(ed, false, false));
                    Item("Open export folder", () => SceneExportRunner.OpenFolder(ed));
                    m.Show(btn, new Point(0, btn.Height));
                },
                "Export the scene: every actor as FBX + Unity clips, and a scene file for Blender (Import VRMR Scene) and Unity (Create Timeline From Scene)");
        }

        sealed class MenuColors : ProfessionalColorTable
        {
            public override Color MenuItemSelected => Theme.AccentDim;
            public override Color MenuItemBorder => Theme.Accent;
            public override Color ToolStripDropDownBackground => Theme.Panel2;
            public override Color MenuBorder => Theme.Border;
            public override Color SeparatorDark => Theme.Border;
            public override Color MenuItemSelectedGradientBegin => Theme.AccentDim;
            public override Color MenuItemSelectedGradientEnd => Theme.AccentDim;
        }
    }

    /// <summary>Runs a scene export in the background: evaluates the scene until every take is solved, writes the files.</summary>
    public static class SceneExportRunner
    {
        /// <summary>The user's choice this session (null = into the Unity project when one is linked).</summary>
        public static bool? IntoUnity;

        public static string Status = "";
        public static Color StatusColor = Theme.TextDim;
        public static string LastDir;
        static volatile bool _busy;
        public static event Action StatusChanged;

        public static string UnityProject(Settings S)
        {
            return S.UnityProjects.FirstOrDefault(UnityProjectScanner.IsUnityProject);
        }

        public static bool ToUnity(Settings S)
        {
            return UnityProject(S) != null && (IntoUnity ?? true);
        }

        static string ExportRoot(Settings S)
        {
            return string.IsNullOrEmpty(S.LastExportDir) ? Path.Combine(S.RecordingsDir, "Exports") : S.LastExportDir;
        }

        public static string TargetDir(Settings S, Scene s, bool toUnity)
        {
            return toUnity
                ? SceneExport.UnityDir(UnityProject(S), s)
                : Path.Combine(ExportRoot(S), SceneExport.SafeName(s.Name));
        }

        static void Set(string text, Color color)
        {
            Status = text;
            StatusColor = color;
            try { StatusChanged?.Invoke(); }
            catch (Exception ex) { AppLog.Error("Scene export status: " + ex.Message); }
        }

        static Control UiOf(SceneEditor ed)
        {
            return ed.Panel.FindForm() ?? (Control)Application.OpenForms.Cast<Form>().FirstOrDefault() ?? ed.Panel;
        }

        static void Post(Control ui, Action a)
        {
            try
            {
                if (ui != null && ui.IsHandleCreated && !ui.IsDisposed) ui.BeginInvoke(a);
            }
            catch { }
        }

        public static void Run(SceneEditor ed, bool openBlender, bool? toUnityOverride = null)
        {
            if (ed.Scene == null) return;
            if (_busy)
            {
                Set("An export is already running…", Theme.Warn);
                return;
            }

            var S = ed.S;
            bool toUnity = toUnityOverride ?? ToUnity(S);
            if (toUnity && UnityProject(S) == null) toUnity = false;
            var snap = ed.Scene.Clone();
            string dir = TargetDir(S, snap, toUnity);
            var opt = new SceneExportOptions
            {
                Fbx = S.ExportFbx || openBlender, FbxFace = S.FbxIncludeFaceMesh, FbxTrackers = S.FbxIncludeTrackers,
                FbxAvatarMesh = S.FbxAvatarMesh,
                BodyAnim = S.ExportAnim, FaceAnim = S.ExportFaceAnim, ParamsAnim = S.ExportParamsAnim,
                FaceMeshPath = string.IsNullOrEmpty(S.FaceMeshPath) ? "Body" : S.FaceMeshPath,
                FaceNaming = S.FaceNaming,
                TakeLoader = p => ed.Eval.Takes.Recording(p)
            };
            var ui = UiOf(ed);
            opt.Progress = msg => Post(ui, () => Set(msg, Theme.TextDim));
            _busy = true;
            Set("Exporting…", Theme.TextDim);
            new Thread(() =>
            {
                SceneExportResult res = null;
                string err = null;
                try
                {
                    var bake = SceneExport.BakeComplete(ed.Eval, snap, 600000, null, opt.Progress);
                    res = SceneExport.Export(bake, dir, opt);
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                    AppLog.Error("Scene export: " + ex);
                }

                Post(ui, () =>
                {
                    _busy = false;
                    if (res == null)
                    {
                        Set("Export failed: " + err, Theme.Rec);
                        return;
                    }

                    LastDir = dir;
                    AppLog.Info($"Scene export: {res.Actors.Count} actor(s), {res.Files.Count} file(s) -> {dir}");
                    string warn = res.Warnings.Count > 0
                        ? Environment.NewLine + string.Join(Environment.NewLine, res.Warnings.Take(4))
                        : "";
                    Set(
                        $"Exported {res.Actors.Count} actor(s), {res.Files.Count} file(s) to{Environment.NewLine}{ShortPath(dir)}{warn}",
                        res.Warnings.Count > 0 ? Theme.Warn : Theme.Ok);
                    if (openBlender) OpenInBlender(ui, res, toUnity);
                });
            }) { IsBackground = true, Name = "scene export" }.Start();
        }

        /// <summary>Long paths don't wrap in the panel: keep the last folders (Open export folder shows the rest).</summary>
        static string ShortPath(string dir)
        {
            if (dir.Length <= 56) return dir;
            string[] parts = dir.Split(Path.DirectorySeparatorChar);
            string tail = parts.Length >= 3 ? string.Join("\\", parts.Skip(parts.Length - 3)) : dir;
            return "…\\" + tail;
        }

        static void OpenInBlender(Control ui, SceneExportResult res, bool toUnity)
        {
            if (res.Manifest == null || res.Actors.All(a => a.Fbx == null))
            {
                Set("Nothing to open: export with FBX ticked.", Theme.Warn);
                return;
            }

            string err = BlenderLocator.OpenExport(res.Manifest, toUnity);
            if (err != null) Set(err, Theme.Rec);
        }

        public static void OpenFolder(SceneEditor ed)
        {
            string dir = LastDir;
            if ((dir == null || !Directory.Exists(dir)) && ed.Scene != null)
                dir = TargetDir(ed.S, ed.Scene, ToUnity(ed.S));
            if (dir != null && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
            if (dir != null && Directory.Exists(dir)) Process.Start("explorer.exe", dir);
        }
    }
}
