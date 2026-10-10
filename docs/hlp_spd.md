# hlp_data.esf / spd_data.esf: native generation

## WH3 (AtlasWH3, Phase 6)

WH3 maps ship `campaign_maps/<map>/hlp_data.esf` and `spd_data.esf`, which the game builds offline from
`pathfinding.ppd` and `map_data.esf`. The `hlp_spd` step and `hlp-spd` CLI build them natively, on top of Atlas3K's
3K generator (below), changed where WH3 differs. The reference files are the vanilla packs' 11 maps and the user's
IEE and Old World maps (all four inputs and outputs per map). `extract_refs.py` in `research/hlp_spd_wh3` pulls them
out through rpfm_server.

- **Pipeline:** the `hlp_spd` step runs with the other native steps. It reads `pathfinding.ppd` and `map_data.esf`
  from the build output's `campaign_maps/<map>`, else the kit's working_data, and the DB values from the linked mod
  packs and vanilla. It writes both files with magic `CB AB`.
  - IEE: 17 s. Old World: 46 s, peak 3.8 GB.
- **CLI:** `hlp-spd --in <dir> [--out <dir>] [--compare <dir>]` reports field-level parity for both files.
  `--tables-from-ref` adds the region tables computed from the reference's own transitions. The research flags are
  listed on `AiPathfindingCommands`.

### Formats (`Formats/Esf/CampaignAiData.cs`, `CaabWriter`, `CaabReader`)

