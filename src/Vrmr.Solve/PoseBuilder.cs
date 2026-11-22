using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VRMR
{
    public enum HandSourceKind { Unknown, Index, ViveWand, OculusTouch, Generic, VrcWrist }

    /// <summary>Full-body IK solver used for "independent" (any world) body reconstruction.</summary>
    public interface IBodySolver
    {
        void Init(AvatarProfile profile, float avatarScale, float realEyeHeight);

        /// <summary>Calibration options that change how the solver runs (spine lock, shoulders, procedural feet).</summary>
        void Configure(CalibrationSettings cal);

        void SetDevice(BodyRole role, Pose pose, bool tracked, HandSourceKind kind);

        /// <summary>Raw SteamVR skeletal wrist bone (parent/controller space, OpenVR right-handed) for controller-driven hands.</summary>
        void SetControllerWrist(bool left, Vec3 rawPos, Quat rawRot);

        void Calibrate();
        void Solve(float dt);

        /// <summary>World position/rotation per HumanBodyBones (55).</summary>
        void GetPose(Vec3[] pos, Quat[] rot);
    }

    public static class BodySolvers
    {
        public static Func<IBodySolver> Factory;
    }

    public sealed class BakeOptions
    {
        /// <summary>Move / turn the solved clip by the take's placement (Edits.Placement). Scenes place strips themselves.</summary>
        public bool ApplyPlacement = true;

        /// <summary>Calibration settings for the IK solve (null = the clip's own, else defaults).</summary>
        public CalibrationSettings Calibration;

        /// <summary>
        ///     Scene editor (input tracks): changes the devices the IK solve is fed (TrackingRig.DeviceEdit). Null = as
        ///     recorded.
        /// </summary>
        public Func<BodyRole, double, Pose, bool, (Pose, bool)> DeviceEdit;

        /// <summary>Scene editor: devices not used at all in this solve (a device's influence off).</summary>
        public HashSet<BodyRole> DisabledRoles;

        public float EyeYaw = 30, EyePitch = 20;
        public bool EyesFromTracking = true;
        public FaceMapping.Naming FaceNaming = FaceMapping.Naming.UnifiedExpressions;
        public bool FingersFromGestures = true;
        public float Fps = 60;
        public bool IncludeBinaryParams = false;
        public float Smoothing = 0.25f;

        /// <summary>
        ///     Solve this source range (take seconds {start, end}) instead of the take's edited timeline (trims and cuts
        ///     ignored): scenes solve whole takes.
        /// </summary>
        public double[] SourceRange;
    }

    /// <summary>Resolves a Recording onto an avatar: HUMR (VRChat's own IK) or the IK solver from SteamVR + VRChat poses.</summary>
    public static class PoseBuilder
    {
        /// <summary>How long the held pose crossfades into VRChat's own pose again after calibration mode / an avatar reload (s).</summary>
        public const double HoldBlend = 0.3;

        /// <summary>
        ///     How long before a logged avatar reload VRChat's own pose is already unusable (the log line and HUMR frames lag
        ///     the click) (s).
        /// </summary>
        public const double HoldLead = 0.25;

        /// <summary>Crossfade between two solve segments (s): a recalibration or a VRChat setting change.</summary>
        public const double SegmentBlend = 0.3;

        /// <summary>
        ///     The base model of the avatar a take was recorded with, when the take has no skeleton of its own (set by the
        ///     app: the avatar library).
        /// </summary>
        public static Func<Recording, AvatarProfile> SourceResolver;

        /// <summary>
        ///     The face-tracking animator of the avatar a take is solved onto, or null (the default: none). When there is
        ///     one, the face shapes it sets replace the general face mapping's for those shapes, as VRChat shows them.
        /// </summary>
        public static Func<AvatarProfile, FaceAnimator> FaceAnimatorResolver;

        /// <summary>
        ///     Off by default: VRChat's calibration mode (from "Calibrate FBT" to the confirm) stays in the clip as VRChat
        ///     recorded
        ///     it, so it can be seen, and is marked on the timeline, where a click on its shaded span selects it for Cut. On:
        ///     those stretches hold the pose before them instead (the older behaviour).
        /// </summary>
        public static bool HoldCalibrationMode = false;

        // ------------------------------------------------------------------ fingers from VRChat gestures

        // curl per finger (thumb, index, middle, ring, little) for GestureLeft/Right 0..7
        static readonly float[][] GestureCurl =
        {
            new[] { 0.15f, 0.12f, 0.15f, 0.18f, 0.2f }, // Neutral
            new[] { 0.85f, 1f, 1f, 1f, 1f }, // Fist
            new[] { 0f, 0f, 0f, 0f, 0f }, // HandOpen
            new[] { 0.8f, 0f, 1f, 1f, 1f }, // FingerPoint
            new[] { 0.8f, 0f, 0f, 1f, 1f }, // Victory
            new[] { 0.8f, 0f, 1f, 1f, 0f }, // RockNRoll
            new[] { 0f, 0f, 1f, 1f, 1f }, // HandGun
            new[] { 0f, 1f, 1f, 1f, 1f } // ThumbsUp
        };

        static readonly float[] Spread = { 0f, -6f, 0f, 6f, 12f };

        /// <summary>
        ///     Solves a take onto an avatar skeleton (prof: the target, any humanoid). The IK solve runs on the target directly,
        ///     so a take transfers to any avatar; VRChat's own IK (HUMR, kept as a reference) is retargeted from the avatar the
        ///     take was recorded with (rec.Profile, else <see cref="SourceResolver" />) by each bone's rotation from its T-pose.
        ///     The avatar keeps its own size unless CalibrationSettings.FollowAvatarScale follows VRChat's avatar scaling.
        /// </summary>
        public static BakedClip Bake(Recording rec, AvatarProfile prof, BakeOptions opt, Action<float> progress = null)
        {
            var source = rec.Profile ?? SourceResolver?.Invoke(rec);
            prof = prof ??
                   source ?? AvatarProfile.Generic(rec.AvatarEyeHeight > 0.2f
                       ? rec.AvatarEyeHeight
                       : 1.6f); // generic only without any avatar library
            source = source ?? prof;
            var clip = new BakedClip { Profile = prof, Fps = opt.Fps, BoneExists = prof.HumanExists };
            double[] range = opt.SourceRange;
            double dur = range != null ? Math.Max(0, range[1] - range[0]) : rec.EditedDuration;
            int n = Math.Max(2, (int)Math.Ceiling(dur * opt.Fps) + 1);
            clip.Frames = n;
            clip.SourceTime = new double[n];
            for (int i = 0; i < n; i++)
                clip.SourceTime[i] = range != null
                    ? range[0] + Math.Min(dur, i / opt.Fps)
                    : rec.Edits.EditedToSource(Math.Min(dur, i / opt.Fps), rec.Duration);
            clip.Hips = new Vec3[n];
            clip.Rot = new Quat[n][];
            var cal = opt.Calibration ?? rec.Edits.Calib ?? new CalibrationSettings();

            // VRChat's own IK frames back in step with the devices (VRChat's clock falls behind on avatar reloads)
            if (rec.HasHumr)
            {
                string hs = HumrSync.Ensure(rec, source);
                if (hs.Length > 0) clip.Warnings.Add(hs);
            }

            // VRChat's avatar scaling during the take (OSC, re-measures; measured on VRChat's own IK when it is in the take)
            var vscale = VrcAvatarScale.For(rec, source);
            // the avatar's own size: the worn avatar as uploaded (its unscaled eye height in VRChat) when the take is solved
            // on its base model; another avatar at its own size. In-game scaling only with FollowAvatarScale.
            float avatarScale = 1f;
            bool sameBase = SameBase(prof, source);
            if (sameBase && vscale.NaturalEye > 0.2f && prof.EyeHeight > 0.2f)
            {
                float s = vscale.NaturalEye / prof.EyeHeight;
                if (Math.Abs(s - 1f) > 0.02f && s > 0.05f && s < 20f)
                {
                    avatarScale = s;
                    clip.Warnings.Add(
                        $"The avatar is ×{s:0.###} its base model {prof.Name} (VRChat eye height {vscale.NaturalEye:0.000} m at its own size, the base model {prof.EyeHeight:0.000} m)");
                }
            }

            if (cal.AvatarScale > 0.01f) avatarScale = cal.AvatarScale;
            clip.Scale = avatarScale * prof.Scale;
            if (vscale.Changes || vscale.AnyUncertain)
            {
                clip.Warnings.Add(vscale.Describe() + (cal.FollowAvatarScale
                    ? " (followed: Follow VRChat's avatar scaling is on)"
                    : " (not followed: the avatar keeps its own size)"));
                clip.Warnings.AddRange(vscale.Notes);
            }

            // body: the IK solve whenever the take has SteamVR / VRChat poses (it works on every avatar); VRChat's own IK
            // (HUMR) when picked as a reference, or when there is nothing else
            string src = rec.Edits.BodySource;
            bool humrOk = rec.HasHumr && HumrCoverage(rec, clip.SourceTime) > 0.6;
            bool ikOk = (rec.HasOpenVR || rec.HasVrcPoses) && BodySolvers.Factory != null;
            if (src == "humr" && !humrOk) clip.Warnings.Add("No HUMR data in range; using IK");
            if (src == "auto" || (src == "humr" && !humrOk)) src = ikOk ? "ik" : humrOk ? "humr" : "none";
            if (src == "ik" && !ikOk) src = humrOk ? "humr" : "none";
            clip.BodySource = src;
            clip.SourceProfile = source;

            Pose[] universe = null;
            if (src == "humr") BakeHumr(rec, prof, source, clip, cal, vscale, progress);
            else if (src == "ik") universe = BakeIK(rec, prof, clip, opt, cal, avatarScale, progress);
            else BakeRest(prof, clip);

            if (src != "none") Smooth(clip, opt.Smoothing);
            // playspace jumps (reset / re-center) were undone for the solver and the smoothing: put them back, so the body
            // teleports in the same frame as the raw devices (and VRChat's avatar)
            if (universe != null)
                for (int f = 0; f < clip.Frames; f++)
                {
                    var U = universe[f];
                    clip.Hips[f] = U.pos + U.rot * clip.Hips[f];
                    for (int b = 0; b < HumanBones.Count; b++) clip.Rot[f][b] = (U.rot * clip.Rot[f][b]).Normalized;
                }

            if (clip.Calibration != null) MeasureCalibration(clip);
            if (cal.FollowAvatarScale && vscale.Changes && src != "none") FollowVrcScale(clip, vscale, src);
            string fsrc = rec.Edits.FingerSource;
            if (fsrc == "auto") fsrc = rec.HasFingers ? "steamvr" : "gestures";
            if (src == "ik" && fsrc == "steamvr" && rec.HasFingers) ApplySteamVRFingers(rec, prof, clip);
            else if (src == "ik" && fsrc == "gestures" && opt.FingersFromGestures) ApplyGestureFingers(rec, prof, clip);
            if (src != "humr" && opt.EyesFromTracking) ApplyEyes(rec, prof, clip, opt);
            BakeFace(rec, clip, opt);
            BakeExtras(rec, clip, src);
            if (opt.ApplyPlacement && rec.Edits.HasPlacement) Place(clip, rec.Edits.Placement);
            progress?.Invoke(1f);
            return clip;
        }

        /// <summary>
        ///     Moves and turns the whole solved clip (body, VRChat camera, devices) by a rigid placement P (world' = P * world).
        ///     Applied after the solve, so it changes nothing about the solve itself; <see cref="BakedClip.Placement" /> records
        ///     it.
        /// </summary>
        public static void Place(BakedClip clip, Pose P)
        {
            var q = P.rot.Normalized;
            P = new Pose(P.pos, q);
            for (int f = 0; f < clip.Frames; f++)
            {
                if (clip.Hips != null) clip.Hips[f] = P.TransformPoint(clip.Hips[f]);
                var r = clip.Rot?[f];
                if (r != null)
                    for (int b = 0; b < r.Length; b++)
                        r[b] = (q * r[b]).Normalized;
            }

            if (clip.Camera != null)
                for (int f = 0; f < clip.Camera.Length; f++)
                    clip.Camera[f] = P * clip.Camera[f];
            foreach (var arr in clip.Trackers.Values)
                for (int f = 0; f < arr.Length; f++)
                    arr[f] = P * arr[f];
            if (clip.Room != null)
                for (int f = 0; f < clip.Room.Length; f++)
                    clip.Room[f] = P * clip.Room[f];
            if (clip.Calibration != null)
                foreach (var seg in clip.Calibration.Segments)
                    seg.Pose?.Transform(P);
            clip.HumrOrigin = clip.HumrOrigin * P.Inverse; // VRChat world -> placed clip
            clip.Placement = P * clip.Placement;
        }

        static double HumrCoverage(Recording rec, double[] times)
        {
            int ok = 0, step = Math.Max(1, times.Length / 200), cnt = 0;
            var h = rec.Humr;
            for (int i = 0; i < times.Length; i += step)
            {
                cnt++;
                double t = times[i];
                int lo = 0, hi = h.Count - 1;
                if (hi < 0) break;
                while (hi - lo > 1)
                {
                    int mid = (lo + hi) >> 1;
                    if (h.T[mid] <= t) lo = mid;
                    else hi = mid;
                }

                if (Math.Abs(h.T[lo] - t) < 0.25 || Math.Abs(h.T[hi] - t) < 0.25) ok++;
            }

            return cnt > 0 ? (double)ok / cnt : 0;
        }

        // ------------------------------------------------------------------ rest / fill helpers

        static void BakeRest(AvatarProfile prof, BakedClip clip)
        {
            var r0 = RestRot(prof);
            var hips = prof.HumanRestPos(HumanBones.Hips) * clip.Scale;
            for (int f = 0; f < clip.Frames; f++)
            {
                clip.Rot[f] = (Quat[])r0.Clone();
                clip.Hips[f] = hips;
            }
        }

        static Quat[] RestRot(AvatarProfile prof)
        {
            var r = new Quat[HumanBones.Count];
            for (int b = 0; b < HumanBones.Count; b++) r[b] = prof.HumanRestRot(b);
            return r;
        }

        /// <summary>Bones that were not driven keep their rest rotation relative to their humanoid parent.</summary>
        public static void FillUndriven(AvatarProfile prof, Quat[] rot, bool[] driven)
        {
            for (int b = 0; b < HumanBones.Count; b++)
            {
                if (driven[b] || !prof.Has(b)) continue;
                int p = HumanBones.ResolveParent(b, prof.HumanExists);
                if (p < 0)
                {
                    rot[b] = prof.HumanRestRot(b);
                    continue;
                }

                rot[b] = (rot[p] * prof.HumanRestRot(p).Conjugate * prof.HumanRestRot(b)).Normalized;
            }
        }

        // ------------------------------------------------------------------ HUMR (VRChat IK)

        /// <summary>
        ///     Same base model (library id, or the same skeleton): no retargeting needed, and the worn avatar's own size
        ///     applies.
        /// </summary>
        public static bool SameBase(AvatarProfile a, AvatarProfile b)
        {
            if (a == null || b == null) return false;
            if (ReferenceEquals(a, b)) return true;
            if (a.LibraryId.Length > 0 && b.LibraryId.Length > 0)
                return string.Equals(a.LibraryId, b.LibraryId, StringComparison.OrdinalIgnoreCase);
            return AvatarLibrary.SameSkeleton(a, b);
        }

        /// <summary>The worn avatar's own size relative to its base model skeleton (1 within 2 %).</summary>
        static float OwnScale(VrcAvatarScale vs, AvatarProfile src)
        {
            if (vs.NaturalEye < 0.2f || src.EyeHeight < 0.2f) return 1f;
            float s = vs.NaturalEye / src.EyeHeight;
            return Math.Abs(s - 1f) > 0.02f && s > 0.05f && s < 20f ? s : 1f;
        }

        /// <summary>
        ///     VRChat's own IK (HUMR) onto the target: each bone keeps its rotation from the worn avatar's T-pose (world-space
        ///     delta, so any humanoid works), the hips path is mapped from VRChat's world at the worn avatar's size to the
        ///     target's own size. VRChat's avatar scaling is taken out (heights divide by the stretch's size, horizontal moves
        ///     are accumulated at it), so the body keeps its own size; FollowAvatarScale puts it back later.
        /// </summary>
        static void BakeHumr(Recording rec, AvatarProfile prof, AvatarProfile src, BakedClip clip,
            CalibrationSettings cal, VrcAvatarScale vs, Action<float> progress)
        {
            var tmp = new Quat[HumanBones.Count];
            bool[] exists = prof.HumanExists;
            bool[] driven = new bool[HumanBones.Count];
            bool same = SameBase(prof, src);
            float srcOwn = OwnScale(vs, src);
            // origin from the HUMR frame at OriginTime, on the worn avatar at VRChat's size then
            var origin = HumrOrigin(rec, src, srcOwn * vs.At(rec.Edits.OriginTime));
            float srcHips = Math.Max(0.05f, src.HumanRestPos(HumanBones.Hips).y * srcOwn),
                tgtHips = Math.Max(0.05f, prof.HumanRestPos(HumanBones.Hips).y * clip.Scale);
            float k = tgtHips / srcHips; // the worn avatar's own size -> the target's own size
            if (!same)
                clip.Warnings.Add($"VRChat's IK retargeted from {src.Name} to {prof.Name} (hips height ×{k:0.###})");
            clip.HumrOrigin = origin;
            clip.HumrK = k;
            // VRChat jumps lift the whole avatar in VRChat's world: taken out unless the take keeps them (VrcJumps)
            var jumps = rec.Edits.IncludeJumps ? null : VrcJumps.For(rec, src);
            if (jumps != null && jumps.Any)
                clip.Warnings.Add(jumps.Describe() +
                                  ": the jump height is taken out of the body (Include VRChat jumps keeps it)");
            var io = origin.rot.Conjugate;
            var srcRestInv = new Quat[HumanBones.Count];
            var tgtRest = new Quat[HumanBones.Count];
            for (int b = 0; b < HumanBones.Count; b++)
            {
                srcRestInv[b] = src.HumanRestRot(b).Conjugate;
                tgtRest[b] = prof.HumanRestRot(b);
            }

            Vec3 acc = Vec3.Zero, prevD = Vec3.Zero;
            bool first = true;
            for (int f = 0; f < clip.Frames; f++)
            {
                double t = clip.SourceTime[f];
                rec.Humr.Sample(t, out var hips, tmp);
                if (jumps != null) hips.y -= jumps.Offset(t);
                var r = new Quat[HumanBones.Count];
                for (int b = 0; b < HumanBones.Count; b++)
                {
                    var q = tmp[b];
                    if (q.Norm < 0.5f)
                    {
                        r[b] = prof.HumanRestRot(b);
                        continue;
                    }

                    var w = (io * q.Normalized).Normalized;
                    r[b] = same ? w : (w * srcRestInv[b] * tgtRest[b]).Normalized;
                }

                // bones absent in the recording (identity rotations from VRChat) stay parent-relative rest
                for (int b = 0; b < HumanBones.Count; b++)
                    driven[b] = exists[b] && tmp[b].Norm > 0.5f &&
                                !(b != 0 && IsIdentity(tmp[b]) && !IsIdentity(src.HumanRestRot(b)));
                FillUndriven(prof, r, driven);
                clip.Rot[f] = r;
                var d = io * (hips - origin.pos);
                float s = Math.Max(0.05f, vs.At(t));
                if (first || clip.SourceTime[f] - clip.SourceTime[f - 1] > 0.25)
                {
                    acc = new Vec3(d.x, 0, d.z) / s;
                    first = false;
                }
                else acc = acc + new Vec3(d.x - prevD.x, 0, d.z - prevD.z) / s;

                prevD = d;
                clip.Hips[f] = new Vec3(acc.x, d.y / s, acc.z) * k;
                if ((f & 255) == 0) progress?.Invoke(0.8f * f / clip.Frames);
            }

            HoldWhereVrcIsNotTracking(rec, clip);
        }

        /// <summary>
        ///     Follow VRChat's avatar scaling (CalibrationSettings.FollowAvatarScale): the whole body, positions about the
        ///     origin's floor point and bone lengths, at VRChat's size per frame. VRChat re-applies the calibration at a new
        ///     size, so this is the same as solving at that size.
        /// </summary>
        static void FollowVrcScale(BakedClip clip, VrcAvatarScale vs, string src)
        {
            clip.ScaleCurve = new float[clip.Frames];
            for (int f = 0; f < clip.Frames; f++)
            {
                float s = vs.At(clip.SourceTime[f]);
                clip.ScaleCurve[f] = s;
                clip.Hips[f] = clip.Hips[f] * s;
            }
        }

        /// <summary>
        ///     VRChat's own IK (HUMR) between "Calibrate FBT" and the confirm, and while an avatar reload applies a setting, is
        ///     not the user's pose: VRChat hides the avatar there and its bones hang 30-60 cm off the controllers. The stretches
        ///     are listed in BakedClip.Held and noted; with <see cref="HoldCalibrationMode" /> they hold the last pose before them
        ///     and crossfade into VRChat's pose after them.
        /// </summary>
        static void HoldWhereVrcIsNotTracking(Recording rec, BakedClip clip)
        {
            var spans = VrcTimeline.For(rec).NotTracking();
            if (!HoldCalibrationMode)
            {
                foreach (double[] sp in spans)
                {
                    double a = sp[0] - HoldLead, b = sp[1];
                    if (clip.SourceTime.Any(t => t >= a && t <= b)) clip.Held.Add(new[] { a, b });
                }

                if (clip.Held.Count > 0)
                    clip.Warnings.Add(
                        $"VRChat's calibration mode / avatar reload at {string.Join(", ", clip.Held.Select(h => $"{h[0]:F1}-{h[1]:F1} s"))} is in the clip as VRChat recorded it (VRChat hides the avatar there): click its shaded span on the timeline and Cut to remove it");
                return;
            }

            int n = clip.Frames;
            foreach (double[] sp in spans)
            {
                double
                    a = sp[0] - HoldLead, b = sp[1]; // VRChat's pose already breaks a moment before the logged reload
                int fa = -1, fb = -1;
                for (int f = 0; f < n; f++)
                {
                    double t = clip.SourceTime[f];
                    if (t >= a && t <= b)
                    {
                        if (fa < 0) fa = f;
                        fb = f;
                    }
                }

                if (fa < 0) continue;
                int hold = fa > 0 && clip.SourceTime[fa] - clip.SourceTime[fa - 1] < 0.25 ? fa - 1 :
                    fb + 1 < n ? fb + 1 : -1;
                if (hold < 0) continue;
                var hr = (Quat[])clip.Rot[hold].Clone();
                var hh = clip.Hips[hold];
                for (int f = fa; f <= fb; f++)
                {
                    clip.Rot[f] = (Quat[])hr.Clone();
                    clip.Hips[f] = hh;
                }

                for (int f = fb + 1; f < n && clip.SourceTime[f] < b + HoldBlend; f++)
                {
                    float w = Smoothstep((float)((clip.SourceTime[f] - b) / HoldBlend));
                    for (int k = 0; k < HumanBones.Count; k++)
                        clip.Rot[f][k] = Quat.Slerp(hr[k], clip.Rot[f][k], w).Normalized;
                    clip.Hips[f] = Vec3.Lerp(hh, clip.Hips[f], w);
                }

                clip.Held.Add(new[] { a, b });
            }

            if (clip.Held.Count > 0)
                clip.Warnings.Add(
                    $"VRChat wasn't tracking (calibration mode / avatar reload) at {string.Join(", ", clip.Held.Select(h => $"{h[0]:F1}-{h[1]:F1} s"))}: the pose before is held there");
        }

        static float Smoothstep(float x)
        {
            x = MathX.Clamp01(x);
            return x * x * (3 - 2 * x);
        }

        static bool IsIdentity(Quat q)
        {
            return Math.Abs(q.x) < 1e-6f && Math.Abs(q.y) < 1e-6f && Math.Abs(q.z) < 1e-6f;
        }

        static Pose HumrOrigin(Recording rec, AvatarProfile prof, float scale)
        {
            var e = rec.Edits;
            var rot = new Quat[HumanBones.Count];
            rec.Humr.Sample(e.OriginTime, out var hips, rot);
            // yaw of the hips relative to their rest orientation
            var d = rot[HumanBones.Hips].Normalized * prof.HumanRestRot(HumanBones.Hips).Conjugate;
            float yaw = d.Yaw + e.OriginYawOffset;
            // floor: lowest foot vs its rest height
            float floor = hips.y - prof.HumanRestPos(HumanBones.Hips).y * scale;
            var tmpClip = new BakedClip
            {
                Profile = prof, Fps = 1, Frames = 1, Hips = new[] { hips }, Rot = new[] { FillCopy(prof, rot) },
                BoneExists = prof.HumanExists, Scale = scale
            };
            var wp = new Vec3[prof.Nodes.Count];
            var wr = new Quat[prof.Nodes.Count];
            tmpClip.EvaluateNodes(0, wp, wr);
            float minFoot = float.MaxValue, restFoot = float.MaxValue;
            foreach (int fb in new[]
                         { HumanBones.LeftFoot, HumanBones.RightFoot, HumanBones.LeftToes, HumanBones.RightToes })
            {
                if (!prof.Has(fb)) continue;
                minFoot = Math.Min(minFoot, wp[prof.Human[fb]].y);
                restFoot = Math.Min(restFoot, prof.HumanRestPos(fb).y * scale);
            }

            if (minFoot < float.MaxValue) floor = minFoot - restFoot;
            // center on the eyes' floor projection (same convention as the IK path, which centers on the headset)
            var c = hips;
            if (prof.Has(HumanBones.LeftEye) && prof.Has(HumanBones.RightEye))
                c = (wp[prof.Human[HumanBones.LeftEye]] + wp[prof.Human[HumanBones.RightEye]]) * 0.5f;
            else if (prof.Has(HumanBones.Head)) c = wp[prof.Human[HumanBones.Head]];
            var pos = new Vec3(c.x + e.OriginOffsetX, floor, c.z + e.OriginOffsetZ);
            return new Pose(pos, Quat.YawRotation(yaw));
        }

        static Quat[] FillCopy(AvatarProfile prof, Quat[] rot)
        {
            var r = new Quat[HumanBones.Count];
            bool[] driven = new bool[HumanBones.Count];
            for (int b = 0; b < HumanBones.Count; b++)
            {
                r[b] = rot[b].Norm > 0.5f ? rot[b].Normalized : prof.HumanRestRot(b);
                driven[b] = rot[b].Norm > 0.5f;
            }

            FillUndriven(prof, r, driven);
            return r;
        }

        /// <summary>When a snapshot was taken, in take seconds (negative = before the take).</summary>
        public static double SnapshotTakeTime(Recording rec, CalibrationSnapshot sn)
        {
            if (sn == null) return double.NaN;
            string file = Path.GetFileName(rec.FilePath ?? "");
            if (sn.FromTake.Length > 0 && sn.FromTakeTime >= 0)
                return sn.FromTake == file ? sn.FromTakeTime : double.NegativeInfinity;
            return (sn.CreatedUtc - rec.CreatedUtc).TotalSeconds;
        }

        /// <summary>
        ///     Whether a take's calibration follows VRChat's calibrations as they happened: chosen explicitly (EditList -3), or
        ///     linked by an older version to one of VRChat's calibrations made during the take (a live snapshot from inside the
        ///     take; a snapshot picked as "this calibration only" is made from a take moment and stays a single calibration).
        /// </summary>
        public static bool FollowsVrcCalibrations(Recording rec, VrcTimeline tl)
        {
            var e = rec.Edits;
            if (tl.Calibrations.Count == 0) return false;
            if (e.CalibrationTime == EditList.UseVrcTimeline) return true;
            return e.CalibrationTime == EditList.UseSnapshot && e.Snapshot != null && e.Snapshot.FromTake.Length == 0 &&
                   SnapshotTakeTime(rec, e.Snapshot) >= 0;
        }

        /// <summary>
        ///     Splits a take where VRChat recalibrated or changed its IK settings (VrcTimeline):
        ///     * Following VRChat's calibrations (<see cref="FollowsVrcCalibrations" />): the calibration in
        ///     effect when the take started (the linked snapshot, when it is older than the take; else the take's first
        ///     VRChat calibration), then each VRChat calibration from its "Calibrate FBT" click on. VRChat hides the avatar
        ///     between click and confirm; the user stands in the calibration pose there with the trackers as at the confirm,
        ///     so the new calibration already fits it.
        ///     * Following VRChat's settings: spine mode (lock) and avatar measurement from the moment VRChat changed them.
        /// </summary>
        public static List<SolvePlan> PlanSegments(Recording rec, CalibrationSettings cal, out VrcTimeline tl)
        {
            tl = VrcTimeline.For(rec);
            var e = rec.Edits;
            var sources = new List<Tuple<double, CalibrationSource, string>>();
            bool timeline = FollowsVrcCalibrations(rec, tl);
            if (timeline)
            {
                var first = tl.Calibrations[0];
                double snT = SnapshotTakeTime(rec, e.Snapshot);
                if (e.Snapshot != null && e.Snapshot.Hmd != null && snT < first.Click - 0.5)
                    sources.Add(Tuple.Create(double.NegativeInfinity,
                        new CalibrationSource { Snapshot = e.Snapshot, Text = "in effect when the take started" },
                        e.Snapshot.Label));
                else
                    sources.Add(Tuple.Create(double.NegativeInfinity,
                        new CalibrationSource
                        {
                            Time = first.Confirm,
                            Text =
                                $"VRChat calibration confirmed at {first.Confirm:F2} s (no earlier calibration: used from the start)"
                        }, $"VRChat calibration at {first.Confirm:F2} s"));
                foreach (var c in tl.Calibrations)
                {
                    if (sources.Count == 1 && sources[0].Item2.Snapshot == null && c == first)
                        continue; // already the first source
                    sources.Add(Tuple.Create(c.Click,
                        new CalibrationSource
                        {
                            Time = c.Confirm,
                            Text =
                                $"VRChat calibration confirmed at {c.Confirm:F2} s (\"Calibrate FBT\" at {c.Click:F1} s)"
                        }, $"VRChat calibration at {c.Confirm:F2} s"));
                }
            }
            else sources.Add(Tuple.Create(double.NegativeInfinity, (CalibrationSource)null, ""));

            var bounds = new SortedSet<double>();
            foreach (var src in sources.Skip(1)) bounds.Add(src.Item1);
            if (cal.FollowVrc)
                foreach (var c in tl.Changes)
                    bounds.Add(c.T);
            var edges = new List<double> { double.NegativeInfinity };
            edges.AddRange(bounds.Where(x => x > 0 && x < rec.Duration));
            edges.Add(double.PositiveInfinity);

            var plans = new List<SolvePlan>();
            for (int i = 0; i + 1 < edges.Count; i++)
            {
                double a = edges[i], b = edges[i + 1];
                double probe = double.IsNegativeInfinity(a) ? -1e9 : a + 1e-6;
                int si = 0;
                for (int k = 0; k < sources.Count; k++)
                    if (sources[k].Item1 <= probe)
                        si = k;
                var st = cal.Clone();
                if (cal.FollowVrc)
                {
                    tl.SettingsAt(Math.Max(0, probe), out int spine, out int meas);
                    if (spine >= 0) st.Lock = VrcTimeline.LockOf(spine);
                    if (meas >= 0) st.Measure = VrcTimeline.MeasureOf(meas);
                }

                var last = plans.Count > 0 ? plans[plans.Count - 1] : null;
                if (last != null && last.SourceIndex == si && last.Settings.SameAs(st))
                {
                    last.End = b;
                    continue;
                }

                plans.Add(new SolvePlan
                {
                    Start = a, End = b, Settings = st, Source = sources[si].Item2, SourceIndex = si,
                    Text = sources[si].Item3
                });
            }

            return plans;
        }

        static Pose[] BakeIK(Recording rec, AvatarProfile prof, BakedClip clip, BakeOptions opt,
            CalibrationSettings cal, float avatarScale, Action<float> progress)
        {
            var plans = PlanSegments(rec, cal, out var tl);
            int n = clip.Frames;
            bool[] exists = prof.HumanExists;
            var universe = new Pose[n];
            bool anyJump = false;
            int[] segOf = new int[n];
            var results = new List<CalibrationResult>();
            var pos = new Vec3[HumanBones.Count];
            var rot = new Quat[HumanBones.Count];
            float dt = 1f / opt.Fps;
            bool multi = plans.Count > 1;
            if (multi) clip.Warnings.Add($"VRChat in this take: {tl.Describe()}");
            // the SteamVR room (play area, base stations) mapped like the devices: the take's own when it recorded one, else
            // the viewer's (MainForm: SteamVR's room now, for older takes) is placed as if captured at the take's start
            var room = SteamVRRoom.From(rec);
            double roomT = room?.T ?? 0;
            clip.Room = new Pose[n];
            clip.RoomScale = new float[n];
            for (int si = 0; si < plans.Count; si++)
            {
                var pl = plans[si];
                bool lastSeg = si == plans.Count - 1;
                // frames: from the segment start to its end plus the next segment's crossfade
                int fa = si == 0 ? 0 : -1, fb = -1;
                for (int f = 0; f < n; f++)
                {
                    double t = clip.SourceTime[f];
                    if (fa < 0 && t >= pl.Start) fa = f;
                    if (lastSeg || t < pl.End + SegmentBlend) fb = f;
                }

                if (fa < 0 || fb < fa) continue;

                var rig = new TrackingRig(rec, prof, avatarScale, pl.Settings, pl.Source)
                    { DeviceEdit = opt.DeviceEdit, Disabled = opt.DisabledRoles };
                string tag = multi
                    ? $"{(double.IsNegativeInfinity(pl.Start) ? 0 : pl.Start):F1}-{(double.IsPositiveInfinity(pl.End) ? rec.Duration : pl.End):F1} s: "
                    : "";
                if (si == 0 || !multi)
                    clip.Warnings.AddRange(multi
                        ? rig.Notes.Where(x => !x.StartsWith("Calibration") && !x.StartsWith("Scale"))
                        : rig.Notes);
                if (multi)
                    clip.Warnings.Add(tag + "calibration " + rig.CalibrationPoseText + "; " + pl.Settings.Describe() +
                                      "; scale " + rig.TrackScale.ToString("0.000"));
                var solver = BodySolvers.Factory();
                solver.Init(prof, avatarScale, rig.RealEyeHeight);
                solver.Configure(pl.Settings);
                // VRChat-style calibration: headset and controllers keep fixed offsets, body trackers are bound to the
                // avatar's T-pose under the headset at the calibration moment (hanging from it, or on the floor: CalibrationSettings.HangFromHeadset)
                rig.Calibrate(solver);
                var res = new CalibrationResult
                {
                    Time = rig.CalibrationTime, Auto = rig.CalibrationAuto, DetectedTPose = rig.DetectedTPose,
                    PoseText = rig.CalibrationPoseText,
                    ScaleText = rig.ScaleText, TrackScale = rig.TrackScale, RealEyeHeight = rig.RealEyeHeight,
                    AvatarEyeHeight = rig.AvatarEyeHeight,
                    Settings = pl.Settings.Clone(), Start = pl.Start, End = pl.End,
                    Pose = rig.CalibrationPose()
                };
                foreach (BodyRole r in Enum.GetValues(typeof(BodyRole)))
                    if (rig.HasTracker(r))
                        res.Trackers.Add(r.ToString());
                // pre-roll so foot planting / smoothing settles before the first frame
                double t0 = clip.SourceTime[fa];
                for (int k = 30; k > 0; k--)
                {
                    rig.Feed(solver, Math.Max(0, t0 - k * dt));
                    solver.Solve(dt);
                }

                var ownTimes = new List<double>();
                var ownFrames = new List<int>();
                for (int f = fa; f <= fb; f++)
                {
                    double t = clip.SourceTime[f];
                    float step = f == fa ? dt : (float)Math.Max(1e-4, Math.Min(0.25, t - clip.SourceTime[f - 1]));
                    if (f > fa && t - clip.SourceTime[f - 1] > 0.25)
                    {
                        for (int k = 10; k > 0; k--)
                        {
                            rig.Feed(solver, t - k * dt);
                            solver.Solve(dt);
                        }

                        step = dt;
                    } // after a cut

                    rig.Feed(solver, t);
                    solver.Solve(step);
                    solver.GetPose(pos, rot);
                    var r = new Quat[HumanBones.Count];
                    bool[] driven = new bool[HumanBones.Count];
                    for (int bb = 0; bb < HumanBones.Count; bb++)
                    {
                        bool ok = exists[bb] && rot[bb].IsFinite && rot[bb].Norm > 0.5f && !HumanBones.IsFinger(bb) &&
                                  bb != HumanBones.LeftEye && bb != HumanBones.RightEye && bb != HumanBones.Jaw;
                        r[bb] = ok ? rot[bb].Normalized : Quat.Identity;
                        driven[bb] = ok;
                    }

                    FillUndriven(prof, r, driven);
                    var hips = pos[HumanBones.Hips].IsFinite
                        ? pos[HumanBones.Hips]
                        : prof.HumanRestPos(HumanBones.Hips) * avatarScale;
                    if (si > 0 && t < pl.Start + SegmentBlend && clip.Rot[f] != null)
                    {
                        // crossfade from the previous segment's solve
                        float w = Smoothstep((float)((t - pl.Start) / SegmentBlend));
                        for (int bb = 0; bb < HumanBones.Count; bb++)
                            clip.Rot[f][bb] = Quat.Slerp(clip.Rot[f][bb], r[bb], w).Normalized;
                        clip.Hips[f] = Vec3.Lerp(clip.Hips[f], hips, w);
                    }
                    else
                    {
                        clip.Rot[f] = r;
                        clip.Hips[f] = hips;
                    }

                    if (t >= pl.Start || si == 0)
                        if (t < pl.End || lastSeg)
                        {
                            segOf[f] = results.Count;
                            universe[f] = rig.UniverseAt(t);
                            if (rig.HasHmd)
                            {
                                clip.Room[f] = rig.RoomAt(t, roomT);
                                clip.RoomScale[f] = rig.TrackScale;
                            }

                            if (rig.Jumps.Any) anyJump = true;
                            ownTimes.Add(t);
                            ownFrames.Add(f);
                        }

                    if ((f & 127) == 0) progress?.Invoke(0.8f * f / n);
                }

                foreach (var kv in rig.TrackerTracks(ownTimes.ToArray()))
                {
                    if (!clip.Trackers.TryGetValue(kv.Key, out var tp))
                    {
                        clip.Trackers[kv.Key] = tp = new Pose[n];
                        clip.TrackerValid[kv.Key] = new bool[n];
                    }

                    bool[] tv = clip.TrackerValid[kv.Key];
                    for (int i = 0; i < ownFrames.Count; i++)
                    {
                        tp[ownFrames[i]] = kv.Value.Item1[i];
                        tv[ownFrames[i]] = kv.Value.Item2[i];
                    }
                }

                if (rig.FeetDropFrames > 0)
                {
                    res.FeetDropMax = rig.MaxFeetDrop;
                    res.FeetDropMean = rig.SumFeetDrop / rig.FeetDropFrames;
                    res.FeetDropShare = (float)rig.FeetDropActive / rig.FeetDropFrames;
                }

                results.Add(res);
            }

            for (int f = 0; f < n; f++)
                if (clip.Rot[f] == null)
                {
                    clip.Rot[f] = RestRot(prof);
                    clip.Hips[f] = prof.HumanRestPos(HumanBones.Hips) * avatarScale;
                }

            // the result shown: the segment at the clip's middle (single segment: the only one), with every segment listed
            var main = results.Count > 0 ? results[segOf[n / 2]] : null;
            if (main != null)
            {
                clip.Calibration = main;
                main.Segments.AddRange(results);
                main.FrameSegment = segOf;
                if (results.Count > 1)
                {
                    main.FeetDropMax = results.Max(x => x.FeetDropMax);
                    main.FeetDropShare = results.Average(x => x.FeetDropShare);
                    main.FeetDropMean = results.Average(x => x.FeetDropMean);
                }
            }

            return anyJump ? universe : null;
        }

        /// <summary>
        ///     How closely the solved body follows its devices (shown in the UI after a solve). Run after smoothing for the
        ///     final numbers.
        /// </summary>
        public static void MeasureCalibration(BakedClip clip)
        {
            var res = clip.Calibration;
            var P = clip.Profile;
            if (res == null || clip.Frames < 2) return;
            var wp = new Vec3[P.Nodes.Count];
            var wr = new Quat[P.Nodes.Count];
            var pairs = new[]
            {
                Tuple.Create("Head", HumanBones.Head, BodyRole.Head),
                Tuple.Create("Hips", HumanBones.Hips, BodyRole.Hips),
                Tuple.Create("LeftFoot", HumanBones.LeftFoot, BodyRole.LeftFoot),
                Tuple.Create("RightFoot", HumanBones.RightFoot, BodyRole.RightFoot)
            };
            var sum = new Dictionary<string, List<Vec3>>();
            float restSole = float.MaxValue;
            var soleY = new List<float>();
            int[] feet = new[] { HumanBones.LeftFoot, HumanBones.RightFoot, HumanBones.LeftToes, HumanBones.RightToes };
            foreach (int b in feet)
                if (P.Has(b))
                    restSole = Math.Min(restSole, P.HumanRestPos(b).y * clip.Scale);
            res.Drift.Clear();
            for (int f = 0; f < clip.Frames; f += 2)
            {
                clip.EvaluateNodes(f, wp, wr);
                foreach (var pr in pairs)
                {
                    if (pr.Item3 != BodyRole.Head && !res.Trackers.Contains(pr.Item1)) continue;
                    if (!P.Has(pr.Item2) || !clip.Trackers.TryGetValue(pr.Item1, out var tp) ||
                        !clip.TrackerValid[pr.Item1][f]) continue;
                    var d = tp[f].rot.Conjugate *
                            (wp[P.Human[pr.Item2]] -
                             tp[f].pos); // bone in the device's frame: constant when bound rigidly
                    string key = pr.Item1 + "|" + (res.FrameSegment != null && f < res.FrameSegment.Length
                        ? res.FrameSegment[f]
                        : 0); // each calibration binds anew
                    if (!sum.TryGetValue(key, out var l)) sum[key] = l = new List<Vec3>();
                    l.Add(d);
                }

                float lo = float.MaxValue;
                foreach (int b in feet)
                    if (P.Has(b))
                        lo = Math.Min(lo, wp[P.Human[b]].y);
                if (lo < float.MaxValue) soleY.Add(lo);
            }

            foreach (var g in sum.GroupBy(kv => kv.Key.Split('|')[0]))
            {
                double ss = 0;
                int cnt = 0;
                foreach (var kv in g)
                {
                    var mean = kv.Value.Aggregate(Vec3.Zero, (a, b) => a + b) * (1f / kv.Value.Count);
                    foreach (var v in kv.Value)
                    {
                        ss += (v - mean).SqrLength;
                        cnt++;
                    }
                }

                res.Drift[g.Key] = cnt > 0 ? (float)Math.Sqrt(ss / cnt) : 0;
            }

            if (soleY.Count > 0 && restSole < float.MaxValue)
            {
                soleY.Sort();
                res.FootFloorGap =
                    soleY[soleY.Count / 10] - restSole; // lowest foot/toe bone vs its standing height, 10th percentile
            }
        }

        /// <summary>Zero-phase (forward + backward) exponential smoothing of every bone rotation and the hips path.</summary>
        static void Smooth(BakedClip clip, float amount)
        {
            if (amount <= 0.001f || clip.Frames < 3) return;
            float tau = amount * 0.08f; // up to 80 ms
            float a = 1f - (float)Math.Exp(-(1.0 / clip.Fps) / tau);
            int n = clip.Frames;
            var tmp = new Quat[n];
            for (int b = 0; b < HumanBones.Count; b++)
            {
                if (!clip.BoneExists[b]) continue;
                var s = clip.Rot[0][b];
                for (int f = 0; f < n; f++)
                {
                    s = Quat.Nlerp(s, clip.Rot[f][b], a);
                    tmp[f] = s;
                }

                s = tmp[n - 1];
                for (int f = n - 1; f >= 0; f--)
                {
                    s = Quat.Nlerp(s, tmp[f], a);
                    clip.Rot[f][b] = s;
                }
            }

            var hp = new Vec3[n];
            var v = clip.Hips[0];
            for (int f = 0; f < n; f++)
            {
                v = Vec3.Lerp(v, clip.Hips[f], a);
                hp[f] = v;
            }

            v = hp[n - 1];
            for (int f = n - 1; f >= 0; f--)
            {
                v = Vec3.Lerp(v, hp[f], a);
                clip.Hips[f] = v;
            }
        }

        static void ApplyGestureFingers(Recording rec, AvatarProfile prof, BakedClip clip)
        {
            foreach (bool left in new[] { true, false })
            {
                var g = rec.Param(left ? "GestureLeft" : "GestureRight");
                var gw = rec.Param(left ? "GestureLeftWeight" : "GestureRightWeight");
                int hand = left ? HumanBones.LeftHand : HumanBones.RightHand;
                if (!prof.Has(hand)) continue;
                float[] cur = new float[5];
                bool first = true;
                for (int f = 0; f < clip.Frames; f++)
                {
                    double t = clip.SourceTime[f];
                    int gi = g != null ? MathX.Clamp((int)Math.Round(g.SampleStep(t)), 0, 7) : 0;
                    float w = gw != null ? gw.Sample(t, 1f) : 1f;
                    float[] target = (float[])GestureCurl[gi].Clone();
                    if (gi == 1)
                        for (int k = 0; k < 5; k++)
                            target[k] = MathX.Lerp(GestureCurl[0][k], target[k], w); // analog fist
                    float a = first ? 1f : 1f - (float)Math.Exp(-(1f / clip.Fps) / 0.06f);
                    for (int k = 0; k < 5; k++) cur[k] += (target[k] - cur[k]) * a;
                    first = false;
                    PoseHand(prof, clip.Rot[f], left, cur, gi == 2 ? 1f : 0.3f);
                }
            }
        }

        /// <summary>SteamVR Skeletal Input (Index finger tracking): per-finger curl + splay drive the avatar's fingers.</summary>
        static void ApplySteamVRFingers(Recording rec, AvatarProfile prof, BakedClip clip)
        {
            float[] sum = new float[9];
            foreach (bool left in new[] { true, false })
            {
                var hs = rec.Hands[left ? 0 : 1];
                int hand = left ? HumanBones.LeftHand : HumanBones.RightHand;
                if (!prof.Has(hand) || hs.Count < 2) continue;
                var g = rec.Param(left ? "GestureLeft" : "GestureRight");
                float[] curl = new float[5];
                float[] spread = new float[5];
                for (int f = 0; f < clip.Frames; f++)
                {
                    double t = clip.SourceTime[f];
                    if (hs.SampleSummary(t, sum))
                    {
                        for (int k = 0; k < 5; k++) curl[k] = MathX.Clamp01(sum[k]);
                        // splay: thumb-index, index-middle, middle-ring, ring-pinky (0 = together)
                        spread[0] = sum[5];
                        spread[1] = -sum[6];
                        spread[2] = 0;
                        spread[3] = sum[7];
                        spread[4] = sum[7] + sum[8];
                        PoseHandSpread(prof, clip.Rot[f], left, curl, spread);
                    }
                    else
                    {
                        // tracking gap: fall back to the VRChat gesture for this frame
                        int gi = g != null ? MathX.Clamp((int)Math.Round(g.SampleStep(t)), 0, 7) : 0;
                        PoseHand(prof, clip.Rot[f], left, GestureCurl[gi], gi == 2 ? 1f : 0.3f);
                    }
                }
            }
        }

        static void PoseHandSpread(AvatarProfile prof, Quat[] rot, bool left, float[] curl, float[] splay)
        {
            float[] spreadDeg = new float[5];
            spreadDeg[0] = 0;
            for (int k = 1; k < 5; k++) spreadDeg[k] = splay[k] * 14f;
            PoseHandCore(prof, rot, left, curl, spreadDeg, splay[0] * 25f);
        }

        static void PoseHand(AvatarProfile prof, Quat[] rot, bool left, float[] curl, float spread)
        {
            float[] spreadDeg = new float[5];
            for (int k = 0; k < 5; k++) spreadDeg[k] = Spread[k] * spread;
            PoseHandCore(prof, rot, left, curl, spreadDeg, 0f);
        }

        static void PoseHandCore(AvatarProfile prof, Quat[] rot, bool left, float[] curl, float[] spreadDeg,
            float thumbAbduct)
        {
            int hand = left ? HumanBones.LeftHand : HumanBones.RightHand;
            var dHand = rot[hand] * prof.HumanRestRot(hand).Conjugate;
            // rest palm normal: fingers point along +/-X, palm faces down in a T-pose; derive from the skeleton when possible
            var handPos = prof.HumanRestPos(hand);
            int midP = left ? HumanBones.LeftMiddleProximal : HumanBones.RightMiddleProximal;
            var fingerDir = prof.Has(midP)
                ? (prof.HumanRestPos(midP) - handPos).Normalized
                : new Vec3(left ? -1 : 1, 0, 0);
            var palm = Vec3.Down;
            int idxP = left ? HumanBones.LeftIndexProximal : HumanBones.RightIndexProximal,
                litP = left ? HumanBones.LeftLittleProximal : HumanBones.RightLittleProximal;
            if (prof.Has(idxP) && prof.Has(litP))
            {
                var across = (prof.HumanRestPos(idxP) - prof.HumanRestPos(litP)).Normalized; // little -> index
                var n = Vec3.Cross(fingerDir, across).Normalized;
                if (left) n = -n;
                if (Vec3.Dot(n, Vec3.Down) < 0) n = -n;
                palm = n;
            }

            float[] maxA = { 75f, 95f, 70f };
            float[] thumbA = { 35f, 45f, 50f };
            for (int fgr = 0; fgr < 5; fgr++)
            {
                int b0 = (left ? 24 : 39) + fgr * 3;
                var acc = Quat.Identity;
                for (int k = 0; k < 3; k++)
                {
                    int b = b0 + k;
                    if (!prof.Has(b)) continue;
                    var p0 = prof.HumanRestPos(b);
                    Vec3 dir;
                    if (k < 2 && prof.Has(b + 1)) dir = (prof.HumanRestPos(b + 1) - p0).Normalized;
                    else
                    {
                        int pp = k == 0 ? hand : b - 1;
                        dir = (p0 - prof.HumanRestPos(pp)).Normalized;
                    }

                    var axis = Vec3.Cross(dir, palm);
                    if (axis.SqrLength < 1e-6f) continue;
                    float ang = curl[fgr] * (fgr == 0 ? thumbA[k] : maxA[k]);
                    var c = Quat.AngleAxis(ang, axis);
                    if (k == 0 && fgr > 0) c = Quat.AngleAxis((left ? -1 : 1) * spreadDeg[fgr], palm) * c;
                    if (k == 0 && fgr == 0)
                        c = Quat.AngleAxis(curl[0] * 25f, fingerDir * (left ? 1 : -1)) *
                            Quat.AngleAxis((left ? 1 : -1) * thumbAbduct, palm) *
                            c; // thumb across the palm / abduction
                    acc = acc * c;
                    rot[b] = (dHand * acc * prof.HumanRestRot(b)).Normalized;
                }
            }
        }

        // ------------------------------------------------------------------ eyes from face tracking

        static void ApplyEyes(Recording rec, AvatarProfile prof, BakedClip clip, BakeOptions opt)
        {
            ParamTrack P(string n)
            {
                return rec.Param("FT/v2/" + n) ?? rec.Param("v2/" + n);
            }

            var lx = P("EyeLeftX") ?? P("EyeX");
            var ly = P("EyeLeftY") ?? P("EyeY");
            var rx = P("EyeRightX") ?? P("EyeX");
            var ry = P("EyeRightY") ?? P("EyeY");
            if (lx == null && rx == null) return;
            int head = HumanBones.Head;
            double off = rec.Edits.FaceOffset;
            for (int f = 0; f < clip.Frames; f++)
            {
                double t = clip.SourceTime[f] - off;
                var dHead = clip.Rot[f][head] * prof.HumanRestRot(head).Conjugate;
                foreach (bool left in new[] { true, false })
                {
                    int eye = left ? HumanBones.LeftEye : HumanBones.RightEye;
                    if (!prof.Has(eye)) continue;
                    var xs = left ? lx : rx;
                    var ys = left ? ly : ry;
                    float x = xs?.Sample(t) ?? 0, y = ys?.Sample(t) ?? 0;
                    var q = dHead * Quat.AngleAxis(x * opt.EyeYaw, Vec3.Up) *
                            Quat.AngleAxis(-y * opt.EyePitch, Vec3.Right) * prof.HumanRestRot(eye);
                    clip.Rot[f][eye] = q.Normalized;
                }
            }
        }

        // ------------------------------------------------------------------ face, params, camera

        static void BakeFace(Recording rec, BakedClip clip, BakeOptions opt)
        {
            double off = rec.Edits.FaceOffset;
            var names = new HashSet<string>(rec.Params.Select(p => p.Name));
            foreach (var p in rec.Params)
            {
                if (!p.Export) continue;
                if (!opt.IncludeBinaryParams && p.IsBinaryBit(names)) continue;
                float[] arr = new float[clip.Frames];
                for (int f = 0; f < clip.Frames; f++)
                    arr[f] = p.Type == ParamType.Float
                        ? p.Sample(clip.SourceTime[f] - off)
                        : p.SampleStep(clip.SourceTime[f] - off);
                clip.ParamValues[p.Name] = arr;
                clip.ParamTypes[p.Name] = p.Type;
            }

            bool hasFt = rec.Params.Any(p =>
                p.Name.StartsWith("FT/v2/", StringComparison.Ordinal) ||
                p.Name.StartsWith("v2/", StringComparison.Ordinal));
            if (!hasFt) return;
            var fm = new FaceMapping { Mode = opt.FaceNaming };
            var animator = clip.Profile == null ? null : FaceAnimatorResolver?.Invoke(clip.Profile);
            for (int f = 0; f < clip.Frames; f++)
            {
                double t = clip.SourceTime[f] - off;
                Func<string, float?> param = name =>
                    rec.ParamByName.TryGetValue(name, out var tr)
                        ? tr.Type == ParamType.Float ? tr.Sample(t) : tr.SampleStep(t)
                        : null;
                var w = fm.Evaluate(name => rec.ParamByName.TryGetValue(name, out var tr) ? tr.Sample(t) : null);
                // the avatar's own face-tracking animator, where there is one, has the last word on the shapes it sets
                if (animator != null)
                    foreach (var kv in animator.Evaluate(param))
                        w[kv.Key] = kv.Value;
                foreach (var kv in w)
                {
                    if (!clip.Face.TryGetValue(kv.Key, out float[] arr))
                        clip.Face[kv.Key] = arr = new float[clip.Frames];
                    arr[f] = kv.Value;
                }
            }
        }

        static void BakeExtras(Recording rec, BakedClip clip, string src)
        {
            var cam = rec.Stream(StreamKind.VrcCamera);
            if (cam == null || cam.Count < 2) return;
            // camera is in VRChat world space: center it with the same origin as the body
            var origin = Pose.Identity;
            float camScale = 1f;
            if (src == "humr")
            {
                origin = clip.HumrOrigin;
                camScale = clip.HumrK;
            }
            else
            {
                var head = rec.Stream(StreamKind.VrcHead);
                if (head != null && head.Sample(rec.Edits.OriginTime, out var hp, out var hr, 1.0))
                {
                    float eye = rec.AvatarEyeHeight > 0.2f ? rec.AvatarEyeHeight : clip.Profile.EyeHeight;
                    origin = new Pose(
                        new Vec3(hp.x + rec.Edits.OriginOffsetX, hp.y - eye, hp.z + rec.Edits.OriginOffsetZ),
                        Quat.YawRotation(hr.Yaw + rec.Edits.OriginYawOffset));
                }
                else return; // no common reference
            }

            var inv = origin.Inverse;
            clip.Camera = new Pose[clip.Frames];
            clip.CameraValid = new bool[clip.Frames];
            for (int f = 0; f < clip.Frames; f++)
            {
                bool ok = cam.Sample(clip.SourceTime[f], out var p, out var r, 0.5);
                var cp = inv * new Pose(p, r);
                if (camScale != 1f)
                    cp = new Pose(cp.pos * camScale,
                        cp.rot); // the camera keeps its framing on the retargeted / resized avatar
                clip.Camera[f] = cp;
                clip.CameraValid[f] = ok;
            }
        }

        // ------------------------------------------------------------------ IK (any world)

        /// <summary>One stretch of a take solved with one calibration and one set of settings.</summary>
        public sealed class SolvePlan
        {
            public CalibrationSettings Settings;

            /// <summary>Null: the take's own calibration choice (EditList); else this calibration.</summary>
            public CalibrationSource Source;

            public int SourceIndex = -1;
            public double Start = double.NegativeInfinity, End = double.PositiveInfinity;
            public string Text = "";
        }
    }
}
