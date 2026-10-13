namespace AtlasWH3.App;

public static partial class Walkthroughs
{
    private static readonly Tour Start = new("start", "Start page",
    [
        new(null, "Welcome to AtlasWH3",
            "AtlasWH3 builds Total War: WARHAMMER III campaign maps from the Assembly Kit's sources and edits those sources. " +
            "This short tour shows the start page; every editor has its own tour the first time you open it."),
        new("start.selector", "Project",
            "Pick the project to work on: every editor you open from here uses its map and mod packs, and the " +
            "choice is remembered. Without a project, pick a map from the assembly kit; the editors then read only the game's own packs."),
        new("start.build", "Build",
            "Compiles the map natively (heights, tiles, props, rivers, trees, devastation pieces, AI pathfinding), runs your own steps, " +
            "then writes it into your campaign's pack."),
        new("start.scene", "Scene editor (preview)",
            "Props, entities, prefabs and layers of the map, in a top-down view and in 3D. Add new props and clamp floating ones to the ground here. " +
            "A preview: parts of it are still being moved to WH3."),
        new("start.tiles", "Tile map (preview)",
            "Paint the campaign tile map hex by hex (roads, rivers, coast, mountains) with live checks for tiles the build can't place. " +
            "A preview: some of its checks are still Three Kingdoms rules."),
        new("start.settingsTile", "Settings",
            "The game and assembly kit folders, found through Steam. Check them on a new install."),
        new("start.recent", "Recent projects",
            "Projects (.atlaswh3) you opened lately; click one to open it in the Build window, or pick it under Project for the editors."),
        new("start.getStarted", "Get started",
            "The usual order: make the map in Terry, create a build project for it, then build. The build writes into your " +
            "campaign's pack and keeps a copy of it as it was."),
        new("start.menu", "Menus and help",
            "Window switches between open editors, Build opens the Build window, and Help › Walkthrough for this window (F1) replays a tour. " +
            "Help › Reset all walkthroughs shows every tour again."),
    ]);
}
