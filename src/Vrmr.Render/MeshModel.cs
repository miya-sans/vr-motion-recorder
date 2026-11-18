using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VRMR.Render
{
    /// <summary>One mesh of an avatar model (a renderer in Unity terms): it can be hidden on its own.</summary>
    public sealed class MeshPart
    {
        public string Name { get; internal set; }
        public int Triangles { get; internal set; }
    }

    /// <summary>One vertex as the GPU gets it. 44 bytes.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct MeshVertex
    {
        public float X, Y, Z;
        public sbyte NX, NY, NZ, NPad;
        public float U, V;
        public ushort B0, B1, B2, B3;

        /// <summary>Skin weights as 16-bit fractions that add up to 65535; all zero for a vertex that is not skinned.</summary>
        public ushort W0, W1, W2, W3;

        /// <summary>Column of this vertex in the part's face shape rows, or -1.</summary>
        public int Morph;

        public const int Size = 44;
    }

    /// <summary>A run of triangles of one part that share a texture: one draw call.</summary>
    struct SubMesh
    {
        public int Part, Texture, IndexStart, IndexCount;
    }

    /// <summary>A texture as the GPU gets it: RGBA bytes, top row first.</summary>
    sealed class TextureData
    {
        /// <summary>Texels under half alpha are holes.</summary>
        public bool Cutout;

        /// <summary>
        ///     Straight alpha, or premultiplied when <see cref="Cutout" /> (so filtered edges do not pick up the colour under
        ///     the holes).
        /// </summary>
        public byte[] Rgba;

        public int Width, Height;

        public static TextureData From(SkinnedAvatar.Texture t)
        {
            if (t?.Px == null) return null;
            var d = new TextureData { Width = t.W, Height = t.H, Cutout = t.Cutout, Rgba = new byte[t.Px.Length * 4] };
            for (int i = 0, o = 0; i < t.Px.Length; i++, o += 4)
            {
                uint c = (uint)t.Px[i];
                uint a = c >> 24, r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
                if (t.Cutout)
                {
                    r = (r * a + 127) / 255;
                    g = (g * a + 127) / 255;
                    b = (b * a + 127) / 255;
                }

                d.Rgba[o] = (byte)r;
                d.Rgba[o + 1] = (byte)g;
                d.Rgba[o + 2] = (byte)b;
                d.Rgba[o + 3] = (byte)a;
            }

            return d;
        }
    }

    /// <summary>
    ///     One face shape of one part: the recording's channel that drives it and where its deltas start in the shape
    ///     texture.
    /// </summary>
    struct MorphRow
    {
        public int Channel, BaseTexel;
    }

    /// <summary>
    ///     An avatar's mesh packed for the GPU: vertices with skin weights, triangles grouped by part and texture, face
    ///     shapes as rows of deltas, and the bind pose of the bones that carry it. Built once per avatar (a worker thread
    ///     is fine); the GPU copy is made by each view on first draw.
    /// </summary>
    public sealed class MeshModel : IDisposable
    {
        /// <summary>Face shapes that can be active on one part at once; the strongest win beyond that.</summary>
        internal const int MaxActiveShapes = 64;

        internal const int MorphTextureWidth = 2048;
        readonly Dictionary<string, int> _channelByName = new(StringComparer.Ordinal);

        readonly List<MeshPart> _parts = new();
        internal Vec3[] BoneBindPos = Array.Empty<Vec3>();
        internal Quat[] BoneBindRot = Array.Empty<Quat>();

        /// <summary>Profile node per bone of the palette the vertices index, with its bind pose.</summary>
        internal int[] BoneNodes = Array.Empty<int>();

        internal uint[] Indices = Array.Empty<uint>();
        internal int MorphHeight;

        /// <summary>Face shape deltas, four halves per texel (x, y, z, unused), <see cref="MorphTextureWidth" /> texels per row.</summary>
        internal Half[] MorphTexels = Array.Empty<Half>();

        /// <summary>Per part: its first sub-mesh, their count, and its face shape rows.</summary>
        internal int[] PartFirstSub = Array.Empty<int>(), PartSubCount = Array.Empty<int>();

        internal MorphRow[][] PartMorphs = Array.Empty<MorphRow[]>();
        internal SubMesh[] SubMeshes = Array.Empty<SubMesh>();

        /// <summary>Per texture of the model; null where the image was not found or could not be read.</summary>
        internal TextureData[] Textures = Array.Empty<TextureData>();

        internal MeshVertex[] Vertices = Array.Empty<MeshVertex>();

        int _channelCount;

        MeshModel() { }

        /// <summary>The model's meshes, in file order.</summary>
        public IReadOnlyList<MeshPart> Parts => _parts;

        public int TriangleCount { get; private set; }
        public int VertexCount => Vertices.Length;

        /// <summary>Number of nodes of the skeleton the mesh was built for; poses must have as many.</summary>
        public int NodeCount { get; private set; }

        /// <summary>The model file this came from.</summary>
        public string SourcePath { get; private set; } = "";

        /// <summary>Number of distinct face shapes the recording can drive on this mesh.</summary>
        public int FaceChannelCount { get; private set; }

        internal bool IsDisposed { get; private set; }

        /// <summary>Frees the GPU copies (at each view's next draw). The model must not be drawn afterwards.</summary>
        public void Dispose()
        {
            IsDisposed = true;
        }

        /// <summary>
        ///     The index of a face shape for <see cref="FaceWeights.Set(int, float)" />, or -1 when the mesh has no such shape.
        ///     Both the Unified Expressions name and its ARKit name are known.
        /// </summary>
        public int FaceChannel(string name)
        {
            return name != null && _channelByName.TryGetValue(name, out int c) ? c : -1;
        }

        /// <summary>A mask for <see cref="ActorView.HiddenParts" /> that hides the parts with these names.</summary>
        public bool[] HiddenParts(IEnumerable<string> names)
        {
            bool[] hidden = new bool[_parts.Count];
            if (names == null) return hidden;
            var set = new HashSet<string>(names, StringComparer.Ordinal);
            for (int i = 0; i < _parts.Count; i++) hidden[i] = set.Contains(_parts[i].Name);
            return hidden;
        }

        /// <summary>Packs a loaded avatar mesh. Returns null for null.</summary>
        public static MeshModel Create(SkinnedAvatar avatar)
        {
            if (avatar == null) return null;
            var m = new MeshModel
            {
                NodeCount = avatar.Profile.Nodes.Count, SourcePath = avatar.SourcePath ?? "",
                Textures = new TextureData[avatar.Textures.Count]
            };
            for (int i = 0; i < m.Textures.Length; i++) m.Textures[i] = TextureData.From(avatar.Textures[i]);
            var verts = new List<MeshVertex>(65536);
            var indices = new List<uint>(262144);
            var subs = new List<SubMesh>();
            var boneOf = new Dictionary<int, int>();
            var boneNodes = new List<int>();
            var morphs = new List<Half>();
            int np = avatar.Parts.Count;
            m.PartFirstSub = new int[np];
            m.PartSubCount = new int[np];
            m.PartMorphs = new MorphRow[np][];

            for (int pi = 0; pi < np; pi++)
            {
                var part = avatar.Parts[pi];
                int nt = part.Tri.Length / 3;
                m._parts.Add(new MeshPart { Name = part.Name, Triangles = nt });
                m.TriangleCount += nt;
                int[] morphOf = m.PackShapes(part, pi, morphs);

                // vertices: one per control point and UV pair
                var vertexOf = new Dictionary<(int, float, float), uint>();
                var byTexture = new SortedDictionary<int, List<uint>>();
                for (int t = 0; t < nt; t++)
                {
                    int tex = part.Mat[t];
                    if (tex < 0 || tex >= m.Textures.Length || m.Textures[tex] == null) tex = -1;
                    if (!byTexture.TryGetValue(tex, out var list)) byTexture[tex] = list = new List<uint>();
                    for (int c = 0; c < 3; c++)
                    {
                        int cp = part.Tri[t * 3 + c];
                        float u = part.UV[t * 6 + c * 2],
                            v = 1f - part.UV[t * 6 + c * 2 + 1]; // image rows run top to bottom
                        if (!vertexOf.TryGetValue((cp, u, v), out uint vi))
                        {
                            vi = (uint)verts.Count;
                            vertexOf[(cp, u, v)] = vi;
                            verts.Add(Vertex(part, cp, u, v, morphOf == null ? -1 : morphOf[cp], boneOf, boneNodes));
                        }

                        list.Add(vi);
                    }
                }

                m.PartFirstSub[pi] = subs.Count;
                foreach (var kv in byTexture)
                {
                    subs.Add(new SubMesh
                        { Part = pi, Texture = kv.Key, IndexStart = indices.Count, IndexCount = kv.Value.Count });
                    indices.AddRange(kv.Value);
                }

                m.PartSubCount[pi] = subs.Count - m.PartFirstSub[pi];
            }

            m.Vertices = verts.ToArray();
            m.Indices = indices.ToArray();
            m.SubMeshes = subs.ToArray();
            m.BoneNodes = boneNodes.ToArray();
            m.BoneBindPos = new Vec3[boneNodes.Count];
            m.BoneBindRot = new Quat[boneNodes.Count];
            for (int i = 0; i < boneNodes.Count; i++)
            {
                m.BoneBindPos[i] = avatar.BindPos[boneNodes[i]];
                m.BoneBindRot[i] = avatar.BindRot[boneNodes[i]];
            }

            m.FaceChannelCount = m._channelCount;
            m.AddAliases();
            m.MorphHeight = (morphs.Count / 4 + MorphTextureWidth - 1) / MorphTextureWidth;
            if (m.MorphHeight > 0)
            {
                m.MorphTexels = new Half[m.MorphHeight * MorphTextureWidth * 4];
                morphs.CopyTo(m.MorphTexels);
            }

            return m;
        }

        /// <summary>
        ///     Packs the built-in mannequin: one part, every vertex rigid on its node, its vertex colours as a one-row
        ///     palette texture. Returns null for null.
        /// </summary>
        public static MeshModel Create(MannequinMesh mannequin)
        {
            if (mannequin == null) return null;
            var profile = mannequin.Profile;
            var m = new MeshModel
            {
                NodeCount = profile.Nodes.Count, SourcePath = Mannequin.Source, TriangleCount = mannequin.TriangleCount
            };
            m._parts.Add(new MeshPart { Name = Mannequin.Name, Triangles = mannequin.TriangleCount });

            var palette = new List<uint>();
            var paletteIndex = new Dictionary<uint, int>();
            foreach (uint c in mannequin.Color)
                if (!paletteIndex.ContainsKey(c))
                {
                    paletteIndex[c] = palette.Count;
                    palette.Add(c);
                }

            var texture = new TextureData { Width = palette.Count, Height = 1, Rgba = new byte[palette.Count * 4] };
            for (int i = 0; i < palette.Count; i++)
            {
                uint c = palette[i];
                texture.Rgba[i * 4] = (byte)(c >> 16);
                texture.Rgba[i * 4 + 1] = (byte)(c >> 8);
                texture.Rgba[i * 4 + 2] = (byte)c;
                texture.Rgba[i * 4 + 3] = 255;
            }

            m.Textures = new[] { texture };

            var boneOf = new Dictionary<int, int>();
            var boneNodes = new List<int>();
            m.Vertices = new MeshVertex[mannequin.VertexCount];
            for (int i = 0; i < m.Vertices.Length; i++)
            {
                var p = mannequin.Positions[i];
                var n = mannequin.Normals[i];
                int node = mannequin.Node[i];
                if (!boneOf.TryGetValue(node, out int b))
                {
                    boneOf[node] = b = boneNodes.Count;
                    boneNodes.Add(node);
                }

                m.Vertices[i] = new MeshVertex
                {
                    X = p.x, Y = p.y, Z = p.z, Morph = -1,
                    U = (paletteIndex[mannequin.Color[i]] + 0.5f) / palette.Count,
                    V = 0.5f, // the middle of its colour's texel
                    NX = (sbyte)Math.Round(MathX.Clamp(n.x, -1, 1) * 127),
                    NY = (sbyte)Math.Round(MathX.Clamp(n.y, -1, 1) * 127),
                    NZ = (sbyte)Math.Round(MathX.Clamp(n.z, -1, 1) * 127),
                    B0 = (ushort)b, W0 = 65535
                };
            }

            m.Indices = Array.ConvertAll(mannequin.Triangles, t => (uint)t);
            m.SubMeshes = new[]
                { new SubMesh { Part = 0, Texture = 0, IndexStart = 0, IndexCount = m.Indices.Length } };
            m.PartFirstSub = new[] { 0 };
            m.PartSubCount = new[] { 1 };
            m.PartMorphs = new[] { Array.Empty<MorphRow>() };
            m.BoneNodes = boneNodes.ToArray();
            m.BoneBindPos = new Vec3[boneNodes.Count];
            m.BoneBindRot = new Quat[boneNodes.Count];
            for (int i = 0; i < boneNodes.Count; i++)
            {
                m.BoneBindPos[i] = profile.RestWorldPos[boneNodes[i]];
                m.BoneBindRot[i] = profile.RestWorldRot[boneNodes[i]];
            }

            return m;
        }

        int ChannelFor(string faceShape)
        {
            if (_channelByName.TryGetValue(faceShape, out int c)) return c;
            c = _channelCount++;
            _channelByName[faceShape] = c;
            return c;
        }

        /// <summary>
        ///     Appends the part's face shapes to the shape texture and returns, per control point, its column in those rows
        ///     (-1: no shape moves it), or null when the part has no face shape.
        /// </summary>
        int[] PackShapes(SkinnedAvatar.Part part, int partIndex, List<Half> morphs)
        {
            var rows = new List<(int Channel, SkinnedAvatar.Shape Shape)>();
            foreach (var kv in part.FaceMap)
                if (part.Shapes.TryGetValue(kv.Value, out var s))
                    rows.Add((ChannelFor(kv.Key), s));
            if (rows.Count == 0)
            {
                PartMorphs[partIndex] = Array.Empty<MorphRow>();
                return null;
            }

            int[] column = new int[part.Rest.Length];
            Array.Fill(column, -1);
            int columns = 0;
            foreach (var (_, s) in rows)
            foreach (int cp in s.Idx)
                if (cp < column.Length && column[cp] < 0)
                    column[cp] = columns++;

            var packed = new MorphRow[rows.Count];
            var row = new Vec3[columns];
            for (int r = 0; r < rows.Count; r++)
            {
                Array.Clear(row);
                var s = rows[r].Shape;
                for (int k = 0; k < s.Idx.Length; k++)
                    if (s.Idx[k] < column.Length)
                        row[column[s.Idx[k]]] = row[column[s.Idx[k]]] + s.Delta[k];
                packed[r] = new MorphRow { Channel = rows[r].Channel, BaseTexel = morphs.Count / 4 };
                foreach (var d in row)
                {
                    morphs.Add((Half)d.x);
                    morphs.Add((Half)d.y);
                    morphs.Add((Half)d.z);
                    morphs.Add((Half)0f);
                }
            }

            PartMorphs[partIndex] = packed;
            return column;
        }

        /// <summary>
        ///     Registers the other naming of each shape (ARKit) as its own input slot after the primary ones, so a weight
        ///     given under the primary name wins when a recording has both: see <see cref="FaceWeights.Resolve" />.
        /// </summary>
        void AddAliases()
        {
            var primary = new List<KeyValuePair<string, int>>(_channelByName);
            foreach (var kv in primary)
            {
                string alias = SkinnedAvatar.FaceAlias(kv.Key);
                if (alias != null && !_channelByName.ContainsKey(alias))
                    _channelByName[alias] = kv.Value + _channelCount;
            }
        }

        static MeshVertex Vertex(SkinnedAvatar.Part part, int cp, float u, float v, int morph,
            Dictionary<int, int> boneOf, List<int> boneNodes)
        {
            var p = part.Rest[cp];
            var n = part.RestN[cp];
            var vx = new MeshVertex
            {
                X = p.x, Y = p.y, Z = p.z, U = u, V = v, Morph = morph,
                NX = (sbyte)Math.Round(MathX.Clamp(n.x, -1, 1) * 127),
                NY = (sbyte)Math.Round(MathX.Clamp(n.y, -1, 1) * 127),
                NZ = (sbyte)Math.Round(MathX.Clamp(n.z, -1, 1) * 127)
            };
            if (part.Bone[cp * 4] < 0) return vx; // not skinned: stays where the file has it
            Span<ushort> bone = stackalloc ushort[4];
            Span<int> weight = stackalloc int[4];
            int sum = 0, largest = 0;
            for (int k = 0; k < 4; k++)
            {
                int node = part.Bone[cp * 4 + k];
                if (node < 0) break;
                if (!boneOf.TryGetValue(node, out int b))
                {
                    boneOf[node] = b = boneNodes.Count;
                    boneNodes.Add(node);
                }

                bone[k] = (ushort)b;
                weight[k] = (int)Math.Round(MathX.Clamp01(part.Weight[cp * 4 + k]) * 65535);
                sum += weight[k];
                if (weight[k] > weight[largest]) largest = k;
            }

            weight[largest] =
                Math.Max(0, weight[largest] + 65535 - sum); // the rounding error goes to the strongest bone
            vx.B0 = bone[0];
            vx.B1 = bone[1];
            vx.B2 = bone[2];
            vx.B3 = bone[3];
            vx.W0 = (ushort)weight[0];
            vx.W1 = (ushort)weight[1];
            vx.W2 = (ushort)weight[2];
            vx.W3 = (ushort)weight[3];
            return vx;
        }
    }
}
