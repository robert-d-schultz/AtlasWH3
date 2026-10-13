# Battle map editor (campaign-battle terrain)

Atlas3K has two battle windows:
- **Campaign battles**, described first: the compiled catchment areas and each settlement's battle, for a mod that ships
  its own `battle_locations_map.bin` (190E).
- **Battle map (experimental)**: the BOB sources of a campaign-battle terrain, further down.

## Campaign battles window

Open it from the start page tile *Campaign battles*, or with
`Atlas3K.exe --campaign-battles [--map <campaign map>] [--pack <mod.pack>]`.

### What it reads

Nothing it reads is ever written.

| What | From |
|---|---|
| Catchments | `terrain/battles/3k_main_map/battle_locations_map.bin` from the linked packs, then the game packs (vanilla: `fast.pack`), or a loose file (*File › Open catchment file*) |
| Settlements | `campaign_maps/<map>/map_data.esf` (REGIONS_BLOCK → SETTLEMENT_INFO), linked packs first; the vanilla copy tells new regions from vanilla ones |
| Painted cities | `terrain/battles/3k_main_map/tile_map.index/.tiles`, for settlements without a redirect |
| Names, suggested kinds | an RPFM extract of the mod (*File › Mod data folder…*): `text/db/*.loc.tsv`, `campaign_settlement_display_settlement_layouts_tables`, `start_pos_settlements_tables` (primary building) |
| Known battles | every pack's binary `db/battles_tables/*`, scanned for its `terrain\battles\…\` paths and types without a schema, plus RPFM TSVs (mod data, DB folder) |
| Redirect targets | `terrain/battles/<folder>/tile_list.bin` in the packs |

### Grid facts

- The catchment boxes and the land/sea grid are stored with row 0 = north.
- Settlements (hex y, row 0 = south) map to catchment rows as `H − 1 − y`.
- The meta item names are CA labels, not geography. The battle landmass is the item the area centres sit on, which is
  index 1 ("sea") on 3K.
- The campaign map must be the battle grid's size: 892×702 hexes against 892×703 cells.
  - A larger map such as `3k_190e_expanded_map` (1478×1133) is reported, and every region shows *Off the battle grid*.
  - Its battle terrain (catchments and tile map) has to be resized to the campaign map first.

### Region status

These rules come from the blm tool's RegionBattleService.

| Status | Meaning | Fix |
|---|---|---|
| OK | Covered and redirected right. Also OK: unfortified-only, as vanilla resource towns, and passes covered by a gate battle. | — |
| No catchment | Neither siege list covers the settlement | Adds 15×15 `<region>_std` / `_unf` areas. A new region is also redirected to its suggested kind. |
| Half covered | A walled area but no unfortified one | As above |
| Wrong type | Redirected to another family than the region's (e.g. a city map on a lumber camp) | Redirects to the suggested kind |
| Redirect missing | A new region with no redirect and no painted city tile, so its battle has no settlement | Redirects to the suggested kind |
| Redirect invalid | The folder is missing, or a settlement/resource map does not embed its centre prefab (the battle loads with a hole) | Redirects to the suggested kind |
| Off the battle grid | Outside the grid, or the map is the wrong size | Not fixable here |

Kinds and their redirect folders:
- City a–h → `settlement_city_han_<l>_small_walled` (walled siege list) and `_small` (unfortified list).
- Port a–b → `settlement_port_han_<l>_small_walled` and `_small`.
- Resources → one embedded map each, for example lumber → `resource_han_lumber_a_small` and tools → `resource_han_tools_a_regular_large`.

### battles_tables rows

