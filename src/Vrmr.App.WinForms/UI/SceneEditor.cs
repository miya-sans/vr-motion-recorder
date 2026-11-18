using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualBasic.FileIO;
using SearchOption = System.IO.SearchOption;
using Timer = System.Windows.Forms.Timer;

namespace VRMR.UI
{
    /// <summary>
    ///     A feature that plugs into the scene editor (found by reflection, attached once): it can add toolbar buttons,
    ///     strip context-menu items, react to recording / evaluation, register viewport tools.
    /// </summary>
    public interface ISceneModule
    {
        void Attach(SceneEditor ed);
    }

    /// <summary>A viewport tool with a toolbar toggle (e.g. IK posing), found by reflection.</summary>
    public interface ISceneTool
    {
        string Name { get; }
        string Tip { get; }
        int Order { get; }

        /// <summary>Creates the tool for the editor (called when it is switched on).</summary>
        Viewport3D.ITool Create(SceneEditor ed);
    }

    /// <summary>
    ///     The scene editor: the open scene, undo history, evaluation (in the background), selection, playback, the NLA
    ///     panel (toolbar + NlaEditor) and the Scene tab's inspector. MainForm hosts its panels; features use its API.
    /// </summary>
    public sealed class SceneEditor : IDisposable
    {
        public readonly SceneEvaluator Eval;
        public readonly InspectorHost Inspector;
        public readonly NlaEditor Nla;

        /// <summary>Toolbar + NLA editor (the form's bottom area in scene mode).</summary>
        public readonly Panel Panel;

        public readonly Settings S;
        public readonly Viewport3D View;
        readonly FlowLayoutPanel _bar;

        // =====================================================================================================  evaluation

        readonly Timer _evalTimer;
        readonly Dictionary<string, Vec3[]> _figPos = new();
        readonly Dictionary<string, Quat[]> _figRot = new();
        readonly SceneHistory _hist = new();
        readonly List<ISceneModule> _modules = new();
        readonly List<(ISceneTool Tool, DarkButton Button)> _tools = new();
        readonly Control _ui;

        /// <summary>Avatar library choices for actors: id, label (set by the host).</summary>
        public Func<List<(string Id, string Label)>> AvatarChoices = () => new List<(string, string)>();

        public Func<bool> IsRecording = () => false;

        public long LastEvalMs;
        public bool Loop = true;

        /// <summary>Opens a take in the Clip tab (calibration, tracker roles), set by the host.</summary>
        public Action<string> OpenTake;

        // playback
        public bool Playing;

        /// <summary>
        ///     Seconds since the current recording's t = 0 (set by the host); Clock.Now - RecordingTime() = its start on the
        ///     shared clock.
        /// </summary>
        public Func<double> RecordingTime = () => 0;

        // ---------------------------------------------------------------- adding takes and keyframes

        /// <summary>The take path the host's list has selected (for "+ Take").</summary>
        public Func<string> SelectedTakePath = () => null;

        public double SpeedMul = 1;

        /// <summary>Lets a feature start / stop a recording (set by the host).</summary>
        public Action StartRecording, StopRecording;

        bool _evalAgain;
        volatile bool _evaluating;

        Dictionary<string, float> _face;

        bool _inEdit;
        DarkButton _playBtn, _undoBtn, _redoBtn, _snapBtn;

