using AtlasWH3.Formats.Packs;

namespace AtlasWH3.Tests;

public class PackTests
{
    private static byte[] Payload(int n)
    {
        var rng = new Random(7);
        var data = new byte[n];
        for (var i = 0; i < n; i++) data[i] = (byte)(i % 97 < 60 ? i % 13 : rng.Next(256));
        return data;
    }

    private static byte[] WithSize(byte[] payload, byte[] frame) => [.. BitConverter.GetBytes(payload.Length), .. frame];

    [Fact]
    public void Decompress_ReadsZstdEntries()
    {
        var payload = Payload(200_000);
        using var zstd = new ZstdSharp.Compressor(3);
        Assert.Equal(payload, PackFile.Decompress(WithSize(payload, zstd.Wrap(payload).ToArray())));
    }

    [Fact]
    public void Decompress_ReadsLz4Frames()
    {
        var payload = Payload(150_000);
        var frame = new MemoryStream();
        using (var lz4 = K4os.Compression.LZ4.Streams.LZ4Stream.Encode(frame, leaveOpen: true)) lz4.Write(payload);
        Assert.Equal(payload, PackFile.Decompress(WithSize(payload, frame.ToArray())));
    }

    [Fact]
    public void Decompress_ReportsLzma()
    {
        // LZMA1 "alone" header: properties byte 0x5D, dictionary size, then the stream
        byte[] lzma = [0x5D, 0, 0, 1, 0, 0x00, 0x41, 0x10];
        Assert.Throws<NotSupportedException>(() => PackFile.Decompress(WithSize(new byte[16], lzma)));
    }

    /// <summary>The game's own packs: PFH5, every campaign file compressed (zstd, some tile meshes LZ4).</summary>
    [Fact]
    public void Wh3_CampaignPacksDecode()
    {
        var data = TestKits.Wh3GameData;
        if (!File.Exists(Path.Combine(data, "tiles_campaign.pack"))) return;
        var tiles = PackFile.Open(Path.Combine(data, "tiles_campaign.pack"));
        Assert.All(tiles.Entries.Values, e => Assert.True(e.IsCompressed, e.Path));
        foreach (var e in tiles.Entries.Values.Where(e => e.Path.EndsWith(".rigid_model_v2", StringComparison.OrdinalIgnoreCase)).Take(50))
            Assert.Equal("RMV2"u8.ToArray(), tiles.TryRead(e.Path)![..4]);
        var settings = tiles.TryRead(@"terrain\tiles\campaign\_tile_database\_settings.bin");
        Assert.NotNull(settings);
    }
}
