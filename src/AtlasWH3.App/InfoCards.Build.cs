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
            "Open an existing .atlaswh3 build project. It stores the map, kit, ticked steps, pack and install settings."),
        new("build.new", "New project",
            "Create a .atlaswh3 project for the current map with sensible defaults: compile into the kit's working_data, then pack next to the project."),
        new("build.save", "Save project (Ctrl+S)",
            "Write the ticks and Project settings back to the .atlaswh3 file. Nothing is saved automatically."),
        new("build.buildAll", "Build all (F5)",
            "Run every ticked row from top to bottom: Validate, Compile steps, Pack, Install and any custom steps in between. It stops at the first failure."),
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
            "Runs the ticked native campaign steps, AtlasWH3's replacements for BOB's terrain actions. Output goes to Project settings → Output; independent steps run in parallel."),
        new("build.pack", "Pack",
            "Writes the output .pack from Project settings → Pack: New makes a pack with only the listed contents, Merge updates a base pack. Untick to skip packing."),
        new("build.install", "Install",
            "Copies the output pack into the game's data folder so the game loads it. It refuses while the game is running and can keep a backup of the pack it replaces."),
        new("build.custom", "Custom step",
            "An external command (python script, CAIME, RPFM startpos build…) run at this point of the build. Edit it under Project settings → Custom steps."),

        // ---- compile steps (key = "build.step." + step name)
        new("build.step.rasters", "rasters  (BOB: Height map compressed / DDS, climate map)",
            "Writes the lf land and sea height maps (.compressed_map + .dds) and climate_map.cm from the kit's .terry layers. Most other steps read these, so run it after any height or climate edit."),
        new("build.step.tile_list", "tile_list  (BOB: Tilemap)",
            "Turns the kit's tile_map.png into tile_list.bin, choosing and placing the 3D terrain tiles exactly as BOB does. Run it after editing the tile map; its source is set under Tile map source."),
        new("build.step.global_map", "global_map  (BOB: Global Mesh, global_map part)",
            "Writes global_map\\: global_blend.dds, texture_arrays.xml and the global copy of tile_list.bin. Needs rasters and tile_list."),
        new("build.step.global_mesh", "global_mesh  (BOB: Global Mesh)",
            "Builds the far-zoom land_mesh_N and sea_mesh_N models and their .compressed_map files, identical to BOB's. It is the slowest step (about 20 s on a full map)."),
        new("build.step.rivers", "rivers  (BOB: Terry file, rivers)",
            "Builds the river_N water models and the height patches that carve river beds into the terrain, from the river entities in the kit layers. Uses BOB's geometry (the CLI's --river-geometry wide is the older alternative)."),
        new("build.step.global_props", "global_props  (BOB: Terry file, global_props.bin)",
            "Packs every prop, building and campaign entity from the kit's region .layer files into global_props.bin, byte-identical to BOB. Run it after moving or adding props."),
        new("build.step.camera_heightmap", "camera_heightmap  (BOB: Generate Camera Height Map)",
            "Writes camera_heightmap.png, which keeps the campaign camera above the ground, meshes and props. Run it after global_mesh, global_props and rivers."),
        new("build.step.trees", "trees  (BOB: Campaign Trees)",
            "Writes trees.campaign_tree_list from the kit's CampaignTree map, with heights from the terrain and tiles. Heights are reused from the existing tree list where they match; the CLI's --fresh-trees recomputes every one."),
        new("build.step.lookup", "lookup  (BOB: Convert lookup texture)",
            "Converts the campaign_maps *lookup*.bmp region/province maps into the game's .tga, .dds and _minimap.tga. Run it after repainting regions."),
        new("build.step.hlp_spd", "hlp_spd  (game startpos build, no BOB action)",
            "Writes the AI pathfinding files spd_data.esf and hlp_data.esf from pathfinding.ppd and map_data.esf. Needs those two files from CAIME or a map data export."),

        // ---- log tab
        new("build.tab.log", "Log", "The build's output, one line per event, tagged with the row that wrote it."),
        new("build.tab.settings", "Project settings",
            "Everything the project saves: map and kit, tile map source, compile output, custom steps, pack and install. Changes mark the project unsaved."),
        new("build.filter", "Filter", "Show only log lines that contain this text, e.g. a step name or \"!\" for problems."),
        new("build.onlySelected", "Selected row only", "Show only the log lines of the row highlighted on the left."),
        new("build.autoScroll", "Auto-scroll", "Keep the log scrolled to the newest line while a build runs."),
        new("build.copy", "Copy", "Copy the log lines currently shown (after the filter) to the clipboard."),
        new("build.logFile", "Log file", "Show the last build's full log file in Explorer. Every build writes one."),
        new("build.outputFolder", "Output folder", "Open the folder Compile writes to (Project settings → Output)."),
        new("build.packFile", "Pack file", "Show the output .pack in Explorer."),
        new("build.tileErrors", "Show tile errors",
            "Open the tile map editor in error mode, with every tile-map error highlighted and a recommended fix for each."),

        // ---- empty state
        new("build.emptyNew", "New project", "Create a build project for the current map and open it."),
        new("build.emptyOpen", "Open project", "Open an existing .atlaswh3 build project."),
        new("build.recent", "Recent project", "Open this recently used project."),

        // ---- Project settings: project
        new("profile.header.project", "Project", "Which map this project builds and where its sources come from."),
        new("profile.name", "Name", "A display name for the project, shown in the window title. It does not affect the build."),
        new("profile.map", "Map",
            "The campaign map folder name, e.g. 3k_main_map or 3k_dlc07_main_map. Every step reads and writes this map's folders."),
        new("profile.kit", "Assembly kit",
            "The Assembly Kit root whose raw_data and working_data are built. Empty uses the default kit from Settings; the game's own files are never changed."),
        new("profile.gameData", "Game data",
            "The game's data folder, read for the tile database, hf maps and vanilla packs, and the target of Install. Empty uses the default from Settings."),
        new("profile.modPacks", "Mod packs",
            "Packs searched before the vanilla packs for tiles and models, highest priority first; separate with ;. Add your mod's packs when it ships its own tiles or props."),
        new("profile.header.tileMap", "Tile map source",
            "Where tile_list reads tile_map.png: the kit's raw_data (default), a file or folder, or an entry inside a .pack. The tile map editor saves to the same place."),

        // ---- Project settings: compile
        new("profile.header.compile", "Compile", "Where the compile steps write and what they do before starting."),
        new("profile.output", "Output folder",
            "Where Compile writes, laid out like working_data (terrain\\campaigns\\{map}, campaign_maps\\{map}). The default {ak}\\working_data replaces the kit's compiled files in place, as BOB did."),
        new("profile.acceptTileMap", "Accepted tile-map errors",
            "Tile-map validator error codes to let through instead of stopping the build, comma-separated, e.g. layout.mesh_columns. Only for errors you know the game tolerates."),
        new("profile.clean", "Delete before compile",
            "Folders under terrain\\campaigns\\{map} in the output to delete before compiling, comma-separated, e.g. global_meshes, height_patches. Clears stale files from an earlier, larger build."),
        new("profile.backup", "Terrain backup folder",
            "When set, the kit's raw terrain and the compiled terrain are copied here before each compile, replacing the previous backup. Empty = no backup."),

        // ---- Project settings: custom steps
        new("profile.header.custom", "Custom steps",
            "External commands the build runs at fixed points (before or after Compile, after Pack, after Install). Use them for scripts, CAIME or a startpos build."),
        new("profile.custom.on", "On", "Ticked steps run on Build all; unticked ones only run with Run selected."),
        new("profile.custom.name", "Name", "The row name in the build list and log. Keep it unique."),
        new("profile.custom.runs", "Runs", "When the step runs: BeforeCompile, AfterCompile, AfterPack or AfterInstall."),
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
        new("profile.header.pack", "Pack", "The .pack the build produces from the compiled files."),
        new("profile.packMode", "Pack mode",
            "New: a fresh pack with only the contents below. Merge: copies a base pack and replaces or adds the contents, keeping its other files."),
        new("profile.packOutput", "Output pack", "The .pack file to write, e.g. {game}\\my_map.pack or {project}\\{map}.pack."),
        new("profile.packBase", "Merge base",
            "Merge mode only: the pack to start from, e.g. your mod's pack with its DB tables. Empty merges into the output pack itself."),
        new("profile.replaceDirs", "Replace folders",
            "Merge mode only: pack folders whose old files are dropped unless re-added, comma-separated, e.g. terrain/campaigns/{map}/. Stops deleted files from lingering."),
        new("profile.contents", "Pack contents",
            "Files and folders copied into the pack. When two rows give the same pack path, the later row wins."),
        new("profile.contents.source", "Source", "A file or folder on disk; tokens like {out} and {map} are expanded."),
        new("profile.contents.path", "Path in pack", "Where the source goes inside the pack; for a folder, the pack folder it is copied into."),
        new("profile.contents.optional", "Optional", "When ticked, a missing source is a warning instead of an error."),
        new("profile.contents.addFolder", "Add folder", "Add a folder on disk to the pack contents."),
        new("profile.contents.addFile", "Add file", "Add a single file to the pack contents."),
        new("profile.contents.remove", "Remove", "Remove the selected row from the pack contents."),
        new("profile.contents.default", "Default contents",
            "Reset the contents to the defaults: the compiled terrain folder, the camera heightmap and the tree list."),

        // ---- Project settings: install
        new("profile.header.install", "Install", "Copies the finished pack into the game."),
        new("profile.installBackup", "Keep a backup",
            "Before Install overwrites a pack in the game's data folder, keep one copy of the old pack in the app's backup folder."),
        new("profile.browse", "Browse", "Pick the path with a file dialog."),
        new("build.walkthrough", "Walkthrough",
            "A short guided tour of the Build window: the project bar, the build steps, the log and the project settings (F1)."),
    ];

    /// <summary>The card key for a compile step row, or null when the step has no text yet.</summary>
    public static string? BuildStepKey(string step) => Has("build.step." + step) ? "build.step." + step : null;
}
