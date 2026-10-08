namespace AtlasWH3.App;

/// <summary>Info-card texts for the scene editor's Props tab (PropToolsPanel) and its menu commands.</summary>
public static partial class InfoCards
{
    private static readonly Entry[] SceneCards =
    [
        // ---- add props
        new("scene.props.add", "Add props",
            "Pick a model from the game packs and linked mod packs, then click in the 2D or 3D view to place it as a new Prop in the active layer. Each placement is one undoable edit."),
        new("scene.props.search", "Search models",
            "Type parts of the model path, separated by spaces (e.g. \"temperate tree pine\"). Down arrow jumps into the list."),
        new("scene.props.all", "All models",
            "Also list models outside rigidmodels/campaign/ (battle and shared models). Campaign maps normally use campaign models only."),
        new("scene.props.yaw", "Yaw",
            "Turn about the vertical axis in degrees. Random yaw picks a new angle for every placed prop."),
        new("scene.props.scale", "Scale",
            "Uniform scale of the new prop. ± % varies it randomly per placement, e.g. 15 gives 0.85 to 1.15 times the scale."),
        new("scene.props.yoffset", "Y offset",
            "Added to the ground height at the click point. Negative values sink the prop into the ground."),
        new("scene.props.seatbase", "Seat base",
            "Put the model's lowest point on the ground instead of its origin. Most campaign props have their origin at the base, so this matters only for a few models."),
        new("scene.props.repeat", "Repeat placement",
            "Stay in placement mode after each click so you can place many copies. Esc, the button or picking another tool ends it."),
        new("scene.props.place", "Place",
            "Start placement mode: left-click on the terrain in the 2D or 3D view to place the selected model there. Double-clicking a model also starts it."),

        // ---- clamp to ground
        new("scene.clamp", "Clamp to ground",
            "Move props up or down onto the terrain so none float or sink by accident. All moved props are one undoable edit."),
        new("scene.clamp.ground", "Ground height source",
            "Scene height is what BOB's scene reports: the terrain plus the height patches of tiles, rivers and other props, so trees sit on mountain props as in vanilla. Built terrain is the bare lf + tile hf; both come from the last build, while the kit lf map includes unbuilt edits but no tile detail."),
        new("scene.clamp.mode", "Clamp mode",
            "Origin puts the prop's pivot on the ground, as Terry and vanilla trees do; Model base puts its lowest point there. Vanilla sink buries big mountain and rock meshes by their usual vanilla depth so only the top shows."),
        new("scene.clamp.offset", "Clamp offset",
            "Extra height added after clamping, in world units. Use a small negative value to tuck props slightly into the ground."),
        new("scene.clamp.onlydown", "Only lower",
            "Move only props that are above their clamped height; props that are already lower (buried) are left where they are."),
        new("scene.clamp.selected", "Clamp selected (Ctrl+G)",
            "Clamp the selected props. Groups, prefab instances and non-prop entities are skipped."),
        new("scene.clamp.layer", "Clamp active layer",
            "Clamp every prop in the active file layer, hidden and frozen ones excluded."),
        new("scene.clamp.view", "Clamp all in view",
            "Clamp every visible, unfrozen prop on screen: the 3D camera's view, or the 2D view's area."),
        new("scene.clamp.find", "Find floating props",
            "Select props whose lowest point (origin or model base) is more than this many world units above the ground, so you can check them before clamping."),
        new("scene.clamp.skipsettlements", "Skip settlement pieces",
            "Leave settlement models out of Find floating: walls, roofs and houses often stand on platforms or other pieces, so they look like they float."),
        new("scene.clamp.sinktable", "Sink table",
            "Per-model depths for the Vanilla sink mode. They are learnt from this map's own mountains and rocks; load a JSON table (such as vanilla_sink.json) to use vanilla's instead."),
    ];
}
