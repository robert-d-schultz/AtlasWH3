using AtlasWH3.Formats.Dds.Bc6hVendor;

namespace AtlasWH3.Formats.Dds;

/// <summary>
/// BC6H_SF16 (signed) decoding and encoding, for full_height_map.dds (land height in red, sea bed in green, blue 0).
/// The mode bit layouts come from BCnEncoder.NET (Bc6hVendorLayouts.cs); the index layout, the decoder and the encoder
/// are AtlasWH3's own, after the D3D11 BC6H specification.
///
/// The encoder is tuned for smooth 2-channel height data: for each block it fits a line through the pixels in BC6H's
/// integer ("unquantized") domain, tries every one-region mode (10.10, 11.9, 12.8, 16.4 bits), refines the endpoints by
/// least squares on the chosen indices and by ±1 steps on the quantized endpoints, and keeps the encoding with the
/// smallest squared error in float. Blocks the one-region modes cannot fit closely enough also try the two-region modes
/// on the best-ranked partitions. BCnEncoder.NET's own BC6H search is 17-40 times less accurate on height maps and
/// DirectXTex's CPU encoder takes about 90 minutes for one map, hence this one.
/// </summary>
public static class Bc6h
{
    public const int BlockBytes = 16;

    /// <summary>Scales the error above which a block also tries the two-region modes (tuning; infinity = never).</summary>
    public static double TwoRegionFactor { get; set; } = 16;

    private static readonly Bc6Mode[] OneRegion = [Bc6Mode.Type3, Bc6Mode.Type7, Bc6Mode.Type11, Bc6Mode.Type15];
    private static readonly Bc6Mode[] TwoRegion =
        [Bc6Mode.Type0, Bc6Mode.Type1, Bc6Mode.Type2, Bc6Mode.Type6, Bc6Mode.Type10, Bc6Mode.Type14, Bc6Mode.Type18, Bc6Mode.Type22, Bc6Mode.Type26, Bc6Mode.Type30];

    private static readonly int[] W3 = [0, 9, 18, 27, 37, 46, 55, 64];
    private static readonly int[] W4 = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    /// <summary>BC6H's 32 two-region partitions (pixel → subset), and the anchor pixel of subset 1.</summary>
    internal static readonly byte[][] Partitions =
    [
        [0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1], [0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1],
        [0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1], [0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 1],
        [0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 1, 1], [0, 0, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1],
        [0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1], [0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 1],
        [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1], [0, 0, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1],
        [0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 1], [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1],
        [0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1], [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1],
        [0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1], [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1],
        [0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0, 1, 1, 1, 1], [0, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0],
        [0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0], [0, 1, 1, 1, 0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0],
        [0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0, 1, 1, 1, 0],
        [0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0], [0, 1, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 0, 1],
        [0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0], [0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0],
        [0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0], [0, 0, 1, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 0, 0],
        [0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 0, 1, 0, 0, 0], [0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0],
        [0, 1, 1, 1, 0, 0, 0, 1, 1, 0, 0, 0, 1, 1, 1, 0], [0, 0, 1, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 1, 0, 0],
    ];

    internal static readonly int[] Anchor2 = [15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2];

    // ------------------------------------------------------------------ shared arithmetic

    private static int SignExtend(int v, int bits) => bits >= 32 ? v : (v << (32 - bits)) >> (32 - bits);

    /// <summary>Spec unquantize, signed: an endpoint of <paramref name="bits"/> bits to the 16-bit interpolation domain.</summary>
    internal static int Unquantize(int comp, int bits)
    {
        if (bits >= 16) return comp;
        var neg = comp < 0;
        if (neg) comp = -comp;
        int unq;
        if (comp == 0) unq = 0;
        else if (comp >= (1 << (bits - 1)) - 1) unq = 0x7FFF;
        else unq = ((comp << 15) + 0x4000) >> (bits - 1);
        return neg ? -unq : unq;
    }

    private static int Interpolate(int e0, int e1, int w) => ((64 - w) * e0 + w * e1 + 32) >> 6;

