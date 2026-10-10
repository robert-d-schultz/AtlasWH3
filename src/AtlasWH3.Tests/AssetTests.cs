using AtlasWH3.Core;
using AtlasWH3.Core.Assets;
using AtlasWH3.Core.Rendering;
using AtlasWH3.Formats.Dds;
using AtlasWH3.Formats.Models;
using AtlasWH3.Formats.Packs;

namespace AtlasWH3.Tests;

public class AssetTests
{
    private static readonly ProjectPaths Paths = new() { GameDataDir = TestKits.Wh3GameData, AssemblyKitRoot = TestKits.Wh3Kit };
    private static readonly Lazy<ModelLibrary?> Library = new(() => Directory.Exists(Paths.GameDataDir) ? ModelLibrary.ForGame(Paths) : null);

    private const string Tree = "rigidmodels/campaign/vegetation/trees/chs_tree_pine_large_01.wsmodel";
    private const string Hall = "rigidmodels/campaign/settlements/beastmen/bst_herdstone_main.rigid_model_v2";
    private const string Decal = "rigidmodels/campaign/decals/burnt_earth/burnt_earth_1.rigid_model_v2";

    [Fact]
    public void ModelFiles_Parse()
    {
        var ws = WsModelFile.Parse("""
            <model version="1">
              <geometry>a/b.rigid_model_v2</geometry>
              <materials>
                <material part_index="0" lod_index="0">m0.material</material>
                <material part_index="1" lod_index="0">m1.material</material>
                <material part_index="0" lod_index="2">m0_far.material</material>
              </materials>
            </model>
            """u8.ToArray());
        Assert.Equal("a/b.rigid_model_v2", ws.Geometry);
        Assert.Equal("m0.material", ws.MaterialFor(0, 1));   // falls back to the lower LOD
        Assert.Equal("m0_far.material", ws.MaterialFor(0, 3));
        Assert.Equal("m1.material", ws.MaterialFor(1, 2));

        var mat = MaterialFile.Parse("""
            <material><name>x_alpha_on.xml</name><shader>shaders/rigid.xml.shader</shader>
             <textures><texture><slot version="2">s_xml_base_colour</slot><source>t/a.dds</source></texture></textures>
             <params><param><name>wave_strength</name><type>float</type><value>0.1</value></param></params></material>
            """u8.ToArray());
        Assert.Equal("t/a.dds", mat.BaseColour);
        Assert.True(mat.AlphaTest);
        Assert.Equal(0.1f, mat.Float("wave_strength"), 5);
    }

    [Fact]
    public void RigidModel_PositionsMatchTheGeometryReader()
    {
        if (Library.Value is not { } lib) return; // game data not available on this machine
        foreach (var path in new[] { Hall, "rigidmodels/campaign/vegetation/trees/gen_tree_pine_large_01.rigid_model_v2" })
        {
            var bytes = lib.Source.Read(path);
            var full = RigidModel.Read(bytes);
            var geometry = RigidModelGeometry.Read(bytes);
            var positions = full.Lods[0].Meshes.SelectMany(m => m.Positions).ToArray();
            Assert.Equal(geometry.Positions.Length, positions.Length);
            Assert.Equal(geometry.Positions, positions, (a, b) => Math.Abs(a - b) < 1e-5);
            Assert.All(full.Lods[0].Meshes, m => Assert.Equal(m.VertexCount * 2, m.Uvs.Length));
        }
    }

    [Fact]
    public void Library_ResolvesWsModelMaterialsAndTextures()
    {
        if (Library.Value is not { } lib) return;
        var tree = lib.Load(Tree)!;
        Assert.Empty(tree.Problems);
        Assert.Equal(3, tree.Lods.Count);
        Assert.All(tree.Lods[0], m => Assert.True(m.AlphaTest));
        Assert.Contains(tree.Lods[0], m => m.BaseColour!.EndsWith("gen_tree_pine_base_colour.dds", StringComparison.OrdinalIgnoreCase));
        var hall = lib.Load(Hall)!;
        Assert.Equal(3, hall.Lods[0].Count);
        Assert.All(hall.Lods[0], m => Assert.True(lib.Source.Exists(m.BaseColour!)));
        // Decal stubs become a textured quad over the unit box.
        var decal = lib.Load(Decal)!;
        Assert.Equal(2, decal.Triangles());
        Assert.EndsWith("_base_colour.dds", decal.Lods[0][0].BaseColour);
        Assert.Equal([-0.5f, -0.5f, -0.5f, 0.5f, 0.5f, 0.5f], decal.Bounds);
    }

    [Fact]
    public void Dds_DecodesTheRequestedMip()
    {
        if (Library.Value is not { } lib) return;
        var bytes = lib.Source.Read("RigidModels/campaign/vegetation/textures/gen_tree_pine_base_colour.dds");
        var full = DdsTexture.Decode(bytes);
        var small = DdsTexture.DecodeMip(bytes, 128);
        Assert.Equal((256, 256), (full.Width, full.Height));
        Assert.Equal((128, 128), (small.Width, small.Height));
        Assert.Contains(small.Rgba.Where((_, i) => i % 4 == 3), a => a < 128); // the leaves' cut-out alpha survives (DX10 header)
        // The small mip is a downscale of the top one: average colours agree.
        static double Mean(byte[] rgba) => rgba.Where((_, i) => i % 4 == 1).Average(b => (double)b);
        Assert.InRange(Mean(small.Rgba) - Mean(full.Rgba), -12, 12);
    }

    [Fact]
    public void Preview_DrawsTheModel()
    {
        if (Library.Value is not { } lib) return;
        var hall = lib.Load(Hall)!;
        var rgba = ModelPreview.Render(hall, lib, new ModelPreview.Options(Size: 96));
        var covered = Enumerable.Range(0, 96 * 96).Count(i => rgba[i * 4 + 3] == 255 && (rgba[i * 4] != 0x26 || rgba[i * 4 + 1] != 0x26));
        Assert.InRange(covered, 96 * 96 / 6, 96 * 96 * 9 / 10);
    }

    [Fact]
    public void AssetSource_LooseFilesOverridePacks()
    {
        if (Library.Value is not { } lib) return;
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "rigidmodels", "campaign", "settlements", "beastmen");
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "bst_herdstone_main.rigid_model_v2"), "override");
            var src = new AssetSource(lib.Source.Vanilla, [dir.FullName]);
            Assert.Equal("override"u8.ToArray(), src.Read(Hall));
            Assert.EndsWith(".rigid_model_v2", src.Locate(Hall));
            Assert.EndsWith(".pack", src.Locate(Tree)); // everything else still comes from the packs
        }
        finally { dir.Delete(true); }
    }
}