Vanilla redirect targets already have rows. Save writes a row only for redirect folders no known row points at, to
`output\campaign_battles\db\battles_tables\atlas3k_campaign_battles.tsv` (RPFM TSV, version 10).
- **Paths:** `specification` and `map_path` both hold `terrain\battles\<folder>\`. Custom battles read `specification`,
  and the redirect proven in game used `map_path`.
- **Type:** the list's battle type (settlement_standard → siege, settlement_unfortified → unfortified_settlement,
  gate_battle → gate_battle).
- **Maps that bring their own row:** for a map whose pack already ships its row, tick *Its pack ships the battles_tables
  row*. The choice is remembered.
- **Open item:** `battle_redirection` is proven in game on gate catchments; on settlement catchments it is still
  unverified.

### Output

| Action | Writes |
|---|---|
| Save (Ctrl+S) | `output\campaign_battles\terrain\battles\3k_main_map\battle_locations_map.bin` + the row TSV |
| Export mod pack… | a new `.pack` holding the catchment file; add the TSV rows with RPFM |
| Write to kit | `<kit>\working_data\terrain\battles\3k_main_map\battle_locations_map.bin`, after a backup in `output\backups` |

Writing into the game's `data` folder or into a linked pack is refused (`SourceGuard`).

Code:
- `Atlas3K.Formats/Battle/BattleLocations.cs` (codec, byte-exact)
- `Atlas3K.Core/Battle/CampaignBattle{Rules,Regions,Workspace}.cs`
- `Atlas3K.App/CampaignBattlesWindow.cs`, `CampaignBattleView.cs`
- Tests: `CampaignBattleTests`

## Battle map (experimental): BOB sources

Atlas3K's battle mode edits the **BOB sources** of a campaign-battle terrain: the land that campaign battles are
composed from, picked by `campaign_map_playable_areas.terrain_folder` (vanilla: `terrain/battles/3k_main_map/`).
CA ships only the compiled files. The sources were reverse-engineered in the BattleMaps project; the formats are in
`Z:\Claude\BattleMaps\docs\bob-battle-terrain-sources.md`, and a ready-made source package for vanilla `3k_main_map`
is in `Z:\Claude\BattleMaps\out\share\3k_main_map_bob_sources`.

## Opening

| Way | How |
|---|---|
| Directly | `Atlas3K.App.exe --battle "<kit>\raw_data\terrain\battles\<map>"` (only the battle editor opens) |
| From the campaign editor | *File → Open battle project…*, then pick the folder holding the `.terry` |

A project folder holds:
- `<map>.terry` and its catchment `.layer`
- `tile_map.png` and `climate_map.png`
- `explicit_tiles.txt`
- the height TIFFs: Terry-named `<map>.height.<id>.tif` / `.sea_height.<id>.tif`, plus `lf_heights.tif` / `lf_sea_heights.tif` for BOB
- `battle_location_map_meta_data.xml` + `blm_land.png`
- `rules.bob`

The palette needs the kit's `raw_data\terrain\battles\tile_placement_groups.xml` and the extracted battle tile
database (default `Z:\Claude\TerryClone\Vanilla\terrain\tiles\battle\_tile_database`).

## Tools

| Tool | Use |
|---|---|
| Paint tile map | Paints the selected palette colour. Exact colours only: BOB matches RGB exactly, and anything else (e.g. black) places no tile. Option: paint only over the colour where the stroke starts. |
| Fill region | Recolours a connected patch of one colour. |
| Eyedropper | Picks the colour under the cursor. |
| Raise / Lower / Smooth / Flatten / Noise | Height brushes on the land or sea raster (*Height brushes edit*). Water shows where the sea is above land. |
| Settlement/resource tiles: select, move | Click a tile to select it and drag to move it. **R** rotates, **Del** deletes. |
| Settlement/resource tiles: place | Pick a tile in the list; a ghost follows the cursor; click to place. **R** turns the ghost. |
| Catchment areas: select, move, resize | Click an area (the smallest one under the cursor; **Tab** cycles overlapping ones). Drag inside to move it, drag an edge or corner to resize, drag the cross to move the infield centre. **Del** deletes. Types, redirect, culture and exact numbers are in the side panel (*Apply*). |
| Catchment areas: draw new | Drag out a box; it gets the ticked types and a new id. |

Encampment areas are not edited: BOB generates one per road-corner tile on land.

Explicit tiles that overlap another tile or leave the map are drawn red (*Build → Check explicit tiles*). BOB stops
at the first such tile.

## Saving and building

- **Ctrl+S** writes only what changed. Both height copies are written. Every overwritten file is first copied to
  `output\backups\battle_<map>_<time>`.
- **Build → Build with BOB** works only for a project inside a kit (`<kit>\raw_data\terrain\battles\<map>`). It:
  1. checks that `rules.bob` has `save_meta_data_map = true`, and offers to write it;
  2. warns about explicit-tile overlaps;
  3. refuses while BOB is running in the same kit, and asks when one is running from another kit;
  4. saves;
  5. runs BOB headless: *Terrain / Tilemap*, then *Terrain / Low frequency data*.

  Output goes to `<kit>\working_data\terrain\battles\<map>`. Pack it together with the vanilla `tile_map.bmd` (a
  static file).
- **Build → Compare…** reports build vs. a reference folder:
  - which files are identical;
  - each catchment list, area by area, plus the land grid;
  - the % of cells with the same tile set;
  - settlement/resource tiles in the same place and rotation.

  After a build it compares automatically when the extracted vanilla map exists under
  `Vanilla\terrain\battles\<map>`.

A vanilla rebuild from the package gives a byte-identical `battle_locations_map.bin`, `climate_map.cm` and height
DDS files, all settlement/resource tiles in place, and ~92% of cells on the same tile set. The rest is BOB's random
choice within mixed groups.

## Code

- `Atlas3K.Formats/Battle/`
  - `BattleTileDatabase` (sets, climates, tiles, masks)
  - `BattlePalette`
  - `ExplicitTilesFile`
  - `BattleCatchmentLayer` (read/write layer entities)
  - `BattleCompiledFiles` (compiled `battle_locations_map.bin`, `tile_map.tiles` — the path index is **1-based**)
- `Atlas3K.Core/Battle/`
  - `BattleProject` (load / changed parts / save)
  - `BattleTools` (paint, fill, height adapter)
  - `BattleObjects` (explicit-tile footprints and conflicts, list undo)
  - `BattleRenderer`
  - `BobBattleBuild` (rules, headless BOB, compare)
- `Atlas3K.App/`
  - `BattleWindow` (+ `.Objects.cs`)
  - `BattleMapView`
  - `BattleInteractions`
- Tests: `BattleProjectTests`, `BattleObjectTests`. Footprints are checked against every settlement/resource tile in
  vanilla's compiled tile map.
