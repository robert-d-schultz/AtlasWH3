# BOB on WH3: what runs headless, what it reads and writes

Measured 2026-10-08 on the 2026-09 kit (`bob.modder.x64.exe`), with AtlasWH3's scratch kit, `BobRunner` and the Frida
tracers in `research/bob_re/` (`trace_files.py`, `probe_selection.py`, `probe_frida_selection.py`). Map:
`cr_combi_expanded_map_1` (IEE).

## Headless BOB runs the default actions only

Atlas3K drove 3K's BOB with a configuration file (`binaries/BOB/<name>_configuration.xml`, `<silent>1</silent>`,
`<selected_actions>`, `<selected_consumers>`). WH3's BOB has **no `<selected_actions>`**: the string does not exist in
`bob.modder.x64.exe`. Its configuration has processors, directories, global rules, options and the selected providers
and consumers, nothing else.

With the map's `.terry` as the consumer, silent BOB creates 12 action objects and selects 8 of them:

| Selected by default (one silent run) | Class |
|---|---|
| Terry file | `ACTION_PROCESS_TERRY_FILE` |
| Color Overlay, Color Overlay (Sea), Snow Mask, Corruption Mask, Patch Visibility Mask, Event Area Mask | `ACTION_PROCESS_TERRAIN_MAP` (6 objects) |
| Devastation pieces | `ACTION_PROCESS_TERRAIN_DEVASTATION_PIECES` |

(The other four objects are `ACTION_INIT_WARSCAPE`, `ACTION_CLEANUP_WARSCAPE`, `ACTION_TERRAIN_AGF` and `ACTION_ERROR`.)

**Campaign Heightmap, Campaign Shroud Heights, Tilemap, Campaign Trees, Global Tilemap, Campaign Global Blendmap and
Generate Camera Height Map are not even constructed** in a silent run. They exist only when picked in the GUI's
action-selection popup (BOB preference "Show action selection popup", `ShowActionSelectionPopup`). So they cannot be
selected by a provider filter (tried), by a different consumer (the tile map, a height TIF: nothing selected), or by
forcing `ACTION_INTERFACE::select` with Frida (there is no object to select).

Consequences for the build:
- The BOB fallback of the hybrid build is **one group**: a silent run of the 8 default actions. It cannot run one of
  them alone.
- `heightmaps`, `tile_list`, `trees` and `global_map` have **no headless BOB fallback**. They must be native (plan
  Phase 3.1–3.4), or BOB has to be driven through its GUI (Atlas3K's `tools/bob_mcp/bobgui.py` approach, not ported).
- BOB start-up is about 10 s. On IEE the defaults take about 10 min: Devastation pieces 5 min and the Terry file
  3 min (in parallel), the masks seconds.

## What the default group reads and writes

From `trace_files.py` (every `CreateFileW` of the run):

- **No game pack is opened.** The defaults read the kit only.
- Reads: the map's `.terry` and **every layer TIF** directly (heights, shroud, blend, overlays, masks, trees, event
  area), `tile_map.png`, `working_data/campaign_maps/<map>/map_data.esf`, the prefab `.terry` files the layers use, the
  `working_data/RigidModels/...` models and materials (height patches), the campaign tile database settings, and its
  own fresh outputs (`colour_overlay.dds`, `lf_sea_colour.dds`, `corruption_mask.dds`, `snow_mask.dds`) for the pieces.
- Writes: `colour_overlay.dds`, `lf_sea_colour.dds`, `snow_mask.dds`, `corruption_mask.dds`, `event_area_mask.dds`,
  `patch_mask.dds` (Patch Visibility Mask), `global_props.bin`, `global_props_sound.bin`, `models/river_*`,
  `environment_collection.xml`, `event_tiles`, `event_trees` and `pieces/event_*`.
- **Devastation pieces makes its own height crops**: each piece got `full_height_map.dds`, `shroud_heights.dds`,
  `lf_normal.dds`, `global_blend.dds` and `tile_mask.dds` although the run had no `full_height_map.dds`,
  `shroud_heights.dds` or `tile_list.bin` in its output folder. It took the trees from the loose
  `campaign_maps/<map>/display/trees/trees.campaign_tree_list`. With no `tile_list.bin`, no piece got a `tile_list`.
- Each raster action also writes its crop into every piece folder (BOB's log lists "Processing …/pieces/event_*" for
  the raster actions too).

So the two pack round trips of the user's GUI workflow (§2 of the plan) come from the manual actions (Tilemap and
Global Tilemap reading the heightmap / tile list from the packs), not from the default group.

## Running BOB safely

BOB writes its outputs, logs and configuration files inside the kit it runs from. AtlasWH3 never runs it in the user's
kit: `ScratchKit` makes an isolated copy (`assembly_kit_atlaswh3` next to the kit; real copies, no hard links; only the
chosen maps' folders; marked with `atlaswh3_scratch_kit.txt`) and `BobRunner` runs BOB there. CLI:

```
AtlasWH3.Cli bob-scratch --maps cr_combi_expanded_map_1           # create / refresh the scratch kit
AtlasWH3.Cli bob-run --maps cr_combi_expanded_map_1 --fresh       # the default group, outputs cleared first
```

## A fresh run against the user's own output (IEE, 2026-10-08)

`bob-run --maps cr_combi_expanded_map_1 --fresh` in the scratch kit: exit 0, 8 actions, 632 s. Compared with the
`working_data` the user's GUI runs left:

| Output | Fresh run vs the user's |
|---|---|
| `colour_overlay.dds`, `lf_sea_colour.dds`, `corruption_mask.dds`, `snow_mask.dds`, `event_area_mask.dds` | byte-identical (deterministic) |
| `patch_mask.dds` | not written: the map's PatchVisibilityMask has no layers (the user's file is older) |
| `global_props.bin`, `global_props_sound.bin`, `models/river_*`, `event_trees` | differ: the layers changed after the user's last run |
| `event_tiles` | empty: the scratch kit had no `tile_list.bin` (it comes from Tilemap, a GUI-only action) |

So a hybrid build must write the native `tile_list.bin` (and trees) into the scratch kit **before** the BOB group
runs, or the pieces lose their tile lists.
