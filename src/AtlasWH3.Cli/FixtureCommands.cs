using System.Text.Json;
using System.Text.Json.Nodes;
using AtlasWH3.Core;
using AtlasWH3.Core.Bob;

/// <summary>
/// fixture-* commands: frozen fixtures (<see cref="KitFixture"/>), a map's sources, BOB outputs and mod packs as they
/// were at one moment, for the parity tests and parity runs. The store defaults to atlaswh3_fixtures next to the kit.
///  - fixture-freeze &lt;name&gt; --maps a,b [--packs p,q] [--replace] [--store dir]: freeze the maps (from --ak) and the
///    packs (file names in the game's data folder); freeze right after a BOB run, so sources and outputs match
///  - fixture-list [--store dir]: the fixtures, their maps, packs and game build
///  - fixture-check [name] [--store dir]: frozen files changed since the freeze (exit 1 if any)
///  - fixture-delete &lt;name&gt; [--store dir]: removes a fixture (its junctions, never the kit behind them)
/// </summary>
static class FixtureCommands
{
    public static readonly HashSet<string> Names = ["fixture-freeze", "fixture-list", "fixture-check", "fixture-delete"];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static int Run(ProjectPaths paths, string command, string[] a)
    {
        try
        {
            var store = Store(paths, a);
            JsonNode result = command switch
            {
                "fixture-freeze" => Freeze(paths, store, a),
                "fixture-list" => new JsonArray(KitFixture.List(store).Select(Describe).ToArray()),
                "fixture-check" => Check(store, Name(a, required: false)),
                "fixture-delete" => Delete(store, Name(a, required: true)!),
                _ => throw new ArgumentException(command),
            };
            Console.WriteLine(result.ToJsonString(Indented));
            return result["ok"]?.GetValue<bool>() == false ? 1 : 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.WriteLine(new JsonObject { ["error"] = ex.Message, ["type"] = ex.GetType().Name }.ToJsonString(Indented));
            return 1;
        }
    }

    public static string Store(ProjectPaths paths, string[] a) => Option(a, "--store") ?? KitFixture.DefaultStore(paths.AssemblyKitRoot);

    /// <summary>A fixture by name, or an error naming the ones there are.</summary>
    public static KitFixture.Fixture Open(string store, string name) =>
        KitFixture.Open(store, name) ?? throw new ArgumentException(
            $"no fixture {name} in {store} (there: {string.Join(", ", KitFixture.List(store).Select(f => f.Manifest.Name).DefaultIfEmpty("none"))}); fixture-freeze makes one");

    private static JsonNode Freeze(ProjectPaths paths, string store, string[] a)
    {
        var name = Name(a, required: true)!;
        var r = KitFixture.Freeze(paths.AssemblyKitRoot, paths.GameDataDir, store, name, List(a, "--maps"), List(a, "--packs"),
            a.Contains("--replace"), m => Console.Error.WriteLine(m));
        var node = Describe(r.Fixture);
        node["linked"] = r.Linked;
        node["copied"] = r.Copied;
        node["gb_copied"] = Math.Round(r.BytesCopied / 1e9, 2);
        node["junctions"] = r.Junctions;
        return node;
    }

    private static JsonNode Check(string store, string? name)
    {
        var fixtures = name is null ? KitFixture.List(store) : [Open(store, name)];
        var ok = true;
        var list = new JsonArray();
        foreach (var f in fixtures)
        {
            var drift = KitFixture.Check(f);
            ok &= drift.Count == 0;
            list.Add(new JsonObject
            {
                ["name"] = f.Manifest.Name, ["intact"] = drift.Count == 0, ["drifted"] = drift.Count,
                ["files"] = new JsonArray(drift.Take(50).Select(d => (JsonNode)d).ToArray()),
            });
        }
        return new JsonObject { ["ok"] = ok, ["store"] = store, ["fixtures"] = list };
    }

    private static JsonNode Delete(string store, string name)
    {
        Open(store, name);
        KitFixture.Delete(store, name);
        return new JsonObject { ["deleted"] = Path.Combine(store, name) };
    }

    private static JsonObject Describe(KitFixture.Fixture f) => new()
    {
        ["name"] = f.Manifest.Name, ["root"] = f.Root, ["kit"] = f.KitRoot, ["created"] = f.Manifest.Created.ToString("u"),
        ["game_build"] = f.Manifest.GameBuild, ["source_kit"] = f.Manifest.SourceKit,
        ["maps"] = new JsonArray(f.Manifest.Maps.Select(m => (JsonNode)m).ToArray()),
        ["packs"] = new JsonArray(f.Manifest.Packs.Select(p => (JsonNode)p).ToArray()),
        ["files"] = f.Manifest.Files.Count, ["gb"] = Math.Round(f.Manifest.Files.Sum(x => x.Size) / 1e9, 2),
        ["sources_newer_than_outputs"] = new JsonArray(f.Manifest.NewerSources.Take(20).Select(s => (JsonNode)s).ToArray()),
    };

    private static string? Name(string[] a, bool required)
    {
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i].StartsWith("--")) { if (a[i] != "--replace") i++; continue; }
            return a[i];
        }
        return required ? throw new ArgumentException("a fixture name is needed") : null;
    }

    private static List<string> List(string[] a, string name) =>
        (Option(a, name) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string? Option(string[] a, string name)
    {
        var i = Array.FindIndex(a, s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }
}
