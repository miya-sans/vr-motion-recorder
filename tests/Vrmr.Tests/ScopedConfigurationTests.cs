using System;
using System.IO;
using Xunit;

namespace VRMR.Tests
{
    public class ScopedConfigurationTests
    {
        [Fact]
        public void CalibrationLibrariesKeepFilesCachesAndEventsSeparate()
        {
            using var firstRoot = new TempDir();
            using var secondRoot = new TempDir();
            var first = new CalibrationLibrary(AppPaths.ForDataRoot(firstRoot.Path));
            var second = new CalibrationLibrary(AppPaths.ForDataRoot(secondRoot.Path));
            int firstChanges = 0, secondChanges = 0;
            first.Changed += () => firstChanges++;
            second.Changed += () => secondChanges++;
            var snapshot = new CalibrationSnapshot { Id = "same-id", Name = "First" };
            snapshot.Devices.Add(new CalibrationSnapshot.Device { Serial = "head", Class = 1 });
            first.Save(snapshot);
            Assert.Single(first.List());
            Assert.Empty(second.List());
            snapshot.Name = "Second";
            second.Save(snapshot);
            Assert.Equal("First", first.Get(snapshot.Id).Name);
            Assert.Equal("Second", second.Get(snapshot.Id).Name);
            Assert.Equal(1, firstChanges);
            Assert.Equal(1, secondChanges);
        }

        [Fact]
        public void PunchInSettingsUseOnlyTheProvidedPaths()
        {
            using var firstRoot = new TempDir();
            using var secondRoot = new TempDir();
            var first = AppPaths.ForDataRoot(firstRoot.Path);
            var second = AppPaths.ForDataRoot(secondRoot.Path);
            new PunchInSettings { PreRoll = 5, Length = 20 }.Save(first);
            new PunchInSettings { PreRoll = 2, Length = 8 }.Save(second);
            Assert.Equal(5, PunchInSettings.Load(first).PreRoll);
            Assert.Equal(20, PunchInSettings.Load(first).Length);
            Assert.Equal(2, PunchInSettings.Load(second).PreRoll);
            Assert.Equal(8, PunchInSettings.Load(second).Length);
        }

        [Fact]
        public void MeshLoadersKeepDecodersAndCachedTexturesSeparate()
        {
            using var root = new TempDir();
            string file = Path.Combine(root.Path, "triangle.fbx");
            File.WriteAllText(Path.Combine(root.Path, "test.png"), "decoder fixture");
            var objects = new FbxNode("Objects");
            objects.Add("Model", 1L, "Model::Triangle", "Mesh");
            var geometry = objects.Add("Geometry", 2L, "Geometry::Triangle", "Mesh");
            geometry.Add("Vertices", new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 });
            geometry.Add("PolygonVertexIndex", new[] { 0, 1, -3 });
            objects.Add("Material", 3L, "Material::Test", "");
            var texture = objects.Add("Texture", 4L, "Texture::Test", "");
            texture.Add("RelativeFilename", "test.png");
            var connections = new FbxNode("Connections");
            connections.Add("C", "OO", 2L, 1L);
            connections.Add("C", "OO", 3L, 1L);
            connections.Add("C", "OP", 4L, 3L, "DiffuseColor");
            FbxBinaryWriter.Write(file, new[] { objects, connections });
            var profile = Mannequin.Profile();
            profile.SourcePath = file;
            var first = new AvatarMeshLoader((path, size) => new TexturePixels
                { Width = 1, Height = 1, Argb = new[] { unchecked((int)0xffff0000) } });
            var second = new AvatarMeshLoader((path, size) => new TexturePixels
                { Width = 1, Height = 1, Argb = new[] { unchecked((int)0xff0000ff) } });
            var red = first.ForProfile(profile);
            var blue = second.ForProfile(profile);
            Assert.NotNull(red);
            Assert.NotNull(blue);
            Assert.Equal(unchecked((int)0xffff0000), Assert.Single(red.Textures).Px[0]);
            Assert.Equal(unchecked((int)0xff0000ff), Assert.Single(blue.Textures).Px[0]);
            Assert.Same(red, first.ForProfile(profile));
            first.Forget(file);
            Assert.NotSame(red, first.ForProfile(profile));
            Assert.Same(blue, second.ForProfile(profile));
            var untextured = new AvatarMeshLoader().ForProfile(profile);
            Assert.Null(Assert.Single(untextured.Textures).Px);
        }
    }
}
