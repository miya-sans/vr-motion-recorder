using System;
using System.Collections.Generic;
using System.Linq;

namespace VRMR
{
    /// <summary>
    ///     Keyframe edits on actions (no UI): keys for bones, the root, devices (headset / controllers / trackers: what the IK
    ///     is fed) and device influence; delete, copy / paste, mirror, move in time, interpolation, keys from the pose below.
    ///     Key times are action (source) seconds: <c>strip.ToSource(sceneTime)</c>.
    ///     Values follow the strip's blend: Replace = absolute, Add = deltas (rotation: base * delta for bones, delta * base
    ///     for
    ///     devices; position: base + delta).
    /// </summary>
    public static class KeyframeOps
    {
        /// <summary>Two keys closer than this are the same key (seconds).</summary>
        public const double Same = 5e-4;

        /// <summary>Interpolation of new keys.</summary>
        public static Interp DefaultInterp = Interp.Bezier;

        // =====================================================================================================  reading

        static readonly float[] _b4 = new float[4];

        public static KeyClip Clipboard;

        // =====================================================================================================  paths

        public static bool IsBone(string path)
        {
            return Channel.BoneOf(path) >= 0;
        }

        public static bool IsRoot(string path)
        {
            return path == Channel.RootPosition;
        }

        public static bool IsDevice(string path)
        {
            return Channel.DeviceOf(path, out _, out string k) && k != "influence";
        }

        public static bool IsInfluence(string path)
        {
            return path != null && path.StartsWith("influence/", StringComparison.Ordinal);
        }

        public static bool IsBody(string path)
        {
            return IsBone(path) || IsRoot(path);
        }

        /// <summary>Paths of a device role (position, rotation, influence).</summary>
        public static string[] DevicePaths(BodyRole r)
        {
            return new[] { Channel.DevicePosition(r), Channel.DeviceRotation(r), Channel.Influence(r) };
        }

        /// <summary>A filter that passes the channels of the given devices and bones (null = everything).</summary>
        public static Func<string, bool> Filter(IEnumerable<BodyRole> roles, IEnumerable<int> bones, bool root = false,
            bool influence = true)
        {
            var rs = roles != null ? new HashSet<BodyRole>(roles) : null;
            var bs = bones != null ? new HashSet<int>(bones) : null;
            return p =>
            {
                if (Channel.DeviceOf(p, out var r, out string k))
                    return rs != null && rs.Contains(r) && (influence || k != "influence");
                int b = Channel.BoneOf(p);
                if (b >= 0) return bs != null && bs.Contains(b);
                if (IsRoot(p)) return root;
                return false;
            };
        }

        /// <summary>Left / right swapped in a path (bones, devices, influence); others unchanged.</summary>
        public static string MirrorPath(string path)
        {
            int b = Channel.BoneOf(path);
            if (b >= 0) return Channel.Bone(HumanBones.Mirror(b));
            if (Channel.DeviceOf(path, out var r, out string kind))
            {
                var m = MirrorRole(r);
                return kind == "influence" ? Channel.Influence(m) :
                    kind == "position" ? Channel.DevicePosition(m) : Channel.DeviceRotation(m);
            }

            return path;
        }

        public static BodyRole MirrorRole(BodyRole r)
        {
            string n = r.ToString();
            if (n.StartsWith("Left", StringComparison.Ordinal))
                return (BodyRole)Enum.Parse(typeof(BodyRole), "Right" + n.Substring(4));
            if (n.StartsWith("Right", StringComparison.Ordinal))
                return (BodyRole)Enum.Parse(typeof(BodyRole), "Left" + n.Substring(5));
            return r;
        }

        // =====================================================================================================  setting keys

        /// <summary>Sets a key (a new key gets <see cref="DefaultInterp" />; an existing one keeps its interpolation).</summary>
        public static Key SetKey(KeyAction a, string path, int dim, double t, float[] v)
        {
            var ch = a.GetOrAdd(path, dim);
            var k = ch.Keys.FirstOrDefault(x => Math.Abs(x.T - t) < Same);
            if (k != null)
            {
                k.V = (float[])v.Clone();
                return k;
            }

            return ch.SetKey(t, v, DefaultInterp);
        }

