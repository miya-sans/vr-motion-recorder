using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VRMR.App.Services
{
    /// <summary>One mesh of an avatar's preview model, as the hidden-meshes list shows it.</summary>
    public sealed class AvatarMeshPart
    {
        public string Name { get; init; } = "";
        public int Triangles { get; init; }
    }

    /// <summary>The meshes of an avatar's preview model, or why there are none to list.</summary>
    public sealed class AvatarMeshList
    {
        public IReadOnlyList<AvatarMeshPart> Parts { get; init; } = Array.Empty<AvatarMeshPart>();

        /// <summary>The model file's name ("" when there is none).</summary>
        public string File { get; init; } = "";

        public int Triangles { get; init; }

        /// <summary>What to tell the user when there is nothing to list ("" otherwise).</summary>
        public string Note { get; init; } = "";
    }

    /// <summary>
    ///     What "Export FBX with face shapes" found for an avatar: the model, the add-ons made for it and a file name to
    ///     suggest, or why it cannot be done.
    /// </summary>
    public sealed class FaceFbxPlan
    {
        /// <summary>The avatar's own FBX.</summary>
        public string Fbx { get; init; } = "";

        /// <summary>The BlendShare assets in the Unity project that add shapes to that FBX.</summary>
        public IReadOnlyList<string> Assets { get; init; } = Array.Empty<string>();

        /// <summary>A file name for the copy, without the extension.</summary>
        public string SuggestedName { get; init; } = "";

        /// <summary>Why there is nothing to export, in plain words with the next step; null when the export can run.</summary>
        public string Problem { get; init; }

        internal AvatarProfile Profile { get; init; }
    }

    /// <summary>
    ///     The avatar library as the app uses it: the base models, adding one from the files an avatar ships with (in the
    ///     background), removing, renaming, the default, the VRChat names that mean a model, Unity projects, linking the
    ///     avatar worn now, the mesh list of a model and the FBX copy with face shapes. Results go to the status bar.
    /// </summary>
    public sealed class AvatarService
    {
        readonly AvatarLibrary _avatars;
        readonly LiveService _live;
        readonly AvatarMeshLoader _meshes;
        readonly SettingsStore _settings;
        readonly StatusService _status;
        readonly IUiDispatcher _ui;
        IDisposable _scanWatch;

        public AvatarService(AvatarLibrary avatars, SettingsStore settings, LiveService live, StatusService status,
            IUiDispatcher ui, AvatarMeshLoader meshes)
        {
            _meshes = meshes;
            _avatars = avatars;
            _settings = settings;
            _live = live;
            _status = status;
            _ui = ui;
            // the library also changes from its own threads (a project scan) and through other services (a link made in the Devices panel)
            _avatars.Updated += () => OnUi(RaiseChanged);
            _settings.Changed += what =>
            {
                if (what == nameof(Settings.AvatarLibraryDir)) Reload();
            };
        }

        // ------------------------------------------------------------------------------------------ entries

        /// <summary>The user's base models by name, then the built-in mannequin.</summary>
        public IReadOnlyList<AvatarEntry> Entries => _avatars.All();

        /// <summary>True when the user has added a base model of their own.</summary>
        public bool HasOwnEntries => _avatars.Stored().Count > 0;

        /// <summary>The base model used for avatars that match nothing.</summary>
        public AvatarEntry Default => _avatars.Default;

        /// <summary>The library folder.</summary>
        public string Folder => _avatars.Dir;

        // ------------------------------------------------------------------------------------------ adding

        /// <summary>True while an avatar is being added.</summary>
        public bool Importing { get; private set; }

        /// <summary>What the import is doing right now.</summary>
        public string ImportMessage { get; private set; } = "";

        /// <summary>Everything the last import reported, one line per step ("" before the first).</summary>
        public string LastImportReport { get; private set; } = "";

        // ------------------------------------------------------------------------------------------ Unity projects

        /// <summary>The Unity projects whose avatars are matched by their VRChat id.</summary>
        public IReadOnlyList<string> UnityProjects => _settings.Value.UnityProjects.ToList();

        /// <summary>True while the projects are being scanned.</summary>
        public bool Scanning => _avatars.Scanning;

        // ------------------------------------------------------------------------------------------ the avatar worn now

        /// <summary>The id of the avatar worn in VRChat ("" when VRChat has not reported one).</summary>
        public string WornAvatarId => _live.Recorder.AvatarId ?? "";

        /// <summary>The avatar worn in VRChat, in words ("" when VRChat has not reported one).</summary>
        public string WornAvatar => _live.WornAvatar;

        // ------------------------------------------------------------------------------------------ meshes

        /// <summary>The names of the meshes hidden in the 3D view.</summary>
        public IReadOnlyList<string> HiddenMeshes => _settings.Value.PreviewHiddenMeshes;

        /// <summary>Progress and linked-avatar counts for this project only.</summary>
        public string ProjectScanInfo(string project)
        {
            return _avatars.ProjectScanInfo(project);
        }

        /// <summary>Raised on the UI thread when the entries, the default, a link, the import state or the scan state changed.</summary>
        public event Action Changed;

        /// <summary>False when the entry's preview FBX is gone (the built-in mannequin has none and needs none).</summary>
        public static bool HasFbx(AvatarEntry entry)
        {
            return entry.BuiltIn || File.Exists(entry.FbxPath);
        }

        /// <summary>Uses this base model for avatars that match nothing.</summary>
        public void SetDefault(AvatarEntry entry)
        {
            if (entry == null) return;
            _settings.Update(nameof(Settings.DefaultAvatar), s => s.DefaultAvatar = entry.Id);
            _live.InvalidateMatch();
            _status.Info($"{entry.Label} is now used for avatars that match nothing");
            RaiseChanged();
        }

        /// <summary>The VRChat names of an entry as the user edits them: comma separated.</summary>
        public static string NamesText(AvatarEntry entry)
        {
            return entry == null ? "" : string.Join(", ", entry.Match);
        }

        /// <summary>
        ///     Stores the VRChat avatar names (comma or semicolon separated) that mean this base model. False when nothing
        ///     changed or it could not be saved.
        /// </summary>
        public bool SetNames(AvatarEntry entry, string text)
        {
            if (entry == null || entry.BuiltIn || entry.Transient) return false;
            var names = (text ?? "").Split(new[] { ',', ';', '、' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
            if (names.SequenceEqual(entry.Match)) return false;
            var before = entry.Match;
            entry.Match = names;
            if (!Save(entry))
            {
                entry.Match = before;
                return false;
            }

            _live.InvalidateMatch();
            return true;
        }

        /// <summary>
        ///     Changes the name an entry is listed under (its id, files and links stay). False when nothing changed or it
        ///     could not be saved.
        /// </summary>
        public bool Rename(AvatarEntry entry, string name)
        {
            name = (name ?? "").Trim();
            if (entry == null || entry.BuiltIn || entry.Transient || name.Length == 0 || name == entry.Name)
                return false;
            string before = entry.Name;
            entry.Name = name;
            if (!Save(entry))
            {
                entry.Name = before;
                return false;
            }

            _avatars.Reload(); // the list is sorted by name
            _live.InvalidateMatch();
            RaiseChanged();
            return true;
        }

        bool Save(AvatarEntry entry)
        {
            try
            {
                _avatars.Save(entry);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("Avatar library save: " + ex);
                _status.Notify(
                    $"{entry.Label} could not be saved: {ex.Message}. Check that the avatar library folder can be written (Settings → Folders).",
                    StatusKind.Error);
                return false;
            }
        }

        /// <summary>Removes a base model: its files go to the Recycle Bin. Takes keep their own copy of the skeleton.</summary>
        public void Remove(AvatarEntry entry)
        {
            if (entry == null || entry.BuiltIn || entry.Transient) return;
            _avatars.Remove(entry);
            if (string.Equals(_settings.Value.DefaultAvatar, entry.Id, StringComparison.OrdinalIgnoreCase))
                _settings.Update(nameof(Settings.DefaultAvatar), s => s.DefaultAvatar = "");
            _live.InvalidateMatch();
            _status.Info($"Moved {entry.Label} to the Recycle Bin");
            RaiseChanged();
        }

        /// <summary>Reads the library folder again (after files were changed by hand).</summary>
        public void Reload()
        {
            _avatars.Reload();
            _live.InvalidateMatch();
            RaiseChanged();
        }

        /// <summary>Opens the library folder in the file manager.</summary>
        public void OpenFolder()
        {
            try { Directory.CreateDirectory(Folder); }
            catch (Exception ex)
            {
                _status.Notify(
                    $"The avatar library folder could not be created: {ex.Message}. Pick another folder under Settings → Folders.",
                    StatusKind.Error);
                return;
            }

            FileActions.OpenFolder(Folder);
        }

        /// <summary>"Default · eye height 1.32 m · 2 linked" for the list row.</summary>
        public string Summary(AvatarEntry entry)
        {
            var parts = new List<string> { FormattableString.Invariant($"eye height {entry.EyeHeight:0.00} m") };
            if (entry.AvatarIds.Count > 0) parts.Add(entry.AvatarIds.Count + " linked");
            return string.Join(" · ", parts);
        }

        /// <summary>Everything known about an entry, one fact per line.</summary>
        public string Details(AvatarEntry entry)
        {
            if (entry == null) return "";
            var lines = new List<string>
            {
                FormattableString.Invariant(
                    $"{entry.Skeleton.Human.Count(h => h >= 0)} humanoid bones, eye height {entry.EyeHeight:0.000} m")
            };
            if (!entry.BuiltIn)
                lines.Add(HasFbx(entry)
                    ? "Model: " + Path.GetFileName(entry.FbxPath)
                    : "Model: missing (" + entry.FbxPath + "). Add the avatar again to get its mesh back.");
            if (entry.AvatarIds.Count > 0) lines.Add("Linked VRChat avatars: " + entry.AvatarIds.Count);
            if (entry.Notes.Length > 0) lines.Add(entry.Notes);
            if (!entry.BuiltIn && entry.FilePath.Length > 0 && File.Exists(
                    Path.Combine(Path.GetDirectoryName(entry.FilePath), FaceAnimatorFiles.FolderName, "files.json")))
                lines.Add("Face-tracking animator: kept with the avatar; takes play the add-on's own face shapes");
            if (!entry.BuiltIn && entry.Source.Length > 0) lines.Add("From: " + entry.Source);
            return string.Join("\n", lines);
        }

        /// <summary>
        ///     Adds a base model from the files an avatar ships with (.unitypackage or .zip plus a face-tracking add-on, or
        ///     a .fbx with its .meta), in the background. The result is the new entry, or null when nothing was added;
        ///     either way the status bar says what happened.
        /// </summary>
        public Task<AvatarEntry> AddAsync(IReadOnlyList<string> files, string name = null)
        {
            if (files == null || files.Count == 0) return Task.FromResult<AvatarEntry>(null);
            if (Importing)
            {
                _status.Notify("An avatar is still being added. Wait until it is done, then add the next one.",
                    StatusKind.Warning);
                return Task.FromResult<AvatarEntry>(null);
            }

            Importing = true;
            ImportMessage = "Reading the files…";
            RaiseChanged();
            var progress = _status.Begin("Adding the avatar…");
            string dir = Folder;
            var projects = _settings.Value.UnityProjects.ToList();
            var taken = new HashSet<string>(_avatars.All().Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
            return Background(() =>
            {
                var result = AvatarImport.Import(files, dir, string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                    message => _ui.Post(() =>
                    {
                        ImportMessage = message;
                        RaiseChanged();
                    }), projects);
                if (result.Entry != null) MakeUnique(result.Entry, taken);
                return result;
            }, (result, error) =>
            {
                progress.Dispose();
                Importing = false;
                ImportMessage = "";
                LastImportReport = error != null ? error.Message : result.Summary;
                _avatars.Reload();
                _live.InvalidateMatch();
                var entry = result?.Entry == null ? null : _avatars.Get(result.Entry.Id);
                if (error != null)
                {
                    AppLog.Error("Avatar import: " + error);
                    _status.Notify(
                        $"The avatar could not be added: {error.Message}. The details are in the log (Settings → About).",
                        StatusKind.Error);
                }
                else if (entry == null) _status.Notify(WhyNothingWasAdded(result), StatusKind.Error);
                else
                    _status.Notify(AddedText(entry, result),
                        result.Log.Any(IsWarning) ? StatusKind.Warning : StatusKind.Success);

                RaiseChanged();
                return entry;
            });
        }

        /// <summary>
        ///     Keeps a face-tracking add-on's animator with a base model already in the library, from the add-on's package
        ///     and the framework it builds on (packages or folders; the linked Unity projects are searched for a missing
        ///     framework). Takes solved onto the avatar from then on play the add-on's own face shapes. The result is a
        ///     line for the status bar.
        /// </summary>
        public Task<string> AddFaceAnimatorAsync(AvatarEntry entry, IReadOnlyList<string> sources)
        {
            if (entry == null || entry.BuiltIn || sources == null || sources.Count == 0)
                return Task.FromResult<string>(null);
            string entryDir = Path.GetDirectoryName(entry.FilePath);
            var projects = _settings.Value.UnityProjects.ToList();
            var progress = _status.Begin("Reading the face-tracking add-on…");
            return Background(() => FaceAnimatorFiles.Gather(sources, projects), (files, error) =>
            {
                progress.Dispose();
                string text;
                if (error != null)
                {
                    AppLog.Error("Face animator: " + error);
                    text = "The face-tracking add-on could not be read: " + error.Message;
                    _status.Notify(text, StatusKind.Error);
                    return text;
                }

                foreach (string line in files.Log) AppLog.Info("Face animator: " + line);
                if (files.Roots.Count == 0)
                {
                    text =
                        "No face-tracking animator was found in those files. Pick the add-on's .unitypackage or .zip (and its face-tracking framework, if it needs one).";
                    _status.Notify(text, StatusKind.Warning);
                    return text;
                }

                try { FaceAnimatorFiles.Save(files, entryDir); }
                catch (Exception ex)
                {
                    text = "The animator could not be saved next to the avatar: " + ex.Message;
                    _status.Notify(text, StatusKind.Error);
                    return text;
                }

                FaceAnimatorFiles.Forget(entryDir);
                string fbx = SkinnedAvatar.FbxFor(entry.Profile());
                if (fbx != null) _meshes.Forget(fbx);
                string missing = files.Log.FirstOrDefault(l => l.Contains("missing"));
                text = missing != null
                    ? $"{entry.Label}: {missing}"
                    : $"{entry.Label} now plays its face-tracking add-on's own face shapes ({files.Log.Last()}). Reopen a take to see it.";
                _status.Notify(text, missing != null ? StatusKind.Warning : StatusKind.Success);
                RaiseChanged();
                return text;
            });
        }

        /// <summary>The same model added a second time gets an id and a name of its own instead of hiding behind the first.</summary>
        void MakeUnique(AvatarEntry entry, HashSet<string> taken)
        {
            if (!taken.Contains(entry.Id)) return;
            string id = entry.Id, name = entry.Name;
            for (int n = 2; taken.Contains(entry.Id); n++)
            {
                entry.Id = id + "_" + n;
                entry.Name = name + " (" + n + ")";
            }

            File.WriteAllText(entry.FilePath, Json.Serialize(entry.ToJson(), true), Encoding.UTF8);
        }

        static bool IsWarning(string line)
        {
            return line.StartsWith("Warning", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("no face-tracking shapes merged");
        }

        static string AddedText(AvatarEntry entry, AvatarImport.Result result)
        {
            string text = FormattableString.Invariant(
                $"Added {entry.Label}: {entry.Skeleton.Human.Count(h => h >= 0)} humanoid bones, eye height {entry.EyeHeight:0.00} m.");
            string warning = result.Log.FirstOrDefault(IsWarning);
            return warning == null ? text : text + " " + warning.TrimEnd('.') + ".";
        }

        static string WhyNothingWasAdded(AvatarImport.Result result)
        {
            // the import says per file why it was of no use; the last line is the verdict
            string reason = result.Log.LastOrDefault(l =>
                !l.StartsWith("No humanoid", StringComparison.Ordinal) &&
                !l.StartsWith("Reading ", StringComparison.Ordinal));
            const string next =
                "Pick the avatar's .unitypackage (or the .zip it came in), or a .fbx that has its .meta next to it.";
            return "No avatar was added. " +
                   (reason != null ? reason.TrimEnd('.') + ". " : "The files hold no humanoid model. ") + next;
        }

        /// <summary>Links a Unity project and scans it. Returns why the folder was not accepted, or null.</summary>
        public string AddUnityProject(string dir)
        {
            return AddUnityProjects(new[] { dir }).FirstOrDefault();
        }

        /// <summary>Links a selection before starting its avatar scan. Returns rejected-folder messages.</summary>
        public List<string> AddUnityProjects(IEnumerable<string> paths)
        {
            var problems = new List<string>();
            var added = new List<string>();
            var known = new HashSet<string>(_settings.Value.UnityProjects.Select(VRMR.UnityProjects.NormalizePath),
                StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    problems.Add("Pick a Unity project folder.");
                    continue;
                }

                string dir = VRMR.UnityProjects.NormalizePath(path);
                if (!UnityProjectScanner.IsUnityProject(dir))
                {
                    problems.Add(
                        "That folder is not a Unity project. Pick the folder that contains Assets and ProjectSettings.");
                    continue;
                }

                if (known.Add(dir)) added.Add(dir);
            }

            if (added.Count > 0)
            {
                _settings.Update(nameof(Settings.UnityProjects), s => s.UnityProjects.AddRange(added));
                Rescan(added);
            }

            return problems;
        }

        /// <summary>Forgets a Unity project (links already made to its models stay).</summary>
        public void RemoveUnityProject(string dir)
        {
            if (!_settings.Value.UnityProjects.Contains(dir)) return;
            _settings.Update(nameof(Settings.UnityProjects), s => s.UnityProjects.Remove(dir));
            _avatars.ForgetProjectScan(dir);
            RaiseChanged();
        }

        /// <summary>Looks through the linked Unity projects for avatars again, in the background.</summary>
        public void Rescan(IEnumerable<string> projects = null)
        {
            if (_settings.Value.UnityProjects.Count == 0)
            {
                RaiseChanged();
                return;
            }

            _avatars.ScanAsync(projects);
            // the scan reports its progress as text only: look at it a few times a second until it is done
            _scanWatch ??= _ui.Every(TimeSpan.FromMilliseconds(250), () =>
            {
                if (!Scanning)
                {
                    _scanWatch?.Dispose();
                    _scanWatch = null;
                    _live.InvalidateMatch();
                }

                RaiseChanged();
            });
            RaiseChanged();
        }

        /// <summary>
        ///     From now on the avatar worn in VRChat is the model at <paramref name="modelPath" /> (a .fbx or its .meta inside
        ///     a Unity project). Returns why not, or null when the link was made.
        /// </summary>
        public string LinkWornAvatar(string modelPath)
        {
            string id = WornAvatarId;
            if (id.Length == 0)
                return
                    "VRChat has not reported an avatar yet. Turn OSC on in VRChat (Action Menu → Options → OSC → Enabled), then try again.";
            if (string.IsNullOrWhiteSpace(modelPath)) return "Pick the avatar's model.";
            string meta = modelPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                ? modelPath
                : modelPath + ".meta";
            if (!File.Exists(meta))
                return "There is no .meta next to that file. Pick the model inside a Unity project's Assets folder.";
            var profile = _avatars.LoadModel(meta);
            if (profile == null)
                return
                    "That model has no humanoid rig in its import settings. Set its Animation Type to Humanoid in Unity, then try again.";
            _avatars.Link(id, profile.SourcePath);
            _live.InvalidateMatch();
            _status.Info($"{(WornAvatar.Length > 0 ? WornAvatar : "The avatar worn now")} is linked to {profile.Name}");
            RaiseChanged();
            return null;
        }

        /// <summary>Hides or shows one mesh of the avatar in the 3D view.</summary>
        public void SetMeshHidden(string name, bool hidden)
        {
            if (string.IsNullOrEmpty(name) || _settings.Value.PreviewHiddenMeshes.Contains(name) == hidden) return;
            _settings.Update(nameof(Settings.PreviewHiddenMeshes), s =>
            {
                // a new list, so a view drawing on another thread never sees it change under it
                var list = s.PreviewHiddenMeshes.Where(n => n != name).ToList();
                if (hidden) list.Add(name);
                s.PreviewHiddenMeshes = list;
            });
        }

        /// <summary>
        ///     Reads the meshes of an avatar's preview model in the background (the model is loaded once and shared with the
        ///     3D view).
        /// </summary>
        public Task<AvatarMeshList> LoadMeshesAsync(AvatarProfile profile)
        {
            if (profile == null)
                return Task.FromResult(new AvatarMeshList { Note = "Open a take to list the meshes of its avatar." });
            if (Mannequin.Is(profile))
                return Task.FromResult(new AvatarMeshList
                {
                    Note = "The built-in mannequin is one mesh. Add your avatar under Avatars to see its own meshes."
                });
            return Background(() =>
                {
                    string fbx = SkinnedAvatar.FbxFor(profile);
                    if (fbx == null)
                        return new AvatarMeshList
                            { Note = "No model file was found for this avatar. Add it under Avatars to see its mesh." };
                    var model = _meshes.ForProfile(profile);
                    if (model == null)
                        return new AvatarMeshList
                        {
                            File = Path.GetFileName(fbx),
                            Note = Path.GetFileName(fbx) +
                                   " could not be read. The details are in the log (Settings → About)."
                        };
                    return new AvatarMeshList
                    {
                        File = Path.GetFileName(fbx), Triangles = model.TriangleCount,
                        Parts = model.Parts.Select(p => new AvatarMeshPart
                                { Name = p.Name, Triangles = p.Tri.Length / 3 })
                            .ToList()
                    };
                },
                (list, error) => list ?? new AvatarMeshList
                    { Note = "The avatar's meshes could not be read: " + error.Message });
        }

        // ------------------------------------------------------------------------------------------ FBX with face shapes

        /// <summary>
        ///     Looks in the avatar's Unity project for add-ons (BlendShare assets) that add shapes to its FBX, in the
        ///     background.
        /// </summary>
        public Task<FaceFbxPlan> PlanFaceFbxAsync(AvatarProfile profile)
        {
            if (profile == null)
                return Task.FromResult(new FaceFbxPlan
                {
                    Problem = "There is no avatar on screen. Open a take, or wear the avatar in VRChat, then try again."
                });
            return Background(() =>
                {
                    string fbx = SkinnedAvatar.FbxFor(profile);
                    if (fbx == null)
                        return new FaceFbxPlan
                        {
                            Problem =
                                "No model file was found for this avatar. Link it to its model in a Unity project first."
                        };
                    var assets = BlendShareAsset.FindFor(fbx);
                    if (assets.Count == 0)
                        return new FaceFbxPlan
                        {
                            Fbx = fbx,
                            Problem =
                                $"No face-tracking add-on (BlendShare asset) in a Unity project uses {Path.GetFileName(fbx)}. Avatars added from a package already have their face shapes merged in."
                        };
                    string suggested = null;
                    try { suggested = BlendShareAsset.Load(assets[0]).DefaultName; }
                    catch (Exception ex) { AppLog.Warn("BlendShare " + assets[0] + ": " + ex.Message); }

                    return new FaceFbxPlan
                    {
                        Fbx = fbx, Assets = assets, Profile = profile,
                        SuggestedName = string.IsNullOrEmpty(suggested)
                            ? Path.GetFileNameWithoutExtension(fbx) + "_FT"
                            : suggested
                    };
                },
                (plan, error) => plan ?? new FaceFbxPlan
                    { Problem = "The Unity project could not be searched: " + error.Message });
        }

        /// <summary>
        ///     Writes a copy of the avatar's FBX with the add-ons' shapes merged in, and copies its textures next to it so
        ///     other tools find them. Runs in the background; false when it failed (the status bar says why).
        /// </summary>
        public Task<bool> ExportFaceFbxAsync(FaceFbxPlan plan, string outFbx)
        {
            if (plan?.Problem != null || string.IsNullOrWhiteSpace(outFbx)) return Task.FromResult(false);
            if (SamePath(outFbx, plan.Fbx))
            {
                _status.Notify("Choose another file name: the copy must not replace the avatar's own FBX.",
                    StatusKind.Warning);
                return Task.FromResult(false);
            }

            string name = Path.GetFileName(outFbx);
            var progress = _status.Begin($"Exporting {name}…");
            return Background(() =>
            {
                string report = BlendShareAsset.MergeIntoFbx(plan.Fbx,
                    plan.Assets.Select(BlendShareAsset.Load).ToList(), outFbx);
                int copied = CopyTextures(_meshes.ForProfile(plan.Profile), plan.Fbx,
                    Path.GetDirectoryName(outFbx));
                return report.Replace(Environment.NewLine, "; ").Replace("\n", "; ") +
                       (copied > 0 ? $"; {copied} textures copied" : "");
            }, (report, error) =>
            {
                progress.Dispose();
                if (error != null)
                {
                    AppLog.Error("Avatar FBX export: " + error);
                    _status.Notify(
                        $"{name} could not be written: {error.Message}. Check that the folder can be written and the file is not open in another program.",
                        StatusKind.Error);
                    return false;
                }

                AppLog.Info($"Exported {name}: {report}");
                _status.Show(new Toast
                {
                    Text = $"Exported {name}: {report}", Kind = StatusKind.Success, ActionLabel = "Show in folder",
                    Action = () => FileActions.Reveal(outFbx)
                });
                return true;
            });
        }

        /// <summary>Copies the textures the model uses next to the exported FBX (tools find them by file name). Returns how many.</summary>
        static int CopyTextures(SkinnedAvatar model, string fbx, string toDir)
        {
            if (model == null || !SamePath(model.SourcePath, fbx)) return 0;
            int copied = 0;
            foreach (var texture in model.Textures)
            {
                if (string.IsNullOrEmpty(texture.File) || !File.Exists(texture.File)) continue;
                string to = Path.Combine(toDir, Path.GetFileName(texture.File));
                if (SamePath(to, texture.File)) continue;
                File.Copy(texture.File, to, true);
                copied++;
            }

            return copied;
        }

        static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        // ------------------------------------------------------------------------------------------ plumbing

        /// <summary>
        ///     Runs work on a worker, then <paramref name="done" /> on the UI thread with its result or its error; the task
        ///     completes there too.
        /// </summary>
        Task<TResult> Background<TWork, TResult>(Func<TWork> work, Func<TWork, Exception, TResult> done)
            where TWork : class
        {
            var completion = new TaskCompletionSource<TResult>();
            Task.Run(() =>
            {
                TWork result = null;
                Exception error = null;
                try { result = work(); }
                catch (Exception ex) { error = ex; }

                _ui.Post(() =>
                {
                    try { completion.SetResult(done(result, error)); }
                    catch (Exception ex) { completion.SetException(ex); }
                });
            });
            return completion.Task;
        }

        void RaiseChanged()
        {
            Changed?.Invoke();
        }

        void OnUi(Action action)
        {
            if (_ui.CheckAccess()) action();
            else _ui.Post(action);
        }
    }
}
