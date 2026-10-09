// Vendored from BCnEncoder.NET (https://github.com/Nominom/BCnEncoder.NET), BCnEnc.Net/Shared/Bc6Block.cs:
// the BC6H block types and the per-mode endpoint bit layouts (StoreEp0..3 / ExtractEp0..3). MIT licence,
// Copyright (c) 2020 Nominom. Only the layouts are taken; index storage, decoding and encoding are AtlasWH3's own
// (Bc6h.cs), checked against DirectXTex's decoder.
// Endpoint layouts were validated by decoding WH3's own full_height_map.dds (written by CA's AMD Compress).
#pragma warning disable
namespace AtlasWH3.Formats.Dds.Bc6hVendor;

internal static class ByteHelper
{
    public static byte Extract5(ulong source, int index) => (byte)((source >> index) & 0b11111);
    public static ulong Store5(ulong dest, int index, byte value) { const ulong mask = 0b11111; dest &= ~(mask << index); dest |= (value & mask) << index; return dest; }
    public static ulong Extract(ulong source, int index, int bitCount) { unchecked { var mask = (1UL << bitCount) - 1; return (source >> index) & mask; } }
    public static ulong Store(ulong dest, int index, int bitCount, ulong value) { unchecked { var mask = (1UL << bitCount) - 1; dest &= ~(mask << index); dest |= (value & mask) << index; return dest; } }
    public static ulong ExtractFrom128(ulong low, ulong high, int index, int bitCount)
    {
        if (index + bitCount <= 64) return Extract(low, index, bitCount);
        if (index >= 64) return Extract(high, index - 64, bitCount);
        var lowBitCount = 64 - index;
        var value = Extract(low, index, lowBitCount);
        return Store(value, lowBitCount, bitCount - lowBitCount, Extract(high, 0, bitCount - lowBitCount));
    }
    public static (ulong, ulong) StoreTo128(ulong low, ulong high, int index, int bitCount, ulong value)
    {
        if (index + bitCount <= 64) return (Store(low, index, bitCount, value), high);
        if (index >= 64) return (low, Store(high, index - 64, bitCount, value));
        var lowBitCount = 64 - index;
        var l = Store(low, index, lowBitCount, value);
        return (l, Store(high, 0, bitCount - lowBitCount, value >> lowBitCount));
    }
}

    internal enum Bc6Mode : uint
    {
        Type0 = 0, // Mode 1
        Type1 = 1, // Mode 2
        Type2 = 2, // Mode 3
        Type6 = 6, // Mode 4
        Type10 = 10, // Mode 5
        Type14 = 14, // Mode 6
        Type18 = 18, // Mode 7
        Type22 = 22, // Mode 8
        Type26 = 26, // Mode 9
        Type30 = 30, // Mode 10
        Type3 = 3, // Mode 11
        Type7 = 7, // Mode 12
        Type11 = 11, // Mode 13
        Type15 = 15, // Mode 14
        Unknown
    }

    internal static class Bc6ModeExtensions
    {
        public static bool HasSubsets(this Bc6Mode Type) => Type switch
        {
            Bc6Mode.Type3 => false,
            Bc6Mode.Type7 => false,
            Bc6Mode.Type11 => false,
            Bc6Mode.Type15 => false,
            _ => true
        };

        public static bool HasTransformedEndpoints(this Bc6Mode Type) => Type switch
        {
            Bc6Mode.Type3 => false,
            Bc6Mode.Type30 => false,
            _ => true
        };

        public static int EndpointBits(this Bc6Mode Type) => Type switch
        {
            Bc6Mode.Type0 => 10,
            Bc6Mode.Type1 => 7,
            Bc6Mode.Type2 => 11,
            Bc6Mode.Type6 => 11,
            Bc6Mode.Type10 => 11,
            Bc6Mode.Type14 => 9,
            Bc6Mode.Type18 => 8,
            Bc6Mode.Type22 => 8,
            Bc6Mode.Type26 => 8,
            Bc6Mode.Type30 => 6,
            Bc6Mode.Type3 => 10,
            Bc6Mode.Type7 => 11,
            Bc6Mode.Type11 => 12,
            Bc6Mode.Type15 => 16,
            _ => 0
        };

        public static (int, int, int) DeltaBits(this Bc6Mode Type) => Type switch
        {
            Bc6Mode.Type0 => (5, 5, 5),
            Bc6Mode.Type1 => (6, 6, 6),
            Bc6Mode.Type2 => (5, 4, 4),
            Bc6Mode.Type6 => (4, 5, 4),
            Bc6Mode.Type10 => (4, 4, 5),
            Bc6Mode.Type14 => (5, 5, 5),
            Bc6Mode.Type18 => (6, 5, 5),
            Bc6Mode.Type22 => (5, 6, 5),
            Bc6Mode.Type26 => (5, 5, 6),
            Bc6Mode.Type30 => (0, 0, 0),
            Bc6Mode.Type3 => (0, 0, 0),
            Bc6Mode.Type7 => (9, 9, 9),
            Bc6Mode.Type11 => (8, 8, 8),
            Bc6Mode.Type15 => (4, 4, 4),
            _ => (0, 0, 0)
        };
    }

    internal struct Bc6Block
    {
        public ulong lowBits;
        public ulong highBits;

        public readonly Bc6Mode Type
        {
            get
            {
                const ulong smallMask = 0b11;
                const ulong bigMask = 0b11111;
                // Type 0 or 1
                if ((lowBits & smallMask) < 2)
                {
                    return (Bc6Mode)(lowBits & smallMask);
                }
                else
                {
                    var typeNum = (lowBits & bigMask);
                    switch (typeNum)
                    {
                        case 2: return Bc6Mode.Type2;
                        case 3: return Bc6Mode.Type3;
                        case 6: return Bc6Mode.Type6;
                        case 7: return Bc6Mode.Type7;
                        case 10: return Bc6Mode.Type10;
                        case 11: return Bc6Mode.Type11;
                        case 14: return Bc6Mode.Type14;
                        case 15: return Bc6Mode.Type15;
                        case 18: return Bc6Mode.Type18;
                        case 22: return Bc6Mode.Type22;
                        case 26: return Bc6Mode.Type26;
                        case 30: return Bc6Mode.Type30;
                        default: return Bc6Mode.Unknown;
                    }
                }
            }
        }
        public readonly bool HasSubsets => Type.HasSubsets();
        public readonly bool HasTransformedEndpoints => Type.HasTransformedEndpoints();
        public readonly int EndpointBits => Type.EndpointBits();
        public readonly (int, int, int) DeltaBits => Type.DeltaBits();

        internal void StoreEp0((int, int, int) endpoint)
        {
            var r0 = (ulong)endpoint.Item1;
            var g0 = (ulong)endpoint.Item2;
            var b0 = (ulong)endpoint.Item3;

            (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 5, Math.Min(10, EndpointBits), r0);
            (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 15, Math.Min(10, EndpointBits), g0);
            (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 25, Math.Min(10, EndpointBits), b0);

            switch (Type)
            {
                case Bc6Mode.Type2:
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 40, 1, r0 >> 10);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 49, 1, g0 >> 10);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 59, 1, b0 >> 10);

                    break;
                case Bc6Mode.Type6:
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 39, 1, r0 >> 10);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 50, 1, g0 >> 10);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 59, 1, b0 >> 10);

                    break;
                case Bc6Mode.Type10:
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 39, 1, r0 >> 10);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 49, 1, g0 >> 10);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 60, 1, b0 >> 10);

                    break;
                case Bc6Mode.Type7:
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 44, 1, r0 >> 10);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 54, 1, g0 >> 10);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 64, 1, b0 >> 10);

                    break;
                case Bc6Mode.Type11:
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 44, 1, r0 >> 10);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 54, 1, g0 >> 10);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 64, 1, b0 >> 10);

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 43, 1, r0 >> 11);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 53, 1, g0 >> 11);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 63, 1, b0 >> 11);

                    break;
                case Bc6Mode.Type15:
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 44, 1, r0 >> 10);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 54, 1, g0 >> 10);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 64, 1, b0 >> 10);

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 43, 1, r0 >> 11);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 53, 1, g0 >> 11);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 63, 1, b0 >> 11);

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 42, 1, r0 >> 12);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 52, 1, g0 >> 12);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 62, 1, b0 >> 12);

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 41, 1, r0 >> 13);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 51, 1, g0 >> 13);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 61, 1, b0 >> 13);

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 40, 1, r0 >> 14);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 50, 1, g0 >> 14);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 60, 1, b0 >> 14);

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 39, 1, r0 >> 15);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 49, 1, g0 >> 15);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 59, 1, b0 >> 15);

                    break;
            }
        }

        internal readonly (int, int, int) ExtractEp0()
        {
            ulong r0 = 0;
            ulong g0 = 0;
            ulong b0 = 0;

            r0 = ByteHelper.ExtractFrom128(lowBits, highBits, 5, Math.Min(10, EndpointBits));
            g0 = ByteHelper.ExtractFrom128(lowBits, highBits, 15, Math.Min(10, EndpointBits));
            b0 = ByteHelper.ExtractFrom128(lowBits, highBits, 25, Math.Min(10, EndpointBits));

            switch (Type)
            {
                case Bc6Mode.Type2:

                    r0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 40, 1) << 10;
                    g0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 49, 1) << 10;
                    b0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 59, 1) << 10;
                    break;
                case Bc6Mode.Type6:

                    r0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 39, 1) << 10;
                    g0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 50, 1) << 10;
                    b0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 59, 1) << 10;
                    break;
                case Bc6Mode.Type10:

                    r0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 39, 1) << 10;
                    g0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 49, 1) << 10;
                    b0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 60, 1) << 10;
                    break;
                case Bc6Mode.Type7:
                    r0 = ByteHelper.ExtractFrom128(lowBits, highBits, 5, 10);
                    g0 = ByteHelper.ExtractFrom128(lowBits, highBits, 15, 10);
                    b0 = ByteHelper.ExtractFrom128(lowBits, highBits, 25, 10);

                    r0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 44, 1) << 10;
                    g0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 54, 1) << 10;
                    b0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 64, 1) << 10;
                    break;
                case Bc6Mode.Type11:
                    r0 = ByteHelper.ExtractFrom128(lowBits, highBits, 5, 10);
                    g0 = ByteHelper.ExtractFrom128(lowBits, highBits, 15, 10);
                    b0 = ByteHelper.ExtractFrom128(lowBits, highBits, 25, 10);

                    r0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 44, 1) << 10;
                    g0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 54, 1) << 10;
                    b0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 64, 1) << 10;

                    r0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 43, 1) << 11;
                    g0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 53, 1) << 11;
                    b0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 63, 1) << 11;
                    break;
                case Bc6Mode.Type15:
                    r0 = ByteHelper.ExtractFrom128(lowBits, highBits, 5, 10);
                    g0 = ByteHelper.ExtractFrom128(lowBits, highBits, 15, 10);
                    b0 = ByteHelper.ExtractFrom128(lowBits, highBits, 25, 10);

                    r0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 44, 1) << 10;
                    g0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 54, 1) << 10;
                    b0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 64, 1) << 10;

                    r0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 43, 1) << 11;
                    g0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 53, 1) << 11;
                    b0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 63, 1) << 11;

                    r0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 42, 1) << 12;
                    g0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 52, 1) << 12;
                    b0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 62, 1) << 12;

                    r0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 41, 1) << 13;
                    g0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 51, 1) << 13;
                    b0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 61, 1) << 13;

                    r0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 40, 1) << 14;
                    g0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 50, 1) << 14;
                    b0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 60, 1) << 14;

                    r0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 39, 1) << 15;
                    g0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 49, 1) << 15;
                    b0 |= ByteHelper.ExtractFrom128(lowBits, highBits, 59, 1) << 15;
                    break;
            }

            return ((int)r0, (int)g0, (int)b0);
        }

        internal void StoreEp1((int, int, int) endpoint)
        {
            var r1 = (ulong)endpoint.Item1;
            var g1 = (ulong)endpoint.Item2;
            var b1 = (ulong)endpoint.Item3;

            if (HasTransformedEndpoints)
            {
                (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 35, Math.Min(5, DeltaBits.Item1), r1);
                (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 45, Math.Min(5, DeltaBits.Item2), g1);
                (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 55, Math.Min(5, DeltaBits.Item3), b1);

            }

            switch (Type)
            {
                case Bc6Mode.Type1:
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 40, 1, r1 >> 5);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 50, 1, g1 >> 5);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 60, 1, b1 >> 5);


                    break;
                case Bc6Mode.Type18:
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 40, 1, r1 >> 5);


                    break;
                case Bc6Mode.Type22:
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 50, 1, g1 >> 5);

                    break;
                case Bc6Mode.Type26:
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 60, 1, b1 >> 5);

                    break;
                case Bc6Mode.Type30:
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 35, 6, r1);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 45, 6, g1);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 55, 6, b1);

                    break;
                case Bc6Mode.Type3:
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 35, 10, r1);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 45, 10, g1);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 55, 10, b1);

                    break;
                case Bc6Mode.Type7:
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 40, 4, r1 >> 5);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 50, 4, g1 >> 5);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 60, 4, b1 >> 5);

                    break;
                case Bc6Mode.Type11:
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 40, 3, r1 >> 5);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 50, 3, g1 >> 5);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 60, 3, b1 >> 5);

                    break;
            }
        }

        internal readonly (int, int, int) ExtractEp1()
        {
            ulong r1 = 0;
            ulong g1 = 0;
            ulong b1 = 0;

            if (HasTransformedEndpoints)
            {
                r1 = ByteHelper.ExtractFrom128(lowBits, highBits, 35, Math.Min(5, DeltaBits.Item1));
                g1 = ByteHelper.ExtractFrom128(lowBits, highBits, 45, Math.Min(5, DeltaBits.Item2));
                b1 = ByteHelper.ExtractFrom128(lowBits, highBits, 55, Math.Min(5, DeltaBits.Item3));
            }

            switch (Type)
            {
                case Bc6Mode.Type1:
                    r1 |= ByteHelper.ExtractFrom128(lowBits, highBits, 40, 1) << 5;
                    g1 |= ByteHelper.ExtractFrom128(lowBits, highBits, 50, 1) << 5;
                    b1 |= ByteHelper.ExtractFrom128(lowBits, highBits, 60, 1) << 5;

                    break;
                case Bc6Mode.Type18:
                    r1 |= ByteHelper.ExtractFrom128(lowBits, highBits, 40, 1) << 5;

                    break;
                case Bc6Mode.Type22:
                    g1 |= ByteHelper.ExtractFrom128(lowBits, highBits, 50, 1) << 5;

                    break;
                case Bc6Mode.Type26:
                    b1 |= ByteHelper.ExtractFrom128(lowBits, highBits, 60, 1) << 5;

                    break;
                case Bc6Mode.Type30:
                    r1 = ByteHelper.ExtractFrom128(lowBits, highBits, 35, 6);
                    g1 = ByteHelper.ExtractFrom128(lowBits, highBits, 45, 6);
                    b1 = ByteHelper.ExtractFrom128(lowBits, highBits, 55, 6);

                    break;
                case Bc6Mode.Type3:
                    r1 = ByteHelper.ExtractFrom128(lowBits, highBits, 35, 10);
                    g1 = ByteHelper.ExtractFrom128(lowBits, highBits, 45, 10);
                    b1 = ByteHelper.ExtractFrom128(lowBits, highBits, 55, 10);

                    break;
                case Bc6Mode.Type7:
                    r1 |= ByteHelper.ExtractFrom128(lowBits, highBits, 40, 4) << 5;
                    g1 |= ByteHelper.ExtractFrom128(lowBits, highBits, 50, 4) << 5;
                    b1 |= ByteHelper.ExtractFrom128(lowBits, highBits, 60, 4) << 5;

                    break;
                case Bc6Mode.Type11:
                    r1 |= ByteHelper.ExtractFrom128(lowBits, highBits, 40, 3) << 5;
                    g1 |= ByteHelper.ExtractFrom128(lowBits, highBits, 50, 3) << 5;
                    b1 |= ByteHelper.ExtractFrom128(lowBits, highBits, 60, 3) << 5;

                    break;
            }

            return ((int)r1, (int)g1, (int)b1);
        }

        internal void StoreEp2((int, int, int) endpoint)
        {
            var r2 = (ulong) endpoint.Item1;
            var g2 = (ulong) endpoint.Item2;
            var b2 = (ulong) endpoint.Item3;

            (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 65, Math.Min(5, DeltaBits.Item1), r2);
            (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 41, 4, g2);
            (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 61, 4, b2);

            switch (Type)
            {
                case Bc6Mode.Type0:
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 2, 1, g2 >> 4);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 3, 1, b2 >> 4);

                    break;
                case Bc6Mode.Type1:
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 70, 1, r2 >> 5);

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 24, 1, g2 >> 4);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 2, 1, g2 >> 5);

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 14, 1, b2 >> 4);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 22, 1, b2 >> 5);

                    break;
                case Bc6Mode.Type6:

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 75, 1, g2 >> 4);

                    break;
                case Bc6Mode.Type10:

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 40, 1, b2 >> 4);

                    break;
                case Bc6Mode.Type14:
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 24, 1, g2 >> 4);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 14, 1, b2 >> 4);

                    break;
                case Bc6Mode.Type18:
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 70, 1, r2 >> 5);

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 24, 1, g2 >> 4);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 14, 1, b2 >> 4);

                    break;
                case Bc6Mode.Type22:

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 24, 1, g2 >> 4);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 23, 1, g2 >> 5);

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 14, 1, b2 >> 4);

                    break;
                case Bc6Mode.Type26:
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 24, 1, g2 >> 4);

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 14, 1, b2 >> 4);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 23, 1, b2 >> 5);

                    break;
                case Bc6Mode.Type30:

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 65, 6, r2);

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 24, 1, g2 >> 4);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 21, 1, g2 >> 5);

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 14, 1, b2 >> 4);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 22, 1, b2 >> 5);

                    break;
            }
        }

        internal readonly (int, int, int) ExtractEp2()
        {
            ulong r2 = 0;
            ulong g2 = 0;
            ulong b2 = 0;

            r2 = ByteHelper.ExtractFrom128(lowBits, highBits, 65, Math.Min(5, DeltaBits.Item1));
            g2 = ByteHelper.ExtractFrom128(lowBits, highBits, 41, 4);
            b2 = ByteHelper.ExtractFrom128(lowBits, highBits, 61, 4);

            switch (Type)
            {
                case Bc6Mode.Type0:

                    g2 |= ByteHelper.ExtractFrom128(lowBits, highBits, 2, 1) << 4;
                    b2 |= ByteHelper.ExtractFrom128(lowBits, highBits, 3, 1) << 4;
                    break;
                case Bc6Mode.Type1:
                    r2 |= ByteHelper.ExtractFrom128(lowBits, highBits, 70, 1) << 5;

                    g2 |= ByteHelper.ExtractFrom128(lowBits, highBits, 24, 1) << 4;
                    g2 |= ByteHelper.ExtractFrom128(lowBits, highBits, 2, 1) << 5;

                    b2 |= ByteHelper.ExtractFrom128(lowBits, highBits, 14, 1) << 4;
                    b2 |= ByteHelper.ExtractFrom128(lowBits, highBits, 22, 1) << 5;

                    break;
                case Bc6Mode.Type6:

                    g2 |= ByteHelper.ExtractFrom128(lowBits, highBits, 75, 1) << 4;

                    break;
                case Bc6Mode.Type10:

                    b2 |= ByteHelper.ExtractFrom128(lowBits, highBits, 40, 1) << 4;

                    break;
                case Bc6Mode.Type14:
                    g2 |= ByteHelper.ExtractFrom128(lowBits, highBits, 24, 1) << 4;
                    b2 |= ByteHelper.ExtractFrom128(lowBits, highBits, 14, 1) << 4;

                    break;
                case Bc6Mode.Type18:
                    r2 |= ByteHelper.ExtractFrom128(lowBits, highBits, 70, 1) << 5;

                    g2 |= ByteHelper.ExtractFrom128(lowBits, highBits, 24, 1) << 4;
                    b2 |= ByteHelper.ExtractFrom128(lowBits, highBits, 14, 1) << 4;

                    break;
                case Bc6Mode.Type22:

                    g2 |= ByteHelper.ExtractFrom128(lowBits, highBits, 24, 1) << 4;
                    g2 |= ByteHelper.ExtractFrom128(lowBits, highBits, 23, 1) << 5;

                    b2 |= ByteHelper.ExtractFrom128(lowBits, highBits, 14, 1) << 4;
                    break;
                case Bc6Mode.Type26:
                    g2 |= ByteHelper.ExtractFrom128(lowBits, highBits, 24, 1) << 4;

                    b2 |= ByteHelper.ExtractFrom128(lowBits, highBits, 14, 1) << 4;
                    b2 |= ByteHelper.ExtractFrom128(lowBits, highBits, 23, 1) << 5;
                    break;
                case Bc6Mode.Type30:

                    r2 = ByteHelper.ExtractFrom128(lowBits, highBits, 65, 6);

                    g2 |= ByteHelper.ExtractFrom128(lowBits, highBits, 24, 1) << 4;
                    g2 |= ByteHelper.ExtractFrom128(lowBits, highBits, 21, 1) << 5;

                    b2 |= ByteHelper.ExtractFrom128(lowBits, highBits, 14, 1) << 4;
                    b2 |= ByteHelper.ExtractFrom128(lowBits, highBits, 22, 1) << 5;

                    break;
            }

            return ((int)r2, (int)g2, (int)b2);
        }

        internal void StoreEp3((int, int, int) endpoint)
        {
            var r3 = (ulong)endpoint.Item1;
            var g3 = (ulong)endpoint.Item2;
            var b3 = (ulong)endpoint.Item3;

            (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 71, Math.Min(5, DeltaBits.Item1), r3);
            (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 51, 4, g3);

            switch (Type)
            {
                case Bc6Mode.Type0:

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 40, 1, g3 >> 4);

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 50, 1, b3 >> 0);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 60, 1, b3 >> 1);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 70, 1, b3 >> 2);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 76, 1, b3 >> 3);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 4, 1, b3 >> 4);

                    break;
                case Bc6Mode.Type1:

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 76, 1, r3 >> 5);

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 3, 2, g3 >> 4);

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 12, 2, b3 >> 0);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 23, 1, b3 >> 2);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 32, 1, b3 >> 3);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 34, 1, b3 >> 4);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 33, 1, b3 >> 5);

                    break;
                case Bc6Mode.Type2:

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 50, 1, b3 >> 0);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 60, 1, b3 >> 1);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 70, 1, b3 >> 2);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 76, 1, b3 >> 3);

                    break;
                case Bc6Mode.Type6:

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 40, 1, g3 >> 4);

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 69, 1, b3 >> 0);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 60, 1, b3 >> 1);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 70, 1, b3 >> 2);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 76, 1, b3 >> 3);

                    break;
                case Bc6Mode.Type10:

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 50, 1, b3 >> 0);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 69, 1, b3 >> 1);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 70, 1, b3 >> 2);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 76, 1, b3 >> 3);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 75, 1, b3 >> 4);

                    break;
                case Bc6Mode.Type14:

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 40, 1, g3 >> 4);

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 50, 1, b3 >> 0);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 60, 1, b3 >> 1);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 70, 1, b3 >> 2);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 76, 1, b3 >> 3);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 34, 1, b3 >> 4);

                    break;
                case Bc6Mode.Type18:

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 76, 1, r3 >> 5);

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 13, 1, g3 >> 4);

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 50, 1, b3 >> 0);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 60, 1, b3 >> 1);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 23, 1, b3 >> 2);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 33, 1, b3 >> 3);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 34, 1, b3 >> 4);

                    break;
                case Bc6Mode.Type22:

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 40, 1, g3 >> 4);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 33, 1, g3 >> 5);

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 13, 1, b3 >> 0);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 60, 1, b3 >> 1);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 70, 1, b3 >> 2);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 76, 1, b3 >> 3);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 34, 1, b3 >> 4);

                    break;
                case Bc6Mode.Type26:

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 40, 1, g3 >> 4);

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 50, 1, b3 >> 0);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 13, 1, b3 >> 1);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 70, 1, b3 >> 2);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 76, 1, b3 >> 3);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 34, 1, b3 >> 4);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 33, 1, b3 >> 5);

                    break;
                case Bc6Mode.Type30:

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 71, 6, r3);

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 11, 1, g3 >> 4);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 31, 1, g3 >> 5);

                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 12, 2, b3 >> 0);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 23, 1, b3 >> 2);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 32, 1, b3 >> 3);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 34, 1, b3 >> 4);
                    (lowBits, highBits) = ByteHelper.StoreTo128(lowBits, highBits, 33, 1, b3 >> 5);

                    break;
            }
        }

        internal readonly (int, int, int) ExtractEp3()
        {
            ulong r3 = 0;
            ulong g3 = 0;
            ulong b3 = 0;

            r3 = ByteHelper.ExtractFrom128(lowBits, highBits, 71, Math.Min(5, DeltaBits.Item1));
            g3 = ByteHelper.ExtractFrom128(lowBits, highBits, 51, 4);

            switch (Type)
            {
                case Bc6Mode.Type0:
                    g3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 40, 1) << 4;

                    b3 = ByteHelper.ExtractFrom128(lowBits, highBits, 50, 1);
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 60, 1) << 1;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 70, 1) << 2;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 76, 1) << 3;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 4, 1) << 4;
                    break;
                case Bc6Mode.Type1:
                    r3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 76, 1) << 5;

                    g3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 3, 2) << 4;

                    b3 = ByteHelper.ExtractFrom128(lowBits, highBits, 12, 2);
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 23, 1) << 2;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 32, 1) << 3;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 34, 1) << 4;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 33, 1) << 5;

                    break;
                case Bc6Mode.Type2:

                    b3 = ByteHelper.ExtractFrom128(lowBits, highBits, 50, 1);
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 60, 1) << 1;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 70, 1) << 2;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 76, 1) << 3;

                    break;
                case Bc6Mode.Type6:

                    g3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 40, 1) << 4;

                    b3 = ByteHelper.ExtractFrom128(lowBits, highBits, 69, 1);
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 60, 1) << 1;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 70, 1) << 2;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 76, 1) << 3;

                    break;
                case Bc6Mode.Type10:

                    b3 = ByteHelper.ExtractFrom128(lowBits, highBits, 50, 1);
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 69, 1) << 1;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 70, 1) << 2;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 76, 1) << 3;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 75, 1) << 4;

                    break;
                case Bc6Mode.Type14:
                    g3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 40, 1) << 4;


                    b3 = ByteHelper.ExtractFrom128(lowBits, highBits, 50, 1);
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 60, 1) << 1;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 70, 1) << 2;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 76, 1) << 3;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 34, 1) << 4;

                    break;
                case Bc6Mode.Type18:
                    r3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 76, 1) << 5;

                    g3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 13, 1) << 4;

                    b3 = ByteHelper.ExtractFrom128(lowBits, highBits, 50, 1);
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 60, 1) << 1;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 23, 1) << 2;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 33, 1) << 3;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 34, 1) << 4;

                    break;
                case Bc6Mode.Type22:

                    g3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 40, 1) << 4;
                    g3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 33, 1) << 5;

                    b3 = ByteHelper.ExtractFrom128(lowBits, highBits, 13, 1);
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 60, 1) << 1;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 70, 1) << 2;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 76, 1) << 3;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 34, 1) << 4;

                    break;
                case Bc6Mode.Type26:
                    g3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 40, 1) << 4;

                    b3 = ByteHelper.ExtractFrom128(lowBits, highBits, 50, 1);
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 13, 1) << 1;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 70, 1) << 2;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 76, 1) << 3;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 34, 1) << 4;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 33, 1) << 5;

                    break;
                case Bc6Mode.Type30:

                    r3 = ByteHelper.ExtractFrom128(lowBits, highBits, 71, 6);


                    g3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 11, 1) << 4;
                    g3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 31, 1) << 5;

                    b3 = ByteHelper.ExtractFrom128(lowBits, highBits, 12, 2);
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 23, 1) << 2;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 32, 1) << 3;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 34, 1) << 4;
                    b3 |= ByteHelper.ExtractFrom128(lowBits, highBits, 33, 1) << 5;

                    break;
            }

            return ((int)r3, (int)g3, (int)b3);
        }

    }
