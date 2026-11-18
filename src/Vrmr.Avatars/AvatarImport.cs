using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VRMR
{
    /// <summary>
    ///     Adds a base model to the avatar library from the files a Booth avatar ships with: the avatar's .unitypackage (or a
    ///     .zip with it inside, or its .fbx + .fbx.meta, or a .fbx.meta in a Unity project), plus optional face-tracking
    ///     add-ons that ship BlendShare assets (Triturbo's). Packages are read in memory; nothing in them is run, and no path
    ///     from an archive is used as a file name. The output is a new folder in the library: the model FBX (with the
    ///     BlendShare shapes merged in as "Name_FT.fbx" when an add-on targets it), the textures its materials use, and
    ///     "Name.vrmavatar.json" with the humanoid skeleton from the model's .meta (bone mapping and rest pose as Unity
    ///     imports it).
    /// </summary>
    public static class AvatarImport
    {
        /// <summary>Prefabs larger than this are not read when looking for the ones that use the model.</summary>
        const long MaxPrefabBytes = 8L * 1024 * 1024;

        /// <param name="unityProjects">Unity projects to look in for a face-tracking framework the add-on builds on, or null.</param>
        public static Result Import(IEnumerable<string> files, string libraryDir, string name = null,
            Action<string> progress = null, IEnumerable<string> unityProjects = null)
        {
            var res = new Result();

            void Log(string s)
            {
                res.Log.Add(s);
                progress?.Invoke(s);
                AppLog.Info("Avatar import: " + s);
            }

            var models = new List<Model>();
            var bss = new List<Bs>();
            var versions = new List<Version>();
            var texSources = new List<Tuple<UnityPackage, UnityPackage.Item>>();
            var looseTexRoots = new List<string>();

            foreach (string f in files)
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".unitypackage" || ext == ".zip")
                {
                    Log("Reading " + Path.GetFileName(f) + "…");
                    List<UnityPackage> pkgs;
                    try { pkgs = UnityPackage.Open(f); }
                    catch (Exception ex)
                    {
                        Log($"{Path.GetFileName(f)}: not readable ({ex.Message})");
                        continue;
                    }

                    if (pkgs.Count == 0)
                        Log($"{Path.GetFileName(f)}: no Unity package inside" +
                            (ext == ".zip" ? " (extract nested archives first)" : ""));
                    foreach (var pkg in pkgs)
                    {
                        var fbxItems = pkg.WithExt(".fbx").Where(i => i.Meta != null && IsHumanoidMeta(i.MetaText))
                            .ToList();
                        var assets = pkg.WithExt(".asset")
                            .Where(i => i.AssetSize > 0 && i.AssetSize < 512L * 1024 * 1024).ToList();
                        var data = pkg.Read(fbxItems.Select(i => i.Guid).Concat(assets.Select(i => i.Guid)));
                        foreach (var it in fbxItems)
                            if (data.TryGetValue(it.Guid, out byte[] b))
                                models.Add(new Model
                                {
                                    Name = it.Stem, Origin = pkg.Source + " > " + it.Path, UnityPath = it.Path,
                                    Guid = it.Guid, Fbx = b, Meta = it.MetaText, HumanBones = HumanCount(it.MetaText),
                                    Pkg = pkg, Item = it
                                });
                        foreach (var it in assets)
                        {
                            if (!data.TryGetValue(it.Guid, out byte[] b)) continue;
                            string head = Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 8192));
                            if (head.IndexOf("m_DeformerID: +BlendShare", StringComparison.Ordinal) >= 0)
                            {
                                var m = Regex.Match(head, @"m_Original:\s*\{[^}]*guid:\s*([0-9a-f]{32})");
                                bss.Add(new Bs
                                {
                                    Guid = it.Guid, Origin = pkg.Source + " > " + it.Path,
                                    Original = m.Success ? m.Groups[1].Value : "", Name = it.Stem, Data = b
                                });
                            }
                            else if (head.IndexOf("m_FbxVersionings:", StringComparison.Ordinal) >= 0)
                                versions.AddRange(ParseVersions(Encoding.UTF8.GetString(b)));
                        }

                        texSources.AddRange(pkg.WithExt(".png", ".jpg", ".jpeg", ".tga")
                            .Select(i => Tuple.Create(pkg, i)));
                        Log(
                            $"{UnityPackage.Leaf(pkg.Source)}: {pkg.Items.Count} assets, {fbxItems.Count} humanoid model(s), {bss.Count(b => b.Origin.StartsWith(pkg.Source))} BlendShare asset(s)");
                    }
                }
                else if (f.EndsWith(".fbx.meta", StringComparison.OrdinalIgnoreCase) || ext == ".fbx")
                {
                    string fbx = ext == ".fbx" ? f : f.Substring(0, f.Length - 5), meta = fbx + ".meta";
                    if (!File.Exists(meta))
                    {
                        Log(
                            $"{Path.GetFileName(fbx)}: no .meta next to it. The humanoid bone mapping comes from Unity's .meta: add the avatar's .unitypackage, or the .fbx from a Unity project.");
                        continue;
                    }

                    string mt = File.ReadAllText(meta, Encoding.UTF8);
                    if (!IsHumanoidMeta(mt))
                    {
                        Log($"{Path.GetFileName(fbx)}: not set up as Humanoid in Unity");
                        continue;
                    }

                    models.Add(new Model
                    {
                        Name = Path.GetFileNameWithoutExtension(fbx), Origin = fbx, UnityPath = UnityRel(fbx),
                        Guid = BlendShareAsset.MetaGuid(meta) ?? "", Meta = mt, HumanBones = HumanCount(mt),
                        LooseFbx = fbx
                    });
                    foreach (string bsPath in BlendShareAsset.FindFor(fbx))
                        bss.Add(new Bs
                        {
                            File = bsPath, Origin = bsPath, Original = BlendShareAsset.MetaGuid(meta) ?? "",
                            Name = Path.GetFileNameWithoutExtension(bsPath)
                        });
                    looseTexRoots.Add(Path.GetDirectoryName(Path.GetDirectoryName(fbx)) ?? Path.GetDirectoryName(fbx));
                }
                else
                    Log($"{Path.GetFileName(f)}: not a .unitypackage, .zip, .fbx or .fbx.meta" +
                        (ext == ".rar" || ext == ".7z" ? " (extract it first)" : ""));
            }

            if (models.Count == 0)
            {
                Log("No humanoid avatar model found.");
                return res;
            }

            // ---- the model: the one a face-tracking add-on targets, else the one with the most humanoid bones; the plain name over variants
            var targeted = new HashSet<string>(bss.Select(b => b.Original));
            var model = models.OrderByDescending(m => targeted.Contains(m.Guid)).ThenByDescending(m => m.HumanBones)
                .ThenBy(m => m.Name.Length).ThenByDescending(m => m.Fbx?.Length ?? 0).First();
            foreach (var m in models.Where(m => m != model))
                Log($"Also in the package: {m.Name}.fbx (add it on its own to use it)");
            byte[] fbxBytes = model.Fbx ?? File.ReadAllBytes(model.LooseFbx);
            string sha = Sha256(fbxBytes);

            // ---- face-tracking shapes for exactly this FBX version
            var mine = bss.Where(b => b.Original == model.Guid).ToList();
            var ver = versions.FirstOrDefault(v => v.FbxGuid == model.Guid && v.Sha == sha);
            string versionLabel = ver?.Label ?? "";
            versionLabel = Regex.Replace(versionLabel, Regex.Escape(NiceName(model.Name)), "", RegexOptions.IgnoreCase)
                .Trim(' ', '-', '_');
            if (mine.Count > 1)
            {
                var pick = ver != null ? mine.FirstOrDefault(b => b.Guid == ver.BlendShare) : null;
                if (pick != null)
                {
                    Log($"Face tracking: {pick.Name} (made for {ver.Label}, the FBX's SHA-256 matches)");
                    mine = new List<Bs> { pick };
                }
                else
                {
                    var last = mine.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).Last();
                    Log(
                        $"Face tracking: {mine.Count} BlendShare assets for this model and none lists this FBX version; using {last.Name}");
                    mine = new List<Bs> { last };
                }
            }
            else if (mine.Count == 1)
            {
                var known = versions.Where(v => v.FbxGuid == model.Guid).ToList();
                if (known.Count > 0 && ver == null)
                    Log(
                        $"Warning: this {model.Name}.fbx is not one of the versions the face-tracking add-on supports ({string.Join(", ", known.Select(v => v.Label))}); its shapes may not fit");
                else if (ver != null) Log($"Face tracking: {mine[0].Name} (for {ver.Label}, SHA-256 matches)");
            }

            if (bss.Count > 0 && mine.Count == 0)
                Log("The BlendShare asset(s) found target another model; no face-tracking shapes merged");

            // ---- name, folder
            string nm = !string.IsNullOrEmpty(name) ? name.Trim() : NiceName(model.Name);
            Directory.CreateDirectory(libraryDir);
            string dest = Path.Combine(libraryDir, AvatarLibrary.Slug(nm));
            for (int k = 2; Directory.Exists(dest) || File.Exists(dest); k++)
                dest = Path.Combine(libraryDir, AvatarLibrary.Slug(nm) + " (" + k + ")");
            Directory.CreateDirectory(dest);
            string tmp = Path.Combine(dest, "_import");
            Directory.CreateDirectory(tmp);
            try
            {
                string fbxTmp = Path.Combine(tmp, "model.fbx");
                File.WriteAllBytes(fbxTmp, fbxBytes);
                File.WriteAllText(fbxTmp + ".meta", model.Meta, new UTF8Encoding(false));
                var prof = AvatarProfile.FromUnityMeta(fbxTmp + ".meta");
                prof.Name = nm;
                prof.SourcePath = model.Origin;
                prof.UnityModelPath = model.UnityPath;

                string outFbx;
                if (mine.Count > 0)
                {
                    var assets = new List<BlendShareAsset>();
                    for (int i = 0; i < mine.Count; i++)
                    {
                        string p = mine[i].File;
                        if (p == null)
                        {
                            p = Path.Combine(tmp, "blendshare_" + i + ".asset");
                            File.WriteAllBytes(p, mine[i].Data);
                        }

                        assets.Add(BlendShareAsset.Load(p));
                    }

                    outFbx = Path.Combine(dest, AvatarLibrary.Slug(nm) + "_FT.fbx");
                    Log("Merging face-tracking shapes: " +
                        BlendShareAsset.MergeIntoFbx(fbxTmp, assets, outFbx).Replace("\n", "; "));
                }
                else
                {
                    outFbx = Path.Combine(dest, AvatarLibrary.Slug(nm) + ".fbx");
                    File.WriteAllBytes(outFbx, fbxBytes);
                }

                // ---- textures the model's materials use
                int copied = 0;
                string texDir = Path.Combine(dest, "textures");
                var wanted = TextureNames(fbxTmp);
                var fromPkg = new List<Tuple<UnityPackage, UnityPackage.Item>>();
                foreach (var w in wanted)
                {
                    // the same file name can be a menu icon too (one test avatar: Animation/UI Image/Hair.png, 0.3 MB, next to Textures/Hair.png, 8.6 MB):
                    // texture folders first, never UI images / icons, then the largest file
                    var hit = texSources
                                  .Where(t => t.Item2.FileName.Equals(w.Item1, StringComparison.OrdinalIgnoreCase))
                                  .OrderBy(t => TexRank(t.Item2.Path)).ThenByDescending(t => t.Item2.AssetSize)
                                  .FirstOrDefault()
                              ?? texSources.Where(t => t.Item2.Stem.Equals(w.Item2, StringComparison.OrdinalIgnoreCase))
                                  .OrderBy(t => TexRank(t.Item2.Path)).ThenByDescending(t => t.Item2.AssetSize)
                                  .FirstOrDefault();
                    if (hit != null && !fromPkg.Contains(hit)) fromPkg.Add(hit);
                    else if (hit == null)
                        foreach (string root in looseTexRoots)
                        {
                            string found = FindFile(root, w.Item1);
                            if (found == null) continue;
                            Directory.CreateDirectory(texDir);
                            string to = Path.Combine(texDir, Path.GetFileName(found));
                            if (!File.Exists(to))
                            {
                                File.Copy(found, to);
                                copied++;
                            }

                            break;
                        }
                }

                foreach (var g in fromPkg.GroupBy(t => t.Item1))
                {
                    var data = g.Key.Read(g.Select(t => t.Item2.Guid));
                    foreach (var t in g)
                    {
                        if (!data.TryGetValue(t.Item2.Guid, out byte[] b)) continue;
                        Directory.CreateDirectory(texDir);
                        string to = Path.Combine(texDir, SafeName(t.Item2.FileName));
                        if (!File.Exists(to))
                        {
                            File.WriteAllBytes(to, b);
                            copied++;
                        }
                    }
                }

                Log($"Textures: {copied} of {wanted.Count} material texture(s) copied");

                // ---- library entry
                var e = new AvatarEntry
                {
                    Name = nm, Version = versionLabel, Fbx = Path.GetFileName(outFbx), Source = model.Origin,
                    Skeleton = prof,
                    Notes = "Unity import: " + ImportNote(model.Meta) + (mine.Count > 0
                        ? "; face tracking: " + string.Join(", ", mine.Select(b => b.Name))
                        : "")
                };
                e.Id = AvatarLibrary.Slug(nm);
                e.Aliases = AvatarAliases.From(nm, model.Name, PackageNames(model));
                if (e.Aliases.Count > 2)
                    Log("Also known as: " + string.Join(", ", e.Aliases.Skip(2)) +
                        " (names from the package; edit them in the library)");
                e.FaceMesh = FaceMeshFinder.InFbx(outFbx, prof) ?? "";
                if (e.FaceMesh.Length > 0) Log("Face mesh: " + e.FaceMesh);
                e.FilePath = Path.Combine(dest, AvatarLibrary.Slug(nm) + ".vrmavatar.json");
                File.WriteAllText(e.FilePath, Json.Serialize(e.ToJson(), true), Encoding.UTF8);
                Log(
                    $"Added {e.Label}: {prof.Human.Count(h => h >= 0)} humanoid bones, eye height {prof.EyeHeight:0.000} m, {Path.GetFileName(outFbx)}");
                res.Entry = e;
                // the face-tracking add-on's animator, so takes play its own face shapes as VRChat does
                if (mine.Count > 0)
                {
                    try
                    {
                        var anim = FaceAnimatorFiles.Gather(
                            files.Where(f =>
                                !f.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) &&
                                !f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)), unityProjects);
                        if (anim.Roots.Count > 0)
                        {
                            FaceAnimatorFiles.Save(anim, dest);
                            Log("Face-tracking animator: " + anim.Log.Last());
                            foreach (string line in anim.Log.Where(l => l.Contains("missing"))) Log("Warning: " + line);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("Warning: the face-tracking animator could not be read: " + ex.Message);
                    }
                }
            }
            finally
            {
                try { Directory.Delete(tmp, true); }
                catch { }
            }

            return res;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        ///     The names the package itself gives the model: the prefabs that use it (they refer to the model's GUID, which
        ///     accessories and other avatars in the same package do not) and the package's file name. These are where a
        ///     model's name in another script comes from; <see cref="AvatarAliases" /> decides which of them become aliases.
        /// </summary>
        static List<string> PackageNames(Model model)
        {
            var names = new List<string>();
            try
            {
                if (model.Pkg != null)
                {
                    // "archive.zip > folder/name.unitypackage": both file names
                    foreach (string part in model.Pkg.Source.Split('>'))
                        names.Add(Path.GetFileNameWithoutExtension(UnityPackage.Leaf(part.Trim())));
                    if (model.Guid.Length > 0)
                    {
                        var prefabs = model.Pkg.WithExt(".prefab")
                            .Where(i => i.AssetSize > 0 && i.AssetSize <= MaxPrefabBytes).ToList();
                        var data = model.Pkg.Read(prefabs.Select(i => i.Guid));
                        foreach (var p in prefabs)
                            if (data.TryGetValue(p.Guid, out byte[] bytes) && UsesGuid(bytes, model.Guid))
                                names.Add(p.Stem);
                    }
                }
                else if (model.LooseFbx != null && model.Guid.Length > 0)
                {
                    string root = Path.GetDirectoryName(Path.GetDirectoryName(model.LooseFbx)) ??
                                  Path.GetDirectoryName(model.LooseFbx);
                    foreach (string f in Directory.EnumerateFiles(root, "*.prefab", SearchOption.AllDirectories)
                                 .Take(500))
                        if (new FileInfo(f).Length <= MaxPrefabBytes && UsesGuid(File.ReadAllBytes(f), model.Guid))
                            names.Add(Path.GetFileNameWithoutExtension(f));
                }
            }
            catch (Exception ex) { AppLog.Warn("Avatar import names: " + ex.Message); }

            return names;
        }

        /// <summary>Whether a Unity text asset refers to an asset by its GUID.</summary>
        static bool UsesGuid(byte[] asset, string guid)
        {
            return Encoding.ASCII.GetString(asset).IndexOf("guid: " + guid, StringComparison.Ordinal) >= 0;
        }

        static bool IsHumanoidMeta(string meta)
        {
            return meta.IndexOf("humanDescription:", StringComparison.Ordinal) >= 0 &&
                   meta.IndexOf("humanName: Hips", StringComparison.Ordinal) >= 0;
        }

        static int HumanCount(string meta)
        {
            return Regex.Matches(meta, @"- boneName:").Count;
        }

        static string UnityRel(string path)
        {
            string p = path.Replace('\\', '/');
            int i = p.IndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
            return i >= 0 ? p.Substring(i + 1) : "";
        }

        static string NiceName(string n)
        {
            n = n.Trim();
            if (n.Length > 3 && n == n.ToUpperInvariant() && n.Any(char.IsLetter))
                n = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(n.ToLowerInvariant());
            return n;
        }

        static string Sha256(byte[] b)
        {
            using (var h = SHA256.Create()) return string.Concat(h.ComputeHash(b).Select(x => x.ToString("x2")));
        }

        static int TexRank(string p)
        {
            string l = p.ToLowerInvariant();
            int r = Plain(p) ? 0 : 2;
            if (l.Contains("/ui ") || l.Contains("/ui/") || l.Contains("ui image") || l.Contains("icon") ||
                l.Contains("thumbnail") || l.Contains("/menu") || l.Contains("/image/")) r += 4;
            if (!l.Contains("/texture")) r += 1;
            return r;
        }

        static bool Plain(string p)
        {
            return p.IndexOf("Mask", StringComparison.OrdinalIgnoreCase) < 0 &&
                   p.IndexOf("Another", StringComparison.OrdinalIgnoreCase) < 0 &&
                   p.IndexOf("UV", StringComparison.Ordinal) < 0;
        }

        static string SafeName(string f)
        {
            var sb = new StringBuilder();
            foreach (char c in f) sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
            return sb.ToString();
        }

        static string FindFile(string root, string file)
        {
            try
            {
                return Directory.GetFiles(root, file, SearchOption.AllDirectories).OrderBy(x => Plain(x) ? 0 : 1)
                    .ThenBy(x => x.Length).FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>(texture file name, material name) for every material of the FBX.</summary>
        static List<Tuple<string, string>> TextureNames(string fbxPath)
        {
            var res = new List<Tuple<string, string>>();
            try
            {
                var top = FbxReader.Read(fbxPath);
                var objs = top.First(n => n.Name == "Objects");
                var byId = objs.Children.ToDictionary(c => c.Id(), c => c);
                var tex = new Dictionary<long, string>();
                foreach (var c in top.First(n => n.Name == "Connections").Children)
                {
                    if (c.Props.Count < 3 || !(c.Props[1] is long ch) || !(c.Props[2] is long pa)) continue;
                    if (!byId.TryGetValue(ch, out var cn) || !byId.TryGetValue(pa, out var pn)) continue;
                    if (cn.Name == "Texture" && pn.Name == "Material")
                    {
                        string file = cn.Child("RelativeFilename")?.Props[0] as string ??
                                      cn.Child("FileName")?.Props[0] as string ?? "";
                        tex[pa] = file.Replace('\\', '/').Split('/').Last();
                    }
                }

                foreach (var m in objs.ChildrenNamed("Material"))
                {
                    string file = tex.TryGetValue(m.Id(), out string f) ? f : "";
                    var t = Tuple.Create(file, m.ObjName());
                    if (!res.Any(x => x.Item1 == t.Item1 && x.Item2 == t.Item2)) res.Add(t);
                }
            }
            catch (Exception ex) { AppLog.Warn("Avatar import textures: " + ex.Message); }

            return res;
        }

        static string ImportNote(string meta)
        {
            string G(string k)
            {
                var m = Regex.Match(meta, @"\n\s*" + k + @":\s*(.*)");
                return m.Success ? m.Groups[1].Value.Trim() : "?";
            }

            return $"globalScale {G("globalScale")}, useFileScale {G("useFileScale")}";
        }

        static IEnumerable<Version> ParseVersions(string yaml)
        {
            var res = new List<Version>();
            string fbx = "";
            Version cur = null;
            foreach (string raw in yaml.Split('\n'))
            {
                string t = raw.Trim();
                var mf = Regex.Match(t, @"^- m_Fbx:\s*\{[^}]*guid:\s*([0-9a-f]{32})");
                if (mf.Success)
                {
                    fbx = mf.Groups[1].Value;
                    continue;
                }

                if (t.StartsWith("- m_Version:"))
                {
                    cur = new Version { FbxGuid = fbx, Label = t.Substring(12).Trim().Trim('\'', '"') };
                    res.Add(cur);
                    continue;
                }

                if (cur == null) continue;
                if (t.StartsWith("m_Sha256:"))
                    cur.Sha = t.Substring(9).Trim().Trim('\'', '"').Trim().ToLowerInvariant();
                var mb = Regex.Match(t, @"^m_BlendShapeData:\s*\{[^}]*guid:\s*([0-9a-f]{32})");
                if (mb.Success) cur.BlendShare = mb.Groups[1].Value;
            }

            return res;
        }

        public sealed class Result
        {
            public readonly List<string> Log = new();
            public AvatarEntry Entry;
            public string Summary => string.Join("\n", Log);
        }

        sealed class Model
        {
            public byte[] Fbx;
            public int HumanBones;
            public UnityPackage.Item Item;
            public string LooseFbx; // a file on disk
            public string Meta = "";
            public string Name = "", Origin = "", UnityPath = "", Guid = "";
            public UnityPackage Pkg;
        }

        sealed class Bs
        {
            public byte[] Data;
            public string File;
            public string Guid = "", Origin = "", Original = "", Name = "";
        }

        sealed class Version
        {
            public string FbxGuid = "", Sha = "", Label = "", BlendShare = "";
        }
    }
}
