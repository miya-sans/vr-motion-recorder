using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VRMR
{
    // =====================================================================================================  avatar FBX
    //
    // The clip written onto the avatar's own FBX: the file is read (the library's face-tracking copy, or the model in the
    // Unity project with its BlendShare shapes merged in memory), its meshes, skin, materials, bind poses and blendshapes
    // are kept as they are, and one take is added:
    //   * the armature chain (humanoid bones + their ancestors) gets the app's rest skeleton as its static pose (Unity's
    //     import of this same model, so Unity and Blender evaluate exactly the app's skeleton), pre-/post-rotations and
    //     pivots of those nodes are folded in;
    //   * humanoid bones are keyed (Lcl Rotation, Hips also Lcl Translation), the armature's top nodes keep VRChat's scaling;
    //   * the face shapes key the face mesh's own shape keys (DeformPercent), and the same-named keys of other meshes;
    //   * camera / trackers as animated empties.
    // Positions are in the file's own units (UnitScaleFactor); the source file is never written.

    public static partial class FbxExporter
    {
        static readonly string[] AnimClasses =
            { "AnimationStack", "AnimationLayer", "AnimationCurveNode", "AnimationCurve" };

        // FBX RotationOrder: 0 XYZ, 1 XZY, 2 YZX, 3 YXZ, 4 ZXY, 5 ZYX (6 SphericXYZ, read as XYZ); the first letter is applied first
        static readonly int[][] EulerOrders =
        {
            new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 2, 0 }, new[] { 1, 0, 2 }, new[] { 2, 0, 1 },
            new[] { 2, 1, 0 }
        };

        static DQ EulerQ(double[] e, int order)
        {
            if (e == null || e.Length < 3) return new DQ(0, 0, 0, 1);
            int[] o = EulerOrders[order >= 0 && order < 6 ? order : 0];
            var q = new DQ(0, 0, 0, 1);
            foreach (int a in o) q = DQ.Axis(a, e[a]) * q; // later axes multiply on the left
            return q;
        }

        static double[] Vec(FbxNode m, string prop, double def)
        {
            double[] v = m.P70(prop);
            return v != null && v.Length >= 3 ? new[] { v[0], v[1], v[2] } : new[] { def, def, def };
        }

        static bool NonZero(double[] v)
        {
            return v != null && v.Any(x => Math.Abs(x) > 1e-9);
        }

        /// <summary>FBX local transform of a model at rest (Lcl values, pre/post rotation when RotationActive, pivots, offsets).</summary>
        static D4 LocalRest(FbxNode m)
        {
            bool active = (m.P70("RotationActive")?[0] ?? 0) != 0;
            int order = active ? (int)(m.P70("RotationOrder")?[0] ?? 0) : 0;
            double[] t = Vec(m, "Lcl Translation", 0);
            double[] r = Vec(m, "Lcl Rotation", 0);
            double[] s = Vec(m, "Lcl Scaling", 1);
            var pre = active ? EulerQ(Vec(m, "PreRotation", 0), 0) : new DQ(0, 0, 0, 1);
            var post = active ? EulerQ(Vec(m, "PostRotation", 0), 0) : new DQ(0, 0, 0, 1);
            double[] roff = Vec(m, "RotationOffset", 0);
            double[] rp = Vec(m, "RotationPivot", 0);
            double[] soff = Vec(m, "ScalingOffset", 0);
            double[] sp = Vec(m, "ScalingPivot", 0);
            var rot = pre * EulerQ(r, order) * post.Conj;
            return D4.T(t[0], t[1], t[2]) * D4.T(roff[0], roff[1], roff[2]) * D4.T(rp[0], rp[1], rp[2]) * D4.R(rot) *
                   D4.T(-rp[0], -rp[1], -rp[2])
                   * D4.T(soff[0], soff[1], soff[2]) * D4.T(sp[0], sp[1], sp[2]) * D4.S(s[0], s[1], s[2]) *
                   D4.T(-sp[0], -sp[1], -sp[2]);
        }

        /// <summary>Sets (or adds) a Properties70 entry.</summary>
        static void SetP(FbxNode obj, string name, string type, string label, string flags, params object[] values)
        {
            var p70 = obj.Child("Properties70") ?? obj.Add("Properties70");
            var p = p70.Children.FirstOrDefault(c => c.Props.Count > 0 && c.Props[0] as string == name);
            if (p == null)
            {
                p70.P(name, type, label, flags, values);
                return;
            }

            while (p.Props.Count > 4) p.Props.RemoveAt(p.Props.Count - 1);
            p.Props.AddRange(values);
        }

        static void RemoveP(FbxNode obj, string name)
        {
            var p70 = obj.Child("Properties70");
            p70?.Children.RemoveAll(c => c.Props.Count > 0 && c.Props[0] as string == name);
        }

        /// <summary>
        ///     Writes the clip onto the avatar FBX <paramref name="src" />. Returns false (with a note) when the file can't carry
        ///     the clip (unusual axis system, no Hips); the caller then writes the armature-only FBX.
        /// </summary>
        static bool ExportOnAvatar(BakedClip clip, string src, string path, FbxExportOptions opt, FbxExportResult res)
        {
            var prof = clip.Profile;
            if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("the export would overwrite the avatar's FBX");
            var top = FbxReader.Read(src);
            BlendShareAsset.PrepareForWrite(top);
            // BlendShare shapes (face-tracking add-ons) for a model in a Unity project; the library's _FT copies already have them
            var bsPaths = BlendShareAsset.FindFor(src);
            if (bsPaths.Count > 0)
            {
                var assets = new List<BlendShareAsset>();
                foreach (string b in bsPaths)
                {
                    try { assets.Add(BlendShareAsset.Load(b)); }
                    catch (Exception ex) { res.Notes.Add("BlendShare " + Path.GetFileName(b) + ": " + ex.Message); }
                }

                if (assets.Count > 0)
                    res.Notes.Add("BlendShare shapes merged: " + BlendShareAsset.MergeInto(top, assets)
                        .Replace(Environment.NewLine, "; ").Replace("\n", "; "));
            }

            var gs = top.FirstOrDefault(n => n.Name == "GlobalSettings");
            int up = (int)(gs?.P70("UpAxis")?[0] ?? 1), upS = (int)(gs?.P70("UpAxisSign")?[0] ?? 1);
            int front = (int)(gs?.P70("FrontAxis")?[0] ?? 2), frontS = (int)(gs?.P70("FrontAxisSign")?[0] ?? 1);
            int coord = (int)(gs?.P70("CoordAxis")?[0] ?? 0), coordS = (int)(gs?.P70("CoordAxisSign")?[0] ?? 1);
            if (up != 1 || upS != 1 || front != 2 || frontS != 1 || coord != 0 || coordS != 1)
            {
                res.Notes.Add(
                    $"{Path.GetFileName(src)} is not Y-up (axes up {up}{(upS > 0 ? "+" : "-")}, front {front}{(frontS > 0 ? "+" : "-")}): armature + face carrier only.");
                return false;
            }

            double unit = (gs?.P70("UnitScaleFactor")?[0] ?? 1.0) * 0.01; // meters per file unit

            var objs = top.First(n => n.Name == "Objects");
            var conns = top.First(n => n.Name == "Connections");
            // drop any animation the model carries
            var animIds = new HashSet<long>(objs.Children.Where(o => AnimClasses.Contains(o.Name)).Select(o => o.Id()));
            if (animIds.Count > 0)
            {
                objs.Children.RemoveAll(o => AnimClasses.Contains(o.Name));
                conns.Children.RemoveAll(c => c.Props.Count >= 3 && ((c.Props[1] is long a && animIds.Contains(a)) ||
                                                                     (c.Props[2] is long b && animIds.Contains(b))));
                res.Notes.Add($"the model's own animation ({animIds.Count} objects) was replaced");
            }

            var byId = new Dictionary<long, FbxNode>();
            foreach (var o in objs.Children) byId[o.Id()] = o;
            var kids = new Dictionary<long, List<FbxNode>>();
            var modelParent = new Dictionary<long, long>();
            foreach (var c in conns.Children)
            {
                if (c.Props.Count < 3 || !(c.Props[1] is long ch) || !(c.Props[2] is long pa) ||
                    !byId.TryGetValue(ch, out var cn)) continue;
                if (!kids.TryGetValue(pa, out var l)) kids[pa] = l = new List<FbxNode>();
                l.Add(cn);
                // a bone is also connected to its skin clusters (Model -> Cluster): only Model -> Model / scene root is the hierarchy
                if (cn.Name == "Model" && c.Props[0] as string == "OO" &&
                    (pa == 0 || (byId.TryGetValue(pa, out var po) && po.Name == "Model"))) modelParent[ch] = pa;
            }

            List<FbxNode> Kids(long id)
            {
                return kids.TryGetValue(id, out var l) ? l : new List<FbxNode>();
            }

            var models = objs.ChildrenNamed("Model").ToList();
            var modelByName = new Dictionary<string, FbxNode>();
            foreach (var m in models)
                if (!modelByName.ContainsKey(m.ObjName()))
                    modelByName[m.ObjName()] = m;

            // ---- the armature chain: humanoid bones and their ancestors (the profile root is the file's scene root)
            int root = prof.RootIndex, hips = prof.Human[HumanBones.Hips];
            if (hips < 0 || !modelByName.ContainsKey(prof.Nodes[hips].Name))
            {
                res.Notes.Add(
                    $"{Path.GetFileName(src)} has no '{(hips >= 0 ? prof.Nodes[hips].Name : "Hips")}' bone: armature + face carrier only.");
                return false;
            }

            var chain = new SortedSet<int>();
            for (int b = 0; b < HumanBones.Count; b++)
            for (int a = prof.Human[b]; a >= 0 && a != root; a = prof.Nodes[a].Parent)
                chain.Add(a);
            var nodeModel = new Dictionary<int, FbxNode>();
            var missing = new List<string>();
            var reparented = new List<string>();
            foreach (int i in chain)
            {
                var nd = prof.Nodes[i];
                if (!modelByName.TryGetValue(nd.Name, out var m))
                {
                    missing.Add(nd.Name);
                    continue;
                }

                long fp = modelParent.TryGetValue(m.Id(), out long pp) ? pp : 0;
                string fpName = fp != 0 && byId.TryGetValue(fp, out var pm) ? pm.ObjName() : null;
                string ppName = nd.Parent == root || nd.Parent < 0 ? null : prof.Nodes[nd.Parent].Name;
                if (fpName != ppName)
                {
                    reparented.Add($"{nd.Name} (FBX parent {fpName ?? "scene"}, avatar parent {ppName ?? "root"})");
                    continue;
                }

                nodeModel[i] = m;
            }

            if (missing.Count > 0)
                res.Notes.Add(
                    $"{missing.Count} bones of the avatar are not in {Path.GetFileName(src)} (not animated): " +
                    string.Join(", ", missing.Take(6)));
            if (reparented.Count > 0)
                res.Notes.Add($"{reparented.Count} bones have another parent in the FBX (not animated): " +
                              string.Join(", ", reparented.Take(4)));
            if (!nodeModel.ContainsKey(hips))
            {
                res.Notes.Add("Hips could not be matched in the FBX: armature + face carrier only.");
                return false;
            }

            // ---- scale: the app's skeleton is Unity's import of this model (meters, the model's import scale); fit the file's
            // own rest skeleton to it (a globalScale set in Unity's import settings shows up here)
            var restW = new Dictionary<long, D4>();

            D4 World(FbxNode m)
            {
                if (restW.TryGetValue(m.Id(), out var w)) return w;
                var l = LocalRest(m);
                w = modelParent.TryGetValue(m.Id(), out long pid) && byId.TryGetValue(pid, out var pm) &&
                    pm.Name == "Model"
                    ? World(pm) * l
                    : l;
                restW[m.Id()] = w;
                return w;
            }

            var profW = ProfileRestWorld(prof);
            var srcP = new List<double[]>();
            var dstP = new List<Vec3>();
            for (int b = 0; b < HumanBones.Count; b++)
            {
                int ni = prof.Human[b];
                if (ni < 0 || !nodeModel.TryGetValue(ni, out var m)) continue;
                double[] o = World(m).Origin;
                srcP.Add(new[] { -o[0] * unit, o[1] * unit, o[2] * unit });
                dstP.Add(profW[ni]);
            }

            double k = 1;
            if (srcP.Count >= 3)
            {
                double[] cs = new double[3];
                var cd = Vec3.Zero;
                foreach (double[] p in srcP)
                    for (int c = 0; c < 3; c++)
                        cs[c] += p[c] / srcP.Count;
                foreach (var p in dstP) cd = cd + p * (1f / dstP.Count);
                double ss = 0, sd = 0;
                for (int i = 0; i < srcP.Count; i++)
                {
                    ss += Sq(srcP[i][0] - cs[0]) + Sq(srcP[i][1] - cs[1]) + Sq(srcP[i][2] - cs[2]);
                    sd += Sq(dstP[i].x - cd.x) + Sq(dstP[i].y - cd.y) + Sq(dstP[i].z - cd.z);
                }

                double kk = ss > 1e-12 ? Math.Sqrt(sd / ss) : 1;
                if (Math.Abs(kk - 1) > 0.02)
                {
                    k = kk;
                    res.Notes.Add(
                        $"the avatar is imported at {k:0.###}x the FBX's size in Unity: motion scaled to the FBX");
                }

                double err = 0, mx = 0;
                for (int i = 0; i < srcP.Count; i++)
                {
                    double e = Math.Sqrt(Sq(srcP[i][0] * k - dstP[i].x) + Sq(srcP[i][1] * k - dstP[i].y) +
                                         Sq(srcP[i][2] * k - dstP[i].z));
                    err += e * e;
                    mx = Math.Max(mx, e);
                }

                res.Notes.Add(
                    $"FBX rest vs the app's skeleton: rms {Math.Sqrt(err / srcP.Count) * 1000:0.0} mm, max {mx * 1000:0.0} mm over {srcP.Count} humanoid bones");
            }

            double conv = unit * k; // profile meters per file unit

            // ---- static pose of the chain = the app's rest skeleton (what Unity made of this model)
            var preQ = new Dictionary<int, DQ>();
            var postQ = new Dictionary<int, DQ>();
            int changed = 0;
            foreach (var kv in nodeModel)
            {
                int i = kv.Key;
                var m = kv.Value;
                var nd = prof.Nodes[i];
                var before = LocalRest(m);
                bool active = (m.P70("RotationActive")?[0] ?? 0) != 0;
                preQ[i] = active ? EulerQ(Vec(m, "PreRotation", 0), 0) : new DQ(0, 0, 0, 1);
                postQ[i] = active ? EulerQ(Vec(m, "PostRotation", 0), 0) : new DQ(0, 0, 0, 1);
                // pivots / offsets folded into Lcl Translation, rotation order XYZ (the values below are the whole local transform)
                foreach (string pv in new[] { "RotationOffset", "RotationPivot", "ScalingOffset", "ScalingPivot" })
                    RemoveP(m, pv);
                if (active) SetP(m, "RotationOrder", "enum", "", "", 0);
                var t = MathX.UnityToFbxPos(nd.LocalPos);
                var e = FbxEuler(preQ[i], MathX.UnityToFbxRot(nd.LocalRot), postQ[i]);
                SetP(m, "Lcl Translation", "Lcl Translation", "", "A", t.x / conv, t.y / conv, t.z / conv);
                SetP(m, "Lcl Rotation", "Lcl Rotation", "", "A", (double)e.x, (double)e.y, (double)e.z);
                SetP(m, "Lcl Scaling", "Lcl Scaling", "", "A", (double)nd.LocalScale.x, (double)nd.LocalScale.y,
                    (double)nd.LocalScale.z);
                var after = LocalRest(m);
                double dt = 0, dr = 0;
                for (int c = 0; c < 3; c++) dt = Math.Max(dt, Math.Abs(after.m[12 + c] - before.m[12 + c]) * conv);
                for (int c = 0; c < 12; c++)
                    if (c % 4 != 3)
                        dr = Math.Max(dr, Math.Abs(after.m[c] - before.m[c]));
                if (dt > 1e-4 || dr > 1e-3) changed++;
            }

            if (changed > 0) res.Notes.Add($"{changed} bones' rest pose set to the avatar's Unity rest pose (T-pose)");

            // ---- frames
            int frames = clip.Frames;
            int step = Math.Max(1, opt.FrameStep);
            var frameIdx = new List<int>();
            for (int f = 0; f < frames; f += step) frameIdx.Add(f);
            if (frameIdx[frameIdx.Count - 1] != frames - 1) frameIdx.Add(frames - 1);
            long[] ktimes = frameIdx.Select(f => (long)Math.Round(f / (double)clip.Fps * KTimeSecond)).ToArray();
            long stop = ktimes[ktimes.Length - 1];

            var ctx = new Ctx { Objects = objs, Connections = conns, Used = new HashSet<long>(byId.Keys) };
            long stackId = ctx.Id(), layerId = ctx.Id();
            var stack = new FbxNode("AnimationStack", stackId, FbxBinaryWriter.Name(opt.TakeName, "AnimStack"), "");
            var sp = stack.Add("Properties70");
            sp.P("LocalStart", "KTime", "Time", "", 0L);
            sp.P("LocalStop", "KTime", "Time", "", stop);
            sp.P("ReferenceStart", "KTime", "Time", "", 0L);
            sp.P("ReferenceStop", "KTime", "Time", "", stop);
            objs.Add(stack);
            objs.Add("AnimationLayer", layerId, FbxBinaryWriter.Name("BaseLayer", "AnimLayer"), "");
            ctx.OO(layerId, stackId);

            // ---- bones
            int nn = prof.Nodes.Count;
            var wp = new Vec3[nn];
            var wr = new Quat[nn];
            var lp = new Vec3[nn];
            var lr = new Quat[nn];
            var animated = nodeModel.Keys.Where(i => prof.HumanOfNode(i) >= 0).ToList();
            var rotKeys = animated.ToDictionary(i => i, i => new Vec3[frameIdx.Count]);
            var posKey = new Vec3[frameIdx.Count];
            float invConv = (float)(1.0 / conv);
            for (int kf = 0; kf < frameIdx.Count; kf++)
            {
                clip.EvaluateLocal(frameIdx[kf], wp, wr, lp, lr);
                foreach (int i in animated)
                {
                    var e = FbxEuler(preQ[i], MathX.UnityToFbxRot(lr[i]), postQ[i]);
                    rotKeys[i][kf] = kf > 0 ? MathX.UnwrapEuler(e, rotKeys[i][kf - 1]) : e;
                }

                posKey[kf] = MathX.UnityToFbxPos(lp[hips]) * invConv;
            }

            foreach (int i in animated)
            {
                long mid = nodeModel[i].Id();
                AddCurveNode(ctx, layerId, mid, "R", "Lcl Rotation", ktimes, rotKeys[i]);
                if (i == hips) AddCurveNode(ctx, layerId, mid, "T", "Lcl Translation", ktimes, posKey);
            }

            res.AnimatedBones = animated.Count;
            // VRChat's avatar scaling (BakedClip.ScaleCurve): the armature's top nodes scale with it, like the armature-only FBX
            if (clip.ScaleCurve != null)
                foreach (var kv in nodeModel)
                {
                    if (prof.Nodes[kv.Key].Parent != root) continue;
                    var ls = prof.Nodes[kv.Key].LocalScale;
                    var sk = frameIdx.Select(f => ls * (clip.ScaleAt(f) / Math.Max(1e-6f, clip.Scale))).ToArray();
                    AddCurveNode(ctx, layerId, kv.Value.Id(), "S", "Lcl Scaling", ktimes, sk);
                }

            // ---- meshes, face shape keys
            var meshModels = models.Where(m => m.ObjClass() == "Mesh").ToList();
            var channelsOf = new Dictionary<long, List<FbxNode>>(); // mesh model -> its blendshape channels
            foreach (var mm in meshModels)
            {
                var geo = Kids(mm.Id()).FirstOrDefault(x => x.Name == "Geometry" && x.ObjClass() == "Mesh");
                if (geo == null) continue;
                res.Meshes++;
                res.Vertices += (geo.Child("Vertices")?.Doubles()?.Length ?? 0) / 3;
                var chs = Kids(geo.Id()).Where(x => x.Name == "Deformer" && x.ObjClass() == "BlendShape")
                    .SelectMany(bs =>
                        Kids(bs.Id()).Where(x => x.Name == "Deformer" && x.ObjClass() == "BlendShapeChannel")).ToList();
                if (chs.Count > 0) channelsOf[mm.Id()] = chs;
            }

            if (opt.IncludeFace && clip.Face.Count > 0)
            {
                // the face mesh: by name (the export's face mesh path), else the mesh whose shape keys match the most face shapes
                var cand = meshModels.Where(m => channelsOf.ContainsKey(m.Id())).ToList();

                Dictionary<string, string> MapFor(FbxNode m)
                {
                    return FaceMapping.MatchToMesh(clip.Face.Keys,
                        channelsOf[m.Id()].Select(c => c.ObjName()).Distinct().ToList());
                }

                var face = cand.FirstOrDefault(m => m.ObjName() == opt.FaceMeshName && MapFor(m).Count > 0)
                           ?? cand.OrderByDescending(m => MapFor(m).Count).FirstOrDefault(m => MapFor(m).Count > 0);
                if (face == null)
                    res.Notes.Add(
                        $"no mesh in {Path.GetFileName(src)} has the face shapes: the face is only in the .face.json and the face .anim");
                else
                {
                    if (face.ObjName() != opt.FaceMeshName)
                        res.Notes.Add($"face mesh '{opt.FaceMeshName}' not found: the face keys '{face.ObjName()}'");
                    var map = MapFor(face);
                    foreach (var kv in map)
                    {
                        if (!clip.Face.TryGetValue(kv.Key, out float[] vals)) continue;
                        float[] pct = frameIdx.Select(f => vals[f] * 100f).ToArray();
                        int hit = 0;
                        // the face mesh first, then every other mesh with a shape key of that exact name (ears, brows on separate meshes)
                        foreach (var mm in new[] { face }.Concat(cand.Where(x => x != face)))
                        foreach (var ch in channelsOf[mm.Id()].Where(c => c.ObjName() == kv.Value))
                        {
                            if (ch.Child("Properties70")?.Children.Any(p =>
                                    p.Props.Count > 0 && p.Props[0] as string == "DeformPercent") != true)
                                SetP(ch, "DeformPercent", "Number", "", "A", 0.0);
                            long cn = ctx.Id(), cv = ctx.Id();
                            var cnode = objs.Add("AnimationCurveNode", cn,
                                FbxBinaryWriter.Name("DeformPercent", "AnimCurveNode"), "");
                            cnode.Add("Properties70").P("d|DeformPercent", "Number", "", "A", (double)pct[0]);
                            ctx.CurveNodeCount++;
                            ctx.OO(cn, layerId);
                            ctx.OP(cn, ch.Id(), "DeformPercent");
                            AddCurve(ctx, cv, ktimes, pct);
                            ctx.OP(cv, cn, "d|DeformPercent");
                            hit++;
                        }

                        if (hit > 0)
                        {
                            res.FaceShapes++;
                            res.FaceChannels += hit;
                        }
                    }

                    int unmatched = clip.Face.Keys.Count(s => !map.ContainsKey(s));
                    if (unmatched > 0)
                        res.Notes.Add(
                            $"{unmatched} recorded face shapes have no shape key on '{face.ObjName()}' (they stay in the .face.json / face .anim)");
                }
            }

            // ---- camera / trackers (world space, the file's units)
            if (opt.IncludeCamera && clip.Camera != null)
                AddEmpty(ctx, layerId, "VRChatCamera", frameIdx, ktimes, f => clip.Camera[f], conv * clip.Scale);
            if (opt.IncludeTrackers)
                foreach (var kv in clip.Trackers)
                {
                    var arr = kv.Value;
                    AddEmpty(ctx, layerId, "Tracker_" + kv.Key, frameIdx, ktimes, f => arr[f], conv * clip.Scale);
                }

            // ---- textures: every material's color texture, embedded (Blender and Unity show it wherever the FBX goes)
            AttachTextures(objs, conns, ctx, src, Path.GetDirectoryName(Path.GetFullPath(path)), opt.TextureDir,
                opt.EmbedTextures, res);

            // ---- document: take, time settings, object counts
            if (gs != null)
            {
                int mode = Math.Abs(clip.Fps - 30) < 0.01 ? 6 :
                    Math.Abs(clip.Fps - 60) < 0.01 ? 3 :
                    Math.Abs(clip.Fps - 24) < 0.01 ? 11 :
                    Math.Abs(clip.Fps - 120) < 0.01 ? 1 :
                    Math.Abs(clip.Fps - 50) < 0.01 ? 4 : 14;
                SetP(gs, "TimeMode", "enum", "", "", mode);
                SetP(gs, "TimeSpanStart", "KTime", "Time", "", 0L);
                SetP(gs, "TimeSpanStop", "KTime", "Time", "", stop);
                SetP(gs, "CustomFrameRate", "double", "Number", "", (double)clip.Fps);
            }

            foreach (var doc in top.Where(n => n.Name == "Documents").SelectMany(d => d.ChildrenNamed("Document")))
                SetP(doc, "ActiveAnimStackName", "KString", "", "", opt.TakeName);
            var takes = new FbxNode("Takes");
            takes.Add("Current", opt.TakeName);
            var take = takes.Add("Take", opt.TakeName);
            take.Add("FileName", opt.TakeName.Replace(' ', '_') + ".tak");
            take.Add("LocalTime", 0L, stop);
            take.Add("ReferenceTime", 0L, stop);
            int ti = top.FindIndex(n => n.Name == "Takes");
            if (ti >= 0) top[ti] = takes;
            else top.Insert(top.IndexOf(conns) + 1, takes);
            RecountDefinitions(top, objs);

            FbxBinaryWriter.Write(path, top);
            res.Mode = "avatar";
            res.SourceFbx = src;
            return true;
        }

        static double Sq(double v)
        {
            return v * v;
        }

        /// <summary>FBX Lcl Rotation (XYZ euler, degrees) that gives the local rotation q (FBX space) under pre/post rotation.</summary>
        static Vec3 FbxEuler(DQ pre, Quat q, DQ post)
        {
            return (pre.Conj * DQ.Of(q) * post).ToQuat().ToEulerXYZ();
        }

        /// <summary>The profile's rest skeleton in world space (root at origin, unscaled), meters.</summary>
        static Vec3[] ProfileRestWorld(AvatarProfile p)
        {
            int n = p.Nodes.Count;
            var pos = new Vec3[n];
            var rot = new Quat[n];
            var scl = new Vec3[n];
            foreach (int i in p.TopoOrder())
            {
                var nd = p.Nodes[i];
                if (nd.Parent < 0)
                {
                    pos[i] = Vec3.Zero;
                    rot[i] = Quat.Identity;
                    scl[i] = Vec3.One;
                    continue;
                }

                int pa = nd.Parent;
                pos[i] = pos[pa] + rot[pa] * Vec3.Scale(scl[pa], nd.LocalPos);
                rot[i] = (rot[pa] * nd.LocalRot).Normalized;
                scl[i] = Vec3.Scale(scl[pa], nd.LocalScale);
            }

            return pos;
        }

        /// <summary>
        ///     Gives every material its color texture as DiffuseColor, which is what Blender's and Unity's FBX importers show: the
        ///     material's own diffuse texture, else the texture named like the material next to the avatar's FBX (the avatar
        ///     library keeps the ones its materials use; for a Unity model, its package folder), else its emission texture. The
        ///     diffuse color becomes white (Unity multiplies it in). The images are copied into <paramref name="textureDir" />
        ///     (the
        ///     export's shared textures folder; not when they already are in the same Unity project as the export) and referenced
        ///     by relative + absolute path, which Blender and Unity both follow; optionally embedded too. Dropped: links to
        ///     textures that can't be found, and an emission map that is the color texture itself (one test avatar's hair: black
        ///     diffuse +
        ///     its texture as emission, which glowed). Only this copy changes, never the source FBX.
        /// </summary>
        static void AttachTextures(FbxNode objs, FbxNode conns, Ctx ctx, string srcFbx, string outDir,
            string textureDir, bool embed, FbxExportResult res)
        {
            // the export's Unity project (its Assets folder), if any: textures already in it are used where they are
            string assets = null;
            for (var d = new DirectoryInfo(outDir); d != null; d = d.Parent)
                if (d.Name.Equals("Assets", StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(Path.Combine(d.Parent?.FullName ?? "", "ProjectSettings")))
                {
                    assets = d.FullName.TrimEnd('\\') + "\\";
                    break;
                }

            var copiedFrom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // copy -> source
            int copied = 0;

            // where the export's FBX points for an image: the image itself (Unity project) or its copy in the textures folder
            string Deliver(string f)
            {
                if (textureDir == null || (assets != null &&
                                           Path.GetFullPath(f).StartsWith(assets, StringComparison.OrdinalIgnoreCase)))
                    return f;
                string name = Path.GetFileName(f), to = Path.Combine(textureDir, name);
                for (int k = 2;
                     copiedFrom.TryGetValue(to, out string prev) &&
                     !string.Equals(prev, f, StringComparison.OrdinalIgnoreCase);
                     k++)
                    to = Path.Combine(textureDir,
                        Path.GetFileNameWithoutExtension(name) + "_" + k + Path.GetExtension(name));
                if (copiedFrom.ContainsKey(to)) return to;
                try
                {
                    Directory.CreateDirectory(textureDir);
                    var si = new FileInfo(f);
                    var di = new FileInfo(to);
                    if (!di.Exists || di.Length != si.Length || di.LastWriteTimeUtc < si.LastWriteTimeUtc)
                    {
                        File.Copy(f, to, true);
                        copied++;
                    } // once per avatar and folder

                    copiedFrom[to] = f;
                    return to;
                }
                catch (Exception ex)
                {
                    res.Notes.Add("texture " + name + ": " + ex.Message);
                    return f;
                }
            }

            string fbxDir = Path.GetDirectoryName(Path.GetFullPath(srcFbx));

            bool IsImage(string f)
            {
                return f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                       f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                       f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                       f.EndsWith(".tga", StringComparison.OrdinalIgnoreCase);
            }

            // the images to choose from: next to the FBX when it has its own (library entries), else its package folder
            List<string> files = null;
            foreach (string root in new[] { fbxDir, Path.GetDirectoryName(fbxDir) }.Where(d =>
                         !string.IsNullOrEmpty(d) && Directory.Exists(d)))
            {
                try
                {
                    files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories).Where(IsImage).Take(5000)
                        .ToList();
                }
                catch { files = null; }

                if (files != null && files.Count > 0) break;
            }

            files = files ?? new List<string>();

            bool Plain(string f)
            {
                return f.IndexOf("Another", StringComparison.OrdinalIgnoreCase) < 0 &&
                       f.IndexOf("Mask", StringComparison.OrdinalIgnoreCase) < 0;
            }

            string ByStem(string stem)
            {
                return string.IsNullOrEmpty(stem)
                    ? null
                    : files.Where(f =>
                            Path.GetFileNameWithoutExtension(f).Equals(stem, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => Plain(f) ? 0 : 1).ThenBy(f => f.Length).FirstOrDefault();
            }

            string Find(string file)
            {
                if (string.IsNullOrEmpty(file)) return null;
                string name = file.Replace('\\', '/').Split('/').Last();
                if (name.Length == 0) return null;
                try
                {
                    if (Path.IsPathRooted(file) && File.Exists(file) && IsImage(file)) return file;
                }
                catch { }

                try
                {
                    string rel = Path.Combine(fbxDir, file);
                    if (File.Exists(rel) && IsImage(rel)) return Path.GetFullPath(rel);
                }
                catch { }

                // the same name, else the same name with another extension (a .psd the author painted, shipped as .png)
                return files.Where(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase))
                           .OrderBy(f => Plain(f) ? 0 : 1).ThenBy(f => f.Length).FirstOrDefault()
                       ?? ByStem(Path.GetFileNameWithoutExtension(name));
            }

            string Rel(string abs)
            {
                try
                {
                    if (!string.Equals(Path.GetPathRoot(abs), Path.GetPathRoot(outDir),
                            StringComparison.OrdinalIgnoreCase)) return abs;
                    var a = new Uri(Path.GetFullPath(abs));
                    var b = new Uri(outDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar);
                    return Uri.UnescapeDataString(b.MakeRelativeUri(a).ToString())
                        .Replace('/', Path.DirectorySeparatorChar);
                }
                catch { return abs; }
            }

            var byId = objs.Children.ToDictionary(o => o.Id(), o => o);

            bool Link(FbxNode c)
            {
                return c.Props.Count >= 3 && c.Props[1] is long && c.Props[2] is long;
            }

            long Child(FbxNode c)
            {
                return (long)c.Props[1];
            }

            long Parent(FbxNode c)
            {
                return (long)c.Props[2];
            }

            string Prop(FbxNode c)
            {
                return c.Props.Count > 3 ? c.Props[3] as string ?? "" : "";
            }

            FbxNode VideoOf(FbxNode tex)
            {
                foreach (var c in conns.Children)
                    if (Link(c) && Parent(c) == tex.Id() && byId.TryGetValue(Child(c), out var v) && v.Name == "Video")
                        return v;
                return null;
            }

            var fileOf = new Dictionary<long, string>(); // texture id -> its image (null: not found)

            string FileOfTexture(FbxNode tex)
            {
                if (fileOf.TryGetValue(tex.Id(), out string f)) return f;
                var vid = VideoOf(tex);
                f = Find(tex.Child("FileName")?.Props.FirstOrDefault() as string) ??
                    Find(tex.Child("RelativeFilename")?.Props.FirstOrDefault() as string)
                    ?? Find(vid?.Child("Filename")?.Props.FirstOrDefault() as string) ??
                    Find(vid?.Child("RelativeFilename")?.Props.FirstOrDefault() as string);
                fileOf[tex.Id()] = f;
                return f;
            }

            var texByFile = new Dictionary<string, FbxNode>(StringComparer.OrdinalIgnoreCase);

            FbxNode NewTexture(string file)
            {
                if (texByFile.TryGetValue(file, out var t)) return t;
                string nm = Path.GetFileNameWithoutExtension(file);
                long vid = ctx.Id(), tid = ctx.Id();
                var v = objs.Add("Video", vid, FbxBinaryWriter.Name(nm, "Video"), "Clip");
                v.Add("Type", "Clip");
                v.Add("Properties70").P("Path", "KString", "XRefUrl", "", file);
                v.Add("UseMipMap", 0);
                v.Add("Filename", file);
                v.Add("RelativeFilename", Rel(file));
                t = objs.Add("Texture", tid, FbxBinaryWriter.Name(nm, "Texture"), "");
                t.Add("Type", "TextureVideoClip");
                t.Add("Version", 202);
                t.Add("TextureName", FbxBinaryWriter.Name(nm, "Texture"));
                t.Add("Properties70").P("UseMaterial", "bool", "", "", 1);
                t.Add("Media", FbxBinaryWriter.Name(nm, "Video"));
                t.Add("FileName", file);
                t.Add("RelativeFilename", Rel(file));
                t.Add("ModelUVTranslation", 0.0, 0.0);
                t.Add("ModelUVScaling", 1.0, 1.0);
                t.Add("Texture_Alpha_Source", "None");
                t.Add("Cropping", 0, 0, 0, 0);
                byId[vid] = v;
                byId[tid] = t;
                ctx.OO(vid, tid);
                fileOf[tid] = file;
                texByFile[file] = t;
                return t;
            }

            int textured = 0, untextured = 0, dropped = 0;
            var dropLinks = new HashSet<FbxNode>();
            var addDiffuse = new List<Tuple<long, long>>(); // texture -> material
            foreach (var m in objs.ChildrenNamed("Material").ToList())
            {
                var mine = conns.Children.Where(c =>
                        Link(c) && Parent(c) == m.Id() && byId.TryGetValue(Child(c), out var tn) &&
                        tn.Name == "Texture")
                    .ToList();
                var diffLink = mine.FirstOrDefault(c => Prop(c) == "DiffuseColor");
                string color = diffLink != null ? FileOfTexture(byId[Child(diffLink)]) : null;
                if (color == null) color = ByStem(m.ObjName());
                if (color == null)
                    color = mine.Where(c => Prop(c) == "EmissiveColor").Select(c => FileOfTexture(byId[Child(c)]))
                        .FirstOrDefault(f => f != null);
                foreach (var c in mine)
                {
                    string f = FileOfTexture(byId[Child(c)]);
                    bool sameAsColor = f != null && color != null && string.Equals(Path.GetFullPath(f),
                        Path.GetFullPath(color), StringComparison.OrdinalIgnoreCase);
                    if (f == null || (c != diffLink && Prop(c) == "EmissiveColor" && sameAsColor))
                    {
                        dropLinks.Add(c);
                        dropped++;
                    }
                }

                if (color == null)
                {
                    untextured++;
                    continue;
                }

                if (diffLink == null || dropLinks.Contains(diffLink))
                    addDiffuse.Add(Tuple.Create(NewTexture(color).Id(), m.Id()));
                SetP(m, "DiffuseColor", "Color", "", "A", 1.0, 1.0, 1.0);
                SetP(m, "DiffuseFactor", "Number", "", "A", 1.0);
                textured++;
            }

            conns.Children.RemoveAll(dropLinks.Contains);
            foreach (var a in addDiffuse) ctx.OP(a.Item1, a.Item2, "DiffuseColor");
            // textures nothing uses any more, then videos no texture uses
            var texUsed = new HashSet<long>(conns.Children
                .Where(c => Link(c) && byId.TryGetValue(Child(c), out var cn) && cn.Name == "Texture").Select(Child));
            var deadTex = new HashSet<long>(objs.Children.Where(o => o.Name == "Texture" && !texUsed.Contains(o.Id()))
                .Select(o => o.Id()));
            conns.Children.RemoveAll(c => Link(c) && (deadTex.Contains(Parent(c)) || deadTex.Contains(Child(c))));
            var videoUsed = new HashSet<long>(conns.Children
                .Where(c => Link(c) && byId.TryGetValue(Child(c), out var cn) && cn.Name == "Video").Select(Child));
            objs.Children.RemoveAll(o =>
                (o.Name == "Texture" && deadTex.Contains(o.Id())) ||
                (o.Name == "Video" && !videoUsed.Contains(o.Id())));

            // the images where the FBX points (copied into the textures folder), paths absolute + relative, embedded if asked
            long bytes = 0;
            int images = 0;
            var data = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in objs.ChildrenNamed("Texture").ToList())
            {
                string f = FileOfTexture(t);
                if (f == null) continue;
                f = Deliver(f);
                (t.Child("FileName") ?? t.Add("FileName", "")).Props[0] = f;
                (t.Child("RelativeFilename") ?? t.Add("RelativeFilename", "")).Props[0] = Rel(f);
                var v = VideoOf(t);
                if (v == null) continue;
                (v.Child("Filename") ?? v.Add("Filename", "")).Props[0] = f;
                (v.Child("RelativeFilename") ?? v.Add("RelativeFilename", "")).Props[0] = Rel(f);
                SetP(v, "Path", "KString", "XRefUrl", "", f);
                v.Children.RemoveAll(c => c.Name == "Content");
                if (!embed) continue;
                if (!data.TryGetValue(f, out byte[] b))
                {
                    try { b = File.ReadAllBytes(f); }
                    catch (Exception ex)
                    {
                        res.Notes.Add("texture " + Path.GetFileName(f) + ": " + ex.Message);
                        b = null;
                    }

                    data[f] = b;
                    if (b != null)
                    {
                        bytes += b.Length;
                        images++;
                    }
                }

                if (b != null) v.Add("Content", new FbxRaw(b));
            }

            res.TexturedMaterials = textured;
            res.UntexturedMaterials = untextured;
            res.EmbeddedImages = images;
            res.EmbeddedBytes = bytes;
            res.TextureDir = textureDir;
            res.CopiedImages = copied;
            res.Notes.Add($"textures: {textured} of {textured + untextured} materials" + (textureDir != null
                              ? (copiedFrom.Count > 0
                                    ? $", {copiedFrom.Count} images in {textureDir}" + (copied < copiedFrom.Count
                                        ? $" ({copiedFrom.Count - copied} already there)"
                                        : "")
                                    : "") +
                                (copiedFrom.Count < texByFileCount()
                                    ? (copiedFrom.Count > 0 ? ", the rest" : ",") +
                                      " used where they are in the Unity project"
                                    : "")
                              : ", referenced where they are") +
                          (embed ? $", {images} embedded ({bytes / 1048576.0:0.0} MB)" : "") +
                          (dropped > 0 ? $", {dropped} unusable texture links dropped" : ""));

            int texByFileCount()
            {
                return objs.ChildrenNamed("Texture").Select(FileOfTexture).Where(x => x != null)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            }
        }

        /// <summary>Definitions: every object type's count from the Objects section (types added by the export get templates).</summary>
        static void RecountDefinitions(List<FbxNode> top, FbxNode objs)
        {
            var defs = top.FirstOrDefault(n => n.Name == "Definitions");
            if (defs == null) return;
            var counts = objs.Children.GroupBy(o => o.Name).ToDictionary(g => g.Key, g => g.Count());
            var templates = new Dictionary<string, string>
            {
                ["AnimationStack"] = "FbxAnimStack", ["AnimationLayer"] = "FbxAnimLayer",
                ["AnimationCurveNode"] = "FbxAnimCurveNode", ["Model"] = "FbxNode", ["NodeAttribute"] = "FbxSkeleton",
                ["Texture"] = "FbxFileTexture", ["Video"] = "FbxVideo"
            };
            var have = new HashSet<string>();
            foreach (var ot in defs.ChildrenNamed("ObjectType").ToList())
            {
                string type = ot.Props.Count > 0 ? ot.Props[0] as string : null;
                if (type == null) continue;
                have.Add(type);
                var cnt = ot.Child("Count");
                if (type == "GlobalSettings") continue;
                int c = counts.TryGetValue(type, out int cc) ? cc : 0;
                if (cnt != null) cnt.Props[0] = c;
            }

            foreach (var kv in counts)
            {
                if (have.Contains(kv.Key)) continue;
                var o = defs.Add("ObjectType", kv.Key);
                o.Add("Count", kv.Value);
                if (templates.TryGetValue(kv.Key, out string tpl)) o.Add("PropertyTemplate", tpl).Add("Properties70");
            }

            var total = defs.Child("Count");
            if (total != null)
                total.Props[0] = defs.ChildrenNamed("ObjectType")
                    .Sum(ot => ot.Child("Count")?.Props.FirstOrDefault() is int c ? c : 0);
        }

        /// <summary>Double-precision affine 4x4 (column-major like FBX: m[c*4+r]).</summary>
        struct D4
        {
            public double[] m;
            public static D4 Identity => new() { m = new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 } };

            public static D4 T(double x, double y, double z)
            {
                var r = Identity;
                r.m[12] = x;
                r.m[13] = y;
                r.m[14] = z;
                return r;
            }

            public static D4 S(double x, double y, double z)
            {
                var r = Identity;
                r.m[0] = x;
                r.m[5] = y;
                r.m[10] = z;
                return r;
            }

            public static D4 R(DQ q)
            {
                double x = q.x, y = q.y, z = q.z, w = q.w;
                var r = Identity;
                r.m[0] = 1 - 2 * (y * y + z * z);
                r.m[1] = 2 * (x * y + w * z);
                r.m[2] = 2 * (x * z - w * y);
                r.m[4] = 2 * (x * y - w * z);
                r.m[5] = 1 - 2 * (x * x + z * z);
                r.m[6] = 2 * (y * z + w * x);
                r.m[8] = 2 * (x * z + w * y);
                r.m[9] = 2 * (y * z - w * x);
                r.m[10] = 1 - 2 * (x * x + y * y);
                return r;
            }

            public static D4 operator *(D4 a, D4 b)
            {
                double[] r = new double[16];
                for (int c = 0; c < 4; c++)
                for (int rr = 0; rr < 4; rr++)
                {
                    double s = 0;
                    for (int k = 0; k < 4; k++) s += a.m[k * 4 + rr] * b.m[c * 4 + k];
                    r[c * 4 + rr] = s;
                }

                return new D4 { m = r };
            }

            public double[] Origin => new[] { m[12], m[13], m[14] };
        }

        /// <summary>Double-precision quaternion.</summary>
        struct DQ
        {
            public readonly double x;
            public readonly double y;
            public readonly double z;
            public readonly double w;

            public DQ(double x, double y, double z, double w)
            {
                this.x = x;
                this.y = y;
                this.z = z;
                this.w = w;
            }

            public static DQ operator *(DQ a, DQ b)
            {
                return new DQ(
                    a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
                    a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
                    a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
                    a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);
            }

            public DQ Conj => new(-x, -y, -z, w);

            public static DQ Axis(int axis, double deg)
            {
                double h = deg * Math.PI / 360.0, s = Math.Sin(h);
                return new DQ(axis == 0 ? s : 0, axis == 1 ? s : 0, axis == 2 ? s : 0, Math.Cos(h));
            }

            public static DQ Of(Quat q)
            {
                return new DQ(q.x, q.y, q.z, q.w);
            }

            public Quat ToQuat()
            {
                double n = Math.Sqrt(x * x + y * y + z * z + w * w);
                return new Quat((float)(x / n), (float)(y / n), (float)(z / n), (float)(w / n));
            }
        }
    }
}
