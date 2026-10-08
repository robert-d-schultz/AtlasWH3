namespace AtlasWH3.App;

public static partial class Walkthroughs
{
    private static readonly Tour Scene = new("scene", "Scene editor",
    [
        new("scene.layers", "Layers",
            "Every layer of the map (a group of props, entities and prefabs) and the objects in it. " +
            "Click to select, tick to show or hide, drag objects onto a layer to move them, and right-click for more."),
        new("scene.layerFilters", "Layer filters",
            "Narrow the tree by name, region, type or visibility; the filters only change the view. " +
            "Show all, Hide all and Show only filtered change the layers' saved visibility, as one undo step."),
        new("scene.search", "Find",
            "Search the whole map by name, asset or id, or with filters such as type:Prop or layer:name. Select all results to edit them together."),
        new("scene.sharedBar", "View options",
            "Preview a season, show hidden layers, colour the map by region, and overlay the placed tiles (roads, rivers, coast, mountains). " +
            "These only change what you see."),
        new("scene.map2d", "Top view (2D)",
            "The map from above: click to select, drag a box to select many, right or middle drag to pan and use the wheel to zoom. " +
            "Ctrl+1 switches to this view."),
        new("scene.tab3d", "3D view",
            "The same map in 3D with terrain, water, trees and tile meshes; W, E and R move, rotate and scale the selection. " +
            "Right-drag orbits, middle-drag pans and Ctrl+2 switches here."),
        new("scene.inspector", "Inspector",
            "The selected object's fields (position, model, components). Type a value and press Enter; every edit is saved at once and Ctrl+Z undoes it."),
        new("scene.terrainTab", "Terrain & trees",
            "Brushes for the kit's land and sea height maps and the campaign tree map. These edits wait in memory: the title shows * until you save them."),
        new("scene.propsTab", "Props",
            "Browse every model in the game and linked packs, pick one and click Place, then click the map to drop it in the active layer, standing on the ground."),
        new("scene.edit", "Edit menu",
            "Undo, checkpoints, duplicate and delete, plus Clamp selected to ground (Ctrl+G), Clamp all in layer or view, and Find floating props."),
        new("scene.create", "Create menu",
            "Add an entity, place a prefab, add a prop from the asset browser, or make a new file layer."),
        new("scene.file", "File menu and saving",
            "Prop and entity edits are written as you make them. Terrain and tree edits need Save (Ctrl+S), and closing asks before dropping them."),
        new("scene.status", "Status bar",
            "What the last action did on the left, and what is under the mouse on the right."),
        new("scene.menu", "Help",
            "Help › Walkthrough for this window (F1) replays this tour; hover any control for a short info card."),
    ]);
}