        public SceneEditor(Settings s, SceneContext ctx, Viewport3D view, Control ui)
        {
            S = s;
            View = view;
            _ui = ui;
            Eval = new SceneEvaluator(ctx);
            Eval.Takes.Changed += () => Post(() =>
            {
                RequestEvaluate();
                UpdateStatus();
            });
            Nla = new NlaEditor { Dock = DockStyle.Fill };
            Inspector = new InspectorHost();
            Panel = new Panel { BackColor = Theme.Panel };
            // wraps onto a second row when the window is narrow (feature buttons add up)
            _bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = Theme.S(38), BackColor = Theme.Panel,
                Padding = new Padding(Theme.S(6), Theme.S(5), Theme.S(6), 0), WrapContents = true, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            BuildToolbar();
            Panel.Controls.Add(Nla);
            Panel.Controls.Add(_bar);

            Nla.Scrub += t =>
            {
                Playing = false;
                UpdatePlayBtn();
                ShowFrame();
            };
            Nla.SelectionChanged += () =>
            {
                Inspector.Rebuild(this);
                SelectionChanged?.Invoke();
            };
            Nla.BeforeEdit += label =>
            {
                if (Scene != null) _hist.Checkpoint(Scene, label);
            };
            Nla.EditCancelled += () => _hist.DropLast();
            Nla.Changing += () =>
            {
                Dirty = true;
                RequestEvaluate(true);
            };
            Nla.Edited += () => AfterEdit(true);
            Nla.DropTake += (a, t, time, path) => AddTakeAt(path, a, t, time);
            Nla.StripActivated += st =>
            {
                if (st.Kind == StripKind.Take)
                {
                    var tr = Scene.Take(st.Source);
                    if (tr != null) OpenTake?.Invoke(tr.Path);
                }
            };
            Nla.SourceName = st =>
                st.Kind == StripKind.Take ? Scene?.Take(st.Source)?.Name ?? "?" : Scene?.Action(st.Source)?.Name ?? "?";
            Nla.KeyTimes = st => Scene?.Action(st.Source)?.KeyTimes() ?? new List<double>();

            _evalTimer = new Timer { Interval = 40 };
            _evalTimer.Tick += (o, e) =>
            {
                _evalTimer.Stop();
                StartEvaluate();
            };

            // features
            foreach (var t in typeof(SceneEditor).Assembly.GetTypes())
            {
                if (t.IsAbstract || t.IsInterface || t.GetConstructor(Type.EmptyTypes) == null) continue;
                try
                {
                    if (typeof(ISceneModule).IsAssignableFrom(t))
                        _modules.Add((ISceneModule)Activator.CreateInstance(t));
                    if (typeof(ISceneTool).IsAssignableFrom(t)) AddTool((ISceneTool)Activator.CreateInstance(t));
                }
                catch (Exception ex) { AppLog.Warn("Scene feature " + t.Name + ": " + ex.Message); }
            }

            foreach (var m in _modules)
                try { m.Attach(this); }
                catch (Exception ex) { AppLog.Error("Scene module " + m.GetType().Name + ": " + ex); }
        }

        public Scene Scene { get; private set; }
        public SceneBake Bake { get; private set; }
        public bool Dirty { get; private set; }

        public ISceneTool ActiveTool { get; private set; }

        // =====================================================================================================  selection

        public Actor SelectedActor => Scene == null
            ? null
            : Scene.ActorById(Nla.SelActor) ?? (Nla.SelTrack != null ? Scene.FindTrack(Nla.SelTrack).Actor : null) ??
            Scene.Actors.FirstOrDefault();

        public NlaTrack SelectedTrack =>
            Scene == null || Nla.SelTrack == null ? null : Scene.FindTrack(Nla.SelTrack).Track;

        public List<Strip> SelectedStrips => Scene == null
            ? new List<Strip>()
            : Scene.AllStrips().Where(x => Nla.SelStrips.Contains(x.Strip.Id)).Select(x => x.Strip).ToList();

        public Strip SelectedStrip => SelectedStrips.FirstOrDefault();

        public Transition SelectedTransition => Scene == null || Nla.SelTransition == null
            ? null
            : Scene.FindTransition(Nla.SelTransition).Transition;

        /// <summary>What the inspector shows: "strip", "transition", "track", "actor" or "scene".</summary>
        public string SelectionKind => SelectedTransition != null ? "transition" :
            Nla.SelStrips.Count > 0 && SelectedStrip != null ? "strip" :
            SelectedTrack != null ? "track" :
            Scene?.ActorById(Nla.SelActor) != null ? "actor" : "scene";

        // =====================================================================================================  playback / display

        public double Playhead
        {
            get => Nla.Playhead;
            set
            {
                Nla.Playhead = value;
                Nla.Invalidate();
                ShowFrame();
            }
        }

        /// <summary>The actor shown with its mesh in the 3D view (the selected one, else the first visible one).</summary>
        public Actor ViewActor => Scene == null ? null :
            Scene.ActorById(Nla.SelActor) is Actor a && !a.Hidden ? a : Scene.Actors.FirstOrDefault(x => !x.Hidden);

        // =====================================================================================================  files

        public string ScenesDir => Scene.DefaultDir(S.RecordingsDir);

