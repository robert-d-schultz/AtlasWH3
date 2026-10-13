# Changelog

## Unreleased

- Settings: the game data cache, DB tables, output and cache folders are one **AtlasWH3 data** folder
  (`%LocalAppData%\AtlasWH3`), shown with Open and a discouraged Move… (settings `dataFolder`; the four old fields are
  no longer read). The game and assembly kit boxes show the paths found through Steam (`<game>\assembly_kit`) instead
  of grey placeholders, and the kit follows the game folder until you pick another.
- Settings is down to the folders, developer mode and walkthroughs. A map's mod packs live only in its project:
  - **Start page:** a **Project** picker. The editors open on the project's map and mod packs; without one, on a map
    from the Assembly Kit with the game's own packs only. Settings `currentProject`; `linkedPacks` and `tileMap`
    are no longer read.
  - **New project** asks for the mod pack the map ships in (it used to take the linked packs).
  - The Start page has no assembly kit dropdown (the kit is the one in Settings) and lists maps by name; a remembered
    map the kit lacks says so ("missing: the assembly kit has no <map>.terry").
  - Build all, Run selected and Pack only (and F5) are disabled until a project is open.
- Project settings hold only what Terry's project doesn't:
  - Gone: **Assembly kit** and **Game data** (Settings has them; the CLI keeps `--ak`), **Tile map source** (the
    build and the tile map editor use the Terry project's `tile_map.png`; the CLI's `--tilemap` still edits another
    file), **Delete before compile** (rivers and devastation_pieces already clear their own old files) and **Terrain
    backup folder** (the build never writes the Terry project).
  - **Compiled files** go to `%LocalAppData%\AtlasWH3\output\compiled\<map>`, shown in full; the Assembly Kit's
    `working_data` is no longer the default (older projects' `{ak}\working_data` is dropped on load; another
    `output` is kept).
  - **Other mod packs** and **Lookup: regions past 1024** are lists; **Accepted tile-map errors** is a tick box per
    error, each saying what it means.
  - The UI says Terry project or Assembly Kit, not "kit".
  - Custom steps moved to the bottom, marked advanced.
  - **Devastated project** is gone: the build uses the devastated map the DB names
    (`campaigns.terrain_location_devastate`, from the project's packs and the game's), as the Scene editor does,
    instead of guessing it from the map's name (`cr_combi_expanded_map_1` → `cr_combi_expanded_map_devastate_1`). The `{devastated}` token is gone; the CLI keeps `--devastated`.
- **One pack.** A project names its campaign's `.pack` (`packFile`), and Pack writes the compiled files into it in
  place: the build's files replaced, the rest kept (DB, scripts, map_data.esf, pathfinding.ppd), a backup copy kept
  first (`backupPack`), refused while the game runs; a missing pack is created. The pack is also read first. Install,
  output pack, merge base, contents (`{compiled}`), replace folders and leave-out are gone, as is the `install`
  segment (`afterInstall` custom steps run after Pack). An optional **pieces pack** (`piecesPackFile`) takes the event-area pieces (the map's `pieces\` and the
  devastated map's folder) for a campaign that ships them separately, like Old World's
  `!cr_oldworld_campaign_devastate.pack`. Older projects' `packs` and `install` become `packFile` (and a second
  pack `piecesPackFile`) on load.
- Removed **Prepare game data** and the CLI `setup` command: nothing copies game files out of the packs any more. The
  Scene editor, Campaign battles and the CLI's `trees-roundtrip` / `find-textures` read the mod and game packs
  directly. `--root` and a project's `compiledRoot` are gone; the `vanilla\` and `db\` folders in
  `%LocalAppData%\AtlasWH3` can be deleted.
- Removed the hidden Atlas3K terrain painter (`--painter`), the only reader of those copies.

## 1.0.0 (2026-10-13)

The first release of AtlasWH3: a Total War: WARHAMMER III campaign map builds from the Assembly Kit's sources in
one run, without BOB, and installs as a mod pack. IEE's full build takes about 5 minutes on a 4-core machine (a BOB
reprocess about 20, with pack round trips between actions).

