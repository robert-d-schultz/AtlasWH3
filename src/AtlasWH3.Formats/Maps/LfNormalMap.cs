namespace AtlasWH3.Formats.Maps;

/// <summary>
/// lf_normal: the terrain normal map Terry exports (tweak_terrainmetadataeditor export_lf_normals, 0x257f00) from the
/// composited Height map, read off the plugin: 3x3 Sobel gradients with the edges clamped (0x25f660 across, 0x25f920
/// down), each tap (1 · h) · k summed in float, kernel row by row; n = (gx, gy, 1 / s) normalised; each channel
/// trunc((n + 1) · 127.5), alpha 255. s = terrain_size.x / map width × the tile database's
/// low_frequency_world_vertical_scale (0x28bc20; warscape's settings reader 0x63a980 names that field), which is 4 on
/// both fixtures (IEE 12800 px, Old World 16384 px wide). Against Terry's own exports: Old World's lf_normal.png
/// (2025-04-20) 99.94% of its sloped pixels identical, the rest in areas edited since; IEE's devastated project's 91.7%,
/// the rest in vanilla-derived areas of a merged file. Terry adds no height patches.
/// </summary>
public static class LfNormalMap
{
    /// <summary>s, measured on both fixtures (see the summary).</summary>
    public const float Spacing = 4f;

    private static readonly float[] Sobel = [1, 0, -1, 2, 0, -2, 1, 0, -1];

    /// <summary>RGBA bytes, row-major in the raster's row order: R from the x gradient, G from the y gradient, B up.</summary>
    public static byte[] Compute(Raster<float> heights, float spacing)
    {
        int w = heights.Width, h = heights.Height;
        var data = heights.Data;
        var rgba = new byte[w * h * 4];
        var invS = 1f / spacing;
        var invS2 = invS * invS;
        Parallel.For(0, h, y =>
        {
            Span<int> rows = [Math.Clamp(y - 1, 0, h - 1) * w, y * w, Math.Clamp(y + 1, 0, h - 1) * w];
            for (var x = 0; x < w; x++)
            {
                Span<int> cols = [Math.Max(x - 1, 0), x, Math.Min(x + 1, w - 1)];
                float gx = 0, gy = 0;
                for (var ky = 0; ky < 3; ky++)
                    for (var kx = 0; kx < 3; kx++)
                        gx += 1f * data[rows[ky] + cols[kx]] * Sobel[ky * 3 + kx];
                for (var ky = 0; ky < 3; ky++)
                    for (var kx = 0; kx < 3; kx++)
                        gy += 1f * data[rows[ky] + cols[kx]] * Sobel[kx * 3 + ky];
                var inv = 1f / MathF.Sqrt(gy * gy + gx * gx + invS2);
                var o = (y * w + x) * 4;
                rgba[o] = (byte)(int)((inv * gx + 1f) * 127.5f);
                rgba[o + 1] = (byte)(int)((inv * gy + 1f) * 127.5f);
                rgba[o + 2] = (byte)(int)((inv * invS + 1f) * 127.5f);
                rgba[o + 3] = 255;
            }
        });
        return rgba;
    }

    /// <summary>
    /// lf_normal.dds as BOB's Campaign Heightmap writes it through NVTT 2.0.8 (measured on IEE's lf_normal.png /
    /// lf_normal.dds pair, 2026-10-10): DXT5 with DDPF_NORMAL, the DXT5nm swizzle (colour (1, y, 0), alpha x), rows as
    /// the image, the full chain of box-filtered mips (BOB's mip 1 is the 2×2 average of mip 0 within its block noise),
    /// and NVTT's header tag. The blocks are DirectXTex's BC1 and BC4 encoders (<see cref="Dds.DirectXTex"/>), not NVTT's,
    /// so the bytes differ from BOB's.
    /// </summary>
    public static byte[] ToDds(byte[] rgba, int width, int height)
    {
        var swizzled = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            swizzled[i] = 255;
            swizzled[i + 1] = rgba[i + 1];
            swizzled[i + 2] = 0;
            swizzled[i + 3] = rgba[i];
        }
        using var ms = new MemoryStream();
        var levels = Dds.DirectXTex.Mips(swizzled, width, height, 4, round: true).ToList();
        ms.Write(Header(width, height, levels.Count));
        foreach (var (data, w, h) in levels)
        {
            var colour = (byte[])data.Clone();
            var x = new byte[w * h];
            for (var i = 0; i < x.Length; i++)
            {
                x[i] = data[i * 4 + 3];
                colour[i * 4 + 3] = 255;
            }
            var alphaBlocks = Dds.DirectXTex.CompressBc4(x, w, h);
            var colourBlocks = Dds.DirectXTex.CompressBc1(colour, w, h);
            var blocks = new byte[alphaBlocks.Length * 2];
            for (var b = 0; b < alphaBlocks.Length / 8; b++)
            {
                Buffer.BlockCopy(alphaBlocks, b * 8, blocks, b * 16, 8);
                Buffer.BlockCopy(colourBlocks, b * 8, blocks, b * 16 + 8, 8);
            }
            ms.Write(blocks);
        }
        return ms.ToArray();
    }

    /// <summary>The DDS header NVTT 2.0.8 writes for a DXT5 normal map (BOB's lf_normal.dds, byte for byte).</summary>
    private static byte[] Header(int width, int height, int mips)
    {
        var h = new byte[128];
        void U32(int at, uint v) => BitConverter.TryWriteBytes(h.AsSpan(at), v);
        U32(0, 0x20534444);                                         // "DDS "
        U32(4, 124);
        U32(8, 0xA1007);                                            // caps, height, width, pixel format, mip count, linear size
        U32(12, (uint)height);
        U32(16, (uint)width);
        U32(20, (uint)(((width + 3) / 4) * ((height + 3) / 4) * 16));
        U32(28, (uint)mips);
        U32(68, 0x5454564E);                                        // "NVTT"
        U32(72, 0x00020008);                                        // version 2.0.8
        U32(76, 32);
        U32(80, 0x80000004);                                        // DDPF_FOURCC | DDPF_NORMAL
        U32(84, 0x35545844);                                        // "DXT5"
        U32(108, 0x401008);                                         // complex, texture, mipmap
        return h;
    }
}