        public void Dispose() { Eval.Dispose(); }

        /// <summary>A different scene object (opened, new, undo / redo).</summary>
        public event Action SceneChanged;

        /// <summary>A new evaluation is available (Bake).</summary>
        public event Action Evaluated;

        public event Action SelectionChanged;

        /// <summary>The scene was edited (after the change, before the evaluation).</summary>
        public event Action Edited;

        /// <summary>A recording started / stopped while the scene editor was open (for punch-in capture).</summary>
        public event Action<Recording> RecordingStarted;

        public event Action<Recording> RecordingStopped;

        void Post(Action a)
        {
            try
            {
                if (_ui.IsHandleCreated && !_ui.IsDisposed) _ui.BeginInvoke(a);
            }
            catch { }
        }

        // =====================================================================================================  toolbar

        DarkButton Bar(string text, Action click, string tip, int w = 0, DarkButton.Kind kind = DarkButton.Kind.Normal)
        {
            int width = w > 0
                ? Theme.S(w)
                : Math.Max(Theme.S(34), TextRenderer.MeasureText(text, Theme.UI).Width + Theme.S(22));
            var b = new DarkButton(text, kind)
                { Width = width, Height = Theme.S(28), Margin = new Padding(0, 0, Theme.S(4), Theme.S(4)) };
            b.Click += (o, e) => click();
            if (tip != null) Theme.Tip(b, tip);
            _bar.Controls.Add(b);
            return b;
        }

        DarkButton IconBar(Theme.Icon icon, Action click, string tip, int w = 34)
        {
            var b = Bar("", click, tip, w);
            b.IconKind = icon;
            return b;
        }

        /// <summary>A thin group separator (like the take transport's).</summary>
        void Gap()
        {
            _bar.Controls.Add(new Sep());
        }

        void BuildToolbar()
        {
            IconBar(Theme.Icon.Prev, () => Seek(Scene?.Start ?? 0), "Go to the scene start (Home)");
            _playBtn = Bar("Play", TogglePlay, "Play / pause (Space)", 78);
            _playBtn.IconKind = Theme.Icon.Play;
            IconBar(Theme.Icon.Next, () => Seek(Scene?.RangeEnd ?? 0), "Go to the scene end (End)");
            var loop = Bar("Loop", () => { }, "Loop playback", 52);
            loop.Toggle = true;
            loop.Checked = Loop;
            loop.CheckedChanged += (o, e) => Loop = loop.Checked;
            Gap();
            Bar("+ Take", AddSelectedTake,
                "Add the take selected in the list at the playhead (or drag a take onto a track)");
            Bar("+ Keys", AddKeyframeStrip,
                "A keyframe strip on a new track: animate without mocap, or on top of it (Blend: Add)");
            Bar("+ Inputs", () => AddInputTrack(),
                "An input track: change what the IK solve is fed (move trackers / headset / controllers, switch their influence, re-recorded devices, face or finger tracking) for the takes below it");
            Bar("Cut", () => Nla.SplitAtPlayhead(),
                "Cut the selected strips at the playhead (K). Nothing is deleted: both halves still use the whole take.");
            Bar("Transition", () => Nla.TransitionSelected(),
                "Crossfade the selected strip into the next one on its track (T)");
            Bar("Delete", () => Nla.DeleteSelected(),
                "Remove the selected strips from the scene (Del). The takes are not touched.");
            Gap();
            _snapBtn = Bar("Snap", () => { },
                "Snap strips to edges, the playhead and frames (hold Ctrl to move freely)", 52);
            _snapBtn.Toggle = true;
            _snapBtn.Checked = true;
            _snapBtn.CheckedChanged += (o, e) => Nla.Snap = _snapBtn.Checked;
            _undoBtn = IconBar(Theme.Icon.Undo, Undo, "Undo (Ctrl+Z)");
            _redoBtn = IconBar(Theme.Icon.Redo, Redo, "Redo (Ctrl+Shift+Z or Ctrl+Y)");
        }

        /// <summary>Adds a toolbar button (for features).</summary>
        public DarkButton AddToolbarButton(string text, Action click, string tip = null)
        {
            var b = Bar(text, click, tip);
            // feature buttons go with the tools, before Undo / Redo
            if (_undoBtn != null) _bar.Controls.SetChildIndex(b, _bar.Controls.IndexOf(_undoBtn));
            return b;
        }

