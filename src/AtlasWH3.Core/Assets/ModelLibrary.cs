using System.Collections.Concurrent;
using AtlasWH3.Formats.Dds;
using AtlasWH3.Formats.Models;
using AtlasWH3.Formats.Packs;

namespace AtlasWH3.Core.Assets;

/// <summary>One drawable mesh: geometry plus what its material says about colour and alpha.</summary>
public sealed record RenderMesh(float[] Positions, float[] Normals, float[] Uvs, ushort[] Indices,
                                string? BaseColour, bool AlphaTest, string? Material, string Shader, float TerrainOffset = 0);

/// <summary>
/// A model as an entity's model_path names it (.wsmodel or bare .rigid_model_v2), resolved through the
/// <see cref="AssetSource"/>: LODs of meshes with their colour texture. <see cref="Problems"/> lists what could not be
/// resolved (missing geometry, material or texture), so callers can show why a model looks wrong.
/// </summary>
public sealed record RenderModel(string Path, string Geometry, IReadOnlyList<IReadOnlyList<RenderMesh>> Lods, float[] Bounds,
                                 IReadOnlyList<float> LodDistances, IReadOnlyList<string> Problems)
{
    public int Vertices(int lod = 0) => Lods.ElementAtOrDefault(lod)?.Sum(m => m.Positions.Length / 3) ?? 0;
    public int Triangles(int lod = 0) => Lods.ElementAtOrDefault(lod)?.Sum(m => m.Indices.Length / 3) ?? 0;
}

