using System;
using VRMR.App.Viewport;

namespace VRMR.App.Services
{
    /// <summary>
    ///     The take's placement: the solved take moved and turned as a whole, applied after the solve so the solve itself
    ///     does not change. Origin puts the avatar on the origin at the playhead, the turn buttons turn it about the hips.
    /// </summary>
    public sealed class PlacementService
    {
        static readonly int[] FeetBones =
            { HumanBones.LeftFoot, HumanBones.RightFoot, HumanBones.LeftToes, HumanBones.RightToes };

        readonly PlaybackClock _clock;
        readonly TakePresenter _presenter;
        readonly TakeSession _session;

        public PlacementService(TakeSession session, TakePresenter presenter, PlaybackClock clock)
        {
            _session = session;
            _presenter = presenter;
            _clock = clock;
        }

        /// <summary>What the last action did, in words ("" when nothing happened yet).</summary>
        public string LastAction { get; private set; } = "";

        /// <summary>The placement of the open take (identity without one).</summary>
        public Pose Current => _session.Take?.Edits.Placement ?? Pose.Identity;

        /// <summary>
        ///     Sets the placement. With <paramref name="solve" /> false (the moves of a drag) the view previews it and the
        ///     solve follows later.
        /// </summary>
        public void Set(Pose p, bool solve = true)
        {
            if (_session.Take == null) return;
            _session.Edit("placement", e => e.Placement = p, solve);
        }

        /// <summary>Back to the take as solved.</summary>
        public void Reset()
        {
            Set(Pose.Identity);
            LastAction = "Placement reset";
        }

        /// <summary>The placement from typed values: centimetres and Unity Euler angles in degrees.</summary>
        public static Pose FromValues(float xCm, float yCm, float zCm, float rxDeg, float ryDeg, float rzDeg)
        {
            return new Pose(new Vec3(xCm, yCm, zCm) * 0.01f, Quat.EulerUnity(rxDeg, ryDeg, rzDeg));
        }

        /// <summary>An angle wrapped to -180..180 with tiny values shown as 0.</summary>
        public static float WrapDegrees(float a)
        {
            a %= 360;
            if (a > 180) a -= 360;
            if (a <= -180) a += 360;
            return Math.Abs(a) < 0.005f ? 0 : a;
        }

        /// <summary>True when two placements are the same within what the fields show.</summary>
        public static bool Same(Pose a, Pose b)
        {
            return (a.pos - b.pos).Length < 1e-5f && Quat.Angle(a.rot, b.rot) < 1e-3f;
        }

        /// <summary>
        ///     The avatar at the playhead onto the origin: hips at X = Z = 0, the lowest foot at its rest height above the
        ///     floor, facing +Z. Replaces the placement's turn; the turn buttons turn from there. False when nothing is solved.
        /// </summary>
        public bool OriginHere()
        {
            if (!_presenter.ShownPose(out var wp, out var wr, out int f))
            {
                LastAction = "Open a solved take first.";
                return false;
            }

            var clip = _session.Clip;
            var prof = clip.Profile;
            int h = prof.Human[HumanBones.Hips];
            var pivot = wp[h];
            float yaw = (wr[h] * prof.HumanRestRot(HumanBones.Hips).Conjugate).Normalized.Yaw;
            var turn = Quat.YawRotation(-yaw);
            float k = clip.ScaleAt(f) / Math.Max(1e-6f, prof.Scale), lift = float.MaxValue;
            foreach (int b in FeetBones)
                if (prof.Has(b))
                    lift = Math.Min(lift, wp[prof.Human[b]].y - prof.HumanRestPos(b).y * k);
            if (lift == float.MaxValue) lift = 0;
            // turn about the hips (a turn about the vertical leaves the heights alone), then the hips onto the origin
            var d = new Vec3(-pivot.x, -lift, -pivot.z);
            var p = Current;
            Set(new Pose(pivot + turn * (p.pos - pivot) + d, (turn * p.rot).Normalized));
            LastAction =
                $"Origin at {TimeText.Format(_clock.Playhead)}: moved {d.x * 100:+0.0;-0.0;0} / {d.y * 100:+0.0;-0.0;0} / {d.z * 100:+0.0;-0.0;0} cm, turned {-yaw:+0.0;-0.0;0}° to face forward";
            return true;
        }

        /// <summary>Turns the take by degrees (positive = clockwise seen from above) about the hips at the playhead.</summary>
        public bool Turn(float degrees)
        {
            if (!_presenter.ShownPose(out var wp, out _, out _))
            {
                LastAction = "Open a solved take first.";
                return false;
            }

            var pivot = wp[_session.Clip.Profile.Human[HumanBones.Hips]];
            var turn = Quat.YawRotation(degrees);
            var p = Current;
            Set(new Pose(pivot + turn * (p.pos - pivot), (turn * p.rot).Normalized));
            LastAction = $"Turned {degrees:+0;-0}° about the hips at {TimeText.Format(_clock.Playhead)}";
            return true;
        }

        /// <summary>A one-line summary for the section header.</summary>
        public string Summary()
        {
            var take = _session.Take;
            if (take == null || !take.Edits.HasPlacement) return "as solved";
            var p = take.Edits.Placement;
            return $"moved {p.pos.Length * 100:0} cm, turned {Quat.Angle(Quat.Identity, p.rot):0}°";
        }
    }

    /// <summary>Time formatting shared by the transport, the timeline and messages.</summary>
    public static class TimeText
    {
        /// <summary>mm:ss.cc, or mm:ss without the fraction.</summary>
        public static string Format(double seconds, bool fraction = true)
        {
            if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
            int m = (int)(seconds / 60);
            double sec = seconds - m * 60;
            if (fraction)
            {
                if (sec >= 59.995)
                {
                    m++;
                    sec = 0;
                }

                return FormattableString.Invariant($"{m:00}:{sec:00.00}");
            }

            return FormattableString.Invariant($"{m:00}:{(int)sec:00}");
        }
    }
}