    /// <summary>Spec finish_unquantize, signed: interpolated value → half-float bits → float.</summary>
    internal static float Finish(int comp) => FinishTable[comp + 0x8000];

    private static float FinishSlow(int comp)
    {
        comp = comp < 0 ? -(((-comp) * 31) >> 5) : (comp * 31) >> 5;
        var bits = comp < 0 ? 0x8000 | -comp : comp;
        return (float)BitConverter.UInt16BitsToHalf((ushort)bits);
    }

    /// <summary><see cref="FinishSlow"/> for every interpolated value (-0x8000..0x7FFF).</summary>
    private static readonly float[] FinishTable = Enumerable.Range(-0x8000, 0x10000).Select(FinishSlow).ToArray();

    /// <summary>The interpolation-domain value whose <see cref="Finish"/> is <paramref name="v"/> (as a real number).</summary>
    internal static double ToDomain(float v)
    {
        var h = BitConverter.HalfToUInt16Bits((Half)v);
        var mag = h & 0x7FFF;
        if (mag > 0x7BFF) mag = 0x7BFF;             // no infinities / NaN
        var d = mag * 32.0 / 31.0;
        return (h & 0x8000) != 0 ? -d : d;
    }

    // ------------------------------------------------------------------ decoding

    private static Bc6Block Block(ReadOnlySpan<byte> b) => new()
    {
        lowBits = BitConverter.ToUInt64(b),
        highBits = BitConverter.ToUInt64(b[8..]),
    };

    /// <summary>The block's mode, endpoints (interpolation domain) and indices.</summary>
    internal static bool Unpack(ReadOnlySpan<byte> bytes, Span<int> endpoints /*12: e0..e3 × rgb*/, Span<int> indices, out int partition, out Bc6Mode mode)
    {
        var block = Block(bytes);
        mode = block.Type;
        partition = 0;
        if (mode == Bc6Mode.Unknown) return false;
        var bits = mode.EndpointBits();
        var (dr, dg, db) = mode.DeltaBits();
        var two = mode.HasSubsets();
        var raw = new (int, int, int)[two ? 4 : 2];
        raw[0] = block.ExtractEp0();
        raw[1] = block.ExtractEp1();
        if (two) { raw[2] = block.ExtractEp2(); raw[3] = block.ExtractEp3(); }
        var e0 = (SignExtend(raw[0].Item1, bits), SignExtend(raw[0].Item2, bits), SignExtend(raw[0].Item3, bits));
        var mask = (1 << bits) - 1;
        for (var i = 0; i < raw.Length; i++)
        {
            var (r, g, b) = raw[i];
            if (i > 0 && mode.HasTransformedEndpoints())
            {
                r = (SignExtend(r, dr) + e0.Item1) & mask;
                g = (SignExtend(g, dg) + e0.Item2) & mask;
                b = (SignExtend(b, db) + e0.Item3) & mask;
            }
            endpoints[i * 3] = Unquantize(SignExtend(r, bits), bits);
            endpoints[i * 3 + 1] = Unquantize(SignExtend(g, bits), bits);
            endpoints[i * 3 + 2] = Unquantize(SignExtend(b, bits), bits);
        }
        if (two)
        {
            partition = (int)ByteHelper.Extract(block.highBits, 13, 5);
            var anchor = Anchor2[partition];
            var pos = 82;
            for (var p = 0; p < 16; p++)
            {
                var n = p == 0 || p == anchor ? 2 : 3;
                indices[p] = (int)ByteHelper.ExtractFrom128(block.lowBits, block.highBits, pos, n);
                pos += n;
            }
        }
        else
        {
            var pos = 65;
            for (var p = 0; p < 16; p++)
            {
                var n = p == 0 ? 3 : 4;
                indices[p] = (int)ByteHelper.ExtractFrom128(block.lowBits, block.highBits, pos, n);
                pos += n;
            }
        }
        return true;
    }

