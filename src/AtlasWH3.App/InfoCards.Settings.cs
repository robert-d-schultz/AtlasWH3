namespace AtlasWH3.App;

/// <summary>Info-card texts for the Start screen (StartWindow) and the Settings page (SettingsWindow).</summary>
public static partial class InfoCards
{
    private static readonly Entry[] SettingsCards =
    [
        // ---- start screen
        new("start.project", "Project",
            "The project (.atlaswh3) the editors open on: its map, assembly kit, mod packs and tile map source, all set in Build > " +
            "Project settings. Without a project the editors open a map from the assembly kit and read only the game's own packs."),
        new("start.map", "Map",
            "Without a project: the campaign map every editor opens, from the Terry projects in the Assembly Kit. Your choice is remembered."),
        new("start.selection", "Current map",
            "What the Scene editor, Tile map, Campaign battles and Build open. Mod packs are only read, never written."),
        new("start.settings", "Settings",
            "The game and assembly kit folders, AtlasWH3's data folder, developer mode and walkthroughs."),

        // ---- settings: folders
        new("settings.game", "Game folder",
            "The game install, holding data\\ and the assembly kits. Found through Steam; change it only if that found the wrong one."),
        new("settings.kit", "Assembly kit",
            "The Assembly Kit holding your maps' Terry projects (raw_data): the game folder's assembly_kit unless you pick another. " +
            "Edits land in the Terry projects or in AtlasWH3's data folder, never in the game's packs."),
        new("settings.data", "AtlasWH3 data",
            "AtlasWH3's own folder, %LocalAppData%\\AtlasWH3: build output, logs and edit history (output\\) and decoded textures " +
            "(cache\\). There is no need to move it."),

        // ---- settings: other
        new("settings.dev", "Developer mode",
            "Shows the extra tools used to compare AtlasWH3 with BOB (BOB launch, tile-matching simulation, self-tests). Leave it off unless you check parity."),
        new("settings.tour", "Show the walkthrough",
            "A short guided tour of this page: what each section is for. Help › Walkthrough for this window (F1) does the same in every editor."),
        new("settings.resetTours", "Reset walkthroughs",
            "Every window's walkthrough starts again the next time that window opens, as on a fresh install."),
        new("menu.settings", "Settings",
            "The game and assembly kit folders, AtlasWH3's data folder and developer mode. Changes apply to windows opened afterwards."),
    ];
}
