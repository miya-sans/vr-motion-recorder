using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VRMR
{
    /// <summary>
    ///     Reads a BlendShare asset (Unity YAML ScriptableObject that adds blendshapes to an FBX model, e.g. Triturbo's
    ///     face-tracking add-ons) and merges its shapes into a copy of the original FBX. BlendShare keeps every shape both as
    ///     Unity per-vertex deltas and as FBX control-point deltas; the FBX form is used here, so no vertex remapping is
    ///     needed.
    /// </summary>
    public sealed class BlendShareAsset
    {
        public readonly List<MeshShapes> Meshes = new();

        public string Path = "", Name = "", OriginalGuid = "", DefaultName = "";

        /// <summary>BlendShare assets under the Unity project whose original model is this FBX (matched by the FBX's .meta guid).</summary>
        public static List<string> FindFor(string fbxPath)
        {
            var res = new List<string>();
            try
            {
                string guid = MetaGuid(fbxPath + ".meta");
                if (guid == null) return res;
                string dir = System.IO.Path.GetDirectoryName(fbxPath);
                while (dir != null && !string.Equals(System.IO.Path.GetFileName(dir), "Assets",
                           StringComparison.OrdinalIgnoreCase)) dir = System.IO.Path.GetDirectoryName(dir);
                if (dir == null) return res;
                foreach (string f in Directory.EnumerateFiles(dir, "*.asset", SearchOption.AllDirectories))
                {
                    string head = ReadHead(f, 4096);
                    if (head == null || !head.StartsWith("%YAML") ||
                        head.IndexOf("m_DeformerID: +BlendShare", StringComparison.Ordinal) < 0) continue;
                    int i = head.IndexOf("m_Original:", StringComparison.Ordinal);
                    if (i >= 0 && head.IndexOf("guid: " + guid, i, Math.Min(200, head.Length - i),
                            StringComparison.Ordinal) >= 0) res.Add(f);
                }
            }
            catch (Exception ex) { AppLog.Warn("BlendShare search: " + ex.Message); }

            return res;
        }

        static string ReadHead(string path, int n)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    byte[] b = new byte[Math.Min(n, (int)Math.Min(int.MaxValue, fs.Length))];
                    int r = fs.Read(b, 0, b.Length);
                    return Encoding.UTF8.GetString(b, 0, r);
                }
            }
            catch { return null; }
        }

        public static string MetaGuid(string metaPath)
        {
            if (!File.Exists(metaPath)) return null;
            foreach (string l in File.ReadLines(metaPath))
                if (l.StartsWith("guid: "))
                    return l.Substring(6).Trim();
            return null;
        }

        static string Unquote(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '\'' && s[s.Length - 1] == '\'')
                return s.Substring(1, s.Length - 2).Replace("''", "'");
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                return Regex.Unescape(s.Substring(1, s.Length - 2));
            return s;
        }

        static int[] Hex32(string hex)
        {
            hex = hex.Trim();
            int[] r = new int[hex.Length / 8];
            for (int i = 0; i < r.Length; i++)
            {
                uint v = 0;
                for (int k = 0; k < 4; k++) v |= (uint)Convert.ToByte(hex.Substring(i * 8 + k * 2, 2), 16) << (8 * k);
                r[i] = (int)v;
            }

            return r;
        }

        public static BlendShareAsset Load(string path)
        {
            var a = new BlendShareAsset { Path = path };
            MeshShapes mesh = null;
            ShapeData shape = null;
            bool inFbx = false;
            List<int> pts = null;
            List<double> dl = null;

            void EndFrame()
            {
                if (shape == null || pts == null) return;
                shape.Points = pts.ToArray();
                shape.Delta = dl.ToArray();
                shape.Frames++;
                pts = null;
                dl = null;
            }

            var ci = CultureInfo.InvariantCulture;
            foreach (string raw in File.ReadLines(path))
            {
                string t = raw.TrimStart();
                int ind = raw.Length - t.Length;
                if (ind == 2 && t.StartsWith("m_Name: ")) a.Name = Unquote(t.Substring(8));
                else if (ind == 2 && t.StartsWith("m_DefaultGeneratedAssetName: "))
                    a.DefaultName = Unquote(t.Substring(29));
                else if (ind == 2 && t.StartsWith("m_Original:"))
                {
                    int g = t.IndexOf("guid: ");
                    if (g > 0) a.OriginalGuid = t.Substring(g + 6).Split(',')[0].Trim();
                }
                else if (ind == 2 && t.StartsWith("- m_OriginMesh:"))
                {
                    EndFrame();
                    mesh = new MeshShapes();
                    a.Meshes.Add(mesh);
                    shape = null;
                }
                else if (mesh != null && ind == 4 && t.StartsWith("m_MeshName: "))
                    mesh.MeshName = Unquote(t.Substring(12));
                else if (mesh != null && ind == 4 && t.StartsWith("m_VertexCount: "))
                    int.TryParse(t.Substring(15), out mesh.UnityVertexCount);
                else if (mesh != null && ind == 4 && t.StartsWith("- m_ShapeName: "))
                {
                    EndFrame();
                    shape = new ShapeData { Name = Unquote(t.Substring(15)) };
                    mesh.Shapes.Add(shape);
                    inFbx = false;
                }
                else if (shape != null && ind == 6 && t.StartsWith("m_FbxBlendShapeData:")) inFbx = true;
                else if (shape != null && ind == 6 && t.StartsWith("m_UnityBlendShapeData:"))
                {
                    EndFrame();
                    inFbx = false;
                }
                else if (inFbx && t.StartsWith("- m_PointsIndices:"))
                {
                    EndFrame();
                    pts = new List<int>(Hex32(t.Substring(18)));
                    dl = new List<double>(pts.Count * 3);
                }
                else if (inFbx && dl != null &&
                         (t.StartsWith("- m_X: ") || t.StartsWith("m_Y: ") || t.StartsWith("m_Z: ")))
                    dl.Add(double.Parse(t.Substring(t.IndexOf(':') + 1).Trim(), NumberStyles.Float, ci));
            }

            EndFrame();
            foreach (var m in a.Meshes)
            foreach (var s in m.Shapes)
                if (s.Delta.Length != s.Points.Length * 3)
                    throw new InvalidDataException(
                        $"BlendShare {m.MeshName}/{s.Name}: {s.Points.Length} points but {s.Delta.Length / 3} deltas");
            return a;
        }

        // ------------------------------------------------------------------ FBX merge

        /// <summary>
        ///     Writes a copy of the FBX with the BlendShare shapes added as blendshape channels (shapes the FBX already has
        ///     are kept as they are). Everything else in the file is copied unchanged. Returns a short report.
        /// </summary>
        public static string MergeIntoFbx(string fbxIn, IEnumerable<BlendShareAsset> assets, string fbxOut)
        {
            var top = FbxReader.Read(fbxIn);
            PrepareForWrite(top);
            string report = MergeInto(top, assets);
            FbxBinaryWriter.Write(fbxOut, top);
            return report;
        }

        /// <summary>
        ///     The writer emits FBX 7.4 with the same file id / time / footer as the app's other FBX exports (Unity and
        ///     Blender read them): patches a file read with <see cref="FbxReader" /> to match before it is written again.
        /// </summary>
        public static void PrepareForWrite(List<FbxNode> top)
        {
            var hdr = top.FirstOrDefault(n => n.Name == "FBXHeaderExtension");
            var ver = hdr?.Child("FBXVersion");
            if (ver != null) ver.Props[0] = 7400;
            var fid = top.FirstOrDefault(n => n.Name == "FileId");
            if (fid != null) fid.Props[0] = new FbxRaw((byte[])FbxBinaryWriter.FileId.Clone());
            var ctime = top.FirstOrDefault(n => n.Name == "CreationTime");
            if (ctime != null) ctime.Props[0] = FbxBinaryWriter.CreationTime;
        }

        /// <summary>
        ///     Adds the BlendShare shapes to an FBX node tree in memory (shapes the FBX already has are kept). Returns a
        ///     report.
        /// </summary>
        public static string MergeInto(List<FbxNode> top, IEnumerable<BlendShareAsset> assets)
        {
            var objs = top.First(n => n.Name == "Objects");
            var conns = top.First(n => n.Name == "Connections");
            long nextId = objs.Children.Max(c => c.Id()) + 1000;
            var byId = objs.Children.ToDictionary(c => c.Id(), c => c);
            var parentsOf = new Dictionary<long, List<long>>();
            var kidsOf = new Dictionary<long, List<long>>();
            foreach (var c in conns.Children)
            {
                if (c.Props.Count < 3 || !(c.Props[1] is long ch) || !(c.Props[2] is long pa)) continue;
                (kidsOf.TryGetValue(pa, out var k) ? k : kidsOf[pa] = new List<long>()).Add(ch);
                (parentsOf.TryGetValue(ch, out var p) ? p : parentsOf[ch] = new List<long>()).Add(pa);
            }

            IEnumerable<FbxNode> Kids(long id)
            {
                return kidsOf.TryGetValue(id, out var l)
                    ? l.Where(byId.ContainsKey).Select(x => byId[x])
                    : Enumerable.Empty<FbxNode>();
            }

            int addedGeo = 0, addedDef = 0;
            var report = new StringBuilder();
            foreach (var asset in assets)
            foreach (var mesh in asset.Meshes)
            {
                var model = objs.ChildrenNamed("Model")
                    .FirstOrDefault(m => m.ObjClass() == "Mesh" && m.ObjName() == mesh.MeshName);
                var geo = model == null
                    ? null
                    : Kids(model.Id()).FirstOrDefault(k => k.Name == "Geometry" && k.ObjClass() == "Mesh");
                if (geo == null)
                {
                    report.AppendLine($"{mesh.MeshName}: not in the FBX, skipped");
                    continue;
                }

                int ncp = (geo.Child("Vertices")?.Doubles()?.Length ?? 0) / 3;
                var bs = Kids(geo.Id()).FirstOrDefault(k => k.Name == "Deformer" && k.ObjClass() == "BlendShape");
                if (bs == null)
                {
                    bs = new FbxNode("Deformer", nextId++, FbxBinaryWriter.Name(mesh.MeshName, "Deformer"),
                        "BlendShape");
                    bs.Add("Version", 100);
                    objs.Children.Add(bs);
                    byId[bs.Id()] = bs;
                    addedDef++;
                    conns.Add("C", "OO", bs.Id(), geo.Id());
                }

                var existing = new HashSet<string>(Kids(bs.Id()).Where(k => k.ObjClass() == "BlendShapeChannel")
                    .Select(k => k.ObjName()));
                int added = 0, skipped = 0;
                foreach (var s in mesh.Shapes)
                {
                    if (existing.Contains(s.Name))
                    {
                        skipped++;
                        continue;
                    }

                    if (s.Points.Any(p => p < 0 || p >= ncp))
                        throw new InvalidDataException(
                            $"{mesh.MeshName}/{s.Name}: control point index out of range (mesh has {ncp})");
                    int[] idx = s.Points.Length > 0 ? s.Points : new[] { 0 };
                    double[] dv = s.Points.Length > 0 ? s.Delta : new double[3];
                    var shp = new FbxNode("Geometry", nextId++, FbxBinaryWriter.Name(s.Name, "Geometry"), "Shape");
                    shp.Add("Version", 100);
                    shp.Add("Indexes", (int[])idx.Clone());
                    shp.Add("Vertices", (double[])dv.Clone());
                    shp.Add("Normals", new double[dv.Length]);
                    var chn = new FbxNode("Deformer", nextId++, FbxBinaryWriter.Name(s.Name, "SubDeformer"),
                        "BlendShapeChannel");
                    chn.Add("Version", 100);
                    chn.Add("DeformPercent", 0.0);
                    chn.Add("FullWeights", new[] { 100.0 });
                    objs.Children.Add(shp);
                    objs.Children.Add(chn);
                    conns.Add("C", "OO", shp.Id(), chn.Id());
                    conns.Add("C", "OO", chn.Id(), bs.Id());
                    existing.Add(s.Name);
                    added++;
                    addedGeo++;
                    addedDef++;
                }

                report.AppendLine($"{mesh.MeshName}: +{added} shapes" +
                                  (skipped > 0 ? $" ({skipped} already in the FBX)" : "") +
                                  $", {existing.Count} total");
            }

            // keep the object counts in Definitions consistent
            var defs = top.FirstOrDefault(n => n.Name == "Definitions");
            if (defs != null)
            {
                foreach (var ot in defs.ChildrenNamed("ObjectType"))
                {
                    string type = ot.Props.Count > 0 ? ot.Props[0] as string : null;
                    var cnt = ot.Child("Count");
                    if (cnt == null || !(cnt.Props[0] is int c0)) continue;
                    if (type == "Geometry") cnt.Props[0] = c0 + addedGeo;
                    else if (type == "Deformer") cnt.Props[0] = c0 + addedDef;
                }

                var total = defs.Child("Count");
                if (total != null && total.Props[0] is int t0) total.Props[0] = t0 + addedGeo + addedDef;
            }

            return report.ToString().TrimEnd();
        }

        public sealed class ShapeData
        {
            public double[] Delta = new double[0];
            public int Frames;
            public string Name;
            public int[] Points = new int[0];
        }

        public sealed class MeshShapes
        {
            public readonly List<ShapeData> Shapes = new();
            public string MeshName = "";
            public int UnityVertexCount;
        }
    }
}
