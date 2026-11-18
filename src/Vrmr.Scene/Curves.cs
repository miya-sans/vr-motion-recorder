using System;
using System.Collections.Generic;
using System.Linq;

namespace VRMR
{
    public enum Interp : byte { Constant = 0, Linear = 1, Bezier = 2 }

    /// <summary>One key: time (action seconds), value (1 float, 3 for a vector, 4 for a quaternion x y z w), interpolation to the next key.</summary>
    public sealed class Key
    {
        public double T;
        public float[] V;
        public Interp Interp = Interp.Bezier;
        public Key() { }
        public Key(double t, float[] v, Interp i = Interp.Bezier) { T = t; V = v; Interp = i; }
    }

    /// <summary>
    /// A keyframe channel. Paths: <c>bone/&lt;HumanBodyBones name&gt;</c> (quaternion: rotation relative to the humanoid parent,
    /// Hips = world), <c>root/position</c> (hips position, vector), <c>face/&lt;shape&gt;</c>, <c>param/&lt;name&gt;</c> (float).
    /// In an Add strip a value is a delta (rotation: base * delta, position / float: base + delta).
    /// </summary>
    public sealed class Channel
    {
        public string Path = "";
        /// <summary>1 float, 3 vector, 4 quaternion.</summary>
        public int Dim = 1;
        public List<Key> Keys = new List<Key>();

        public const string RootPosition = "root/position";
        public static string Bone(int b) => "bone/" + HumanBones.Names[b];
        public static string Face(string shape) => "face/" + shape;
        public static string Param(string name) => "param/" + name;
        /// <summary>Humanoid bone of a bone/ path, else -1.</summary>
        public static int BoneOf(string path) => path != null && path.StartsWith("bone/", StringComparison.Ordinal) ? HumanBones.FromName(path.Substring(5)) : -1;

        // device channels (on input tracks): what the IK solve is fed, in the solve's space (the take's centred, avatar-scaled
        // space: the same space as BakedClip.Trackers). Add strips: position + delta, delta * rotation; Replace: absolute.
        public static string DevicePosition(BodyRole r) => "device/" + r + "/position";
        public static string DeviceRotation(BodyRole r) => "device/" + r + "/rotation";
        /// <summary>A device's influence on the solve, 0 (not used) .. 1 (as recorded), multiplied over layers.</summary>
        public static string Influence(BodyRole r) => "influence/" + r;
        /// <summary>The device role of a device/ or influence/ path (and which kind: "position", "rotation", "influence").</summary>
        public static bool DeviceOf(string path, out BodyRole role, out string kind)
        {
            role = BodyRole.Head; kind = null;
            if (path == null) return false;
            if (path.StartsWith("influence/", StringComparison.Ordinal)) { kind = "influence"; return Enum.TryParse(path.Substring(10), out role); }
            if (!path.StartsWith("device/", StringComparison.Ordinal)) return false;
            var parts = path.Split('/');
            if (parts.Length != 3 || !Enum.TryParse(parts[1], out role)) return false;
            kind = parts[2];
            return kind == "position" || kind == "rotation";
        }

        public void Sort() => Keys.Sort((a, b) => a.T.CompareTo(b.T));
        public double FirstT => Keys.Count > 0 ? Keys[0].T : 0;
        public double LastT => Keys.Count > 0 ? Keys[Keys.Count - 1].T : 0;

        /// <summary>Sets (or replaces) the key at time t (within half a millisecond).</summary>
        public Key SetKey(double t, float[] v, Interp interp = Interp.Bezier)
        {
            foreach (var k in Keys) if (Math.Abs(k.T - t) < 5e-4) { k.V = (float[])v.Clone(); return k; }
            var key = new Key(t, (float[])v.Clone(), interp);
            Keys.Add(key); Sort();
            return key;
        }

        public bool RemoveKey(double t) => Keys.RemoveAll(k => Math.Abs(k.T - t) < 5e-4) > 0;

        /// <summary>Value at time t (held before the first and after the last key).</summary>
        public void Evaluate(double t, float[] o)
        {
            int n = Keys.Count;
            if (n == 0) { for (int i = 0; i < o.Length; i++) o[i] = Dim == 4 && i == 3 ? 1 : 0; return; }
            if (n == 1 || t <= Keys[0].T) { Copy(Keys[0].V, o); return; }
            if (t >= Keys[n - 1].T) { Copy(Keys[n - 1].V, o); return; }
            int lo = 0, hi = n - 1;
            while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (Keys[mid].T <= t) lo = mid; else hi = mid; }
            var k0 = Keys[lo]; var k1 = Keys[hi];
            double span = Math.Max(1e-9, k1.T - k0.T);
            float u = (float)((t - k0.T) / span);
            if (k0.Interp == Interp.Constant) { Copy(k0.V, o); return; }
            if (Dim == 4)
            {
                var q0 = Q(k0.V); var q1 = Q(k1.V);
                if (k0.Interp == Interp.Linear) { W(Quat.Slerp(q0, q1, u), o); return; }
                // cubic (Catmull-Rom on the hemisphere-aligned quaternions, then normalised)
                var qa = lo > 0 ? Q(Keys[lo - 1].V) : q0; var qb = hi < n - 1 ? Q(Keys[hi + 1].V) : q1;
                if (Quat.Dot(qa, q0) < 0) qa = qa.Negated; if (Quat.Dot(q1, q0) < 0) q1 = q1.Negated; if (Quat.Dot(qb, q1) < 0) qb = qb.Negated;
                double ta = lo > 0 ? Keys[lo - 1].T : k0.T - span, tb = hi < n - 1 ? Keys[hi + 1].T : k1.T + span;
                var r = new Quat(Hermite(qa.x, q0.x, q1.x, qb.x, ta, k0.T, k1.T, tb, u), Hermite(qa.y, q0.y, q1.y, qb.y, ta, k0.T, k1.T, tb, u),
                                 Hermite(qa.z, q0.z, q1.z, qb.z, ta, k0.T, k1.T, tb, u), Hermite(qa.w, q0.w, q1.w, qb.w, ta, k0.T, k1.T, tb, u));
                W(r.Normalized, o);
                return;
            }
            if (k0.Interp == Interp.Linear) { for (int i = 0; i < Dim; i++) o[i] = k0.V[i] + (k1.V[i] - k0.V[i]) * u; return; }
            {
                var ka = lo > 0 ? Keys[lo - 1] : null; var kb = hi < n - 1 ? Keys[hi + 1] : null;
                double ta = ka?.T ?? k0.T - span, tb = kb?.T ?? k1.T + span;
                for (int i = 0; i < Dim; i++)
                    o[i] = Hermite(ka?.V[i] ?? k0.V[i], k0.V[i], k1.V[i], kb?.V[i] ?? k1.V[i], ta, k0.T, k1.T, tb, u, clamp: true);
            }
        }

