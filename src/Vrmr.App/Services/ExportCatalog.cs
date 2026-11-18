using System;
using System.Collections.Generic;
using System.IO;

namespace VRMR.App.Services
{
    /// <summary>Where a format is listed on the Export tab.</summary>
    public enum ExportGroup
    {
        /// <summary>Shown right away.</summary>
        Main,
        /// <summary>Behind "More formats", closed until wanted.</summary>
        More,
    }

    /// <summary>The two places export choices are remembered in: the app settings and the window's own state.</summary>
    public sealed record ExportStores(SettingsStore Settings, UiStateStore UiState);

    /// <summary>
    /// Where the on/off state of one export choice is kept: a field of the app settings, or the window's own state for
    /// a choice that is not a setting.
    /// </summary>
    public sealed class ExportState
    {
        readonly Func<ExportStores, bool> _read;
        readonly Action<ExportStores, bool> _write;

        ExportState(Func<ExportStores, bool> read, Action<ExportStores, bool> write) { _read = read; _write = write; }

        /// <summary>Kept in the app settings; <paramref name="field"/> is the name listeners of the settings are told.</summary>
        public static ExportState InSettings(string field, Func<Settings, bool> get, Action<Settings, bool> set) =>
            new ExportState(stores => get(stores.Settings.Value), (stores, on) => stores.Settings.Update(field, s => set(s, on)));

        /// <summary>Kept in the window's own state under <paramref name="key"/>.</summary>
        public static ExportState InUiState(string key, bool fallback = false) =>
            new ExportState(stores => stores.UiState.GetBool(key, fallback), (stores, on) => stores.UiState.Set(key, on));

        public bool Read(ExportStores stores) => _read(stores);
        public void Write(ExportStores stores, bool on) => _write(stores, on);
    }

    /// <summary>Something the Export tab switches on or off: a format, or an option of one.</summary>
    public abstract class ExportChoice
    {
        public string Id { get; init; } = "";
        public string Title { get; init; } = "";
        /// <summary>The tooltip: what the file is and what it is for, or what the option does to it.</summary>
        public string Description { get; init; } = "";
        /// <summary>Where its on/off state is kept.</summary>
        public ExportState State { get; init; }
        /// <summary>Adds what it stands for to an export that is about to run.</summary>
        public Action<ExportJob> Apply { get; init; }
    }

    /// <summary>A detail of one or more formats (the face shapes of the FBX, the camera in it).</summary>
    public sealed class ExportOption : ExportChoice
    {
        /// <summary>
        /// The ids of the formats this option belongs to: it applies while any of them is on, and is listed at their
        /// level. Empty for an option of the one format it is listed under, which is shown indented below it.
        /// </summary>
        public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();

        /// <summary>The format it is listed under.</summary>
        public ExportFormat Format { get; internal set; }

        /// <summary>True for an option of a single format (the one it is listed under).</summary>
        public bool IsNested => DependsOn.Count == 0;
    }

    /// <summary>One kind of file an export can write.</summary>
    public sealed class ExportFormat : ExportChoice
    {
        readonly IReadOnlyList<ExportOption> _options = Array.Empty<ExportOption>();

        public ExportGroup Group { get; init; }

        /// <summary>The options listed after this format.</summary>
        public IReadOnlyList<ExportOption> Options
        {
            get => _options;
            init
            {
                _options = value ?? Array.Empty<ExportOption>();
                foreach (var option in _options) option.Format = this;
            }
        }
    }

    /// <summary>
    /// What can be exported, as data: the Export tab is built from these lists, and an export asks each chosen entry
    /// to add itself to the job. A new format is one more entry here.
    /// </summary>
    public static class ExportCatalog
    {
        const string Fbx = "fbx", FbxArmature = "fbxArmature";

