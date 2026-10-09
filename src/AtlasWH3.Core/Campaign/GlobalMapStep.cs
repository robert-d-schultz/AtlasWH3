using System.Diagnostics;
using AtlasWH3.Core.Campaign.TileMapCheck;
using AtlasWH3.Formats.Db;
using AtlasWH3.Formats.Dds;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Packs;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Campaign;

/// <summary>
/// terrain\campaigns\&lt;map&gt;\global_map\ (BOB "Global Tilemap" and "Campaign Global Blendmap"). Byte-identical to BOB's
/// on Old World; on IEE too, but for blend pixels and tile heights edited after its BOB run:
///  - global_blend.dds: the composited BlendCampaign map as is (not flipped), 255 (empty) written as 0, 8-bit luminance
///    with every optional header field 0 (<see cref="DdsHeader.BuildGlobalBlend"/>);
///  - texture_arrays.xml: the merged asset variation db's campaign texture groups (<see cref="TextureArrays.FromAssetDbs"/>),
///    from the game packs and the linked mod packs: a mod can add groups, which shifts the index of every later one.
///    Terry paints with the same list (its palette is the groups' display colours), so a blend whose palette disagrees
///    was painted with other packs loaded and is reported;
///  - tile_list.bin: the root tile list cut to the tiles of exclude_from_global_mesh sets (<see cref="TileList.GlobalMapSubset"/>).
/// </summary>
public sealed class GlobalMapStep : ICampaignBuildStep
{
    public string Name => "global_map";
    public string ReplacesBobAction => "Global Tilemap, Campaign Global Blendmap";
    public IReadOnlyList<string> DependsOn => ["tile_list"];

    public IReadOnlyList<string> CheckInputs(CampaignBuildContext ctx)
    {
        var missing = new List<string>();
        if (!File.Exists(ctx.TerryFile)) missing.Add($"missing {ctx.TerryFile}");
        else if (TerryProject.Load(ctx.TerryFile).Find("BlendCampaign") is not { } map) missing.Add("no BlendCampaign map in the .terry");
        else
            try { TerrainComposite.Inputs(TerryProject.Load(ctx.TerryFile), map); }
            catch (FileNotFoundException e) { missing.Add(e.Message); }
        if (!Directory.Exists(ctx.Paths.GameDataDir)) missing.Add($"no game data folder {ctx.Paths.GameDataDir} (texture groups and tile sets come from its packs)");
        return missing;
    }

    /// <summary>The root tile_list.bin: this build's, else the kit's working_data copy.</summary>
    private static string? RootTileList(CampaignBuildContext ctx) =>
        new[] { ctx.OutFile("tile_list.bin"), Path.Combine(ctx.Paths.AkWorkingDir, "terrain", "campaigns", ctx.MapName, "tile_list.bin") }
            .FirstOrDefault(File.Exists);

    public StepResult Run(CampaignBuildContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var written = new List<string>();
        var notes = new List<string>();
        var outDir = ctx.OutFile("global_map");
        Directory.CreateDirectory(outDir);

        ctx.Log("texture groups...");
        var packs = GameSetup.OpenWithLinked(ctx.Paths.GameDataDir, ctx.Paths.ModPacks);
        var arrays = TextureArrays.FromAssetDbs(AssetDbs(packs, notes), out var problems);
        notes.AddRange(problems);
        if (arrays.Groups.Count == 0) throw new InvalidDataException("no campaign_base_colour texture groups in the packs' asset variation dbs");
        var arraysPath = Path.Combine(outDir, "texture_arrays.xml");
        File.WriteAllBytes(arraysPath, arrays.ToXml());
        written.Add(arraysPath);
        notes.Add($"{arrays.Groups.Count} texture groups");

        ctx.Log("global_blend.dds...");
        var (blend, palette) = TerrainComposite.Indexed(TerryProject.Load(ctx.TerryFile), "BlendCampaign");
        ctx.Cancel.ThrowIfCancellationRequested();
        notes.AddRange(CheckBlend(blend, palette, arrays));
        var header = DdsHeader.BuildGlobalBlend(blend.Width, blend.Height);
        var blendPath = Path.Combine(outDir, "global_blend.dds");
        using (var fs = File.Create(blendPath))
        {
            fs.Write(header);
            var data = blend.Data;
            for (var i = 0; i < data.Length; i++) if (data[i] == 255) data[i] = 0;
            fs.Write(data);
        }
        written.Add(blendPath);

        if (RootTileList(ctx) is { } root)
        {
            ctx.Log("global_map\\tile_list.bin...");
            var db = TileMapValidator.LoadDatabase(ctx.Paths);
            var excluded = db.Tiles.Where(t => db.TileSet(t.TileSet) is { ExcludeFromGlobalMesh: true })
                .Select(t => t.Variations[0].Location).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var subset = TileList.Read(root).GlobalMapSubset(excluded.Contains);
            var listPath = Path.Combine(outDir, "tile_list.bin");
            subset.Write(listPath);
            written.Add(listPath);
            notes.Add($"tile_list.bin: {subset.Records.Count} records (exclude_from_global_mesh tiles) from {root}");
        }
        else notes.Add("no root tile_list.bin (this build or working_data); global_map\\tile_list.bin not written");
        return new StepResult(Name, written, notes, sw.Elapsed);
    }

