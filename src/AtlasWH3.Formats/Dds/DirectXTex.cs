namespace AtlasWH3.Formats.Dds;

/// <summary>
/// The parts of Microsoft's DirectXTex (MIT; THIRD_PARTY_NOTICES.md) that WH3's BOB writes the campaign masks with, ported
/// so the output is byte-identical to BOB's (measured 2026-10-09 on IEE and Old World): warscape.modder.x64.dll links an
/// old DirectXTex statically and saves through WARSCAPE::save_image_and_generate_mips.
///  - Mips: GenerateMipMaps with the linear filter (filters.h CreateLinearFilter, BILINEAR_INTERPOLATE in float) from
///    the previous level, never through WIC. Each level is stored back to 8 bits before the next one is made: R8_UNORM
///    truncates (v · 255), R8G8B8A8_UNORM rounds half up (XMStoreUByteN4). Values load as b · (1/255).
///  - Compress: 4×4 blocks; a partial block repeats its pixels as DirectXTex does (uSrc = 0, 0, 0, 1), not the edge.
///  - BC1: D3DXEncodeBC1 (EncodeBC1 + OptimizeRGB), perceptual weights, no dithering, alpha threshold 0.5.
///  - BC4: D3DXEncodeBC4U (FindEndPointsBC4U + OptimizeAlpha), with the palette FindClosestUNORM builds as MSVC folded it
///    in warscape (0x18076c0d0): r0 · c′ + f1 · c with the binary's constants, not (f0 · (7 − i) + f1 · i) / 7. The plain
///    formula picks another index at about 1 pixel in 100 ties.
/// </summary>
public static class DirectXTex
{
    private const float Inv255 = 1f / 255f;

    // ---------------------------------------------------------------- resize / mips

    private readonly record struct Tap(int U0, int U1, float W0, float W1);

    /// <summary>filters.h CreateLinearFilter (clamp).</summary>
    private static Tap[] LinearFilter(int source, int dest)
    {
        var lf = new Tap[dest];
        var scale = (float)source / dest;
        for (var u = 0; u < dest; u++)
        {
            var srcB = (u + 0.5f) * scale + 0.5f;
            int isrcB = (int)srcB, isrcA = isrcB - 1;
            var weight = 1f + isrcB - srcB;
            if (isrcA < 0) isrcA = 0;
            if (isrcB >= source) isrcB = source - 1;
            lf[u] = new Tap(isrcA, isrcB, weight, 1f - weight);
        }
        return lf;
    }

    /// <summary>
    /// The linear resize of an 8-bit image with <paramref name="channels"/> interleaved channels to
    /// <paramref name="width"/> × <paramref name="height"/>, stored back to 8 bits (<paramref name="round"/>: half up as
    /// R8G8B8A8_UNORM does, else truncated as R8_UNORM). Also the mip filter, from one level to the next.
    /// </summary>
    public static byte[] Resize(byte[] src, int srcWidth, int srcHeight, int channels, int width, int height, bool round)
    {
        var fx = LinearFilter(srcWidth, width);
        var fy = LinearFilter(srcHeight, height);
        var dst = new byte[width * height * channels];
        var bias = round ? 0.5f : 0f;
        Parallel.For(0, height, y =>
        {
            var ty = fy[y];
            int r0 = ty.U0 * srcWidth * channels, r1 = ty.U1 * srcWidth * channels, o = y * width * channels;
            for (var x = 0; x < width; x++)
            {
                var tx = fx[x];
                int a = tx.U0 * channels, b = tx.U1 * channels;
                for (var c = 0; c < channels; c++)
                {
                    var v = (src[r0 + a + c] * Inv255 * tx.W0 + src[r0 + b + c] * Inv255 * tx.W1) * ty.W0
                          + (src[r1 + a + c] * Inv255 * tx.W0 + src[r1 + b + c] * Inv255 * tx.W1) * ty.W1;
                    v = v < 0f ? 0f : v > 1f ? 1f : v;
                    dst[o + x * channels + c] = (byte)(v * 255f + bias);
                }
            }
        });
        return dst;
    }

    /// <summary>Levels of a full mip chain (down to 1 × 1, each side halved and floored).</summary>
    public static int MipCount(int width, int height)
    {
        var n = 1;
        while (width > 1 || height > 1) { width = Math.Max(1, width / 2); height = Math.Max(1, height / 2); n++; }
        return n;
    }

