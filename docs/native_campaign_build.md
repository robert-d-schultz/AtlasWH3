# Native campaign build (BOB replacement)

AtlasWH3 rebuilds BOB's campaign-map outputs itself, straight from the assembly-kit sources. Intermediate files are
read from disk, so no pack import is needed between steps. The plan is [`atlaswh3_plan.md`](atlaswh3_plan.md).

## WH3 steps

AtlasWH3's per-step parity record on the WH3 fixtures (plan Phase 3). Atlas3K's Three Kingdoms record, which this one
grew from, is in [`atlas3k/native_campaign_build_3k.md`](atlas3k/native_campaign_build_3k.md).

| Step | Replaces BOB action | Parity (IEE `cr_combi_expanded_map_1`, Old World `cr_oldworld_map_1`) |
|---|---|---|
| `heightmaps` | Campaign Heightmap, Campaign Shroud Heights | 2026-10-08, against the user's BOB output. `full_logic_map.compressed_map`: byte-identical on IEE and Old World. `shroud_heights.dds`: byte-identical on IEE and Old World. `full_height_map.dds`: header byte-identical; the BC6H blocks are AtlasWH3's own encode (`Bc6h`), not AMD Compress's, see below. Checked in game on IEE, 2026-10-12 |
| `tile_list` | Tilemap | 2026-10-09, against the user's BOB lists. Old World: `tile_list.bin` and `tile_mask.dds` **byte-identical** (598,161 records). IEE: `tile_list.bin` and `tile_mask.dds` **byte-identical** against the user's BOB reprocess of 2026-10-10 (the older run differed in 473 low/high pairs, where the height layers had been edited after it). The same 12 IEE points left without a tile as BOB. IEE 61 s, Old World 139 s. `patch_mask.dds` (2026-10-09): `--patch-mask vanilla` byte-identical on both, from BOB's placement; the default `fitted` fixes BOB's north-band bug (see Masks). Checked in game on IEE, 2026-10-12 |
| `trees` | Campaign Trees | 2026-10-08, against the user's BOB lists. Old World: **byte-identical** (507,966 trees). IEE: every tree id, position and rotation identical; heights 238,458 of 253,903 bit-exact (93.9%), 253,880 within 1e-3, 23 beyond (max 0.02, on steep patch slopes) against the user's BOB reprocess of 2026-10-10, after the patch pivot (2026-10-10; 238,143 and 457 beyond before), see below. IEE 12 s, Old World 6 s. Checked in game on IEE, 2026-10-12 |
| `global_map` | Global Tilemap, Campaign Global Blendmap | 2026-10-09, against the user's BOB output. Old World: `global_blend.dds`, `texture_arrays.xml` and `tile_list.bin` **byte-identical** (with `!cr_oldworld_campaign.pack` linked). IEE: `texture_arrays.xml` and `tile_list.bin` byte-identical; `global_blend.dds` header identical, 187,094 pixels differ, all in one 1240 × 556 px area of the `iee` blend layer, saved 2026-10-03, after that BOB run (2026-09-30). IEE 5 s, Old World 7 s. Checked in game on IEE, 2026-10-12 |
| `masks` | Color Overlay, Color Overlay (Sea), Snow Mask, Corruption Mask, Event Area Mask | 2026-10-09. `colour_overlay.dds`, `lf_sea_colour.dds`, `snow_mask.dds`, `corruption_mask.dds`, `event_area_mask.dds`: **byte-identical** on IEE (against the user's BOB output, which a fresh BOB run reproduced) and Old World (against a fresh BOB run in the scratch kit: its working_data overlays were trimmed to 14 mips by `trim_mips.py`, and its event area TIF is newer than that run), every mip level. IEE 7 s, Old World 12 s. Checked in game on IEE, 2026-10-12 |
| `devastation_pieces` | Devastation pieces | 2026-10-09, against IEE's mod pack (BOB's pieces cut from the pack's own map textures), the user's Old World working_data and a fresh BOB run. Cut from the same map textures: every piece texture (all mips), `texture_info` and `mask` **byte-identical** (IEE 248 pieces, Old World 254). `tile_list`: the same road tiles in every piece; `event_tiles` the same set, numbered in BOB's hash-map order (not reproducible), so the indices differ. `tree_list` and `event_trees`: byte-identical on Old World (254 pieces, 261 types); IEE's pack ships a newer tree list than its pieces were cut from. Devastated folder, built from IEE's devastated project in the cache: `corruption_mask`, `lf_sea_colour`, `snow_mask`, `shroud_heights`, `tile_mask`, `mask`, `texture_info` byte-identical with the pack's in all 248 pieces, road and tree lists the same tiles and trees, `event_trees` identical; `full_height_map` is AtlasWH3's BC6H. **Objects and sounds** (2026-10-10, Phase 4.1): the same files as BOB in every piece of all four folders; BOB's record order changes between runs, so parity is content (records with their `.culture` masks as multisets, `gp27-pieces-diff`): IEE main against the fresh BOB run 99.96% of 485,625 records, IEE devastate against the pack 99.78% of 552,712, Old World main against its working_data 95.98% of 80,002, Old World devastate 78.36% of 5,518; leaving out the three fields BOB takes only from models loose in its kit (`UsesTerrainVertexOffset`, `Animated`, `DynamicShadows`, see global_props): 99.98%, 100.00%, 99.87%, 99.95%. **Rivers files:** see below. Cutting: IEE 4 s, Old World 7 s; objects about 20 s per project; the devastated build IEE 207 s. Checked in game on IEE, 2026-10-12 |
| `lookup` | Texture / Convert lookup texture | 2026-10-09, against the user's BOB output (IEE) and the shipped packs (both). IEE: `.tga` and `.dds` **byte-identical** to BOB's and to `!cr_immortal_empires_expanded.pack`'s; `_minimap.tga` differs in 1,545 of 388,000 pixels, along region borders (see below). Old World, with the user's region list (2026-10-10, `research/lookup/oldworld_lookup_last.txt`): `.tga`, `.dds` and `_minimap.tga` **byte-identical** to `!cr_oldworld_campaign.pack`'s (see below). IEE 0.2 s, Old World 4.7 s (with the packs' regions_tables) |
| `global_props` | Terry file (props export) | 2026-10-09, against BOB runs of the same sources (the scratch kit, Old World and IEE). BOB is not byte-reproducible here: two Old World runs of identical input differ in record order (20,912 bodies), entry order and the one-prop bucket numbers, so parity is **content parity** (every bucket body's records as a multiset; `gp27-diff`), on which two BOB runs agree 100%. Measured with BOB's model boxes (a research switch since removed): Old World 58,875 of 59,085 groups the same (99.6%), IEE 101,465 of 101,561 (99.9%); `global_props_sound.bin` IEE 3,172 of 3,215. The same bodies as BOB on both maps (60,753 and 102,578) and the same objects (132,644 and 1,326,436). The step boxes every model by its own bounds, so 436 Old World and 8,248 IEE objects go to a coarser cell than BOB's (Old World 98.8%, IEE 85.1%), see below. The v27 reader / writer round-trips all 166,549 bodies of both maps' BOB files. IEE 17.6 s, Old World 5.2 s (BOB's Terry file about 3 min on IEE). **2026-10-10 (Phase 4.8):** the quadtree root grows by `quadtree_margin` (the scratch kit's headless runs above leave it out; CA's files and the user's GUI runs have it) and sound axes come from the matrix columns: against the user's GUI reprocess of the frozen IEE sources, content parity 85.3% (10.6% without the margin), `global_props_sound.bin` 99.7%, every object kind the same count. Checked in game on IEE, 2026-10-12 |
| `environment` | Terry file (environment_collection.xml) | 2026-10-10, against BOB's output (the scratch kit's fresh runs, IEE's devastated project's in the kit). IEE's devastate folder **byte-identical**; IEE (2 spheres, 25 cylinders) and Old World (9, 247): every line the same, in another order. Two BOB runs of the same IEE sources differ in that order too (address order), so the step writes layer and entity order. Under 0.1 s. **2026-10-11:** the devastated folder gets the map's own file, byte for byte (the user's call; BOB compiled the devastated project's). **2026-10-12:** but with an empty `ENVIRONMENT_DEVASTATIONS`, as vanilla's `wh3_main_combi_map_devastate_1` (the user's call). IEE's env layer (`a_iee_environments`) fixed in the kit: its `vanilla` group (vanilla's 1 sphere and 65 cylinders) was export="false" and every one of its volumes, plus 10 of IEE's own, named `Weather/campaign/combi_expanded/<vanilla name>.environment`, which no pack has (now `combi/`); 33 positions rounded to 6 digits set to vanilla's. IEE now 3 spheres, 90 cylinders, all 66 vanilla lines identical to `wh3_main_combi_map_1`'s. `global_lighting` is always `weather/campaign/combi/default.environment` (the user: it suits every campaign map), not the .terry's, which is what Terry shows, so authors keep a battle environment there (IEE's names a file only the kit's working_data\Weather has). Old World's 26 `weather/campaign/cr_oldworld/` environments that share a name with vanilla's `combi/` are vanilla's value for value (parsed, 2026-10-11) but for `campaign_cr_oldworld_corruption.xml.material`, which is vanilla's `campaign_combi_corruption` renamed; khorne, nurgle, slaanesh and tzeentch are vanilla's `weather/campaign/chaos/` ones with the combi snow and corruption materials (the chaos materials are the same shaders, other tiling); `underwater` is Old World's own (the default's settings, prebattle_underwater's colours). Old World's env layer now names `combi/` (251 volumes), `chaos/` (4) and `cr_oldworld/underwater` (1), so the combi global_lighting suits it too. Checked in game on IEE, 2026-10-12 |
| `rivers` | Terry file (river models) | 2026-10-09, against four BOB runs: the user's working_data (IEE 60 rivers, Old World 127; IEE's reprocess of 2026-10-10 has all 266, all identical) and the scratch kit's fresh runs (IEE all 266, Old World 127). Every `river_<id>.wsmodel.rigid_model_v2` **byte-identical** apart from the two bytes BOB leaves uninitialised (0x31A–0x31B), every `.wsmodel` byte-identical; reversed rivers, a first point off the origin and 2310-row waterfalls included. `global_props` now boxes and places rivers from this bake: content parity unchanged, no river record differs. IEE 8 s, Old World 0.8 s. Checked in game on IEE, 2026-10-12 |

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
  - **Pivot** (2026-10-10): a patch sits at position − RS·pivot, not global_props' position + pivot − RS·pivot. The
    457 IEE trees beyond 1e-3 were under pivoted mountains (def_mountain_volcano_01 pivot y 0.543, the
    ogr_large_mountain_01/02 group near (760, 310) up to 3.38), where BOB's patch is pivot.y · scale.y lower and,
    where the patch is thin, under the terrain.
