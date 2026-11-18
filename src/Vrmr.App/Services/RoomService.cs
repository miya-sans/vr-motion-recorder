using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VRMR.App.Services
{
    /// <summary>
    ///     The SteamVR room (play area and base stations) as last seen, kept in a small file so takes that did not record
    ///     their own room can still be shown with it when SteamVR is not running.
    /// </summary>
    public sealed class RoomService
    {
        readonly string _file;
        readonly Dictionary<Recording, (SteamVRRoom room, float fit)> _fit = new();
        SteamVRRoom _known;
        bool _loaded;

        public RoomService(string file) { _file = file; }

        /// <summary>The room as last seen (from SteamVR now, else from the saved file), or null.</summary>
        public SteamVRRoom Known
        {
            get
            {
                if (!_loaded)
                {
                    _loaded = true;
                    _known = Load(_file);
                }

                return _known;
            }
        }

        /// <summary>Raised when the known room changed.</summary>
        public event Action Changed;

        /// <summary>Called with SteamVR's room as it is now: remembered and saved when it differs from the known one.</summary>
        public void Update(SteamVRRoom live)
        {
            if (live == null || SameRoom(live, Known)) return;
            _known = live;
            _fit.Clear();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file));
                File.WriteAllText(_file,
                    Json.Serialize(new Dictionary<string, object>
                        { ["seen"] = DateTime.UtcNow.ToString("o"), ["room"] = live.ToJson() }));
            }
            catch (Exception ex) { AppLog.Warn("SteamVR room file: " + ex.Message); }

            Changed?.Invoke();
        }

        static SteamVRRoom Load(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                var d = Json.ParseObject(File.ReadAllText(file));
                var holder = new Recording();
                holder.Extra[SteamVRRoom.Key] = d.GetObj("room");
                return SteamVRRoom.From(holder);
            }
            catch (Exception ex)
            {
                AppLog.Warn("SteamVR room file: " + ex.Message);
                return null;
            }
        }

        /// <summary>The same room within tracking noise (base stations 2 cm, play area 1 cm).</summary>
        public static bool SameRoom(SteamVRRoom a, SteamVRRoom b)
        {
            if (a == null || b == null) return a == b;
            if (a.PlayArea == null != (b.PlayArea == null) || a.BaseStations.Count != b.BaseStations.Count)
                return false;
            if (a.PlayArea != null)
                for (int i = 0; i < 4; i++)
                    if ((a.PlayArea[i] - b.PlayArea[i]).Length > 0.01f)
                        return false;
            foreach (var s in a.BaseStations)
            {
                var o = b.BaseStations.FirstOrDefault(x => x.Serial == s.Serial);
                if (o.Serial == null || (o.Pose.pos - s.Pose.pos).Length > 0.02f) return false;
            }

            return true;
        }

        /// <summary>
        ///     How well a room fits a take: the share of its headset's floor positions inside the play area grown by
        ///     half a metre. 1 when there is nothing to check.
        /// </summary>
        public float Fit(Recording take, SteamVRRoom room)
        {
            if (_fit.TryGetValue(take, out var cached) && cached.room == room) return cached.fit;
            float fit = ComputeFit(take, room);
            _fit[take] = (room, fit);
            return fit;
        }

        /// <summary>The fit of a room to a take, uncached.</summary>
        public static float ComputeFit(Recording take, SteamVRRoom room)
        {
            var pa = room?.PlayArea;
            if (pa == null) return 1;
            lock (take.Sync)
            {
                var hmd = take.Devices.Where(d => d.DeviceClass == 1).OrderByDescending(d => d.Count).FirstOrDefault();
                if (hmd == null || hmd.Count <= 10) return 1;
                Vec3 u = pa[1] - pa[0], v = pa[3] - pa[0];
                float lu = u.Length, lv = v.Length;
                u = u * (1 / Math.Max(1e-4f, lu));
                v = v * (1 / Math.Max(1e-4f, lv));
                int inside = 0, n = 0;
                for (int i = 0; i < hmd.Count; i += 10)
                {
                    var d = hmd.P[i] - pa[0];
                    d.y = 0;
                    float a = Vec3.Dot(d, u), b = Vec3.Dot(d, v);
                    n++;
                    if (a > -0.5f && a < lu + 0.5f && b > -0.5f && b < lv + 0.5f) inside++;
                }

                return n > 0 ? inside / (float)n : 1;
            }
        }
    }
}
