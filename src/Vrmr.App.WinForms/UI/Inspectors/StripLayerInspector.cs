using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace VRMR.UI
{
    /// <summary>
    /// The layer side of a strip: what it drives and its cleanup. On an input track: the inputs (devices by body part,
    /// finger tracking per hand, face tracking, parameters) as a clickable figure with presets, how a re-recorded take is
    /// placed, and per-device influence. On a pose track: the body-part mask. Then the strip's modifier stack.
    /// </summary>
    public sealed class StripLayerInspector : ISceneInspector
    {
        public int Order => 35;
        public bool Applies(SceneEditor ed) => ed.SelectionKind == "strip";

        public void Build(InspectorContext c)
        {
            var ed = c.Editor; var s0 = ed.SelectedStrip; if (s0 == null) return;
            string id = s0.Id;
            Strip S() => ed.Scene.FindStrip(id).Strip;
            var where = ed.Scene.FindStrip(id);
            bool input = where.Track?.Kind == TrackKind.Input;
            if (input) BuildInputs(c, id, S, where.Actor);
            else BuildMask(c, S);
            BuildModifiers(c, id, S, input);
            if (!input && s0.Kind == StripKind.Take) BuildDeviceCleanup(c, id, S, where.Actor);
        }

        // ---------------------------------------------------------------- input layers

        void BuildInputs(InspectorContext c, string id, Func<Strip> S, Actor actor)
        {
            var ed = c.Editor; var s0 = S();
            c.Section("Layer inputs");
            c.Note(s0.Kind == StripKind.Take
                ? "What this re-recorded take replaces. Its devices replace the ones of the take below (which is solved again with them); face, fingers and parameters layer directly."
                : "What these keys drive: device positions and how much each device steers the body (IK).");
            var presets = InputPresets();
            var names = presets.Select(p => p.Name).ToList(); names.Add("Custom");
            int Idx() { var m = S()?.Inputs ?? InputPart.All; int i = presets.FindIndex(p => p.Mask == m); return i >= 0 ? i : names.Count - 1; }
            Props.Choice(c, "Preset", names.ToArray(), Idx, i => { if (i < presets.Count) ed.Edit("Layer inputs", () => S().Inputs = presets[i].Mask); },
                "Which inputs this layer drives. Click the figure to switch single devices on or off.");
            var fig = c.Add(new LayerFigure(LayerFigure.Mode.Inputs) { Value = (int)s0.Inputs });
            fig.Toggled += v => ed.Edit("Layer inputs", () => S().Inputs = (InputPart)v);
            var desc = c.Note("", false);
            c.OnRefresh(() =>
            {
                var s = S(); if (s == null) return;
                fig.Value = (int)s.Inputs;
                fig.Available = s.Kind == StripKind.Take ? Available(ed, s, actor) : -1;
                fig.Invalidate();
                var missing = s.Kind == StripKind.Take && fig.Available >= 0 ? InputMask.Roles(s.Inputs).Where(r => (fig.Available & (int)InputMask.PartOf(r)) == 0).ToList() : new List<BodyRole>();
                // the solve can only take over devices the take below was calibrated with
                int below = BelowAvailable(ed, s, actor);
                var notBelow = below >= 0 ? InputMask.Roles(s.Inputs).Where(r => (below & (int)InputMask.PartOf(r)) == 0 && !missing.Contains(r)).ToList() : new List<BodyRole>();
                desc.Text = "Drives: " + LayerFigure.Describe(s.Inputs)
                    + (missing.Count > 0 ? "\nNot in this take (the take below keeps its own): " + string.Join(", ", missing.Select(r => Label(r).ToLowerInvariant())) : "")
                    + (notBelow.Count > 0 ? "\nThe take below has no " + string.Join(", ", notBelow.Select(r => Label(r).ToLowerInvariant())) + ": these can't be added to its solve yet." : "");
            });
            if (s0.Kind == StripKind.Take)
            {
                Props.Choice(c, "Placement", new[] { "As recorded (same spot in the playspace)", "Headset matched at the strip's start" }, () => (int)(S()?.InputAlign ?? InputAlign.Head),
                    i => ed.Edit("Layer placement", () => S().InputAlign = (InputAlign)i),
                    "How the re-recorded devices are placed onto the take below: where you stood, or moved so the headset meets the base take's (floor position and facing) where the strip starts.");
                BuildTakeWeights(c, S);
            }
            else BuildActionInfluence(c, S);
        }

        /// <summary>A re-recorded take: how much of each of its devices replaces the one below (Strip.InputWeights).</summary>
        void BuildTakeWeights(InspectorContext c, Func<Strip> S)
        {
            var ed = c.Editor;
            var roles = InputMask.Roles(S().Inputs).ToList();
            if (roles.Count == 0) return;
            c.Section("Device weights");
            c.Note("How much of each re-recorded device replaces the one below: 100% = the new one, 50% = halfway, 0% = the old one.");
            foreach (var r in roles)
            {
                var role = r; string key = role.ToString();
                Props.Slider(c, Label(role), 0, 100, () => (S()?.InputWeight(role) ?? 1) * 100,
                    v => { var s = S(); if (s == null) return; float x = MathX.Clamp(v / 100f, 0, 1); if (x >= 0.9999f) s.InputWeights.Remove(key); else s.InputWeights[key] = x; },
                    1, "0", "%", Label(role) + ": share of the re-recorded device");
            }
        }

        /// <summary>Input presets: the punch-in ones (Arms, Legs, Face tracking...) then the rest of InputMask's.</summary>
        public static List<(string Name, InputPart Mask)> InputPresets()
        {
            var l = PunchIn.Presets.Select(p => (p.Name, p.Parts)).ToList();
            foreach (var p in InputMask.Presets) if (!l.Any(x => x.Item2 == p.Mask)) l.Add((p.Name, p.Mask));
            return l;
        }

        /// <summary>Devices of the take strips under an input strip (pose tracks below it, overlapping it); -1 = unknown yet.</summary>
        static int BelowAvailable(SceneEditor ed, Strip s, Actor actor)
        {
            var x = ed.Scene.FindStrip(s.Id); if (x.Track == null || actor == null) return -1;
            int ti = actor.Tracks.IndexOf(x.Track), m = 0; bool any = false;
            for (int i = 0; i < ti; i++)
            {
                var t = actor.Tracks[i]; if (t.Kind != TrackKind.Pose || t.Mute) continue;
                foreach (var b in t.Strips)
                {
                    if (b.Kind != StripKind.Take || b.Mute || b.End <= s.Start || b.Start >= s.End) continue;
                    int a = Available(ed, b, actor); if (a < 0) return -1;
                    m |= a; any = true;
                }
            }
            return any ? m : -1;
        }

        /// <summary>Which inputs a take has (devices by role from its solve, fingers, face, parameters); -1 = unknown yet.</summary>
        static int Available(SceneEditor ed, Strip s, Actor actor)
        {
            var tr = ed.Scene.Take(s.Source); if (tr == null) return -1;
            var prof = ed.ClipOf(actor)?.Profile; if (prof == null) return -1;
            var sv = ed.Eval.Takes.Solve(tr.Path, prof, ed.Scene.Fps);
            if (sv == null) return -1;
            int m = 0;
            foreach (var k in sv.Clip.Trackers.Keys) if (Enum.TryParse(k, out BodyRole r)) m |= (int)InputMask.PartOf(r);
            if (sv.Clip.Face.Count > 0) m |= (int)InputPart.Face;
            if (sv.Clip.ParamValues.Count > 0) m |= (int)InputPart.Params;
            lock (sv.Rec.Sync)
            {
                if (sv.Rec.Hands[0].Count > 2) m |= (int)InputPart.LeftFingers;
                if (sv.Rec.Hands[1].Count > 2) m |= (int)InputPart.RightFingers;
            }
            return m;
        }

        /// <summary>Keys on an input track: each driven device's influence on the solve, as a constant (one key) unless it is keyed over time.</summary>
        void BuildActionInfluence(InspectorContext c, Func<Strip> S)
        {
            var ed = c.Editor; var s0 = S();
            var act = ed.Scene.Action(s0.Source); if (act == null) return;
            var roles = InputMask.Roles(s0.Inputs).ToList();
            if (roles.Count == 0) return;
            c.Section("Device influence");
            c.Note("How much each device steers the body: 100% = as recorded, 0% = not used (like VRChat without that tracker).");
            foreach (var r in roles)
            {
                var role = r;
                string path = Channel.Influence(role);
                KeyAction A() => ed.Scene.Action(S()?.Source ?? "");
                int Keys() => A()?.Get(path)?.Keys.Count ?? 0;
                if (Keys() > 1)
                {
                    c.Note($"{Label(role)}: keyed ({Keys()} keys) — edit the keys on the strip", false);
                    continue;
                }
                Props.Slider(c, Label(role), 0, 100, () =>
                {
                    var ch = A()?.Get(path); if (ch == null || ch.Keys.Count == 0) return 100;
                    return ch.Keys[0].V[0] * 100;
                }, v =>
                {
                    var a = A(); if (a == null) return;
                    var ch = a.GetOrAdd(path, 1);
                    float x = MathX.Clamp(v / 100f, 0, 1);
                    if (ch.Keys.Count == 0) ch.SetKey(S().SrcIn, new[] { x }, Interp.Constant); else ch.Keys[0].V = new[] { x };
                }, 1, "0", "%", $"{Label(role)}'s influence on the solve for the whole strip");
            }
        }

        static string Label(BodyRole r)
        {
            switch (r)
            {
                case BodyRole.Head: return "Headset";
                case BodyRole.LeftHand: return "Left controller";
                case BodyRole.RightHand: return "Right controller";
                default:
                    var n = r.ToString();
                    return (n.StartsWith("Left") ? "Left " + n.Substring(4).ToLowerInvariant() : n.StartsWith("Right") ? "Right " + n.Substring(5).ToLowerInvariant() : n) + " tracker";
            }
        }

        // ---------------------------------------------------------------- pose layers

        void BuildMask(InspectorContext c, Func<Strip> S)
        {
            var ed = c.Editor; var s0 = S();
            c.Section("Body parts");
            var presets = BodyMask.Presets.ToList();
            var names = presets.Select(p => p.Name).ToList(); names.Add("Custom");
            int Idx() { var m = S()?.Mask ?? BodyPart.All; int i = presets.FindIndex(p => p.Mask == m); return i >= 0 ? i : names.Count - 1; }
            Props.Choice(c, "Preset", names.ToArray(), Idx, i => { if (i < presets.Count) ed.Edit("Strip body parts", () => S().Mask = presets[i].Mask); },
                "Which body parts this strip drives (the tracks below keep the others). Click the figure for single parts.");
            var fig = c.Add(new LayerFigure(LayerFigure.Mode.BodyParts) { Value = (int)s0.Mask });
            fig.Toggled += v => ed.Edit("Strip body parts", () => S().Mask = (BodyPart)v);
            var desc = c.Note("", false);
            c.OnRefresh(() => { var s = S(); if (s == null) return; fig.Value = (int)s.Mask; fig.Invalidate(); desc.Text = "Drives: " + BodyMask.Describe(s.Mask); });
        }

        // ---------------------------------------------------------------- modifiers

        void BuildModifiers(InspectorContext c, string id, Func<Strip> S, bool input)
        {
            var ed = c.Editor;
            var s0 = S();
            LayerModifiers.Host h;
            if (input)
            {
                // device cleanup: runs on this layer's devices before the take below is solved again (ranges in scene seconds)
                h = new LayerModifiers.Host { What = "input", Key = id, List = () => S()?.Modifiers, Types = LayerDeviceModifiers.Types(), RangeUnits = "scene" };
                LayerModifiers.Build(c, h, "Device cleanup");
                return;
            }
            h = new LayerModifiers.Host
            {
                What = "strip", Key = id, List = () => S()?.Modifiers,
                Types = SceneModifiers.Strip.Values.Select(m => (m.Type, m.Label, (object)m)).OrderBy(t => t.Label).ToList(),
                RangeUnits = s0.Kind == StripKind.Take ? "take" : "scene",
                FromScene = t => { var s = S(); return s == null ? t : s.Kind == StripKind.Take ? s.ToSource(t) : t; },
                ToScene = t => { var s = S(); return s == null ? t : s.Kind == StripKind.Take ? s.ToScene(t) : t; },
                FindGlitches = s0.Kind == StripKind.Take ? (Func<List<RepairRange>>)(() => Glitches(ed, S())) : null,
            };
            LayerModifiers.Build(c, h);
        }

        /// <summary>
        /// Device-level cleanup of this take: a cleanup layer (an input track right above, with a keyless strip over this
        /// strip that drives just the chosen tracker and carries a device modifier), so the take is solved again from
        /// the cleaned tracker and the body follows it.
        /// </summary>
        void BuildDeviceCleanup(InspectorContext c, string id, Func<Strip> S, Actor actor)
        {
            var ed = c.Editor;
            if (LayerDeviceModifiers.Types().Count == 0) return;
            var bSmooth = new DarkButton("Smooth a tracker...");
            var bFill = new DarkButton("Fill a tracker gap...");
            new ToolTip().SetToolTip(bSmooth, "Adds a cleanup layer above this strip that smooths one device before the body is solved");
            new ToolTip().SetToolTip(bFill, "Adds a cleanup layer above this strip that fills marked stretches of one device (tracking loss the take's repair missed)");
            void Menu(DarkButton b, string type, string what)
            {
                var m = new ContextMenuStrip { ShowImageMargin = false };
                var s = S(); int avail = s != null ? Available(ed, s, actor) : -1;
                foreach (BodyRole r in Enum.GetValues(typeof(BodyRole)))
                {
                    var role = r;
                    if (avail >= 0 && (avail & (int)InputMask.PartOf(r)) == 0) continue;
                    m.Items.Add(Label(r)).Click += (o, e) => AddCleanupLayer(ed, id, role, type, what);
                }
                if (m.Items.Count == 0) m.Items.Add("(the take has no devices yet: still solving)").Enabled = false;
                m.Show(b, new System.Drawing.Point(0, b.Height));
            }
            bSmooth.Click += (o, e) => Menu(bSmooth, "devsmooth", "Smooth");
            bFill.Click += (o, e) => Menu(bFill, "devfill", "Fill");
            c.Section("Device cleanup");
            c.Note("Cleans a tracker before the body is solved (the body follows the cleaned device).");
            c.Page.Row(bSmooth, bFill);
        }

        public static Strip AddCleanupLayer(SceneEditor ed, string stripId, BodyRole role, string type, string what)
        {
            Strip made = null;
            ed.Edit(what + " " + Label(role).ToLowerInvariant(), () =>
            {
                var x = ed.Scene.FindStrip(stripId); if (x.Strip == null) return;
                made = MakeCleanupLayer(ed.Scene, x.Actor, x.Track, x.Strip, role, type, what);
                if (type == "devfill") { var m = made.Modifiers[0]; var l = m.Ranges(); l.Add(new RepairRange { A = Math.Max(made.Start, ed.Playhead - 0.25), B = Math.Min(made.End, ed.Playhead + 0.25) }); m.SetRanges(l); }
            });
            if (made != null) ed.Select(made);
            return made;
        }

        /// <summary>The cleanup layer itself (no UI): an input track right above the strip's track with a keyless action strip over it.</summary>
        public static Strip MakeCleanupLayer(Scene scene, Actor actor, NlaTrack track, Strip st, BodyRole role, string type, string what)
        {
            int ti = actor.Tracks.IndexOf(track);
            var t = SceneOps.AddTrack(actor, ti + 1, what + " " + Label(role).ToLowerInvariant());
            t.Kind = TrackKind.Input;
            var made = SceneOps.AddActionStrip(scene, t, what + " " + Label(role).ToLowerInvariant(), st.Start, st.Length);
            made.Inputs = InputMask.PartOf(role);
            SceneModifiers.Device.TryGetValue(type, out var impl);
            made.Modifiers.Add(ModArgs.Create(type, impl as IModifierParams));
            return made;
        }

        /// <summary>Glitch stretches in the strip's part of its solved take (take seconds); null while it solves.</summary>
        static List<RepairRange> Glitches(SceneEditor ed, Strip s)
        {
            if (s == null) return null;
            var tr = ed.Scene.Take(s.Source); var x = ed.Scene.FindStrip(s.Id);
            var prof = ed.ClipOf(x.Actor)?.Profile; if (tr == null || prof == null) return null;
            var sv = ed.Eval.Takes.Solve(tr.Path, prof, ed.Scene.Fps);
            if (sv == null) return null;
            int f0 = sv.FrameOf(s.SrcIn), f1 = sv.FrameOf(s.SrcOut);
            int n = f1 - f0 + 1; if (n < 3) return new List<RepairRange>();
            var pf = new PoseFrames { Rig = sv.Rig, Hips = new Vec3[n], Local = new Quat[n][], Scale = new float[n], Time = new double[n], Fps = sv.Fps };
            for (int k = 0; k < n; k++) { pf.Hips[k] = sv.Clip.Hips[f0 + k]; pf.Local[k] = sv.Local[f0 + k]; pf.Scale[k] = sv.Clip.Scale; pf.Time[k] = (f0 + k) / (double)sv.Fps; }
            return PoseRepair.FindGlitches(pf);
        }
    }
}
