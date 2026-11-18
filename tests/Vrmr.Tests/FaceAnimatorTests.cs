using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

namespace VRMR.Tests
{
    /// <summary>Replaying a face-tracking add-on's animator from Unity's own files, on small made-up ones.</summary>
    public class FaceAnimatorTests
    {
        const string Head = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";

        /// <summary>A clip that sets face shapes on "Body" (Unity's 0..100) and, optionally, an animator parameter.</summary>
        static string Clip(string name, (string shape, float value)[] shapes, (string param, float value)[] parameters = null)
        {
            string curve(string attr, string path, float v, int classId) => string.Format(CultureInfo.InvariantCulture,
                "  - serializedVersion: 2\n    curve:\n      serializedVersion: 2\n      m_Curve:\n      - serializedVersion: 3\n        time: 0\n        value: {0}\n        inSlope: 0\n      m_PreInfinity: 2\n    attribute: {1}\n    path: {2}\n    classID: {3}\n", v, attr, path, classId);
            var sb = new System.Text.StringBuilder(Head + "--- !u!74 &7400000\nAnimationClip:\n  m_Name: " + name + "\n  m_FloatCurves:\n");
            foreach (var (s, v) in shapes) sb.Append(curve("blendShape." + s, "Body", v, 137));
            foreach (var (p, v) in parameters ?? Array.Empty<(string, float)>()) sb.Append(curve(p, "", v, 95));
            sb.Append("  m_Events: []\n");
            return sb.ToString();
        }

        const string Open = "0000000000000000000000000000000a", TongueA = "0000000000000000000000000000000b", TongueB = "0000000000000000000000000000000c",
                     Ctl = "0000000000000000000000000000000d", Over = "0000000000000000000000000000000e", Swap = "0000000000000000000000000000000f",
                     Links = "00000000000000000000000000000010", Prefab = "00000000000000000000000000000011";

        /// <summary>
        /// A controller with one layer: an "Init" state that moves on when Enable is on, to a state whose Direct tree
        /// (weighted by Blend) plays a clip and a 1D tree over Tongue between two clips.
        /// </summary>
        static string Controller() => Head + @"--- !u!91 &9100000
AnimatorController:
  m_Name: FX - Test
  m_AnimatorParameters:
  - m_Name: Enable
    m_Type: 4
    m_DefaultFloat: 0
    m_DefaultInt: 0
    m_DefaultBool: 0
  - m_Name: Blend
    m_Type: 1
    m_DefaultFloat: 1
    m_DefaultInt: 0
    m_DefaultBool: 0
  m_AnimatorLayers:
  - serializedVersion: 5
    m_Name: Face
    m_StateMachine: {fileID: 1000}
    m_BlendingMode: 0
    m_SyncedLayerIndex: -1
    m_DefaultWeight: 1
--- !u!1107 &1000
AnimatorStateMachine:
  m_Name: Face
  m_ChildStates:
  - serializedVersion: 1
    m_State: {fileID: 2000}
  - serializedVersion: 1
    m_State: {fileID: 3000}
  m_ChildStateMachines: []
  m_AnyStateTransitions: []
  m_DefaultState: {fileID: 2000}
--- !u!1102 &2000
AnimatorState:
  m_Name: Init
  m_Transitions:
  - {fileID: 4000}
  m_Motion: {fileID: 0}
--- !u!1101 &4000
AnimatorStateTransition:
  m_Name:
  m_Conditions:
  - m_ConditionMode: 1
    m_ConditionEvent: Enable
    m_EventTreshold: 0
  m_DstStateMachine: {fileID: 0}
  m_DstState: {fileID: 3000}
--- !u!1102 &3000
AnimatorState:
  m_Name: Tracking
  m_Transitions: []
  m_Motion: {fileID: 5000}
--- !u!206 &5000
BlendTree:
  m_Name: Root
  m_Childs:
  - serializedVersion: 2
    m_Motion: {fileID: 7400000, guid: " + Open + @", type: 2}
    m_Threshold: 0
    m_Position: {x: 0, y: 0}
    m_TimeScale: 1
    m_CycleOffset: 0
    m_DirectBlendParameter: FT/MouthOpenness
    m_Mirror: 0
  - serializedVersion: 2
    m_Motion: {fileID: 6000}
    m_Threshold: 0
    m_Position: {x: 0, y: 0}
    m_TimeScale: 1
    m_CycleOffset: 0
    m_DirectBlendParameter: Blend
    m_Mirror: 0
  m_BlendParameter: Blend
  m_BlendParameterY: Blend
  m_BlendType: 4
--- !u!206 &6000
BlendTree:
  m_Name: Tongue
  m_Childs:
  - serializedVersion: 2
    m_Motion: {fileID: 7400000, guid: " + TongueA + @", type: 2}
    m_Threshold: 0
    m_Position: {x: 0, y: 0}
    m_TimeScale: 1
    m_CycleOffset: 0
    m_DirectBlendParameter: Blend
    m_Mirror: 0
  - serializedVersion: 2
    m_Motion: {fileID: 7400000, guid: " + TongueB + @", type: 2}
    m_Threshold: 0.5
    m_Position: {x: 0, y: 0}
    m_TimeScale: 1
    m_CycleOffset: 0
    m_DirectBlendParameter: Blend
    m_Mirror: 0
  m_BlendParameter: Drive/FT/v2/TongueOut
  m_BlendParameterY: Blend
  m_BlendType: 0
";

