using AtlasWH3.Core;
using AtlasWH3.Core.Campaign;
using AtlasWH3.Core.Campaign.GlobalMesh;

namespace AtlasWH3.Tests;

/// <summary>The global_mesh step's BOB path against BOB's "Global Mesh" output, each with the lf maps and tile list BOB
/// read (it reads them through the game's file system, not the kit's working_data):
///  - main190: output/bob_runs/frida_gmesh2_main190_bob (2026-10-05, inputs from the main190 pack);
///  - vanilla: output/bob_runs/gmesh_vanilla_bob (2026-10-05, inputs from CA's terrain.pack; research/gmesh/find_pack_inputs.py).
/// Each needs its assembly kit and saved run and skips without them.</summary>
public class BobGlobalMeshTests
{
    private static readonly ProjectPaths Main190 = new()
    {
        AssemblyKitRoot = TestKits.Expanded,
        MapName = "3k_190e_expanded_map",
    };
    private static readonly ProjectPaths Vanilla = new() { MapName = "3k_dlc07_main_map", AssemblyKitRoot = TestKits.Vanilla };

    /// <summary>Bytes BOB leaves uninitialised: 0x148..0x14B on every mesh (material block), 0xA5..0xA7 on sea meshes
    /// (LOD padding); they differ between BOB runs.</summary>
    private static bool Uninitialised(string file, int i) =>
        i is >= 0x148 and <= 0x14B || (Path.GetFileName(file).StartsWith("sea_", StringComparison.Ordinal) && i is >= 0xA5 and <= 0xA7);

    [Fact]
    public void Main190_GlobalMeshes_MatchBobApartFromUninitialisedBytes() => MatchBob(Main190, "frida_gmesh2_main190_bob", 494);

    [Fact]
    public void Vanilla_GlobalMeshes_MatchBobApartFromUninitialisedBytes() => MatchBob(Vanilla, "gmesh_vanilla_bob", 465);

    private static void MatchBob(ProjectPaths paths, string run, int files)
    {
        var bobRun = Path.Combine(paths.OutputRoot, "bob_runs", run);
        var inputs = Path.Combine(bobRun, "inputs");
        if (!Directory.Exists(inputs) || !Directory.Exists(Path.Combine(bobRun, "global_meshes")) || !Directory.Exists(paths.AkWorkingDir)) return;
        if (Environment.GetEnvironmentVariable("ATLASWH3_GMESH_BOB_HEIGHT") is not null) return;   // research override set
        var outDir = Path.Combine(Path.GetTempPath(), "atlaswh3_bobgmesh_" + Guid.NewGuid().ToString("N"));
        try
        {
            var ctx = new CampaignBuildContext(paths, outDir);
            Directory.CreateDirectory(ctx.TerrainOutDir);
            foreach (var f in Directory.GetFiles(inputs)) File.Copy(f, ctx.OutFile(Path.GetFileName(f)));
            var result = new GlobalMeshStep().Run(ctx);
            Assert.Contains(result.Notes, n => n.StartsWith("BOB height query", StringComparison.Ordinal));
            var reference = Directory.GetFiles(Path.Combine(bobRun, "global_meshes"));
            Assert.Equal(files, reference.Length);
            Assert.Equal(files, Directory.GetFiles(ctx.OutFile("global_meshes")).Length);
            foreach (var file in reference)
            {
                var bob = File.ReadAllBytes(file);
                var ours = File.ReadAllBytes(ctx.OutFile("global_meshes", Path.GetFileName(file)));
                Assert.True(bob.Length == ours.Length, Path.GetFileName(file));
                for (var i = 0; i < bob.Length; i++)
                    if (bob[i] != ours[i] && !Uninitialised(file, i))
                        Assert.Fail($"{Path.GetFileName(file)} differs at 0x{i:X}");
            }
        }
        finally { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); }
    }

    /// <summary>MESH_SPLITTER: under 65,000 vertices a mesh is kept as is; over it, triangles go in order into chunks
    /// that close after the triangle reaching 65,000 vertices, each renumbered by first use; the RMV2 keeps them as
    /// meshes of one LOD and reads back the same.</summary>
    [Fact]
    public void MeshSplitter_SplitsAfterTheTriangleReaching65000Vertices()
    {
        var positions = new List<(float X, float Y, float Z)>();
        var indices = new List<int>();
        for (var t = 0; t < 30000; t++)          // 30,000 separate triangles: 90,000 vertices
            for (var k = 0; k < 3; k++)
            {
                indices.Add(positions.Count);
                positions.Add((t, k, -t));
            }
        var small = GlobalMeshBuilder.SplitMesh(positions.Take(300).ToList(), indices.Take(300).ToList()).ToList();
        Assert.Single(small);
        var chunks = GlobalMeshBuilder.SplitMesh(positions, indices).ToList();
        Assert.Equal(2, chunks.Count);
        Assert.Equal(65001, chunks[0].Positions.Count);   // 21,667 triangles: the first to reach 65,000 closes the chunk
        Assert.Equal(90000 - 65001, chunks[1].Positions.Count);
        Assert.Equal(Enumerable.Range(0, 3), chunks[1].Indices.Take(3));
        Assert.Equal(positions[65001], chunks[1].Positions[0]);

        var model = AtlasWH3.Formats.Models.RigidModelV2.NewTerrainTile(false);
        foreach (var (p, i) in chunks)
        {
            var part = model.Vertices.Length == 0 ? model : AtlasWH3.Formats.Models.RigidModelV2.NewTerrainTile(false);
            part.Vertices = AtlasWH3.Formats.Models.RigidModelV2.PackPositions(p);
            part.Indices = [.. i.Select(v => (ushort)v)];
            part.ComputeBounds();
            if (part != model) model.MoreMeshes.Add(part);
        }
        var bytes = model.ToBytes();
        var back = AtlasWH3.Formats.Models.RigidModelV2.Read(bytes);
        Assert.Single(back.MoreMeshes);
        Assert.Equal(model.MoreMeshes[0].Vertices, back.MoreMeshes[0].Vertices);
        Assert.Equal(bytes, back.ToBytes());
    }
}