        void AddTool(ISceneTool tool)
        {
            var b = Bar(tool.Name, () => { }, tool.Tip);
            b.Toggle = true;
            b.CheckedChanged += (o, e) => SetTool(b.Checked ? tool : null);
            _tools.Add((tool, b));
            // keep tool buttons after the edit buttons, before undo
            _bar.Controls.SetChildIndex(b, _bar.Controls.IndexOf(_undoBtn));
        }

        public void SetTool(ISceneTool tool)
        {
            ActiveTool = tool;
            foreach (var (t, b) in _tools)
            {
                b.Checked = t == tool;
                b.Invalidate();
            }

            View.Tool = tool?.Create(this);
            View.Invalidate();
        }

        /// <summary>Switches to the registered tool with this name (null / unknown = no tool); lights its toolbar toggle.</summary>
        public void SetTool(string name)
        {
            SetTool(_tools.Select(x => x.Tool).FirstOrDefault(t => t.Name == name));
        }

        void UpdatePlayBtn()
        {
            _playBtn.Text = Playing ? "Pause" : "Play";
            _playBtn.IconKind = Playing ? Theme.Icon.Pause : Theme.Icon.Play;
            _playBtn.Invalidate();
        }

        void UpdateStatus()
        {
            if (_undoBtn == null) return;
            // status in the NLA ruler's corner: solving, else the first warning
            Nla.Status = Eval.Takes.Working ? "solving…" : Bake?.Warnings.Count > 0 ? "⚠ " + Bake.Warnings[0] : "";
            _undoBtn.Enabled = _hist.CanUndo;
            _redoBtn.Enabled = _hist.CanRedo;
            Nla.Invalidate();
        }

        public void Select(Strip st)
        {
            Nla.SelStrips.Clear();
            Nla.SelTransition = null;
            if (st != null)
            {
                Nla.SelStrips.Add(st.Id);
                var x = Scene.FindStrip(st.Id);
                Nla.SelTrack = x.Track?.Id;
                Nla.SelActor = x.Actor?.Id;
            }

            Nla.Invalidate();
            Inspector.Rebuild(this);
            SelectionChanged?.Invoke();
        }

        public void Select(Transition tr)
        {
            Nla.SelStrips.Clear();
            Nla.SelTransition = tr?.Id;
            if (tr != null)
            {
                var x = Scene.FindTransition(tr.Id);
                Nla.SelTrack = x.Track?.Id;
                Nla.SelActor = x.Actor?.Id;
            }

            Nla.Invalidate();
            Inspector.Rebuild(this);
            SelectionChanged?.Invoke();
        }

        // =====================================================================================================  editing

        /// <summary>One undoable edit: checkpoint, change, re-evaluate.</summary>
        public void Edit(string label, Action change)
        {
            if (Scene == null) return;
            _hist.Checkpoint(Scene, label);
            change();
            AfterEdit(true);
        }

        /// <summary>Starts a continuous edit (a drag): one undo step until EndEdit; call Changing() while it changes.</summary>
        public void BeginEdit(string label)
        {
            if (Scene == null || _inEdit) return;
            _inEdit = true;
            _hist.Checkpoint(Scene, label);
            Eval.PreviewAt = Nla.Playhead; // re-solves only a window around the playhead until EndEdit
        }

        public void Changing()
        {
            Dirty = true;
            RequestEvaluate(true);
            Nla.Invalidate();
        }

        public void EndEdit()
        {
            if (!_inEdit) return;
            _inEdit = false;
            Eval.PreviewAt = double.NaN;
            AfterEdit(true);
        }

        void AfterEdit(bool rebuildInspector)
        {
            SceneOps.NormalizeAll(Scene);
            Dirty = true;
            Edited?.Invoke();
            if (rebuildInspector) Inspector.Rebuild(this);
            else Inspector.RefreshValues();
            Nla.Invalidate();
            UpdateStatus();
            RequestEvaluate();
        }

        public void Undo()
        {
            if (Scene == null || !_hist.CanUndo) return;
            SetSceneObject(_hist.Undo(Scene));
            Dirty = true;
            UpdateStatus();
        }