- **CAAB / CBAB with child records.** The game's current files have magic `CB AB` (combi maps 5 and 7, IEE); older
  ones `CA AB`. Without strings the two are the same bytes. `CaabWriter` writes child records (short header
  `0x80 | 0x40 nested | version << 1 | name bit 8`, then the name's low byte), nested groups and typed arrays.
  - A size takes CA's shortest uleb128 unless the size field and the end of what it measures fall in different
    1 MiB blocks of the file; then it takes the 5-byte form, and so does a nested record's group count. All 126
    sizes of 1,000 and more in the 30 reference files fit; a size threshold does not (303,967 padded, 458,488 not).
  - All 30 reference hlp/spd files round-trip byte for byte (`esf-roundtrip`).
- **hlp** (`CAI_HIGH_LEVEL_PATHFINDER` v1):
  - `TRANSITION_DATA` (nested, one group): 3K's stream, with `REGION_AREA_INDEX` records (u16 region, u16 area) in
    place of 3K's packed u16 area ids.
  - A u32 array of 1024 × 1024: region-to-region costs, upper triangle only.
  - A u8 array of 1024 × 1024: region-to-region hop counts, symmetric.
  - `OTHER_CONSTANTS` (nested): the largest cost in the table.
- **spd** (`CAI_SIMPLE_PATH_DIRECTORY` v1):
  - Header: hex box, cell count, landmark set count, area count.
  - Per cell: 8 costs to the cell's landmark set, 8 costs to its own area's landmarks, u32 set index
    (`FFFFFFFF` = none), u16 region, u16 area.
  - `REFERENCE_POINTS` (nested): group 0 holds 8 landmarks per set; group 1 holds per area its (region, area) and 8
    landmarks (`FFFF` where it has none).
- **map_data.esf**: WH3 keeps 3K's layout with records in place of packed ids.
  - The per-hex area map is `MASKED_REGIONS_DATA/REGION_AREA_INDEX_OVERRIDE`: one group per run, holding
    `REGION_AREA_INDEX` and a u16 length. 3K's run-length u16 array is empty.
  - `REGION_AREA_DATA`'s two link lists are `REGION_AREA_INDEX` lists.
  - Packed area keys are `region | area << 16`: Old World has 1,465 regions, and the chaos maps have regions with
    over 32 areas.

### spd

- **Landmark sets** are the connected pieces of the movement grid, numbered in the order their first hex comes,
  scanning x outer and y inner. Each set gets 3K's 8 landmarks (corners and edge midpoints of its box, nearest hex)
  and 8 searches. Every map has its sets and landmarks exactly, except Old World Classic (below).
- **Areas** are every map_data area, in region order and then area order.
  - The landmark targets come from the box of all the area's hexes; the landmarks are its nearest passable hexes.
    The passable hexes' box gives 3,432 of 3,440 on combi map 1, against 3,440.
  - The 8 searches run over the whole grid, not inside the area. A path may leave and come back; prologue has
    1,036 such costs.
  - Only the area's own hexes are recorded. A search stops once it has settled every hex of the area it can reach.
- **Stored costs** are rounded down to a multiple of 4 (steps of 75 store 72, 148, 224, 300). The search runs on the
  exact sums.
- **DB values** come from the mod packs and vanilla's db packs, the rows the game reads. The kit's raw_data\db names
  `wh3_main_combi_old` for combi map 1, a campaign with no road rows.
  - Defaults are road 80 and beaches 2100: every vanilla campaign's lowest road is 80. The old combi and chaos maps'
    rows now name `*_old` campaigns.
- **Settlement slots**:
  - Edges to land, sea and other slot hexes cost 0 and bypass the hex-type gate. WH3's ppd types slot hexes as plain
    terrain; Arnheim's port slot reaches the sea at 0.
  - Edges to bridge decks (type 5) cost 0 where the type pair allows the move: Isle of Wight's and Fu Chow's land
    slots step onto their bridges at their own cost; Lothern's type-3 slot does not.

Parity (2026-10-10, `hlp-spd --compare`; Old World in 9 s):

| map | spd |
|---|---|
| prologue | byte-identical |
| chaos 1–4, combi 4, 5, 7, IEE, Old World, Darklands | every landmark; set costs 100 %; area costs 99.99 % |
| combi 1–3 | every landmark; set costs 99.96 %; area costs 99.99 % |
| Old World Classic | one extra set: a 2-hex sea strip on the east edge (x 2039–2040, portal sea regions) that CA leaves out; set costs 95.4 % |

Open on spd:
- Matorca's river slot (combi 1–3) reaches a type-3 hex at the edge's 80, which no type rule above gives.
- A few hundred cells per map (mostly of map_data area type 6) have no area in CA's file.
- Classic's sea strip.

### hlp

- **Transitions** come from 3K's generator, with these WH3 changes (each measured on combi map 1 or Old World):
  - The cost path is the start region's faction path: its own settlement is open at the slots' 0 cost, and foreign
    ones are closed. 3K closed every slot. Example: Altdorf's bridge transitions cost 500 through its slots.
  - A land-sea transition (flag 1) takes the cheaper of that path and the landmark search's grid
    (`CampaignPathGrid.PairSearch`: every settlement open, the type gate).
    - CA's 1,000, 785 and 1,191 on combi are the grid's paths, where the A* path paid the 2,100 beach.
    - Old World's land → deck → deck → sea bridges (500) exist only on the A* grid: its navigation bits allow deck
      steps that the type gate does not.
  - Flag 2 says whether the path taken goes through a settlement (a port). 3K's rule was "flag 1 and cost 0".
  - A bridge crossing counts 500 for the whole run: onto the first deck hex, along the deck (links or neighbouring
    deck hexes) and off the last one. 3K counted only "on, link, off".
- **Region tables** (`HlpRegionTables`):
  - Per region pair (from < to), the cheapest path over the transitions. Crossing costs the transition's cost, and
    moving on inside an area costs that area's matrix value. The cost goes in the u32 table's upper triangle.
  - **The sums are u32 and wrap, as the game's.** An area's matrix value of `FFFFFFFF` (no path inside the area) is
    a step of −1. Old World has 38 such values and combi and Darklands none, which is all of Old World's gap from
    CA's own transitions: 87 % → 99.7 % (Classic 81 % → 99.1 %). Example: 0 → 409 = 40,462 + `FFFFFFFF` + 80 =
    40,541.
  - The u8 table's upper triangle holds that path's number of region changes; crossings between areas of one region
    don't count.
  - Its lower triangle `[to, from]` holds the region changes of the cheapest path without land-sea transitions:
    99.3 % from CA's own transitions on combi map 1.
  - No path: `FFFFFFFF` / 255. Regions without areas, the diagonal and the costs' lower triangle: `FFFFFFFE` / 0.
  - Regions from 1,024 on have no row; paths still pass through them.

Parity (2026-10-10). "Areas" counts areas whose transitions, matrix, centre, `a` and `b` all equal CA's. "From CA's"
is the region tables computed from CA's own transitions; "ours" from ours.

| map | areas | region costs (from CA's / ours) | region hops (from CA's / ours) |
|---|---|---|---|
| prologue | 22 / 22, byte-identical | 100 % / 100 % | 100 % / 100 % |
| chaos 1–4 | 263/263, 274/274, 280/281, 280/281 | 100 % / 100 % | 99 % / 99 % |
| combi 1–4 | 655/695 … 672/713 (93–94 %) | 99.95 % / 90–96 % | 98 % / 95–97 % |
| combi 5, 7 | 607/717, 612/720 (85 %) | 99.93 % / 82 % | 94 % / 92 % |
| IEE | 925 / 1,068 (87 %) | 99.9 % / 81 % | 97 % / 95 % |
| Old World | 1,368 / 1,703 (80 %) | 99.7 % / 82 % | 95 % / 91 % |
| Old World Classic | 792 / 992 (80 %) | 99.1 % / 77 % | 96 % / 91 % |
| Darklands | 277 / 303 (91 %) | 99.95 % / 91 % | 98 % / 97 % |

Open on hlp:
- The rest of the transition costs: mostly ports and bridges on the bigger maps.
- The equal-cost hop ties.

## Three Kingdoms (Atlas3K)

Three Kingdoms campaign maps ship two offline AI pathfinding files in `campaign_maps/<map>/`:

| file | record | contents |
|---|---|---|
| `spd_data.esf` | CAI_SIMPLE_PATH_DIRECTORY | landmark (ALT) table: per hex, the cost to/from 8 landmarks |
| `hlp_data.esf` | CAI_TRANSITION_DATA | high-level graph: per region area, the transitions into neighbour areas and an intra-area cost matrix |

The game normally writes both with `CAI_PATHFINDER::reprocess_spd_data` / `reprocess_hlp_data` (empirecampaign.modder.x64.dll) during a startpos build. Atlas3K now builds them without the game, BOB or any CA tool. The only inputs are:

- `pathfinding.ppd`
- `map_data.esf`
- three DB tables from the kit: campaign_map_roads, campaign_variables and campaign_map_playable_areas.

## Running it

- **Pipeline step:** `hlp_spd`, which runs after `lookup`. It reads `pathfinding.ppd` and `map_data.esf` from the build output's `campaign_maps/<map>` folder, falling back to the kit's `working_data`. It writes `spd_data.esf` and `hlp_data.esf` next to them.
- **CLI:**

```
Atlas3K.Cli --ak <kit> --map <map> hlp-spd [--in <dir>] [--out <dir>] [--compare <dir>]
            [--only hlp|spd] [--legacy-stl] [--threshold 0.4] [--threads n] [--verbose]
```

- `--compare` takes a folder holding CA's files. It reports byte identity for spd, plus field statistics for hlp.
- `--legacy-stl` reproduces the transition order of files built with the 2019 toolchain (dlc04, 8p).
- 190E example:

```
Atlas3K.Cli --ak "C:\Program Files (x86)\Steam\steamapps\common\Total War THREE KINGDOMS\assembly_kit_190E" \
    --map 3k_190e_expanded_map hlp-spd --in <folder with pathfinding.ppd + map_data.esf> --out <folder>
```

Code lives in `src/Atlas3K.Core/Campaign/AiPathfinding/`:

- `CampaignPathGrid` and `SpdBuilder` build spd.
- `AiPathGrid`, `AiSearch` and `HlpBuilder` build hlp.
- `MapDataRegions` reads map_data.
- `AiPathfindingStep` is the pipeline step.

The formats are in `src/Atlas3K.Formats/Esf/`: `CaabFlat`, `CampaignAiData` and `EsfTree`. Research scripts and Ghidra notes are in `research/hlp_spd/`.

## Parity (2026-10-05)

| map | spd | hlp: identical areas | hlp: transitions with same hexes + target + cost | hlp: matrix values |
|---|---|---|---|---|
| 3k_dlc07_main_map | byte-identical | 330 / 334 | 2394 / 2398 | 19796 / 19796 |
| 3k_dlc06_main_map | byte-identical | 335 / 339 | 2450 / 2454 | 20312 / 20312 |
| 3k_dlc04_main_map (`--legacy-stl`) | byte-identical | 313 / 321 | 2460 / 2470 | 21270 / 21270 |
| 8p_main_map (`--legacy-stl`) | byte-identical | 311 / 321 | 2474 / 2486 | 21318 / 21318 |
| 3k_190e_expanded_map | 99.997 % of values, same box + landmarks (see below) | 633 / 644 | 4626 / 4644 | 46228 / 46228 |

On every map these hlp fields match 100 %: node count, area set, centre, `a`, plus `b` (except 2 of the 190E areas). The matrix column counts only areas whose transition list is identical; inside those, every matrix value matches. None of the hlp files is byte-identical yet, because every remaining differing area changes the file.

## Timing

Measured on this machine (Release build, all cores):

| map | spd | hlp | total (incl. reading inputs) |
|---|---|---|---|
| vanilla (892×702) | ≈0.2–0.3 s | ≈1.1–1.5 s | ≈1.9 s |
| 190E (1478×1133) | ≈0.6–0.9 s | ≈3.1–3.5 s | ≈4.5 s |

The Python research prototypes were much slower. What makes the C# versions fast:

- the 16 spd searches run in parallel, using Dial buckets on maps up to 1024 hexes;
- hlp phase 1 runs in parallel;
- gated edge sets are cached per (HLCI pair);
- searches use generation-stamped arrays (no per-search clears).

In the game, these files come out of a startpos build that takes many minutes.

## SPD algorithm (byte-identical)

1. **Bounding box.** Take the box of hexes whose type ≠ 2.
2. **Landmarks.** There are 8 targets: the 4 corners, then (mid x, min y), (mid x, max y), (min x, mid y) and (max x, mid y), where mid = ((max − min + 1) >> 1) + min. Each landmark is the passable hex nearest its target, scanning x outer, y inner, with a strict `<`.
3. **Edges and costs.**
   - Edges come from the ppd. Bit 6 is set equal to bit 7.
   - The cost table has 256 entries: the ppd costs repeated in 4 blocks of 64.
   - Slots 1 and 2 hold the beach costs (`pathfinding_land_to_sea` / `sea_to_land`). Slot 0x3E holds the road cost (the lowest campaign_map_roads threshold).
   - Road hexes use the road slot on their masked edges. A road hex on a river (type 6) uses it on all 6 edges, and on the edges coming back to it.
4. **Settlement slots.** Slot hexes are the map_data PRIMARY/PORT_SLOT_AREA_BLOCK hexes. Their edges to land, sea or other slot hexes cost 0 in both directions. A slot hex the ppd marks impassable becomes walkable.
5. **Moves.** A move is allowed only if the type-pair table allows it (FUN_1805fa120 + FUN_1805d3a70). Bridges (ppd bridge lists) link their two banks at cost 500.
6. **Searches.** Each landmark gets one outward search and one inward search.
   - On maps over 1024 hexes in either direction, the search copies FUN_18059f480 exactly: a binary heap of (hex, cost) ordered by cost only, with duplicates, and the game's pop and push sift rules.
   - The visited set is a `CAI_SPARSE_MAP<1024,64,bool>`: a coordinate c ≥ 1024 shares the flag of 960 + (c & 63).
   - Values are stored in a `CAI_SPARSE_MAP<1024,32>`: c ≥ 1024 aliases to 992 + (c & 31), and the last write wins. The output box is the box of written cells.
   - I confirmed both alias rules by loading the DLL in a Python harness and calling its sparse-map functions (`research/hlp_spd/oracle/`).
   - I also confirmed the neighbour order the same way. `LOGICAL_POSITION_UTILITIES::adjacent_hexes` in empireutility returns the same 6 directions as the ppd direction table.

**Why 190E is not byte-identical.** 160 of its 948k cells differ (0.017 %).

- **The search itself is exact.** `research/hlp_spd/oracle/search_oracle.py` runs the DLL's own landmark search loop (FUN_18059f480, with its own heap and visited sparse map) on a fake CAMPAIGN_PATHFINDER built from Atlas3K's grid (`hlp-spd --dump-grid`). For 190E landmark 0 it settles the same 382,331 hexes as `SearchGame`, in the same order with the same costs. So every remaining difference comes from the grid the game had, not from the search code.
- **One settlement's slot area.** CA's file treats the slot area of `ironic_hexi_dunhuang_resource_1` (settlement 133,945) as ordinary terrain (240 per step, not 0). It is the only one of the 244 slot areas in the box that does this. Leaving that slot area out (`SPD_SKIP_SLOT=ironic_hexi_dunhuang_resource_1`) brings the difference down to 37 cells, all landmark 0/1 slots near (973..1023, 670..685), plus a few cells in slots 2/3/11.
- **Those 37 cells.** They are equal-cost ties between hexes that share a visited flag across the 1024 wrap, for example (973,678) and (1037,678), both at cost 93780. Which one settles first depends on the whole push history. The values elsewhere match exactly, so the grid difference that decides these ties is invisible in the values.
- **Likely cause.** The dunhuang slot area (and two roads through it, see the HLP gaps) suggests the game's campaign model at generation time (startpos settlements, garrisons, road levels) differed from the map files. That state exists only in the game runtime, so this gap stops here.
- **Adjacency.** `LOGICAL_POSITION_UTILITIES::adjacent_hexes` (empireutility) returns the same direction order as the ppd table. Bridge link lists are assigned per hex, with the last bridge winning, as in the loader FUN_1811f7fa0.

**Why the DLL cannot simply run the step.** The full `reprocess_spd_data` / `reprocess_hlp_data` entry needs a constructed CAMPAIGN_PATHFINDER and campaign model: DB tables, campaign setup and campaign variables. Those cannot be built outside the game.

## HLP algorithm (field-level)

1. **Nodes and areas.**
   - Nodes are regions. Each node keeps the areas of type 0, 3 or 4. Area id = region | areaIndex << 9.
   - The centre is the settlement when it lies inside the area, else the map_data area centre. `a` is the area's map_data id.
2. **Phase 1 (FIND_REGION_BORDER).**
   - A heuristic-free search runs from the centre over the AI grid.
   - The grid uses the navigation bit only (no move-type table; FUN_180538bd0), plus type-5 → type-5 port links at 500.
   - Beaches are gated by the centre's HLCI.
     - Beach objects (FUN_181208f40 / FUN_181209d80) keep a counter per edge: an edge is open only while every beach side that lists it is on.
     - A beach only switches edges that are navigable in the ppd (FUN_1812013e0 masks each entry with them).
   - `b` is the largest cost to a land or sea hex of the area itself.
   - Land or sea hexes of other areas (but not slot hexes) are recorded per neighbour area, in discovery order, and not expanded.
3. **Transitions.**
   - The first transition comes from the refined centre-to-centre path. The refine_path tolerance is the map's half hex height (pathfinder +0x54 = hexSize·0.8660254), compared with squared distances.
   - The others come from border clusters: an integer-mean centroid, nearest hexes on both sides, the 2·nearest-distance acceptance test, and erasing hexes within distance 10.
   - `cost` is the waypoint sum of the path. A bridge crossing (step on, link, step off) counts 500, but a step onto the bridge from a river hex still counts. When both ends touch the same settlement, the path is costed without a faction.
   - `f1` = exactly one of the two areas is land (type 0). `f2` = `f1` and cost 0.
4. **Order.** Transitions are written in MSVC `unordered_multimap` iteration order: hash y·1016 + x, VS2019 rehash-before-insert, or VS2017 insert-then-rehash with `--legacy-stl`.
5. **Matrix.** Costs between the transitions of an area, in creation order. Each search stays inside the area itself (other areas of the same region are closed), with settlement slots blocked and beaches off.

**Remaining HLP gaps** (4 to 11 areas per map; trace one with `HLP_DEBUG_PAIR=<area>,<area>` or `HLP_DEBUG_COST=x,y,x,y`):

- **Equal-cost path ties.**
  - Examples:
    - the centre paths 248↔252 and 265↔273 on dlc06/07, which are 230↔234 and 247↔255 on dlc04/8p;
    - the 204/717/1229/661 group on dlc04/8p;
    - 386↔390 on 190E;
    - the 8p transition (358,120)→(359,121), where CA's path takes the bridge hex (cost 500) and mine takes the coast hex (1250).
  - In the 8p case, both routes cost the same and have bit-identical tiebreak values (checked in float32). The heap comparator (FUN_1805316e0), pop/push sifts and grid float constants (FUN_18126b890) all match the DLL, so the order is decided by the push history.
  - That points at a grid difference that is invisible in the costs. The prime suspect is the setup modifier lists (CAMPAIGN_PATHFINDER::setup: settlements, garrisons, armies) that apply in the 0x80 navigation-bit mode. They come from the campaign model at generation time.
- **190E.**
  - Areas 219, 220 and 314 border `ironic_hexi_dunhuang_resource_1`. CA's file treats that settlement's slot area as ordinary terrain, and roads 502/530 through it are not applied (CA's 120/240 against 100).
  - Road 485 near (1333,703) is also not applied in CA's file. That causes the two different 190E `b` values (areas 1228/1229) and the transition choice.
  - All of these need the game's campaign state.
- **Knock-on effects.** A differing transition shifts the `idx` values in that area only.

**The start position does not explain the gaps** (checked 2026-10-05). Tools: `research/hlp_spd/startpos_unpack.py` unpacks the LZMA payload of a CAAB startpos, and `esf_tree.py` now reads the Three Kingdoms 0x26 value type with RPFM's rule. I read:

- 190E: `campaigns/3k_main_campaign_map/startpos_historical.esf` from the main190 pack. It is the campaign of `3k_190e_expanded_map` in campaign_map_playable_areas, and was built 42 s after CA's spd_data.
- Vanilla: 8p_start_pos and 3k_dlc07_start_pos from data.pack.

What they show:

- **CAMPAIGN_PATHFINDER.** This record holds only a 3-byte state per ppd road (723 roads on 190E, 467 on vanilla). Every entry is `00 ff 00`, so there are no per-road differences. Roads 485/502/530 are like all the others.
- **Settlement slot areas.** All 339 settlements' slot areas (`SETTLEMENT_EXPANSION_MAP_DATA` / `SLOT_ZOE_ARRAY`, primary and port) equal the map_data slot blocks exactly. `ironic_hexi_dunhuang_resource_1` included: settlement (133,945) with its 7 slot hexes.
- **Settlement records.** Its SETTLEMENT, REGION_SLOT, SPRAWL_BLOCK and GARRISON_RESIDENCE records match those of a normal region (`ironic_central_pei_resource_1`). There is nothing marking it absent, abandoned (FACTION_SETTLEMENT_ABANDON_MANAGER lists are empty) or razed.
- **Characters.** No character (LOCOMOTABLE world position → hex) stands within 25 hexes of the dunhuang slot area or roads 502/530. On 8p, none stands within 8 hexes of (358,120); on dlc07 and 8p, none near the other tie cases. The only one near road 485 is the garrison inside the settlement at (1321,698).

So the remaining differences are not in the startpos data. They need state that exists only while the game runs. The 190E inputs CA's files were built from may also have differed from the current pack (a ppd or map_data that was rebuilt later); there is no way to check that from the files.

The game reads these files at campaign start. Small transition differences change AI route planning slightly, but the files stay structurally valid. Use the CA-built files when they are available.