    /// <summary>The full mip chain of an 8-bit image, the top level first (<see cref="Resize"/> level by level).</summary>
    public static IEnumerable<(byte[] Data, int Width, int Height)> Mips(byte[] top, int width, int height, int channels, bool round)
    {
        var (level, w, h) = (top, width, height);
        while (true)
        {
            yield return (level, w, h);
            if (w == 1 && h == 1) yield break;
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            level = Resize(level, w, h, channels, nw, nh, round);
            (w, h) = (nw, nh);
        }
    }

    // ---------------------------------------------------------------- block compression

    /// <summary>The 4×4 pixel offsets of the block at (bx, by), with DirectXTex's partial-block repeats.</summary>
    private static void BlockPixels(int bx, int by, int width, int height, Span<int> index)
    {
        ReadOnlySpan<int> uSrc = [0, 0, 0, 1];
        int pw = Math.Min(4, width - bx * 4), ph = Math.Min(4, height - by * 4);
        for (var j = 0; j < 4; j++)
            for (var i = 0; i < 4; i++)
            {
                int sj = j < ph ? j : uSrc[j], si = i < pw ? i : uSrc[i];
                if (sj >= ph) sj = uSrc[sj];
                if (si >= pw) si = uSrc[si];
                index[j * 4 + i] = (by * 4 + sj) * width + bx * 4 + si;
            }
    }

    /// <summary>BC4_UNORM blocks of an R8 image.</summary>
    public static byte[] CompressBc4(byte[] r8, int width, int height)
    {
        int bw = (width + 3) / 4, bh = (height + 3) / 4;
        var dst = new byte[bw * bh * 8];
        Parallel.For(0, bh, by =>
        {
            Span<int> index = stackalloc int[16];
            Span<float> texels = stackalloc float[16];
            for (var bx = 0; bx < bw; bx++)
            {
                BlockPixels(bx, by, width, height, index);
                for (var i = 0; i < 16; i++) texels[i] = r8[index[i]] * Inv255;
                BitConverter.TryWriteBytes(dst.AsSpan((by * bw + bx) * 8, 8), EncodeBc4U(texels));
            }
        });
        return dst;
    }

    /// <summary>BC1_UNORM blocks of an RGBA8 image (R, G, B, A bytes per pixel).</summary>
    public static byte[] CompressBc1(byte[] rgba, int width, int height)
    {
        int bw = (width + 3) / 4, bh = (height + 3) / 4;
        var dst = new byte[bw * bh * 8];
        Parallel.For(0, bh, by =>
        {
            Span<int> index = stackalloc int[16];
            Span<float> texels = stackalloc float[64];
            for (var bx = 0; bx < bw; bx++)
            {
                BlockPixels(bx, by, width, height, index);
                for (var i = 0; i < 16; i++)
                    for (var c = 0; c < 4; c++) texels[i * 4 + c] = rgba[index[i] * 4 + c] * Inv255;
                BitConverter.TryWriteBytes(dst.AsSpan((by * bw + bx) * 8, 8), EncodeBc1(texels, 0.5f));
            }
        });
        return dst;
    }

    // ---------------------------------------------------------------- BC4

    private static readonly float[] C6 = [5f / 5f, 4f / 5f, 3f / 5f, 2f / 5f, 1f / 5f, 0f / 5f], D6 = [0f / 5f, 1f / 5f, 2f / 5f, 3f / 5f, 4f / 5f, 5f / 5f];
    private static readonly float[] C8 = [7f / 7f, 6f / 7f, 5f / 7f, 4f / 7f, 3f / 7f, 2f / 7f, 1f / 7f, 0f / 7f], D8 = [0f / 7f, 1f / 7f, 2f / 7f, 3f / 7f, 4f / 7f, 5f / 7f, 6f / 7f, 7f / 7f];

