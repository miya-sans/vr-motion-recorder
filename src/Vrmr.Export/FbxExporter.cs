using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace VRMR
{
    public sealed class FbxExportOptions
    {
        public string TakeName = "Take 001";
        public bool IncludeFace = true;
        public string FaceMeshName = "Body";
        public Dictionary<string, string> ShapeRename; // output shape -> avatar blendshape name
        public bool IncludeCamera = true;
        public bool IncludeTrackers = false;
        public int FrameStep = 1;
        /// <summary>Write the motion onto the avatar's own FBX (its skinned meshes, materials and face-tracking shapes) when
        /// one is found for the clip's avatar; otherwise (or when false) the armature + a face carrier mesh.</summary>
        public bool AvatarMesh = true;
        /// <summary>Avatar FBX: each material's color texture is copied into this folder (once; the export's shared
        /// textures\&lt;avatar&gt; folder) and referenced from the FBX by relative path, which Blender and Unity both follow.
        /// Textures already inside the same Unity project as the export are referenced where they are. Null: the original
        /// files are referenced.</summary>
        public string TextureDir;
        /// <summary>Also embed the images in the FBX (Blender uses them wherever the file goes; Unity only after its manual
        /// "Extract Textures", so the files are what Unity uses). Off by default: it adds the images' size to every FBX.</summary>
        public bool EmbedTextures = false;
        /// <summary>The avatar FBX to use (null = the clip avatar's: the library's face-tracking FBX, else the Unity model).</summary>
        public string AvatarFbx;
    }

    /// <summary>What an FBX export wrote.</summary>
    public sealed class FbxExportResult
    {
        /// <summary>"avatar" (the avatar's own FBX with the motion) or "armature" (armature + face carrier).</summary>
        public string Mode = "armature";
        public string SourceFbx = "";
        public int Meshes, Vertices, AnimatedBones, FaceShapes, FaceChannels;
        public int TexturedMaterials, UntexturedMaterials, EmbeddedImages, CopiedImages; public long EmbeddedBytes; public string TextureDir;
        public readonly List<string> Notes = new List<string>();
        public override string ToString() => Mode == "avatar"
            ? $"{Path.GetFileName(SourceFbx)}: {Meshes} meshes, {Vertices} vertices, {AnimatedBones} bones animated, {FaceShapes} face shapes on {FaceChannels} shape keys"
            : $"armature only ({AnimatedBones} bones), face carrier {FaceShapes} shapes";
    }

    /// <summary>
    /// Writes a BakedClip as a binary FBX: the avatar's own armature (exact bone names, hierarchy and rest pose),
    /// humanoid bone rotations + hips translation as one take, an optional face "carrier" mesh whose blendshape
    /// channels carry the face animation, plus optional camera/tracker empties.
    /// Y-up, meters (UnitScaleFactor 100), right-handed (Unity's left-handed data is mirrored on X; Unity mirrors back on import).
    /// </summary>
    public static partial class FbxExporter
    {
        const long KTimeSecond = 46186158000L;

        sealed class Ctx
        {
            long _next = 1000000;
            /// <summary>Ids already taken (when adding to an existing file).</summary>
            public HashSet<long> Used;
            public long Id() { while (Used != null && Used.Contains(_next)) _next++; return _next++; }
            public FbxNode Objects, Connections;
            public int ModelCount, AttrCount, CurveNodeCount, CurveCount, GeomCount, DeformerCount;
            public void OO(long child, long parent) => Connections.Add("C", "OO", child, parent);
            public void OP(long child, long parent, string prop) => Connections.Add("C", "OP", child, parent, prop);
        }

        static double[] V(Vec3 v) => new double[] { v.x, v.y, v.z };

        /// <summary>Writes the clip as an FBX: on the avatar's own FBX when there is one (<see cref="FbxExportOptions.AvatarMesh"/>),
        /// else the armature + face carrier. Never changes the source FBX.</summary>
        public static FbxExportResult Export(BakedClip clip, string path, FbxExportOptions opt)
        {
            var res = new FbxExportResult();
            if (opt.AvatarMesh)
            {
                string src = !string.IsNullOrEmpty(opt.AvatarFbx) ? opt.AvatarFbx : SkinnedAvatar.FbxFor(clip.Profile);
                if (src == null) res.Notes.Add("No FBX of the avatar was found (library entry or Unity model): armature + face carrier only.");
                else
                {
                    try
                    {
                        if (ExportOnAvatar(clip, src, path, opt, res)) return res;
                    }
                    catch (Exception ex)
                    {
                        res.Notes.Add($"Could not use the avatar's FBX ({Path.GetFileName(src)}): {ex.Message}. Armature + face carrier only.");
                        AppLog.Warn("FBX export on the avatar's FBX failed: " + ex);
                    }
                    res.Mode = "armature"; res.Meshes = res.Vertices = res.AnimatedBones = res.FaceShapes = res.FaceChannels = 0;
                }
            }
            ExportArmature(clip, path, opt, res);
            return res;
        }

        static void ExportArmature(BakedClip clip, string path, FbxExportOptions opt, FbxExportResult res)
        {
            var prof = clip.Profile;
            var ctx = new Ctx { Objects = new FbxNode("Objects"), Connections = new FbxNode("Connections") };
            int frames = clip.Frames;
            int step = Math.Max(1, opt.FrameStep);
            var frameIdx = new List<int>();
            for (int f = 0; f < frames; f += step) frameIdx.Add(f);
            if (frameIdx[frameIdx.Count - 1] != frames - 1) frameIdx.Add(frames - 1);
            long[] ktimes = frameIdx.Select(f => (long)Math.Round(f / (double)clip.Fps * KTimeSecond)).ToArray();
            long stop = ktimes[ktimes.Length - 1];

            // ---- animation stack / layer
            long stackId = ctx.Id(), layerId = ctx.Id();
            var stack = ctx.Objects.Add("AnimationStack", stackId, FbxBinaryWriter.Name(opt.TakeName, "AnimStack"), "");
            var sp = stack.Add("Properties70");
            sp.P("LocalStart", "KTime", "Time", "", 0L); sp.P("LocalStop", "KTime", "Time", "", stop);
            sp.P("ReferenceStart", "KTime", "Time", "", 0L); sp.P("ReferenceStop", "KTime", "Time", "", stop);
            ctx.Objects.Add("AnimationLayer", layerId, FbxBinaryWriter.Name("BaseLayer", "AnimLayer"), "");
            ctx.OO(layerId, stackId);

            // ---- skeleton
            var arm = prof.ArmatureNodes();
            int root = prof.RootIndex;
            var inArm = new HashSet<int>(arm);
            var isBone = new bool[prof.Nodes.Count];
            int hips = prof.Human[HumanBones.Hips];
            foreach (int i in arm)
                for (int a = i; a >= 0; a = prof.Nodes[a].Parent) if (a == hips) { isBone[i] = true; break; }

            int nn = prof.Nodes.Count;
            var wp = new Vec3[nn]; var wr = new Quat[nn]; var lp = new Vec3[nn]; var lr = new Quat[nn];
            // sample local transforms for animated nodes
            var animated = arm.Where(i => i != root && (prof.HumanOfNode(i) >= 0)).ToList();
            var rotKeys = animated.ToDictionary(i => i, i => new Vec3[frameIdx.Count]);
            var posKey = new Vec3[frameIdx.Count];
            for (int k = 0; k < frameIdx.Count; k++)
            {
                clip.EvaluateLocal(frameIdx[k], wp, wr, lp, lr);
                foreach (int i in animated)
                {
                    var e = MathX.UnityToFbxRot(lr[i]).ToEulerXYZ();
                    rotKeys[i][k] = k > 0 ? MathX.UnwrapEuler(e, rotKeys[i][k - 1]) : e;
                }
                if (hips >= 0) posKey[k] = MathX.UnityToFbxPos(lp[hips]);
            }

            var modelIds = new Dictionary<int, long>();
            foreach (int i in arm)
            {
                if (i == root) continue;
                var nd = prof.Nodes[i];
                long mid = ctx.Id();
                modelIds[i] = mid;
                string type = isBone[i] ? "LimbNode" : "Null";
                var m = ctx.Objects.Add("Model", mid, FbxBinaryWriter.Name(nd.Name, "Model"), type);
                m.Add("Version", 232);
                var p = m.Add("Properties70");
                var rt = MathX.UnityToFbxPos(nd.LocalPos);
                var rr = MathX.UnityToFbxRot(nd.LocalRot).ToEulerXYZ();
                p.P("Lcl Translation", "Lcl Translation", "", "A", (double)rt.x, (double)rt.y, (double)rt.z);
                p.P("Lcl Rotation", "Lcl Rotation", "", "A", (double)rr.x, (double)rr.y, (double)rr.z);
                p.P("Lcl Scaling", "Lcl Scaling", "", "A", (double)nd.LocalScale.x, (double)nd.LocalScale.y, (double)nd.LocalScale.z);
                p.P("DefaultAttributeIndex", "int", "Integer", "", 0);
                p.P("InheritType", "enum", "", "", 1);
                m.Add("Shading", true);
                m.Add("Culling", "CullingOff");
                ctx.ModelCount++;
                if (isBone[i])
                {
                    long aid = ctx.Id();
                    var na = ctx.Objects.Add("NodeAttribute", aid, FbxBinaryWriter.Name(nd.Name, "NodeAttribute"), "LimbNode");
                    na.Add("Properties70").P("Size", "double", "Number", "", 3.0);
                    na.Add("TypeFlags", "Skeleton");
                    ctx.OO(aid, mid);
                    ctx.AttrCount++;
                }
                int parent = nd.Parent;
                ctx.OO(mid, parent == root || parent < 0 ? 0L : modelIds[parent]);
            }

            res.AnimatedBones = animated.Count;
            // ---- bone curves
            foreach (int i in animated)
            {
                if (!modelIds.TryGetValue(i, out long mid)) continue;
                var keys = rotKeys[i];
                AddCurveNode(ctx, layerId, mid, "R", "Lcl Rotation", ktimes, keys);
                if (i == hips) AddCurveNode(ctx, layerId, mid, "T", "Lcl Translation", ktimes, posKey);
            }
            // following VRChat's avatar scaling (BakedClip.ScaleCurve): the armature's top nodes scale with it
            if (clip.ScaleCurve != null)
                foreach (int i in arm)
                {
                    if (i == root || prof.Nodes[i].Parent != root || !modelIds.TryGetValue(i, out long mid)) continue;
                    var ls = prof.Nodes[i].LocalScale;
                    var sk = frameIdx.Select(f => ls * (clip.ScaleAt(f) / Math.Max(1e-6f, clip.Scale))).ToArray();
                    AddCurveNode(ctx, layerId, mid, "S", "Lcl Scaling", ktimes, sk);
                }

            // ---- face carrier mesh
            if (opt.IncludeFace && clip.Face.Count > 0)
            {
                var shapes = clip.Face.Keys.ToList();
                res.FaceShapes = shapes.Count; res.FaceChannels = shapes.Count;
                float eyeY = prof.EyeHeight * clip.Scale;
                long gid = ctx.Id(), mid = ctx.Id(), bsId = ctx.Id();
                var verts = new double[] { -0.005, eyeY, 0.1, 0.005, eyeY, 0.1, 0, eyeY + 0.008, 0.1 };
                var g = ctx.Objects.Add("Geometry", gid, FbxBinaryWriter.Name(opt.FaceMeshName, "Geometry"), "Mesh");
                g.Add("Properties70");
                g.Add("GeometryVersion", 124);
                g.Add("Vertices", verts);
                g.Add("PolygonVertexIndex", new[] { 0, 1, -3 });
                var len = g.Add("LayerElementNormal", 0);
                len.Add("Version", 102); len.Add("Name", ""); len.Add("MappingInformationType", "ByPolygonVertex"); len.Add("ReferenceInformationType", "Direct");
                len.Add("Normals", new double[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 });
                var layer = g.Add("Layer", 0);
                layer.Add("Version", 100);
                var le = layer.Add("LayerElement"); le.Add("Type", "LayerElementNormal"); le.Add("TypedIndex", 0);
                ctx.GeomCount++;
                var m = ctx.Objects.Add("Model", mid, FbxBinaryWriter.Name(opt.FaceMeshName, "Model"), "Mesh");
                m.Add("Version", 232);
                var mp = m.Add("Properties70");
                mp.P("DefaultAttributeIndex", "int", "Integer", "", 0);
                mp.P("InheritType", "enum", "", "", 1);
                m.Add("Shading", true); m.Add("Culling", "CullingOff");
                ctx.ModelCount++;
                ctx.OO(mid, 0L); ctx.OO(gid, mid);
                var bs = ctx.Objects.Add("Deformer", bsId, FbxBinaryWriter.Name(opt.FaceMeshName, "Deformer"), "BlendShape");
                bs.Add("Version", 100);
                ctx.DeformerCount++;
                ctx.OO(bsId, gid);
                int si = 0;
                foreach (var s in shapes)
                {
                    string name = opt.ShapeRename != null && opt.ShapeRename.TryGetValue(s, out var rn) ? rn : s;
                    long chId = ctx.Id(), shId = ctx.Id();
                    var ch = ctx.Objects.Add("Deformer", chId, FbxBinaryWriter.Name(name, "SubDeformer"), "BlendShapeChannel");
                    ch.Add("Version", 100);
                    ch.Add("DeformPercent", 0.0);
                    ch.Add("FullWeights", new double[] { 100.0 });
                    ch.Add("Properties70").P("DeformPercent", "Number", "", "A", 0.0);
                    ctx.DeformerCount++;
                    var sh = ctx.Objects.Add("Geometry", shId, FbxBinaryWriter.Name(name, "Geometry"), "Shape");
                    sh.Add("Version", 100);
                    sh.Add("Indexes", new[] { si % 3 });
                    sh.Add("Vertices", new double[] { 0, 0.0002 * (1 + si / 3 % 7), 0 });
                    sh.Add("Normals", new double[] { 0, 0, 0 });
                    ctx.GeomCount++;
                    ctx.OO(chId, bsId); ctx.OO(shId, chId);
                    var vals = clip.Face[s];
                    var pct = frameIdx.Select(f => vals[f] * 100f).ToArray();
                    long cn = ctx.Id(), cv = ctx.Id();
                    var cnode = ctx.Objects.Add("AnimationCurveNode", cn, FbxBinaryWriter.Name("DeformPercent", "AnimCurveNode"), "");
                    cnode.Add("Properties70").P("d|DeformPercent", "Number", "", "A", (double)pct[0]);
                    ctx.CurveNodeCount++;
                    ctx.OO(cn, layerId); ctx.OP(cn, chId, "DeformPercent");
                    AddCurve(ctx, cv, ktimes, pct);
                    ctx.OP(cv, cn, "d|DeformPercent");
                    si++;
                }
            }

            // ---- camera / trackers as animated empties (world space)
            if (opt.IncludeCamera && clip.Camera != null) AddEmpty(ctx, layerId, "VRChatCamera", frameIdx, ktimes, f => clip.Camera[f], clip.Scale);
            if (opt.IncludeTrackers)
                foreach (var kv in clip.Trackers) { var arr = kv.Value; AddEmpty(ctx, layerId, "Tracker_" + kv.Key, frameIdx, ktimes, f => arr[f], clip.Scale); }

            // ---- assemble document
            var top = new List<FbxNode>();
            top.Add(Header());
            top.Add(new FbxNode("FileId", new FbxRaw(FbxBinaryWriter.FileId)));
            top.Add(new FbxNode("CreationTime", FbxBinaryWriter.CreationTime));
            top.Add(new FbxNode("Creator", "VR Motion Recorder"));
            top.Add(GlobalSettings(clip.Fps, stop));
            var docs = new FbxNode("Documents");
            docs.Add("Count", 1);
            var doc = docs.Add("Document", ctx.Id(), "Scene", "Scene");
            var dp = doc.Add("Properties70");
            dp.P("SourceObject", "object", "", "");
            dp.P("ActiveAnimStackName", "KString", "", "", opt.TakeName);
            doc.Add("RootNode", 0L);
            top.Add(docs);
            top.Add(new FbxNode("References"));
            top.Add(Definitions(ctx));
            top.Add(ctx.Objects);
            top.Add(ctx.Connections);
            var takes = new FbxNode("Takes");
            takes.Add("Current", opt.TakeName);
            var take = takes.Add("Take", opt.TakeName);
            take.Add("FileName", opt.TakeName.Replace(' ', '_') + ".tak");
            take.Add("LocalTime", 0L, stop);
            take.Add("ReferenceTime", 0L, stop);
            top.Add(takes);
            FbxBinaryWriter.Write(path, top);
        }

        /// <summary>An animated empty at the scene root (world pose per frame, Unity space; positions divided by metersPerUnit).</summary>
        static void AddEmpty(Ctx ctx, long layerId, string name, List<int> frameIdx, long[] ktimes, Func<int, Pose> get, double metersPerUnit)
        {
            long mid = ctx.Id();
            var m = ctx.Objects.Add("Model", mid, FbxBinaryWriter.Name(name, "Model"), "Null");
            m.Add("Version", 232);
            float inv = (float)(1.0 / metersPerUnit);
            var first = get(frameIdx[0]);
            var p0 = MathX.UnityToFbxPos(first.pos) * inv; var r0 = MathX.UnityToFbxRot(first.rot).ToEulerXYZ();
            var p = m.Add("Properties70");
            p.P("Lcl Translation", "Lcl Translation", "", "A", (double)p0.x, (double)p0.y, (double)p0.z);
            p.P("Lcl Rotation", "Lcl Rotation", "", "A", (double)r0.x, (double)r0.y, (double)r0.z);
            m.Add("Shading", true); m.Add("Culling", "CullingOff");
            ctx.ModelCount++;
            ctx.OO(mid, 0L);
            var tk = new Vec3[frameIdx.Count]; var rk = new Vec3[frameIdx.Count];
            for (int k = 0; k < frameIdx.Count; k++)
            {
                var ps = get(frameIdx[k]);
                tk[k] = MathX.UnityToFbxPos(ps.pos) * inv;
                var e = MathX.UnityToFbxRot(ps.rot).ToEulerXYZ();
                rk[k] = k > 0 ? MathX.UnwrapEuler(e, rk[k - 1]) : e;
            }
            AddCurveNode(ctx, layerId, mid, "T", "Lcl Translation", ktimes, tk);
            AddCurveNode(ctx, layerId, mid, "R", "Lcl Rotation", ktimes, rk);
        }

        static void AddCurveNode(Ctx ctx, long layerId, long modelId, string shortName, string prop, long[] ktimes, Vec3[] keys)
        {
            long cn = ctx.Id();
            var node = ctx.Objects.Add("AnimationCurveNode", cn, FbxBinaryWriter.Name(shortName, "AnimCurveNode"), "");
            var p = node.Add("Properties70");
            p.P("d|X", "Number", "", "A", (double)keys[0].x);
            p.P("d|Y", "Number", "", "A", (double)keys[0].y);
            p.P("d|Z", "Number", "", "A", (double)keys[0].z);
            ctx.CurveNodeCount++;
            ctx.OO(cn, layerId);
            ctx.OP(cn, modelId, prop);
            for (int c = 0; c < 3; c++)
            {
                long cv = ctx.Id();
                var vals = keys.Select(k => c == 0 ? k.x : c == 1 ? k.y : k.z).ToArray();
                AddCurve(ctx, cv, ktimes, vals);
                ctx.OP(cv, cn, c == 0 ? "d|X" : c == 1 ? "d|Y" : "d|Z");
            }
        }

        static void AddCurve(Ctx ctx, long id, long[] ktimes, float[] vals)
        {
            var c = ctx.Objects.Add("AnimationCurve", id, FbxBinaryWriter.Name("", "AnimCurve"), "");
            c.Add("Default", (double)vals[0]);
            c.Add("KeyVer", 4008);
            c.Add("KeyTime", ktimes);
            c.Add("KeyValueFloat", vals);
            c.Add("KeyAttrFlags", new[] { 0x00000004 | 0x00000100 | 0x00002000 | 0x00004000 }); // linear
            c.Add("KeyAttrDataFloat", new[] { 0f, 0f, 9.419963346924634e-30f, 0f });
            c.Add("KeyAttrRefCount", new[] { ktimes.Length });
            ctx.CurveCount++;
        }

        static FbxNode Header()
        {
            var h = new FbxNode("FBXHeaderExtension");
            h.Add("FBXHeaderVersion", 1003);
            h.Add("FBXVersion", 7400);
            h.Add("EncryptionType", 0);
            var ct = h.Add("CreationTimeStamp");
            var now = DateTime.Now;
            ct.Add("Version", 1000); ct.Add("Year", now.Year); ct.Add("Month", now.Month); ct.Add("Day", now.Day);
            ct.Add("Hour", now.Hour); ct.Add("Minute", now.Minute); ct.Add("Second", now.Second); ct.Add("Millisecond", now.Millisecond);
            h.Add("Creator", "VR Motion Recorder");
            var si = h.Add("SceneInfo", FbxBinaryWriter.Name("GlobalInfo", "SceneInfo"), "UserData");
            si.Add("Type", "UserData");
            si.Add("Version", 100);
            var md = si.Add("MetaData");
            md.Add("Version", 100); md.Add("Title", ""); md.Add("Subject", ""); md.Add("Author", ""); md.Add("Keywords", ""); md.Add("Revision", ""); md.Add("Comment", "");
            var p = si.Add("Properties70");
            p.P("DocumentUrl", "KString", "Url", "", "/foobar.fbx");
            p.P("SrcDocumentUrl", "KString", "Url", "", "/foobar.fbx");
            p.P("Original", "Compound", "", "");
            p.P("Original|ApplicationVendor", "KString", "", "", "VRMR");
            p.P("Original|ApplicationName", "KString", "", "", "VR Motion Recorder");
            p.P("Original|ApplicationVersion", "KString", "", "", "1.0");
            p.P("Original|DateTime_GMT", "DateTime", "", "", "01/01/1970 00:00:00.000");
            p.P("Original|FileName", "KString", "", "", "/foobar.fbx");
            p.P("LastSaved", "Compound", "", "");
            p.P("LastSaved|ApplicationVendor", "KString", "", "", "VRMR");
            p.P("LastSaved|ApplicationName", "KString", "", "", "VR Motion Recorder");
            p.P("LastSaved|ApplicationVersion", "KString", "", "", "1.0");
            p.P("LastSaved|DateTime_GMT", "DateTime", "", "", "01/01/1970 00:00:00.000");
            return h;
        }

        static FbxNode GlobalSettings(float fps, long stop)
        {
            var g = new FbxNode("GlobalSettings");
            g.Add("Version", 1000);
            var p = g.Add("Properties70");
            p.P("UpAxis", "int", "Integer", "", 1); p.P("UpAxisSign", "int", "Integer", "", 1);
            p.P("FrontAxis", "int", "Integer", "", 2); p.P("FrontAxisSign", "int", "Integer", "", 1);
            p.P("CoordAxis", "int", "Integer", "", 0); p.P("CoordAxisSign", "int", "Integer", "", 1);
            p.P("OriginalUpAxis", "int", "Integer", "", 1); p.P("OriginalUpAxisSign", "int", "Integer", "", 1);
            p.P("UnitScaleFactor", "double", "Number", "", 100.0);
            p.P("OriginalUnitScaleFactor", "double", "Number", "", 100.0);
            p.P("AmbientColor", "ColorRGB", "Color", "", 0.0, 0.0, 0.0);
            p.P("DefaultCamera", "KString", "", "", "Producer Perspective");
            int mode = Math.Abs(fps - 30) < 0.01 ? 6 : Math.Abs(fps - 60) < 0.01 ? 3 : Math.Abs(fps - 24) < 0.01 ? 11 : Math.Abs(fps - 120) < 0.01 ? 1 : Math.Abs(fps - 50) < 0.01 ? 4 : 14;
            p.P("TimeMode", "enum", "", "", mode);
            p.P("TimeProtocol", "enum", "", "", 2);
            p.P("SnapOnFrameMode", "enum", "", "", 0);
            p.P("TimeSpanStart", "KTime", "Time", "", 0L);
            p.P("TimeSpanStop", "KTime", "Time", "", stop);
            p.P("CustomFrameRate", "double", "Number", "", (double)fps);
            p.P("CurrentTimeMarker", "int", "Integer", "", -1);
            return g;
        }

        static FbxNode Definitions(Ctx c)
        {
            var d = new FbxNode("Definitions");
            d.Add("Version", 100);
            var list = new List<Tuple<string, int, string>>
            {
                Tuple.Create("GlobalSettings", 1, (string)null),
                Tuple.Create("AnimationStack", 1, "FbxAnimStack"),
                Tuple.Create("AnimationLayer", 1, "FbxAnimLayer"),
                Tuple.Create("Model", c.ModelCount, "FbxNode"),
                Tuple.Create("NodeAttribute", c.AttrCount, "FbxSkeleton"),
                Tuple.Create("AnimationCurveNode", c.CurveNodeCount, "FbxAnimCurveNode"),
                Tuple.Create("AnimationCurve", c.CurveCount, (string)null),
                Tuple.Create("Geometry", c.GeomCount, "FbxMesh"),
                Tuple.Create("Deformer", c.DeformerCount, (string)null),
            };
            list = list.Where(x => x.Item2 > 0).ToList();
            d.Add("Count", list.Sum(x => x.Item2));
            foreach (var x in list)
            {
                var o = d.Add("ObjectType", x.Item1);
                o.Add("Count", x.Item2);
                if (x.Item3 != null) o.Add("PropertyTemplate", x.Item3).Add("Properties70");
            }
            return d;
        }
    }
}
