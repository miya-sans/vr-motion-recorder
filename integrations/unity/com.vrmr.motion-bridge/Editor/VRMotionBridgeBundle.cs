#if UNITY_EDITOR
// VR Motion Bridge — Blender round trip.
// Blender's "Send to Unity" (vrmr_bridge.py) writes <clip>.fbx (body + fingers on the avatar's own armature) and
// <clip>.vrmr.json (face/avatar parameter curves edited alongside the body, Blender cameras with FOV, avatar link).
// This turns the bundle into: <clip>_params.anim (Animator parameters), <clip>_face.anim (blendshapes mapped onto the
// avatar's real face mesh), <clip>_<camera>_camera.anim (transform + field of view), and on request a Timeline.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
#if VRMR_TIMELINE
using UnityEngine.Playables;
using UnityEngine.Timeline;
#endif

namespace VRMotionBridge
{
    [Serializable] public class BundleParam { public string name; public float[] values; }
    [Serializable] public class BundleCamera { public string name; public float[] pos; public float[] rot; public float[] fov; public float near = 0.01f; public float far = 1000f; }
    [Serializable]
    public class Bundle
    {
        public string source, clip, sourceAvatarModel, faceMesh, avatarId;
        public float fps = 60;
        public int frameStart, frameEnd;
        public BundleParam[] @params;
        public BundleCamera[] cameras;
    }

    public class VRMotionBridgeBundlePostprocessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            foreach (var p in imported)
            {
                if (!p.EndsWith(".vrmr.json", StringComparison.OrdinalIgnoreCase)) continue;
                string path = p;
                EditorApplication.delayCall += () => VRMotionBridgeBundle.Build(path);
            }
        }
    }

    public static class VRMotionBridgeBundle
    {
        public static Bundle Load(string path) => JsonUtility.FromJson<Bundle>(File.ReadAllText(path));

        static string ClipName(Bundle b, string jsonPath) =>
            !string.IsNullOrEmpty(b.clip) ? b.clip : Path.GetFileName(jsonPath).Replace(".vrmr.json", "");

        public static void Build(string jsonPath)
        {
            Bundle b;
            try { b = Load(jsonPath); }
            catch (Exception e) { Debug.LogWarning($"[VR Motion Bridge] {jsonPath}: {e.Message}"); return; }
            bool hasParams = b.@params != null && b.@params.Length > 0;
            bool hasCams = b.cameras != null && b.cameras.Length > 0;
            if (!hasParams && !hasCams) return; // plain sidecar from the app (humanoid setup only)
            string dir = Path.GetDirectoryName(jsonPath).Replace('\\', '/');
            string clip = ClipName(b, jsonPath);
            float fps = b.fps > 0 ? b.fps : 60f;
            var made = new List<string>();

            if (hasParams)
            {
                var pc = new AnimationClip { frameRate = fps };
                foreach (var prm in b.@params)
                {
                    if (prm.values == null || prm.values.Length == 0) continue;
                    AnimationUtility.SetEditorCurve(pc, EditorCurveBinding.FloatCurve("", typeof(Animator), prm.name), Curve(prm.values, fps, Integral(prm.values)));
                }
                made.Add(Save(pc, dir + "/" + clip + "_params.anim"));

                // face: Unified Expressions weights from the (edited) parameters -> the avatar's own blendshape names
                string meshPath = string.IsNullOrEmpty(b.faceMesh) ? "Body" : b.faceMesh;
                var meshShapes = TargetShapes(b, meshPath);
                var lookup = new Dictionary<string, float[]>();
                foreach (var prm in b.@params) if (prm.values != null) lookup[prm.name] = prm.values;
                int n = b.@params.Max(p => p.values?.Length ?? 0);
                var fm = new FaceMapping { Mode = FaceMapping.Naming.UnifiedExpressions };
                var series = new Dictionary<string, float[]>();
                for (int f = 0; f < n; f++)
                {
                    int ff = f;
                    var w = fm.Evaluate(name => lookup.TryGetValue(name, out var v) && ff < v.Length ? v[ff] : (float?)null);
                    foreach (var kv in w)
                    {
                        if (!series.TryGetValue(kv.Key, out var arr)) series[kv.Key] = arr = new float[n];
                        arr[f] = kv.Value;
                    }
                }
                if (series.Count > 0)
                {
                    var map = meshShapes != null ? FaceMapping.MatchToMesh(series.Keys, meshShapes) : null;
                    var fc = new AnimationClip { frameRate = fps };
                    int bound = 0;
                    var used = new HashSet<string>();
                    foreach (var kv in series)
                    {
                        string shape = map != null ? (map.TryGetValue(kv.Key, out var m) ? m : null) : kv.Key;
                        if (shape == null || !used.Add(shape)) continue;
                        AnimationUtility.SetEditorCurve(fc, EditorCurveBinding.FloatCurve(meshPath, typeof(SkinnedMeshRenderer), "blendShape." + shape),
                            Curve(kv.Value.Select(v => v * 100f).ToArray(), fps, false));
                        bound++;
                    }
                    if (bound > 0) made.Add(Save(fc, dir + "/" + clip + "_face.anim"));
                    Debug.Log($"[VR Motion Bridge] {clip}: face tracking -> {bound} blendshapes on '{meshPath}'" +
                              (meshShapes == null ? " (avatar not found in the open scene: used Unified Expressions names)" : $" (matched against {meshShapes.Count} shapes on the avatar)"));
                }
            }

            if (hasCams)
                foreach (var c in b.cameras)
                {
                    var cc = CameraClip(c, fps);
                    if (cc != null) made.Add(Save(cc, dir + "/" + clip + "_" + Safe(c.name) + "_camera.anim"));
                }
            AssetDatabase.SaveAssets();
            Debug.Log($"[VR Motion Bridge] {clip}: built " + string.Join(", ", made.Select(Path.GetFileName)) + "  (Tools > VR Motion Bridge > Create Timeline From Bundle to assemble a render scene)");
            // the standalone humanoid clip picks up the face clip just built (a Blender send's FBX has no face keys)
            string fbx = dir + "/" + clip + ".fbx";
            if (File.Exists(fbx)) VRMotionBridgeHumanoidClip.Write(fbx, quiet: true);
        }

        /// <summary>Camera transform + field of view per frame (Unity space) as a clip on the camera object; null if empty.</summary>
        internal static AnimationClip CameraClip(BundleCamera c, float fps)
        {
            int n = c.fov?.Length ?? 0;
            if (n == 0 || c.pos == null || c.rot == null) return null;
            n = Math.Min(n, Math.Min(c.pos.Length / 3, c.rot.Length / 4));
            if (n == 0) return null;
            var cc = new AnimationClip { frameRate = fps };
            var px = new float[n]; var py = new float[n]; var pz = new float[n];
            var qx = new float[n]; var qy = new float[n]; var qz = new float[n]; var qw = new float[n];
            Quaternion prev = Quaternion.identity;
            for (int i = 0; i < n; i++)
            {
                px[i] = c.pos[i * 3]; py[i] = c.pos[i * 3 + 1]; pz[i] = c.pos[i * 3 + 2];
                var q = new Quaternion(c.rot[i * 4], c.rot[i * 4 + 1], c.rot[i * 4 + 2], c.rot[i * 4 + 3]);
                if (i > 0 && Quaternion.Dot(prev, q) < 0) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                prev = q;
                qx[i] = q.x; qy[i] = q.y; qz[i] = q.z; qw[i] = q.w;
            }
            void Set(string prop, Type t, float[] v) => AnimationUtility.SetEditorCurve(cc, EditorCurveBinding.FloatCurve("", t, prop), Curve(v, fps, false));
            Set("m_LocalPosition.x", typeof(Transform), px); Set("m_LocalPosition.y", typeof(Transform), py); Set("m_LocalPosition.z", typeof(Transform), pz);
            Set("m_LocalRotation.x", typeof(Transform), qx); Set("m_LocalRotation.y", typeof(Transform), qy); Set("m_LocalRotation.z", typeof(Transform), qz); Set("m_LocalRotation.w", typeof(Transform), qw);
            Set("field of view", typeof(Camera), c.fov.Take(n).ToArray());
            return cc;
        }

        static bool Integral(float[] v)
        {
            foreach (var x in v) if (Mathf.Abs(x - Mathf.Round(x)) > 1e-4f) return false;
            return true;
        }

        /// <summary>Linear (or stepped) curve with redundant keys removed.</summary>
        static AnimationCurve Curve(float[] v, float fps, bool stepped)
        {
            int n = v.Length;
            var keep = new List<int> { 0 };
            if (stepped)
            {
                for (int i = 1; i < n; i++) if (v[i] != v[i - 1]) keep.Add(i);
            }
            else
            {
                int a = 0;
                for (int i = 1; i < n - 1; i++)
                {
                    int bb = i + 1;
                    bool ok = true;
                    for (int m = a + 1; m < bb; m++)
                    {
                        float t = (m - a) / (float)(bb - a);
                        if (Mathf.Abs(v[a] + (v[bb] - v[a]) * t - v[m]) > 1e-4f * Mathf.Max(1f, Mathf.Abs(v[m]))) { ok = false; break; }
                    }
                    if (!ok) { keep.Add(i); a = i; }
                }
            }
            if (n > 1 && keep[keep.Count - 1] != n - 1) keep.Add(n - 1);
            var keys = new Keyframe[keep.Count];
            for (int k = 0; k < keep.Count; k++)
            {
                int i = keep[k];
                float t = i / fps;
                if (stepped) { keys[k] = new Keyframe(t, v[i], float.PositiveInfinity, float.PositiveInfinity); continue; }
                float inS = 0, outS = 0;
                if (k > 0) { int p = keep[k - 1]; inS = (v[i] - v[p]) / ((i - p) / fps); }
                if (k < keep.Count - 1) { int q = keep[k + 1]; outS = (v[q] - v[i]) / ((q - i) / fps); }
                if (k == 0) inS = outS;
                if (k == keep.Count - 1) outS = inS;
                keys[k] = new Keyframe(t, v[i], inS, outS);
            }
            return new AnimationCurve(keys);
        }

        internal static string Save(AnimationClip clip, string path)
        {
            clip.name = Path.GetFileNameWithoutExtension(path);
            var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (existing != null) { EditorUtility.CopySerialized(clip, existing); EditorUtility.SetDirty(existing); }
            else AssetDatabase.CreateAsset(clip, path);
            return path;
        }

        internal static string Safe(string s)
        {
            foreach (var ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
            return s;
        }

        /// <summary>The recorded avatar in the open scene(s) (matched by its source model), or null.</summary>
        public static Animator FindAvatar(Bundle b)
        {
            var all = UnityEngine.Object.FindObjectsOfType<Animator>(true);
            if (!string.IsNullOrEmpty(b.sourceAvatarModel))
                foreach (var a in all)
                    if (a.avatar != null && AssetDatabase.GetAssetPath(a.avatar) == b.sourceAvatarModel) return a;
            string mesh = string.IsNullOrEmpty(b.faceMesh) ? "Body" : b.faceMesh;
            return all.FirstOrDefault(a => a.isHuman && a.transform.Find(mesh) != null);
        }

        static List<string> TargetShapes(Bundle b, string meshPath)
        {
            Transform t = FindAvatar(b)?.transform.Find(meshPath);
            if (t == null && !string.IsNullOrEmpty(b.sourceAvatarModel))
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(b.sourceAvatarModel);
                t = model != null ? model.transform.Find(meshPath) : null;
            }
            var smr = t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;
            if (smr == null || smr.sharedMesh == null || smr.sharedMesh.blendShapeCount == 0) return null;
            var names = new List<string>();
            for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++) names.Add(smr.sharedMesh.GetBlendShapeName(i));
            return names;
        }

        static string SelectedBundlePath()
        {
            foreach (var o in Selection.objects)
            {
                string p = AssetDatabase.GetAssetPath(o);
                if (string.IsNullOrEmpty(p)) continue;
                if (p.EndsWith(".vrmr.json", StringComparison.OrdinalIgnoreCase)) return p;
                string cand = Path.ChangeExtension(p, null);
                foreach (var suffix in new[] { "_face", "_params", "_body", "_humanoid", "_Timeline" }) if (cand.EndsWith(suffix)) cand = cand.Substring(0, cand.Length - suffix.Length);
                if (File.Exists(cand + ".vrmr.json")) return cand + ".vrmr.json";
                if (AssetDatabase.IsValidFolder(p))
                {
                    var j = Directory.GetFiles(p, "*.vrmr.json").FirstOrDefault();
                    if (j != null) return j.Replace('\\', '/');
                }
            }
            return null;
        }

        [MenuItem("Tools/VR Motion Bridge/Rebuild Clips From Bundle")]
        static void RebuildMenu()
        {
            var p = SelectedBundlePath();
            if (p == null) { EditorUtility.DisplayDialog("VR Motion Bridge", "Select a VR Motion Recorder clip folder, its .fbx or its .vrmr.json.", "OK"); return; }
            Build(p);
        }