- **Open (IEE):** 15,422 trees within 1e-3 but not bit-exact, almost all under height patches: a few to a few hundred
  ulps, both signs, also on yaw-only unscaled props, and 23 beyond 1e-3 on steep patch slopes (Cathay pillars and
  domes). Nudging a prop's yaw fixes some of its trees but never all, so BOB's entity matrix differs from
  `QtuTransform`'s by more than the angle text (not yet found).

### global_map\ (WH3)

Two GUI-only BOB actions write it: Campaign Global Blendmap (`global_blend.dds`, `texture_arrays.xml`) and Global
Tilemap (`tile_list.bin`). Measured on the fixtures, with the source of `texture_arrays.xml` read off
bob_terrain.modder.x64.dll's strings (2026-10-09):

- **`global_blend.dds`:** the composited BlendCampaign map as is (not flipped), 255 (empty) written as 0. 8-bit
  luminance (pixel format flags 0x20000, R mask 0xFF), with dwFlags, pitch, depth, mip count and caps all 0. The
  size is the BlendCampaign map's (8 px per hex plus 4 rows: IEE 12800 × 7764, Old World 16384 × 14196). 3K's was
  16-bit, with the climate in byte 1.
- **`texture_arrays.xml`:** generated from the **asset variation db**, not copied. BOB (like the game) merges every
  `warscape_asset_variation_db\*.assetdb` in the packs (format: campaign_tools' `docs/assetdb.md`; `AssetVariationDb`).
  It writes one group per `campaign_base_colour` key, in ordinal key order, with that key's file in
  `campaign_base_colour`, `campaign_material` and `campaign_normal`, an empty `normal_array` and an empty `<climate/>`
  per group. Tabs, CRLF, no XML declaration. The kit's `raw_data\warscape_asset_variation_db\terrain_textures_campaign.xml`
  holds only water-plane materials and is not the source.
  - Vanilla has 172 groups, so IEE's file is byte-identical to vanilla's `wh3_main_combi_map_1` one. Old World's mod
    pack `!cr_oldworld_campaign.pack` adds `oldworld_terrain_textures_campaign.assetdb` with `mud_dry_darklands`,
    which sorts to index 99 and shifts every later group by one.
  - So the step reads the vanilla packs and the **linked mod packs** (`--pack`, as for trees).
  - Terry paints with the same list: a BlendCampaign TIF's palette is the groups' `display_r/g/b` in group order.
    Old World's palette matches the 173 groups, IEE's the 172. The step checks the palette at every used index and
    notes a blend painted with another group list (built without the right mod packs, Old World's has 50 wrong
    values and one beyond the list).
  - Not verified: which variation BOB uses when a key has several, or when two files define it. The step uses the
    first and notes both cases (neither happens on the fixtures).
- **`global_map\tile_list.bin`:** the root list cut to the tiles of `exclude_from_global_mesh` sets (roads,
  roads_light, cliff_gen, cliff_gen_ends, sea_coast). The far zoom draws generic and sea ground from the global
  blend, not from tiles. Records stay in root order and unchanged (flag 07). The path table keeps the paths still
  used, in root order. Header, climates and trailer are the root's. IEE keeps 52,707 of 339,338 records, Old World
  73,817 of 598,161. 3K kept every record and cleared the flag of base tiles.
- BOB's Global Tilemap reads the tile list from the installed pack (the tilemap → global tilemap pack round trip).
  The step reads this build's `tile_list.bin` only; without one it leaves `global_map\tile_list.bin` out (a note).

### Masks (WH3)

The six terrain-map actions of BOB's default group (`ACTION_PROCESS_TERRAIN_MAP`) save through
`WARSCAPE::save_image_and_generate_mips`, which links an old DirectXTex statically into warscape.modder.x64.dll.
`DirectXTex` ports the parts they use (MIT; measured 2026-10-09 on both fixtures):

- **Pixels:** the composited map (`TerrainComposite`), stored in TIF row order (not flipped, unlike the heights).
  - Masks union as `out += opacity · layer · (1 − out)` in float, **truncated** to 8 bits. IEE's snow mask (two visible
    layers) is byte-identical only with truncation; rounding missed 2,927 of 389,286 top-level blocks.
  - A colour overlay with no visible layer comes out flat grey 127 (0.5, truncated), every block `7BEF 7BEF 00000000`:
    Old World's ColorOverlay has only a hidden layer.
