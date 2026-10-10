# AtlasWH3 plan

AtlasWH3 is a standalone hard fork of [Atlas3K](https://github.com/Ironictw2st/Atlas3K), retargeted from Total War:
THREE KINGDOMS to Total War: WARHAMMER III. This document is the plan. Progress is ticked off per phase in §6.

Written 2026-10-06, at fork point `fork-point` (= Atlas3K `cdd0a08`, 0.1.0-alpha.2).

## 1. Decisions so far

| Question | Decision |
|---|---|
| Fork type | Hard fork. New repo `robert-d-schultz/AtlasWH3` (public), Atlas3K history kept, `upstream` remote is fetch-only. No regular PRs upstream. |
| Games | WH3 only. 3K code paths are **replaced**, not kept behind a game switch. |
| Main goal | Replace BOB's campaign build. Editors come after the build. |
| Parity bar | Game-valid first, then byte parity with BOB where it's cheap. BC6H/BCn textures may never be byte-equal (CA encodes with AMDCompress). |
| WH3_visual_map_decompiler | Stays a separate repo. Its readers (BMD v27, BC6H, tile DB, rivers, pieces) are **copied** into `AtlasWH3.Formats` as needed. Its output doubles as round-trip test data. |
| Keep | Build, editors (scene / tile map / terrain), Campaign battles window, AI pathfinding (hlp/spd). |
| Cut | The experimental battle terrain editor. |
| Packs | Keep Atlas3K's own C# pack reader, plus zstd. No RPFM server dependency. |
| hlp/spd today | The game builds them. A native version is the last phase. |
| Devastated maps | Not separate campaigns. The devastated project is an input of the main map's build (§2). |
| Fixtures | Old World and IEE only. The OvN maps are out of date. |

## 2. The problem being solved

From the user: BOB's individual actions are tolerable (Tilemap takes about 5 minutes). The real cost is a full map
reprocess:
- about 10 separate actions,
- some of which read their inputs from the **.pack**, not `working_data`, so intermediate results have to be packed
  and installed between actions,
- and BOB takes a few minutes just to open.

All of that adds up to about **20 minutes** per full reprocess. So the first deliverable is **one command that rebuilds
the whole map from `raw_data`, with no manual pack round trips**. It may start out as a hybrid: native where ready,
headless BOB where not. Native steps then replace BOB actions one by one, starting with the ones that cost the most.

**The current BOB sequence and its pack dependencies** (from the user, 2026-10-06):

```
Campaign Heightmap ──pack──► Tilemap (main tile_list.bin) ──pack──► Global Tilemap
                   └─pack──► Campaign Trees
Blend, Color Overlay(s), Corruption Mask, Snow Mask, Event Area Mask   (no dependencies; usually run last)
Props: one BOB action → global_props.bin, global_props_sound.bin,
       global_props[_sound]_devastation_<type>.bin (incl. custom types), models\river_<id>   (usually run last)
Devastation pieces   (run last; probably reads working_data directly, not yet confirmed)
```

Notes on what reads what:
- **Rivers** are `ECRiver` splines in the `.layer` XML. The props action bakes each one into
  `models\river_<entity id>.wsmodel(.rigid_model_v2)` and places it in `global_props.bin` like any other prop.
- **Props and the heightmap.** In 3K, BOB used each entity's stored transform as it is. Ground alignment happens in
  Terry when the entity is placed or moved, so props did not read the heightmap. They do read the **map.hex** (the
  region of each object's hex) and the **`map_data.esf` header bounds** (3K's props action aborted without a loose
  `map_data.esf`). This still needs confirming on WH3.
- **Trees and height patches.** WH3 tree heights follow `full_logic_map` (survey: correlation 0.997), *plus*
  height-patched props: models with `apply_height_patch`, and their loose `.compressed_map` / `.wsmodel` / material
  `.xml` in `working_data` (`campaign_tools/docs/height_patches.md`). So trees depend on the heightmap and on the
  layers' height-patched props. It is not yet known whether they read those props from the `.layer` files or from
  `global_props.bin`; Phase 2's file-read trace settles it.
  **Settled 2026-10-08 (disassembly, 3.2):** the tree pass reads the props from the `.layer` files (every layer, visible
  or not) and the logic map through the game's file system. The tree heights are the nearest full_logic_map texel plus
  the patches; AtlasWH3 reads the patch files from the packs.

That is two pack round trips:
- the heightmap, before Tilemap and Trees
- the main tile list, before Global Tilemap

3K had the same trap (`docs/bob_campaign_build.md` §3: BOB's `.dds` and `global_map` steps decode what is in the game
packs, not the new loose file). Native steps read loose files, so these round trips go away **once Heightmap, Tilemap,
Trees and Global Tilemap are all native**. Phase 3 does those four first.

**Devastated maps are a BOB workaround, not real campaigns.** No DB row names `cr_oldworld_map_devastate_1` or
`cr_combi_expanded_map_devastate_1` as a campaign. They exist only so BOB will process the devastated version as a
campaign map and export its pieces. To make that work, they need:
- their own Terry project
- a fake `campaign_maps\<map>_devastate_1` folder with map.hex and map_data.esf
- a full second compile

AtlasWH3 should treat the devastated project as an **input of the main map's build**. The pieces step reads both
projects, composites each event area, and writes `pieces\`. There is no fake campaign folder and no second full
compile.

**What actually ships** (checked in the packs with RPFM, 2026-10-06):

| Folder | Ships |
|---|---|
| main map (`wh3_main_combi_map_1`, `cr_combi_expanded_map_1`, `cr_oldworld_map_1`) | the full compiled map **plus `pieces\event_*`**: the *undevastated* state of each event area, used to restore it |
| devastate map (`*_devastate_1`) | **only** `environment_collection.xml`, `event_area_mask.dds`, `event_tiles`, `event_trees` and `pieces\event_*`: the devastated state |

Notes:
- Both folders use the same piece ids (vanilla: 202 = 202; IEE: 248 = 248).
- Each piece holds:
  - `objects(.culture)`, `bmd_objects_sound(.culture)`
  - `objects_devastation_<type>` / `bmd_objects_sound_devastation_<type>` (devastate side only)
  - 9 raster crops: full_height_map, shroud_heights, global_blend, colour_overlay, lf_sea_colour, lf_normal,
    corruption, snow, tile_mask
  - `mask`, `texture_info`, `tile_list` (missing on a few), `tree_list`, and `rivers` on a few
- Vanilla's devastate folder also has the map-wide `global_props_sound_devastation_{chaos,nagash,skaven}.bin`.
  Almost every emitter in them is also in a piece; the few that aren't sit on event-area borders. These are the
  erroneous extras the user noticed. The user's packs ship none of them.
- `global_props_devastation_*.bin` and the devastate folder's full rasters, tile list and global props are BOB
  by-products: they are in `working_data` but ship nowhere.
- BOB never cleans `pieces\`. The kit's `working_data` has 464 Old World pieces where the map has 254 event areas.
  The native step writes exactly the current event areas' pieces into an emptied folder.

**`camera_heightmap.png` has no working BOB action.** *Generate Camera Height Map* always crashes on the user's maps.
The user makes the file by hand:
- a PNG with a tEXt `height_scale` chunk
- for IEE, the new areas merged into vanilla's file

So a native step here replaces manual work, not a BOB action. There is no BOB output to compare against: the reference
is CA's shipped `wh3_main_combi_map_1` file, built from the decompiled vanilla project.
- **Found in `!cr_oldworld_campaign_devastate.pack`:** `cr_oldworld_map_devastate_1\pieces` holds 502 pieces:
  - Old World's own 254
  - **plus 248 with IEE's ids** (all 202 vanilla combi ids among them), not in Old World's main folder
  - only those 248 carry `objects_devastation_<type>` files; Old World's own 254 have none

  Explained by the user: BOB exports a piece for **every** event area in the DB, even those linked only to provinces
  that aren't on the map. These extra pieces are harmless. **Checked 2026-10-09:** the 248 are byte-identical to IEE's
  devastate pieces (IEE's 3200 × 1941 mask in their texture_info), so they are stale files in that working_data folder.
  BOB does create a folder for every DB area, but leaves it empty when the area is not on the map (Old World 210). The
  native step writes a piece for every area on the map's event mask, into an emptied folder.

## 3. Where things stand

**The fork**
- About 50k lines of C#: App 17k, Core 19k, Formats 9k, Cli 2.7k, Tests 4.9k. There are 106 `3k_` literals; "Atlas3K"
  appears in 257 files.
- **It does not build yet.** `global.json` pins SDK 9.0.312, but this machine has 9.0.301, 10.0.203 and 10.0.302.
- The tests (`TestKits.cs`) expect 3K kit data, so they will all need WH3 fixtures. (Done in 4.6: every 3K test was
  moved to the WH3 fixtures or removed.)

**The survey** ([`surveys/warhammer3.md`](surveys/warhammer3.md))
- It is the step-by-step technical baseline.
- Its probes and extracted files (`output/survey_wh3`, the 2023 kit with `cr_albion_map_1` on an `F:` drive) are **not on
  this machine**. Treat its numbers as claims to re-verify, especially the byte-identical `full_logic_map` rebuild.

**Test data is much better than the survey assumed.** It said the only WH3 source map was albion (200 × 200, no rivers,
few props). This machine's kit has two up-to-date maps, both the user's own:

| Map | Hexes | Sources | BOB output in `working_data` | Use |
|---|---|---|---|---|
| `cr_combi_expanded_map_1` (IEE) | 1600 × 970 | 226 layers | full: rivers, props, sound, event tiles/trees | IE-scale props, rivers; the main fixture |
| `cr_oldworld_map_1` | 2048 × 1774 (Terry's max) | 23 layers | full, plus `pieces/`, `patch_mask`, `tile_mask` | worst case for size and time |
| `*_devastate_1` (both maps) | as above | hard-linked + devastation layers | pieces, `global_props_devastation_*` | devastation pieces (see §2) |

The OvN maps (`ovn_mootland_map_1`, `ovn_southlands_map_1`) are out of date and are **not** fixtures.

**There is no small fixture.** Both maps are large, so full-map runs are slow tests run on demand. The fast unit tests
work on windows cut from the fixtures: a block of hexes for tile matching, one region's props, one river.

**Caveat:** some sources are newer than their outputs. For example, `cr_oldworld_map_devastate_1/tile_map.png` changed
after its BOB run. Parity needs **frozen fixture pairs** (sources and BOB output from the same moment).

## 4. Resources and how each is used

| Resource | Use | Licence |
|---|---|---|
| Atlas3K (this repo's history) | Architecture, 3K rules that still hold (hex grid, tree RNG, compressed_map, PNG, lookup), research method (Ghidra + Frida + parity diffs), `tools/bob_mcp` headless BOB runner | MIT; keep the notice |
| Survey `docs/surveys/warhammer3.md` | Per-step verdicts and formats | — |
| WH3 game `D:\...\Total War WARHAMMER III` | Packs (vanilla compiled maps, DB, tile DB), `Warhammer3.exe` (hlp/spd generator) | CA's; never shipped |
| Assembly kit `...\assembly_kit\binaries` | `bob.modder.x64.exe` and the warscape / bob_* DLLs for Ghidra and Frida; headless BOB for the hybrid build | CA's; decompiles never committed (as in Atlas3K) |
| `raw_data\terrain\campaigns`, `working_data\terrain\campaigns` | Fixtures (see §3) | your maps |
| `WH3_visual_map_decompiler` | WH3 readers to copy: `Bmd/` (GlobalPropsParser, CultureMask, entities), `Formats/` (BC6H, CompressedMap), `Tiles/` (TileDatabase, TileSettings), `Rivers/` (RiverMesh, spline fit), `Pieces/`, `Imaging/SnowMaskStretch`, `Terry/` (project reader/writer). Docs on river splines, event-area pieces, colour-overlay blending and tree scatter. | MIT (yours) |
| `campaign_tools` | Format knowledge: `map_hex.py` (map.hex v20), `terry.py` (layer compositing), `assetdb.py`, `height_patches.py`, `trim_mips.py` (the BOB 14-mip pieces crash), `devastation.py`, `corruption_mask.py`; `exe_tools/` for the `Warhammer3.exe` work in Phase 6 | yours; add a licence before copying code |
| `CampaignMapToolkit` (CAIME) | Today it makes map.hex, map_data.esf and pathfinding.ppd; AtlasWH3 takes those files as **inputs**, as Atlas3K did. `FileFormatDefinitions/*.bt` are a format reference. | **Non-commercial share-alike.** Incompatible with MIT: **never copy CAIME code into AtlasWH3**. Read its docs and templates for knowledge only. |
| RPFM `schema_wh3.ron` (`%AppData%\FrodoWazEre\rpfm\config\schemas`) | DB table layouts read straight from the file (see Phase 1). This is not the RPFM server. | MIT |

## 5. Principles

1. **WH3 only, no game-profile layer.** Constants, names and versions are WH3's. Where a number comes from data
   (world width from `campaign_map_playable_areas`, grid size from `tile_map.png`), read it; don't hardcode it.
2. **Loose files in, loose files out.** Every step reads `raw_data` and `working_data` (or the output folder), and
   never needs a pack round trip. Where BOB would read from a pack, AtlasWH3 reads the loose file the pack would have
   held.
3. **Every step has a BOB fallback** until its native version is game-valid on all fixtures. A build always produces a
   complete map. The one exception is `camera_heightmap`, where BOB crashes: its fallback is a copy of the user's
   hand-made file.
4. **Each step reaches game-valid before parity.** Game-valid means: the game loads it, no visual or logic regressions
   on the fixture maps, and the round trip through the decompiler gives the same Terry project. Parity is measured
   (`Cli parity`, masked bytes) and recorded per step.
5. **Never write CA code into the repo** (decompiles, DLL bytes) or CAIME code.
6. Keep upstream cherry-picks possible: change files in place where it's reasonable, and rename in one mechanical commit.

## 6. Phases

Estimates are focused-work days with an agent doing most of the coding, and they inherit the survey's uncertainty.
Phases 1 and 2 overlap.

### Phase 0: Fork housekeeping (1–2 days)

- [x] SDK: move `global.json` to the installed **.NET 10** SDK (LTS) and retarget the projects to `net10.0(-windows)`.
  Build green, with the 3K tests skipped or failing as expected.
- [x] Rename `Atlas3K` → `AtlasWH3` in one mechanical commit: solution, projects, namespaces, `Product`, settings
  folders (`%AppData%\AtlasWH3`, `%LocalAppData%\AtlasWH3`), project extension `.atlas3k` → `.atlaswh3`, CLI name,
  `ATLAS3K_*` env vars → `ATLASWH3_*`.
- [x] README: what AtlasWH3 is, "derived from Atlas3K" credit, MIT notice kept and a copyright line added,
  `THIRD_PARTY_NOTICES.md` updated.
- [x] Game plumbing: install folder `Total War WARHAMMER III`, process `Warhammer3`, kit `bob.modder.x64.exe`.
- [x] Cut, in their own commits so the cuts are easy to find later (2026-10-08):
  - the battle terrain editor (`BattleWindow*`, the battle project and object code that only it uses; keep what the
    Campaign battles window needs)
  - `global_mesh`, `GlobalMesh/`, river `height_patches`, `climate_map.cm`, `lf_height_map` / `lf_sea_height_map`
  - seasons (DB `seasons_tables`, season buckets, the 3D viewer's season switch)
  - 3K tree tables and the 3K-only `research/` folders (they stay in git history). The research folders are cut; the
    3K tree tables and the season fields of the 3K formats go with their WH3 replacements in Phase 1 (the tree step
    and the scene editor still read them until then).
- [x] Mark every remaining 3K-shaped step as `PendingStep`, so the pipeline runs but says what isn't ported.
- [x] A `docs/upstream_sync.md` note: how to look at upstream (`git fetch upstream; git log fork-point..upstream/master`)
  and cherry-pick fixes in shared code.

### Phase 1: Read WH3 (1.5–2.5 weeks)

The editors, the build and parity all need this foundation.

- [x] **Packs:** zstd entries in `PackFile` (u32 size + zstd frame) with a managed library (ZstdSharp.Port). Confirm
  the header variant on the current game build (PFH5 expected). Done 2026-10-08: every pack is PFH5; some tile meshes
  are LZ4 (K4os).
- [x] **DB schemas:** this addresses the schema-drift concern. WH3 is still being updated, so table versions change.
  `DbBinaryTable` looks layouts up in RPFM's `schema_wh3.ron` (plain file, parsed directly) when present, falls back to
  an embedded snapshot, and reports an unknown version clearly instead of misreading it. Tables needed:
  - `campaigns`, `campaign_map_playable_areas`
  - `campaign_tree_ids` / `_types` / `_type_cultures` / `_variants`
  - `prefab_types` (culture bits)
  - `campaign_map_event_areas`
  - `region_to_province_junctions`
  - the battle tables the Campaign battles window uses
- [x] **Terry:** `.terry` project v26 / scene v41; the WH3 TerrainMap types (Height / HeightSea float32, HeightShroud,
  BlendCampaign, ColorOverlay(Sea), CampaignTree, CorruptionMask, SnowMask, EventAreaMask, PatchVisibilityMask,
  BorderMask); regenerate `component_schema.json` / `entity_configuration.xml` from the WH3 kit; layer compositing
  rules (add, replace-unless-255, hard light, `dst + src·(1−dst)`, AND), from the decompiler's colour-overlay
  measurements.
- [x] **map.hex v20** (extra table plus the trailing bit array; 40 climates), from `campaign_tools/shared/map_hex.py`.
- [ ] **Tile DB:** `_settings.bin` v12, tile `.bin` v6 / variation v11 (climate texture set), CHMF `hf_height_map.data`,
  RMV2 v7 tile meshes. Copy from the decompiler's `Tiles/`. Tiles, variations and `_settings.bin` done (2026-10-08);
  CHMF and the RMV2 v7 tile meshes not yet checked on WH3.
- [ ] **Compiled readers** for parity and the editors:
  - `tile_list.bin` v2 (done: read and write, byte-identical)
  - `trees.campaign_tree_list` v4 (done: read and write, byte-identical)
  - BMD v27 bodies, `global_props(_sound).bin` and `.culture` (copy from the decompiler's `Bmd/`)
  - BC6H `full_height_map.dds` (done: decoder and encoder, `Bc6h`)
  - `map_data.esf` with `REGION_AREA_INDEX`
  - hlp/spd v1 (read only)
- [x] **Fixtures** (done in 4.6 as `fixture-freeze`, beside the kit rather than in %LocalAppData%, so the files can be
  hard links): freeze one source + BOB-output snapshot per fixture map outside the kit, with a manifest and the game
  build.
  - IEE first, then Old World. Each needs a fresh full BOB run from the frozen sources, done once and scripted.
  - Then a "decompiled vanilla IE" fixture: the decompiler's Terry project of `wh3_main_combi_map_1`, with CA's
    shipped files as the reference.
  - Record which files of a build actually ship in the mod pack, especially from the devastate folder (only
    `pieces\`, or also `global_props_devastation_*` and the rasters?).
- [x] Rewrite `TestKits` against the fixtures (4.6). Tests skip cleanly when a fixture is missing, as they do now.

### Phase 2: One-click build, hybrid (1–1.5 weeks)

This is the first user-visible win. It fixes the 20-minute problem before any step is native.

**Found 2026-10-08 ([`bob_wh3.md`](bob_wh3.md)):** WH3's BOB has no `<selected_actions>`. A silent run of the map's
`.terry` always runs the same 8 default actions (Terry file, the six terrain-map masks, Devastation pieces) and opens
no game pack. Campaign Heightmap, Shroud Heights, Tilemap, Trees, Global Tilemap and the camera height map exist only in
the GUI's action popup, so they have **no headless fallback**: 3.1–3.4 must be native. The pack round trips of §2 come
from those GUI actions alone, so they go away with 3.1–3.4 and are not automated.

- [x] Confirm the §2 sequence by instrumenting a run (Frida file-read trace, `research/bob_re/`): the default group reads
  the kit only; the GUI-only actions are not instrumented, since they are replaced rather than driven.
- [x] Headless BOB: `ScratchKit` (an isolated copy of the kit, so BOB never writes into the user's) and `BobRunner`
  (silent run of the default group, `bob.log` guard, one launch). CLI `bob-scratch`, `bob-run`, `bob-actions`.
- [ ] ~~Automatic pack round trips~~: dropped, see above.
- [x] Devastated builds without the fake campaign: done natively by 3.6 (the devastated project is an input).
- [ ] ~~Hybrid pipeline~~: dropped in Phase 4, since every step but hlp/spd is native.
- [x] Pack and install segments retargeted to WH3 (`data` folder, `Warhammer3.exe` running check): done in Phase 4.5.
- [ ] ~~The `trim_mips` fix-up~~: dropped, since the pieces are native (3.6).
- [ ] ~~Timing report per step~~: dropped in Phase 4 (each step's time is in `docs/native_campaign_build.md`).

### Phase 3: Native steps, game-valid (5–8 weeks)

3.1–3.4 come first, because together they remove both pack round trips (§2). After that, the order follows the
Phase 2 timing report: BOB time saved compared with cost to port.

| # | Step | Writes | Basis | Est. |
|---|---|---|---|---|
| 3.1 | `heightmaps` | `full_logic_map.compressed_map`, `full_height_map.dds` (BC6H, BCnEncoder.Net if its BC6H is good enough, else DirectXTex), `shroud_heights.dds` | survey: logic map rebuilt byte-identical with 3 rule changes; shroud is a flipped copy | 3–5 d |
| 3.2 | `trees` | `trees.campaign_tree_list` v4 | 3K generator, height from `full_logic_map`, WH3 tree tables, 256-variant check (`tree_variants.py`) | 2–4 d |
| 3.3 | `tile_list` | `tile_list.bin` v2, `tile_mask.dds` | 3K tile matching on the WH3 tile DB; verify against the fixtures. BOB takes about 5 min for this. Atlas3K's tile-map editor and validator come with it. | 1–2 w |
| 3.4 | `global_map` (Global Tilemap) | 8-bit `global_blend.dds` (255 → 0), per-map `texture_arrays.xml` (from the asset db), subset `tile_list.bin` | survey | 1–2 d |
| 3.5 | `masks` | `colour_overlay`, `lf_sea_colour` (BC1), `corruption_mask` (R8), `snow_mask` (BC4, with the stretch to whole blocks), `event_area_mask`; `patch_mask` comes with 3.3 (Tilemap writes it); `lf_normal` (DXT5nm, NVTT) belongs to Campaign Heightmap (3.1) | TIF → DDS; mips as BOB writes them | ~1 w |
| 3.6 | `devastation_pieces` | `pieces\event_*` for **both** the main and the devastate folder, plus the devastate folder's `environment_collection.xml`, `event_area_mask.dds`, `event_tiles`, `event_trees` (§2 table) | read the main **and** devastated projects directly (§2); the decompiler's `docs/event-area-pieces.md`; exactly the current event areas; also removes the 14-mip crash. Needs 3.9's BMD writer for the piece objects, so the piece rasters, tiles and trees come first, then the objects. | 1–2 w |
| 3.7 | `lookup` | `*lookup*.tga/.dds`, `_minimap.tga` from CAIME's exported `*_lookup.bmp` | works as is (byte-identical on albion); fix palette alpha | ≤ 1 d |
| 3.8 | `camera_heightmap` | `camera_heightmap.png` (0.5 px/hex on IE; tEXt `height_scale`, 8192-byte IDAT chunks, Atlas3K's byte-exact PNG/zlib writer) | logic heights + prop height patches (`height_patches.py`). BOB crashes on this action (§2), so this replaces hand editing. Check it against CA's shipped vanilla IE file, and against the user's hand-made IEE and Old World files. Worth moving earlier if the hand editing costs more than the BOB steps. | ~1 w |
| 3.9 | `global_props` | `global_props.bin`, `global_props_sound.bin`, `global_props[_sound]_devastation_<type>.bin` for every `bmd_export_type`, custom ones included (BMD v27, culture-mask buckets) | the decompiler's reader is the spec; region lookup from map.hex v20 + map_data bounds; IEE fixture | 2–3 w |
| 3.10 | `rivers` (part of BOB's props action) | `models/river_<id>` RMV2 v8 2-vertex ribbon + `.wsmodel` | the decompiler's `docs/river-splines.md` (tessellation rules already measured). Only `ECRiver` splines are baked; a prop that references a river mesh is an ordinary prop. | 1–2 w |

**3.1 status (2026-10-08):** `HeightmapsStep` is in the pipeline. `full_logic_map` and `shroud_heights.dds` are
byte-identical to BOB's on IEE and Old World; `full_height_map.dds` uses AtlasWH3's own BC6H encoder (neither
BCnEncoder.NET's nor DirectXTex's was usable), with half BOB's RMS error on both maps. Left: the in-game check.
Numbers in `docs/native_campaign_build.md`.

**3.2 status (2026-10-08):** `TreesStep` is in the pipeline. Old World's list is byte-identical to BOB's. On IEE every
tree id, position and rotation is identical, and 93.8% of heights are bit-exact (99.8% within 1e-3). The rest are ulps
under height patches, plus 457 trees not yet explained. Found on the way: the grid comes from map_data.esf, the
terrain width from the .terry's `world_width`, the tree ids from the mods' own tables as well, and the patches from
the packs. IEE's map_data.esf is CBAB. Left: the in-game check. Numbers in `docs/native_campaign_build.md`.

**3.3 status (2026-10-09):** `TileListStep` is in the pipeline, with `tile_mask.dds` (Tilemap writes it with the tile
list; it was listed under 3.5). Old World's `tile_list.bin` and `tile_mask.dds` are byte-identical to BOB's; on IEE the
mask is byte-identical and every record identical but 473 heights in an area whose height layers were edited after its
BOB run. IEE 61 s, Old
World 139 s (BOB about 5 min on IEE). Found on the way (read off warscape.modder.x64.dll): passes 2-5 visit
scan_tile_areas' point list, the junction pass's 2×2 strip rule skips off-map neighbours, heights from the composited
Height/HeightSea (BOB reads the pack's) with the box mapped by multiplying with 1/W, 1/H, use_alt_lf read from the right
tile byte, the path table sorted. Left: the in-game check. The validator's 3K hex rules and the tile-map editor are
still 3K-shaped (Phase 5); the two findings that blocked the WH3 maps (no climate map, black off-map hexes) no longer
do. Numbers in `docs/native_campaign_build.md`.

**3.4 status (2026-10-09):** `GlobalMapStep` is in the pipeline. On Old World all three files are byte-identical to
BOB's. On IEE `texture_arrays.xml` and `tile_list.bin` are byte-identical; `global_blend.dds` differs only in one area
of the `iee` blend layer that was edited after its BOB run. IEE 5 s, Old World 7 s. The survey was right on the blend
(255 → 0, not flipped) and the subset, but `texture_arrays.xml` is not a copy. It is generated from the merged asset
variation db (`warscape_asset_variation_db\*.assetdb`) of the game **and the mod packs**: Old World's mod adds
`mud_dry_darklands`, which shifts every later group index. So the map's mod pack has to be linked (`--pack`), as for
trees. The step reports a blend whose palette disagrees with the group list. The pack round trip into Global Tilemap
goes, since the step reads the build's own `tile_list.bin`. Left: the in-game check. Numbers in
`docs/native_campaign_build.md`.

**3.5 status (2026-10-09):** `MasksStep` is in the pipeline. All five files are byte-identical to BOB's on IEE and Old
World, every mip level (Old World against a fresh BOB run in the scratch kit; its working_data overlays had been through
`trim_mips.py`). BOB saves them through a statically linked old DirectXTex, now ported (`DirectXTex`: linear mip
filter, R8 truncating and RGBA8 rounding stores, BC1, BC4 with MSVC's folded palette). IEE 7 s, Old World 12 s. The
mask union truncates (it rounded). `patch_mask.dds` turned out to be the Tilemap action's: `TileListStep` writes it (BOB's mode
byte-identical on both maps, fitted to the whole map by default). Left:
- the in-game check;
- the in-game check of the **patch_mask fix** (the user's report): BOB's 128 × 77 grid covers 1,925 of IEE's 1,941
  tile-map rows (Old World 3,520 of 3,549), and the game stretches it over the whole map, so the sea-floor mask pokes
  out under land, worst in the north. `TileListStep` now writes it fitted to the whole map by default (project setting
  "Patch mask", `--patch-mask vanilla` for BOB's);
- BOB's Patch Visibility Mask with a painted PatchVisibilityMask (no fixture has layers);
- `lf_normal.dds` (NVTT, from the Heightmap action), still not native;
- the overlays have the full mip chain as BOB writes it (15 on Old World), which BOB's Devastation pieces crash on
  (the `trim_mips` fix-up of Phase 2).
Numbers in `docs/native_campaign_build.md`.

**3.6 status (2026-10-09):** `DevastationPiecesStep` is in the pipeline, without the objects. Read off bob_terrain and
measured on IEE's mod pack (both folders), Old World's working_data and a fresh BOB run: every piece texture (all mips),
`texture_info` and `mask` cut from the same map textures are byte-identical (IEE 248, Old World 254 pieces); every
road and tree list holds the same tiles and trees (Old World's tree lists and `event_trees` byte for byte);
`event_tiles` is numbered in the order of a hash map keyed by addresses, so its indices cannot match. The devastated
project is an input: its rasters, tile list and trees are built into the cache (no fake campaign_maps folder;
full_height_map only where the pieces cut it), and its pieces match IEE's shipped devastate pieces the same way
(full_height_map aside, AtlasWH3's BC6H). Found on the way: BOB's pieces action regenerates the trees from the kit's
XML database, dropping the mods' tree types; Old World's extra devastate pieces are stale IEE copies (§2); and
`!cr_oldworld_campaign.pack` (Oct 6) ships an `event_area_mask.dds` with every pixel 255, BOB's from before the event
area TIF was painted (the devastate folder's is right). Left:
- the objects and sounds (`objects`, `bmd_objects_sound`, `_devastation_<type>`, `.culture`): done in Phase 4.1;
- the `rivers` files (lava river splines; IEE's main map has 2): done in Phase 4.2;
- `lf_normal.dds` (NVTT; cut from working_data's for now): Phase 4.3. The devastated folder's
  `environment_collection.xml` is the map author's own file;
- the in-game check.
Numbers in `docs/native_campaign_build.md`.

**3.7 status (2026-10-09):** `LookupStep` works as is on WH3: IEE's `.tga` and `.dds` are byte-identical to BOB's and
to the shipped pack's, the minimap differs along region borders (BOB samples up to one pixel off [::4, ::4]; good
enough, the user's call). There is no palette-alpha fix to make: the alpha-0 elector_counts palette the survey saw is a
different source image. Old World's shipped lookup has the same colour in every pixel, but **the user reordered its
palette by hand** (`lookup_tweak.py`): the game reads only 1024 palette entries and Old World has more settlement
regions than that. The reorder is now a build setting (Phase 4.4). Left: the in-game check. Numbers in `docs/native_campaign_build.md`.

**3.8 status (2026-10-09):** `CameraHeightmapStep` is in the pipeline. WH3's generator (read off the kit) is 3K's:
BOB's grid, cell sampling and PNG encoding carry over, and the grid is the tile map × `cam_hmap_resolution_scale`. Why
BOB fails: the kit's rules.bob sets none of the `cam_hmap_*` keys and BOB has no defaults, so the grid is 0 × 0. CA ran
0.25 (every shipped vanilla combi map is 720 × 486, and the user's files have the same scale); the step defaults to
that and 8 samples per unit. BOB's scene height is max(height patches, tile terrain, lf). The step uses the logic map
plus every height-patched layer entity, `for_camera_height_map_only` included (the trees' patch loader), so mountains'
patches are in the map, unlike the hand-made files. The tile terrain is not modelled. There is no BOB output to match:
CA's vanilla file is stale in places. Against the user's files: IEE correlation 0.986 (higher on the 3,040 patched
props and along slopes, from BOB's max-sampling), Old World 0.997 (no patches). IEE 18 s, Old World 15 s. Atlas3K's 3K
scene is gone: the editors' ground height now uses the same WH3 ground (`TreeHeightField`). Left:
- the in-game check;
- a BOB run with the `cam_hmap_*` keys added to rules.bob, which should make the GUI action work and give a reference;
- BOB's tile terrain term and blur.
Numbers in `docs/native_campaign_build.md`.

**3.9 status (2026-10-09):** `GlobalPropsStep` is in the pipeline: `global_props.bin` and `global_props_sound.bin` (BMD
v27, `Bmd27Body`, which round-trips all 166,549 bodies of BOB's IEE and Old World files). WH3's BOB is not
byte-reproducible here: two runs on the same Old World sources differ in record order, entry order and the numbering of
the mountains' one-prop buckets (address order). So parity is measured as content (each body's records as a set),
on which two BOB runs agree 100%. With BOB's model boxes (a research switch, since removed): Old World 99.6% of 59,085
groups, IEE 99.9% of 101,561, IEE sound file 98.7%; the same bodies and objects as BOB on both maps. The step boxes every
model by its own bounds from the packs (decided 2026-10-09: there is no working_data for a model to be missing from);
BOB boxes as 2 × 2 × 2 any model whose .wsmodel isn't loose in the kit, so Old World 436 objects and IEE 8,248 go to a
coarser cell than BOB's (content parity 98.8% and 85.1%). IEE 17.6 s, Old World 5.2 s. Read off the data and qttoolutility: culture buckets (16 ×
prefab_types value, one per culture name), WH3's ECTransform maths and its prefab flattening (through Euler degrees),
the ear clipping, hidden groups in layers and prefabs. Left:
- the in-game check;
- the remaining ulps (effects inside prefabs, rotated sound emitters), see `docs/native_campaign_build.md`;
- the devastated project's `global_props[_sound]_devastation_<type>.bin` (they ship nowhere, §2) and the pieces'
  objects (3.6, done in Phase 4.1).
Numbers in `docs/native_campaign_build.md`.

**3.10 status (2026-10-09):** `RiversStep` (`Wh3River`) is in the pipeline: `models\river_<entity id>.wsmodel` and
`.wsmodel.rigid_model_v2` for every river of the exported layers. Against four BOB runs (the user's working_data: IEE
60 rivers, Old World 127; the scratch kit's fresh runs: IEE all 266, Old World 127) every model is identical apart from
the two bytes BOB leaves uninitialised, and every `.wsmodel` is identical. Read off warscape (no Ghidra here: capstone,
`research/bob_re/disasm_fn.py`) and prototyped in `research/rivers/wh3_tessellate.py`. WH3's bake is not 3K's: a 4-D
spline (x, y, z, width) evaluated in a different order, rows every L / ⌈L / 0.2⌉ at t = k / n, a column count from the
**start** width (⌈clamp(w(0), 1, 10)⌉ + 1, so the survey's "2-vertex ribbon" holds only for rivers under 1 wide),
float positions in the entity's frame, and no snap pass, half floats or height patches. Found on the way: BOB takes a
spline's heights relative to its stored first point and raises the prop by it (Old World's 19261f91ac8e913; x and z
are not rebased, as 15 IEE rivers show), which `global_props` now does too; `global_props` boxes rivers by this bake,
so the working_data lookup is gone. Content parity unchanged (Old World 98.83%, IEE 85.09%, IEE sound file 98.66%),
with no river record differing. IEE 8 s, Old World 0.8 s. Atlas3K's 3K river bake (`BobRiver`, river numbering,
`--river-geometry`) is removed. Left:
- the in-game check;
- the pieces' `rivers` files (3.6, done in Phase 4.2).
Numbers in `docs/native_campaign_build.md`.

Each step is done when:
- it is native in the hybrid pipeline,
- the game loads every fixture map and shows no visible difference from BOB's build,
- the decompiler round trip agrees,
- and parity numbers are recorded in `docs/native_campaign_build.md`.

### Phase 4: Loose ends, then parity

Rewritten 2026-10-09, after 3.10. Every BOB step but hlp/spd is native, so this phase first closes what a native build
still can't write (checked against the shipped IEE, Old World and vanilla packs), then the Phase 1–3 leftovers, then
parity.

**Completeness:** what ships but is still BOB's
- [x] 4.1 The pieces' objects: `objects(.culture)`, `bmd_objects_sound(.culture)`, and on the devastate side
  `objects_devastation_<type>` / `bmd_objects_sound_devastation_<type>`. This is 3.9's builder applied per event area,
  to the main and devastated projects (3.6 left).
- [x] 4.2 The pieces' `rivers` files, cut from 3.10's bake (IEE's main map has 2, from its lava rivers).
- [x] 4.2b `environment_collection.xml` for both folders (BOB's Terry file action; new `environment` step) and the main
  folder's `event_vfx` (nothing in the kit writes it; the pieces step does now).
- [x] 4.3 `lf_normal.dds` (DXT5nm; BOB's Campaign Heightmap action, NVTT). The main map ships it and the pieces cut it.

**4.1–4.2 status (2026-10-10):** `DevastationPiecesStep` writes every piece's objects and sound files, per
bmd_export_type, with their `.culture`, for the main and the devastated project (`Wh3GlobalPropsBuilder.BuildPieces`).
The same files as BOB in all four folders. BOB's record order changes from run to run, so parity is content (records
with their culture masks, `gp27-pieces-diff`): IEE main 99.96% of 485,625 records, IEE devastate 99.78% of 552,712, Old
World main 95.98% of 80,002, Old World devastate 78.36% of 5,518. Leaving out the three fields BOB reads only from
models loose in its kit (the model-box decision of 3.9) gives 99.98%, 100.00%, 99.87% and 99.95%. The `rivers` files
list the river splines whose material contains `cwb_campaign_river_lava` (BOB's test, read off bob_terrain; IEE's
current `cr_campaign_water_plane_river_lava` rivers do not match it, so neither BOB nor the step lists them). Found on the
way:
- the `.culture` layout: sections `p m v lp ls ht sc` / `ss`; bit 63 for a culture with no prefab_types row; `ht` is
  (triangle count, mask) per run of one hole's triangles;
- BOB writes no files for a type with nothing in the piece, and no light probes or rivers in pieces;
- **an ECTransform pivot moves the entity** (position + pivot − RS·pivot). This also fixes two IEE mountains in
  global_props (content parity: IEE 85.10%, Old World unchanged at 98.83%);
- **the vanilla kit's Terry `configuration.xml` names `art/campaign/prefabs` for campaign prefabs**, a mistake (they
  are in `art/prefabs/campaign`) that the user corrects by hand in each kit; this kit had the vanilla value again
  since 2026-10-09, so IEE's `white_tree_of_morash_chaos_bits` was dropped. The prefab library now ignores that value.

**4.2b status (2026-10-10):** `EnvironmentStep` compiles `environment_collection.xml` from the project (the .terry's
global_lighting and devastation_light_environments, a SPHERE / CYLINDER per ECEnvironmentVolume). Against BOB: IEE's
devastate folder byte-identical; IEE and Old World the same lines in another order, which two BOB runs of the same IEE
sources also differ in (address order). `event_vfx`: the game reads it line by line and hands each name to the asset
interface the piece loader uses, keeping nothing (Warhammer3.exe 0x271ec08); not an index. Whether that call caches is
open (a breakpoint in the running game would settle it). See `docs/native_campaign_build.md` (pieces).

**4.3 status (2026-10-10):** the `heightmaps` step writes `lf_normal.dds` straight from the composited Height map, with
no Terry export and no PNG: Terry's own normals (read off its plugin: a Sobel filter, spacing 4, no height patches;
identical to Old World's Terry export on 99.94% of sloped pixels) in BOB's NVTT DXT5nm layout and header, the full mip
chain. The blocks are the DirectXTex port's, with lower error than NVTT's on the same input. Left: the in-game check.

Left: the in-game check (an area devastates and restores, with its objects and lava rivers). Numbers in
`docs/native_campaign_build.md`.

**Leftovers from Phases 1–3**
- [x] 4.4 Lookup: build Old World's palette reorder (more settlement regions than the game's 1024 palette entries,
  today `lookup_tweak.py`) into the step (3.7).
- [x] 4.5 Pack and install for WH3: `data` folder, the `Warhammer3.exe` running check (Phase 2), so a full rebuild is
  one command from `raw_data` to an installed pack.
- [x] 4.6 Fixtures and `TestKits` (Phase 1): frozen source + output snapshots, so the parity work below has regression
  tests.

**4.6 status (2026-10-10):** `fixture-freeze <name> --maps a,b --packs p,q` (`KitFixture`) freezes maps' folders
(raw_data and working_data, main and devastated) and their mod packs into `atlaswh3_fixtures\<name>` beside the kit.
Sources and packs are hard links: no disk space, and Terry and the pack install replace a file when they save it, so
the link keeps the frozen bytes. working_data is copied: BOB rewrites its outputs in place (2026-10-10, the IEE
reprocess changed the frozen outputs through the links). `kit\` is an assembly kit of junctions (into `maps\` for the frozen folders, to the
live kit for the rest). `manifest.json` records every file's size and time, the game build, and the sources newer than
their map's BOB outputs. `fixture-check` finds a file rewritten in place (a hard link shares its size and time);
`fixture-delete` removes the junctions, never the kit behind them.
- Frozen: `iee` (both IEE maps and the IEE pack: 822 files, 9.1 GB, in 4 s) and `oldworld` (both maps, both packs:
  8,741 files, 18.4 GB). Old World's pair matches. IEE's sources have edits from 2026-10-03/05, after its BOB outputs
  (2026-09-29/30); its manifest lists them. Freeze IEE again after its next BOB reprocess.
- 2026-10-10: `iee` frozen again after the user's BOB reprocess of trees, tilemap and global_props (1,234 files, of
  which 719 working_data files copied, 1.1 GB). `oldworld` is still the linked freeze: freeze it again after its next
  BOB run. Its global_props pair is not clean either way: the 10-03 GUI run predates the loose mountain .wsmodel files
  of 10-05 (so BOB put those mountains in bucket 31), and the scratch kit's headless runs leave out the quadtree margin
  (4.8).
- `TestKits.Kit(map)` / `Pack(name)` / `Built(map, ..)` / `Paths(map, packs)` read the frozen fixture that holds the
  map, else the live kit. A fixture that changed fails its tests, naming the files. `bob-scratch --fixture <name>` builds
  BOB's scratch kit from the frozen sources; `parity <built> --fixture <name>` compares with its frozen working_data.
- The 3K tests: moved to WH3 where the code is WH3's (assets and models, map.hex, the hex region lookup, the map
  audit, the map catalog, Terry projects and configuration, tree TIFs, prop placement, the river models, the
  global_props container, the tile hf maps, the tile validator's palette and layout checks, the tile simulator, the
  tile map editor, tile holes); removed where it is 3K's (lf_height_map, climate_map and global meshes, BMD v35, 3K
  river height patches, 3K's tile database folder, 3K's campaign battles data, the Guandu and 190E data). Dead 3K
  code went with them (`CompiledTerrainExporter`, `TerrainDds.WriteL16`, the validator's CAIME/Attila colour hints,
  which called WH3's own sea colour wrong). `MapCatalog.DefaultMap` was still 3K's, so a remembered map never
  applied; it is now `wh3_main_combi_map_1`.
- Terry XML round trip: Terry's own files (all of Old World's, most prefabs) come back byte for byte. 210 of IEE's 226
  layers were written by the decompiler (tab indent, a `<!-- province -->` comment), and some CA prefabs by other
  tools: they come back with the same elements and attributes in Terry's layout, without the comment. So an edit in
  AtlasWH3 rewrites such a layer as Terry would.
- Found for Phase 5: the tile validator's set categories (`TileMapValidator.Category`) and reference tile map are 3K's,
  so its line, coast and cliff rules see every WH3 set as plain area. The campaign battles reader does not read WH3's
  `battle_locations_map.bin` (none of 357). The 3K global_props region reader (`GlobalProps.ReadRegions`,
  props-to-layers) does not read v27. WH3's tile `bmd_data.bin` is BMD v26. WH3's height TIFs are float32, which the
  terrain painter's 16-bit save does not handle. IEE's map audit finds one model path with a doubled slash
  (`mudbanks//gen_mudbank_01`).

**4.4 status (2026-10-10):** the user lists the regions (decided 2026-10-10: a list the user provides, not chosen
by the step). Build profile "Lookup: regions past 1024" (`LookupLast`), CLI `--lookup-last <region,..|file.txt>`:
region keys, or text files of them. The step finds each region's colour in `regions_tables` (r, g, b, which CAIME
paints the BMP with) from the mod packs and vanilla, and moves those palette entries and black (hexes without a region)
to the end of any lookup with more than 1024 entries, in the `.tga`, `.dds` and `_minimap.tga` alike. The small
lookups (`elector_counts_small`, `wh3_main_hef_court_small`) hold region colours too and are left in BOB's order. With
`lookup_tweak.py`'s list as region keys (`research/lookup/oldworld_lookup_last.txt`, 301 regions) all three Old World
files are **byte-identical** to `!cr_oldworld_campaign.pack`'s. The step names the regions still past entry 1024: on
Old World one, `cr_oldworld_region_skull_guardian` (1,327 entries, 302 moved, so 1,025 are left in front; the shipped
file has the same). Left: the in-game check.

**4.5 status (2026-10-10):** the shipped packs (read through RPFM) are whole mods, uncompressed PFH5: DB, scripts,
battle terrain, CAIME's and the game's files beside the map, and Old World ships its pieces (both folders) in a second,
4.3 GB pack. So a build **merges** into the mod's pack rather than making a map-only one, and packs only what it wrote:
- Compile records each step's files in a build manifest (app cache, per map and output folder); the pack source
  `{compiled}` is those files, so working_data's BOB by-products (the devastated folder's full rasters,
  `global_props_devastation_*`) and CAIME's inputs (`*_lookup.bmp`) never ship.
- A project has a list of packs (`packs`; an older `pack` still loads), each with `exclude`, so Old World's split is two
  merges; `{devastated}` names the devastated project.
- A step declares the files it rewrites whole (rivers `models\river_*`, the pieces step both `pieces\event_*`), the
  manifest keeps that, and a merge always drops the base pack's files under them that the build no longer writes (a
  deleted river's model, an area no longer on the map), even when it writes none. IEE: rivers alone drop the pack's
  206 stale river model files. A profile's extra replace folders drop only where the build put files; compressed base
  entries are copied as they are.
- Default with a linked mod pack (`new-project --pack`, the GUI's linked packs): merge into a copy beside the project,
  then Install (backup, copy-then-swap, refused while `Warhammer3.exe` runs).
IEE end to end (`build --segments validate,compile,pack`, scratch output): 513 s, of which the merge into a copy of the
3.1 GB pack 5 s (2,203 kept, 11,014 replaced, 412 added, 208 dropped). RPFM opens it; against the shipped pack only the
river models differ (all 266 rivers baked, the pack has an older run's 163) and the two pieces' `rivers` files are
gone (4.2). Install was tested on a scratch data folder, not the game's. Left: the in-game check.

**Parity** (the Atlas3K method: disassembly of the WH3 DLLs, Frida on `bob.modder.x64.exe`, field-level diffs; only
where the gap is small or a mismatch is visible)
- [x] 4.7 `trees`: the 457 IEE heights not explained (3.2).
- [x] 4.8 `global_props`: the remaining ulps (effects inside prefabs, rotated sound emitters) (3.9).

**4.7 status (2026-10-10):** the 457 were the **pivot**. A height patch is placed at position − RS·pivot (not
global_props' position + pivot − RS·pivot): IEE's pivoted mountains (def_mountain_volcano_01, pivot y 0.543; the
ogr_large_mountain_01/02 group near (760, 310), pivots up to 3.38) sit pivot.y · scale.y lower in BOB's trees, and where
the patch is thin that drops it under the terrain. Against the reprocessed list: 238,458 of 253,903 heights bit-exact
(238,143 before), 253,880 within 1e-3; the 23 beyond (1e-3 to 2e-2, both signs) are on steep patch slopes, the tail of
the matrix ulps that are still open (BOB's entity matrix vs `QtuTransform`). The patch files BOB reads loose are the
packs' byte for byte, and the pack's logic map is working_data's. The camera height map uses the same patches.

**4.8 status (2026-10-10):** two fixes, one of them not an ulp:
- **The quadtree root is the map_data.esf bounds grown by `quadtree_margin`** (campaign_map_playable_areas; 10 on
  every vanilla and IEE row) on every side: IEE −10..1078.11 × −10..758.57. CA's shipped vanilla combi file and every
  GUI BOB run of the user's (IEE today, Old World 10-03) are padded; the scratch kit's headless BOB runs are not (not
  yet explained: the live kit had no IEE row in raw_data\db, the scratch kit had one, both have it in
  EmpireDesignData), and 3.9 was fitted to those, so the step's cells were not the game's. The step reads the margin
  from the map's mod packs and vanilla db (default 10) and notes the root. Against the reprocessed IEE file: content
  parity 10.6% → 85.3%, the sound file 14.5% → 99.7%, every object kind the same count as BOB (the 255 props that
  reached past the unpadded root are kept).
- **Sound axes are the world matrix's z and y columns divided by their lengths**, not the quaternion's axes: all 50 IEE
  emitters whose scale is off 1 by ulps now match.
Left, as known and invisible in game: prefab children's transforms (8,597 props, 233 decals, 21 VFX: BOB's composed
matrix has exact zeros where ours has ~5e-7, so BOB flattens prefabs by another float path; reading it off
qttoolutility is the next step if it ever matters), 533 hole vertices by an ulp, the last of the 53 unknown bytes on
10 river sounds, one sound line point by an ulp. The content-parity test is now IEE against its fresh frozen pair
(`Step_Iee_MatchesBobContent`). Found on the way: IEE's `tile_list.bin` is now byte-identical to BOB's (the 473 heights
of 3.3 were stale), and BOB's run has all 266 rivers, all identical (3.10).
- [x] ~~4.9 `camera_heightmap`: a BOB run with the `cam_hmap_*` keys in rules.bob as the reference, then BOB's
  tile-terrain term and blur (3.8).~~ **Dropped 2026-10-10:** no BOB parity for this file. With the keys set (0.25,
  8 samples per unit, no blur) BOB's action runs, but on IEE it had filled 69 of 486 rows after 90 minutes on three
  threads (about 10 hours in all), nearly all of it in the height-patch lookups (warscape 0x5b5c30), so the run was
  cancelled. `camera_heightmap` is AtlasWH3's own: judged in game and against the user's hand-made files, not BOB's.

BC6H/BCn byte parity is out of scope unless AMDCompress's behaviour turns out to be cheap to match. The in-game checks
of 3.1–3.10 are done in one pass once 4.1–4.3 are in.

**Dropped:** the hybrid BOB fallback and the per-step override (no BOB-only step is left; `BobRunner` stays for parity
runs), the `trim_mips` fix-up (the pieces are native), the timing report (it was there to choose the next native step),
and the devastated project's `global_props[_sound]_devastation_<type>.bin` (they ship nowhere, §2).

### Phase 5: Editors on WH3 (3–5 weeks)

- **Scene editor:** WH3 entity types and attributes (`ECCampaignProperties.culture_mask`, `visible_in_shroud`,
  `ECVisibilitySettingsCampaign`, `ECPropHeightPatch`), a culture-mask view in place of the season view, and WH3
  prefabs.
- **Tile map painter:** WH3 tile sets (`cliff_gen`, `roads_grey`, `river_mouth`, …) and the Errors tab against the
  WH3 rules from 3.6. The validator's set categories and reference tile map are still 3K's (4.6).
- **Terrain painter:** float32 heights, shroud height, colour overlay, corruption and snow layers, and Terry's
  compositing rules.
- **Campaign battles window:** WH3 `battle_locations_map.bin` / catchments and battle tables.

### Phase 6: AI pathfinding, hlp/spd (3–5 weeks, last)

- hlp v1 (`CAI_HIGH_LEVEL_PATHFINDER` with `REGION_AREA_INDEX`, the extra arrays, `OTHER_CONSTANTS`) and spd v1
  (variable `REFERENCE_POINTS`, the new per-cell encoding).
- The generator is in `Warhammer3.exe`. `campaign_tools/exe_tools` (string/xref index, call counters, hardware
  breakpoints) is the starting kit.
- The game-built files are the oracle, as CA's shipped files were for 3K.
- Until then: a documented custom step for the game-driven generation.

**Inputs, not outputs (for now):**
- `pathfinding.ppd` and `map_data.esf`. Atlas3K never wrote them: its `PathfindingPpd` is a reader, and `hlp_spd`
  builds hlp/spd *from* them. Today CAIME makes them.
- Decision (2026-10-06): keep it that way, as in Atlas3K. Generating them natively may come later as a new phase. It
  would have to be clean-room, because of CAIME's licence.
- `startpos.esf` is out of scope (RPFM's build).

Atlas3K's "custom steps" feature (run any program at a build stage) stays as a general hook, but AtlasWH3 doesn't
depend on any external tool.

## 7. Verification

- **Unit:** format round trips (read → write → same bytes) on every fixture file.
- **Parity:** `Cli parity <built> --fixture <name>` against the frozen BOB output (`fixture-freeze`, 4.6), with
  masked bytes as in Atlas3K; the data tests read the same fixtures through `TestKits`.
- **Round trip:** the decompiler takes AtlasWH3's compiled output back to a Terry project, which should match the
  source within the decompiler's known losses.
- **In game:** a short checklist per fixture map:
  - campaign loads
  - terrain, sea, shroud and overlays look right
  - trees and props in place, culture swaps work
  - rivers drawn
  - an event area devastates and restores
  - AI moves
  - no new script errors

## 8. Risks

| Risk | Mitigation |
|---|---|
| WH3 patches change formats or DB versions | Readers check versions and fail loudly; DB layouts come from RPFM's updatable schema file; the fixtures carry the game build they were made on |
| BC6H quality or BCn differences show in game | Quality first (compare with CA's encode); keep BOB fallback for textures until then |
| BOB's pack-reading actions resist automation | Phase 2 starts by documenting them; worst case, write the temporary pack with `PackWriter` and keep the user out of it |
| Atlas3K 3K tile rules don't hold for WH3 | 3.6 is measured against the fixtures before the editor's Errors tab is trusted |
| Old World size (2048 hexes, 16k textures) blows memory or time | Profile on Old World from Phase 3.1 on; stream rasters per tile row where needed |
| The survey's unverified claims | Re-run its key checks on this machine's fixtures in Phase 1 |

## 9. Open questions for the user

Answered 2026-10-06:
- The reprocess sequence and its pack dependencies → §2.
- Fixtures: only Old World and IEE are current → §3.
- Devastated maps are a BOB workaround → §2.

- Props, sound and rivers are one BOB action, with no dependencies → §2.
- `pathfinding.ppd` / `map_data.esf` stay inputs, as in Atlas3K → §6.
- What the devastate build ships → §2 (checked in the packs).

- The extra pieces in the Old World devastate pack: BOB exports every DB event area; they're harmless → §2.
- `camera_heightmap.png`: BOB crashes, so it's hand-made → §2, step 3.8.
- Lookup textures: CAIME exports the `*_lookup.bmp`, and BOB's *Convert lookup texture* turns it into the `.tga` /
  `.dds` / `_minimap.tga` → step 3.7 (the BMP stays an input).

No open questions right now.
