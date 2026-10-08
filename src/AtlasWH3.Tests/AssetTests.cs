using AtlasWH3.Core;
using AtlasWH3.Core.Assets;
using AtlasWH3.Core.Rendering;
using AtlasWH3.Formats.Dds;
using AtlasWH3.Formats.Models;
using AtlasWH3.Formats.Packs;

namespace AtlasWH3.Tests;

public class AssetTests
{
    private static readonly ProjectPaths Paths = TestKits.VanillaPaths;
    private static readonly Lazy<ModelLibrary?> Library = new(() => Directory.Exists(Paths.GameDataDir) ? ModelLibrary.ForGame(Paths) : null);

    private const string Tree = "rigidmodels/campaign/vegetation/temperate/temperate_tree_metasequoia_harvest_large_2.wsmodel";
    private const string Hall = "rigidmodels/campaign/settlements/3k_dlc06_nanman_hall_3.rigid_model_v2";
    private const string Decal = "rigidmodels/campaign/decals/abandoned_settlement_decal_1.rigid_model_v2";

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
        foreach (var path in new[] { Hall, "rigidmodels/campaign/vegetation/temperate/temperate_tree_metasequoia_large_2.rigid_model_v2" })
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
        Assert.Contains(tree.Lods[0], m => m.BaseColour!.EndsWith("temperate_trees_base_colour.dds", StringComparison.OrdinalIgnoreCase));
        var hall = lib.Load(Hall)!;
        Assert.Equal(5, hall.Lods[0].Count);
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
        var bytes = lib.Source.Read("RigidModels/campaign/vegetation/textures/temperate_trees_base_colour.dds");
        var full = DdsTexture.Decode(bytes);
        var small = DdsTexture.DecodeMip(bytes, 128);
        Assert.Equal((1024, 1024), (full.Width, full.Height));
        Assert.Equal((128, 128), (small.Width, small.Height));
        Assert.Contains(small.Rgba.Where((_, i) => i % 4 == 3), a => a < 128); // BC1 punch-through alpha survives
        // The small mip is a downscale of the top one: average colours agree.
        static double Mean(byte[] rgba) => rgba.Where((_, i) => i % 4 == 1).Average(b => (double)b);
        Assert.InRange(Mean(small.Rgba) - Mean(full.Rgba), -12, 12);
    }

    [Fact]
    public void HeaderBounds_CoverUndecodableModels()
    {
        if (Library.Value is not { } lib) return;
        const string lily = "rigidmodels/campaign/vegetation/water_lily/water_lily_2.rigid_model_v2";
        Assert.Null(lib.Load(lily)); // vertex format 12 is not decoded
        var b = lib.Bounds(lily);
        Assert.NotNull(b);
        Assert.True(b![3] > b[0] && b[5] > b[2]);
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
            var path = Path.Combine(dir.FullName, "rigidmodels", "campaign", "settlements");
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "3k_dlc06_nanman_hall_3.rigid_model_v2"), "override");
            var src = new AssetSource(lib.Source.Vanilla, [dir.FullName]);
            Assert.Equal("override"u8.ToArray(), src.Read(Hall));
            Assert.EndsWith(".rigid_model_v2", src.Locate(Hall));
            Assert.EndsWith(".pack", src.Locate(Tree)); // everything else still comes from the packs
        }
        finally { dir.Delete(true); }
    }

    /// <summary>Campaign tile bmd_data.bin: the same v35 body as global_props; props in 32 units per tile-map cell
    /// with z north from the tile's south-west corner; the river tiles' unknown sound section ends the read.</summary>
    [Fact]
    public void TileBmd_ReadsMountainAndCrossingProps()
    {
        if (Library.Value is not { } lib) return;
        var mountain = AtlasWH3.Formats.Props.GlobalProps.ReadBody(lib.Source.Read("terrain/tiles/campaign/mountains_subtropical/6x4_b3/bmd_data.bin"));
        Assert.Equal(252, mountain.Props.Count);
        Assert.All(mountain.Props, p => Assert.InRange(p.Transform.X, -10, 6 * 32 + 10));
        Assert.All(mountain.Props, p => Assert.InRange(p.Transform.Z, -10, 4 * 32 + 10));
        Assert.All(mountain.Props, p => Assert.StartsWith("BHM_", p.HeightMode)); // ABSOLUTE trees, CUSTOM_VERTEX_OFFSETTING rocks

        var crossing = AtlasWH3.Formats.Props.GlobalProps.ReadBody(lib.Source.Read("terrain/tiles/campaign/river_crossing/cross_3/bmd_data.bin"));
        var bridge = Assert.Single(crossing.Props, p => p.Path.Contains("bridge_medium"));
        Assert.InRange(bridge.Transform.X, 0, 64);
        Assert.InRange(bridge.Transform.Z, 0, 64);
    }
}
