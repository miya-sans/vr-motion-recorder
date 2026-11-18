using System;
using System.Collections.Generic;
using System.Linq;

namespace VRMR.UI
{
    /// <summary>
    ///     A keyframe strip (action) in the Scene tab: the keys at the playhead, previous / next key, key / delete / key from
    ///     the take, interpolation, mirror / copy / paste, moving keys in time, the keyed channels, and for device keys (an
    ///     input track) each device's influence on the IK solve at the playhead (0 = the tracker is off, like VRChat without
    ///     it).
    /// </summary>
    public sealed class ActionInspector : ISceneInspector
    {
        public int Order => 40;

        public bool Applies(SceneEditor ed)
        {
            return ed.SelectionKind == "strip" && ed.SelectedStrip?.Kind == StripKind.Action &&
                   ed.Scene.Action(ed.SelectedStrip.Source) != null;
        }

        public void Build(InspectorContext c)
        {
            var ed = c.Editor;
            var x = new Ctx { Ed = ed, StripId = ed.SelectedStrip.Id };
            bool dev = x.Devices;
            c.Section(dev ? "Device keys (IK inputs)" : "Keyframes");
            c.Note(dev
                ? "Keys move the headset, controllers and trackers that the IK solves the body from; influence switches a device off / on over time."
                : x.Strip.Blend == BlendMode.Add
                    ? "Bone keys on top of the tracks below (Add: changes to their pose)."
                    : "Bone keys (Replace: these bones are posed as keyed).");

            // ---- at the playhead
            var here = c.Note("", false);
            c.OnRefresh(() =>
            {
                var a = x.Act;
                if (a == null) return;
                if (!x.Inside)
                {
                    here.Text = "The playhead is outside this strip.";
                    return;
                }

                double t = x.T;
                var keyed = KeyframeOps.KeyedAt(a, t);
                string head =
                    $"Frame {Math.Round(ed.Playhead * ed.Scene.Fps)}  ·  strip time {t:0.000} s  ·  {a.KeyTimes().Count} keys in all";
                here.Text = head + "\n" + (keyed.Count == 0 ? "No keys at the playhead." : "◆ " + Describe(keyed));
            });
            Props.Buttons(c, ("◀ Prev key", () => Jump(x, -1)), ("Next key ▶", () => Jump(x, 1)),
                ("Key all here", () => KeyAll(x)));
            Props.Buttons(c, ("Key from take", () => KeyFromBelow(x)), ("Delete keys here", () => DeleteHere(x)),
                ("Mirror here", () => MirrorHere(x)));
            Props.Buttons(c, ("Copy", () => CopyHere(x)), ("Paste", () => PasteHere(x, false)),
                ("Paste mirrored", () => PasteHere(x, true)));

            // ---- interpolation
            string[] interpNames = new[] { "Constant (hold)", "Linear", "Smooth (bezier)" };
            Props.Choice(c, "Keys here", interpNames, () =>
                {
                    var a = x.Act;
                    var i = a != null && x.Inside ? KeyframeOps.InterpAt(a, x.T) : null;
                    return i.HasValue ? (int)i.Value : -1;
                },
                i =>
                {
                    if (x.Act != null && KeyframeOps.HasKey(x.Act, x.T))
                        ed.Edit("Key interpolation", () => KeyframeOps.SetInterp(x.Act, x.T, (Interp)i));
                },
                "How the keys at the playhead move on to the next key");
            Props.Choice(c, "New keys", interpNames, () => (int)KeyframeOps.DefaultInterp,
                i => KeyframeOps.DefaultInterp = (Interp)i, "Interpolation of keys added from now on");

            // ---- timing
            Props.Buttons(c, ("◀ 1 frame", () => MoveHere(x, -1)), ("1 frame ▶", () => MoveHere(x, 1)),
                ("Later keys ▶", () => MoveLater(x, 1)));
            c.Note("Moves the keys at the playhead (or every key after it) by one frame.");

            // ---- devices: influence per device
            if (dev) BuildDevices(c, x);

            // ---- keyed channels
            c.Section("Keyed channels");
            var chans = c.Note("", false);
            c.OnRefresh(() =>
            {
                var a = x.Act;
                if (a != null) chans.Text = Channels(a);
            });
            Props.Buttons(c, ("Clear selected", () => ClearSelected(x)), ("Pose tool", () => OpenTool(ed, dev)));
        }