        public static float[] V(Quat q)
        {
            return new[] { q.x, q.y, q.z, q.w };
        }

        public static float[] V(Vec3 p)
        {
            return new[] { p.x, p.y, p.z };
        }

        public static Quat Q(float[] v)
        {
            return new Quat(v[0], v[1], v[2], v[3]).Normalized;
        }

        public static Vec3 P(float[] v)
        {
            return new Vec3(v[0], v[1], v[2]);
        }

        /// <summary>Keeps a quaternion key on the same hemisphere as its neighbour so interpolation takes the short way.</summary>
        static Quat Continuous(Channel ch, double t, Quat q)
        {
            if (ch == null || ch.Keys.Count == 0) return q;
            Key near = null;
            double best = double.MaxValue;
            foreach (var k in ch.Keys)
            {
                double d = Math.Abs(k.T - t);
                if (d > Same && d < best)
                {
                    best = d;
                    near = k;
                }
            }

            if (near != null && Quat.Dot(Q(near.V), q) < 0) return q.Negated;
            return q;
        }

        public static void KeyBone(KeyAction a, double t, int bone, Quat local)
        {
            string path = Channel.Bone(bone);
            SetKey(a, path, 4, t, V(Continuous(a.Get(path), t, local.Normalized)));
        }

        public static void KeyRoot(KeyAction a, double t, Vec3 hips)
        {
            SetKey(a, Channel.RootPosition, 3, t, V(hips));
        }

        public static void KeyDevice(KeyAction a, double t, BodyRole r, Pose p, bool pos = true, bool rot = true)
        {
            if (pos) SetKey(a, Channel.DevicePosition(r), 3, t, V(p.pos));
            if (rot)
            {
                string path = Channel.DeviceRotation(r);
                SetKey(a, path, 4, t, V(Continuous(a.Get(path), t, p.rot.Normalized)));
            }
        }

        public static void KeyInfluence(KeyAction a, double t, BodyRole r, float v)
        {
            SetKey(a, Channel.Influence(r), 1, t, new[] { MathX.Clamp01(v) });
        }

        /// <summary>A channel's value at t (null when the channel has no keys).</summary>
        public static float[] Value(KeyAction a, string path, double t)
        {
            var ch = a?.Get(path);
            if (ch == null || ch.Keys.Count == 0) return null;
            float[] o = new float[ch.Dim];
            ch.Evaluate(t, o);
            return o;
        }

        /// <summary>A device's keyed influence at t (1 when it has no influence keys).</summary>
        public static float Influence(KeyAction a, BodyRole r, double t)
        {
            return Value(a, Channel.Influence(r), t)?[0] ?? 1f;
        }

        /// <summary>Is there a key at t in any channel passing the filter?</summary>
        public static bool HasKey(KeyAction a, double t, Func<string, bool> filter = null)
        {
            return a != null && a.Channels.Values.Any(c =>
                (filter == null || filter(c.Path)) && c.Keys.Any(k => Math.Abs(k.T - t) < Same));
        }

        /// <summary>Key times of the channels passing the filter (sorted, merged).</summary>
        public static List<double> KeyTimes(KeyAction a, Func<string, bool> filter = null)
        {
            var all = a.Channels.Values.Where(c => filter == null || filter(c.Path))
                .SelectMany(c => c.Keys.Select(k => k.T)).OrderBy(x => x).ToList();
            var r = new List<double>();
            foreach (double t in all)
                if (r.Count == 0 || t - r[r.Count - 1] > Same)
                    r.Add(t);
            return r;
        }

        public static double? PrevKey(KeyAction a, double t, Func<string, bool> filter = null)
        {
            var l = KeyTimes(a, filter).Where(x => x < t - Same).ToList();
            return l.Count > 0 ? l.Last() : null;
        }

