# Changelog

## 1.2.1 (2026-11-10)

Props, sounds and lights on the campaign map now come out of the build the way BOB makes them.

- Fixed: sounds drawn as a line in Terry (such as IEE's hot rivers) didn't follow the ground.
- Fixed: props, sounds, lights and scenes set to a DLC in Terry weren't tied to that DLC in the game.
- Fixed: props, lights and polygon meshes with no visibility settings in Terry were hidden in the tactical view.
- Fixed: a polygon mesh's snow, destruction and shroud-only settings, a scene's hold-first/last-frame settings and
  cylinder-shaped probes were ignored.

## 1.2.0 (2026-10-31)

AtlasWH3 now reads and writes packs through RPFM, and the shroud is filled from the terrain.

### RPFM
- **RPFM 5.3 or newer is required.** Every pack is read and written through RPFM's server, and RPFM's schema decodes
  the DB tables, so a game patch that changes a table only needs RPFM's schemas updated. AtlasWH3 starts the server
  when it isn't running.
- **Settings** has a new **RPFM** folder, the one holding `rpfm_server.exe`.
- Fixed: every language pack was read along with the game's own, so `local_zh.pack` replaced some English files, such
  as the campaign maps' `_text.dds`. Only the game's own language pack is read now.

### The build
- `shroud_heights.dds` is filled from the terrain, as Terry's Fill From Terrain does: the Height map at half
  resolution, raised by the props' height patches. The Terry project's HeightShroud layers are no longer read (BOB
  copied them).
- Two new build-project settings, both off by default:
  - **Keep props outside the quadtree:** a prop, decal, effect or sound emitter reaching past the quadtree goes into
    its nearest cell instead of being dropped, as BOB drops it.
  - **Shroud at 0 along the west edge:** `shroud_heights.dds` is 0 over the map's westmost 10 units, for a visual
    extension west of the map.
- Fixed: devastation variants kept in typed groups inside a prefab (CA's devastation prefabs) went into the plain
  objects, so they showed for any devastation (Nagash props under Chaos).
- Fixed: the pieces pack kept BOB's left-over `campaign_maps\<devastated map>\` folder, which the game doesn't read.
  It is now dropped.

### Command line
- New `extract`: files of the vanilla packs (`--vanilla`) or one pack (`--pack`) into a folder, at their paths.
- `db-dump` reads through RPFM. Removed `db-check` and `db-schema-snapshot`.
- `build-campaign`: `--keep-outside-quadtree` and `--zero-shroud-west`.

### Upgrading from 1.1
- Install RPFM 5.3 or newer, update its schemas, and choose its folder in Settings.

## 1.1.0 (2026-10-13)

Fewer settings, plainer words. Everything about a map now lives in its build project, and the build writes straight
into your campaign's own pack.

### Settings and the Start page
- **Settings** is down to the game and Assembly Kit folders (found through Steam and shown filled in), AtlasWH3's own
  data folder (`%LocalAppData%\AtlasWH3`, shown with Open; moving it is possible but not needed), developer mode and
  the walkthroughs.
- Removed **Linked packs**, the global **Tile map source** and **Prepare game data**: the editors and the build read the
  mod and game packs directly, so nothing is copied out of them any more.
- **Start page:** a **Project** picker. The Scene editor, Tile map and Campaign battles open on the project's map and
  packs; with *No project*, on a map from the Assembly Kit and the game's own packs. The Assembly Kit is the one in
  Settings (no dropdown), maps are listed by name, and a missing one says why.

### Build projects
- **One pack.** A project names your campaign's `.pack`, and the build writes the compiled map into it in place: its
  own files are replaced, everything else (DB, scripts, map_data.esf, pathfinding.ppd) is kept. It keeps a copy of
  the pack as it was first (`output\pack_backups`), writes beside it and swaps it in, refuses while the game runs, and
  creates the pack if there is none. Install, output pack, merge base, pack contents, replace folders and leave-out are
  gone.
- An optional **pieces pack** takes the event-area pieces (the map's `pieces\` and the devastated map's folder) for a
  campaign that ships them in a pack of their own, like Old World's `!cr_oldworld_campaign_devastate.pack`.
- **New project** asks for the pack the map ships in.
- **Project settings** hold only what the Terry project doesn't: the pack (and pieces pack), other mod packs to read,
  the compile options, and custom steps (now at the bottom). Removed: Assembly kit and Game data (Settings has them),
  Tile map source (always the Terry project's `tile_map.png`), Output folder, Delete before compile, Terrain backup
  folder and Devastated project.
- **Compiled files** go to `%LocalAppData%\AtlasWH3\output\compiled\<map>`, shown in full. The Assembly Kit's
  `working_data` is no longer written.
- **Devastated map:** the one the DB names (`campaigns.terrain_location_devastate`), as in the Scene editor, instead
  of a guess from the map's name.
- **Other mod packs** and **Lookup: regions past 1024** are real lists; **Accepted tile-map errors** is a tick box per
  error, each saying what it means.
- Build all, Run selected and Pack only are disabled until a project is open.

### Wording
- The UI says **Terry project** for a map's sources and **Assembly Kit** for the install, never just "kit". The Get
  started text, walkthroughs and info cards are rewritten to match.

### Command line
- Removed `setup` and the `--root` option. `build --segments` no longer has `install`; `--pack-output` sets the pack
  the build writes into. `trees-roundtrip` and `find-textures` read the packs (`find-textures` now finds every
  texture).
- Removed the hidden Atlas3K terrain painter (`--painter`).

### Upgrading from 1.0
- Projects convert when opened: their `packs` and `install` become the pack (where Install put it) and, for a second
  pack, the pieces pack; the old `{ak}\working_data` output default is dropped; `assemblyKit`, `gameData`, `tileMap`,
  `clean`, `backup` and `devastatedMap` are ignored. Save the project to write it in the new form.
- The old `vanilla\` and `db\` folders in `%LocalAppData%\AtlasWH3` are no longer used and can be deleted.

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
