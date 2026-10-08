namespace AtlasWH3.App;

/// <summary>Info-card texts for the Campaign battles workspace (CampaignBattlesWindow).</summary>
public static partial class InfoCards
{
    private static readonly Entry[] BattleCards =
    [
        new("start.battles", "Campaign battles",
            "Checks every settlement of the selected map has a battle location and the right battle map, and fixes the catchment areas. Reads your linked packs."),

        // ---- left: lists and view
        new("battles.lists", "Catchment lists",
            "Each list is one battle type: field battles (land_ambush), walled sieges, unwalled settlements, encampments and gate battles. Tick a list to draw its areas."),
        new("battles.activeList", "Active list",
            "New areas you draw go into this list, and the coverage gaps are shown for it."),
        new("battles.gaps", "Show coverage gaps",
            "Shades battle land that no area of the active list covers. A battle of that type started there has no location."),
        new("battles.markers", "Settlements",
            "Dots for the campaign's settlements, coloured by their battle status. Click one to select its region."),
        new("battles.toolSelect", "Select, move, resize",
            "Click an area to select it (Shift+click picks the next one under the cursor). Drag inside to move it, drag a white handle to resize it."),
        new("battles.toolDraw", "Draw new area",
            "Drag out a box to add an area to the active list, with vanilla defaults. Name it and set its redirect on the Area tab."),
        new("battles.summary", "Map summary",
            "Where the catchments were read from, the grid size and how many areas each list holds."),

        // ---- area tab
        new("battles.area.list", "List",
            "The battle type this area belongs to. To use the same box for another type, draw a new area in that list."),
        new("battles.area.name", "Name",
            "The area's name. The game does not show it; the region fixer names its areas <region>_std and <region>_unf."),
        new("battles.area.redirect", "Battle redirection",
            "A battle-map folder under terrain\\battles\\ that battles here load instead of the composed terrain. Leave it empty for the normal terrain."),
        new("battles.area.pick", "Pick a battle map",
            "Choose from the battle-map folders in your linked and game packs."),
        new("battles.area.noRow", "No battles_tables row",
            "Tick when the battle map's own pack already ships its battles_tables row; Save then writes no row for this folder. Greyed out when a row is already known."),
        new("battles.area.check", "Redirect check",
            "Problems with the redirect: a folder that does not exist, a battle type that does not match the list, or a settlement map without its centre."),
        new("battles.area.faction", "Defending faction restriction",
            "Only battles where this faction defends use the area. Empty on almost every vanilla area."),
        new("battles.area.box", "Box",
            "The cells the area covers, inclusive (x to the east, y to the south). A battle starting on a covered cell can use this location."),
        new("battles.area.centre", "Infield centre",
            "The cell the battle is centred on. Moving the area moves it too."),
        new("battles.area.approaches", "Approaches",
            "The campaign directions an army may attack from here. Vanilla areas allow all four."),
        new("battles.area.apply", "Apply",
            "Writes the typed values into the area. One Ctrl+Z undoes it."),
        new("battles.area.delete", "Delete area",
            "Removes the selected area (Del). One Ctrl+Z brings it back."),
        new("battles.area.row", "battles_tables row",
            "Copies a battles_tables row for the redirect folder to the clipboard; specification and map_path both point at the folder. Only needed for a custom map without its own row."),

        // ---- regions tab
        new("battles.regions.summary", "Region summary",
            "How many settlements are fine and how many have a problem, counted over the regions of the open campaign map."),
        new("battles.regions.filter", "Filter",
            "Shows regions whose name or key contains this text."),
        new("battles.regions.state", "Show",
            "All regions, only those with a problem, or only new regions (not in the vanilla map)."),
        new("battles.regions.list", "Regions",
            "Each settlement with its battle kind now, the kind its buildings suggest, and its status. Double-click to go to it."),
        new("battles.regions.issues", "Details",
            "What the checks found for the selected region."),
        new("battles.regions.fix", "Fix",
            "Covers the settlement in both siege lists and, for a new or wrongly redirected region, redirects it to its suggested kind."),
        new("battles.regions.kind", "Kind",
            "A city layout, a port or a resource battle map to give the selected region. Vanilla clears its redirects."),
        new("battles.regions.apply", "Apply kind",
            "Gives the selected region the chosen kind. Refuses maps that would load with a hole in the middle."),
        new("battles.regions.fixAll", "Fix all",
            "Runs Fix on every listed region with a problem, as one undo step. Regions without a known kind are skipped and reported."),
        new("battles.regions.goto", "Go to",
            "Centres the map on the selected settlement and selects its walled siege area."),

        // ---- status
        new("battles.status", "Status",
            "The cell under the cursor, the areas covering it and whether there are unsaved changes."),
    ];
}