        static Dictionary<string, string> Files(bool withOverride = false)
        {
            var files = new Dictionary<string, string>
            {
                [Ctl] = Controller(),
                [Open] = Clip("Open", new[] { ("MouthOpen*", 100f) }),
                [TongueA] = Clip("TongueIn", new[] { ("TongueOutStep1", 0f), ("MouthTongueOutMorph*", 0f) }),
                [TongueB] = Clip("TongueOut", new[] { ("TongueOutStep1", 100f), ("MouthTongueOutMorph*", 100f), ("MouthLowerDownLeft", -100f) }),
                // the add-on's parameter links: openness = JawOpen - MouthClosed
                [Links] = Head + "--- !u!114 &11400000\nMonoBehaviour:\n  m_Name: Links\n  m_Parameters:\n  - m_ParameterName: FT/v2/JawOpen\n    m_LinkedParameters:\n    - m_ParameterName: FT/MouthOpenness\n      m_Weight: 1\n      m_Absolute: 0\n  - m_ParameterName: FT/v2/MouthClosed\n    m_LinkedParameters:\n    - m_ParameterName: FT/MouthOpenness\n      m_Weight: -1\n      m_Absolute: 0\n",
                // a prefab that switches tracking on by default (Modular Avatar's parameter list)
                [Prefab] = Head + "--- !u!114 &1\nMonoBehaviour:\n  m_Name: \n  parameters:\n  - nameOrPrefix: Enable\n    remapTo: \n    syncType: 3\n    defaultValue: 1\n",
            };
            if (withOverride)
            {
                files[Swap] = Clip("TongueOut (avatar's own)", new[] { ("TongueOutStep1", 50f) });
                files[Over] = Head + "--- !u!221 &22100000\nAnimatorOverrideController:\n  m_Name: FX - Test (avatar)\n  m_Controller: {fileID: 9100000, guid: " + Ctl + ", type: 2}\n  m_Clips:\n  - m_OriginalClip: {fileID: 7400000, guid: " + TongueB + ", type: 2}\n    m_OverrideClip: {fileID: 7400000, guid: " + Swap + ", type: 2}\n";
            }
            return files;
        }

        static Func<string, float?> Recorded(params (string name, float value)[] values) =>
            name => values.Where(v => v.name == name).Select(v => (float?)v.value).FirstOrDefault();

        [Fact]
        public void TheLayerSettlesIntoItsTrackingStateAndBlendsTheTrees()
        {
            var anim = FaceAnimator.Load(Files());
            Assert.Equal("Body", anim.FacePath);
            Assert.Contains("MouthTongueOutMorph*", anim.Shapes);

            // the prefab turns Enable on, so the layer leaves Init; the 1D tree is half way to the tongue-out clip
            var w = anim.Evaluate(Recorded(("FT/v2/TongueOut", 0.25f), ("FT/v2/JawOpen", 0.7f), ("FT/v2/MouthClosed", 0.2f)));
            Assert.Equal(0.5f, w["TongueOutStep1"], 4);
            Assert.Equal(0.5f, w["MouthTongueOutMorph*"], 4);
            Assert.Equal(-0.5f, w["MouthLowerDownLeft"], 4);  // below zero, as the add-on drives it
            Assert.Equal(0.5f, w["MouthOpen*"], 4);            // Direct weight: the linked openness 0.7 - 0.2

            // past the last threshold the end clip has it all
            Assert.Equal(1f, anim.Evaluate(Recorded(("FT/v2/TongueOut", 0.9f)))["TongueOutStep1"], 4);
        }

        [Fact]
        public void TheRecordingWinsOverTheDefaults()
        {
            var anim = FaceAnimator.Load(Files());
            // the recording says tracking is off: the layer stays in Init, which plays nothing
            var w = anim.Evaluate(Recorded(("Enable", 0f), ("FT/v2/TongueOut", 1f)));
            Assert.Empty(w);
        }

        [Fact]
        public void AnOverrideControllerPlaysItsOwnClips()
        {
            var files = Files(withOverride: true);
            var anim = FaceAnimator.Load(files, roots: new[] { Over });
            var w = anim.Evaluate(Recorded(("FT/v2/TongueOut", 1f)));
            Assert.Equal(0.5f, w["TongueOutStep1"], 4);           // the avatar's clip in place of the base one
            Assert.False(w.ContainsKey("MouthLowerDownLeft"));
        }

