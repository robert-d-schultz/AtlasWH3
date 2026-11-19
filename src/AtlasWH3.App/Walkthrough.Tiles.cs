namespace AtlasWH3.App;

public static partial class Walkthroughs
{
    private static readonly Tour Tiles = new("tiles", "Tile map",
    [
        new("tiles.map", "The tile map",
            "Every hex of the campaign map coloured by its tile set (land, sea, roads, rivers, mountains, ...). " +
            "Right or middle drag pans and the wheel zooms."),
        new("tiles.tools", "Tools",
            "Navigate, paint with a brush, erase back to the land or sea around it, draw a line, fill an area, or pick a tile set with the eyedropper. " +
            "The slider sets the brush radius."),
        new("tiles.palette", "Tile sets",
            "The tile set that Paint, Line and Fill put down, such as roads, rivers, coast or mountains."),
        new("tiles.issues", "New issues",
            "Problems your unsaved strokes would cause, checked live: double-click one to go to it. Blocking issues stop the save; Ctrl+Z undoes the last stroke."),
        new("tiles.save", "Save",
            "Writes your strokes to tile_map.png (Ctrl+S). Build the map afterwards to see them in game."),
        new("tiles.history", "Saved edits",
            "Every saved batch, including those made from the command line; Edit › Undo last saved batch takes one back."),
        new("tiles.errorsTab", "Errors (F8)",
            "Lists every tile error on the whole map with a checked fix for each. Select one to see it, press Enter to apply the fix, N for the next."),
        new("tiles.file", "File menu",
            "Save to the Terry project's tile_map.png (or save despite the issues), reload it from disk, and Settings."),
        new("tiles.status", "Status bar",
            "The hex under the mouse and what the last action did."),
    ]);
}
