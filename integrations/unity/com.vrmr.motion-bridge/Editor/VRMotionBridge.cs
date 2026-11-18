#if UNITY_EDITOR
// VR Motion Bridge — Unity side of VR Motion Recorder.
//  * FBX files exported by the app into Assets/VRMotionRecorder/** are auto-configured as Humanoid,
//    copying the rig definition of the avatar they were recorded with (exact bone names, so it maps 1:1); when that
//    model isn't in the project, the base model's rig is rebuilt from the sidecar (bone mapping + its Unity T-pose).
//  * Each such FBX also gets <clip>_humanoid.anim: its humanoid clip (muscles + face blendshapes) as a standalone
//    clip that plays on any Humanoid avatar (the base models), without the FBX.
//  * Tools > VR Motion Bridge > Make Humanoid Clip: bakes a generic clip (body transform curves) into a humanoid
//    clip on the selected avatar via HumanPoseHandler (retargetable to any humanoid), keeping face/param curves.
//  * Tools > VR Motion Bridge > Export Avatar Skeleton: writes the avatar's rest skeleton + blendshape names for the app.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRMotionBridge
{
    public class VRMotionBridgePostprocessor : AssetPostprocessor
    {
        [Serializable]
        public class HumanMap { public string boneName, humanName; }

        /// <summary>A bone of the avatar's Unity rest pose (the base model's T-pose), Unity-space local transform.</summary>
        [Serializable]
        public class RestBone { public string name, parent; public float[] p, r, s; }

        [Serializable]
        class Sidecar
        {
            public string source;
            public string sourceAvatarModel;
            public string avatarId;
            public string avatarName;
            public double fps;
            public string faceMesh;
            public string avatarBase;
            public HumanMap[] human; // the avatar's humanoid bone mapping: a Humanoid without the avatar's model in this project
            public RestBone[] rest;  // the base model's Unity rest pose (T-pose) for the humanoid chain
            public string kind;
        }

        static bool HasMapping(Sidecar sc) => sc != null && sc.human != null && sc.human.Any(h => h.humanName == "Hips");

        const string OwnSkeleton = "vrmr-own-skeleton";

        /// <summary>
        /// The base model's T-pose on top of the model's own rest pose, so the humanoid has the base model's muscle zero.
        /// App exports: their rest already is that pose (rotations set again, harmless). Blender sends: Blender's FBX
        /// exporter writes the pose of the current frame as the armature's rest, and its armature root has Blender's
        /// conventions; so the Hips get the base model's T-pose in world space (relative to the FBX's own armature root)
        /// and every bone below the Hips its T-pose local rotation and position.
        /// </summary>
        static SkeletonBone[] WithRestPose(SkeletonBone[] rest, Sidecar sc, string fbxPath)
        {
            if (sc?.rest == null || sc.rest.Length == 0) return rest;
            var byName = new Dictionary<string, RestBone>();
            foreach (var b in sc.rest) if (b != null && !string.IsNullOrEmpty(b.name) && b.r != null && b.r.Length == 4 && b.p != null && b.p.Length == 3) byName[b.name] = b;
            var outBones = (SkeletonBone[])rest.Clone();
            if (sc.source != "blender")
            {
                for (int i = 1; i < outBones.Length; i++)
                    if (byName.TryGetValue(outBones[i].name, out var b)) outBones[i].rotation = new Quaternion(b.r[0], b.r[1], b.r[2], b.r[3]);
                return outBones;
            }
            string hips = sc.human?.FirstOrDefault(h => h.humanName == "Hips")?.boneName;
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (hips == null || model == null || !byName.ContainsKey(hips)) return rest;
            // the base model's T-pose in world space (avatar root at the origin)
            var world = new Dictionary<string, Matrix4x4>();
            foreach (var b in sc.rest)
            {
                if (b == null || !byName.ContainsKey(b.name)) continue;
                var local = Matrix4x4.TRS(new Vector3(b.p[0], b.p[1], b.p[2]), new Quaternion(b.r[0], b.r[1], b.r[2], b.r[3]).normalized,
                    b.s != null && b.s.Length == 3 ? new Vector3(b.s[0], b.s[1], b.s[2]) : Vector3.one);
                world[b.name] = !string.IsNullOrEmpty(b.parent) && world.TryGetValue(b.parent, out var pw) ? pw * local : local;
            }
            var hipsT = model.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == hips);
            if (hipsT == null) return rest;
            var below = new HashSet<string>();
            foreach (var t in hipsT.GetComponentsInChildren<Transform>(true)) if (t != hipsT) below.Add(t.name);
            // Hips: T-pose world -> local under the FBX's own parent chain (its default pose; nothing above the Hips is animated)
            var parentWorld = hipsT.parent != null ? model.transform.worldToLocalMatrix * hipsT.parent.localToWorldMatrix : Matrix4x4.identity;
            var hl = parentWorld.inverse * world[hips];
            for (int i = 1; i < outBones.Length; i++)
            {
                string n = outBones[i].name;
                if (n == hips)
                {
                    outBones[i].position = hl.GetColumn(3);
                    outBones[i].rotation = hl.rotation;
                }
                else if (below.Contains(n) && byName.TryGetValue(n, out var b))
                {
                    outBones[i].position = new Vector3(b.p[0], b.p[1], b.p[2]);
                    outBones[i].rotation = new Quaternion(b.r[0], b.r[1], b.r[2], b.r[3]);
                }
            }
            return outBones;
        }

        static Sidecar ReadSidecar(string fbxPath)
        {
            string sidecar = Path.ChangeExtension(fbxPath, null) + ".vrmr.json";
            if (!File.Exists(sidecar)) return null;
            try { return JsonUtility.FromJson<Sidecar>(File.ReadAllText(sidecar)); } catch { return null; }
        }

        void OnPreprocessModel()
        {
            if (!assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) return;
            var sc = ReadSidecar(assetPath);
            if (sc == null) return;
            var importer = (ModelImporter)assetImporter;
            if (importer.userData == OwnSkeleton) return; // humanoid already built from this model's own rest pose
            if (importer.importSettingsMissing == false && importer.userData == "vrmr-configured") return;
            importer.preserveHierarchy = true; // keep "Armature" even when it is the only root node (Blender exports) -> paths match the avatar
            importer.importBlendShapes = true;
            importer.importAnimation = true;
            importer.resampleCurves = true;
            importer.animationCompression = ModelImporterAnimationCompression.KeyframeReduction;
            importer.animationRotationError = 0.05f;  // degrees (Unity's default 0.5 visibly smooths fingers and IK reaches)
            importer.animationPositionError = 0.05f;  // percent
            importer.animationScaleError = 0.05f;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            if (sc.source == "blender")
            {
                // Blender writes its own armature-root transform, so "Copy From Other" would misplace the root.
                // Import once as Generic, then build the humanoid from this model's own T-pose (OnPostprocessModel).
                importer.animationType = ModelImporterAnimationType.Generic;
                return;
            }
            Avatar src = null;
            if (!string.IsNullOrEmpty(sc.sourceAvatarModel))
                src = AssetDatabase.LoadAllAssetsAtPath(sc.sourceAvatarModel).OfType<Avatar>().FirstOrDefault();
            if (src != null && src.isHuman)
            {
                importer.animationType = ModelImporterAnimationType.Human;
                importer.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
                importer.sourceAvatar = src;
                importer.userData = "vrmr-configured";
            }
            else
            {
                importer.animationType = ModelImporterAnimationType.Generic;
                if (!string.IsNullOrEmpty(sc.sourceAvatarModel) && File.Exists(sc.sourceAvatarModel))
                {
                    // the source model may simply not be imported yet (fresh project / same import batch): retry afterwards
                    string self = assetPath, model = sc.sourceAvatarModel;
                    EditorApplication.delayCall += () =>
                    {
                        if (AssetDatabase.LoadAllAssetsAtPath(model).OfType<Avatar>().Any(a => a.isHuman))
                            AssetDatabase.ImportAsset(self, ImportAssetOptions.ForceUpdate);
                    };
                }
                else if (HasMapping(sc))
                {
                    // library / public avatars: the bone mapping came with the clip; OnPostprocessModel builds the humanoid
                    // from this FBX's own T-pose with it (any avatar scale: the humanoid clip is scale-free)
                    importer.userData = "vrmr-mapping";
                }
                else
                {
                    importer.userData = "vrmr-configured";
                    Debug.LogWarning($"[VR Motion Bridge] {assetPath}: source avatar '{sc.sourceAvatarModel}' not found; imported as Generic (the clip still plays on the avatar by path).");
                }
            }
        }

        void OnPostprocessModel(GameObject root)
        {
            var sc = ReadSidecar(assetPath);
            if (sc == null) return;
            var importer = (ModelImporter)assetImporter;
            if (importer.userData == OwnSkeleton) return;
            if (sc.source != "blender" && !(importer.userData == "vrmr-mapping" && HasMapping(sc))) return;
            string path = assetPath;
            var rest = root.GetComponentsInChildren<Transform>(true)
                .Select((t, i) => new SkeletonBone { name = i == 0 ? root.name + "(Clone)" : t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale })
                .ToArray();
            var mapping = sc.human;
            EditorApplication.delayCall += () =>
            {
                // the recorded avatar's model (muscle limits) when the project has it, else the mapping from the clip
                if (!ApplyOwnSkeletonHumanoid(path, rest, sc.sourceAvatarModel, quiet: HasMapping(sc)) && HasMapping(sc))
                    ApplyMappingHumanoid(path, rest, mapping);
            };
        }

        /// <summary>
        /// Humanoid avatar from the model's own rest pose and a bone mapping that came with the clip (default muscle
        /// limits): for avatars whose model is not in this Unity project (avatar library, public avatars).
        /// </summary>
        public static bool ApplyMappingHumanoid(string fbxPath, SkeletonBone[] rest, HumanMap[] mapping)
        {
            var imp = (ModelImporter)AssetImporter.GetAtPath(fbxPath);
            rest = WithRestPose(rest, ReadSidecar(fbxPath), fbxPath);
            var names = new HashSet<string>(rest.Select(r => r.name));
            var hd = new HumanDescription
            {
                human = mapping.Where(m => names.Contains(m.boneName)).Select(m => new HumanBone { boneName = m.boneName, humanName = m.humanName, limit = new HumanLimit { useDefaultValues = true } }).ToArray(),
                skeleton = rest,
                upperArmTwist = 0.5f, lowerArmTwist = 0.5f, upperLegTwist = 0.5f, lowerLegTwist = 0.5f,
                armStretch = 0.05f, legStretch = 0.05f, feetSpacing = 0f, hasTranslationDoF = false,
            };
            imp.animationType = ModelImporterAnimationType.Human;
            imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            imp.humanDescription = hd;
            imp.userData = OwnSkeleton;
            imp.SaveAndReimport();
            var av = AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<Avatar>().FirstOrDefault();
            if (av == null || !av.isHuman) { Debug.LogWarning($"[VR Motion Bridge] {fbxPath}: humanoid from the clip's bone mapping failed (left as imported)."); return false; }
            Debug.Log($"[VR Motion Bridge] {fbxPath}: Humanoid from the clip's own T-pose and its bone mapping ({hd.human.Length} bones)");
            return true;
        }

        /// <summary>
        /// Humanoid avatar from the model's own rest pose (T-pose), using the recorded avatar's bone mapping and muscle
        /// limits. Robust to however the DCC wrote the armature root; the humanoid clip then retargets to any avatar.
        /// </summary>
        public static bool ApplyOwnSkeletonHumanoid(string fbxPath, SkeletonBone[] rest, string sourceAvatarModel, bool quiet = false)
        {
            var src = string.IsNullOrEmpty(sourceAvatarModel) ? null : AssetDatabase.LoadAllAssetsAtPath(sourceAvatarModel).OfType<Avatar>().FirstOrDefault(a => a.isHuman);
            if (src == null) { if (!quiet) Debug.LogWarning($"[VR Motion Bridge] {fbxPath}: source avatar '{sourceAvatarModel}' not found; left as Generic."); return false; }
            var imp = (ModelImporter)AssetImporter.GetAtPath(fbxPath);
            rest = WithRestPose(rest, ReadSidecar(fbxPath), fbxPath);
            var hd = src.humanDescription;
            var names = new System.Collections.Generic.HashSet<string>(rest.Select(r => r.name));
            hd.human = hd.human.Where(h => names.Contains(h.boneName)).ToArray();
            hd.skeleton = rest;
            imp.animationType = ModelImporterAnimationType.Human;
            imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            imp.humanDescription = hd;
            imp.userData = OwnSkeleton;
            imp.SaveAndReimport();
            return true;
        }

        /// <summary>Synchronous version for scripts/tests: reads the imported model's rest pose and applies the humanoid.</summary>
        public static bool ConfigureBlenderHumanoid(string fbxPath)
        {
            var sc = ReadSidecar(fbxPath);
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (sc == null || go == null) return false;
            var rest = go.GetComponentsInChildren<Transform>(true)
                .Select((t, i) => new SkeletonBone { name = i == 0 ? go.name + "(Clone)" : t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale })
                .ToArray();
            if (ApplyOwnSkeletonHumanoid(fbxPath, rest, sc.sourceAvatarModel, quiet: HasMapping(sc))) return true;
            return HasMapping(sc) && ApplyMappingHumanoid(fbxPath, rest, sc.human);
        }

        void OnPreprocessAnimation()
        {
            string sidecar = Path.ChangeExtension(assetPath, null) + ".vrmr.json";
            if (!File.Exists(sidecar)) return;
            var importer = (ModelImporter)assetImporter;
            var clips = importer.defaultClipAnimations;
            foreach (var c in clips)
            {
                c.keepOriginalOrientation = true;
                c.keepOriginalPositionY = true;
                c.keepOriginalPositionXZ = true;
                c.lockRootRotation = false;
                c.lockRootHeightY = false;
                c.lockRootPositionXZ = false;
                c.loopTime = false;
            }
            importer.clipAnimations = clips;
        }
    }

    /// <summary>
    /// &lt;clip&gt;_humanoid.anim next to every app / Blender FBX that imports as Humanoid: the FBX's humanoid clip (muscles,
    /// root, the face blendshape curves of its meshes) as a standalone clip that plays on any Humanoid avatar.
    /// </summary>
    public class VRMotionBridgeHumanoidClip : AssetPostprocessor
    {
        static readonly HashSet<string> Pending = new HashSet<string>();

        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            foreach (var p in imported)
            {
                if (!p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(Path.ChangeExtension(p, null) + ".vrmr.json")) continue;
                if (!Pending.Add(p)) continue;
                string path = p;
                EditorApplication.delayCall += () => { Pending.Remove(path); Write(path, quiet: true); };
            }
        }

        /// <summary>How far a reduced curve may stray: muscles 0.001 (≈0.05°), body position 0.1 mm, body rotation
        /// 0.00005, blendshapes 0.2 of 100.</summary>
        static float Tolerance(EditorCurveBinding b)
        {
            string n = b.propertyName;
            if (n.StartsWith("blendShape.")) return 0.2f;
            if (b.type == typeof(Animator))
            {
                if (n.StartsWith("RootT.") || n.EndsWith("T.x") || n.EndsWith("T.y") || n.EndsWith("T.z")) return 0.0001f;
                if (n.StartsWith("RootQ.") || n.EndsWith("Q.x") || n.EndsWith("Q.y") || n.EndsWith("Q.z") || n.EndsWith("Q.w")) return 0.00005f;
                return 0.001f;
            }
            return 0.0001f;
        }

        /// <summary>
        /// The curve sampled at every frame, rebuilt with only the keys a cubic fit within tol needs: each key gets the
        /// sampled slope as both tangents, and a segment grows while Unity's Hermite curve between its two keys stays
        /// within tol of every frame (mocap clips otherwise carry a key per frame: ~100 MB of text per minute).
        /// </summary>
        internal static AnimationCurve Reduce(AnimationCurve c, float tol, float fps, float length)
        {
            if (c == null || c.length == 0) return c;
            if (fps <= 0) fps = 60;
            int n = Mathf.Max(2, Mathf.RoundToInt(length * fps) + 1);
            double dt = 1.0 / fps;
            var v = new double[n]; var tm = new float[n];
            for (int f = 0; f < n; f++) { tm[f] = Mathf.Min(length, (float)(f * dt)); v[f] = c.Evaluate(tm[f]); }
            var sl = new double[n];
            for (int f = 0; f < n; f++)
                sl[f] = f == 0 ? (v[1] - v[0]) / dt : f == n - 1 ? (v[n - 1] - v[n - 2]) / dt : (v[f + 1] - v[f - 1]) / (2 * dt);
            bool Fits(int a, int b)
            {
                double T = (b - a) * dt;
                for (int m = a + 1; m < b; m++)
                {
                    double h = (m - a) / (double)(b - a), h2 = h * h, h3 = h2 * h;
                    double y = (2 * h3 - 3 * h2 + 1) * v[a] + (h3 - 2 * h2 + h) * T * sl[a] + (-2 * h3 + 3 * h2) * v[b] + (h3 - h2) * T * sl[b];
                    if (Math.Abs(y - v[m]) > tol) return false;
                }
                return true;
            }
            var keep = new List<int> { 0 };
            int k0 = 0;
            while (k0 < n - 1)
            {
                int best = k0 + 1;
                for (int b = k0 + 2; b < n && b - k0 <= 600; b++) { if (Fits(k0, b)) best = b; else break; }
                keep.Add(best); k0 = best;
            }
            var outKeys = new Keyframe[keep.Count];
            for (int j = 0; j < keep.Count; j++) { int f = keep[j]; outKeys[j] = new Keyframe(tm[f], (float)v[f], (float)sl[f], (float)sl[f]); }
            return new AnimationCurve(outKeys);
        }

        public static string HumanoidClipPath(string fbxPath) => Path.ChangeExtension(fbxPath, null).Replace('\\', '/') + "_humanoid.anim";

        /// <summary>Writes (or updates, keeping its GUID) &lt;clip&gt;_humanoid.anim; null when the FBX isn't Humanoid (yet).</summary>
        public static AnimationClip Write(string fbxPath, bool quiet = false)
        {
            var imp = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (imp == null || imp.animationType != ModelImporterAnimationType.Human) return null;
            var src = AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview"));
            if (src == null || !src.humanMotion) return null;
            var clip = new AnimationClip { frameRate = src.frameRate };
            int muscles = 0, shapes = 0;
            foreach (var b in AnimationUtility.GetCurveBindings(src))
            {
                if (b.type == typeof(Transform)) continue; // the FBX's empties (camera / trackers) don't exist on an avatar
                AnimationUtility.SetEditorCurve(clip, b, Reduce(AnimationUtility.GetEditorCurve(src, b), Tolerance(b), src.frameRate, src.length));
                if (b.type == typeof(Animator)) muscles++; else if (b.propertyName.StartsWith("blendShape.")) shapes++;
            }
            if (shapes == 0)
            {
                // an FBX without face keys (face unticked, armature-only export): the app's face clip, when there is one
                string face = Path.ChangeExtension(fbxPath, null) + "_face.anim";
                var fc = AssetDatabase.LoadAssetAtPath<AnimationClip>(face);
                if (fc != null)
                    foreach (var b in AnimationUtility.GetCurveBindings(fc))
                    {
                        AnimationUtility.SetEditorCurve(clip, b, Reduce(AnimationUtility.GetEditorCurve(fc, b), Tolerance(b), fc.frameRate, fc.length));
                        shapes++;
                    }
            }
            // the motion stays on the body, relative to the avatar's root, on any Animator (no root motion needed)
            var st = AnimationUtility.GetAnimationClipSettings(clip);
            st.loopTime = false;
            st.keepOriginalOrientation = st.keepOriginalPositionXZ = st.keepOriginalPositionY = true;
            st.loopBlendOrientation = st.loopBlendPositionXZ = st.loopBlendPositionY = true;
            AnimationUtility.SetAnimationClipSettings(clip, st);
            string dst = HumanoidClipPath(fbxPath);
            VRMotionBridgeBundle.Save(clip, dst);
            AssetDatabase.SaveAssets();
            var saved = AssetDatabase.LoadAssetAtPath<AnimationClip>(dst);
            Debug.Log($"[VR Motion Bridge] {dst}: humanoid clip ({muscles} muscle/root curves, {shapes} face blendshape curves, {src.length:0.##} s) - plays on any Humanoid avatar");
            return saved;
        }
    }

    public static class VRMotionBridgeMenu
    {
        const string Menu = "Tools/VR Motion Bridge/";

        // ------------------------------------------------------------------ humanoid bake

        [MenuItem(Menu + "Make Humanoid Clip (select avatar + clip)")]
        static void MakeHumanoid()
        {
            var clip = Selection.objects.OfType<AnimationClip>().FirstOrDefault();
            var go = Selection.gameObjects.FirstOrDefault(g => g.GetComponent<Animator>() != null && g.GetComponent<Animator>().isHuman);
            if (clip == null || go == null)
            {
                EditorUtility.DisplayDialog("VR Motion Bridge", "Select a generic AnimationClip (the *_body.anim from VR Motion Recorder) AND your avatar in the scene (with a Humanoid Animator), then run this again.", "OK");
                return;
            }
            string src = AssetDatabase.GetAssetPath(clip);
            string dst = Path.Combine(Path.GetDirectoryName(src), Path.GetFileNameWithoutExtension(src).Replace("_body", "") + "_humanoid.anim").Replace('\\', '/');
            var result = Bake(clip, go.GetComponent<Animator>());
            if (result == null) return;
            AssetDatabase.CreateAsset(result, AssetDatabase.GenerateUniqueAssetPath(dst));
            AssetDatabase.SaveAssets();
            EditorGUIUtility.PingObject(result);
            Debug.Log("[VR Motion Bridge] Humanoid clip written: " + AssetDatabase.GetAssetPath(result));
        }

        static AnimationClip Bake(AnimationClip generic, Animator animator)
        {
            var root = animator.transform;
            // snapshot pose to restore afterwards
            var all = root.GetComponentsInChildren<Transform>(true);
            var saved = all.Select(t => (t, t.localPosition, t.localRotation, t.localScale)).ToArray();
            var rootPos = root.position; var rootRot = root.rotation;
            root.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var handler = new HumanPoseHandler(animator.avatar, root);
            var pose = new HumanPose();
            float fps = generic.frameRate > 0 ? generic.frameRate : 60f;
            int frames = Mathf.Max(2, Mathf.RoundToInt(generic.length * fps) + 1);
            var rootT = new[] { new AnimationCurve(), new AnimationCurve(), new AnimationCurve() };
            var rootQ = new[] { new AnimationCurve(), new AnimationCurve(), new AnimationCurve(), new AnimationCurve() };
            var muscles = new AnimationCurve[HumanTrait.MuscleCount];
            for (int i = 0; i < muscles.Length; i++) muscles[i] = new AnimationCurve();
            Quaternion prev = Quaternion.identity;
            bool animMode = AnimationMode.InAnimationMode();
            if (!animMode) AnimationMode.StartAnimationMode();
            try
            {
                for (int f = 0; f < frames; f++)
                {
                    float t = Mathf.Min(generic.length, f / fps);
                    AnimationMode.BeginSampling();
                    AnimationMode.SampleAnimationClip(root.gameObject, generic, t);
                    AnimationMode.EndSampling();
                    handler.GetHumanPose(ref pose);
                    var q = pose.bodyRotation;
                    if (f > 0 && Quaternion.Dot(prev, q) < 0) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                    prev = q;
                    rootT[0].AddKey(t, pose.bodyPosition.x); rootT[1].AddKey(t, pose.bodyPosition.y); rootT[2].AddKey(t, pose.bodyPosition.z);
                    rootQ[0].AddKey(t, q.x); rootQ[1].AddKey(t, q.y); rootQ[2].AddKey(t, q.z); rootQ[3].AddKey(t, q.w);
                    for (int i = 0; i < muscles.Length; i++) muscles[i].AddKey(t, pose.muscles[i]);
                    if (f % 60 == 0 && EditorUtility.DisplayCancelableProgressBar("VR Motion Bridge", "Baking humanoid clip…", f / (float)frames)) break;
                }
            }
            finally
            {
                if (!animMode) AnimationMode.StopAnimationMode();
                EditorUtility.ClearProgressBar();
                handler.Dispose();
                foreach (var s in saved) { s.t.localPosition = s.localPosition; s.t.localRotation = s.localRotation; s.t.localScale = s.localScale; }
                root.SetPositionAndRotation(rootPos, rootRot);
            }
            var outClip = new AnimationClip { frameRate = fps, name = generic.name.Replace("_body", "") + "_humanoid" };
            var at = typeof(Animator);
            outClip.SetCurve("", at, "RootT.x", rootT[0]); outClip.SetCurve("", at, "RootT.y", rootT[1]); outClip.SetCurve("", at, "RootT.z", rootT[2]);
            outClip.SetCurve("", at, "RootQ.x", rootQ[0]); outClip.SetCurve("", at, "RootQ.y", rootQ[1]); outClip.SetCurve("", at, "RootQ.z", rootQ[2]); outClip.SetCurve("", at, "RootQ.w", rootQ[3]);
            for (int i = 0; i < muscles.Length; i++) outClip.SetCurve("", at, MuscleAttribute(HumanTrait.MuscleName[i]), muscles[i]);
            // keep every non-transform curve (face blendshapes, parameters)
            foreach (var b in AnimationUtility.GetCurveBindings(generic))
            {
                if (b.type == typeof(Transform)) continue;
                AnimationUtility.SetEditorCurve(outClip, b, AnimationUtility.GetEditorCurve(generic, b));
            }
            var st = AnimationUtility.GetAnimationClipSettings(outClip);
            st.keepOriginalOrientation = st.keepOriginalPositionXZ = st.keepOriginalPositionY = true;
            st.loopBlendOrientation = st.loopBlendPositionXZ = st.loopBlendPositionY = true;
            AnimationUtility.SetAnimationClipSettings(outClip, st);
            return outClip;
        }

        static string MuscleAttribute(string muscle)
        {
            if (!muscle.StartsWith("Left ") && !muscle.StartsWith("Right ")) return muscle;
            foreach (var finger in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
                if (muscle.Contains(finger))
                    return muscle.Replace("Left ", "LeftHand.").Replace("Right ", "RightHand.").Replace(finger + " ", finger + ".");
            return muscle;
        }

        // ------------------------------------------------------------------ skeleton export for the app

        [MenuItem(Menu + "Export Avatar Skeleton for VR Motion Recorder")]
        static void ExportSkeleton()
        {
            var go = Selection.activeGameObject;
            var animator = go ? go.GetComponentInParent<Animator>() : null;
            if (animator == null || animator.avatar == null || !animator.isHuman)
            {
                EditorUtility.DisplayDialog("VR Motion Bridge", "Select your avatar (root with a Humanoid Animator) in the scene.", "OK");
                return;
            }
            var root = animator.transform;
            var hd = animator.avatar.humanDescription;
            var rest = new Dictionary<string, SkeletonBone>();
            foreach (var sb in hd.skeleton) if (!rest.ContainsKey(sb.name)) rest[sb.name] = sb;
            var nodes = new List<Transform>();
            void Walk(Transform t) { nodes.Add(t); foreach (Transform c in t) Walk(c); }
            Walk(root);
            var index = new Dictionary<Transform, int>();
            for (int i = 0; i < nodes.Count; i++) index[nodes[i]] = i;
            var sbJ = new System.Text.StringBuilder();
            string F(float v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            string Esc(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            string blueprint = "";
            foreach (var mb in root.GetComponents<MonoBehaviour>())
            {
                if (mb == null) continue;
                var fi = mb.GetType().GetField("blueprintId");
                if (fi != null) { blueprint = fi.GetValue(mb) as string ?? ""; break; }
            }
            sbJ.Append("{\"name\":").Append(Esc(root.name)).Append(",\"source\":").Append(Esc("unity:" + AssetDatabase.GetAssetPath(animator.avatar)))
               .Append(",\"unityModel\":").Append(Esc(AssetDatabase.GetAssetPath(animator.avatar)));
            sbJ.Append(",\"scale\":1,\"avatarIds\":[").Append(string.IsNullOrEmpty(blueprint) ? "" : Esc(blueprint)).Append("],\"nodes\":[");
            for (int i = 0; i < nodes.Count; i++)
            {
                var t = nodes[i];
                Vector3 p = t.localPosition; Quaternion q = t.localRotation; Vector3 s = t.localScale;
                if (i > 0 && rest.TryGetValue(t.name, out var sb)) { p = sb.position; q = sb.rotation; s = sb.scale; }
                if (i == 0) { p = Vector3.zero; q = Quaternion.identity; }
                int parent = i == 0 ? -1 : index[t.parent];
                if (i > 0) sbJ.Append(',');
                sbJ.Append("{\"n\":").Append(Esc(t.name)).Append(",\"p\":").Append(parent)
                   .Append(",\"t\":[").Append(F(p.x)).Append(',').Append(F(p.y)).Append(',').Append(F(p.z)).Append(']')
                   .Append(",\"r\":[").Append(F(q.x)).Append(',').Append(F(q.y)).Append(',').Append(F(q.z)).Append(',').Append(F(q.w)).Append(']')
                   .Append(",\"s\":[").Append(F(s.x)).Append(',').Append(F(s.y)).Append(',').Append(F(s.z)).Append("]}");
            }
            sbJ.Append("],\"human\":{");
            bool first = true;
            for (int b = 0; b < (int)HumanBodyBones.LastBone; b++)
            {
                var t = animator.GetBoneTransform((HumanBodyBones)b);
                if (t == null || !index.ContainsKey(t)) continue;
                if (!first) sbJ.Append(',');
                first = false;
                sbJ.Append(Esc(((HumanBodyBones)b).ToString())).Append(':').Append(index[t]);
            }
            sbJ.Append("},\"meshes\":[");
            first = true;
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null || smr.sharedMesh.blendShapeCount == 0) continue;
                if (!first) sbJ.Append(',');
                first = false;
                sbJ.Append("{\"path\":").Append(Esc(AnimationUtility.CalculateTransformPath(smr.transform, root))).Append(",\"shapes\":[");
                for (int k = 0; k < smr.sharedMesh.blendShapeCount; k++) { if (k > 0) sbJ.Append(','); sbJ.Append(Esc(smr.sharedMesh.GetBlendShapeName(k))); }
                sbJ.Append("]}");
            }
            sbJ.Append("]}");
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRMotionRecorder", "profiles");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, MakeSafe(root.name) + ".vrmskel.json");
            File.WriteAllText(file, sbJ.ToString());
            EditorUtility.DisplayDialog("VR Motion Bridge", $"Skeleton exported:\n{file}\n\nVR Motion Recorder picks it up automatically" + (string.IsNullOrEmpty(blueprint) ? " (link it to your avatar in the app's Settings)." : $" for {blueprint}."), "OK");
        }

        static string MakeSafe(string s) { foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return s; }
    }
}
#endif