        public void Redo()
        {
            if (Scene == null || !_hist.CanRedo) return;
            SetSceneObject(_hist.Redo(Scene));
            Dirty = true;
            UpdateStatus();
        }

        void SetSceneObject(Scene s)
        {
            Scene = s;
            Nla.Scene = s;
            // drop selection that no longer exists
            if (s != null)
            {
                Nla.SelStrips.RemoveWhere(id => s.FindStrip(id).Strip == null);
                if (Nla.SelTransition != null && s.FindTransition(Nla.SelTransition).Transition == null)
                    Nla.SelTransition = null;
                if (Nla.SelTrack != null && s.FindTrack(Nla.SelTrack).Track == null) Nla.SelTrack = null;
                if (Nla.SelActor != null && s.ActorById(Nla.SelActor) == null) Nla.SelActor = null;
            }

            SceneChanged?.Invoke();
            Inspector.Rebuild(this);
            Nla.Invalidate();
            RequestEvaluate();
        }

        void AddSelectedTake()
        {
            string p = SelectedTakePath();
            if (string.IsNullOrEmpty(p))
            {
                MessageBox.Show(_ui, "Pick a take in the list on the left first (or drag it onto a track).",
                    "Add take");
                return;
            }

            var a = SelectedActor ?? Scene?.Actors.FirstOrDefault();
            AddTakeAt(p, a, SelectedTrack, Nla.Playhead);
        }

