# Building a new campaign map: the Battle of Guandu (3k_guandu_map)

This is the complete process for building a **new** Three Kingdoms campaign map and startpos: a cropped, 2.25× upscaled north China map (198 CE, Cao Cao only). It covers everything from the vanilla `map.hex` to a startpos that RPFM builds without crashing. It was worked out on 2026-09-28. Everything here was hit for real, and each gotcha has the failure it caused.

Companion documents:
- `docs\bob_campaign_build.md`: BOB terrain build order and vanilla parity. Read it first; the terrain steps here follow it.
- `research\guandu\caime_3k_tilemap_issue.md`: how 3K uses tile-map colours (bug report for CAIME).

**Paths:**
- **Scripts:** `Z:\Claude\TerryClone\research\guandu\` unless noted.
- **Kit:** `…\Total War THREE KINGDOMS\assembly_kit\` (the Steam AK).
- **Pack:** `…\Total War THREE KINGDOMS\data\GuanDu.pack`.

**Names:**
- **Map:** `3k_guandu_map`.
- **Campaign:** `3k_guandu_start_pos`.


> **v2 update (2026-09-29):** the map is now **1,328 × 784** hexes. The west edge is column 120 (Jincheng and Wuwei); Baxi, Bajun, Chengdu and Jiangyang are dropped.
> - **Width rule:** the map width **must be a multiple of 4 hexes**. BOB lays the global meshes on 16 columns of tile-map width ÷ 16. At 1,094 hexes (2,188 px) it built a 17th column, which produced see-through notches beside every road tile in game.
> - **Heights and climate** come from Atlas3K `compile-map`; the other BOB steps run headless through `tools/bob_mcp`.
> - **GuanDu.pack type:** **Movie** for BOB generation, **Mod** for the RPFM startpos and for play.
> - **EmpireDesignData:** BOB reads its DB from `raw_data/EmpireDesignData`, so run `mirror_empiredesign.py`. This is chained in `build_startpos_all.py`.

---

## 0. Status (2026-09-28 23:03)

| Part | State |
|---|---|
| map.hex (1094×785, 108 land + 47 sea regions) | Built. CAIME validation is clean apart from one vanilla-type warning |
| Terrain project + Terry | Loads and displays correctly in real Terry (TWeak) |
| Tile map | Rebuilt from the user's vanilla tile map. 0 BOB "Failed to find tile" holes |
| map_data / pathfinding / trade routes / borders / lookup | Built and in the pack |
| Startpos (RPFM) | **Builds**: `startpos_historical.esf`, `startpos_romance.esf`, `hlp_data.esf`, `spd_data.esf` in the pack |
| In-game test | **Not done yet** |
| Known leftovers | Area-of-interest label positions use dlc07 coordinates. River junction warning at tile-map px 1360,376. Shader resolution corrector for 1094×785. Terrain relief sharpening |

---

## 1. The pipeline at a glance

```
vanilla AK map.hex (892×702)
  └─ rebuild_hex.py ─────────────► hex\map_x2.25\map.hex (1094×785)          [§3]
        └─ install to raw_data\EmpireDesignData\campaign_maps\3k_guandu_map\map.hex
              ├─ CAIME validate / process (map_data, pathfinding, trade routes, lookup .bmp)  [§5]
              ├─ CAIME GUI: borders                                                             [§5]
              ├─ BOB: Convert lookup texture (.bmp → .tga/.dds/minimap)                         [§5]
              ├─ build_startpos_all.py (AK DB + all pack TSVs + frontend + loc)                [§6]
              └─ build_map_extras.py (camera heightmap, per-map images, renamed lookups)       [§5]
vanilla AK terrain + rasters
  └─ crop_scale_terrain.py → build_ak_project.py → BOB (bob_campaign_build.md)                 [§4]
