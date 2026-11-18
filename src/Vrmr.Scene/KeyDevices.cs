using System;
using System.Collections.Generic;
using System.Linq;

namespace VRMR
{
    /// <summary>Devices (headset, controllers, trackers) at one scene frame: poses in solve space, valid flags, influence.</summary>
    public sealed class DeviceFrame
    {
        public readonly Dictionary<BodyRole, Pose> Pose = new Dictionary<BodyRole, Pose>();
        /// <summary>Tracked / present (a keyframe-only take's devices without keys are listed, not valid: drag one to add it).</summary>
        public readonly Dictionary<BodyRole, bool> Valid = new Dictionary<BodyRole, bool>();
        /// <summary>How much each device drives the solve (product over input layers; 1 = as recorded).</summary>
        public readonly Dictionary<BodyRole, float> Influence = new Dictionary<BodyRole, float>();
        /// <summary>Solve space -> scene space (the base strip's root offset); devices are drawn at ToScene * Pose.</summary>
        public Pose ToScene = VRMR.Pose.Identity;
        /// <summary>No take under the layer: a keyframe-only (device) take, or nothing yet.</summary>
        public bool KeyframeOnly;
        /// <summary>The strip the devices come from (a take, or a device take's keyframe strip), or null.</summary>
        public Strip Base;
        public int BaseTrack = -1;

        public bool Has(BodyRole r) => Valid.TryGetValue(r, out bool v) && v;
        public float InfluenceOf(BodyRole r) => Influence.TryGetValue(r, out float v) ? v : 1f;
        public Pose Scene(BodyRole r) => ToScene * Pose[r];

        public void Clear() { Pose.Clear(); Valid.Clear(); Influence.Clear(); ToScene = VRMR.Pose.Identity; KeyframeOnly = false; Base = null; BaseTrack = -1; }

        public DeviceFrame Clone()
        {
            var d = new DeviceFrame { ToScene = ToScene, KeyframeOnly = KeyframeOnly, Base = Base, BaseTrack = BaseTrack };
            foreach (var kv in Pose) d.Pose[kv.Key] = kv.Value;
            foreach (var kv in Valid) d.Valid[kv.Key] = kv.Value;
            foreach (var kv in Influence) d.Influence[kv.Key] = kv.Value;
            return d;
        }
    }

    /// <summary>
    /// The device stack of an actor at a scene frame, below a track (what a device layer edits on top of), with the same
    /// rules as the evaluator (SceneInputs / SceneDeviceTake): the base is the topmost take or device take (a keyframe
    /// strip with device keys on a pose track) under the limit; input action strips on tracks above that base and below
    /// the limit are applied (Replace / Add, strip influence and blend ramps, influence keys multiplied). Used by the
    /// posing tool and the action inspector: immediate, without waiting for the re-solve.
    /// </summary>
    public static class KeyDevices
    {
        public static readonly BodyRole[] Roles = (BodyRole[])Enum.GetValues(typeof(BodyRole));

        /// <summary>Does the action key devices (position keys: a device take on a pose track)?</summary>
        public static bool IsDeviceAction(KeyAction act) =>
            act != null && act.Channels.Values.Any(c => c.Keys.Count > 0 && Channel.DeviceOf(c.Path, out _, out var k) && k == "position");

        static bool Covers(Strip x, double time) => !x.Mute && time >= x.Start - 1e-9 && time <= x.End + 1e-9;

        /// <summary>Weight of a strip at scene time (influence, blend in / out ramps); 0 outside it.</summary>
        public static float StripWeight(Strip st, double time)
        {
            if (!Covers(st, time)) return 0;
            float w = st.Influence;
            if (st.BlendIn > 1e-6 && time < st.Start + st.BlendIn) w *= MathX.SmoothStep((float)((time - st.Start) / st.BlendIn));
            if (st.BlendOut > 1e-6 && time > st.End - st.BlendOut) w *= MathX.SmoothStep((float)((st.End - time) / st.BlendOut));
            return w;
        }

        static bool ThreePoint(BodyRole r) => r == BodyRole.Head || r == BodyRole.LeftHand || r == BodyRole.RightHand;