        public static double? NextKey(KeyAction a, double t, Func<string, bool> filter = null)
        {
            var l = KeyTimes(a, filter).Where(x => x > t + Same).ToList();
            return l.Count > 0 ? l.First() : null;
        }

        /// <summary>Paths keyed at t.</summary>
        public static List<string> KeyedAt(KeyAction a, double t, Func<string, bool> filter = null)
        {
            return a.Channels.Values
                .Where(c => (filter == null || filter(c.Path)) && c.Keys.Any(k => Math.Abs(k.T - t) < Same))
                .Select(c => c.Path).OrderBy(p => p).ToList();
        }

        // =====================================================================================================  posing through a strip

        /// <summary>
        ///     Keys bones (and the root) so the strip makes <paramref name="target" /> over <paramref name="below" /> (the pose of
        ///     the tracks under the strip's track): Replace strips get the target's local rotations (the hips / root through the
        ///     inverse of the strip's offset), Add strips get base⁻¹ · target and target − base.
        /// </summary>
        public static void KeyPose(KeyAction a, Strip st, double t, ScenePose target, ScenePose below,
            IEnumerable<int> bones, bool root)
        {
            bool add = st.Blend == BlendMode.Add;
            var off = st.Offset;
            var offI = off.Inverse;
            foreach (int b in bones)
            {
                Quat v;
                if (add) v = below.Local[b].Conjugate * target.Local[b];
                else if (b == HumanBones.Hips) v = offI.rot * target.Local[b];
                else v = target.Local[b];
                KeyBone(a, t, b, v);
            }

            if (root) KeyRoot(a, t, add ? target.Hips - below.Hips : offI.TransformPoint(target.Hips));
        }

        /// <summary>What a strip's bone channels give at t over a base pose (for showing / editing the current pose).</summary>
        public static void ApplyStripPose(KeyAction a, Strip st, double t, ScenePose below, ScenePose o)
        {
            o.CopyFrom(below);
            bool add = st.Blend == BlendMode.Add;
            var off = st.Offset;
            foreach (var ch in a.Channels.Values)
            {
                if (ch.Keys.Count == 0) continue;
                int b = Channel.BoneOf(ch.Path);
                if (b >= 0 && ch.Dim == 4)
                {
                    ch.Evaluate(t, _b4);
                    var q = Q(_b4);
                    o.Local[b] = add ? (below.Local[b] * q).Normalized :
                        b == HumanBones.Hips ? (off.rot * q).Normalized : q;
                }
                else if (IsRoot(ch.Path) && ch.Dim == 3)
                {
                    ch.Evaluate(t, _b4);
                    var p = P(_b4);
                    o.Hips = add ? below.Hips + p : off.TransformPoint(p);
                }
            }
        }

        /// <summary>
        ///     Keys devices so the strip makes the target device poses over the devices below (both in solve space):
        ///     Replace = absolute, Add = position delta and rotation delta (target · base⁻¹).
        /// </summary>
        public static void KeyDevices(KeyAction a, Strip st, double t, IDictionary<BodyRole, Pose> target,
            IDictionary<BodyRole, Pose> below, IEnumerable<BodyRole> roles, bool pos = true, bool rot = true,
            bool absolute = false)
        {
            bool add = st.Blend == BlendMode.Add && !absolute;
            foreach (var r in roles)
            {
                if (!target.TryGetValue(r, out var tp)) continue;
                var v = tp;
                if (add)
                {
                    var bp = below != null && below.TryGetValue(r, out var x) ? x : tp;
                    v = new Pose(tp.pos - bp.pos, (tp.rot * bp.rot.Conjugate).Normalized);
                }

                KeyDevice(a, t, r, v, pos, rot);
            }
        }

        /// <summary>A device pose as a strip's channels make it at t over the device below (solve space).</summary>
        public static Pose ApplyStripDevice(KeyAction a, Strip st, double t, BodyRole r, Pose below)
        {
            bool add = st.Blend == BlendMode.Add;
            var p = below;
            float[] pv = Value(a, Channel.DevicePosition(r), t);
            float[] rv = Value(a, Channel.DeviceRotation(r), t);
            if (pv != null) p.pos = add ? below.pos + P(pv) : P(pv);
            if (rv != null) p.rot = add ? (Q(rv) * below.rot).Normalized : Q(rv);
            return p;
        }

