namespace AtlasWH3.App;

public static partial class Walkthroughs
{
    private static readonly Tour Settings = new("settings", "Settings",
    [
        new("settings.folders", "Folders",
            "The game and its assembly kit, found through Steam, and AtlasWH3's own data folder (build output, logs and caches), " +
            "which you can leave where it is. A tick means the folder was found. A map's mod packs and tile map source belong to " +
            "its project (Build > Project settings), not here."),
        new("settings.other", "Developer mode and walkthroughs",
            "Developer mode adds the BOB comparison tools; leave it off for normal editing. " +
            "The buttons replay this tour or make every window's tour show again."),
        new("settings.save", "Save",
            "Saves the settings; folder changes apply to windows opened afterwards. Cancel keeps the old settings."),
    ]);
}
