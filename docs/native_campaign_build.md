# Native campaign build (BOB replacement)

Atlas3K rebuilds BOB's campaign-map outputs itself, straight from the assembly-kit sources. Intermediate files are
read from disk, so no pack import is needed between steps. Plan: `~/.claude/plans/we-have-a-bob-snazzy-valiant.md`.

## WH3 steps (AtlasWH3)

AtlasWH3's per-step parity record on the WH3 fixtures (plan Phase 3). Everything from *Running it* on is Atlas3K's
Three Kingdoms record, kept for the method and the 3K rules that still hold.

| Step | Replaces BOB action | Parity (IEE `cr_combi_expanded_map_1`, Old World `cr_oldworld_map_1`) |
|---|---|---|
| `heightmaps` | Campaign Heightmap, Campaign Shroud Heights | 2026-10-08, against the user's BOB output. `full_logic_map.compressed_map`: byte-identical on IEE and Old World. `shroud_heights.dds`: byte-identical on IEE and Old World. `full_height_map.dds`: header byte-identical; the BC6H blocks are AtlasWH3's own encode (`Bc6h`), not AMD Compress's, see below. Not yet checked in game |
| `tile_list` | Tilemap | 2026-10-09, against the user's BOB lists. Old World: `tile_list.bin` and `tile_mask.dds` **byte-identical** (598,161 records). IEE: `tile_mask.dds` byte-identical; every record identical (339,338) but 473 low/high pairs (452 on sea tiles, x 2814-3068, y 412-730), where the user edited the height layers after that BOB run. The same 12 IEE points left without a tile as BOB. IEE 61 s, Old World 139 s. Not yet checked in game |
| `trees` | Campaign Trees | 2026-10-08, against the user's BOB lists. Old World: **byte-identical** (507,966 trees). IEE: every tree id, position and rotation identical; heights 238,143 of 253,903 bit-exact (93.8%), 253,446 within 1e-3, 457 beyond (max 3.1), see below. IEE 12 s, Old World 6 s. Not yet checked in game |

### tile_list.bin and tile_mask.dds (WH3)

Read off warscape.modder.x64.dll (capstone, 2026-10-09; its EDITOR_TILE_MAP / TILE_MAP functions are exported by name)
and measured on the fixtures:

- **Format** (`TileList`, v2): the path table sorted (ordinal), not in first-use order as in 3K; one climate,
  `default`; floats (0, 0, 0, 0, 500, 1.333); ints (0, W, H, W/2, H/2, 0, 0, x0, y0, W + 2, H + 2) with W × H the tile map
  and (x0, y0) the lowest anchor − max(w, h); records cell by cell from the south, layer 1 before layer 2, flag 7; one
  trailing 0 byte.
- **Heights:** `TILE_MAP::calculate_lf_min_maxs` (0x1805b62b0; bob_terrain imports this one, not BATTLE_TILE_MAP's)
  takes min/max over [anchor − 2, anchor + max(w, h) + 2) of the composited Height map, HeightSea for use_alt_lf tiles.
  The box is mapped by **multiplying with 1/W and 1/H**: 1/4096 is exact, 1/3549 is not, so on Old World about a fifth
  of the boxes take one more texel row at the north end (dividing, as BATTLE_TILE_MAP does, gets 82% right). A NaN
  texel counts as 0 (Old World has one). Values are world units straight from the float TIFs. BOB's Tilemap reads the
  heightmap from the installed pack; the step reads the sources, so that round trip is gone too.
- **use_alt_lf** is the byte after barbarian, which follows the links: v5 tiles have one more byte in front of the
  two, v6+ end with geometry_blend_edge_left/top/right/bottom (+1 byte in v9). Set on the 20 sea tiles (and
  sea_coastx1_straight).
- **tile_mask.dds:** L8, one byte per tile-map point, rows from the south. Each tile on the point (both layers) adds 16
  (land) or 32 (use_alt_lf), nothing when its set is exclude_from_global_mesh (roads, cliffs, coasts); no tile = 64.
- **Matching:** Atlas3K's `TileMatchSimulator` with two WH3 changes, read off `EDITOR_TILE_MAP::scan_for_tiles`
  (0x1805f1540) and `scan_tile_areas` (0x1805f3ef0):
  - passes 2-5 (transition, junction, link target, linked) visit a point list, not every point: the points with a
    group that have, in [x − 3, x + 3) × [y − 3, y + 3), a point of a group holding a tile set (`group_is_linked`;
    `tile_set_to_link_as` is never empty), row by row. So no tile has its origin on a black point: before, the masked
    `*_tri` tiles were placed there 270 (IEE) and 543 (Old World) times, and the random draws drifted from there.
  - the junction pass's 2×2 strip rule (after `test_final_tile_position` and its draws) also skips the tile when any of
    its 8 neighbour points (columns x − 1 and x + 2, rows y − 1 .. y + 2, indexed linearly) is outside the map. 3K's port
    counted those as another group; on IEE's south edge that placed cliffs BOB does not.
  The rest is 3K's: `TILE_DATABASE::sort` (area, the +0x11c count, name) and `std::sort` (32-element insertion sort,
  1.5·log2 N budget), the tile files from tiles_campaign.pack (already in ordinal order), `test_final_tile_position`,
  `links_match` (off-map links skipped), the xoroshiro128+ draws. Not reproduced, and not met on the fixtures:
  `space_free_for_tile` checks x ≥ W and y ≥ H but not negative coordinates, which then index linearly (a wrapped row,
  or memory before the group array). The simulator rejects them.
- **Speed:** a point only tries the masked candidates and the unmasked ones its group matches (an unmasked tile fails
  at once, before any draw, on a foreign group); the final pass visits only matching points; sub-tile, link and target
  offsets are precomputed per rotation. Same placements, matching IEE 34 s (was 110 s), Old World 85 s (was 293 s).

### trees.campaign_tree_list (WH3)

Read off qttoolutility (capstone, 2026-10-08) and measured on the fixtures:

- **Grid:** map_data.esf's CAMPAIGN_THEATRE bounds (`MapDataBounds`) and the composited CampaignTree map (2 px per hex;
  255 = empty). Not the campaign_map_playable_areas row: Old World's row is IEE's 1068.1111 × 748.1, its map_data.esf
  says 1367.396 × 1368.7428, as the tree list header does. IEE's map_data.esf is **CBAB** (CAAB with u32 string-table
  lengths); `EsfTree` reads both.
- **Placement:** 3K's rules (minstd_rand per hex; id, z jitter, x jitter, rotation) with one change:
  z = row + (jitter − 0.2), where 3K had (row + jitter) − 0.2. Every IEE and Old World tree agrees.
- **Tree ids:** BOB placed the mods' own trees, so `TreeDatabase.FromPacks` reads campaign_tree_ids from the linked mod
  packs in front of vanilla db.pack (IEE's `cr_iee_campaign_trees` adds 28 ids, Old World's 34). The kit's
  raw_data\db XML is vanilla only. Hexes painted with a colour no row has get no tree and are listed in the notes.
