using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VRMR
{
    // =====================================================================================================  scene model
    //
    // A scene is a non-destructive edit of takes: it references .vrmr files (never modified) and holds everything
    // editable: actors, NLA tracks, strips (a stretch of a take or of a keyframe action), transitions between strips,
    // keyframe actions, markers. Saved as JSON (.vrmscene). See SceneEval for how a scene becomes a pose.

    public enum StripKind { Take = 0, Action = 1 }
    public enum BlendMode { Replace = 0, Add = 1 }
    public enum TransitionCurve { Smooth = 0, Linear = 1 }
    /// <summary>How the strip after a transition is placed: not moved, its centre of mass (horizontal) and facing onto the
    /// strip before it, its hips, or its feet.</summary>
    public enum TransitionAlign { None = 0, Com = 1, Hips = 2, Feet = 3 }

    /// <summary>Body parts for masks (which bones / channels a strip or layer drives).</summary>
    [Flags]
    public enum BodyPart
    {
        None = 0,
        Root = 1,          // hips position (where the body is)
        Hips = 2,          // hips rotation
        Spine = 4,         // spine, chest, upper chest
        Head = 8,          // neck, head, eyes, jaw
        LeftArm = 16,      // shoulder, upper arm, forearm, hand
        RightArm = 32,
        LeftLeg = 64,      // thigh, shin, foot, toes
        RightLeg = 128,
        LeftFingers = 256,
        RightFingers = 512,
        Face = 1024,       // face shapes
        Params = 2048,     // raw avatar parameters
        All = 4095,
    }

    public static class BodyMask
    {
        public static readonly BodyPart UpperBody = BodyPart.Spine | BodyPart.Head | BodyPart.LeftArm | BodyPart.RightArm | BodyPart.LeftFingers | BodyPart.RightFingers | BodyPart.Face | BodyPart.Params;
        public static readonly BodyPart LowerBody = BodyPart.Root | BodyPart.Hips | BodyPart.LeftLeg | BodyPart.RightLeg;
        public static readonly BodyPart Arms = BodyPart.LeftArm | BodyPart.RightArm | BodyPart.LeftFingers | BodyPart.RightFingers;
        public static readonly BodyPart Legs = BodyPart.LeftLeg | BodyPart.RightLeg;
        public static readonly BodyPart HeadFace = BodyPart.Head | BodyPart.Face;
        public static readonly BodyPart Body = BodyPart.All & ~(BodyPart.Face | BodyPart.Params);

        /// <summary>Named presets, in menu order.</summary>
        public static readonly (string Name, BodyPart Mask)[] Presets =
        {
            ("Full body", BodyPart.All), ("Body (no face)", Body), ("Upper body", UpperBody), ("Lower body", LowerBody),
            ("Head + face", HeadFace), ("Arms + hands", Arms), ("Legs", Legs),
            ("Left arm", BodyPart.LeftArm | BodyPart.LeftFingers), ("Right arm", BodyPart.RightArm | BodyPart.RightFingers),
            ("Left leg", BodyPart.LeftLeg), ("Right leg", BodyPart.RightLeg), ("Fingers", BodyPart.LeftFingers | BodyPart.RightFingers),
            ("Face", BodyPart.Face),
        };

        static readonly BodyPart[] _ofBone = Build();
        static BodyPart[] Build()
        {
            var p = new BodyPart[HumanBones.Count];
            for (int b = 0; b < HumanBones.Count; b++)
            {
                string n = HumanBones.Names[b];
                bool left = n.StartsWith("Left", StringComparison.Ordinal);
                if (b == HumanBones.Hips) p[b] = BodyPart.Hips;
                else if (b == HumanBones.Spine || b == HumanBones.Chest || b == HumanBones.UpperChest) p[b] = BodyPart.Spine;
                else if (b == HumanBones.Neck || b == HumanBones.Head || b == HumanBones.LeftEye || b == HumanBones.RightEye || b == HumanBones.Jaw) p[b] = BodyPart.Head;
                else if (HumanBones.IsFinger(b)) p[b] = left ? BodyPart.LeftFingers : BodyPart.RightFingers;
                else if (n.Contains("Shoulder") || n.Contains("Arm") || n.EndsWith("Hand")) p[b] = left ? BodyPart.LeftArm : BodyPart.RightArm;
                else p[b] = left ? BodyPart.LeftLeg : BodyPart.RightLeg; // legs, feet, toes
            }
            return p;
        }

        /// <summary>The body part a humanoid bone belongs to.</summary>
        public static BodyPart PartOf(int bone) => _ofBone[bone];
        public static bool Has(BodyPart mask, BodyPart part) => (mask & part) != 0;

        public static string Describe(BodyPart m)
        {
            foreach (var p in Presets) if (p.Mask == m) return p.Name;
            if (m == BodyPart.None) return "Nothing";
            return string.Join(" + ", Enum.GetValues(typeof(BodyPart)).Cast<BodyPart>().Where(v => v != BodyPart.None && v != BodyPart.All && (m & v) == v).Select(v => v.ToString()));
        }

        public static List<object> ToJson(BodyPart m)
        {
            if (m == BodyPart.All) return new List<object> { "All" };
            return Enum.GetValues(typeof(BodyPart)).Cast<BodyPart>().Where(v => v != BodyPart.None && v != BodyPart.All && (m & v) == v).Select(v => (object)v.ToString()).ToList();
        }

        public static BodyPart FromJson(object o, BodyPart def = BodyPart.All)
        {
            if (!(o is List<object> l)) return def;
            var m = BodyPart.None;
            foreach (var x in l) if (x is string s && Enum.TryParse(s, out BodyPart p)) m |= p;
            return m;
        }
    }

    /// <summary>
    /// Pose tracks blend solved poses. Input tracks change what the IK solve is fed for the take strips below them
    /// (devices: headset, controllers, trackers, and their influence; plus face, fingers, parameters), then those takes
    /// are solved again: re-recorded body parts, moved trackers, trackers switched on/off over time.
    /// </summary>
    public enum TrackKind { Pose = 0, Input = 1 }

    /// <summary>Inputs an input strip drives: each device (by body role), the finger tracking per hand, face tracking, other parameters.</summary>
    [Flags]
    public enum InputPart
    {
        None = 0,
        Head = 1,          // headset (HMD)
        LeftHand = 2,      // controllers
        RightHand = 4,
        Hips = 8,
        Chest = 16,
        LeftFoot = 32,
        RightFoot = 64,
        LeftKnee = 128,
        RightKnee = 256,
        LeftElbow = 512,
        RightElbow = 1024,
        LeftFingers = 2048,  // SteamVR finger tracking
        RightFingers = 4096,
        Face = 8192,         // face tracking (FT parameters / face shapes)
        Params = 16384,      // other avatar parameters
        All = 32767,
    }

    /// <summary>How a re-recorded take's devices are placed onto the take they replace parts of.</summary>
    public enum InputAlign
    {
        /// <summary>As recorded (the same spot in the playspace).</summary>
        None = 0,
        /// <summary>Its headset matched to the base take's (position on the floor and facing) at the strip's start.</summary>
        Head = 1,
    }

    public static class InputMask
    {
        public static readonly InputPart Devices = InputPart.Head | InputPart.LeftHand | InputPart.RightHand | InputPart.Hips | InputPart.Chest |
            InputPart.LeftFoot | InputPart.RightFoot | InputPart.LeftKnee | InputPart.RightKnee | InputPart.LeftElbow | InputPart.RightElbow;
        public static readonly InputPart Trackers = Devices & ~(InputPart.Head | InputPart.LeftHand | InputPart.RightHand);
        public static readonly InputPart Fingers = InputPart.LeftFingers | InputPart.RightFingers;

        public static readonly (string Name, InputPart Mask)[] Presets =
        {
            ("Everything", InputPart.All), ("All devices", Devices),
            ("Upper body (headset, controllers, chest, elbows)", InputPart.Head | InputPart.LeftHand | InputPart.RightHand | InputPart.Chest | InputPart.LeftElbow | InputPart.RightElbow),
            ("Lower body (hips, knees, feet)", InputPart.Hips | InputPart.LeftKnee | InputPart.RightKnee | InputPart.LeftFoot | InputPart.RightFoot),
            ("Head (headset)", InputPart.Head), ("Hands (controllers)", InputPart.LeftHand | InputPart.RightHand),
            ("Left arm (controller, elbow)", InputPart.LeftHand | InputPart.LeftElbow), ("Right arm (controller, elbow)", InputPart.RightHand | InputPart.RightElbow),
            ("Legs (knees, feet)", InputPart.LeftKnee | InputPart.RightKnee | InputPart.LeftFoot | InputPart.RightFoot),
            ("All body trackers", Trackers),
            ("Finger tracking", Fingers), ("Face tracking", InputPart.Face), ("Face + fingers", InputPart.Face | Fingers),
        };

        /// <summary>The device role of a device input part (null for fingers / face / params).</summary>
        public static BodyRole? RoleOf(InputPart p)
        {
            if ((p & Devices) == 0 || (p & (p - 1)) != 0) return null;
            return (BodyRole)Enum.Parse(typeof(BodyRole), p.ToString());
        }

        public static InputPart PartOf(BodyRole r) => (InputPart)Enum.Parse(typeof(InputPart), r.ToString());

        public static IEnumerable<BodyRole> Roles(InputPart m)
        {
            foreach (BodyRole r in Enum.GetValues(typeof(BodyRole))) if ((m & PartOf(r)) != 0) yield return r;
        }

        public static string Describe(InputPart m)
        {
            foreach (var p in Presets) if (p.Mask == m) return p.Name.Split('(')[0].Trim();
            if (m == InputPart.None) return "Nothing";
            return string.Join(" + ", Enum.GetValues(typeof(InputPart)).Cast<InputPart>().Where(v => v != InputPart.None && v != InputPart.All && (m & v) == v).Select(v => v.ToString()));
        }

        public static List<object> ToJson(InputPart m)
        {
            if (m == InputPart.All) return new List<object> { "All" };
            return Enum.GetValues(typeof(InputPart)).Cast<InputPart>().Where(v => v != InputPart.None && v != InputPart.All && (m & v) == v).Select(v => (object)v.ToString()).ToList();
        }

        public static InputPart FromJson(object o, InputPart def = InputPart.All)
        {
            if (!(o is List<object> l)) return def;
            var m = InputPart.None;
            foreach (var x in l) if (x is string s && Enum.TryParse(s, out InputPart p)) m |= p;
            return m;
        }
    }

    /// <summary>A take used by the scene. The .vrmr file is only read, never written.</summary>
    public sealed class TakeRef
    {
        public string Id = "", Path = "", Name = "", Created = "";
        public double Duration;

        public Dictionary<string, object> ToJson() => new Dictionary<string, object> { ["id"] = Id, ["path"] = Path, ["name"] = Name, ["created"] = Created, ["duration"] = Duration };
        public static TakeRef FromJson(Dictionary<string, object> d) => new TakeRef { Id = d.GetStr("id", ""), Path = d.GetStr("path", ""), Name = d.GetStr("name", ""), Created = d.GetStr("created", ""), Duration = d.GetNum("duration") };
    }

    /// <summary>A strip modifier (foot lock, smoothing, ...): a type and its parameters, evaluated by SceneEval.Modifiers.</summary>
    public sealed class StripModifier
    {
        public string Type = "";
        public bool Enabled = true;
        public Dictionary<string, object> Params = new Dictionary<string, object>();
        public Dictionary<string, object> ToJson() => new Dictionary<string, object> { ["type"] = Type, ["enabled"] = Enabled, ["params"] = Params };
        public static StripModifier FromJson(Dictionary<string, object> d) => new StripModifier { Type = d.GetStr("type", ""), Enabled = d.GetBool("enabled", true), Params = d.GetObj("params") ?? new Dictionary<string, object>() };
        public StripModifier Clone() => FromJson(Json.ParseObject(Json.Serialize(ToJson())));
    }

    /// <summary>
    /// A stretch of a take (or of a keyframe action) placed on a track: plays source [SrcIn, SrcOut] from scene time Start
    /// at Speed. Its root can be moved (Offset: x/y/z and a turn about the vertical axis through the origin).
    /// </summary>
    public sealed class Strip
    {
        public string Id = "", Name = "";
        public StripKind Kind = StripKind.Take;
        /// <summary>TakeRef.Id or Action.Id.</summary>
        public string Source = "";
        public double Start, SrcIn, SrcOut;
        public double Speed = 1;
        public BlendMode Blend = BlendMode.Replace;
        public float Influence = 1;
        /// <summary>Ramp in / out (seconds) of the influence at the strip's ends (inside the strip).</summary>
        public double BlendIn, BlendOut;
        public BodyPart Mask = BodyPart.All;
        public float OffsetX, OffsetY, OffsetZ, OffsetYaw;
        public bool Mute;
        public List<StripModifier> Modifiers = new List<StripModifier>();
        /// <summary>On an input track: which inputs this strip drives (devices, fingers, face, params).</summary>
        public InputPart Inputs = InputPart.All;
        /// <summary>On an input track (a take): how its devices are placed onto the take below.</summary>
        public InputAlign InputAlign = InputAlign.Head;
        /// <summary>On an input track: per-device weight (role name -> 0..1, missing = 1), multiplied onto the strip's weight for that device.</summary>
        public Dictionary<string, float> InputWeights = new Dictionary<string, float>();
        public float InputWeight(BodyRole r) => InputWeights.TryGetValue(r.ToString(), out var w) ? w : 1f;

        public double Length => (SrcOut - SrcIn) / Math.Max(1e-6, Speed);
        public double End => Start + Length;
        /// <summary>Source time at scene time t (not clamped).</summary>
        public double ToSource(double t) => SrcIn + (t - Start) * Speed;
        public double ToScene(double src) => Start + (src - SrcIn) / Math.Max(1e-6, Speed);
        public Pose Offset => new Pose(new Vec3(OffsetX, OffsetY, OffsetZ), Quat.YawRotation(OffsetYaw));
        public void SetOffset(Pose p) { OffsetX = p.pos.x; OffsetY = p.pos.y; OffsetZ = p.pos.z; OffsetYaw = p.rot.Yaw; }

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["id"] = Id, ["name"] = Name, ["kind"] = Kind == StripKind.Take ? "take" : "action", ["source"] = Source,
            ["start"] = Start, ["srcIn"] = SrcIn, ["srcOut"] = SrcOut, ["speed"] = Speed,
            ["blend"] = Blend == BlendMode.Add ? "add" : "replace", ["influence"] = (double)Influence, ["blendIn"] = BlendIn, ["blendOut"] = BlendOut,
            ["mask"] = BodyMask.ToJson(Mask), ["offset"] = new List<object> { (double)OffsetX, (double)OffsetY, (double)OffsetZ, (double)OffsetYaw },
            ["mute"] = Mute, ["modifiers"] = Modifiers.Select(m => (object)m.ToJson()).ToList(),
            ["inputs"] = InputMask.ToJson(Inputs), ["inputAlign"] = InputAlign.ToString().ToLowerInvariant(),
        }.WithIf(InputWeights.Count > 0, "inputWeights", () => InputWeights.ToDictionary(k => k.Key, k => (object)(double)k.Value));

        public static Strip FromJson(Dictionary<string, object> d)
        {
            var s = new Strip
            {
                Id = d.GetStr("id", ""), Name = d.GetStr("name", ""), Kind = d.GetStr("kind", "take") == "action" ? StripKind.Action : StripKind.Take,
                Source = d.GetStr("source", ""), Start = d.GetNum("start"), SrcIn = d.GetNum("srcIn"), SrcOut = d.GetNum("srcOut"), Speed = d.GetNum("speed", 1),
                Blend = d.GetStr("blend", "replace") == "add" ? BlendMode.Add : BlendMode.Replace, Influence = d.GetF("influence", 1),
                BlendIn = d.GetNum("blendIn"), BlendOut = d.GetNum("blendOut"), Mask = BodyMask.FromJson(d.ContainsKey("mask") ? d["mask"] : null), Mute = d.GetBool("mute"),
            };
            if (d.GetList("offset") is List<object> o && o.Count >= 4) { s.OffsetX = Convert.ToSingle(o[0]); s.OffsetY = Convert.ToSingle(o[1]); s.OffsetZ = Convert.ToSingle(o[2]); s.OffsetYaw = Convert.ToSingle(o[3]); }
            foreach (var m in d.GetList("modifiers") ?? new List<object>()) if (m is Dictionary<string, object> md) s.Modifiers.Add(StripModifier.FromJson(md));
            if (s.Speed <= 1e-6) s.Speed = 1;
            s.Inputs = InputMask.FromJson(d.ContainsKey("inputs") ? d["inputs"] : null);
            if (Enum.TryParse(d.GetStr("inputAlign", "head"), true, out InputAlign ia)) s.InputAlign = ia;
            var iw = d.GetObj("inputWeights");
            if (iw != null) foreach (var kv in iw) s.InputWeights[kv.Key] = (float)MathX.Clamp(Convert.ToDouble(kv.Value), 0, 1);
            return s;
        }

        public Strip Clone() => FromJson(Json.ParseObject(Json.Serialize(ToJson())));
    }

    /// <summary>
    /// A crossfade between two adjacent strips of one track (From ends where To starts), centred on the cut: From keeps
    /// playing Duration/2 past its end and To starts Duration/2 early, using the takes' data beyond the cut ("handles").
    /// </summary>
    public sealed class Transition
    {
        public string Id = "", From = "", To = "";
        public double Duration = 0.5;
        public TransitionCurve Curve = TransitionCurve.Smooth;
        public TransitionAlign Align = TransitionAlign.Com;
        /// <summary>How well the poses match across the cut, 0..1 (1 = seamless); negative = not computed yet.</summary>
        public double Score = -1;
        /// <summary>The turn also follows the centre of mass's travel direction (walking into walking keeps its path).</summary>
        public bool MatchVelocity;

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["id"] = Id, ["from"] = From, ["to"] = To, ["duration"] = Duration, ["curve"] = Curve.ToString().ToLowerInvariant(),
            ["align"] = Align.ToString().ToLowerInvariant(), ["score"] = Score, ["matchVelocity"] = MatchVelocity,
        };
        public static Transition FromJson(Dictionary<string, object> d)
        {
            var t = new Transition { Id = d.GetStr("id", ""), From = d.GetStr("from", ""), To = d.GetStr("to", ""), Duration = d.GetNum("duration", 0.5), Score = d.GetNum("score", -1), MatchVelocity = d.GetBool("matchVelocity") };
            if (Enum.TryParse(d.GetStr("curve", "smooth"), true, out TransitionCurve c)) t.Curve = c;
            if (Enum.TryParse(d.GetStr("align", "com"), true, out TransitionAlign a)) t.Align = a;
            return t;
        }

        /// <summary>Crossfade weight of To at u in 0..1.</summary>
        public float Weight(double u) { float x = (float)MathX.Clamp(u, 0, 1); return Curve == TransitionCurve.Linear ? x : MathX.SmoothStep(x); }
    }

    /// <summary>One NLA track: strips that don't overlap (except across a transition), evaluated as one layer.</summary>
    public sealed class NlaTrack
    {
        public string Id = "", Name = "Track";
        public bool Mute, Solo, Lock;
        public TrackKind Kind = TrackKind.Pose;
        public List<Strip> Strips = new List<Strip>();
        public List<Transition> Transitions = new List<Transition>();

        public void SortStrips() => Strips.Sort((a, b) => a.Start.CompareTo(b.Start));
        public Transition TransitionInto(string stripId) => Transitions.FirstOrDefault(t => t.To == stripId);
        public Transition TransitionOutOf(string stripId) => Transitions.FirstOrDefault(t => t.From == stripId);

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["id"] = Id, ["name"] = Name, ["kind"] = Kind == TrackKind.Input ? "input" : "pose", ["mute"] = Mute, ["solo"] = Solo, ["lock"] = Lock,
            ["strips"] = Strips.Select(s => (object)s.ToJson()).ToList(), ["transitions"] = Transitions.Select(t => (object)t.ToJson()).ToList(),
        };
        public static NlaTrack FromJson(Dictionary<string, object> d)
        {
            var t = new NlaTrack { Id = d.GetStr("id", ""), Name = d.GetStr("name", "Track"), Mute = d.GetBool("mute"), Solo = d.GetBool("solo"), Lock = d.GetBool("lock"), Kind = d.GetStr("kind", "pose") == "input" ? TrackKind.Input : TrackKind.Pose };
            foreach (var s in d.GetList("strips") ?? new List<object>()) if (s is Dictionary<string, object> sd) t.Strips.Add(Strip.FromJson(sd));
            foreach (var s in d.GetList("transitions") ?? new List<object>()) if (s is Dictionary<string, object> sd) t.Transitions.Add(Transition.FromJson(sd));
            t.SortStrips();
            return t;
        }
    }

    /// <summary>A performer: an avatar and its NLA tracks (index 0 = bottom = evaluated first).</summary>
    public sealed class Actor
    {
        public string Id = "", Name = "Actor";
        /// <summary>Avatar library entry id; "" = the avatar of the actor's first take.</summary>
        public string Avatar = "";
        public bool Hidden;
        public List<NlaTrack> Tracks = new List<NlaTrack>();
        /// <summary>Applied to the actor's evaluated output after all tracks (SceneModifiers.Actor), e.g. a final foot lock.</summary>
        public List<StripModifier> Modifiers = new List<StripModifier>();

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["id"] = Id, ["name"] = Name, ["avatar"] = Avatar, ["hidden"] = Hidden, ["tracks"] = Tracks.Select(t => (object)t.ToJson()).ToList(),
            ["modifiers"] = Modifiers.Select(m => (object)m.ToJson()).ToList(),
        };
        public static Actor FromJson(Dictionary<string, object> d)
        {
            var a = new Actor { Id = d.GetStr("id", ""), Name = d.GetStr("name", "Actor"), Avatar = d.GetStr("avatar", ""), Hidden = d.GetBool("hidden") };
            foreach (var t in d.GetList("tracks") ?? new List<object>()) if (t is Dictionary<string, object> td) a.Tracks.Add(NlaTrack.FromJson(td));
            foreach (var m in d.GetList("modifiers") ?? new List<object>()) if (m is Dictionary<string, object> md) a.Modifiers.Add(StripModifier.FromJson(md));
            return a;
        }
    }

    public sealed class SceneMarker
    {
        public double T; public string Name = "";
        public Dictionary<string, object> ToJson() => new Dictionary<string, object> { ["t"] = T, ["name"] = Name };
        public static SceneMarker FromJson(Dictionary<string, object> d) => new SceneMarker { T = d.GetNum("t"), Name = d.GetStr("name", "") };
    }

    public sealed class Scene
    {
        public const int FormatVersion = 1;
        public const string Extension = ".vrmscene";
        public string Name = "Scene";
        public float Fps = 60;
        /// <summary>Scene range (seconds); End &lt; 0 = up to the last strip.</summary>
        public double Start = 0, End = -1;
        public List<TakeRef> Takes = new List<TakeRef>();
        public List<Actor> Actors = new List<Actor>();
        public List<KeyAction> Actions = new List<KeyAction>();
        public List<SceneMarker> Markers = new List<SceneMarker>();
        /// <summary>Reserved for a future camera system (kept verbatim on load/save; nothing evaluates it yet).</summary>
        public List<object> Cameras = new List<object>();
        /// <summary>Top-level keys this version doesn't know, kept so newer files survive a round trip.</summary>
        public Dictionary<string, object> Extra = new Dictionary<string, object>();

        /// <summary>Where the scene was loaded from / saved to (not serialized).</summary>
        public string FilePath;

        // ---------------------------------------------------------------- lookups

        public TakeRef Take(string id) => Takes.FirstOrDefault(t => t.Id == id);
        public KeyAction Action(string id) => Actions.FirstOrDefault(a => a.Id == id);
        public Actor ActorById(string id) => Actors.FirstOrDefault(a => a.Id == id);

        public IEnumerable<(Actor Actor, NlaTrack Track, Strip Strip)> AllStrips()
        {
            foreach (var a in Actors) foreach (var t in a.Tracks) foreach (var s in t.Strips) yield return (a, t, s);
        }

        public (Actor Actor, NlaTrack Track, Strip Strip) FindStrip(string id)
        {
            foreach (var x in AllStrips()) if (x.Strip.Id == id) return x;
            return (null, null, null);
        }

        public (Actor Actor, NlaTrack Track) FindTrack(string id)
        {
            foreach (var a in Actors) foreach (var t in a.Tracks) if (t.Id == id) return (a, t);
            return (null, null);
        }

        public (Actor Actor, NlaTrack Track, Transition Transition) FindTransition(string id)
        {
            foreach (var a in Actors) foreach (var t in a.Tracks) foreach (var x in t.Transitions) if (x.Id == id) return (a, t, x);
            return (null, null, null);
        }

        /// <summary>End of the content (last strip end, at least 1 s).</summary>
        public double ContentEnd => Math.Max(1, AllStrips().Select(x => x.Strip.End).DefaultIfEmpty(0).Max());
        public double RangeEnd => End > Start ? End : ContentEnd;

        public static string NewId(string prefix) => prefix + Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>Adds (or finds) a take reference for a .vrmr file.</summary>
        public TakeRef AddTake(string path, Recording rec)
        {
            string full = System.IO.Path.GetFullPath(path);
            foreach (var t in Takes) if (string.Equals(ResolveTakePath(t), full, StringComparison.OrdinalIgnoreCase)) return t;
            var tr = new TakeRef { Id = NewId("t"), Path = full, Name = rec?.Name ?? System.IO.Path.GetFileNameWithoutExtension(path), Created = rec?.CreatedUtc.ToString("o") ?? "", Duration = rec?.Duration ?? 0 };
            Takes.Add(tr);
            return tr;
        }

        /// <summary>Absolute path of a take (relative paths are relative to the scene file's folder).</summary>
        public string ResolveTakePath(TakeRef t)
        {
            if (string.IsNullOrEmpty(t.Path)) return "";
            if (System.IO.Path.IsPathRooted(t.Path)) return t.Path;
            string dir = FilePath != null ? System.IO.Path.GetDirectoryName(FilePath) : Environment.CurrentDirectory;
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, t.Path));
        }

        // ---------------------------------------------------------------- JSON

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object>(Extra)
            {
                ["app"] = "VR Motion Recorder", ["kind"] = "scene", ["formatVersion"] = FormatVersion,
                ["name"] = Name, ["fps"] = (double)Fps, ["start"] = Start, ["end"] = End,
                ["takes"] = Takes.Select(t => (object)TakeToJson(t)).ToList(),
                ["actors"] = Actors.Select(a => (object)a.ToJson()).ToList(),
                ["actions"] = Actions.Select(a => (object)a.ToJson()).ToList(),
                ["markers"] = Markers.Select(m => (object)m.ToJson()).ToList(),
                ["cameras"] = Cameras,
            };
            return d;
        }

        /// <summary>Take paths are stored relative to the scene file when they share a drive (the folder can move).</summary>
        Dictionary<string, object> TakeToJson(TakeRef t)
        {
            var j = t.ToJson();
            if (FilePath != null)
            {
                string abs = ResolveTakePath(t), dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(FilePath));
                string rel = RelativePath(dir, abs);
                if (rel != null) j["path"] = rel;
            }
            return j;
        }

        static readonly HashSet<string> Known = new HashSet<string> { "app", "kind", "formatVersion", "name", "fps", "start", "end", "takes", "actors", "actions", "markers", "cameras" };

        public static Scene FromJson(Dictionary<string, object> d, string filePath = null)
        {
            var s = new Scene { FilePath = filePath, Name = d.GetStr("name", "Scene"), Fps = d.GetF("fps", 60), Start = d.GetNum("start", 0), End = d.GetNum("end", -1) };
            if (s.Fps < 1) s.Fps = 60;
            foreach (var t in d.GetList("takes") ?? new List<object>()) if (t is Dictionary<string, object> td) s.Takes.Add(TakeRef.FromJson(td));
            foreach (var t in s.Takes) t.Path = s.ResolveTakePath(t); // in memory, take paths are always absolute
            foreach (var t in d.GetList("actors") ?? new List<object>()) if (t is Dictionary<string, object> td) s.Actors.Add(Actor.FromJson(td));
            foreach (var t in d.GetList("actions") ?? new List<object>()) if (t is Dictionary<string, object> td) s.Actions.Add(KeyAction.FromJson(td));
            foreach (var t in d.GetList("markers") ?? new List<object>()) if (t is Dictionary<string, object> td) s.Markers.Add(SceneMarker.FromJson(td));
            s.Cameras = d.GetList("cameras") ?? new List<object>();
            foreach (var kv in d) if (!Known.Contains(kv.Key)) s.Extra[kv.Key] = kv.Value;
            return s;
        }

        public static Scene Load(string path)
        {
            var d = Json.ParseObject(File.ReadAllText(path));
            if (d == null || d.GetStr("kind", "") != "scene") throw new InvalidDataException("Not a VR Motion Recorder scene: " + path);
            return FromJson(d, System.IO.Path.GetFullPath(path));
        }

        /// <summary>Writes the scene (to a temporary file first, then replaces the old one).</summary>
        public void Save(string path)
        {
            path = System.IO.Path.GetFullPath(path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            FilePath = path;
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, Json.Serialize(ToJson(), true));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }

        public Scene Clone() => FromJson(Json.ParseObject(Json.Serialize(this.ToJsonAbsolute())), FilePath);

        public static string RelativePath(string fromDir, string toFile)
        {
            try
            {
                if (!string.Equals(System.IO.Path.GetPathRoot(fromDir), System.IO.Path.GetPathRoot(toFile), StringComparison.OrdinalIgnoreCase)) return null;
                var a = new Uri(fromDir.TrimEnd('\\', '/') + "\\");
                var b = new Uri(toFile);
                return Uri.UnescapeDataString(a.MakeRelativeUri(b).ToString()).Replace('/', '\\');
            }
            catch { return null; }
        }

        /// <summary>The default folder for scenes: Documents\VR Motion Recorder\Scenes (next to the recordings).</summary>
        public static string DefaultDir(string recordingsDir) => System.IO.Path.Combine(recordingsDir, "Scenes");

        /// <summary>A new scene with one actor and one empty track.</summary>
        public static Scene CreateNew(string name)
        {
            var s = new Scene { Name = name };
            var a = new Actor { Id = NewId("a"), Name = "Actor 1" };
            a.Tracks.Add(new NlaTrack { Id = NewId("k"), Name = "Base" });
            s.Actors.Add(a);
            return s;
        }
    }

    /// <summary>
    /// Undo / redo for a scene: every edit first records the scene as JSON (Checkpoint). Undo swaps the whole scene, so
    /// UI code keeps ids (not object references) of its selection.
    /// </summary>
    public sealed class SceneHistory
    {
        readonly List<(string Label, string Json)> _undo = new List<(string, string)>();
        readonly List<(string Label, string Json)> _redo = new List<(string, string)>();
        public int Limit = 200;
        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public string UndoLabel => _undo.Count > 0 ? _undo[_undo.Count - 1].Label : "";
        public string RedoLabel => _redo.Count > 0 ? _redo[_redo.Count - 1].Label : "";

        static string Snap(Scene s) => Json.Serialize(s.ToJsonAbsolute());

        /// <summary>Call before changing the scene.</summary>
        public void Checkpoint(Scene s, string label)
        {
            _undo.Add((label, Snap(s)));
            if (_undo.Count > Limit) _undo.RemoveAt(0);
            _redo.Clear();
        }

        public Scene Undo(Scene current)
        {
            if (_undo.Count == 0) return current;
            var e = _undo[_undo.Count - 1]; _undo.RemoveAt(_undo.Count - 1);
            _redo.Add((e.Label, Snap(current)));
            return Restore(e.Json, current.FilePath);
        }

        public Scene Redo(Scene current)
        {
            if (_redo.Count == 0) return current;
            var e = _redo[_redo.Count - 1]; _redo.RemoveAt(_redo.Count - 1);
            _undo.Add((e.Label, Snap(current)));
            return Restore(e.Json, current.FilePath);
        }

        public void Clear() { _undo.Clear(); _redo.Clear(); }

        /// <summary>Forgets the last checkpoint (its edit changed nothing).</summary>
        public void DropLast() { if (_undo.Count > 0) _undo.RemoveAt(_undo.Count - 1); }

        static Scene Restore(string json, string path) => Scene.FromJson(Json.ParseObject(json), path);
    }

    static class SceneJsonExt
    {
        /// <summary>Adds a key only when cond holds (keeps files small and old readers happy).</summary>
        public static Dictionary<string, object> WithIf(this Dictionary<string, object> d, bool cond, string key, Func<object> value)
        {
            if (cond) d[key] = value();
            return d;
        }

        /// <summary>JSON with absolute take paths (for undo snapshots).</summary>
        public static Dictionary<string, object> ToJsonAbsolute(this Scene s)
        {
            string fp = s.FilePath; s.FilePath = null;
            try { return s.ToJson(); } finally { s.FilePath = fp; }
        }
    }
}
