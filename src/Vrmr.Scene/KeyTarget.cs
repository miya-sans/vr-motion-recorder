using System;
using System.Linq;

namespace VRMR
{
    /// <summary>
    /// Which keyframe strip the posing tool keys into.
    /// Devices: the selected strip when it is an action on an input track or a device take (device keys on a pose track),
    /// else the topmost input-track action under the playhead, else the device take under it, else a new one: a "Device
    /// keys" input track (Add, over the takes) or, for an actor with nothing recorded, a device take (Replace).
    /// Bones: the selected / topmost bone-key action on a pose track, else a new "Pose keys" track (Add over takes).
    /// </summary>
    public static class KeyTarget
    {
        static bool Covers(Strip x, double time) => !x.Mute && time >= x.Start - 1e-9 && time <= x.End + 1e-9;
        static bool IsDeviceTake(Scene s, NlaTrack t, Strip st) => t.Kind == TrackKind.Pose && st.Kind == StripKind.Action && KeyDevices.IsDeviceAction(s.Action(st.Source));

        public static bool FitsDevices(Scene s, NlaTrack t, Strip st) => t != null && st != null && st.Kind == StripKind.Action && s.Action(st.Source) != null && (t.Kind == TrackKind.Input || IsDeviceTake(s, t, st));
        public static bool FitsBones(Scene s, NlaTrack t, Strip st) => t != null && st != null && st.Kind == StripKind.Action && s.Action(st.Source) != null && t.Kind == TrackKind.Pose && !IsDeviceTake(s, t, st);

        /// <summary>The strip to key (null = none yet: <see cref="Create"/> one on the first edit).</summary>
        public static (NlaTrack Track, Strip Strip) Find(Scene s, Actor a, Strip selected, bool devices, double time)
        {
            if (s == null || a == null) return (null, null);
            if (selected != null)
            {
                var x = s.FindStrip(selected.Id);
                if (x.Actor == a && (devices ? FitsDevices(s, x.Track, x.Strip) : FitsBones(s, x.Track, x.Strip))) return (x.Track, x.Strip);
            }
            for (int pass = 0; pass < (devices ? 2 : 1); pass++)
                for (int ti = a.Tracks.Count - 1; ti >= 0; ti--)
                {
                    var t = a.Tracks[ti];
                    if (t.Mute || t.Lock) continue;
                    var st = t.Strips.Where(x => Covers(x, time) && (devices ? (pass == 0 ? t.Kind == TrackKind.Input && FitsDevices(s, t, x) : IsDeviceTake(s, t, x)) : FitsBones(s, t, x)))
                                     .OrderByDescending(x => x.Start).FirstOrDefault();
                    if (st != null) return (t, st);
                }
            return (null, null);
        }

        /// <summary>Does the actor have anything the solve / pose comes from (a take or a device take)?</summary>
        public static bool HasBase(Scene s, Actor a) =>
            a.Tracks.Any(t => !t.Mute && t.Kind == TrackKind.Pose && t.Strips.Any(x => !x.Mute && (x.Kind == StripKind.Take || IsDeviceTake(s, t, x))));

        /// <summary>A new keyframe strip for device keys (input track over a base, else a device take) or bone keys (pose track).</summary>
        public static (NlaTrack Track, Strip Strip) Create(Scene s, Actor a, bool devices)
        {
            bool over = HasBase(s, a);
            string name = devices ? (over ? "Device keys" : "Device take") : "Pose keys";
            var t = SceneOps.AddTrack(a, a.Tracks.Count, devices ? (over ? "Device keys" : "Devices") : "Pose keys");
            t.Kind = devices && over ? TrackKind.Input : TrackKind.Pose;
            double start = s.Start, len = Math.Max(over ? 1 : 5, s.RangeEnd - s.Start);
            var st = SceneOps.AddActionStrip(s, t, name, start, len, over ? BlendMode.Add : BlendMode.Replace);
            if (t.Kind == TrackKind.Input) st.Inputs = InputMask.Devices;
            return (t, st);
        }

        /// <summary>The scene frame of a time and the action time of that frame in a strip.</summary>
        public static int FrameOf(Scene s, double time) => Math.Max(0, (int)Math.Round((time - s.Start) * s.Fps));
        public static double TimeOf(Scene s, int f) => s.Start + f / (double)s.Fps;
        public static double ActionTime(Scene s, Strip st, int f) => Math.Round(st.ToSource(TimeOf(s, f)) * 1e6) / 1e6;
    }
}
