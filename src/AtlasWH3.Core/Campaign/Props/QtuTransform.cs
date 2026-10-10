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

    /// <summary>
    /// WH3's QTU::ECTransform (qttoolutility on_property_changed / update_transform, read off the 2026-09 kit): the same
    /// quaternion product and matrix as <see cref="Matrix"/>, but radians = degrees × 0.017453294 (one constant); for x
    /// and y, cos = sin(h + π/2) with no abs and no small-angle case (FUN_180584763); for z, |h| &lt; 2^-13 gives
    /// (h, 1 − h²/2) (FUN_180584720); and no "position × 0" terms, so a −0 element stays −0. Bit-exact on 21,725 of the
    /// 21,851 Old World props matched to a single layer entity (the rest pair two entities at one spot).
    /// </summary>
    public static double[] MatrixWh3(float rx, float ry, float rz, float sx, float sy, float sz)
    {
        var (x, y, z, w) = QuaternionWh3(rx, ry, rz);
        float x2 = x + x, w2 = w + w;
        float zz2 = (z + z) * z, yy2 = (y + y) * y;
        float f13 = w2 * z + y * x2, f19 = y * x2 - w2 * z, f18 = z * x2 - w2 * y;
        float f12 = 1f - x2 * x, zy2 = z * (y + y), f9 = w2 * x + zy2, l148 = zy2 - w2 * x;
        float f17 = w2 * y + z * x2;
        float m11 = f12 - zz2, m00 = (1f - yy2) - zz2, m22 = f12 - yy2;
        return [m00 * sx, f19 * sy, f17 * sz, f13 * sx, sy * m11, sz * l148, f18 * sx, sy * f9, sz * m22];
    }

    /// <summary>
    /// WH3's world transform of an entity inside a prefab instance: the parent's and the child's row-major 3x3 multiplied
    /// in float, then QTU::set_decomposed_transform (column lengths as the scale, a trace-method quaternion of the
    /// normalised columns), set_rotation's trip through Euler degrees (quaternion → radians → × 180 × (1/π)), and the
    /// matrix rebuilt from those degrees (<see cref="MatrixWh3"/>). The position is ((Tp + r2·t2) + r1·t1) + r0·t0.
    /// Bit-exact on all 3,003 Old World corruption-crack decals tested (with the CRT's float atan2f / asinf).
    /// </summary>
    public static (float[] R, float X, float Y, float Z, (float X, float Y, float Z, float W) Q) ComposeWh3(float[] rp, float px, float py, float pz, float[] rc, float cx, float cy, float cz)
    {
        var m = new float[9];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                m[i * 3 + j] = rp[i * 3] * rc[j] + rp[i * 3 + 1] * rc[3 + j] + rp[i * 3 + 2] * rc[6 + j];
        var (q, s0, s1, s2) = Decompose(m);
        var (ex, ey, ez) = ToEuler(q);
        static float Deg(float r) => r * 180f * 0.31830987f;
        var r = MatrixWh3(Deg(ex), Deg(ey), Deg(ez), s0, s1, s2).Select(v => (float)v).ToArray();
        var rotation = QuaternionWh3(Deg(ex), Deg(ey), Deg(ez));
        float T(int i, float p) => p + rp[i * 3 + 2] * cz + rp[i * 3 + 1] * cy + rp[i * 3] * cx;
        return (r, T(0, px), T(1, py), T(2, pz), rotation);
    }

    /// <summary>qttoolutility QTU::set_decomposed_transform on a row-major 3x3: quaternion (x, y, z, w) and scale.</summary>
    public static ((float X, float Y, float Z, float W) Q, float S0, float S1, float S2) Decompose(float[] m)
    {
        var r = new float[9];
        var s0 = MathF.Sqrt(m[0] * m[0] + m[3] * m[3] + m[6] * m[6]);
        var i0 = 1f / s0;
        r[0] = m[0] * i0; r[3] = m[3] * i0; r[6] = m[6] * i0;
        var s1 = MathF.Sqrt(m[4] * m[4] + m[1] * m[1] + m[7] * m[7]);
        var i1 = 1f / s1;
        r[4] = m[4] * i1; r[7] = m[7] * i1; r[1] = i1 * m[1];
        var s2 = MathF.Sqrt(m[5] * m[5] + m[2] * m[2] + m[8] * m[8]);
        var i2 = 1f / s2;
        r[2] = m[2] * i2; r[5] = m[5] * i2; r[8] = i2 * m[8];
        var trace = (r[4] + r[0]) + (r[8] + 1f);
        if (trace >= 1f)
        {
            var s = MathF.Sqrt(trace);
            var k = 0.5f / s;
            return (((r[7] - r[5]) * k, (r[2] - r[6]) * k, (r[3] - r[1]) * k, s * 0.5f), s0, s1, s2);
        }
        int[] next = [1, 2, 0];
        var a = 0;
        if (r[4] > r[0]) a = 1;
        if (r[8] > r[a * 4]) a = 2;
        int b = next[a], c = next[b];
        var root = MathF.Sqrt(r[a * 4] - r[b * 4] - r[c * 4] + 1f);
        if (root == 0) root = 9.99999975e-05f;
        var t = 0.5f / root;
        var q = new float[3];
        q[a] = root * 0.5f;
        var w = (r[c * 3 + b] - r[b * 3 + c]) * t;
        q[b] = (r[a * 3 + b] + r[b * 3 + a]) * t;
        q[c] = (r[a * 3 + c] + r[c * 3 + a]) * t;
        return ((q[0], q[1], q[2], w), s0, s1, s2);
    }

    /// <summary>qttoolutility FUN_1801033f0: a quaternion's Euler angles (radians, x, y, z), folded into |x| &lt;= π/2.</summary>
    public static (float X, float Y, float Z) ToEuler((float X, float Y, float Z, float W) q)
    {
        var (x, y, z, w) = q;
        const float halfPi = 1.57079637f, pi = 3.14159274f;
        static float Atan2(float a, float b) => MathF.Atan2(a, b);   // the CRT's atan2f, as BOB
        var d = y * w - x * z;
        var t = d + d;
        float ex, ey, ez;
        if (t > 0.999998987f) { ex = Atan2(x, w) * 2f; ey = halfPi; ez = 0; }
        else if (t < -0.999998987f) { ex = Atan2(x, w) * 2f; ey = -halfPi; ez = 0; }
        else
        {
            float xw = x * w, zy = z * y, xx = x * x, xy = x * y, zw = z * w, zz = z * z;
            var yy2 = (y + y) * y;
            ex = Atan2((xw + xw) + (zy + zy), 1f - ((xx + xx) + yy2));
            ey = MathF.Asin(t);
            ez = Atan2((xy + xy) + (zw + zw), 1f - ((zz + zz) + yy2));
        }
        if (ex > halfPi) { ex -= pi; ey = pi - ey; ez -= pi; }
        else if (ex < -halfPi) { ex += pi; ey = pi - ey; ez += pi; }
        return (ex, ey, ez);
    }

    public static (float X, float Y, float Z, float W) QuaternionWh3(float rx, float ry, float rz)
    {
        const float k = 0.017453294f, halfPi = 1.57079637f;
        var hx = rx * k * 0.5f;
        var hy = ry * k * 0.5f;
        var hz = rz * k * 0.5f;
        float sx = Sin(hx), cx = Sin(hx + halfPi), sy = Sin(hy), cy = Sin(hy + halfPi);
        float sz, cz;
        if (MathF.Abs(hz) < 1.22070313e-4f) { sz = hz; cz = 1f - MathF.Abs(hz) * MathF.Abs(hz) * 0.5f; }
        else { sz = Sin(hz); cz = Sin(MathF.Abs(hz) + halfPi); }
        float a = sz * sy, b = sz * cy, c = cz * cy, d = cz * sy;
        return (c * sx - a * cx, b * sx + d * cx, b * cx - d * sx, c * cx + a * sx);
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
