using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VRMR
{
    /// <summary>
    /// Replays an avatar's face-tracking animator (the controllers a face-tracking add-on puts on the avatar, as VRChat
    /// runs them) on recorded parameters: which face shapes the add-on's blend trees set, and how far, for one moment.
    /// It reads Unity's own files (controllers, override controllers, animation clips, the add-on's parameter links)
    /// and evaluates the blend trees as Unity does: 1D, 2D (directional and cartesian, by gradient bands) and Direct,
    /// layer by layer. Only face shapes (blendShape curves) and animated parameters are used; transitions are not
    /// followed: every layer plays its default state.
    /// </summary>
    public sealed class FaceAnimator
    {
        // ------------------------------------------------------------------ the files

        sealed class Clip
        {
            /// <summary>Face shapes it sets: (mesh path, shape) → weight 0..1 (Unity writes 0..100).</summary>
            public readonly Dictionary<(string Path, string Shape), float> Shapes = new Dictionary<(string, string), float>();
            /// <summary>Animator parameters it sets.</summary>
            public readonly Dictionary<string, float> Params = new Dictionary<string, float>(StringComparer.Ordinal);
        }

        sealed class Child { public Motion Motion; public float Threshold, X, Y; public string Direct = ""; }

        sealed class Motion
        {
            public Clip Clip;                      // a clip, or …
            public int Type = -1;                  // … a blend tree: 0 1D, 1 2D simple directional, 2 2D freeform directional, 3 2D freeform cartesian, 4 direct
            public string ParamX = "", ParamY = "";
            public readonly List<Child> Children = new List<Child>();
        }

        sealed class Condition { public int Mode; public string Param = ""; public float Threshold; }

        sealed class Transition
        {
            public State Dest;                     // null with ToDefault: back to the layer's default state (an exit)
            public bool ToDefault;
            public readonly List<Condition> Conditions = new List<Condition>();
        }

        sealed class State { public Motion Motion; public readonly List<Transition> Out = new List<Transition>(); }

        sealed class Layer
        {
            public string Name = ""; public float Weight; public bool Additive;
            public State Default;
            public readonly List<Transition> Any = new List<Transition>();
        }

        /// <summary>One controller as the avatar runs it (an override controller is its base with clips replaced).</summary>
        sealed class Controller { public string Name = ""; public readonly List<Layer> Layers = new List<Layer>(); }

        sealed class Link { public string Target = ""; public float Weight; public bool Absolute; }

        readonly Dictionary<string, string> _texts;               // guid → file text
        readonly Dictionary<string, string> _names;               // guid → file name, for messages
        readonly Dictionary<string, List<UnityYaml.Document>> _docs = new Dictionary<string, List<UnityYaml.Document>>();
        readonly Dictionary<string, Clip> _clips = new Dictionary<string, Clip>();
        readonly List<Controller> _controllers = new List<Controller>();
        readonly Dictionary<string, float> _defaults = new Dictionary<string, float>(StringComparer.Ordinal);
        readonly Dictionary<string, List<Link>> _links = new Dictionary<string, List<Link>>(StringComparer.Ordinal);

        /// <summary>What was read and what was left out, for the log.</summary>
        public readonly List<string> Notes = new List<string>();

        /// <summary>The mesh the face shapes are on (the path most of the add-on's shape curves name), e.g. "Body".</summary>
        public string FacePath { get; private set; } = "";

        /// <summary>The face shapes (on <see cref="FacePath"/>) the animator sets.</summary>
        public IReadOnlyCollection<string> Shapes => _shapes;
        readonly HashSet<string> _shapes = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>True when there is at least one layer that sets face shapes.</summary>
        public bool HasFace => _shapes.Count > 0;

        readonly HashSet<string> _roots;

        FaceAnimator(Dictionary<string, string> texts, Dictionary<string, string> names, IEnumerable<string> roots)
        {
            _texts = texts; _names = names;
            _roots = roots == null ? null : new HashSet<string>(roots);
        }

        /// <summary>
        /// Builds the animator from Unity files by guid (text of each .controller, .overrideController, .anim and .asset;
        /// <paramref name="namesByGuid"/> gives their file names). <paramref name="roots"/> are the controllers and
        /// override controllers the avatar plays; without it every controller is played except those whose trees other
        /// controllers use (libraries), and a controller an override controller replaces is played as the override.
        /// </summary>
        public static FaceAnimator Load(IReadOnlyDictionary<string, string> textsByGuid, IReadOnlyDictionary<string, string> namesByGuid = null, IEnumerable<string> roots = null)
        {
            var a = new FaceAnimator(textsByGuid.ToDictionary(k => k.Key, k => k.Value), namesByGuid?.ToDictionary(k => k.Key, k => k.Value) ?? new Dictionary<string, string>(), roots);
            a.Build();
            return a;
        }

        string NameOf(string guid) => _names.TryGetValue(guid, out var n) ? n : guid;

        List<UnityYaml.Document> Docs(string guid)
        {
            if (_docs.TryGetValue(guid, out var d)) return d;
            d = _texts.TryGetValue(guid, out var text) ? UnityYaml.Parse(text) : new List<UnityYaml.Document>();
            _docs[guid] = d;
            return d;
        }

        UnityYaml.Document Find(string guid, long fileId) => Docs(guid).FirstOrDefault(x => x.FileId == fileId);

        void Build()
        {
            var controllers = new List<string>();
            var overrides = new List<string>();
            foreach (var kv in _texts)
            {
                string t = kv.Value;
                if (t.IndexOf("AnimatorOverrideController:", StringComparison.Ordinal) >= 0) { if (_roots == null || _roots.Contains(kv.Key)) overrides.Add(kv.Key); }
                else if (t.IndexOf("AnimatorController:", StringComparison.Ordinal) >= 0 && t.IndexOf("m_AnimatorLayers:", StringComparison.Ordinal) >= 0) controllers.Add(kv.Key);
                else
                {
                    // an add-on's prefab can hold both: its parameter list with defaults, and its parameter links
                    if (t.IndexOf("m_LinkedParameters:", StringComparison.Ordinal) >= 0) ReadLinks(kv.Key);
                    if (t.IndexOf("nameOrPrefix:", StringComparison.Ordinal) >= 0) ReadPrefabDefaults(kv.Key);
                }
            }
            // a controller whose objects another controller points at is a library of trees
            var used = new HashSet<string>();
            foreach (var g in controllers)
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(_texts[g], @"guid: ([0-9a-f]{32})"))
                    if (m.Groups[1].Value != g && controllers.Contains(m.Groups[1].Value)) used.Add(m.Groups[1].Value);
            // the override controllers stand in for their bases
            var replaced = new Dictionary<string, Dictionary<string, string>>(); // base guid → original clip guid → override clip guid
            var overrideNames = new Dictionary<string, string>();
            foreach (var g in overrides)
            {
                var doc = Docs(g).FirstOrDefault();
                if (doc == null) continue;
                var baseRef = doc.Body.RefOf("m_Controller");
                if (!_texts.ContainsKey(baseRef.Guid)) { Notes.Add($"{NameOf(g)}: its base controller is not among the files (guid {baseRef.Guid})"); continue; }
                var map = new Dictionary<string, string>();
                foreach (var c in doc.Body.Maps("m_Clips"))
                {
                    var o = c.RefOf("m_OriginalClip"); var r = c.RefOf("m_OverrideClip");
                    if (o.Guid.Length > 0 && r.Guid.Length > 0) map[o.Guid] = r.Guid;
                }
                replaced[baseRef.Guid] = map;
                overrideNames[baseRef.Guid] = NameOf(g);
            }
            foreach (var g in controllers)
            {
                if (_roots != null ? !_roots.Contains(g) && !replaced.ContainsKey(g) : used.Contains(g)) continue;
                replaced.TryGetValue(g, out var clipMap);
                var ctl = ReadController(g, clipMap ?? new Dictionary<string, string>());
                if (overrideNames.TryGetValue(g, out var on)) ctl.Name = on + " (over " + ctl.Name + ")";
                _controllers.Add(ctl);
            }
            // the face mesh: the path most shape curves name
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var clip in _clips.Values) if (clip != null) foreach (var k in clip.Shapes.Keys) counts[k.Path] = (counts.TryGetValue(k.Path, out int n) ? n : 0) + 1;
            FacePath = counts.Count == 0 ? "" : counts.OrderByDescending(k => k.Value).First().Key;
            foreach (var clip in _clips.Values) if (clip != null) foreach (var k in clip.Shapes.Keys) if (k.Path == FacePath) _shapes.Add(k.Shape);
            foreach (var c in _controllers)
                Notes.Add($"{c.Name}: {c.Layers.Count(l => l.Weight > 0 && l.Default != null)} layer(s) played");
            if (_prefabDefaults.Count > 0) Notes.Add($"parameter defaults from the add-on's prefabs: {_prefabDefaults.Count}");
            if (_links.Count > 0) Notes.Add($"parameter links: {_links.Sum(l => l.Value.Count)} from {_links.Count} parameters");
        }

        Controller ReadController(string guid, Dictionary<string, string> clipMap)
        {
            var ctl = new Controller();
            var root = Docs(guid).FirstOrDefault(d => d.ClassId == 91);
            if (root == null) return ctl;
            ctl.Name = root.Body.Str("m_Name");
            foreach (var p in root.Body.Maps("m_AnimatorParameters"))
            {
                string name = p.Str("m_Name");
                if (name.Length == 0 || _defaults.ContainsKey(name)) continue;
                int type = (int)p.Num("m_Type");
                _defaults[name] = type == 1 ? p.Num("m_DefaultFloat") : type == 3 ? p.Num("m_DefaultInt") : p.Num("m_DefaultBool");
            }
            foreach (var l in root.Body.Maps("m_AnimatorLayers"))
            {
                var layer = new Layer { Name = l.Str("m_Name"), Weight = l.Num("m_DefaultWeight"), Additive = l.Num("m_BlendingMode") == 1 };
                var states = new Dictionary<long, State>();
                long smId = l.RefOf("m_StateMachine").FileId;
                layer.Default = DefaultOf(guid, smId, states, clipMap);
                // transitions from Any State, of this machine and the machines inside it
                foreach (long sub in Machines(guid, smId, 0))
                {
                    var sm = Find(guid, sub);
                    if (sm == null) continue;
                    foreach (var tr in sm.Body.Maps("m_AnyStateTransitions"))
                    {
                        var t = ReadTransition(guid, ToRef(tr), states, clipMap);
                        if (t != null) layer.Any.Add(t);
                    }
                }
                ctl.Layers.Add(layer);
            }
            return ctl;
        }

        static UnityYaml.Ref ToRef(Dictionary<string, object> flow)
        {
            long.TryParse(flow.Str("fileID"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id);
            return new UnityYaml.Ref(id, flow.Str("guid"));
        }

        IEnumerable<long> Machines(string guid, long smId, int depth)
        {
            if (smId == 0 || depth > 16) yield break;
            yield return smId;
            var sm = Find(guid, smId);
            if (sm == null) yield break;
            foreach (var c in sm.Body.Maps("m_ChildStateMachines"))
                foreach (long sub in Machines(guid, c.RefOf("m_StateMachine").FileId, depth + 1)) yield return sub;
        }

        State DefaultOf(string guid, long smId, Dictionary<long, State> states, Dictionary<string, string> clipMap)
        {
            var sm = Find(guid, smId);
            return sm == null ? null : StateOf(guid, sm.Body.RefOf("m_DefaultState").FileId, states, clipMap);
        }

        State StateOf(string guid, long id, Dictionary<long, State> states, Dictionary<string, string> clipMap)
        {
            if (id == 0) return null;
            if (states.TryGetValue(id, out var s)) return s;
            var doc = Find(guid, id);
            if (doc == null || doc.ClassId != 1102) return null;
            s = new State();
            states[id] = s; // before the transitions: they may lead back here
            s.Motion = ReadMotion(guid, doc.Body.RefOf("m_Motion"), clipMap, 0);
            foreach (var tr in doc.Body.Maps("m_Transitions"))
            {
                var t = ReadTransition(guid, ToRef(tr), states, clipMap);
                if (t != null) s.Out.Add(t);
            }
            return s;
        }

        Transition ReadTransition(string guid, UnityYaml.Ref r, Dictionary<long, State> states, Dictionary<string, string> clipMap)
        {
            var doc = Find(guid, r.FileId);
            if (doc == null) return null;
            var t = new Transition();
            foreach (var c in doc.Body.Maps("m_Conditions"))
                t.Conditions.Add(new Condition { Mode = (int)c.Num("m_ConditionMode"), Param = c.Str("m_ConditionEvent"), Threshold = c.Num("m_EventTreshold") });
            long dst = doc.Body.RefOf("m_DstState").FileId, dstMachine = doc.Body.RefOf("m_DstStateMachine").FileId;
            if (dst != 0) t.Dest = StateOf(guid, dst, states, clipMap);
            else if (dstMachine != 0) t.Dest = DefaultOf(guid, dstMachine, states, clipMap);
            t.ToDefault = t.Dest == null;
            return t;
        }

        /// <summary>
        /// The state a layer is in for these parameter values: from its default state, the transitions whose
        /// conditions hold are followed (from Any State first), as the animator would have settled after a moment. A
        /// transition without conditions (one that waits for the end of its state) is taken too.
        /// </summary>
        static State Settle(Layer layer, Func<string, float> get)
        {
            var s = layer.Default;
            var seen = new HashSet<State>();
            for (int k = 0; k < 16 && s != null && seen.Add(s); k++)
            {
                Transition take = null;
                foreach (var t in layer.Any) if (t.Dest != s && t.Conditions.Count > 0 && Holds(t, get)) { take = t; break; }
                if (take == null) foreach (var t in s.Out) if (Holds(t, get)) { take = t; break; }
                if (take == null) break;
                s = take.ToDefault ? layer.Default : take.Dest;
            }
            return s;
        }

        static bool Holds(Transition t, Func<string, float> get)
        {
            foreach (var c in t.Conditions)
            {
                float v = get(c.Param);
                bool ok = c.Mode switch
                {
                    1 => v != 0f,                            // If (a bool is true)
                    2 => v == 0f,                            // IfNot
                    3 => v > c.Threshold,                    // Greater
                    4 => v < c.Threshold,                    // Less
                    6 => Math.Abs(v - c.Threshold) < 1e-4f,  // Equals
                    7 => Math.Abs(v - c.Threshold) >= 1e-4f, // NotEqual
                    _ => true,
                };
                if (!ok) return false;
            }
            return true;
        }

        Motion ReadMotion(string ownerGuid, UnityYaml.Ref r, Dictionary<string, string> clipMap, int depth)
        {
            if (r.IsNull || depth > 32) return null;
            string guid = r.Guid.Length > 0 ? r.Guid : ownerGuid;
            if (r.Guid.Length > 0 && r.FileId == 7400000) // an animation clip asset
            {
                if (clipMap.TryGetValue(guid, out var over)) guid = over;
                var clip = ClipOf(guid);
                return clip == null ? null : new Motion { Clip = clip };
            }
            var doc = Find(guid, r.FileId);
            if (doc == null || doc.ClassId != 206) return null;
            var m = new Motion { Type = (int)doc.Body.Num("m_BlendType"), ParamX = doc.Body.Str("m_BlendParameter"), ParamY = doc.Body.Str("m_BlendParameterY") };
            foreach (var c in doc.Body.Maps("m_Childs"))
            {
                var (x, y) = c.Vec2("m_Position");
                m.Children.Add(new Child { Motion = ReadMotion(guid, c.RefOf("m_Motion"), clipMap, depth + 1), Threshold = c.Num("m_Threshold"), X = x, Y = y, Direct = c.Str("m_DirectBlendParameter") });
            }
            return m;
        }

        Clip ClipOf(string guid)
        {
            if (_clips.TryGetValue(guid, out var c)) return c;
            var doc = Docs(guid).FirstOrDefault(d => d.ClassId == 74);
            if (doc == null) { _clips[guid] = null; return null; }
            c = new Clip();
            foreach (var fc in doc.Body.Maps("m_FloatCurves"))
            {
                string attr = fc.Str("attribute"), path = fc.Str("path");
                var keys = fc.Map("curve").Maps("m_Curve");
                if (keys.Count == 0) continue;
                float v = keys[0].Num("value");
                if (attr.StartsWith("blendShape.", StringComparison.Ordinal)) c.Shapes[(path, attr.Substring(11))] = v / 100f;
                else if (path.Length == 0 && fc.Num("classID") == 95) c.Params[attr] = v; // an animated animator parameter
            }
            _clips[guid] = c;
            return c;
        }

        /// <summary>
        /// Parameter defaults an add-on's prefabs declare (Modular Avatar's parameter lists): what the avatar starts
        /// with in VRChat when a recording does not say (a menu toggle that is on, a strength setting).
        /// </summary>
        void ReadPrefabDefaults(string guid)
        {
            foreach (var doc in Docs(guid))
                foreach (var p in doc.Body.Maps("parameters"))
                {
                    string name = p.Str("nameOrPrefix");
                    if (name.Length == 0 || _prefabDefaults.ContainsKey(name)) continue;
                    _prefabDefaults[name] = p.Num("defaultValue");
                }
        }

        readonly Dictionary<string, float> _prefabDefaults = new Dictionary<string, float>(StringComparer.Ordinal);

        void ReadLinks(string guid)
        {
            foreach (var doc in Docs(guid))
                foreach (var p in doc.Body.Maps("m_Parameters"))
                {
                    var links = p.Maps("m_LinkedParameters");
                    if (links.Count == 0) continue;
                    string src = p.Str("m_ParameterName");
                    if (!_links.TryGetValue(src, out var list)) _links[src] = list = new List<Link>();
                    foreach (var l in links) list.Add(new Link { Target = l.Str("m_ParameterName"), Weight = l.Num("m_Weight", 1), Absolute = l.Num("m_Absolute") != 0 });
                }
        }

        // ------------------------------------------------------------------ evaluation

        /// <summary>
        /// The face shapes the animator sets for one moment, by shape name on <see cref="FacePath"/>, as weights (1 =
        /// Unity's 100; add-ons drive some below 0). <paramref name="parameter"/> gives a recorded parameter's value by its
        /// full name ("FT/v2/JawOpen"), or null when the recording has none: the controllers' defaults are used then.
        /// </summary>
        public Dictionary<string, float> Evaluate(Func<string, float?> parameter)
        {
            var values = new Dictionary<string, float>(StringComparer.Ordinal);
            // the add-on's linked parameters (one recorded value adds into others, e.g. JawOpen - MouthClosed = openness)
            var linked = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var kv in _links)
            {
                float? v = parameter(kv.Key);
                if (v == null) continue;
                foreach (var l in kv.Value) linked[l.Target] = (linked.TryGetValue(l.Target, out float s) ? s : 0f) + l.Weight * (l.Absolute ? Math.Abs(v.Value) : v.Value);
            }
            var animated = new Dictionary<string, float>(StringComparer.Ordinal);
            float Get(string name)
            {
                if (string.IsNullOrEmpty(name)) return 0f;
                if (values.TryGetValue(name, out float cached)) return cached;
                float v = Resolve(name, parameter, linked, animated);
                values[name] = v;
                return v;
            }
            // twice: parameters the layers animate (an openness value, a smoothed copy) are read by the second pass, as
            // Unity reads them on the next frame
            Dictionary<(string, string), float> shapes = null;
            for (int pass = 0; pass < 2; pass++)
            {
                values.Clear();
                shapes = new Dictionary<(string, string), float>();
                var paramOut = new Dictionary<string, float>(StringComparer.Ordinal);
                foreach (var ctl in _controllers)
                    foreach (var layer in ctl.Layers)
                    {
                        if (layer.Weight <= 0 || layer.Default == null) continue;
                        var state = Settle(layer, Get);
                        if (state?.Motion == null) continue;
                        var ls = new Dictionary<(string, string), float>();
                        var lp = new Dictionary<string, float>(StringComparer.Ordinal);
                        Accumulate(state.Motion, 1f, Get, ls, lp);
                        foreach (var kv in ls)
                            shapes[kv.Key] = layer.Additive ? (shapes.TryGetValue(kv.Key, out float b) ? b : 0f) + kv.Value * layer.Weight
                                                            : (shapes.TryGetValue(kv.Key, out float o) ? o : 0f) * (1 - layer.Weight) + kv.Value * layer.Weight;
                        foreach (var kv in lp) paramOut[kv.Key] = kv.Value;
                    }
                animated.Clear();
                foreach (var kv in paramOut) animated[kv.Key] = kv.Value;
                if (paramOut.Count == 0) break;
            }
            var res = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var kv in shapes) if (kv.Key.Item1 == FacePath) res[kv.Key.Item2] = kv.Value;
            return res;
        }

        /// <summary>
        /// A parameter's value: recorded (with the add-on's "Drive/" and proxy prefixes taken off: they are the add-on's
        /// own copies of the recorded value), else linked, else animated by a layer, else the add-on's declared default,
        /// else the controller's default.
        /// </summary>
        float Resolve(string name, Func<string, float?> parameter, Dictionary<string, float> linked, Dictionary<string, float> animated)
        {
            string bare = name;
            foreach (var pre in new[] { "Drive/", "OSCm/Proxy/", "OSCm/" })
                if (bare.StartsWith(pre, StringComparison.Ordinal)) { bare = bare.Substring(pre.Length); break; }
            float? v = parameter(bare) ?? (bare != name ? parameter(name) : null);
            if (v.HasValue) return v.Value;
            if (linked.TryGetValue(bare, out float l)) return l;
            if (animated.TryGetValue(name, out float a)) return a;
            if (_prefabDefaults.TryGetValue(name, out float pd)) return pd;
            if (_defaults.TryGetValue(name, out float d)) return d;
            return 0f;
        }

        static void Accumulate(Motion m, float weight, Func<string, float> get, Dictionary<(string, string), float> shapes, Dictionary<string, float> parameters)
        {
            if (m == null || weight == 0f) return;
            if (m.Clip != null)
            {
                foreach (var kv in m.Clip.Shapes) shapes[kv.Key] = (shapes.TryGetValue(kv.Key, out float s) ? s : 0f) + kv.Value * weight;
                foreach (var kv in m.Clip.Params) parameters[kv.Key] = (parameters.TryGetValue(kv.Key, out float p) ? p : 0f) + kv.Value * weight;
                return;
            }
            int n = m.Children.Count;
            if (n == 0) return;
            var w = new float[n];
            switch (m.Type)
            {
                case 0: Weights1D(m, get(m.ParamX), w); break;
                case 4: for (int i = 0; i < n; i++) w[i] = get(m.Children[i].Direct); break;
                case 3: Weights2D(m, get(m.ParamX), get(m.ParamY), w, polar: false); break;
                default: Weights2D(m, get(m.ParamX), get(m.ParamY), w, polar: true); break;
            }
            for (int i = 0; i < n; i++) if (w[i] != 0f) Accumulate(m.Children[i].Motion, weight * w[i], get, shapes, parameters);
        }

        /// <summary>1D: the two neighbouring thresholds share the weight linearly; outside the range the end one has it all.</summary>
        static void Weights1D(Motion m, float x, float[] w)
        {
            var ch = m.Children;
            int n = ch.Count;
            if (x <= ch[0].Threshold) { w[0] = 1; return; }
            if (x >= ch[n - 1].Threshold) { w[n - 1] = 1; return; }
            for (int i = 0; i < n - 1; i++)
            {
                float a = ch[i].Threshold, b = ch[i + 1].Threshold;
                if (x < a || x > b) continue;
                float t = b - a < 1e-6f ? 1f : (x - a) / (b - a);
                w[i] = 1 - t; w[i + 1] = t;
                return;
            }
        }

        /// <summary>
        /// 2D by gradient bands, as Unity's freeform blending: each child's influence falls off linearly towards every
        /// other child, measured in plain coordinates (cartesian) or in length and angle (directional); then normalised.
        /// </summary>
        static void Weights2D(Motion m, float px, float py, float[] w, bool polar)
        {
            var ch = m.Children;
            int n = ch.Count;
            float sum = 0;
            for (int i = 0; i < n; i++)
            {
                float h = 1f;
                for (int j = 0; j < n && h > 0; j++)
                {
                    if (j == i) continue;
                    float g = polar ? PolarBand(ch[i].X, ch[i].Y, ch[j].X, ch[j].Y, px, py) : CartesianBand(ch[i].X, ch[i].Y, ch[j].X, ch[j].Y, px, py);
                    if (g < h) h = g;
                }
                w[i] = Math.Max(0f, h);
                sum += w[i];
            }
            if (sum > 1e-6f) for (int i = 0; i < n; i++) w[i] /= sum;
            else
            {
                // outside every band (should not happen): the nearest child
                int best = 0; float bd = float.MaxValue;
                for (int i = 0; i < n; i++) { float d = (ch[i].X - px) * (ch[i].X - px) + (ch[i].Y - py) * (ch[i].Y - py); if (d < bd) { bd = d; best = i; } }
                w[best] = 1;
            }
        }

        static float CartesianBand(float ix, float iy, float jx, float jy, float px, float py)
        {
            float dx = jx - ix, dy = jy - iy, len = dx * dx + dy * dy;
            if (len < 1e-9f) return 1f;
            return 1f - ((px - ix) * dx + (py - iy) * dy) / len;
        }

        const float AngleScale = 2f;

        static float PolarBand(float ix, float iy, float jx, float jy, float px, float py)
        {
            float li = MathF.Sqrt(ix * ix + iy * iy), lj = MathF.Sqrt(jx * jx + jy * jy), lp = MathF.Sqrt(px * px + py * py);
            if (li < 1e-5f || lj < 1e-5f) return CartesianBand(ix, iy, jx, jy, px, py); // the centre: plain distance
            float avg = (li + lj) * 0.5f;
            float aij = SignedAngle(ix, iy, jx, jy), aip = lp < 1e-5f ? 0f : SignedAngle(ix, iy, px, py);
            float vx = (lj - li) / avg, vy = aij * AngleScale;
            float qx = (lp - li) / avg, qy = aip * AngleScale;
            float len = vx * vx + vy * vy;
            if (len < 1e-9f) return 1f;
            return 1f - (qx * vx + qy * vy) / len;
        }

        static float SignedAngle(float ax, float ay, float bx, float by) => MathF.Atan2(ax * by - ay * bx, ax * bx + ay * by);

        /// <summary>Every parameter the played layers read (blend trees and transitions), for checking a recording against them.</summary>
        public IReadOnlyCollection<string> ReadParameters()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<object>();
            void Walk(Motion m)
            {
                if (m == null || !seen.Add(m)) return;
                if (m.Clip != null) return;
                if (m.Type == 4) foreach (var c in m.Children) names.Add(c.Direct);
                else { names.Add(m.ParamX); if (m.Type != 0) names.Add(m.ParamY); }
                foreach (var c in m.Children) Walk(c.Motion);
            }
            void Visit(State s)
            {
                if (s == null || !seen.Add(s)) return;
                Walk(s.Motion);
                foreach (var t in s.Out) { foreach (var c in t.Conditions) names.Add(c.Param); Visit(t.Dest); }
            }
            foreach (var c in _controllers)
                foreach (var l in c.Layers)
                {
                    if (l.Weight <= 0) continue;
                    foreach (var t in l.Any) { foreach (var cd in t.Conditions) names.Add(cd.Param); Visit(t.Dest); }
                    Visit(l.Default);
                }
            names.Remove("");
            return names;
        }

        /// <summary>A one-line summary for the log.</summary>
        public override string ToString() =>
            $"face animator: {_controllers.Count} controller(s), {_clips.Count(c => c.Value != null)} clips, {_shapes.Count} face shapes on \"{FacePath}\"";

        internal static string Format(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