        [Fact]
        public void TwoDimensionalTreesGiveACornerAllOfItsWeight()
        {
            // a cartesian 2D tree with four corners: at a corner, that corner's clip only; in the middle, a quarter each
            string ctl = Head + "--- !u!91 &9100000\nAnimatorController:\n  m_Name: FX - 2D\n  m_AnimatorParameters: []\n  m_AnimatorLayers:\n  - serializedVersion: 5\n    m_Name: L\n    m_StateMachine: {fileID: 1}\n    m_BlendingMode: 0\n    m_DefaultWeight: 1\n"
                       + "--- !u!1107 &1\nAnimatorStateMachine:\n  m_ChildStateMachines: []\n  m_AnyStateTransitions: []\n  m_DefaultState: {fileID: 2}\n"
                       + "--- !u!1102 &2\nAnimatorState:\n  m_Name: S\n  m_Transitions: []\n  m_Motion: {fileID: 3}\n"
                       + "--- !u!206 &3\nBlendTree:\n  m_Name: T\n  m_Childs:\n";
            var files = new Dictionary<string, string>();
            var corners = new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) };
            for (int i = 0; i < 4; i++)
            {
                string g = (0x20 + i).ToString("x32");
                files[g] = Clip("C" + i, new[] { ("Corner" + i, 100f) });
                ctl += "  - serializedVersion: 2\n    m_Motion: {fileID: 7400000, guid: " + g + ", type: 2}\n    m_Threshold: 0\n    m_Position: {x: " + corners[i].Item1 + ", y: " + corners[i].Item2 + "}\n    m_DirectBlendParameter: \n";
            }
            ctl += "  m_BlendParameter: X\n  m_BlendParameterY: Y\n  m_BlendType: 3\n";
            files["00000000000000000000000000000030"] = ctl;
            var anim = FaceAnimator.Load(files);

            var corner = anim.Evaluate(Recorded(("X", 1f), ("Y", 1f)));
            Assert.Equal(1f, corner["Corner3"], 4);
            Assert.Equal(0f, corner.TryGetValue("Corner0", out float c0) ? c0 : 0f, 4);
            var middle = anim.Evaluate(Recorded(("X", 0f), ("Y", 0f)));
            for (int i = 0; i < 4; i++) Assert.Equal(0.25f, middle["Corner" + i], 4);
        }

        [Fact]
        public void TheFilesAreGatheredFromAFolderKeptWithTheAvatarAndReadBack()
        {
            using var src = new TempDir();
            using var entry = new TempDir();
            // as in a Unity project: each file with its .meta, the add-on's controller in its "_FT" folder
            var files = Files();
            foreach (var kv in files)
            {
                string name = kv.Key == Ctl ? "Addon_FT/FX - Test.controller" : kv.Key == Links ? "Common/Links.asset" : kv.Key == Prefab ? "Addon_FT/Mouth.prefab" : "Clips/" + kv.Key + ".anim";
                string path = System.IO.Path.Combine(src.Path, name);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.WriteAllText(path, kv.Value);
                System.IO.File.WriteAllText(path + ".meta", "fileFormatVersion: 2\nguid: " + kv.Key + "\n");
            }
            var gathered = FaceAnimatorFiles.Gather(new[] { src.Path });
            Assert.Equal(new[] { Ctl }, gathered.Roots);
            Assert.Equal(files.Count, gathered.Texts.Count);

            string dir = FaceAnimatorFiles.Save(gathered, entry.Path);
            var anim = FaceAnimatorFiles.Load(dir);
            Assert.Equal(0.5f, anim.Evaluate(Recorded(("FT/v2/TongueOut", 0.25f)))["TongueOutStep1"], 4);
        }

        [Fact]
        public void UnityYamlReadsNestedMapsListsAndReferences()
        {
            var docs = UnityYaml.Parse(Controller());
            Assert.Equal(new[] { 91, 1107, 1102, 1101, 1102, 206, 206 }, docs.Select(d => d.ClassId));
            var tree = docs.First(d => d.FileId == 5000);
            Assert.Equal("BlendTree", tree.Type);
            var kids = tree.Body.Maps("m_Childs");
            Assert.Equal(2, kids.Count);
            Assert.Equal(Open, kids[0].RefOf("m_Motion").Guid);
            Assert.Equal(6000, kids[1].RefOf("m_Motion").FileId);
            Assert.Equal("Blend", kids[1].Str("m_DirectBlendParameter"));
            Assert.Equal(4000, docs.First(d => d.FileId == 2000).Body.Maps("m_Transitions")[0].Num("fileID"));
        }
    }
}
