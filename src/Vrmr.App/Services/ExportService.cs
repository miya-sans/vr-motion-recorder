using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VRMR.App.Services
{
    /// <summary>
    ///     Exports the open take. It knows which formats are on (the catalogue's entries, each kept in the settings), where
    ///     the files go and the options of the export, and it runs the export: that waits for the solve of the latest
    ///     edits, writes on a worker thread, and says what it is doing in the status bar and through
    ///     <see cref="ProgressChanged" />.
    /// </summary>
    public sealed class ExportService
    {
        const string SplitKey = "export.splitAvatars";

        /// <summary>An avatar's stretch shorter than this (seconds) is left out of a per-avatar export.</summary>
        const double ShortestPart = 0.2;

        readonly AvatarLibrary _avatars;
        readonly object _progressLock = new();
        readonly TakeSession _session;

        readonly SettingsStore _settings;
        readonly StatusService _status;
        readonly ExportStores _stores;
        readonly IUiDispatcher _ui;
        readonly UiStateStore _uiState;
        StatusProgress _bar;
        bool _reportPosted;
        double _reportedFraction = -1;
        string _reportedText = "";

        /// <param name="formats">What can be exported; the formats of a take when left out.</param>
        public ExportService(SettingsStore settings, UiStateStore uiState, TakeSession session, AvatarLibrary avatars,
            StatusService status, IUiDispatcher ui,
            IReadOnlyList<ExportFormat> formats = null)
        {
            _settings = settings;
            _uiState = uiState;
            _session = session;
            _avatars = avatars;
            _status = status;
            _ui = ui;
            _stores = new ExportStores(settings, uiState);
            Formats = formats ?? ExportCatalog.Takes;
            Folders = new ExportFolders(settings);
            _settings.Changed += what => Changed?.Invoke();
            _uiState.Changed += key =>
            {
                if (key.StartsWith("export.", StringComparison.Ordinal)) Changed?.Invoke();
            };
        }

        /// <summary>The frame rates offered for the solved clip.</summary>
        public static IReadOnlyList<int> FrameRates { get; } = new[] { 24, 30, 60, 90, 120 };

        Settings S => _settings.Value;

        /// <summary>What can be exported, in the order shown.</summary>
        public IReadOnlyList<ExportFormat> Formats { get; }

        /// <summary>Where the files go.</summary>
        public ExportFolders Folders { get; }

        // ------------------------------------------------------------------------------------------ options

        /// <summary>Frames per second of the solved clip. An app setting: the open take solves again when it changes.</summary>
        public int FrameRate
        {
            get => S.ExportFps;
            set
            {
                if (value > 0 && S.ExportFps != value)
                    _settings.Update(nameof(Settings.ExportFps), s => s.ExportFps = value);
            }
        }

        /// <summary>
        ///     How the exported face shapes are named: "UnifiedExpressions" or "ARKit". The open take solves again when it
        ///     changes.
        /// </summary>
        public string FaceNaming
        {
            get => S.FaceNaming;
            set
            {
                if (!string.IsNullOrEmpty(value) && S.FaceNaming != value)
                    _settings.Update(nameof(Settings.FaceNaming), s => s.FaceNaming = value);
            }
        }

        /// <summary>The mesh the face shapes are keyed on, as a path from the avatar's root. Empty: the avatar's own face mesh.</summary>
        public string FaceMeshPath
        {
            get => S.FaceMeshPath ?? "";
            set
            {
                string path = (value ?? "").Trim();
                if (FaceMeshPath != path) _settings.Update(nameof(Settings.FaceMeshPath), s => s.FaceMeshPath = path);
            }
        }

        /// <summary>
        ///     A take in which the avatar was changed is written as one clip per avatar, each solved onto that avatar's base
        ///     model.
        /// </summary>
        public bool SplitAtAvatarChanges
        {
            get => _uiState.GetBool(SplitKey, false);
            set => _uiState.Set(SplitKey, value);
        }

        // ------------------------------------------------------------------------------------------ progress

        /// <summary>True from the start of an export until its result is there.</summary>
        public bool IsExporting { get; private set; }

        /// <summary>What the running export is doing right now.</summary>
        public string ProgressText { get; private set; } = "";

        /// <summary>0..1, or negative while the amount is unknown.</summary>
        public double ProgressFraction { get; private set; } = -1;

        /// <summary>Raised on the UI thread when a choice, an option or the folder changed, here or in the settings.</summary>
        public event Action Changed;

        // ------------------------------------------------------------------------------------------ formats

        /// <summary>True when a format or option is switched on.</summary>
        public bool IsOn(ExportChoice choice)
        {
            return choice.State.Read(_stores);
        }

        /// <summary>Switches a format or option on or off, where it is remembered.</summary>
        public void SetOn(ExportChoice choice, bool on)
        {
            if (IsOn(choice) != on) choice.State.Write(_stores, on);
        }

        /// <summary>True while one of the formats an option belongs to is on (else the option has nothing to act on).</summary>
        public bool IsAvailable(ExportOption option)
        {
            return option.IsNested ? IsOn(option.Format) : Formats.Any(f => option.DependsOn.Contains(f.Id) && IsOn(f));
        }

        /// <summary>How many formats are on: of one group, or of all.</summary>
        public int CountOn(ExportGroup? group = null)
        {
            return Formats.Count(f => (group == null || f.Group == group) && IsOn(f));
        }

        /// <summary>What an export started now would write: every format that is on, with the options that apply to them.</summary>
        public ExportJob BuildJob()
        {
            var job = new ExportJob();
            foreach (var format in Formats)
            {
                if (!IsOn(format)) continue;
                job.Formats.Add(format.Id);
                format.Apply(job);
            }

            foreach (var option in Formats.SelectMany(f => f.Options))
                if (IsOn(option) && IsAvailable(option))
                    option.Apply(job);
            return job;
        }

        /// <summary>Raised on the UI thread when an export started, got further or ended.</summary>
        public event Action ProgressChanged;

        void ShowProgress(string text, double fraction = -1)
        {
            ProgressText = text;
            ProgressFraction = fraction;
            _bar?.Report(fraction, text);
            ProgressChanged?.Invoke();
        }

        /// <summary>Progress from the worker thread: the UI thread shows the latest state once it gets to it.</summary>
        void Report(string text, double fraction = -1)
        {
            lock (_progressLock)
            {
                _reportedText = text;
                _reportedFraction = fraction;
                if (_reportPosted) return;
                _reportPosted = true;
            }

            _ui.Post(() =>
            {
                string latest;
                double amount;
                lock (_progressLock)
                {
                    _reportPosted = false;
                    latest = _reportedText;
                    amount = _reportedFraction;
                }

                if (IsExporting) ShowProgress(latest, amount);
            });
        }

        /// <summary>
        ///     Writes the open take in every chosen format. Edits not solved yet are solved first. One export runs at a
        ///     time. The result always says in plain words what happened; it never throws.
        /// </summary>
        public async Task<ExportResult> ExportAsync()
        {
            var take = _session.Take;
            if (take == null) return ExportResult.Skipped("Open a take first: pick one in the library.");
            if (IsExporting) return ExportResult.Skipped("An export is already running. Wait until it is done.");
            var job = BuildJob();
            if (job.Formats.Count == 0) return ExportResult.Skipped("Choose at least one format.");

            IsExporting = true;
            _bar = _status.Begin("Exporting " + take.Name);
            ShowProgress("Exporting…");
            ExportResult result;
            string dir = null;
            try
            {
                string problem = await WaitForSolve(take);
                if (problem != null) result = ExportResult.Failed(problem);
                else
                {
                    var run = new ExportRun
                    {
                        Take = take, Clip = _session.Clip, Edits = take.Edits, Solve = _session.MakeBakeOptions(),
                        Job = job,
                        Name = ExportFolders.SafeName(take.Name), Root = Folders.ExportDir,
                        Dir = Folders.TakeDir(take.Name),
                        FaceMesh = FaceMeshPath, FaceNaming = S.FaceNaming, Split = SplitAtAvatarChanges
                    };
                    dir = run.Dir;
                    result = await OnWorker(() => Write(run));
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Export: " + ex);
                result = ExportResult.Failed(Describe(ex, dir));
            }

            Finish(result);
            return result;
        }

        /// <summary>Waits until the clip shows every edit of the take. Returns why the export cannot go on, or null.</summary>
        async Task<string> WaitForSolve(Recording take)
        {
            const string anotherTake =
                "Export did not start: another take was opened before this one was solved. Open the take again and export it.";
            while (_session.Take == take && !_session.ClipIsCurrent)
            {
                ShowProgress("Solving the latest edit, then exporting…");
                if (await _session.WhenSolved()) continue;
                if (_session.Take != take) return anotherTake;
                string why = string.IsNullOrEmpty(_session.SolveError)
                    ? ""
                    : " (" + _session.SolveError.TrimEnd('.', ' ') + ")";
                return
                    $"Export did not start: the take could not be solved{why}. Try another calibration source or avatar in View, then export again.";
            }

            return _session.Take == take ? null : anotherTake;
        }

        /// <summary>Runs work on a thread of its own; the task completes on the UI thread.</summary>
        Task<T> OnWorker<T>(Func<T> work)
        {
            var done = new TaskCompletionSource<T>();
            var thread = new Thread(() =>
            {
                T result = default;
                Exception error = null;
                try { result = work(); }
                catch (Exception ex) { error = ex; }

                _ui.Post(() =>
                {
                    if (error != null) done.SetException(error);
                    else done.SetResult(result);
                });
            }) { IsBackground = true, Name = "take export" };
            thread.Start();
            return done.Task;
        }

        void Finish(ExportResult result)
        {
            IsExporting = false;
            ProgressText = "";
            ProgressFraction = -1;
            _bar?.Dispose();
            _bar = null;
            if (result.Exported)
            {
                Folders.LastOutputDir = result.Folder;
                if (result.Notes.Count == 0) _status.Success(result.Message);
                else
                    _status.Warn(
                        $"{result.Message}, with {result.Notes.Count} {(result.Notes.Count == 1 ? "note" : "notes")} on the Export tab");
            }
            else if (result.Kind == StatusKind.Error) _status.Error(result.Message);

            ProgressChanged?.Invoke();
        }

        // ------------------------------------------------------------------------------------------ the worker

        /// <summary>The worker's whole job: solve the parts, then write each of them.</summary>
        ExportResult Write(ExportRun run)
        {
            var parts = Parts(run);
            if (parts.Count == 0)
                return ExportResult.Skipped(
                    "Nothing was written: after its trims, no avatar's stretch of this take is long enough. Widen the trim on the timeline.");
            Directory.CreateDirectory(run.Dir);
            var files = new List<string>();
            var notes = new List<string>();
            for (int i = 0; i < parts.Count; i++)
            {
                string prefix = parts.Count > 1 ? $"Part {i + 1} of {parts.Count}: " : "";
                double fraction = parts.Count > 1 ? (double)i / parts.Count : -1;
                Report(prefix + "Writing the files…", fraction);
                WritePart(run, parts[i], stage => Report(prefix + stage, fraction), files, notes);
            }

            if (files.Count == 0)
                return ExportResult.Skipped(
                    "Nothing was written: this take has no data for the chosen formats. Choose another format.");
            return ExportResult.Done(files, notes.Distinct().ToList(), run.Dir, parts.Count);
        }

        /// <summary>
        ///     The clips to write: the solved take, or one clip per avatar worn during it, each solved onto that avatar's
        ///     base model.
        /// </summary>
        List<ExportPart> Parts(ExportRun run)
        {
            var take = run.Take;
            var parts = new List<ExportPart>();
            var stretches = run.Split ? AvatarLibrary.AvatarSegments(take) : null;
            if (stretches == null || stretches.Count < 2)
            {
                parts.Add(new ExportPart { Take = take, Clip = run.Clip, Dir = run.Dir, Name = run.Name });
                return parts;
            }

            double end = run.Edits.TrimOut < 0 ? take.Duration : run.Edits.TrimOut;
            for (int k = 0; k < stretches.Count; k++)
            {
                var stretch = stretches[k];
                var edits = TakeSession.CloneEdits(run.Edits);
                edits.TrimIn = Math.Max(run.Edits.TrimIn, stretch.Start);
                edits.TrimOut = Math.Min(end, stretch.End);
                if (edits.TrimOut - edits.TrimIn < ShortestPart) continue;
                var match = _avatars.Match(stretch.AvatarId, stretch.AvatarName);
                edits.TargetAvatar = match.Entry?.Id ?? "";
                string stage = $"Solving part {k + 1} of {stretches.Count}" +
                               (match.Entry != null ? " onto " + match.Entry.Label : "") + "…";
                int index = k;
                var clip = SolvePart(take, edits, match.Entry?.Profile(), run.Solve,
                    f => Report(stage, (index + f) / stretches.Count));
                parts.Add(new ExportPart { Take = take, Clip = clip, Dir = run.Dir, Name = run.Name + "_" + (k + 1) });
            }

            return parts;
        }

        /// <summary>
        ///     Solves the take with another edit list (one avatar's stretch) on a view of the take that has that list. The
        ///     open take and its own edit list are never touched, so an edit made while the parts solve is kept; the export
        ///     writes the take as it was when Export was pressed.
        /// </summary>
        static BakedClip SolvePart(Recording take, EditList edits, AvatarProfile target, BakeOptions options,
            Action<float> progress)
        {
            var part = take.WithEdits(edits);
            lock (TakeCache.SolveLock)
            lock (take.Sync)
                return PoseBuilder.Bake(part, target, options, progress);
        }

        /// <summary>
        ///     Writes one clip in every chosen format: the clip files shared with the scene export, the formats with their
        ///     own writer, and the take's manifest next to an FBX.
        /// </summary>
        void WritePart(ExportRun run, ExportPart part, Action<string> progress, List<string> files, List<string> notes)
        {
            var take = part.Take;
            var clip = part.Clip;
            string faceMesh = FaceMeshFor(run, clip);
            var options = run.Job.Clip;
            options.FaceMeshPath = faceMesh;
            options.FaceNaming = run.FaceNaming;
            options.AvatarId = take.AvatarId ?? "";
            options.AvatarName = take.AvatarName ?? "";
            options.Kind = "take";
            options.TextureDir = ExportFolders.TextureDir(run.Root, clip.Profile?.Name);
            options.Extra = new Dictionary<string, object> { ["take"] = take.Name ?? "" };
            options.Progress = progress;
            var written = ClipExport.Write(clip, part.Dir, part.Name, options);
            files.AddRange(written.Paths);
            notes.AddRange(written.Warnings);
            foreach (var writer in run.Job.Writers)
            {
                string path = writer(part);
                if (path != null) files.Add(path);
            }

            if (run.Job.AnyFbx)
                files.Add(SceneExport.WriteTakeManifest(part.Dir, part.Name, clip, written, clip.Profile?.Name,
                    faceMesh, take.AvatarId, take.AvatarName, options.FbxCamera, written.Warnings));
        }

        /// <summary>
        ///     The mesh the face shapes are keyed on: the one set as an option, else the one the avatar's library entry
        ///     names, else "Body".
        /// </summary>
        string FaceMeshFor(ExportRun run, BakedClip clip)
        {
            if (!string.IsNullOrEmpty(run.FaceMesh)) return run.FaceMesh;
            string own = _avatars.Get(clip.Profile?.LibraryId)?.FaceMesh;
            return string.IsNullOrEmpty(own) ? "Body" : own;
        }

        /// <summary>A failure in plain words, with what to try next.</summary>
        static string Describe(Exception ex, string dir)
        {
            string folder = dir ?? "the export folder";
            switch (ex)
            {
                case UnauthorizedAccessException _:
                    return
                        $"Export failed: {folder} cannot be written (access was denied). Pick another export folder with Browse.";
                case DirectoryNotFoundException _:
                case DriveNotFoundException _:
                    return
                        $"Export failed: {folder} could not be created because its drive or a folder above it is missing. Pick another export folder with Browse.";
                case PathTooLongException _:
                    return
                        $"Export failed: the path of a file in {folder} is too long. Pick an export folder with a shorter path, or give the take a shorter name.";
                case IOException _:
                    return
                        $"Export failed: {Sentence(ex.Message)} If a file is open in another program (Blender, Unity), close it there and export again; else pick another export folder.";
                default:
                    return $"Export failed: {Sentence(ex.Message)} The details are in the log (Settings → About).";
            }
        }

        static string Sentence(string text)
        {
            return (text ?? "").TrimEnd('.', ' ') + ".";
        }

        // ------------------------------------------------------------------------------------------ export

        /// <summary>Everything one export needs, read on the UI thread before the worker starts.</summary>
        sealed class ExportRun
        {
            public BakedClip Clip;

            /// <summary>The take's edits when the export started: the state the files show.</summary>
            public EditList Edits;

            public ExportJob Job;
            public string Name, Root, Dir, FaceMesh, FaceNaming;
            public BakeOptions Solve;
            public bool Split;
            public Recording Take;
        }
    }
}
