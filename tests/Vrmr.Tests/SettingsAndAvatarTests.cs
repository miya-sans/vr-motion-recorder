using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace VRMR.Tests
{
    public class SettingsTests
    {
        /// <summary>A settings file as the first version wrote it: no schema number, the user's eye height on its own, the old face mesh default.</summary>
        const string FirstVersionFile = @"{
  ""recordingsDir"": ""Takes-elsewhere"",
  ""useOscQuery"": false,
  ""legacyPort"": 9123,
  ""realEyeHeight"": 1.55,
  ""faceMeshPath"": ""Body"",
  ""exportFps"": 30,
  ""wx"": 40, ""wy"": 50,
  ""avatarModelMap"": { ""avtr_00000000-0000-0000-0000-000000000001"": ""model.fbx.meta"" },
  ""somethingFromAnotherVersion"": { ""keep"": [1, 2, 3] }
}";

        static AppPaths PathsWith(TempDir tmp, string settingsJson)
        {
            var p = AppPaths.ForDataRoot(tmp.Path);
            if (settingsJson != null) { Directory.CreateDirectory(p.AppDataDir); File.WriteAllText(p.SettingsFile, settingsJson); }
            return p;
        }

        [Fact]
        public void WithoutAFileTheDefaultsBelongToThePaths()
        {
            using (var tmp = new TempDir())
            {
                var p = PathsWith(tmp, null);
                var s = Settings.Load(p);
                Assert.Equal(0, s.LoadedSchema);
                Assert.Equal(p.RecordingsDir, s.RecordingsDir);
                Assert.Equal(p.AvatarLibraryDir, s.AvatarLibraryDir);
                Assert.Equal("", s.FaceMeshPath);
                Assert.Equal("", s.BlenderPath);
                Assert.Empty(s.UnityProjects);
            }
        }

        [Fact]
        public void AFirstVersionFileIsUpgraded()
        {
            using (var tmp = new TempDir())
            {
                var s = Settings.Load(PathsWith(tmp, FirstVersionFile));
                Assert.Equal(1, s.LoadedSchema);
                Assert.Equal("Takes-elsewhere", s.RecordingsDir);
                Assert.False(s.UseOscQuery);
                Assert.Equal(9123, s.LegacyPort);
                Assert.Equal(30, s.ExportFps);
                Assert.Equal(40, s.WindowX);
                Assert.Equal(CalHeightMode.EyeHeight, s.CalibrationDefaults.Height);
                Assert.Equal(1.55f, s.CalibrationDefaults.HeightValue, 4);
                Assert.Equal("", s.FaceMeshPath); // the old default now means "the avatar's own face mesh"
                Assert.Single(s.AvatarModelMap);
            }
        }

        [Fact]
        public void SavingKeepsUnknownKeysAndWritesTheSchema()
        {
            using (var tmp = new TempDir())
            {
                var p = PathsWith(tmp, FirstVersionFile);
                var s = Settings.Load(p);
                s.ExportFps = 50;
                s.Save();
                var d = Json.ParseObject(File.ReadAllText(p.SettingsFile));
                Assert.Equal(Settings.SchemaVersion, d.GetInt("schema"));
                Assert.Equal(50, d.GetInt("exportFps"));
                Assert.Equal(3, d.GetObj("somethingFromAnotherVersion").GetList("keep").Count);
                Assert.False(d.ContainsKey("realEyeHeight"));
                Assert.False(File.Exists(p.SettingsFile + ".tmp"));
                var again = Settings.Load(p);
                Assert.Equal(Settings.SchemaVersion, again.LoadedSchema);
                Assert.Equal(1.55f, again.CalibrationDefaults.HeightValue, 4);
            }
        }

        [Fact]
        public void DefaultFoldersAreNotWrittenToTheFile()
        {
            using (var tmp = new TempDir())
            {
                var p = PathsWith(tmp, null);
                var s = Settings.Load(p);
                s.Save();
                var d = Json.ParseObject(File.ReadAllText(p.SettingsFile));
                Assert.False(d.ContainsKey("recordingsDir"));
                Assert.False(d.ContainsKey("avatarLibraryDir"));
                Assert.DoesNotContain(tmp.Path.Replace("\\", "\\\\"), File.ReadAllText(p.SettingsFile));

                s.RecordingsDir = Path.Combine(tmp.Path, "Elsewhere");
                s.Save();
                Assert.Equal(s.RecordingsDir, Settings.Load(p).RecordingsDir);
            }
        }

        [Fact]
        public void UpgradingTwiceChangesNothingMore()
        {
            var d = Json.ParseObject(FirstVersionFile);
            Assert.Equal(1, SettingsMigrations.Upgrade(d));
            string once = Json.Serialize(d);
            d.Remove("schema"); // another version wrote the file back without it
            Assert.Equal(1, SettingsMigrations.Upgrade(d));
            Assert.Equal(once, Json.Serialize(d));
        }

        [Fact]
        public void AFileFromANewerVersionKeepsItsSchemaNumber()
        {
            using (var tmp = new TempDir())
            {
                var p = PathsWith(tmp, "{ \"schema\": 99, \"exportFps\": 24, \"futureSetting\": true }");
                var s = Settings.Load(p);
                Assert.Equal(99, s.LoadedSchema);
                Assert.Equal(24, s.ExportFps);
                s.Save();
                var d = Json.ParseObject(File.ReadAllText(p.SettingsFile));
                Assert.Equal(99, d.GetInt("schema"));
                Assert.True(d.GetBool("futureSetting"));
            }
        }
    }

    public class AvatarLibraryTests
    {
        static AvatarLibrary Library(TempDir tmp, out Settings settings)
        {
            settings = new Settings(AppPaths.ForDataRoot(tmp.Path));
            return new AvatarLibrary(settings);
        }

        /// <summary>Writes a library entry the way a build from before aliases did: the names under "match".</summary>
        static void WriteOldEntry(Settings s, string name, params string[] match)
        {
            var skeleton = Mannequin.Profile(1.5f); skeleton.SourcePath = name + ".fbx.meta"; skeleton.LibraryId = "";
            var d = new Dictionary<string, object>
            {
                ["vrmrAvatar"] = 1, ["id"] = name, ["name"] = name, ["fbx"] = "", ["source"] = "",
                ["match"] = match.Cast<object>().ToList(), ["avatarIds"] = new List<object>(), ["skeleton"] = skeleton.ToJson(),
            };
            Directory.CreateDirectory(s.AvatarLibraryDir);
            File.WriteAllText(Path.Combine(s.AvatarLibraryDir, name + ".vrmavatar.json"), Json.Serialize(d, true));
        }

        [Fact]
        public void AnEmptyLibraryStillHasTheMannequin()
        {
            using (var tmp = new TempDir())
            {
                var lib = Library(tmp, out _);
                Assert.Single(lib.All());
                Assert.True(lib.BuiltIn.BuiltIn);
                Assert.Same(lib.BuiltIn, lib.Default);
                Assert.Same(lib.BuiltIn, lib.Get(Mannequin.Id));
                var m = lib.Match("avtr_unknown", "Some avatar", 1.3f, 1f);
                Assert.Same(lib.BuiltIn, m.Entry);
                Assert.False(m.Sure);
                var profile = lib.ForAvatar("avtr_unknown");
                Assert.True(Mannequin.Is(profile));
                Assert.Equal(Mannequin.Id, profile.LibraryId);
                // it is not a file: saving and removing do nothing
                lib.Save(lib.BuiltIn); lib.Remove(lib.BuiltIn);
                Assert.Single(lib.All());
                Assert.False(Directory.Exists(lib.Dir));
            }
        }

        [Fact]
        public void EntriesFromBeforeAliasesKeepMatchingByTheirNames()
        {
            using (var tmp = new TempDir())
            {
                var lib = Library(tmp, out var s);
                WriteOldEntry(s, "Alpha", "alpha", "アルファ");
                WriteOldEntry(s, "Beta", "beta");
                lib.Reload();
                var alpha = lib.Get("Alpha");
                Assert.Equal(new[] { "alpha", "アルファ" }, alpha.Aliases);
                Assert.Same(alpha.Aliases, alpha.Match);
                Assert.Same(alpha, lib.Match("", "My ALPHA (edit) v2").Entry);
                Assert.Same(alpha, lib.Match("", "かわいいアルファ改変").Entry);
                Assert.Same(lib.Get("Beta"), lib.Match("", "beta_test").Entry);
                Assert.True(lib.Match("", "beta_test").Sure);
                // nothing matches: the default is one of the user's entries, never the mannequin while they have one
                var unknown = lib.Match("", "Something else");
                Assert.False(unknown.Entry.BuiltIn);
                Assert.Equal(3, lib.All().Count);
                Assert.True(lib.All().Last().BuiltIn);
            }
        }

        [Fact]
        public void AliasesAreSavedAndEditable()
        {
            using (var tmp = new TempDir())
            {
                var lib = Library(tmp, out var s);
                WriteOldEntry(s, "Alpha", "alpha");
                lib.Reload();
                var alpha = lib.Get("Alpha");
                alpha.Aliases.Add("gamma");
                lib.Save(alpha);
                var d = Json.ParseObject(File.ReadAllText(alpha.FilePath));
                Assert.Equal(new object[] { "alpha", "gamma" }, d.GetList("aliases"));
                lib.Reload();
                Assert.Same(lib.Get("Alpha"), lib.Match("", "Gamma custom").Entry);
            }
        }

        [Fact]
        public void AnAvatarIdCanBeLinkedToTheMannequin()
        {
            using (var tmp = new TempDir())
            {
                var lib = Library(tmp, out var s);
                WriteOldEntry(s, "Alpha", "alpha");
                lib.Reload();
                lib.LinkId("avtr_1", lib.BuiltIn);
                Assert.Same(lib.BuiltIn, lib.Match("avtr_1", "alpha").Entry);
                // and the link survives a restart, through the settings
                var again = new AvatarLibrary(Settings.Load(s.Paths));
                Assert.True(again.Match("avtr_1", "alpha").Entry.BuiltIn);
            }
        }

        [Theory]
        [InlineData("Alpha_PC", "Alpha")]
        [InlineData("Alpha_Quest_v1.02", "Alpha")]
        [InlineData("アルファver2", "アルファ")]
        [InlineData("alpha (FT)", "alpha")]
        [InlineData("ShopName_Alpha", null)]       // two names: which one is the model?
        [InlineData("Alpha_Hair", null)]
        [InlineData("Avatar_v3", null)]            // nothing but generic words
        [InlineData("ab", null)]                   // too short to be safe
        public void OnlySingleWordNamesBecomeAliases(string packageName, string expected)
        {
            Assert.Equal(expected, AvatarAliases.SingleName(packageName));
        }

        [Fact]
        public void ImportAliasesComeFromThePackagesOwnNames()
        {
            var aliases = AvatarAliases.From("Alpha", "ALPHA", new[] { "Alpha_PC", "アルファ", "Alpha_Ribbon", "ShopName Alpha", "Alpha_v1.3" });
            Assert.Equal(new[] { "alpha", "アルファ" }, aliases);
        }

        [Fact]
        public void TheFaceMeshIsTheOneWithTheFaceShapes()
        {
            using (var tmp = new TempDir())
            {
                var lib = Library(tmp, out _);
                var profile = Mannequin.Profile(); profile.SourcePath = "x.vrmskel.json"; profile.LibraryId = "";
                Assert.Equal(FaceMeshFinder.Fallback, lib.FaceMeshFor(profile));
                profile.Meshes.Add(new AvatarMesh { Path = "Clothes", BlendShapes = { "Shrink_A", "Shrink_B" } });
                profile.Meshes.Add(new AvatarMesh { Path = "Armature/Face", BlendShapes = { "JawOpen", "EyeClosedLeft", "EyeClosedRight", "MouthSmileLeft" } });
                Assert.Equal("Armature/Face", lib.FaceMeshFor(profile));
                Assert.Equal("Custom", lib.FaceMeshFor(profile, " Custom "));
                Assert.Equal(FaceMeshFinder.Fallback, lib.FaceMeshFor(lib.BuiltIn.Profile()));
            }
        }
    }

    public class MannequinTests
    {
        [Fact]
        public void TheSkeletonIsACompleteHumanoidInATPose()
        {
            var p = Mannequin.Profile();
            for (int b = 0; b < HumanBones.Count; b++)
                Assert.True(p.Has(b) == (b != HumanBones.Jaw), HumanBones.Names[b]);
            Assert.True(Mannequin.Is(p));
            Assert.True(Mannequin.Is(p.Clone()));
            Assert.Equal(1.59f, p.EyeHeight, 3);
            foreach (var n in p.Nodes) Assert.Equal(1f, n.LocalRot.w, 5);
            // arms straight out, legs straight down, left and right mirrored
            Assert.Equal(p.HumanRestPos(HumanBones.LeftUpperArm).y, p.HumanRestPos(HumanBones.LeftHand).y, 4);
            Assert.Equal(p.HumanRestPos(HumanBones.LeftUpperLeg).x, p.HumanRestPos(HumanBones.LeftFoot).x, 4);
            for (int b = 0; b < HumanBones.Count; b++)
            {
                if (!HumanBones.IsLeft(b)) continue;
                var l = p.HumanRestPos(b); var r = p.HumanRestPos(HumanBones.Mirror(b));
                Assert.True((new Vec3(-l.x, l.y, l.z) - r).Length < 1e-5f, HumanBones.Names[b]);
            }
            Assert.True(p.HumanRestPos(HumanBones.LeftHand).x < 0, "left is at -X");
            // every bone hangs from its humanoid parent
            for (int b = 1; b < HumanBones.Count; b++)
            {
                if (!p.Has(b)) continue;
                int parent = HumanBones.ResolveParent(b, p.HumanExists);
                Assert.Equal(p.Human[parent], p.Nodes[p.Human[b]].Parent);
            }
            float span = p.HumanRestPos(HumanBones.RightMiddleProximal + 2).x - p.HumanRestPos(HumanBones.LeftMiddleProximal + 2).x;
            Assert.InRange(span, 1.60f, 1.80f); // about as wide as it is tall
        }

        [Fact]
        public void TheSizeFollowsTheHeightAskedFor()
        {
            Assert.Equal(1.59f * 1.2f / 1.7f, Mannequin.Profile(1.2f).EyeHeight, 3);
            Assert.Equal(1.4f, AvatarProfile.Generic(1.4f).EyeHeight, 3);
        }

        [Fact]
        public void TheMeshIsClosedPiecesFacingOutwards()
        {
            var p = Mannequin.Profile();
            var m = MannequinMesh.For(p);
            Assert.Same(m, MannequinMesh.For(Mannequin.Profile()));
            Assert.Null(MannequinMesh.For(new AvatarProfile()));
            Assert.InRange(m.VertexCount, 2000, 20000);
            Assert.Equal(m.VertexCount, m.Normals.Length);
            Assert.Equal(m.VertexCount, m.Node.Length);
            Assert.Equal(m.VertexCount, m.Color.Length);
            Assert.Equal(0, m.Triangles.Length % 3);
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < m.VertexCount; i++)
            {
                Assert.InRange(m.Normals[i].Length, 0.999f, 1.001f);
                Assert.InRange(m.Node[i], 0, p.Nodes.Count - 1);
                Assert.True(p.HumanOfNode(m.Node[i]) >= 0, "every vertex moves with a humanoid bone");
                minY = Math.Min(minY, m.Positions[i].y); maxY = Math.Max(maxY, m.Positions[i].y);
            }
            Assert.InRange(minY, -0.005f, 0.01f);          // stands on the floor
            Assert.InRange(maxY, 1.68f, 1.72f);            // as tall as it says
            for (int t = 0; t < m.Triangles.Length; t += 3)
            {
                int a = m.Triangles[t], b = m.Triangles[t + 1], c = m.Triangles[t + 2];
                Assert.Equal(m.Node[a], m.Node[b]); Assert.Equal(m.Node[a], m.Node[c]); // rigid pieces
                var face = Vec3.Cross(m.Positions[b] - m.Positions[a], m.Positions[c] - m.Positions[a]);
                Assert.True(Vec3.Dot(face, m.Normals[a] + m.Normals[b] + m.Normals[c]) >= 0, "front faces point outwards");
            }
        }

        [Fact]
        public void SkinningFollowsThePose()
        {
            var p = Mannequin.Profile();
            var m = MannequinMesh.For(p);
            var pos = (Vec3[])p.RestWorldPos.Clone(); var rot = (Quat[])p.RestWorldRot.Clone();
            var outPos = new Vec3[m.VertexCount]; var outNrm = new Vec3[m.VertexCount];
            m.Skin(pos, rot, outPos, outNrm);
            for (int i = 0; i < m.VertexCount; i++) Assert.True((outPos[i] - m.Positions[i]).Length < 1e-5f);
            // lift everything by a metre and turn it a quarter: the head ends up where the pose puts it
            var turn = Quat.YawRotation(90);
            for (int n = 0; n < pos.Length; n++) { pos[n] = turn * pos[n] + Vec3.Up; rot[n] = turn * rot[n]; }
            m.Skin(pos, rot, outPos, outNrm);
            for (int i = 0; i < m.VertexCount; i++) Assert.True((outPos[i] - (turn * m.Positions[i] + Vec3.Up)).Length < 1e-4f);
        }
    }
}
