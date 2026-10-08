using System.Text.Json.Nodes;
using AtlasWH3.Core;
using AtlasWH3.Core.Campaign.TileMapCheck;
using AtlasWH3.Formats.Maps;
using Xunit;

namespace AtlasWH3.Tests;

/// <summary>Tile error mode: per-hex errors and their recommended fixes on small synthetic tile maps.</summary>
public class TileErrorsTests
{
    private static readonly Lazy<CampaignTileDatabase?> Db = new(() =>
    {
        try { return TileMapValidator.LoadDatabase(new ProjectPaths()); }
        catch (Exception e) when (e is IOException or InvalidDataException) { return null; }
    });

    private static uint Rgb(string set) => Db.Value!.TileSet(set)!.Rgb;

    /// <summary>The database has these tile sets (3K's roads_tracks, blockout_cliff, generic_sea: not in WH3's database,
    /// whose tile rules are re-measured in Phase 3.3).</summary>
    private static bool Has(params string[] sets) => Db.Value is { } db && sets.All(s => db.TileSet(s) is not null);

    /// <summary>A 24 x 24 map of plain land (generic) with the given hexes painted.</summary>
    private static HexTileMap Map(params (int Col, int Row, string Set)[] hexes)
    {
        var map = HexTileMap.Create(24, 24);
        for (var r = 0; r < map.Height; r++)
            for (var c = 0; c < map.Width; c++)
                map.SetHex(c, r, Rgb("generic"));
        foreach (var (c, r, set) in hexes) map.SetHex(c, r, set == "black" ? 0 : Rgb(set));
        return map;
    }

    private static void Apply(HexTileMap map, JsonArray ops)
    {
        var tileOps = new TileMapOps(map, Db.Value!);
        foreach (var op in ops) tileOps.Apply((JsonObject)op!);
    }

    private static TileError At(IReadOnlyList<TileError> errors, string code, int c, int r) =>
        errors.Single(e => e.Code == code && e.Col == c && e.Row == r);

    [Fact]
    public void BlackHexIsRepaintedAsTheSurroundingLand()
    {
        if (Db.Value is null) return;
        var map = Map((10, 10, "black"));
        var e = At(TileErrors.Find(map, Db.Value), "palette.black", 10, 10);
        Assert.NotNull(e.Fix);
        Assert.Equal(Rgb("generic"), e.Fix!.Preview.Single().Rgb);
        Apply(map, e.Fix.Ops);
        Assert.DoesNotContain(TileErrors.Find(map, Db.Value, suggest: false), x => x.Code == "palette.black");
    }

    [Fact]
    public void LoneRiverHexBecomesLand()
    {
        if (Db.Value is null) return;
        var map = Map((10, 10, "river"));
        var e = At(TileErrors.Find(map, Db.Value), "river.ends", 10, 10);
        Assert.NotNull(e.Fix);
        Apply(map, e.Fix!.Ops);
        Assert.DoesNotContain(TileErrors.Find(map, Db.Value, suggest: false), x => x.Code == "river.ends");
    }

    [Fact]
    public void ThickRoadIsThinnedWithoutNewErrors()
    {
        if (!Has("roads_tracks")) return;
        // a road blob: the centre and its whole ring
        var hexes = new List<(int, int, string)> { (10, 10, "roads_tracks") };
        var probe = HexTileMap.Create(24, 24);
        for (var d = 0; d < 6; d++)
            if (probe.Neighbour(10, 10, d, out var nc, out var nr)) hexes.Add((nc, nr, "roads_tracks"));
        var map = Map([.. hexes]);
        var before = TileErrors.Find(map, Db.Value!);
        Assert.Contains(before, e => e.Code == "line.thick");
        var all = TileErrors.FixAll(map, Db.Value!, before.Where(e => e.Code == "line.thick"), climate: null, verifiedOnly: false);
        Assert.True(all.Fixed > 0);
        Apply(map, all.Ops);
        var after = TileErrors.Find(map, Db.Value!, suggest: false);
        Assert.True(after.Count(e => e.Code == "line.thick") < before.Count(e => e.Code == "line.thick"));
        Assert.True(after.Count(e => e.Severity == TileMapFinding.Error) <= before.Count(e => e.Severity == TileMapFinding.Error));
    }

    [Fact]
    public void CliffTouchingCoastIsSuggestedAsCliffEnd()
    {
        if (!Has("blockout_cliff", "blockout_cliff_ends", "generic_sea")) return;
        var map = Map((10, 10, "blockout_cliff"), (10, 11, "sea_coast"), (10, 12, "generic_sea"));
        var e = At(TileErrors.Find(map, Db.Value!), "coast.cliff_end", 10, 10);
        Assert.NotNull(e.Fix);
        Assert.Contains(e.Fix!.Preview, h => h.Col == 10 && h.Row == 10 && h.Rgb == Rgb("blockout_cliff_ends"));
    }

    [Fact]
    public void FixAllLowersTheErrorCountAndLeavesTheInputAlone()
    {
        if (Db.Value is null) return;
        var map = Map((4, 4, "black"), (12, 12, "river"), (18, 6, "black"));
        var snapshot = (uint[])map.Pixels.Clone();
        var before = TileErrors.Find(map, Db.Value);
        var all = TileErrors.FixAll(map, Db.Value, before, climate: null, verifiedOnly: false);
        Assert.Equal(snapshot, map.Pixels);                   // FixAll works on a copy
        Apply(map, all.Ops);
        Assert.True(TileErrors.Find(map, Db.Value, suggest: false).Count < before.Count);
        Assert.Equal(0, TileErrors.FixAll(map, Db.Value, TileErrors.Find(map, Db.Value), climate: null, verifiedOnly: false).Fixed);   // nothing left to fix
    }

    [Fact]
    public void HoleCheckedFixIsVerified()
    {
        if (Db.Value is null) return;
        var map = Map((10, 10, "black"));
        var climate = new byte[map.Pixels.Length];          // one climate everywhere
        var e = At(TileErrors.Find(map, Db.Value, suggest: false), "palette.black", 10, 10);
        var fix = TileErrors.Suggest(map, Db.Value, e, climate: climate);
        Assert.NotNull(fix);
        Assert.True(fix!.Verified);
        Assert.True(fix.HolesAfter <= fix.HolesBefore);
        // without a climate the same fix is rules-only
        Assert.False(TileErrors.Suggest(map, Db.Value, e)!.Verified);
    }

    [Fact]
    public void DatabaseLoadsWhenTheGameIsInstalled()
    {
        // the tests above return early without a tile database; make sure that only happens without the game
        if (Directory.Exists(new ProjectPaths().GameDataDir)) Assert.NotNull(Db.Value);
    }

    [Fact]
    public void WholeMapCodesHaveAdvice()
    {
        foreach (var code in new[] { "layout.mesh_columns", "climate.size", "inputs.extents", "pattern.unseen", TileErrors.HoleCode })
            Assert.False(string.IsNullOrEmpty(TileErrors.AdviceFor(code)));
    }
}
