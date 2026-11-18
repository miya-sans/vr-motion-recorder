using System;
using System.Collections.Generic;
using System.Linq;

namespace VRMR.App.Services
{
    /// <summary>A body tracker of the open take and the body part it is on.</summary>
    public sealed class TrackerRoleInfo
    {
        public PoseStream Stream { get; init; }
        public string Serial { get; init; } = "";
        public string Model { get; init; } = "";
        /// <summary>The role found automatically, or "" before the first solve.</summary>
        public string AutoRole { get; init; } = "";
        /// <summary>The role set by hand, or "" for automatic.</summary>
        public string Role { get; init; } = "";
        /// <summary>What it counts as now.</summary>
        public string Effective => Role.Length > 0 ? Role : AutoRole.Length > 0 ? AutoRole : "?";
    }

    /// <summary>A stretch where a tracker lost tracking, as the list shows it.</summary>
    public sealed class TrackerLossInfo
    {
        public TrackerGap Gap { get; init; }
        public string Tracker { get; init; } = "";
        public string Label { get; init; } = "";
        /// <summary>"Repair", "Remove mark" or "Keep as recorded": what the toggle would do.</summary>
        public string ToggleLabel { get; init; } = "";
        public override string ToString() => Label;
    }

    /// <summary>
    /// The take's body trackers: which body part each is on (automatic or set by hand) and the stretches where one
    /// lost tracking, which are repaired by carrying the tracker with its neighbour unless the user keeps them as recorded.
    /// </summary>
    public sealed class TrackerService
    {
        readonly TakeSession _session;

        /// <summary>The roles a tracker can be given; the first two mean automatic and unused.</summary>
        public static readonly string[] Roles = { "Auto", "None", "Hips", "Chest", "LeftFoot", "RightFoot", "LeftKnee", "RightKnee", "LeftElbow", "RightElbow" };

        /// <summary>Roles in the user's words, same order as <see cref="Roles"/>.</summary>
        public static readonly string[] RoleLabels = { "Automatic", "Not used", "Hips", "Chest", "Left foot", "Right foot", "Left knee", "Right knee", "Left elbow", "Right elbow" };

        public TrackerService(TakeSession session) { _session = session; }

        static bool IsBodyTracker(PoseStream d) => d.DeviceClass == 3 || (d.DeviceClass == 2 && d.ControllerRole != 1 && d.ControllerRole != 2);

        /// <summary>The role name in the user's words.</summary>
        public static string RoleLabel(string role)
        {
            int i = Array.IndexOf(Roles, role);
            return i >= 0 ? RoleLabels[i] : role;
        }

        /// <summary>The take's body trackers.</summary>
        public List<TrackerRoleInfo> Trackers()
        {
            var take = _session.Take;
            if (take == null) return new List<TrackerRoleInfo>();
            return take.Devices.Where(IsBodyTracker).Select(d => new TrackerRoleInfo
            {
                Stream = d, Serial = d.Serial ?? "", Model = d.ModelNumber.Length > 0 ? d.ModelNumber : "Tracker",
                AutoRole = d.Info.GetStr("autoRole", ""), Role = d.BodyRole ?? "",
            }).ToList();
        }

        /// <summary>Sets a tracker's role by its index in <see cref="Roles"/> (0 = automatic).</summary>
        public void SetRole(PoseStream tracker, int roleIndex)
        {
            if (_session.Take == null || tracker == null || roleIndex < 0 || roleIndex >= Roles.Length) return;
            string role = roleIndex == 0 ? "" : Roles[roleIndex];
            if ((tracker.BodyRole ?? "") == role) return;
            _session.EditTake("roles", r => tracker.BodyRole = role);
        }

        /// <summary>One line for the section header.</summary>
        public string RolesSummary()
        {
            if (_session.Take == null) return "";
            var t = Trackers();
            return t.Count == 0 ? "none" : string.Join(", ", t.Select(x => RoleLabel(x.Effective)));
        }

        // ------------------------------------------------------------------------------------------ tracking loss

        static string NameOf(PoseStream d)
        {
            string r = d.BodyRole;
            if (string.IsNullOrEmpty(r)) r = d.Info.GetStr("autoRole", "");
            return string.IsNullOrEmpty(r) ? d.Serial : RoleLabel(r);
        }

        /// <summary>The trackers a loss can be marked for.</summary>
        public List<(PoseStream stream, string name)> MarkableTrackers()
        {
            var take = _session.Take;
            if (take == null) return new List<(PoseStream, string)>();
            return take.Devices.Where(d => d.Count > 2 && IsBodyTracker(d)).Select(d => (d, NameOf(d))).ToList();
        }

