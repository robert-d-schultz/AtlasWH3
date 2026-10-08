using AtlasWH3.Core;

namespace AtlasWH3.Tests;

/// <summary>
/// Fixed game data for the data tests, independent of the user's AtlasWH3 settings.
///  - WH3: <see cref="Wh3Game"/> (the Steam install) and its kit.
///  - Atlas3K's inherited tests still read Three Kingdoms data (<see cref="Vanilla"/>, <see cref="Expanded"/>); on a
///    machine without a 3K install they skip, until Phase 1 moves them to WH3 fixtures.
/// </summary>
internal static class TestKits
{
    public static string Wh3Game => GameSetup.FindGameFolder() ?? @"C:\Program Files (x86)\Steam\steamapps\common\Total War WARHAMMER III";
    public static string Wh3GameData => Path.Combine(Wh3Game, "data");
    public static string Wh3Kit => Path.Combine(Wh3Game, "assembly_kit");

    public const string ThreeKingdoms = @"C:\Program Files (x86)\Steam\steamapps\common\Total War THREE KINGDOMS";
    public static string Vanilla => Path.Combine(ThreeKingdoms, "assembly_kit");
    public static string Expanded => Path.Combine(ThreeKingdoms, "assembly_kit_190E");

    /// <summary>Default paths with the 3K vanilla kit and data.</summary>
    public static ProjectPaths VanillaPaths => new()
    {
        AssemblyKitRoot = Vanilla, GameDataDir = Path.Combine(ThreeKingdoms, "data"), MapName = "3k_dlc07_main_map",
        VanillaRoot = Path.Combine(ThreeKingdoms, "atlas_vanilla"),
    };
}
