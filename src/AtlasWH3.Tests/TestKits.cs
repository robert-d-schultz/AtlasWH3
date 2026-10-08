using AtlasWH3.Core;

namespace AtlasWH3.Tests;

/// <summary>
/// Fixed assembly kits for the data tests, independent of the user's AtlasWH3 settings (which may point the default
/// kit at a modded copy, e.g. assembly_kit_190E): vanilla data tests read <see cref="Vanilla"/>, the 190 Expanded
/// tests read <see cref="Expanded"/>. Both live next to each other in the game folder.
/// </summary>
internal static class TestKits
{
    public static string Vanilla => Path.Combine(Defaults.GameFolder, "assembly_kit");
    public static string Expanded => Path.Combine(Defaults.GameFolder, "assembly_kit_190E");

    /// <summary>Default paths with the vanilla kit.</summary>
    public static ProjectPaths VanillaPaths => new() { AssemblyKitRoot = Vanilla };
}
