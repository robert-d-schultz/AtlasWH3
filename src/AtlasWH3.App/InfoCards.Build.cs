namespace AtlasWH3.App;

/// <summary>Info-card texts for the Build window (BuildWindow) and its Project settings tab (BuildProfileEditor).</summary>
public static partial class InfoCards
{
    private static readonly Entry[] BuildCards =
    [
        // ---- toolbar
        new("build.project", "Current project",
            "The open .atlaswh3 project and the campaign map it builds. A * in the title means unsaved changes."),
        new("build.open", "Open project",
            "Open an existing .atlaswh3 build project. It stores the map, its pack, the ticked steps and the build options."),
        new("build.new", "New project",
            "Create a .atlaswh3 project for the current map: compile into AtlasWH3's data folder, then pack next to the project, merged into the map's mod pack when it has one."),
        new("build.save", "Save project (Ctrl+S)",
            "Write the ticks and Project settings back to the .atlaswh3 file. Nothing is saved automatically."),
        new("build.buildAll", "Build all (F5)",
            "Run every ticked row from top to bottom: Validate, Compile steps, Pack and any custom steps in between. It stops at the first failure."),
        new("build.runSelected", "Run selected",
            "Run only the highlighted row (one segment, one compile step or one custom step), whether it is ticked or not. Handy for redoing one step after an edit."),
        new("build.packOnly", "Pack only",
            "Rebuild the output pack from the files already on disk, without compiling. Use it after hand-editing compiled files or changing the pack contents."),
        new("build.cancel", "Cancel",
            "Stop the build at the next safe point. Files already written stay on disk."),
        new("build.progress", "Progress", "What the build is doing now, or the last run's result."),

        // ---- tree
        new("build.tree", "Build steps",
            "Everything a build does, in the order it runs. Ticked rows run on Build all; hover a row to see what it does."),
        new("build.validate", "Validate",
            "Pre-flight checks before anything is written: every ticked step's inputs exist, and the tile map passes the tile-map validator. Errors stop the build; double-click to open tile errors."),
        new("build.compile", "Compile",
            "Runs the ticked native campaign steps, AtlasWH3's replacements for BOB's terrain actions. Output goes to AtlasWH3's data folder (Project settings > Compiled files); independent steps run in parallel."),
        new("build.pack", "Pack",
            "Writes the compiled files into the project's pack (Project settings > Pack): the build's files are replaced, everything else in the pack is kept. Refuses while Warhammer3.exe is running. Untick to skip it."),
        new("build.custom", "Custom step",
            "An external command (python script, CAIME, RPFM startpos build…) run at this point of the build. Edit it under Project settings → Custom steps."),

        // ---- compile steps (key = "build.step." + step name)
        new("build.step.heightmaps", "heightmaps  (BOB: Campaign Heightmap, Campaign Shroud Heights)",
            "Writes full_logic_map.compressed_map, full_height_map.dds, lf_normal.dds and shroud_heights.dds from the Terry project's height layers (lf_normal without Terry's export). tile_list, trees and the camera height map read them, so run it after any height edit."),
        new("build.step.tile_list", "tile_list  (BOB: Tilemap)",
            "Turns the Terry project's tile_map.png into tile_list.bin, choosing and placing the 3D terrain tiles. Run it after editing the tile map; its source is set under Tile map source."),
        new("build.step.trees", "trees  (BOB: Campaign Trees)",
            "Writes trees.campaign_tree_list from the Terry project's CampaignTree map, with heights from the logic height map."),
        new("build.step.global_map", "global_map  (BOB: Global Tilemap, Campaign Global Blendmap)",
            "Writes global_map\\: global_blend.dds, texture_arrays.xml and the far-zoom tile_list.bin. Needs tile_list."),
        new("build.step.masks", "masks  (BOB: Color Overlay, Corruption, Snow, Event Area, Patch Visibility Mask)",
            "Writes the colour overlays, corruption, snow and event area masks from the Terry project's layers."),
        new("build.step.rivers", "rivers  (BOB: Terry file, rivers)",
            "Builds the river_<id> water models from the river splines in the Terry project's layers."),
        new("build.step.global_props", "global_props  (BOB: Terry file, global_props.bin)",
            "Packs every prop, sound and campaign entity from the Terry project's .layer files into global_props.bin, global_props_sound.bin and the devastation-type files. Run it after moving or adding props."),
        new("build.step.devastation_pieces", "devastation_pieces  (BOB: Devastation pieces)",
            "Writes pieces\\event_* for every event area of the map, for both the main map and its devastated version, from the two Terry projects."),
        new("build.step.devastated_tiles", "devastated_tiles  (BOB: Devastation pieces)",
            "Builds the devastated Terry project's tile list, global map and masks into the cache, for devastation_pieces. Runs from the start of the build, beside the main map's steps."),
        new("build.step.devastated_heights", "devastated_heights  (BOB: Devastation pieces)",
            "Builds the devastated Terry project's heightmaps and trees into the cache, for devastation_pieces. Runs after heightmaps: full_height_map.dds copies the blocks that are the main map's instead of encoding them again."),
        new("build.step.lookup", "lookup  (BOB: Convert lookup texture)",
            "Converts the campaign_maps *lookup*.bmp region/province maps into the game's .tga, .dds and _minimap.tga. Run it after repainting regions."),
        new("build.step.camera_heightmap", "camera_heightmap  (BOB: Generate Camera Height Map)",
            "Writes camera_heightmap.png, which keeps the campaign camera above the ground and props. BOB's own action crashes on large maps."),
        new("build.step.hlp_spd", "hlp_spd  (the game's own generation, no BOB action)",
            "Writes the AI pathfinding files spd_data.esf and hlp_data.esf from pathfinding.ppd and map_data.esf, without the game. Needs those two files from CAIME. Run it after changing regions, roads, settlements or passability."),

        // ---- log tab
        new("build.tab.log", "Log", "The build's output, one line per event, tagged with the row that wrote it."),
        new("build.tab.settings", "Project settings",
            "Everything the project saves: the map, its pack, compile options and custom steps. Changes mark the project unsaved."),
        new("build.filter", "Filter", "Show only log lines that contain this text, e.g. a step name or \"!\" for problems."),
        new("build.onlySelected", "Selected row only", "Show only the log lines of the row highlighted on the left."),
        new("build.autoScroll", "Auto-scroll", "Keep the log scrolled to the newest line while a build runs."),
        new("build.copy", "Copy", "Copy the log lines currently shown (after the filter) to the clipboard."),
        new("build.logFile", "Log file", "Show the last build's full log file in Explorer. Every build writes one."),
        new("build.outputFolder", "Output folder", "Open the folder Compile writes to, in AtlasWH3's data folder."),
        new("build.packFile", "Pack file", "Show the output .pack in Explorer."),
        new("build.tileErrors", "Show tile errors",
            "Open the tile map editor in error mode, with every tile-map error highlighted and a recommended fix for each."),

        // ---- empty state
        new("build.emptyNew", "New project", "Create a build project for the current map and open it."),
        new("build.emptyOpen", "Open project", "Open an existing .atlaswh3 build project."),
        new("build.recent", "Recent project", "Open this recently used project."),

        // ---- Project settings: project
        new("profile.header.project", "Project", "Which map this project builds and the mod packs it reads. The Assembly Kit and the game are the ones in Settings."),
        new("profile.name", "Name", "A display name for the project, shown in the window title. It does not affect the build."),
        new("profile.map", "Map",
            "The campaign map folder name, e.g. wh3_main_combi_map_1 or cr_oldworld_map_1. Every step reads and writes this map's folders."),
        new("profile.modPacks", "Mod packs",
            "Other packs read before the game's own, top first: mods whose models, tiles or DB the map uses. Only read. The " +
            "map's own pack is set under Pack and read first."),

        // ---- Project settings: compile
        new("profile.header.compile", "Compile", "Options of the compile steps."),
        new("profile.output", "Compiled files",
            "Where Compile writes the map's compiled files (terrain\\campaigns\\{map}, campaign_maps\\{map}), in AtlasWH3's data " +
            "folder. Pack reads them from here; the Assembly Kit's working_data is not touched."),
        new("profile.acceptTileMap", "Accepted tile-map errors",
            "The tile map check stops the build on these errors. Tick one to build anyway, only when you know the game tolerates it."),
        new("profile.patchMask", "Patch mask",
            "How tile_list writes patch_mask.dds, the per-patch mask the game hides the sea floor under land with. Fitted (default) spreads the cells over the whole tile map, as the game reads them. Vanilla is BOB's: its rows fall short of the north edge, so the sea floor pokes out under land, worst in the north."),
        new("profile.lookupLast", "Lookup: regions past 1024",
            "Regions whose lookup palette entries go to the end: region keys, or .txt files of them (one per line, # comments). The game reads only the first 1024 palette entries, so on a map with more regions than that, list the ones that can go without (wastelands, unreachable or tiny regions). The .tga, .dds and _minimap.tga are reordered together, and hexes without a region (black) go to the end with them. Empty (default): BOB's order. The lookup step names the regions left past entry 1024."),

        // ---- Project settings: custom steps
        new("profile.header.custom", "Custom steps",
            "External commands the build runs at fixed points (before or after Compile, or after Pack). Use them for scripts, CAIME or a startpos build."),
        new("profile.custom.on", "On", "Ticked steps run on Build all; unticked ones only run with Run selected."),
        new("profile.custom.name", "Name", "The row name in the build list and log. Keep it unique."),
        new("profile.custom.runs", "Runs", "When the step runs: BeforeCompile, AfterCompile or AfterPack."),
        new("profile.custom.command", "Command", "The program to start, e.g. python, powershell or a full .exe path. Tokens like {ak} work here."),
        new("profile.custom.arguments", "Arguments",
            "The command line passed to the command; tokens {project} {ak} {map} {game} {out} {pack} are expanded. Quote paths that contain spaces."),
        new("profile.custom.workingDir", "Working folder", "The folder the command runs in. Default {project}, the folder of the .atlaswh3 file."),
        new("profile.custom.timeout", "Timeout (s)", "Seconds before the command is stopped and counted as failed."),
        new("profile.custom.continue", "Continue on error", "When ticked, a non-zero exit is logged but the build carries on."),
        new("profile.custom.add", "Add", "Add a new custom step (python script.py) to edit in the grid."),
        new("profile.custom.remove", "Remove", "Delete the selected custom step."),
        new("profile.custom.up", "Up", "Move the selected step earlier; steps at the same point run top to bottom."),
        new("profile.custom.down", "Down", "Move the selected step later."),
        new("profile.custom.browse", "Browse command", "Pick a program or script for the selected step; .py and .ps1 files get the right command line."),

        // ---- Project settings: pack
        new("profile.header.pack", "Pack",
            "The .pack the build writes the compiled map into: your campaign's own, with its DB, map_data.esf and pathfinding.ppd."),
        new("profile.piecesPackFile", "Pieces pack",
            "Optional. The .pack the event-area pieces go into: the map's pieces\\ folder and everything in the devastated map's " +
            "folder (Old World's !cr_oldworld_campaign_devastate.pack). The rest goes into the pack above. Like it, the pack is " +
            "read first, backed up, updated in place (its other files kept), or created. Empty: everything goes into the pack above."),
        new("profile.backupPack", "Back up the pack",
            "Before each build writes the pack (and the pieces pack), keep a copy of it as it was in AtlasWH3's data folder (output\\pack_backups). The next build replaces the copy."),
        new("profile.packFile", "Pack file",
            "Your campaign's .pack, usually in the game's data folder. The build replaces the files it makes (heights, tiles, props, " +
            "river models, pieces…), drops the ones it no longer makes, and keeps everything else (DB, scripts, map_data.esf, " +
            "pathfinding.ppd). It is also read before the game's packs. A pack that doesn't exist yet is created with the build's files."),
        new("profile.browse", "Browse", "Pick the path with a file dialog."),
        new("build.walkthrough", "Walkthrough",
            "A short guided tour of the Build window: the project bar, the build steps, the log and the project settings (F1)."),
    ];

    /// <summary>The card key for a compile step row, or null when the step has no text yet.</summary>
    public static string? BuildStepKey(string step) => Has("build.step." + step) ? "build.step." + step : null;
}
