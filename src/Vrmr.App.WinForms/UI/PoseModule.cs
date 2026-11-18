using System;
using System.Linq;
using System.Windows.Forms;

namespace VRMR.UI
{
    /// <summary>
    ///     Wires the posing tool / action inspector into the scene editor: bumps the pose cache on edits and evaluations,
    ///     refreshes the inspector when the playhead moves, and adds "+ Device take" (a keyframe-only take: an actor animated
    ///     only by keyed devices, solved by the IK, starting from a standing pose).
    /// </summary>
    public sealed class PoseModule : ISceneModule
    {
        readonly Timer _timer = new() { Interval = 120 };
        SceneEditor _ed;
        double _lastPlayhead = double.NaN;
        DateTime _lastRefresh;

        public void Attach(SceneEditor ed)
        {
            _ed = ed;
            ed.Edited += PoseState.Notify;
            ed.Evaluated += () => { PoseState.Version++; };
            ed.SceneChanged += () => { PoseState.Notify(); };
            ed.SelectionChanged += () =>
            {
                PoseState.Version++;
                ed.View.Invalidate();
            };
            var b = ed.AddToolbarButton("+ Device take", () => { },
                "A keyframe-only take: a new actor animated only by keyed devices (headset, controllers, trackers), solved by the IK like a recording. Starts from a standing pose.");
            b.Click += (o, e) => DeviceTakeMenu(b);
            // next to "+ Keys" (module buttons are appended after the status text, out of view)
            var bar = b.Parent;
            if (bar != null)
            {
                Control keys = null;
                foreach (Control c in bar.Controls)
                    if (c is DarkButton db && db.Text.StartsWith("+ Keys"))
                        keys = c;
                if (keys != null) bar.Controls.SetChildIndex(b, bar.Controls.IndexOf(keys) + 1);
            }

            _timer.Tick += (o, e) => Tick();
            _timer.Start();
        }

        void Tick()
        {
            if (_ed?.Scene == null) return;
            double p = _ed.Playhead;
            if (Math.Abs(p - _lastPlayhead) < 1e-9) return;
            // only the keyframe section shows playhead values; refresh it (at most 4 times a second while playing)
            if (_ed.Playing && (DateTime.Now - _lastRefresh).TotalMilliseconds < 250) return;
            _lastPlayhead = p;
            _lastRefresh = DateTime.Now;
            var st = _ed.SelectedStrip;
            if (st != null && st.Kind == StripKind.Action) _ed.Inspector.RefreshValues();
        }

        void DeviceTakeMenu(Control at)
        {
            var m = new ContextMenuStrip();
            foreach (var set in KeyRest.Sets)
            {
                var roles = set.Roles;
                m.Items.Add(set.Name, null, (o, e) => CreateDeviceTake(_ed, roles));
            }

            m.Show(at, 0, at.Height);
        }

        /// <summary>
        ///     The library entry of a skeleton: its own id, else the entry whose name it carries (a take's saved skeleton is
        ///     named after its avatar, e.g. "Avatar_ft" -> "Avatar"), else the library's first avatar.
        /// </summary>
        static string LibraryIdOf(SceneEditor ed, AvatarProfile p)
        {
            var choices = ed.AvatarChoices().Where(x => !string.IsNullOrEmpty(x.Id)).ToList();
            if (p != null && !string.IsNullOrEmpty(p.LibraryId) && choices.Any(x => x.Id == p.LibraryId))
                return p.LibraryId;
            if (p != null)
            {
                var m = choices.Where(x =>
                        x.Label.Length > 0 && (string.Equals(x.Label, p.Name, StringComparison.OrdinalIgnoreCase) ||
                                               p.Name.StartsWith(x.Label.Split(' ')[0],
                                                   StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(x => x.Label.Length).FirstOrDefault();
                if (m.Id != null) return m.Id;
            }

            return choices.Select(x => x.Id).FirstOrDefault() ?? "";
        }

        /// <summary>
        ///     A keyframe-only take: a new actor (or the selected one when it has nothing yet) with a "Devices" input track and
        ///     a Replace keyframe strip whose devices are keyed at its start in a standing pose (and held). Move them with the
        ///     Pose tool; the IK solves the body.
        /// </summary>
        public static Strip CreateDeviceTake(SceneEditor ed, BodyRole[] roles, double length = 5)
        {
            if (ed.Scene == null) return null;
            Strip made = null;
            ed.Edit("Add device take", () =>
            {
                var s = ed.Scene;
                var sel = ed.SelectedActor;
                var a = sel != null && !sel.Tracks.Any(t => t.Strips.Count > 0) ? sel : null;
                if (a == null)
                {
                    a = new Actor { Id = Scene.NewId("a"), Name = "Actor " + (s.Actors.Count + 1) };
                    // a take-less actor needs an avatar: the shown actor's (the one it is solved on), else the library's first
                    a.Avatar = ed.ViewActor?.Avatar ?? "";
                    if (string.IsNullOrEmpty(a.Avatar)) a.Avatar = LibraryIdOf(ed, ed.ClipOf(ed.ViewActor)?.Profile);
                    s.Actors.Add(a);
                }

                // a device take: device keys on a pose track (the evaluator solves them with the IK like a recording)
                var tr = SceneOps.AddTrack(a, a.Tracks.Count, "Devices");
                tr.Kind = TrackKind.Pose;
                double start = Math.Max(s.Start, Math.Round(ed.Playhead * s.Fps) / s.Fps);
                made = SceneOps.AddActionStrip(s, tr, "Device take", start, length);
                var act = s.Action(made.Source);
                act.Name = "Device take";
                (var rig, float scale) = ed.Eval.RigOf(s, a);
                var pose = new ScenePose();
                KeyRest.Standing(rig, scale, pose);
                // place the standing actor next to the others (1 m to the side per actor)
                int idx = s.Actors.IndexOf(a);
                if (idx > 0)
                {
                    var shift = new Vec3(idx * 1.0f, 0, 0);
                    pose.Hips += shift;
                }

                foreach (var kv in KeyRest.DevicesFor(rig, scale, pose, roles))
                    KeyframeOps.KeyDevice(act, 0, kv.Key, kv.Value);
            });
            if (made != null)
            {
                ed.Select(made);
                PoseState.Current = PoseState.Mode.Devices;
                PoseState.Notify();
            }

            return made;
        }
    }
}
