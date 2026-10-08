namespace AtlasWH3.Core.Campaign.Props;

/// <summary>
/// The world matrix BOB writes into bmd records, bit-exact: qttoolutility's QTU::ECTransform turns the layer's euler
/// degrees into a quaternion (on_property_changed) and the quaternion, scale and position into a float matrix
/// (update_transform), with its own vectorised sine (FUN_180590110) and cos(x) taken as sin(|x| + pi/2).
/// Checked against all 94,348 main190 BOB prop records whose entity is directly in a layer (2026-10-05).
/// </summary>
public static class QtuTransform
{
    /// <summary>Row-major 3x3 world rotation-scale (ECTransform's 4x4 rows; the bmd record stores its columns).</summary>
    public static double[] Matrix(float rx, float ry, float rz, float sx, float sy, float sz, float px, float py, float pz)
    {
        var (x, y, z, w) = Quaternion(rx, ry, rz);
        float x2 = x + x, w2 = w + w;
        float zz2 = (z + z) * z, yy2 = (y + y) * y;
        float f13 = w2 * z + y * x2, f19 = y * x2 - w2 * z, f18 = z * x2 - w2 * y;
        float f12 = 1f - x2 * x, zy2 = z * (y + y), f9 = w2 * x + zy2, l148 = zy2 - w2 * x;
        float f17 = w2 * y + z * x2;
        float m11 = f12 - zz2, m00 = (1f - yy2) - zz2, m22 = f12 - yy2;
        // a "-0" position component is +0 in BOB's matrix (layer text "-0"; 2026-10-05: a building_boat at y = -0 gave a
        // -0 rotation element natively, +0 in BOB)
        if (px == 0f) px = 0f;
        if (py == 0f) py = 0f;
        if (pz == 0f) pz = 0f;
        float zx = px * 0f, zy = py * 0f, zz = pz * 0f;
        float[,] r =
        {
            { zx + m00 * sx, zx + f19 * sy, zx + f17 * sz },
            { zy + f13 * sx, zy + sy * m11, zy + sz * l148 },
            { zz + f18 * sx, zz + sy * f9, zz + sz * m22 },
        };
        var m = new double[9];
        for (var k = 0; k < 9; k++) m[k] = r[k / 3, k % 3];
        return m;
    }

    /// <summary>ECTransform::on_property_changed: degrees * pi * (1/180), half angles, (x, y, z, w).</summary>
    public static (float X, float Y, float Z, float W) Quaternion(float rx, float ry, float rz)
    {
        const float pi = 3.14159274f, inv180 = 0.00555555569f;
        var (xl, xh) = SinCos(rx * pi * inv180 * 0.5f);
        var (yl, yh) = SinCos(ry * pi * inv180 * 0.5f);
        var (zl, zh) = SinCos(rz * pi * inv180 * 0.5f);
        float f7 = zl * yl, f8 = zl * yh, f9 = zh * yh, f10 = zh * yl;
        return (f9 * xl - f7 * xh, f8 * xl + f10 * xh, f8 * xh - f10 * xl, f7 * xl + f9 * xh);
    }

    /// <summary>FUN_180590130: (x, 1) below 2^-13, else (sin x, sin(|x| + pi/2)).</summary>
    public static (float Sin, float Cos) SinCos(float x)
    {
        if (MathF.Abs(x) < 1.22070313e-4f) return (x, 1f);
        return (Sin(x), Sin(MathF.Abs(x) + 1.57079637f));
    }

    /// <summary>FUN_180590110 (SSE4.1 path, |x| &lt; 10000): reduction by pi in four parts, odd degree-9 polynomial.</summary>
    public static float Sin(float x)
    {
        var ax = MathF.Abs(x);
        var t = ax * BitConverter.UInt32BitsToSingle(0x3ea2f983) + 12582912f;
        var n = t - 12582912f;
        var r = ax - BitConverter.UInt32BitsToSingle(0x40490000) * n;
        r -= BitConverter.UInt32BitsToSingle(0x3a7da000) * n;
        r -= BitConverter.UInt32BitsToSingle(0x34222000) * n;
        r -= BitConverter.UInt32BitsToSingle(0x2cb4611a) * n;
        var r2 = r * r;
        if ((BitConverter.SingleToUInt32Bits(t) & 1) != 0) r = -r;
        var p = ((BitConverter.UInt32BitsToSingle(0x362edef8) * r2 + BitConverter.UInt32BitsToSingle(0xb94fb7ff)) * r2
                 + BitConverter.UInt32BitsToSingle(0x3c088766)) * r2 + BitConverter.UInt32BitsToSingle(0xbe2aaaa6);
        var s = r + r2 * p * r;
        return BitConverter.SingleToUInt32Bits(x) >> 31 != 0 ? -s : s;
    }
}
