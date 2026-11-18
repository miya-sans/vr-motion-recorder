using System;
using System.Collections.Generic;
using System.Linq;

namespace VRMR
{
    /// <summary>
    ///     The live raw SteamVR -> VRChat world mapping (VRChat = s * yaw * raw + T), estimated from the last few seconds of a
    ///     recording in progress, so the live VRChat IK skeleton (HUMR, VRChat world) can be drawn over the live SteamVR
    ///     devices (raw playspace). VRChat's world differs from the playspace by its spawn point, thumbstick moves and turns,
    ///     and by the tracking scale (avatar size / user size), so the skeleton was drawn somewhere else and at avatar size.
    ///     * yaw: VRChat's head bone facing vs the headset's (median of the window);
    ///     * s: VRChat hand-to-hand distance vs controller-to-controller distance while the hands are apart, else VRChat's
    ///     avatar eye height vs the user's eye height (0.94 x User Real Height);
    ///     * T: VRChat's head bone vs the scaled, turned headset, with VRChat's measured head offset (0, -4.1, -4.0) cm.
    ///     HUMR frames trail SteamVR by about 0.1 s (measured on test takes): the devices are sampled that much later.
    /// </summary>
    public sealed class LiveHumrMap
    {
        public static double Lag = 0.1, Window = 3.0;

        static readonly Vec3
            HeadOffsetVrc =
                new(0f, -0.041f, -0.040f); // head bone in the headset frame, VRChat units (one test avatar, eye 1.097)

        public float Scale = 1f, Yaw;
        public Vec3 T;
        public bool Valid;
        double _lastHumrT = -1, _lastUpdate = -1;

        /// <summary>Re-estimates from the recording (call each live frame; cheap when nothing new arrived).</summary>
        public void Update(Recording rec, AvatarProfile prof, float avatarEye, float userEye)
        {
            Update(rec, prof, avatarEye, userEye, double.MaxValue);
        }

