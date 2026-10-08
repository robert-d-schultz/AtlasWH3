using SkiaSharp;
using AtlasWH3.Formats.Dds;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Packs;

namespace AtlasWH3.Core.Rendering;

/// <summary>A decoded texture with a box-filtered mip chain, stored as packed RGBA (0xAABBGGRR).</summary>
public sealed class MipTexture
{
    public IReadOnlyList<(int Size, uint[] Pixels)> Levels { get; }
    public uint Average { get; }

    public MipTexture(int size, uint[] level0)
    {
        var levels = new List<(int, uint[])> { (size, level0) };
        while (size > 1)
        {
            var prev = levels[^1].Item2;
            var half = size / 2;
            var next = new uint[half * half];
            for (var y = 0; y < half; y++)
            for (var x = 0; x < half; x++)
                next[y * half + x] = Avg4(prev[2 * y * size + 2 * x], prev[2 * y * size + 2 * x + 1],
                                          prev[(2 * y + 1) * size + 2 * x], prev[(2 * y + 1) * size + 2 * x + 1]);
            levels.Add((half, next));
            size = half;
        }
        Levels = levels;
        Average = levels[^1].Item2[0];
    }

    /// <summary>Nearest sample with wrap-around; <paramref name="u"/>/<paramref name="v"/> in texture repeats.</summary>
    public uint Sample(double u, double v, int level)
    {
        var (size, px) = Levels[Math.Min(level, Levels.Count - 1)];
        var x = (int)((u - Math.Floor(u)) * size) & (size - 1);
        var y = (int)((v - Math.Floor(v)) * size) & (size - 1);
        return px[y * size + x];
    }

    private static uint Avg4(uint a, uint b, uint c, uint d)
    {
        uint Ch(int shift) => (((a >> shift) & 0xFF) + ((b >> shift) & 0xFF) + ((c >> shift) & 0xFF) + ((d >> shift) & 0xFF) + 2) / 4;
        return Ch(0) | (Ch(8) << 8) | (Ch(16) << 16) | (Ch(24) << 24);
    }
}

/// <summary>
/// The 32 campaign terrain base-colour textures (texture_arrays.xml order), pulled from the game packs
/// once and cached as PNGs under <see cref="ProjectPaths.TextureCacheDir"/>.
/// </summary>
public sealed class TerrainTextureSet
{
    public const int CacheSize = 512;

    public IReadOnlyList<MipTexture?> Textures { get; }

    private TerrainTextureSet(IReadOnlyList<MipTexture?> textures) => Textures = textures;

    public static TerrainTextureSet Load(ProjectPaths paths, TextureArrays arrays, Action<string>? log = null)
    {
        Directory.CreateDirectory(paths.TextureCacheDir);
        PackSet? packs = null;
        var result = new MipTexture?[arrays.Groups.Count];

        foreach (var group in arrays.Groups)
        {
            var cachePath = Path.Combine(paths.TextureCacheDir, $"{group.Index:D2}_{group.Name}.png");
            if (!File.Exists(cachePath))
            {
                packs ??= PackSet.OpenVanilla(paths.GameDataDir,
                    n => n.StartsWith("terrain", StringComparison.OrdinalIgnoreCase));
                var bytes = packs.TryRead(group.BaseColour);
                if (bytes == null)
                {
                    log?.Invoke($"texture missing from packs: {group.BaseColour}");
                    continue;
                }
                var image = DdsTexture.Decode(bytes);
                SaveResizedPng(image, cachePath);
                log?.Invoke($"cached {group.Name} ({image.Width}x{image.Height})");
            }
            result[group.Index] = LoadPng(cachePath);
        }
        return new TerrainTextureSet(result);
    }

    private static void SaveResizedPng(DdsTexture.Image image, string path)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var source = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(image.Rgba, 0, source.GetPixels(), image.Rgba.Length);
        using var resized = source.Resize(new SKImageInfo(CacheSize, CacheSize, SKColorType.Rgba8888, SKAlphaType.Unpremul),
            new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var data = resized.Encode(SKEncodedImageFormat.Png, 90);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    private static MipTexture LoadPng(string path)
    {
        using var decoded = SKBitmap.Decode(path);
        using var bitmap = decoded.Width == CacheSize && decoded.Height == CacheSize
            ? decoded.Copy(SKColorType.Rgba8888)
            : decoded.Resize(new SKImageInfo(CacheSize, CacheSize, SKColorType.Rgba8888, SKAlphaType.Unpremul), SKSamplingOptions.Default);
        var pixels = new uint[CacheSize * CacheSize];
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bitmap.GetPixelSpan()).CopyTo(pixels);
        return new MipTexture(CacheSize, pixels);
    }
}
