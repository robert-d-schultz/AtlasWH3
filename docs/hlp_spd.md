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

- **Transitions** come from 3K's generator, with these WH3 changes, read from the game's own waypoints
  (`probe.py --waypoints`, below):
  - A transition's cost is one A* path between its two hexes with every settlement open (slot hexes at 0), summed
    per waypoint. There is no faction and no cheaper alternative path. (Before: the start region's faction path
    with foreign settlements closed, and for land-sea transitions the cheaper of that and the landmark grid.)
  - A bridge crossing is four waypoints: the hex before the first type-5 hex, that hex, the hex after it and the
    next one, 500 together whatever the other three are (land → deck → sea → sea, sea → deck → land → land,
    land → deck → deck → land). A type-5 hex inside a settlement's slot area is a slot hex, not a crossing. A step
    onto the deck from a river hex still counts (3K). The ppd's bridge links are not what the game flags.
  - Flag 2 says whether a land-sea transition's path goes through a settlement (a waypoint flagged `0xf800`). 3K's
    rule was "flag 1 and cost 0".
  - Identical areas from these: IEE 944 → 965, Old World 1,419 → 1,491, Classic 801 → 853, combi 1–7 +15 to +17.
  - **Centre paths** (`probe.py --centre`, 2026-10-10). The game picks one of four searches by which of the two
    areas has a settlement (its centre is the settlement):
    - Towards a settlement, the first search stops at the first settled hex exactly 3 hexes from it (goal
      `0x14292ab0c`: hex distance == 3), not at the settlement. Otherwise it runs to the other centre.
    - The path from the start to that end is refined as in 3K (`0x14291dc3c`; tolerance `pf+0x54`, the half hex
      height, against squared distances).
    - A settlement end then loses its hexes of game type ≥ 4: slot hexes, port/bridge (5), river (6).
    - Fewer than 2 hexes left make no waypoints (`0x141e481e4`), so that pair has no centre transition.
    - On IEE 2,138 of the game's 2,164 centre paths are now ours exactly (1,609 before). The rest are equal-cost ties
      and one search the game fails (sea → another region's port-slot hex).
    - Identical areas: IEE 965 → 1,050, Old World 1,491 → 1,671, Classic 853 → 978, combi 5 635 → 706, combi 7
      640 → 709.
    - **Older game builds used 3K's rule** (centre to centre, settlement hexes kept): chaos 1–4, combi 1–4, the
      prologue and Darklands only match with it. `--legacy-centre` (`HlpBuilder.Options.LegacyCentrePath`) keeps
      it for those files' parity. Under the new rule they drop: chaos 1 263 → 234, combi 1 671 → 600.
- **Region tables** (`HlpRegionTables`):
  - Per region pair (from < to), the cheapest path over the transitions. Crossing costs the transition's cost, and
    moving on inside an area costs that area's matrix value. The cost goes in the u32 table's upper triangle.
  - **The game's sums are u32 and wrap: a bug.** An area's matrix value of `FFFFFFFF` (no path inside the area) is
    a step of −1, so the game writes impossible cheap paths. Old World has 38 such values and combi and Darklands
    none. Example: 0 → 409 = 40,462 + `FFFFFFFF` + 80 = 40,541.
    - AtlasWH3 leaves those steps out by default: no region cost comes out below the wrapped one, and 0 → 409 costs
      more than the game's 40,541.
    - `--wrap-like-game` (`HlpBuilder.Options.WrapLikeGame`) reproduces the game. With it, Old World's tables from
      CA's own transitions go from 87 % to 99.7 % (Classic 81 % → 99.1 %).
    - The 38 matrix values themselves are written as CA writes them: the game's runtime reads them, and how it adds
      them there is not known.
  - The u8 table's upper triangle holds that path's number of region changes; crossings between areas of one region
    don't count.
  - Its lower triangle `[to, from]` holds the region changes of the cheapest path without land-sea transitions:
    99.3 % from CA's own transitions on combi map 1.
  - No path: `FFFFFFFF` / 255. Regions without areas, the diagonal and the costs' lower triangle: `FFFFFFFE` / 0.
  - Regions from 1,024 on have no row; paths still pass through them.

Parity (2026-10-10, `hlp-spd --compare`). "Areas" counts areas whose transitions, matrix, centre, `a` and `b` all
equal CA's. "From CA's" is the region tables computed from CA's own transitions (unchanged by the transition work);
"ours" is the CLI's region-table line for our own build, matching entries of those it compares.

| map | areas | region costs (from CA's / ours) | region hops (from CA's / ours) |
|---|---|---|---|
| prologue | 22 / 22, byte-identical | 100 % / 100 % | 100 % / 100 % |
| chaos 1–4 | 263/263, 274/274, 281/281, 281/281 | 100 % / 100 % | 99 % / 99 % |
| combi 1–3 | 671/695, 680, 685 | 99.95 % / 94 % | 98 % / 95 % |
| combi 4 | 702/713 | 99.95 % / 99.9 % | 98 % / 98 % |
| combi 5, 7 | 706/717, 709/720 | 99.93 % / 99.9 % | 94 % / 94 % |
| IEE | 1,050 / 1,068 (98.3 %) | 99.9 % / 99.5 % | 97 % / 96.8 % |
| Old World | 1,671 / 1,703 (98.1 %) | 99.7 % / 86.6 % (99.0 % wrapped) | 95 % / 81 % (95.2 % wrapped) |
| Old World Classic | 978 / 992 (98.6 %) | 99.1 % / 80 % (98.7 % wrapped) | 96 % / 76 % (95.8 % wrapped) |
| Darklands | 295 / 303 (97 %) | 99.95 % / 94 % | 98 % / 97 % |

Prologue, chaos 1–4, combi 1–4 and Darklands are with `--legacy-centre`; the others with the current rule.

Open on hlp:
- IEE has 4 transition costs left: A* ties where the game's path takes a different hex at the same cost (and then
  is or is not a bridge crossing), and two that leave a slot area straight onto a crossing.