        /// <summary>
        /// Devices below track index trackLimit at scene frame f, from the evaluator (SceneEvaluator.EvaluateDevices: the
        /// base take / device take with the input strips between it and the limit, per-device weights and modifiers
        /// included). A device take also lists the devices it doesn't use yet (not valid), placed on the body as it is now;
        /// its headset and controllers fall back to the standing pose like the solve. False while solving.
        /// </summary>
        public static bool Below(SceneEvaluator ev, Scene s, Actor a, int trackLimit, int f, DeviceFrame o)
        {
            o.Clear();
            double time = s.Start + f / (double)s.Fps;
            var (rig, scale) = ev.RigOf(s, a);
            bool anySolo = a.Tracks.Any(t => t.Solo);
            int lim = Math.Min(trackLimit, a.Tracks.Count);
            // the base (same rule as the evaluator): the topmost take / device take on a pose track below the limit
            Strip bs = null; int bti = -1;
            for (int ti = lim - 1; ti >= 0 && bs == null; ti--)
            {
                var tr = a.Tracks[ti];
                if (tr.Mute || (anySolo && !tr.Solo) || tr.Kind != TrackKind.Pose) continue;
                bs = tr.Strips.FirstOrDefault(x => Covers(x, time) && (x.Kind == StripKind.Take || (x.Kind == StripKind.Action && IsDeviceAction(s.Action(x.Source)))));
                if (bs != null) bti = ti;
            }
            o.Base = bs; o.BaseTrack = bti;
            o.ToScene = bs?.Offset ?? Pose.Identity;
            var pose = new Dictionary<BodyRole, Pose>(); var inf = new Dictionary<BodyRole, float>();
            bool ready = ev.EvaluateDevices(s, a, f, trackLimit, pose, inf, bs?.Id);
            if (bs == null || bs.Kind != StripKind.Take)
            {
                // a device take (or nothing yet): headset and controllers stand, the rest sit on the body
                o.KeyframeOnly = true;
                var stand = new ScenePose(); KeyRest.Standing(rig, scale, stand);
                foreach (var kv in KeyRest.DevicesFor(rig, scale, stand)) { o.Pose[kv.Key] = kv.Value; o.Valid[kv.Key] = bs != null && ThreePoint(kv.Key); o.Influence[kv.Key] = 1f; }
            }
            foreach (var kv in pose) { o.Pose[kv.Key] = kv.Value; o.Valid[kv.Key] = true; }
            foreach (var kv in inf) o.Influence[kv.Key] = kv.Value;
            foreach (var r in o.Pose.Keys.ToList()) if (!o.Influence.ContainsKey(r)) o.Influence[r] = 1f;
            if (o.KeyframeOnly && o.Valid.Any(kv => !kv.Value))
            {
                var now = new ScenePose();
                if (ev.EvaluateFrame(s, a, f, now))
                {
                    var invS = o.ToScene.Inverse;
                    foreach (var kv in KeyRest.DevicesFor(rig, scale, now, Roles.Where(r => !o.Has(r)))) o.Pose[kv.Key] = invS * kv.Value;
                }
            }
            return ready;
        }

        /// <summary>Applies one input action strip (at action time at, weight w) onto the devices (as SceneInputs.InputEdit does).</summary>
        public static void Apply(KeyAction act, Strip st, double at, float w, DeviceFrame o, bool absolute = false)
        {
            bool add = st.Blend == BlendMode.Add && !absolute;
            var buf = new float[4];
            float w0 = w;
            foreach (var r in Roles)
            {
                if ((st.Inputs & InputMask.PartOf(r)) == 0) continue;
                w = w0 * st.InputWeight(r);
                if (w <= 1e-4f) continue;
                bool ok = o.Has(r);
                var pc = act.Get(Channel.DevicePosition(r)); var rc = act.Get(Channel.DeviceRotation(r));
                bool keyed = (pc != null && pc.Keys.Count > 0) || (rc != null && rc.Keys.Count > 0);
                if (keyed && !(add && !ok))
                {
                    var p = o.Pose.TryGetValue(r, out var bp) ? bp : Pose.Identity;
                    if (pc != null && pc.Keys.Count > 0)
                    {
                        pc.Evaluate(at, buf);
                        var v = new Vec3(buf[0], buf[1], buf[2]);
                        p.pos = add ? p.pos + v * w : Vec3.Lerp(p.pos, v, w);
                        if (!add && w >= 0.5f) ok = true;
                    }
                    if (rc != null && rc.Keys.Count > 0)
                    {
                        rc.Evaluate(at, buf);
                        var q = new Quat(buf[0], buf[1], buf[2], buf[3]).Normalized;
                        p.rot = add ? (Quat.Slerp(Quat.Identity, q, w) * p.rot).Normalized : Quat.Slerp(p.rot, q, w).Normalized;
                    }
                    o.Pose[r] = p;
                    o.Valid[r] = ok;
                    if (!o.Influence.ContainsKey(r)) o.Influence[r] = 1f;
                }
                var ic = act.Get(Channel.Influence(r));
                if (ic != null && ic.Keys.Count > 0)
                {
                    ic.Evaluate(at, buf);
                    o.Influence[r] = o.InfluenceOf(r) * (1 - w * (1 - MathX.Clamp01(buf[0])));
                }
            }
        }

        /// <summary>
        /// Devices as a keyframe strip makes them: the stack below its track plus the strip at full weight (an input
        /// strip: Replace / Add; a device take: the base itself).
        /// </summary>
        public static bool With(SceneEvaluator ev, Scene s, Actor a, int trackIndex, Strip st, int f, DeviceFrame o)
        {
            var tr = trackIndex >= 0 && trackIndex < a.Tracks.Count ? a.Tracks[trackIndex] : null;
            if (tr != null && tr.Kind == TrackKind.Pose) return Below(ev, s, a, trackIndex + 1, f, o); // a device take is its own base
            bool ok = Below(ev, s, a, trackIndex, f, o);
            var act = s.Action(st.Source);
            double time = s.Start + f / (double)s.Fps;
            if (act != null) Apply(act, st, st.ToSource(time), 1f, o);
            return ok;
        }
    }
}
