using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VRMR
{
    // =====================================================================================================  mocap cleanup
    //
    // Modifiers that repair a strip's poses (IStripModifier: a whole PoseRun) or an actor's evaluated output
    // (IActorModifier: the BakedClip after all layers), found by SceneModifiers through reflection:
    //   footlock  feet planted while they touch the ground: contacts from the foot's height and speed, the foot pinned,
    //             the leg re-solved with two-bone IK, blended in and out around each contact;
    //   repair    marked stretches of bad tracking: the chosen body parts interpolated (or held) between the good poses
    //             around them, in local space so the limbs move with the body; the feet can stay planted (leg IK);
    //   smooth    zero-phase Gaussian smoothing of the chosen body parts (rotations, hips position, face shapes).
    // Parameters live in StripModifier.Params (SI units); IModifierParams describes them for the Layer inspector.

    public enum ModParamKind { Number, Check, Choice, Parts, Ranges }

    /// <summary>One modifier parameter, for the inspector (values are stored in SI units; the UI shows value * UiScale).</summary>
    public sealed class ModParam
    {
        public string Key, Label, Tip, Format = "0.00", Suffix = "";
        public ModParamKind Kind = ModParamKind.Number;
        public double Default, Min = double.NegativeInfinity, Max = double.PositiveInfinity, UiScale = 1;
        /// <summary>Choice: stored keys and their labels.</summary>
        public string[] Choices, ChoiceLabels;
        public BodyPart DefaultParts = BodyMask.Body;
        public bool Slider;
    }

    /// <summary>A modifier that describes its parameters (the Layer inspector builds its rows from these).</summary>
    public interface IModifierParams
    {
        IReadOnlyList<ModParam> Params { get; }
        /// <summary>One or two sentences on what it does (tooltip / note).</summary>
        string Help { get; }
    }

    /// <summary>A marked stretch for the repair modifier (source seconds on a strip, scene seconds on an actor).</summary>
    public sealed class RepairRange
    {
        public double A, B;
        /// <summary>Body parts to repair in this stretch; null = the modifier's own parts.</summary>
        public BodyPart? Parts;
        public string Note = "";
        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object> { ["a"] = A, ["b"] = B };
            if (Parts.HasValue) d["parts"] = BodyMask.ToJson(Parts.Value);
            if (!string.IsNullOrEmpty(Note)) d["note"] = Note;
            return d;
        }
        public static RepairRange FromJson(Dictionary<string, object> d) => new RepairRange
        {
            A = d.GetNum("a"), B = d.GetNum("b"), Parts = d.ContainsKey("parts") ? BodyMask.FromJson(d["parts"]) : (BodyPart?)null, Note = d.GetStr("note", ""),
        };
    }

    /// <summary>Typed access to StripModifier.Params.</summary>
    public static class ModArgs
    {
        public static double Num(this StripModifier m, string k, double def)
        {
            if (m?.Params == null || !m.Params.TryGetValue(k, out var v) || v == null) return def;
            if (v is bool b) return b ? 1 : 0;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return def; }
        }
        public static bool Flag(this StripModifier m, string k, bool def)
        {
            if (m?.Params == null || !m.Params.TryGetValue(k, out var v) || v == null) return def;
            if (v is bool b) return b;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture) != 0; } catch { return def; }
        }
        public static string Str(this StripModifier m, string k, string def) => m?.Params != null && m.Params.TryGetValue(k, out var v) && v is string s ? s : def;
        public static BodyPart Parts(this StripModifier m, string k, BodyPart def) => m?.Params != null && m.Params.TryGetValue(k, out var v) && v is List<object> ? BodyMask.FromJson(v, def) : def;
        public static List<RepairRange> Ranges(this StripModifier m, string k = "ranges")
        {
            var r = new List<RepairRange>();
            if (m?.Params != null && m.Params.TryGetValue(k, out var v) && v is List<object> l)
                foreach (var x in l) if (x is Dictionary<string, object> d) r.Add(RepairRange.FromJson(d));
            return r;
        }
        public static void SetRanges(this StripModifier m, List<RepairRange> ranges, string k = "ranges") => m.Params[k] = ranges.OrderBy(x => x.A).Select(x => (object)x.ToJson()).ToList();

        /// <summary>Fills missing parameters with their defaults.</summary>
        public static void Defaults(StripModifier m, IModifierParams p)
        {
            if (p == null) return;
            foreach (var q in p.Params)
            {
                if (m.Params.ContainsKey(q.Key)) continue;
                switch (q.Kind)
                {
                    case ModParamKind.Number: m.Params[q.Key] = q.Default; break;
                    case ModParamKind.Check: m.Params[q.Key] = q.Default != 0; break;
                    case ModParamKind.Choice: m.Params[q.Key] = q.Choices[(int)q.Default]; break;
                    case ModParamKind.Parts: m.Params[q.Key] = BodyMask.ToJson(q.DefaultParts); break;
                    case ModParamKind.Ranges: m.Params[q.Key] = new List<object>(); break;
                }
            }
        }

        /// <summary>A new modifier of a type with its default parameters.</summary>
        public static StripModifier Create(string type, IModifierParams p)
        {
            var m = new StripModifier { Type = type };
            Defaults(m, p);
            return m;
        }
    }

    /// <summary>
    /// A view of poses to clean: hips position + local rotations per frame (PoseRun or a BakedClip converted to local),
    /// the rig, the avatar scale per frame, the frame rate and each frame's time (source or scene seconds).
    /// </summary>
    public sealed class PoseFrames
    {
        public PoseRig Rig;
        public Vec3[] Hips;
        public Quat[][] Local;
        public float[] Scale;
        public double[] Time;
        public float Fps = 60;
        /// <summary>Bones the data drives (null = every existing bone).</summary>
        public bool[] Has;
        public int Count => Hips.Length;

        public bool Drives(int b) => Rig.Exists[b] && (Has == null || Has[b]);

        readonly Vec3[] _p = new Vec3[HumanBones.Count];
        readonly Quat[] _w = new Quat[HumanBones.Count];
        /// <summary>FK of frame f (arrays reused: valid until the next call).</summary>
        public Vec3[] Fk(int f, out Quat[] world) { Rig.Fk(Hips[f], Local[f], Scale[f], _p, _w); world = _w; return _p; }

        public static PoseFrames OfRun(StripModifierContext c, PoseRun run)
        {
            var pf = new PoseFrames { Rig = c.Rig, Hips = run.Hips, Local = run.Local, Fps = c.Fps, Has = run.HasBone, Scale = new float[run.Count], Time = new double[run.Count] };
            for (int k = 0; k < run.Count; k++) { pf.Scale[k] = c.Scale; pf.Time[k] = c.Strip != null && c.Strip.Kind == StripKind.Take ? c.Strip.ToSource(c.TimeOf(k)) : c.TimeOf(k); }
            return pf;
        }

        /// <summary>A clip's frames as local rotations (call <see cref="WriteBack"/> after changing them).</summary>
        public static PoseFrames OfClip(PoseRig rig, BakedClip clip, double start = 0)
        {
            int n = clip.Frames;
            var pf = new PoseFrames { Rig = rig, Hips = clip.Hips, Local = new Quat[n][], Fps = clip.Fps, Scale = new float[n], Time = new double[n] };
            for (int f = 0; f < n; f++)
            {
                pf.Local[f] = new Quat[HumanBones.Count];
                for (int b = 0; b < HumanBones.Count; b++) pf.Local[f][b] = Quat.Identity;
                rig.ToLocal(clip.Rot[f], pf.Local[f]);
                pf.Scale[f] = clip.ScaleAt(f);
                pf.Time[f] = clip.SourceTime != null && f < clip.SourceTime.Length ? clip.SourceTime[f] : start + f / (double)clip.Fps;
            }
            return pf;
        }

        public void WriteBack(BakedClip clip)
        {
            for (int f = 0; f < clip.Frames; f++) Rig.ToWorld(Local[f], clip.Rot[f]);
        }
    }

    // =====================================================================================================  leg IK

    public static class LegIK
    {
        public static readonly int[] UpperLeg = { HumanBones.LeftUpperLeg, HumanBones.RightUpperLeg };
        public static readonly int[] LowerLeg = { HumanBones.LeftLowerLeg, HumanBones.RightLowerLeg };
        public static readonly int[] Foot = { HumanBones.LeftFoot, HumanBones.RightFoot };
        public static readonly int[] Toes = { HumanBones.LeftToes, HumanBones.RightToes };

        /// <summary>
        /// Two-bone IK for one leg of frame f: puts the ankle on <paramref name="target"/> keeping the knee's bend plane,
        /// the hips where they are and the foot's world rotation (<paramref name="footWorld"/>). pos / world = FK of the
        /// frame (updated for the leg). Returns how far the target was out of reach (m).
        /// </summary>
        public static float Solve(PoseRig rig, Quat[] local, Vec3[] pos, Quat[] world, int side, Vec3 target, Quat footWorld, bool keepHorizontal = false, float maxLift = 0.02f)
        {
            int ul = UpperLeg[side], ll = LowerLeg[side], ft = Foot[side];
            if (!rig.Exists[ul] || !rig.Exists[ll] || !rig.Exists[ft]) return 0;
            Vec3 h = pos[ul], k = pos[ll], a = pos[ft];
            float l1 = (k - h).Length, l2 = (a - k).Length;
            if (l1 < 1e-5f || l2 < 1e-5f) return 0;
            float lo = Math.Abs(l1 - l2) + 1e-4f, hi = (l1 + l2) * 0.99995f;
            float miss = 0;
            if (keepHorizontal && (target - h).Length > hi)
            {
                // out of reach with a straight leg: keep the foot where it is pinned on the ground plane and let it rise
                // (a few mm of lift reads as a heel coming up; sliding reads as sliding)
                float dx = target.x - h.x, dz = target.z - h.z, dxz2 = dx * dx + dz * dz;
                if (dxz2 < hi * hi)
                {
                    // the reachable point straight above / below the pin nearest to it (the hips can be below the feet when lying down)
                    float r = (float)Math.Sqrt(hi * hi - dxz2), up = h.y + r, dn = h.y - r;
                    float y = Math.Abs(up - target.y) < Math.Abs(dn - target.y) ? up : dn;
                    if (Math.Abs(y - target.y) <= maxLift) { miss = Math.Abs(y - target.y); target = new Vec3(target.x, y, target.z); }
                }
            }
            Vec3 ht = target - h; float d0 = ht.Length;
            if (d0 < 1e-6f) return 0;
            Vec3 dir = ht / d0;
            float d = MathX.Clamp(d0, lo, hi); miss = Math.Max(miss, d0 - hi);
            // bend direction: the knee's offset from the hip-ankle line (fallback: the knee's own forward)
            Vec3 kn = (k - h); kn = kn - dir * Vec3.Dot(kn, dir);
            Vec3 fwd = (world[ll] * rig.RestWorld[ll].Conjugate) * Vec3.Forward; fwd = fwd - dir * Vec3.Dot(fwd, dir);
            Vec3 bend = (kn + fwd.Normalized * (0.02f * l1)).Normalized;
            if (bend.SqrLength < 1e-8f) return 0;
            float cosA = MathX.Clamp((l1 * l1 + d * d - l2 * l2) / (2 * l1 * d), -1, 1), sinA = (float)Math.Sqrt(Math.Max(0, 1 - cosA * cosA));
            Vec3 kNew = h + dir * (l1 * cosA) + bend * (l1 * sinA);
            Vec3 tgt = h + dir * d;
            var q1 = Quat.FromToRotation(k - h, kNew - h);
            var wUl = (q1 * world[ul]).Normalized;
            var wLl0 = (q1 * world[ll]).Normalized;
            var q2 = Quat.FromToRotation(q1 * (a - k), tgt - kNew);
            var wLl = (q2 * wLl0).Normalized;
            int pa = rig.Parent[ul];
            local[ul] = pa < 0 ? wUl : (world[pa].Conjugate * wUl).Normalized;
            local[ll] = (wUl.Conjugate * wLl).Normalized;
            local[ft] = (wLl.Conjugate * footWorld).Normalized;
            world[ul] = wUl; world[ll] = wLl; world[ft] = footWorld;
            pos[ll] = kNew; pos[ft] = tgt;
            int to = Toes[side];
            if (rig.Exists[to]) { world[to] = (footWorld * local[to]).Normalized; }
            return miss;
        }

        /// <summary>Facing (degrees about +Y) of a bone's world rotation: its turn from rest applied to forward.</summary>
        public static float Yaw(PoseRig rig, int bone, Quat world)
        {
            var f = (world * rig.RestWorld[bone].Conjugate) * Vec3.Forward;
            if (f.x * f.x + f.z * f.z < 1e-8f) return 0;
            return (float)(Math.Atan2(f.x, f.z) * MathX.Rad2Deg);
        }

        /// <summary>The leg's length (hip to knee to ankle) at rest, scale 1.</summary>
        public static float Length(PoseRig rig, int side) => rig.RestOffset[LowerLeg[side]].Length + rig.RestOffset[Foot[side]].Length;
    }

    // =====================================================================================================  foot lock

    public sealed class FootContact
    {
        /// <summary>0 = left foot, 1 = right foot.</summary>
        public int Side;
        /// <summary>First and last frame of the contact (inclusive).</summary>
        public int A, B;
        /// <summary>Where the ankle is pinned, and the foot's facing then.</summary>
        public Vec3 Pin;
        public float Yaw;
        public double Seconds(float fps) => (B - A + 1) / (double)fps;
    }

    public sealed class FootLockOptions
    {
        /// <summary>A foot touches the ground while it is less than this above its ground level (m, avatar size)...</summary>
        public float Height = 0.04f;
        /// <summary>...and moves slower than this (m/s, horizontally).</summary>
        public float Speed = 0.3f;
        /// <summary>Shortest contact (s); gaps shorter than MaxGap between contacts at the same spot are bridged.</summary>
        public double MinContact = 0.1, MaxGap = 0.08;
        /// <summary>How far a planted foot may creep before the contact is split into small steps (m).</summary>
        public float Radius = 0.03f;
        /// <summary>A pin the straight leg can't reach lifts the foot by up to this (m); further out, the frame isn't planted.</summary>
        public float MaxLift = 0.015f;
        /// <summary>Blend in / out around a contact (s).</summary>
        public double Blend = 0.12;
        public bool Left = true, Right = true;
        /// <summary>Also keep the foot from turning about the vertical axis while planted.</summary>
        public bool LockTurn = true;
        public float Strength = 1;

        public static FootLockOptions From(StripModifier m) => new FootLockOptions
        {
            Height = (float)m.Num("height", 0.04), Speed = (float)m.Num("speed", 0.3), MinContact = m.Num("minContact", 0.1), Blend = m.Num("blend", 0.12),
            Radius = (float)m.Num("radius", 0.03), MaxLift = (float)m.Num("maxLift", 0.015),
            Left = m.Flag("left", true), Right = m.Flag("right", true), LockTurn = m.Flag("lockTurn", true), Strength = (float)MathX.Clamp(m.Num("strength", 1), 0, 1),
        };
    }

    /// <summary>Foot contact detection, foot pinning with leg IK, and foot-slide measurement.</summary>
    public static class FootLock
    {
        public sealed class Result
        {
            public List<FootContact> Contacts = new List<FootContact>();
            public int FramesChanged, OutOfReach;
            public float MaxMiss;
        }

        /// <summary>Per-frame ankle position, foot yaw and "lowest point" height of one foot (FK of every frame).</summary>
        sealed class FootTrack { public Vec3[] Ankle, Hip; public float[] Low, Yaw, Reach; }

        static FootTrack[] Track(PoseFrames pf)
        {
            var rig = pf.Rig; int n = pf.Count;
            // rest heights of the ankle and toes (scale 1, root at the origin)
            var rp = new Vec3[HumanBones.Count]; var rw = new Quat[HumanBones.Count];
            var restLocal = new Quat[HumanBones.Count]; for (int b = 0; b < HumanBones.Count; b++) restLocal[b] = rig.Exists[b] ? rig.RestLocal[b] : Quat.Identity;
            rig.Fk(rig.RestHips, restLocal, 1, rp, rw);
            var tr = new FootTrack[2];
            for (int s = 0; s < 2; s++) tr[s] = new FootTrack { Ankle = new Vec3[n], Low = new float[n], Yaw = new float[n], Hip = new Vec3[n], Reach = new float[n] };
            for (int f = 0; f < n; f++)
            {
                var pos = pf.Fk(f, out var world);
                for (int s = 0; s < 2; s++)
                {
                    int ft = LegIK.Foot[s], to = LegIK.Toes[s];
                    if (!rig.Exists[ft]) continue;
                    var t = tr[s];
                    t.Ankle[f] = pos[ft];
                    int ul = LegIK.UpperLeg[s], ll = LegIK.LowerLeg[s];
                    t.Hip[f] = pos[ul]; t.Reach[f] = (pos[ll] - pos[ul]).Length + (pos[ft] - pos[ll]).Length;
                    float low = pos[ft].y - rp[ft].y * pf.Scale[f];
                    if (rig.Exists[to]) low = Math.Min(low, pos[to].y - rp[to].y * pf.Scale[f]);
                    t.Low[f] = low;
                    t.Yaw[f] = LegIK.Yaw(rig, ft, world[ft]);
                }
            }
            return tr;
        }

        /// <summary>Horizontal ankle speed (m/s) over +-2 frames.</summary>
        static float[] Speed(Vec3[] p, float fps)
        {
            int n = p.Length; var v = new float[n];
            for (int f = 0; f < n; f++)
            {
                int a = Math.Max(0, f - 2), b = Math.Min(n - 1, f + 2);
                if (b == a) continue;
                var d = p[b] - p[a];
                v[f] = (float)Math.Sqrt(d.x * d.x + d.z * d.z) * fps / (b - a);
            }
            return v;
        }

        static float Percentile(float[] v, double q)
        {
            if (v.Length == 0) return 0;
            var s = (float[])v.Clone(); Array.Sort(s);
            return s[MathX.Clamp((int)(q * (s.Length - 1)), 0, s.Length - 1)];
        }

        /// <summary>Finds the frames where each foot is planted (and where to pin it).</summary>
        public static List<FootContact> Detect(PoseFrames pf, FootLockOptions o)
        {
            var res = new List<FootContact>();
            if (pf.Count < 3) return res;
            var tracks = Track(pf);
            for (int s = 0; s < 2; s++)
            {
                if ((s == 0 && !o.Left) || (s == 1 && !o.Right) || !pf.Drives(LegIK.Foot[s])) continue;
                var t = tracks[s];
                var v = Speed(t.Ankle, pf.Fps);
                float ground = MathX.Clamp(Percentile(t.Low, 0.05), -0.2f, 0.2f);
                int n = pf.Count;
                var on = new bool[n];
                bool cur = false;
                for (int f = 0; f < n; f++)
                {
                    float h = t.Low[f] - ground;
                    cur = cur ? (h < o.Height * 1.5f && v[f] < o.Speed * 1.6f) : (h < o.Height && v[f] < o.Speed);
                    on[f] = cur;
                }
                // segments
                var segs = new List<FootContact>();
                for (int f = 0; f < n; f++)
                {
                    if (!on[f]) continue;
                    int a = f; while (f + 1 < n && on[f + 1]) f++;
                    segs.Add(new FootContact { Side = s, A = a, B = f });
                }
                foreach (var c in segs) Pin(c, t, v);
                // bridge short gaps between contacts at the same spot
                int maxGap = (int)Math.Round(o.MaxGap * pf.Fps);
                for (int i = segs.Count - 2; i >= 0; i--)
                {
                    var a = segs[i]; var b = segs[i + 1];
                    var d = a.Pin - b.Pin;
                    if (b.A - a.B - 1 <= maxGap && Math.Sqrt(d.x * d.x + d.z * d.z) < o.Radius)
                    { a.B = b.B; Pin(a, t, v); segs.RemoveAt(i + 1); }
                }
                // a foot that creeps further than Radius while "planted" (a slow shuffle or pivot) takes small steps:
                // split where it moves fastest until every part stays within Radius of its pin
                int minLen = Math.Max(2, (int)Math.Round(o.MinContact * pf.Fps));
                var done = new List<FootContact>();
                var todo = new Stack<FootContact>(segs);
                while (todo.Count > 0)
                {
                    var c = todo.Pop();
                    if (c.B - c.A + 1 < minLen) continue;
                    if (MaxDev(c, t) <= o.Radius) { done.Add(c); continue; }
                    int cut = -1; float best = -1;
                    for (int f = c.A + minLen / 2; f <= c.B - minLen / 2; f++) if (v[f] > best) { best = v[f]; cut = f; }
                    if (cut < 0) continue;  // too short to split and creeping: leave it alone
                    var l = new FootContact { Side = s, A = c.A, B = cut - 1 }; var r = new FootContact { Side = s, A = cut + 1, B = c.B };
                    Pin(l, t, v); Pin(r, t, v);
                    todo.Push(l); todo.Push(r);
                }
                // frames where the straight leg can't reach the pin (the hips moved away: lying down, a lunge) are not
                // planted: drop them, the blends carry the foot between what is left
                var reach = new List<FootContact>();
                foreach (var c in done)
                {
                    int f = c.A;
                    while (f <= c.B)
                    {
                        while (f <= c.B && Excess(c, t, f) > o.MaxLift) f++;
                        int a = f;
                        while (f <= c.B && Excess(c, t, f) <= o.MaxLift) f++;
                        if (f - a >= minLen)
                        {
                            var part = new FootContact { Side = s, A = a, B = f - 1 };
                            if (a == c.A && f - 1 == c.B) { part.Pin = c.Pin; part.Yaw = c.Yaw; } else Pin(part, t, v);
                            reach.Add(part);
                        }
                    }
                }
                res.AddRange(reach.OrderBy(c => c.A));
            }
            return res;
        }

        /// <summary>How far the pin is beyond the leg's reach at frame f (m).</summary>
        static float Excess(FootContact c, FootTrack t, int f) => (c.Pin - t.Hip[f]).Length - t.Reach[f] * 0.9995f;

        static float MaxDev(FootContact c, FootTrack t)
        {
            float m = 0;
            for (int f = c.A; f <= c.B; f++) { var d = t.Ankle[f] - c.Pin; m = Math.Max(m, (float)Math.Sqrt(d.x * d.x + d.z * d.z)); }
            return m;
        }

        static void Pin(FootContact c, FootTrack t, float[] v)
        {
            Vec3 sum = Vec3.Zero; double ws = 0, sx = 0, sz = 0;
            for (int f = c.A; f <= c.B; f++)
            {
                float w = 1f / (v[f] + 0.03f);
                sum += t.Ankle[f] * w; ws += w;
                double r = t.Yaw[f] * MathX.Deg2Rad; sx += Math.Sin(r) * w; sz += Math.Cos(r) * w;
            }
            c.Pin = sum / (float)ws;
            c.Yaw = (float)(Math.Atan2(sx, sz) * MathX.Rad2Deg);
        }

        /// <summary>Pins the planted feet (detected with <paramref name="o"/>) and re-solves the legs. Changes pf in place.</summary>
        public static Result Apply(PoseFrames pf, FootLockOptions o, List<FootContact> contacts = null)
        {
            var r = new Result { Contacts = contacts ?? Detect(pf, o) };
            int n = pf.Count;
            if (n < 3 || r.Contacts.Count == 0 || o.Strength <= 0) return r;
            int nb = Math.Max(1, (int)Math.Round(o.Blend * pf.Fps));
            var tr = Track(pf); // the original feet
            // per foot and frame: inside a contact = its pin; around contacts = the recorded foot plus the correction at
            // the contact's edge, fading in / out (between two close contacts: one correction blending into the next),
            // so the blends never add a jump or drag the foot back
            var mode = new byte[2][]; var pin = new Vec3[2][]; var off = new Vec3[2][]; var yawPin = new float[2][]; var yawOff = new float[2][];
            for (int s = 0; s < 2; s++)
            {
                mode[s] = new byte[n]; pin[s] = new Vec3[n]; off[s] = new Vec3[n]; yawPin[s] = new float[n]; yawOff[s] = new float[n];
                var cs = r.Contacts.Where(c => c.Side == s).OrderBy(c => c.A).ToList();
                if (cs.Count == 0) continue;
                var t = tr[s];
                foreach (var c in cs) for (int f = Math.Max(0, c.A); f <= Math.Min(n - 1, c.B); f++) { mode[s][f] = 1; pin[s][f] = c.Pin; yawPin[s][f] = c.Yaw; }
                Vec3 OffIn(FootContact c) => c.Pin - t.Ankle[c.A];
                Vec3 OffOut(FootContact c) => c.Pin - t.Ankle[c.B];
                float YIn(FootContact c) => MathX.DeltaAngle(t.Yaw[c.A], c.Yaw);
                float YOut(FootContact c) => MathX.DeltaAngle(t.Yaw[c.B], c.Yaw);
                void Put(int f, Vec3 v, float y, float w)
                {
                    if (f < 0 || f >= n || mode[s][f] == 1 || w <= 0) return;
                    mode[s][f] = 2; off[s][f] += v * w; yawOff[s][f] += y * w;
                }
                for (int i = 0; i <= cs.Count; i++)
                {
                    var prev = i > 0 ? cs[i - 1] : null; var next = i < cs.Count ? cs[i] : null;
                    int a = prev != null ? prev.B + 1 : Math.Max(0, next.A - nb), b = next != null ? next.A - 1 : Math.Min(n - 1, prev.B + nb);
                    int g = b - a + 1;
                    if (g <= 0) continue;
                    if (prev != null && next != null && g <= 2 * nb)
                    {
                        for (int f = a; f <= b; f++)
                        {
                            float x = MathX.SmoothStep((f - prev.B) / (float)(g + 1));
                            Put(f, Vec3.Lerp(OffOut(prev), OffIn(next), x), MathX.Lerp(YOut(prev), YIn(next), x), 1);
                        }
                        continue;
                    }
                    if (prev != null) for (int f = a; f <= Math.Min(b, prev.B + nb); f++) Put(f, OffOut(prev), YOut(prev), MathX.SmoothStep(1 - (f - prev.B) / (float)(nb + 1)));
                    if (next != null) for (int f = Math.Max(a, next.A - nb); f <= b; f++) Put(f, OffIn(next), YIn(next), MathX.SmoothStep(1 - (next.A - f) / (float)(nb + 1)));
                }
            }
            for (int f = 0; f < n; f++)
            {
                if (mode[0][f] == 0 && mode[1][f] == 0) continue;
                var pos = pf.Fk(f, out var world);
                bool changed = false;
                for (int s = 0; s < 2; s++)
                {
                    if (mode[s][f] == 0) continue;
                    int ft = LegIK.Foot[s];
                    float cur = LegIK.Yaw(pf.Rig, ft, world[ft]);
                    Vec3 target; float dy;
                    if (mode[s][f] == 1) { target = Vec3.Lerp(pos[ft], pin[s][f], o.Strength); dy = MathX.DeltaAngle(cur, yawPin[s][f]) * o.Strength; }
                    else { target = pos[ft] + off[s][f] * o.Strength; dy = yawOff[s][f] * o.Strength; }
                    var fw = world[ft];
                    if (o.LockTurn && Math.Abs(dy) > 1e-4f) fw = (Quat.YawRotation(dy) * fw).Normalized;
                    float miss = LegIK.Solve(pf.Rig, pf.Local[f], pos, world, s, target, fw, true);
                    if (miss > 1e-4f) { r.OutOfReach++; r.MaxMiss = Math.Max(r.MaxMiss, miss); }
                    changed = true;
                }
                if (changed) r.FramesChanged++;
            }
            return r;
        }

        /// <summary>Foot slide during the given contacts: mean horizontal distance of the ankle from where it sits on
        /// average during each contact, and the horizontal distance it travels per second of contact (cm).</summary>
        public static (double MeanDevCm, double PathCmPerS, double MaxDevCm, double Seconds) Measure(PoseFrames pf, List<FootContact> contacts)
        {
            if (contacts.Count == 0) return (0, 0, 0, 0);
            var tr = Track(pf);
            double dev = 0, path = 0, maxDev = 0; int cnt = 0; double secs = 0;
            foreach (var c in contacts)
            {
                var a = tr[c.Side].Ankle;
                double mx = 0, mz = 0; int m = c.B - c.A + 1;
                for (int f = c.A; f <= c.B; f++) { mx += a[f].x; mz += a[f].z; }
                mx /= m; mz /= m;
                for (int f = c.A; f <= c.B; f++)
                {
                    double d = Math.Sqrt((a[f].x - mx) * (a[f].x - mx) + (a[f].z - mz) * (a[f].z - mz));
                    dev += d; cnt++; maxDev = Math.Max(maxDev, d);
                    if (f > c.A) { var s = a[f] - a[f - 1]; path += Math.Sqrt(s.x * s.x + s.z * s.z); }
                }
                secs += c.Seconds(pf.Fps);
            }
            return (dev / Math.Max(1, cnt) * 100, path / Math.Max(1e-9, secs) * 100, maxDev * 100, secs);
        }
    }

    // =====================================================================================================  repair

    public static class PoseRepair
    {
        /// <summary>Repairs every marked range of frames (by pf.Time) for its body parts. Returns the frames changed.</summary>
        public static int Apply(PoseFrames pf, List<RepairRange> ranges, BodyPart defParts, bool hold, double blend, bool plantFeet)
        {
            int n = pf.Count, changed = 0;
            if (n < 2) return 0;
            int nb = Math.Max(1, (int)Math.Round(blend * pf.Fps));
            foreach (var rr in ranges.OrderBy(x => x.A))
            {
                var parts = rr.Parts ?? defParts;
                if (parts == BodyPart.None || rr.B <= rr.A) continue;
                // frames inside the range, and the good frames around it
                int kA = -1, kB = -1;
                for (int k = 0; k < n; k++) if (pf.Time[k] >= rr.A - 1e-6 && pf.Time[k] <= rr.B + 1e-6) { if (kA < 0) kA = k; kB = k; }
                if (kA < 0) continue;
                int k0 = kA - 1, k1 = kB + 1;
                if (k0 < 0 && k1 >= n) continue;
                bool[] bone = new bool[HumanBones.Count];
                for (int b = 0; b < HumanBones.Count; b++) bone[b] = pf.Drives(b) && (parts & BodyMask.PartOf(b)) != 0;
                bool root = (parts & BodyPart.Root) != 0;
                bool[] feet = { plantFeet && (parts & BodyPart.LeftLeg) != 0, plantFeet && (parts & BodyPart.RightLeg) != 0 };
                // anchors (copies: the range's frames change)
                var la = k0 >= 0 ? (Quat[])pf.Local[k0].Clone() : null; var lb = k1 < n ? (Quat[])pf.Local[k1].Clone() : null;
                Vec3 ha = k0 >= 0 ? pf.Hips[k0] : Vec3.Zero, hb = k1 < n ? pf.Hips[k1] : Vec3.Zero;
                Vec3[] ankA = new Vec3[2], ankB = new Vec3[2];
                if (feet[0] || feet[1])
                {
                    if (k0 >= 0) { var p = pf.Fk(k0, out _); ankA[0] = p[HumanBones.LeftFoot]; ankA[1] = p[HumanBones.RightFoot]; }
                    if (k1 < n) { var p = pf.Fk(k1, out _); ankB[0] = p[HumanBones.LeftFoot]; ankB[1] = p[HumanBones.RightFoot]; }
                    if (k0 < 0) { ankA = (Vec3[])ankB.Clone(); } if (k1 >= n) { ankB = (Vec3[])ankA.Clone(); }
                }
                // hips velocity at the anchors (for a smooth root path)
                Vec3 va = k0 >= 1 ? (pf.Hips[k0] - pf.Hips[k0 - 1]) : Vec3.Zero, vb = k1 + 1 < n ? (pf.Hips[k1 + 1] - pf.Hips[k1]) : Vec3.Zero;
                int span = (k1 < n ? k1 : kB + 1) - (k0 >= 0 ? k0 : kA - 1);
                int last = hold ? Math.Min(n - 1, kB + nb) : kB;
                for (int k = kA; k <= last; k++)
                {
                    var L = pf.Local[k];
                    float u = (float)(k - (k0 >= 0 ? k0 : kA - 1)) / span;
                    float wBack = hold && k > kB ? MathX.SmoothStep((k - kB) / (float)nb) : 0; // hold: blend back into the recorded motion
                    for (int b = 0; b < HumanBones.Count; b++)
                    {
                        if (!bone[b]) continue;
                        Quat v;
                        if (hold || lb == null) v = (la ?? lb)[b];
                        else if (la == null) v = lb[b];
                        else v = Quat.Slerp(la[b], lb[b], u);
                        L[b] = wBack > 0 ? Quat.Slerp(v, L[b], wBack) : v;
                    }
                    if (root)
                    {
                        Vec3 v;
                        if (hold || k1 >= n) v = k0 >= 0 ? ha : hb;
                        else if (k0 < 0) v = hb;
                        else v = Hermite(ha, va * span, hb, vb * span, u);
                        pf.Hips[k] = wBack > 0 ? Vec3.Lerp(v, pf.Hips[k], wBack) : v;
                    }
                    if (feet[0] || feet[1])
                    {
                        var pos = pf.Fk(k, out var world);
                        for (int s = 0; s < 2; s++)
                        {
                            if (!feet[s]) continue;
                            var tgt = hold ? ankA[s] : Vec3.Lerp(ankA[s], ankB[s], u);
                            if (wBack > 0) tgt = Vec3.Lerp(tgt, pos[LegIK.Foot[s]], wBack);
                            LegIK.Solve(pf.Rig, L, pos, world, s, tgt, world[LegIK.Foot[s]], true);
                        }
                    }
                    changed++;
                }
            }
            return changed;
        }

        static Vec3 Hermite(Vec3 p0, Vec3 m0, Vec3 p1, Vec3 m1, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return p0 * (2 * t3 - 3 * t2 + 1) + m0 * (t3 - 2 * t2 + t) + p1 * (-2 * t3 + 3 * t2) + m1 * (t3 - t2);
        }

        /// <summary>
        /// Stretches that look like tracking glitches in solved poses: a hand, foot or the head moving faster than
        /// <paramref name="maxSpeed"/> (m/s, scaled by the avatar size), or a limb bone turning faster than
        /// <paramref name="maxTurn"/> (deg/s). Padded and merged; parts = the body parts that glitched.
        /// </summary>
        public static List<RepairRange> FindGlitches(PoseFrames pf, float maxSpeed = 7f, float maxTurn = 1800f, double pad = 0.1, double merge = 0.25)
        {
            int n = pf.Count; var res = new List<RepairRange>();
            if (n < 3) return res;
            int[] ends = { HumanBones.LeftHand, HumanBones.RightHand, HumanBones.LeftFoot, HumanBones.RightFoot, HumanBones.Head };
            var prevP = new Vec3[ends.Length]; var prevW = new Quat[HumanBones.Count];
            var bad = new BodyPart[n];
            float sc = pf.Scale.Length > 0 ? Math.Max(0.05f, pf.Scale[0]) : 1;
            for (int f = 0; f < n; f++)
            {
                var pos = pf.Fk(f, out var world);
                if (f > 0)
                {
                    double dt = Math.Max(1e-4, Math.Abs(pf.Time[f] - pf.Time[f - 1]));
                    for (int i = 0; i < ends.Length; i++)
                        if (pf.Drives(ends[i]) && (pos[ends[i]] - prevP[i]).Length / dt > maxSpeed * sc) bad[f] |= BodyMask.PartOf(ends[i]);
                    for (int b = 0; b < HumanBones.Count; b++)
                    {
                        if (!pf.Drives(b) || HumanBones.IsFinger(b) || b == HumanBones.Hips) continue;
                        // the bone's turn relative to its parent
                        var q = pf.Local[f][b]; var qp = pf.Local[f - 1][b];
                        if (Quat.Angle(q, qp) / dt > maxTurn) bad[f] |= BodyMask.PartOf(b);
                    }
                }
                for (int i = 0; i < ends.Length; i++) prevP[i] = pos[ends[i]];
            }
            for (int f = 0; f < n; f++)
            {
                if (bad[f] == BodyPart.None) continue;
                int a = f; var parts = bad[f];
                while (f + 1 < n && bad[f + 1] != BodyPart.None) { f++; parts |= bad[f]; }
                var r = new RepairRange { A = pf.Time[a] - pad, B = pf.Time[f] + pad, Parts = parts, Note = "found" };
                if (res.Count > 0 && r.A - res[res.Count - 1].B < merge) { var l = res[res.Count - 1]; l.B = r.B; l.Parts = (l.Parts ?? 0) | parts; }
                else res.Add(r);
            }
            return res;
        }
    }

    // =====================================================================================================  smoothing

    public static class PoseSmooth
    {
        /// <summary>Zero-phase Gaussian smoothing (sigma in seconds) of the chosen parts' rotations, the hips position (Root)
        /// and face shapes (Face). Returns the number of channels smoothed.</summary>
        public static int Apply(PoseFrames pf, BodyPart parts, double sigma, Dictionary<string, float[]> face = null)
        {
            int n = pf.Count; if (n < 3 || sigma <= 1e-4) return 0;
            int r = Math.Max(1, (int)Math.Ceiling(3 * sigma * pf.Fps));
            var g = new float[r + 1];
            for (int i = 0; i <= r; i++) g[i] = (float)Math.Exp(-0.5 * (i / (sigma * pf.Fps)) * (i / (sigma * pf.Fps)));
            int count = 0;
            var buf = new Quat[n];
            for (int b = 0; b < HumanBones.Count; b++)
            {
                if (!pf.Drives(b) || (parts & BodyMask.PartOf(b)) == 0) continue;
                for (int f = 0; f < n; f++)
                {
                    var c = pf.Local[f][b];
                    float x = 0, y = 0, z = 0, w = 0, ws = 0;
                    for (int i = -r; i <= r; i++)
                    {
                        int k = MathX.Clamp(f + i, 0, n - 1);
                        var q = pf.Local[k][b]; if (Quat.Dot(q, c) < 0) q = q.Negated;
                        float gw = g[Math.Abs(i)];
                        x += q.x * gw; y += q.y * gw; z += q.z * gw; w += q.w * gw; ws += gw;
                    }
                    buf[f] = new Quat(x, y, z, w).Normalized;
                }
                for (int f = 0; f < n; f++) pf.Local[f][b] = buf[f];
                count++;
            }
            if ((parts & BodyPart.Root) != 0)
            {
                var hb = new Vec3[n];
                for (int f = 0; f < n; f++)
                {
                    Vec3 s = Vec3.Zero; float ws = 0;
                    for (int i = -r; i <= r; i++) { int k = MathX.Clamp(f + i, 0, n - 1); s += pf.Hips[k] * g[Math.Abs(i)]; ws += g[Math.Abs(i)]; }
                    hb[f] = s / ws;
                }
                Array.Copy(hb, pf.Hips, n);
                count++;
            }
            if ((parts & BodyPart.Face) != 0 && face != null)
                foreach (var arr in face.Values)
                {
                    if (arr.Length != n) continue;
                    var fb = new float[n];
                    for (int f = 0; f < n; f++)
                    {
                        float s = 0, ws = 0;
                        for (int i = -r; i <= r; i++) { int k = MathX.Clamp(f + i, 0, n - 1); s += arr[k] * g[Math.Abs(i)]; ws += g[Math.Abs(i)]; }
                        fb[f] = s / ws;
                    }
                    Array.Copy(fb, arr, n);
                    count++;
                }
            return count;
        }
    }

    // =====================================================================================================  the modifiers

    static class CleanupParams
    {
        public static ModParam Num(string key, string label, double def, double min, double max, string format, string suffix, double uiScale, string tip, bool slider = false) =>
            new ModParam { Key = key, Label = label, Default = def, Min = min, Max = max, Format = format, Suffix = suffix, UiScale = uiScale, Tip = tip, Slider = slider };
        public static ModParam Check(string key, string label, bool def, string tip) => new ModParam { Key = key, Label = label, Kind = ModParamKind.Check, Default = def ? 1 : 0, Tip = tip };
        public static ModParam Parts(string key, string label, BodyPart def, string tip) => new ModParam { Key = key, Label = label, Kind = ModParamKind.Parts, DefaultParts = def, Tip = tip };
        public static ModParam Choice(string key, string label, int def, string[] keys, string[] labels, string tip) => new ModParam { Key = key, Label = label, Kind = ModParamKind.Choice, Default = def, Choices = keys, ChoiceLabels = labels, Tip = tip };

        public static readonly ModParam[] FootLock =
        {
            Num("strength", "Strength", 1, 0, 1, "0%", "", 1, "How firmly the feet are held (100% = pinned)", true),
            Check("left", "Left foot", true, null), Check("right", "Right foot", true, null),
            Num("height", "Contact height", 0.04, 0.005, 0.3, "0.0", " cm", 100, "A foot is on the ground while its lowest point is less than this above its ground level (avatar size)"),
            Num("speed", "Contact speed", 0.3, 0.02, 3, "0", " cm/s", 100, "...and it moves slower than this horizontally"),
            Num("minContact", "Shortest contact", 0.1, 0.02, 2, "0.00", " s", 1, "Shorter touches are left as they are"),
            Num("radius", "Creep allowed", 0.03, 0.005, 0.3, "0.0", " cm", 100, "A planted foot that creeps further than this is taking small steps: the contact is split there"),
            Num("blend", "Blend", 0.12, 0.01, 1, "0.00", " s", 1, "Eases the correction in before and out after each contact"),
            Check("lockTurn", "Keep planted feet from turning", true, "Also holds the foot's facing while it is planted (tracker yaw drift)"),
        };

        public static readonly ModParam[] Repair =
        {
            Choice("mode", "Fill", 0, new[] { "interpolate", "hold" }, new[] { "Interpolate", "Hold, then blend back" }, "Interpolate between the good poses around each stretch, or hold the pose before it and blend back after"),
            Parts("parts", "Body parts", BodyMask.Body & ~BodyPart.Root, "Parts repaired in ranges that don't name their own"),
            Check("plantFeet", "Keep the feet where they were (leg IK)", true, "Legs: the ankles move in a straight line between the good poses instead of following the hips"),
            Num("blend", "Blend back", 0.2, 0.01, 2, "0.00", " s", 1, "Hold: how long the held pose takes to blend back into the recorded motion"),
            new ModParam { Key = "ranges", Label = "Ranges", Kind = ModParamKind.Ranges, Tip = "Stretches to repair (take seconds on a strip, scene seconds on an actor)" },
        };

        public static readonly ModParam[] Smooth =
        {
            Num("sigma", "Strength", 0.04, 0.005, 0.5, "0", " ms", 1000, "Gaussian smoothing width (zero phase: no lag)"),
            Parts("parts", "Body parts", BodyMask.Body, "What is smoothed (Root = the hips' path, Face = face shapes)"),
        };
    }

    /// <summary>Foot lock on one strip (its own poses, before layers).</summary>
    public sealed class FootLockModifier : IStripModifier, IActorModifier, IModifierParams
    {
        public string Type => "footlock";
        public string Label => "Foot lock";
        public string Help => "Keeps feet planted while they touch the ground (no foot sliding): contacts from the foot's height and speed, the leg re-solved with IK.";
        public IReadOnlyList<ModParam> Params => CleanupParams.FootLock;

        public void Apply(StripModifierContext c, StripModifier m, PoseRun run)
        {
            if (run.IsDelta || !run.HasBone[HumanBones.LeftFoot] && !run.HasBone[HumanBones.RightFoot]) return;
            FootLock.Apply(PoseFrames.OfRun(c, run), FootLockOptions.From(m));
        }

        public void Apply(ActorModifierContext c, StripModifier m, BakedClip clip)
        {
            var pf = PoseFrames.OfClip(c.Rig, clip);
            var r = FootLock.Apply(pf, FootLockOptions.From(m));
            if (r.FramesChanged > 0) pf.WriteBack(clip);
        }
    }

    /// <summary>Repairs marked stretches of bad tracking (pose level).</summary>
    public sealed class RepairModifier : IStripModifier, IActorModifier, IModifierParams
    {
        public string Type => "repair";
        public string Label => "Tracking repair";
        public string Help => "Fills marked stretches of bad tracking: the chosen body parts are interpolated (or held) between the good poses around each stretch, moving with the rest of the body.";
        public IReadOnlyList<ModParam> Params => CleanupParams.Repair;

        static void Run(PoseFrames pf, StripModifier m)
        {
            var ranges = m.Ranges();
            if (ranges.Count == 0) return;
            PoseRepair.Apply(pf, ranges, m.Parts("parts", BodyMask.Body & ~BodyPart.Root), m.Str("mode", "interpolate") == "hold", m.Num("blend", 0.2), m.Flag("plantFeet", true));
        }

        public void Apply(StripModifierContext c, StripModifier m, PoseRun run)
        {
            if (run.IsDelta) return;
            Run(PoseFrames.OfRun(c, run), m);
        }

        public void Apply(ActorModifierContext c, StripModifier m, BakedClip clip)
        {
            if (m.Ranges().Count == 0) return;
            var pf = PoseFrames.OfClip(c.Rig, clip, c.Scene?.Start ?? 0);
            Run(pf, m);
            pf.WriteBack(clip);
        }
    }

    /// <summary>Zero-phase smoothing.</summary>
    public sealed class SmoothModifier : IStripModifier, IActorModifier, IModifierParams
    {
        public string Type => "smooth";
        public string Label => "Smoothing";
        public string Help => "Smooths jitter out of the chosen body parts (zero phase: the motion doesn't lag).";
        public IReadOnlyList<ModParam> Params => CleanupParams.Smooth;

        public void Apply(StripModifierContext c, StripModifier m, PoseRun run)
        {
            if (run.IsDelta) return;
            PoseSmooth.Apply(PoseFrames.OfRun(c, run), m.Parts("parts", BodyMask.Body), m.Num("sigma", 0.04), run.Face);
        }

        public void Apply(ActorModifierContext c, StripModifier m, BakedClip clip)
        {
            var pf = PoseFrames.OfClip(c.Rig, clip);
            if (PoseSmooth.Apply(pf, m.Parts("parts", BodyMask.Body), m.Num("sigma", 0.04), clip.Face) > 0) pf.WriteBack(clip);
        }
    }
}