- IEE has 10 flag-2 differences, all land-sea transitions of cost 500 where CA sets the flag and we don't.
- Transitions on other hexes than CA's (IEE: 6 of 7,476). `probe.py --centre` records the game's centre paths
  (refined and trimmed) and `HLP_DUMP_CENTRE=<file>` ours. The 26 IEE paths that still differ are equal-cost ties
  (3K had the same: decided by the push history), and the one search the game fails.
- Region tables: one misplaced transition changes every region pair whose cheapest path crosses it. Without the
  game's wrapping Old World is 86.6 % and Classic 80 %, nearly all of it the wrapped steps.
- Old World's largest region cost: 188,931 against CA's 189,229, also with `--wrap-like-game` (Classic's is equal with it).
- The equal-cost hop ties.

### The game's generator (Warhammer3.exe, 2026-10-10)

`research/hlp_spd_wh3/exe/probe.py` attaches Frida to the game during a `build_starpos` with `process_hlp_spd` and
sets hardware breakpoints only (a code patch trips the integrity check). It can snapshot the game's memory (`snap.py`
reads it), watch hex records for writes, or count pathfinder setups (`--trace`). `x.py` disassembles the exe.

- **Code.** `0x14261050c(model, reprocess)` is the entry: spd compute `0x142a24a0c` on `[model+0x910]`, spd writer
  `0x142a35f10`, then the hlp writer `0x142a31f44` on `[model+0x918]`, which builds the transitions (`0x142a16d44`)
  and then the region tables (`0x142a12e80`). Without `reprocess` it loads the files instead.
  - IEE: spd and the transitions take about 20 s; the region tables take over 10 minutes, the slow part of the
    game's build.
  - The game's IEE spd is byte-identical to the shipped one apart from the timestamp, so the shipped files are
    exactly what the game produces from those inputs.
- **CAMPAIGN_PATHFINDER** (`[[model+0x8d0]+0x5bc8]`): 3K's layout. Width and height at `+0xa8`, the hex array (8
  bytes per hex) at `+0x118`, the cost table at `+0x458` (4 × 64 u32: ppd costs, beaches 2100 in slots 1 and 2,
  road 80 in 0x3e). The search's navigation bit is `+0x89c` (0x40 or 0x80).
- **Two navigation bits.** Bit 6 is a static copy of the ppd's bit 7. Bit 7 is live: the setup `0x142944564`
  switches modifiers on it. Every modifier is a list of (x, y, direction mask, counter) entries with a reference
  count per edge, so an edge is open while nothing holds it closed.
  - Beaches (`pf+0x188`, 22 on IEE) switch both bits by the search's HLCI, in every mode.
  - In full mode (`params+0xc0 = 0`, bit 7) the setup also applies characters (`pf+0x1b8`, empty during the
    build), settlements (`pf+0x1e8`, 765 on IEE), a reopen list (`pf+0x218`) and `pf+0x248`.
  - A settlement holds 8 edge sets (`0x142944ba4`): set 0 is a zone up to 6 hexes around the slot area, sets 1–3
    smaller zones, and sets 4–7 the slot area with the ring of edges into it (5 and 7 inside, 4 and 6 the ring).
    Their default state is 4–7 closed.
- **hlp searches run in simple mode only.** `--trace` on IEE: all ~18,200 setups of the hlp phase have
  `params+0xc0 = 1` (bit 6, beaches only), and no settlement set is switched. Some pass a faction. The settlement
  zones and the live bit 7 belong to the campaign's own pathfinding at load, not to hlp. So the grid AtlasWH3 uses
  for hlp (static navigation plus beach gating) is the game's; the remaining differences are in the transition
  search and costing itself.
- **Transition searches** (`--transitions`, recorded once the hlp writer is hit):
  - `0x1429ef520(pf, out, &start, &end)`: the A* (`0x141e41bd0`) between a transition's two hexes, run in both
    directions; `out` = (u8 found, u32 cost). IEE: 7,260 searches. CA's transition cost is that A* cost for most
    transitions; where it is not, the path crosses a bridge and is re-costed (the 500 run), and our paths equal the
    game's (same A* cost). Slot transitions of cost 0 run no search.
  - `0x1429efcc4` (settlement → hex, 530) and `0x1429eff24` (settlement → settlement, 1,220) take a settlement
    object (`+8` id) and run with that settlement's faction. They do not give transition costs.
  - The matrix: `0x142a12d68` runs one search per transition from its hex (`0x1429f17f8`, 7,476 on IEE).
- **Transition costs** (`--waypoints`): `0x142a17200` builds the transitions of an area pair. Per hex pair,
  `0x142a25aec(a, b)` finds each end's first neighbour of slot type (4, 7–9; the same 14,535 hexes as map_data's
  slot areas) and its settlement through the tile group; the same settlement on both ends picks the
  settlement-passable search (`0x1429ef7c4`), else the A* (`0x1429ef520`). The path comes back as waypoints of 20
  bytes (world x, y; hex; step cost × 1024; flags), and the cost is the sum of the rounded step costs. Flags: 0x1, 0x2,
  0x4, 0x8 for a bridge crossing (the 0x1 hex's step is 500 for all four), 0x800 / 0x880 entering a slot / port
  area, 0x1000–0x7000 inside, 0x8000 the first hex after; any of 0xf800 sets flag 2. On IEE these sums equal CA's
  cost for all 33 transitions we had wrong. `wp_compare.py` lists our differing transitions with the game's waypoints.

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