build_tilemap.py (user's vanilla tile map → Guandu tile map)                                   [§4]
all of the above → GuanDu.pack (rpfm_cli)                                                       [§7]
RPFM: build_starpos (process_hlp_spd = true) → build_starpos_post → save                        [§8]
```

Rebuild order when **map.hex changes**:
1. `python rebuild_hex.py`
2. Install map.hex.
3. `python build_startpos_all.py`
4. CAIME: validate, then map-data, pathfinding, trade-routes and lookup.
5. CAIME GUI: export borders.
6. BOB: convert the lookup texture.
7. `python build_map_extras.py`
8. Push everything to the pack.
9. RPFM: startpos.

Skipping any of these left something stale every time we tried.

---

## 2. Map scope and hex facts

- **Crop:** vanilla hex cols 224–709 and rows 323–671 (`crop_scale_map_hex.py`: `COL0, COL1, ROW0, ROW1`). That covers everything north of the Yangtze, with Hanzhong on the west edge. The crop column start must be **even**, because columns are offset.
- **Scale:** 2.25×, giving 1094×785 hexes. CAIME needs an **even width**. The size stays within the user's tested large map (8768×7252 px), so size isn't the limit.
- **Hex grid:** flat-top hexes, column-offset. **Row 0 is south**, and odd columns sit half a hex north. Hex centres in world units: `x = col·0.668`, `z = row·0.772 + (col&1)·0.386` (`hexgrid.py`, verified against CAIME).
- **map.hex record:** 16 bytes. The layout is in `rebuild_hex.py` `unpack`/`pack`, and the round trip is verified on vanilla. Byte 9 is "reserved" but is non-zero on 855 vanilla hexes, so it's carried through.
- **World extents:**
  - DB extents follow the hex rule: `(hex−1)·0.668 = 730.124` and `(hex−1)·0.772 = 605.248`.
  - The rasters are 8 px/hex plus 4 rows: 8752×6284 full, 4376×3142 for sea, and 2188×1571 at quarter resolution. The raster world is 729.865×605.798.

---

## 3. Building map.hex (`rebuild_hex.py`)

Block-scaling the records (repeating each hex 2.25×) broke every line layer and hung CAIME's trade-route processor, so the map is **rebuilt**. The steps, in order:

1. **Crop and clean at 1×** (`crop_scale_map_hex.py`):
   - `fix_orphans`: any region whose town-slot footprint the crop cuts into is handed to its nearest neighbour and made impassable.
   - `frame_edges`: a 2-hex impassable frame. CA's map-data builder segfaults if passable land, roads or rivers reach the edge.
   - `drop_broken_bridges`: a bridge with one side gone breaks pathfinding.
2. **Area layers** (terrain, region, impassable, ground, attrition, climate, area of interest, restriction): each new hex takes the old hex nearest its world position.
3. **Settlements:** each town footprint is pasted at its **original size** (the main slot must stay 16/19 hexes), nudged up to 6 hexes to fit the scaled coast.
4. **Lines:**
   - Roads, rivers and trade routes are redrawn as 1-hex hex lines.
   - Bridges are redrawn between the two shore sides.
   - Trade-route masks are rebuilt from adjacency.
5. **Beach/cliff:** only on land that touches the sea. Roads on the coast lose beach/cliff unless they lead to a bridge.
6. **Region clean-up:**
   - Stray fragments are merged into their neighbour.
   - `reg_non_playable` is forced impassable.
   - Rough ground is cleared within 2 hexes of towns.
   - Sprawl is extended to touch a nearby river.
7. **6d, south of the Yangtze:**
   - **River line:** the mean row of the `riv_sea_yangtze` hexes in each column, interpolated across gaps.
   - **Whole regions dropped:** land regions more than 50% south of the line, and sea regions more than 90% south.
   - **Straddling regions** (East Sea, lakes) lose only their southern hexes.
   - **Dropped land** becomes impassable `3k_main_reg_non_playable`, dropped water becomes `3k_main_sea_non_playable`, and roads, rivers, trade routes, slots and bridges there are cleared.
   - **Town footprints of kept regions are protected.** Without this, Jiangling lost 1 of its 16 main-slot hexes and startpos crashed (§9, crash 3).
   - **Clipped regions:** `CLIPPED = ["3k_main_wudu_resource_1"]`, a region whose province capital lies outside the crop, is dropped the same way (§9, crash 2).
   - **Scraps:** small non-playable pieces inside playable land are absorbed by the neighbouring land region, still impassable.
8. **Region edges:** each hex marks which neighbours are in a different region.
9. **Empty regions dropped from the header lists**, and every hex's region index is remapped, the same as CAIME's *Remove region*. CA's builder lists every header region in map_data. A listed region with no startpos record crashes startpos.

**Checks to run after every rebuild:**
- **Settlement footprints:** every region's town-slot hex count, per slot, must equal vanilla's. One short means no primary slot, which crashes startpos.
- **Province capitals:** every province on the map must still have its capital region. Compare with `region_to_province_junctions.is_capital`. Provinces missing only resource regions are fine; vanilla's own map lacks `3k_main_penchang_resource_2`.
- **CAIME validate:** see §5.

---

## 4. Terrain, tile map and Terry

- `crop_scale_terrain.py`: crops and scales the vanilla AK rasters with the same hex transform: heights and sea cubic, blend nearest, trees and climate. Heights keep vanilla's vertical values.
- `build_ak_project.py`: builds the AK terrain project from the **Steam AK `3k_dlc07_main_map` project**, the one real Terry loads. Props are moved to the new positions at life size, anything outside the crop is dropped, rivers are rescaled and clipped, and BOB's river meshes are dropped. It installs to `raw_data\terrain\campaigns\3k_guandu_map`.
- `build_tilemap.py` + `tilemap_hex.py`:
  - **Layout:** the tile map is the hex grid at 2×2 px per hex (`x = 2c+{0,1}`, `y = H−1−(2r+(c&1)+{0,1})`).
  - **Source:** it's rebuilt from the **user's vanilla tile map**, with lines redrawn and thinned. CAIME's baseline gave 3,235 "Failed to find tile" holes.
  - **Coast colours:** beach `ffff00`, cliff `f9ad69`, cliff end `9f222a`, river mouth `ccccff`.
  - **File format:** CAIME's baseline tile map is a TGA with a `.png` name.
- **BOB:** GUI only, config `binaries\BOB\campaign_3k_guandu_terrain_configuration.xml`. Run **one action at a time**; running several at once crashed it and deleted outputs. The build order is in `bob_campaign_build.md`.

**Real Terry** (`Tweak.retail.x64.exe /standalone TerrainMetadataEditor`; open a project by dragging the `.terry` onto it):
- It **crashes** if `working_data\terrain\campaigns\<map>\tile_list.bin` is missing, so run BOB's tile step first.
- It reads display heights **only** from raw `lf_heights.tif` / `lf_sea_heights.tif`. They must use Atlas3K's TIFF layout: RowsPerStrip=1 (2 for LZW palette images), SampleFormat=1, SamplesPerPixel=1. Write them with `tiff16.py`. PIL's single-strip TIFFs showed flat terrain with spikes.
- Its **world scale comes from the game-pack table `campaign_map_playable_areas.maxx`**, not from the AK XML. With no row it uses 1 unit per tile-map pixel, so the terrain came out 3× too big for the layers. Put the row in a pack that's **loaded** before judging a new map in Terry. GuanDu.pack is now **Mod** type (§7); if Terry loses the scale again, set it back to Movie while working in Terry.
- It locks every data pack while open, so close it before writing packs.

---

## 5. CAIME, BOB lookup and per-map files

**CAIME CLI** (`Z:\CAIME\CAIME\CAIME.exe`; log in `tool.log`, rewritten per run):
```
CAIME.exe validate --map <map.hex> --all
CAIME.exe process  --map <map.hex> --map-data | --pathfinding | --trade-routes | --lookup
```
- **map-data** *always* reports "Failed to export processed map data file". It still writes a complete `map_data.esf`: check that the header's string-table offset lies inside the file.
- **borders:** a CLI bug (the GUI options are null), so export borders from the **GUI** (Process → Borders data → Export, default selection).
- **lookup:** the CLI writes only `3k_guandu_start_pos_lookup.bmp`.
- **Map-data prerequisites:** it needs AK DB rows `campaign_maps`, `campaign_map_regions` (one per header region), `campaign_map_playable_areas` and `campaigns` (`ak_db_add_guandu.py`).
- **Expected validation now:** the only message is "3k_main_reg_non_playable is split into 8 disconnected areas". Vanilla's is split into 9. The Yangtze runs to the south edge in two places, so this can't be avoided.

**Lookup textures via BOB:** the .bmp must go through BOB. In the Working Data tree, open `campaign_maps\3k_guandu_map`, click the lookup file, and tick only the provider action **"Texture / Convert lookup texture"**; leave the consumer actions (process start pos, create pack) unticked. That writes `…_lookup.tga`, `….dds` (R16, DXGI 56) and `…_lookup_minimap.tga`.

**`build_map_extras.py`** makes the rest of what vanilla ships in `campaign_maps\<map>\` (compare vanilla's set in `data.pack`). Every vanilla image is proportional to the hex grid: 4.3 px/hex for lookup-sized images, 1.16 for the minimap, 1.08 for the lookup minimap, and 2.0 for the camera heightmap. So each is cropped to the Guandu window and resized:
- **`camera_heightmap.png`:** 16-bit, cropped from vanilla's. **BOB's "Generate Camera Height Map" crashes on this map**, even run alone.
- **Visual images:** `3k_overlay_map.dds` (written as uncompressed BGRA; vanilla's is BC7), `3k_main_minimap.png`, `three_kingdoms_china_map.png`, `campaign_map_multiplayer.png`, `campaign_map_records.png` and `custom_battle_map.png`.
- **Region lookups:** `3k_main_lookup.tga` / `.dds` and `3k_main_lookup_minimap.tga` are copies of the BOB output, **renamed to the names the `campaign_map_playable_areas` row references**.
- **Display textures:** vanilla's `display\area_of_interest_spline.dds` and `display\borders\textures\*`, plus our own `display\trees\trees.campaign_tree_list` (from the compiled root).

These files were **not** the startpos crash cause (it crashed the same without them), but a playable map needs them.

---

## 6. Database and startpos tables (`build_startpos_all.py`)

**Always run the whole chain** with `python build_startpos_all.py`. `ak_db_add_startpos.py` gives records new IDs on every run, and every other table and the loc keys depend on them. Pushing one regenerated table on its own put a raw, un-trimmed faction table into the pack once (Yellow Turban Anding playable) and crashed the build. The chain:

| Script | Writes |
|---|---|
| `ak_db_add_guandu.py` | AK XMLs: `campaign_maps` (731×606), `campaign_map_regions` (every header region), `campaign_map_playable_areas` (**index 1958466083**), `campaigns`. Insert-only, CRLF kept, idempotent |
| `ak_db_add_startpos.py` | AK XMLs: `3k_guandu_start_pos` cloned from `3k_dlc07_start_pos`. Cao Cao only, 29 of his regions; the rest unowned. Calendar 198; script `three_kingdoms_early`. Cao Cao moved from dlc07 hex (528,453) to Guandu hex (684,292): `startx`/`starty` are **hex col/row** |
| `build_startpos_tables.py` | Pack TSVs in `startpos_tables\db\`, with the columns, versions and formats of the user's dlc08 pack (bools `true/false`, floats `%.4f`). Fills any missing population pooled resource (every region needs one; 120 for capitals, 12 otherwise). Empties `start_pos_character_retinue_unit_modifiers`, as in dlc08 |
| `build_startpos_extra.py` | Everything else dlc08 ships: Cao Cao + `cao_separatists` (dlc08 values), regional factions + `province_to_emergent_faction_junctions`, diplomacy (Cao Cao → each regional faction, hidden `access_to_han_empire`), world power token (emperor, no holder), 12 empty tables, `db\victory_objectives.txt`. **No rebel or Yellow Turban factions**: dlc08 has none, and landless factions with no characters aren't needed |
| `build_frontend.py` | `frontend_faction_to_frontend_faction_leaders`, `faction_to_faction_groups_junctions`, camera bounds, road levels; `text\guandu.loc` (campaign name and settlement names, keyed by settlement **IDs**); `ui\frontend ui\new_campaign.twui.xml` (dlc08 button → Guandu, fresh GUIDs, CRLF) |
| (built in) | `startpos_tables\db\campaign_map_regions_tables\guandu.tsv` from the AK rows |

**Pack DB rules we learned:**
- **The game reads DB only from packs.** Every AK-only table the game needs must ship in the pack too, e.g. `campaign_map_regions`. The AK XMLs only feed CAIME/BOB.
- **Playable-area index:** the pack's `campaign_map_playable_areas.index` **must equal the AK index**, because `map_data.esf` stores it as text. Keep `maxx` at 729.865 for Terry.
- **Template to copy:** the user's `new_startpos_dlc08.pack` (extracted in `research\guandu\example_newcampaign\`). RPFM's startpos build needs every table it has, including the empty ones.
- **Test-only tables, removed afterwards:**
  - Vanilla `region_to_province_junctions` overrides would change vanilla provinces while the mod is loaded.
  - `campaign_map_areas_of_interest` rows pointing dlc07's area keys at our map break dlc07's own areas.
  - Only an `audio_campaign_maps` row for `3k_guandu_map` stays, a new key that affects nothing else.

---

## 7. The pack (GuanDu.pack)

Build and update it with `rpfm_cli` from **PowerShell**; Git Bash mangles the paths.

```powershell
$schema = "$env:APPDATA\FrodoWazEre\rpfm\config\schemas\schema_3k.ron"
Z:\RPFM\rpfm_cli.exe --game three_kingdoms pack add -p $pack -t $schema -f "<file.tsv>;db/<table>_tables/guandu"
Z:\RPFM\rpfm_cli.exe --game three_kingdoms pack add -p $pack -f "<file>;campaign_maps/3k_guandu_map/<name>"
```
- **One `-f` per call**, in a loop (`$args` is reserved in PowerShell and broke a multi-file call). The TSV name becomes the table file name.
- **Check for locks first** with `whoLocks.ps1 <pack>`. Terry, RPFM (UI and server) and the game all lock the pack.
- **Back up before every change:** `output\backups\GuanDu_pack_*.pack`.
- **Verify by extracting** the files back out and comparing hashes.
- **Pack type:**
  - **Movie** packs in `/data` load in **every** game run, whatever the mod list says, so they interfere with other campaigns' startpos builds. It broke the dlc08 baseline until GuanDu was switched to Mod.
  - The type is the low nibble of the u32 at byte 4 (3 = mod, 4 = movie).
  - GuanDu.pack is **Mod** now (step 8 of the user's process).
- **Loose files:** after a startpos build, RPFM leaves loose copies in `data\campaigns\3k_guandu_start_pos\` and `data\campaign_maps\3k_guandu_map\` (its cleanup didn't remove them). Loose files **override packs**, so move them out once they're in the pack.

Contents now: 37 DB tables, `text\guandu.loc`, the frontend twui, `campaign_maps\3k_guandu_map\*` (map data, pathfinding, trade routes, borders, lookups, HLP/SPD, images, display), `terrain\campaigns\3k_guandu_map\*` (653 files) and `campaigns\3k_guandu_start_pos\startpos_*.esf`.

---

## 8. Building the startpos with RPFM

The RPFM MCP server is registered for this project (`.mcp.json` → `http://127.0.0.1:45127/mcp`; RPFM's UI starts `Z:\RPFM\rpfm_server.exe`, or run it yourself). `rpfm_mcp.py` is a small client that keeps a session in `output\mcp_session.txt`.

```
python rpfm_mcp.py set_game_selected '{"game_name":"three_kingdoms","rebuild_dependencies":true}'   # sessions reset to warhammer_3!
python rpfm_mcp.py open_packfiles '{"paths":["C:/…/data/GuanDu.pack"]}'                            # forward slashes
python rpfm_mcp.py build_starpos '{"pack_key":"C:/…/data/GuanDu.pack","campaign_id":"3k_guandu_start_pos","process_hlp_spd":true}'
python rpfm_mcp.py build_starpos_post '{…same…}'
python rpfm_mcp.py save_packfile '{"pack_key":"C:/…/data/GuanDu.pack"}'
python rpfm_mcp.py close_pack '{"pack_key":"C:/…/data/GuanDu.pack"}'                               # before rpfm_cli writes
```
- **`process_hlp_spd` must be `true` for a new map.** Without `hlp_data.esf` / `spd_data.esf`, creating the campaign AI (CAI_INTERFACE) throws `std::bad_alloc`.
- **Sessions:** a session expires after 5 minutes idle and **resets the game to Warhammer 3**. Then `build_starpos` fails with "The Pack needs to be in /data", so reselect the game.
- **Crash reports:** `build_starpos` returns "Success" even when the game crashes. Check `%APPDATA%\The Creative Assembly\ThreeKingdoms\crash_report\` for a new `.mdmp`, or use §10.

---

## 9. The startpos crashes and their causes (in the order hit)

| # | Where (game debug log / stack) | Cause | Fix |
|---|---|---|---|
| — | first RPFM build fails | tables missing compared with dlc08 | `build_startpos_extra.py` |
| — | null record | `campaign_map_regions` only in the AK XMLs, not in the pack | ship it in the pack |
| 1 | `lua_gethookcount+0x2670e0`, `record_exists` on `campaign_map_playable_areas_tables` | pack row index 1904283715 ≠ AK/map_data index 1958466083 | pack row uses the AK index |
| 2 | "About to create WORLD": null `A->[0x40]` (the province capital) | province `3k_main_wudu`: only `wudu_resource_1` on the map; its capital was outside the crop | drop clipped regions (`CLIPPED` in `rebuild_hex.py`) |
| 3 | WORLD: region with no primary slot (`[rax+0x318]`) | `3k_main_jingzhou_capital` main slot 15/16 hexes: the south-drop line cut one hex | protect town footprints in 6d; check footprints = vanilla |
| 4 | after "Initialised trade": `std::bad_alloc` in CAI_INTERFACE | no `hlp_data.esf`/`spd_data.esf` | `process_hlp_spd = true` |

Things that were suspected but were **not** the cause: empty regions in the map.hex header, a missing population pooled resource, retinue modifiers, the rebel factions, the per-map images and the AOI/audio tables. Each was still worth fixing or mirroring from dlc08.

---

## 10. How to debug a startpos crash (the method that worked)

The game's own `crash_report\*.mdmp` has **no heap memory**, and the exe has no symbols and no RTTI. Visual Studio only JIT-attaches after the crash, and Process Monitor can't show DB reads because packs are memory-mapped. What worked:

1. **Attach cdb as the game starts.** `catch_crash.ps1` waits for `Three_Kingdoms.exe` and runs cdb with the command file `output\catch_cmds.txt` via `-cf`, because PowerShell mangles quoted `-c` arguments. The cdb log then contains the game's **OutputDebugString** output:
   - `Loading database: <table>` for each table.
   - The **`**CAMPAIGN MODEL CREATION**`** step list: CAMPAIGN_MAP_DATA → EPISODIC_RESTRICTIONS → WORLD → CAMPAIGN_PATHFINDER → TRADE_MANAGER → CAI_INTERFACE → diplomacy → event generator.
   The last step printed tells you where it died.
2. **Log every access violation:** `sxe -c "r rip; kc 6; gc" -c2 ".dump /ma /o <path>; .kill; q" av`. **Use absolute dump paths with doubled backslashes**; otherwise the path loses its slashes and lands in the current directory.
3. **Full dump at a given instruction:** `sxe -c ".if (@rip == <addr>) {.dump /ma …; .kill; q} .else {gc}" av`. The exe loads at the same base (`7ff6a55a0000`) run after run.
4. **Breakpoints must be hardware:** `ba e1 <addr> "<commands>"`. A software `bp` patches code and trips an integrity check early in DB loading.
5. **Compare with a working baseline** (the user's idea): build the dlc08 pack's startpos with the same breakpoint and dump the same object. The difference was obvious: dlc08's record was a province with its capital `3k_main_bajun_capital`, while ours had an empty capital. A Movie-type pack in `/data` pollutes the baseline, so switch it to Mod first.
6. **Name what you see:**
   - Table getters pass a literal to `"Loading database: %s."`, which gives the table name.
   - For C++ exceptions, decode the ThrowInfo (`.exr -1` parameter 2) with the helpers in `rtti_name.py`, e.g. `.?AVbad_alloc@stdext@@`.
   - Match objects by geometry: a region object's float bounds matched `jingzhou_capital`'s hex bounding box.

---

## 11. Notes for Atlas3K

What this build taught us that Atlas3K should know or could automate:

- **Formats confirmed here:**
  - map.hex's 16-byte record, header lists and colour tables, with a CRC32 footer (`rebuild_hex.py`, `hexfields.py`, `hexgrid.py`).
  - The tile-map ↔ hex mapping (`tilemap_hex.py`).
  - The lookup `.dds` is DXGI 56 (R16_UNORM) with one mip level.
  - `map_data.esf` stores the playable-area index as text.
  - Pack header type nibble: 3 = mod, 4 = movie.
- **TIFF layout:** Terry-compatible 16-bit TIFFs follow Atlas3K's `TiffMap.WriteGray16` layout. `tiff16.py` is the Python port, so use it for any raster Terry must display.
- **World scale:** it comes from the loaded game-pack `campaign_map_playable_areas.maxx`. Atlas3K's `TerrainData.CoordsFor` reads the world size from the tree-list header instead. That's safer, but both have to agree. `ProjectPaths.FromArgs` (`--map/--root/--ak`) lets Atlas3K open non-vanilla maps. Run it with `Atlas3K.App.exe --map 3k_guandu_map --root research\guandu\compiled\Map`.
- **Compiled root for Atlas3K** (`build_compiled.py`): its `.dds` files use **vanilla's** value ranges (sea ÷44217). Atlas3K compares raw DDS land against sea, and BOB-style per-map normalisation flooded the plain.
- **Candidates for a BOB/CAIME-free exporter**, following on from the compile-map work in `bob_campaign_build.md`:
  - camera heightmap (BOB crashes on it);
  - lookup `.bmp` → `.tga` / `.dds` / minimap (currently needs BOB's GUI);
  - borders export (currently needs CAIME's GUI);
  - the per-map image set (`build_map_extras.py` already does it by cropping).
- **Validation Atlas3K could run before any export:**
  - every region's slot footprint matches its source;
  - every province on the map keeps its capital;
  - no empty header regions;
  - `campaign_map_regions` = header regions;
  - pack playable-area index = AK index;
  - no passable land at the map edge;
  - no one-sided bridges.
  All of these were real crashes.
- **GUI automation:** `uiclick.ps1`, `winshot.ps1` (PrintWindow capture; it doesn't show popup menus, so use a full-screen capture for those), `pidclick.ps1`, `dragdrop.ps1`.
  - SendKeys treats `( ) + ^ % ~ { }` as special: "(x86)" in a path loses its parentheses. Click file names in dialogs instead.
  - Don't drive the GUI while the user is at the PC.

---

## 12. File index (`research\guandu\`)

| File | Purpose |
|---|---|
| `georef.py`, `crop_preview.py` | lon/lat → world fit (~19 units/degree), crop previews |
| `crop_scale_map_hex.py` | crop constants, `fix_orphans`, `frame_edges`, `drop_broken_bridges`, header rename |
| `hexgrid.py`, `hexfields.py` | hex grid and record helpers (CAIME conventions) |
| `rebuild_hex.py` | map.hex builder (§3); `CLIPPED` list |
| `crop_scale_terrain.py`, `tiff16.py` | raster crop/scale; Terry-compatible TIFFs |
| `build_ak_project.py`, `build_compiled.py` | AK terrain project; Atlas3K compiled root |
| `build_tilemap.py`, `tilemap_hex.py` | tile map from the user's vanilla tile map |
| `ak_db_add_guandu.py`, `ak_db_add_startpos.py` | AK DB XML records |
| `build_startpos_tables.py`, `build_startpos_extra.py`, `build_frontend.py` | pack TSVs, frontend, loc |
| `build_startpos_all.py` | **run this**: the whole table chain in order |
| `build_map_extras.py` | per-map images, camera heightmap, renamed lookups |
| `rpfm_mcp.py` | RPFM MCP client |
| `catch_crash.ps1`, `bp_watch.ps1`, `dlc08_watch.ps1`, `rtti_name.py` | crash debugging (§10) |
| `whoLocks.ps1` | who holds a file open |
| `example_newcampaign\` | the user's dlc08 pack, extracted (the template) |
| `caime_3k_tilemap_issue.md` | tile-map colour bug report |

Backups of everything overwritten (map.hex, AK DB XMLs, pack versions, loose startpos output, dlc08 test files) are in `Z:\Claude\TerryClone\output\backups\`.
