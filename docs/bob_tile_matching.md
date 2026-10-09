# BOB campaign tile matching (Terrain / Tilemap): reverse-engineering notes

BOB's *Terrain / Tilemap* action turns `tile_map.png` into `tile_list.bin`. It is
`WARSCAPE::EDITOR_TILE_MAP::build_tile_map` in `warscape.modder.x64.dll`, called from `QTU::process_tile_map`
(qttoolutility).
- Decompiles: `research/bob_re/editor_tile_map/`, `tilematch/`, `tilematch2..5/`, `process_tile_map/`.
- Port: `src/Atlas3K.Core/Campaign/TileMapCheck/TileMatchSimulator.cs`.
- Run it with `validate-tilemap --simulate`, or with the terry MCP tool `validate_tilemap(simulate=True)`.

## WH3 (AtlasWH3, 2026-10-09)

WH3's BOB (`warscape.modder.x64.dll`, which exports the EDITOR_TILE_MAP functions by name) runs the same algorithm with
two changes, and the port reproduces every placement of the user's IEE and Old World tile maps:
- **Passes 2-5 visit a point list** (`scan_tile_areas`, stored at `this+0xe0`): row by row, the points with a group that
  have a linked group (`group_is_linked`: the group holds a tile set) in [x − 3, x + 3) × [y − 3, y + 3). The large pass
  and the final pass still scan every point. In effect no tile gets its origin on a black point.
- **The junction pass's 2×2 strip rule** skips the tile when any of its 8 neighbour points is outside the map (the
  unsigned linear index ≥ W·H), after `test_final_tile_position` has drawn.
- Not reproduced (no fixture meets it): `space_free_for_tile` rejects x ≥ W and y ≥ H but not negative coordinates.
- WH3 has one climate (`default`) and no climate map; heights, header and `tile_mask.dds` are in
  `docs/native_campaign_build.md`.

The rest of this document is Atlas3K's 3K record.

## How well the port matches BOB

Measured against the BOB run in `output/bob_runs/20260927_191115_step2`, which used the vanilla tile map:

| | BOB | Port |
|---|---|---|
| large / transition / junction / link target | 22,029 / 375 / 416 / 110,122 | identical |
| linked / all | 17,032 / 32,037 | 17,024 / 32,050 |
| points left without a tile | 176 | 175, all shared with BOB |
| records identical (path, x, y, rotation) | | 88% |

On main190 (`output/backups/main190_holes_20261002_125552`), BOB's tile list leaves 54 points uncovered. The port finds those 54 plus 4 more.

What still differs:
- When several edge tiles tie in the pass sort (the sea_coast `end*` tiles, `canal_link_*`), BOB tries them in an order the port doesn't reproduce yet. Both orders give a valid tile, so coverage is unaffected, but the variant or rotation differs.
- BOB's "Failed to find tile for point" count depends on the last candidate in the final pass, so the port's `BobFailedMessages` is only approximate.
- Tools: `validate-tilemap --pass-order <db|0-5|tile:name>`, `research/sim_compare.py` (record diff) and `research/tilelist_holes.py` (coverage diff).

## Inputs
- **Points.** Every tile-map pixel is a point; a hex is 2×2 points. Internal y = H−1−image row, so y = 0 is the south row.
- **Groups** (`TILE_PLACEMENT_GROUPS::init_from_tile_database`). There is one group per tile set, then one per tile with a non-zero colour, then one per variation with a non-zero colour. `find_group` uses the first exact RGB match. A pixel with no match is `INVALID_GROUP`: no tile ever covers it and nothing is reported.
- **No campaign `tile_placement_groups.xml`.** None exists in the kit or the packs.
- **Climate.** The climate map is matched by exact colour, and no match gives 0. It is recorded per tile and never affects matching.
- **Tile database.** `terrain/tiles/campaign/_tile_database` comes from the game packs, listed as `*.bin` in lower-case ordinal order.
  - `TILE_DATABASE::sort` uses MSVC `std::sort`: larger w·h first, then more link targets, then name (ordinal).
  - Ids: set = index | 0x80000000; tile = index | 0x20000000; variation = (v | 0x4000) << 16 | tile.
- **File formats.**
  - Tile and set records: `CampaignTileDatabase.cs`.
  - A tile link: u16 1, link_set, x, y, base_x, base_y, u8 is_entry, blend_quad (u32 count), i32 blend_size, u8 no_offline_blend, test string.
  - A link target: u16 1, target_set, x, y.
  - Coordinates are in tile space with y = 0 the north row. BOB inverts them: (x, H−1−y).
  - `transition_tile_set` is never set on campaign tiles.

## Passes (`build_tile_map`)
The passes run in this order: explicit tiles, then large (1), transition (2), junction (3), link target (4), linked (5), all (0), then `calculate_flow` (rivers only).

`calculate_flow` can still change tiles before `tile_list.bin` is written. At a river junction whose back-link is an entry, it swaps in the first same-layout tile whose back-link is an exit. If no such tile exists, it removes the instance. The simulator stops before this step and outputs the pre-flow placement. The tile_list writer (Trees session) applies the swap.

