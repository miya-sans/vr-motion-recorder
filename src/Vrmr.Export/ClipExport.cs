using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VRMR
{
    // =====================================================================================================  clip files
    //
    // One solved clip (a take, or one actor of a scene) as the file set both exports write:
    //   <clip>.fbx           the avatar's own FBX (meshes, skin, materials, face-tracking shape keys) with the clip as its
    //                        take: humanoid bones, face shape keys, camera / trackers as empties. Armature + a face carrier
    //                        mesh only when no FBX of the avatar is found.
    //   <clip>_armature.fbx  optional: the armature and its animation only (no meshes, no face), with its own sidecar
    //   <clip>.vrmr.json     Unity sidecar: humanoid mapping + the avatar's Unity rest pose (the bridge sets the FBX up as
    //                        the base model's Humanoid and writes <clip>_humanoid.anim from it)
    //   <clip>.anim          one Humanoid clip made here, without Unity: root, muscles, IK goals and the face
    //                        blendshapes (plays on any Humanoid avatar; see docs/dev/unity-humanoid.md)
    //   <clip>_body.anim     generic clip on the avatar's transform paths (exact on a Generic Animator)
    //   <clip>_face.anim     face blendshapes;  <clip>_params.anim  raw avatar parameters
    //   <clip>.face.json     face shapes, parameters, avatar link (the Blender add-on reads it)
    // The take export adds a manifest (<clip>.vrmrscene.json, the scene export's format with one actor), so Blender's
    // "Import VRMR Scene" and Unity's "Create Timeline From Scene" open takes and scenes the same way.

    public sealed class ClipExportOptions
    {
        public string AvatarId = "", AvatarName = "";
        public bool BodyAnim = true, FaceAnim = true, ParamsAnim = true;

        /// <summary>Extra keys for the sidecar and the face .json (scene, actor…).</summary>
        public Dictionary<string, object> Extra = new();

        /// <summary>Face .json next to the FBX (the Blender add-on reads it); written whenever the FBX is.</summary>
        public bool FaceJson = true;

        /// <summary>Face mesh path relative to the avatar root (Unity); its last part names the face mesh in the FBX.</summary>
        public string FaceMeshPath = "Body";

        public string FaceNaming = "UnifiedExpressions";
        public bool Fbx = true, FbxFace = true, FbxAvatarMesh = true, FbxCamera = false, FbxTrackers = false;

        /// <summary>
        ///     Also &lt;clip&gt;_armature.fbx: the avatar's armature with the animation, no meshes and no face (camera /
        ///     trackers as for the FBX).
        /// </summary>
        public bool FbxArmature = false;

        /// <summary>&lt;clip&gt;.anim: one Humanoid clip (root, muscles, IK goals, face blendshapes), written without Unity.</summary>
        public bool HumanoidAnim = false; // experimental: not yet verified in Unity, so off unless asked for

        /// <summary>Also the raw avatar parameters in the Humanoid clip.</summary>
        public bool HumanoidAnimParams = false;

        /// <summary>"take" or "scene-actor" (sidecar kind).</summary>
        public string Kind = "take";

        public Action<string> Progress;

        /// <summary>
        ///     Where the model FBX's textures are copied (shared by every clip of an avatar exported there). Null:
        ///     &lt;dir&gt;\textures\&lt;avatar&gt;.
        /// </summary>
        public string TextureDir;

        /// <summary>The Unity sidecar (.vrmr.json) next to the FBX.</summary>
        public bool UnitySidecar = true;
    }

    public sealed class ClipExportResult
    {
        /// <summary>
        ///     Role ("fbx", "sidecar", "fbxArmature", "sidecarArmature", "humanoidAnim", "bodyAnim", "faceAnim", "paramsAnim",
        ///     "faceJson")
        ///     -> file name in Dir.
        /// </summary>
        public readonly Dictionary<string, string> Files = new();

        public readonly List<string> Paths = new();
        public readonly List<string> Warnings = new();
        public string Clip, Dir;
        public FbxExportResult Fbx, FbxArmature;
        public string FbxPath => Files.TryGetValue("fbx", out string f) ? Path.Combine(Dir, f) : null;
    }

    public static class ClipExport
    {
        /// <summary>VRChat sends its camera's pose but not its zoom: the exported camera gets this vertical field of view.</summary>
        public const float VrchatCameraFov = 45f;

        /// <summary>Writes the clip's files into dir (names start with clipName).</summary>
        public static ClipExportResult Write(BakedClip clip, string dir, string clipName, ClipExportOptions o)
        {
            o = o ?? new ClipExportOptions();
            Directory.CreateDirectory(dir);
            var r = new ClipExportResult { Clip = clipName, Dir = dir };
            string mesh = string.IsNullOrEmpty(o.FaceMeshPath) ? "Body" : o.FaceMeshPath;
            var rename = SceneExport.ShapeRename(clip, mesh);

            string F(string suffix)
            {
                return clipName + suffix;
            }

            void Add(string role, string file)
            {
                r.Files[role] = file;
                r.Paths.Add(Path.Combine(dir, file));
            }

            void Sidecar(string role, string file)
            {
                var side = SceneExport.UnitySidecar(clip, clipName, mesh, o.AvatarId, o.AvatarName);
                side["kind"] = o.Kind;
                side["rest"] = RestSkeleton(clip.Profile);
                foreach (var kv in o.Extra) side[kv.Key] = kv.Value;
                File.WriteAllText(Path.Combine(dir, file), Json.Serialize(side, true), new UTF8Encoding(false));
                Add(role, file);
            }

            if (o.Fbx)
            {
                o.Progress?.Invoke("Writing the FBX…");
                string p = Path.Combine(dir, F(".fbx"));
                r.Fbx = FbxExporter.Export(clip, p, new FbxExportOptions
                {
                    TakeName = clipName, IncludeFace = o.FbxFace, FaceMeshName = mesh.Split('/').Last(),
                    ShapeRename = rename,
                    IncludeCamera = o.FbxCamera, IncludeTrackers = o.FbxTrackers, AvatarMesh = o.FbxAvatarMesh,
                    TextureDir = o.TextureDir ?? Path.Combine(dir, "textures",
                        SceneExport.SafeName(clip.Profile?.Name ?? "avatar"))
                });
                Add("fbx", F(".fbx"));
                if (o.FbxAvatarMesh && r.Fbx.Mode != "avatar")
                    r.Warnings.AddRange(r.Fbx.Notes.Where(n => n.Contains("armature + face carrier")));
                AppLog.Info($"FBX {F(".fbx")}: {r.Fbx}" +
                            (r.Fbx.Notes.Count > 0 ? "; " + string.Join("; ", r.Fbx.Notes) : ""));
                if (o.UnitySidecar) Sidecar("sidecar", F(".vrmr.json"));
            }

            if (o.FbxArmature)
            {
                o.Progress?.Invoke("Writing the armature FBX…");
                string fa = F("_armature.fbx");
                r.FbxArmature = FbxExporter.Export(clip, Path.Combine(dir, fa), new FbxExportOptions
                {
                    TakeName = clipName, IncludeFace = false, FaceMeshName = mesh.Split('/').Last(),
                    ShapeRename = rename,
                    IncludeCamera = o.FbxCamera, IncludeTrackers = o.FbxTrackers, AvatarMesh = false
                });
                Add("fbxArmature", fa);
                AppLog.Info($"FBX {fa}: {r.FbxArmature}");
                if (o.UnitySidecar)
                    Sidecar("sidecarArmature", F("_armature.vrmr.json")); // the bridge sets it up as Humanoid too
            }

            if (o.HumanoidAnim && (clip.BodySource != "none" || clip.Face.Count > 0))
            {
                o.Progress?.Invoke("Writing the Humanoid clip…");
                var hr = HumanoidAnimExporter.Export(clip, Path.Combine(dir, F(".anim")),
                    new HumanoidAnimOptions
                    {
                        ClipName = clipName, Params = o.HumanoidAnimParams, FaceMeshPath = mesh, ShapeRename = rename
                    });
                Add("humanoidAnim", F(".anim"));
                AppLog.Info($"Humanoid clip {F(".anim")}: {hr}");
            }

            if (o.BodyAnim && clip.BodySource != "none")
            {
                o.Progress?.Invoke("Writing the Unity clips…");
                UnityAnimExporter.Export(clip, Path.Combine(dir, F("_body.anim")),
                    new AnimExportOptions
                    {
                        ClipName = F("_body"), Body = true, Face = o.FaceAnim, FaceMeshPath = mesh, ShapeRename = rename
                    });
                Add("bodyAnim", F("_body.anim"));
            }

            if (o.FaceAnim && clip.Face.Count > 0)
            {
                UnityAnimExporter.Export(clip, Path.Combine(dir, F("_face.anim")),
                    new AnimExportOptions
                    {
                        ClipName = F("_face"), Body = false, Face = true, FaceMeshPath = mesh, ShapeRename = rename
                    });
                Add("faceAnim", F("_face.anim"));
            }

            if (o.ParamsAnim && clip.ParamValues.Count > 0)
            {
                UnityAnimExporter.Export(clip, Path.Combine(dir, F("_params.anim")),
                    new AnimExportOptions { ClipName = F("_params"), Body = false, Face = false, Params = true });
                Add("paramsAnim", F("_params.anim"));
            }

            if (o.FaceJson || o.Fbx || o.FbxArmature)
            {
                var meta = new Dictionary<string, object>
                {
                    ["unityModel"] = clip.Profile?.UnityModelPath ?? "", ["avatarBase"] = clip.Profile?.Name ?? "",
                    ["faceMesh"] = mesh,
                    ["avatarId"] = o.AvatarId ?? "", ["avatarName"] = o.AvatarName ?? "",
                    ["faceNaming"] = o.FaceNaming ?? "", ["clip"] = clipName,
                    // the Blender add-on hands these to "Send to Unity": the humanoid is then the base model's (T-pose, mapping)
                    ["human"] = HumanMap(clip.Profile), ["rest"] = RestSkeleton(clip.Profile)
                };
                foreach (var kv in o.Extra) meta[kv.Key] = kv.Value;
                MiscExporters.ExportFaceJson(clip, Path.Combine(dir, F(".face.json")), rename, meta);
                Add("faceJson", F(".face.json"));
            }

            return r;
        }

        /// <summary>The avatar's humanoid bone mapping (bone name -> Unity human name).</summary>
        public static List<object> HumanMap(AvatarProfile P)
        {
            var human = new List<object>();
            if (P != null)
                for (int b = 0; b < HumanBones.Count; b++)
                    if (P.Has(b))
                        human.Add(new Dictionary<string, object>
                            { ["boneName"] = P.Nodes[P.Human[b]].Name, ["humanName"] = HumanBones.UnityName(b) });
            return human;
        }

        /// <summary>
        ///     The avatar's rest pose as Unity imported its model (the base model's T-pose: the .meta skeleton the humanoid was
        ///     configured with), for the humanoid bones and their ancestors: Unity-space local position / rotation / scale.
        ///     The bridge builds the Humanoid from it, so the humanoid clip has the base model's muscle space.
        /// </summary>
        public static List<object> RestSkeleton(AvatarProfile P)
        {
            var list = new List<object>();
            if (P == null) return list;
            int root = P.RootIndex;
            var chain = new SortedSet<int>();
            for (int b = 0; b < HumanBones.Count; b++)
            for (int a = P.Human[b]; a >= 0 && a != root; a = P.Nodes[a].Parent)
                chain.Add(a);
            foreach (int i in P.TopoOrder().Where(chain.Contains))
            {
                var n = P.Nodes[i];
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = n.Name, ["parent"] = n.Parent >= 0 && n.Parent != root ? P.Nodes[n.Parent].Name : "",
                    ["p"] = new List<object> { (double)n.LocalPos.x, (double)n.LocalPos.y, (double)n.LocalPos.z },
                    ["r"] = new List<object>
                        { (double)n.LocalRot.x, (double)n.LocalRot.y, (double)n.LocalRot.z, (double)n.LocalRot.w },
                    ["s"] = new List<object> { (double)n.LocalScale.x, (double)n.LocalScale.y, (double)n.LocalScale.z }
                });
            }

            return list;
        }

        /// <summary>VRChat's camera path as a manifest camera (pose per clip frame; FOV not recorded), or null.</summary>
        public static SceneCameraExport VrchatCamera(BakedClip clip)
        {
            if (clip?.Camera == null || clip.CameraValid == null || !clip.CameraValid.Any(v => v)) return null;
            float
                inv = 1f / Math.Max(1e-6f,
                    clip.Scale); // same space as the FBX (its armature is at the avatar's base size)
            var frames = new Pose[clip.Frames];
            for (int f = 0; f < clip.Frames; f++) frames[f] = new Pose(clip.Camera[f].pos * inv, clip.Camera[f].rot);
            return new SceneCameraExport
                { Id = "vrchat", Name = "VRChat Camera", Frames = frames, FovDeg = new[] { VrchatCameraFov } };
        }
    }
}
