using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace VRMR
{
    // =====================================================================================================  scene export
    //
    // A scene (several actors, each evaluated into one ordinary BakedClip by SceneEvaluator.Bake) is exported as the
    // same files a take gives, once per actor, plus a scene manifest (<scene>.vrmrscene.json) that ties them together:
    //   <scene>_<actor>.fbx          armature + body animation + face carrier mesh (blendshapes)
    //   <scene>_<actor>.face.json    face shapes + raw parameters (the Blender add-on turns them into armature curves)
    //   <scene>_<actor>.anim         Humanoid Unity clip (root, muscles, IK goals, face), made without Unity
    //   <scene>_<actor>_body.anim    generic Unity clip on the avatar's transform paths (+ face)
    //   <scene>_<actor>_face.anim    face blendshapes only
    //   <scene>_<actor>_params.anim  raw avatar parameters (Animator parameter curves)
    //   <scene>_<actor>.vrmr.json    Unity sidecar: humanoid bone mapping (the bridge sets the FBX up as Humanoid)
    //   <scene>.vrmrscene.json       fps, range, markers, actors (avatar, files, the NLA layout), cameras
    // Clip frame 0 = scene time Start. File names in the manifest are relative to its folder.
    //
    // Cameras: the manifest's "cameras" list is where a future camera system writes (per scene frame, Unity space, the
    // same shape as the Blender add-on's "Send to Unity" cameras). Nothing produces cameras yet, so it stays empty.

    /// <summary>A camera to export with a scene (reserved for a future camera system; nothing produces these yet).</summary>
    public sealed class SceneCameraExport
    {
        public string Id = "", Name = "Camera";
        /// <summary>World pose per scene frame (Unity space, the actors' space; Unity camera convention: looks along +Z).</summary>
        public Pose[] Frames;
        /// <summary>Vertical field of view per scene frame, degrees.</summary>
        public float[] FovDeg;
        public float Near = 0.01f, Far = 1000f;
    }

    public sealed class SceneExportOptions
    {
        public bool Fbx = true, FbxFace = true, FbxTrackers = false;
        /// <summary>The FBX is the actor's avatar FBX (meshes, face shape keys) with the motion; else armature + face carrier.</summary>
        public bool FbxAvatarMesh = true;
        public bool BodyAnim = true, FaceAnim = true, ParamsAnim = true;
        /// <summary>One Humanoid clip per actor (&lt;scene&gt;_&lt;actor&gt;.anim), written without Unity.</summary>
        public bool HumanoidAnim = true;
        /// <summary>Face .json next to each FBX (the Blender add-on reads it); written whenever the FBX is.</summary>
        public bool FaceJson = true;
        /// <summary>The Unity sidecar (.vrmr.json, humanoid mapping) next to each FBX.</summary>
        public bool UnitySidecar = true;
        /// <summary>Face mesh path relative to the avatar root (Unity), its last part names the FBX face carrier.</summary>
        public string FaceMeshPath = "Body";
        public string FaceNaming = "UnifiedExpressions";
        /// <summary>Cameras to export (empty for now; see SceneCameraExport).</summary>
        public List<SceneCameraExport> Cameras = new List<SceneCameraExport>();
        /// <summary>Optional: a take's recording by path (gives each actor's VRChat avatar id / name).</summary>
        public Func<string, Recording> TakeLoader;
        public Action<string> Progress;
    }

    public sealed class SceneExportResult
    {
        public string Dir, Manifest;
        public readonly List<string> Files = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        /// <summary>Per exported actor: actor id, clip name, FBX path (null if no FBX).</summary>
        public readonly List<(string ActorId, string Clip, string Fbx)> Actors = new List<(string, string, string)>();
    }

    public static class SceneExport
    {
        public const string ManifestSuffix = ".vrmrscene.json";
        public const int FormatVersion = 1;

        public static string SafeName(string s)
        {
            s = s ?? "";
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            s = s.Trim();
            return s.Length == 0 ? "Scene" : s;
        }

        /// <summary>Where a scene goes in a Unity project: Assets/VRMotionRecorder/&lt;scene&gt;.</summary>
        public static string UnityDir(string project, Scene s) => Path.Combine(project, "Assets", "VRMotionRecorder", SafeName(s.Name));

        public static string ManifestPath(string dir, Scene s) => Path.Combine(dir, SafeName(s.Name) + ManifestSuffix);

        /// <summary>
        /// Evaluates the scene until every take is solved (the takes solve in the background on first use), at most
        /// timeoutMs. Returns the complete bake, or the last (pending) one when the time ran out or cancel() said so.
        /// </summary>
        public static SceneBake BakeComplete(SceneEvaluator ev, Scene s, int timeoutMs = 600000, Func<bool> cancel = null, Action<string> progress = null)
        {
            var start = DateTime.UtcNow;
            SceneBake b = null;
            while (true)
            {
                b = ev.Bake(s);
                if (!b.Pending && !ev.Takes.Working) return b;
                if ((DateTime.UtcNow - start).TotalMilliseconds > timeoutMs || (cancel != null && cancel())) return b;
                string busy = ev.Takes.Busy;
                progress?.Invoke("Solving takes" + (busy.Length > 0 ? $" ({ev.Takes.Progress * 100:0}%)" : "") + "…");
                Thread.Sleep(150);
            }
        }

        /// <summary>Face shape name -> the avatar's own blendshape name (the face mesh's shapes from the avatar profile).</summary>
        public static Dictionary<string, string> ShapeRename(BakedClip c, string faceMeshPath)
        {
            var mesh = c.Profile?.Meshes.FirstOrDefault(m => m.Path == faceMeshPath) ?? c.Profile?.Meshes.FirstOrDefault();
            if (mesh == null || mesh.BlendShapes.Count == 0) return null;
            return FaceMapping.MatchToMesh(c.Face.Keys, mesh.BlendShapes);
        }

        /// <summary>Unity sidecar for one clip: the avatar's humanoid mapping (the bridge builds a Humanoid with it).</summary>
        public static Dictionary<string, object> UnitySidecar(BakedClip clip, string clipName, string faceMesh, string avatarId, string avatarName)
        {
            var P = clip.Profile;
            var human = new List<object>();
            if (P != null) for (int b = 0; b < HumanBones.Count; b++) if (P.Has(b)) human.Add(new Dictionary<string, object> { ["boneName"] = P.Nodes[P.Human[b]].Name, ["humanName"] = HumanBones.UnityName(b) });
            // no "params" here on purpose: the bridge would rebuild <clip>_face.anim from them and lose keyed face shapes;
            // the app writes the face and parameter clips itself
            return new Dictionary<string, object>
            {
                ["source"] = "app", ["clip"] = clipName, ["sourceAvatarModel"] = P?.UnityModelPath ?? "", ["avatarId"] = avatarId ?? "", ["avatarName"] = avatarName ?? "",
                ["fps"] = (double)clip.Fps, ["faceMesh"] = faceMesh, ["avatarBase"] = P?.Name ?? "", ["human"] = human,
            };
        }

        /// <summary>Writes every actor's files and the manifest into dir. Returns what was written.</summary>
        public static SceneExportResult Export(SceneBake bake, string dir, SceneExportOptions opt)
        {
            if (bake == null || bake.Scene == null) throw new ArgumentNullException(nameof(bake));
            opt = opt ?? new SceneExportOptions();
            var s = bake.Scene;
            Directory.CreateDirectory(dir);
            var res = new SceneExportResult { Dir = dir };
            res.Warnings.AddRange(bake.Warnings);
            if (bake.Pending) res.Warnings.Add("Some takes were still solving: the export is incomplete.");
            string sceneName = SafeName(s.Name);
            string mesh = string.IsNullOrEmpty(opt.FaceMeshPath) ? "Body" : opt.FaceMeshPath;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var actorsJson = new List<object>();

            foreach (var (actor, clip) in bake.Actors)
            {
                if (clip == null) continue;
                bool hasStrips = actor.Tracks.Any(t => t.Strips.Any(x => !x.Mute));
                if (!hasStrips) { res.Warnings.Add($"Actor \"{actor.Name}\" has no strips: not exported."); continue; }
                string clipName = sceneName + "_" + SafeName(actor.Name);
                for (int k = 2; !used.Add(clipName); k++) clipName = sceneName + "_" + SafeName(actor.Name) + "_" + k;
                opt.Progress?.Invoke($"Exporting {actor.Name}…");

                string avatarId = "", avatarName = "";
                var firstTake = FirstTake(s, actor);
                if (firstTake != null && opt.TakeLoader != null)
                    try { var rec = opt.TakeLoader(firstTake.Path); if (rec != null) { avatarId = rec.AvatarId ?? ""; avatarName = rec.AvatarName ?? ""; } } catch { }

                var cr = ClipExport.Write(clip, dir, clipName, new ClipExportOptions
                {
                    Fbx = opt.Fbx, FbxFace = opt.FbxFace, FbxAvatarMesh = opt.FbxAvatarMesh, FbxCamera = false, FbxTrackers = opt.FbxTrackers,
                    BodyAnim = opt.BodyAnim, FaceAnim = opt.FaceAnim, ParamsAnim = opt.ParamsAnim, FaceJson = opt.FaceJson, UnitySidecar = opt.UnitySidecar,
                    HumanoidAnim = opt.HumanoidAnim,
                    FaceMeshPath = mesh, FaceNaming = opt.FaceNaming, AvatarId = avatarId, AvatarName = avatarName, Kind = "scene-actor",
                    Extra = new Dictionary<string, object> { ["scene"] = sceneName, ["actor"] = actor.Name },
                });
                res.Files.AddRange(cr.Paths);
                foreach (var w in cr.Warnings) res.Warnings.Add($"{actor.Name}: {w}");
                var files = cr.Files.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                res.Actors.Add((actor.Id, clipName, cr.FbxPath));

                var aj = new Dictionary<string, object>
                {
                    ["id"] = actor.Id, ["name"] = actor.Name, ["clip"] = clipName, ["hidden"] = actor.Hidden,
                    ["avatarBase"] = clip.Profile?.Name ?? "", ["sourceAvatarModel"] = clip.Profile?.UnityModelPath ?? "",
                    ["avatarLibraryId"] = string.IsNullOrEmpty(actor.Avatar) ? (clip.Profile?.LibraryId ?? "") : actor.Avatar,
                    ["avatarId"] = avatarId, ["avatarName"] = avatarName, ["faceMesh"] = mesh,
                    ["scale"] = (double)clip.Scale, ["frames"] = clip.Frames,
                    ["faceShapes"] = clip.Face.Count, ["params"] = clip.ParamValues.Count,
                    ["tracks"] = actor.Tracks.Select(t => (object)TrackJson(s, t)).ToList(),
                };
                foreach (var kv in files) aj[kv.Key] = kv.Value;
                actorsJson.Add(aj);
            }

            var man = Manifest(s.Name, bake.Fps, bake.Frames, bake.Start, actorsJson,
                s.Markers.OrderBy(m => m.T).Select(m => (object)new Dictionary<string, object>
                {
                    ["name"] = m.Name, ["t"] = m.T - bake.Start, ["sceneTime"] = m.T, ["frame"] = (int)Math.Round((m.T - bake.Start) * bake.Fps),
                }).ToList(), opt.Cameras, res.Warnings);
            string mp = ManifestPath(dir, s);
            File.WriteAllText(mp, Json.Serialize(man, true), new UTF8Encoding(false));
            res.Files.Add(mp);
            res.Manifest = mp;
            return res;
        }

        /// <summary>The manifest (kind "scene-export"): Blender's Import VRMR Scene and Unity's Create Timeline From Scene read it.
        /// Clip frame 0 = time <paramref name="start"/>.</summary>
        public static Dictionary<string, object> Manifest(string name, float fps, int frames, double start, List<object> actors, List<object> markers, IEnumerable<SceneCameraExport> cameras, IEnumerable<string> warnings) =>
            new Dictionary<string, object>
            {
                ["app"] = "VR Motion Recorder", ["kind"] = "scene-export", ["formatVersion"] = FormatVersion,
                ["name"] = name, ["fps"] = (double)fps, ["start"] = start, ["end"] = start + (frames - 1) / (double)fps,
                ["duration"] = (frames - 1) / (double)fps, ["frames"] = frames,
                // Blender / Unity frame numbers: clip frame 0 = start
                ["frameStart"] = 0, ["frameEnd"] = frames - 1,
                ["actors"] = actors ?? new List<object>(),
                ["markers"] = markers ?? new List<object>(),
                ["cameras"] = (cameras ?? new List<SceneCameraExport>()).Where(c => c?.Frames != null && c.Frames.Length > 0).Select(c => (object)CameraJson(c, frames)).ToList(),
                ["warnings"] = (warnings ?? new string[0]).Cast<object>().ToList(),
            };

        /// <summary>
        /// A take's manifest (one actor, VRChat's camera when there is one): the same file a scene export writes, so the
        /// take opens in Blender and Unity the way a scene does. Returns its path.
        /// </summary>
        public static string WriteTakeManifest(string dir, string clipName, BakedClip clip, ClipExportResult files, string actorName, string faceMesh, string avatarId, string avatarName, bool camera, IEnumerable<string> warnings)
        {
            var aj = new Dictionary<string, object>
            {
                ["id"] = "take", ["name"] = string.IsNullOrEmpty(actorName) ? (clip.Profile?.Name ?? "Avatar") : actorName, ["clip"] = clipName, ["hidden"] = false,
                ["avatarBase"] = clip.Profile?.Name ?? "", ["sourceAvatarModel"] = clip.Profile?.UnityModelPath ?? "",
                ["avatarLibraryId"] = clip.Profile?.LibraryId ?? "", ["avatarId"] = avatarId ?? "", ["avatarName"] = avatarName ?? "",
                ["faceMesh"] = faceMesh ?? "Body", ["scale"] = (double)clip.Scale, ["frames"] = clip.Frames,
                ["faceShapes"] = clip.Face.Count, ["params"] = clip.ParamValues.Count, ["tracks"] = new List<object>(),
            };
            foreach (var kv in files.Files) aj[kv.Key] = kv.Value;
            if (!aj.ContainsKey("fbx") && files.Files.TryGetValue("fbxArmature", out var arm)) aj["fbx"] = arm; // armature-only export
            var cams = new List<SceneCameraExport>();
            if (camera) { var c = ClipExport.VrchatCamera(clip); if (c != null) cams.Add(c); }
            var man = Manifest(clipName, clip.Fps, clip.Frames, 0, new List<object> { aj }, new List<object>(), cams, warnings);
            man["source"] = "take";
            string mp = Path.Combine(dir, clipName + ManifestSuffix);
            File.WriteAllText(mp, Json.Serialize(man, true), new UTF8Encoding(false));
            return mp;
        }

        static TakeRef FirstTake(Scene s, Actor a) =>
            a.Tracks.SelectMany(t => t.Strips).Where(x => x.Kind == StripKind.Take && !x.Mute).OrderBy(x => x.Start).Select(x => s.Take(x.Source)).FirstOrDefault(t => t != null);

        /// <summary>The NLA layout (for reference in Blender's NLA editor, and for tools): tracks bottom to top.</summary>
        static Dictionary<string, object> TrackJson(Scene s, NlaTrack t)
        {
            int Frame(double time) => (int)Math.Round((time - s.Start) * s.Fps);
            return new Dictionary<string, object>
            {
                ["id"] = t.Id, ["name"] = t.Name, ["kind"] = t.Kind == TrackKind.Input ? "input" : "pose", ["mute"] = t.Mute, ["solo"] = t.Solo,
                ["strips"] = t.Strips.OrderBy(x => x.Start).Select(x => (object)new Dictionary<string, object>
                {
                    ["id"] = x.Id, ["name"] = x.Name, ["kind"] = x.Kind == StripKind.Take ? "take" : "action",
                    ["source"] = x.Kind == StripKind.Take ? s.Take(x.Source)?.Name ?? "" : s.Action(x.Source)?.Name ?? "",
                    ["start"] = x.Start, ["end"] = x.End, ["srcIn"] = x.SrcIn, ["srcOut"] = x.SrcOut, ["speed"] = x.Speed,
                    ["blend"] = x.Blend == BlendMode.Add ? "add" : "replace", ["influence"] = (double)x.Influence, ["blendIn"] = x.BlendIn, ["blendOut"] = x.BlendOut,
                    ["mask"] = BodyMask.ToJson(x.Mask), ["maskName"] = BodyMask.Describe(x.Mask), ["mute"] = x.Mute,
                    ["frameStart"] = Frame(x.Start), ["frameEnd"] = Frame(x.End),
                }).ToList(),
                ["transitions"] = t.Transitions.Select(x => (object)new Dictionary<string, object>
                {
                    ["from"] = x.From, ["to"] = x.To, ["duration"] = x.Duration, ["curve"] = x.Curve.ToString().ToLowerInvariant(), ["align"] = x.Align.ToString().ToLowerInvariant(),
                }).ToList(),
            };
        }

        /// <summary>A camera in the Blender add-on's "Send to Unity" shape: flat pos (xyz), rot (xyzw), fov per frame, Unity space.</summary>
        static Dictionary<string, object> CameraJson(SceneCameraExport c, int frames)
        {
            var pos = new List<object>(); var rot = new List<object>(); var fov = new List<object>();
            for (int f = 0; f < frames; f++)
            {
                var p = c.Frames[Math.Min(f, c.Frames.Length - 1)];
                pos.Add(Math.Round(p.pos.x, 5)); pos.Add(Math.Round(p.pos.y, 5)); pos.Add(Math.Round(p.pos.z, 5));
                rot.Add(Math.Round(p.rot.x, 6)); rot.Add(Math.Round(p.rot.y, 6)); rot.Add(Math.Round(p.rot.z, 6)); rot.Add(Math.Round(p.rot.w, 6));
                float v = c.FovDeg != null && c.FovDeg.Length > 0 ? c.FovDeg[Math.Min(f, c.FovDeg.Length - 1)] : 60f;
                fov.Add(Math.Round(v, 4));
            }
            return new Dictionary<string, object> { ["id"] = c.Id, ["name"] = c.Name, ["near"] = (double)c.Near, ["far"] = (double)c.Far, ["pos"] = pos, ["rot"] = rot, ["fov"] = fov };
        }
    }
}
