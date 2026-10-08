using System.Globalization;
using System.Xml.Linq;

namespace AtlasWH3.Formats.Terry;

/// <summary>
/// An ECTransform: position, Euler rotation in degrees and scale. Rotation follows the convention the build uses (and
/// global_props.bin matches byte for byte): Blender's XYZ Euler, R = Rz·Ry·Rx, so world = p + R·diag(s)·local.
/// </summary>
public readonly record struct TerryTransform(double[] Position, double[] Rotation, double[] Scale)
{
    public static TerryTransform Identity => new([0, 0, 0], [0, 0, 0], [1, 1, 1]);

    public static TerryTransform Read(XElement? ecTransform) => new(
        LayerDocument.ReadVector((string?)ecTransform?.Attribute("position") ?? "0 0 0"),
        LayerDocument.ReadVector((string?)ecTransform?.Attribute("rotation") ?? "0 0 0"),
        LayerDocument.ReadVector((string?)ecTransform?.Attribute("scale") ?? "1 1 1"));

    /// <summary>Writes position, rotation and scale into an ECTransform element (other attributes kept).</summary>
    public void WriteTo(XElement ecTransform)
    {
        ecTransform.SetAttributeValue("position", Join(Position));
        ecTransform.SetAttributeValue("rotation", Join(Rotation));
        ecTransform.SetAttributeValue("scale", Join(Scale));
    }

    private static string Join(double[] v) => string.Join(' ', v.Select(x => LayerWriter.F(Math.Abs(x) < 1e-9 ? 0 : x)));

    /// <summary>Rotation matrix (row-major 3×3), no scale.</summary>
    public static double[] RotationMatrix(double[] rotDeg)
    {
        const double rad = Math.PI / 180;
        double ci = Math.Cos(rotDeg[0] * rad), si = Math.Sin(rotDeg[0] * rad);
        double cj = Math.Cos(rotDeg[1] * rad), sj = Math.Sin(rotDeg[1] * rad);
        double ch = Math.Cos(rotDeg[2] * rad), sh = Math.Sin(rotDeg[2] * rad);
        double cc = ci * ch, cs = ci * sh, sc = si * ch, ss = si * sh;
        // Blender eul_to_mat3 gives columns; stored here row-major.
        return
        [
            cj * ch, sj * sc - cs, sj * cc + ss,
            cj * sh, sj * ss + cc, sj * cs - sc,
            -sj, cj * si, cj * ci,
        ];
    }

    /// <summary>World = p + R·diag(s)·local, as a row-major 3×3 (R·S) plus translation.</summary>
    public double[] Matrix()
    {
        var r = RotationMatrix(Rotation);
        var m = new double[9];
        for (var row = 0; row < 3; row++)
            for (var c = 0; c < 3; c++)
                m[row * 3 + c] = r[row * 3 + c] * Scale[c];
        return m;
    }

    public (double X, double Y, double Z) Apply(double x, double y, double z)
    {
        var m = Matrix();
        return (Position[0] + m[0] * x + m[1] * y + m[2] * z,
                Position[1] + m[3] * x + m[4] * y + m[5] * z,
                Position[2] + m[6] * x + m[7] * y + m[8] * z);
    }

    /// <summary>
    /// This transform applied after <paramref name="child"/> (child space → this space → world): the transform a
    /// prefab's inner entity gets when the prefab instance has this transform. Yaw-only pairs stay exact (angles add);
    /// otherwise the product matrix is decomposed back to XYZ Euler and per-axis scale (exact without shear, i.e.
    /// uniform instance scale or aligned axes).
    /// </summary>
    public TerryTransform Compose(TerryTransform child)
    {
        var (px, py, pz) = Apply(child.Position[0], child.Position[1], child.Position[2]);
        bool YawOnly(double[] r) => Math.Abs(r[0]) < 1e-9 && Math.Abs(r[2]) < 1e-9;
        if (YawOnly(Rotation) && YawOnly(child.Rotation) && Math.Abs(Scale[0] - Scale[2]) < 1e-9)
            return new TerryTransform([px, py, pz], [0, Wrap(Rotation[1] + child.Rotation[1]), 0],
                [Scale[0] * child.Scale[0], Scale[1] * child.Scale[1], Scale[2] * child.Scale[2]]);

        var a = Matrix();
        var b = child.Matrix();
        var m = new double[9];
        for (var row = 0; row < 3; row++)
            for (var c = 0; c < 3; c++)
                m[row * 3 + c] = a[row * 3] * b[c] + a[row * 3 + 1] * b[3 + c] + a[row * 3 + 2] * b[6 + c];
        var (rot, scale) = Decompose(m);
        return new TerryTransform([px, py, pz], rot, scale);
    }

    /// <summary>Splits R·S into XYZ Euler degrees and per-axis scale (Blender mat3_normalized_to_eul, the solution
    /// with the smaller angle sum).</summary>
    public static (double[] Rotation, double[] Scale) Decompose(double[] m)
    {
        var s = new double[3];
        for (var c = 0; c < 3; c++) s[c] = Math.Sqrt(m[c] * m[c] + m[3 + c] * m[3 + c] + m[6 + c] * m[6 + c]);
        // mat[col][row] in Blender's terms
        double M(int col, int row) => m[row * 3 + col] / (s[col] == 0 ? 1 : s[col]);
        var cy = Math.Sqrt(M(0, 0) * M(0, 0) + M(0, 1) * M(0, 1));
        double[] e1, e2;
        if (cy > 16 * 1.1920929e-7)
        {
            e1 = [Math.Atan2(M(1, 2), M(2, 2)), Math.Atan2(-M(0, 2), cy), Math.Atan2(M(0, 1), M(0, 0))];
            e2 = [Math.Atan2(-M(1, 2), -M(2, 2)), Math.Atan2(-M(0, 2), -cy), Math.Atan2(-M(0, 1), -M(0, 0))];
        }
        else
        {
            e1 = [Math.Atan2(-M(2, 1), M(1, 1)), Math.Atan2(-M(0, 2), cy), 0];
            e2 = e1;
        }
        var e = Math.Abs(e1[0]) + Math.Abs(e1[1]) + Math.Abs(e1[2]) > Math.Abs(e2[0]) + Math.Abs(e2[1]) + Math.Abs(e2[2]) ? e2 : e1;
        return (e.Select(x => x * 180 / Math.PI).ToArray(), s);
    }

    /// <summary>An angle in (-180, 180].</summary>
    public static double Wrap(double deg)
    {
        deg %= 360;
        if (deg > 180) deg -= 360;
        if (deg <= -180) deg += 360;
        return deg;
    }

    public override string ToString() =>
        string.Join(" | ", new[] { Position, Rotation, Scale }.Select(v => string.Join(' ', v.Select(x => x.ToString("0.###", CultureInfo.InvariantCulture)))));
}
