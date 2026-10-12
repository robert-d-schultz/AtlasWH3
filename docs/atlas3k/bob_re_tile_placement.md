# BOB tile placement (Tilemap) — reverse-engineering notes

Status: **in progress**. Goal: rebuild `terrain\campaigns\<map>\tile_list.bin` natively, byte-identical on vanilla 3k_dlc07.

The decompiled sources are in `research/bob_re/editor_tile_map`, `process_tile_map`, `tilesort*`, `tilematch` and `tiledb*`. The tile database parser is `research/tiles/tiledb.py`, with its format in `research/tiles/TILEDB_FORMAT.md`.

## Entry point (BOB / Terry path)

`QTU::process_tile_map` (qttoolutility) does the following:
- Builds the tile map image: the project tile map's float RGB, converted to 8-bit RGBA with A = 255.
- Builds the climate image: the per-tile-pixel climate index, mapped to the database climate's colour.
- Constructs `WARSCAPE::EDITOR_TILE_MAP(db, placement_groups, false, false, flag)` and calls `load_map_from_memory` with **no explicit tiles**.
- Calls `build_tile_map` into a new `TILE_MAP`.

`load_map_from_memory`:
- **Grid:** W × H = the image size. Row y = 0 is the **bottom** image row (the image is flipped).
- **Group per pixel:** `TILE_PLACEMENT_GROUPS::find_group(rgba)` (cached for runs of the same colour). Stored in `this+0x18`.
- **Climate per pixel:** the index of the database climate whose (r, g, b) matches, with −1 → 0. Stored in `this+0x20`.
- **Tile-set bounds:** `scan_tile_areas()` fills `this+200` with the min/max point that each tile set may occupy.

Tile-map colours are tile-set colours from `_settings.bin` (e.g. generic `96aa64`). Climate colours are cold `005555`, arid `ffaa00`, temperate `005500` and sub_tropical `aaaa55`.

## RNG

- **State:** `this+0x40`, xoroshiro128+ seeded by `FUN_1803e2470(0x12344332, 0x12344332)`: s1 = rotl(a ^ b, 36), s0 = rotl(a, 55) ^ (a ^ b) ^ ((a ^ b) << 14).
- **Draw(n):** r = (s0 + s1) >> 48 (from the state **before** stepping). Step: t = s0 ^ s1; s0 = rotl(s0, 55) ^ t ^ (t << 14); s1 = rotl(t, 36). Redraw while r ≤ 0xFFFF % n. The result is r % n.
- **Draws per placement attempt that passes:**
  1. `test_final_tile_position` draws a variation index (n = variation_count).
  2. If the tile is `random_rotatable`, it draws a start rotation (n = 4).
  3. `scan_for_tiles` draws the tile among `collect_matching_tiles` (n = match count).

## build_tile_map

1. `TILE_MAP::reset(W, H)`, then `add_explicit_tiles` (none in the BOB path).
2. `scan_for_tiles` with these types, in order:

   | Order | Type | Pass |
   |---|---|---|
   | 1 | 1 | large |
   | 2 | 2 | transition |
   | 3 | 3 | junction |
   | 4 | 4 | link target |
   | 5 | 5 | linked |
   | 6 | 0 | all |

3. `TILE_MAP::calculate_flow`.

## select_tiles_for_pass(type): database tiles in database order

| Type | Tiles selected |
|---|---|
| 4 | For each tile T, each link L of T: every tile U with no links and tile_set(U) == link_set(L), each added once |
| 5 | Tiles with links |
| 3 | A link target whose target set's link-as equals the tile's own set link-as, **or** ≥ 3 link targets, **or** (own set has a link_as_set **and** ≥ 1 link target) |
| 2 | Link targets that point at more than one target set |
| 1 | No link targets, not masked, width > 7 and height > 7 |
| 0 | Every tile |

**Sorting** uses MSVC `std::sort`, which is unstable:
- Type 3 sorts by link_target_count ascending.
- The other types sort by valid-subtile count (mask area) descending, then link_target_count descending.

**Database order:** `TILE_DATABASE::sort` (`std::sort`) sorts by w × h descending, then link_target_count descending, then name ascending (memcmp, shorter first). Names repeat across tile sets (e.g. `1x1`), so ties depend on load order.

## scan_for_tiles(type)

- **Type 0:** for each candidate tile (outer loop), for y, for x:
  - If the tile is unmasked and the point is already filled on layer 1, skip it, unless the set has `also_place_tile_set`.
  - The point must lie inside the tile set's scanned bounds.
  - `test_final_tile_position`, then `collect_matching_tiles` and a draw, then `place_tile` and `add_tile_to_link_map`.
  - At the last candidate, a point whose group is valid but which has no tile on layer 1 or 2 logs "Failed to find tile".
- **Types 1–5:** for y, for x (outer loop), then for each candidate:
  - "Occupied" means layer 1 for sets without `also_place`, layer 2 for sets with it.
  - The test runs only if type == 3, or the tile is masked, or the point is not occupied, and the point is inside the set bounds.
  - Then the same test, match, draw and place as type 0.
  - **Type 3 extra check (campaign, 2×2 tiles):** the placement is skipped (no draw is consumed by the match/place, though the test already drew) unless the groups at x−1 and x+2 match this point's group on rows y and y+1, with neither y−1 nor y+2 matching. The exact boolean is in the decompile.
  - **Type 5:** stop scanning candidates at this point after the first placement.

