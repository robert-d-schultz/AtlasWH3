namespace AtlasWH3.App;

public static partial class Walkthroughs
{
    private static readonly Tour Start = new("start", "Start page",
    [
        new(null, "Welcome to AtlasWH3",
            "AtlasWH3 builds Total War: WARHAMMER III campaign maps from the Assembly Kit's sources and edits those sources. " +
            "This short tour shows the start page; every editor has its own tour the first time you open it."),
        new("start.selector", "Assembly kit and map",
            "Pick the assembly kit and the map to work on, including maps from your linked mod packs. " +
            "Every editor you open from here uses this map, and the choice is remembered."),
        new("start.build", "Build",
            "Compiles the map natively (heights, tiles, props, rivers, trees, devastation pieces, AI pathfinding), runs your own steps, " +
            "then packs and installs the mod."),
        new("start.scene", "Scene editor (preview)",
            "Props, entities, prefabs and layers of the map, in a top-down view and in 3D. Add new props and clamp floating ones to the ground here. " +
            "A preview: parts of it are still being moved to WH3."),
        new("start.tiles", "Tile map (preview)",
            "Paint the campaign tile map hex by hex (roads, rivers, coast, mountains) with live checks for tiles the build can't place. " +
            "A preview: some of its checks are still Three Kingdoms rules."),
        new("start.settingsTile", "Settings",
            "Game and kit folders, linked mod packs (only ever read), the tile map source and Prepare game data. " +
            "Check these first on a new install."),
        new("start.recent", "Recent projects",
            "Build projects (.atlaswh3) you opened lately; click one to open it in the Build window."),
        new("start.getStarted", "Get started",
            "The usual order: check Settings, edit the map, then build it. Back up your assembly kit before building over it."),
        new("start.menu", "Menus and help",
            "Window switches between open editors, Build opens the Build window, and Help › Walkthrough for this window (F1) replays a tour. " +
            "Help › Reset all walkthroughs shows every tour again."),
    ]);
}