        /// <summary>Does the strip key this device (position or rotation)?</summary>
        public static bool KeysDevice(KeyAction a, BodyRole r)
        {
            return (a.Get(Channel.DevicePosition(r))?.Keys.Count ?? 0) > 0 ||
                   (a.Get(Channel.DeviceRotation(r))?.Keys.Count ?? 0) > 0;
        }

        /// <summary>
        ///     Inserts keys holding the current values at t for the given paths (a channel without keys gets the value from
        ///     its default: the base for Replace, no change for Add).
        /// </summary>
        public static void InsertCurrent(KeyAction a, Strip st, double t, IEnumerable<string> paths,
            Func<string, float[]> baseValue)
        {
            bool add = st.Blend == BlendMode.Add;
            foreach (string path in paths.Distinct().ToList())
            {
                float[] cur = Value(a, path, t);
                int dim = DimOf(path);
                if (cur == null)
                {
                    if (IsInfluence(path)) cur = new[] { 1f };
                    else if (add) cur = dim == 4 ? new float[] { 0, 0, 0, 1 } : new float[dim];
                    else cur = baseValue?.Invoke(path);
                }

                if (cur == null) continue;
                SetKey(a, path, dim, t, cur);
            }
        }

        public static int DimOf(string path)
        {
            if (IsBone(path)) return 4;
            if (IsRoot(path)) return 3;
            if (Channel.DeviceOf(path, out _, out string k)) return k == "rotation" ? 4 : k == "position" ? 3 : 1;
            return 1;
        }

        // =====================================================================================================  delete, move, interpolation

        /// <summary>Deletes the keys at t of the channels passing the filter (empty channels are dropped). Returns how many.</summary>
        public static int DeleteKeys(KeyAction a, double t, Func<string, bool> filter = null)
        {
            int n = 0;
            foreach (var ch in a.Channels.Values.ToList())
            {
                if (filter != null && !filter(ch.Path)) continue;
                n += ch.Keys.RemoveAll(k => Math.Abs(k.T - t) < Same);
                if (ch.Keys.Count == 0) a.Channels.Remove(ch.Path);
            }

            return n;
        }

        /// <summary>Deletes whole channels (all their keys).</summary>
        public static int ClearChannels(KeyAction a, Func<string, bool> filter)
        {
            var del = a.Channels.Keys.Where(filter).ToList();
            foreach (string p in del) a.Channels.Remove(p);
            return del.Count;
        }

        /// <summary>
        ///     Moves the keys at the given times by dt (channels passing the filter). A moved key replaces a key it lands on.
        /// </summary>
        public static void MoveKeys(KeyAction a, IEnumerable<double> times, double dt, Func<string, bool> filter = null)
        {
            var ts = times.ToList();
            if (ts.Count == 0 || Math.Abs(dt) < 1e-9) return;
            foreach (var ch in a.Channels.Values)
            {
                if (filter != null && !filter(ch.Path)) continue;
                var moving = ch.Keys.Where(k => ts.Any(t => Math.Abs(k.T - t) < Same)).ToList();
                if (moving.Count == 0) continue;
                foreach (var k in moving) k.T += dt;
                // keys that a moved key now sits on (and that didn't move) are replaced
                ch.Keys.RemoveAll(k => !moving.Contains(k) && moving.Any(m => Math.Abs(m.T - k.T) < Same));
                ch.Sort();
            }
        }

        /// <summary>Scales key times about a pivot (retiming): t' = pivot + (t - pivot) * factor.</summary>
        public static void ScaleKeys(KeyAction a, IEnumerable<double> times, double pivot, double factor,
            Func<string, bool> filter = null)
        {
            var ts = times.ToList();
            if (factor <= 1e-6) return;
            foreach (var ch in a.Channels.Values)
            {
                if (filter != null && !filter(ch.Path)) continue;
                foreach (var k in ch.Keys)
                    if (ts.Any(t => Math.Abs(k.T - t) < Same))
                        k.T = pivot + (k.T - pivot) * factor;
                ch.Sort();
            }
        }

