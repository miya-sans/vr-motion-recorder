using System.Collections.Generic;
using System.Linq;

namespace VRMR.UI
{
    /// <summary>
    ///     Cleanup on a whole actor: modifiers on the final pose, after all layers and transitions (e.g. a foot lock
    ///     that also covers the crossfades between takes).
    /// </summary>
    public sealed class LayerActorInspector : ISceneInspector
    {
        public int Order => 15;

        public bool Applies(SceneEditor ed)
        {
            return ed.SelectionKind == "actor";
        }

        public void Build(InspectorContext c)
        {
            var ed = c.Editor;
            var a0 = ed.SelectedActor;
            if (a0 == null) return;
            string id = a0.Id;

            Actor A()
            {
                return ed.Scene.ActorById(id);
            }

            var h = new LayerModifiers.Host
            {
                What = "actor", Key = id, List = () => A()?.Modifiers,
                Types = SceneModifiers.Actor.Values.Select(m => (m.Type, m.Label, (object)m)).OrderBy(t => t.Label)
                    .ToList(),
                RangeUnits = "scene",
                FindGlitches = () =>
                {
                    var clip = ed.ClipOf(A());
                    if (clip == null) return null;
                    return PoseRepair.FindGlitches(PoseFrames.OfClip(PoseRig.For(clip.Profile), clip, ed.Scene.Start));
                }
            };
            LayerModifiers.Build(c, h, "Cleanup (whole actor)");
        }
    }

    /// <summary>Device-level cleanup modifiers for strips on input tracks (smoothing / filling a tracker before the solve).</summary>
    public static class LayerDeviceModifiers
    {
        public static List<(string Type, string Label, object Impl)> Types()
        {
            return SceneModifiers.Device.Values.Select(m => (m.Type, m.Label, (object)m)).OrderBy(t => t.Label)
                .ToList();
        }
    }
}
