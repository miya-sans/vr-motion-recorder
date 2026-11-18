using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace VRMR
{

    /// <summary>
    /// The calibration library: snapshots in %APPDATA%\VRMotionRecorder\calibrations\snapshots\*.json (next to the
    /// settings presets in calibrations\). Each take embeds a copy of the snapshot it uses, so takes stay
    /// self-contained when snapshots are deleted.
    /// </summary>
    public static class CalibrationLibrary
    {
        public static string Dir => DirOverride ?? Path.Combine(CalibrationPresets.Dir, "snapshots");
        /// <summary>Tests only: use another folder for the library.</summary>
        public static string DirOverride;
        public static event Action Changed;

        public static string NewId(DateTime utc) => CalibrationSnapshot.NewId(utc);

        public static string Save(CalibrationSnapshot s)
        {
            Directory.CreateDirectory(Dir);
            if (string.IsNullOrEmpty(s.Id)) s.Id = NewId(s.CreatedUtc);
            string f = Path.Combine(Dir, Safe(s.Id) + ".json");
            File.WriteAllText(f, Json.Serialize(s.ToJson(), true), Encoding.UTF8);
            _list = null;
            try { Changed?.Invoke(); } catch { }
            return f;
        }

        static string Safe(string id)
        {
            var bad = Path.GetInvalidFileNameChars();
            return new string(id.Select(c => bad.Contains(c) ? '_' : c).ToArray());
        }

        static List<CalibrationSnapshot> _list; static DateTime _listTime;

        /// <summary>All snapshots, newest first (cached; re-read when the folder changes).</summary>
        public static List<CalibrationSnapshot> List()
        {
            try
            {
                var dir = new DirectoryInfo(Dir);
                if (!dir.Exists) return new List<CalibrationSnapshot>();
                var stamp = dir.LastWriteTimeUtc;
                if (_list != null && stamp == _listTime) return _list;
                var l = new List<CalibrationSnapshot>();
                foreach (var f in dir.GetFiles("*.json"))
                {
                    try { var s = CalibrationSnapshot.FromJson(Json.ParseObject(File.ReadAllText(f.FullName, Encoding.UTF8))); if (s != null && s.Devices.Count > 0) { if (string.IsNullOrEmpty(s.Id)) s.Id = Path.GetFileNameWithoutExtension(f.Name); l.Add(s); } }
                    catch (Exception ex) { AppLog.Warn("Calibration snapshot " + f.Name + ": " + ex.Message); }
                }
                _list = l.OrderByDescending(s => s.CreatedUtc).ToList(); _listTime = stamp;
                return _list;
            }
            catch { return new List<CalibrationSnapshot>(); }
        }

        public static CalibrationSnapshot Get(string id) => List().FirstOrDefault(s => s.Id == id);

        /// <summary>Moves a snapshot file to the Recycle Bin. Takes that use it keep their embedded copy.</summary>
        public static void Delete(string id)
        {
            string f = Path.Combine(Dir, Safe(id) + ".json");
            if (File.Exists(f)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(f, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            _list = null;
            try { Changed?.Invoke(); } catch { }
        }

        /// <summary>
        /// The snapshot a take should use: the latest one taken before the take ended (users often press Record and
        /// then calibrate) whose body trackers cover the take's, preferring the same SteamVR session (same playspace
        /// and tracker straps).
        /// </summary>
        public static CalibrationSnapshot BestFor(Recording rec, IEnumerable<CalibrationSnapshot> candidates = null, bool sameSessionOnly = false, DateTime? before = null)
        {
            string session = rec.Extra.GetStr("steamvrSession", "");
            var end = before ?? rec.CreatedUtc.AddSeconds(Math.Max(rec.Duration, 0) + 1);
            CalibrationSnapshot best = null; int bestScore = int.MinValue;
            foreach (var s in candidates ?? List())
            {
                if (s.CreatedUtc > end) continue;
                s.Compare(rec, out var missing, out var extra);
                if (missing.Count > 0) continue; // a tracker of the take has no binding here
                bool same = session.Length > 0 && s.SteamVRSession == session;
                if (sameSessionOnly && !same) continue;
                int score = (same ? 1000 : 0) - extra.Count * 10 + (s.Source == "vrchat" ? 5 : 0);
                if (best == null || score > bestScore || (score == bestScore && s.CreatedUtc > best.CreatedUtc)) { best = s; bestScore = score; }
            }
            return best;
        }

        /// <summary>vrserver.exe start time, identifying the SteamVR session ("" if SteamVR isn't running).</summary>
        public static string CurrentSteamVRSession()
        {
            try
            {
                var p = System.Diagnostics.Process.GetProcessesByName("vrserver").FirstOrDefault();
                return p == null ? "" : p.StartTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            }
            catch { return ""; }
        }
    }
}