/// <summary>
/// Loads and caches models and textures for previews and the viewport. Thread-safe; each path is read once.
/// .wsmodel: geometry from &lt;geometry&gt;, and per mesh (part) and LOD the .material → shader and base colour texture.
/// Bare .rigid_model_v2: the texture list in each mesh's own material block.
/// </summary>
public sealed class ModelLibrary(AssetSource source)
{
    private readonly ConcurrentDictionary<string, Lazy<RenderModel?>> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<MaterialFile?>> _materials = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string, int), Lazy<DdsTexture.Image?>> _textures = new();
    private readonly ConcurrentDictionary<string, Lazy<float[]?>> _bounds = new(StringComparer.OrdinalIgnoreCase);

    public AssetSource Source { get; } = source;

    public static ModelLibrary ForGame(ProjectPaths paths, IEnumerable<string>? looseRoots = null, IEnumerable<string>? modPacks = null) =>
        new(AssetSource.ForGame(paths.GameDataDir, looseRoots, modPacks));

    /// <summary>The model, or null when its file (or geometry) cannot be read; see <see cref="TryLoad"/> for why.</summary>
    public RenderModel? Load(string path) => _models.GetOrAdd(AssetSource.Normalize(path), p => new Lazy<RenderModel?>(() =>
    {
        try { return Build(p); }
        catch (Exception) { return null; }
    })).Value;

    /// <summary>Like <see cref="Load"/> but reports the failure.</summary>
    public (RenderModel? Model, string? Error) TryLoad(string path)
    {
        try { return (Build(AssetSource.Normalize(path)), null); }
        catch (Exception ex) { return (null, ex.Message); }
    }

    /// <summary>LOD 0 bounds (min xyz, max xyz) in model space; for models that do not load, the bounds in their mesh
    /// headers. Null when nothing is readable.</summary>
    public float[]? Bounds(string path) => _bounds.GetOrAdd(AssetSource.Normalize(path), p => new Lazy<float[]?>(() =>
    {
        if (Load(p) is { } m) return m.Bounds;
        try
        {
            var geometry = p;
            if (p.EndsWith(".wsmodel", StringComparison.OrdinalIgnoreCase) && Source.TryRead(p) is { } ws)
                geometry = WsModelFile.Parse(ws).Geometry;
            return Source.TryRead(geometry) is { } bytes ? RigidModel.HeaderBounds(bytes) : null;
        }
        catch (Exception) { return null; }
    })).Value;

    public MaterialFile? Material(string path) => _materials.GetOrAdd(AssetSource.Normalize(path), p => new Lazy<MaterialFile?>(() =>
    {
        try { return Source.TryRead(p) is { } b ? MaterialFile.Parse(b) : null; }
        catch (Exception) { return null; }
    })).Value;

    /// <summary>A texture decoded at the largest mip no bigger than <paramref name="maxSize"/>.</summary>
    public DdsTexture.Image? Texture(string path, int maxSize = 512) => _textures.GetOrAdd((AssetSource.Normalize(path).ToLowerInvariant(), maxSize),
        k => new Lazy<DdsTexture.Image?>(() =>
        {
            try { return Source.TryRead(k.Item1) is { } b ? DdsTexture.DecodeMip(b, k.Item2) : null; }
            catch (Exception) { return null; }
        })).Value;

    private RenderModel Build(string path)
    {
        var problems = new List<string>();
        var bytes = Source.TryRead(path) ?? throw new FileNotFoundException($"{path} not found in any pack or loose folder");
        WsModelFile? ws = null;
        var geometry = path;
        if (path.EndsWith(".wsmodel", StringComparison.OrdinalIgnoreCase))
        {
            ws = WsModelFile.Parse(bytes);
            geometry = AssetSource.Normalize(ws.Geometry);
            bytes = Source.TryRead(geometry) ?? throw new FileNotFoundException($"geometry {geometry} of {path} not found");
        }
        else if (!path.EndsWith(".rigid_model_v2", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"{Path.GetExtension(path)} models are not supported yet");

        var rmv2 = RigidModel.Read(bytes);
        var lods = new List<IReadOnlyList<RenderMesh>>();
        for (var l = 0; l < rmv2.Lods.Count; l++)
        {
            var meshes = new List<RenderMesh>();
            for (var part = 0; part < rmv2.Lods[l].Meshes.Count; part++)
            {
                var m = rmv2.Lods[l].Meshes[part];
                if (m.VertexCount == 0 && m.MaterialPath is { Length: > 0 } stub)
                {
                    // Decal stub: the game builds the quad; draw the header box's x/z extent with <path>_base_colour.
                    meshes.Add(DecalQuad(m, stub));
                    continue;
                }
                string? colour, materialPath = null, shader = m.Shader;
                bool alpha;
                float terrainOffset = 0;
                if (ws is not null)
                {
                    materialPath = ws.MaterialFor(part, l);
                    var mat = materialPath is null ? null : Material(materialPath);
                    if (materialPath is not null && mat is null && l == 0) problems.Add($"material {materialPath} not found");
                    colour = mat?.BaseColour ?? m.Texture(RigidModel.TexBaseColour) ?? m.Texture(RigidModel.TexDiffuse);
                    alpha = mat?.AlphaTest ?? m.AlphaMode == 1;
                    shader = mat?.Shader ?? shader;
                    terrainOffset = mat?.TerrainOffset ?? 0;
                }
                else
                {
                    colour = m.Texture(RigidModel.TexBaseColour) ?? m.Texture(RigidModel.TexDiffuse);
                    alpha = m.AlphaMode == 1;
                }
                if (l == 0 && colour is not null && !Source.Exists(colour)) problems.Add($"texture {colour} not found");
                meshes.Add(new RenderMesh(m.Positions, m.Normals, m.Uvs, m.Indices, colour, alpha, materialPath, shader, terrainOffset));
            }
            lods.Add(meshes);
        }
        var bounds = rmv2.Bounds;
        if (bounds.All(v => v == 0)) bounds = RigidModel.HeaderBounds(bytes) ?? bounds; // decal stubs: no vertices
        return new RenderModel(path, geometry, lods, bounds, rmv2.Lods.Select(l => l.Distance).ToList(), problems);
    }

    private RenderMesh DecalQuad(RigidModel.Mesh m, string stub)
    {
        float x0 = m.BoundsMin[0], x1 = m.BoundsMax[0], z0 = m.BoundsMin[2], z1 = m.BoundsMax[2];
        var y = (m.BoundsMin[1] + m.BoundsMax[1]) / 2;
        if (x0 >= x1 || z0 >= z1) (x0, x1, z0, z1) = (-0.5f, 0.5f, -0.5f, 0.5f);
        var texture = AssetSource.Normalize(stub) + "_base_colour.dds";
        if (!Source.Exists(texture)) texture = AssetSource.Normalize(stub) + "_diffuse.dds";
        return new RenderMesh(
            [x0, y, z0, x1, y, z0, x1, y, z1, x0, y, z1], [0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0], [0, 1, 1, 1, 1, 0, 0, 0], [0, 1, 2, 0, 2, 3],
            Source.Exists(texture) ? texture : null, AlphaTest: true, Material: stub, Shader: "decal");
    }
}
