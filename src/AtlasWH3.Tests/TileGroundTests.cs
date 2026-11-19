using System.Numerics;
using AtlasWH3.Core;
using AtlasWH3.Core.Campaign.TileMapCheck;
using AtlasWH3.Core.Rendering;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Packs;

namespace AtlasWH3.Tests;

/// <summary>Where the 3D view lays the tiles' own textures (roads, banks) and whether their roads join.</summary>
public class TileGroundTests
{
    [Theory]
    // A 3x2 tile placed at cell (10, 20): tile point (a, b) lands at (a, b), (b, W−a), (W−a, H−b), (H−b, a) cells
    // from the south-west corner (get_tile_transform).
    [InlineData(0x10, 1, 0, 11, 20)]
    [InlineData(0x20, 1, 0, 10, 22)]
    [InlineData(0x40, 1, 0, 12, 22)]
    [InlineData(0x80, 1, 0, 12, 21)]
    public void TileToWorld_TurnsAsTheEngine(byte orientation, int a, int b, float worldX, float worldZ)
    {
        var m = TileGroundMapping.TileToWorld(10, 20, 3, 2, orientation, 1f / 128, 1f / 128);
        var p = Vector3.Transform(new Vector3(a * 128, 0, b * 128), m);
        Assert.Equal(worldX, p.X, 4);
        Assert.Equal(worldZ, p.Z, 4);
    }

    [Fact]
    public void Uv_SkipsTheOneCellApron()
    {
        Assert.Equal(new Vector2(1 / 4f, 1 / 4f), TileGroundMapping.Uv(Vector3.Zero, 2, 2));
        Assert.Equal(new Vector2(3 / 4f, 3 / 4f), TileGroundMapping.Uv(new Vector3(256, 0, 256), 2, 2));
    }

    [Fact]
    public void Iee_RoadsJoinAcrossTileBorders()
    {
        var pack = Path.Combine(TestKits.Wh3GameData, TestKits.IeePack);
        if (!File.Exists(pack)) return;
        var source = AssetSource.ForGame(TestKits.Wh3GameData, extraPacks: [pack]);
        var list = TileList.Read(source.Read($"terrain/campaigns/{TestKits.Iee}/tile_list.bin"));
        var db = TileMapValidator.LoadDatabase(new ProjectPaths { GameDataDir = TestKits.Wh3GameData, AssemblyKitRoot = TestKits.Wh3Kit });
        static bool IsRoad(string layer) => layer.Contains("road", StringComparison.OrdinalIgnoreCase);
        var drawn = new TileRoadCheck(list, db, source.TryRead, IsRoad).Run();
        Assert.True(drawn.RoadTiles > 1000);
        Assert.True(drawn.JoinedShare > 0.95, $"joined {drawn.JoinedShare:P1}");
        foreach (var other in new[] { TileRoadCheck.Mapping.RowsFlipped, TileRoadCheck.Mapping.TurnsSwapped, TileRoadCheck.Mapping.NoApron })
            Assert.True(new TileRoadCheck(list, db, source.TryRead, IsRoad, other).Run().JoinedShare < 0.75, other.ToString());
    }
}
