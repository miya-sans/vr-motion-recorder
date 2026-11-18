using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VRMR
{
    /// <summary>What goes into a Humanoid clip and how closely its reduced curves follow the frames.</summary>
    public sealed class HumanoidAnimOptions
    {
        /// <summary>Largest error of a blendshape weight, of 100.</summary>
        public float BlendShapeTolerance = 0.2f;

        public string ClipName = "Clip";

        /// <summary>Face blendshape curves on <see cref="FaceMeshPath" />.</summary>
        public bool Face = true;

        public string FaceMeshPath = "Body";

        /// <summary>The foot and hand IK goal curves (Unity's foot IK reads the foot goals).</summary>
        public bool Goals = true;

        public bool Loop = false;

        /// <summary>Largest error of a reduced muscle curve, in muscle units (0.0005 is about 0.03 degrees).</summary>
        public float MuscleTolerance = 0.0005f;

        /// <summary>Largest error of a fractional parameter.</summary>
        public float ParamTolerance = 0.002f;

        /// <summary>The raw avatar parameters as Animator parameter curves.</summary>
        public bool Params = false;

        /// <summary>Largest error of root and goal positions, in human-scale units (0.0001 is under 0.1 mm).</summary>
        public float PositionTolerance = 0.0001f;

        /// <summary>Largest error of a root or goal quaternion component.</summary>
        public float RotationTolerance = 0.00005f;

        public Dictionary<string, string> ShapeRename;
    }

    /// <summary>What a Humanoid clip export wrote.</summary>
    public sealed class HumanoidAnimResult
    {
        public long Bytes;
        public int Frames, Curves, Keys;

        public override string ToString()
        {
            return $"{Curves} curves, {Keys} keys, {Bytes / 1048576.0:0.0} MB";
        }
    }

    /// <summary>
    ///     Writes one Unity Humanoid clip for a solved clip without Unity: root, muscles and IK goals as Animator curves
    ///     (they play on any Humanoid avatar), the face blendshapes and, on request, the avatar parameters.
    /// </summary>
    public static class HumanoidAnimExporter
    {
        static readonly string[] GoalNames = { "LeftFoot", "RightFoot", "LeftHand", "RightHand" };

        static readonly int[] TwistSharingBones =
        {
            HumanBones.LeftUpperLeg, HumanBones.RightUpperLeg, HumanBones.LeftLowerLeg, HumanBones.RightLowerLeg,
            HumanBones.LeftUpperArm, HumanBones.RightUpperArm, HumanBones.LeftLowerArm, HumanBones.RightLowerArm
        };

        /// <summary>Converts every frame of a solved clip to Humanoid form on the clip's own avatar.</summary>
        public static HumanoidFrame[] Convert(BakedClip clip, HumanoidRig rig)
        {
            var solver = new HumanoidPoseSolver(rig);
            var frames = new HumanoidFrame[clip.Frames];
            bool[] has = new bool[HumanBones.Count];
            for (int b = 0; b < has.Length; b++)
                has[b] = rig.Exists[b] && clip.BoneExists != null && clip.BoneExists[b];
            for (int f = 0; f < clip.Frames; f++)
            {
                frames[f] = new HumanoidFrame();
                solver.Solve(clip.Rot[f], has, clip.Hips[f] / Math.Max(1e-6f, clip.ScaleAt(f)), frames[f]);
            }

            MakeContinuous(frames, rig);
            return frames;
        }

        /// <summary>Removes full-turn jumps of limb twists and sign flips of quaternions between frames.</summary>
        static void MakeContinuous(HumanoidFrame[] frames, HumanoidRig rig)
        {
            foreach (int bone in TwistSharingBones)
            {
                int m = HumanoidTables.MuscleOfBone[bone, 0];
                float turn = (float)(2 * Math.PI) / rig.MuscleMax[m];
                for (int f = 1; f < frames.Length; f++)
                    frames[f].Muscles[m] -=
                        turn * (float)Math.Round((frames[f].Muscles[m] - frames[f - 1].Muscles[m]) / turn);
            }

            for (int f = 1; f < frames.Length; f++)
            {
                if (Quat.Dot(frames[f].RootQ, frames[f - 1].RootQ) < 0) frames[f].RootQ = frames[f].RootQ.Negated;
                for (int g = 0; g < HumanoidFrame.GoalCount; g++)
                    if (Quat.Dot(frames[f].GoalQ[g], frames[f - 1].GoalQ[g]) < 0)
                        frames[f].GoalQ[g] = frames[f].GoalQ[g].Negated;
            }
        }

        /// <summary>Writes the clip (Unity YAML, AnimationClip) to path.</summary>
        public static HumanoidAnimResult Export(BakedClip clip, string path, HumanoidAnimOptions opt)
        {
            opt = opt ?? new HumanoidAnimOptions();
            var result = new HumanoidAnimResult { Frames = clip.Frames };
            using (var w = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" })
            {
                w.Write("%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!74 &7400000\nAnimationClip:\n");
                w.Write(
                    "  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n");
                w.Write("  m_Name: " + UnityAnimExporter.YamlName(opt.ClipName) + "\n");
                w.Write("  serializedVersion: 7\n  m_Legacy: 0\n  m_Compressed: 0\n  m_UseHighQualityCurve: 1\n");
                w.Write(
                    "  m_RotationCurves: []\n  m_CompressedRotationCurves: []\n  m_EulerCurves: []\n  m_PositionCurves: []\n  m_ScaleCurves: []\n");
                w.Write("  m_FloatCurves:");
                var sb = new StringBuilder(1 << 16);

                void Curve(string attribute, string curvePath, int classId, float[] values, float tolerance,
                    bool stepped = false)
                {
                    var keys = stepped
                        ? CubicKeyReducer.ReduceStepped(values)
                        : CubicKeyReducer.Reduce(values, clip.Fps, tolerance);
                    sb.Clear();
                    if (result.Curves == 0) sb.Append('\n');
                    AppendCurve(sb, attribute, curvePath, classId, keys, clip.Fps);
                    w.Write(sb.ToString());
                    result.Curves++;
                    result.Keys += keys.Count;
                }

                if (clip.BodySource != "none" && clip.Frames > 0 && clip.Profile != null)
                    WriteBody(clip, opt, Curve);
                if (opt.Face)
                    foreach (var kv in clip.Face.OrderBy(k => k.Key, StringComparer.Ordinal))
                    {
                        string name = opt.ShapeRename != null && opt.ShapeRename.TryGetValue(kv.Key, out string rn)
                            ? rn
                            : kv.Key;
                        Curve("blendShape." + name, opt.FaceMeshPath, 137, kv.Value.Select(x => x * 100f).ToArray(),
                            opt.BlendShapeTolerance);
                    }

                if (opt.Params)
                    foreach (var kv in clip.ParamValues.OrderBy(k => k.Key, StringComparer.Ordinal))
                        Curve(kv.Key, "", 95, kv.Value, opt.ParamTolerance,
                            kv.Value.All(x => Math.Abs(x - Math.Round(x)) <= 1e-4));
                if (result.Curves == 0) w.Write(" []\n");
                float stop = clip.Frames > 1 ? (float)((clip.Frames - 1) / (double)clip.Fps) : 0;
                w.Write("  m_PPtrCurves: []\n  m_SampleRate: " + UnityAnimExporter.F(clip.Fps) + "\n  m_WrapMode: 0\n");
                w.Write("  m_Bounds:\n    m_Center: {x: 0, y: 0, z: 0}\n    m_Extent: {x: 0, y: 0, z: 0}\n");
                w.Write("  m_ClipBindingConstant:\n    genericBindings: []\n    pptrCurveMapping: []\n");
                w.Write(
                    "  m_AnimationClipSettings:\n    serializedVersion: 2\n    m_AdditiveReferencePoseClip: {fileID: 0}\n    m_AdditiveReferencePoseTime: 0\n");
                w.Write("    m_StartTime: 0\n    m_StopTime: " + UnityAnimExporter.F(stop) + "\n");
                w.Write(
                    "    m_OrientationOffsetY: 0\n    m_Level: 0\n    m_CycleOffset: 0\n    m_HasAdditiveReferencePose: 0\n");
                w.Write("    m_LoopTime: " + (opt.Loop ? 1 : 0) + "\n    m_LoopBlend: 0\n");
                // the whole motion stays on the body ("bake into pose", based upon the original), so the clip plays in
                // place as recorded whether or not the Animator applies root motion
                w.Write("    m_LoopBlendOrientation: 1\n    m_LoopBlendPositionY: 1\n    m_LoopBlendPositionXZ: 1\n");
                w.Write(
                    "    m_KeepOriginalOrientation: 1\n    m_KeepOriginalPositionY: 1\n    m_KeepOriginalPositionXZ: 1\n    m_HeightFromFeet: 0\n    m_Mirror: 0\n");
                w.Write(
                    "  m_EditorCurves: []\n  m_EulerEditorCurves: []\n  m_HasGenericRootTransform: 0\n  m_HasMotionFloatCurves: 0\n  m_Events: []\n");
            }

            result.Bytes = new FileInfo(path).Length;
            return result;
        }

        static void WriteBody(BakedClip clip, HumanoidAnimOptions opt,
            Action<string, string, int, float[], float, bool> curve)
        {
            var rig = HumanoidRig.FromProfile(clip.Profile);
            var frames = Convert(clip, rig);

            float[] Of(Func<HumanoidFrame, float> pick)
            {
                return frames.Select(pick).ToArray();
            }

            curve("RootT.x", "", 95, Of(f => f.RootT.x), opt.PositionTolerance, false);
            curve("RootT.y", "", 95, Of(f => f.RootT.y), opt.PositionTolerance, false);
            curve("RootT.z", "", 95, Of(f => f.RootT.z), opt.PositionTolerance, false);
            curve("RootQ.x", "", 95, Of(f => f.RootQ.x), opt.RotationTolerance, false);
            curve("RootQ.y", "", 95, Of(f => f.RootQ.y), opt.RotationTolerance, false);
            curve("RootQ.z", "", 95, Of(f => f.RootQ.z), opt.RotationTolerance, false);
            curve("RootQ.w", "", 95, Of(f => f.RootQ.w), opt.RotationTolerance, false);
            for (int g = 0; opt.Goals && g < HumanoidFrame.GoalCount; g++)
            {
                int goal = g;
                curve(GoalNames[g] + "T.x", "", 95, Of(f => f.GoalT[goal].x), opt.PositionTolerance, false);
                curve(GoalNames[g] + "T.y", "", 95, Of(f => f.GoalT[goal].y), opt.PositionTolerance, false);
                curve(GoalNames[g] + "T.z", "", 95, Of(f => f.GoalT[goal].z), opt.PositionTolerance, false);
                curve(GoalNames[g] + "Q.x", "", 95, Of(f => f.GoalQ[goal].x), opt.RotationTolerance, false);
                curve(GoalNames[g] + "Q.y", "", 95, Of(f => f.GoalQ[goal].y), opt.RotationTolerance, false);
                curve(GoalNames[g] + "Q.z", "", 95, Of(f => f.GoalQ[goal].z), opt.RotationTolerance, false);
                curve(GoalNames[g] + "Q.w", "", 95, Of(f => f.GoalQ[goal].w), opt.RotationTolerance, false);
            }

            for (int m = 0; m < HumanoidTables.MuscleCount; m++)
            {
                int muscle = m;
                curve(HumanoidTables.MuscleAttribute[m], "", 95, Of(f => f.Muscles[muscle]), opt.MuscleTolerance,
                    false);
            }
        }

        static void AppendCurve(StringBuilder sb, string attribute, string path, int classId, List<CurveKey> keys,
            float fps)
        {
            sb.Append("  - serializedVersion: 2\n    curve:\n      serializedVersion: 2\n      m_Curve:\n");
            foreach (var k in keys)
            {
                string slope = k.Stepped ? "Infinity" : UnityAnimExporter.F(k.Slope);
                sb.Append("      - serializedVersion: 3\n        time: ")
                    .Append(UnityAnimExporter.F((float)(k.Frame / (double)fps)))
                    .Append("\n        value: ").Append(UnityAnimExporter.F(k.Value))
                    .Append("\n        inSlope: ").Append(slope).Append("\n        outSlope: ").Append(slope)
                    .Append(k.Stepped ? "\n        tangentMode: 103" : "\n        tangentMode: 0")
                    .Append("\n        weightedMode: 0\n        inWeight: 0\n        outWeight: 0\n");
            }

            sb.Append("      m_PreInfinity: 2\n      m_PostInfinity: 2\n      m_RotationOrder: 4\n");
            sb.Append("    attribute: ").Append(UnityAnimExporter.YamlName(attribute)).Append('\n');
            sb.Append("    path: ").Append(UnityAnimExporter.YamlName(path)).Append('\n');
            sb.Append("    classID: ").Append(classId).Append("\n    script: {fileID: 0}\n    flags: 0\n");
        }
    }
}
