using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace VRMR
{
    public sealed class AnimExportOptions
    {
        public bool Body = true;
        public string ClipName = "Clip";
        public bool Face = true;
        public string FaceMeshPath = "Body";
        public float FloatTolerance = 0.002f; // 0..1 units (blendshapes scaled to 0..100)
        public int FrameStep = 1;
        public bool Loop = false;
        public bool Params = false;
        public float PosTolerance = 0.0002f; // meters
        public float RotTolerance = 0.0005f; // quaternion component error
        public Dictionary<string, string> ShapeRename;
    }

    /// <summary>
    ///     Writes Unity .anim (YAML, AnimationClip serializedVersion 7) directly - no Unity required.
    ///     Body = generic transform curves on the avatar's exact paths (plays on the avatar as-is), face = blendShape.*
    ///     curves on the face mesh, params = Animator parameter curves (AAP) to replay face tracking through the FX layer.
    ///     Keys are linear with light keyframe reduction.
    /// </summary>
    public static class UnityAnimExporter
    {
        static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        internal static string F(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) v = 0;
            if (Math.Abs(v) < 1e-9f) return "0";
            return v.ToString("R", CI);
        }

        public static void Export(BakedClip clip, string path, AnimExportOptions opt)
        {
            var prof = clip.Profile;
            int step = Math.Max(1, opt.FrameStep);
            var frames = new List<int>();
            for (int f = 0; f < clip.Frames; f += step) frames.Add(f);
            if (frames[frames.Count - 1] != clip.Frames - 1) frames.Add(clip.Frames - 1);
            float[] times = frames.Select(f => (float)(f / (double)clip.Fps)).ToArray();
            float stop = times[times.Length - 1];

            var rotCurves = new List<Tuple<string, Quat[]>>();
            var posCurves = new List<Tuple<string, Vec3[]>>();
            var floatCurves = new List<Tuple<string, string, int, float[]>>(); // attribute, path, classID, values

            if (opt.Body && clip.BodySource != "none")
            {
                int nn = prof.Nodes.Count;
                var wp = new Vec3[nn];
                var wr = new Quat[nn];
                var lp = new Vec3[nn];
                var lr = new Quat[nn];
                var animated = new List<int>();
                for (int b = 0; b < HumanBones.Count; b++)
                    if (prof.Has(b) && clip.BoneExists[b])
                        animated.Add(prof.Human[b]);
                var rk = animated.ToDictionary(i => i, i => new Quat[frames.Count]);
                int hips = prof.Human[HumanBones.Hips];
                var pk = new Vec3[frames.Count];
                for (int k = 0; k < frames.Count; k++)
                {
                    clip.EvaluateLocal(frames[k], wp, wr, lp, lr);
                    foreach (int i in animated)
                    {
                        var q = lr[i];
                        if (k > 0 && Quat.Dot(q, rk[i][k - 1]) < 0) q = q.Negated; // continuity
                        rk[i][k] = q;
                    }

                    pk[k] = lp[hips];
                }

                foreach (int i in animated) rotCurves.Add(Tuple.Create(prof.Nodes[i].Path, rk[i]));
                posCurves.Add(Tuple.Create(prof.Nodes[hips].Path, pk));
            }

            if (opt.Face)
            {
                foreach (var kv in clip.Face.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    string name = opt.ShapeRename != null && opt.ShapeRename.TryGetValue(kv.Key, out string rn)
                        ? rn
                        : kv.Key;
                    float[] vals = frames.Select(f => kv.Value[f] * 100f).ToArray();
                    floatCurves.Add(Tuple.Create("blendShape." + name, opt.FaceMeshPath, 137, vals));
                }
            }

            if (opt.Params)
            {
                foreach (var kv in clip.ParamValues.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    float[] vals = frames.Select(f => kv.Value[f]).ToArray();
                    floatCurves.Add(Tuple.Create(kv.Key, "", 95, vals));
                }
            }

            var sb = new StringBuilder(1 << 20);
            sb.Append("%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!74 &7400000\nAnimationClip:\n");
            sb.Append(
                "  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n");
            sb.Append("  m_Name: ").Append(YamlName(opt.ClipName)).Append('\n');
            sb.Append("  serializedVersion: 7\n  m_Legacy: 0\n  m_Compressed: 0\n  m_UseHighQualityCurve: 1\n");
            // rotation curves
            if (rotCurves.Count == 0) sb.Append("  m_RotationCurves: []\n");
            else
            {
                sb.Append("  m_RotationCurves:\n");
                foreach (var c in rotCurves)
                {
                    sb.Append("  - curve:\n      serializedVersion: 2\n      m_Curve:\n");
                    WriteQuatKeys(sb, times, c.Item2, opt.RotTolerance);
                    sb.Append("      m_PreInfinity: 2\n      m_PostInfinity: 2\n      m_RotationOrder: 4\n");
                    sb.Append("    path: ").Append(YamlName(c.Item1)).Append('\n');
                }
            }

            sb.Append("  m_CompressedRotationCurves: []\n  m_EulerCurves: []\n");
            if (posCurves.Count == 0) sb.Append("  m_PositionCurves: []\n");
            else
            {
                sb.Append("  m_PositionCurves:\n");
                foreach (var c in posCurves)
                {
                    sb.Append("  - curve:\n      serializedVersion: 2\n      m_Curve:\n");
                    WriteVecKeys(sb, times, c.Item2, opt.PosTolerance);
                    sb.Append("      m_PreInfinity: 2\n      m_PostInfinity: 2\n      m_RotationOrder: 4\n");
                    sb.Append("    path: ").Append(YamlName(c.Item1)).Append('\n');
                }
            }

            // following VRChat's avatar scaling (BakedClip.ScaleCurve): the avatar root's scale (absolute: replaces the prefab root's own scale)
            if (opt.Body && clip.ScaleCurve != null && clip.BodySource != "none")
            {
                var rs = prof.Nodes[prof.RootIndex].LocalScale;
                var sc = frames.Select(f => rs * (clip.ScaleAt(f) / Math.Max(1e-6f, clip.Scale))).ToArray();
                sb.Append("  m_ScaleCurves:\n  - curve:\n      serializedVersion: 2\n      m_Curve:\n");
                WriteVecKeys(sb, times, sc, opt.PosTolerance);
                sb.Append("      m_PreInfinity: 2\n      m_PostInfinity: 2\n      m_RotationOrder: 4\n    path: \n");
            }
            else sb.Append("  m_ScaleCurves: []\n");

            WriteFloatCurves(sb, "m_FloatCurves", floatCurves, times, opt.FloatTolerance);
            sb.Append("  m_PPtrCurves: []\n");
            sb.Append("  m_SampleRate: ").Append(F(clip.Fps / step)).Append('\n');
            sb.Append(
                "  m_WrapMode: 0\n  m_Bounds:\n    m_Center: {x: 0, y: 0, z: 0}\n    m_Extent: {x: 0, y: 0, z: 0}\n");
            sb.Append("  m_ClipBindingConstant:\n    genericBindings: []\n    pptrCurveMapping: []\n");
            sb.Append(
                "  m_AnimationClipSettings:\n    serializedVersion: 2\n    m_AdditiveReferencePoseClip: {fileID: 0}\n    m_AdditiveReferencePoseTime: 0\n");
            sb.Append("    m_StartTime: 0\n    m_StopTime: ").Append(F(stop)).Append('\n');
            sb.Append(
                "    m_OrientationOffsetY: 0\n    m_Level: 0\n    m_CycleOffset: 0\n    m_HasAdditiveReferencePose: 0\n");
            sb.Append("    m_LoopTime: ").Append(opt.Loop ? 1 : 0).Append('\n');
            sb.Append(
                "    m_LoopBlend: 0\n    m_LoopBlendOrientation: 0\n    m_LoopBlendPositionY: 0\n    m_LoopBlendPositionXZ: 0\n");
            sb.Append(
                "    m_KeepOriginalOrientation: 1\n    m_KeepOriginalPositionY: 1\n    m_KeepOriginalPositionXZ: 1\n    m_HeightFromFeet: 0\n    m_Mirror: 0\n");
            // editor curves: per-component copies (Unity's Animation window edits these)
            var editor = new List<Tuple<string, string, int, float[]>>();
            foreach (var c in rotCurves)
                for (int k = 0; k < 4; k++)
                    editor.Add(Tuple.Create("m_LocalRotation." + "xyzw"[k], c.Item1, 4,
                        c.Item2.Select(q => k == 0 ? q.x : k == 1 ? q.y : k == 2 ? q.z : q.w).ToArray()));
            foreach (var c in posCurves)
                for (int k = 0; k < 3; k++)
                    editor.Add(Tuple.Create("m_LocalPosition." + "xyz"[k], c.Item1, 4,
                        c.Item2.Select(v => k == 0 ? v.x : k == 1 ? v.y : v.z).ToArray()));
            editor.AddRange(floatCurves);
            WriteFloatCurves(sb, "m_EditorCurves", editor, times, opt.FloatTolerance, opt.RotTolerance,
                opt.PosTolerance);
            sb.Append(
                "  m_EulerEditorCurves: []\n  m_HasGenericRootTransform: 0\n  m_HasMotionFloatCurves: 0\n  m_Events: []\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        internal static string YamlName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            bool plain = s.All(ch => ch < 128 && (char.IsLetterOrDigit(ch) || "_-./ ()".IndexOf(ch) >= 0)) &&
                         !s.StartsWith(" ") && !s.EndsWith(" ") && !char.IsDigit(s[0]) &&
                         s.IndexOf(": ", StringComparison.Ordinal) < 0;
            if (plain) return s;
            var sb = new StringBuilder("\"");
            foreach (char ch in s)
            {
                if (ch == '"') sb.Append("\\\"");
                else if (ch == '\\') sb.Append("\\\\");
                else if (ch < 32) sb.Append("\\x").Append(((int)ch).ToString("X2"));
                else sb.Append(ch);
            }

            return sb.Append('"').ToString();
        }

        // ---------------------------------------------------------------- key writing with reduction

        static List<int> Reduce(int n, Func<int, int, int, float> err, float tol)
        {
            // greedy: extend each linear segment as far as all skipped keys stay within tolerance
            var keep = new List<int> { 0 };
            int a = 0;
            while (a < n - 1)
            {
                int b = a + 1;
                while (b + 1 < n)
                {
                    bool ok = true;
                    for (int m = a + 1; m <= b; m++)
                        if (err(a, b + 1, m) > tol)
                        {
                            ok = false;
                            break;
                        }

                    if (!ok) break;
                    b++;
                    if (b - a > 240) break;
                }

                keep.Add(b);
                a = b;
            }

            return keep;
        }

        static float Lerp(float[] t, float a0, float a1, int i0, int i1, int m)
        {
            return a0 + (a1 - a0) * ((t[m] - t[i0]) / Math.Max(1e-9f, t[i1] - t[i0]));
        }

        static void WriteQuatKeys(StringBuilder sb, float[] t, Quat[] q, float tol)
        {
            int n = q.Length;
            var keep = Reduce(n, (a, b, m) =>
            {
                float e = Math.Abs(Lerp(t, q[a].x, q[b].x, a, b, m) - q[m].x);
                e = Math.Max(e, Math.Abs(Lerp(t, q[a].y, q[b].y, a, b, m) - q[m].y));
                e = Math.Max(e, Math.Abs(Lerp(t, q[a].z, q[b].z, a, b, m) - q[m].z));
                return Math.Max(e, Math.Abs(Lerp(t, q[a].w, q[b].w, a, b, m) - q[m].w));
            }, tol);
            for (int k = 0; k < keep.Count; k++)
            {
                int i = keep[k];
                Quat inS = new(0, 0, 0, 0), outS = new(0, 0, 0, 0);
                if (k > 0)
                {
                    int p = keep[k - 1];
                    float dt = t[i] - t[p];
                    inS = new Quat((q[i].x - q[p].x) / dt, (q[i].y - q[p].y) / dt, (q[i].z - q[p].z) / dt,
                        (q[i].w - q[p].w) / dt);
                }

                if (k < keep.Count - 1)
                {
                    int nx = keep[k + 1];
                    float dt = t[nx] - t[i];
                    outS = new Quat((q[nx].x - q[i].x) / dt, (q[nx].y - q[i].y) / dt, (q[nx].z - q[i].z) / dt,
                        (q[nx].w - q[i].w) / dt);
                }

                if (k == 0) inS = outS;
                if (k == keep.Count - 1) outS = inS;
                sb.Append("      - serializedVersion: 3\n        time: ").Append(F(t[i]))
                    .Append("\n        value: ").Append(Q4(q[i]))
                    .Append("\n        inSlope: ").Append(Q4(inS))
                    .Append("\n        outSlope: ").Append(Q4(outS))
                    .Append(
                        "\n        tangentMode: 0\n        weightedMode: 0\n        inWeight: {x: 0.33333334, y: 0.33333334, z: 0.33333334, w: 0.33333334}\n        outWeight: {x: 0.33333334, y: 0.33333334, z: 0.33333334, w: 0.33333334}\n");
            }
        }

        static void WriteVecKeys(StringBuilder sb, float[] t, Vec3[] v, float tol)
        {
            int n = v.Length;
            var keep = Reduce(n, (a, b, m) =>
            {
                float e = Math.Abs(Lerp(t, v[a].x, v[b].x, a, b, m) - v[m].x);
                e = Math.Max(e, Math.Abs(Lerp(t, v[a].y, v[b].y, a, b, m) - v[m].y));
                return Math.Max(e, Math.Abs(Lerp(t, v[a].z, v[b].z, a, b, m) - v[m].z));
            }, tol);
            for (int k = 0; k < keep.Count; k++)
            {
                int i = keep[k];
                Vec3 inS = Vec3.Zero, outS = Vec3.Zero;
                if (k > 0)
                {
                    int p = keep[k - 1];
                    inS = (v[i] - v[p]) / (t[i] - t[p]);
                }

                if (k < keep.Count - 1)
                {
                    int nx = keep[k + 1];
                    outS = (v[nx] - v[i]) / (t[nx] - t[i]);
                }

                if (k == 0) inS = outS;
                if (k == keep.Count - 1) outS = inS;
                sb.Append("      - serializedVersion: 3\n        time: ").Append(F(t[i]))
                    .Append("\n        value: ").Append(V3(v[i]))
                    .Append("\n        inSlope: ").Append(V3(inS))
                    .Append("\n        outSlope: ").Append(V3(outS))
                    .Append(
                        "\n        tangentMode: 0\n        weightedMode: 0\n        inWeight: {x: 0.33333334, y: 0.33333334, z: 0.33333334}\n        outWeight: {x: 0.33333334, y: 0.33333334, z: 0.33333334}\n");
            }
        }

        static void WriteFloatCurves(StringBuilder sb, string field, List<Tuple<string, string, int, float[]>> curves,
            float[] t, float tol, float rotTol = 0, float posTol = 0)
        {
            if (curves.Count == 0)
            {
                sb.Append("  ").Append(field).Append(": []\n");
                return;
            }

            sb.Append("  ").Append(field).Append(":\n");
            foreach (var c in curves)
            {
                float[] v = c.Item4;
                float ctol = c.Item1.StartsWith("m_LocalRotation", StringComparison.Ordinal) ? rotTol :
                    c.Item1.StartsWith("m_LocalPosition", StringComparison.Ordinal) ? posTol :
                    c.Item1.StartsWith("blendShape.", StringComparison.Ordinal) ? tol * 100f : tol;
                bool stepped = c.Item3 == 95 && IsStepped(v);
                var keep = stepped
                    ? StepKeys(v)
                    : Reduce(v.Length, (a, b, m) => Math.Abs(Lerp(t, v[a], v[b], a, b, m) - v[m]), ctol);
                sb.Append("  - serializedVersion: 2\n    curve:\n      serializedVersion: 2\n      m_Curve:\n");
                for (int k = 0; k < keep.Count; k++)
                {
                    int i = keep[k];
                    float inS = 0, outS = 0;
                    if (!stepped)
                    {
                        if (k > 0)
                        {
                            int p = keep[k - 1];
                            inS = (v[i] - v[p]) / (t[i] - t[p]);
                        }

                        if (k < keep.Count - 1)
                        {
                            int nx = keep[k + 1];
                            outS = (v[nx] - v[i]) / (t[nx] - t[i]);
                        }

                        if (k == 0) inS = outS;
                        if (k == keep.Count - 1) outS = inS;
                    }

                    string slopeIn = stepped ? "Infinity" : F(inS), slopeOut = stepped ? "Infinity" : F(outS);
                    sb.Append("      - serializedVersion: 3\n        time: ").Append(F(t[i]))
                        .Append("\n        value: ").Append(F(v[i]))
                        .Append("\n        inSlope: ").Append(slopeIn)
                        .Append("\n        outSlope: ").Append(slopeOut)
                        .Append(stepped ? "\n        tangentMode: 103\n" : "\n        tangentMode: 0\n")
                        .Append(
                            "        weightedMode: 0\n        inWeight: 0.33333334\n        outWeight: 0.33333334\n");
                }

                sb.Append("      m_PreInfinity: 2\n      m_PostInfinity: 2\n      m_RotationOrder: 4\n");
                sb.Append("    attribute: ").Append(YamlName(c.Item1)).Append('\n');
                sb.Append("    path: ").Append(YamlName(c.Item2)).Append('\n');
                sb.Append("    classID: ").Append(c.Item3).Append('\n');
                sb.Append("    script: {fileID: 0}\n    flags: 0\n");
            }
        }

        static bool IsStepped(float[] v)
        {
            foreach (float x in v)
                if (Math.Abs(x - Math.Round(x)) > 1e-4)
                    return false;
            return true;
        }

        static List<int> StepKeys(float[] v)
        {
            var keep = new List<int> { 0 };
            for (int i = 1; i < v.Length; i++)
                if (v[i] != v[i - 1])
                    keep.Add(i);
            if (keep[keep.Count - 1] != v.Length - 1) keep.Add(v.Length - 1);
            return keep;
        }

        static string Q4(Quat q)
        {
            return "{x: " + F(q.x) + ", y: " + F(q.y) + ", z: " + F(q.z) + ", w: " + F(q.w) + "}";
        }

        static string V3(Vec3 v)
        {
            return "{x: " + F(v.x) + ", y: " + F(v.y) + ", z: " + F(v.z) + "}";
        }
    }
}
