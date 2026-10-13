# IEE end to end

The first full run of AtlasWH3 into the game: build IEE (`cr_combi_expanded_map_1`) from the kit, merge the result
into `!cr_immortal_empires_expanded.pack`, install it, and do the in-game checks of plan §7 (every step's "Left: the
in-game check", 3.1–3.10, 4.1–4.5 and 6.6) in one pass.

## The project

`C:\Users\rob\Desktop\tw modding\projects\AtlasWH3 IEE\iee.atlaswh3`:
- map `cr_combi_expanded_map_1`; the devastated project `cr_combi_expanded_map_devastate_1` is found by name;
- mod pack and merge base: `{game}\!cr_immortal_empires_expanded.pack` (the data folder's, read at build time, so
  edits made to it in RPFM before the run are kept);
- output `{project}\out`: the kit's `working_data` (BOB's outputs) is not touched;
- the merged pack is written to `{project}\!cr_immortal_empires_expanded.pack`, then Install copies it into `data`;
- Install's own backup is **off** (the user backs the pack up; C: has about 6 GB free, and the backup is 3 GB). Set
  `build.install.backup` to `true` to keep a copy in `%LocalAppData%\AtlasWH3\output\pack_backups`.

## Dry run (2026-10-11)

`build --project iee.atlaswh3 --segments validate,compile,pack`: succeeded in 497 s. Every step ran natively (no
BOB). Peak disk: `out\` 2.6 GB, the merged pack 2.97 GB.

| Step | Time | Notes |
|---|---|---|
| validate | 3.5 s | warnings only: 739 non-black filler pixels and 103,366 black hexes (BOB's tile list has the same) |
| heightmaps | 230 s | logic map 12800 × 7764, heights −17.27 .. 28.42 |
| hlp_spd | 248 s | **byte-identical to the pack's hlp_data.esf and spd_data.esf** apart from the 3 timestamp bytes |
| devastation_pieces | 237 s | 248 event areas, both folders; includes the devastated project's rasters |
| tile_list | 121 s | 339,338 records, 12 points without a tile (as BOB) |
| global_props | 53 s | 499,667 objects, 14,823 sound emitters; one model path with a doubled slash (`mudbanks//gen_mudbank_01`) is boxed 2 × 2 × 2 |
| others | < 45 s each | rivers (266), masks, environment, lookup, global_map, trees, camera_heightmap |
| pack | 5 s | 2,205 kept, 11,016 replaced, 412 added, 208 dropped |

Fixed after the user's first look: `camera_heightmap.png` had horizontal and vertical stripes in the eastern sea.
`FillOffMap` took IEE's sea there (about 0 in the logic map) for the map's edge, and copied coastal heights into it.
It is removed, and the project's `out\` and merged pack were rebuilt with the new camera map (camera_heightmap and
pack only).

