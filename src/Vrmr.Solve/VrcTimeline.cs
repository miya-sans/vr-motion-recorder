using System;
using System.Collections.Generic;
using System.Linq;

namespace VRMR
{
    /// <summary>
    /// What VRChat's IK did during a take, from the take's own data: every calibrate-and-confirm (the "Calibrate FBT"
    /// click and the confirm), avatar-measurement changes (a setting-only avatar reload), spine-mode changes, and the
    /// settings at the start. Sources, in order of trust:
    ///  * "vrcSettings" events: VRChat's IK settings polled from the registry while recording (this version);
    ///  * setting-only avatar reloads (VRChat log): each toggles the measurement type, which has two values;
    ///  * changes added by hand to the take (EditList.VrcChanges), e.g. a spine-mode switch in an older take.
    /// Everything is read-only; nothing here depends on the controller or tracker models.
    /// </summary>
    public sealed class VrcTimeline
    {
        public sealed class Change
        {
            public double T;
            public int Spine = -1, Measure = -1;
            public string Source = "";
            public override string ToString() => $"{T:F1} s: " + string.Join(", ", new[] { Spine >= 0 ? SpineName(Spine) : null, Measure >= 0 ? MeasureName(Measure) : null }.Where(x => x != null)) + $" ({Source})";
        }

        public List<TakeCalibrations.TakeCalibration> Calibrations = new List<TakeCalibrations.TakeCalibration>();
        public List<TakeCalibrations.TakeReload> SettingReloads = new List<TakeCalibrations.TakeReload>();
        public readonly List<Change> Changes = new List<Change>();
        public int InitialSpine = -1, InitialMeasure = -1;

        public static string SpineName(int v) => v == 0 ? "Lock Hip" : v == 1 ? "Lock Head" : v == 2 ? "Lock Both" : "?";
        public static string MeasureName(int v) => v == 0 ? "by arm span" : v == 1 ? "by height" : "?";
        public static CalLock LockOf(int spine) => spine == 0 ? CalLock.Hips : spine == 1 ? CalLock.Head : CalLock.Both;
        public static CalMeasure MeasureOf(int m) => m == 0 ? CalMeasure.ArmSpan : CalMeasure.Height;

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Recording, VrcTimeline> Cache = new System.Runtime.CompilerServices.ConditionalWeakTable<Recording, VrcTimeline>();
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Recording, string> CacheKey = new System.Runtime.CompilerServices.ConditionalWeakTable<Recording, string>();

        /// <summary>The take's timeline (cached per take; rebuilt when the hand-added changes or the events change).</summary>
        public static VrcTimeline For(Recording rec)
        {
            string key = rec.Events.Count + "|" + Json.Serialize(rec.Edits.VrcChanges.Select(c => (object)c.ToJson()).ToList());
            lock (Cache)
            {
                if (Cache.TryGetValue(rec, out var tl) && CacheKey.TryGetValue(rec, out var k) && k == key) return tl;
                tl = Build(rec);
                Cache.Remove(rec); CacheKey.Remove(rec);
                Cache.Add(rec, tl); CacheKey.Add(rec, key);
                return tl;
            }
        }

        public static VrcTimeline Build(Recording rec)
        {
            var tl = new VrcTimeline();
            var init = VrcSettings.Values.FromJson(rec.Extra.GetObj("vrchatSettings"));
            tl.InitialSpine = init.SpineMode; tl.InitialMeasure = init.MeasurementType;
            tl.Calibrations = TakeCalibrations.FindAllInTake(rec, out tl.SettingReloads);

            // recorded settings (polled while recording)
            int spine = tl.InitialSpine, meas = tl.InitialMeasure;
            foreach (var e in rec.Events.Where(e => e.Type == "vrcSettings").OrderBy(e => e.T))
            {
                Dictionary<string, object> d;
                try { d = Json.ParseObject(e.Text); } catch { continue; }
                var v = VrcSettings.Values.FromJson(d);
                if (v.SpineMode >= 0 && v.SpineMode != spine)
                {
                    spine = v.SpineMode;
                    tl.Changes.Add(new Change { T = e.T, Spine = spine, Source = "VRChat settings" });
                }
                if (v.MeasurementType >= 0 && v.MeasurementType != meas)
                {
                    meas = v.MeasurementType;
                    // VRChat applies a measurement change with an avatar reload: it takes effect when the IK is set up again
                    var r = tl.SettingReloads.Where(x => Math.Abs(x.Reload - e.T) < 4).OrderBy(x => Math.Abs(x.Reload - e.T)).FirstOrDefault();
                    tl.Changes.Add(new Change { T = r != null ? r.Ready : e.T, Measure = meas, Source = "VRChat settings" });
                }
            }
            // takes recorded without the settings log: each setting-only reload toggles the measurement (two values)
            if (!rec.Events.Any(e => e.Type == "vrcSettings") && tl.InitialMeasure >= 0)
            {
                int m = tl.InitialMeasure;
                foreach (var r in tl.SettingReloads.Where(x => x.Ready > 0.5).OrderBy(x => x.Reload)) // earlier ones are in the start settings already
                {
                    m = 1 - m;
                    tl.Changes.Add(new Change { T = r.Ready, Measure = m, Source = "avatar reload in VRChat's log (measurement toggled)" });
                }
            }
            foreach (var c in rec.Edits.VrcChanges)
                tl.Changes.Add(new Change { T = c.T, Spine = c.Spine, Measure = c.Measure, Source = "added to the take" });
            tl.Changes.Sort((a, b) => a.T.CompareTo(b.T));
            return tl;
        }

        /// <summary>VRChat's spine mode and measurement type at a moment (-1 = unknown).</summary>
        public void SettingsAt(double t, out int spine, out int measure)
        {
            spine = InitialSpine; measure = InitialMeasure;
            foreach (var c in Changes)
            {
                if (c.T > t) break;
                if (c.Spine >= 0) spine = c.Spine;
                if (c.Measure >= 0) measure = c.Measure;
            }
        }

        /// <summary>
        /// Stretches where VRChat's own avatar is not tracking the user (calibration mode from the click until just after the
        /// confirm; a setting-only avatar reload until its IK is set up): VRChat hides or freezes the avatar there.
        /// </summary>
        public List<double[]> NotTracking()
        {
            var res = new List<double[]>();
            foreach (var c in Calibrations) res.Add(new[] { c.Click, Math.Max(c.Confirm, c.Logged) + 0.35 });
            foreach (var r in SettingReloads) if (r.Ready + 0.35 > 0) res.Add(new[] { r.Reload, r.Ready + 0.35 });
            res.Sort((a, b) => a[0].CompareTo(b[0]));
            return res;
        }

        public string Describe()
        {
            var parts = new List<string>();
            if (InitialSpine >= 0 || InitialMeasure >= 0) parts.Add($"start: {SpineName(InitialSpine)}, {MeasureName(InitialMeasure)}");
            foreach (var c in Calibrations) parts.Add($"calibration {c.Click:F1}-{c.Confirm:F1} s");
            foreach (var c in Changes) parts.Add(c.ToString());
            return parts.Count == 0 ? "no VRChat events" : string.Join("; ", parts);
        }
    }
}