    /// <summary>One block to 16 RGB float pixels (row-major, 48 floats). Reserved modes decode to zero.</summary>
    public static void DecodeBlock(ReadOnlySpan<byte> bytes, Span<float> rgb)
    {
        Span<int> ep = stackalloc int[12];
        Span<int> idx = stackalloc int[16];
        if (!Unpack(bytes, ep, idx, out var partition, out var mode)) { rgb[..48].Clear(); return; }
        var two = mode.HasSubsets();
        var w = two ? W3 : W4;
        for (var p = 0; p < 16; p++)
        {
            var s = two ? Partitions[partition][p] : 0;
            for (var c = 0; c < 3; c++)
                rgb[p * 3 + c] = Finish(Interpolate(ep[s * 6 + c], ep[s * 6 + 3 + c], w[idx[p]]));
        }
    }

    /// <summary>A whole BC6H_SF16 image (blocks row-major) to RGB floats, row 0 first as stored.</summary>
    public static float[] Decode(ReadOnlySpan<byte> data, int width, int height)
    {
        var result = new float[width * height * 3];
        int bw = (width + 3) / 4, bh = (height + 3) / 4;
        var bytes = data[..(bw * bh * BlockBytes)].ToArray();
        Parallel.For(0, bh, by =>
        {
            Span<float> px = stackalloc float[48];
            for (var bx = 0; bx < bw; bx++)
            {
                DecodeBlock(bytes.AsSpan((by * bw + bx) * BlockBytes, BlockBytes), px);
                for (var j = 0; j < 4; j++)
                    for (var i = 0; i < 4; i++)
                    {
                        int x = bx * 4 + i, y = by * 4 + j;
                        if (x >= width || y >= height) continue;
                        for (var c = 0; c < 3; c++) result[(y * width + x) * 3 + c] = px[(j * 4 + i) * 3 + c];
                    }
            }
        });
        return result;
    }

    // ------------------------------------------------------------------ encoding

    /// <summary>Encodes a 2-channel image (red, green; blue 0), rows as stored, into BC6H_SF16 blocks.
    /// <paramref name="encodeBlock"/> (block column, block row) limits the work to some blocks; the others stay zero.</summary>
    public static byte[] Encode(float[] red, float[] green, int width, int height, Action<double>? progress = null,
                                Func<int, int, bool>? encodeBlock = null)
    {
        int bw = (width + 3) / 4, bh = (height + 3) / 4;
        var output = new byte[bw * bh * BlockBytes];
        var done = 0;
        Parallel.For(0, bh, () => new Encoder(), (by, _, enc) =>
        {
            Span<float> px = stackalloc float[48];
            for (var bx = 0; bx < bw; bx++)
            {
                if (encodeBlock is not null && !encodeBlock(bx, by)) continue;
                for (var j = 0; j < 4; j++)
                    for (var i = 0; i < 4; i++)
                    {
                        int x = Math.Min(bx * 4 + i, width - 1), y = Math.Min(by * 4 + j, height - 1);
                        var k = (j * 4 + i) * 3;
                        px[k] = red[y * width + x];
                        px[k + 1] = green[y * width + x];
                        px[k + 2] = 0;
                    }
                enc.EncodeBlock(px, output.AsSpan((by * bw + bx) * BlockBytes, BlockBytes));
            }
            if (progress is not null && Interlocked.Increment(ref done) % 64 == 0) progress((double)done / bh);
            return enc;
        }, _ => { });
        return output;
    }

    /// <summary>One block (16 RGB float pixels, 48 floats, row-major) to 16 bytes.</summary>
    public static void EncodeBlock(ReadOnlySpan<float> rgb, Span<byte> output) => new Encoder().EncodeBlock(rgb, output);

    /// <summary>Per-thread scratch for the block search.</summary>
    private sealed class Encoder
    {
        private readonly float[] _target = new float[48];
        private readonly double[] _u = new double[48];
        private readonly int[] _idx = new int[16], _bestIdx = new int[16], _trialIdx = new int[16];
        private readonly int[] _q = new int[12], _bestQ = new int[12];
        private readonly int[] _trial = new int[12];

