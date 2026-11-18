using System;
using System.Linq;

namespace VRMR.UI
{
    /// <summary>A strip: timing, blending, root offset. (Body-part masks and modifiers are their own sections.)</summary>
    public sealed class StripInspector : ISceneInspector
    {
        public int Order => 30;

        public bool Applies(SceneEditor ed)
        {
            return ed.SelectionKind == "strip";
        }

        public void Build(InspectorContext c)
        {
            var ed = c.Editor;
            var s0 = ed.SelectedStrip;
            if (s0 == null) return;
            string id = s0.Id;

            Strip S()
            {
                return ed.Scene.FindStrip(id).Strip;
            }

            int n = ed.SelectedStrips.Count;
            c.Section(n > 1 ? $"Strip (1 of {n} selected)" : "Strip");
            Props.Text(c, "Name", () => S()?.Name ?? "", v => ed.Edit("Rename strip", () => S().Name = v.Trim()));
            var src = c.Note("", false);
            c.OnRefresh(() =>
            {
                var s = S();
                if (s == null) return;
                if (s.Kind == StripKind.Take)
                {
                    var t = ed.Scene.Take(s.Source);
                    src.Text = t == null
                        ? "Take: missing"
                        : $"Take: {t.Name}  ({Theme.Time(t.Duration)})\nplays {s.SrcIn:0.00}–{s.SrcOut:0.00} s of it";
                }
                else
                {
                    var a = ed.Scene.Action(s.Source);
                    src.Text = a == null
                        ? "Keyframes: missing"
                        : $"Keyframes: {a.Name}  ({a.KeyTimes().Count} keys on {a.Channels.Count(ch => ch.Value.Keys.Count > 0)} channels)";
                }
            });
            if (s0.Kind == StripKind.Take)
                Props.Buttons(c, ("Open take in the Clip tab", () =>
                {
                    var t = ed.Scene.Take(S()?.Source ?? "");
                    if (t != null) ed.OpenTake?.Invoke(t.Path);
                }));

            c.Section("Timing");
            Props.Number(c, "Start", () => S()?.Start ?? 0, v => ed.Edit("Strip start", () => S().Start = Quant(ed, v)),
                "0.000", " s", "Where the strip starts on the scene timeline");
            Props.Number(c, "Source in", () => S()?.SrcIn ?? 0, v => ed.Edit("Strip in", () =>
            {
                var s = S();
                SceneOps.SourceLimits(ed.Scene, s, out double lo, out _);
                s.SrcIn = Math.Max(lo, Math.Min(v, s.SrcOut - 1 / ed.Scene.Fps));
            }), "0.000", " s", "Where in the take the strip starts");
            Props.Number(c, "Source out", () => S()?.SrcOut ?? 0, v => ed.Edit("Strip out", () =>
            {
                var s = S();
                SceneOps.SourceLimits(ed.Scene, s, out _, out double hi);
                s.SrcOut = Math.Min(hi, Math.Max(v, s.SrcIn + 1 / ed.Scene.Fps));
            }), "0.000", " s");
            Props.Number(c, "Speed", () => S()?.Speed ?? 1, v => ed.Edit("Strip speed", () => S().Speed = v), "0.###",
                "×", "Playback speed (the strip gets shorter or longer)", 0.05, 20);

            c.Section("Blending");
            Props.Choice(c, "Blend", new[] { "Replace", "Add (on top of the tracks below)" },
                () => (int)(S()?.Blend ?? BlendMode.Replace),
                i => ed.Edit("Strip blend", () => S().Blend = (BlendMode)i),
                "Replace: this strip's pose replaces what the tracks below give (for its body parts). Add: its motion is added on top (a take: relative to its first frame).");
            Props.Slider(c, "Influence", 0, 1, () => S()?.Influence ?? 1, v => S().Influence = v, 0.01f, "0%");
            Props.Number(c, "Blend in", () => S()?.BlendIn ?? 0,
                v => ed.Edit("Blend in", () => S().BlendIn = Math.Max(0, v)), "0.00", " s",
                "Fades the strip in at its start (when it has no transition there)", 0);
            Props.Number(c, "Blend out", () => S()?.BlendOut ?? 0,
                v => ed.Edit("Blend out", () => S().BlendOut = Math.Max(0, v)), "0.00", " s",
                "Fades the strip out at its end (when it has no transition there)", 0);
            Props.Check(c, "Mute", () => S()?.Mute ?? false, v => ed.Edit("Mute strip", () => S().Mute = v));

            c.Section("Position (root)");
            Props.Number(c, "Move X", () => S()?.OffsetX ?? 0,
                v => ed.Edit("Strip position", () => S().OffsetX = (float)v), "0.000", " m");
            Props.Number(c, "Move Z", () => S()?.OffsetZ ?? 0,
                v => ed.Edit("Strip position", () => S().OffsetZ = (float)v), "0.000", " m");
            Props.Number(c, "Height", () => S()?.OffsetY ?? 0,
                v => ed.Edit("Strip position", () => S().OffsetY = (float)v), "0.000", " m");
            Props.Number(c, "Turn", () => S()?.OffsetYaw ?? 0,
                v => ed.Edit("Strip turn", () => S().OffsetYaw = (float)v), "0.0", "°",
                "Turns the strip about the vertical axis through the scene origin");
            var align = c.Note("");
            c.OnRefresh(() =>
            {
                var s = S();
                if (s == null) return;
                var tr = ed.Scene.FindStrip(id).Track?.TransitionInto(id);
                align.Text = tr != null && tr.Align != TransitionAlign.None
                    ? "Placed onto the strip before it by its transition (the values above are added on top)."
                    : "";
            });
            Props.Buttons(c, ("Reset position", () => ed.Edit("Reset position", () =>
            {
                var s = S();
                s.OffsetX = s.OffsetY = s.OffsetZ = s.OffsetYaw = 0;
            })));
        }

        static double Quant(SceneEditor ed, double t)
        {
            return Math.Round(t * ed.Scene.Fps) / ed.Scene.Fps;
        }
    }
}