        /// <summary>The found and marked losses of the open take.</summary>
        public List<TrackerLossInfo> Losses()
        {
            var take = _session.Take;
            var list = new List<TrackerLossInfo>();
            if (take == null) return list;
            var trackers = take.Devices.Where(IsBodyTracker).ToList();
            foreach (var g in TrackerGaps.For(take))
            {
                var d = trackers.FirstOrDefault(x => x.Serial == g.Serial);
                string name = d != null ? NameOf(d) : g.Serial;
                string end = double.IsPositiveInfinity(g.B) ? "end" : TimeText.Format(g.B);
                string state = g.Ignored ? "kept as recorded" : g.Manual ? "marked" : "repaired";
                list.Add(new TrackerLossInfo
                {
                    Gap = g, Tracker = name,
                    Label = $"{name}  {TimeText.Format(Math.Max(0, g.A))} – {end}  ·  {state}" + (string.IsNullOrEmpty(g.Why) ? "" : "  ·  " + g.Why),
                    ToggleLabel = g.Ignored ? "Repair" : g.Manual ? "Remove mark" : "Keep as recorded",
                });
            }
            return list;
        }

        /// <summary>"2 repaired · 1.4 s", "none", …</summary>
        public string LossSummary(out bool anyRepaired)
        {
            anyRepaired = false;
            var take = _session.Take;
            if (take == null) return "";
            var gaps = TrackerGaps.For(take);
            if (gaps.Count == 0) return "none";
            int repaired = gaps.Count(g => !g.Ignored);
            anyRepaired = repaired > 0;
            double total = gaps.Where(g => !g.Ignored && !double.IsInfinity(g.Length)).Sum(g => g.Length);
            int kept = gaps.Count - repaired;
            return FormattableString.Invariant($"{repaired} repaired · {total:0.0} s") + (kept > 0 ? $" · {kept} kept" : "");
        }

        /// <summary>Repairs a loss, keeps it as recorded, or removes a mark made by hand, whichever applies.</summary>
        public void Toggle(TrackerGap g)
        {
            var take = _session.Take;
            if (take == null || g == null) return;
            double end = double.IsPositiveInfinity(g.B) ? take.Duration : g.B;
            _session.Edit("trackerGaps", e =>
            {
                if (g.Manual) e.TrackerGaps.RemoveAll(m => !m.Keep && m.Serial == g.Serial && Math.Abs(m.A - g.A) < 1e-6);
                else if (g.Ignored) e.TrackerGaps.RemoveAll(m => m.Keep && m.Serial == g.Serial && m.A < g.B && m.B > g.A);
                else e.TrackerGaps.Add(new TrackerGapMark { Serial = g.Serial, A = g.A, B = end, Keep = true });
            }, solve: false);
            TrackerGaps.Invalidate(take);
            _session.RequestSolve();
        }

        /// <summary>Marks a stretch as lost for a tracker (repaired like a found loss). False when the stretch is too short.</summary>
        public bool MarkLost(PoseStream tracker, double start, double end)
        {
            var take = _session.Take;
            if (take == null || tracker == null || start < 0 || end - start < 0.02) return false;
            _session.Edit("trackerGaps", e => e.TrackerGaps.Add(new TrackerGapMark { Serial = tracker.Serial, A = start, B = end }), solve: false);
            TrackerGaps.Invalidate(take);
            _session.RequestSolve();
            return true;
        }

        /// <summary>Removes every mark made by hand in this take.</summary>
        public void ClearMarks()
        {
            var take = _session.Take;
            if (take == null || take.Edits.TrackerGaps.Count == 0) return;
            _session.Edit("trackerGaps", e => e.TrackerGaps.Clear(), solve: false);
            TrackerGaps.Invalidate(take);
            _session.RequestSolve();
        }
    }

    /// <summary>
    /// Which avatar a take is solved onto: the base model of the avatar it was recorded with (found by the library),
    /// or any other entry of the library picked for this take.
    /// </summary>
    public sealed class TakeAvatarService
    {
        readonly TakeSession _session;
        readonly AvatarLibrary _avatars;
        readonly LiveService _live;
        readonly StatusService _status;

        public TakeAvatarService(TakeSession session, AvatarLibrary avatars, LiveService live, StatusService status)
        {
            _session = session; _avatars = avatars; _live = live; _status = status;
        }

        /// <summary>The choices: null first (automatic), then every library entry.</summary>
        public List<AvatarEntry> Choices()
        {
            var list = new List<AvatarEntry> { null };
            list.AddRange(_avatars.All());
            return list;
        }

