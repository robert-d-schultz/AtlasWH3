using AtlasWH3.Core;
using AtlasWH3.Core.Campaign;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Models;

namespace AtlasWH3.Tests;

/// <summary>Native replacements for BOB's campaign actions, byte-compared against vanilla 3k_dlc07_main_map.</summary>
public class CampaignBuildTests
{
    private static readonly ProjectPaths Paths = TestKits.VanillaPaths;
    private static string Vanilla(params string[] relative) => Path.Combine([Paths.TerrainDir, .. relative]);

    [Fact]
    public void RigidModelV2_Vanilla_AllTerrainMeshes_RoundTripByteIdentical()
    {
        if (!Directory.Exists(Vanilla("global_meshes"))) return;
        var files = Directory.GetFiles(Vanilla("global_meshes"), "*.rigid_model_v2")
            .Concat(Directory.GetFiles(Vanilla("models"), "*.rigid_model_v2"));
        foreach (var file in files)
        {
            var original = File.ReadAllBytes(file);
            Assert.Equal(original, RigidModelV2.Read(original).ToBytes());
        }
    }

    [Fact]
    public void RigidModelV2_NewTerrainTile_HeadersMatchVanilla()
    {
        if (!Directory.Exists(Vanilla("global_meshes"))) return;
        foreach (var (pattern, sea) in new[] { ("land_mesh_*.rigid_model_v2", false), ("sea_mesh_*.rigid_model_v2", true) })
        foreach (var file in Directory.GetFiles(Vanilla("global_meshes"), pattern).Take(20))
        {
            var original = File.ReadAllBytes(file);
            var read = RigidModelV2.Read(original);
            var fresh = RigidModelV2.NewTerrainTile(sea);
            fresh.Vertices = read.Vertices;
            fresh.Indices = read.Indices;
            fresh.SetTileBounds(read.Bounds[0], read.Bounds[2], read.Bounds[3], read.Bounds[5]);
            Assert.Equal(original, fresh.ToBytes());
        }
    }

    [Fact]
    public void TileList_Vanilla_RoundTrips()
    {
        if (!File.Exists(Vanilla("tile_list.bin"))) return;
        var root = File.ReadAllBytes(Vanilla("tile_list.bin"));
        Assert.Equal(root, TileList.Read(root).ToBytes());
    }

    [Fact]
    public void HeightPatchCollection_Vanilla_RoundTrips()
    {
        var path = Vanilla("height_patches", "rivers.height_patch_collection");
        if (!File.Exists(path)) return;
        var original = File.ReadAllBytes(path);
        Assert.Equal(original, HeightPatchCollection.Read(original).ToBytes());
    }

    [Fact]
    public void LookupTexture_VanillaBmp_MatchesBobTgaAndDds()
    {
        var bmp = Path.Combine(Paths.AkDesignCampaignMapDir, "3k_main_lookup.bmp");
        var working = Paths.AkWorkingCampaignMapDir;
        if (!File.Exists(bmp) || !File.Exists(Path.Combine(working, "3k_main_lookup.tga"))) return;
        var lookup = LookupTexture.FromBmp(bmp);
        Assert.Equal(File.ReadAllBytes(Path.Combine(working, "3k_main_lookup.tga")), lookup.ToTga());
        Assert.Equal(File.ReadAllBytes(Path.Combine(working, "3k_main_lookup.dds")), lookup.ToDds());
    }

    [Fact]
    public void LookupTexture_GuanduBobSet_MatchesIncludingMinimap()
    {
        var dir = @"Z:\Claude\TerryClone\research\guandu\pack_mapcheck_212629\campaign_maps\3k_guandu_map";
        var stem = Path.Combine(dir, "3k_guandu_start_pos_lookup");
        if (!File.Exists(stem + ".bmp")) return;
        var lookup = LookupTexture.FromBmp(stem + ".bmp");
        Assert.Equal(File.ReadAllBytes(stem + ".tga"), lookup.ToTga());
        Assert.Equal(File.ReadAllBytes(stem + ".dds"), lookup.ToDds());
        Assert.Equal(File.ReadAllBytes(stem + "_minimap.tga"), lookup.Minimap().ToTga());
    }