Fixed after the user's second look: the main map's `environment_collection.xml` lacked vanilla's volumes. The env
layer `a_iee_environments` was corrected in the kit (backup in the project's `backups\`), and the devastated folder's
file is now a copy of the main map's. Rebuilt with environment and pack only.

## Dry run (2026-10-12, build speed)

The same build, timed (4-core i5-6600K): 517 s before the changes below, 280-297 s after (runs vary by about 10 s).
CPU work went from 1,371 to about 820 core-seconds. Every file in `out\` and the devastated cache is byte-identical
to the 517 s run's, except `hlp_data.esf` / `spd_data.esf`, whose timestamp differs (their content was checked equal
with the timestamp fixed).

| Change | Effect |
|---|---|
| devastated full_height_map.dds copies the blocks whose texels are the main map's from its file | 8.6% of IEE's blocks differ; the devastated encode went from ~126 s to ~12 s |
| BC6H: the error search stops at the error to beat; no arrays per line fit; the nearest palette entry 8 at a time (AVX) | encoder −34% |
| layer TIFs composited in parallel bands (strip-aware readers) | Height 4.8 → 1.8 s, overlays 1.9 → 0.7 s |
| devastated_tiles (from the start) and devastated_heights (after heightmaps) steps build the devastated project's cache; devastation_pieces writes both maps' pieces in parallel | devastation_pieces 231 → ~50 s |
| .layer files parsed ahead on other threads (global_props, devastation objects) | |
| hlp search: open-list positions instead of IndexOf | small |

Left as they are: hlp's region tables (40 s solo, already parallel, parity-exact A*) and the tile matcher (sequential
by design). The build is now bound by BC6H (~105 s of 4 cores solo) and contention while ten steps start at once.

Checked before the run:
- the kit's `map_data.esf` and `pathfinding.ppd` (hlp/spd's inputs) are byte-identical to today's pack's;
- `!cr_immortal_empires_expanded_start_pos.pack` holds only `startpos.esf` and DB tables, nothing under
  `campaign_maps/`, so it cannot shadow the build's files;
- the last launch's `mod_list.txt` loads the data folder's IEE pack, not the workshop copy (3007996493);
- against the live pack, the merged pack's file list differs only in:
  - the river models: 206 old `models\river_*` files out, 412 in (all 266 rivers, named by entity id);
  - the two pieces' `rivers` files, `event_9ca317` and `event_c14516` (lava river points around x 950–1005,
    z 168–210). They are from an older BOB run. IEE's lava rivers now use `cr_campaign_water_plane_river_lava`, which
    BOB's `cwb_campaign_river_lava` test does not match, so a fresh BOB run would not write them either (4.2).

## The run

1. Back up `data\!cr_immortal_empires_expanded.pack` (the user).
2. Close Warhammer3.exe (Install refuses while it runs), and close the pack in RPFM.
3. `AtlasWH3.Cli build --project "C:\Users\rob\Desktop\tw modding\projects\AtlasWH3 IEE\iee.atlaswh3"` (about 5
   min), or the Build window with the project open. To install the dry run's pack as it is instead, use
   `--segments install`. Only do that if the data folder's pack has not changed since the dry run (01:42 on 2026-10-12).
4. Install writes `data\!cr_immortal_empires_expanded.pack.tmp`, then swaps it in (3 GB free needed on D:).

Undo: copy the backup over `data\!cr_immortal_empires_expanded.pack`.

## In game

New IEE campaign (any faction), then (2026-10-12, the build with the lava rivers: all passed):
- [x] the campaign loads, no startpos crash
- [x] terrain and sea: heights (BC6H `full_height_map`), lf normals, the coast, no seams at tile edges
- [x] the sea floor under land in the far north (the fitted `patch_mask`; BOB's covered 1,925 of 1,941 rows)
- [x] shroud heights, colour overlay, sea colour, snow, corruption
- [x] tiles and roads; the global (zoomed-out) map's blend
- [x] trees in place, on the ground, under mountains' height patches
- [x] props in place; culture swaps (take a settlement with another culture); sounds
- [x] the pivoted props: `def_mountain_volcano_01`, the `ogr_large_mountain_01/02` group near (760, 310),
      `campaign_vortex_tear`
- [x] rivers, including the 266 renumbered models
- [x] the camera height: zoom low across mountains and patched props (AtlasWH3's file replaces the hand-made one)
- [x] region lookup: hover regions, the minimap, the court and elector counts small maps
- [x] devastation: an event area devastates and restores (objects, tiles, trees, VFX)
- [x] lava rivers: devastating one of the 12 `_fire` areas (table below) turns its listed rivers to lava, and restoring it
      turns them back (e.g. `wh3_main_combi_province_southern_badlands_fire`, 3 rivers). The game switches only the
      rivers a piece's `rivers` file lists; the build lists the devastated project's river splines whose material is
      `cwb_campaign_river_lava`, by origin. Bloodfire Falls' `wh_campaign_lava` river is listed but, as in vanilla,
      deleted by the swap (its material is not a water or blood river one). IEE's 7
      `cr_campaign_water_plane_river_lava` rivers (x 950–1005, z 165–210) are lava all the time.
- [x] the AI moves (end several turns: armies by land and sea, ports, bridges)
- [x] no new script errors (`script_log_*.txt`)

### The devastated project's rivers (2026-10-12)

CA's devastated combi map holds all 206 rivers, each identical to the main map's (the 127 model files that differ do
so only in two header padding bytes), 26 of them with `cwb_campaign_river_lava`: 25 whose origin is in one of 12
`_fire` areas (vanilla's 25 `rivers` records) and `river_17eddb13a82fedf` at (418, 78), outside every area. Other
rivers in fire areas (32) stay water.

IEE's devastated project had only IEE's own 60 rivers (one, `18f1c9dc0448720`, with the main project's height from
before 2026-10-03). It now holds all 266 main-project rivers, copied whole (same entity ids and positions; the game
matches a record to the main map's river by x and z), the 26 above with the lava material, each in the layer of the
event area its origin is in (IEE's own event mask), or `_global` (20). Backups of every layer touched:
`projects\AtlasWH3 IEE\backups\*.2026-10-12.bak`. The build writes 25 records in 12 devastated pieces.

| Area | Lava rivers |
|---|---|
| `the_abyssal_glacier_fire` | 5 |
| `southern_badlands_fire`, `the_daemonium_hills_fire` | 3 each |
| `western_jungles_fire`, `titan_peaks_fire`, `vanaheim_mountains_fire`, `southern_jungles_fire`, `bloodfire_falls_fire` | 2 each |
| `the_turtle_isles_fire`, `mountains_of_hel_fire`, `the_black_flood_fire`, `the_shard_lands_fire` | 1 each |