    /// <summary>BC.h OptimizeAlpha&lt;false&gt;: Newton's method on the sum of squared errors.</summary>
    private static void OptimizeAlpha(out float pX, out float pY, ReadOnlySpan<float> points, int steps)
    {
        var pC = steps == 6 ? C6 : C8;
        var pD = steps == 6 ? D6 : D8;
        const float max = 1f, min = 0f;
        float fX = max, fY = min;
        if (steps == 8)
            for (var i = 0; i < 16; i++)
            {
                if (points[i] < fX) fX = points[i];
                if (points[i] > fY) fY = points[i];
            }
        else
        {
            for (var i = 0; i < 16; i++)
            {
                if (points[i] < fX && points[i] > min) fX = points[i];
                if (points[i] > fY && points[i] < max) fY = points[i];
            }
            if (fX == fY) fY = max;
        }
        float fSteps = steps - 1;
        Span<float> pSteps = stackalloc float[8];
        for (var iteration = 0; iteration < 8; iteration++)
        {
            if (fY - fX < 1f / 256f) break;
            var fScale = fSteps / (fY - fX);
            for (var s = 0; s < steps; s++) pSteps[s] = pC[s] * fX + pD[s] * fY;
            if (steps == 6) { pSteps[6] = min; pSteps[7] = max; }
            float dX = 0, dY = 0, d2X = 0, d2Y = 0;
            for (var i = 0; i < 16; i++)
            {
                var fDot = (points[i] - fX) * fScale;
                int s;
                if (fDot <= 0f) s = steps == 6 && points[i] <= fX * 0.5f ? 6 : 0;
                else if (fDot >= fSteps) s = steps == 6 && points[i] >= (fY + 1f) * 0.5f ? 7 : steps - 1;
                else s = (int)(fDot + 0.5f);
                if (s >= steps) continue;
                var fDiff = pSteps[s] - points[i];
                dX += pC[s] * fDiff; d2X += pC[s] * pC[s];
                dY += pD[s] * fDiff; d2Y += pD[s] * pD[s];
            }
            if (d2X > 0f) fX -= dX / d2X;
            if (d2Y > 0f) fY -= dY / d2Y;
            if (fX > fY) (fX, fY) = (fY, fX);
            if (dX * dX < 1f / 64f && dY * dY < 1f / 64f) break;
        }
        pX = fX < min ? min : fX > max ? max : fX;
        pY = fY < min ? min : fY > max ? max : fY;
    }

    private static float F(uint bits) => BitConverter.UInt32BitsToSingle(bits);

    /// <summary>The 8 palette values of a BC4 block, as warscape's FindClosestUNORM computes them.</summary>
    private static void Bc4Palette(byte e0, byte e1, Span<float> g)
    {
        float r0 = e0, f0 = r0 * F(0x3B808081), f1 = e1 * F(0x3B808081);   // · (1/255)
        g[0] = f0;
        g[1] = f1;
        if (e0 > e1)
        {
            g[2] = f1 * F(0x3E124925) + r0 * F(0x3B5C4A03);
            g[3] = f1 * F(0x3E924925) + r0 * F(0x3B379302);
            g[4] = f1 * F(0x3EDB6DB8) + r0 * F(0x3B12DC02);
            g[5] = f1 * F(0x3F124925) + r0 * F(0x3ADC4A03);
            g[6] = f1 * F(0x3F36DB6E) + r0 * F(0x3A92DC02);
            g[7] = f0 * F(0x3E124925) + f1 * F(0x3F5B6DB8);
        }
        else
        {
            g[2] = f1 * F(0x3E4CCCCD) + r0 * F(0x3B4D9A68);
            g[3] = f1 * F(0x3ECCCCCD) + r0 * F(0x3B1A33CF);
            g[4] = r0 * F(0x3ACD9A68) + f1 * F(0x3F19999A);
            g[5] = f1 * F(0x3F4CCCCD) + r0 * F(0x3A4D9A68);
            g[6] = 0f;
            g[7] = 1f;
        }
    }

    /// <summary>D3DXEncodeBC4U of one block (16 values in 0..1).</summary>
    public static ulong EncodeBc4U(ReadOnlySpan<float> texels)
    {
        float max = texels[0], min = texels[0];
        for (var i = 0; i < 16; i++)
        {
            if (texels[i] < min) min = texels[i];
            else if (texels[i] > max) max = texels[i];
        }
        byte e0, e1;
        if (min != 0f && max != 1f)
        {
            OptimizeAlpha(out var start, out var end, texels, 8);
            e0 = (byte)(end * 255f);
            e1 = (byte)(start * 255f);
        }
        else
        {
            OptimizeAlpha(out var start, out var end, texels, 6);
            e1 = (byte)(end * 255f);
            e0 = (byte)(start * 255f);
        }
        Span<float> g = stackalloc float[8];
        Bc4Palette(e0, e1, g);
        var data = e0 | (ulong)e1 << 8;
        for (var i = 0; i < 16; i++)
        {
            int best = 0;
            var bestDelta = 100000f;
            for (var k = 0; k < 8; k++)
            {
                var d = MathF.Abs(g[k] - texels[i]);
                if (d < bestDelta) { best = k; bestDelta = d; }
            }
            data |= (ulong)best << (3 * i + 16);
        }
        return data;
    }

    // ---------------------------------------------------------------- BC1

    private struct Rgb(float r, float g, float b)
    {
        public float R = r, G = g, B = b;
    }

