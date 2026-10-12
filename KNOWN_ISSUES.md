# Known issues

## Build

- **Checked in game on IEE only.** The whole build of `cr_combi_expanded_map_1` went into the game on 2026-10-12 and
  passed every check in [`docs/end_to_end.md`](docs/end_to_end.md). Old World and other maps are checked against
  BOB's output (byte or content parity), not in game.
- **Start positions are not built.** `startpos.esf` still comes from the game (RPFM's start-position build, for
  example). Add it as a custom step after Pack if you need it.
- **CAIME's files are inputs.** The build does not write `map_data.esf`, `pathfinding.ppd` or the region lookup
  `.bmp`. It reads `map_data.esf` and `pathfinding.ppd` from the map's mod pack (link it), and the lookup `.bmp` from
  the kit's working_data or EmpireDesignData folder. It does not read `map.hex` (only the Scene editor does).
- **A new project builds into the kit's `working_data`**, as BOB does, and with a linked mod pack its Install replaces
  that pack in the game's `data` folder (keeping one backup copy, unless the project turns it off). Set the project's
  output to its own folder to leave `working_data` alone.
- **Height map:** `full_height_map.dds` is BC6H from AtlasWH3's own encoder. It is not byte-identical to BOB's (AMD
  Compress); its error against the source heights is about half BOB's.
- **Tile list:** byte-identical to BOB's on IEE and Old World. A map whose hex width is not a multiple of 4 reports
  `layout.mesh_columns`. Accept it in the profile to build anyway; BOB would also leave notches beside road tiles there.
- **Lookup textures:** the game reads only the first 1024 colours of a region lookup's palette. On a map with more
  regions than that (Old World), list the regions to move past entry 1024 in the build profile ("Lookup: regions past
  1024", CLI `--lookup-last`); the step names any region still past 1024.
- **AI pathfinding:** `hlp_data.esf` and `spd_data.esf` are built from the `map_data.esf` and `pathfinding.ppd` in
  the map's mod pack. On Old World one hlp value differs from the game's own build, where the game's A*
  has a bug; AtlasWH3 keeps the correct value.
- **Camera height map:** the `camera_heightmap` step samples the logic heights and the layers' height patches the way
  BOB's action does, but not BOB's tile terrain, at CA's resolution (0.25 cells per tile-map pixel). It is not
  byte-identical to anything: BOB's own action fails on WH3's kit.
- **Global props:** `global_props.bin` matches BOB's content, not its bytes: BOB's own runs differ in record order
  and in the numbering of the mountain bodies. Differences on purpose:
  - props are put in quadtree cells by their models' real bounds. BOB treats any model whose `.wsmodel` isn't loose
    in the kit as a 2 × 2 × 2 cube, so a big prop can land in a cell smaller than itself;
  - cultures come from the packs' `prefab_types`. IEE's 23 own cultures (values 39-61) get their own buckets, where
    BOB, reading the kit's vanilla table, put those objects in the NONE bucket; the mountain bodies then start at 992,
    not 640. Not yet checked in game;
  - regions come from `map_data.esf`. They are `map.hex`'s wherever both come from one CAIME export.
- **Devastation pieces:** `event_tiles` lists the same road tiles as BOB's, in path-table order; BOB's order comes
  from a hash map and is not reproducible.
- The build runs while editors stay open. Saving an edit during a build can make the output mix old and new data;
  rebuild afterwards.

## Editors (preview)

- The Scene editor, Tile map and Campaign battles windows are previews: parts of them are still Three Kingdoms-shaped.
  - The tile map validator's neighbourhood rules and reference pattern are 3K's; tile props and some tile textures
    are not drawn yet, and some props draw black.
  - The Campaign battles window does not read WH3's `battle_locations_map.bin` yet.
  - The Terrain painter is hidden: it reads 3K's lf height rasters, which WH3 maps don't have.
- The `tiles-*`, `props-*` and `entity-*` CLI commands share the editors' code and status.

## App

- **Prepare game data** extracts one map at a time. Run it again for another map.
- Folder changes in Settings apply to windows opened after saving.
- The download is unsigned, so Windows SmartScreen may warn on the first start (More info › Run anyway).
- Windows 10/11 x64 only. The 3D view needs Direct3D 11.