        public static void SetInterp(KeyAction a, double t, Interp i, Func<string, bool> filter = null)
        {
            foreach (var ch in a.Channels.Values)
                if (filter == null || filter(ch.Path))
                    foreach (var k in ch.Keys)
                        if (Math.Abs(k.T - t) < Same)
                            k.Interp = i;
        }

        /// <summary>The interpolation of the keys at t (null when none, or when they differ).</summary>
        public static Interp? InterpAt(KeyAction a, double t, Func<string, bool> filter = null)
        {
            Interp? r = null;
            foreach (var ch in a.Channels.Values)
                if (filter == null || filter(ch.Path))
                    foreach (var k in ch.Keys)
                        if (Math.Abs(k.T - t) < Same)
                        {
                            if (r == null) r = k.Interp;
                            else if (r != k.Interp) return null;
                        }

            return r;
        }

        // =====================================================================================================  influence

        /// <summary>
        ///     Switches a device on / off over a ramp ending at t: a key holding the current influence at t - ramp, the new value
        ///     at t (keys inside the ramp are removed).
        /// </summary>
        public static void RampInfluence(KeyAction a, BodyRole r, double t, float to, double ramp)
        {
            string path = Channel.Influence(r);
            ramp = Math.Max(0, ramp);
            float from = Influence(a, r, t - ramp);
            var ch = a.Get(path);
            if (ch != null) ch.Keys.RemoveAll(k => k.T > t - ramp + Same && k.T < t - Same);
            if (ramp > Same)
            {
                var k0 = SetKey(a, path, 1, t - ramp, new[] { from });
                k0.Interp = Interp.Bezier;
            }

            var k1 = SetKey(a, path, 1, t, new[] { MathX.Clamp01(to) });
        }

        /// <summary>Copies the values at t (keyed or interpolated) of the channels passing the filter.</summary>
        public static KeyClip Copy(KeyAction a, Strip st, double t, Func<string, bool> filter = null)
        {
            var c = new KeyClip { Delta = st != null && st.Blend == BlendMode.Add };
            foreach (var ch in a.Channels.Values)
            {
                if (ch.Keys.Count == 0 || (filter != null && !filter(ch.Path))) continue;
                float[] v = new float[ch.Dim];
                ch.Evaluate(t, v);
                c.Values[ch.Path] = v;
                var k = ch.Keys.FirstOrDefault(x => Math.Abs(x.T - t) < Same);
                c.Interps[ch.Path] = k?.Interp ?? DefaultInterp;
            }

            return c;
        }

        /// <summary>
        ///     Pastes copied values as keys at t (mirror: left / right swapped and reflected, see <see cref="MirrorValues" />
        ///     ).
        /// </summary>
        public static void Paste(KeyAction a, double t, KeyClip c, MirrorFrame mirror = null)
        {
            var vals = mirror != null ? MirrorValues(c.Values, c.Delta, mirror) : c.Values;
            foreach (var kv in vals)
            {
                var k = SetKey(a, kv.Key, kv.Value.Length, t, kv.Value);
                string src = mirror != null ? MirrorPath(kv.Key) : kv.Key;
                if (c.Interps.TryGetValue(src, out var i)) k.Interp = i;
            }
        }