        // ---------------------------------------------------------------- devices

        void BuildDevices(InspectorContext c, Ctx x)
        {
            var ed = x.Ed;
            c.Section("Influence on the IK solve (at the playhead)");
            bool take = x.X.Track?.Kind == TrackKind.Pose;
            c.Note(take
                ? "1 = the device drives the body, 0 = the IK solves without it. On a device take a device is either on or off (below 50 % it is off). Dragging keys it here."
                : "1 = the device drives the body as recorded, 0 = the IK solves without it (the bone follows the rest of the body); in between blends the two. Dragging keys it here.");
            var below = new DeviceFrame();
            KeyDevices.Below(ed.Eval, x.S, x.X.Actor, x.TrackIndex, x.Frame, below);
            var roles = KeyDevices.Roles.Where(r =>
                (x.Strip.Inputs & InputMask.PartOf(r)) != 0 && (below.Has(r) || KeyframeOps.KeysDevice(x.Act, r) ||
                                                                x.Act.Get(Channel.Influence(r)) != null ||
                                                                below.KeyframeOnly)).ToList();
            foreach (var r0 in roles)
            {
                var r = r0;
                Props.Slider(c, PoseState.RoleLabel(r), 0, 1,
                    () => x.Act != null && x.Inside ? KeyframeOps.Influence(x.Act, r, x.T) : 1f,
                    v =>
                    {
                        if (x.Act != null && x.Inside) KeyframeOps.KeyInfluence(x.Act, x.T, r, v);
                    }, 0.01f, "0%", "",
                    "This device's influence at the playhead (a key). 0 switches it off.");
            }

            Props.Number(c, "Ramp", () => PoseState.InfluenceRamp, v => PoseState.InfluenceRamp = v, "0.00", " s",
                "How long switching a device off / on takes (Off / On below, H in the Pose tool)", 0, 10);
            Props.Buttons(c, ("Selected off", () => Ramp(x, false)), ("Selected on", () => Ramp(x, true)), (
                "Select all", () =>
                {
                    foreach (var r in roles) PoseState.Devices.Add(r);
                    PoseState.Notify();
                    ed.View.Invalidate();
                }));
            var sel = c.Note("");
            c.OnRefresh(() => sel.Text = PoseState.Devices.Count == 0
                ? "Selected: none (pick devices with the Pose tool, or Select all)"
                : "Selected: " + string.Join(", ", PoseState.Devices.Select(PoseState.RoleLabel)));
        }

        static void Ramp(Ctx x, bool on)
        {
            if (PoseState.Devices.Count == 0 || !x.Inside) return;
            x.Ed.Edit(on ? "Devices on" : "Devices off", () =>
            {
                foreach (var r in PoseState.Devices)
                    KeyframeOps.RampInfluence(x.Act, r, x.T, on ? 1f : 0f, PoseState.InfluenceRamp);
            });
            PoseState.Notify();
        }

        // ---------------------------------------------------------------- operations

        static Func<string, bool> Sel(Ctx x)
        {
            if (x.Devices)
                return PoseState.Devices.Count > 0
                    ? KeyframeOps.Filter(PoseState.Devices, null)
                    : p => Channel.DeviceOf(p, out _, out _);
            return PoseState.Bones.Count > 0
                ? KeyframeOps.Filter(null, PoseState.Bones, PoseState.Bones.Contains(HumanBones.Hips))
                : (Func<string, bool>)KeyframeOps.IsBody;
        }

