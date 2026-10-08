namespace AtlasWH3.Core.Bob;

/// <summary>
/// WH3's BOB campaign actions (short names from bob_terrain / bob_texture, ACTION_INTERFACE::get_name(false)), what
/// each consumes and what it writes, and the AtlasWH3 step it belongs to. "Terry" actions consume the map's .terry.
/// <see cref="Action.Default"/> marks the actions a silent BOB run selects for a .terry; the others exist only when
/// picked in BOB's GUI (docs/bob_wh3.md).
/// </summary>
public static class BobActions
{
    public sealed record Action(string Name, string Step, string Processor, ActionInput Input, string Writes, bool Default = false);
    public enum ActionInput { Terry, LookupBitmap, NormalTexture }

    public static readonly IReadOnlyList<Action> All =
    [
        new("Campaign Heightmap", "heightmaps", "Terrain", ActionInput.Terry, "full_height_map.dds, full_logic_map.compressed_map"),
        new("Campaign Shroud Heights", "heightmaps", "Terrain", ActionInput.Terry, "shroud_heights.dds"),
        new("Tilemap", "tile_list", "Terrain", ActionInput.Terry, "tile_list.bin, tile_mask.dds"),
        new("Campaign Trees", "trees", "Terrain", ActionInput.Terry, "campaign_maps\\<map>\\display\\trees\\trees.campaign_tree_list"),
        new("Global Tilemap", "global_map", "Terrain", ActionInput.Terry, "global_map\\tile_list.bin, texture_arrays.xml"),
        new("Campaign Global Blendmap", "global_map", "Terrain", ActionInput.Terry, "global_map\\global_blend.dds"),
        new("Color Overlay", "masks", "Terrain", ActionInput.Terry, "colour_overlay.dds", true),
        new("Color Overlay (Sea)", "masks", "Terrain", ActionInput.Terry, "lf_sea_colour.dds", true),
        new("Corruption Mask", "masks", "Terrain", ActionInput.Terry, "corruption_mask.dds", true),
        new("Snow Mask", "masks", "Terrain", ActionInput.Terry, "snow_mask.dds", true),
        new("Event Area Mask", "masks", "Terrain", ActionInput.Terry, "event_area_mask.dds", true),
        new("Patch Visibility Mask", "masks", "Terrain", ActionInput.Terry, "patch_mask.dds", true),
        new("Terry file", "global_props", "Terrain", ActionInput.Terry,
            "global_props.bin, global_props_sound.bin, models\\river_*, environment_collection.xml, event_tiles, event_trees", true),
        new("Devastation pieces", "devastation_pieces", "Terrain", ActionInput.Terry, "pieces\\event_*", true),
        new("Generate Camera Height Map", "camera_heightmap", "Terrain", ActionInput.Terry, "camera_heightmap.png (crashes on the user's maps)"),
        new("Convert lookup texture", "lookup", "Texture", ActionInput.LookupBitmap, "*_lookup.tga / .dds, _minimap.tga"),
    ];

    /// <summary>What one silent BOB run of a campaign .terry does.</summary>
    public static IReadOnlyList<Action> DefaultGroup { get; } = All.Where(a => a.Default).ToList();

    /// <summary>The steps the default group makes (all of each, except that "masks" lacks tile_mask.dds and lf_normal.dds).</summary>
    public static IReadOnlySet<string> DefaultGroupSteps { get; } = DefaultGroup.Select(a => a.Step).ToHashSet();

    public static Action Find(string name) =>
        All.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"unknown BOB action '{name}'. Actions: {string.Join(", ", All.Select(a => a.Name))}");

    /// <summary>The BOB actions that make one AtlasWH3 step's files.</summary>
    public static IReadOnlyList<Action> ForStep(string step) => All.Where(a => a.Step == step).ToList();

    /// <summary>The .terry consumer of a campaign map, in BOB's notation.</summary>
    public static string TerryConsumer(string map) => BobRunner.KitPath("raw", "terrain", "campaigns", map, map + ".terry");

    /// <summary>A silent run of the default group on <paramref name="maps"/>.</summary>
    public static BobRunner.Request DefaultRun(IReadOnlyList<string> maps, TimeSpan? timeout = null) => new()
    {
        Actions = DefaultGroup.Select(a => a.Name).ToList(),
        Consumers = maps.Select(TerryConsumer).ToList(),
        ExpectedSelected = DefaultGroup.Count * maps.Count,
        Timeout = timeout ?? TimeSpan.FromHours(2),
    };
}
