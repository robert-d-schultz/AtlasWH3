# Building the 3k_dlc07_main_map campaign terrain with BOB

This is the complete, tested process for building the campaign terrain with the Three Kingdoms Assembly Kit, from AK sources to a pack. It was worked out on 2026-09-27 against CA's vanilla files, and every output is either byte-identical to vanilla or its difference is explained below.

Paths are relative to the kit (`...\Total War THREE KINGDOMS\assembly_kit\`) unless they start with `Z:\Claude\TerryClone\`. The map is `3k_dlc07_main_map`; substitute your own map name for other maps.

---

## 1. Prerequisites (one-time per kit)

A fresh or reinstalled kit is missing several files that BOB needs but never mentions. Each one below is a real failure we hit.

| # | File(s) | Why BOB needs it | Symptom when missing |
|---|---|---|---|
| 1 | `raw_data\terrain\campaigns\rules.bob` (contents below) | Turns on the campaign terrain actions | BOB only offers global_props, environment and height patches. No lf maps, climate, tile list or global mesh |
| 2 | `raw_data\EmpireDesignData\*.xml`, the full DB export | global_props looks up `campaign_maps`, `campaign_map_playable_areas`, … here | global_props crashes (abort, `0xc0000409`) right after reading the layers |
| 3 | `working_data\campaign_maps\<map>\`: `map_data.esf`, `hlp_data.esf`, `spd_data.esf`, `3k_main_lookup.*`, `pathfinding.ppd`, `trade_routes.ptd`, `camera_heightmap.png`, `display\` | global_props reads `map_data.esf` as a **loose file**; it doesn't look inside the packs | global_props crashes (abort) with `map_data.esf` not found |
| 4 | A quarter-resolution `climate_map.png` in the map folder | Source for `climate_map.cm` | `climate_map.cm` comes out at the wrong size (see §2) |

**rules.bob** (`raw_data\terrain\campaigns\rules.bob`):
```
[Terrain]
	PrefabRoot = art\prefabs\campaign
	save_meta_data_map = false
	save_final_tile_map = true
	generate_global_mesh = true
```

**DB export:** the kit ships the tables in `raw_data\db\*.xml`. Copy every one into `raw_data\EmpireDesignData\` without overwriting anything already there. That's 3,919 tables; a working kit has the same set in both folders. Use your mod's own tables instead if the mod changes them (start positions, regions, …).

**Campaign map files:** for a vanilla build, use CA's files from the game packs (extracted in `Z:\Claude\TerryClone\Vanilla\Map\campaign_maps\3k_dlc07_main_map\`). For a mod, use your own CAIME/HLP/SPD output.

---

## 2. Source data rules (`raw_data\terrain\campaigns\<map>\`)

Rules for a 1:1 rebuild of the vanilla map. For your own map, the same *format* rules apply to your edited data.

| Source | Rule | Why |
|---|---|---|
| `…height.<id>.tif`, `lf_heights.tif` (7136×5620 u16) | Raw u16 heights. For vanilla, exactly the values in `lf_height_map.dds` | BOB stores land with range 0–1, i.e. unchanged |
| `…sea_height.<id>.tif`, `lf_sea_heights.tif` (3568×2810 u16) | The **source** sea values: the vanilla DDS × 44217/65535, adjusted per pixel so BOB's rounding lands back on vanilla | BOB rescales the source to its own min–max range. Vanilla's range is 0–44217, so the DDS values are 1.48× the source. Feeding in DDS values makes Terry show the sea flooding river valleys and coasts |
| `climate_map.png` (map folder) | Quarter resolution (1784×1405), equal to the full-res `climate_map_g.png` sampled every 4th pixel (`[::4, ::4]`) | Vanilla `climate_map.cm` is exactly that sampling; any other downscale is ~0.5% off |
| `climate_map_g.png` | Full resolution (7136×5620) | Kept as the master |
| Rivers layer (`1972bd217a4938e`) | 24 ECRiverSpline, rebuilt from the vanilla river meshes with `research\rivers_from_meshes.py`: width 3, `terrain_relative="false"`, entity y = 0, absolute water height per point | All 55 height patches come out byte-identical to vanilla |
| Props layers | Converted from vanilla `global_props.bin` with `research\3k_global_props_to_terry_layers.py`, including the building/settlement **tag layers** | Without the tags, the building keys are lost |
| `tile_map.png` | Your CAIME tile map. Yours has holes fixed on purpose, so `tile_list.bin` won't match vanilla | — |

The pixel formula for the sea source, and the tools that build these files, are in `research\compressed_map.py` (docstring) and the memory notes. Everything the kit had before these changes is backed up in `Z:\Claude\TerryClone\output\backups\`.

---

## 3. How BOB's compressed maps work (why the order matters)

- Every `*.compressed_map` is a FASTBIN0 file split into 16×16 tiles. The header records the **source** value range.
- BOB normalizes the source into that range: `v = trunc(float32(src − lo) / float32(hi − lo) × 65535)`.
- **The `.dds` steps don't read your source.** They decode the `.compressed_map` that's in the **game packs**. If you haven't imported your new compressed map yet, the `.dds` is built from vanilla's.
- **`global_map` does the same with `tile_list.bin`.** If your new `tile_list.bin` isn't in the pack yet, `global_map` (`global_blend.dds` and the tile list inside it) is built from vanilla's, and any tiles you changed, **including river tiles**, are missing in game. This is why the river near Danyang/Jianye was missing.

**Rule: import each output into the pack before running any step that depends on it.**

---

## 4. Build order

Before each step, capture the output (see §5). Use BOB's GUI (`binaries\bob.retail.x64.exe`) with the campaign configuration `binaries\BOB\campaign_3k_dlc07_terrain_configuration.xml` (Terrain processor only), or Terry's *Process with BOB*. **Don't run every action at once.** BOB crashes on the camera heightmap if you do.

| Step | BOB action / manual step | Needs in the pack first | Produces (`working_data\terrain\campaigns\<map>\`) | Expected vs vanilla (vanilla sources) |
|---|---|---|---|---|
| 0 | Start the pack with the CAIME files + HLP/SLP data | — | — | — |
| 1 | Height map **compressed** | — | `lf_height_map.compressed_map`, `lf_sea_height_map.compressed_map` | land byte-identical; sea off by 1 on 1,047 px (0.01%), which is the u16 limit |
| 2 | **Import** both compressed maps (RPFM, as "movie" files) | — | — | — |
| 3 | Height map **DDS** | step 2 | `lf_height_map.dds`, `lf_sea_height_map.dds` | same as step 1 |
| 4 | **tile_list + climate map** (+ global_props if needed) | — | `tile_list.bin`, `climate_map.cm` | climate byte-identical; tile_list different on purpose (your tile map) |
| 5 | **Import** `tile_list.bin` and `climate_map.cm` | — | — | — |
| 6 | **global_map + global_meshes** | **step 5** (your tile_list) | `global_map\global_blend.dds`, `texture_arrays.xml`, `tile_list.bin`; `global_meshes\land_mesh_N.*`, `sea_mesh_N.*` | `global_map\tile_list.bin` = your root `tile_list.bin` with one flag byte cleared (`07`→`00`) on some entries: same size, same path list. If it equals **vanilla's** `global_map\tile_list.bin`, step 5 didn't take. `global_blend.dds` stays vanilla's (it comes from the blend TIF) |
| 7 | **Import only `global_map`** | — | — | — |
| 8 | **Regenerate global_meshes**, then import them | step 7 | `global_meshes\*` (+ `.agf` sidecar files, not needed) | land meshes: geometry identical, 4–7 junk header bytes; sea meshes: heights ≤0.00015 off |
| 9 | **global_props** | prerequisites 2 + 3 | `global_props.bin`, `models\river_N.*`, `height_patches\*` | patches byte-identical; river shapes match; props match except 2 merged duplicate wells |
| 10 | **Renumber rivers**: `python research\renumber_rivers.py "<working terrain dir>" Vanilla\Map\terrain\campaigns\3k_dlc07_main_map` | — | renames `models\river_N.*` and fixes `global_props.bin` | river files numbered as in vanilla |
| 11 | **Environment**: copy vanilla `environment_collection.xml` + `ambient_light_probes\` (BOB fails on this step) | — | — | identical |
| 12 | **Import** global_props, models, height_patches, environment | — | — | — |
| 13 | **DB**: `campaign_maps` and `campaign_map_playable_areas` max x / max y = `(hexmap_max_x − 1) × 0.668` and `(hexmap_max_y − 1) × 0.772` | — | — | 3k_dlc07: 596 × 542 |
| 14 | Generate **startpos**, switch the pack back to a mod | all | — | — |

**Step 10b, river meshes:** BOB's river meshes from the rebuilt splines stop short of vanilla's at the river ends and where the river widens. The land mesh is cut open to vanilla's footprint, so those spots show as **holes with no water** (732 px map-wide; the worst are by Danyang/Jianye, river_18, and Chibi, river_16). For an unchanged river network, replace `models\river_N.*` with vanilla's (same numbering after step 10). `global_props.bin` and `height_patches\` stay as BOB built them.

**Step 8b, sea meshes:** with a changed tile list, BOB can drop sea-mesh vertices inside rivers to its edge "skirt" height, 1 unit below the water (sea level −1.03 → −2.03). The river then looks too low or empty. For 3k_dlc07 this hit 308 vertices, in sea meshes 81 and 91 (the Danyang channel), 102, 103 and 112 (north of it), 82 and 2. The fix is to use vanilla's version of those meshes. Vanilla's cover the same land holes or more, so nothing is lost. To find affected meshes, compare vertices by x/z with vanilla and look for |dy| ≈ 1.

Why step 10: BOB numbers the river models in its own internal order, which ignores the entity names. The script matches each river to vanilla by its shape, renames the files, and rewrites the references in `global_props.bin` (rebuilding the offset table). It works out the mapping itself, so it's safe to rerun after every global_props build.

---

## 5. Capturing and checking each step

After each BOB step, copy the output folder and logs, then compare:

```
# capture (PowerShell or bash); <ts> = a timestamp, <step> = a label
copy  working_data\terrain\campaigns\3k_dlc07_main_map  ->  Z:\Claude\TerryClone\output\bob_runs\<ts>_<step>\terrain
copy  binaries\bob*.log                                   ->  Z:\Claude\TerryClone\output\bob_runs\<ts>_<step>\

# compare with vanilla
python research\bob_compare.py output\bob_runs\<ts>_<step>\terrain Vanilla\Map\terrain\campaigns\3k_dlc07_main_map output\bob_runs\<ts>_<step>\report.md
```

`report.md` lists every file as *match*, *differs* (decoded rasters show pixel counts and value fits) or *only in one side*. Files from steps you haven't run show as *only in vanilla*.

To check what's actually in a pack (e.g. whether a file is yours or vanilla's), read the pack index. The PFH5 layout is in `src\Atlas3K.Formats\Packs\PackFile.cs`. Then compare hashes against `output\bob_runs\…` and `Vanilla\Map\…`.

---

## 6. Troubleshooting

**Two kinds of crash, told apart in the Windows Application event log:**

| Exception | When | Meaning |
|---|---|---|
| `0xc0000005` in `ucrtbase.dll`, offset `0x268f1` | Right after a step finishes | BOB crashing on exit. **Harmless**: the outputs were already written |
| `0xc0000409` (subcode 7) in `ucrtbase.dll`, offset `0x7286e` | Partway through a step | BOB called `abort()`, almost always because an input file is **missing**. The logs stay empty |

**Finding the missing file** (this is how prerequisites 2 and 3 were found):
1. Run Process Monitor with a filter on the BOB process and reproduce the crash. Save the log as `.PML`.
2. Parse it: `PYTHONPATH=output/pylibs python` with `procmon_parser.ProcmonLogsReader`. The library is installed in `Z:\Claude\TerryClone\output\pylibs`.
3. Find the last `CreateFile` with result `0xC0000034` (NAME NOT FOUND) before BOB opens `binaries\abort` / `WerFault.exe`. That's the missing file.

**Other known quirks:**
- **`.rigid_model_v2` junk bytes:** BOB writes 4–7 uninitialized header bytes (offsets 0xa6, 0xf0–f1, 0x148–14b), so they differ from run to run. The geometry is identical.
- **Merged duplicate props:** two `prop_well_large` props are stacked twice with identical tags and near-identical rotation, so BOB keeps one of each. Vanilla keeps both. Nothing visible changes.
- **`environment_collection.xml`:** BOB fails because the project has no environment spheres. Copy vanilla's (it's static data: 6 lighting spheres pointing at the `3k_main_map` lighting files).
- **Sea flooding in Terry:** the sea TIFFs hold DDS values instead of source values (see §2).
- **River visible only once the area is explored:** the river entity has `visible_in_unseen_shroud="false"`. The original reverse-engineered rivers layer had this wrong on river_18 and river_19 (Danyang), so they vanished under the unexplored-area shroud. Vanilla has `true` on all 24 rivers. Fixed in the source layer on 2026-09-27; in an already-built `global_props.bin`, it's the byte 128 bytes after the text `models/river_N.wsmodel` (not after the start of the full path) in that region's block. After patching, check that the block is byte-identical to vanilla's.
- **If `global_props.bin` breaks things in game:** use vanilla's `global_props.bin` (from `terrain.pack`). For an unchanged prop set it's equivalent. Ours differs only in encoding, the grouping of one lake water plane, and 2 merged wells.
- **River missing in game:** `global_map` was built before your `tile_list.bin` was in the pack (see §3). Check that `global_map\tile_list.bin` is **not** vanilla's, and that it matches your root `tile_list.bin` apart from `07`→`00` flag bytes.

---

## 7. Parity with vanilla (2026-09-27, vanilla sources)

Confirmed in game on 2026-09-27: all rivers show, including Danyang and Chibi, using the BOB-built `global_props.bin`, the vanilla river meshes (step 10b) and the patched sea meshes (step 8b).

| Output | Status |
|---|---|
| `lf_height_map.compressed_map` / `.dds` | byte-identical |
| `lf_sea_height_map.compressed_map` / `.dds` | 1,047 px off by 1 (0.01%); a u16 source can't do better |
| `climate_map.cm` | byte-identical |
| `global_map\global_blend.dds`, `texture_arrays.xml`, `tile_list.bin` | byte-identical when built from vanilla's tile list |
| `global_meshes\land_mesh_N.compressed_map` (170) | byte-identical |
| `global_meshes\land_mesh_N.rigid_model_v2` (170) | geometry identical; 4–7 junk header bytes |
| `global_meshes\sea_mesh_N.rigid_model_v2` (125) | layout identical; heights ≤0.00015 off |
| `height_patches\*` (55 + collection) | byte-identical |
| `models\river_N.*` (24 × 2) | shapes match within ~0.2 units median; numbering fixed by step 10; `.wsmodel` differs only in line endings |
| `global_props.bin` | all 264 regions match; 69,814 vs 69,816 objects (the 2 merged wells) |
| `environment_collection.xml`, `ambient_light_probes\*` | copied from vanilla |
| `tile_list.bin` | different on purpose (your fixed tile map) |

---

## 8. Scripts used

| Script (`Z:\Claude\TerryClone\research\`) | Purpose |
|---|---|
| `compressed_map.py` | Decodes `*.compressed_map` / `.cm` / height patches; documents BOB's normalization |
| `rivers_from_meshes.py` | Rebuilds the river splines from the baked river meshes; RMV2 river mesh reader |
| `renumber_rivers.py` | Step 10: vanilla river numbering + `global_props.bin` fix-up |
| `bob_compare.py` | Compares one build against vanilla and writes `report.md` |
| `3k_global_props_to_terry_layers.py` | `global_props.bin` → Terry `.layer` files (with tag layers) |
| `source_sea_from_compiled.py` | Superseded: the "flat sea" idea was wrong (see §2) |