    /// <summary>Every warscape_asset_variation_db\*.assetdb the packs hold, each from its highest-priority pack, vanilla
    /// packs first, then by path. The merge order only matters for a key two files define (its first variation wins);
    /// BOB's order there is not verified, so such keys are noted.</summary>
    private static IEnumerable<AssetVariationDb> AssetDbs(PackSet packs, List<string> notes)
    {
        var prefix = PackFile.Normalize(AssetVariationDb.Folder + "/");
        var owner = new Dictionary<string, int>(StringComparer.Ordinal);   // path → index of its highest-priority pack
        for (var i = packs.Packs.Count - 1; i >= 0; i--)
            foreach (var k in packs.Packs[i].Entries.Keys)
                if (k.StartsWith(prefix, StringComparison.Ordinal) && k.EndsWith(".assetdb", StringComparison.Ordinal)) owner[k] = i;
        var seen = new Dictionary<(string, string), string>();
        foreach (var (path, i) in owner.OrderByDescending(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal))
        {
            var db = AssetVariationDb.Read(packs.Packs[i].TryRead(path)!);
            foreach (var e in db.Entries.Where(e => e.Namespace == TextureArrays.BaseColourNamespace))
                if (!seen.TryAdd((e.Namespace, e.Key), path))
                    notes.Add($"texture group {e.Key} is in {seen[(e.Namespace, e.Key)]} and {path}; the first is used");
            yield return db;
        }
    }

    /// <summary>Blend indices with no group, and used indices whose palette colour is not their group's display colour
    /// (the blend was painted with another group list: other mod packs loaded in Terry than linked here).</summary>
    private static IEnumerable<string> CheckBlend(Raster<byte> blend, TiffMap.Palette? palette, TextureArrays arrays)
    {
        var counts = new long[256];
        foreach (var v in blend.Data) counts[v]++;
        var beyond = Enumerable.Range(arrays.Groups.Count, 255 - arrays.Groups.Count).Where(i => counts[i] > 0).ToList();
        if (beyond.Count > 0)
            yield return $"global_blend: values {string.Join(", ", beyond)} have no texture group ({arrays.Groups.Count} groups); " +
                         "link the mod packs Terry had loaded";
        if (palette is null) yield break;
        var wrong = arrays.Groups.Where(g => counts[g.Index] > 0 && g.Colour is { } c &&
                                             (uint)((palette.R[g.Index] >> 8) << 16 | (palette.G[g.Index] >> 8) << 8 | palette.B[g.Index] >> 8) != c)
            .Select(g => g.Name).ToList();
        if (wrong.Count > 0)
            yield return $"global_blend: the blend palette disagrees with the texture groups at {wrong.Count} used values " +
                         $"(first: {string.Join(", ", wrong.Take(5))}); it was painted with another group list, so link the " +
                         "mod packs Terry had loaded";
    }
}
