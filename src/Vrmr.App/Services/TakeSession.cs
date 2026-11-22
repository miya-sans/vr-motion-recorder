using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VRMR.App.Services
{
    /// <summary>Where the solve of the open take stands.</summary>
    public enum SolveState
    {
        /// <summary>No take, or nothing to solve.</summary>
        None,

        /// <summary>An edit arrived; the solve starts once edits pause.</summary>
        Waiting,
        Solving,

        /// <summary>The clip shows the take as it is now.</summary>
        Done,
        Failed
    }

    /// <summary>
    ///     The open take: loading it, the list of edits, undo and redo, saving, and the background solve that turns it
    ///     into a clip. Edits never touch the raw data and never block: each one swaps in a new edit list and the solve
    ///     follows a moment later on a worker, cancelling the one before.
    /// </summary>
    public sealed class TakeSession : IDisposable
    {
        /// <summary>Wait after the last edit before solving again.</summary>
        public static readonly TimeSpan SolveDelay = TimeSpan.FromMilliseconds(350);

        readonly AvatarLibrary _avatars;

        readonly SettingsStore _settings;
        readonly Debouncer _solveSoon;
        readonly List<TaskCompletionSource<bool>> _solveWaiters = new();
        readonly StatusService _status;
        readonly IUiDispatcher _ui;
        int _editVersion, _solvedVersion = -1, _solveRun, _openRun;
        volatile float _progress;
        int _progressPosted;
        bool _restoring;
        CancellationTokenSource _previewCancel;
        CancellationTokenSource _solveCancel;

        public TakeSession(SettingsStore settings, AvatarLibrary avatars, IUiDispatcher ui, StatusService status,
            Func<double> now = null)
        {
            _settings = settings;
            _avatars = avatars;
            _ui = ui;
            _status = status;
            History = new EditHistory(now);
            _solveSoon = new Debouncer(ui, SolveDelay, StartSolve);
            _settings.Changed += OnSettingsChanged;
        }

        /// <summary>The open take, or null.</summary>
        public Recording Take { get; private set; }

        /// <summary>The last finished solve of the open take (it may be one edit behind while <see cref="Solve" /> is not Done).</summary>
        public BakedClip Clip { get; private set; }

        /// <summary>Edits not saved yet.</summary>
        public bool Dirty { get; private set; }

        /// <summary>The take had no skeleton of its own: the base model its avatar matches in the library was used.</summary>
        public bool AutoProfile { get; private set; }

        /// <summary>True while a take is being read from disk.</summary>
        public bool Opening { get; private set; }

        public SolveState Solve { get; private set; }

        /// <summary>0..1 while solving.</summary>
        public float SolveProgress => _progress;

        /// <summary>Why the last solve failed, in the solver's words.</summary>
        public string SolveError { get; private set; }

        /// <summary>How long the last solve took.</summary>
        public long SolveMilliseconds { get; private set; }

        /// <summary>True when <see cref="Clip" /> reflects every edit made so far.</summary>
        public bool ClipIsCurrent =>
            Take != null && Clip != null && Solve == SolveState.Done && _solvedVersion == _editVersion;

        public EditHistory History { get; }

        public void Dispose()
        {
            _settings.Changed -= OnSettingsChanged;
            ClearPreviewClip();
            CancelSolve();
            _solveSoon.Dispose();
            CompleteWaiters(false);
        }

        /// <summary>Another take was opened, or the take was closed.</summary>
        public event Action TakeChanged;

        /// <summary>The edits changed; the argument names what ("*" after undo, redo or revert).</summary>
        public event Action<string> EditsChanged;

        /// <summary>The solve state or its progress changed.</summary>
        public event Action SolveChanged;

        /// <summary>A new solve arrived in <see cref="Clip" /> (or it was cleared).</summary>
        public event Action ClipChanged;

        /// <summary><see cref="Dirty" /> or the take's name changed.</summary>
        public event Action DirtyChanged;

        // ------------------------------------------------------------------------------------------ open / close

        /// <summary>
        ///     Opens a take file in the background; edits of the take open before are saved first. Returns false when it
        ///     could not be read.
        /// </summary>
        public async Task<bool> OpenAsync(string path, bool force = false)
        {
            if (!force && Take?.FilePath != null &&
                string.Equals(Take.FilePath, path, StringComparison.OrdinalIgnoreCase)) return true;
            if (!force) await SaveIfDirtyAsync();
            int run = ++_openRun;
            Opening = true;
            SolveChanged?.Invoke();
            Recording rec = null;
            string error = null;
            try { rec = await Task.Run(() => RecordingIO.Load(path)); }
            catch (Exception ex)
            {
                error = ex.Message;
                AppLog.Error("Open take: " + ex);
            }

            if (run != _openRun) return false; // a newer open won
            Opening = false;
            if (rec == null)
            {
                SolveChanged?.Invoke();
                _status.Show(new Toast
                {
                    Text =
                        $"Could not open {Path.GetFileName(path)}: {error}. The file may be damaged or still being written.",
                    Kind = StatusKind.Error, Duration = TimeSpan.FromSeconds(8)
                });
                return false;
            }

            Show(rec);
            return true;
        }

        /// <summary>Makes a take in memory the open one (a take that was just recorded, or one a test built).</summary>
        public void Show(Recording rec)
        {
            CancelSolve();
            _solveSoon.Cancel();
            CompleteWaiters(false);
            ClearPreviewClip();
            Take = rec;
            Clip = null;
            Dirty = false;
            Opening = false;
            AutoProfile = rec != null && rec.Profile == null;
            if (AutoProfile)
                rec.Profile = _avatars.MatchTake(rec).Entry?.Profile(); // the base model the take's avatar matches
            _editVersion++;
            _solvedVersion = -1;
            History.Reset(Snapshot());
            Solve = SolveState.None;
            SolveError = null;
            TakeChanged?.Invoke();
            ClipChanged?.Invoke();
            DirtyChanged?.Invoke();
            if (rec != null) RequestSolve(true);
            else SolveChanged?.Invoke();
        }

        /// <summary>Closes the take (after it was deleted, say).</summary>
        public void Close()
        {
            Show(null);
        }

        // ------------------------------------------------------------------------------------------ edits

        /// <summary>
        ///     Changes the take's edit list. The change is applied to a copy that then replaces the list, so a solve that is
        ///     running never sees a half-made edit. <paramref name="what" /> names the field for undo merging and listeners.
        ///     With <paramref name="solve" /> false the solve is left for a later edit (the moves of a drag).
        /// </summary>
        public void Edit(string what, Action<EditList> change, bool solve = true)
        {
            var rec = Take;
            if (rec == null) return;
            var next = CloneEdits(rec.Edits);
            change(next);
            rec.Edits = next;
            AfterEdit(what, solve);
        }

        /// <summary>Changes something on the take outside its edit list (tracker roles, its skeleton).</summary>
        public void EditTake(string what, Action<Recording> change, bool solve = true)
        {
            var rec = Take;
            if (rec == null) return;
            change(rec);
            AfterEdit(what, solve);
        }

        void AfterEdit(string what, bool solve)
        {
            _editVersion++;
            SetDirty(true);
            if (!_restoring) History.Commit(Snapshot(), what);
            EditsChanged?.Invoke(what);
            if (solve) RequestSolve();
            else if (Solve == SolveState.Done)
            {
                Solve = SolveState.Waiting;
                SolveChanged?.Invoke();
            }
        }

        /// <summary>A copy of an edit list that can be changed without touching the original (the linked calibration is shared).</summary>
        public static EditList CloneEdits(EditList e)
        {
            var copy = EditList.FromJson(Json.ParseObject(Json.Serialize(e.ToJson())));
            if (e.Snapshot != null) copy.Snapshot = e.Snapshot;
            return copy;
        }

        /// <summary>
        ///     Everything edited until the result is disposed is one undo step (a drag on the timeline, in the view or on a
        ///     value).
        /// </summary>
        public IDisposable Gesture()
        {
            return History.Gesture();
        }

        /// <summary>The take's name (shown in the library, used for export file names).</summary>
        public void SetName(string name)
        {
            if (Take == null || Take.Name == name) return;
            Take.Name = name ?? "";
            SetDirty(true, true);
        }

        public void SetNotes(string notes)
        {
            if (Take == null || Take.Notes == notes) return;
            Take.Notes = notes ?? "";
            SetDirty(true);
        }

        void SetDirty(bool dirty, bool force = false)
        {
            if (Dirty == dirty && !force) return;
            Dirty = dirty;
            DirtyChanged?.Invoke();
        }

        // ------------------------------------------------------------------------------------------ undo / redo

        string Snapshot()
        {
            var rec = Take;
            if (rec == null) return null;
            return Json.Serialize(new Dictionary<string, object>
            {
                ["edits"] = rec.Edits.ToJson(),
                ["roles"] = rec.Streams.Select(s => (object)s.BodyRole).ToList()
            });
        }

        public void Undo()
        {
            Restore(History.Undo(Snapshot()));
        }

        public void Redo()
        {
            Restore(History.Redo());
        }

        void Restore(string json)
        {
            var rec = Take;
            if (json == null || rec == null) return;
            var d = Json.ParseObject(json);
            _restoring = true;
            try
            {
                var old = rec.Edits.Snapshot;
                var edits = EditList.FromJson(d.GetObj("edits"));
                if (old != null && edits.Snapshot != null && edits.Snapshot.Id == old.Id) edits.Snapshot = old;
                rec.Edits = edits;
                var roles = d.GetList("roles");
                if (roles != null)
                    for (int i = 0; i < rec.Streams.Count && i < roles.Count; i++)
                    {
                        string v = roles[i] as string ?? "";
                        if (rec.Streams[i].BodyRole != v)
                            rec.Streams[i].BodyRole = v; // only real changes (no empty "bodyRole" keys)
                    }

                TrackerGaps.Invalidate(rec);
                AfterEdit("*", true);
            }
            finally { _restoring = false; }
        }

        // ------------------------------------------------------------------------------------------ save / revert

        /// <summary>Saves the edits into the take file in the background (the raw data is written back unchanged).</summary>
        public async Task<bool> SaveAsync()
        {
            var rec = Take;
            if (rec == null) return false;
            string path = rec.FilePath ?? Path.Combine(_settings.Value.RecordingsDir,
                DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".vrmr");
            SetDirty(false);
            try
            {
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    RecordingIO.Save(rec, path);
                });
                _status.Info($"Saved {rec.Name}");
                return true;
            }
            catch (Exception ex)
            {
                if (Take == rec) SetDirty(true);
                AppLog.Error("Save take: " + ex);
                _status.Show(new Toast
                {
                    Text = $"Could not save {rec.Name}: {ex.Message}. Check that the recordings folder can be written.",
                    Kind = StatusKind.Error, Duration = TimeSpan.FromSeconds(8)
                });
                return false;
            }
        }

        /// <summary>Saves when there are unsaved edits.</summary>
        public Task SaveIfDirtyAsync()
        {
            return Dirty ? SaveAsync() : Task.CompletedTask;
        }

        /// <summary>On exit: saves unsaved edits and waits for the file.</summary>
        public void SaveIfDirtyNow()
        {
            var rec = Take;
            if (rec == null || !Dirty || rec.FilePath == null) return;
            try
            {
                RecordingIO.Save(rec, rec.FilePath);
                Dirty = false;
            }
            catch (Exception ex) { AppLog.Error("Save take on exit: " + ex); }
        }

        /// <summary>Reloads the take as it was last saved.</summary>
        public Task<bool> RevertAsync()
        {
            return Take?.FilePath != null ? OpenAsync(Take.FilePath, true) : Task.FromResult(false);
        }

        // ------------------------------------------------------------------------------------------ solve

        /// <summary>The options of the solve: app settings plus the take's own calibration settings (else the defaults).</summary>
        public BakeOptions MakeBakeOptions()
        {
            var s = _settings.Value;
            return new BakeOptions
            {
                Fps = s.ExportFps,
                FaceNaming = s.FaceNaming == "ARKit" ? FaceMapping.Naming.ARKit : FaceMapping.Naming.UnifiedExpressions,
                EyeYaw = s.EyeYawRange, EyePitch = s.EyePitchRange, Smoothing = s.Smoothing,
                Calibration = (Take?.Edits.Calib ?? s.CalibrationDefaults).Clone()
            };
        }

        void OnSettingsChanged(string what)
        {
            if (Take == null) return;
            switch (what)
            {
                case nameof(Settings.ExportFps):
                case nameof(Settings.FaceNaming):
                case nameof(Settings.Smoothing):
                case nameof(Settings.EyeYawRange):
                case nameof(Settings.EyePitchRange):
                    _editVersion++;
                    RequestSolve();
                    break;
                case nameof(Settings.CalibrationDefaults):
                    if (Take.Edits.Calib == null)
                    {
                        _editVersion++;
                        RequestSolve();
                    }

                    break;
            }
        }

        /// <summary>Solves again: after the usual pause, or right away.</summary>
        public void RequestSolve(bool now = false)
        {
            if (Take == null) return;
            CancelSolve(); // free the solver for the newer state
            Solve = SolveState.Waiting;
            _progress = 0;
            SolveChanged?.Invoke();
            if (now)
            {
                _solveSoon.Cancel();
                StartSolve();
            }
            else _solveSoon.Trigger();
        }

        /// <summary>Completes with true once the clip reflects every edit, false when that solve failed or the take changed.</summary>
        public Task<bool> WhenSolved()
        {
            if (Take == null) return Task.FromResult(false);
            if (ClipIsCurrent) return Task.FromResult(true);
            var tcs = new TaskCompletionSource<bool>();
            _solveWaiters.Add(tcs);
            if (Solve != SolveState.Solving) RequestSolve(true);
            return tcs.Task;
        }

        void CancelSolve()
        {
            _solveCancel?.Cancel();
            _solveCancel = null;
        }

        void StartSolve()
        {
            var rec = Take;
            if (rec == null) return;
            CancelSolve();
            var cancel = _solveCancel = new CancellationTokenSource();
            var token = cancel.Token;
            int run = ++_solveRun, version = _editVersion;
            var options = MakeBakeOptions();
            Solve = SolveState.Solving;
            _progress = 0;
            SolveError = null;
            SolveChanged?.Invoke();
            var thread = new Thread(() =>
            {
                BakedClip clip = null;
                string error = null;
                var sw = Stopwatch.StartNew();
                try
                {
                    var target = _avatars.TargetFor(rec, out _);
                    lock (TakeCache.SolveLock)
                    lock (rec.Sync)
                    {
                        token.ThrowIfCancellationRequested();
                        sw.Restart();
                        clip = PoseBuilder.Bake(rec, target, options, f =>
                        {
                            token.ThrowIfCancellationRequested();
                            ReportProgress(f, run);
                        });
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    error = ex.Message;
                    AppLog.Error("Solve: " + ex);
                }

                long ms = sw.ElapsedMilliseconds;
                _ui.Post(() => FinishSolve(rec, run, version, clip, error, ms));
            }) { IsBackground = true, Name = "take solve", Priority = ThreadPriority.BelowNormal };
            thread.Start();
        }

        /// <summary>Untrimmed poses for a timeline drag that reaches outside the solved clip.</summary>
        public BakedClip PreviewClip { get; private set; }

        public event Action PreviewClipReady;

        public void RequestPreviewClip()
        {
            var rec = Take;
            var target = Clip?.Profile;
            if (rec == null || target == null || _previewCancel != null) return;
            var cancel = _previewCancel = new CancellationTokenSource();
            var token = cancel.Token;
            var options = MakeBakeOptions();
            options.SourceRange = [0, rec.Duration];
            Task.Run(() =>
            {
                BakedClip preview;
                try
                {
                    lock (TakeCache.SolveLock)
                    lock (rec.Sync)
                    {
                        token.ThrowIfCancellationRequested();
                        preview = PoseBuilder.Bake(rec, target, options, _ => token.ThrowIfCancellationRequested());
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    AppLog.Warn("Timeline pose preview: " + ex.Message);
                    return;
                }

                _ui.Post(() =>
                {
                    if (_previewCancel != cancel) return;
                    PreviewClip = preview;
                    PreviewClipReady?.Invoke();
                });
            });
        }

        public void ClearPreviewClip()
        {
            _previewCancel?.Cancel();
            _previewCancel = null;
            PreviewClip = null;
        }

        void ReportProgress(float f, int run)
        {
            _progress = f;
            if (Interlocked.Exchange(ref _progressPosted, 1) == 1) return;
            _ui.Post(() =>
            {
                _progressPosted = 0;
                if (run == _solveRun && Solve == SolveState.Solving) SolveChanged?.Invoke();
            });
        }

        void FinishSolve(Recording rec, int run, int version, BakedClip clip, string error, long ms)
        {
            if (run != _solveRun || Take != rec) return; // a newer solve or another take took over
            SolveMilliseconds = ms;
            if (clip != null)
            {
                ClearPreviewClip();
                Clip = clip;
                _solvedVersion = version;
                bool current = version == _editVersion;
                Solve = current ? SolveState.Done : SolveState.Waiting;
                _progress = 1;
                ClipChanged?.Invoke();
                SolveChanged?.Invoke();
                if (current) CompleteWaiters(true);
            }
            else
            {
                Solve = SolveState.Failed;
                SolveError = error;
                SolveChanged?.Invoke();
                CompleteWaiters(false);
            }
        }

        void CompleteWaiters(bool ok)
        {
            if (_solveWaiters.Count == 0) return;
            var waiters = _solveWaiters.ToArray();
            _solveWaiters.Clear();
            foreach (var w in waiters) w.TrySetResult(ok);
        }
    }
}
