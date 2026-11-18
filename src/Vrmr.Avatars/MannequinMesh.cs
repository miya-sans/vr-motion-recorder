using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace VRMR
{
    /// <summary>
    /// The built-in mannequin's surface, built in code from capsules and ellipsoids like an artist's wooden figure:
    /// one rigid piece per bone and a ball at every joint, so it holds together in any pose without blended skin
    /// weights. There is no asset file behind it. Every vertex belongs to exactly one node of the mannequin's profile.
    /// </summary>
    public sealed class MannequinMesh
    {
        /// <summary>Vertex colours (0xAARRGGBB): the body's pieces, the joints, and the eyes and nose that show which way it faces.</summary>
        public const uint BodyColor = 0xFFC8CCD2, JointColor = 0xFF858B94, MarkColor = 0xFF2E3238;

        /// <summary>Vertex positions in the rest pose (metres, Unity space: +Y up, +Z forward, the avatar's right at +X).</summary>
        public Vec3[] Positions { get; private set; }
        /// <summary>Unit normals in the rest pose.</summary>
        public Vec3[] Normals { get; private set; }
        /// <summary>The profile node each vertex moves with.</summary>
        public int[] Node { get; private set; }
        /// <summary>Three vertex indices per triangle; front faces are clockwise, as in Unity.</summary>
        public int[] Triangles { get; private set; }
        /// <summary>One colour per vertex (0xAARRGGBB).</summary>
        public uint[] Color { get; private set; }
        /// <summary>The skeleton the mesh was built for.</summary>
        public AvatarProfile Profile { get; private set; }

        public int VertexCount => Positions.Length;
        public int TriangleCount => Triangles.Length / 3;

        static readonly ConcurrentDictionary<int, MannequinMesh> Cache = new ConcurrentDictionary<int, MannequinMesh>();

        /// <summary>The mesh for a mannequin skeleton (built once per size); null when the profile is not the mannequin.</summary>
        public static MannequinMesh For(AvatarProfile mannequin)
        {
            if (!Mannequin.Is(mannequin) || !mannequin.Has(HumanBones.Head)) return null;
            int sizeKey = (int)Math.Round(mannequin.EyeHeight * 10000f);
            return Cache.GetOrAdd(sizeKey, _ => new Builder(mannequin).Build());
        }

        /// <summary>
        /// Moves the mesh with a pose: every vertex follows its node rigidly. <paramref name="nodePos"/> and
        /// <paramref name="nodeRot"/> are world transforms per profile node (BakedClip.EvaluateNodes); the outputs must
        /// hold <see cref="VertexCount"/> items. Allocates nothing.
        /// </summary>
        public void Skin(Vec3[] nodePos, Quat[] nodeRot, Vec3[] outPositions, Vec3[] outNormals, float scale = 1f)
        {
            var restPos = Profile.RestWorldPos; var restRot = Profile.RestWorldRot;
            for (int i = 0; i < Positions.Length; i++)
            {
                int n = Node[i];
                var turn = nodeRot[n] * restRot[n].Inverse;
                outPositions[i] = nodePos[n] + turn * ((Positions[i] - restPos[n]) * scale);
                if (outNormals != null) outNormals[i] = turn * Normals[i];
            }
        }

        // ------------------------------------------------------------------ construction

        sealed class Builder
        {
            readonly AvatarProfile _p;
            readonly float _s; // size relative to the default mannequin
            readonly List<Vec3> _pos = new List<Vec3>(), _nrm = new List<Vec3>();
            readonly List<int> _node = new List<int>(), _tri = new List<int>();
            readonly List<uint> _col = new List<uint>();

            public Builder(AvatarProfile p) { _p = p; _s = p.EyeHeight / (Mannequin.DefaultHeight * Mannequin.EyeHeightRatio); }

            Vec3 At(int bone) => _p.HumanRestPos(bone);
            int NodeOf(int bone) => _p.Human[bone];

            public MannequinMesh Build()
            {
                float s = _s;
                // ---- trunk and head
                Ellipsoid(At(HumanBones.Hips) + new Vec3(0, -0.01f, 0) * s, new Vec3(0.125f, 0.085f, 0.088f) * s, HumanBones.Hips, BodyColor);
                Ellipsoid(Mid(HumanBones.Spine, HumanBones.Chest), new Vec3(0.110f, 0.082f, 0.080f) * s, HumanBones.Spine, BodyColor);
                Ellipsoid(Mid(HumanBones.Chest, HumanBones.UpperChest), new Vec3(0.132f, 0.086f, 0.090f) * s, HumanBones.Chest, BodyColor);
                Ellipsoid(At(HumanBones.UpperChest) + new Vec3(0, 0.07f, 0) * s, new Vec3(0.152f, 0.094f, 0.095f) * s, HumanBones.UpperChest, BodyColor);
                Capsule(At(HumanBones.Neck), At(HumanBones.Head), 0.045f * s, 0.043f * s, HumanBones.Neck, JointColor, 10, 2);
                var skull = At(HumanBones.Head) + new Vec3(0, 0.097f, 0.012f) * s;
                Ellipsoid(skull, new Vec3(0.078f, 0.098f, 0.092f) * s, HumanBones.Head, BodyColor, 16, 10);
                Ellipsoid(skull + new Vec3(0, -0.027f, 0.090f) * s, new Vec3(0.012f, 0.019f, 0.016f) * s, HumanBones.Head, MarkColor, 8, 4);
                foreach (int eye in new[] { HumanBones.LeftEye, HumanBones.RightEye })
                    Ellipsoid(At(eye), new Vec3(0.012f, 0.012f, 0.012f) * s, eye, MarkColor, 8, 4);

                foreach (bool left in new[] { true, false })
                {
                    int B(int leftBone) => left ? leftBone : HumanBones.Mirror(leftBone);
                    float m = left ? -1 : 1;

                    // ---- arm
                    Capsule(At(B(HumanBones.LeftShoulder)), At(B(HumanBones.LeftUpperArm)), 0.036f * s, 0.040f * s, B(HumanBones.LeftShoulder), BodyColor, 10, 2);
                    Ball(B(HumanBones.LeftUpperArm), 0.052f * s);
                    Capsule(At(B(HumanBones.LeftUpperArm)), At(B(HumanBones.LeftLowerArm)), 0.045f * s, 0.038f * s, B(HumanBones.LeftUpperArm), BodyColor);
                    Ball(B(HumanBones.LeftLowerArm), 0.040f * s);
                    Capsule(At(B(HumanBones.LeftLowerArm)), At(B(HumanBones.LeftHand)), 0.037f * s, 0.028f * s, B(HumanBones.LeftLowerArm), BodyColor);
                    Ball(B(HumanBones.LeftHand), 0.029f * s);
                    Ellipsoid(At(B(HumanBones.LeftHand)) + new Vec3(m * 0.050f, -0.003f, -0.002f) * s, new Vec3(0.047f, 0.014f, 0.040f) * s, B(HumanBones.LeftHand), BodyColor, 10, 6);
                    for (int f = 0; f < 5; f++)
                    {
                        float r = (f == 0 ? 0.0105f : f == 4 ? 0.0075f : 0.0086f) * s;
                        for (int k = 0; k < 3; k++)
                        {
                            int bone = B(HumanBones.LeftThumbProximal + f * 3 + k);
                            var from = At(bone);
                            // the last bone has no child to point at: it carries on from the one before, a little shorter
                            var to = k < 2 ? At(bone + 1) : from + (from - At(bone - 1)) * 0.82f;
                            Capsule(from, to, r * (1f - 0.08f * k), r * (1f - 0.08f * (k + 1)), bone, k == 0 ? BodyColor : k == 1 ? JointColor : BodyColor, 6, 2);
                        }
                    }

                    // ---- leg
                    Ball(B(HumanBones.LeftUpperLeg), 0.078f * s);
                    Capsule(At(B(HumanBones.LeftUpperLeg)), At(B(HumanBones.LeftLowerLeg)), 0.076f * s, 0.055f * s, B(HumanBones.LeftUpperLeg), BodyColor);
                    Ball(B(HumanBones.LeftLowerLeg), 0.057f * s);
                    Capsule(At(B(HumanBones.LeftLowerLeg)), At(B(HumanBones.LeftFoot)), 0.053f * s, 0.038f * s, B(HumanBones.LeftLowerLeg), BodyColor);
                    Ball(B(HumanBones.LeftFoot), 0.041f * s);
                    var ankle = At(B(HumanBones.LeftFoot)); var toes = At(B(HumanBones.LeftToes));
                    Ellipsoid(new Vec3(ankle.x, 0.042f * s, (ankle.z + toes.z) * 0.5f - 0.005f * s), new Vec3(0.045f, 0.040f, 0.095f) * s, B(HumanBones.LeftFoot), BodyColor, 10, 6);
                    Ellipsoid(toes + new Vec3(0, 0.006f, 0.035f) * s, new Vec3(0.045f, 0.025f, 0.055f) * s, B(HumanBones.LeftToes), BodyColor, 10, 6);
                }
                FaceOutwards();
                return new MannequinMesh { Positions = _pos.ToArray(), Normals = _nrm.ToArray(), Node = _node.ToArray(), Triangles = _tri.ToArray(), Color = _col.ToArray(), Profile = _p };
            }

            Vec3 Mid(int a, int b) => (At(a) + At(b)) * 0.5f;

            void Ball(int bone, float radius) => Ellipsoid(At(bone), new Vec3(radius, radius, radius), bone, JointColor, 10, 6);

            int Vertex(Vec3 p, Vec3 n, int bone, uint color)
            {
                _pos.Add(p); _nrm.Add(n.Normalized); _node.Add(NodeOf(bone)); _col.Add(color);
                return _pos.Count - 1;
            }

            /// <summary>An axis-aligned ellipsoid: rings of latitude between two poles.</summary>
            void Ellipsoid(Vec3 centre, Vec3 radii, int bone, uint color, int segments = 14, int rings = 8)
            {
                Vec3 Point(double lat, double lon, out Vec3 normal)
                {
                    var unit = new Vec3((float)(Math.Cos(lat) * Math.Cos(lon)), (float)Math.Sin(lat), (float)(Math.Cos(lat) * Math.Sin(lon)));
                    normal = new Vec3(unit.x / radii.x, unit.y / radii.y, unit.z / radii.z);
                    return centre + Vec3.Scale(unit, radii);
                }
                int south = Vertex(Point(-Math.PI / 2, 0, out var n0), n0, bone, color);
                var ring = new List<int[]>();
                for (int r = 1; r < rings; r++)
                {
                    var row = new int[segments];
                    double lat = -Math.PI / 2 + Math.PI * r / rings;
                    for (int k = 0; k < segments; k++) row[k] = Vertex(Point(lat, 2 * Math.PI * k / segments, out var n), n, bone, color);
                    ring.Add(row);
                }
                int north = Vertex(Point(Math.PI / 2, 0, out var n1), n1, bone, color);
                Stitch(south, ring, north);
            }

            /// <summary>A capsule from a to b whose radius goes from ra to rb, with rounded ends.</summary>
            void Capsule(Vec3 a, Vec3 b, float ra, float rb, int bone, uint color, int segments = 12, int capRings = 3)
            {
                var axis = (b - a).Normalized;
                if (axis.SqrLength < 0.5f) axis = Vec3.Up;
                var side = Vec3.Cross(axis, Math.Abs(axis.y) < 0.9f ? Vec3.Up : Vec3.Right).Normalized;
                var other = Vec3.Cross(axis, side);
                int[] Row(Vec3 centre, float radius, double tilt, float along)
                {
                    // tilt: 0 at the widest ring, towards 90 degrees at the pole; along: -1 the cap at a, +1 the cap at b
                    var row = new int[segments];
                    for (int k = 0; k < segments; k++)
                    {
                        double lon = 2 * Math.PI * k / segments;
                        var radial = side * (float)Math.Cos(lon) + other * (float)Math.Sin(lon);
                        var n = radial * (float)Math.Cos(tilt) + axis * (along * (float)Math.Sin(tilt));
                        row[k] = Vertex(centre + n * radius, n, bone, color);
                    }
                    return row;
                }
                var rings = new List<int[]>();
                int start = Vertex(a - axis * ra, -axis, bone, color);
                for (int r = capRings - 1; r >= 0; r--) rings.Add(Row(a, ra, Math.PI / 2 * r / capRings, -1));
                for (int r = 0; r < capRings; r++) rings.Add(Row(b, rb, Math.PI / 2 * r / capRings, 1));
                int end = Vertex(b + axis * rb, axis, bone, color);
                Stitch(start, rings, end);
            }

            /// <summary>Triangles between a pole, a stack of rings and the other pole.</summary>
            void Stitch(int first, List<int[]> rings, int last)
            {
                int n = rings[0].Length;
                for (int k = 0; k < n; k++) Tri(first, rings[0][k], rings[0][(k + 1) % n]);
                for (int r = 0; r + 1 < rings.Count; r++)
                    for (int k = 0; k < n; k++)
                    {
                        int a = rings[r][k], b = rings[r][(k + 1) % n], c = rings[r + 1][k], d = rings[r + 1][(k + 1) % n];
                        Tri(a, c, b); Tri(b, c, d);
                    }
                var top = rings[rings.Count - 1];
                for (int k = 0; k < n; k++) Tri(last, top[(k + 1) % n], top[k]);
            }

            void Tri(int a, int b, int c) { _tri.Add(a); _tri.Add(b); _tri.Add(c); }

            /// <summary>Orders every triangle so that its front (clockwise in Unity's left-handed space) faces along its vertices' normals.</summary>
            void FaceOutwards()
            {
                for (int t = 0; t < _tri.Count; t += 3)
                {
                    int a = _tri[t], b = _tri[t + 1], c = _tri[t + 2];
                    var face = Vec3.Cross(_pos[b] - _pos[a], _pos[c] - _pos[a]);
                    var outward = _nrm[a] + _nrm[b] + _nrm[c];
                    if (Vec3.Dot(face, outward) < 0) { _tri[t + 1] = c; _tri[t + 2] = b; }
                }
            }
        }
    }
}
