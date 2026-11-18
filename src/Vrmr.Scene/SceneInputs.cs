using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace VRMR
{
    // =====================================================================================================  input tracks
    //
    // An input track changes what the IK solve is fed for the take strips below it, then those takes are solved again:
    //   - a take on an input track (re-recorded parts): its masked devices replace the base take's (time-mapped through
    //     both strips, placed by its headset at the strip start), crossfaded by the strip's influence / blend in / out;
    //   - an action on an input track (device keys): device position / rotation keys (Add: offset, Replace: absolute)
    //     and influence keys (0 = the device doesn't drive the IK solve, 1 = as recorded).
    // Device poses are in the solve space (the take's centred, avatar-scaled space = BakedClip.Trackers).
    // Influence is done with variant solves (the device switched off) blended by the influence curve.

    /// <summary>One input strip's contribution to a take's solve.</summary>
    public sealed class InputLayer
    {
        /// <summary>Device / influence keys, or null.</summary>
        public KeyAction Action;

        /// <summary>Where the re-recorded take's devices are put (headset match at the strip start).</summary>
        public Pose Align = Pose.Identity;

        /// <summary>This strip's output devices over its frames after its device modifiers, or null (no modifiers).</summary>
        public DeviceFrames Frames;

        public InputPart Mask;
        public Strip Strip;

        /// <summary>A re-recorded take (its devices, solved as recorded), or null.</summary>
        public TakeSolve Take;

        /// <summary>The strip's weight for one device (Strip.InputWeights on top of WeightAt).</summary>
        public float WeightFor(BodyRole r, double t)
        {
            float w = WeightAt(t);
            return w <= 0 ? 0 : w * Strip.InputWeight(r);
        }

        /// <summary>Device pose from the modifier frames at scene time t (interpolated).</summary>
        public bool FramePose(BodyRole r, double t, out Pose p)
        {
            p = Pose.Identity;
            if (Frames == null || !Frames.Pose.TryGetValue(r, out var arr)) return false;
            double[] tm = Frames.Time;
            int n = tm.Length;
            if (n == 0) return false;
            double ff = MathX.Clamp((t - tm[0]) * Frames.Fps, 0, n - 1);
            int f0 = (int)Math.Floor(ff), f1 = Math.Min(n - 1, f0 + 1);
            bool[] v = Frames.Valid[r];
            if (!v[f0] && !v[f1]) return false;
            if (!v[f0])
            {
                p = arr[f1];
                return true;
            }

            if (!v[f1])
            {
                p = arr[f0];
                return true;
            }

            p = Pose.Lerp(arr[f0], arr[f1], (float)(ff - f0));
            return true;
        }

        /// <summary>The strip's weight at scene time t: its influence with blend in / out, 0 outside the strip.</summary>
        public float WeightAt(double t)
        {
            var s = Strip;
            if (t < s.Start - 1e-9 || t > s.End + 1e-9) return 0;
            float w = s.Influence;
            if (s.BlendIn > 1e-6 && t < s.Start + s.BlendIn) w *= MathX.SmoothStep((float)((t - s.Start) / s.BlendIn));
            if (s.BlendOut > 1e-6 && t > s.End - s.BlendOut) w *= MathX.SmoothStep((float)((s.End - t) / s.BlendOut));
            return w;
        }
    }

    /// <summary>
    ///     The device edits for one take strip's solve (handed to PoseBuilder as BakeOptions.DeviceEdit / DisabledRoles).
    ///     Immutable once built: the solve runs on a worker thread.
    /// </summary>
    public sealed class InputEdit
    {
        public readonly List<InputLayer> Layers = new();

        /// <summary>Devices this solve doesn't use at all (a variant for influence), or null.</summary>
        public HashSet<BodyRole> Disabled;

        /// <summary>What makes this edit different (part of the solve cache key).</summary>
        public string Key = "";

        /// <summary>Take time of the base strip -> scene time.</summary>
        public Func<double, double> SceneTime = t => t;

        /// <summary>Devices with influence keys (each gets a variant solve).</summary>
        public List<BodyRole> InfluenceRoles()
        {
            var r = new List<BodyRole>();
            foreach (BodyRole role in Enum.GetValues(typeof(BodyRole)))
                if (Layers.Any(L =>
                        L.Action != null && (L.Mask & InputMask.PartOf(role)) != 0 &&
                        (L.Action.Get(Channel.Influence(role))?.Keys.Count ?? 0) > 0))
                    r.Add(role);
            return r;
        }

        /// <summary>The same edit with one device switched off (influence 0).</summary>
        public InputEdit Variant(BodyRole off)
        {
            var e = new InputEdit
                { Key = Key + "|off:" + off, Disabled = new HashSet<BodyRole> { off }, SceneTime = SceneTime };
            e.Layers.AddRange(Layers);
            return e;
        }

        /// <summary>Device pose as fed to the solver after every layer: (role, take time, pose, tracked) -> (pose, tracked).</summary>
        public (Pose, bool) Apply(BodyRole role, double takeTime, Pose p, bool ok)
        {
            double t = SceneTime(takeTime);
            var part = InputMask.PartOf(role);
            float[] buf = new float[4];
            foreach (var L in Layers)
            {
                if ((L.Mask & part) == 0) continue;
                float w = L.WeightFor(role, t);
                if (w <= 1e-4f) continue;
                double src = L.Strip.ToSource(t);
                if (L.Frames != null)
                {
                    // the strip's own result after its device modifiers
                    if (!L.FramePose(role, t, out var q)) continue;
                    if (!ok)
                    {
                        if (w >= 0.5f)
                        {
                            p = q;
                            ok = true;
                        }
                    }
                    else p = w >= 0.9999f ? q : new Pose(Vec3.Lerp(p.pos, q.pos, w), Quat.Slerp(p.rot, q.rot, w));
                }
                else if (L.Take != null)
                {
                    if (!L.Take.Device(role.ToString(), src, out var q)) continue;
                    q = L.Align * q;
                    if (!ok)
                    {
                        if (w >= 0.5f)
                        {
                            p = q;
                            ok = true;
                        }
                    }
                    else p = w >= 0.9999f ? q : new Pose(Vec3.Lerp(p.pos, q.pos, w), Quat.Slerp(p.rot, q.rot, w));
                }
                else if (L.Action != null)
                {
                    bool add = L.Strip.Blend == BlendMode.Add;
                    var pc = L.Action.Get(Channel.DevicePosition(role));
                    var rc = L.Action.Get(Channel.DeviceRotation(role));
                    if (add && !ok) continue; // nothing to offset
                    if (pc != null && pc.Keys.Count > 0)
                    {
                        pc.Evaluate(src, buf);
                        var v = new Vec3(buf[0], buf[1], buf[2]);
                        p.pos = add ? p.pos + v * w : Vec3.Lerp(p.pos, v, w);
                        if (!add && w >= 0.5f) ok = true;
                    }

                    if (rc != null && rc.Keys.Count > 0)
                    {
                        rc.Evaluate(src, buf);
                        var q = new Quat(buf[0], buf[1], buf[2], buf[3]).Normalized;
                        p.rot = add
                            ? (Quat.Slerp(Quat.Identity, q, w) * p.rot).Normalized
                            : Quat.Slerp(p.rot, q, w).Normalized;
                    }
                }
            }

            return (p, ok);
        }

        /// <summary>A device's influence at scene time t: 1 = as recorded, 0 = not used; layers multiply.</summary>
        public float Influence(BodyRole role, double sceneTime)
        {
            float f = 1;
            var part = InputMask.PartOf(role);
            float[] buf = new float[4];
            foreach (var L in Layers)
            {
                if (L.Action == null || (L.Mask & part) == 0) continue;
                var ch = L.Action.Get(Channel.Influence(role));
                if (ch == null || ch.Keys.Count == 0) continue;
                float w = L.WeightFor(role, sceneTime);
                if (w <= 0) continue;
                ch.Evaluate(L.Strip.ToSource(sceneTime), buf);
                f *= 1 - w * (1 - MathX.Clamp01(buf[0]));
            }

            return f;
        }
    }

    public sealed partial class SceneEvaluator
    {
        static int _combinedVersion = 1 << 29;
        readonly Dictionary<string, TakeSolve> _combined = new();
        readonly HashSet<string> _usedSlots = new();

        /// <summary>
        ///     While a device is being dragged: scene time to solve a short window around (±1.5 s) instead of the whole strip,
        ///     for a quick preview; NaN = full solves.
        /// </summary>
        public double PreviewAt = double.NaN;

        /// <summary>The pose-level part of an input strip: face, fingers, parameters (devices go into the solve).</summary>
        public static BodyPart InputPoseMask(InputPart m)
        {
            var r = BodyPart.None;
            if ((m & InputPart.Face) != 0) r |= BodyPart.Face;
            if ((m & InputPart.LeftFingers) != 0) r |= BodyPart.LeftFingers;
            if ((m & InputPart.RightFingers) != 0) r |= BodyPart.RightFingers;
            if ((m & InputPart.Params) != 0) r |= BodyPart.Params;
            return r;
        }

        /// <summary>Strips on input tracks above track ti that overlap scene time [ta, tb] (mute / solo respected).</summary>
        static List<(NlaTrack Track, Strip Strip)> InputStripsOver(Actor a, int ti, double ta, double tb)
        {
            var res = new List<(NlaTrack, Strip)>();
            bool anySolo = a.Tracks.Any(t => t.Solo);
            for (int k = ti + 1; k < a.Tracks.Count; k++)
            {
                var t = a.Tracks[k];
                if (t.Kind != TrackKind.Input || t.Mute || (anySolo && !t.Solo)) continue;
                foreach (var s in t.Strips)
                    if (!s.Mute && s.Start < tb && s.End > ta)
                        res.Add((t, s));
            }

            return res;
        }

        /// <summary>
        ///     The device edits of the input strips above track ti over the scene range [ta, tb] for base strip st (null when
        ///     there are none). waiting = a re-recorded take they need isn't solved yet. key = what makes the edit different.
        /// </summary>
        InputEdit BuildEdit(Scene s, Actor a, int ti, Strip st, TakeSolve baseSolve, ActorPlan plan, double ta,
            double tb, out bool waiting, out string key)
        {
            waiting = false;
            key = null;
            var inputs = InputStripsOver(a, ti, ta, tb);
            if (inputs.Count == 0) return null;
            var edit = new InputEdit { SceneTime = x => st.ToScene(x) };
            var sb = new StringBuilder();
            sb.Append(st.Start.ToString("R")).Append('|').Append(st.SrcIn.ToString("R")).Append('|')
                .Append(st.Speed.ToString("R"));
            foreach (var (it, istr) in inputs)
            {
                var layer = new InputLayer { Strip = istr, Mask = istr.Inputs };
                bool hasDeviceMods = istr.Modifiers.Any(m => m.Enabled && SceneModifiers.Device.ContainsKey(m.Type));
                if (istr.Kind == StripKind.Take)
                {
                    if ((istr.Inputs & InputMask.Devices) == 0) continue;
                    var itr = s.Take(istr.Source);
                    if (itr == null) continue;
                    var isv = Takes.Solve(itr.Path, plan.Profile, s.Fps, null, null, null, out bool pi);
                    if (isv == null)
                    {
                        if (pi) waiting = true;
                        continue;
                    }

                    layer.Take = isv;
                    if (istr.InputAlign == InputAlign.Head)
                    {
                        if (baseSolve == null)
                        {
                            waiting = true;
                            continue;
                        }

                        layer.Align = HeadAlign(baseSolve, st, isv, istr);
                    }

                    var al = layer.Align;
                    sb.Append("|T").Append(isv.Version).Append(':').Append(al.pos.x.ToString("0.#####")).Append(',')
                        .Append(al.pos.z.ToString("0.#####")).Append(',').Append(al.rot.Yaw.ToString("0.###"));
                }
                else
                {
                    var act = s.Action(istr.Source);
                    if (act == null) continue;
                    bool deviceKeys =
                        act.Channels.Values.Any(c => c.Keys.Count > 0 && Channel.DeviceOf(c.Path, out _, out _));
                    if (!deviceKeys && !hasDeviceMods) continue;
                    layer.Action = act;
                    sb.Append("|A").Append(Json.Serialize(act.ToJson()).GetHashCode());
                }

                sb.Append('|').Append(Json.Serialize(istr.ToJson()).GetHashCode());
                edit.Layers.Add(layer);
            }

            if (edit.Layers.Count == 0) return null;
            if (!waiting)
            {
                // device modifiers (smooth, hold, fill...): run over each such strip's frames, on the devices as they arrive at it
                for (int i = 0; i < edit.Layers.Count; i++)
                {
                    var L = edit.Layers[i];
                    if (!L.Strip.Modifiers.Any(m => m.Enabled && SceneModifiers.Device.ContainsKey(m.Type))) continue;
                    L.Frames = DeviceFramesFor(s, a, st, baseSolve, edit, i, plan, ta, tb);
                }
            }

            key = sb.ToString();
            edit.Key = key;
            return edit;
        }

        /// <summary>The devices layer i sees and makes over its frames, then its device modifiers applied.</summary>
        DeviceFrames DeviceFramesFor(Scene s, Actor a, Strip st, TakeSolve baseSolve, InputEdit edit, int i,
            ActorPlan plan, double ta, double tb)
        {
            var L = edit.Layers[i];
            double a0 = Math.Max(ta, L.Strip.Start), b0 = Math.Min(tb, L.Strip.End);
            int n = Math.Max(2, (int)Math.Ceiling((b0 - a0) * s.Fps) + 1);
            var d = new DeviceFrames { Time = new double[n], Fps = s.Fps, Scale = plan.Scale };
            var below = new InputEdit { SceneTime = edit.SceneTime };
            below.Layers.AddRange(edit.Layers.Take(i + 1));
            var roles = InputMask.Roles(L.Mask).ToList();
            foreach (var r in roles)
            {
                d.Pose[r] = new Pose[n];
                d.Valid[r] = new bool[n];
            }

            for (int k = 0; k < n; k++)
            {
                double t = Math.Min(b0, a0 + k / (double)s.Fps);
                d.Time[k] = t;
                double src = st.ToSource(t);
                foreach (var r in roles)
                {
                    var p = Pose.Identity;
                    bool ok = baseSolve != null && baseSolve.Device(r.ToString(), src, out p);
                    var e = below.Apply(r, src, p, ok);
                    d.Pose[r][k] = e.Item1;
                    d.Valid[r][k] = e.Item2;
                }
            }

            var ctx = new StripModifierContext
            {
                Scene = s, Actor = a, Strip = L.Strip, Take = baseSolve, Rig = plan.Rig, Scale = plan.Scale,
                Fps = s.Fps, TimeOf = k => d.Time[Math.Max(0, Math.Min(n - 1, k))]
            };
            foreach (var m in L.Strip.Modifiers)
                if (m.Enabled && SceneModifiers.Device.TryGetValue(m.Type, out var mod))
                    try { mod.Apply(ctx, m, d); }
                    catch (Exception ex) { AppLog.Error($"Device modifier {m.Type}: {ex}"); }

            return d;
        }

        /// <summary>
        ///     The solve a take strip on a pose track uses: the take as recorded when no input track is over it, else the take
        ///     solved with the input strips' device edits (and influence variants blended in).
        /// </summary>
        TakeSolve SolveStrip(Scene s, Actor a, int ti, Strip st, TakeRef tr, ActorPlan plan, List<string> warnings,
            out bool pending)
        {
            var track = a.Tracks[ti];
            double hIn = track.TransitionInto(st.Id)?.Duration / 2 ?? 0,
                hOut = track.TransitionOutOf(st.Id)?.Duration / 2 ?? 0;
            double ta = st.Start - hIn, tb = st.End + hOut;
            var baseSolve = Takes.Solve(tr.Path, plan.Profile, s.Fps, null, null, null, out bool pendBase);
            pending = pendBase;
            if (InputStripsOver(a, ti, ta, tb).Count == 0) return baseSolve;
            string slot = st.Id + "|main";
            _usedSlots.Add(slot);
            var edit = BuildEdit(s, a, ti, st, baseSolve, plan, ta, tb, out bool waiting, out _);
            if (edit == null) return baseSolve;
            if (waiting)
            {
                // a re-recorded take isn't solved yet: keep showing what this strip had (or the take as recorded)
                pending = true;
                return Takes.Peek(slot) ?? baseSolve;
            }

            // only the part of the take the strip plays (+1 s settle time before and after); while a device is dragged,
            // just a window around the playhead
            double sa = Math.Min(st.ToSource(ta), st.ToSource(tb)), sb = Math.Max(st.ToSource(ta), st.ToSource(tb));
            double[] range = new[] { Math.Max(0, sa - 1.0), sb + 1.0 };
            bool preview = !double.IsNaN(PreviewAt) && PreviewAt >= ta - 1 && PreviewAt <= tb + 1;
            if (preview)
            {
                double c = st.ToSource(PreviewAt);
                range = new[] { Math.Max(range[0], c - 1.5), Math.Min(range[1], c + 1.5) };
            }

            var main = Takes.Solve(tr.Path, plan.Profile, s.Fps, edit, range, slot, out bool pm);
            pending |= pm;
            if (main == null) return baseSolve;
            var roles = edit.InfluenceRoles();
            if (roles.Count == 0 || preview) return main;
            var variants = new List<(BodyRole Role, TakeSolve Solve)>();
            foreach (var r in roles)
            {
                string vs = st.Id + "|off:" + r;
                _usedSlots.Add(vs);
                var v = Takes.Solve(tr.Path, plan.Profile, s.Fps, edit.Variant(r), range, vs, out bool pv);
                pending |= pv;
                if (v != null) variants.Add((r, v));
            }

            if (variants.Count < roles.Count || main.Edit == null) return main;
            return Combine(main, variants);
        }

        /// <summary>
        ///     The devices fed to the solve at scene frame f for the actor: from the take strip under the input layers (the
        ///     topmost pose-track take strip covering f below trackLimit, or baseStripId), with the input strips between it and
        ///     trackLimit applied; plus each device's influence. Device-only takes: their keyed devices. Solve space
        ///     (BakedClip.Trackers space; Head = the headset). False while something is still solving.
        /// </summary>
        public bool EvaluateDevices(Scene s, Actor a, int f, int trackLimit, Dictionary<BodyRole, Pose> pose,
            Dictionary<BodyRole, float> influence, string baseStripId = null)
        {
            lock (this)
            {
                pose.Clear();
                influence?.Clear();
                double t = s.Start + f / (double)s.Fps;
                var plan = new ActorPlan { Actor = a, Start = s.Start, Fps = s.Fps };
                plan.Profile = ActorProfile(s, a, out bool pend);
                plan.Rig = PoseRig.For(plan.Profile);
                plan.Scale = plan.Profile.Scale;
                if (_lastSolves.TryGetValue(a.Id, out var last))
                {
                    var anyTake = a.Tracks.Where(x => x.Kind == TrackKind.Pose).SelectMany(x => x.Strips)
                        .Where(x => x.Kind == StripKind.Take && last.ContainsKey(x.Id)).OrderBy(x => x.Start)
                        .FirstOrDefault();
                    if (anyTake != null) plan.Scale = last[anyTake.Id].Clip.Scale;
                }

                // the base strip
                int bti = -1;
                Strip bst = null;
                for (int k = Math.Min(trackLimit, a.Tracks.Count) - 1; k >= 0 && bst == null; k--)
                {
                    var tk = a.Tracks[k];
                    if (tk.Kind != TrackKind.Pose || tk.Mute) continue;
                    foreach (var st in tk.Strips)
                    {
                        if (st.Mute) continue;
                        if (baseStripId != null
                                ? st.Id != baseStripId
                                : !(t >= st.Start - 1e-9 && t <= st.End + 1e-9 &&
                                    (st.Kind == StripKind.Take || IsDeviceAction(s.Action(st.Source))))) continue;
                        bti = k;
                        bst = st;
                        break;
                    }
                }

                if (bst == null) return !pend;
                double src = bst.ToSource(t);
                TakeSolve baseSolve = null;
                KeyAction devAct = null;
                if (bst.Kind == StripKind.Take)
                {
                    var tr = s.Take(bst.Source);
                    if (tr == null) return true;
                    baseSolve = Takes.Solve(tr.Path, plan.Profile, s.Fps, null, null, null, out bool pb);
                    if (baseSolve == null) return false;
                    pend |= pb;
                }
                else devAct = s.Action(bst.Source);

                // input strips between the base and trackLimit
                var sub = new Actor
                    { Id = a.Id, Tracks = a.Tracks.Take(Math.Min(trackLimit, a.Tracks.Count)).ToList() };
                var edit = BuildEdit(s, sub, bti, bst, baseSolve, plan, t - 1e-6, t + 1e-6, out bool waiting, out _);
                float[] buf = new float[4];
                foreach (BodyRole r in Enum.GetValues(typeof(BodyRole)))
                {
                    var p = Pose.Identity;
                    bool ok = false;
                    float inf = 1;
                    if (baseSolve != null) ok = baseSolve.Device(r.ToString(), src, out p);
                    else if (devAct != null)
                    {
                        var pc = devAct.Get(Channel.DevicePosition(r));
                        var rc = devAct.Get(Channel.DeviceRotation(r));
                        var ic = devAct.Get(Channel.Influence(r));
                        if (pc != null && pc.Keys.Count > 0)
                        {
                            pc.Evaluate(src, buf);
                            p.pos = new Vec3(buf[0], buf[1], buf[2]);
                            ok = true;
                        }

                        if (rc != null && rc.Keys.Count > 0)
                        {
                            rc.Evaluate(src, buf);
                            p.rot = new Quat(buf[0], buf[1], buf[2], buf[3]).Normalized;
                        }

                        if (ic != null && ic.Keys.Count > 0)
                        {
                            ic.Evaluate(src, buf);
                            inf = MathX.Clamp01(buf[0]);
                        }
                    }

                    if (edit != null)
                    {
                        var e = edit.Apply(r, src, p, ok);
                        p = e.Item1;
                        ok = e.Item2;
                        inf *= edit.Influence(r, t);
                    }

                    if (ok) pose[r] = p;
                    if (influence != null && (ok || inf < 1)) influence[r] = inf;
                }

                return !(pend || waiting);
            }
        }

        /// <summary>
        ///     Places a re-recorded take's devices onto the base take: a turn about the vertical axis and a horizontal move that
        ///     put its headset where the base take's headset is, at the input strip's start (heights unchanged).
        /// </summary>
        static Pose HeadAlign(TakeSolve baseSolve, Strip baseStrip, TakeSolve input, Strip inputStrip)
        {
            double t0 = Math.Max(inputStrip.Start, baseStrip.Start);
            if (!baseSolve.Device("Head", baseStrip.ToSource(t0), out var ha) ||
                !input.Device("Head", inputStrip.ToSource(t0), out var hb)) return Pose.Identity;
            float yaw = MathX.DeltaAngle(YawOf(hb.rot), YawOf(ha.rot));
            var r = Quat.YawRotation(yaw);
            var d = ha.pos - r * hb.pos;
            return new Pose(new Vec3(d.x, 0, d.z), r);
        }

        static float YawOf(Quat q)
        {
            var f = q * Vec3.Forward;
            return (float)(Math.Atan2(f.x, f.z) * MathX.Rad2Deg);
        }

        /// <summary>
        ///     Blends the influence variants into the main edited solve: per frame, each device's (1 - influence) share of
        ///     the solve without it.
        /// </summary>
        TakeSolve Combine(TakeSolve main, List<(BodyRole Role, TakeSolve Solve)> variants)
        {
            string key = main.Key + "|" + main.Version + "|" + string.Join(",", variants.Select(v => v.Solve.Version));
            if (_combined.TryGetValue(key, out var c)) return c;
            var edit = main.Edit;
            var src = main.Clip;
            int n = src.Frames;
            var clip = new BakedClip
            {
                Profile = src.Profile, Fps = src.Fps, Frames = n, SourceTime = src.SourceTime, Hips = new Vec3[n],
                Rot = new Quat[n][], BoneExists = src.BoneExists,
                BodySource = src.BodySource, Scale = src.Scale, ScaleCurve = src.ScaleCurve,
                SourceProfile = src.SourceProfile, Calibration = src.Calibration, Camera = src.Camera,
                CameraValid = src.CameraValid
            };
            foreach (var kv in src.Face) clip.Face[kv.Key] = kv.Value;
            foreach (var kv in src.ParamValues) clip.ParamValues[kv.Key] = kv.Value;
            foreach (var kv in src.ParamTypes) clip.ParamTypes[kv.Key] = kv.Value;
            foreach (var kv in src.Trackers) clip.Trackers[kv.Key] = kv.Value;
            foreach (var kv in src.TrackerValid) clip.TrackerValid[kv.Key] = kv.Value;
            clip.Warnings.AddRange(src.Warnings);
            clip.TrackerInfluence = new Dictionary<string, float[]>();
            foreach (var v in variants) clip.TrackerInfluence[v.Role.ToString()] = new float[n];
            var rig = main.Rig;
            var local = new Quat[n][];
            for (int f = 0; f < n; f++)
            {
                double t = edit.SceneTime(src.SourceTime[f]);
                var L = (Quat[])main.Local[f].Clone();
                var H = src.Hips[f];
                foreach (var v in variants)
                {
                    float w = edit.Influence(v.Role, t);
                    clip.TrackerInfluence[v.Role.ToString()][f] = w;
                    if (w >= 0.9999f) continue;
                    int fv = Math.Min(f, v.Solve.Clip.Frames - 1);
                    var Lv = v.Solve.Local[fv];
                    for (int b = 0; b < HumanBones.Count; b++)
                        if (rig.Exists[b])
                            L[b] = Quat.Slerp(L[b], Lv[b], 1 - w);
                    H = Vec3.Lerp(H, v.Solve.Clip.Hips[fv], 1 - w);
                }

                local[f] = L;
                clip.Hips[f] = H;
                var wr = new Quat[HumanBones.Count];
                rig.ToWorld(L, wr);
                clip.Rot[f] = wr;
            }

            c = new TakeSolve
            {
                Path = main.Path, Key = key, Slot = main.Slot, Rec = main.Rec, Clip = clip, Rig = rig, Local = local,
                Fps = main.Fps, Start = main.Start, Edit = edit, Version = Interlocked.Increment(ref _combinedVersion)
            };
            foreach (string k in _combined.Where(kv => kv.Value.Slot == main.Slot).Select(kv => kv.Key).ToList())
                _combined.Remove(k);
            _combined[key] = c;
            return c;
        }
    }
}
