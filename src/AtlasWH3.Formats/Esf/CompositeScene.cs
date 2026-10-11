using System.Buffers.Binary;
using System.Numerics;

namespace AtlasWH3.Formats.Esf;

/// <summary>
/// A composite scene (.csc): an ESF file (<see cref="EsfTree"/>) whose ROOT holds the scene's elements and their attach
/// tree. Only the static placement is read: each model element's transform at time 0, composed through its attach
/// parents; animation, timing, VFX and sound are not. Grammar after the Asset Editor's CSC editor (RootStructureDetector,
/// CurveDetector, CscElement):
///  - ROOT := header (3, 4, 4, 7, 10 fields for versions 1-5) + U32 n + n × (Ascii label + records, ELEMENT first)
///    + U32 m + m × (U32 parent id (−1 = none) + U32 k + k × (U32 child id, ATTACH_TO_PARAMETERS)) + tail;
///  - ELEMENT := U32 id, Ascii, F32 begin, F32 end, Ascii timing, Ascii anchor, then four channel groups (position x/y/z,
///    rotation x/y/z in radians, uniform scale, weight), then Coord3d base position, Coord3d base rotation, extras;
///  - group := U32 marker + channels; channel := F32 header [+ Ascii, Ascii] + U32 keys + keys × (F32 t, F32 value,
///    Ascii, Coord2d, Coord2d, Ascii). A channel without keys holds the header as its constant value.
/// An element's local matrix (row vectors) is Euler(base rot) · T(base pos) · S(scale) · Euler(rot) · T(pos), Euler =
/// Rx · Ry · Rz; its world is local · parent's local · …
/// </summary>
public sealed class CompositeScene
{
    /// <summary>One element: its model (model elements only) and its local matrix (row-vector convention).</summary>
    public sealed record Element(int Id, string Label, string? Model, Matrix4x4 Local, int ParentId);

    public IReadOnlyList<Element> Elements { get; }

    private readonly Dictionary<int, Element> _byId;

    private CompositeScene(List<Element> elements)
    {
        Elements = elements;
        _byId = elements.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
    }

    private static readonly Dictionary<int, int> HeaderLength = new() { [1] = 3, [2] = 4, [3] = 4, [4] = 7, [5] = 10 };

    private static readonly string[] ModelExtensions = [".wsmodel", ".rigid_model_v2", ".variantmeshdefinition"];

    /// <summary>The element's matrix in scene space: its local matrix through its attach parents.</summary>
    public Matrix4x4 World(Element e)
    {
        var m = e.Local;
        var seen = new HashSet<int> { e.Id };
        for (var p = e.ParentId; p >= 0 && _byId.TryGetValue(p, out var parent) && seen.Add(p); p = parent.ParentId)
            m *= parent.Local;
        return m;
    }

    /// <summary>Every model element with its scene-space matrix (row-vector convention).</summary>
    public IEnumerable<(string Model, Matrix4x4 World)> Models() =>
        Elements.Where(e => e.Model is not null).Select(e => (e.Model!, World(e)));

    public static CompositeScene Read(byte[] bytes)
    {
        var tree = EsfTree.Read(bytes);
        var fields = tree.Root.Children;
        if (!HeaderLength.TryGetValue(tree.Root.Version, out var i)) throw new InvalidDataException($"ROOT v{tree.Root.Version} is not a known .csc layout");
        string? Ascii(EsfNode n) => n is EsfValue { Type: 0x0f } v ? tree.Ascii.GetValueOrDefault((uint)v.Int) : null;

        var elements = new List<Element>();
        var count = UInt(fields[i++]);
        for (var g = 0; g < count; g++)
        {
            var label = Ascii(fields[i++]) ?? throw new InvalidDataException("element group without a label");
            var records = new List<EsfRecord>();
            while (i < fields.Count && fields[i] is EsfRecord r) { records.Add(r); i++; }
            if (records.FirstOrDefault(r => r.Name == "ELEMENT") is not { } element) continue;
            var model = records.Where(r => r.Name != "ELEMENT").SelectMany(r => r.Children).Select(Ascii)
                .FirstOrDefault(s => s is not null && ModelExtensions.Any(x => s.EndsWith(x, StringComparison.OrdinalIgnoreCase)));
            var ef = element.Children;
            elements.Add(new Element((int)UInt(ef[0]), label, model?.Replace('\\', '/'), LocalMatrix(ef), -1));
        }

        var parents = new Dictionary<int, int>();
        var groups = UInt(fields[i++]);
        for (var g = 0; g < groups; g++)
        {
            var parent = (int)UInt(fields[i++]);
            var children = UInt(fields[i++]);
            for (var c = 0; c < children; c++)
            {
                parents[(int)UInt(fields[i])] = parent;
                i += 2;   // child id, ATTACH_TO_PARAMETERS
            }
        }
        return new CompositeScene(elements.Select(e => e with { ParentId = parents.GetValueOrDefault(e.Id, -1) }).ToList());
    }

