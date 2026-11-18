using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VRMR
{
    /// <summary>
    ///     The user's settings (settings.json in the app data folder, see <see cref="AppPaths" />). The file carries a
    ///     schema version: older files are upgraded when loaded (<see cref="SettingsMigrations" />), and keys this version
    ///     does not know are kept and written back, so two versions of the app can share one file.
    /// </summary>
    public sealed class Settings
    {
        /// <summary>The version of the settings file this code writes.</summary>
        public const int SchemaVersion = 2;

        public bool AutoLinkCalibration = true; // new takes use the latest matching calibration snapshot

        /// <summary>
        ///     Avatar library: base models (*.vrmavatar.json) the takes are solved onto (AvatarLibrary). Defaults to
        ///     <see cref="AppPaths.AvatarLibraryDir" />.
        /// </summary>
        public string AvatarLibraryDir;

        public Dictionary<string, string>
            AvatarModelMap = new(); // avtr_ id -> model .meta path (Unity projects; library entries keep their own ids)

        /// <summary>The Blender to open exports in: its executable or its folder ("" = the best one found, see BlenderInstalls).</summary>
        public string BlenderPath = "";

        public CalibrationSettings CalibrationDefaults = new(); // used by clips without their own calibration settings
        public bool CaptureFingers = true; // SteamVR Skeletal Input (Index finger tracking)

        /// <summary>Library entry used for avatars that match nothing ("" = the most used one, else the built-in mannequin).</summary>
        public string DefaultAvatar = "";

        public bool EnableHumr = true;
        public bool EnableOpenVR = true;

        public bool ExportFbx = true,
            ExportAnim = true,
            ExportFaceAnim = true,
            ExportParamsAnim = true,
            ExportOscJson,
            ExportHumrLog;

        /// <summary>Take export: also / only &lt;take&gt;_armature.fbx (no meshes); a folder per take inside the export folder.</summary>
        public bool ExportFbxArmature, ExportTakeFolders = true;

        public int ExportFps = 60;
        public float EyeYawRange = 30f, EyePitchRange = 20f;

        /// <summary>
        ///     The mesh face shapes are keyed on in Unity animations ("" = the avatar's own face mesh, see
        ///     AvatarLibrary.FaceMeshFor).
        /// </summary>
        public string FaceMeshPath = "";

        public string FaceNaming = "UnifiedExpressions"; // UnifiedExpressions | ARKit | Raw
        public bool FbxIncludeFaceMesh = true, FbxIncludeTrackers, FbxIncludeCamera = true, FbxAvatarMesh = true;
        public bool FollowHumrTakes; // start/stop with HUMR in-world button
        public string HotkeyRecord = "F9";
        public string LastExportDir = "";

        /// <summary>Scene editor: the last scene open, the NLA panel height (unscaled px).</summary>
        public string LastScene = "";

        public int LegacyPort = 9001;
        public bool ListenLegacyPort = true; // also bind 9001 if free
        public int NlaHeight = 300;
        public int OpenVRRate = 90;

        public bool
            OpenVRUtility = true; // utility app type (required for skeletal input); only connects to a running SteamVR

        public List<string> PreviewHiddenMeshes = new();
        public bool PreviewMesh = true, PreviewSkeleton; // avatar mesh from its FBX in the 3D view
        public bool PreviewWhileRecording = true;
        public string RecordTriggerParam = "VRMR/Record";

        /// <summary>Where takes are saved. Defaults to <see cref="AppPaths.RecordingsDir" />.</summary>
        public string RecordingsDir;

        /// <summary>3D view: draw the headset, controllers and trackers.</summary>
        public bool ShowDevices = true;

        /// <summary>The headset's model in the 3D view's devices (off: it covers the avatar's face).</summary>
        public bool ShowHmdModel;

        /// <summary>3D view: the SteamVR play area's outline and the base stations (live, and takes that recorded them).</summary>
        public bool ShowPlayArea = true, ShowBaseStations = true;

        public float Smoothing = 0.25f;
        public List<string> UnityProjects = new();
        public bool UseOscQuery = true;
        public int VrcInputPort = 9000;

        public bool
            WatchCalibrations = true; // save every VRChat full-body calibration as a snapshot (calibration library)

        public bool WindowMax;

        // ---- Window and panel state of the previous UI. This is UI state, not a setting: the new app keeps its own in
        // AppPaths.UiStateFile and does not use these. They stay readable and are written back unchanged.
        public int WindowX = -1, WindowY = -1, WindowW = 1400, WindowH = 860;

        Dictionary<string, object>
            _loaded; // the file as it was read (after upgrading), for the keys this version does not know

        /// <summary>Default settings for the current paths.</summary>
        public Settings() : this(AppPaths.Current) { }

        /// <summary>Default settings whose folders and file belong to <paramref name="paths" />.</summary>
        public Settings(AppPaths paths)
        {
            Paths = paths ?? throw new ArgumentNullException(nameof(paths));
            RecordingsDir = paths.RecordingsDir;
            AvatarLibraryDir = paths.AvatarLibraryDir;
        }

        /// <summary>The app data folder in use. New code asks <see cref="AppPaths" /> directly.</summary>
        public static string AppDataDir => AppPaths.Current.AppDataDir;

        /// <summary>The settings file in use. New code asks <see cref="AppPaths" /> directly.</summary>
        public static string FilePath => AppPaths.Current.SettingsFile;

        /// <summary>The schema version of the file these settings were loaded from (0 = there was no file).</summary>
        public int LoadedSchema { get; private set; }

        /// <summary>The paths these settings belong to (their file, and the defaults of their folders).</summary>
        public AppPaths Paths { get; }

        /// <summary>The settings of the current paths (<see cref="AppPaths.Current" />); defaults when there is no file yet.</summary>
        public static Settings Load()
        {
            return Load(AppPaths.Current);
        }

        /// <summary>The settings stored under <paramref name="paths" />; defaults when there is no file or it cannot be read.</summary>
        public static Settings Load(AppPaths paths)
        {
            var s = new Settings(paths);
            try
            {
                if (!File.Exists(paths.SettingsFile)) return s;
                var d = Json.ParseObject(File.ReadAllText(paths.SettingsFile, Encoding.UTF8));
                if (d == null) return s;
                s.LoadedSchema = SettingsMigrations.Upgrade(d);
                s._loaded = d;
                s.Read(d);
            }
            catch (Exception ex) { AppLog.Warn("Settings load failed: " + ex.Message); }

            return s;
        }

        void Read(Dictionary<string, object> d)
        {
            // folders: a missing or empty entry means the default location
            RecordingsDir = NonEmpty(d.GetStr("recordingsDir"), RecordingsDir);
            AvatarLibraryDir = NonEmpty(d.GetStr("avatarLibraryDir"), AvatarLibraryDir);
            UnityProjects = ToStrList(d.GetList("unityProjects")) ?? UnityProjects;
            var map = d.GetObj("avatarModelMap");
            if (map != null)
                foreach (var kv in map)
                    if (kv.Value is string v)
                        AvatarModelMap[kv.Key] = v;
            DefaultAvatar = d.GetStr("defaultAvatar", DefaultAvatar);
            UseOscQuery = d.GetBool("useOscQuery", UseOscQuery);
            ListenLegacyPort = d.GetBool("listenLegacyPort", ListenLegacyPort);
            LegacyPort = d.GetInt("legacyPort", LegacyPort);
            VrcInputPort = d.GetInt("vrcInputPort", VrcInputPort);
            EnableOpenVR = d.GetBool("enableOpenVR", EnableOpenVR);
            CaptureFingers = d.GetBool("captureFingers", CaptureFingers);
            OpenVRUtility = d.GetBool("openVRUtility", OpenVRUtility);
            OpenVRRate = d.GetInt("openVRRate", OpenVRRate);
            EnableHumr = d.GetBool("enableHumr", EnableHumr);
            FollowHumrTakes = d.GetBool("followHumrTakes", FollowHumrTakes);
            HotkeyRecord = d.GetStr("hotkeyRecord", HotkeyRecord);
            RecordTriggerParam = d.GetStr("recordTriggerParam", RecordTriggerParam);
            PreviewWhileRecording = d.GetBool("previewWhileRecording", PreviewWhileRecording);
            var cal = d.GetObj("calibrationDefaults");
            if (cal != null) CalibrationDefaults = CalibrationSettings.FromJson(cal);
            WatchCalibrations = d.GetBool("watchCalibrations", WatchCalibrations);
            AutoLinkCalibration = d.GetBool("autoLinkCalibration", AutoLinkCalibration);
            ExportFps = d.GetInt("exportFps", ExportFps);
            FaceMeshPath = d.GetStr("faceMeshPath", FaceMeshPath);
            FaceNaming = d.GetStr("faceNaming", FaceNaming);
            ExportFbx = d.GetBool("exportFbx", ExportFbx);
            ExportAnim = d.GetBool("exportAnim", ExportAnim);
            ExportFaceAnim = d.GetBool("exportFaceAnim", ExportFaceAnim);
            ExportParamsAnim = d.GetBool("exportParamsAnim", ExportParamsAnim);
            ExportOscJson = d.GetBool("exportOscJson", ExportOscJson);
            ExportHumrLog = d.GetBool("exportHumrLog", ExportHumrLog);
            FbxIncludeFaceMesh = d.GetBool("fbxFaceMesh", FbxIncludeFaceMesh);
            FbxIncludeTrackers = d.GetBool("fbxTrackers", FbxIncludeTrackers);
            FbxIncludeCamera = d.GetBool("fbxCamera", FbxIncludeCamera);
            FbxAvatarMesh = d.GetBool("fbxAvatarMesh", FbxAvatarMesh);
            LastExportDir = d.GetStr("lastExportDir", LastExportDir);
            ExportFbxArmature = d.GetBool("exportFbxArmature", ExportFbxArmature);
            ExportTakeFolders = d.GetBool("exportTakeFolders", ExportTakeFolders);
            EyeYawRange = d.GetF("eyeYawRange", EyeYawRange);
            EyePitchRange = d.GetF("eyePitchRange", EyePitchRange);
            Smoothing = d.GetF("smoothing", Smoothing);
            BlenderPath = d.GetStr("blenderPath", BlenderPath);
            PreviewMesh = d.GetBool("previewMesh", PreviewMesh);
            PreviewSkeleton = d.GetBool("previewSkeleton", PreviewSkeleton);
            PreviewHiddenMeshes = ToStrList(d.GetList("previewHiddenMeshes")) ?? PreviewHiddenMeshes;
            ShowDevices = d.GetBool("showDevices", ShowDevices);
            ShowHmdModel = d.GetBool("showHmdModel", ShowHmdModel);
            ShowPlayArea = d.GetBool("showPlayArea", ShowPlayArea);
            ShowBaseStations = d.GetBool("showBaseStations", ShowBaseStations);
            WindowX = d.GetInt("wx", -1);
            WindowY = d.GetInt("wy", -1);
            WindowW = d.GetInt("ww", WindowW);
            WindowH = d.GetInt("wh", WindowH);
            WindowMax = d.GetBool("wmax");
            LastScene = d.GetStr("lastScene", LastScene);
            NlaHeight = d.GetInt("nlaHeight", NlaHeight);
        }

        static string NonEmpty(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        static List<string> ToStrList(List<object> l)
        {
            if (l == null) return null;
            var r = new List<string>();
            foreach (object o in l)
                if (o is string s)
                    r.Add(s);
            return r;
        }

        /// <summary>The file's content: the schema version, every key of the loaded file, then this version's values over them.</summary>
        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object> { ["schema"] = Math.Max(SchemaVersion, LoadedSchema) };
            if (_loaded != null)
                foreach (var kv in _loaded)
                    if (kv.Key != "schema")
                        d[kv.Key] = kv.Value;

            // a folder still at its default is left out: the file then holds no path of this computer, and follows the
            // default wherever the file is used
            SetOrOmit(d, "recordingsDir", RecordingsDir, Paths.RecordingsDir);
            SetOrOmit(d, "avatarLibraryDir", AvatarLibraryDir, Paths.AvatarLibraryDir);
            d["unityProjects"] = new List<object>(UnityProjects);
            var map = new Dictionary<string, object>();
            foreach (var kv in AvatarModelMap) map[kv.Key] = kv.Value;
            d["avatarModelMap"] = map;
            d["defaultAvatar"] = DefaultAvatar;
            d["useOscQuery"] = UseOscQuery;
            d["listenLegacyPort"] = ListenLegacyPort;
            d["legacyPort"] = LegacyPort;
            d["vrcInputPort"] = VrcInputPort;
            d["enableOpenVR"] = EnableOpenVR;
            d["captureFingers"] = CaptureFingers;
            d["openVRUtility"] = OpenVRUtility;
            d["openVRRate"] = OpenVRRate;
            d["enableHumr"] = EnableHumr;
            d["followHumrTakes"] = FollowHumrTakes;
            d["hotkeyRecord"] = HotkeyRecord;
            d["recordTriggerParam"] = RecordTriggerParam;
            d["previewWhileRecording"] = PreviewWhileRecording;
            d["calibrationDefaults"] = CalibrationDefaults.ToJson();
            d["watchCalibrations"] = WatchCalibrations;
            d["autoLinkCalibration"] = AutoLinkCalibration;
            d["exportFps"] = ExportFps;
            d["faceMeshPath"] = FaceMeshPath;
            d["faceNaming"] = FaceNaming;
            d["exportFbx"] = ExportFbx;
            d["exportAnim"] = ExportAnim;
            d["exportFaceAnim"] = ExportFaceAnim;
            d["exportParamsAnim"] = ExportParamsAnim;
            d["exportOscJson"] = ExportOscJson;
            d["exportHumrLog"] = ExportHumrLog;
            d["fbxFaceMesh"] = FbxIncludeFaceMesh;
            d["fbxTrackers"] = FbxIncludeTrackers;
            d["fbxCamera"] = FbxIncludeCamera;
            d["fbxAvatarMesh"] = FbxAvatarMesh;
            d["lastExportDir"] = LastExportDir;
            d["exportFbxArmature"] = ExportFbxArmature;
            d["exportTakeFolders"] = ExportTakeFolders;
            d["eyeYawRange"] = (double)EyeYawRange;
            d["eyePitchRange"] = (double)EyePitchRange;
            d["smoothing"] = (double)Smoothing;
            d["blenderPath"] = BlenderPath;
            d["previewMesh"] = PreviewMesh;
            d["previewSkeleton"] = PreviewSkeleton;
            d["previewHiddenMeshes"] = new List<object>(PreviewHiddenMeshes);
            d["showDevices"] = ShowDevices;
            d["showHmdModel"] = ShowHmdModel;
            d["showPlayArea"] = ShowPlayArea;
            d["showBaseStations"] = ShowBaseStations;
            d["wx"] = WindowX;
            d["wy"] = WindowY;
            d["ww"] = WindowW;
            d["wh"] = WindowH;
            d["wmax"] = WindowMax;
            d["lastScene"] = LastScene;
            d["nlaHeight"] = NlaHeight;
            return d;
        }

        static void SetOrOmit(Dictionary<string, object> d, string key, string value, string defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value) || SamePath(value, defaultValue)) d.Remove(key);
            else d[key] = value;
        }

        static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>Writes the settings to the file of the paths they belong to. A failure is logged, not thrown.</summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Paths.AppDataDir);
                // written next to the file and moved over it, so a crash cannot leave half a settings file
                string tmp = Paths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, Json.Serialize(ToJson(), true), Encoding.UTF8);
                File.Move(tmp, Paths.SettingsFile, true);
            }
            catch (Exception ex) { AppLog.Warn("Settings save failed: " + ex.Message); }
        }

        /// <summary>What the environment probe needs to know about these settings.</summary>
        public ProbeOptions ProbeOptions()
        {
            return new ProbeOptions
            {
                BlenderPath = BlenderPath ?? "", RecordingsDir = RecordingsDir, AvatarLibraryDir = AvatarLibraryDir,
                UnityProjects = new List<string>(UnityProjects)
            };
        }
    }

    /// <summary>
    ///     Upgrades a settings file read by an older version. Each step takes the parsed file from one schema version to
    ///     the next and is safe to run again on a file that was already upgraded (another version of the app may write
    ///     the file back without the schema number).
    /// </summary>
    public static class SettingsMigrations
    {
        /// <summary>
        ///     Upgrades <paramref name="file" /> in place to <see cref="Settings.SchemaVersion" />. Returns the version it
        ///     had.
        /// </summary>
        public static int Upgrade(Dictionary<string, object> file)
        {
            int from = Math.Max(1, file.GetInt("schema", 1)); // files from before the schema number are version 1
            if (from < 2) ToVersion2(file);
            file["schema"] = Math.Max(from, Settings.SchemaVersion);
            return from;
        }

        /// <summary>Version 2: calibration settings as one object with the current defaults; the face mesh found per avatar.</summary>
        static void ToVersion2(Dictionary<string, object> d)
        {
            // the user's eye height used to be a setting of its own
            float eye = d.GetF("realEyeHeight");
            if (d.GetObj("calibrationDefaults") == null && eye > 0.3f)
                d["calibrationDefaults"] = new CalibrationSettings
                    { Height = CalHeightMode.EyeHeight, HeightValue = eye }.ToJson();
            d.Remove("realEyeHeight");
            // defaults that were never changed from the first version's (measured height, lock both) follow the current
            // defaults: VRChat's User Real Height, and the head locked to the headset like the hands to the controllers
            var first = new CalibrationSettings { Height = CalHeightMode.Measure, Lock = CalLock.Both };
            var cal = d.GetObj("calibrationDefaults");
            if (cal != null && CalibrationSettings.FromJson(cal).SameAs(first)) d.Remove("calibrationDefaults");
            // the face mesh used to default to the most common mesh name; empty now means "the avatar's own face mesh"
            if (d.GetStr("faceMeshPath", "") == "Body") d["faceMeshPath"] = "";
        }
    }
}
