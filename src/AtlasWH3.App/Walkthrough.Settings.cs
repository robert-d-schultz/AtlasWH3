namespace AtlasWH3.App;

public static partial class Walkthroughs
{
    private static readonly Tour Settings = new("settings", "Settings",
    [
        new("settings.folders", "Folders",
            "Where the game, the assembly kit, the extracted game data, the DB tables and your build output live. " +
            "A tick means the folder was found; an empty box uses the default shown in grey."),
        new("settings.packs", "Linked packs",
            "Mod .pack files to read map files, DB tables and assets from, before the vanilla packs (the top of the list wins). " +
            "They are only ever read: edits go to the assembly kit or the output folder."),
        new("settings.tileMap", "Tile map source",
            "Which tile_map.png the Tile map editor and the build use: the kit's own, a loose file, or one read from a pack."),
        new("settings.prepare", "Prepare game data",
            "Copies the chosen map's compiled files and the tree DB tables out of the linked and vanilla packs into the game data cache, " +
            "which the editors read. Do this once per map, and again after the pack changes."),
        new("settings.other", "Developer mode and walkthroughs",
            "Developer mode adds the BOB comparison tools; leave it off for normal editing. " +
            "The buttons replay this tour or make every window's tour show again."),
        new("settings.save", "Save",
            "Saves the settings; folder changes apply to windows opened afterwards. Cancel keeps the old settings."),
    ]);
}