        /// <summary>Cubic Hermite with Catmull-Rom tangents for uneven key spacing; clamp = auto-clamped (no overshoot at extremes), like Blender's "auto clamped".</summary>
        static float Hermite(float pa, float p0, float p1, float pb, double ta, double t0, double t1, double tb, float u, bool clamp = false)
        {
            double h = t1 - t0;
            double m0 = (p1 - pa) / Math.Max(1e-9, t1 - ta) * h;
            double m1 = (pb - p0) / Math.Max(1e-9, tb - t0) * h;
            if (clamp)
            {
                // a key that is a local extreme gets a flat tangent
                if ((p0 - pa) * (p1 - p0) <= 0) m0 = 0;
                if ((p1 - p0) * (pb - p1) <= 0) m1 = 0;
            }
            double u2 = u * u, u3 = u2 * u;
            return (float)((2 * u3 - 3 * u2 + 1) * p0 + (u3 - 2 * u2 + u) * m0 + (-2 * u3 + 3 * u2) * p1 + (u3 - u2) * m1);
        }

        static void Copy(float[] s, float[] o) { for (int i = 0; i < o.Length && i < s.Length; i++) o[i] = s[i]; }
        static Quat Q(float[] v) => new Quat(v[0], v[1], v[2], v[3]);
        static void W(Quat q, float[] o) { o[0] = q.x; o[1] = q.y; o[2] = q.z; o[3] = q.w; }

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["path"] = Path, ["dim"] = Dim,
            // compact: [t, interp, v...] per key
            ["keys"] = Keys.Select(k => (object)new List<object> { Math.Round(k.T, 6), (int)k.Interp }.Concat(k.V.Select(v => (object)Math.Round((double)v, 6))).ToList()).ToList(),
        };

        public static Channel FromJson(Dictionary<string, object> d)
        {
            var c = new Channel { Path = d.GetStr("path", ""), Dim = Math.Max(1, Math.Min(4, d.GetInt("dim", 1))) };
            foreach (var k in d.GetList("keys") ?? new List<object>())
                if (k is List<object> l && l.Count >= 2 + c.Dim)
                {
                    var v = new float[c.Dim];
                    for (int i = 0; i < c.Dim; i++) v[i] = (float)Convert.ToDouble(l[2 + i]);
                    c.Keys.Add(new Key(Convert.ToDouble(l[0]), v, (Interp)Math.Max(0, Math.Min(2, Convert.ToInt32(l[1])))));
                }
            c.Sort();
            return c;
        }
    }

    /// <summary>A set of keyframe channels (Blender's "action"), played by Action strips.</summary>
    public sealed class KeyAction
    {
        public string Id = "", Name = "Action";
        public readonly Dictionary<string, Channel> Channels = new Dictionary<string, Channel>();

        public Channel Get(string path) => Channels.TryGetValue(path, out var c) ? c : null;
        public Channel GetOrAdd(string path, int dim)
        {
            if (!Channels.TryGetValue(path, out var c)) { c = new Channel { Path = path, Dim = dim }; Channels[path] = c; }
            return c;
        }

        public double FirstKey => Channels.Values.Where(c => c.Keys.Count > 0).Select(c => c.FirstT).DefaultIfEmpty(0).Min();
        public double LastKey => Channels.Values.Where(c => c.Keys.Count > 0).Select(c => c.LastT).DefaultIfEmpty(0).Max();
        /// <summary>Every key time (sorted, merged within half a millisecond).</summary>
        public List<double> KeyTimes()
        {
            var all = Channels.Values.SelectMany(c => c.Keys.Select(k => k.T)).OrderBy(t => t).ToList();
            var r = new List<double>();
            foreach (var t in all) if (r.Count == 0 || t - r[r.Count - 1] > 5e-4) r.Add(t);
            return r;
        }

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["id"] = Id, ["name"] = Name, ["channels"] = Channels.Values.Where(c => c.Keys.Count > 0).Select(c => (object)c.ToJson()).ToList(),
        };

        public static KeyAction FromJson(Dictionary<string, object> d)
        {
            var a = new KeyAction { Id = d.GetStr("id", ""), Name = d.GetStr("name", "Action") };
            foreach (var c in d.GetList("channels") ?? new List<object>())
                if (c is Dictionary<string, object> cd) { var ch = Channel.FromJson(cd); a.Channels[ch.Path] = ch; }
            return a;
        }
    }
}