    [Fact]
    public void LookupTexture_Iee_MatchesBobTgaAndDds()
    {
        const string map = "cr_combi_expanded_map_1";
        var bmp = Path.Combine(TestKits.Wh3Kit, "raw_data", "EmpireDesignData", "campaign_maps", map, "cr_combi_expanded_lookup.bmp");
        var stem = Path.Combine(TestKits.Wh3Kit, "working_data", "campaign_maps", map, "cr_combi_expanded_lookup");
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
        var pack = Path.Combine(TestKits.Wh3GameData, "!cr_oldworld_campaign.pack");
        var list = Path.Combine(RepoRoot(), "research", "lookup", "oldworld_lookup_last.txt");
        var paths = new ProjectPaths { MapName = map, AssemblyKitRoot = TestKits.Wh3Kit, GameDataDir = TestKits.Wh3GameData, ModPacks = [pack] };
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

    [Fact]
    public void GlobalProps_Container_RoundTripsByteIdentical()
    {
        if (!File.Exists(Paths.GlobalPropsBin)) return;
        var original = File.ReadAllBytes(Paths.GlobalPropsBin);
        var props = Formats.Props.GlobalProps.Read(original);
        Assert.Equal(original, Formats.Props.GlobalProps.Pack(props.Bodies()));
    }

    [Fact]
    public void TileDatabase_ParsesVanillaTile()
    {
        var file = Path.Combine(Path.GetDirectoryName(Paths.VanillaRoot)!, "terrain", "tiles", "campaign", "_tile_database", "tiles", "mountains_cold_11x11_diamond.bin");
        if (!File.Exists(file)) return;
        var tile = TileDatabase.Parse(File.ReadAllBytes(file));
        Assert.Equal("mountains_cold", tile.Category);
        Assert.Equal((11, 11), (tile.Width, tile.Height));
        Assert.Equal(121, tile.Mask.Length);
        Assert.StartsWith(@"terrain\tiles\campaign\mountains_c", tile.Path);
        Assert.False(tile.SubtileValid(0, 0));   // diamond corner
        Assert.True(tile.SubtileValid(5, 5));    // centre
    }

    [Fact]
    public void LfSampler_ReproducesVanillaLandMeshHeights()
    {
        var mesh = Vanilla("global_meshes", "land_mesh_40.rigid_model_v2");
        if (!File.Exists(mesh)) return;
        var model = RigidModelV2.Read(File.ReadAllBytes(mesh));
        var lf = CompressedMap.Read(Vanilla("lf_height_map.compressed_map"));
        const float tile = 595.1f / 1784f;
        var sampler = new Core.Campaign.Terrain.LfSampler(lf, 1784 * tile, 1405 * tile, tile);
        var errors = new List<float>();
        for (var v = 0; v < model.VertexCount; v += 3)
        {
            var x = BitConverter.ToSingle(model.Vertices, v * 16);
            var y = BitConverter.ToSingle(model.Vertices, v * 16 + 4);
            var z = BitConverter.ToSingle(model.Vertices, v * 16 + 8);
            errors.Add(MathF.Abs(sampler.Height(x, z) - y));
        }
        errors.Sort();
        // within ~20 ulps (not yet bit-exact, see docs/bob_re_global_mesh.md); skirts (y - 1) are the tail
        Assert.True(errors[errors.Count / 2] < 1e-4f, $"median {errors[errors.Count / 2]}");
    }

    [Fact]
    public void Rivers_BuildFromSplineLayer()
    {
        var layer = Path.Combine(Paths.AkTerrainDir, Paths.MapName + ".1972bd217a4938e.layer");
        if (!File.Exists(layer)) return;
        var rivers = Core.Campaign.Rivers.RiverBuilder.ReadLayer(layer);
        Assert.Equal(24, rivers.Count);
        var river = rivers.Single(r => r.Number == 0);
        var sections = Core.Campaign.Rivers.RiverBuilder.Sample(river, null);
        Assert.True(sections.Count > 10);
        var model = Core.Campaign.Rivers.RiverBuilder.BuildModel(sections, 595.1f, 541.78619f);
        Assert.Equal(sections.Count * 5, model.VertexCount);
        Assert.Equal(48, model.VertexStride);
        Assert.Equal(68, model.Material);
        // round trip through the RMV2 reader
        var back = RigidModelV2.Read(model.ToBytes());
        Assert.Equal(model.Indices, back.Indices);
        // vanilla river_0 lies around (115, 410)
        Assert.InRange((model.Bounds[0] + model.Bounds[3]) / 2, 105, 125);
        Assert.InRange((model.Bounds[2] + model.Bounds[5]) / 2, 400, 420);
    }

    [Fact]
    public void BmdBody_Vanilla_AllBodiesRoundTripByteIdentical()
    {
        if (!File.Exists(Paths.GlobalPropsBin)) return;
        var props = Formats.Props.GlobalProps.Load(Paths.GlobalPropsBin);
        var count = 0;
        foreach (var (name, body) in props.Bodies())
        {
            var parsed = Formats.Props.BmdBody.Parse(body);
            Assert.True(body.AsSpan().SequenceEqual(parsed.ToBytes()), name);
            count++;
        }
        Assert.Equal(8891, count);
    }

    [Fact]
    public void BmdRecords_Prop_RebuildsVanillaRecordsFromTheirOwnFields()
    {
        if (!File.Exists(Paths.GlobalPropsBin)) return;
        var gp = Formats.Props.GlobalProps.Load(Paths.GlobalPropsBin);
        var checkedCount = 0;
        foreach (var (_, bytes) in gp.Bodies().Take(400))
            foreach (var rec in Formats.Props.BmdBody.Parse(bytes).Props)
            {
                var m = new double[9];
                for (var c = 0; c < 3; c++)
                    for (var r = 0; r < 3; r++)
                        m[r * 3 + c] = BitConverter.ToSingle(rec, 24 + (c * 3 + r) * 4);
                var pos = (BitConverter.ToSingle(rec, 60), BitConverter.ToSingle(rec, 64), BitConverter.ToSingle(rec, 68));
                var strLen = BitConverter.ToUInt16(rec, Formats.Props.BmdRecords.PropHeadSize);
                var tail = Formats.Props.BmdRecords.PropHeadSize + 2 + strLen;
                var rebuilt = Formats.Props.BmdRecords.Prop(rec, BitConverter.ToUInt32(rec, 2), BitConverter.ToUInt64(rec, 8), BitConverter.ToUInt64(rec, 16),
                    m, pos, rec[72] == 1, rec[75] == 1, rec[76] == 1, rec[77] == 1, rec[78] == 1, BitConverter.ToUInt32(rec, 96),
                    rec[100] == 1, rec[101] == 1, rec[tail + 4] == 1, rec[tail + 5] == 1, rec[tail + 23] == 1);
                Assert.Equal(rec, rebuilt);
                checkedCount++;
            }
        Assert.True(checkedCount > 1000);
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

    [Fact]
    public void CompressedMap_Version2_HfMapsDecode()
    {
        // version 2: lo/hi only (3 river and roads_tracks junction hf maps in terrain2.pack), kept at header[1] / [4]
        if (!Directory.Exists(Paths.GameDataDir)) return;
        var packs = AtlasWH3.Formats.Packs.PackSet.OpenVanilla(Paths.GameDataDir);
        var bytes = packs.TryRead("terrain/tiles/campaign/river/junction6_c/hf_height_map.compressed_map");
        if (bytes is null) return;
        var map = CompressedMap.Decode(bytes);
        Assert.Equal(2, map.Version);
        Assert.Equal(513, map.Raster.Width);
        Assert.True(map.Header[1] < 0f && map.Header[4] > 0f, $"lo {map.Header[1]} hi {map.Header[4]}");
        Assert.Contains(map.Raster.Data, v => v != map.Raster.Data[0]);
    }

    [Fact]
    public void TileHfHeight_VanillaTrees_MatchShippedHeights()
    {
        if (!File.Exists(Vanilla("tile_list.bin")) || !File.Exists(Paths.TreeList) || !Directory.Exists(Paths.GameDataDir)) return;
        var packs = AtlasWH3.Formats.Packs.PackSet.OpenVanilla(Paths.GameDataDir);
        var prefix = AtlasWH3.Formats.Packs.PackFile.Normalize(TileDatabase.Folder);
        var db = TileDatabase.Load(packs.Packs.SelectMany(p => p.Entries.Keys).Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Distinct().Select(k => packs.TryRead(k)).OfType<byte[]>());
        var terrain = new AtlasWH3.Core.Campaign.Terrain.TileHfHeight(TileList.Read(Vanilla("tile_list.bin")), db, packs.TryRead,
            CompressedMap.Read(Vanilla("lf_height_map.compressed_map")), AtlasWH3.Core.Campaign.Terrain.TileHfHeight.TileSize3K);
        var trees = AtlasWH3.Formats.Trees.CampaignTreeList.Load(Paths.TreeList);
        int n = 0, exact = 0, close = 0;
        var misses = new List<string>();
        foreach (var t in trees.Types.SelectMany(type => type.Instances))
        {
            var y = terrain.Height(t.X, t.Z / AtlasWH3.Core.Campaign.Trees.TreeHeightField.ZScale);
            n++;
            if (BitConverter.SingleToInt32Bits(y) == BitConverter.SingleToInt32Bits(t.Y)) exact++;
            else if (misses.Count < 60) misses.Add(FormattableString.Invariant($"{t.X:R},{t.Z:R},{t.Y:R},{y:R}"));
            if (Math.Abs(y - t.Y) <= 1e-5f) close++;
        }
        // 2026-10-04: 205,765 of 205,767 bit-exact against CA's shipped list (was 61.46%): lf scaled with
        // TERRAIN_RENDER_SETUP's 1100 / 240 and (1/25.6)*T, height 0 outside the bounds and where no tile answers.
        // Left: one tree 1 ulp off on an hf tile, one where BOB rejects the sub-tile.
        Assert.True(exact >= n - 2, $"bit-exact {exact} of {n}, within 1e-5 {close}; first misses: " + string.Join(" ; ", misses));
        Assert.True(close >= n - 1, $"within 1e-5 {close} of {n}");
    }

    [Fact]
    public void TileHfHeight_VanillaTrees_MatchBobOwnOutput()
    {
        // BOB's own fresh "Campaign Trees" output on the vanilla kit (2026-10-04 Frida run)
        var bobList = Path.Combine(Paths.OutputRoot, "bob_runs", "frida_ctrees_vanilla1", "trees_bob.campaign_tree_list");
        var gm = Vanilla(Path.Combine("global_map", "tile_list.bin"));
        if (!File.Exists(Vanilla("tile_list.bin")) || !File.Exists(bobList) || !File.Exists(gm) || !Directory.Exists(Paths.GameDataDir)) return;
        var packs = AtlasWH3.Formats.Packs.PackSet.OpenVanilla(Paths.GameDataDir);
        var prefix = AtlasWH3.Formats.Packs.PackFile.Normalize(TileDatabase.Folder);
        var db = TileDatabase.Load(packs.Packs.SelectMany(p => p.Entries.Keys).Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Distinct().Select(k => packs.TryRead(k)).OfType<byte[]>());
        var list = TileList.Read(Vanilla("tile_list.bin"));
        var terrain = new AtlasWH3.Core.Campaign.Terrain.TileHfHeight(list, db, packs.TryRead,
            CompressedMap.Read(Vanilla("lf_height_map.compressed_map")), AtlasWH3.Core.Campaign.Terrain.TileHfHeight.TileSize3K)
            { BobCells = true, CellScaleX = Env("ATLASWH3_TREE_IX"), CellScaleZ = Env("ATLASWH3_TREE_IZ") };
        var trees = AtlasWH3.Formats.Trees.CampaignTreeList.Load(bobList);
        int n = 0, exact = 0;
        var misses = new List<string>();
        foreach (var t in trees.Types.SelectMany(type => type.Instances))
        {
            var y = terrain.TreeHeight(t.X, t.Z);
            n++;
            if (BitConverter.SingleToInt32Bits(y) == BitConverter.SingleToInt32Bits(t.Y)) exact++;
            else if (misses.Count < 20) misses.Add(FormattableString.Invariant($"{t.X:R},{t.Z:R},{t.Y:R},{y:R}"));
        }
        Assert.True(exact == n, $"bit-exact {exact} of {n}; misses: " + string.Join(" ; ", misses));
    }

    private static float Env(string name) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : float.NaN;
}