    // Perceptual weightings (BC.cpp g_Luminance, g_LuminanceInv).
    private const float LumR = 0.2125f / 0.7154f, LumB = 0.0721f / 0.7154f, InvR = 0.7154f / 0.2125f, InvB = 0.7154f / 0.0721f;
    private static readonly float[] C3 = [2f / 2f, 1f / 2f, 0f / 2f], D3 = [0f / 2f, 1f / 2f, 2f / 2f];
    private static readonly float[] C4 = [3f / 3f, 2f / 3f, 1f / 3f, 0f / 3f], D4 = [0f / 3f, 1f / 3f, 2f / 3f, 3f / 3f];

    /// <summary>BC.cpp OptimizeRGB (COLOR_WEIGHTS off).</summary>
    private static void OptimizeRgb(out Rgb pX, out Rgb pY, ReadOnlySpan<Rgb> points, int steps)
    {
        const float epsilon = 0.25f / 64f * (0.25f / 64f);
        var pC = steps == 3 ? C3 : C4;
        var pD = steps == 3 ? D3 : D4;
        Rgb x = new(LumR, 1f, LumB), y = new(0, 0, 0);
        foreach (var p in points)
        {
            if (p.R < x.R) x.R = p.R;
            if (p.G < x.G) x.G = p.G;
            if (p.B < x.B) x.B = p.B;
            if (p.R > y.R) y.R = p.R;
            if (p.G > y.G) y.G = p.G;
            if (p.B > y.B) y.B = p.B;
        }
        Rgb ab = new(y.R - x.R, y.G - x.G, y.B - x.B);
        var fAB = ab.R * ab.R + ab.G * ab.G + ab.B * ab.B;
        if (fAB < float.Epsilon * 8388608f) { pX = x; pY = y; return; }   // FLT_MIN

        var fABInv = 1f / fAB;
        Rgb dir = new(ab.R * fABInv, ab.G * fABInv, ab.B * fABInv);
        Rgb mid = new((x.R + y.R) * 0.5f, (x.G + y.G) * 0.5f, (x.B + y.B) * 0.5f);
        float d0 = 0, d1 = 0, d2 = 0, d3 = 0;
        foreach (var p in points)
        {
            float r = (p.R - mid.R) * dir.R, g = (p.G - mid.G) * dir.G, b = (p.B - mid.B) * dir.B, f;
            f = r + g + b; d0 += f * f;
            f = r + g - b; d1 += f * f;
            f = r - g + b; d2 += f * f;
            f = r - g - b; d3 += f * f;
        }
        int dirMax = 0;
        var fDirMax = d0;
        if (d1 > fDirMax) { fDirMax = d1; dirMax = 1; }
        if (d2 > fDirMax) { fDirMax = d2; dirMax = 2; }
        if (d3 > fDirMax) dirMax = 3;
        if ((dirMax & 2) != 0) (x.G, y.G) = (y.G, x.G);
        if ((dirMax & 1) != 0) (x.B, y.B) = (y.B, x.B);
        if (fAB < 1f / 4096f) { pX = x; pY = y; return; }

        float fSteps = steps - 1;
        Span<Rgb> pSteps = stackalloc Rgb[4];
        for (var iteration = 0; iteration < 8; iteration++)
        {
            for (var s = 0; s < steps; s++)
                pSteps[s] = new Rgb(x.R * pC[s] + y.R * pD[s], x.G * pC[s] + y.G * pD[s], x.B * pC[s] + y.B * pD[s]);
            dir = new Rgb(y.R - x.R, y.G - x.G, y.B - x.B);
            var fLen = dir.R * dir.R + dir.G * dir.G + dir.B * dir.B;
            if (fLen < 1f / 4096f) break;
            var fScale = fSteps / fLen;
            dir.R *= fScale; dir.G *= fScale; dir.B *= fScale;
            float d2X = 0, d2Y = 0;
            Rgb dX = default, dY = default;
            foreach (var p in points)
            {
                var fDot = (p.R - x.R) * dir.R + (p.G - x.G) * dir.G + (p.B - x.B) * dir.B;
                var s = fDot <= 0f ? 0 : fDot >= fSteps ? steps - 1 : (int)(fDot + 0.5f);
                float dr = pSteps[s].R - p.R, dg = pSteps[s].G - p.G, db = pSteps[s].B - p.B;
                float fC = pC[s] * (1f / 8f), fD = pD[s] * (1f / 8f);
                d2X += fC * pC[s]; dX.R += fC * dr; dX.G += fC * dg; dX.B += fC * db;
                d2Y += fD * pD[s]; dY.R += fD * dr; dY.G += fD * dg; dY.B += fD * db;
            }
            if (d2X > 0f) { var f = -1f / d2X; x.R += dX.R * f; x.G += dX.G * f; x.B += dX.B * f; }
            if (d2Y > 0f) { var f = -1f / d2Y; y.R += dY.R * f; y.G += dY.G * f; y.B += dY.B * f; }
            if (dX.R * dX.R < epsilon && dX.G * dX.G < epsilon && dX.B * dX.B < epsilon &&
                dY.R * dY.R < epsilon && dY.G * dY.G < epsilon && dY.B * dY.B < epsilon) break;
        }
        pX = x;
        pY = y;
    }

