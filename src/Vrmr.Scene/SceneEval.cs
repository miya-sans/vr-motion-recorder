using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace VRMR
{
    // =====================================================================================================  evaluation
    //
    //   raw take --(PoseBuilder.Bake over the take's whole source range, cached: TakeCache)--> TakeSolve
    //   TakeSolve / KeyAction --(strip timing, speed, root offset + transition alignment, strip modifiers)--> PoseRun
    //   PoseRuns --(per track: crossfade across transitions; tracks bottom -> top: Replace / Add, influence,
    //              blend in/out, body-part mask)--> pose per scene frame --(actor modifiers)--> BakedClip
    //
    // The output is an ordinary BakedClip, so the viewport and every exporter work on scenes unchanged.

    /// <summary>What the evaluator needs from the app (avatar library, solve settings).</summary>
    public sealed class SceneContext
    {
        /// <summary>Avatar library entry id -> skeleton (null if unknown).</summary>
        public Func<string, AvatarProfile> AvatarById = id => null;

        /// <summary>Solve settings for a take (the take's own calibration settings, the app's smoothing / face naming).</summary>
        public Func<Recording, BakeOptions> BakeOptionsFor = r => new BakeOptions();

        /// <summary>The skeleton a take is solved onto by default (its own target or the avatar it was recorded with).</summary>
        public Func<Recording, AvatarProfile> TakeAvatar = r => null;
    }

    /// <summary>
    ///     A take solved at the scene frame rate over a source range (frame 0 = source time Start), plus local rotations per
    ///     frame. With input tracks over it, the solve is of the take with edited devices (Edit).
    /// </summary>
    public sealed class TakeSolve
    {
        public BakedClip Clip;

        /// <summary>The device edits this solve was made with (null = the take as recorded).</summary>
        public InputEdit Edit;

        public float Fps;
        public Quat[][] Local;
        public string Path, Key, Slot;
        public Recording Rec;
        public PoseRig Rig;

        /// <summary>Source time of frame 0.</summary>
        public double Start;

        public int Version;
        public double Duration => Clip.Duration;

        double FrameF(double src)
        {
            return MathX.Clamp((src - Start) * Fps, 0, Clip.Frames - 1);
        }

        /// <summary>Pose at source time src (held before the start / after the end of the solved range).</summary>
        public void Sample(double src, ScenePose o, bool faceAndParams = true)
        {
            double ff = FrameF(src);
            int f0 = (int)Math.Floor(ff), f1 = Math.Min(Clip.Frames - 1, f0 + 1);
            float a = (float)(ff - f0);
            o.Hips = Vec3.Lerp(Clip.Hips[f0], Clip.Hips[f1], a);
            var l0 = Local[f0];
            var l1 = Local[f1];
            for (int b = 0; b < HumanBones.Count; b++) o.Local[b] = a <= 1e-5f ? l0[b] : Quat.Slerp(l0[b], l1[b], a);
            if (!faceAndParams) return;
            o.Face.Clear();
            foreach (var kv in Clip.Face) o.Face[kv.Key] = kv.Value[f0] + (kv.Value[f1] - kv.Value[f0]) * a;
            o.Params.Clear();
            foreach (var kv in Clip.ParamValues)
                o.Params[kv.Key] = Clip.ParamTypes.TryGetValue(kv.Key, out var ty) && ty != ParamType.Float
                    ? a < 0.5f ? kv.Value[f0] : kv.Value[f1]
                    : kv.Value[f0] + (kv.Value[f1] - kv.Value[f0]) * a;
        }

        public int FrameOf(double src)
        {
            return (int)Math.Round(FrameF(src));
        }

        /// <summary>
        ///     A device's pose (BakedClip.Trackers space) at source time src, interpolated; false if it isn't there / not
        ///     tracked.
        /// </summary>
        public bool Device(string role, double src, out Pose p)
        {
            p = Pose.Identity;
            if (!Clip.Trackers.TryGetValue(role, out var arr) ||
                !Clip.TrackerValid.TryGetValue(role, out bool[] ok)) return false;
            double ff = FrameF(src);
            int f0 = (int)Math.Floor(ff), f1 = Math.Min(Clip.Frames - 1, f0 + 1);
            float a = (float)(ff - f0);
            if (!ok[f0] && !ok[f1]) return false;
            if (!ok[f0])
            {
                p = arr[f1];
                return true;
            }

            if (!ok[f1])
            {
                p = arr[f0];
                return true;
            }

            p = Pose.Lerp(arr[f0], arr[f1], a);
            return true;
        }
    }

    /// <summary>
    ///     Loads takes (read-only) and solves them in the background, one at a time. A take is re-solved when its file
    ///     changes (its calibration was edited and saved in the Clip tab), when the avatar / settings change, or when the
    ///     input tracks over a strip change (an edited solve, per strip "slot"). While a slot's new solve is on its way, its
    ///     previous one is handed out (no flicker while dragging a device).
    /// </summary>
    public sealed class TakeCache : IDisposable
    {
        /// <summary>Held while any solve runs (the scene's solves and the app's take solve don't run at once).</summary>
        public static readonly object SolveLock = new();

        readonly Dictionary<string, TakeSolve> _bySlot = new();

        readonly SceneContext _ctx;
        readonly List<Job> _jobs = new();
        readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);
        readonly object _lock = new();
        readonly Dictionary<string, RecEntry> _recs = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> _solveErrors = new();
        readonly Dictionary<string, TakeSolve> _solves = new();
        readonly AutoResetEvent _wake = new(false);

        /// <summary>What the worker is doing ("" = idle).</summary>
        public volatile string Busy = "";

        /// <summary>Last solve time per kind (ms), for the status line.</summary>
        public long LastSolveMs;

        public float Progress;
        volatile bool _stop;
        Thread _thread;
        int _version;

        public TakeCache(SceneContext ctx) { _ctx = ctx; }

        /// <summary>Anything queued or running.</summary>
        public bool Working
        {
            get
            {
                lock (_lock) return _jobs.Count > 0 || Busy.Length > 0;
            }
        }

        public void Dispose()
        {
            _stop = true;
            _wake.Set();
        }

        /// <summary>A load or solve finished (raised on the worker thread).</summary>
        public event Action Changed;

        static DateTime FileTime(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch { return DateTime.MinValue; }
        }

        /// <summary>The take, loaded and current; null while it loads (a load is queued) or when it can't be read (see Error).</summary>
        public Recording Recording(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            lock (_lock)
            {
                if (_recs.TryGetValue(path, out var e) && e.Rec != null && e.Time == FileTime(path)) return e.Rec;
                if (e != null && e.Error != null && e.Time == FileTime(path)) return null;
                if (_loading.Add(path))
                {
                    _jobs.Add(new Job { Path = path });
                    Start();
                }

                return null;
            }
        }

        public string Error(string path)
        {
            lock (_lock)
            {
                if (!File.Exists(path)) return "missing: " + path;
                if (_recs.TryGetValue(path, out var e) && e.Error != null) return e.Error;
                foreach (var kv in _solveErrors)
                    if (kv.Key.StartsWith(path + "|", StringComparison.OrdinalIgnoreCase))
                        return kv.Value;
                return null;
            }
        }

        static string TargetKey(AvatarProfile p)
        {
            return p == null
                ? "-"
                : $"{p.LibraryId}|{p.Name}|{p.SourcePath}|{p.Nodes.Count}|{p.EyeHeight:0.0000}|{p.Scale:0.0000}";
        }

        static string OptionsKey(BakeOptions o)
        {
            return
                $"{o.Fps}|{o.Smoothing:0.###}|{o.FaceNaming}|{o.EyeYaw}|{o.EyePitch}|{o.FingersFromGestures}|{o.EyesFromTracking}|{(o.Calibration != null ? Json.Serialize(o.Calibration.ToJson()) : "")}";
        }

        /// <summary>The take as recorded, solved onto target at fps over its whole length: ready and current, else null (queued).</summary>
        public TakeSolve Solve(string path, AvatarProfile target, float fps)
        {
            return Solve(path, target, fps, null, null, null, out _);
        }

        /// <summary>
        ///     The take solved onto target at fps: as recorded (edit null) or with edited devices, over a source range (null =
        ///     all of it). Returns the ready solve; while it is being made: the slot's previous solve (stale) or null, and
        ///     pending = true.
        /// </summary>
        public TakeSolve Solve(string path, AvatarProfile target, float fps, InputEdit edit, double[] range,
            string slot, out bool pending)
        {
            pending = false;
            var rec = Recording(path);
            if (rec == null)
            {
                pending = Error(path) == null;
                return null;
            }

            BakeOptions opt;
            lock (rec.Sync) opt = _ctx.BakeOptionsFor(rec);
            opt.Fps = fps;
            string key = path + "|" + FileTime(path).Ticks + "|" + TargetKey(target) + "|" + OptionsKey(opt) + "|" +
                         (range != null ? $"{range[0]:0.####}-{range[1]:0.####}" : "all") + "|" + (edit?.Key ?? "");
            slot = slot ?? path + "|" + TargetKey(target) + "|base";
            lock (_lock)
            {
                if (_solves.TryGetValue(key, out var s))
                {
                    _bySlot[slot] = s;
                    return s;
                }

                if (_solveErrors.ContainsKey(key)) return null;
                pending = true;
                if (!_jobs.Any(j => j.Key == key) && Busy != key)
                {
                    _jobs.RemoveAll(j => j.Slot == slot && j.Key != null); // a newer edit replaces a queued one
                    _jobs.Add(new Job
                    {
                        Path = path, Target = target, Fps = fps, Key = key, Slot = slot, Edit = edit, Range = range
                    });
                    Start();
                }

                return _bySlot.TryGetValue(slot, out var stale) ? stale : null;
            }
        }

        void Start()
        {
            if (_thread != null)
            {
                _wake.Set();
                return;
            }

            _thread = new Thread(Worker)
                { IsBackground = true, Name = "scene takes", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        void Worker()
        {
            while (!_stop)
            {
                Job job = null;
                lock (_lock)
                {
                    // loads first (cheap and needed to know what to solve), then edited solves (interactive), then the rest
                    job = _jobs.FirstOrDefault(j => j.Key == null) ??
                          _jobs.FirstOrDefault(j => j.Edit != null) ?? _jobs.FirstOrDefault();
                    if (job != null)
                    {
                        _jobs.Remove(job);
                        Busy = job.Key ?? "load|" + job.Path;
                    }
                }

                if (job == null)
                {
                    _wake.WaitOne(500);
                    continue;
                }

                try
                {
                    if (job.Key == null) Load(job.Path);
                    else SolveNow(job);
                }
                catch (Exception ex) { AppLog.Error("Scene take: " + ex); }
                finally { Busy = ""; }

                try { Changed?.Invoke(); }
                catch { }
            }
        }

        void Load(string path)
        {
            var t = FileTime(path);
            Recording rec = null;
            string err = null;
            try { rec = RecordingIO.Load(path); }
            catch (Exception ex) { err = ex.Message; }

            lock (_lock)
            {
                _recs[path] = new RecEntry { Rec = rec, Time = t, Error = err };
                _loading.Remove(path);
            }
        }

        void SolveNow(Job job)
        {
            Recording rec;
            lock (_lock) rec = _recs.TryGetValue(job.Path, out var e) ? e.Rec : null;
            if (rec == null) return;
            BakeOptions opt;
            lock (rec.Sync) opt = _ctx.BakeOptionsFor(rec);
            opt.Fps = job.Fps;
            opt.ApplyPlacement =
                false; // a take's own placement (Clip tab) isn't used in scenes: strips are placed by the scene
            double a = job.Range != null ? MathX.Clamp(job.Range[0], 0, rec.Duration) : 0,
                b = job.Range != null ? MathX.Clamp(job.Range[1], a, rec.Duration) : rec.Duration;
            opt.SourceRange = new[] { a, b };
            if (job.Edit != null)
            {
                opt.DeviceEdit = job.Edit.Apply;
                opt.DisabledRoles = job.Edit.Disabled;
            }

            BakedClip clip = null;
            string err = null;
            var sw = Stopwatch.StartNew();
            try
            {
                lock (SolveLock)
                lock (rec.Sync)
                    clip = PoseBuilder.Bake(rec, job.Target, opt, f => Progress = f);
            }
            catch (Exception ex)
            {
                err = ex.Message;
                AppLog.Error("Scene take solve: " + ex);
            }

            LastSolveMs = sw.ElapsedMilliseconds;
            lock (_lock)
            {
                if (clip == null)
                {
                    _solveErrors[job.Key] = err ?? "solve failed";
                    return;
                }

                var rig = PoseRig.For(clip.Profile);
                var local = new Quat[clip.Frames][];
                for (int f = 0; f < clip.Frames; f++)
                {
                    local[f] = new Quat[HumanBones.Count];
                    rig.ToLocal(clip.Rot[f], local[f]);
                }

                // the slot's previous solve is replaced
                if (_bySlot.TryGetValue(job.Slot, out var old) && old.Key != job.Key) _solves.Remove(old.Key);
                var ts = new TakeSolve
                {
                    Path = job.Path, Key = job.Key, Slot = job.Slot, Rec = rec, Clip = clip, Rig = rig, Local = local,
                    Fps = job.Fps, Start = a, Edit = job.Edit, Version = Interlocked.Increment(ref _version)
                };
                _solves[job.Key] = ts;
                _bySlot[job.Slot] = ts;
            }
        }

        /// <summary>The latest solve made for a slot (any edit), or null.</summary>
        public TakeSolve Peek(string slot)
        {
            lock (_lock) return _bySlot.TryGetValue(slot, out var s) ? s : null;
        }

        /// <summary>Forgets slots no strip uses any more (keeps the base solves).</summary>
        public void Forget(Func<string, bool> keepSlot)
        {
            lock (_lock)
                foreach (var kv in _bySlot.Where(kv => !kv.Key.EndsWith("|base") && !keepSlot(kv.Key)).ToList())
                {
                    _bySlot.Remove(kv.Key);
                    _solves.Remove(kv.Value.Key);
                }
        }

        sealed class RecEntry
        {
            public string Error;
            public Recording Rec;
            public DateTime Time;
        }

        sealed class Job
        {
            public InputEdit Edit;
            public float Fps;
            public string Key, Slot;
            public string Path;
            public double[] Range;
            public AvatarProfile Target;
        }
    }

    /// <summary>
    ///     A strip's poses at every scene frame it covers (its transition handles included): what the layer blend reads.
    ///     Strip modifiers change these in place. For an Add strip the values are deltas.
    /// </summary>
    public sealed class PoseRun
    {
        public readonly Dictionary<string, float[]> Face = new();

        /// <summary>
        ///     Which bones / the root position this strip drives (all existing bones for a take; keyed channels for an
        ///     action).
        /// </summary>
        public readonly bool[] HasBone = new bool[HumanBones.Count];

        public readonly Dictionary<string, ParamType> ParamTypes = new();
        public readonly Dictionary<string, float[]> Params = new();
        public readonly Dictionary<string, bool[]> TrackerValid = new();
        public readonly Dictionary<string, Pose[]> Trackers = new();

        /// <summary>Scene frame index (from the scene start) of element 0, and how many frames.</summary>
        public int First, Count;

        public bool HasRoot;
        public Vec3[] Hips;

        /// <summary>Values are deltas (Add strips).</summary>
        public bool IsDelta;

        public string Key;
        public Quat[][] Local;

        /// <summary>Body parts this run drives (a pose strip's Mask; an input strip's face / fingers / params).</summary>
        public BodyPart Mask = BodyPart.All;

        /// <summary>The root transform applied to the strip (manual offset, then transition alignment).</summary>
        public Pose Offset = Pose.Identity;

        public Strip Strip;

        /// <summary>Each device's influence on the solve (input tracks), or null.</summary>
        public Dictionary<string, float[]> TrackerInfluence;

        public int Last => First + Count - 1;

        public bool Covers(int f)
        {
            return f >= First && f < First + Count;
        }
    }

    // ---------------------------------------------------------------- modifiers (extension points)

    public sealed class StripModifierContext
    {
        public Actor Actor;
        public PoseRig Rig;
        public float Scale, Fps;
        public Scene Scene;
        public Strip Strip;

        /// <summary>The solved take (null for an action strip).</summary>
        public TakeSolve Take;

        /// <summary>Scene time of run element k.</summary>
        public Func<int, double> TimeOf;

        public NlaTrack Track;
    }

    /// <summary>Changes a strip's frames (whole run at once, so temporal filters work): foot lock, smoothing, gap repair...</summary>
    public interface IStripModifier
    {
        string Type { get; }
        string Label { get; }
        void Apply(StripModifierContext c, StripModifier m, PoseRun run);
    }

    public sealed class ActorModifierContext
    {
        public Actor Actor;
        public PoseRig Rig;

        /// <summary>The runs that made each output frame (for modifiers that need to know where the data came from).</summary>
        public List<PoseRun> Runs;

        public float Scale, Fps;
        public Scene Scene;
    }

    /// <summary>
    ///     An input strip's devices over its frames (scene time), before the solve: what an IDeviceModifier changes in place.
    ///     Space = the solve space (BakedClip.Trackers space). Only the roles in the strip's Inputs are present.
    /// </summary>
    public sealed class DeviceFrames
    {
        public readonly Dictionary<BodyRole, Pose[]> Pose = new();
        public readonly Dictionary<BodyRole, bool[]> Valid = new();
        public float Fps, Scale;
        public double[] Time;
    }

    /// <summary>Cleans / changes devices on an input strip before the re-solve (smooth, hold, fill a tracker...).</summary>
    public interface IDeviceModifier
    {
        string Type { get; }
        string Label { get; }
        void Apply(StripModifierContext c, StripModifier m, DeviceFrames d);
    }

    /// <summary>Changes an actor's evaluated output (after all layers), e.g. a final foot lock.</summary>
    public interface IActorModifier
    {
        string Type { get; }
        string Label { get; }
        void Apply(ActorModifierContext c, StripModifier m, BakedClip clip);
    }

    /// <summary>Modifier types, found by reflection (any class implementing the interfaces with a parameterless constructor).</summary>
    public static class SceneModifiers
    {
        static Dictionary<string, IStripModifier> _strip;
        static Dictionary<string, IActorModifier> _actor;
        static Dictionary<string, IDeviceModifier> _device;

        public static IReadOnlyDictionary<string, IStripModifier> Strip
        {
            get
            {
                Init();
                return _strip;
            }
        }

        public static IReadOnlyDictionary<string, IActorModifier> Actor
        {
            get
            {
                Init();
                return _actor;
            }
        }

        public static IReadOnlyDictionary<string, IDeviceModifier> Device
        {
            get
            {
                Init();
                return _device;
            }
        }

        static void Init()
        {
            if (_strip != null) return;
            var s = new Dictionary<string, IStripModifier>();
            var a = new Dictionary<string, IActorModifier>();
            var dv = new Dictionary<string, IDeviceModifier>();
            foreach (var t in typeof(SceneModifiers).Assembly.GetTypes())
            {
                if (t.IsAbstract || t.IsInterface || t.GetConstructor(Type.EmptyTypes) == null) continue;
                try
                {
                    if (typeof(IStripModifier).IsAssignableFrom(t))
                    {
                        var m = (IStripModifier)Activator.CreateInstance(t);
                        s[m.Type] = m;
                    }

                    if (typeof(IActorModifier).IsAssignableFrom(t))
                    {
                        var m = (IActorModifier)Activator.CreateInstance(t);
                        a[m.Type] = m;
                    }

                    if (typeof(IDeviceModifier).IsAssignableFrom(t))
                    {
                        var m = (IDeviceModifier)Activator.CreateInstance(t);
                        dv[m.Type] = m;
                    }
                }
                catch (Exception ex) { AppLog.Warn("Modifier " + t.Name + ": " + ex.Message); }
            }

            _strip = s;
            _actor = a;
            _device = dv;
        }
    }

    // ---------------------------------------------------------------- the evaluator

    /// <summary>An evaluated scene: one clip per actor on the scene timeline (frame i = scene time Start + i / Fps).</summary>
    public sealed class SceneBake
    {
        public readonly List<(Actor Actor, BakedClip Clip)> Actors = new();
        public readonly List<string> Warnings = new();
        public float Fps;
        public int Frames;

        /// <summary>Takes are still loading / solving: the result is incomplete and will be redone.</summary>
        public bool Pending;

        public Scene Scene;
        public double Start;

        public double TimeOf(int f)
        {
            return Start + f / (double)Fps;
        }

        public int FrameAt(double t)
        {
            return MathX.Clamp((int)Math.Round((t - Start) * Fps), 0, Math.Max(0, Frames - 1));
        }

        public BakedClip ClipOf(string actorId)
        {
            return Actors.FirstOrDefault(a => a.Actor.Id == actorId).Clip;
        }
    }

    public sealed partial class SceneEvaluator : IDisposable
    {
        public readonly SceneContext Ctx;
        public readonly TakeCache Takes;

        /// <summary>Per actor: the solve each take strip used in the last plan (strip id -> solve).</summary>
        readonly Dictionary<string, Dictionary<string, TakeSolve>> _lastSolves = new();

        readonly Dictionary<string, PoseRun> _runs = new();
        readonly HashSet<string> _usedRuns = new();

        public SceneEvaluator(SceneContext ctx)
        {
            Ctx = ctx;
            Takes = new TakeCache(ctx);
        }

        public void Dispose()
        {
            Takes.Dispose();
        }

        // ---------------------------------------------------------------- actor setup

        /// <summary>The skeleton an actor is solved onto: its library avatar, else the avatar of its first take.</summary>
        public AvatarProfile ActorProfile(Scene s, Actor a, out bool pending)
        {
            pending = false;
            if (!string.IsNullOrEmpty(a.Avatar))
            {
                var p = Ctx.AvatarById(a.Avatar);
                if (p != null) return p;
            }

            foreach (var t in a.Tracks)
            foreach (var st in t.Strips.Where(x => x.Kind == StripKind.Take).OrderBy(x => x.Start))
            {
                var tr = s.Take(st.Source);
                if (tr == null) continue;
                var rec = Takes.Recording(tr.Path);
                if (rec == null)
                {
                    if (Takes.Error(tr.Path) == null) pending = true;
                    continue;
                }

                AvatarProfile p;
                lock (rec.Sync) p = Ctx.TakeAvatar(rec) ?? rec.Profile;
                if (p != null) return p;
            }

            return Ctx.AvatarById(a.Avatar) ?? AvatarProfile.Generic();
        }

        /// <summary>Frames over the scene range (frame i at Start + i / Fps, the last one at or before the end).</summary>
        public static int FrameCount(Scene s)
        {
            return Math.Max(2, (int)Math.Floor((s.RangeEnd - s.Start) * s.Fps + 1e-6) + 1);
        }

        /// <summary>Evaluates every actor over the scene range.</summary>
        public SceneBake Bake(Scene s)
        {
            lock (this)
            {
                _usedRuns.Clear();
                _usedSlots.Clear();
                var bake = new SceneBake { Scene = s, Fps = s.Fps, Start = s.Start };
                double end = s.RangeEnd;
                bake.Frames = FrameCount(s);
                foreach (var a in s.Actors)
                {
                    var plan = Plan(s, a, bake.Frames, bake.Warnings);
                    bake.Pending |= plan.Pending;
                    bake.Actors.Add((a, Render(s, plan, bake.Frames)));
                }

                // forget runs no strip uses any more
                foreach (string k in _runs.Keys.Where(k => !_usedRuns.Contains(k)).ToList()) _runs.Remove(k);
                Takes.Forget(slot => _usedSlots.Contains(slot));
                foreach (var tr in s.Takes)
                {
                    string e = Takes.Error(tr.Path);
                    if (e != null && s.AllStrips().Any(x => x.Strip.Source == tr.Id))
                        bake.Warnings.Add($"Take \"{tr.Name}\": {e}");
                }

                return bake;
            }
        }

        /// <summary>
        ///     The actor's pose at scene frame f from its tracks below <paramref name="trackLimit" /> (all tracks by default):
        ///     for tools that edit a layer on top of what is under it. Returns false while takes are still solving.
        /// </summary>
        public bool EvaluateFrame(Scene s, Actor a, int f, ScenePose pose, int trackLimit = int.MaxValue)
        {
            lock (this)
            {
                int frames = FrameCount(s);
                var plan = Plan(s, a, frames, new List<string>());
                var acc = new Acc(plan.Rig, plan.Scale);
                acc.Rest();
                BlendFrame(plan, f, acc, trackLimit);
                pose.Hips = acc.Hips;
                Array.Copy(acc.Local, pose.Local, HumanBones.Count);
                pose.Face.Clear();
                foreach (var kv in acc.Face) pose.Face[kv.Key] = kv.Value;
                pose.Params.Clear();
                foreach (var kv in acc.Params) pose.Params[kv.Key] = kv.Value;
                return !plan.Pending;
            }
        }

        /// <summary>The rig and scale an actor is evaluated with (for tools that need FK).</summary>
        public (PoseRig Rig, float Scale) RigOf(Scene s, Actor a)
        {
            lock (this)
            {
                var plan = Plan(s, a, 2, new List<string>());
                return (plan.Rig, plan.Scale);
            }
        }

        ActorPlan Plan(Scene s, Actor a, int frames, List<string> warnings)
        {
            var plan = new ActorPlan { Actor = a, Start = s.Start, Fps = s.Fps };
            plan.Profile = ActorProfile(s, a, out bool pend);
            plan.Pending = pend;
            plan.Rig = PoseRig.For(plan.Profile);
            plan.Scale = plan.Profile.Scale;
            // a solve per take strip: the take as recorded, or re-solved with the input tracks above it (edited devices,
            // influences); a take on an input track: its own solve (its devices / face / fingers are the edit)
            var solves = new Dictionary<string, TakeSolve>();
            for (int ti = 0; ti < a.Tracks.Count; ti++)
            {
                var t = a.Tracks[ti];
                foreach (var st in t.Strips.Where(x => x.Kind == StripKind.Take && !x.Mute))
                {
                    var tr = s.Take(st.Source);
                    if (tr == null)
                    {
                        warnings.Add($"Strip \"{st.Name}\": its take is not in the scene");
                        continue;
                    }

                    bool pendSolve;
                    var sv = t.Kind == TrackKind.Pose
                        ? SolveStrip(s, a, ti, st, tr, plan, warnings, out pendSolve)
                        : Takes.Solve(tr.Path, plan.Profile, s.Fps, null, null, null, out pendSolve);
                    if (pendSolve) plan.Pending = true;
                    if (sv != null) solves[st.Id] = sv;
                }

                // keyframe-only takes animated through devices (IK solve without a recording)
                if (t.Kind == TrackKind.Pose)
                    foreach (var st in t.Strips.Where(x => x.Kind == StripKind.Action && !x.Mute))
                    {
                        var act = s.Action(st.Source);
                        if (!IsDeviceAction(act)) continue;
                        var sv = SolveDeviceAction(s, a, ti, st, act, plan, out bool pendDev);
                        if (pendDev) plan.Pending = true;
                        if (sv != null) solves[st.Id] = sv;
                    }
            }

            _lastSolves[a.Id] = solves;
            var first = a.Tracks.Where(t => t.Kind == TrackKind.Pose).SelectMany(t => t.Strips)
                .Where(x => x.Kind == StripKind.Take && solves.ContainsKey(x.Id)).OrderBy(x => x.Start)
                .FirstOrDefault();
            if (first != null)
            {
                plan.Scale = solves[first.Id].Clip.Scale;
                plan.Calibration = solves[first.Id].Clip.Calibration;
            }

            bool anySolo = a.Tracks.Any(t => t.Solo);
            foreach (var t in a.Tracks)
            {
                var runs = new List<PoseRun>();
                if (!t.Mute && (!anySolo || t.Solo))
                {
                    if (t.Kind == TrackKind.Pose)
                    {
                        var offsets = ResolveOffsets(s, a, t, plan, solves);
                        foreach (var st in t.Strips)
                        {
                            if (st.Mute) continue;
                            solves.TryGetValue(st.Id, out var sv);
                            if (st.Kind == StripKind.Take && sv == null) continue;
                            if (st.Kind == StripKind.Action && s.Action(st.Source) == null)
                            {
                                warnings.Add($"Strip \"{st.Name}\": its action is missing");
                                continue;
                            }

                            if (st.Kind == StripKind.Action && sv == null && IsDeviceAction(s.Action(st.Source)))
                                continue; // device take still solving
                            var run = GetRun(s, a, t, st, sv, offsets.TryGetValue(st.Id, out var off) ? off : st.Offset,
                                plan, frames, st.Mask);
                            if (run != null) runs.Add(run);
                        }
                    }
                    else
                    {
                        // input track: devices went into the solves below; its face / finger / parameter inputs layer here
                        foreach (var st in t.Strips)
                        {
                            if (st.Mute || st.Kind != StripKind.Take || !solves.TryGetValue(st.Id, out var sv))
                                continue;
                            var mask = InputPoseMask(st.Inputs);
                            if (mask == BodyPart.None) continue;
                            var run = GetRun(s, a, t, st, sv, Pose.Identity, plan, frames, mask);
                            if (run != null) runs.Add(run);
                        }
                    }
                }

                plan.Tracks.Add((t, runs));
            }

            return plan;
        }

        // ---------------------------------------------------------------- root offsets, transition alignment

        /// <summary>
        ///     Effective root transform per strip: the strip's own offset, and for a strip entered through an aligning
        ///     transition, the alignment onto the strip before it (chained in time order) on top.
        /// </summary>
        Dictionary<string, Pose> ResolveOffsets(Scene s, Actor a, NlaTrack t, ActorPlan plan,
            Dictionary<string, TakeSolve> solves)
        {
            var res = new Dictionary<string, Pose>();
            var pa = new ScenePose();
            var pb = new ScenePose();
            foreach (var st in t.Strips.OrderBy(x => x.Start))
            {
                var off = st.Offset;
                var tr = t.TransitionInto(st.Id);
                var from = tr != null ? t.Strips.FirstOrDefault(x => x.Id == tr.From) : null;
                if (tr != null && from != null && tr.Align != TransitionAlign.None && st.Blend == BlendMode.Replace &&
                    (st.Mask & BodyPart.Root) != 0)
                {
                    var fromOff = res.TryGetValue(from.Id, out var fo) ? fo : from.Offset;
                    var toOff = off;
                    if (SamplePose(s, from, plan, solves, st.Start, pa, fromOff) &&
                        SamplePose(s, st, plan, solves, st.Start, pb, toOff))
                    {
                        // both strips around the cut are passed in, so the alignment can also match the centre of mass's travel
                        var align = Transitions.AlignTransition(tr, plan.Rig, plan.Scale, pa, pb,
                            (t2, o) => SamplePose(s, from, plan, solves, t2, o, fromOff),
                            (t2, o) => SamplePose(s, st, plan, solves, t2, o, toOff), st.Start);
                        off = align * off;
                    }
                }

                res[st.Id] = off;
            }

            return res;
        }

        /// <summary>
        ///     A strip's pose at scene time t (not clamped to the strip), with a root transform. False if its data isn't
        ///     ready.
        /// </summary>
        public bool SamplePose(Scene s, Strip st, PoseRig rig, float scale, double t, ScenePose o, Pose offset)
        {
            lock (this)
            {
                var plan = new ActorPlan { Rig = rig, Scale = scale };
                return SamplePose(s, st, plan, null, t, o, offset);
            }
        }

        bool SamplePose(Scene s, Strip st, ActorPlan plan, Dictionary<string, TakeSolve> solves, double t, ScenePose o,
            Pose offset)
        {
            double src = st.ToSource(t);
            TakeSolve sv = null;
            if (solves != null) solves.TryGetValue(st.Id, out sv);
            else
                foreach (var d in _lastSolves.Values)
                    if (d.TryGetValue(st.Id, out sv))
                        break; // the strip's own (possibly edited) solve
            if (sv == null && st.Kind == StripKind.Take && solves == null)
            {
                var tr = s.Take(st.Source);
                if (tr != null) sv = Takes.Solve(tr.Path, plan.Rig.Profile, s.Fps);
            }

            if (sv != null) sv.Sample(src, o, false);
            else if (st.Kind == StripKind.Take) return false;
            else
            {
                var act = s.Action(st.Source);
                if (act == null) return false;
                o.SetRest(plan.Rig, plan.Scale);
                SampleAction(act, src, o, null, out _);
            }

            o.Transform(offset);
            return true;
        }

        /// <summary>Evaluates an action's channels into a pose (has: which bones were keyed).</summary>
        static void SampleAction(KeyAction act, double src, ScenePose o, bool[] has, out bool hasRoot)
        {
            hasRoot = false;
            float[] buf = new float[4];
            foreach (var ch in act.Channels.Values)
            {
                if (ch.Keys.Count == 0) continue;
                ch.Evaluate(src, buf);
                int b = Channel.BoneOf(ch.Path);
                if (b >= 0 && ch.Dim == 4)
                {
                    o.Local[b] = new Quat(buf[0], buf[1], buf[2], buf[3]).Normalized;
                    if (has != null) has[b] = true;
                }
                else if (ch.Path == Channel.RootPosition && ch.Dim == 3)
                {
                    o.Hips = new Vec3(buf[0], buf[1], buf[2]);
                    hasRoot = true;
                }
                else if (ch.Path.StartsWith("face/", StringComparison.Ordinal)) o.Face[ch.Path.Substring(5)] = buf[0];
                else if (ch.Path.StartsWith("param/", StringComparison.Ordinal))
                    o.Params[ch.Path.Substring(6)] = buf[0];
            }
        }

        // ---------------------------------------------------------------- runs

        PoseRun GetRun(Scene s, Actor a, NlaTrack t, Strip st, TakeSolve sv, Pose offset, ActorPlan plan, int frames,
            BodyPart mask)
        {
            var trIn = t.TransitionInto(st.Id);
            var trOut = t.TransitionOutOf(st.Id);
            double hIn = trIn != null ? trIn.Duration / 2 : 0, hOut = trOut != null ? trOut.Duration / 2 : 0;
            double ta = st.Start - hIn, tb = st.End + hOut;
            int fa = Math.Max(0, (int)Math.Ceiling((ta - s.Start) * s.Fps - 1e-6)),
                fb = Math.Min(frames - 1, (int)Math.Floor((tb - s.Start) * s.Fps + 1e-6));
            if (fb < fa) return null;
            var act = st.Kind == StripKind.Action ? s.Action(st.Source) : null;
            string key = string.Join("|", Json.Serialize(st.ToJson()), sv?.Version ?? 0,
                act != null ? Json.Serialize(act.ToJson()).GetHashCode() : 0,
                offset.pos.x, offset.pos.y, offset.pos.z, offset.rot.x, offset.rot.y, offset.rot.z, offset.rot.w, hIn,
                hOut, s.Fps, s.Start, fa, fb, TakeCacheKey(plan), (int)mask);
            _usedRuns.Add(key);
            if (_runs.TryGetValue(key, out var cached)) return cached;
            var run = BuildRun(s, a, t, st, sv, act, offset, plan, fa, fb);
            run.Key = key;
            run.Mask = mask;
            _runs[key] = run;
            return run;
        }

        static string TakeCacheKey(ActorPlan p)
        {
            return p.Profile.Name + "|" + p.Profile.Nodes.Count + "|" + p.Scale;
        }

        PoseRun BuildRun(Scene s, Actor a, NlaTrack t, Strip st, TakeSolve sv, KeyAction act, Pose offset,
            ActorPlan plan, int fa, int fb)
        {
            int n = fb - fa + 1;
            var run = new PoseRun
            {
                Strip = st, First = fa, Count = n, Hips = new Vec3[n], Local = new Quat[n][], Offset = offset,
                IsDelta = st.Blend == BlendMode.Add
            };
            var p = new ScenePose();
            ScenePose refPose = null;
            bool solved = sv != null; // a take, or a keyframe-only take solved from its devices
            if (solved)
            {
                for (int b = 0; b < HumanBones.Count; b++) run.HasBone[b] = plan.Rig.Exists[b];
                run.HasRoot = true;
                foreach (var kv in sv.Clip.Face) run.Face[kv.Key] = new float[n];
                foreach (var kv in sv.Clip.ParamValues)
                {
                    run.Params[kv.Key] = new float[n];
                    run.ParamTypes[kv.Key] = sv.Clip.ParamTypes.TryGetValue(kv.Key, out var ty) ? ty : ParamType.Float;
                }

                foreach (var kv in sv.Clip.Trackers)
                {
                    run.Trackers[kv.Key] = new Pose[n];
                    run.TrackerValid[kv.Key] = new bool[n];
                }

                if (sv.Clip.TrackerInfluence != null)
                {
                    run.TrackerInfluence = new Dictionary<string, float[]>();
                    foreach (var kv in sv.Clip.TrackerInfluence) run.TrackerInfluence[kv.Key] = new float[n];
                }

                if (run.IsDelta)
                {
                    refPose = new ScenePose();
                    sv.Sample(st.SrcIn, refPose);
                }
            }

            for (int k = 0; k < n; k++)
            {
                double time = s.Start + (fa + k) / (double)s.Fps;
                double src = st.ToSource(time);
                run.Local[k] = new Quat[HumanBones.Count];
                if (solved)
                {
                    sv.Sample(src, p);
                    if (run.IsDelta)
                    {
                        // an additive take layer: its motion relative to the strip's first frame
                        run.Hips[k] = p.Hips - refPose.Hips;
                        for (int b = 0; b < HumanBones.Count; b++)
                            run.Local[k][b] = (refPose.Local[b].Conjugate * p.Local[b]).Normalized;
                        foreach (var kv in p.Face)
                            if (run.Face.TryGetValue(kv.Key, out float[] arr))
                                arr[k] = kv.Value - (refPose.Face.TryGetValue(kv.Key, out float r0) ? r0 : 0);
                    }
                    else
                    {
                        p.Transform(offset);
                        run.Hips[k] = p.Hips;
                        Array.Copy(p.Local, run.Local[k], HumanBones.Count);
                        foreach (var kv in p.Face)
                            if (run.Face.TryGetValue(kv.Key, out float[] arr))
                                arr[k] = kv.Value;
                    }

                    foreach (var kv in p.Params)
                        if (run.Params.TryGetValue(kv.Key, out float[] arr))
                            arr[k] = kv.Value;
                    int sf = sv.FrameOf(src);
                    foreach (var kv in sv.Clip.Trackers)
                    {
                        run.Trackers[kv.Key][k] = offset * kv.Value[sf];
                        run.TrackerValid[kv.Key][k] = sv.Clip.TrackerValid.TryGetValue(kv.Key, out bool[] v) && v[sf];
                    }

                    if (run.TrackerInfluence != null)
                        foreach (var kv in sv.Clip.TrackerInfluence)
                            run.TrackerInfluence[kv.Key][k] = kv.Value[sf];
                }
                else
                {
                    if (run.IsDelta)
                    {
                        p.Hips = Vec3.Zero;
                        for (int b = 0; b < HumanBones.Count; b++) p.Local[b] = Quat.Identity;
                        p.Face.Clear();
                        p.Params.Clear();
                    }
                    else p.SetRest(plan.Rig, plan.Scale);

                    SampleAction(act, src, p, run.HasBone, out bool hasRoot);
                    run.HasRoot |= hasRoot;
                    if (!run.IsDelta && (hasRoot || run.HasBone[HumanBones.Hips])) p.Transform(offset);
                    run.Hips[k] = p.Hips;
                    Array.Copy(p.Local, run.Local[k], HumanBones.Count);
                    foreach (var kv in p.Face)
                    {
                        if (!run.Face.TryGetValue(kv.Key, out float[] arr)) run.Face[kv.Key] = arr = new float[n];
                        arr[k] = kv.Value;
                    }

                    foreach (var kv in p.Params)
                    {
                        if (!run.Params.TryGetValue(kv.Key, out float[] arr))
                        {
                            run.Params[kv.Key] = arr = new float[n];
                            run.ParamTypes[kv.Key] = ParamType.Float;
                        }

                        arr[k] = kv.Value;
                    }
                }
            }

            // strip modifiers (foot lock, smoothing, ...) on the whole run
            if (st.Modifiers.Count > 0)
            {
                var ctx = new StripModifierContext
                {
                    Scene = s, Actor = a, Track = t, Strip = st, Take = sv, Rig = plan.Rig, Scale = plan.Scale,
                    Fps = s.Fps, TimeOf = k => s.Start + (fa + k) / (double)s.Fps
                };
                foreach (var m in st.Modifiers)
                {
                    if (!m.Enabled) continue;
                    if (SceneModifiers.Strip.TryGetValue(m.Type, out var mod))
                        try { mod.Apply(ctx, m, run); }
                        catch (Exception ex) { AppLog.Error($"Modifier {m.Type}: {ex}"); }
                }
            }

            return run;
        }

        /// <summary>Weight of a strip at frame time: influence, blend in/out ramps, crossfade through its transitions.</summary>
        static float StripWeight(NlaTrack t, Strip st, double time, out float crossfade)
        {
            crossfade = 1;
            float w = st.Influence;
            var trIn = t.TransitionInto(st.Id);
            var trOut = t.TransitionOutOf(st.Id);
            if (trIn != null && trIn.Duration > 1e-6 && time < st.Start + trIn.Duration / 2)
                crossfade = trIn.Weight((time - (st.Start - trIn.Duration / 2)) / trIn.Duration);
            else if (trIn == null && st.BlendIn > 1e-6 && time < st.Start + st.BlendIn)
                w *= MathX.SmoothStep((float)((time - st.Start) / st.BlendIn));
            if (trOut != null && trOut.Duration > 1e-6 && time > st.End - trOut.Duration / 2)
                crossfade *= 1 - trOut.Weight((time - (st.End - trOut.Duration / 2)) / trOut.Duration);
            else if (trOut == null && st.BlendOut > 1e-6 && time > st.End - st.BlendOut)
                w *= MathX.SmoothStep((float)((st.End - time) / st.BlendOut));
            return w * crossfade;
        }

        void BlendFrame(ActorPlan plan, int f, Acc acc, int trackLimit)
        {
            double time = plan.Start + f / (double)plan.Fps;
            for (int ti = 0; ti < plan.Tracks.Count && ti < trackLimit; ti++)
            {
                var (track, runs) = plan.Tracks[ti];
                PoseRun a = null, b = null;
                float wa = 0, wb = 0;
                foreach (var r in runs)
                {
                    if (!r.Covers(f)) continue;
                    float w = StripWeight(track, r.Strip, time, out _);
                    if (w <= 1e-5f) continue;
                    if (a == null)
                    {
                        a = r;
                        wa = w;
                        continue;
                    }

                    if (b == null)
                    {
                        b = r;
                        wb = w;
                        continue;
                    }

                    // more than two at once (overlapping strips without a transition): apply the first two, keep going
                    ApplyPair(acc, a, wa, b, wb, f);
                    a = r;
                    wa = w;
                    b = null;
                    wb = 0;
                }

                if (a != null) ApplyPair(acc, a, wa, b, wb, f);
            }
        }

        /// <summary>
        ///     Applies one strip, or two crossfading strips of one track (A going out, B coming in), onto the pose.
        ///     A bone driven by both: slerp(A, B) at B's share, full weight; driven by one: that one at its own weight.
        /// </summary>
        void ApplyPair(Acc acc, PoseRun A, float wa, PoseRun B, float wb, int f)
        {
            if (B != null && A.IsDelta != B.IsDelta)
            {
                ApplyPair(acc, A, wa, null, 0, f);
                ApplyPair(acc, B, wb, null, 0, f);
                return;
            }

            int ka = f - A.First, kb = B != null ? f - B.First : -1;
            float x = B == null ? 0 : wb / Math.Max(1e-6f, wa + wb); // B's share of the crossfade
            bool delta = A.IsDelta;
            var la = A.Local[ka];
            var lb = B?.Local[kb];
            for (int bone = 0; bone < HumanBones.Count; bone++)
            {
                if (!acc.Rig.Exists[bone]) continue;
                var part = BodyMask.PartOf(bone);
                bool inA = A.HasBone[bone] && (A.Mask & part) != 0,
                    inB = B != null && B.HasBone[bone] && (B.Mask & part) != 0;
                if (!inA && !inB) continue;
                Quat v;
                float w;
                if (inA && inB)
                {
                    v = Quat.Slerp(la[bone], lb[bone], x);
                    w = Math.Min(1, wa + wb);
                }
                else if (inA)
                {
                    v = la[bone];
                    w = wa;
                }
                else
                {
                    v = lb[bone];
                    w = wb;
                }

                if (delta) acc.Local[bone] = (acc.Local[bone] * Quat.Slerp(Quat.Identity, v, w)).Normalized;
                else acc.Local[bone] = w >= 0.9999f ? v : Quat.Slerp(acc.Local[bone], v, w);
            }

            // root position
            {
                bool inA = A.HasRoot && (A.Mask & BodyPart.Root) != 0,
                    inB = B != null && B.HasRoot && (B.Mask & BodyPart.Root) != 0;
                if (inA || inB)
                {
                    Vec3 v;
                    float w;
                    if (inA && inB)
                    {
                        v = Vec3.Lerp(A.Hips[ka], B.Hips[kb], x);
                        w = Math.Min(1, wa + wb);
                    }
                    else if (inA)
                    {
                        v = A.Hips[ka];
                        w = wa;
                    }
                    else
                    {
                        v = B.Hips[kb];
                        w = wb;
                    }

                    if (delta) acc.Hips += v * w;
                    else acc.Hips = Vec3.Lerp(acc.Hips, v, w);
                    if (!delta)
                    {
                        var main = inA && (!inB || x < 0.5f) ? A : B;
                        if (w >= acc.RootW)
                        {
                            acc.RootRun = main;
                            acc.RootW = w;
                        }
                    }
                }
            }
            BlendDict(acc.Face, A, A.Face, ka, wa, B, B?.Face, kb, wb, x, BodyPart.Face, delta, null);
            BlendDict(acc.Params, A, A.Params, ka, wa, B, B?.Params, kb, wb, x, BodyPart.Params, delta, A.ParamTypes);
        }

        static void BlendDict(Dictionary<string, float> acc, PoseRun A, Dictionary<string, float[]> da, int ka,
            float wa,
            PoseRun B, Dictionary<string, float[]> db, int kb, float wb, float x, BodyPart part, bool delta,
            Dictionary<string, ParamType> types)
        {
            bool inA = (A.Mask & part) != 0, inB = B != null && (B.Mask & part) != 0;
            if (inA)
                foreach (var kv in da)
                {
                    float v = kv.Value[ka], w = wa;
                    if (inB && db.TryGetValue(kv.Key, out float[] bv))
                    {
                        v = v + (bv[kb] - v) * x;
                        w = Math.Min(1, wa + wb);
                    }

                    Put(acc, kv.Key, v, w, delta, types);
                }

            if (inB)
                foreach (var kv in db)
                {
                    if (inA && da.ContainsKey(kv.Key)) continue;
                    Put(acc, kv.Key, kv.Value[kb], wb, delta, types);
                }
        }

        static void Put(Dictionary<string, float> acc, string k, float v, float w, bool delta,
            Dictionary<string, ParamType> types)
        {
            acc.TryGetValue(k, out float cur);
            if (delta)
            {
                acc[k] = cur + v * w;
                return;
            }

            bool step = types != null && types.TryGetValue(k, out var ty) && ty != ParamType.Float;
            acc[k] = step ? w >= 0.5f ? v : cur : cur + (v - cur) * w;
        }

        // ---------------------------------------------------------------- output

        BakedClip Render(Scene s, ActorPlan plan, int frames)
        {
            var rig = plan.Rig;
            var clip = new BakedClip
            {
                Profile = plan.Profile, Fps = s.Fps, Frames = frames, SourceTime = new double[frames],
                Hips = new Vec3[frames], Rot = new Quat[frames][],
                BoneExists = rig.Exists, BodySource = "scene", Scale = plan.Scale, Calibration = plan.Calibration
            };
            // every face shape / parameter / tracker any run of this actor has
            var runs = plan.Tracks.SelectMany(x => x.Runs).ToList();
            foreach (var r in runs)
            {
                foreach (string k in r.Face.Keys)
                    if (!clip.Face.ContainsKey(k))
                        clip.Face[k] = new float[frames];
                foreach (string k in r.Params.Keys)
                    if (!clip.ParamValues.ContainsKey(k))
                    {
                        clip.ParamValues[k] = new float[frames];
                        clip.ParamTypes[k] = r.ParamTypes.TryGetValue(k, out var ty) ? ty : ParamType.Float;
                    }

                foreach (string k in r.Trackers.Keys)
                    if (!clip.Trackers.ContainsKey(k))
                    {
                        clip.Trackers[k] = new Pose[frames];
                        clip.TrackerValid[k] = new bool[frames];
                    }
            }

            var acc = new Acc(rig, plan.Scale);
            for (int f = 0; f < frames; f++)
            {
                clip.SourceTime[f] = s.Start + f / (double)s.Fps;
                acc.Rest();
                BlendFrame(plan, f, acc, int.MaxValue);
                clip.Hips[f] = acc.Hips;
                var w = new Quat[HumanBones.Count];
                rig.ToWorld(acc.Local, w);
                clip.Rot[f] = w;
                foreach (var kv in acc.Face) clip.Face[kv.Key][f] = kv.Value;
                foreach (var kv in acc.Params) clip.ParamValues[kv.Key][f] = kv.Value;
                var rr = acc.RootRun;
                if (rr != null)
                {
                    foreach (var kv in rr.Trackers)
                    {
                        int k = f - rr.First;
                        clip.Trackers[kv.Key][f] = kv.Value[k];
                        clip.TrackerValid[kv.Key][f] = rr.TrackerValid[kv.Key][k];
                    }

                    if (rr.TrackerInfluence != null)
                    {
                        if (clip.TrackerInfluence == null) clip.TrackerInfluence = new Dictionary<string, float[]>();
                        foreach (var kv in rr.TrackerInfluence)
                        {
                            if (!clip.TrackerInfluence.TryGetValue(kv.Key, out float[] arr))
                            {
                                clip.TrackerInfluence[kv.Key] = arr = new float[frames];
                                for (int i = 0; i < frames; i++) arr[i] = 1;
                            }

                            arr[f] = kv.Value[f - rr.First];
                        }
                    }
                }
            }

            // actor modifiers (after all layers)
            if (plan.Actor.Modifiers.Count > 0)
            {
                var ctx = new ActorModifierContext
                    { Scene = s, Actor = plan.Actor, Rig = rig, Scale = plan.Scale, Fps = s.Fps, Runs = runs };
                foreach (var m in plan.Actor.Modifiers)
                    if (m.Enabled && SceneModifiers.Actor.TryGetValue(m.Type, out var mod))
                        try { mod.Apply(ctx, m, clip); }
                        catch (Exception ex) { AppLog.Error($"Modifier {m.Type}: {ex}"); }
            }

            return clip;
        }

        sealed class ActorPlan
        {
            public readonly List<(NlaTrack Track, List<PoseRun> Runs)> Tracks = new();
            public Actor Actor;
            public CalibrationResult Calibration;
            public float Fps = 60;
            public bool Pending;
            public AvatarProfile Profile;
            public PoseRig Rig;
            public float Scale = 1;
            public double Start;
        }

        // ---------------------------------------------------------------- blending

        /// <summary>The pose being built for one frame.</summary>
        sealed class Acc
        {
            public readonly Dictionary<string, float> Face = new();
            public readonly Quat[] Local = new Quat[HumanBones.Count];
            public readonly Dictionary<string, float> Params = new();
            public readonly PoseRig Rig;
            public readonly float Scale;
            public Vec3 Hips;

            /// <summary>The run whose root (position) weighs most at this frame (trackers are drawn from it).</summary>
            public PoseRun RootRun;

            public float RootW;

            public Acc(PoseRig rig, float scale)
            {
                Rig = rig;
                Scale = scale;
            }

            public void Rest()
            {
                Hips = Rig.RestHips * Scale;
                for (int b = 0; b < HumanBones.Count; b++) Local[b] = Rig.Exists[b] ? Rig.RestLocal[b] : Quat.Identity;
                Face.Clear();
                Params.Clear();
                RootRun = null;
                RootW = 0;
            }
        }
    }
}