- **Height** (`TreeHeightField`): `TerrainSurface::height` = `height_split`'s (hf, lf) → `sample_height_patches(lf) + hf`.
  - The campaign provider (vtable 0x5fec10, +0x70) reads the nearest full_logic_map texel: z' = z / 1.15476,
    texel (trunc(w · x / width), trunc(h · z' / depth)), row 0 south, value raw · (1/65535) · (hi − lo) + lo, hf = 0.
    **width is the .terry's world_width** (Old World 1367.4, IEE 1068.11), not map_data.esf's; depth = h · (width / w).
    With map_data's width Old World had 95.9% bit-exact; with the .terry's, 100%.
  - Height patches (`sample_height_patches` 0x12f980, `add_height_patch` 0x12f090, record builder 0x135bc0, inverse
    0xfea70): every entity of every layer file with apply_height_patch (and not camera-only); the x/z part of its QTU
    world matrix inverted into model space; the patch's model bounds from its compressed-map header; bilinear sample
    with f = (l − min) / (max − min) · size (not size − 1); v ≥ 0 only; height = v · |column 1| + position y, plus lf
    when a material has add_terrain_height; the highest one wins over lf.
  - Patch files come from the packs (vanilla plus the linked mod packs), not working_data, so campaign_tools'
    height_patches.py export is no longer needed. add_terrain_height is read from each material's .xml.material.
  - BOB's tree pass reads the logic map through the game's file system (the installed pack); the step reads the
    build's own full_logic_map, so the heightmap → trees pack round trip is gone.
- **Open (IEE):** 15,303 trees within 1e-3 but not bit-exact, almost all under height patches: a few to a few hundred
  ulps, both signs, also on yaw-only unscaled props. Nudging a prop's yaw fixes some of its trees but never all, so
  BOB's entity matrix differs from `QtuTransform`'s by more than the angle text (not yet found). 457 trees beyond
  1e-3, not yet explained: some under patches (before the width fix, most of those sat under
  ogr_large_mountain_01 and def_mountain_volcano_01 props, where BOB is lower), the rest terrain-only.

### full_height_map.dds (BC6H_SF16)

Error of the decoded texture against the composited Height (red) and HeightSea (green) sources, per texel:

| Map | Encoder | Red RMS / max | Green RMS / max | Time |
|---|---|---|---|---|
| IEE (12800 × 7764) | AtlasWH3 | 0.0031 / 2.63 | 0.0019 / 1.08 | whole step 160 s |
| IEE | BOB (AMD Compress) | 0.0063 / 2.55 | 0.0082 / 4.44 | |
| Old World (16384 × 14196) | AtlasWH3 | 0.0052 / 1.25 | 0.0063 / 2.30 | whole step 307 s |
| Old World | BOB (AMD Compress) | 0.0127 / 2.11 | 0.0126 / 4.13 | |

Old World's composited Height has **one NaN texel** (composite x 16046, row 13162 from the bottom). BOB's BC6H writes
0 there; AtlasWH3's fills it with its block's mean (4.90), so it does not drag the 15 other texels of the block.

The largest errors are in blocks where a channel crosses 0: BC6H interpolates the half-float bits, so a gradient
through 0 bends, and both encoders lose most there. Three encoder bugs were found by this comparison (2026-10-08):
- a least-squares refit that was no better (or did not fit the mode) was kept with the old indices;
- the vendored BCnEncoder.NET layout for mode 7 (8.6.5.5, `Type18`) stored bit 4 of the subset-1 red deltas where bit
  5 goes;
- the NaN texel entered the fit as 65504, and its block's other texels were off by up to 5.

Before the first two fixes, about 900 IEE blocks decoded to values in the hundreds. `Bc6hTests` covers all three.

## Running it

| Way | Command |
|---|---|
| CLI | `Atlas3K.Cli build-campaign [--map <map>] [--steps a,b] [--out <dir>] [--json]` |
| CLI | `Atlas3K.Cli diagnose-campaign [--map <map>]`: per-step readiness |
| CLI | `Atlas3K.Cli parity <builtDir> <referenceDir> [--mask-junk]`: byte comparison |
| MCP | `terry` server (`tools/terry_mcp/server.py`): `build_campaign`, `build_step`, `diagnose`, `parity_check`, `list_outputs`, `rebuild_tool` (prop tools below) |

- **Output location:** by default the output goes to `output/compiled/<map>/`, laid out like `working_data`: `terrain/campaigns/<map>/…` and `campaign_maps/<map>/…`.
- **Straight into the kit:** `--out <kit>/working_data`, or `to_working_data=true` in the MCP tools, writes into the kit and replaces BOB's files there.
- **Step order:** steps run in dependency order, and independent steps run in parallel. On dlc07 the whole native set takes about 24 s (global meshes 21 s, in parallel).

## Prop editing (AK layers)

The `terry` MCP server also edits the props in the kit's region layers (`<map>.<id>.layer`), the way the battlemap MCP drives Dungeondraft.

| Way | Commands / tools |
|---|---|
| CLI | `props-layers`, `props-list`, `props-get`, `props-models`, `props-ground`, `props-edit --ops <json>`, `props-undo`, `props-checkpoint`, `props-rollback`, `props-history`, `props-preview` (all print JSON) |
| MCP | `list_prop_layers`, `list_props`, `get_prop`, `list_prop_models`, `ground_height`, `edit_props` (batch), `add_props`, `move_props`, `transform_props`, `set_prop_properties`, `duplicate_props`, `delete_props`, `prop_checkpoint`, `prop_rollback`, `prop_undo`, `prop_history`, `preview_props` (returns the image), `rebuild_props` |

- **Code:** `LayerDocument` (Formats/Terry) edits single entities and writes Terry's exact layout. Loading and saving an unedited layer gives identical bytes; this is tested on all 265 dlc07 layers. `PropEditor` (Core/Editing) adds op batches, history, ground sampling and the region lookup. `PropPreview` (Core/Rendering) draws the previews.
- **Undo:** each batch snapshots the layers it touches into `output/prop_edits/<map>/` (a copy of the kit gets its own folder). Undo and rollback refuse to restore if a layer changed outside the tools since then.
- **Season variants:** these are separate props at one spot. `group: true` applies an op to all of them.
- **Ground height:** `ground_y` comes from the lf height TIF. Campaign relief such as mountains is made of props, so trees often stand above the lf ground. `snap: "relative"` keeps a moved object's height above the terrain.
- **Compiling:** edits reach `global_props.bin` only through `rebuild_props`, which runs the `global_props` and `camera_heightmap` steps.

## Steps

| Step | Replaces BOB action | Status | Parity (vanilla 3k_dlc07) |
|---|---|---|---|
| `rasters` | Height map compressed / DDS (land, sea), climate map | native | byte-identical |
| `tile_list` | Tilemap | native | **byte-identical to BOB on vanilla** (2026-10-04: `tile_list.bin`, and `global_map\` with it). Fixes: `TILE_DATABASE::sort` runs before link targets are loaded (all 0), so the DB order is area then name; `calculate_flow` neither flows nor queues a tile with no TLT_EQUALS entry link. main190 (BOB 2026-10-03): every record field identical; only low/high differ because that BOB run had no lf (all records +/-FLT_MAX sentinels). Evidence: Frida dumps `research/bob_re/frida_out`, `research/sim_first_divergence.py`, `research/tilelist_fields.py` |
| `global_map` | Global Mesh (`global_map\` part) | native | `global_blend.dds`, `texture_arrays.xml`, `global_map\tile_list.bin` byte-identical |
| `global_mesh` | Global Mesh (`land_mesh_N`, `sea_mesh_N`) | native (identical to BOB) | default "bob" geometry: on main190 (494 files) and vanilla 3k_dlc07 (465 files, 2026-10-05) every `.rigid_model_v2` and `.compressed_map` matches BOB apart from the bytes BOB leaves uninitialised (0x148–0x14B; sea 0xA5–0xA7), given the lf maps and tile list BOB itself read (see *Global meshes vs BOB on vanilla*; `BobGlobalMeshTests`). Meshes over 65,000 vertices are split like BOB's MESH_SPLITTER. `--global-mesh native`: the older game-valid approximation |
| `rivers` | Terry file (river models, height patches) | native (identical to BOB) | default `--river-geometry bob` (`BobRiver`): on main190 all 24 `river_N.wsmodel.rigid_model_v2` are identical to BOB's apart from 3-5 bytes BOB leaves uninitialised (they differ between BOB runs too); all 24 `.wsmodel`, all 87 height patches and `rivers.height_patch_collection` byte-identical. `--river-geometry wide`: the older game-valid water (78 px of land-mesh river holes left uncovered map-wide, BOB 732). See *Rivers vs BOB* |
| `global_props` | Terry file (`global_props.bin`) | native (byte-identical to BOB) | main190 against BOB (2026-10-05): the whole file is byte-identical (24,778,485 bytes, 12,465 entries); see "global_props.bin vs BOB" below. main190 checked in game 2026-10-04 |
| `camera_heightmap` | Generate Camera Height Map | native (byte-identical to BOB) | vanilla 3k_dlc07 against BOB (2026-10-05): the PNG is byte-identical (same scene inputs: CA's packed meshes, tile list, lf; global_props from the kit layers); every one of the 2,506,520 float cells bit-exact; see *camera_heightmap.png* |
| `trees` | Campaign Trees (`trees.campaign_tree_list`) | native | byte-identical when the CampaignTree map is the one decoded from vanilla (`trees-decode`) and heights are reused; heights computed from scratch (`TileHfHeight`, BOB cells): **byte-identical to BOB's own fresh vanilla list** (all 205,767 trees; 1 byte from CA's shipped list, where BOB itself differs) |
| `lookup` | Texture / Convert lookup texture | native | byte-identical to BOB (vanilla's minimap differs in 304 bytes because of CA's own file) |

## main190 in-game test (2026-10-04)

The first in-game test of a native main190 build (`output/compiled/main190_native_20261004b`, 223 s against about 20 min
through BOB) showed **empty rivers** and **missing northern mountains**. A/B packs (native with BOB file groups swapped
in) traced both to `global_props.bin` alone. Fixed in `GlobalPropsBuilder` and `BmdRecords`:
- **River records:** byte 91 (`PropRiver`) = 1, cast shadow on, season bucket 16, as vanilla and BOB. Without byte 91 the
  game culls the river water.
- **Quadtree cell:** by position only (radius 0). BOB's main190 cells match a radius-0 point for 92% of objects; the model
  radius put mountain props in much coarser cells, and the game didn't draw them.
- **Bucket references** in the cell bodies: campaign mask 0 (was 1), as vanilla and BOB.

After the fix both render in game. Other findings from the comparison:
- **BOB tile heights:** BOB's main190 `tile_list.bin` stores +/-FLT_MAX ("unset") low/high on every record, while vanilla
  stores real heights. Writing the sentinel into the native lists made no difference in game.
- **tile_list height source:** it now reads the .terry's lf maps, the same as `rasters`. The kit's `lf_heights.tif` was stale
  (main190 `ranges_relief.py` only edits the `.height` tif).
- **Open:** native land meshes cover about one cell more than BOB at road/river/coast hole rims
  (`TileCoverage.Covered`'s diagonal probes and far-edge test).
- **BOB threads:** BOB's "Enable multiple threads in processing" preference doesn't speed up Tilemap; it runs on one core
  either way.

## main190 check (2026-10-02)

`3k_190e_expanded_map` (1478×1133 hexes, assembly_kit_190E) was built natively in 231 s into `output/compiled/main190_native`.
- **Inputs:** a staging copy of the kit, `output/stage_main190_native`. It uses the tile map BOB ran on (`output/backups/main190_holes_20261002_150202`) and `--accept-tilemap layout.mesh_columns`.
- **Reference:** BOB's 14:20–14:33 outputs in the kit's working_data.
- **Script:** `research/native_vs_bob.py`; the full output is in `output/main190_native_vs_bob.txt`.

| Output | Native vs BOB |
|---|---|
| tile_list | 383,440 vs 383,410 records; header identical; 97.9% same tile at the same place, 88.4% identical records; holes 145 vs 144 points, 141 shared |
| global_map | `global_blend.dds` and `texture_arrays.xml` byte-identical; the `tile_list.bin` copy differs (same differences as tile_list) |
| global_meshes | 184 land / 126 sea meshes, same as BOB; not byte-identical (expected) |
| rivers | 24 rivers / 48 model files, identical to BOB's apart from BOB's uninitialised bytes; height patches byte-identical (2026-10-05, `BobRiver`) |
| global_props | 66,271 vs 66,268 objects; model + position 100%. Region: 100% (331 regions, same set as BOB) since the fix below. It was 67.7% while the step used layer names. River models are numbered as BOB (see the regions note) |
| lookup | `.dds` and `.tga` byte-identical; `_minimap.tga` differs in 6,959 of 3.88M bytes |
| rasters, climate | byte-identical to the kit's copies, but those were built natively too, so there is no BOB reference |
| camera_heightmap, trees | no BOB output in the kit to compare against (camera: byte-identical to BOB on vanilla since 2026-10-05) |

## Guandu check (2026-09-29)

The full native build of `3k_guandu_map` takes 37 s, into `output/compiled/3k_guandu_map`. Against your BOB v3 build:

| Output | Result |
|---|---|
| Global meshes | same count (154 land, 73 sea) |
| Rivers | same count (20) |
| Props | 40,352 against BOB's 40,339 |
| Prop regions | same as BOB for 98.6% of objects |

**Not yet checked in game.**

## Format notes

### global_map

- **`global_blend.dds`:** DDPF_RGB, 16-bit, R mask 0xFF, G mask 0xFF00. dwFlags, pitch and caps are all 0.
  - Byte 0 is the palette index from the blend TIF.
  - Byte 1 is `climate_map.cm` repeated 4×4 (0 cold, 1 arid, 2 temperate, 3 sub_tropical).
- **`global_map\tile_list.bin`:** the root file with record byte +12 cleared from 07 to 00 for base tiles (generic, generic_sea, sea_coast, mountains_*). Linear features (rivers, roads, cliffs, canals) keep 07.
- **`texture_arrays.xml`:** the same on every map.

### Lookup textures (`*lookup*.bmp` → `.tga`, `.dds`, `_minimap.tga`)

- **Palette:** unique colours in first-appearance order, scanning the BMP from the top.
- **TGA:** colour-map type 1, image type 1, first-entry field 18, 32-bit BGRA entries with A = 255, 16-bit indices stored top row first, no footer.
- **DDS:** DX10 header, R16_UNORM (DXGI 56), depth 1, 1 mip.
- **Minimap:** `indices[::4, ::4]` with the same palette.

### Meshes (`RigidModelV2`)

- **Layout:** RMV2 v8, 1 LOD, 1 mesh.
  - Land/sea: material 101, 16-byte vertices.
  - River: material 68, 48-byte vertices, 860-byte material block.
- **Header bytes that aren't real data:** vanilla carries the same values in every file of a kind, and the writer hard-codes them.
  - Land: 0xA4..A7 = 0, 0xE8..EF = 0.
  - Sea: 0xA4 = `00 41 0c c9`, 0xE8 = `b0 a0 19 c9 fd 7f 00 00`.
  - River: 0xA4 = `00 ff ff ff`, 0xE8 = `a5 70 cf 28 fa 7f 00 00`.
  - Rivers also have 2 uninitialised bytes at 0x31A that differ per file. They're written as 0 and masked by `parity --mask-junk`.
- **Bounding box:** BOB's box for land/sea tiles is the **whole tile rectangle** in x/z (`col × 37.19375` computed in double, then cast to float) and the vertex extent in y.
- **Mesh layout** (Phase 2 findings so far):
  - Surface triangles come first as fans over merged polygons, which are mostly rectangles: 1×1 up to 7×2 cells.
  - Vertical skirt triangles come last.
  - Vertices are appended in the order the merged polygons are visited.
  - Winding is clockwise seen from above.
- **River `.wsmodel`:** 2-space indent, CRLF, no trailing newline. The material differs per river.

### Global meshes vs BOB on vanilla (2026-10-05)

- **Inputs:** BOB's "Global Mesh" on the vanilla kit reads `tile_list.bin` and `lf_sea_height_map.compressed_map` through the game's file system: CA's copies in `data/terrain.pack`, not the kit's working_data (the kit tile list is 4,216,221 bytes, CA's 4,125,739; lf_height_map is the same in both). `research/gmesh/find_pack_inputs.py <map> [out root [pack]]` lists and extracts them. Built from the kit copies, 397 of 465 files differ; from CA's, all 465 match (`output/bob_runs/gmesh_vanilla_bob`, fresh BOB run).
- **Land byte 0xA6** is stale string memory: character 165 of the mesh's `<kit>\working_data\terrain\campaigns\<map>\global_meshes\land_mesh_N.compressed_map` path. On the Steam kits that is the first digit of N on assembly_kit_190E / 3k_190e_expanded_map and 's' / 'e' / 'r' for 1- / 2- / 3-digit N on assembly_kit / 3k_dlc07_main_map (`GlobalMeshStep.StaleLandByte`). Sea 0xA5–0xA7 stay unpredictable (they vary between main190 runs).
- **MESH_SPLITTER** (tooldatabuilder FUN_1800d0f80, runs after the skirts): the triangles in order go into a chunk with first-use vertex numbering; after a whole triangle, a chunk with 65,000 or more vertices is closed and the next triangle starts a new one. All chunks are meshes of the file's one LOD (mesh count at 0x8C, sections back to back, LOD vertex/index bytes summed), each with the tile's x/z box and its own y extent; the land `.compressed_map` rasterises every chunk into one field. `GlobalMeshBuilder.SplitMesh`, `RigidModelV2.MoreMeshes`. No BOB output splits: vanilla's largest mesh has 33,679 vertices, main190's 62,561 (sea_mesh_54); a mesh needs (int)(maxTiles/8)+1 ≥ 255 grid points per side to pass 65,000. Checked natively by adding ±4000 u16 noise to main190's lf_sea (`research/gmesh/noisy_sea.cs`): 57 sea meshes split into 2–3 meshes of 65,000 + rest (up to 139,118 vertices), where the step used to throw.

### Global meshes (`GlobalMeshBuilder`)

The decompiled algorithm is written up in `docs/bob_re_global_mesh.md`. Native version, game-valid:

- **Tiling:** square meshes of (int)(maxTiles × 0.125) cells, 2 cells per tile-map pixel. They're visited south-west first, row by row, and empty ones are skipped.
- **Heights:** `LfSampler` gives BOB's bilinear lf sample, (l × 5500 − 1200) × tile/128.
- **Holes:** `TileCoverage` works from the placed tiles, with masks and orientation, plus ±0.001 diagonal probes. Tile categories:

  | Category | Feeds |
  |---|---|
  | `river*`, `roads_*`, `canal*` | neither (holes) |
  | `generic_sea`, `river_mouth`, `blockout_cliff*` | sea |
  | `sea_coast` | both |
  | everything else | land |

- **Vertex flags:**
  - 0 near holes.
  - 0 on mesh seams.
  - 3 on map edges.
  - 2 elsewhere.
  - Sea meshes also pin every 4th row and column.
- **Decimation:**
  - Normalised Sobel normals.
  - `TriangleMerger` (factor 0.9999, 10 passes, limit growing by 6.4).
  - Winding flipped, then first-use renumbering.
- **Skirts:** double-sided quads 1.0 below boundary edges next to holes or the map edge.
- **Land compressed map:** the final surface rasterised onto the grid, with header (0, −50, 0, 0, max, 0) and 0 = hole.

### Rivers (`BobRiver`, default; `RiverBuilder` with `--river-geometry wide`)

- **Source:** every `ECRiverSpline` entity in the AK layers.
- **Numbering:** BOB's (`RiverNumbering.Bob`, see the global_props regions note) whenever the map.hex region lookup is available, so the models, height patches and `global_props.bin` match a BOB build. `RiverNumbersByName` (rivers step and `GlobalPropsBuilder`, keep both in step) numbers by the entity name (`river_N`) instead, which is CA's numbering in the shipped vanilla files. Height patches carry the model's number.
- **Geometry:** `CampaignBuildContext.RiverGeometry`, CLI `build-campaign --river-geometry bob|wide`.
  - `bob` (default): BOB's own geometry, described below. Like a BOB build, its water leaves 732 px of land-mesh river holes uncovered on main190.
  - `wide`: the older game-valid water (`RiverBuilder`). It samples the Béziers every `spline_step_size`, multiplies the width by 1.15 and pushes both ends out by w/2, so it also covers the tile-based land-mesh holes (78 px left). It uses packed up normals and writes patches in 32-unit blocks from the river's first block.
- **`.wsmodel`:** BOB writes LF line endings and no final newline (`bob`). CA's shipped vanilla files use CRLF (`wide` keeps CRLF).
- **Vanilla:** CA's shipped 3k_dlc07 river models are not this BOB's output for the vanilla kit layer (they have more vertices, from an older tool), so `bob` doesn't reproduce them.

#### Rivers vs BOB (2026-10-05)

**Result** on main190, against BOB's "Terry file" output saved in `output/bob_runs/frida_rivers2_main190_bob_terrain` (tests: `BobRiverTests`):
- **Models:** all 24 `.rigid_model_v2` are identical apart from the bytes BOB leaves uninitialised: 0xA5–0xA7 (LOD padding) and 0x31A–0x31B (material). Both ranges differ between two BOB runs of the same input.
- **`.wsmodel`:** all 24 identical.
- **Height patches:** all 87 patches and `rivers.height_patch_collection` byte-identical, when rasterised from the models BOB actually read (see the note under *Height patches*).

**How it was found:**
- **Frida dumps of BOB:**
  - `research/bob_re/frida_rivers.js`: spline segments and `optimise_spline`;
  - `frida_rivers2.js`: the 32-byte vertices and the indices that `FUN_18015e9e0` returns.
- **Python prototype:** `research/rivers/bob_spline.py`, `bob_mesh.py`, `bob_file.py` and `bob_patch.py`, compared with `dump_cmp.py` and `raw_cmp2.py`.
- **C# port:** `BobRiver`.

**Spline** (tooldatabuilder `FUN_18016e440`, utilitydll `SEGMENTED_SPLINE_3`; 451/451 segments and 24/24 sample lists identical):
- **World points:** float32(local) + float32(entity position); yaw is 0 on main190.
- **Control points:** world(p_i) + tangent_out and world(p_i+1) + tangent_in, in float32.
- **Degenerate segments:**
  - p0 = p1 → p1 = (p2 + p0)·0.5;
  - p2 = p3 → p2 = (p1 + p3)·0.5;
  - both → p1 = p2 = (p3 + p0)·0.5;
  - any degenerate segment uses the straight length |p0 − p3|.
- **Length:** Σ|B′(u)|·du over 1000 steps, with derivative weights (3u², 2u, 1, 0).
- **Evaluation order:** every evaluation sums w0·P0 + w1·P1 + w2·P2 + w3·P3 left to right. This is **not** the order Ghidra prints for `FUN_1800b13b0`.
- **Samples** (`optimise_spline`, density 20, tolerance 0.02):
  1. N = trunc(20·total + 0.5) candidates at k/(N−1);
  2. greedy keep when the direction dot product < 0.98;
  3. append 1.0 and the 7 extras k/8, then `std::sort`;
  4. BOB's in-place unique, which never shrinks the list, so the old tail stays in.
- `spline_step_size` is not used.

**Mesh** (`FUN_18015e9e0`; 5,460/5,460 32-byte vertices and 24/24 index lists identical):
- **Per sample:**
  - segment found by BOB's linear search, accumulating (1/total)·len;
  - u = (total·t − start)/len;
  - width = lerp of the segment's two point widths by the clamped u;
  - position and derivative at u;
  - 5 vertices at off = j·0.25·w − 0.5·w: x = dz·off + px, z = −(dx·off) + pz, y = py;
  - each component goes through BOB's float→half (`FUN_1803811a0`): it adds ((v−1)&v)&0x1fff before dropping 13 bits, so exact ties truncate;
  - the w half is 0x0002.
- **Indices** per sample pair: (n_j, c_j, n_j+1) and (c_j, c_j+1, n_j+1).
- **Snap pass:** for each triangle in order, every other vertex whose (x, z) lies inside it (`WARSCAPE::contains`, a crossing test) takes the nearest corner's x, y, z and w halves, in place. This explains the "shared y" and the x/z half steps.
- **Normals:**
  - face normals come from the half positions and accumulate only when the face normal's y ≠ 0;
  - normal bytes = trunc(n·0.5·255 + 127.5);
  - tangent (the normalised derivative) and bitangent (n × t) bytes = trunc((v + 1)·127.5);
  - stored z, y, x, 0xff.
- **Dropped triangles:** any triangle touching a vertex whose normal y byte is < 0x82. This explains the three odd-count rivers.
- **uv:**
  - half(0.1·off) and half(0.1·total·t);
  - world uv half((x − x0)/(x1 − x0)) and half((z − z0)/(z1 − z0)), over the map rectangle in `map_data.esf`'s header;
  - main190's rectangle is (0, 0, 986.05096, 873.79541). The playable-area row says max y 874.185, but BOB uses the ESF.

**File** (`FUN_180146460`, VERTEX_LIST_CLEANER, `MODEL_PROCESSOR::write`):
- **Order:** vertices renumbered by first use in the index list; triangles written (a, c, b).
- **Vertex:**
  - position = half − pivot, where pivot = the bbox centre of the used half positions (stored in the material block);
  - w = 1;
  - uv as floats;
  - NTB bytes decoded as b/255·2−1 and re-encoded as trunc((v + 1)·127.5), 4th byte 0;
  - last 4 bytes 0.
- **Header:**
  - bounds = the world bbox;
  - shader block tail zero, with 9e d4 at 0xF0 in both BOB runs; vanilla has other stale bytes there.

**Height patches** (bob_terrain `FUN_18005eb30`, `WARSCAPE::rasterise_max_heights`, `COMPRESSED_MAP::compress`):
- **Field:**
  - vertices = model positions + pivot;
  - origin = bbox floored to 16 units, far side ceiled;
  - 16 px per unit, filled with −50 (INVALID_HEIGHT).
- **Per triangle:**
  - pixel bbox from the truncated corners ±1, clipped to the field;
  - pixel (x, y) is inside by warscape's crossing test;
  - barycentric weights |…|·0.5·(2/area);
  - height = y_b·w_b + y_a·w_a + y_c·w_c, keeping the max.
- **Blocks:**
  - 512 × 512, z-major then x, written only when a pixel is valid;
  - u16 = trunc((h − lo)·(1/(hi − lo))·65535), with lo/hi the block's min/max (lo = −50 whenever a pixel is empty);
  - header (0, lo, 0, 0, hi, 0);
  - rectangle = origin + pixel offset / 16.
- **Collection:** models in number order; each entry is `terrain/campaigns/<map>//height_patches//river_N_patch_XxZ.compressed_map` + (x0, z0, x1, z1).
- **BOB reads the models already on disk:** when a run starts, BOB rasterises the river models already there, not the ones it writes in the same run. The saved run's patches therefore come from the native models the kit held, and they're byte-identical when rasterised from those models. A BOB run on its own models (a second run) gives what `BobRiver` writes.

**Rotation, terrain_relative, reverse_direction** (none on main190 or vanilla), checked on a scratch copy of the vanilla river layer against BOB (2026-10-05, `BobRiverTests.Vanilla_RotatedRelativeAndReversedRivers_MatchBob`): all three byte-identical apart from the uninitialised bytes.
  - Yaw (ECTransform rotation y, degrees): cos/sin of the double angle rounded to float; points and tangents turned in float (x·c + z·s, y, −x·s + z·c), then the float position added. Rotating in double is 131 bytes off.
  - `terrain_relative="true"` changes nothing in BOB's river mesh.
  - `reverse_direction="true"`: the points are walked last to first with tangent in/out swapped (widths too).

**Vanilla 3k_dlc07 vs a fresh BOB Terry file run (2026-10-05, `output/bob_runs/20261005_104349_terryfile_vanilla_fresh`):** all 24 river models and `.wsmodel` files are identical to BOB's apart from the uninitialised bytes, but 11 carry another number: native river_13..23 are BOB's 23, 13, 22, 14..21. BOB's order is native's up to river_15 (entity), then river_20, 10, 9, 8, 3, 4, 2, 1, 0, 11, 16, so `RiverNumbering.Bob` (regions by largest entity id) does not hold on vanilla: entities river_11 and river_16 come last. Not explained by map.hex regions near their positions, nor by the props hash-map region order. **Open.** The vanilla `global_props.bin` differs from BOB's in the same river numbers only (14 bytes). The fresh run's height patches are byte-identical to the kit's previous ones (BOB rasterises the models already on disk, see above).

### global_props.bin (`GlobalPropsBuilder`)

**Layout**, recovered from vanilla and the decompiled serializers (`research/bob_re/bmd_fields`, `bmd_export`):
- **Regions:** BOB puts every object in the region of the map.hex hex under its entity's ECTransform position, whichever layer it came from.
  - The lookup is a port of bob_terrain `FUN_18005fe10`, in `HexRegionLookup.cs`.
  - Grid geometry: flat-top hexes of radius R = (2/3)·(maxx − minx)/(columns − 1) over the map bounds, odd columns half a hex north. There is a corner test at the slanted edges, and the maths is in float32.
  - The bounds come from the map's `campaign_map_playable_areas` row, refined to the floats in the working `map_data.esf` header (two ESF vec2 values, type 0x0c), which BOB reads: they can be an ulp off the XML's decimal text (main190 max x 986.05096 vs float(986.051)), which moved one boundary prop.
  - For river models (placed at the origin) the position is the ECRiver entity's transform.
  - Without a map.hex the step falls back to the layer name. A layer whose name isn't one of the map's regions goes to `3k_main_reg_non_playable`.
  - Checked on main190: 66,268 of 66,268 objects get BOB's region. On vanilla, 99.99% get CA's; the exceptions are the 24 duplicate river-model props that the converted kit layers carry at the origin.
  - **River numbering** (`RiverNumbering.Bob`): BOB numbers `models/river_N` by how many river models it has already written, walking the river entities grouped by region: regions by their largest river entity id, descending; inside a region, ascending id. Checked on all 24 main190 rivers in two BOB runs (Frida, `research/bob_re/frida_props_finish.js`). CA's shipped vanilla files number by entity name instead (`RiverNumbersByName`). Models, height patches and props agree with each other either way.
- **Cells:** 7 quadtree levels, each a row-major 2^L × 2^L grid over the world (595.1 × 541.79 scaled to the map; row 0 = north). Cell id = the number of cells in the shallower levels + row × 2^L + col. An object goes to the deepest cell that holds its box (see below); rivers go in cell 0.
- **Season buckets:** 16 + bits (spring 1, summer 2, autumn 4, winter 8; harvest adds none). No season mask = 31.
- **Files:**
  - `bmd_objects.<region>.<cell>.<bucket>.bin` holds the objects.
  - `bmd_objects.<region>.<cell>.bin` holds nested references to its buckets (campaign mask 1).
  - `bmd_objects.bin` (the root) holds nested references to every cell body, with the region key.
  - Entry order: each cell's buckets, then the cell; the root comes last.

**Round trip:** `BmdBody` parses every section into raw records, and all 8,891 vanilla bodies re-encode byte for byte.

#### global_props.bin vs BOB (main190, 2026-10-05)

Reference: `output/bob_runs/20261004_230523_frida_trees_main190/bob_terrain_out/global_props.bin`. Re-run with
`research/props/run_parity.sh` (frozen kit in `output/props_parity/ak`; `gp-diff` / `gp-body` / `gp-props-raw` CLI).
BOB's rules, from the decompiled bob_terrain / qttoolutility / empireutility / calibs code and checked on the reference:
- **Quadtree cell:** the deepest of 7 levels (first child NW, NE, SW, SE) holding the object's box.
  - Root: the map bounds' x range by the hex grid's z extent ((rows + 0.5) × row step).
  - Boxes: ECMesh entities use the model AABB (all LODs' mesh header bounds) through the world matrix, with [-1, 1]^3 for .wsmodel and for models BOB fails to load: a vertex stride that doesn't fit the declared format (`RigidModel.Mesh.PositionsOnly`; main190: `water_lily_1/3`, format 12 with stride 20; Frida on `FUN_18005e050`). Point lights use radius × mean column length × 0.5. Everything else is a point, a polygon mesh included: the point at its entity transform, not its outline (main190: (0, 0) → level-6 cell 5397).
  - Boxes outside the root are dropped.
- **Buckets:** only ECMesh and ECVFX entities are season-bucketed. Decals, scenes, lights, sounds, probes and polygon meshes use 16.
- **Record order:** ascending entity id in every section (BOB iterates the scene by id). In the props section the decals (ECDecal) come before the ECMesh props, each by id.
- **Polygon mesh triangulation:** ear clipping that starts at vertex 1 and stays on the same slot after clipping an ear; each ear is written (next, ear, previous) and the last three vertices (V2, V1, V0).
- **Preamble:** only the enum types and season codes that are used.
  - They're registered in first-use order with composite scenes first, then lights, props and VFX (each by id).
  - An empty season mask registers every code in catalog order.
  - Header u32 at offset 12 = `BMD_META_TAG_COLLECTION::checksum`: every enum value's `FUN_180f35730` hash mixed in preamble order, 0 without types.
- **World matrix:** `QTU::ECTransform` bit for bit (`QtuTransform.cs`).
  - Euler degrees × π × (1/180) → half angles → a quaternion. The sine is qttoolutility's own vector sine, and cos = sin(|x| + π/2).
  - Then `update_transform`'s float formula. A -0 position component is written +0.
  - Exact on 94,347 of 94,348 prop matrices.
- **Record fields:**
  - Light colour = byte × (1/255); light radius = radius × mean column length.
  - Prop byte 79 (animated) = 1 when the model path has `_anim`.
  - Decal: bytes 80..83 = parallax_scale, 102 = apply_to_terrain, 103 = render_above_snow, 104 = apply_to_objects.
  - Composite scene last byte = autoplay.
  - Sound cloud points = float(position) + float(offset).
- **Entry order:** cells ascending. Inside a cell, regions are in the list order of a CA_STD hash map keyed by region name, filled by ascending first entity id (`CaHash.HashMapOrder`: CA::murmur_hash, buckets 1 -> 2b+1, re-bucketing in list order). Matches all 2,417 main190 cells (`research/props/cell_map_order.py`, 2026-10-05).

**Result:** the native main190 `global_props.bin` is byte-identical to BOB's (`cmp`, 24,778,485 bytes, 12,465 entries, 2026-10-05). Progress that day: 12,429 of 12,461 common bodies → entry order (CA_STD hash map), BOB's river numbering, ESF bounds, -0 positions, decals first, default box for unloadable models, polygon mesh cell and triangulation.

**Not covered by the main190 check:** polygons wound clockwise (the builder reverses them first; BOB's handling is unchecked) and vanilla's own `global_props.bin`, which CA built with entity-name river numbers.

**Record templates:** new records start from vanilla's most common record of each type (read from `global_props.bin` in the game packs), with the layer's fields replaced:
- path, transform, meta tags (flags plus a mask covering whole enum types) and season mask
- snow, destruction and shroud visibility
- decal, apply-to-terrain and apply-to-objects
- cast shadow and height-patch flags
- light, sound and probe parameters

**One preamble:** every body lists every vanilla enum type.

**Object sources:**

| Layer entity | Becomes |
|---|---|
| `ECPropMesh`, `ECDecal` | props |
| `ECRiver` | `models/river_N.wsmodel` prop at the origin |
| `ECVFX` | VFX |
| `ECPointLight` | point lights |
| `ECCompositeScene` | composite scenes |
| `ECSoundMarker` | sounds (+ `ECPointCloud` → multi-point) |
| `ECLightProbe` | light probes |
| `ECPolygonMesh` | polygon meshes (ear-clipped) |

Tags come from the tag layers (`ECLayerExportTags`) linked through the Logical association.

### trees.campaign_tree_list (`CampaignTreeGenerator`)

Decompiled from `QTU::CampaignTreeGenerator` / `generate_campaign_tree_list_for` (qttoolutility) and `EMPIREUTILITY::CAMPAIGN_TREE_LIST::add_tree` / `write` (empireutility). Notes in `research/bob_re/trees*`, prototype in `research/trees/`.

- **Grid:** the campaign hex grid from `map_data.esf`: n columns (892 on dlc07 = tree map width / 2), rows = tree map height / 2 (702). Hex size s = (2/3) / (n − 1) × world width, columns 1.5·s apart, rows √3·s apart, odd columns half a row north. World height = (rows + 0.5) × row step (541.7862). All float32 in BOB's order.
- **One tree per hex:** BOB samples the AK CampaignTree map at pixel (2·col, H − 2·row − 1 − (col & 1)), y = 0 the top row. Palette index 19 (no tree), or a colour with no `campaign_tree_ids.colour_hex` match, means no tree.
- **RNG:** `std::minstd_rand` (× 48271 mod 2³¹ − 1) seeded with ((row << 16) | col) mod (2³¹ − 1), 0 → 1. Draws, in order:
  1. tree id among that colour's ids, sorted ordinally (MSVC `uniform_int_distribution`: 30-bit draws, values > 0x3FFFFFFF rejected; no draw for a single id)
  2. z jitter, then x jitter: `generate_canonical<float>` = (e − 1) / 2³¹, × 0.4 − 0.2
  3. rotation index 0..5 (`uniform_int`)
- **Position:** z = ((0 + row·dz [+ dz/2 if col odd]) + jz·0.4) − 0.2, x = ((jx·0.4 − 0.2) + col·dx) + 0.
- **Height:** `TerrainSurface::Object::height_split` -> `TERRAIN_RENDER_SETUP::get_height_worker` (warscape 0x370e20 / 0x371030), ported as `Atlas3K.Core.Campaign.Terrain.TileHfHeight` and used by the `trees` step when a tile list exists (2026-10-04):
  - tile instances in tile_list record order; the first that answers wins (point inside its rotated bounds, edges inclusive; masked tiles need a valid sub-tile; a tile with an invalid sub-tile and zero hf does not answer).
  - Bounds: a point outside (0, 0)-(tilesW·T, tilesH·T) (z' = z / 1.15476) gives height 0; so does a point where no tile answers (tile holes, an invalid unmasked sub-tile with zero hf). Both from BOB's dump: (hf, lf) = (0, 0), shipped y = 0.
  - lf: BOB's bilinear `FUN_18039eea0` (corners int(fx), int(fx)+1, int(fy)-1, int(fy)) at (x / (tilesW·T), 1 - z' / (tilesH·T)); height = (l·1100)·f' - f'·240, f' = (1/25.6)·T = 0.0390625·T. The constants are TERRAIN_RENDER_SETUP's fields (+0x8c = 1100, +0x84 = 240, +0x324 = T, +0x320 = T/128 the hf scale), read from BOB's memory; (l·5500)·(T/128) - (T/128)·1200 is the same value but rounds differently in float32 - that was the 1-4 ulp gap.
  - hf (`get_high_frequency_height_new`): the tile's `hf_height_map.compressed_map` (terrain2.pack terrain/tiles/campaign/<set>/<tile>/) sampled with the same bilinear at the tile-local (u, v) (rotation 0x20: (1-v, u), 0x40: (1-u, 1-v), 0x80: (v, 1-u)), per-corner value = raw/65535·(hi-lo)+lo, times f. Only river/road/canal/river_mouth/crossing maps have a non-zero range; generic/mountain/coast maps are all-zero. Not ported: the old `.data` (CHMF) path (blockout cliffs, terrace farms, some roads, mines) and 3 version-2 maps (river junction6_c, junction7_c, junction7_d).
  - y = (total - lf) + lf, total = hf·f + lf.
  - Vanilla vs CA's shipped list: **205,765 of 205,767 trees bit-exact** (99.999%; was 61.46%), test `TileHfHeight_VanillaTrees_MatchShippedHeights`. Against BOB's own fresh "Campaign Trees" output the native list differs in **4 bytes (1 tree)**: (364.13, 332.04) lies exactly on the edge y = 862 between two generic 8x8 tiles; BOB samples the first (its row 8 is out of range, so it does not answer) and never samples the second, giving 0 - edge handling not yet understood. The other CA miss (312.07, 297.13, 1 ulp) matches BOB: BOB itself differs from CA there.
  - 2026-10-05: **the native list is byte-identical to BOB's own "Campaign Trees" output** (`build-campaign --steps rasters,trees --fresh-trees` on the vanilla kit; test `TileHfHeight_VanillaTrees_MatchBobOwnOutput`). The tree provider is qttoolutility `FUN_18011f1d0` (`height_split`'s HeightDataProvider): it maps the WORLD point (x, z) through the tile map's inverse world transform, cell = (int(x / T), int(z / (T · 1.15476))), asks `ws_tile_instance_indices_at` (RVA 0x11be90) for the tile instances registered at that cell - a tile is registered on its cells [X, X+w) x [Y, Y+h), half-open - calls get_height (lf + hf) on each at (x, z / 1.15476), and keeps the HIGHEST answering height (0 when none answers); the lf part is that tile's. The edge tree at (364.13, 332.04) has z / (T · 1.15476) just under 862, so only the tile below (whose row 8 is out of range there) is asked: height 0, as BOB. Frida script: `research/bob_re/frida_trees4.js`.
  - Tile list: BOB's Campaign Trees reads the tile list through the game's file system, so the step uses the build output's tile_list.bin, then the compiled terrain (`--root`, the game's own), then the kit's working copy. On vanilla the kit's tile list (built from the kit's tile map, not CA's) would move 1,359 tree heights.
  - Version-2 hf maps (lo/hi only: river/roads_tracks junction6_c, junction7_c, junction7_d) are read like version 3. Tiles with only `hf_height_map.data` take BOB's mesh path (`get_tile_space_high_frequency_positions`), not ported: no vanilla tree lands on one (every answering tile of every vanilla tree sampled a compressed hf map in BOB's dump).
  - **Run it:** `Atlas3K.Cli build-campaign --ak <kit> --map <map> --steps rasters,tile_list,trees [--fresh-trees] --out <dir>`. Inputs: the kit's `.terry` with a CampaignTree map (for vanilla the decoded TIF from `trees-decode`), the raw height layer (-> rasters -> lf), a tile list (the tile_list step's output, the compiled one under `--root`, or the kit's working copy), the game packs (tile database + hf maps, `GameDataDir`) and the campaign_tree_ids / variants / seasons TSVs (`DbTsvRoot`). `--fresh-trees` computes every height (no reuse of the reference list). Vanilla check: `--steps rasters,trees --fresh-trees` against the shipped list = 5 bytes different (the 2 trees above), against BOB's = 4.
  - How it was found: BOB's "Campaign Trees" action (`ACTION_PROCESS_CAMPAIGN_TREES`, bob_terrain; it calls `QTU::generate_campaign_tree_list_for`) instrumented with Frida - `research/bob_re/frida_bob.py frida_trees3.js <vanilla kit> raw_data/terrain/campaigns/3k_dlc07_main_map "Campaign Trees" <label>` (headless, ~10 s; back up and restore the kit's working tree list). frida_trees.js logs each height_split (x, z) -> (hf, lf); frida_trees2/3.js add warscape's sampler calls (map, u, v, l via an Interceptor.replace wrapper) and the TERRAIN_RENDER_SETUP fields. Analysis: `research/trees/hs_dump_analyze.py`, `uv_analyze.py`, `l_check.py`. A fresh BOB run on the vanilla kit differs from CA's shipped list in 1 byte.
  - The reference list's y is still reused where a tree's (x, z) is unchanged (`ReuseReferenceHeights`).
- **Seasons:** each variant row sets its season's model (later rows win; no/unknown season = default model). Written: the `seasons_tables.index` of each season with a model, ascending, then 0xFFFFFFFF if the default model is set. Flag byte is always 1.
- **Type order:** a CA_STD hash map keyed by `CA::murmur_hash` (= MurmurHash3 x86_32, seed 0x4A545EED). Starts at 1 bucket, grows to 2b + 1 when count + 1 > b; new keys are appended to their bucket; a rehash re-buckets in list order. The file lists types in that list order, inserted in row-major first appearance (`CaHash.HashMapOrder`).
- **Header:** version 5, world bounds (0, 0, width, height), type count.
- **Reverse:** `Atlas3K.Cli trees-decode [--list] [--out]` maps a compiled list back to hex colours and heights, checks that regenerating is byte-identical, and writes the AK CampaignTree TIF (1784 × 1405, 2×2 px per hex, palette = sorted DB colours, 19 = no tree).
- **Vanilla AK caveat:** neither the kit's `3k_dlc07_main_map.tree.*.tif` (no trees) nor `tree_new` (90% of hexes) reproduces the shipped list. Use the decoded TIF.

### camera_heightmap.png

- **What the game needs:** `empirecampaign.dll` loads `campaign_maps\<map>\camera_heightmap.png` and requires the tEXt `height_scale`.
- **Status:** byte-identical to BOB on vanilla 3k_dlc07 (2026-10-05; 2,195,093 bytes, MD5 `6c6353e4…`), every float cell of BOB's sample buffer bit-exact. Before this work the native step rasterised props (correlation 0.94); a BOB-faithful Python prototype reached 95.6% bit-exact cells.
- **Settings:** `raw_data	errain\campaigns
ules.bob` [Terrain] `cam_hmap_resolution_scale`, `cam_hmap_samples_per_wu`, `cam_hmap_apply_blur`, `cam_hmap_blur_kernel`, `cam_hmap_standard_drv`. Without them BOB's settings are 0 and it writes no usable map; the native step then uses 1 / 4 / no blur (the values the parity run used). BOB's blur is not ported (a note says so when it is on).
- **Grid and samples** (`TOOLDATABUILDER::generate_camera_height_map`, FUN_18006bf20): (tiles W × res) × (tiles H × res) cells over x 0..W·T, z 0..(H·128·(T/128))·1.15476. Cell (u, v) is centred at (u·step, v·step), half extents step·0.5. n = ceil(extent × samples per unit) per axis; the samples are accumulated from the min corner and BOB's inner loop also runs the z count; plus one sample at the centre. The cell keeps the max, starting from −1.
- **Pixels:** highest = max cell; pixel = ceil(max(h / highest, 0) · 65535), PNG row 0 = the north edge (BOB's buffer row 0 is south); `height_scale` = "%f" of highest · (1/65535).
- **PNG encoding** (`PngLib`, `ZlibDeflate`): IHDR, tEXt, IDAT in 8192-byte chunks, IEND; each row takes libpng's adaptive filter (lowest sum of |signed byte|, ties to the earlier filter); zlib level 6 with Z_FILTERED, ported from zlib 1.2.x deflate_slow + trees.c (.NET's ZLibStream is zlib-ng and differs). BOB's IDAT reproduced byte for byte.
- **Scene height** (warscape FUN_18034cf20, `CameraHeightField`): max(P, G).
  - P, height patches (FUN_180350320): river patches (`height_patches
ivers.height_patch_collection`, identity transform), the props of every placed tile's `bmd_data.bin` with `has_height_patch`, and the global props with `has_height_patch`. Each needs `<geometry>.rigid_model_v2.compressed_map`; local bounds = LOD0 first mesh bounds (x, z). Value = |column 1| · sample + translation y, sample = max of corners (x0, y−1), (x1, y−1), (x1, y) (FUN_18039f140), −50 = none. Patches whose AABB leaves the quadtree build box (−1, −1)..(scene W, scene D) are never stored (18 on vanilla).
  - Patch matrices, bit-exact on all 11,670 vanilla objects: tile props = get_tile_transform (s = T/128, scale (s, s, s·1.15476), z translation (p·s)·1.15476) times 5 (bmd units) then the stored prop matrix, element (t·5)·p, then y lifted by the tile terrain height at (x, z / 1.15476); global props = the stored 4x3 from global_props.bin (it must be the one built from the kit layers, as BOB's scene is: CA's shipped file differs in the last bits). AABB over the model box's 8 corners; inverse = warscape's inlined adjugate / determinant (FUN_18034a210).
  - G, global mesh (FUN_180350620): the first land_mesh block in file-name order whose bounds (the RMV2 bounds at 0xC0) contain (x, z / 1.15476); bilinear with invalid corners filled (FUN_18039f3e0). Invalid (−50) or no block: the tile fallback (FUN_180350820) = the highest get_height (hf + lf) over the tiles the scene quadtree reaches at (x, z'·1.15476), else 0.
  - Scene quadtree (`TileQuadtree`, from a Frida dump of the whole tree): 7 levels of midpoint splits of (−1, −1)..(scene W, scene D); it holds the `global_map	ile_list.bin` records with flag bit 0 (instance flag 0x100), each in the leaf holding the centre of its extent; a leaf's bounds grow to its tiles' extents, inner nodes to their children's. Extent: x = X·128·s + w·128·s, z = (Y·128·s + h·128·s)·1.15476, widened for blockout cliffs by the custom mesh bounds without the tile's turn (a over x, b over −z; for 90° turns a over x min..−z min and b over −z max..x max). A tile answers only inside its leaf's bounds: sample points on a tile edge can miss it by an ulp.
- **Inputs a native build uses:** the build output first (loose files under the target root), else the game packs: tile_list.bin, global_map	ile_list.bin, lf_height_map, global_meshes, height_patches, global_props.bin, the tile database and tile bmd/hf/custom meshes.
- **Open:** BOB's blur; the single-precision epsilon of the singular-matrix test; vanilla only so far (main190 needs a BOB run with the same inputs to confirm).
- **Research and tools:** `research/bob_re/frida_camera.js` (pass buffer, probes, quadtree nodes with tile lists, tile instances), `frida_campatch.js` (patch objects), `frida_camera.py` / `cam_run.sh` (GUI run with helper actions), `research/camera/` (Python prototype, PNG and patch matching, the CamProbe console used for the fits).

### lf_normal.dds

Not needed for campaign maps. dlc07 ships none, and the game uses the file as the battle resource `RC_BATTLE_TERRAIN_LF_NORMAL`.

## Code

- **Formats** (`src/Atlas3K.Formats/`):
  - `Models/RigidModelV2.cs`: terrain-tile and river RMV2 writer.
  - `Models/RigidModelGeometry.cs`: any RMV2, positions and indices for one LOD.
  - `Models/WsModel.cs`
  - `Maps/TileList.cs`, `Maps/LookupTexture.cs`, `Maps/HeightPatchCollection.cs`, `Maps/Png16.cs`, `Maps/PngLib.cs` + `Maps/ZlibDeflate.cs` (libpng/zlib-exact PNG)
  - `TerrainDds.WriteBlend`
- **Core** (`src/Atlas3K.Core/Campaign/`): `CampaignBuildPipeline`, `BuildSteps` (rasters, global_map, lookup, pending steps), `CameraHeightmapStep` (+ `Camera/CameraHeightField`, `Camera/TileQuadtree`), `Parity`.
- **Tests:** `src/Atlas3K.Tests/CampaignBuildTests.cs`.
- **Research:** `research/derived_maps/` holds the camera heightmap and lf_normal analysis and the mesh study. Ghidra decompiles of the kit DLLs are in `research/bob_re/`. Ghidra and JDK 21 are installed portably in `Z:\Claude\Tools`.
