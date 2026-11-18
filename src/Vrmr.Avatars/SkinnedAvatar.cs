using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace VRMR
{
    /// <summary>A decoded image: 32-bit pixels as 0xAARRGGBB with straight (not premultiplied) alpha, top row first.</summary>
    public sealed class TexturePixels
    {
        public int[] Argb;
        public int Width, Height;
    }

    /// <summary>Decodes an image file, scaled down so that neither side is longer than maxSize. Throws when it cannot.</summary>
    public delegate TexturePixels TextureDecoder(string path, int maxSize);

    /// <summary>
    ///     The avatar's own meshes from its FBX (skin weights, UVs, textures, a few face shapes), skinned on the CPU onto
    ///     the app's skeleton for the 3D preview. Bind poses come from the FBX clusters and are converted to Unity space the
    ///     same way Unity's importer does (right- to left-handed by mirroring X, file units to meters), then matched to the
    ///     avatar profile's rest skeleton.
    /// </summary>
    public sealed class SkinnedAvatar
    {
        /// <summary>Longest side of a loaded texture; larger images are scaled down by the decoder.</summary>
        public const int MaxTextureSize = 1024;

        // Common shape names on avatars without face-tracking shapes (VRChat visemes / Japanese avatar conventions)
        static readonly Dictionary<string, string[]> PreviewAliases = new()
        {
            ["EyeClosedLeft"] = new[] { "eye_blink_1_L", "Blink_L", "blink_L", "eye_close_L", "まばたき左" },
            ["EyeClosedRight"] = new[] { "eye_blink_1_R", "Blink_R", "blink_R", "eye_close_R", "まばたき右" },
            ["JawOpen"] = new[] { "vrc.v_aa", "mouth_A_1", "mouth_a", "あ" },
            ["MouthSmileLeft"] = new[] { "mouth_smile_1_L", "Smile_L", "smile_L" },
            ["MouthSmileRight"] = new[] { "mouth_smile_1_R", "Smile_R", "smile_R" }
        };

        // clips baked with ARKit naming: look the preview shapes up by their ARKit names too
        static readonly Dictionary<string, string> ArkitOf = new()
        {
            ["EyeClosedLeft"] = "eyeBlinkLeft", ["EyeClosedRight"] = "eyeBlinkRight", ["JawOpen"] = "jawOpen",
            ["MouthSmileLeft"] = "mouthSmileLeft", ["MouthSmileRight"] = "mouthSmileRight"
        };


        /// <summary>Recording face shape (Unified Expressions) -> mesh shape name, for the shapes this mesh has.</summary>
        public readonly Dictionary<string, string> FaceShapes = new();

        public readonly List<string> Notes = new();

        public readonly List<Part> Parts = new();
        public readonly List<Texture> Textures = new();
        public Vec3[] BindPos;
        public Quat[] BindRot;
        public bool[] IsBound; // per profile node
        public AvatarProfile Profile;
        public string SourcePath = "";

        // FBX global axes -> canonical Y-up right-handed -> Unity (mirror X) in meters
        double[] _axis = { 1, 0, 0, 0, 1, 0, 0, 0, 1 }; // row-major 3x3: canonical = A * fbx
        Vec3 _fitOffset;
        float _fitScale = 1;

        // ------------------------------------------------------------------ skinning

        float[] _m; // per node 3x4
        double _unit = 0.01;
        public int TriangleCount => Parts.Sum(p => p.Tri.Length / 3);

        /// <summary>The other name a recording may use for a preview face shape (ARKit naming), or null.</summary>
        public static string FaceAlias(string faceShape)
        {
            return faceShape != null && ArkitOf.TryGetValue(faceShape, out string a) ? a : null;
        }

        /// <summary>The profile's preview FBX (library entry), else the FBX next to its .fbx.meta, or null.</summary>
        public static string FbxFor(AvatarProfile p)
        {
            if (p != null && !string.IsNullOrEmpty(p.MeshFbx) && File.Exists(p.MeshFbx)) return p.MeshFbx;
            if (p == null || string.IsNullOrEmpty(p.SourcePath)) return null;
            string s = p.SourcePath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                ? p.SourcePath.Substring(0, p.SourcePath.Length - 5)
                : p.SourcePath;
            return s.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) && File.Exists(s) ? s : null;
        }

        Vec3d Canon(Vec3d p)
        {
            return new Vec3d(_axis[0] * p.x + _axis[1] * p.y + _axis[2] * p.z,
                _axis[3] * p.x + _axis[4] * p.y + _axis[5] * p.z, _axis[6] * p.x + _axis[7] * p.y + _axis[8] * p.z);
        }

        Vec3 ToUnityPoint(Vec3d p)
        {
            var c = Canon(p);
            return new Vec3((float)(-c.x * _unit), (float)(c.y * _unit), (float)(c.z * _unit)) * _fitScale + _fitOffset;
        }

        Vec3 ToUnityDir(Vec3d p, bool scale)
        {
            var c = Canon(p);
            double k = scale ? _unit * _fitScale : 1;
            return new Vec3((float)(-c.x * k), (float)(c.y * k), (float)(c.z * k));
        }

        Quat ToUnityRot(M4 m)
        {
            // rotation part (columns = local axes in FBX global), converted axis by axis
            var x = ToUnityDir(m.Col(0).Unit, false);
            var y = ToUnityDir(m.Col(1).Unit, false);
            var z = ToUnityDir(m.Col(2).Unit, false);
            // the mirror flips handedness: Unity's local X axis is the negated mirrored FBX X axis
            x = x * -1f;
            return Quat.FromMatrix(x.x, y.x, z.x, x.y, y.y, z.y, x.z, y.z, z.z).Normalized;
        }

        public static SkinnedAvatar Load(string fbxPath, AvatarProfile prof, TextureDecoder decoder = null)
        {
            var sw = Stopwatch.StartNew();
            var top = FbxReader.Read(fbxPath);
            var m = new SkinnedAvatar { SourcePath = fbxPath, Profile = prof };
            var gs = top.FirstOrDefault(n => n.Name == "GlobalSettings");
            int up = (int)(gs?.P70("UpAxis")?[0] ?? 1), upSign = (int)(gs?.P70("UpAxisSign")?[0] ?? 1);
            int front = (int)(gs?.P70("FrontAxis")?[0] ?? 2), frontSign = (int)(gs?.P70("FrontAxisSign")?[0] ?? 1);
            int coord = (int)(gs?.P70("CoordAxis")?[0] ?? 0), coordSign = (int)(gs?.P70("CoordAxisSign")?[0] ?? 1);
            m._unit = (gs?.P70("UnitScaleFactor")?[0] ?? 1) * 0.01;
            // canonical X = coord axis, Y = up axis, Z = front axis (FBX's "front" is +Z for Y-up right-handed files)
            double[] A = new double[9];
            A[0 * 3 + coord] = coordSign;
            A[1 * 3 + up] = upSign;
            A[2 * 3 + front] = frontSign;
            m._axis = A;

            var objs = top.First(n => n.Name == "Objects");
            var byId = new Dictionary<long, FbxNode>();
            foreach (var o in objs.Children) byId[o.Id()] = o;
            var kids = new Dictionary<long, List<FbxNode>>();
            foreach (var c in top.First(n => n.Name == "Connections").Children)
            {
                if (c.Props.Count < 3 || !(c.Props[1] is long ch) || !(c.Props[2] is long pa) ||
                    !byId.TryGetValue(ch, out var cn)) continue;
                if (!kids.TryGetValue(pa, out var l)) kids[pa] = l = new List<FbxNode>();
                l.Add(cn);
            }

            List<FbxNode> Kids(FbxNode n)
            {
                return kids.TryGetValue(n.Id(), out var l) ? l : new List<FbxNode>();
            }

            // bind pose matrices (mesh globals)
            var bindPose = new Dictionary<long, M4>();
            foreach (var pose in objs.ChildrenNamed("Pose"))
            foreach (var pn in pose.ChildrenNamed("PoseNode"))
            {
                var node = pn.Child("Node");
                double[] mat = pn.Child("Matrix")?.Doubles();
                if (node != null && node.Props[0] is long nid && mat != null && mat.Length == 16 &&
                    !bindPose.ContainsKey(nid)) bindPose[nid] = new M4(mat);
            }

            int nodeCount = prof.Nodes.Count;
            var nodeByName = new Dictionary<string, int>();
            for (int i = 0; i < nodeCount; i++)
                if (!nodeByName.ContainsKey(prof.Nodes[i].Name))
                    nodeByName[prof.Nodes[i].Name] = i;
            var bindF = new Dictionary<int, M4>(); // profile node -> FBX bone global at bind

            // Only humanoid bones move in the preview. Every other bone (hair, ribbons, accessories, toes the rig doesn't
            // map) rides rigidly on its nearest humanoid ancestor in the FBX's own bind pose: right at rest by construction,
            // even where the model's .meta skeleton is stale (one test avatar: ribbon bones missing from it, side-hair bones at up to
            // 175° from their bind; another's choker / ribbons 80°). Bones missing from the skeleton resolve through the
            // FBX's own hierarchy.
            int[] rigid = new int[nodeCount];
            for (int i = 0; i < nodeCount; i++)
            {
                int a = i;
                while (a >= 0 && prof.HumanOfNode(a) < 0 && prof.Nodes[a].Parent >= 0) a = prof.Nodes[a].Parent;
                rigid[i] = a >= 0 ? a : i;
            }

            var modelById = objs.ChildrenNamed("Model").ToDictionary(x => x.Id(), x => x);
            var modelParent = new Dictionary<long, long>();
            foreach (var c in top.First(n => n.Name == "Connections").Children)
                if (c.Props.Count >= 3 && c.Props[1] is long ch && c.Props[2] is long pa && modelById.ContainsKey(ch) &&
                    modelById.ContainsKey(pa))
                    modelParent[ch] = pa;
            var fbxIdByNode = new Dictionary<int, long>();
            foreach (var mm in modelById.Values)
                if (nodeByName.TryGetValue(mm.ObjName(), out int nn) && !fbxIdByNode.ContainsKey(nn))
                    fbxIdByNode[nn] = mm.Id();

            int NodeOf(FbxNode bone)
            {
                for (var b = bone;
                     b != null;
                     b = modelParent.TryGetValue(b.Id(), out long pid) && modelById.TryGetValue(pid, out var pm)
                         ? pm
                         : null)
                    if (nodeByName.TryGetValue(b.ObjName(), out int ni))
                        return rigid[ni];
                return -1;
            }

            // bind matrices of every bone that has a cluster in any mesh, then the pose of the rest (rigid targets)
            foreach (var cl in objs.ChildrenNamed("Deformer").Where(d => d.ObjClass() == "Cluster"))
            {
                var bone = Kids(cl).FirstOrDefault(k => k.Name == "Model");
                double[] tl = cl.Child("TransformLink")?.Doubles();
                if (bone != null && tl != null && tl.Length == 16 &&
                    nodeByName.TryGetValue(bone.ObjName(), out int ni) &&
                    !bindF.ContainsKey(ni)) bindF[ni] = new M4(tl);
            }

            foreach (int t in rigid.Distinct())
                if (!bindF.ContainsKey(t) && fbxIdByNode.TryGetValue(t, out long fid) &&
                    bindPose.TryGetValue(fid, out var bpm))
                    bindF[t] = bpm;

            // ---- textures (one per FBX material)
            // textures: next to the FBX when it has its own (library entries, exported copies), else the model's package folder
            string fbxDir = Path.GetDirectoryName(fbxPath);
            bool own = false;
            try
            {
                own = Directory.EnumerateFiles(fbxDir, "*.*", SearchOption.AllDirectories).Any(f =>
                    f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".tga", StringComparison.OrdinalIgnoreCase));
            }
            catch { }

            string searchRoot = own ? fbxDir : Path.GetDirectoryName(fbxDir) ?? fbxDir;
            var texIndex = new Dictionary<long, int>();
            bool noDecoder = false;

            int TextureFor(FbxNode mat)
            {
                if (texIndex.TryGetValue(mat.Id(), out int ti)) return ti;
                var t = new Texture { Name = mat.ObjName() };
                var tex = Kids(mat).FirstOrDefault(k => k.Name == "Texture");
                string file = tex?.Child("RelativeFilename")?.Props[0] as string ??
                              tex?.Child("FileName")?.Props[0] as string ?? "";
                string found = FindTexture(searchRoot, Path.GetFileName(file.Replace('\\', '/').Split('/').Last()),
                    t.Name);
                if (found != null && decoder == null)
                {
                    t.File = found;
                    noDecoder = true;
                }
                else if (found != null)
                {
                    try { LoadTexture(t, found, decoder); }
                    catch (Exception ex) { m.Notes.Add("texture " + found + ": " + ex.Message); }
                }
                else m.Notes.Add($"no texture found for material {t.Name}");

                m.Textures.Add(t);
                return texIndex[mat.Id()] = m.Textures.Count - 1;
            }

            // ---- meshes
            var rawParts = new List<Tuple<Part, Vec3d[], Vec3d[], M4, FbxNode>>();
            foreach (var model in objs.ChildrenNamed("Model").Where(o => o.ObjClass() == "Mesh"))
            {
                var geo = Kids(model).FirstOrDefault(k => k.Name == "Geometry" && k.ObjClass() == "Mesh");
                if (geo == null) continue;
                double[] vtx = geo.Child("Vertices")?.Doubles();
                int[] pvi = geo.Child("PolygonVertexIndex")?.Ints();
                if (vtx == null || pvi == null) continue;
                int ncp = vtx.Length / 3;
                var part = new Part { Name = model.ObjName() };
                var meshG = bindPose.TryGetValue(model.Id(), out var bm) ? bm : M4.Identity;
                double[] gt = model.P70("GeometricTranslation");
                var cps = new Vec3d[ncp];
                for (int i = 0; i < ncp; i++)
                {
                    var p = new Vec3d(vtx[i * 3], vtx[i * 3 + 1], vtx[i * 3 + 2]);
                    if (gt != null && gt.Length >= 3) p = new Vec3d(p.x + gt[0], p.y + gt[1], p.z + gt[2]);
                    cps[i] = meshG.Point(p);
                }

                // normals: averaged per control point
                var nsum = new Vec3d[ncp];
                var nrmLayer = geo.Child("LayerElementNormal");
                double[] nrm = nrmLayer?.Child("Normals")?.Doubles();
                string nMap = nrmLayer?.Child("MappingInformationType")?.Props[0] as string ?? "",
                    nRef = nrmLayer?.Child("ReferenceInformationType")?.Props[0] as string ?? "";
                int[] nIdx = nrmLayer?.Child("NormalsIndex")?.Ints();
                var uvLayer = geo.Child("LayerElementUV");
                double[] uv = uvLayer?.Child("UV")?.Doubles();
                int[] uvIdx = uvLayer?.Child("UVIndex")?.Ints();
                string uvMap = uvLayer?.Child("MappingInformationType")?.Props[0] as string ?? "",
                    uvRef = uvLayer?.Child("ReferenceInformationType")?.Props[0] as string ?? "";
                var matLayer = geo.Child("LayerElementMaterial");
                int[] matIdx = matLayer?.Child("Materials")?.Ints();
                bool matAllSame = (matLayer?.Child("MappingInformationType")?.Props[0] as string ?? "AllSame") ==
                                  "AllSame";
                var mats = Kids(model).Where(k => k.Name == "Material").ToList();

                var tri = new List<int>();
                var tuv = new List<float>();
                var tmat = new List<int>();
                int corner = 0, poly = 0, start = 0;
                var polyC = new List<int>();

                int Pick(string map, string refType, int[] index, int cornerI, int cp, int polyI)
                {
                    int k = map == "ByPolygonVertex" ? cornerI :
                        map == "ByPolygon" ? polyI : cp; // ByControlPoint / ByVertice
                    return refType == "IndexToDirect" && index != null ? index[k] : k;
                }

                for (int k = 0; k < pvi.Length; k++)
                {
                    int v = pvi[k];
                    bool end = v < 0;
                    if (end) v = ~v;
                    polyC.Add(k);
                    if (nrm != null)
                    {
                        int ni = Pick(nMap, nRef, nIdx, k, v, poly);
                        if (ni * 3 + 2 < nrm.Length)
                        {
                            var a = nsum[v];
                            nsum[v] = new Vec3d(a.x + nrm[ni * 3], a.y + nrm[ni * 3 + 1], a.z + nrm[ni * 3 + 2]);
                        }
                    }

                    if (!end) continue;
                    int mi = matIdx == null || matIdx.Length == 0 ? 0 :
                        matAllSame ? matIdx[0] :
                        poly < matIdx.Length ? matIdx[poly] : 0;
                    int texI = mi >= 0 && mi < mats.Count ? TextureFor(mats[mi]) : -1;
                    for (int t = 1; t + 1 < polyC.Count; t++)
                    {
                        foreach (int c in new[] { polyC[0], polyC[t], polyC[t + 1] })
                        {
                            int cpI = pvi[c] < 0 ? ~pvi[c] : pvi[c];
                            tri.Add(cpI);
                            if (uv != null)
                            {
                                int ui = Pick(uvMap, uvRef, uvIdx, c, cpI, poly);
                                tuv.Add(ui * 2 + 1 < uv.Length ? (float)uv[ui * 2] : 0);
                                tuv.Add(ui * 2 + 1 < uv.Length ? (float)uv[ui * 2 + 1] : 0);
                            }
                            else
                            {
                                tuv.Add(0);
                                tuv.Add(0);
                            }
                        }

                        tmat.Add(texI);
                    }

                    polyC.Clear();
                    poly++;
                }

                part.Tri = tri.ToArray();
                part.UV = tuv.ToArray();
                part.Mat = tmat.ToArray();
                var nWorld = new Vec3d[ncp];
                for (int i = 0; i < ncp; i++) nWorld[i] = meshG.Dir(nsum[i]).Unit;

                // skin
                part.Bone = new int[ncp * 4];
                part.Weight = new float[ncp * 4];
                var infl = new List<Tuple<int, float>>[ncp];
                var skin = Kids(geo).FirstOrDefault(k => k.Name == "Deformer" && k.ObjClass() == "Skin");
                int firstBone = -1; // Unity binds vertices without weights to the skin's first bone
                if (skin != null)
                    foreach (var cl in Kids(skin).Where(k => k.ObjClass() == "Cluster"))
                    {
                        var bone = Kids(cl).FirstOrDefault(k => k.Name == "Model");
                        int[] idx = cl.Child("Indexes")?.Ints();
                        double[] w = cl.Child("Weights")?.Doubles();
                        double[] tl = cl.Child("TransformLink")?.Doubles();
                        if (bone == null) continue;
                        int ni = NodeOf(bone);
                        if (ni < 0 || !bindF.ContainsKey(ni)) continue;
                        if (firstBone < 0) firstBone = ni;
                        if (idx == null || w == null) continue;
                        for (int i = 0; i < idx.Length && i < w.Length; i++)
                        {
                            int cp = idx[i];
                            if (cp < 0 || cp >= ncp || w[i] <= 0) continue;
                            (infl[cp] ?? (infl[cp] = new List<Tuple<int, float>>())).Add(Tuple.Create(ni, (float)w[i]));
                        }
                    }
                else
                {
                    // rigid mesh: follow its parent bone if it has one
                    var parentBone = objs.ChildrenNamed("Model").FirstOrDefault(pm => Kids(pm).Contains(model));
                    int ni = parentBone == null ? -1 : NodeOf(parentBone);
                    if (ni >= 0 && bindF.ContainsKey(ni))
                    {
                        for (int i = 0; i < ncp; i++) infl[i] = new List<Tuple<int, float>> { Tuple.Create(ni, 1f) };
                    }
                }

                int unskinned = 0;
                for (int i = 0; i < ncp; i++)
                {
                    var l = infl[i];
                    if ((l == null || l.Count == 0) && skin != null && firstBone >= 0)
                        l = new List<Tuple<int, float>> { Tuple.Create(firstBone, 1f) };
                    if (l == null || l.Count == 0)
                    {
                        unskinned++;
                        part.Bone[i * 4] = -1;
                        continue;
                    }

                    var top4 = l.GroupBy(t => t.Item1).Select(g => Tuple.Create(g.Key, g.Sum(x => x.Item2)))
                        .OrderByDescending(t => t.Item2).Take(4).ToList();
                    float s = top4.Sum(t => t.Item2);
                    for (int k = 0; k < 4; k++)
                    {
                        part.Bone[i * 4 + k] = k < top4.Count ? top4[k].Item1 : -1;
                        part.Weight[i * 4 + k] = k < top4.Count ? top4[k].Item2 / s : 0;
                    }
                }

                if (unskinned > 0) m.Notes.Add($"{part.Name}: {unskinned} unskinned vertices");
                rawParts.Add(Tuple.Create(part, cps, nWorld, meshG, geo));
            }

            // ---- bind skeleton in Unity space, fitted to the profile's rest skeleton
            var src = new List<Vec3>();
            var dst = new List<Vec3>();
            m._fitScale = 1;
            m._fitOffset = Vec3.Zero;
            // fitted on the humanoid bones: accessory bones are sometimes bound away from their rest pose (one test avatar's
            // choker and ribbons are bound up to 95 cm off), which would pull the whole fit
            var humanNodes = new HashSet<int>(prof.Human.Where(h => h >= 0));
            bool humanOnly = bindF.Keys.Count(humanNodes.Contains) >= 6;
            foreach (var kv in bindF)
            {
                if (humanOnly && !humanNodes.Contains(kv.Key)) continue;
                src.Add(m.ToUnityPoint(kv.Value.Point(new Vec3d(0, 0, 0))));
                dst.Add(prof.RestWorldPos[kv.Key]);
            }

            if (src.Count >= 3)
            {
                Vec3 cs = Vec3.Zero, cd = Vec3.Zero;
                for (int i = 0; i < src.Count; i++)
                {
                    cs = cs + src[i];
                    cd = cd + dst[i];
                }

                cs = cs * (1f / src.Count);
                cd = cd * (1f / src.Count);
                double ss = 0, sd = 0;
                for (int i = 0; i < src.Count; i++)
                {
                    ss += (src[i] - cs).Length * (src[i] - cs).Length;
                    sd += (dst[i] - cd).Length * (dst[i] - cd).Length;
                }

                float k = ss > 1e-12 ? (float)Math.Sqrt(sd / ss) : 1f;
                if (Math.Abs(k - 1) > 0.02f) m._fitScale = k; // e.g. a globalScale set in Unity's import settings
                m._fitOffset = cd - cs * m._fitScale;
                if (m._fitOffset.Length < 0.005f) m._fitOffset = Vec3.Zero;
                double err = 0, maxErr = 0;
                for (int i = 0; i < src.Count; i++)
                {
                    double e = (src[i] * m._fitScale + m._fitOffset - dst[i]).Length;
                    err += e * e;
                    maxErr = Math.Max(maxErr, e);
                }

                m.Notes.Add(
                    $"bind vs rest skeleton: {src.Count} {(humanOnly ? "humanoid " : "")}bones, rms {Math.Sqrt(err / src.Count) * 1000:F1} mm, max {maxErr * 1000:F1} mm, scale {m._fitScale:F4}");
            }

            m.BindPos = new Vec3[nodeCount];
            m.BindRot = new Quat[nodeCount];
            m.IsBound = new bool[nodeCount];
            var angs = new List<Tuple<double, string>>();
            foreach (var kv in bindF)
            {
                m.BindPos[kv.Key] = m.ToUnityPoint(kv.Value.Point(new Vec3d(0, 0, 0)));
                m.BindRot[kv.Key] = m.ToUnityRot(kv.Value);
                m.IsBound[kv.Key] = true;
                float dot = Math.Abs(Quat.Dot(m.BindRot[kv.Key], prof.RestWorldRot[kv.Key]));
                angs.Add(Tuple.Create(2 * Math.Acos(Math.Min(1, dot)) * 180 / Math.PI, prof.Nodes[kv.Key].Name));
            }

            var worst = angs.OrderByDescending(a => a.Item1).Take(4).ToList();
            m.Notes.Add(
                $"bind vs rest rotation: {angs.Count(a => a.Item1 < 0.5)}/{angs.Count} bones within 0.5°, largest " +
                string.Join(", ", worst.Select(a => $"{a.Item2} {a.Item1:F1}°")));

            // ---- shapes added in Unity by BlendShare (e.g. face-tracking add-ons): FBX control-point deltas for this model
            var animatorShapes = FaceAnimatorFiles.ForProfile(prof)?.Shapes;
            var extraShapes = new Dictionary<string, List<BlendShareAsset.ShapeData>>();
            foreach (string bsPath in BlendShareAsset.FindFor(fbxPath))
            {
                try
                {
                    var asset = BlendShareAsset.Load(bsPath);
                    foreach (var bm in asset.Meshes)
                    {
                        if (!extraShapes.TryGetValue(bm.MeshName, out var l))
                            extraShapes[bm.MeshName] = l = new List<BlendShareAsset.ShapeData>();
                        l.AddRange(bm.Shapes);
                    }

                    m.Notes.Add($"BlendShare {Path.GetFileName(bsPath)}: " + string.Join(", ",
                        asset.Meshes.Select(x => $"{x.MeshName} +{x.Shapes.Count}")));
                }
                catch (Exception ex) { m.Notes.Add("BlendShare " + Path.GetFileName(bsPath) + ": " + ex.Message); }
            }

            // ---- convert vertices, shapes
            foreach (var rp in rawParts)
            {
                var part = rp.Item1;
                var cps = rp.Item2;
                var nW = rp.Item3;
                var meshG = rp.Item4;
                var geo = rp.Item5;
                int ncp = cps.Length;
                part.Rest = new Vec3[ncp];
                part.RestN = new Vec3[ncp];
                for (int i = 0; i < ncp; i++)
                {
                    part.Rest[i] = m.ToUnityPoint(cps[i]);
                    part.RestN[i] = m.ToUnityDir(nW[i], false).Normalized;
                }

                part.Pos = (Vec3[])part.Rest.Clone();
                part.Nrm = (Vec3[])part.RestN.Clone();
                var bs = Kids(geo).FirstOrDefault(k => k.Name == "Deformer" && k.ObjClass() == "BlendShape");
                var channels = bs == null
                    ? new List<FbxNode>()
                    : Kids(bs).Where(k => k.ObjClass() == "BlendShapeChannel").ToList();
                var extra = extraShapes.TryGetValue(part.Name, out var ex2)
                    ? ex2.Where(s => s.Points.Length > 0).ToList()
                    : new List<BlendShareAsset.ShapeData>();
                var names = channels.Select(c => c.ObjName()).Concat(extra.Select(s => s.Name)).Distinct().ToList();
                if (names.Count == 0) continue;
                // prefer the face-tracking shapes (BlendShare) over the model's own when both match a name
                var map = FaceMapping.MatchToMesh(FaceMapping.AllShapeNames, extra.Select(s => s.Name).ToList());
                foreach (var kv in FaceMapping.MatchToMesh(FaceMapping.AllShapeNames, names))
                    if (!map.ContainsKey(kv.Key))
                        map[kv.Key] = kv.Value;
                // the avatar's own face-tracking animator sets shapes by their exact names (correctives and the like)
                if (animatorShapes != null)
                    foreach (string s in animatorShapes)
                        if (!map.ContainsKey(s) && names.Contains(s))
                            map[s] = s;
                foreach (var al in PreviewAliases)
                    if (!map.ContainsKey(al.Key))
                    {
                        string hit = al.Value.FirstOrDefault(a => names.Contains(a));
                        if (hit != null) map[al.Key] = hit;
                    }

                foreach (var kv in map)
                {
                    int[] idx = null;
                    Vec3[] delta = null;
                    var es = extra.FirstOrDefault(s => s.Name == kv.Value);
                    if (es != null)
                    {
                        idx = es.Points;
                        delta = new Vec3[idx.Length];
                        for (int i = 0; i < idx.Length; i++)
                            delta[i] = m.ToUnityDir(
                                meshG.Dir(new Vec3d(es.Delta[i * 3], es.Delta[i * 3 + 1], es.Delta[i * 3 + 2])), true);
                    }
                    else
                    {
                        var ch = channels.FirstOrDefault(c => c.ObjName() == kv.Value);
                        var shp = ch == null
                            ? null
                            : Kids(ch).FirstOrDefault(k => k.Name == "Geometry" && k.ObjClass() == "Shape");
                        idx = shp?.Child("Indexes")?.Ints();
                        double[] dv = shp?.Child("Vertices")?.Doubles();
                        if (idx == null || dv == null) continue;
                        delta = new Vec3[idx.Length];
                        for (int i = 0; i < idx.Length; i++)
                            delta[i] = m.ToUnityDir(meshG.Dir(new Vec3d(dv[i * 3], dv[i * 3 + 1], dv[i * 3 + 2])),
                                true);
                    }

                    if (idx.Any(i => i < 0 || i >= ncp)) continue;
                    part.Shapes[kv.Value] = new Shape { Idx = idx, Delta = delta };
                    part.FaceMap[kv.Key] = kv.Value;
                    m.FaceShapes[kv.Key] = kv.Value;
                }
            }

            m.Parts.AddRange(rawParts.Select(r => r.Item1));
            if (noDecoder) m.Notes.Add("textures not loaded: the loader has no texture decoder");
            m.Notes.Insert(0,
                $"{Path.GetFileName(fbxPath)}: {m.Parts.Count} meshes, {m.TriangleCount} triangles, {m.Textures.Count(t => t.Px != null)}/{m.Textures.Count} textures, face shapes {m.FaceShapes.Count}, {sw.ElapsedMilliseconds} ms");
            foreach (string n in m.Notes) AppLog.Info("Avatar mesh: " + n);
            return m;
        }

        static string FindTexture(string root, string file, string material)
        {
            try
            {
                var all = Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)).ToList();

                bool Plain(string f)
                {
                    return f.IndexOf("Another", StringComparison.OrdinalIgnoreCase) < 0 &&
                           f.IndexOf("Mask", StringComparison.OrdinalIgnoreCase) < 0;
                }

                return all.Where(f => Path.GetFileName(f).Equals(file, StringComparison.OrdinalIgnoreCase))
                           .OrderBy(f => Plain(f) ? 0 : 1).ThenBy(f => f.Length).FirstOrDefault()
                       ?? all.Where(f =>
                               Path.GetFileNameWithoutExtension(f).Equals(material, StringComparison.OrdinalIgnoreCase))
                           .OrderBy(f => Plain(f) ? 0 : 1).FirstOrDefault();
            }
            catch { return null; }
        }

        static void LoadTexture(Texture t, string path, TextureDecoder decoder)
        {
            t.File = path;
            var img = decoder(path, MaxTextureSize);
            t.Px = img.Argb;
            t.W = img.Width;
            t.H = img.Height;
            int clear = 0;
            foreach (int c in t.Px)
                if ((uint)c >> 24 < 128)
                    clear++;
            t.Cutout = clear > t.Px.Length / 200;
        }

        /// <summary>
        ///     Skins every visible part onto the given world-space skeleton (positions/rotations per profile node).
        ///     faceWeights: recording face shape (Unified Expressions name) -> 0..1. scale: avatar scale of the skeleton.
        /// </summary>
        public void Skin(Vec3[] nodePos, Quat[] nodeRot, IDictionary<string, float> faceWeights, float scale = 1f)
        {
            int n = Profile.Nodes.Count;
            if (_m == null || _m.Length != n * 12) _m = new float[n * 12];
            for (int i = 0; i < n; i++)
            {
                if (!IsBound[i] || i >= nodePos.Length) continue;
                var q = (nodeRot[i] * BindRot[i].Conjugate).Normalized;
                // rotation matrix (row-major) * scale
                float x = q.x, y = q.y, z = q.z, w = q.w;
                float r00 = 1 - 2 * (y * y + z * z), r01 = 2 * (x * y - z * w), r02 = 2 * (x * z + y * w);
                float r10 = 2 * (x * y + z * w), r11 = 1 - 2 * (x * x + z * z), r12 = 2 * (y * z - x * w);
                float r20 = 2 * (x * z - y * w), r21 = 2 * (y * z + x * w), r22 = 1 - 2 * (x * x + y * y);
                var b = BindPos[i];
                var c = nodePos[i];
                int o = i * 12;
                _m[o] = r00;
                _m[o + 1] = r01;
                _m[o + 2] = r02;
                _m[o + 4] = r10;
                _m[o + 5] = r11;
                _m[o + 6] = r12;
                _m[o + 8] = r20;
                _m[o + 9] = r21;
                _m[o + 10] = r22;
                _m[o + 3] = c.x - scale * (r00 * b.x + r01 * b.y + r02 * b.z);
                _m[o + 7] = c.y - scale * (r10 * b.x + r11 * b.y + r12 * b.z);
                _m[o + 11] = c.z - scale * (r20 * b.x + r21 * b.y + r22 * b.z);
            }

            float[] mm = _m;
            Parallel.ForEach(Parts.Where(p => p.Visible), part =>
            {
                var src = part.Rest;
                Vec3[] morphed = null;
                if (faceWeights != null && part.Shapes.Count > 0)
                    foreach (var kv in part.FaceMap)
                    {
                        if (!part.Shapes.TryGetValue(kv.Value, out var s)) continue;
                        if (!faceWeights.TryGetValue(kv.Key, out float wgt) &&
                            !(ArkitOf.TryGetValue(kv.Key, out string ak) && faceWeights.TryGetValue(ak, out wgt)))
                            continue;
                        if (wgt < 0.01f) continue;
                        if (morphed == null) morphed = (Vec3[])src.Clone();
                        wgt = Math.Min(1f, wgt);
                        for (int k = 0; k < s.Idx.Length; k++)
                        {
                            int vi = s.Idx[k];
                            if (vi < morphed.Length) morphed[vi] = morphed[vi] + s.Delta[k] * wgt;
                        }
                    }

                if (morphed != null) src = morphed;
                var pos = part.Pos;
                var nrm = part.Nrm;
                var rn = part.RestN;
                int[] bones = part.Bone;
                float[] wts = part.Weight;
                for (int v = 0; v < src.Length; v++)
                {
                    var p = src[v];
                    var nr = rn[v];
                    float px = 0, py = 0, pz = 0, nx = 0, ny = 0, nz = 0;
                    int bi = v * 4;
                    if (bones[bi] < 0)
                    {
                        pos[v] = p;
                        nrm[v] = nr;
                        continue;
                    }

                    for (int k = 0; k < 4; k++)
                    {
                        int bone = bones[bi + k];
                        if (bone < 0) break;
                        float wgt = wts[bi + k];
                        int o = bone * 12;
                        float sx = p.x * scale, sy = p.y * scale, sz = p.z * scale;
                        px += wgt * (mm[o] * sx + mm[o + 1] * sy + mm[o + 2] * sz + mm[o + 3]);
                        py += wgt * (mm[o + 4] * sx + mm[o + 5] * sy + mm[o + 6] * sz + mm[o + 7]);
                        pz += wgt * (mm[o + 8] * sx + mm[o + 9] * sy + mm[o + 10] * sz + mm[o + 11]);
                        nx += wgt * (mm[o] * nr.x + mm[o + 1] * nr.y + mm[o + 2] * nr.z);
                        ny += wgt * (mm[o + 4] * nr.x + mm[o + 5] * nr.y + mm[o + 6] * nr.z);
                        nz += wgt * (mm[o + 8] * nr.x + mm[o + 9] * nr.y + mm[o + 10] * nr.z);
                    }

                    pos[v] = new Vec3(px, py, pz);
                    nrm[v] = new Vec3(nx, ny, nz);
                }
            });
        }

        public sealed class Texture
        {
            public bool Cutout;
            public string Name = "", File = "";
            public int[] Px;
            public int W, H;
        }

        public sealed class Shape
        {
            public Vec3[] Delta;
            public int[] Idx;
        }

        public sealed class Part
        {
            public readonly Dictionary<string, string> FaceMap = new(); // recording face shape -> this part's shape
            public readonly Dictionary<string, Shape> Shapes = new();
            public int[] Bone;
            public int[] Mat; // texture index per triangle (-1 = untextured)
            public string Name;
            public Vec3[] Pos, Nrm; // skinned
            public Vec3[] Rest, RestN; // per control point, Unity space at bind
            public int[] Tri; // 3 control points per triangle
            public float[] UV; // 6 per triangle
            public bool Visible = true;
            public float[] Weight; // 4 influences per control point (profile node indices)
        }

        // ------------------------------------------------------------------ loading

        struct M4 // FBX column-major 4x4
        {
            public readonly double[] m;
            public M4(double[] a) { m = a; }
            public static M4 Identity => new(new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 });

            public Vec3d Point(Vec3d p)
            {
                return new Vec3d(m[0] * p.x + m[4] * p.y + m[8] * p.z + m[12],
                    m[1] * p.x + m[5] * p.y + m[9] * p.z + m[13], m[2] * p.x + m[6] * p.y + m[10] * p.z + m[14]);
            }

            public Vec3d Dir(Vec3d p)
            {
                return new Vec3d(m[0] * p.x + m[4] * p.y + m[8] * p.z, m[1] * p.x + m[5] * p.y + m[9] * p.z,
                    m[2] * p.x + m[6] * p.y + m[10] * p.z);
            }

            public Vec3d Col(int c)
            {
                return new Vec3d(m[c * 4], m[c * 4 + 1], m[c * 4 + 2]);
            }

            public static M4 Mul(M4 a, M4 b)
            {
                double[] r = new double[16];
                for (int c = 0; c < 4; c++)
                for (int rr = 0; rr < 4; rr++)
                {
                    double s = 0;
                    for (int k = 0; k < 4; k++) s += a.m[k * 4 + rr] * b.m[c * 4 + k];
                    r[c * 4 + rr] = s;
                }

                return new M4(r);
            }
        }

        struct Vec3d
        {
            public readonly double x;
            public readonly double y;
            public readonly double z;

            public Vec3d(double x, double y, double z)
            {
                this.x = x;
                this.y = y;
                this.z = z;
            }

            public static Vec3d operator -(Vec3d a, Vec3d b)
            {
                return new Vec3d(a.x - b.x, a.y - b.y, a.z - b.z);
            }

            public double Len => Math.Sqrt(x * x + y * y + z * z);

            public Vec3d Unit
            {
                get
                {
                    double l = Len;
                    return l > 1e-12 ? new Vec3d(x / l, y / l, z / l) : this;
                }
            }
        }
    }
}