        public void EncodeBlock(ReadOnlySpan<float> rgb, Span<byte> output)
        {
            // a non-finite texel (Old World's Height has one NaN) takes the mean of the block's finite ones in its
            // channel, so it does not drag the fit
            for (var c = 0; c < 3; c++)
            {
                double sum = 0;
                var n = 0;
                for (var p = 0; p < 16; p++)
                    if (float.IsFinite(rgb[p * 3 + c])) { sum += rgb[p * 3 + c]; n++; }
                var fill = n > 0 ? (float)(sum / n) : 0f;
                for (var p = 0; p < 16; p++)
                {
                    var i = p * 3 + c;
                    _target[i] = float.IsFinite(rgb[i]) ? rgb[i] : fill;
                    _u[i] = ToDomain(_target[i]);
                }
            }
            var best = double.MaxValue;
            var bestMode = Bc6Mode.Unknown;
            var bestPartition = 0;
            foreach (var mode in OneRegion)
            {
                var err = FitMode(mode, -1);
                if (err < best) { best = err; bestMode = mode; Array.Copy(_q, _bestQ, 12); Array.Copy(_idx, _bestIdx, 16); }
            }
            // smooth blocks end here; the rest try the two-region modes on their best partitions
            if (best > TwoRegionThreshold())
            {
                foreach (var partition in RankPartitions(2))
                    foreach (var mode in TwoRegion)
                    {
                        var err = FitMode(mode, partition);
                        if (err < best) { best = err; bestMode = mode; bestPartition = partition; Array.Copy(_q, _bestQ, 12); Array.Copy(_idx, _bestIdx, 16); }
                    }
            }
            Pack(bestMode, bestPartition, _bestQ, _bestIdx, output);
        }

        /// <summary>Squared error worth a second region: 1/2 of a half-float step at the block's largest value,
        /// per pixel and channel.</summary>
        private double TwoRegionThreshold()
        {
            var max = 0f;
            foreach (var v in _target) max = Math.Max(max, Math.Abs(v));
            var step = max < 6.1e-5f ? 6e-8f : MathF.Pow(2, MathF.Floor(MathF.Log2(max)) - 10);
            return 32 * (step * 0.5) * (step * 0.5) * TwoRegionFactor;
        }

        /// <summary>Partitions in order of the summed residual of a straight-line fit of each subset.</summary>
        private int[] RankPartitions(int count)
        {
            Span<double> scores = stackalloc double[32];
            for (var p = 0; p < 32; p++)
                scores[p] = LineResidual(p, 0) + LineResidual(p, 1);
            var order = Enumerable.Range(0, 32).ToArray();
            var s = scores.ToArray();
            Array.Sort(s, order);
            return order[..count];
        }

        private double LineResidual(int partition, int subset)
        {
            double n = 0, mr = 0, mg = 0;
            for (var p = 0; p < 16; p++)
                if (Partitions[partition][p] == subset) { n++; mr += _u[p * 3]; mg += _u[p * 3 + 1]; }
            if (n == 0) return 0;
            mr /= n; mg /= n;
            double crr = 0, cgg = 0, crg = 0;
            for (var p = 0; p < 16; p++)
                if (Partitions[partition][p] == subset)
                {
                    double dr = _u[p * 3] - mr, dg = _u[p * 3 + 1] - mg;
                    crr += dr * dr; cgg += dg * dg; crg += dr * dg;
                }
            // residual = smallest eigenvalue of the 2×2 covariance, plus 3-bit quantisation along the largest
            var tr = crr + cgg;
            var disc = Math.Sqrt(Math.Max(0, (crr - cgg) * (crr - cgg) / 4 + crg * crg));
            var small = tr / 2 - disc;
            var large = tr / 2 + disc;
            return small + large / (7.0 * 7.0 * 12.0);
        }