- **Mips:** the full chain (Old World's overlays have 15 levels; the user's working_data copies had 14 after
  `trim_mips.py`), each level from the previous one with DirectXTex's **linear** filter, never WIC:
  `CreateLinearFilter` weights (`srcB = (u + 0.5) · scale + 0.5`), `BILINEAR_INTERPOLATE` in float, values loaded as
  b · (1/255). The level is stored back to 8 bits before the next: R8_UNORM **truncates** (v · 255), so a flat area
  can lose 1 where the two weights sum to just under 1 (1,537 such pixels on IEE's corruption level 1); R8G8B8A8_UNORM
  **rounds half up** (`XMStoreUByteN4`). An exact 2:1 level is then the 2 × 2 average.
- **Blocks:** 4 × 4, a partial block repeating pixels as DirectXTex does (missing columns/rows take 0, 0, 0, 1), not
  the edge pixel.
  - BC1 (`colour_overlay`, `lf_sea_colour`): `D3DXEncodeBC1` as in the source (`OptimizeRGB`, perceptual weights, no
    dithering, alpha threshold 0.5).
  - BC4 (`snow_mask`): `D3DXEncodeBC4U` (`FindEndPointsBC4U`, `OptimizeAlpha`), but the palette of
    `FindClosestUNORM` (warscape 0x18076c0d0) is MSVC-folded: entry i is `f1 · c + r0 · c′` (r0 the raw endpoint byte,
    c′ = (7 − i) / 7 / 255 as a constant, some constants an ulp off i / 7), not `(f0 · (7 − i) + f1 · i) / 7`. With the
    source formula ties broke the other way in about 2% of blocks.
- **snow_mask.dds** is the SnowMask stretched to one pixel more each way, rounded up to whole blocks (IEE 3200 × 1941 →
  3204 × 1944), with the same linear filter (R8, truncated), then mipped and compressed.
- **Formats:** DX10 headers, LINEARSIZE for BC1 / BC4, PITCH otherwise, caps COMPLEX | MIPMAP with mips:
  BC1_UNORM (71), BC4_UNORM (80), corruption R8_UNORM (61) mipped, event area R8_UINT (62) one level.
- **Patch Visibility Mask** writes nothing while the PatchVisibilityMask map has no layers (both fixtures); the
  `patch_mask.dds` in working_data is the **Tilemap** action's (bob_terrain 0x180004636, after `tile_mask.dds`):
  - grid: `terrain_map_size` type 2 (warscape 0x1805ba840): 128 columns, r = round(128 · h / w) "rows", then each axis
    cut to whole cells of ⌈w / 128⌉ and ⌈h / r⌉ points: IEE 128 × 77, Old World 128 × 110;
  - cell (x, y), side s = ⌊w / 128⌋ for **both** axes, ORs over the tile-map points of [x·s − s, x·s + s] ×
    [y·s − s, y·s + s] on the map (all three tile layers): 64 for no tile or a tile of an exclude_from_global_mesh
    set, 16 for a land tile, 32 for a use_alt_lf tile (warscape tile +0x1a2; +0x3c must be 0). R8_UINT, rows from the
    south.
  - **BOB's bug:** the rows cover only 77 · 25 = 1,925 of IEE's 1,941 tile-map rows (Old World 110 · 32 = 3,520 of
    3,549), but the game stretches the mask over the whole map, so the rows drift north of the area they describe and
    the top band is in no cell. The sea-floor mask pokes out under land, worst in the north; the user used to fix it by
    hand (a duplicated row near the middle, making the mask one pixel taller).
  - **Fix** (`PatchMaskMode`, project setting "Patch mask", CLI `--patch-mask fitted|vanilla`): `Fitted`, the default,
    keeps BOB's grid and window shape but puts each cell on its true share of the map, sx = w / 128 and sy = h / rows:
    [⌊(x − 1)·sx⌋, ⌈(x + 1)·sx⌉] × [⌊(y − 1)·sy⌋, ⌈(y + 1)·sy⌉]. Where the cell size divides the map (both fixtures'
    columns) that is BOB's window. Against BOB's mask: IEE 625 of 9,856 cells change, Old World 1,298 of 14,080, more
    and more towards the north. `Vanilla` writes BOB's mask byte for byte. Checked in game on IEE, 2026-10-12.
- **lf_normal.dds** is not one of these: the GUI-only Campaign Heightmap writes it through NVTT 2.0.8 (header tag
  `NVTT`, DXT5 with DDPF_NORMAL). Not native yet.

### pieces\ (WH3)

BOB's Devastation pieces action (bob_terrain `ACTION_PROCESS_TERRAIN_DEVASTATION_PIECES`), read off bob_terrain
(capstone, 2026-10-09) and measured on IEE's mod pack (both folders), Old World's working_data and devastate pack, and a
fresh BOB run. The formats are in the decompiler's `docs/event-area-pieces.md`; `EventPieces` writes them.

- **Areas:** a piece per index on the EventAreaMask map but black (palette colour 000000, the `_empty` area of
  `campaign_map_event_areas`, which covers the outside), in `pieces/event_%06x/` named by the palette colour. The box
  is the index's tight box in event-mask pixels. BOB creates a folder for every area in the database, empty when the
  area is not on the map (IEE 6, Old World 210), and never deletes old pieces; the step empties `pieces\` and writes
  only the areas on the map. Old World's devastate pack holds 248 more pieces: byte-identical copies of IEE's devastate
  pieces left in that working_data folder, not a BOB rule.
- **Textures:** each is a raw crop of the map texture, mip by mip, no re-encoding. For a texture of W × H over a mask
  of Wm × Hm, s = W / Wm per axis (the snow mask's 3204 / 3200 included), the box is [trunc(s·x), trunc(s·(x + w − 1)) +
  1); it is widened to a grid (64 px for a mipped block-compressed texture, 8 for a mipped R8, else one block), and for
  full_height_map and tile_mask (rows from the south) flipped. Mipped textures get ⌊log2(min(W, H))⌋ + 1 levels; level
  m starts at (x0 >> m) rounded down to a block and covers (x0 >> m) − start + (W >> m) pixels rounded up to
  blocks. Rows are read linearly with no bounds check, so a crop past the map's right edge takes the next row's first
  blocks, past the bottom the next mip's. The piece header is the map's with the crop's size and mip count and
  MIPMAPCOUNT set (the pitch / linear size stays the map's).
- **tile_list:** the records whose path contains `road` (strstr), in record order, whose footprint centre
  trunc((x + w · 0.5) · sx + 0.5), trunc((y + h · 0.5) · sy + 0.5) (w, h swapped for rotations 0x20 / 0x80, rows from
  the south) is in the area. `event_tiles` lists every road path with a record on the mask (any index, the outside
  too), CRLF; BOB numbers it through a hash map keyed by the tile object's address (`(hi ^ lo) ^ 0x4a545eed`), so its
  order changes from run to run. The step uses the path table's order.
- **tree_list:** BOB does not read trees.campaign_tree_list here: it calls `generate_campaign_tree_list_for` with the
  kit's database (raw_data XML), so the mods' tree types are dropped (a fresh IEE run lost the 20 khuresh, khosun and
  mushroom types) and the heights are the loose logic map's (0 without one). A tree is in the area of event-mask pixel
  (x · sx, z · sz), each rounded half away from zero, rows from the south, with sx = W / world_width and sz = W /
  (world_width · 1.15476) in float (the .terry's world_width: IEE's tree list header says 1068.1111, its .terry 1068.11,
  and 14 trees change area). `event_trees` is every type of the generated list, CRLF, and a record's index its type's
  line; the records go type by type. The step takes the trees step's list, which is that list with the mods' types.
- **Devastated map:** the same cut from the devastated project's own build. IEE's devastate pieces in the pack match a
  native build of the devastated project in everything above (see the table), so its pieces need nothing from the
  main map but the event mask layout, which is the same.
- **objects / bmd_objects_sound** (+ `_<bmd_export_type>`, each a `.bin` and a `.culture`; Phase 4.1, 2026-10-10): the
  project's layer objects (`Wh3GlobalPropsBuilder`, as for global_props) whose position maps into the area the trees'
  way, one flat BMD v27 body per file. Rivers and light probes stay map-wide (no BOB piece holds one). An object goes to
  the file of the bmd_export_type of the nearest typed `ECLayer` group that owns it (IEE's devastated layers:
  `devastation_chaos` / `_nagash` / `_skaven` groups, each with a nested `sound` group), and is left out of
  global_props.bin. A type's two files are written when the piece has an object or a sound emitter of it, so the sound
  body can be empty (27 IEE pieces), and a piece with neither has no files (IEE's devastated `event_903b5c`, 71 Old
  World devastated pieces). The body's culture masks are a placeholder (1, sounds 0); the `.culture` holds the real
  ones, sections `p m v lp ls ht sc` (`ss` in the sound files), empty ones left out: the bit of each culture in the
  culture_mask (bit 63 for one without a prefab_types row, as the NONE bucket), 0 for none. Record order is BOB's
  address order: two BOB runs of the same IEE sources give 268 files whose records differ in order alone, and it is not
  the order of a walk through global_props.bin either. The step writes layer order. The `ht` section is (triangle count,
  mask) per run of consecutive triangles of one hole: Old World's BOB pieces (21, 0), (34, 0), ...; IEE's, whose order
  is shuffled, mostly runs of 1 (the decompiler's "one hexagon of 4 triangles, 2 masks" is one (4, mask) pair).
- **rivers** (Phase 4.2): 16-byte records (x, y, z, 0), the entity position (ECTransform::position) of each river
  spline whose material contains `cwb_campaign_river_lava`, in the piece whose area holds it: bob_terrain 0x10d81
  (TerrainObjects export), `ECSpline::material().find("cwb_campaign_river_lava") != -1` (2026-10-10). That is the
  material the game switches a listed river to while its area is devastated; a river prop survives a swap only when its
  material contains `campaign_water_plane` or `campaign_blood_river`, and is claimed when its x, z match a record
  (WH3_visual_map_decompiler `docs/event-area-pieces.md`). IEE's pack has two files listing the 7 rivers that today use
  `cr_campaign_water_plane_river_lava`, which BOB's test does not match: a fresh BOB run of today's sources writes none,
  and so does the step.
- **event_vfx** (main map folder). Nothing in the kit writes it (no BOB DLL has the name) and CA's list is hand-kept
  (IEE ships vanilla's copy). Read off Warhammer3.exe (2026-10-10 build, 0x271ec08; the only reference to
  `terrain/campaigns/%s/event_vfx`): a missing file is skipped; each line is passed to slot +0xa0 of the asset interface
  that the piece loader also hands to its object loader (0x2722670 → 0x26fd638), and the result is not kept. Just
  before, the same function makes that call for each entry of another list and keeps the results in a map at
  this+0x140. Nothing else reads the names, so the file is not an index. Whether slot +0xa0 caches what it loads (which
  would make the file a warm-up of the swap's own loads) is not settled statically. The step writes, per
  bmd_export_type (the untyped files last, like vanilla's chaos / nagash / skaven groups), the effects in the devastated
  pieces that no global_props.bin object uses and no earlier group listed. Written only with a devastated project.
- **environment_collection.xml**: the `environment` step (below).
- `lf_normal.dds` is cut from this build's (the `heightmaps` step).

### Lookup textures (WH3)

`LookupTexture` is unchanged from Atlas3K (format under *Format notes*); the step converts every `*lookup*.bmp` of
the map's EmpireDesignData folder (never working_data's copies).
- **Minimap:** BOB's is not exactly `indices[::4, ::4]`. On IEE (3200 × 1941 → 800 × 485) 1,545 pixels differ, and
  every one of BOB's values sits within one source pixel of the [::4, ::4] sample, so BOB's sample point is off by up to
  a pixel along region borders. No simple coordinate formula (floor/round of y·H/h, centre sampling, (H−1)/(h−1)) or
  block mode reproduces it. Left as is: the regions are the right colours in the right places.
- **The game reads only the first 1024 palette entries** (the user's finding). Old World has more than 1024 settlement
  regions, so BOB's first-appearance palette leaves random regions without a colour. The shipped Old World `.tga` and
  `_minimap.tga` are BOB's output reordered by the user's `lookup_tweak.py` (`Desktop\tw modding\projects\WH3
  campaign_scripts`): a hand-kept list of colours (black, the Chaos Wastes and Realm of Chaos regions, small and
  island regions) moved to the end of the palette, with the indices remapped. The shipped `.dds` holds the same
  remapped indices (the script itself rewrites only the two TGAs).
- **Region list (Phase 4.4):** the build profile's "Lookup: regions past 1024" (`LookupLast`) or `--lookup-last`:
  region keys, or text files of them (one per line, `#` comments). Each key's colour is its `regions_tables` r, g, b
  (CAIME's `LookupImageExporter` paints the BMP with them), read from the mod packs and vanilla. In a lookup with more
  than 1024 palette entries, the listed regions' entries and black (CAIME's hexes without a region) go to the end,
  each group in its old order, as `lookup_tweak.py` does; the `.tga`, `.dds` and `_minimap.tga` share the new order.
  Lookups of 1024 entries or fewer are untouched (Old World's `elector_counts_small` and `wh3_main_hef_court_small`
  hold some listed region colours). The step notes the regions still past entry 1024, and listed keys missing from
  `regions_tables` or from the lookup. Old World with the script's list as keys
  (`research/lookup/oldworld_lookup_last.txt`, 301 regions): all three files byte-identical to the pack's; 1,327
  entries, 302 moved, and `cr_oldworld_region_skull_guardian` is left at entry 1,025 (in the shipped file too).
- The other lookups (`elector_counts_small`, `wh3_main_hef_court_small`) shipped in the IEE and Old World packs come
  from other sources than the kit's BMPs (different sizes or palettes); not compared further.

### camera_heightmap.png (WH3)

`CameraHeightmapStep`. Read off the WH3 kit (capstone, 2026-10-09): `TOOLDATABUILDER::generate_camera_height_map`
(tooldatabuilderdll 0x19de90, cell max 0x19fd40, row and pixel lambdas 0x1e16f0 / 0x1e1ca0), its action in bob_terrain
(0x2d040, settings 0x33180) and `WS_TERRAIN_LOGIC::height` in warscape (0x579b80 → 0x5b20e0).
- **Why BOB fails:** the settings come from rules.bob `[Terrain]` `cam_hmap_resolution_scale`, `cam_hmap_samples_per_wu`,
  `cam_hmap_apply_blur`, `cam_hmap_blur_kernel`, `cam_hmap_standard_drv` and (new in WH3) `cam_hmap_output_target`. The
  kit's `raw_data\terrain\campaigns\rules.bob` sets none of them, and BOB has no defaults, so the resolution is 0, the
  grid is 0 × 0 and the action fails. Not tried in BOB yet: adding the keys to rules.bob should let the GUI action run.
- **Generator:** the same as 3K's (below, under *Format notes*). The grid is ceil(terrain size in tiles · res), and
  the terrain size is the tile map's (`tile_list.bin` ints 1 and 2: vanilla combi 2880 × 1941). Every shipped vanilla
  combi map is 720 × 486, so CA ran res 0.25; the user's IEE (800 × 486) and Old World (1024 × 887) files have the same
  scale. Cells are centred at (u · step, j · step) over `WS_TERRAIN_LOGIC::bounds`. The cell max takes n × n samples
  plus the centre, n = ceil(extent · samples per unit) cut to u16, and both loops run the z count. Pixels and
  `height_scale` are as in 3K, and the blur (0x3d6780) runs only with `cam_hmap_apply_blur` (not ported).
- **Scene height:** `WS_TERRAIN_LOGIC::height` → for a campaign, max(P(x, z), T(x, z′), lf(x, z′)) with z′ = z / 1.15476:
  P = the height patch objects at the point (0x5b5c30, max from −FLT_MAX), T = the tile terrain (0x5b5f20), lf =
  `TERRAIN_RENDER_SETUP::get_lf_height` over `lf_world_space_bounds`, clamped to the map. The native step uses
  `TreeHeightField` for it: the nearest full_logic_map texel, raised by every height-patched layer entity
  (`apply_height_patch`), **including `for_camera_height_map_only`**, which the trees leave out. T is not modelled.
- **Defaults** when rules.bob sets nothing: res 0.25 (CA's) and 8 samples per unit. On vanilla combi, BOB's pattern over
  the shipped full_logic_map matches CA's pixels exactly in 2% / 4% / 21% / 26% / 23% / 22% of land cells at 1 / 2 / 4 /
  8 / 16 / 32 samples (bilinear sampling: 17% / 24% at 4 / 8).
- **CA's vanilla file is not an oracle:** its mountains are the height patches (the logic map has none of them), but it
  is stale in places (in the south-east the logic map is 11.8 where the camera map says 1.4), and off the logic map
  BOB's scene had terrain (sea-floor props) where full_logic_map is 0. Correlation with the max-pooled logic map is 0.92.
- **Against the user's hand-made files** (made from the raw heights, no patches): IEE correlation 0.986. West of the
  old vanilla edge they agree within the sampling; to the east the native map is higher on the 3,040 patched props
  (mountains, 204 models) and along slopes (BOB's max over samples), and a few lake bowls are lower. Old World has no
  height patches: correlation 0.997, the native map up to about 1 higher on slopes (the user's file is also a row short:
  3549 · 0.25 rounds up to 888).
- **Against vanilla combi** (2026-10-10; CA's `wh3_main_combi_map_1` file through rpfm_server against the native IEE
  map, vanilla's 720 columns, the IEE-only east ignored): the same scale (median ratio 1.001 on land), the same
  roughness, and no box or max filter of the native map comes closer, so the sampling stays as it is. West of the IEE
  edits, 89.6% of cells within 0.25 and 96.2% within 1. The one systematic gap is at the map's edges, where
  full_logic_map is 0 and vanilla carries flat sea or coast values (~1.0) out to the edge. Left: vanilla's own
  leftovers (a 3.8 plateau in the south-west corner, lake discs).
- **Removed 2026-10-11: `FillOffMap`** (2026-10-10). It gave cells at or below 0 that touch the border their nearest
  terrain cell's value, breadth first, to match vanilla's edges. But IEE's eastern sea is at about 0 in the logic map,
  so it is not off the map: 27,749 cells, most of them in the east, were filled, and the breadth-first copy carried
  coastal values (up to 15.0 under cliffs and mountains) along rows and columns into the sea, as stripes (the user's
  first look at the dry run). A cell is again the scene height, 0 where the ground is at or below 0.
- **Not BOB's any more** (2026-10-10): with the cam_hmap keys set BOB's action runs, but on IEE it filled 69 of 486 rows
  in 90 minutes (about 10 hours in all, almost all in the height-patch lookup, warscape 0x5b5c30), so there is no BOB
  reference; the step is judged in game and against vanilla.
- **Time:** IEE 11–18 s, Old World 15 s.
- **Inputs:** the build's (else working_data's) full_logic_map and tile_list.bin, the .terry's `world_width` (else
  map_data.esf's), the layers' height-patched entities, their models' patches from the packs (vanilla plus `--pack`),
  and the rules.bob files (`campaigns\rules.bob`, then the map folder's).
- **Open:** entities inside groups (their parents' transforms; none in the fixtures); the in-game check.

### global_props.bin and global_props_sound.bin (WH3)

`GlobalPropsStep` / `Wh3GlobalPropsBuilder`, BMD v27 bodies in `Bmd27Body`. Read off BOB's output on the fixtures,
qttoolutility (ECTransform, set_decomposed_transform, set_rotation) and the Frida file trace (2026-10-09):

- **Bodies (v27):** no per-body preamble; every framing byte is the same in all 166,549 bodies of both maps
  (`global_props_sound.bin`'s bodies carry (0, 0) where the props file's carry (64, 64) after the point lights).
  Records: prop v30, VFX v11, light probe v3, terrain hole v3, point light v7, polygon mesh v4, spot light v8, sound v10,
  composite scene v12, reference v9. The layout is WH3_visual_map_decompiler's `GlobalPropsParser`, with the bytes it
  skips named from the layers (prop: visible_in_shroud, decal apply_to_terrain, receive_decals / decal
  apply_to_objects, render_above_snow; a flag block of tactical-view visibility on props, VFX, lights, holes, polygons,
  spots). Record culture masks are 1 everywhere but on sounds.
- **Container:** 3K's. Cells ascending; in a cell, regions in CA hash-map order; per (cell, region) its bucket bodies
  ascending, then the cell body; the root last. Sounds have their own file of bucket-16 bodies only (no cell or root
  bodies), and each sound's (region, cell) also gets an empty bucket-16 body in global_props.bin.
- **Buckets** (read off bob_terrain's Terry file export, 0x180022570, 2026-10-12): every (region, cell) has a list of
  bucket bodies, each referenced from the cell body by a PREFAB_INSTANCE whose campaign mask is the bucket's culture
  bit.
  - A culture bucket is 16 × prefab_types value + the object's season mask (0x18007b1a0: an empty season list is all
    four seasons, 15; WH3's props and VFX have none) for props and VFX, + 0 for everything else (decals, lights,
    scenes, holes, polygons). Values from prefab_types_tables of the mod packs and vanilla db (BOB: the kit's).
  - An object goes to one bucket per culture name in its culture_mask, duplicates kept (Kislev and Kislev Prologue
    share 24, so 384 twice); no mask is BASE (1); a culture without a prefab_types row is NONE (0).
  - The reference's mask is 1 << (bucket / 16 − 1) below 16 × prefab_types' row count, else 1 (BASE). The list starts
    that long (vanilla's 40 rows: 640 buckets).
  - Mountains: a prop whose .wsmodel has an `add_terrain_height` material (QTU::ws_model_material_add_terrain_height_flag,
    the "terrain vertex offset" shader) gets uses_terrain_vertex_offset set and is appended as a bucket of its own: the
    list grows to max(16 × rows, its length) + 1, so they are 640, 641, … (n in BOB's address order: not
    reproducible), each referenced with mask 1. Such a prop may not have a culture mask: BOB logs "All props using
    this shader must have no culture mask due to how they are rendered in the game" and stops the export (0x28a3c →
    status 4, its file-error exit). AtlasWH3 gives such a prop its own body with mask 1, ignoring its cultures. Why
    each needs a body of its own is not in bob_terrain: nothing per body differs (the flag is set on the prop record),
    so it is the game's renderer's requirement, not yet found in Warhammer3.exe.
  - The game (Warhammer3.exe, 2026-10-13): a culture's bit is (1 << value) >> 1 (0x141707480), so value v is bit v − 1,
    value 0 (NONE) has none, and 63 is the last value with a bit (62); BOB's NONE references (bit 63) carry a bit no
    culture has. The cell body names each bucket body by its path (0x142725348 looks it up by name), so the bucket
    number is only a file name; the mountains' bit is BASE's. No 640 in the exe's global-props code, and CA's shipped
    maps start their mountain bodies at 16 × the row count of their build: the Prologue at 576 (36 rows), Realm of
    Chaos at 592 (37), Immortal Empires at 640 (40). Today's 40 rows put UNDEAD_LEGIONS (36) and BUILDING_DEVASTATED
    (37) on 576-607, so the Prologue's and RoC's mountains share those cultures' bucket numbers, still with mask 1.
  - IEE's prefab_types has 63 rows (vanilla's 40 and its 23 cultures, values 39-61), so its mountains start at 1008;
    BOB, reading the kit's vanilla table, used 640 and put IEE's own cultures' objects in NONE.
- **Regions and cells:** the region of the hex under the object, from map_data.esf's MASKED_REGIONS_DATA
  (`HexRegionLookup`; 2026-10-12, BOB reads map.hex: the two agree on all of IEE's hexes, and on Old World but for 12
  hexes repainted in map.hex after its last export), bounds from map_data.esf: Old World's
  playable-areas row carries IEE's 1068.11, its ESF 1367.396). The quadtree covers the ESF bounds grown by the
  campaign_map_playable_areas row's `quadtree_margin` on every side (10 on every row; IEE −10..1078.11 ×
  −10..758.57, Old World −10..1377.396 × −10..1378.743), read from the map's mod packs and vanilla db: fitted to CA's
  vanilla combi file and the user's GUI BOB runs (2026-10-10). BOB run headless in the scratch kit leaves the margin
  out (not yet explained), so its cells are not the game's; boxes as 3K (model box through the world matrix, lights by radius, decals and the rest
  points), except: a terrain hole's triangles all take the hole's outline box; rivers take the box of their mesh as
  the `rivers` step bakes it (`Wh3River`, no file read), and are raised by their spline's first-point height; models
  with an unexpected vertex stride load fine (no 3K-style [-1, 1] fallback).
- **Model boxes (deliberately not BOB's):** BOB opens no geometry file for props (Frida trace: only its own river
  meshes and the loose .wsmodel files). It resolves a .wsmodel's geometry only through a loose .wsmodel in the kit's
  working_data, and a .rigid_model_v2 from the game's packs; anything else (a mod's model, a .wsmodel the kit doesn't
  have loose) is boxed as [-1, 1]^3, so it lands in a finer cell than its size. The step boxes every model by its own
  bounds from the game and mod packs: AtlasWH3 has no working_data for a model to be missing from. A model no pack
  has (usually an unlinked pack, but a map may name a model another mod will supply) keeps BOB's [-1, 1]^3 box, and
  the step lists those models in its notes. Likewise a mod model's skeleton sets "animated", which BOB only reads for
  the game's own models.
- **World matrices:** WH3's ECTransform is 3K's (`QtuTransform`) with radians = degrees × 0.017453294, cos = sin(h + π/2)
  for x and y, the z small-angle case (h, 1 − h²/2), and no position × 0 terms (bit-exact on 21,725 of 21,851 Old World
  props matched to one entity). Prefab children: the parent and child 3x3 multiplied in float, then
  set_decomposed_transform (column lengths, trace-method quaternion), set_rotation's trip through Euler degrees (the
  CRT's atan2f / asinf) and the matrix rebuilt; position ((t + r2·z) + r1·y) + r0·x (all 3,003 corruption-crack decals
  tested). Entity-space points (holes, sound lines) go to the world in that same order. VFX: the world matrix times the
  effect's scale as a full 3x3 product. Sound axes: the world matrix's z and y columns, each divided by its length (all 50 IEE emitters with a scale an ulp off 1; the quaternion's axes missed them by 1-4 ulps). An ECTransform pivot turns
  the entity about it: the origin is position + pivot − RS·pivot (IEE's two dragonspine mountains, pivot (0, 2.88, 0)
  under a 17° tilt, 0.85 east of their layer position in BOB's file; 2026-10-10).
- **Polygons and holes:** the outline in entity space, ear-clipped in float from slot 1, staying on the slot after a clip
  and back to 1 past the end, triangles written clockwise, the last as the ear at the current slot (1,252 of 1,253
  polygon meshes and 164 of 166 Old World holes). Polygon vertices stay in entity space; hole vertices are placed.
- **Fields from elsewhere:** cast_shadow is always 1; composite scenes' autoplay is always 1; use_dynamic_shadows on
  `rigid_campaign_mountain_emissive` materials (all 8 such models, no other); "animated" when the game's RMV2 has a
  skeleton; spot lights take the decomposed world rotation, angles (degrees × π) / 180, colour × (1/255) × intensity;
  polygons that are tactical-view-only without tactical view drop the flag and clear the byte after visible_in_shroud;
  sound masks have only their known cultures' bits; ECPolyline3D emitters are SST_LINE_LIST of every point.
- **Layers:** every exported file layer; an `ECLayer export="false"` group hides its members (transitively), inside
  prefabs too (marienburg_big's "houses"); members of a group with a bmd_export_type go to the pieces' typed files
  instead. Prefabs come from the folder Terry's configuration.xml names for the campaign database, except the vanilla
  kit's mistaken `art/campaign/prefabs`, which is read as `art/prefabs/campaign` (with it, IEE's
  `white_tree_of_morash_chaos_bits` was dropped).
- **Not matched yet:** prefab children's transforms (IEE 8,597 props, 233 decals, 21 VFX, Old World's tow_torch
  effects: BOB's composed matrix has exact zeros where ours has ~5e-7), 533 hole vertices by an ulp, the last byte of the 53 unknown bytes on 10 IEE river sounds, a spot light's length by an ulp, one polygon
  BOB stops triangulating early. Not written: the devastated project's `global_props[_sound]_devastation_<type>.bin`,
  which ship nowhere (plan §2).

### models\river_&lt;id&gt; (WH3)

`RiversStep` / `Wh3River`. One `river_<entity id>.wsmodel` + `.wsmodel.rigid_model_v2` per top-level entity with
`ECRiver` and `ECRiverSpline` in an exported layer, outside non-exported groups (the entities `global_props` places).
Read off warscape.modder.x64.dll (kit of 2026-09; capstone, `research/bob_re/disasm_fn.py`), prototyped in float32 in
`research/rivers/wh3_tessellate.py`, then ported. Call chain: bob_terrain 0x2ddf0 → `QTU::to_warscape_spline`
(qttoolutility 0xa3810) → `TOOLDATABUILDER::process_river_spline` → `WARSCAPE::tessellate_spline` (0x72c120) →
`MODEL_PROCESSOR::open_river_spline` / `write` (3K's).

- **Spline input** (`to_warscape_spline`, called with 0.2 and no world matrix): the points in the entity's frame, each
  segment p_i, p_i + tangent_out_i, p_i+1 + tangent_in_i+1, p_i+1 with the two point widths. reverse_direction walks
  the points last to first with each point's tangents swapped. Steps: columns 5 × 0.2 = 1.0, rows 0.2, uv 0.1.
- **First point:** the heights are taken relative to the stored first point's y, and `global_props` raises the prop
  by it. Old World's 19261f91ac8e913 (first point y −0.757, entity y +0.757): BOB's mesh starts at y 0 and its prop
  sits at y 0. x and z are not rebased: 15 IEE rivers with first points 1e-6 off in x or z match only unshifted.
- **4-D segments** (0x7109a0, 0x736b60): (x, y, z, width) controls, widths clamped to ≥ 0.01, inner widths
  0.75·w0 + 0.25·w1 and 0.25·w0 + 0.75·w1. A segment whose end equals its start is dropped. Degenerate ends take
  midpoints and the straight length; otherwise the length is 3K's 1001-step float32 rectangle sum of |B′(u)| over xyz.
- **Evaluation** (0x741410): power-basis weights with w0 = (d − 3c) + (−a + 3b) (3K: ((−a + 3b) − 3c) + d), each channel
  (P0·w0 + P1·w1) + (P2·w2 + P3·w3). Position weights (u³, u², u, 1), derivative (u·3·u, u + u, 1, 0). t outside
  [0, 1] is wrapped with fmodf; the segment search and local u are 3K's.
- **Grid** (0x7111b0, 0x733340): columns = ⌈clamp(width(t = 0) / 1.0, 1, 10)⌉ + 1, from the width at the **start**
  (2 for a river starting at most 1 wide, 3 up to 2, 4 up to 3); n = ⌈max(L / 0.2, 1)⌉ steps, row k at t = k·(1/n).
  Per row: P = eval(t), D = deriv(t), (dx, dz) normalised; off = (j / (cols − 1))·w − w/2; vertex
  (dz·off + Px, Py, −(off·dx) + Pz), u = 0.1·off, v = 0.1·t·L. No half floats, no snap pass, no dropped triangles
  (3K had all three) and no height patches.
- **Faces and frames** (0x733980, 0x710be0): per quad (A, B, C), (C, B, D); normalised face normals summed per vertex
  and normalised ((0, 1, 0) for degenerate faces); tangent n × X = (0, nz, −ny), below length 0.001 (ny, −nx, 0), not
  normalised; bitangent n × t. 0x71fc60 writes each as trunc((v + 1)·127.5) in z, y, x order.
- **File** (3K's MODEL_PROCESSOR): vertices renumbered by first use, winding flipped, float positions relative to the
  box centre (stored in the material block), bounds = the box, normals decoded (b/255·2 − 1) and re-encoded, the
  fourth bytes and the colour 0. Header: material 68, LOD quality 0, shader block `rigid_default` with byte 24 = 0x44
  (3K: `00 ff ff ff` and other stray bytes); 0x31A–0x31B are uninitialised in BOB (81 values over 187 files), written
  0. tessellate_spline fails at 65,535 vertices or more; the step writes nothing for such a river, as for one with no
  segment, and lists it.
- **`.wsmodel`:** 2-space indent, LF, no final newline, geometry `terrain/campaigns/<map>/models/river_<id>…`, the
  spline's material.

### lf_normal.dds (WH3)

`LfNormalMap`, written by the `heightmaps` step (2026-10-10). Two tools made it before: Terry's *export lf normals*
(tweak_terrainmetadataeditor 0x257f00) wrote `raw_data/.../lf_normal.png`, a minute or two per export, and BOB's
Campaign Heightmap converted that PNG with NVTT 2.0.8. The step does both from the composited Height map.

- **Terry's normals** (read off the plugin): `TerrainMapType` 3 (Height), `data_composited`; 3×3 Sobel gradients with
  clamped edges, gx = Σ rows Σ columns (1 · h) · K[r][c], gy the same with K transposed, K = [1 0 −1; 2 0 −2; 1 0 −1];
  n = (gx, gy, 1/s) / √(gy² + gx² + (1/s)²); R, G, B = trunc((n + 1) · 127.5), A = 255; rows as the Height map.
  s = terrain_size.x / map width × `low_frequency_world_vertical_scale` (0x28bc20, warscape 0x63a980), 4 on both
  fixtures. Height patches are not part of it.
- **Against Terry's exports:** Old World's `lf_normal.png` (2025-04-20) 99.94% of its sloped pixels identical (0.045%
  of the map differs, in areas edited since); IEE's devastated project's 91.7% of all pixels, the rest in the vanilla
  areas of a merged file. IEE's main file is merged (vanilla's normals pasted in), so it is no reference.
- **BOB's DDS** (NVTT, measured on IEE's PNG / DDS pair): DXT5 with DDPF_NORMAL and NVTT's header tag, DXT5nm (colour
  (255, y, 0), alpha x), rows unflipped, the full mip chain of 2×2 box averages. The step writes the same header and
  layout; its blocks are the DirectXTex port's BC1 / BC4 encoders, not NVTT's, so the bytes differ. Encoding IEE's PNG
  both ways (24 random 256 px windows of mip 0): mean absolute error against the PNG native x 0.40 / y 0.58, BOB's
  0.50 / 0.70.
- **Mips:** the full chain (Old World 15 levels). Old World's working_data file had its 15th level deleted by hand
  because BOB's Devastation pieces crashed on it; the native pieces step cuts any number of levels.

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
| CLI | `AtlasWH3.Cli build --project <file.atlaswh3>`: a project's whole build (Validate, Compile, Pack, Install), as the Build window |
| CLI | `AtlasWH3.Cli build-campaign [--map <map>] [--steps a,b] [--out <dir>] [--json]`: the compile steps alone |
| CLI | `AtlasWH3.Cli diagnose-campaign [--map <map>]`: per-step readiness |
| CLI | `AtlasWH3.Cli parity <builtDir> <referenceDir>\|--fixture <name> [--mask-junk]`: byte comparison |
| MCP | `terry` server (`tools/terry_mcp/server.py`): `build_campaign`, `build_project`, `build_step`, `diagnose`, `parity_check` |

- **Output location:** `build-campaign` writes to `%LocalAppData%\AtlasWH3\output\compiled\<map>\` by default, laid out
  like `working_data`: `terrain\campaigns\<map>\…` and `campaign_maps\<map>\…`. A project's output is its
  `build.output` (default the kit's `working_data`, as BOB).
- **Step order:** steps run in dependency order, and independent steps run in parallel. IEE's whole build takes about
  5 minutes on a 4-core machine ([`end_to_end.md`](end_to_end.md)).

## Project build: pack and install

`AtlasWH3.Cli build --project <map>.atlaswh3` (or the Build window) runs Validate → Compile → Pack → Install.
`new-project <file> --map <map> --pack <the mod's pack>` makes a project that merges the build into a copy of that pack
and installs it. Pack contents:
- `{compiled}`: the files the steps wrote (the build manifest, `%LocalAppData%\AtlasWH3\cache\builds`), at their
  paths under the output; with a `path`, only that pack folder. Nothing else in working_data is packed.
- a file or folder on disk, as before.

A step's own files are cleared out of the base pack: `rivers` owns `models\river_*` (named by entity id, as BOB's, so a
deleted or re-made river leaves a stale model), `devastation_pieces` owns both folders' `pieces\event_*`. A merge drops
every base file under those that the build did not write, also when it wrote none, unless the pack leaves the folder
out (`exclude`).

Old World's two packs, as shipped:

```json
"packs": [
  { "mode": "merge", "base": "{game}\\!cr_oldworld_campaign.pack", "output": "{project}\\!cr_oldworld_campaign.pack",
    "contents": [{ "source": "{compiled}" }],
    "exclude": ["terrain/campaigns/{map}/pieces/", "terrain/campaigns/{devastated}/"] },
  { "mode": "merge", "base": "{game}\\!cr_oldworld_campaign_devastate.pack", "output": "{project}\\!cr_oldworld_campaign_devastate.pack",
    "contents": [{ "source": "{compiled}", "path": "terrain/campaigns/{map}/pieces" },
                 { "source": "{compiled}", "path": "terrain/campaigns/{devastated}" }] }
]
```

The main folder's `event_tiles`, `event_trees` and `event_vfx` then go to the first pack. The shipped
`!cr_oldworld_campaign.pack` has none of them, an oversight (the user, 2026-10-10): the build fixes it.