    private static ushort Encode565(Rgb c)
    {
        float r = c.R < 0 ? 0 : c.R > 1 ? 1 : c.R, g = c.G < 0 ? 0 : c.G > 1 ? 1 : c.G, b = c.B < 0 ? 0 : c.B > 1 ? 1 : c.B;
        return (ushort)((int)(r * 31f + 0.5f) << 11 | (int)(g * 63f + 0.5f) << 5 | (int)(b * 31f + 0.5f));
    }

    private static Rgb Decode565(ushort w) => new((w >> 11 & 31) * (1f / 31f), (w >> 5 & 63) * (1f / 63f), (w & 31) * (1f / 31f));

    private static Rgb Lerp(Rgb a, Rgb b, float s) => new(a.R + s * (b.R - a.R), a.G + s * (b.G - a.G), a.B + s * (b.B - a.B));

    /// <summary>D3DXEncodeBC1 of one block (16 × RGBA in 0..1), no dithering, perceptual weights.</summary>
    public static ulong EncodeBc1(ReadOnlySpan<float> rgba, float threshold)
    {
        var keyed = 0;
        for (var i = 0; i < 16; i++) if (rgba[i * 4 + 3] < threshold) keyed++;
        if (keyed == 16) return 0xFFFFFFFF_FFFF0000ul;
        var steps = keyed > 0 ? 3 : 4;

        Span<Rgb> color = stackalloc Rgb[16];
        for (var i = 0; i < 16; i++)
            color[i] = new Rgb((int)(rgba[i * 4] * 31f + 0.5f) * (1f / 31f) * LumR,
                               (int)(rgba[i * 4 + 1] * 63f + 0.5f) * (1f / 63f),
                               (int)(rgba[i * 4 + 2] * 31f + 0.5f) * (1f / 31f) * LumB);
        OptimizeRgb(out var a, out var b, color, steps);
        var wA = Encode565(new Rgb(a.R * InvR, a.G, a.B * InvB));
        var wB = Encode565(new Rgb(b.R * InvR, b.G, b.B * InvB));
        if (steps == 4 && wA == wB) return wA | (ulong)wB << 16;

        var cA = Decode565(wA);
        var cB = Decode565(wB);
        a = new Rgb(cA.R * LumR, cA.G, cA.B * LumB);
        b = new Rgb(cB.R * LumR, cB.G, cB.B * LumB);
        Span<Rgb> step = stackalloc Rgb[4];
        ushort c0, c1;
        if (steps == 3 == wA <= wB) { c0 = wA; c1 = wB; step[0] = a; step[1] = b; }
        else { c0 = wB; c1 = wA; step[0] = b; step[1] = a; }
        ReadOnlySpan<int> order3 = [0, 2, 1], order4 = [0, 2, 3, 1];
        var order = steps == 3 ? order3 : order4;
        if (steps == 3) step[2] = Lerp(step[0], step[1], 0.5f);
        else
        {
            step[2] = Lerp(step[0], step[1], 1f / 3f);
            step[3] = Lerp(step[0], step[1], 2f / 3f);
        }
        Rgb dir = new(step[1].R - step[0].R, step[1].G - step[0].G, step[1].B - step[0].B);
        float fSteps = steps - 1;
        var fScale = wA != wB ? fSteps / (dir.R * dir.R + dir.G * dir.G + dir.B * dir.B) : 0f;
        dir.R *= fScale; dir.G *= fScale; dir.B *= fScale;

        uint dw = 0;
        for (var i = 0; i < 16; i++)
        {
            if (steps == 3 && rgba[i * 4 + 3] < threshold) { dw = 3u << 30 | dw >> 2; continue; }
            float r = rgba[i * 4] * LumR, g = rgba[i * 4 + 1], bl = rgba[i * 4 + 2] * LumB;
            var fDot = (r - step[0].R) * dir.R + (g - step[0].G) * dir.G + (bl - step[0].B) * dir.B;
            var s = fDot <= 0f ? 0u : fDot >= fSteps ? 1u : (uint)order[(int)(fDot + 0.5f)];
            dw = s << 30 | dw >> 2;
        }
        return c0 | (ulong)c1 << 16 | (ulong)dw << 32;
    }
}