#if VRMR_TIMELINE
        [MenuItem("Tools/VR Motion Bridge/Create Timeline From Bundle")]
        static void CreateTimelineMenu()
        {
            var p = SelectedBundlePath();
            if (p == null) { EditorUtility.DisplayDialog("VR Motion Bridge", "Select a VR Motion Recorder clip folder, its .fbx or its .vrmr.json.", "OK"); return; }
            CreateTimeline(p);
        }

        /// <summary>Director + Timeline: avatar body (humanoid FBX clip) and face tracks, plus one animated camera per Blender camera.</summary>
        public static GameObject CreateTimeline(string jsonPath)
        {
            var b = Load(jsonPath);
            string dir = Path.GetDirectoryName(jsonPath).Replace('\\', '/');
            string clip = ClipName(b, jsonPath);
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            string tlPath = AssetDatabase.GenerateUniqueAssetPath(dir + "/" + clip + "_Timeline.playable");
            AssetDatabase.CreateAsset(timeline, tlPath);
            var go = new GameObject(clip + " (VRMR Timeline)");
            Undo.RegisterCreatedObjectUndo(go, "Create VRMR Timeline");
            var director = go.AddComponent<PlayableDirector>();
            director.playableAsset = timeline;
            var avatar = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponentInParent<Animator>() : null;
            if (avatar == null) avatar = FindAvatar(b);
            var body = AssetDatabase.LoadAllAssetsAtPath(dir + "/" + clip + ".fbx").OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview"));
            var face = AssetDatabase.LoadAssetAtPath<AnimationClip>(dir + "/" + clip + "_face.anim");
            if (avatar != null)
            {
                AnimationTrack bt = null;
                AlwaysAnimate(avatar);
                if (body != null) { bt = timeline.CreateTrack<AnimationTrack>(null, "Body"); bt.CreateClip(body); director.SetGenericBinding(bt, avatar); }
                if (face != null) AddFaceTrack(timeline, director, null, bt, face, avatar, dir);
            }
            else Debug.LogWarning("[VR Motion Bridge] No avatar found in the scene for " + clip + "; select it and run again, or bind the Body/Face tracks manually.");
            if (b.cameras != null)
                foreach (var c in b.cameras)
                {
                    var camClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(dir + "/" + clip + "_" + Safe(c.name) + "_camera.anim");
                    if (camClip == null) continue;
                    AddCameraTrack(timeline, director, c, camClip);
                }
            EditorUtility.SetDirty(timeline);
            AssetDatabase.SaveAssets();
            Selection.activeGameObject = go;
            Debug.Log($"[VR Motion Bridge] Timeline created: {tlPath}. Enter Play mode to see PhysBones react while it plays (Unity Recorder can render it).");
            return go;
        }

        /// <summary>
        /// The face clip on top of the body: an override track of the body track, masked to no humanoid body part. A second
        /// track on the same Animator without that mask overrides the humanoid muscles with their defaults, which froze
        /// the body (only the root moved) when Body and Face were separate tracks.
        /// </summary>
        internal static AnimationTrack AddFaceTrack(TimelineAsset timeline, PlayableDirector director, TrackAsset parent, AnimationTrack body, AnimationClip face, Animator avatar, string maskDir)
        {
            AnimationTrack t;
            if (body != null) t = timeline.CreateTrack<AnimationTrack>(body, "Face"); // a child of an animation track = its override track
            else { t = timeline.CreateTrack<AnimationTrack>(parent, "Face"); if (avatar != null) director.SetGenericBinding(t, avatar); }
            t.avatarMask = NoBodyMask(maskDir);
            t.applyAvatarMask = true;
            var c = t.CreateClip(face); c.start = 0;
            return t;
        }

        /// <summary>
        /// Animate the avatar even when no camera sees it. The default (Cull Update Transforms) keeps the root moving but
        /// freezes the bones while the avatar is off screen, so PhysBones would see a frozen body and a jump when it
        /// comes back into view (and batch renders with no visible renderer get the rest pose).
        /// </summary>
        internal static void AlwaysAnimate(Animator a)
        {
            if (a == null || a.cullingMode == AnimatorCullingMode.AlwaysAnimate) return;
            Undo.RecordObject(a, "Animator always animate");
            a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }

        /// <summary>An AvatarMask with every humanoid body part off (generic curves such as blendshapes still play).</summary>
        internal static AvatarMask NoBodyMask(string dir)
        {
            string p = dir + "/VRMR_FaceOnly.mask";
            var m = AssetDatabase.LoadAssetAtPath<AvatarMask>(p);
            if (m != null) return m;
            m = new AvatarMask();
            for (int i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++) m.SetHumanoidBodyPartActive((AvatarMaskBodyPart)i, false);
            AssetDatabase.CreateAsset(m, p);
            return m;
        }

        /// <summary>A new camera object (near/far from the bundle) animated by camClip on its own Timeline track.</summary>
        internal static Camera AddCameraTrack(TimelineAsset timeline, PlayableDirector director, BundleCamera c, AnimationClip camClip)
        {
            if (camClip == null) return null;
            var camGo = new GameObject(c.name);
            Undo.RegisterCreatedObjectUndo(camGo, "Create VRMR Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = Mathf.Max(0.001f, c.near);
            cam.farClipPlane = Mathf.Max(cam.nearClipPlane + 1f, c.far);
            var anim = camGo.AddComponent<Animator>();
            var t = timeline.CreateTrack<AnimationTrack>(null, "Camera " + c.name);
            t.CreateClip(camClip);
            director.SetGenericBinding(t, anim);
            return cam;
        }
#endif
    }
}
#endif
