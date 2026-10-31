using AtlasWH3.Formats.Db;
using AtlasWH3.Formats.Packs;

namespace AtlasWH3.Tests;

/// <summary>DB tables decoded by RPFM's server, as the build reads them.</summary>
public class DbTableTests
{
    private static PackSet? Vanilla() => Directory.Exists(TestKits.Wh3GameData) ? PackSet.OpenVanilla(TestKits.Wh3GameData) : null;

    [Fact]
    public void Wh3_ValuesHaveTheirColumnTypes()
    {
        if (Vanilla() is not { } packs) return;
        var (ids, grass) = DbTables.PackRows(packs, "campaign_tree_ids_tables").First(r => (string?)r.Table.Get(r.Row, "tree_id") == "grass_01");
        Assert.IsType<bool>(ids.Get(grass, "can_be_removed"));
        Assert.Equal(0x285064u, ids.Colour(grass));

        var (variables, row) = DbTables.PackRows(packs, "campaign_variables_tables").First();
        Assert.IsType<string>(variables.Get(row, "variable_key"));
        Assert.IsType<float>(variables.Get(row, "value"));
    }

    /// <summary>regions_tables' r, g, b come back as one colour column, which <see cref="DbTables.Table.Colour"/> reads.</summary>
    [Fact]
    public void Wh3_RegionColours()
    {
        if (Vanilla() is not { } packs) return;
        var (t, row) = DbTables.PackRows(packs, "regions_tables").First(r => (string?)r.Table.Get(r.Row, "key") == "wh3_dlc20_chaos_region_black_pit");
        Assert.Equal(0x896406u, t.Colour(row));
        Assert.Single(t.Columns, c => c.Type == DbTables.FieldType.ColourRGB);
    }

    /// <summary>A higher-priority pack's file shadows a lower one's with the same name; other files are all read.</summary>
    [Fact]
    public void Wh3_ModTablesComeFirst()
    {
        if (!File.Exists(TestKits.Pack(TestKits.IeePack)) || Vanilla() is not { } vanilla) return;
        var packs = new PackSet([PackFile.Open(TestKits.Pack(TestKits.IeePack)), .. vanilla.Packs]);
        var keys = DbTables.PackRows(packs, "campaign_tree_ids_tables").Select(r => (string)r.Table.Get(r.Row, "tree_id")!).ToList();
        Assert.Contains("tree_large_khuresh_01", keys);
        Assert.Contains("grass_01", keys);
        Assert.True(keys.IndexOf("tree_large_khuresh_01") < keys.IndexOf("grass_01"));
    }
}