        /// <summary>The formats of a take, in the order they are shown.</summary>
        public static IReadOnlyList<ExportFormat> Takes { get; } = new[]
        {
            new ExportFormat
            {
                Id = Fbx, Title = "FBX with the avatar model", Group = ExportGroup.Main,
                Description = "<take>.fbx: your avatar's own model (meshes, materials, face shapes) with the take as its animation, for Blender and Unity. "
                            + "Its textures are copied once per avatar into the export folder's textures folder; textures already in the same Unity project are used where they are. "
                            + "When no FBX of the avatar is found in the avatar library or the linked Unity project, only the armature is written.",
                State = ExportState.InSettings(nameof(Settings.ExportFbx), s => s.ExportFbx, (s, on) => s.ExportFbx = on),
                Apply = job => job.Clip.Fbx = true,
                Options = new[]
                {
                    new ExportOption
                    {
                        Id = "fbx.face", Title = "Face shapes",
                        Description = "Keys the face mesh's own shape keys with the recorded face tracking. Off: the model keeps a still face.",
                        State = ExportState.InSettings(nameof(Settings.FbxIncludeFaceMesh), s => s.FbxIncludeFaceMesh, (s, on) => s.FbxIncludeFaceMesh = on),
                        Apply = job => job.Clip.FbxFace = true,
                    },
                },
            },
            new ExportFormat
            {
                Id = FbxArmature, Title = "FBX, armature only", Group = ExportGroup.Main,
                Description = "<take>_armature.fbx: the avatar's armature (exact bone names and rest pose) with the take's animation and no meshes. "
                            + "Small, for retargeting onto any rig. The face is not in it: that is in the model FBX and the Unity .anim.",
                State = ExportState.InSettings(nameof(Settings.ExportFbxArmature), s => s.ExportFbxArmature, (s, on) => s.ExportFbxArmature = on),
                Apply = job => job.Clip.FbxArmature = true,
                Options = new[]
                {
                    new ExportOption
                    {
                        Id = "fbx.camera", Title = "VRChat camera in the FBX", DependsOn = new[] { Fbx, FbxArmature },
                        Description = "Adds VRChat's photo or stream camera as an animated camera to either FBX. Needs one of the two FBX formats.",
                        State = ExportState.InSettings(nameof(Settings.FbxIncludeCamera), s => s.FbxIncludeCamera, (s, on) => s.FbxIncludeCamera = on),
                        Apply = job => job.Clip.FbxCamera = true,
                    },
                    new ExportOption
                    {
                        Id = "fbx.trackers", Title = "Trackers in the FBX", DependsOn = new[] { Fbx, FbxArmature },
                        Description = "Adds the headset, controllers and trackers as animated empties to either FBX. Needs one of the two FBX formats.",
                        State = ExportState.InSettings(nameof(Settings.FbxIncludeTrackers), s => s.FbxIncludeTrackers, (s, on) => s.FbxIncludeTrackers = on),
                        Apply = job => job.Clip.FbxTrackers = true,
                    },
                },
            },
            new ExportFormat
            {
                Id = "anim", Title = "Unity .anim", Group = ExportGroup.Main,
                Description = "<take>_body.anim: the body and the face on the avatar's own transform paths, exact on a Generic Animator; "
                            + "in Unity, Tools > VR Motion Bridge > Make Humanoid Clip turns it into a Humanoid clip. "
                            + "<take>_face.anim: the face shapes alone, as a face layer for any avatar with the same face mesh.",
                State = ExportState.InSettings(nameof(Settings.ExportAnim), s => s.ExportAnim || s.ExportFaceAnim, (s, on) => s.ExportAnim = s.ExportFaceAnim = on),
                Apply = job => job.Clip.BodyAnim = job.Clip.FaceAnim = true,
            },
            new ExportFormat
            {
                Id = "paramsAnim", Title = "Unity .anim: avatar parameters", Group = ExportGroup.More,
                Description = "<take>_params.anim: the recorded avatar parameters as animator curves, for replaying them through the FX layer in Unity.",
                State = ExportState.InSettings(nameof(Settings.ExportParamsAnim), s => s.ExportParamsAnim, (s, on) => s.ExportParamsAnim = on),
                Apply = job => job.Clip.ParamsAnim = true,
            },
            new ExportFormat
            {
                Id = "oscJson", Title = "OSC_Recorder .json", Group = ExportGroup.More,
                Description = "<take>_osc.json: the recorded avatar parameters in the OSC_Recorder tool's format, for sending them to VRChat again from Unity.",
                State = ExportState.InSettings(nameof(Settings.ExportOscJson), s => s.ExportOscJson, (s, on) => s.ExportOscJson = on),
                Apply = job => job.Writers.Add(part =>
                {
                    string path = part.PathFor("_osc.json");
                    MiscExporters.ExportOscRecorderJson(part.Take, path, part.Name);
                    return path;
                }),
            },
            new ExportFormat
            {
                Id = "humrLog", Title = "HUMR log", Group = ExportGroup.More,
                Description = "output_log_<take>.txt: the solved body as a HUMR log, for HUMR's loader in Unity to make a Humanoid clip from. Left out for a take without a body.",
                State = ExportState.InSettings(nameof(Settings.ExportHumrLog), s => s.ExportHumrLog, (s, on) => s.ExportHumrLog = on),
                Apply = job => job.Writers.Add(part =>
                {
                    if (part.Clip.BodySource == "none") return null;
                    string path = Path.Combine(part.Dir, "output_log_" + part.Name + ".txt");
                    MiscExporters.ExportHumrLog(part.Clip, path, string.IsNullOrEmpty(part.Take.LocalUser) ? "Recorder" : part.Take.LocalUser);
                    return path;
                }),
            },
            new ExportFormat
            {
                Id = "faceJson", Title = "Face .json (Blender add-on)", Group = ExportGroup.More,
                Description = "<take>.face.json: the face shapes and avatar parameters for the Blender add-on to key any mesh with. Always written next to an FBX; this also writes it without one.",
                State = ExportState.InUiState("export.faceJson"),
                Apply = job => job.Clip.FaceJson = true,
            },
        };
    }
}
