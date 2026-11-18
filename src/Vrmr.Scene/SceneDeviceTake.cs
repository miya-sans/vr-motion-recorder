using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VRMR
{
    // =====================================================================================================  device-only takes
    //
    // A keyframe-only take animated through devices: an action strip on a POSE track whose action has device channels
    // (device/<Role>/position, /rotation, influence/<Role>). The body is solved by the same IK solver the takes use,
    // without a recording: the devices are calibrated on the avatar's T-pose (KeyRest.DevicesFor), hanging from the headset
    // at the avatar's eye height (tracking scale 1: device keys are in avatar metres, floor at y = 0), then fed frame by frame.
    //  - a device is present when its position channel has keys (the headset and controllers fall back to a standing pose);
    //  - rotation channel missing: the device keeps its calibration (T-pose) rotation;
    //  - influence/<Role> below 0.5: the device isn't tracked at that frame;
    //  - input tracks above still edit these devices (InputEdit) like a take's.

    public sealed partial class SceneEvaluator
    {
        readonly Dictionary<string, TakeSolve> _deviceSolves = new Dictionary<string, TakeSolve>();
        static int _deviceVersion = 1 << 28;

        /// <summary>Does this action animate devices (a keyframe-only take solved by the IK when it is on a pose track)?</summary>
        public static bool IsDeviceAction(KeyAction act) =>
            act != null && act.Channels.Values.Any(c => c.Keys.Count > 0 && Channel.DeviceOf(c.Path, out _, out var k) && k == "position");

        TakeSolve SolveDeviceAction(Scene s, Actor a, int ti, Strip st, KeyAction act, ActorPlan plan, out bool pending)
        {
            pending = false;
            var track = a.Tracks[ti];
            double hIn = track.TransitionInto(st.Id)?.Duration / 2 ?? 0, hOut = track.TransitionOutOf(st.Id)?.Duration / 2 ?? 0;
            double ta = st.Start - hIn, tb = st.End + hOut;
            // input tracks above edit these devices too
            var edit = BuildEdit(s, a, ti, st, null, plan, ta, tb, out bool waiting, out string editKey);
            if (waiting) pending = true;
            float fps = s.Fps, scale = plan.Scale;
            double sa = Math.Min(st.ToSource(ta), st.ToSource(tb)) - 0.5, sb = Math.Max(st.ToSource(ta), st.ToSource(tb)) + 0.5;
            string key = string.Join("|", st.Id, Json.Serialize(act.ToJson()).GetHashCode(), sa.ToString("R"), sb.ToString("R"), fps, scale, plan.Profile.Name, plan.Profile.Nodes.Count, editKey ?? "");
            if (_deviceSolves.TryGetValue(key, out var cached)) return cached;

            var prof = plan.Profile; var rig = plan.Rig;
            var solver = BodySolvers.Factory?.Invoke();
            if (solver == null) return null;
            float eye = Math.Max(0.1f, prof.EyeHeight * scale);
            solver.Init(prof, 1f, prof.EyeHeight);
            var cal = new CalibrationSettings();
            solver.Configure(cal);

            // calibration: devices on the T-pose
            var tpose = new ScenePose(); KeyRest.TPose(rig, scale, tpose);
            var calDev = KeyRest.DevicesFor(rig, scale, tpose);
            var standing = new ScenePose(); KeyRest.Standing(rig, scale, standing);
            var standDev = KeyRest.DevicesFor(rig, scale, standing, KeyRest.ThreePoint);
            var roles = (BodyRole[])Enum.GetValues(typeof(BodyRole));
            var pc = new Channel[roles.Length]; var rc = new Channel[roles.Length]; var ic = new Channel[roles.Length];
            for (int i = 0; i < roles.Length; i++)
            {
                pc[i] = act.Get(Channel.DevicePosition(roles[i])); if (pc[i] != null && pc[i].Keys.Count == 0) pc[i] = null;
                rc[i] = act.Get(Channel.DeviceRotation(roles[i])); if (rc[i] != null && rc[i].Keys.Count == 0) rc[i] = null;
                ic[i] = act.Get(Channel.Influence(roles[i])); if (ic[i] != null && ic[i].Keys.Count == 0) ic[i] = null;
            }
            bool Present(int i) => pc[i] != null || roles[i] == BodyRole.Head || roles[i] == BodyRole.LeftHand || roles[i] == BodyRole.RightHand;
            Pose Eye(Pose headset) { float k = eye / FbikAdapter.VrcHandRefEye; return new Pose(headset.pos + headset.rot * new Vec3(0, 0, -TrackingRig.VrcEyeBehindHeadset * k), headset.rot); }
            foreach (bool left in new[] { true, false }) solver.SetControllerWrist(left, TrackingRig.DefaultWrist(left), TrackingRig.DefaultWristRot(left));
            for (int i = 0; i < roles.Length; i++)
            {
                var r = roles[i];
                bool has = Present(i) && calDev.TryGetValue(r, out var p);
                calDev.TryGetValue(r, out p);
                if (r == BodyRole.Head) p = Eye(p);
                solver.SetDevice(r, p, has, r == BodyRole.LeftHand || r == BodyRole.RightHand ? HandSourceKind.Index : HandSourceKind.Generic);
            }
            solver.Calibrate();

            // frames on the action's own clock (TakeSolve frames are spaced 1/fps in source time)
            int n = Math.Max(2, (int)Math.Ceiling((sb - sa) * fps) + 1);
            var clip = new BakedClip { Profile = prof, Fps = fps, Frames = n, SourceTime = new double[n], Hips = new Vec3[n], Rot = new Quat[n][], BoneExists = rig.Exists, BodySource = "ik", Scale = scale };
            for (int i = 0; i < roles.Length; i++) if (pc[i] != null || Present(i)) { clip.Trackers[roles[i].ToString()] = new Pose[n]; clip.TrackerValid[roles[i].ToString()] = new bool[n]; }
            if (ic.Any(c => c != null)) { clip.TrackerInfluence = new Dictionary<string, float[]>(); for (int i = 0; i < roles.Length; i++) if (ic[i] != null) clip.TrackerInfluence[roles[i].ToString()] = new float[n]; }
            var buf = new float[4];
            var pos = new Vec3[HumanBones.Count]; var rot = new Quat[HumanBones.Count];
            float dt = 1f / fps;
            void Feed(double src, int f)
            {
                for (int i = 0; i < roles.Length; i++)
                {
                    var r = roles[i];
                    if (!Present(i)) { solver.SetDevice(r, Pose.Identity, false, HandSourceKind.Generic); continue; }
                    calDev.TryGetValue(r, out var p);
                    if (pc[i] != null) { pc[i].Evaluate(src, buf); p.pos = new Vec3(buf[0], buf[1], buf[2]); }
                    else if (standDev.TryGetValue(r, out var sp)) p = sp;
                    if (rc[i] != null) { rc[i].Evaluate(src, buf); p.rot = new Quat(buf[0], buf[1], buf[2], buf[3]).Normalized; }
                    bool ok = true;
                    float inf = 1;
                    if (ic[i] != null) { ic[i].Evaluate(src, buf); inf = MathX.Clamp01(buf[0]); ok = inf >= 0.5f; }
                    if (edit != null) { var e = edit.Apply(r, src, p, ok); p = e.Item1; ok = e.Item2; inf *= edit.Influence(r, st.ToScene(src)); ok &= inf >= 0.5f; }
                    if (f >= 0)
                    {
                        string name = r.ToString();
                        if (clip.Trackers.TryGetValue(name, out var tp)) { tp[f] = p; clip.TrackerValid[name][f] = true; }
                        if (clip.TrackerInfluence != null && clip.TrackerInfluence.TryGetValue(name, out var ti2)) ti2[f] = inf;
                    }
                    solver.SetDevice(r, r == BodyRole.Head ? Eye(p) : p, ok, r == BodyRole.LeftHand || r == BodyRole.RightHand ? HandSourceKind.Index : HandSourceKind.Generic);
                }
            }
            for (int k = 30; k > 0; k--) { Feed(sa - k * dt, -1); solver.Solve(dt); } // settle
            var exists = prof.HumanExists;
            for (int f = 0; f < n; f++)
            {
                double src = sa + f / (double)fps;
                clip.SourceTime[f] = src;
                Feed(src, f);
                solver.Solve(dt);
                solver.GetPose(pos, rot);
                var r = new Quat[HumanBones.Count]; var driven = new bool[HumanBones.Count];
                for (int b = 0; b < HumanBones.Count; b++)
                {
                    bool okb = exists[b] && rot[b].IsFinite && rot[b].Norm > 0.5f && !HumanBones.IsFinger(b) && b != HumanBones.LeftEye && b != HumanBones.RightEye && b != HumanBones.Jaw;
                    r[b] = okb ? rot[b].Normalized : Quat.Identity;
                    driven[b] = okb;
                }
                PoseBuilder.FillUndriven(prof, r, driven);
                clip.Rot[f] = r;
                clip.Hips[f] = pos[HumanBones.Hips].IsFinite ? pos[HumanBones.Hips] : prof.HumanRestPos(HumanBones.Hips) * scale;
            }
            var local = new Quat[n][];
            for (int f = 0; f < n; f++) { local[f] = new Quat[HumanBones.Count]; rig.ToLocal(clip.Rot[f], local[f]); }
            var sv = new TakeSolve { Path = "", Key = key, Slot = st.Id + "|device", Clip = clip, Rig = rig, Local = local, Fps = fps, Start = sa, Edit = edit, Version = System.Threading.Interlocked.Increment(ref _deviceVersion) };
            foreach (var k2 in _deviceSolves.Where(kv => kv.Value.Slot == sv.Slot).Select(kv => kv.Key).ToList()) _deviceSolves.Remove(k2);
            _deviceSolves[key] = sv;
            return sv;
        }
    }
}
