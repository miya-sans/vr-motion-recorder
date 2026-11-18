using System;
using System.Linq;

namespace VRMR.UI
{
    /// <summary>Scene settings (nothing more specific selected).</summary>
    public sealed class SceneInspector : ISceneInspector
    {
        public int Order => 0;
        public bool Applies(SceneEditor ed) => ed.SelectionKind == "scene";
        public void Build(InspectorContext c)
        {
            var ed = c.Editor; var s = ed.Scene;
            c.Section("Scene");
            Props.Text(c, "Name", () => ed.Scene.Name, v => ed.Rename(v));
            Props.Number(c, "Frame rate", () => ed.Scene.Fps, v => ed.Edit("Frame rate", () => ed.Scene.Fps = (float)v), "0", " fps", "Evaluation and export frame rate", 1, 240);
            Props.Number(c, "Start", () => ed.Scene.Start, v => ed.Edit("Scene start", () => ed.Scene.Start = Math.Min(v, ed.Scene.RangeEnd - 0.1)), "0.00", " s");
            Props.Number(c, "End", () => ed.Scene.RangeEnd, v => ed.Edit("Scene end", () => ed.Scene.End = Math.Max(v, ed.Scene.Start + 0.1)), "0.00", " s", "Right-click the ruler: 'Scene end: last strip' to follow the content again");
            var info = c.Note("", false);
            c.OnRefresh(() =>
            {
                var sc = ed.Scene; if (sc == null) return;
                info.Text = $"{sc.Actors.Count} actor(s), {sc.AllStrips().Count()} strip(s) from {sc.Takes.Count} take(s), {sc.Actions.Count} keyframe action(s)" +
                            (ed.Bake != null ? $"\nEvaluated in {ed.LastEvalMs} ms" : "");
            });
            var unused = Props.Buttons(c, ("Remove unused takes", () => ed.Edit("Remove unused takes", () => SceneOps.DropUnusedTakes(ed.Scene))));
            Theme.Tip(info, "Takes are only read: cutting, moving or deleting strips never changes a recording. Scenes are saved in Documents\\VR Motion Recorder\\Scenes.");
        }
    }

    /// <summary>An actor: name, avatar.</summary>
    public sealed class ActorInspector : ISceneInspector
    {
        public int Order => 10;
        public bool Applies(SceneEditor ed) => ed.SelectionKind == "actor";
        public void Build(InspectorContext c)
        {
            var ed = c.Editor; var a = ed.SelectedActor; if (a == null) return;
            string id = a.Id;
            Actor A() => ed.Scene.ActorById(id);
            c.Section("Actor");
            Props.Text(c, "Name", () => A()?.Name ?? "", v => ed.Edit("Rename actor", () => A().Name = v.Trim()));
            var choices = ed.AvatarChoices();
            var items = new[] { "Auto (the first take's avatar)" }.Concat(choices.Select(x => x.Label)).ToArray();
            Props.Choice(c, "Avatar", items, () => { var cur = A()?.Avatar ?? ""; int i = choices.FindIndex(x => x.Id == cur); return i < 0 ? 0 : i + 1; },
                i => ed.Edit("Actor avatar", () => A().Avatar = i == 0 ? "" : choices[i - 1].Id),
                "Every take of this actor is solved onto this avatar (any humanoid in the library)");
            Props.Check(c, "Hidden in the 3D view", () => A()?.Hidden ?? false, v => ed.Edit("Hide actor", () => A().Hidden = v));
        }
    }

    /// <summary>A track: name, mute / solo / lock.</summary>
    public sealed class TrackInspector : ISceneInspector
    {
        public int Order => 20;
        public bool Applies(SceneEditor ed) => ed.SelectionKind == "track";
        public void Build(InspectorContext c)
        {
            var ed = c.Editor; var t0 = ed.SelectedTrack; if (t0 == null) return;
            string id = t0.Id;
            NlaTrack T() => ed.Scene.FindTrack(id).Track;
            c.Section("Track");
            Props.Text(c, "Name", () => T()?.Name ?? "", v => ed.Edit("Rename track", () => T().Name = v.Trim()));
            Props.Check(c, "Mute", () => T()?.Mute ?? false, v => ed.Edit("Mute track", () => T().Mute = v));
            Props.Check(c, "Solo (only solo tracks play)", () => T()?.Solo ?? false, v => ed.Edit("Solo track", () => T().Solo = v));
            Props.Check(c, "Lock (no edits)", () => T()?.Lock ?? false, v => ed.Edit("Lock track", () => T().Lock = v));
            c.Note(T()?.Kind == TrackKind.Input ? "Input track: changes what the IK solve is fed (devices, influence, face, fingers) for the takes below it." : "Tracks play bottom to top, each strip for its body parts only.");
        }
    }
}
