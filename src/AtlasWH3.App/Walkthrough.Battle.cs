namespace AtlasWH3.App;

public static partial class Walkthroughs
{
    private static readonly Tour Battles = new("battles", "Campaign battles",
    [
        new(null, "Campaign battles",
            "Every campaign battle is cut from one battle terrain; its catchment areas decide where field battles, sieges and " +
            "gate battles can start, and which battle map a settlement loads. This window checks and fixes them for your regions."),
        new("battles.menu", "File menu",
            "The catchments are read from your linked packs, then the game's. Save writes to the output folder; Export mod pack " +
            "and Write to kit put the file where the game or the kit picks it up. The packs themselves are never written."),
        new("battles.lists", "Catchment lists",
            "One list per battle type. Tick the lists to draw; the active list takes new areas and shows its coverage gaps."),
        new("battles.tools", "Tools",
            "Select, move and resize areas, or drag out a new one. Ctrl+Z undoes every edit."),
        new("battles.map", "Battle grid",
            "One cell per campaign hex, north at the top, with each settlement as a dot coloured by its status. " +
            "Right-drag pans, the wheel zooms, F fits the map."),
        new("battles.regions", "Regions",
            "Each settlement's battle: OK, no catchment, wrong type, or a missing or broken redirect. Fix covers it in both " +
            "siege lists and redirects new regions to the battle map their buildings call for."),
        new("battles.area", "Area",
            "The selected area's name, redirect, box and approaches. Pick a battle map from the packs; the check below warns " +
            "about a wrong battle type or a map that would load with a hole."),
        new("battles.status", "Status bar",
            "The cell under the cursor, the areas covering it, and whether there are unsaved changes (also a * in the title)."),
    ]);
}