        static void Jump(Ctx x, int dir)
        {
            var a = x.Act;
            if (a == null) return;
            double? k = dir < 0 ? KeyframeOps.PrevKey(a, x.T) : KeyframeOps.NextKey(a, x.T);
            if (k.HasValue) x.Ed.Seek(x.Strip.ToScene(k.Value));
        }

        /// <summary>Keys every keyed channel (and for devices every device present) at the playhead with its current value.</summary>
        static void KeyAll(Ctx x)
        {
            if (!x.Inside) return;
            var ed = x.Ed;
            if (x.Devices)
            {
                var below = new DeviceFrame();
                KeyDevices.Below(ed.Eval, x.S, x.X.Actor, x.TrackIndex, x.Frame, below);
                var paths = x.Act.Channels.Keys.Where(p => Channel.DeviceOf(p, out _, out _)).ToList();
                foreach (var r in KeyDevices.Roles)
                    if (below.Has(r))
                    {
                        paths.Add(Channel.DevicePosition(r));
                        paths.Add(Channel.DeviceRotation(r));
                    }

                ed.Edit("Key devices",
                    () => KeyframeOps.InsertCurrent(x.Act, x.Strip, x.T, paths, p => DeviceBase(below, p)));
            }
            else
            {
                var below = new ScenePose();
                if (!ed.Eval.EvaluateFrame(x.S, x.X.Actor, x.Frame, below, x.TrackIndex)) return;
                var paths = x.Act.Channels.Keys.Where(KeyframeOps.IsBody).ToList();
                if (paths.Count == 0)
                {
                    var (rig, _) = ed.Eval.RigOf(x.S, x.X.Actor);
                    paths = rig.Order.Where(b => !HumanBones.IsFinger(b)).Select(Channel.Bone).ToList();
                    paths.Add(Channel.RootPosition);
                }

                ed.Edit("Key pose",
                    () => KeyframeOps.InsertCurrent(x.Act, x.Strip, x.T, paths, p => BoneBase(x.Strip, below, p)));
            }

            PoseState.Notify();
        }

        static float[] DeviceBase(DeviceFrame below, string p)
        {
            if (!Channel.DeviceOf(p, out var r, out string k) || !below.Pose.TryGetValue(r, out var bp)) return null;
            return k == "position" ? KeyframeOps.V(bp.pos) : k == "rotation" ? KeyframeOps.V(bp.rot) : new[] { 1f };
        }

        static float[] BoneBase(Strip st, ScenePose below, string p)
        {
            var offI = st.Offset.Inverse;
            int b = Channel.BoneOf(p);
            if (b >= 0)
                return KeyframeOps.V(b == HumanBones.Hips ? (offI.rot * below.Local[b]).Normalized : below.Local[b]);
            if (KeyframeOps.IsRoot(p)) return KeyframeOps.V(offI.TransformPoint(below.Hips));
            return null;
        }

        /// <summary>
        ///     Keys the selection (all devices / the whole body when nothing is selected) as the take below has it at the
        ///     playhead.
        /// </summary>
        static void KeyFromBelow(Ctx x)
        {
            if (!x.Inside) return;
            var ed = x.Ed;
            if (x.Devices)
            {
                var below = new DeviceFrame();
                if (!KeyDevices.Below(ed.Eval, x.S, x.X.Actor, x.TrackIndex, x.Frame, below)) return;
                var roles = (PoseState.Devices.Count > 0 ? PoseState.Devices.AsEnumerable() : KeyDevices.Roles)
                    .Where(r => below.Pose.ContainsKey(r) && (below.Has(r) || below.KeyframeOnly)).ToList();
                if (roles.Count == 0) return;
                ed.Edit("Key devices from take",
                    () => KeyframeOps.KeyDevices(x.Act, x.Strip, x.T, below.Pose, below.Pose, roles));
            }
            else
            {
                var below = new ScenePose();
                if (!ed.Eval.EvaluateFrame(x.S, x.X.Actor, x.Frame, below, x.TrackIndex)) return;
                var (rig, _) = ed.Eval.RigOf(x.S, x.X.Actor);
                var bones = PoseState.Bones.Count > 0
                    ? PoseState.Bones.Where(b => rig.Exists[b]).ToList()
                    : rig.Order.Where(b =>
                        !HumanBones.IsFinger(b) && b != HumanBones.LeftEye && b != HumanBones.RightEye &&
                        b != HumanBones.Jaw).ToList();
                ed.Edit("Key pose from take",
                    () => KeyframeOps.KeyPose(x.Act, x.Strip, x.T, below, below, bones,
                        bones.Contains(HumanBones.Hips)));
            }

            PoseState.Notify();
        }

