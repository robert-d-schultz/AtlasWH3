namespace AtlasWH3.App;

public static partial class Walkthroughs
{
    private static readonly Tour Start = new("start", "Start page",
    [
        new(null, "Welcome to AtlasWH3",
            "AtlasWH3 edits Total War: THREE KINGDOMS campaign maps from the Assembly Kit and builds them without BOB. " +
            "This short tour shows the start page; every editor has its own tour the first time you open it."),
        new("start.selector", "Assembly kit and map",
            "Pick the assembly kit and the map to work on, including maps from your linked mod packs. " +
            "Every editor you open from here uses this map, and the choice is remembered."),
        new("start.scene", "Scene editor",
            "Props, entities, prefabs and layers of the map, in a top-down view and in 3D. Add new props and clamp floating ones to the ground here."),
        new("start.tiles", "Tile map",
            "Paint the campaign tile map hex by hex (roads, rivers, coast, mountains) with live checks for tiles the build can't place."),
        new("start.painter", "Terrain painter",
            "Raise, lower and smooth the heights, paint ground textures and place or erase trees on the compiled map."),
        new("start.build", "Build",
            "Compiles the map natively (tiles, meshes, props, rivers, trees), runs your own steps, then packs and installs the mod."),
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
