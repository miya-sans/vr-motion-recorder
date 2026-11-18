using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace VRMR
{
    /// <summary>
    ///     One base model in the avatar library: its humanoid skeleton (rest pose + bone mapping, as
    ///     Unity imports it), the FBX shown in the preview (the face-tracking FBX when there is one), and how avatars worn in
    ///     VRChat are matched to it (linked avatar ids, aliases). Stored as "&lt;Name&gt;.vrmavatar.json" in the library
    ///     folder; everything the solve needs is in that file, so it works without the Unity project.
    /// </summary>
    public sealed class AvatarEntry
    {
        /// <summary>
        ///     Other names of this base model: an avatar worn in VRChat whose name contains one of them (ignoring case and
        ///     punctuation) is this model. Filled at import from the package's own names (the model file, the prefabs that
        ///     use it, the package), which is where a name in another script comes from; edited by the user.
        /// </summary>
        public List<string> Aliases = new();

        /// <summary>VRChat avatar ids (avtr_...) known to be this base model (set by hand or by a Unity project).</summary>
        public List<string> AvatarIds = new();

        /// <summary>The app's own mannequin: always in the library, not stored in a file, cannot be removed.</summary>
        public bool BuiltIn;

        /// <summary>
        ///     The mesh that carries the face-tracking shapes, as an animation path from the avatar's root ("" = not known
        ///     yet).
        /// </summary>
        public string FaceMesh = "";

        /// <summary>The FBX was already searched for the face mesh in this run (so a model without one is not read again).</summary>
        internal bool FaceMeshSearched;

        /// <summary>Preview / reference FBX (absolute, or relative to the entry file).</summary>
        public string Fbx = "";

        public string FilePath = "";
        public string Id = "", Name = "", Version = "", Notes = "";
        public AvatarProfile Skeleton;

        /// <summary>Where the skeleton came from (package, .fbx.meta).</summary>
        public string Source = "";

        /// <summary>Not stored in the library: made on the fly from a linked Unity project or an older link.</summary>
        public bool Transient;

        /// <summary>The same list under its earlier name.</summary>
        public List<string> Match { get => Aliases; set => Aliases = value ?? new List<string>(); }

        public float EyeHeight => Skeleton?.EyeHeight ?? 0;
        public string Label => Name + (Version.Length > 0 ? " " + Version : "");

        public string FbxPath
        {
            get
            {
                if (string.IsNullOrEmpty(Fbx)) return "";
                if (Path.IsPathRooted(Fbx) || string.IsNullOrEmpty(FilePath)) return Fbx;
                return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(FilePath), Fbx));
            }
        }

        public override string ToString()
        {
            return Label;
        }

        /// <summary>A copy of the skeleton tagged with this entry (library id, preview FBX).</summary>
        public AvatarProfile Profile()
        {
            var p = Skeleton.Clone();
            p.LibraryId = Id;
            p.Name = Name;
            p.MeshFbx = FbxPath;
            return p;
        }

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                ["vrmrAvatar"] = 1, ["id"] = Id, ["name"] = Name, ["version"] = Version, ["notes"] = Notes,
                ["fbx"] = Fbx, ["source"] = Source,
                ["aliases"] = Aliases.Cast<object>().ToList(),
                // also under the key builds from before "aliases" read, so a library shared with one keeps its names
                ["match"] = Aliases.Cast<object>().ToList(),
                ["avatarIds"] = AvatarIds.Cast<object>().ToList(), ["faceMesh"] = FaceMesh,
                ["eyeHeight"] = (double)EyeHeight, ["skeleton"] = Skeleton.ToJson()
            };
        }

        public static AvatarEntry FromJson(Dictionary<string, object> d, string file)
        {
            var e = new AvatarEntry
            {
                Id = d.GetStr("id", ""), Name = d.GetStr("name", ""), Version = d.GetStr("version", ""),
                Notes = d.GetStr("notes", ""),
                Fbx = d.GetStr("fbx", ""), Source = d.GetStr("source", ""), FilePath = file,
                FaceMesh = d.GetStr("faceMesh", ""),
                Aliases = ReadAliases(d),
                AvatarIds = (d.GetList("avatarIds") ?? new List<object>()).OfType<string>()
                    .Select(x => x.ToLowerInvariant()).ToList(),
                Skeleton = AvatarProfile.FromJson(d.GetObj("skeleton"))
            };
            if (e.Id.Length == 0) e.Id = AvatarLibrary.Slug(e.Name);
            if (e.Name.Length == 0) e.Name = e.Skeleton.Name;
            return e;
        }

        /// <summary>"aliases", plus what an older file (or a build from before aliases) keeps under "match".</summary>
        static List<string> ReadAliases(Dictionary<string, object> d)
        {
            var list = (d.GetList("aliases") ?? new List<object>()).OfType<string>().ToList();
            foreach (string m in (d.GetList("match") ?? new List<object>()).OfType<string>())
                if (!list.Contains(m))
                    list.Add(m);
            return list;
        }

        /// <summary>The built-in mannequin as a library entry.</summary>
        internal static AvatarEntry Mannequin()
        {
            return new AvatarEntry
            {
                Id = VRMR.Mannequin.Id, Name = VRMR.Mannequin.Name, Source = VRMR.Mannequin.Source,
                Skeleton = VRMR.Mannequin.Profile(), BuiltIn = true,
                Notes = "Built in: a plain humanoid for takes whose avatar is not in the library."
            };
        }
    }

    /// <summary>How the avatar worn in VRChat was matched to a base model.</summary>
    public sealed class AvatarMatch
    {
        public AvatarEntry Entry;
        public string How = "";

        /// <summary>False for a guess (eye height only, or the default avatar): the UI asks to confirm.</summary>
        public bool Sure;

        public override string ToString()
        {
            return Entry == null ? "none" : $"{Entry.Label} ({How})";
        }
    }

    /// <summary>
    ///     The avatar library: base models the app can solve onto, plus how VRChat avatars are matched to them. VRChat drives
    ///     any humanoid, and every take keeps its raw data, so a take can be solved onto any entry (see PoseBuilder): the one
    ///     matched to the avatar worn, or one picked by hand.
    ///     Matching, in order: an avatar id linked to an entry; a linked Unity project (blueprint id -> model .meta); the
    ///     avatar's name (from VRChat's OSCQuery, its log, or its OSC config file) containing an entry's name or alias; the
    ///     avatar's natural eye height (within 3 %); else the default entry, which is the built-in mannequin when the
    ///     user has no avatar of their own.
    /// </summary>
    public sealed class AvatarLibrary
    {
        public readonly List<AvatarEntry> Entries = new();
        readonly Dictionary<string, AvatarProfile> _cache = new(StringComparer.OrdinalIgnoreCase);
        readonly Settings _s;
        readonly Dictionary<string, AvatarEntry> _transient = new(StringComparer.OrdinalIgnoreCase);
        public string LastScanInfo = "";
        public volatile bool Scanning;

        public AvatarLibrary(Settings s)
        {
            _s = s;
            Reload();
            LoadExportedProfiles();
        }

        /// <summary>Skeletons the Unity bridge exported (see <see cref="AppPaths.UnityProfilesDir" />).</summary>
        public string ProfileDir => _s.Paths.UnityProfilesDir;

        public string Dir => _s.AvatarLibraryDir;

        /// <summary>The app's own mannequin: always there, used when nothing else matches.</summary>
        public AvatarEntry BuiltIn { get; } = AvatarEntry.Mannequin();

        public AvatarEntry Default
        {
            get
            {
                // the one set in Settings, else the one with the most linked VRChat avatars (the one worn most), else the
                // first; with no avatar of the user's own, the built-in mannequin
                if (BuiltIn.Id.Equals(_s.DefaultAvatar ?? "", StringComparison.OrdinalIgnoreCase)) return BuiltIn;
                lock (Entries)
                    return Entries.FirstOrDefault(e =>
                               e.Id.Equals(_s.DefaultAvatar ?? "", StringComparison.OrdinalIgnoreCase))
                           ?? Entries.OrderByDescending(e => e.AvatarIds.Count).FirstOrDefault()
                           ?? BuiltIn;
            }
        }

        public event Action Updated;

        // ------------------------------------------------------------------ store

        public void Reload()
        {
            lock (Entries)
            {
                Entries.Clear();
                try
                {
                    if (Directory.Exists(Dir))
                        foreach (string f in Directory.GetFiles(Dir, "*.vrmavatar.json", SearchOption.TopDirectoryOnly)
                                     .Concat(Directory.GetDirectories(Dir)
                                         .SelectMany(d => SafeFiles(d, "*.vrmavatar.json"))))
                        {
                            try
                            {
                                var e = AvatarEntry.FromJson(Json.ParseObject(File.ReadAllText(f, Encoding.UTF8)), f);
                                if (e.Skeleton == null || e.Skeleton.Human[HumanBones.Hips] < 0) continue;
                                if (Entries.Any(x => x.Id.Equals(e.Id, StringComparison.OrdinalIgnoreCase)))
                                {
                                    AppLog.Warn(
                                        $"Avatar library: {f} has the same id as another entry ({e.Id}); skipped");
                                    continue;
                                }

                                Entries.Add(e);
                            }
                            catch (Exception ex) { AppLog.Warn("Avatar library: " + f + ": " + ex.Message); }
                        }
                }
                catch (Exception ex) { AppLog.Warn("Avatar library: " + ex.Message); }

                Entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            }
        }

        static IEnumerable<string> SafeFiles(string d, string pat)
        {
            try { return Directory.GetFiles(d, pat); }
            catch { return new string[0]; }
        }

        /// <summary>The user's entries, then the built-in mannequin.</summary>
        public List<AvatarEntry> All()
        {
            var all = Stored();
            all.Add(BuiltIn);
            return all;
        }

        /// <summary>The entries stored in the library folder (without the built-in one).</summary>
        public List<AvatarEntry> Stored()
        {
            lock (Entries) return Entries.ToList();
        }

        public AvatarEntry Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (Entries)
            {
                var e = Entries.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (e != null) return e;
            }

            if (id.Equals(BuiltIn.Id, StringComparison.OrdinalIgnoreCase)) return BuiltIn;
            lock (_transient) return _transient.TryGetValue(id, out var t) ? t : null;
        }

        public void Save(AvatarEntry e)
        {
            if (e.Transient || e.BuiltIn) return;
            if (string.IsNullOrEmpty(e.FilePath))
            {
                Directory.CreateDirectory(Dir);
                e.FilePath = Path.Combine(Dir, Slug(e.Name) + ".vrmavatar.json");
            }

            File.WriteAllText(e.FilePath, Json.Serialize(e.ToJson(), true), Encoding.UTF8);
            lock (Entries)
                if (!Entries.Contains(e))
                {
                    Entries.Add(e);
                    Entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                }

            Updated?.Invoke();
        }

        /// <summary>Removes an entry from the library (its file goes to the Recycle Bin; an imported avatar's folder too).</summary>
        public void Remove(AvatarEntry e)
        {
            if (e == null || e.Transient || e.BuiltIn) return;
            lock (Entries) Entries.Remove(e);
            try
            {
                string dir = Path.GetDirectoryName(e.FilePath);
                bool ownFolder = !string.Equals(Path.GetFullPath(dir).TrimEnd('\\'),
                                     Path.GetFullPath(Dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) &&
                                 Path.GetFullPath(dir).StartsWith(Path.GetFullPath(Dir),
                                     StringComparison.OrdinalIgnoreCase);
                RecycleBin.Delete(ownFolder ? dir : e.FilePath);
            }
            catch (Exception ex) { AppLog.Warn("Avatar library remove: " + ex.Message); }

            Updated?.Invoke();
        }

        /// <summary>From now on, the VRChat avatar with this id is this base model (also used by new takes).</summary>
        public void LinkId(string avatarId, AvatarEntry e)
        {
            if (string.IsNullOrEmpty(avatarId) || e == null) return;
            string id = avatarId.ToLowerInvariant();
            foreach (var o in All().Where(o => o != e && o.AvatarIds.Remove(id))) Save(o);
            if (!e.AvatarIds.Contains(id)) e.AvatarIds.Add(id);
            // entries without a file of their own (a Unity project's model, the built-in mannequin) keep the link in the settings
            if (e.Transient || e.BuiltIn) _s.AvatarModelMap[id] = e.Skeleton.SourcePath;
            else
            {
                _s.AvatarModelMap.Remove(id);
                Save(e);
            }

            _s.Save();
            Updated?.Invoke();
        }

        public static string Slug(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in (name ?? "").Trim())
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            string s = sb.ToString().Trim('_');
            return s.Length == 0 ? "Avatar" : s;
        }

        // ------------------------------------------------------------------ matching

        /// <summary>
        ///     The base model of a VRChat avatar. eyeHeight / scaleFactor: VRChat's EyeHeightAsMeters and ScaleFactor (0 =
        ///     unknown).
        /// </summary>
        public AvatarMatch Match(string avatarId, string avatarName, float eyeHeight = 0, float scaleFactor = 0)
        {
            string id = (avatarId ?? "").ToLowerInvariant();
            var all = Stored(); // the built-in mannequin is never matched by name or size: it is only the last fallback
            if (id.Length > 0)
            {
                var linked = all.FirstOrDefault(e => e.AvatarIds.Contains(id));
                if (linked != null) return new AvatarMatch { Entry = linked, How = "avatar id linked", Sure = true };
                if (_s.AvatarModelMap.TryGetValue(id, out string meta))
                {
                    if (meta == Mannequin.Source)
                        return new AvatarMatch { Entry = BuiltIn, How = "avatar id linked", Sure = true };
                    var fromProject = EntryForModel(meta, all);
                    if (fromProject != null)
                        return new AvatarMatch { Entry = fromProject, How = "Unity project", Sure = true };
                }
            }

            string name = string.IsNullOrEmpty(avatarName) ? OscConfigName(id) : avatarName;
            if (!string.IsNullOrEmpty(name))
            {
                string n = Norm(name);
                AvatarEntry best = null;
                int bestLen = 0;
                string bestPat = "";
                foreach (var e in all)
                foreach (string pat in e.Aliases.Concat(new[] { e.Name }))
                {
                    string p = Norm(pat);
                    if (p.Length < (p.Any(c => c > 0x2E80) ? 2 : 3) ||
                        n.IndexOf(p, StringComparison.Ordinal) < 0) continue;
                    if (p.Length > bestLen)
                    {
                        best = e;
                        bestLen = p.Length;
                        bestPat = pat;
                    }
                }

                if (best != null)
                    return new AvatarMatch
                        { Entry = best, How = $"name \"{name}\" contains \"{bestPat}\"", Sure = true };
            }

            if (eyeHeight > 0.2f)
            {
                float natural = scaleFactor > 0.05f ? eyeHeight / scaleFactor : eyeHeight;
                var near = all.Where(e => e.EyeHeight > 0.2f)
                    .Select(e => Tuple.Create(e, Math.Abs(Math.Log(natural / e.EyeHeight)))).OrderBy(t => t.Item2)
                    .ToList();
                if (near.Count > 0 && near[0].Item2 < 0.03)
                    return new AvatarMatch
                    {
                        Entry = near[0].Item1,
                        How =
                            $"eye height {natural:0.000} m ≈ {near[0].Item1.EyeHeight:0.000} m (a guess: pick the base model to be sure)",
                        Sure = false
                    };
            }

            var def = Default;
            return def.BuiltIn
                ? new AvatarMatch
                {
                    Entry = def,
                    How = "unknown avatar: the built-in mannequin (add your avatar to the library to see it instead)",
                    Sure = false
                }
                : new AvatarMatch
                {
                    Entry = def, How = "unknown avatar: the default base model (pick the base model to change it)",
                    Sure = false
                };
        }

        static string Norm(string s)
        {
            return new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        }

        /// <summary>The library entry for a Unity model (.fbx.meta or .vrmskel.json), else a transient entry made from it.</summary>
        AvatarEntry EntryForModel(string modelPath, List<AvatarEntry> all)
        {
            string full;
            try { full = Path.GetFullPath(modelPath); }
            catch { return null; }

            var e = all.FirstOrDefault(x =>
                string.Equals(SafeFull(x.Skeleton.SourcePath), full, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(SafeFull(x.Source), full, StringComparison.OrdinalIgnoreCase));
            if (e != null) return e;
            lock (_transient)
                if (_transient.TryGetValue(full, out var t))
                    return t;
            var p = LoadModel(modelPath);
            if (p == null) return null;
            string fbx = SkinnedAvatar.FbxFor(p) ?? "";
            var same = all.FirstOrDefault(x => SameSkeleton(x.Skeleton, p));
            if (same != null) return same;
            var te = new AvatarEntry
                { Id = full, Name = p.Name, Skeleton = p, Fbx = fbx, Source = full, Transient = true };
            lock (_transient) _transient[full] = te;
            return te;
        }

        static string SafeFull(string p)
        {
            try { return string.IsNullOrEmpty(p) ? "" : Path.GetFullPath(p); }
            catch { return ""; }
        }

        /// <summary>Same base model: same bone names and humanoid mapping, rest positions within 1 mm.</summary>
        public static bool SameSkeleton(AvatarProfile a, AvatarProfile b)
        {
            if (a == null || b == null || a.Nodes.Count != b.Nodes.Count) return false;
            for (int i = 0; i < HumanBones.Count; i++)
            {
                if (a.Human[i] >= 0 != b.Human[i] >= 0) return false;
                if (a.Human[i] < 0) continue;
                if (a.Nodes[a.Human[i]].Name != b.Nodes[b.Human[i]].Name) return false;
                if ((a.HumanRestPos(i) - b.HumanRestPos(i)).Length > 0.001f) return false;
            }

            return true;
        }

        /// <summary>
        ///     VRChat's OSC config of an avatar (OSC\usr_*\Avatars\avtr_*.json in VRChat's data folder, read-only):
        ///     its uploaded name, for avatars VRChat hasn't named over OSCQuery or in the log.
        /// </summary>
        public static string OscConfigName(string avatarId)
        {
            if (string.IsNullOrEmpty(avatarId)) return null;
            try
            {
                string low = VrcPaths.OscDir;
                if (!Directory.Exists(low)) return null;
                foreach (string u in Directory.GetDirectories(low, "usr_*"))
                {
                    string f = Path.Combine(u, "Avatars", avatarId + ".json");
                    if (!File.Exists(f)) continue;
                    var d = Json.ParseObject(File.ReadAllText(f, Encoding.UTF8).TrimStart('﻿'));
                    string n = d?.GetStr("name");
                    if (!string.IsNullOrEmpty(n)) return n;
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        ///     The avatars worn during a take, in order: the one at the start, then a new stretch at every OSC /avatar/change
        ///     to another avatar id ("avatar" events with an avtr_ id; reloads of the same avatar, e.g. "Calibrate FBT", don't
        ///     split). Names come from VRChat's log ("Switching ... to avatar NAME", within a few seconds) or its OSC config.
        /// </summary>
        public static List<AvatarSegment> AvatarSegments(Recording rec)
        {
            var res = new List<AvatarSegment>
                { new() { Start = 0, AvatarId = rec.AvatarId ?? "", AvatarName = rec.AvatarName ?? "" } };
            List<RecEvent> evs;
            lock (rec.Sync) evs = rec.Events.Where(e => e.Type == "avatar").OrderBy(e => e.T).ToList();
            foreach (var e in evs)
            {
                if (string.IsNullOrEmpty(e.Text) ||
                    !e.Text.StartsWith("avtr_", StringComparison.OrdinalIgnoreCase)) continue;
                var cur = res[res.Count - 1];
                if (string.Equals(cur.AvatarId, e.Text, StringComparison.OrdinalIgnoreCase)) continue;
                if (e.T < 0.5 && res.Count == 1)
                {
                    cur.AvatarId = e.Text;
                    continue;
                }

                string nm = evs
                    .Where(x => !x.Text.StartsWith("avtr_", StringComparison.OrdinalIgnoreCase) && x.T >= e.T - 3 &&
                                x.T <= e.T + 6).Select(x => x.Text).FirstOrDefault() ?? OscConfigName(e.Text) ?? "";
                cur.End = e.T;
                res.Add(new AvatarSegment { Start = e.T, AvatarId = e.Text, AvatarName = nm });
            }

            res[res.Count - 1].End = rec.Duration;
            return res;
        }

        /// <summary>The base model of the avatar a take was recorded with (first avatar of the take).</summary>
        public AvatarMatch MatchTake(Recording rec)
        {
            var sf = rec.Param("ScaleFactor");
            var eh = rec.Param("EyeHeightAsMeters");
            float eye = eh != null && eh.Count > 0 ? eh.V[0] : rec.AvatarEyeHeight;
            float scale = sf != null && sf.Count > 0 ? sf.V[0] : 0;
            return Match(rec.AvatarId, rec.AvatarName, eye, scale);
        }

        /// <summary>
        ///     The skeleton a take is solved onto: the one picked for the take (EditList.TargetAvatar), else the avatar it was
        ///     recorded with (its embedded skeleton, or its match in the library).
        /// </summary>
        public AvatarProfile TargetFor(Recording rec, out string how)
        {
            var pick = Get(rec.Edits.TargetAvatar);
            if (pick != null)
            {
                how = "picked for this take";
                return pick.Profile();
            }

            if (!string.IsNullOrEmpty(rec.Edits.TargetAvatar))
                AppLog.Warn(
                    $"Avatar '{rec.Edits.TargetAvatar}' picked for {rec.Name} is not in the library; using the recorded avatar");
            if (rec.Profile != null)
            {
                var lib = Get(rec.Profile.LibraryId);
                how = lib != null ? "the avatar it was recorded with" : "the skeleton saved in the take";
                if (lib != null)
                {
                    var p = lib.Profile();
                    return p;
                }

                return rec.Profile;
            }

            var m = MatchTake(rec);
            how = m.ToString();
            return m.Entry?.Profile();
        }

        // ------------------------------------------------------------------ face mesh

        /// <summary>
        ///     The mesh face shapes are keyed on for a skeleton, as an animation path from the avatar's root: the user's own
        ///     setting when there is one (<paramref name="configured" />), else the face mesh of the skeleton's library entry,
        ///     else the mesh of the skeleton with the most face shapes, else <see cref="FaceMeshFinder.Fallback" />. The
        ///     first call for an entry imported before face meshes were recorded reads its FBX: call it from a worker.
        /// </summary>
        public string FaceMeshFor(AvatarProfile profile, string configured = null)
        {
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
            var entry = profile == null ? null : Get(profile.LibraryId);
            if (entry != null && !entry.BuiltIn)
            {
                lock (entry)
                {
                    if (string.IsNullOrEmpty(entry.FaceMesh) && !entry.FaceMeshSearched)
                    {
                        entry.FaceMeshSearched = true;
                        entry.FaceMesh = FaceMeshFinder.InFbx(entry.FbxPath, entry.Skeleton) ?? "";
                    }

                    if (!string.IsNullOrEmpty(entry.FaceMesh)) return entry.FaceMesh;
                }
            }

            return FaceMeshFinder.InProfile(profile) ?? FaceMeshFinder.Fallback;
        }

        // ------------------------------------------------------------------ Unity projects (older path, still supported)

        /// <summary>Skeletons exported from Unity by the VR Motion Bridge script (include blendshape names).</summary>
        void LoadExportedProfiles()
        {
            try
            {
                if (!Directory.Exists(ProfileDir)) return;
                foreach (string f in Directory.GetFiles(ProfileDir, "*.vrmskel.json"))
                {
                    var p = LoadModel(f);
                    if (p == null) continue;
                    foreach (string id in p.AvatarIds) _s.AvatarModelMap[id.ToLowerInvariant()] = f;
                }
            }
            catch (Exception ex) { AppLog.Warn("Profiles: " + ex.Message); }
        }

        public AvatarProfile LoadModel(string metaPath)
        {
            if (string.IsNullOrEmpty(metaPath)) return null;
            if (metaPath.EndsWith(".vrmskel.json", StringComparison.OrdinalIgnoreCase))
            {
                lock (_cache)
                {
                    if (_cache.TryGetValue(metaPath, out var jp)) return jp;
                    try
                    {
                        jp = AvatarProfile.FromJson(Json.ParseObject(File.ReadAllText(metaPath)));
                        jp.SourcePath = metaPath;
                        _cache[metaPath] = jp;
                        return jp;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn("Profile " + metaPath + ": " + ex.Message);
                        return null;
                    }
                }
            }

            if (!metaPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) metaPath += ".meta";
            lock (_cache)
            {
                if (_cache.TryGetValue(metaPath, out var p)) return p;
                try
                {
                    p = AvatarProfile.FromUnityMeta(metaPath);
                    _cache[metaPath] = p;
                    return p;
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Avatar profile " + metaPath + ": " + ex.Message);
                    return null;
                }
            }
        }

        /// <summary>
        ///     The base model's skeleton for a VRChat avatar (live view, new takes). Never null: the last fallback is the
        ///     built-in mannequin.
        /// </summary>
        public AvatarProfile ForAvatar(string avatarId, string avatarName = null, float eye = 0, float scaleFactor = 0)
        {
            var m = Match(avatarId, avatarName, eye, scaleFactor);
            if (m.Entry == null) return null;
            var p = m.Entry.Profile();
            if (!string.IsNullOrEmpty(avatarId) && !p.AvatarIds.Contains(avatarId)) p.AvatarIds.Add(avatarId);
            return p;
        }

        /// <summary>Older API: link an avatar id to a model .meta / skeleton json (the Unity-project way).</summary>
        public void Link(string avatarId, string modelMetaPath)
        {
            if (string.IsNullOrEmpty(avatarId)) return;
            if (!modelMetaPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) &&
                !modelMetaPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) modelMetaPath += ".meta";
            var e = EntryForModel(modelMetaPath, All());
            if (e != null && !e.Transient)
            {
                LinkId(avatarId, e);
                return;
            }

            _s.AvatarModelMap[avatarId.ToLowerInvariant()] = modelMetaPath;
            _s.Save();
            Updated?.Invoke();
        }

        public void ScanAsync()
        {
            if (Scanning) return;
            Scanning = true;
            var projects = _s.UnityProjects.ToList();
            new Thread(() =>
            {
                int found = 0;
                try
                {
                    foreach (string proj in projects)
                    {
                        foreach (var r in UnityProjectScanner.Scan(proj, msg => LastScanInfo = msg))
                        {
                            bool hasJson = _s.AvatarModelMap.TryGetValue(r.AvatarId, out string cur) &&
                                           cur.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                                           File.Exists(cur);
                            if (!hasJson && (!_s.AvatarModelMap.ContainsKey(r.AvatarId) ||
                                             !File.Exists(_s.AvatarModelMap[r.AvatarId])))
                            {
                                _s.AvatarModelMap[r.AvatarId] = r.ModelMetaPath;
                            }

                            found++;
                            AppLog.Info(
                                $"Avatar {r.AvatarId} -> {Path.GetFileName(r.ModelMetaPath.Replace(".meta", ""))} ({r.DisplayName})");
                        }
                    }

                    _s.Save();
                    LastScanInfo = $"Found {found} avatar(s) in {projects.Count} project(s)";
                }
                catch (Exception ex) { LastScanInfo = "Scan failed: " + ex.Message; }
                finally
                {
                    Scanning = false;
                    Updated?.Invoke();
                }
            }) { IsBackground = true, Name = "Unity scan" }.Start();
        }

        // ------------------------------------------------------------------ takes

        public sealed class AvatarSegment
        {
            public string AvatarId = "", AvatarName = "";
            public double Start, End;
        }
    }
}