        /// <summary>The base model the take's own avatar maps to, in words.</summary>
        public string RecordedLabel()
        {
            var r = _session.Take;
            if (r == null) return "";
            if (r.Profile != null) return _avatars.Get(r.Profile.LibraryId)?.Label ?? r.Profile.Name;
            return _avatars.MatchTake(r).Entry?.Label ?? "none";
        }

        /// <summary>The index in <see cref="Choices"/> the take uses now.</summary>
        public int SelectedIndex(List<AvatarEntry> choices)
        {
            var r = _session.Take;
            if (r == null) return 0;
            return Math.Max(0, choices.FindIndex(e => e != null && e.Id.Equals(r.Edits.TargetAvatar, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>Solves the take onto a library entry (null = the avatar it was recorded with).</summary>
        public void Pick(AvatarEntry entry)
        {
            var r = _session.Take;
            if (r == null) return;
            string id = entry?.Id ?? "";
            if (string.Equals(r.Edits.TargetAvatar ?? "", id, StringComparison.OrdinalIgnoreCase)) return;
            _session.Edit("targetAvatar", e => e.TargetAvatar = id);
        }

        /// <summary>What the take was recorded as and what it is solved onto: a short line, the whole story, whether it needs attention.</summary>
        public CalibrationNote Describe()
        {
            var r = _session.Take;
            if (r == null) return new CalibrationNote("", "", false);
            var m = _avatars.MatchTake(r);
            string worn = string.IsNullOrEmpty(r.AvatarName) ? r.AvatarId : r.AvatarName;
            string how = _session.AutoProfile ? m?.How ?? "" : r.Extra.GetObj("avatarBase")?.GetStr("how", null) ?? "skeleton saved with the take";
            var target = _avatars.TargetFor(r, out _);
            bool guess = _session.AutoProfile && m != null && !m.Sure;
            string shortText = target == null ? "There is no avatar to solve onto. Add one under Settings → Avatars."
                : $"Recorded as {worn}" + (guess && m.Entry != null && m.Entry.BuiltIn ? ". None of your base models matches it, so it is solved onto the built-in mannequin: add your avatar under Settings → Avatars, or pick one above."
                                             : guess ? ". The base model is a guess: pick the right one above, then choose Link." : "");
            string full = $"Recorded with: {worn} → base model {RecordedLabel()}" + (how.Length > 0 ? $" ({how})" : "")
                        + $"\nSolved onto: {target?.Name ?? "nothing"}" + (target != null ? FormattableString.Invariant($", eye height {target.EyeHeight:0.000} m") : "");
            return new CalibrationNote(shortText, full, guess || target == null);
        }

        /// <summary>
        /// The avatar this take was recorded with IS the given base model: this take, and every later take with the
        /// same VRChat avatar, use it. For avatars whose name does not say which base model they are.
        /// </summary>
        public bool Link(AvatarEntry entry)
        {
            var r = _session.Take;
            if (r == null) return false;
            if (entry == null) { _status.Notify("Pick the base model in the Avatar list first, then choose Link.", StatusKind.Info); return false; }
            _session.EditTake("profile", take => { take.Profile = entry.Profile(); }, solve: false);
            if (!string.IsNullOrEmpty(r.AvatarId)) _avatars.LinkId(r.AvatarId, entry);
            TrackerRoleFinder.Invalidate(r); VrcAvatarScale.Invalidate(r);
            _session.Edit("targetAvatar", e => e.TargetAvatar = "");
            _live.InvalidateMatch();
            _status.Info(string.IsNullOrEmpty(r.AvatarName) ? $"This take now uses {entry.Label}" : $"{r.AvatarName} is now linked to {entry.Label}");
            return true;
        }

        /// <summary>Uses a humanoid model from a Unity project (its .fbx.meta) as this take's skeleton. Returns a message when the file cannot be used.</summary>
        public string UseUnityModel(string path, bool linkAvatar)
        {
            var r = _session.Take;
            if (r == null || string.IsNullOrEmpty(path)) return null;
            string meta = path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ? path : path + ".meta";
            if (!System.IO.File.Exists(meta)) return "There is no .meta file next to that model. Pick a model inside a Unity project's Assets folder.";
            var profile = _avatars.LoadModel(meta);
            if (profile == null) return "That model has no humanoid rig in its import settings. Set its rig to Humanoid in Unity first.";
            _session.EditTake("profile", take => take.Profile = profile.Clone());
            if (linkAvatar && !string.IsNullOrEmpty(r.AvatarId)) { _avatars.Link(r.AvatarId, profile.SourcePath); _live.InvalidateMatch(); }
            return null;
        }
    }
}
