using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VRMR
{
    public sealed class AvatarNode
    {
        public Vec3 LocalPos;
        public Quat LocalRot = Quat.Identity;
        public Vec3 LocalScale = Vec3.One;
        public string Name;
        public int Parent = -1;
        public string Path; // Unity animation path relative to the avatar root ("" for root)
    }

    public sealed class AvatarMesh
    {
        public List<string> BlendShapes = new();
        public string Path;
    }

    /// <summary>
    ///     The skeleton binding of one avatar: every transform's rest pose (Unity space) and the humanoid mapping.
    ///     Built from a Unity model .meta (humanDescription) so no Unity instance is needed.
    /// </summary>
    public sealed class AvatarProfile
    {
        // ------------------------------------------------------------------ Unity .meta import

        static readonly Regex HumanRx = new(@"-\s*boneName:\s*(?<b>.*?)\s*\n\s*humanName:\s*(?<h>.*?)\s*\n",
            RegexOptions.Compiled);

        static readonly Regex SkelRx = new(
            @"-\s*name:\s*(?<n>.*?)\s*\n\s*parentName:\s*(?<p>.*?)\s*\n\s*position:\s*\{x:\s*(?<px>[^,]+),\s*y:\s*(?<py>[^,]+),\s*z:\s*(?<pz>[^}]+)\}\s*\n\s*rotation:\s*\{x:\s*(?<rx>[^,]+),\s*y:\s*(?<ry>[^,]+),\s*z:\s*(?<rz>[^,]+),\s*w:\s*(?<rw>[^}]+)\}\s*\n\s*scale:\s*\{x:\s*(?<sx>[^,]+),\s*y:\s*(?<sy>[^,]+),\s*z:\s*(?<sz>[^}]+)\}",
            RegexOptions.Compiled);

        public List<string> AvatarIds = new();
        public int[] Human = Enumerable.Repeat(-1, HumanBones.Count).ToArray(); // HumanBodyBones -> node index

        /// <summary>The avatar library entry (base model) this skeleton belongs to, "" when it isn't from the library.</summary>
        public string LibraryId = "";

        /// <summary>
        ///     The FBX shown in the mesh preview (the library's face-tracking FBX); "" = the FBX next to
        ///     <see cref="SourcePath" />.
        /// </summary>
        public string MeshFbx = "";

        public List<AvatarMesh> Meshes = new();
        public string Name = "Avatar";
        public List<AvatarNode> Nodes = new();

        // ---- derived (rest pose, root at origin) ----
        public Vec3[] RestWorldPos;
        public Quat[] RestWorldRot;
        public Vec3[] RestWorldScale;

        public float
            Scale = 1f; // extra uniform scale applied on top of the rest skeleton (avatar root scale in the scene)

        public string SourcePath = "";

        public string
            UnityModelPath = ""; // "Assets/.../Model.fbx" inside the Unity project (for the humanoid copy in Unity)

        public bool[] HumanExists => Human.Select(h => h >= 0).ToArray();

        public int RootIndex => Nodes.FindIndex(n => n.Parent < 0);

        /// <summary>Eye height of the rest skeleton (midpoint of eye bones, or head-based estimate).</summary>
        public float EyeHeight
        {
            get
            {
                if (Has(HumanBones.LeftEye) && Has(HumanBones.RightEye))
                    return (HumanRestPos(HumanBones.LeftEye).y + HumanRestPos(HumanBones.RightEye).y) * 0.5f;
                if (Has(HumanBones.Head))
                {
                    float headY = HumanRestPos(HumanBones.Head).y;
                    float neckY = Has(HumanBones.Neck) ? HumanRestPos(HumanBones.Neck).y : headY * 0.93f;
                    return headY + (headY - neckY) * 0.8f;
                }

                return 1.6f;
            }
        }

        /// <summary>
        ///     The view point (center eye) in rest pose: midpoint of the eye bones, as the base solver and VRChat place the
        ///     HMD.
        /// </summary>
        public Vec3 ViewPoint
        {
            get
            {
                if (Has(HumanBones.LeftEye) && Has(HumanBones.RightEye))
                    return (HumanRestPos(HumanBones.LeftEye) + HumanRestPos(HumanBones.RightEye)) * 0.5f;
                var h = HumanRestPos(HumanBones.Head);
                return new Vec3(h.x, EyeHeight, h.z + 0.08f * Scale);
            }
        }

        public bool Has(int bone)
        {
            return Human[bone] >= 0;
        }

        public void Finish()
        {
            int n = Nodes.Count;
            RestWorldPos = new Vec3[n];
            RestWorldRot = new Quat[n];
            RestWorldScale = new Vec3[n];
            var order = TopoOrder();
            foreach (int i in order)
            {
                var nd = Nodes[i];
                if (nd.Parent < 0)
                {
                    // the root itself is placed at origin with identity: animation paths are relative to it
                    RestWorldPos[i] = Vec3.Zero;
                    RestWorldRot[i] = Quat.Identity;
                    RestWorldScale[i] = new Vec3(Scale, Scale, Scale);
                }
                else
                {
                    int p = nd.Parent;
                    RestWorldPos[i] = RestWorldPos[p] + RestWorldRot[p] * Vec3.Scale(RestWorldScale[p], nd.LocalPos);
                    RestWorldRot[i] = (RestWorldRot[p] * nd.LocalRot).Normalized;
                    RestWorldScale[i] = Vec3.Scale(RestWorldScale[p], nd.LocalScale);
                }
            }

            foreach (int i in order)
            {
                var nd = Nodes[i];
                if (nd.Parent < 0) nd.Path = "";
                else
                {
                    string pp = Nodes[nd.Parent].Path;
                    nd.Path = string.IsNullOrEmpty(pp) ? nd.Name : pp + "/" + nd.Name;
                }
            }
        }

        public List<int> TopoOrder()
        {
            var res = new List<int>(Nodes.Count);
            bool[] done = new bool[Nodes.Count];

            void Visit(int i)
            {
                if (done[i]) return;
                if (Nodes[i].Parent >= 0) Visit(Nodes[i].Parent);
                done[i] = true;
                res.Add(i);
            }

            for (int i = 0; i < Nodes.Count; i++) Visit(i);
            return res;
        }

        public Vec3 HumanRestPos(int bone)
        {
            return Human[bone] >= 0 ? RestWorldPos[Human[bone]] : Vec3.Zero;
        }

        public Quat HumanRestRot(int bone)
        {
            return Human[bone] >= 0 ? RestWorldRot[Human[bone]] : Quat.Identity;
        }

        /// <summary>Indices of nodes that belong to the armature (ancestors of Hips and everything below Hips).</summary>
        public List<int> ArmatureNodes()
        {
            var set = new HashSet<int>();
            int hips = Human[HumanBones.Hips];
            if (hips < 0) return Enumerable.Range(0, Nodes.Count).ToList();
            for (int a = hips; a >= 0; a = Nodes[a].Parent) set.Add(a);
            for (int i = 0; i < Nodes.Count; i++)
            {
                for (int a = i; a >= 0; a = Nodes[a].Parent)
                    if (a == hips)
                    {
                        set.Add(i);
                        break;
                    }
            }

            return TopoOrder().Where(set.Contains).ToList();
        }

        public int HumanOfNode(int node)
        {
            for (int b = 0; b < HumanBones.Count; b++)
                if (Human[b] == node)
                    return b;
            return -1;
        }

        public static AvatarProfile FromUnityMeta(string metaPath)
        {
            string text = File.ReadAllText(metaPath, Encoding.UTF8).Replace("\r\n", "\n");
            int hd = text.IndexOf("humanDescription:", StringComparison.Ordinal);
            if (hd < 0)
                throw new InvalidDataException(
                    "No humanDescription in " + metaPath + " (is the model set to Humanoid?)");
            int sk = text.IndexOf("\n    skeleton:", hd, StringComparison.Ordinal);
            if (sk < 0) throw new InvalidDataException("No skeleton in humanDescription");
            string humanPart = text.Substring(hd, sk - hd);
            string skelPart = text.Substring(sk);

            var prof = new AvatarProfile();
            prof.SourcePath = metaPath;
            string file = Path.GetFileNameWithoutExtension(metaPath); // "Avatar.fbx"
            prof.Name = Path.GetFileNameWithoutExtension(file);
            string model = metaPath.Substring(0, metaPath.Length - 5).Replace('\\', '/');
            int ai = model.IndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
            if (ai >= 0) prof.UnityModelPath = model.Substring(ai + 1);

            var byName = new Dictionary<string, int>();
            var parents = new List<string>();
            foreach (Match m in SkelRx.Matches(skelPart))
            {
                string name = YamlStr(m.Groups["n"].Value);
                if (byName.ContainsKey(name)) continue;
                var nd = new AvatarNode
                {
                    Name = name,
                    LocalPos = new Vec3(F(m, "px"), F(m, "py"), F(m, "pz")),
                    LocalRot = new Quat(F(m, "rx"), F(m, "ry"), F(m, "rz"), F(m, "rw")).Normalized,
                    LocalScale = new Vec3(F(m, "sx"), F(m, "sy"), F(m, "sz"))
                };
                byName[name] = prof.Nodes.Count;
                prof.Nodes.Add(nd);
                parents.Add(YamlStr(m.Groups["p"].Value));
            }

            if (prof.Nodes.Count == 0) throw new InvalidDataException("Skeleton is empty");
            for (int i = 0; i < prof.Nodes.Count; i++)
            {
                prof.Nodes[i].Parent =
                    !string.IsNullOrEmpty(parents[i]) && byName.TryGetValue(parents[i], out int pi) && pi != i
                        ? pi
                        : -1;
            }

            // keep a single root: re-parent stray roots to the first one
            int root = prof.Nodes.FindIndex(n => n.Parent < 0);
            for (int i = 0; i < prof.Nodes.Count; i++)
                if (i != root && prof.Nodes[i].Parent < 0)
                    prof.Nodes[i].Parent = root;
            // root rest transform is ignored (avatar root sits at origin)
            prof.Nodes[root].LocalPos = Vec3.Zero;
            prof.Nodes[root].LocalRot = Quat.Identity;

            foreach (Match m in HumanRx.Matches(humanPart))
            {
                string bone = YamlStr(m.Groups["b"].Value);
                int hb = HumanBones.FromName(YamlStr(m.Groups["h"].Value));
                if (hb >= 0 && byName.TryGetValue(bone, out int ni)) prof.Human[hb] = ni;
            }

            if (prof.Human[HumanBones.Hips] < 0) throw new InvalidDataException("Humanoid mapping has no Hips");
            prof.Finish();
            return prof;
        }

        static float F(Match m, string g)
        {
            return float.Parse(m.Groups[g].Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        /// <summary>Unquote a YAML scalar (plain, 'single' or "double" with \x \u escapes).</summary>
        public static string YamlStr(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '\'' && s[s.Length - 1] == '\'')
                return s.Substring(1, s.Length - 2).Replace("''", "'");
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
            {
                var bytes = new List<byte>();
                var sb = new StringBuilder();
                string body = s.Substring(1, s.Length - 2);

                void FlushBytes()
                {
                    if (bytes.Count > 0)
                    {
                        sb.Append(Encoding.UTF8.GetString(bytes.ToArray()));
                        bytes.Clear();
                    }
                }

                for (int i = 0; i < body.Length; i++)
                {
                    char c = body[i];
                    if (c == '\\' && i + 1 < body.Length)
                    {
                        char e = body[++i];
                        if (e == 'x' && i + 2 < body.Length + 1)
                        {
                            bytes.Add(Convert.ToByte(body.Substring(i + 1, 2), 16));
                            i += 2;
                            continue;
                        }

                        FlushBytes();
                        switch (e)
                        {
                            case 'u':
                                sb.Append((char)Convert.ToInt32(body.Substring(i + 1, 4), 16));
                                i += 4;
                                break;
                            case 'n': sb.Append('\n'); break;
                            case 't': sb.Append('\t'); break;
                            case '0': sb.Append('\0'); break;
                            default: sb.Append(e); break;
                        }
                    }
                    else
                    {
                        FlushBytes();
                        sb.Append(c);
                    }
                }

                FlushBytes();
                // Unity writes \xNN for single Latin-1 code points (e.g. \xD7 = U+00D7), not UTF-8 bytes; detect that case.
                return FixLatin1(sb.ToString(), body);
            }

            return s;
        }

        static string FixLatin1(string decoded, string raw)
        {
            if (decoded.IndexOf('\uFFFD') < 0) return decoded;
            // reinterpret \xNN as code points
            var sb = new StringBuilder();
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] == '\\' && i + 3 < raw.Length + 1 && raw[i + 1] == 'x')
                {
                    sb.Append((char)Convert.ToInt32(raw.Substring(i + 2, 2), 16));
                    i += 3;
                }
                else if (raw[i] == '\\' && i + 1 < raw.Length)
                {
                    sb.Append(raw[i + 1]);
                    i++;
                }
                else sb.Append(raw[i]);
            }

            return sb.ToString();
        }

        // ------------------------------------------------------------------ JSON (embedded in recordings)

        public Dictionary<string, object> ToJson()
        {
            var nodes = new List<object>();
            foreach (var n in Nodes)
                nodes.Add(new Dictionary<string, object>
                {
                    ["n"] = n.Name, ["p"] = n.Parent, ["t"] = Json.V3(n.LocalPos), ["r"] = Json.Q(n.LocalRot),
                    ["s"] = Json.V3(n.LocalScale)
                });
            var human = new Dictionary<string, object>();
            for (int b = 0; b < HumanBones.Count; b++)
                if (Human[b] >= 0)
                    human[HumanBones.Names[b]] = Human[b];
            var meshes = new List<object>();
            foreach (var m in Meshes)
                meshes.Add(new Dictionary<string, object>
                    { ["path"] = m.Path, ["shapes"] = m.BlendShapes.Cast<object>().ToList() });
            return new Dictionary<string, object>
            {
                ["name"] = Name, ["source"] = SourcePath, ["scale"] = (double)Scale, ["unityModel"] = UnityModelPath,
                ["libraryId"] = LibraryId, ["meshFbx"] = MeshFbx,
                ["avatarIds"] = AvatarIds.Cast<object>().ToList(),
                ["nodes"] = nodes, ["human"] = human, ["meshes"] = meshes
            };
        }

        public static AvatarProfile FromJson(Dictionary<string, object> d)
        {
            var p = new AvatarProfile
            {
                Name = d.GetStr("name", "Avatar"),
                SourcePath = d.GetStr("source", ""),
                Scale = d.GetF("scale", 1f),
                UnityModelPath = d.GetStr("unityModel", ""),
                LibraryId = d.GetStr("libraryId", ""),
                MeshFbx = d.GetStr("meshFbx", "")
            };
            var ids = d.GetList("avatarIds");
            if (ids != null) p.AvatarIds = ids.Select(x => x as string).Where(x => x != null).ToList();
            foreach (object o in d.GetList("nodes") ?? new List<object>())
            {
                var nd = (Dictionary<string, object>)o;
                p.Nodes.Add(new AvatarNode
                {
                    Name = nd.GetStr("n"), Parent = nd.GetInt("p", -1),
                    LocalPos = Json.ToV3(nd["t"]), LocalRot = Json.ToQ(nd["r"]).Normalized,
                    LocalScale = Json.ToV3(nd.ContainsKey("s") ? nd["s"] : null, Vec3.One)
                });
            }

            var human = d.GetObj("human");
            if (human != null)
                foreach (var kv in human)
                {
                    int b = HumanBones.FromName(kv.Key);
                    if (b >= 0) p.Human[b] = (int)(double)kv.Value;
                }

            foreach (object o in d.GetList("meshes") ?? new List<object>())
            {
                var md = (Dictionary<string, object>)o;
                var m = new AvatarMesh { Path = md.GetStr("path", "") };
                foreach (object s in md.GetList("shapes") ?? new List<object>())
                    if (s is string ss)
                        m.BlendShapes.Add(ss);
                p.Meshes.Add(m);
            }

            p.Finish();
            return p;
        }

        public AvatarProfile Clone()
        {
            return FromJson(Json.ParseObject(Json.Serialize(ToJson())));
        }

        /// <summary>
        ///     The fallback humanoid for a take without any avatar: the built-in <see cref="Mannequin" />, sized to an eye
        ///     height.
        /// </summary>
        public static AvatarProfile Generic(float eyeHeight = 1.6f)
        {
            return Mannequin.Profile(eyeHeight / Mannequin.EyeHeightRatio);
        }
    }
}