        /// <summary>Fits one mode (one region when <paramref name="partition"/> is -1); leaves the quantized endpoints
        /// and indices in _q / _idx and returns the squared float error (MaxValue when the mode cannot hold the block).</summary>
        private double FitMode(Bc6Mode mode, int partition)
        {
            var two = partition >= 0;
            var subsets = two ? 2 : 1;
            var bits = mode.EndpointBits();
            for (var s = 0; s < subsets; s++)
            {
                LineEndpoints(partition, s, out var a, out var b);
                for (var c = 0; c < 3; c++)
                {
                    _q[s * 6 + c] = Quantize(a[c], bits);
                    _q[s * 6 + 3 + c] = Quantize(b[c], bits);
                }
            }
            var err = Evaluate(mode, partition, _q, _idx);
            if (err == double.MaxValue) return err;     // the line's endpoints do not fit this mode at all
            // least squares on the chosen indices, twice; Refit writes _q, so a refit that is no better (or does not fit
            // the mode) is undone
            var trial = _trial;
            for (var it = 0; it < 2; it++)
            {
                Array.Copy(_q, trial, 12);
                Refit(mode, partition, _idx);
                var e = Evaluate(mode, partition, _q, _trialIdx);
                if (e < err) { err = e; Array.Copy(_trialIdx, _idx, 16); }
                else { Array.Copy(trial, _q, 12); break; }
            }
            // ±1 steps on each quantized endpoint coordinate (red, green)
            for (var pass = 0; pass < 2; pass++)
            {
                var improved = false;
                for (var k = 0; k < subsets * 6; k++)
                {
                    if (k % 3 == 2) continue;              // blue stays 0
                    foreach (var step in (ReadOnlySpan<int>)[-1, 1])
                    {
                        Array.Copy(_q, trial, 12);
                        trial[k] += step;
                        var e = Evaluate(mode, partition, trial, _trialIdx);
                        if (e < err) { err = e; Array.Copy(trial, _q, 12); Array.Copy(_trialIdx, _idx, 16); improved = true; }
                    }
                }
                if (!improved) break;
            }
            return err;
        }

        /// <summary>Endpoints of the principal axis through one subset, in the interpolation domain.</summary>
        private void LineEndpoints(int partition, int subset, out double[] a, out double[] b)
        {
            double n = 0, mr = 0, mg = 0;
            for (var p = 0; p < 16; p++)
                if (partition < 0 || Partitions[partition][p] == subset) { n++; mr += _u[p * 3]; mg += _u[p * 3 + 1]; }
            mr /= n; mg /= n;
            double crr = 0, cgg = 0, crg = 0;
            for (var p = 0; p < 16; p++)
                if (partition < 0 || Partitions[partition][p] == subset)
                {
                    double dr = _u[p * 3] - mr, dg = _u[p * 3 + 1] - mg;
                    crr += dr * dr; cgg += dg * dg; crg += dr * dg;
                }
            // principal eigenvector of [[crr, crg], [crg, cgg]]
            double vx, vy;
            if (Math.Abs(crg) > 1e-12) { var l = (crr + cgg) / 2 + Math.Sqrt((crr - cgg) * (crr - cgg) / 4 + crg * crg); vx = l - cgg; vy = crg; }
            else if (crr >= cgg) { vx = 1; vy = 0; }
            else { vx = 0; vy = 1; }
            var len = Math.Sqrt(vx * vx + vy * vy);
            vx /= len; vy /= len;
            double tmin = double.MaxValue, tmax = double.MinValue;
            for (var p = 0; p < 16; p++)
                if (partition < 0 || Partitions[partition][p] == subset)
                {
                    var t = (_u[p * 3] - mr) * vx + (_u[p * 3 + 1] - mg) * vy;
                    tmin = Math.Min(tmin, t); tmax = Math.Max(tmax, t);
                }
            a = [mr + tmin * vx, mg + tmin * vy, 0];
            b = [mr + tmax * vx, mg + tmax * vy, 0];
        }

        /// <summary>Least-squares endpoints for fixed indices, written to _q.</summary>
        private void Refit(Bc6Mode mode, int partition, int[] idx)
        {
            var two = partition >= 0;
            var w = two ? W3 : W4;
            var bits = mode.EndpointBits();
            Span<double> xa = stackalloc double[2], xb = stackalloc double[2];
            for (var s = 0; s < (two ? 2 : 1); s++)
            {
                double aa = 0, ab = 0, bb = 0;
                xa.Clear(); xb.Clear();
                for (var p = 0; p < 16; p++)
                {
                    if (two && Partitions[partition][p] != s) continue;
                    var t = w[idx[p]] / 64.0;
                    aa += (1 - t) * (1 - t); ab += (1 - t) * t; bb += t * t;
                    for (var c = 0; c < 2; c++) { xa[c] += (1 - t) * _u[p * 3 + c]; xb[c] += t * _u[p * 3 + c]; }
                }
                var det = aa * bb - ab * ab;
                if (Math.Abs(det) < 1e-9) continue;
                for (var c = 0; c < 2; c++)
                {
                    var e0 = (bb * xa[c] - ab * xb[c]) / det;
                    var e1 = (aa * xb[c] - ab * xa[c]) / det;
                    _q[s * 6 + c] = Quantize(e0, bits);
                    _q[s * 6 + 3 + c] = Quantize(e1, bits);
                }
            }
        }

