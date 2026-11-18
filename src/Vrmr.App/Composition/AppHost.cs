using System;
using System.IO;
using VRMR.App.Services;
using VRMR.App.Viewport;
using VRMR.Render;

namespace VRMR.App.Composition
{
    /// <summary>
    ///     The composition root: creates the engine objects (settings, recorder, avatar library, solver) and the app's
    ///     services once, hands them to the view-models, and shuts them down in order. Nothing else news these up.
    /// </summary>
    public sealed class AppHost : IDisposable
    {
        /// <param name="faceSender">
        ///     Where replayed face parameters go; null for VRChat's OSC input on this computer. Tests pass
        ///     their own.
        /// </param>
        public AppHost(AppOptions options, IUiDispatcher ui, IGlobalHotkey hotkey,
            Func<IParameterSender> faceSender = null)
        {
            Options = options;
            Ui = ui;
            var paths = options.Paths;
            Program.UsePaths(options);
            Settings = Settings.Load(paths);
            if (paths.DataRoot != null)
            {
                // portable mode: the takes and the avatar library always sit under the data folder
                Settings.RecordingsDir = paths.RecordingsDir;
                Settings.AvatarLibraryDir = paths.AvatarLibraryDir;
            }
            else LinkEnclosingUnityProject(Settings);

            try { Directory.CreateDirectory(Settings.RecordingsDir); }
            catch (Exception ex) { AppLog.Warn("Recordings folder: " + ex.Message); }

            BodySolverSetup.Register();
            Meshes = new AvatarMeshLoader(SkiaTextureDecoder.Decode);

            Avatars = new AvatarLibrary(Settings);
            // takes without a skeleton of their own: the base model their avatar matches in the library
            PoseBuilder.SourceResolver = r => Avatars.MatchTake(r).Entry?.Profile();
            // an avatar's face-tracking add-on, kept with it in the library, plays its own face shapes
            PoseBuilder.FaceAnimatorResolver = FaceAnimatorFiles.ForProfile;
            Recorder = new Recorder(Settings);
            if (CaptureOn) Recorder.Start();
            else AppLog.Info("Live sources are off (--no-capture)");

            SettingsStore = new SettingsStore(Settings, ui);
            UiState = new UiStateStore(paths.UiStateFile, ui);
            Status = new StatusService(ui);
            Library = new TakeLibrary(SettingsStore, ui);
            Live = new LiveService(Recorder, Avatars, CaptureOn);
            Rooms = new RoomService(paths.RoomFile);
            Session = new TakeSession(SettingsStore, Avatars, ui, Status);
            Clock = new PlaybackClock();
            TakePresenter = new TakePresenter(Session, Clock, Rooms);
            LivePresenter = new LivePresenter(Live);
            Placement = new PlacementService(Session, TakePresenter, Clock);
            // replaying the face sends OSC to VRChat: only offered when the live sources run
            FaceReplay = new FaceReplayService(!CaptureOn
                ? null
                : faceSender ?? (() =>
                    new OscParameterSender(Recorder.OscQuery?.VrcOsc?.Port ?? Settings.VrcInputPort)));
            Recording = new RecordingController(Recorder, SettingsStore, Live, Session, Library, Status, ui,
                CaptureOn ? hotkey : null);
            Calibration = new CalibrationService(Session, SettingsStore, Live, Status, Clock, ui);
            Trackers = new TrackerService(Session);
            TakeAvatars = new TakeAvatarService(Session, Avatars, Live, Status);
            Export = new ExportService(SettingsStore, UiState, Session, Avatars, Status, ui);
            AvatarService = new AvatarService(Avatars, SettingsStore, Live, Status, ui, Meshes);
            Environment = new EnvironmentService(paths, SettingsStore, ui);
            if (!CaptureOn) hotkey?.Dispose();
        }

        public AppOptions Options { get; }
        public IUiDispatcher Ui { get; }

        // engine
        public Settings Settings { get; }
        public Recorder Recorder { get; }
        public AvatarLibrary Avatars { get; }
        public AvatarMeshLoader Meshes { get; }

        // services shared by both workspaces
        public SettingsStore SettingsStore { get; }
        public UiStateStore UiState { get; }
        public StatusService Status { get; }
        public TakeLibrary Library { get; }
        public LiveService Live { get; }
        public RoomService Rooms { get; }
        public RecordingController Recording { get; }

        // the Takes workspace
        public TakeSession Session { get; }
        public PlaybackClock Clock { get; }
        public TakePresenter TakePresenter { get; }
        public LivePresenter LivePresenter { get; }
        public PlacementService Placement { get; }
        public FaceReplayService FaceReplay { get; }
        public CalibrationService Calibration { get; }
        public TrackerService Trackers { get; }
        public TakeAvatarService TakeAvatars { get; }
        public ExportService Export { get; }
        public AvatarService AvatarService { get; }
        public EnvironmentService Environment { get; }

        /// <summary>True when the recorder's sources run (false with --no-capture).</summary>
        public bool CaptureOn => !Options.NoCapture;

        /// <summary>Stops recording, saves what is unsaved and releases the sources.</summary>
        public void Dispose()
        {
            try
            {
                if (Recording.IsRecording) Recording.Stop();
            }
            catch (Exception ex) { AppLog.Error("Stop on exit: " + ex); }

            Session.SaveIfDirtyNow();
            Recording.Dispose();
            FaceReplay.Dispose();
            Session.Dispose();
            Library.Dispose();
            SettingsStore.Dispose();
            UiState.Dispose();
            Recorder.Dispose();
            AppLog.Info("bye");
        }

        /// <summary>First run from inside a Unity project: link it, so its avatars are found without any setup.</summary>
        static void LinkEnclosingUnityProject(Settings settings)
        {
            if (settings.UnityProjects.Count > 0) return;
            try
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (int i = 0; i < 4 && dir != null; i++, dir = dir.Parent)
                    if (UnityProjectScanner.IsUnityProject(dir.FullName))
                    {
                        settings.UnityProjects.Add(dir.FullName);
                        settings.Save();
                        break;
                    }
            }
            catch (Exception ex) { AppLog.Warn("Unity project lookup: " + ex.Message); }
        }
    }
}
