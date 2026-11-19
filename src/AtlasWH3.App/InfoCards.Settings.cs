namespace AtlasWH3.App;

/// <summary>Info-card texts for the Start screen (StartWindow) and the Settings page (SettingsWindow).</summary>
public static partial class InfoCards
{
    private static readonly Entry[] SettingsCards =
    [
        // ---- start screen
        new("start.project", "Project",
            "The project (.atlaswh3) the editors open on: its map and its packs, set in Build › Project settings. The Assembly " +
            "Kit is the one in Settings. Without a project the editors open a map from the Assembly Kit and read only the game's own packs."),
        new("start.map", "Map",
            "Without a project: the campaign map every editor opens, from the Terry projects in the Assembly Kit. Your choice is remembered."),
        new("start.selection", "Current map",
            "What the Scene editor and Tile map open, and what Build (F7) builds. The editors only read the packs; Build writes the " +
            "project's own pack (and its pieces pack), keeping copies of it first."),
        new("start.settings", "Settings",
            "The game, Assembly Kit and RPFM folders, AtlasWH3's data folder, developer mode and walkthroughs."),

        // ---- settings: folders
        new("settings.game", "Game folder",
            "The game install, holding data\\ and the Assembly Kit. Found through Steam; change it only if that found the wrong one."),
        new("settings.kit", "Assembly Kit",
            "The Assembly Kit holding your maps' Terry projects (raw_data): the game folder's assembly_kit unless you pick another. " +
            "Edits land in the Terry projects or in AtlasWH3's data folder; of the packs, only a build writes, into its project's own."),
        new("settings.rpfm", "RPFM",
            "RPFM's folder, holding rpfm_server.exe (RPFM 5.3 or newer). AtlasWH3 reads and writes every pack through RPFM's " +
            "server, and starts it when it is not running. RPFM's schema decodes the DB tables: keep RPFM's schemas up to date."),
        new("settings.data", "AtlasWH3 data",
            "AtlasWH3's own folder, %LocalAppData%\\AtlasWH3: build output, build logs, pack backups and edit history (output\\), " +
            "error logs (logs\\) and decoded textures (cache\\). There is no need to move it."),

        // ---- settings: other
        new("settings.dev", "Developer mode",
            "Shows the tools still in the works: the Tile map's Check menu (BOB's tile matching on the whole map), the Campaign " +
            "battles window, which does not read WH3's catchment file yet, and the self-test flags. Leave it off for normal work."),
        new("settings.tour", "Show the walkthrough",
            "A short guided tour of this page: what each section is for. Help › Walkthrough for this window (F1) does the same in every editor."),
        new("settings.resetTours", "Reset walkthroughs",
            "Every window's walkthrough starts again the next time that window opens, as on a fresh install."),
        new("menu.settings", "Settings",
            "The game, Assembly Kit and RPFM folders, AtlasWH3's data folder and developer mode. Changes apply to windows opened afterwards."),
    ];
}