        /// <summary>The quantized value whose unquantization is closest to <paramref name="u"/>.</summary>
        private static int Quantize(double u, int bits)
        {
            if (bits >= 16) return (int)Math.Clamp(Math.Round(u), short.MinValue + 1, short.MaxValue);
            var max = (1 << (bits - 1)) - 1;
            var guess = (int)Math.Round((Math.Abs(u) * (1 << (bits - 1)) - 0x4000) / 32768.0) * Math.Sign(u);
            var best = 0;
            var bestErr = double.MaxValue;
            for (var q = guess - 1; q <= guess + 1; q++)
            {
                var c = Math.Clamp(q, -max, max);
                var e = Math.Abs(Unquantize(c, bits) - u);
                if (e < bestErr) { bestErr = e; best = c; }
            }
            return best;
        }

        /// <summary>Picks the indices for quantized endpoints <paramref name="q"/> and returns the squared float error,
        /// or MaxValue if the mode cannot store them (endpoint range, delta range).</summary>
        private double Evaluate(Bc6Mode mode, int partition, int[] q, int[] idx)
        {
            var two = partition >= 0;
            var bits = mode.EndpointBits();
            var limit = bits >= 16 ? short.MaxValue : (1 << (bits - 1)) - 1;
            for (var k = 0; k < (two ? 12 : 6); k++)
                if (q[k] > limit || q[k] < -limit) return double.MaxValue;
            if (!DeltasFit(mode, q, two, -1)) return double.MaxValue;
            var w = two ? W3 : W4;
            var levels = w.Length;
            Span<int> ep = stackalloc int[12];
            for (var k = 0; k < 12; k++) ep[k] = Unquantize(q[k], bits);
            Span<float> palette = stackalloc float[16 * 2];
            var total = 0.0;
            for (var s = 0; s < (two ? 2 : 1); s++)
            {
                for (var i = 0; i < levels; i++)
                {
                    palette[i * 2] = Finish(Interpolate(ep[s * 6], ep[s * 6 + 3], w[i]));
                    palette[i * 2 + 1] = Finish(Interpolate(ep[s * 6 + 1], ep[s * 6 + 4], w[i]));
                }
                for (var p = 0; p < 16; p++)
                {
                    if (two && Partitions[partition][p] != s) continue;
                    float tr = _target[p * 3], tg = _target[p * 3 + 1];
                    var bestE = double.MaxValue;
                    var bestI = 0;
                    for (var i = 0; i < levels; i++)
                    {
                        double dr = palette[i * 2] - tr, dg = palette[i * 2 + 1] - tg;
                        var e = dr * dr + dg * dg;
                        if (e < bestE) { bestE = e; bestI = i; }
                    }
                    idx[p] = bestI;
                    total += bestE;
                }
            }
            // Pack swaps subset 0's endpoints when pixel 0's index has its top bit set: the base is then endpoint 1
            if (mode.HasTransformedEndpoints() && !DeltasFit(mode, q, two, (idx[0] & (two ? 4 : 8)) != 0 ? 1 : 0)) return double.MaxValue;
            return total;
        }

