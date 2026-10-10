using AtlasWH3.Core;
using AtlasWH3.Core.Campaign;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Models;

namespace AtlasWH3.Tests;

/// <summary>Native replacements for BOB's campaign actions and their file formats, against BOB's output for the fixture
/// maps.</summary>
public class CampaignBuildTests
{
    /// <summary>BOB's river models (IEE's working_data models\river_*.rigid_model_v2) read and write back byte for byte.</summary>
    [Fact]
    public void RigidModelV2_BobRiverModels_RoundTripByteIdentical()
    {
        var dir = TestKits.Built(TestKits.Iee, "models");
        if (!Directory.Exists(dir)) return;
        var files = Directory.GetFiles(dir, "*.rigid_model_v2");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var original = File.ReadAllBytes(file);
            Assert.True(original.AsSpan().SequenceEqual(RigidModelV2.Read(original).ToBytes()), Path.GetFileName(file));
        }
    }

    [Fact]
    public void LookupTexture_Iee_MatchesBobTgaAndDds()
    {
        const string map = "cr_combi_expanded_map_1";
        var bmp = Path.Combine(TestKits.Kit(map), "raw_data", "EmpireDesignData", "campaign_maps", map, "cr_combi_expanded_lookup.bmp");
        var stem = Path.Combine(TestKits.Kit(map), "working_data", "campaign_maps", map, "cr_combi_expanded_lookup");
        if (!File.Exists(bmp) || !File.Exists(stem + ".tga")) return;
        var lookup = LookupTexture.FromBmp(bmp);
        Assert.Equal(File.ReadAllBytes(stem + ".tga"), lookup.ToTga());
        Assert.Equal(File.ReadAllBytes(stem + ".dds"), lookup.ToDds());
        // BOB's minimap samples up to one source pixel off indices[::4, ::4] along region borders (1,545 of 388,000)
        var bob = File.ReadAllBytes(stem + "_minimap.tga");
        var ours = lookup.Minimap().ToTga();
        Assert.Equal(bob.Length, ours.Length);
        Assert.InRange(bob.Where((b, i) => b != ours[i]).Count(), 0, 2500);
    }

    [Fact]
    public void LookupTexture_MoveToEnd_KeepsEveryPixelsColour()
    {
        var bmp = Path.Combine(Path.GetTempPath(), $"atlaswh3_lookup_{Guid.NewGuid():N}.bmp");
        // 4 × 1, 24-bit bottom-up: red, green, red, blue
        byte[] pixels = [0, 0, 255, 0, 255, 0, 0, 0, 255, 255, 0, 0];
        var header = new byte[54];
        "BM"u8.CopyTo(header);
        BitConverter.GetBytes(54 + pixels.Length).CopyTo(header, 2);
        BitConverter.GetBytes(54).CopyTo(header, 10);
        BitConverter.GetBytes(40).CopyTo(header, 14);
        BitConverter.GetBytes(4).CopyTo(header, 18);
        BitConverter.GetBytes(1).CopyTo(header, 22);
        BitConverter.GetBytes((short)1).CopyTo(header, 26);
        BitConverter.GetBytes((short)24).CopyTo(header, 28);
        File.WriteAllBytes(bmp, [.. header, .. pixels]);
        try
        {
            var lookup = LookupTexture.FromBmp(bmp);
            var moved = lookup.MoveToEnd(new HashSet<uint> { 0xFF0000 });
            Assert.Equal([0x00FF00u, 0x0000FF, 0xFF0000], moved.Palette);
            Assert.Equal([2, 0, 2, 1], moved.Indices.Select(i => (int)i));
        }
        finally { File.Delete(bmp); }
    }

    /// <summary>The user's lookup_tweak.py list as region keys (research/lookup) reproduces the shipped Old World lookup:
    /// .tga, .dds and _minimap.tga byte for byte.</summary>
    [Fact]
    public void LookupStep_OldWorldRegionList_MatchesShippedPack()
    {
        const string map = "cr_oldworld_map_1";
        var pack = TestKits.Pack(TestKits.OldWorldPack);
        var list = Path.Combine(RepoRoot(), "research", "lookup", "oldworld_lookup_last.txt");
        var paths = new ProjectPaths { MapName = map, AssemblyKitRoot = TestKits.Kit(map), GameDataDir = TestKits.Wh3GameData, ModPacks = [pack] };
        if (!File.Exists(pack) || !File.Exists(list) || !File.Exists(Path.Combine(paths.AkDesignCampaignMapDir, "cr_oldworld_lookup.bmp"))) return;
        var outDir = Path.Combine(Path.GetTempPath(), $"atlaswh3_lookup_{Guid.NewGuid():N}");
        try
        {
            var ctx = new CampaignBuildContext(paths, outDir) { LookupLastRegions = [list] };
            var result = new LookupStep().Run(ctx);
            Assert.DoesNotContain(result.Notes, n => n.StartsWith("lookup region list"));
            Assert.Contains("cr_oldworld_lookup.bmp: 302 of 1327 palette entries moved to the end (the region list, and black)", result.Notes);
            Assert.DoesNotContain(result.Notes, n => n.StartsWith("elector_counts_small") || n.StartsWith("wh3_main_hef_court_small"));
            var shipped = Formats.Packs.PackFile.Open(pack);
            byte[] Shipped(string file) => shipped.TryRead(Formats.Packs.PackFile.Normalize($"campaign_maps/{map}/{file}"))!;
            byte[] Ours(string file) => File.ReadAllBytes(Path.Combine(ctx.CampaignMapOutDir, file));
            Assert.Equal(Shipped("cr_oldworld_lookup.tga"), Ours("cr_oldworld_lookup.tga"));
            Assert.Equal(Shipped("cr_oldworld_lookup.dds"), Ours("cr_oldworld_lookup.dds"));
            Assert.Equal(Shipped("cr_oldworld_lookup_minimap.tga"), Ours("cr_oldworld_lookup_minimap.tga"));
        }
        finally { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); }
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "AtlasWH3.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new DirectoryNotFoundException("AtlasWH3.slnx not found above the test binaries");
    }

    [Fact]
    public void PropTransform_Matrix_InvertsFromColumns()
    {
        var rng = new Random(3);
        for (var n = 0; n < 200; n++)
        {
            // random rotation (Rz * Ry * Rx) with scale
            double ax = rng.NextDouble() * 6 - 3, ay = rng.NextDouble() * 3 - 1.5, az = rng.NextDouble() * 6 - 3;
            double[] scale = [0.2 + rng.NextDouble() * 5, 0.2 + rng.NextDouble() * 5, 0.2 + rng.NextDouble() * 5];
            var expected = new Formats.Props.PropTransform(0, 0, 0,
                ax * 180 / Math.PI, ay * 180 / Math.PI, az * 180 / Math.PI, scale[0], scale[1], scale[2]).Matrix();
            // file layout: column i = (m[0][i], m[1][i], m[2][i])
            var columns = new float[9];
            for (var c = 0; c < 3; c++)
                for (var r = 0; r < 3; r++)
                    columns[c * 3 + r] = (float)expected[r * 3 + c];
            var roundTrip = Formats.Props.PropTransform.FromColumns(columns, 0, 0, 0).Matrix();
            for (var i = 0; i < 9; i++) Assert.Equal(expected[i], roundTrip[i], 3);
        }
    }

    [Fact]
    public void Png16_WritesHeader_TextChunk_AndBigEndianSamples()
    {
        var raster = new Raster<ushort>(3, 2, [0, 1, 0x1234, 65535, 7, 8]);
        var png = Png16.Encode(raster, new Dictionary<string, string> { ["height_scale"] = "0.000382" });
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png[..4]);
        Assert.Equal(16, png[24]);            // IHDR bit depth
        Assert.Equal(0, png[25]);             // greyscale
        Assert.Contains("height_scale\0" + "0.000382", System.Text.Encoding.Latin1.GetString(png));
        var idat = png.AsSpan().IndexOf("IDAT"u8);
        var len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(idat - 4));
        using var z = new System.IO.Compression.ZLibStream(new MemoryStream(png, idat + 4, len), System.IO.Compression.CompressionMode.Decompress);
        var rows = new byte[2 * (1 + 6)];
        z.ReadExactly(rows);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 1, 0x12, 0x34, 0, 0xFF, 0xFF, 0, 7, 0, 8 }, rows);
    }

    /// <summary>The global_props.bin container (the BMD v27 bodies are GlobalPropsTests') splits and packs back byte for byte.</summary>
    [Fact]
    public void GlobalProps_Container_RoundTripsByteIdentical()
    {
        var path = TestKits.Built(TestKits.Iee, "global_props.bin");
        if (!File.Exists(path)) return;
        var original = File.ReadAllBytes(path);
        var props = Formats.Props.GlobalProps.Read(original);
        Assert.True(original.AsSpan().SequenceEqual(Formats.Props.GlobalProps.Pack(props.Bodies())));
    }

    [Fact]
    public void Pipeline_OnlyNativeStepsRunByDefault()
    {
        Assert.DoesNotContain(CampaignBuildPipeline.NativeSteps, s => s is PendingStep);
        Assert.Contains(CampaignBuildPipeline.NativeSteps, s => s.Name == "lookup");
        // dependencies name real steps and come earlier (the pipeline creates tasks in this order)
        var seen = new HashSet<string>();
        foreach (var step in CampaignBuildPipeline.AllSteps)
        {
            Assert.All(step.DependsOn, d => Assert.Contains(d, seen));
            seen.Add(step.Name);
        }
    }

    /// <summary>WH3's tile hf maps (compressed_map version 3, in the vanilla packs) decode. (The encoder does not
    /// reproduce CA's v3 bytes; nothing writes hf maps.)</summary>
    [Fact]
    public void CompressedMap_TileHfMapsDecode()
    {
        if (!Directory.Exists(TestKits.Wh3GameData)) return;
        var packs = AtlasWH3.Formats.Packs.PackSet.OpenVanilla(TestKits.Wh3GameData);
        var keys = packs.Packs.SelectMany(p => p.Entries.Keys).Where(k => k.EndsWith("hf_height_map.compressed_map", StringComparison.Ordinal)).Distinct().ToList();
        Assert.Equal(135, keys.Count);
        var varied = 0;
        foreach (var key in keys)
        {
            var map = CompressedMap.Decode(packs.TryRead(key)!);
            Assert.Equal(3, map.Version);
            Assert.True(map.Raster.Width > 1 && map.Raster.Height > 1, key);   // 9 x 9 up to 513 x 257, by tile
            Assert.True(map.Header[1] <= map.Header[4], key);
            if (map.Raster.Data.Any(v => v != map.Raster.Data[0])) varied++;
        }
        Assert.Equal(104, varied);   // the others are flat
    }
}
