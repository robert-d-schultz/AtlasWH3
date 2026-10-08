namespace AtlasWH3.App;

public static partial class Walkthroughs
{
    private static readonly Tour Painter = new("painter", "Terrain painter",
    [
        new("painter.map", "The map",
            "The compiled campaign map with its heights, ground textures, water and trees. Left-drag paints with the current tool; " +
            "right or middle drag pans and the wheel zooms."),
        new("painter.tool", "Tool",
            "What a left-drag does: raise, lower, smooth, flatten or roughen the terrain, paint a ground texture, or place, scatter and erase trees."),
        new("painter.brush", "Brush",
            "Size, strength and soft edge of the brush for every tool."),
        new("painter.texture", "Ground texture",
            "The texture the Paint ground texture tool lays down."),
        new("painter.trees", "Tree tools",
            "The species to place, how dense a scattered forest is and how far apart trees stay, plus erase options."),
        new("painter.view", "View",
            "Turn ground textures, water and trees on or off, and preview a season. This only changes what you see."),
        new("painter.species", "Tree species",
            "Show or hide single tree species on the map; hidden species are also left alone by the eraser."),
        new("painter.menu", "Menus",
            "Ctrl+Z and Ctrl+Y undo and redo. File › Export to Assembly Kit writes your terrain into the kit (with a backup first), " +
            "and Export trees list writes the trees."),
        new("painter.status", "Status bar",
            "The point under the mouse: world position, height, water depth, ground texture and nearest tree, plus zoom and undo steps."),
    ]);
}