        /// <summary>Transformed modes store the other endpoints as deltas from endpoint 0 (after the anchor swap that
        /// <see cref="Pack"/> may do, which only exchanges endpoints within a subset).</summary>
        private static bool DeltasFit(Bc6Mode mode, int[] q, bool two, int requiredBase)
        {
            if (!mode.HasTransformedEndpoints()) return true;
            var (dr, dg, db) = mode.DeltaBits();
            Span<int> d = [dr, dg, db];
            // the base is one endpoint of subset 0 (either, when not yet known); every other endpoint must be within
            // delta range of it
            for (var baseIdx = 0; baseIdx < 2; baseIdx++)
            {
                if (requiredBase >= 0 && baseIdx != requiredBase) continue;
                var ok = true;
                for (var e = 0; e < (two ? 4 : 2) && ok; e++)
                {
                    if (e == baseIdx) continue;
                    for (var c = 0; c < 3 && ok; c++)
                    {
                        var delta = q[e * 3 + c] - q[baseIdx * 3 + c];
                        if (delta < -(1 << (d[c] - 1)) || delta >= 1 << (d[c] - 1)) ok = false;
                    }
                }
                if (ok) return true;
            }
            return false;
        }

        /// <summary>Writes the block: anchor indices made to fit (swapping a subset's endpoints and inverting its indices
        /// when an anchor index has its top bit set), endpoints transformed to deltas for transformed modes.</summary>
        private static void Pack(Bc6Mode mode, int partition, int[] q, int[] idx, Span<byte> output)
        {
            var two = mode.HasSubsets();
            var ib = two ? 3 : 4;
            var top = 1 << (ib - 1);
            var maxIdx = (1 << ib) - 1;
            var e = (int[])q.Clone();
            var ix = (int[])idx.Clone();
            for (var s = 0; s < (two ? 2 : 1); s++)
            {
                var anchor = s == 0 ? 0 : Anchor2[partition];
                if ((ix[anchor] & top) == 0) continue;
                for (var c = 0; c < 3; c++) (e[s * 6 + c], e[s * 6 + 3 + c]) = (e[s * 6 + 3 + c], e[s * 6 + c]);
                for (var p = 0; p < 16; p++)
                    if (!two || Partitions[partition][p] == s) ix[p] = maxIdx - ix[p];
            }
            var bits = mode.EndpointBits();
            var mask = (1 << bits) - 1;
            (int, int, int) Ep(int k) => (e[k * 3] & mask, e[k * 3 + 1] & mask, e[k * 3 + 2] & mask);
            (int, int, int) Delta(int k)
            {
                var (dr, dg, db) = mode.DeltaBits();
                return ((e[k * 3] - e[0]) & ((1 << dr) - 1), (e[k * 3 + 1] - e[1]) & ((1 << dg) - 1), (e[k * 3 + 2] - e[2]) & ((1 << db) - 1));
            }
            var transformed = mode.HasTransformedEndpoints();
            var block = new Bc6Block { lowBits = ModeBits(mode) };
            block.StoreEp0(Ep(0));
            block.StoreEp1(transformed ? Delta(1) : Ep(1));
            if (two)
            {
                block.StoreEp2(transformed ? Delta(2) : Ep(2));
                block.StoreEp3(transformed ? Delta(3) : Ep(3));
                block.highBits = ByteHelper.Store(block.highBits, 13, 5, (ulong)partition);
                var pos = 82;
                var anchor = Anchor2[partition];
                for (var p = 0; p < 16; p++)
                {
                    var n = p == 0 || p == anchor ? 2 : 3;
                    (block.lowBits, block.highBits) = ByteHelper.StoreTo128(block.lowBits, block.highBits, pos, n, (ulong)ix[p]);
                    pos += n;
                }
            }
            else
            {
                var pos = 65;
                for (var p = 0; p < 16; p++)
                {
                    var n = p == 0 ? 3 : 4;
                    (block.lowBits, block.highBits) = ByteHelper.StoreTo128(block.lowBits, block.highBits, pos, n, (ulong)ix[p]);
                    pos += n;
                }
            }
            BitConverter.TryWriteBytes(output, block.lowBits);
            BitConverter.TryWriteBytes(output[8..], block.highBits);
        }

        /// <summary>The mode field: 2 bits for modes 1-2 (types 0-1), else 5 bits.</summary>
        private static ulong ModeBits(Bc6Mode mode) => (ulong)(uint)mode;
    }
}
