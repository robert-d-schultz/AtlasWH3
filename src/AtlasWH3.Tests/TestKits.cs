using System.Collections.Concurrent;
using AtlasWH3.Core;
using AtlasWH3.Core.Bob;

namespace AtlasWH3.Tests;

/// <summary>
/// Fixed game data for the data tests, independent of the user's AtlasWH3 settings.
///  - <see cref="Wh3Game"/>: the Steam install, its data folder (vanilla packs) and its kit.
///  - The fixture maps (<see cref="Iee"/>, <see cref="OldWorld"/>) come from the frozen fixtures
///    (<see cref="KitFixture"/>, <c>fixture-freeze</c>) when there are any, so the parity tests keep comparing the same
///    sources with the same BOB outputs while the user goes on editing the maps; else from the live kit and data
///    folder. A fixture whose files changed since the freeze fails the tests that use it, naming the files.
/// </summary>
internal static class TestKits
{
    public const string Iee = "cr_combi_expanded_map_1", OldWorld = "cr_oldworld_map_1";
    public const string IeePack = "!cr_immortal_empires_expanded.pack", OldWorldPack = "!cr_oldworld_campaign.pack";

    public static string Wh3Game => GameSetup.FindGameFolder() ?? @"C:\Program Files (x86)\Steam\steamapps\common\Total War WARHAMMER III";
    public static string Wh3GameData => Path.Combine(Wh3Game, "data");
    /// <summary>The live kit (DB, prefabs, Terry configuration…); a fixture map's folders: <see cref="Kit"/>.</summary>
    public static string Wh3Kit => Path.Combine(Wh3Game, "assembly_kit");
    public static string FixtureStore => KitFixture.DefaultStore(Wh3Kit);

    private static readonly Lazy<IReadOnlyList<KitFixture.Fixture>> Fixtures = new(() => KitFixture.List(FixtureStore));
    private static readonly ConcurrentDictionary<string, Lazy<bool>> Checked = new();

    /// <summary>The frozen fixture holding <paramref name="map"/>, checked once per run; null when there is none.</summary>
    public static KitFixture.Fixture? Fixture(string map)
    {
        var fixture = Fixtures.Value.FirstOrDefault(f => f.HasMap(map));
        if (fixture is null) return null;
        _ = Checked.GetOrAdd(fixture.Root, _ => new Lazy<bool>(() =>
        {
            var drift = KitFixture.Check(fixture);
            if (drift.Count > 0)
                throw new InvalidOperationException($"fixture {fixture.Manifest.Name} changed since it was frozen ({drift.Count} files: " +
                    $"{string.Join("; ", drift.Take(5))}); freeze it again: fixture-freeze {fixture.Manifest.Name} --replace …");
            return true;
        })).Value;
        return fixture;
    }

    /// <summary>The kit to read <paramref name="map"/> from: its frozen fixture's, else the live kit.</summary>
    public static string Kit(string map) => Fixture(map)?.KitRoot ?? Wh3Kit;

    /// <summary>A mod pack: the frozen copy of a fixture that has it, else the data folder's.</summary>
    public static string Pack(string name) =>
        Fixtures.Value.Where(f => f.Pack(name) is not null).Select(f => Fixture(f.Manifest.Maps[0])!.Pack(name)).FirstOrDefault()
        ?? Path.Combine(Wh3GameData, name);

    /// <summary>A map's working_data\terrain\campaigns output (BOB's), frozen or live.</summary>
    public static string Built(string map, params string[] path) => Path.Combine([Kit(map), "working_data", "terrain", "campaigns", map, .. path]);

    /// <summary>Paths for a fixture map: its kit, the game's data folder, and the given mod packs.</summary>
    public static ProjectPaths Paths(string map, params string[] packs) => new()
    {
        MapName = map, AssemblyKitRoot = Kit(map), GameDataDir = Wh3GameData, ModPacks = packs.Select(Pack).ToList(),
    };
}
