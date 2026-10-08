namespace AtlasWH3.App;

/// <summary>Info-card texts for the Start screen (StartWindow) and the Settings page (SettingsWindow).</summary>
public static partial class InfoCards
{
    private static readonly Entry[] SettingsCards =
    [
        // ---- start screen
        new("start.kit", "Assembly kit",
            "The kit whose raw_data you edit and build, e.g. assembly_kit. The list shows the assembly_kit* folders next to the game."),
        new("start.map", "Map",
            "The campaign map every editor opens: maps with a .terry in the kit, and maps in your linked packs. Your choice is remembered."),
        new("start.selection", "Current map",
            "What the Scene editor, Tile map, Terrain painter and Build open. Linked packs are read only, never written."),
        new("start.settings", "Settings",
            "Folders, linked mod packs, the tile map source and Prepare game data."),

        // ---- settings: folders
        new("settings.game", "Game folder",
            "The game install, holding data\\ and the assembly kits. Leave it empty to find it through Steam."),
        new("settings.kit", "Assembly kit",
            "The kit whose raw_data you edit and build. All edits land here or in the output folder, never in the game's packs."),
        new("settings.compiled", "Game data cache",
            "Compiled map files copied out of the packs, which the editors read. Prepare game data fills it."),
        new("settings.db", "DB tables",
            "The tree DB tables as TSV files. Prepare game data fills it, or point it at an RPFM TSV export."),
        new("settings.output", "Output",
            "Edit journals, build logs and previews go here."),
        new("settings.cache", "Cache",
            "Decoded textures, kept to speed up the editors. Safe to delete."),

        // ---- settings: linked packs
        new("settings.packs", "Linked packs",
            "Mod packs read for compiled map files, DB tables and assets before the vanilla packs; the top of the list wins. They are opened read-only."),
        new("settings.packAdd", "Add packs",
            "Link one or more .pack files, e.g. your map mod in the game's data folder. AtlasWH3 only reads them."),
        new("settings.packRemove", "Remove",
            "Unlink the selected pack. The pack file itself is not touched."),
        new("settings.packUp", "Up", "Give the selected pack a higher priority."),
        new("settings.packDown", "Down", "Give the selected pack a lower priority."),

        // ---- settings: game data and other
        new("settings.map", "Map to prepare",
            "Every campaign map found in the vanilla packs, the assembly kit (with a .terry) and the linked packs. The one you pick is also the Start screen's map."),
        new("settings.prepare", "Prepare game data",
            "Copy the map's compiled files and the tree DB tables out of the linked packs, then the vanilla packs, into the game data cache. Packs are only read."),
        new("settings.dev", "Developer mode",
            "Shows the extra tools used to compare AtlasWH3 with BOB (BOB launch, tile-matching simulation, self-tests). Leave it off unless you check parity."),
        new("settings.tour", "Show the walkthrough",
            "A short guided tour of this page: what each section is for. Help › Walkthrough for this window (F1) does the same in every editor."),
        new("settings.resetTours", "Reset walkthroughs",
            "Every window's walkthrough starts again the next time that window opens, as on a fresh install."),
        new("menu.settings", "Settings",
            "Folders, linked mod packs (read-only), tile map source, game data and developer mode. Changes apply to windows opened afterwards."),
    ];
}
