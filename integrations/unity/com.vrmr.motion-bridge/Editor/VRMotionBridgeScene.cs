#if UNITY_EDITOR
// VR Motion Bridge — scenes from VR Motion Recorder's NLA scene editor.
// The app exports a scene as one set of take files per actor (<scene>_<actor>.fbx + .vrmr.json humanoid sidecar,
// _face.anim, _params.anim, _body.anim) plus <scene>.vrmrscene.json: fps, range, markers, actors (avatar base model,
// files) and a "cameras" list (reserved for a future camera system; Unity-space pose + vertical FOV per frame).
// Each actor's FBX is set up as Humanoid by VRMotionBridgePostprocessor, exactly like a take.
// Tools > VR Motion Bridge > Create Timeline From Scene: one Timeline, a group per actor with Body + Face tracks bound
// to the avatars in the open scene, markers as Signals. Enter Play mode and PhysBones simulate on top.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
#if VRMR_TIMELINE
using UnityEngine.Events;
using UnityEngine.Playables;
using UnityEngine.Timeline;
#endif

namespace VRMotionBridge
{
    [Serializable] public class SceneStripInfo { public string id, name, kind, source, blend, maskName; public double start, end, srcIn, srcOut, speed, blendIn, blendOut; public float influence; public string[] mask; public bool mute; public int frameStart, frameEnd; }
    [Serializable] public class SceneTransitionInfo { public string from, to, curve, align; public double duration; }
    [Serializable] public class SceneTrackInfo { public string id, name, kind; public bool mute, solo; public SceneStripInfo[] strips; public SceneTransitionInfo[] transitions; }
    [Serializable]
    public class SceneActorInfo
    {
        public string id, name, clip, fbx, sidecar, bodyAnim, faceAnim, paramsAnim, faceJson;
        public string avatarBase, sourceAvatarModel, avatarLibraryId, avatarId, avatarName, faceMesh;
        public float scale = 1; public int frames; public bool hidden;
        public SceneTrackInfo[] tracks;
    }
    [Serializable] public class SceneMarkerInfo { public string name; public double t, sceneTime; public int frame; }
    [Serializable]
    public class SceneManifest
    {
        public string app, kind, name;
        public int formatVersion;
        public float fps = 60;
        public double start, end, duration;
        public int frames, frameStart, frameEnd;
        public SceneActorInfo[] actors;
        public SceneMarkerInfo[] markers;
        /// <summary>Reserved for a future camera system (same shape as the Blender add-on's cameras).</summary>
        public BundleCamera[] cameras;
        public string[] warnings;
    }

    public class VRMotionBridgeScenePostprocessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            foreach (var p in imported)
                if (p.EndsWith(VRMotionBridgeScene.Suffix, StringComparison.OrdinalIgnoreCase))
                    Debug.Log($"[VR Motion Bridge] Scene imported: {p}. Select its folder, then Tools > VR Motion Bridge > Create Timeline From Scene.");
        }
    }

    public static class VRMotionBridgeScene
    {
        public const string Suffix = ".vrmrscene.json";

        public static SceneManifest Load(string path)
        {
            var m = JsonUtility.FromJson<SceneManifest>(File.ReadAllText(path));
            if (m == null || m.kind != "scene-export") throw new InvalidDataException("Not a VR Motion Recorder scene export: " + path);
            return m;
        }

        /// <summary>The selected scene manifest: the file itself, a folder holding one, or any file next to one.</summary>
        static string SelectedManifest()
        {
            foreach (var o in Selection.objects)
            {
                string p = AssetDatabase.GetAssetPath(o);
                if (string.IsNullOrEmpty(p)) continue;
                if (p.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) return p;
                string dir = AssetDatabase.IsValidFolder(p) ? p : Path.GetDirectoryName(p);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                var j = Directory.GetFiles(dir, "*" + Suffix).FirstOrDefault();
                if (j != null) return j.Replace('\\', '/');
            }
            return null;
        }

        static bool Same(string a, string b) => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        static bool Contains(string hay, string needle) => !string.IsNullOrEmpty(hay) && !string.IsNullOrEmpty(needle) && hay.IndexOf(needle.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// The avatar in the open scene(s) for an actor, not used by an earlier actor: an object named like the actor,
        /// then the avatar whose model is the actor's base model (selected ones first), then one whose name contains the
        /// base model's / VRChat avatar's name, then any still-unused selected avatar. Null if nothing fits.
        /// </summary>
        public static Animator FindActorAvatar(SceneActorInfo a, HashSet<Animator> used, List<Animator> selected)
        {
            var all = UnityEngine.Object.FindObjectsOfType<Animator>(true).Where(x => !used.Contains(x) && x.gameObject.scene.IsValid()).ToList();
            var pick = all.FirstOrDefault(x => Same(x.gameObject.name, a.name));
            if (pick == null && !string.IsNullOrEmpty(a.sourceAvatarModel))
            {
                var byModel = all.Where(x => x.avatar != null && AssetDatabase.GetAssetPath(x.avatar) == a.sourceAvatarModel).ToList();
                pick = byModel.FirstOrDefault(selected.Contains) ?? byModel.FirstOrDefault();
            }
            if (pick == null)
            {
                var byName = all.Where(x => x.isHuman && (Contains(x.gameObject.name, a.avatarBase) || Contains(x.gameObject.name, a.avatarName) ||
                                                           (x.avatar != null && Contains(x.avatar.name, a.avatarBase)))).ToList();
                pick = byName.FirstOrDefault(selected.Contains) ?? byName.FirstOrDefault();
            }
            if (pick == null) pick = selected.FirstOrDefault(x => !used.Contains(x));
            if (pick != null) used.Add(pick);
            return pick;
        }

        /// <summary>The actor's body clip: the humanoid clip of its FBX, else its generic _body.anim.</summary>
        public static AnimationClip BodyClip(string dir, SceneActorInfo a)
        {
            if (!string.IsNullOrEmpty(a.fbx))
            {
                var c = AssetDatabase.LoadAllAssetsAtPath(dir + "/" + a.fbx).OfType<AnimationClip>().FirstOrDefault(x => !x.name.StartsWith("__preview"));
                if (c != null) return c;
            }
            return string.IsNullOrEmpty(a.bodyAnim) ? null : AssetDatabase.LoadAssetAtPath<AnimationClip>(dir + "/" + a.bodyAnim);
        }

#if VRMR_TIMELINE
        [MenuItem("Tools/VR Motion Bridge/Create Timeline From Scene")]
        static void CreateTimelineMenu() => CreateFromSelection(false);

        [MenuItem("Tools/VR Motion Bridge/Create Timeline From Scene (exact pose, Generic)")]
        static void CreateTimelineExactMenu()
        {
            if (!EditorUtility.DisplayDialog("VR Motion Bridge",
                "Exact pose: each actor plays its transform clip (_body.anim), so the bound avatars' Animators are switched to Generic (their Avatar field is cleared; Undo or re-assign it to go back).\n\n" +
                "Use it for renders on the same avatar the scene was solved on. The normal (Humanoid) version retargets onto any avatar but goes through Unity's muscle space, which can move the arms for some poses.", "Create", "Cancel")) return;
            CreateFromSelection(true);
        }

        static void CreateFromSelection(bool exact)
        {
            var p = SelectedManifest();
            if (p == null) { EditorUtility.DisplayDialog("VR Motion Bridge", "Select a VR Motion Recorder scene folder or its .vrmrscene.json (export the scene from VR Motion Recorder into this Unity project).", "OK"); return; }
            try { CreateTimeline(p, exact); }
            catch (Exception e) { Debug.LogError("[VR Motion Bridge] " + e); EditorUtility.DisplayDialog("VR Motion Bridge", e.Message, "OK"); }
        }

        /// <summary>
        /// One Timeline for the scene: per actor a group with Body and Face tracks bound to its avatar, markers as Signals
        /// (a SignalReceiver on the director lists them), and the manifest's cameras, if any.
        /// Body = the humanoid FBX clip (retargets onto any humanoid; Unity's muscle space can't hold every IK pose exactly),
        /// or with exact = true the transform clip (_body.anim) with the avatar's Animator switched to Generic (Unity ignores
        /// transform curves on a humanoid's bones), which reproduces the app's pose on the same avatar.
        /// </summary>
        public static GameObject CreateTimeline(string manifestPath, bool exact = false)
        {
            var m = Load(manifestPath);
            string dir = Path.GetDirectoryName(manifestPath).Replace('\\', '/');
            string sceneName = string.IsNullOrEmpty(m.name) ? Path.GetFileName(manifestPath).Replace(Suffix, "") : m.name;
            float fps = m.fps > 0 ? m.fps : 60f;
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            string tlPath = AssetDatabase.GenerateUniqueAssetPath(dir + "/" + VRMotionBridgeBundle.Safe(sceneName) + "_Timeline.playable");
            AssetDatabase.CreateAsset(timeline, tlPath);
            timeline.editorSettings.frameRate = fps;
            if (m.duration > 0) { timeline.durationMode = TimelineAsset.DurationMode.FixedLength; timeline.fixedDuration = m.duration; }

            var go = new GameObject(sceneName + " (VRMR Scene)");
            Undo.RegisterCreatedObjectUndo(go, "Create VRMR Scene Timeline");
            var director = go.AddComponent<PlayableDirector>();
            director.playableAsset = timeline;

            var selected = Selection.gameObjects.Select(g => g.GetComponentInParent<Animator>()).Where(a => a != null).Distinct().ToList();
            var used = new HashSet<Animator>();
            var report = new List<string>();
            foreach (var a in m.actors ?? new SceneActorInfo[0])
            {
                var group = timeline.CreateTrack<GroupTrack>(null, a.name);
                var avatar = FindActorAvatar(a, used, selected);
                VRMotionBridgeBundle.AlwaysAnimate(avatar);
                var body = exact && !string.IsNullOrEmpty(a.bodyAnim) ? AssetDatabase.LoadAssetAtPath<AnimationClip>(dir + "/" + a.bodyAnim) : BodyClip(dir, a);
                if (exact && body != null && !body.humanMotion && avatar != null && avatar.avatar != null)
                {
                    Undo.RecordObject(avatar, "Animator to Generic");
                    report.Add($"{avatar.gameObject.name}: Animator switched to Generic for the exact pose (was {avatar.avatar.name}; Undo or re-assign to restore)");
                    avatar.avatar = null;
                }
                var face = string.IsNullOrEmpty(a.faceAnim) ? null : AssetDatabase.LoadAssetAtPath<AnimationClip>(dir + "/" + a.faceAnim);
                AnimationTrack bt = null;
                if (body != null)
                {
                    bt = timeline.CreateTrack<AnimationTrack>(group, "Body");
                    var c = bt.CreateClip(body); c.start = 0; c.displayName = a.clip;
                    // the clip's root motion is in scene space: actors keep their places relative to each other
                    bt.trackOffset = TrackOffset.ApplyTransformOffsets; bt.position = Vector3.zero; bt.rotation = Quaternion.identity;
                    if (avatar != null) director.SetGenericBinding(bt, avatar);
                }
                // face: an override track of Body masked to no humanoid part (a separate track would reset the muscles)
                if (face != null) VRMotionBridgeBundle.AddFaceTrack(timeline, director, group, bt, face, avatar, dir).name = "Face";
                string what = (body != null ? (body.humanMotion ? "humanoid body" : "generic body") : "no body clip") + (face != null ? " + face" : "");
                report.Add(avatar != null ? $"{a.name} -> {avatar.gameObject.name} ({what})" : $"{a.name}: no avatar found ({what}); bind its tracks by hand");
                if (!exact && body != null && !body.humanMotion && !string.IsNullOrEmpty(a.fbx))
                    Debug.LogWarning($"[VR Motion Bridge] {a.fbx} is not Humanoid yet (still importing?): the body plays by transform path. Run Create Timeline From Scene again once it is.");
            }

            // markers -> Signals on the timeline's marker area (received by the director's GameObject)
            if (m.markers != null && m.markers.Length > 0)
            {
                timeline.CreateMarkerTrack();
                var receiver = go.AddComponent<SignalReceiver>();
                string sigDir = dir + "/Signals";
                if (!AssetDatabase.IsValidFolder(sigDir)) AssetDatabase.CreateFolder(dir, "Signals");
                foreach (var mk in m.markers)
                {
                    string nm = string.IsNullOrEmpty(mk.name) ? "Marker" : mk.name;
                    string sp = sigDir + "/" + VRMotionBridgeBundle.Safe(nm) + ".signal";
                    var sig = AssetDatabase.LoadAssetAtPath<SignalAsset>(sp);
                    if (sig == null) { sig = ScriptableObject.CreateInstance<SignalAsset>(); AssetDatabase.CreateAsset(sig, sp); }
                    double time = mk.frame / (double)fps;
                    var e = timeline.markerTrack.CreateMarker<SignalEmitter>(time);
                    e.asset = sig; e.name = nm; e.retroactive = false; e.emitOnce = false;
                    if (!receiver.GetRegisteredSignals().Contains(sig)) receiver.AddReaction(sig, new UnityEvent());
                }
            }

            // cameras (a future camera system writes them; same as the Blender bundle's)
            if (m.cameras != null)
                foreach (var cam in m.cameras)
                {
                    var clip = VRMotionBridgeBundle.CameraClip(cam, fps);
                    if (clip == null) continue;
                    string cp = VRMotionBridgeBundle.Save(clip, dir + "/" + VRMotionBridgeBundle.Safe(sceneName) + "_" + VRMotionBridgeBundle.Safe(cam.name) + "_camera.anim");
                    VRMotionBridgeBundle.AddCameraTrack(timeline, director, cam, AssetDatabase.LoadAssetAtPath<AnimationClip>(cp));
                }

            EditorUtility.SetDirty(timeline);
            AssetDatabase.SaveAssets();
            Selection.activeGameObject = go;
            Debug.Log($"[VR Motion Bridge] Scene Timeline created: {tlPath} ({m.actors?.Length ?? 0} actor(s), {m.markers?.Length ?? 0} marker(s), {fps} fps, {m.duration:0.##} s)\n  " +
                      string.Join("\n  ", report) + "\nEnter Play mode to see PhysBones react while it plays (Unity Recorder can render it).");
            return go;
        }
#endif
    }
}
#endif