        /// <summary>Adds a whole take as a strip at scene time t on a track (a free track above if it is taken).</summary>
        public Strip AddTakeAt(string path, Actor actor, NlaTrack track, double t)
        {
            if (Scene == null || string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            RecordingIO.Summary sum;
            try { sum = RecordingIO.ReadSummary(path); }
            catch (Exception ex)
            {
                MessageBox.Show(_ui, "Can't read the take:\n" + ex.Message, "Add take");
                return null;
            }

            Strip made = null;
            Edit("Add take", () =>
            {
                actor = actor ?? Scene.Actors.FirstOrDefault();
                if (actor == null)
                {
                    actor = new Actor { Id = Scene.NewId("a"), Name = "Actor 1" };
                    Scene.Actors.Add(actor);
                }

                if (actor.Tracks.Count == 0) SceneOps.AddTrack(actor, 0, "Base");
                var tr = Scene.Takes.FirstOrDefault(x =>
                    string.Equals(x.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
                if (tr == null)
                {
                    tr = new TakeRef
                    {
                        Id = Scene.NewId("t"), Path = Path.GetFullPath(path), Name = sum.Name, Created = sum.Created,
                        Duration = sum.Duration
                    };
                    Scene.Takes.Add(tr);
                }

                double len = Math.Max(0.1, sum.Duration);
                double start = Math.Max(0, Math.Round(t * Scene.Fps) / Scene.Fps);
                var dest = SceneOps.FreeTrack(actor,
                    track ?? actor.Tracks.LastOrDefault(x => x.Strips.Count == 0) ?? actor.Tracks[0], start,
                    start + len);
                made = SceneOps.AddTakeStrip(Scene, dest, tr, start, 0, len);
            });
            if (made != null) Select(made);
            return made;
        }

        void AddKeyframeStrip()
        {
            if (Scene == null) return;
            var a = SelectedActor ?? Scene.Actors.FirstOrDefault();
            if (a == null) return;
            Strip made = null;
            Edit("Add keyframes", () =>
            {
                var t = SceneOps.AddTrack(a, a.Tracks.Count, "Keys " + (Scene.Actions.Count + 1));
                bool onTop = a.Tracks.Any(x => x != t && x.Strips.Count > 0);
                double start = onTop ? Scene.Start : Math.Max(0, Nla.Playhead);
                double len = onTop ? Math.Max(1, Scene.RangeEnd - Scene.Start) : 5;
                made = SceneOps.AddActionStrip(Scene, t, onTop ? "Keys (Add)" : "Keyframes", start, len,
                    onTop ? BlendMode.Add : BlendMode.Replace);
            });
            if (made != null) Select(made);
        }

        /// <summary>A new input track above the selected track (or on top).</summary>
        public NlaTrack AddInputTrack()
        {
            if (Scene == null) return null;
            var a = SelectedActor ?? Scene.Actors.FirstOrDefault();
            if (a == null) return null;
            NlaTrack made = null;
            var sel = SelectedTrack;
            Edit("Add input track", () =>
            {
                int idx = sel != null && a.Tracks.Contains(sel) ? a.Tracks.IndexOf(sel) + 1 : a.Tracks.Count;
                made = SceneOps.AddTrack(a, idx, "Inputs " + (a.Tracks.Count(t => t.Kind == TrackKind.Input) + 1));
                made.Kind = TrackKind.Input;
            });
            if (made != null)
            {
                Nla.SelTrack = made.Id;
                Nla.SelStrips.Clear();
                Nla.SelTransition = null;
                Nla.Invalidate();
                Inspector.Rebuild(this);
            }

            return made;
        }

        /// <summary>Re-evaluates soon (debounced; fast = during a drag).</summary>
        public void RequestEvaluate(bool fast = false)
        {
            if (Scene == null) return;
            _evalTimer.Interval = fast ? 15 : 40;
            _evalTimer.Stop();
            _evalTimer.Start();
        }

        void StartEvaluate()
        {
            if (Scene == null) return;
            if (_evaluating)
            {
                _evalAgain = true;
                return;
            }

            _evaluating = true;
            var snap = Scene.Clone();
            var sw = Stopwatch.StartNew();
            new Thread(() =>
            {
                SceneBake b = null;
                try { b = Eval.Bake(snap); }
                catch (Exception ex) { AppLog.Error("Scene evaluate: " + ex); }

                Post(() =>
                {
                    _evaluating = false;
                    if (b != null)
                    {
                        // the bake refers to the snapshot's objects: map them back to the live scene by id
                        Bake = b;
                        LastEvalMs = sw.ElapsedMilliseconds;
                        ShowFrame();
                        Inspector.RefreshValues();
                        Evaluated?.Invoke();
                    }

                    UpdateStatus();
                    if (_evalAgain)
                    {
                        _evalAgain = false;
                        StartEvaluate();
                    }
                });
            }) { IsBackground = true, Name = "scene eval" }.Start();
        }

        /// <summary>The evaluated clip of an actor (by id) from the last evaluation.</summary>
        public BakedClip ClipOf(Actor a)
        {
            return a == null ? null : Bake?.ClipOf(a.Id);
        }

        public void Seek(double t)
        {
            Playing = false;
            UpdatePlayBtn();
            Playhead = t;
        }

        public void TogglePlay()
        {
            if (Scene == null) return;
            Playing = !Playing;
            if (Playing && (Playhead >= Scene.RangeEnd - 1e-3 || Playhead < Scene.Start)) Nla.Playhead = Scene.Start;
            UpdatePlayBtn();
        }

        public void Step(int frames)
        {
            if (Scene == null) return;
            Playing = false;
            UpdatePlayBtn();
            Playhead = Math.Round(Playhead * Scene.Fps + frames) / Scene.Fps;
        }

        /// <summary>Advances playback (called by the host's UI timer).</summary>
        public void Tick(double dt)
        {
            if (!Playing || Scene == null) return;
            double t = Nla.Playhead + dt * SpeedMul, end = Scene.RangeEnd;
            if (t > end)
            {
                if (Loop) t = Scene.Start;
                else
                {
                    t = end;
                    Playing = false;
                    UpdatePlayBtn();
                }
            }

            Nla.Playhead = t;
            // keep the playhead in view
            if (t > Nla.ViewEnd || t < Nla.ViewStart)
            {
                double w = Nla.ViewEnd - Nla.ViewStart;
                Nla.ViewStart = t - w * 0.1;
                Nla.ViewEnd = Nla.ViewStart + w;
            }

            Nla.Invalidate();
            ShowFrame();
        }

        /// <summary>Shows the evaluated scene at the playhead in the 3D view.</summary>
        public void ShowFrame()
        {
            var b = Bake;
            View.Markers.Clear();
            View.Figures.Clear();
            View.PlayArea = null;
            if (b == null || Scene == null)
            {
                View.Overlay = Scene == null ? "" : "Scene: evaluating…";
                View.Invalidate();
                return;
            }

            int f = b.FrameAt(Nla.Playhead);
            var main = ViewActor;
            foreach (var (actor, clip) in b.Actors)
            {
                var live = Scene.ActorById(actor.Id);
                if (clip == null || live == null || live.Hidden) continue;
                if (main != null && actor.Id == main.Id)
                {
                    if (View.Profile != clip.Profile) View.SetProfile(clip.Profile);
                    var wr = View.NodeRot != null && View.NodeRot.Length == clip.Profile.Nodes.Count
                        ? View.NodeRot
                        : new Quat[clip.Profile.Nodes.Count];
                    clip.EvaluateNodes(f, View.NodePos, wr);
                    View.NodeRot = wr;
                    View.MeshScale = clip.ScaleAt(f);
                    var fw = _face ?? (_face = new Dictionary<string, float>());
                    fw.Clear();
                    foreach (var kv in clip.Face) fw[kv.Key] = kv.Value[f];
                    View.FaceWeights = fw;
                    float devSize = clip.Calibration != null && clip.Calibration.TrackScale > 0.05f
                        ? clip.Calibration.TrackScale
                        : 1f;
                    // the devices as the IK solve was fed (input-track edits included); a device switched off (influence) is faded
                    foreach (var kv in clip.Trackers)
                        if (clip.TrackerValid[kv.Key][f])
                        {
                            bool hand = kv.Key == "LeftHand" || kv.Key == "RightHand", head = kv.Key == "Head";
                            float inf = clip.TrackerInfluence != null &&
                                        clip.TrackerInfluence.TryGetValue(kv.Key, out float[] ia)
                                ? ia[f]
                                : 1f;
                            View.Markers.Add(new Viewport3D.Marker
                            {
                                Pose = kv.Value[f], Color = head ? Theme.Center : hand ? Theme.Accent : Theme.TextDim,
                                Kind = head ? Viewport3D.KHmd : hand ? Viewport3D.KController : Viewport3D.KTracker,
                                Size = devSize, NoDropLine = true,
                                Faded = inf < 0.5f // tools draw their own labels
                            });
                        }
                }
                else
                {
                    int n = clip.Profile.Nodes.Count;
                    if (!_figPos.TryGetValue(actor.Id, out var pos) || pos.Length != n)
                    {
                        _figPos[actor.Id] = pos = new Vec3[n];
                        _figRot[actor.Id] = new Quat[n];
                    }

                    clip.EvaluateNodes(f, pos, _figRot[actor.Id]);
                    View.Figures.Add((clip.Profile, pos, Color.FromArgb(200, 150, 180, 230)));
                }
            }

            if (main == null || b.ClipOf(main.Id) == null) View.SetProfile(null);
            View.Overlay =
                $"SCENE  {Scene.Name}\nframe {Math.Round(Nla.Playhead * Scene.Fps)}   {Theme.Time(Nla.Playhead)}" +
                (Eval.Takes.Working ? "\nsolving takes…" : "");
            View.OverlayRight = main?.Name ?? "";
            View.Invalidate();
        }

        public void NewScene(string name = null)
        {
            if (!ConfirmDiscard()) return;
            name = name ?? NextSceneName();
            var s = Scene.CreateNew(name);
            s.FilePath = Path.Combine(ScenesDir, SafeName(name) + Scene.Extension);
            _hist.Clear();
            SetSceneObject(s);
            Dirty = true;
            Nla.Playhead = 0;
            Nla.ViewStart = -0.5;
            Nla.ViewEnd = 20;
            Save();
        }

        string NextSceneName()
        {
            for (int i = 1;; i++)
            {
                string n = "Scene " + i;
                if (!File.Exists(Path.Combine(ScenesDir, SafeName(n) + Scene.Extension))) return n;
            }
        }

        public static string SafeName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim().Length == 0 ? "Scene" : s.Trim();
        }

        public bool Open(string path)
        {
            if (Scene != null && Scene.FilePath != null && string.Equals(Path.GetFullPath(Scene.FilePath),
                    Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return true;
            if (!ConfirmDiscard()) return false;
            Scene s;
            try { s = Scene.Load(path); }
            catch (Exception ex)
            {
                MessageBox.Show(_ui, "Could not open the scene:\n" + ex.Message, "VR Motion Recorder");
                return false;
            }

            Relink(s);
            _hist.Clear();
            SetSceneObject(s);
            Dirty = false;
            S.LastScene = path;
            S.Save();
            Nla.Playhead = s.Start;
            Nla.FrameAll();
            UpdateStatus();
            return true;
        }

        /// <summary>Takes that moved: found again in the recordings folder by name and creation time.</summary>
        void Relink(Scene s)
        {
            var missing = s.Takes.Where(t => !File.Exists(t.Path)).ToList();
            if (missing.Count == 0 || !Directory.Exists(S.RecordingsDir)) return;
            foreach (string f in Directory.EnumerateFiles(S.RecordingsDir, "*.vrmr", SearchOption.AllDirectories))
            {
                RecordingIO.Summary sum;
                try { sum = RecordingIO.ReadSummary(f); }
                catch { continue; }

                foreach (var t in missing.ToList())
                    if (!string.IsNullOrEmpty(t.Created) && t.Created == sum.Created)
                    {
                        AppLog.Info($"Scene: take {t.Name} relinked to {f}");
                        t.Path = f;
                        missing.Remove(t);
                    }

                if (missing.Count == 0) break;
            }
        }

        public void Save()
        {
            if (Scene == null) return;
            if (string.IsNullOrEmpty(Scene.FilePath))
                Scene.FilePath = Path.Combine(ScenesDir, SafeName(Scene.Name) + Scene.Extension);
            try
            {
                Scene.Save(Scene.FilePath);
                Dirty = false;
                S.LastScene = Scene.FilePath;
                S.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show(_ui, "Saving the scene failed:\n" + ex.Message, "VR Motion Recorder");
            }

            UpdateStatus();
            Saved?.Invoke();
        }

        public event Action Saved;

        /// <summary>Renames the scene and its file (the old file goes to the Recycle Bin after the new one is written).</summary>
        public void Rename(string name)
        {
            if (Scene == null || string.IsNullOrWhiteSpace(name)) return;
            string old = Scene.FilePath;
            string np = Path.Combine(ScenesDir, SafeName(name) + Scene.Extension);
            if (old != null &&
                !string.Equals(Path.GetFullPath(old), Path.GetFullPath(np), StringComparison.OrdinalIgnoreCase) &&
                File.Exists(np))
            {
                MessageBox.Show(_ui, "A scene with that name exists.", "Rename");
                return;
            }

            Edit("Rename scene", () => Scene.Name = name.Trim());
            Scene.FilePath = np;
            Save();
            if (old != null && File.Exists(old) && !string.Equals(Path.GetFullPath(old), Path.GetFullPath(np),
                    StringComparison.OrdinalIgnoreCase))
                try { FileSystem.DeleteFile(old, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); }
                catch { }
        }

        /// <summary>Unsaved changes: save them (Yes), drop them (No) or stay (Cancel).</summary>
        public bool ConfirmDiscard()
        {
            if (Scene == null || !Dirty) return true;
            var r = MessageBox.Show(_ui, $"Save changes to \"{Scene.Name}\"?", "VR Motion Recorder",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes) Save();
            return true;
        }

        public void Close()
        {
            Playing = false;
            Scene = null;
            Nla.Scene = null;
            Bake = null;
            _hist.Clear();
            Dirty = false;
            Inspector.Rebuild(this);
            SceneChanged?.Invoke();
            Nla.Invalidate();
        }

        // ---------------------------------------------------------------- recording hooks (called by the host)

        public void NotifyRecordingStarted(Recording r)
        {
            try { RecordingStarted?.Invoke(r); }
            catch (Exception ex) { AppLog.Error("Scene: " + ex); }
        }

        public void NotifyRecordingStopped(Recording r)
        {
            try { RecordingStopped?.Invoke(r); }
            catch (Exception ex) { AppLog.Error("Scene: " + ex); }
        }

        sealed class Sep : Control
        {
            public Sep()
            {
                Width = Theme.S(11);
                Height = Theme.S(28);
                BackColor = Theme.Panel;
                Margin = new Padding(0, 0, 0, Theme.S(4));
                SetStyle(
                    ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint,
                    true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(Theme.Panel);
                using (var p = new Pen(Theme.Border))
                    e.Graphics.DrawLine(p, Width / 2 - 2, Theme.S(5), Width / 2 - 2, Height - Theme.S(5));
            }
        }
    }
}