### The build
- **Every BOB campaign action is native**, plus the AI pathfinding data the game otherwise builds itself:

  | Step | Writes | BOB action it replaces |
  |---|---|---|
  | `heightmaps` | `full_height_map.dds`, `full_logic_map.compressed_map`, `shroud_heights.dds`, `lf_normal.dds` | Campaign Heightmap, Campaign Shroud Heights |
  | `tile_list` | `tile_list.bin`, `tile_mask.dds`, `patch_mask.dds` | Tilemap |
  | `trees` | `trees.campaign_tree_list` | Campaign Trees |
  | `global_map` | `global_map\` (blend, `texture_arrays.xml`, tile list) | Global Tilemap, Campaign Global Blendmap |
  | `masks` | colour overlays, corruption, snow and event area masks | Color Overlay, Corruption / Snow / Event Area Mask |
  | `rivers` | `models\river_*` | Terry file (river export) |
  | `global_props` | `global_props.bin`, `global_props_sound.bin` | Terry file (props export) |
  | `environment` | `environment_collection.xml` (the map's and the devastated folder's) | Terry file |
  | `devastation_pieces` | `pieces\event_*`, `event_tiles`, `event_trees`, `event_vfx`, for the main and devastated maps | Devastation pieces, without a fake campaign |
  | `lookup` | `*_lookup.tga` / `.dds`, `_minimap.tga` | Convert lookup texture |
  | `camera_heightmap` | `camera_heightmap.png` | Generate Camera Height Map (fails in BOB) |
  | `hlp_spd` | `hlp_data.esf`, `spd_data.esf` | the game's own generation during a startpos build |

- **Parity** with BOB on the two test maps, IEE and The Old World:
  - byte-identical: the logic map, shroud heights, tile list, masks, piece textures and river models (but for two
    bytes BOB leaves uninitialised); on Old World also the trees, the global map and the lookups;
  - IEE's trees: every tree's id, position and rotation identical, and all but 23 of 253,903 heights within 1e-3
    (float rounding under the props' height patches);
  - global_props: the same objects in the same bodies as BOB. BOB's record order changes from run to run, so it is
    compared by content; the cells differ only where AtlasWH3 boxes a model by its real bounds, where BOB uses a
    2 × 2 × 2 cube for any model not loose in the kit;
  - `hlp_data.esf` and `spd_data.esf`: byte-identical to the game's own on IEE (apart from the timestamp).

  The remaining differences, and why, are recorded per step in [`docs/native_campaign_build.md`](docs/native_campaign_build.md).
- **Checked in game** on IEE: terrain, tiles, trees, props, rivers (lava rivers included), devastation, the camera,
  the region lookups and the AI ([`docs/end_to_end.md`](docs/end_to_end.md)).
- **Fixes over BOB:** `patch_mask.dds` covers the whole map (BOB's leaves a north band uncovered); the devastated map
  needs no fake second campaign; the camera height map builds; region lookups can move regions past the 1024 palette
  entries the game reads; quadtree cells use the models' real bounds; global_props takes its cultures from the packs'
  prefab_types (a mod's own cultures get their own buckets; BOB read the kit's vanilla table) and its regions from
  map_data.esf, the file the game reads (BOB read map.hex).
- **No working_data in between:** every step reads the kit's sources and the game and mod packs directly (CAIME's
  `map_data.esf` and `pathfinding.ppd` from the map's mod pack), and a step's inputs from other steps only from this
  build's output, never BOB's old files, so no intermediate pack is installed between actions. Independent steps run
  in parallel.

### Projects, pack and install
- A **project** (`.atlaswh3`) holds a map's build profile: Validate → Compile → Pack → Install, with your own commands
  before or after any segment.
- **Pack** takes exactly the files the build wrote (no BOB by-products from `working_data`), into a new pack or merged
  into a copy of the map's mod pack, which keeps the mod's DB, scripts and other files. A map can ship in more than one
  pack (Old World's main and devastate packs). River models and pieces the build no longer writes are dropped.
- **Install** copies the pack into the game's `data` folder through a temporary file, refuses while the game runs, and
  keeps one backup of the pack it replaces.
- The same build from the app's **Build** window or the command line (`AtlasWH3.Cli build --project <file>`).

### Tools for map authors and research
- `validate-tilemap`: tile-map pre-flight check before a build, with BOB's tile-matching simulation.
- `parity` and `fixture-*`: compare a build with BOB's output, against frozen source and output snapshots.
- `bob-*`: BOB headless in an isolated copy of the kit, for reference output.
- `db-*`, `hlp-spd`, `gp27-*`: DB tables, AI pathfinding and global_props research.

### Editors (preview)
- The Scene editor, Tile map and Campaign battles windows come from Atlas3K and are partly moved to WH3. See
  [`KNOWN_ISSUES.md`](KNOWN_ISSUES.md).

### From Atlas3K
AtlasWH3 is a hard fork of [Atlas3K](https://github.com/Ironictw2st/Atlas3K) 0.1.0-alpha.2, the same tooling for
Total War: THREE KINGDOMS. Its changelog up to the fork is
[Atlas3K's CHANGELOG.md](https://github.com/Ironictw2st/Atlas3K/blob/cdd0a08fa0a07db3bc2e7faba6822a75d5512d91/CHANGELOG.md).
Cut in the fork, as 3K-only: the battle terrain editor, the global mesh and climate map steps, seasons, the web
editor, the lake and terrain-shaping commands, the map audit, the Terrain painter's start-page entry and the 3K BOB
driver.