        /// <summary>Channel values mirrored: paths swap left / right, positions and rotations reflect across the middle plane.</summary>
        public static Dictionary<string, float[]> MirrorValues(Dictionary<string, float[]> vals, bool delta,
            MirrorFrame m)
        {
            var o = new Dictionary<string, float[]>();
            var face = Quat.YawRotation(m.Yaw);
            var fi = face.Conjugate;

            Quat R0(Quat q)
            {
                return new Quat(q.x, -q.y, -q.z, q.w);
            } // conjugation by the reflection x -> -x

            Quat RefOp(Quat q)
            {
                return (face * R0(fi * q * face) * fi).Normalized;
            } // a world-space rotation operator, reflected

            Quat RefFrame(Quat q)
            {
                return (face * R0(fi * q)).Normalized;
            } // a frame (world rotation), reflected

            Vec3 RefV(Vec3 v)
            {
                var l = fi * v;
                return face * new Vec3(-l.x, l.y, l.z);
            }

            foreach (var kv in vals)
            {
                string path = MirrorPath(kv.Key);
                float[] v = kv.Value;
                int b = Channel.BoneOf(kv.Key);
                if (b >= 0 && v.Length == 4 && m.Rig != null)
                {
                    int mb = HumanBones.Mirror(b);
                    if (!m.Rig.Exists[mb]) mb = b;
                    var q = Q(v);
                    var rb = m.Rig.RestWorld[b];
                    var rm = m.Rig.RestWorld[mb];
                    Quat outQ;
                    if (b == HumanBones.Hips && !delta)
                    {
                        // the hips' world rotation: its rotation from rest is reflected across the body's middle plane
                        outQ = (RefFrame(q * rb.Conjugate) * rm).Normalized;
                    }
                    else if (delta)
                    {
                        // a delta in the bone's own frame: as a rotation in the avatar's rest space (facing +Z), reflected
                        var w = rb * q * rb.Conjugate;
                        outQ = (rm.Conjugate * R0(w) * rm).Normalized;
                    }
                    else
                    {
                        // a local rotation: D = parentRest · local · rest⁻¹ is the bone's turn in rest space; mirrored, it is
                        // reflected (independent of where the body faces), then made local to the mirror bone again
                        int pb = m.Rig.Parent[b], pm = m.Rig.Parent[mb];
                        var rpb = pb >= 0 ? m.Rig.RestWorld[pb] : Quat.Identity;
                        var rpm = pm >= 0 ? m.Rig.RestWorld[pm] : Quat.Identity;
                        var D = rpb * q * rb.Conjugate;
                        outQ = (rpm.Conjugate * R0(D) * rm).Normalized;
                    }

                    o[path] = V(outQ);
                }
                else if (IsRoot(kv.Key) && v.Length == 3)
                {
                    var p = P(v);
                    o[path] = V(delta ? RefV(p) : m.Origin + RefV(p - m.Origin));
                }
                else if (Channel.DeviceOf(kv.Key, out _, out string kind) && kind != "influence")
                {
                    if (kind == "position")
                    {
                        var p = P(v);
                        o[path] = V(delta ? RefV(p) : m.Origin + RefV(p - m.Origin));
                    }
                    else
                    {
                        var q = Q(v);
                        o[path] = V(delta ? RefOp(q) : RefFrame(q));
                    }
                }
                else o[path] = (float[])v.Clone();
            }

            return o;
        }

        /// <summary>Mirrors the keys at t (of the channels passing the filter) onto the other side, in place.</summary>
        public static void MirrorKeys(KeyAction a, Strip st, double t, MirrorFrame m, Func<string, bool> filter = null)
        {
            var c = Copy(a, st, t, filter);
            // keys at t of mirrored paths are replaced; the centre bones (spine, head, hips) mirror onto themselves
            Paste(a, t, c, m);
        }

        // =====================================================================================================  copy / paste

        /// <summary>Values of channels at one time (copied keys).</summary>
        public sealed class KeyClip
        {
            public readonly Dictionary<string, Interp> Interps = new();
            public readonly Dictionary<string, float[]> Values = new();
            public bool Delta;
            public int Count => Values.Count;
        }

        // =====================================================================================================  mirror

        /// <summary>
        ///     The body's middle plane for mirroring: through Origin, facing Yaw degrees (the avatar's forward). Rig: the
        ///     skeleton (bone rotations are mirrored as rotations from rest; the rest pose must be symmetric).
        /// </summary>
        public sealed class MirrorFrame
        {
            public Vec3 Origin;
            public PoseRig Rig;
            public float Yaw;
        }
    }
}