        /// <summary>As if live at take time <paramref name="now" /> (tests).</summary>
        public void Update(Recording rec, AvatarProfile prof, float avatarEye, float userEye, double now)
        {
            if (rec == null || prof == null || !prof.Has(HumanBones.Head))
            {
                Valid = false;
                return;
            }

            PoseStream hmd, cl, cr;
            int n;
            double last;
            lock (rec.Sync)
            {
                n = rec.Humr.Count;
                while (n > 0 && rec.Humr.T[n - 1] > now) n--;
                if (n < 3)
                {
                    Valid = false;
                    return;
                }

                last = rec.Humr.T[n - 1];
                hmd = rec.Devices.Where(d => d.DeviceClass == 1 && d.Count > 2).OrderByDescending(d => d.Count)
                    .FirstOrDefault();
                cl = rec.Devices.FirstOrDefault(d => d.DeviceClass == 2 && d.ControllerRole == 1 && d.Count > 2);
                cr = rec.Devices.FirstOrDefault(d => d.DeviceClass == 2 && d.ControllerRole == 2 && d.Count > 2);
            }

            if (hmd == null)
            {
                Valid = false;
                return;
            }

            if (last == _lastHumrT && Valid) return;
            if (Valid && last - _lastUpdate < 0.25) return; // a few times a second is plenty
            _lastHumrT = last;
            _lastUpdate = last;
            var clip = new BakedClip
            {
                Profile = prof, Fps = 1, Frames = 1, BoneExists = prof.HumanExists, Scale = 1, Hips = new Vec3[1],
                Rot = new Quat[1][]
            };
            var wp = new Vec3[prof.Nodes.Count];
            var wr = new Quat[prof.Nodes.Count];
            var yaws = new List<float>();
            var spans = new List<float>();
            var frames = new List<Tuple<Vec3, Quat, Pose>>();
            var
                pts = new List<Tuple<Vec3, Pose, Vec3, Vec3>>(); // device position, device pose (rotation), bone offset in the device frame (VRChat units), bone (VRChat)
            float ek = avatarEye > 0.2f
                ? avatarEye / FbikAdapter.VrcHandRefEye
                : 1f; // the offset was measured on one test avatar (eye 1.097)
            for (int i = n - 1; i >= 0; i -= 2)
            {
                double t;
                Vec3 hips;
                Quat[] rot;
                lock (rec.Sync)
                {
                    t = rec.Humr.T[i];
                    if (last - t > Window) break;
                    hips = rec.Humr.Hips[i];
                    rot = (Quat[])rec.Humr.Rot[i].Clone();
                }

                bool ok;
                Vec3 hp;
                Quat hq;
                Vec3 lp = Vec3.Zero, rp = Vec3.Zero;
                Quat lq = Quat.Identity, rq = Quat.Identity;
                bool hl = false, hr = false;
                lock (rec.Sync)
                {
                    ok = hmd.Sample(t + Lag, out hp, out hq, 0.1);
                    if (cl != null) hl = cl.Sample(t + Lag, out lp, out lq, 0.1);
                    if (cr != null) hr = cr.Sample(t + Lag, out rp, out rq, 0.1);
                }

                if (!ok) continue;
                for (int b = 0; b < HumanBones.Count; b++)
                    rot[b] = rot[b].Norm > 0.5f ? rot[b].Normalized : prof.HumanRestRot(b);
                clip.Hips[0] = hips;
                clip.Rot[0] = rot;
                clip.EvaluateNodes(0, wp, wr);
                var head = wp[prof.Human[HumanBones.Head]];
                var headRot =
                    wr[prof.Human[HumanBones.Head]] *
                    prof.HumanRestRot(HumanBones.Head).Conjugate; // avatar head facing
                yaws.Add(MathX.DeltaAngle(hq.Yaw, headRot.Yaw));
                if (hl && hr && prof.Has(HumanBones.LeftHand) && prof.Has(HumanBones.RightHand))
                {
                    // hand bones sit ~17 real cm behind the controllers: compare them with the hand points, not the controllers
                    float s0 = userEye > 0.5f && avatarEye > 0.2f ? avatarEye / userEye : 0.62f;
                    var off = FbikAdapter.VrcIndexHandPosLeft * (ek / s0);
                    var tl = lp + lq * off;
                    var tr = rp + rq * new Vec3(-off.x, off.y, off.z);
                    float raw = (tl - tr).Length,
                        vrc = (wp[prof.Human[HumanBones.LeftHand]] - wp[prof.Human[HumanBones.RightHand]]).Length;
                    if (raw > 0.6f) spans.Add(vrc / raw);
                }

                frames.Add(Tuple.Create(head, hq, new Pose(hp, hq)));
                pts.Add(Tuple.Create(hp, new Pose(hp, hq), HeadOffsetVrc, head));
                if (hl && prof.Has(HumanBones.LeftHand))
                    pts.Add(Tuple.Create(lp, new Pose(lp, lq), FbikAdapter.VrcIndexHandPosLeft,
                        wp[prof.Human[HumanBones.LeftHand]]));
                if (hr && prof.Has(HumanBones.RightHand))
                    pts.Add(Tuple.Create(rp, new Pose(rp, rq),
                        new Vec3(-FbikAdapter.VrcIndexHandPosLeft.x, FbikAdapter.VrcIndexHandPosLeft.y,
                            FbikAdapter.VrcIndexHandPosLeft.z), wp[prof.Human[HumanBones.RightHand]]));
            }

            if (frames.Count < 2)
            {
                Valid = false;
                return;
            }

            yaws.Sort();
            Yaw = yaws[yaws.Count / 2];
            float sFallback = userEye > 0.5f && avatarEye > 0.2f ? avatarEye / userEye : Scale;
            if (spans.Count >= 5)
            {
                spans.Sort();
                Scale = spans[spans.Count / 2];
            }
            else Scale = sFallback;

            // yaw + translation fitted on the head and both hands together (VRChat's measured offsets on the devices), with
            // the points more than 12 VRChat cm off dropped once (calibration mode, a turn inside the window)
            var a = new List<Vec3>();
            var v = new List<Vec3>();
            foreach (var f in pts)
            {
                a.Add(f.Item1 * Scale + f.Item2.rot * (f.Item3 * ek));
                v.Add(f.Item4);
            }

            bool[] keep = Enumerable.Repeat(true, a.Count).ToArray();
            for (int pass = 0; pass < 2; pass++)
            {
                Vec3 ma = Vec3.Zero, mv = Vec3.Zero;
                int c = 0;
                for (int k = 0; k < a.Count; k++)
                    if (keep[k])
                    {
                        ma += a[k];
                        mv += v[k];
                        c++;
                    }

                if (c < 3) break;
                ma = ma * (1f / c);
                mv = mv * (1f / c);
                double sxx = 0, sxy = 0;
                for (int k = 0; k < a.Count; k++)
                {
                    if (!keep[k]) continue;
                    var p0 = a[k] - ma;
                    var p1 = v[k] - mv;
                    sxx += p0.x * p1.x + p0.z * p1.z;
                    sxy += p0.z * p1.x - p0.x * p1.z;
                }

                Yaw = (float)(Math.Atan2(sxy, sxx) * 180 / Math.PI);
                var R = Quat.YawRotation(Yaw);
                T = mv - R * ma;
                for (int k = 0; k < a.Count; k++) keep[k] = (v[k] - (R * a[k] + T)).Length < 0.12f;
            }

            Valid = true;
        }

        static float Med(IEnumerable<float> v)
        {
            var l = v.OrderBy(x => x).ToList();
            return l[l.Count / 2];
        }

        /// <summary>VRChat world -> raw playspace.</summary>
        public Vec3 ToRaw(Vec3 v)
        {
            return Quat.YawRotation(-Yaw) * (v - T) * (1f / Scale);
        }

        public Quat ToRaw(Quat q)
        {
            return (Quat.YawRotation(-Yaw) * q).Normalized;
        }
    }
}