        static void DeleteHere(Ctx x)
        {
            if (!x.Inside || !KeyframeOps.HasKey(x.Act, x.T, Sel(x))) return;
            x.Ed.Edit("Delete keys", () => KeyframeOps.DeleteKeys(x.Act, x.T, Sel(x)));
            PoseState.Notify();
        }

        static KeyframeOps.MirrorFrame Frame(Ctx x)
        {
            var ed = x.Ed;
            var (rig, _) = ed.Eval.RigOf(x.S, x.X.Actor);
            if (x.Devices)
            {
                var d = new DeviceFrame();
                KeyDevices.Below(ed.Eval, x.S, x.X.Actor, x.X.Actor.Tracks.Count, x.Frame, d);
                var h = d.Pose.TryGetValue(BodyRole.Head, out var hp) ? hp : Pose.Identity;
                return new KeyframeOps.MirrorFrame { Origin = h.pos, Yaw = h.rot.Yaw, Rig = rig };
            }

            var p = new ScenePose();
            ed.Eval.EvaluateFrame(x.S, x.X.Actor, x.Frame, p);
            float[] rv = KeyframeOps.Value(x.Act, Channel.RootPosition, x.T);
            return new KeyframeOps.MirrorFrame
            {
                Origin = rv != null && x.Strip.Blend == BlendMode.Replace ? KeyframeOps.P(rv) : p.Hips,
                Yaw = rig.Facing(p.Local[HumanBones.Hips]), Rig = rig
            };
        }

        static void MirrorHere(Ctx x)
        {
            if (!x.Inside || !KeyframeOps.HasKey(x.Act, x.T, Sel(x))) return;
            var m = Frame(x);
            x.Ed.Edit("Mirror keys", () => KeyframeOps.MirrorKeys(x.Act, x.Strip, x.T, m, Sel(x)));
            PoseState.Notify();
        }

        static void CopyHere(Ctx x)
        {
            if (x.Inside) KeyframeOps.Clipboard = KeyframeOps.Copy(x.Act, x.Strip, x.T, Sel(x));
        }

        static void PasteHere(Ctx x, bool mirror)
        {
            var c = KeyframeOps.Clipboard;
            if (c == null || c.Count == 0 || !x.Inside) return;
            var m = mirror ? Frame(x) : null;
            x.Ed.Edit(mirror ? "Paste keys mirrored" : "Paste keys", () => KeyframeOps.Paste(x.Act, x.T, c, m));
            PoseState.Notify();
        }

        static void MoveHere(Ctx x, int frames)
        {
            if (!x.Inside || !KeyframeOps.HasKey(x.Act, x.T, Sel(x))) return;
            double dt = frames / (double)x.S.Fps * x.Strip.Speed;
            double t = x.T;
            x.Ed.Edit("Move keys", () => KeyframeOps.MoveKeys(x.Act, new[] { t }, dt, Sel(x)));
            x.Ed.Seek(x.Strip.ToScene(t + dt));
        }