`select_tiles_for_pass` builds each candidate list in database order:
- **Large:** no targets, not masked, w > 7 and h > 7.
- **Transition:** targets in at least 2 different sets.
- **Junction:** a target whose link_as differs from the tile's own (`FUN_1803e6680` is a "not equal" test), or 3+ targets, or a set with `link_as_set` and targets.
- **Link target:** for each link of each tile, every link-less tile of that link's set, without duplicates.
- **Linked:** has links.
- **All:** every tile.

The list is then sorted with MSVC `std::sort` (introsort, unstable):
- Junction: fewer targets first (`FUN_1803ea0a0`).
- Every other pass: more valid sub-tiles first, then more targets (`FUN_1803e7dd0`).

Scan order:
- **Passes 1–5:** row (y), then column (x), then candidate.
  - A candidate is tried if the pass is junction, or the tile is masked, or the point is free.
  - "Free" means layer 2 is empty if the set has `also_place_tile_set`, otherwise layer 1 is empty.
  - The point must also be inside the set's area box: the bounds of the set's points, ±128.
  - Linked stops at a point after its first placement.
  - Junction skips a 2×2 tile when the column at x−1 or x+2 holds a run of the same group exactly 2 points tall. BOB indexes those neighbours linearly, so rows wrap.
- **Pass 0:** candidate, then y, then x.
  - An unmasked tile is skipped where layer 1 is taken, unless its set also-places.
  - "Failed to find tile for point" is printed only by the last candidate: at points in its set's box where the group is valid and both layers are still empty.

## Testing a position (`test_final_tile_position`)
1. For each rotation r = 0..3 (0x10/0x20/0x40/0x80), the rotation is OK if `space_free_for_tile` and `links_match` both pass.
2. If no rotation is OK, the position fails and nothing is drawn from the RNG.
3. Otherwise one RNG draw picks a variation number, which is never used.
4. A `random_rotatable` tile draws a start rotation and takes the first OK rotation from there, wrapping round. Its climate comes from the point at the first valid sub-tile.
5. Any other tile only accepts rotation 0.

**`space_free_for_tile`.** Every valid sub-tile must be on the map, in a valid group that matches the tile's variation 0, in the same group as the other sub-tiles, and free on the layer described above. The sub-tile's group is reported back, and the last one written wins across rotations.

**`TILE_MAP::rotate_in_tile_space`** (W, H = tile size; y from the tile's south row):
- 0x20 → (y, W−1−x)
- 0x40 → (W−1−x, H−1−y)
- 0x80 → (H−1−y, x)

These come from the disassembly of 0x180351330 and its constants at 0x180761f64..7c.

**`links_match`.** For each link:
1. p = origin + rotate(link point, y inverted).
2. If p is off the map, skip the link (no transition sets on campaign).
3. S = the set named by the link set's link_as. Fail if the group at p is invalid or S is missing.
4. Compute eq:
   - no tile at p: eq = the group at p links as S (`matches_link_as`);
   - a tile at p: eq = S.link_as == link_as of the placed tile's set.
5. TLT_EQUALS:
   - eq holds and p's link state is empty: pass.
   - Otherwise p's state must flag S. If it also stores non-zero points for S, one of them must be one of this tile's rotated target points.
6. TLT_NOT_EQUALS: fail if eq.

## Placing
- `collect_matching_tiles` takes every variation of the group's tiles (set groups: every variation of every tile in the set, in database order). It keeps the tiles with the same w, h and link count and identical links and mask (`all_links_match`). One is picked with the RNG, and its variation 0 is placed.
- `TILE_MAP::place_tile` writes each valid sub-tile into layer 1. If a sub-tile is already taken it stops and returns false, keeping what it already wrote.
- `place_also_place_tiles` puts a tile of the also-place set in layer 2 under the tile: the first tile in database order with the same mask, otherwise a 1×1 tile of that set on each sub-tile.
- `add_tile_to_link_map` runs with the *candidate* tile, which may differ from the tile actually placed:
  - **No targets:** flag the set named by the tile set's link_as on each sub-tile whose group matches the tile.
  - **With targets:** flag the "linked" sentinel (id = tile_set_count) on every sub-tile. Then, for each entry built in the constructor, store the rotated link point at the rotated target point, in the first of 4 empty slots for that set.
    - The constructor makes an entry for each TLT_EQUALS link whose point, clamped into the tile, equals a target with the same link_as: {target set, link point (inverted), target point (inverted)}.

## RNG
xoroshiro128+, seeded by `FUN_1803e2470(0x12344332, 0x12344332)`, so s0 = rotr(seed, 9) and s1 = 0.
- **Step:** out = (s0 + s1) >> 48; t = s0 ^ s1; s0 = rotl(s0, 55) ^ t ^ (t << 14); s1 = rotl(t, 36).
- **Bounded draw for n:** reject values ≤ 0xffff % n, then return value % n.
- **Draw order:** variation number (always, once a position has an OK rotation), then rotation (random-rotatable tiles), then the matching-tile pick.