    /// <summary>The ELEMENT's local matrix at time 0; identity when its layout is not the channel one (old versions).</summary>
    private static Matrix4x4 LocalMatrix(List<EsfNode> f)
    {
        var p = 6;
        var values = new List<float>();
        foreach (var channels in new[] { 3, 3, 1, 1 })
        {
            if (p >= f.Count || !IsUInt(f[p])) return Matrix4x4.Identity;
            p++;   // marker
            for (var c = 0; c < channels; c++)
            {
                if (p >= f.Count || !IsFloat(f[p])) return Matrix4x4.Identity;
                var header = Float(f[p++]);
                if (p + 1 < f.Count && f[p] is EsfValue { Type: 0x0f } && f[p + 1] is EsfValue { Type: 0x0f }) p += 2;
                if (p >= f.Count || !IsUInt(f[p])) return Matrix4x4.Identity;
                var keys = UInt(f[p++]);
                var value = header;
                for (var k = 0; k < keys; k++)
                {
                    if (k == 0) value = Float(f[p + 1]);
                    p += 6;
                }
                values.Add(value);
            }
        }
        if (p + 1 >= f.Count || f[p] is not EsfValue { Type: 0x0d } basePos || f[p + 1] is not EsfValue { Type: 0x0d } baseRot)
            return Matrix4x4.Identity;
        static Matrix4x4 Euler(Vector3 r) => Matrix4x4.CreateRotationX(r.X) * Matrix4x4.CreateRotationY(r.Y) * Matrix4x4.CreateRotationZ(r.Z);
        var channel = Matrix4x4.CreateScale(values[6]) * Euler(new Vector3(values[3], values[4], values[5]))
                      * Matrix4x4.CreateTranslation(values[0], values[1], values[2]);
        return Euler(Vec3(baseRot)) * Matrix4x4.CreateTranslation(Vec3(basePos)) * channel;
    }

    private static Vector3 Vec3(EsfValue v) => new(BinaryPrimitives.ReadSingleLittleEndian(v.Raw),
        BinaryPrimitives.ReadSingleLittleEndian(v.Raw.AsSpan(4)), BinaryPrimitives.ReadSingleLittleEndian(v.Raw.AsSpan(8)));

    private static bool IsUInt(EsfNode n) => n is EsfValue { Type: 0x08 or 0x14 or 0x15 or 0x16 or 0x17 or 0x18 };
    private static bool IsFloat(EsfNode n) => n is EsfValue { Type: 0x0a or 0x1d };

    private static uint UInt(EsfNode n) => n is EsfValue v && IsUInt(v)
        ? v.Type switch { 0x14 => 0u, 0x15 => 1u, _ => (uint)v.Int }
        : throw new InvalidDataException($"expected a U32, found {Describe(n)}");

    private static float Float(EsfNode n) => n is EsfValue v && IsFloat(v)
        ? v.Type == 0x1d ? 0f : BinaryPrimitives.ReadSingleLittleEndian(v.Raw)
        : throw new InvalidDataException($"expected an F32, found {Describe(n)}");

    private static string Describe(EsfNode n) => n switch { EsfRecord r => "record " + r.Name, EsfValue v => $"type {v.Type:x2}", _ => n.GetType().Name };
}