        static void MoveLater(Ctx x, int frames)
        {
            double dt = frames / (double)x.S.Fps * x.Strip.Speed;
            var times = KeyframeOps.KeyTimes(x.Act).Where(t => t > x.T - KeyframeOps.Same).ToList();
            if (times.Count == 0) return;
            x.Ed.Edit("Move later keys", () => KeyframeOps.MoveKeys(x.Act, times.OrderByDescending(t => t), dt));
        }

        static void ClearSelected(Ctx x)
        {
            bool any = x.Devices ? PoseState.Devices.Count > 0 : PoseState.Bones.Count > 0;
            if (!any) return;
            var f = x.Devices
                ? KeyframeOps.Filter(PoseState.Devices, null)
                : KeyframeOps.Filter(null, PoseState.Bones, PoseState.Bones.Contains(HumanBones.Hips));
            if (!x.Act.Channels.Keys.Any(f)) return;
            x.Ed.Edit("Clear channels", () => KeyframeOps.ClearChannels(x.Act, f));
            PoseState.Notify();
        }

        static void OpenTool(SceneEditor ed, bool devices)
        {
            PoseState.Current = devices ? PoseState.Mode.Devices : PoseState.Mode.Bones;
            if (!(ed.ActiveTool is PoseToolEntry))
                ed.SetTool(new PoseToolEntry().Name); // the registered tool: its toggle lights
            PoseState.Notify();
            ed.View.Focus();
        }

        // ---------------------------------------------------------------- text

        /// <summary>"Headset (position, rotation), L foot (influence), L hand, spine…" for a list of paths.</summary>
        static string Describe(List<string> paths)
        {
            var parts = new List<string>();
            foreach (var g in paths.GroupBy(Group))
            {
                var kinds = g.Select(p => Channel.DeviceOf(p, out _, out string k) ? k : null).Where(k => k != null)
                    .Distinct().ToList();
                parts.Add(g.Key + (kinds.Count > 0 && kinds.Count < 3 ? " (" + string.Join(", ", kinds) + ")" : ""));
            }

            return string.Join(", ", parts);
        }

        static string Group(string p)
        {
            if (Channel.DeviceOf(p, out var r, out _)) return PoseState.RoleLabel(r);
            int b = Channel.BoneOf(p);
            if (b >= 0) return PoseState.BoneLabel(b);
            if (KeyframeOps.IsRoot(p)) return "Root position";
            if (p.StartsWith("face/")) return "Face";
            if (p.StartsWith("param/")) return "Parameters";
            return p;
        }

        static string Channels(KeyAction a)
        {
            var groups = a.Channels.Values.Where(c => c.Keys.Count > 0).GroupBy(c => Group(c.Path)).OrderBy(g => g.Key)
                .ToList();
            if (groups.Count == 0) return "Nothing keyed yet: pose with the Pose tool (auto-key) or use Key all here.";
            return string.Join("\n",
                groups.Select(g => $"{g.Key}: " + string.Join(", ",
                    g.Select(c =>
                        (Channel.DeviceOf(c.Path, out _, out string k) ? k :
                            KeyframeOps.IsRoot(c.Path) ? "position" : "rotation") + " " + c.Keys.Count))));
        }

        sealed class Ctx
        {
            public SceneEditor Ed;
            public string StripId;
            public Scene S => Ed.Scene;
            public (Actor Actor, NlaTrack Track, Strip Strip) X => S.FindStrip(StripId);
            public Strip Strip => X.Strip;
            public KeyAction Act => Strip != null ? S.Action(Strip.Source) : null;
            public bool Devices => KeyTarget.FitsDevices(S, X.Track, Strip);

            public int Frame => Math.Min(KeyTarget.FrameOf(S, Ed.Playhead),
                Math.Max(0, SceneEvaluator.FrameCount(S) - 1));

            public double T => KeyTarget.ActionTime(S, Strip, Frame);
            public int TrackIndex => X.Actor.Tracks.IndexOf(X.Track);
            public bool Inside => Strip != null && Ed.Playhead >= Strip.Start - 1e-6 && Ed.Playhead <= Strip.End + 1e-6;
        }
    }
}