## test_final_tile_position(tile, x, y)

- **For r in 0..3:** ok[r] = `space_free_for_tile(tile, x, y, r)` && `links_match(tile, x, y, r)`.
- **No ok[r]:** return false.
- **Otherwise:** draw a variation index (n = variation_count).
  - **random_rotatable:** draw a start s in 0..3, then take the first r with ok[r], scanning s..3 then 0..s−1. The rotation is 0x10 / 0x20 / 0x40 / 0x80 for r = 0..3. If the tile is masked, the climate is the climate at the rotation's first covered point.
  - **Not rotatable:** rotation 0x10, return ok[0].

## space_free_for_tile(tile, x, y, r)

For each sub-tile (i, j) (masked → `subtile_masked_valid(i, j)`):
- (dx, dy) = `TILE_MAP::rotate_in_tile_space(w, h, rot(r), i, h − 1 − j)`. The point is (x + dx, y + dy) and must be inside the map.
- The first such point is recorded as corner[r].
- The point's group must be valid and must `match` variation 0 of the tile. All points need the same group.
- The point must not be occupied: layer 2 if the set has `also_place_tile_set`, layer 1 otherwise.

## links_match(tile, x, y, r) (checked against the disassembly)

For each link L, the cell is c = (x, y) + rot(link_point_inverted(L)), where link_point_inverted = (Lx, h − 1 − Ly).
- **c outside the map:** skip the link (`transition_tile_set` is always null in a loaded database).
- **Group at c invalid, or no tile set for link_as(L.link_set):** fail.
- **m (does c already suit the link set S):**
  - No layer-1 tile at c: `matches_link_as(group(c), S)`.
  - Otherwise: link_as(tile set of the tile at c) == link_as(S).
- **TLT_EQUALS:**
  - If m and the cell's link state is empty, the link is fine.
  - Otherwise state.flag[S] must be set. If the state holds any non-(0, 0) points for S, one of them must equal (x, y) + rot(target_point_inverted(T)) for some link target T of this tile.
- **TLT_NOT_EQUALS:** fail if m.

## Link state (add_tile_to_link_map), per cell

The state is a byte flag per tile-set id (plus one extra id = tile_set_count) and up to 4 points per id. The first empty (0, 0) slot is used; a 5th point is dropped.

- **Tile with no link targets:** S = the group's link_as_set, or the tile set's link-as. For each valid sub-tile cell whose group matches the tile, set flag[S].
- **Tile with link targets:** set flag[tile_set_count] on every valid cell. Then, for each link entry (built in the constructor), with cell = (x, y) + rot(target_inv) and point = (x, y) + rot(link_inv), set flag[S] at the cell and append the point.
- **Link entries (constructor):** for every TLT_EQUALS link, clamp the link point into the tile. Each link target at that clamped point whose set link-as equals the link set's link-as gives (S = the target set's link-as id, link_point_inverted, target_point_inverted).

## Placement groups (init_from_tile_database)

No groups file exists in the kit, so the loaded file merges nothing.
- Groups 0..n−1: one per tile set, in settings order, with colour = the set's colour.
- Then one group per database tile with a non-zero colour (17 tiles: lakes, mines, salt pools; vanilla uses none).
- Then one group per variation colour (none in vanilla).
- `find_group(rgb)` returns the first match.
- `get_tiles(g)`: the explicit variations (unique), then every variation of every database tile in the group's sets, in database order.
- `collect_matching_tiles`: tiles from `get_tiles(group)` with the same w, h and link count, an identical mask and the same links (`all_links_match`).

## Placing

`TILE_MAP::place_tile(layer 1)` stores the instance at the first covered cell and references (~index) in the other cells. The instance flag byte is 7.

**also_place** (sets river_mouth, sea_coast, blockout_cliff and blockout_cliff_ends → generic_sea; sea → beach) checks the target set's tiles in database order:
- The first tile with the same size and mask goes onto layer 2 at the same anchor.
- Otherwise, the first 1×1 tile goes onto layer 2 on every covered cell (rotation 0x10).

## Source tile map (important)

The kit's `raw_data/.../3k_dlc07_main_map/tile_map.png` is the user's **CAIME** tile map with holes fixed, not CA's original. River/road cells there trace the water/road line, not tile footprints, so vanilla's 4×2 curves can't be placed from it.

CA's original isn't shipped. `research/tiles/reconstruct_map.py` rebuilds it from vanilla `tile_list.bin`:
- Each layer-1 tile's rotated, masked footprint is painted in its set colour.
- Layer 2 = generic_sea/beach records lying entirely inside an also_place tile's footprint (17,788 records).
- 1,811 cells stay uncovered (holes).

## Climate

The record's climate is `climate_map.cm` (flipped, row 0 = south) at the anchor. Masked tiles instead use the climate at the rotation's first covered cell. This matches 99.93% of vanilla before that rule.

## Record order in tile_list.bin

Records are spatial: row-major by (y, x) of the tile origin, not the placement order.
