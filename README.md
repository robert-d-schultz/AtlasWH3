# AtlasWH3

Campaign map tooling for **Total War: WARHAMMER III**. It builds a campaign map from the Assembly Kit's own sources
(the Terry project, its layers and maps) **natively**, without BOB, and adds editors for those sources.

AtlasWH3 is a standalone fork of [Atlas3K](https://github.com/Ironictw2st/Atlas3K), the same tooling for Total War:
THREE KINGDOMS. The two games share an engine, but WH3's campaign pipeline differs a lot from 3K's, so AtlasWH3 is
retargeted to WH3 only.

> **Status: planning. Nothing has been ported yet.** The code in this repository is still Atlas3K's 3K code, as forked
> at tag `fork-point` (Atlas3K 0.1.0-alpha.2). It does not support WH3 yet, and with the pinned SDK it does not build
> as is. The plan is in [`docs/atlaswh3_plan.md`](docs/atlaswh3_plan.md).

## Why

A full reprocess of a WH3 campaign map in BOB takes about 20 minutes:
- about ten separate actions
- BOB takes minutes just to open
- some actions read their inputs from the **.pack** instead of `working_data`, so intermediate results have to be
  packed and installed between actions

Two outputs don't build in BOB at all:
- *Generate Camera Height Map* crashes, so `camera_heightmap.png` is made by hand.
- Devastation pieces only come out if you set up a fake second campaign map for BOB to process.

The goal is **one command that rebuilds the whole map from loose files**, with no pack round trips and no workarounds.

## Planned build steps

| Step | Writes | Replaces |
|---|---|---|
| `heightmaps` | `full_height_map.dds`, `full_logic_map.compressed_map`, `shroud_heights.dds` | Campaign Heightmap, Campaign Shroud Heights |
| `trees` | `trees.campaign_tree_list` | Campaign Trees |
| `tile_list` | `tile_list.bin` | Tilemap |
| `global_map` | `global_map\` (blend, texture arrays, tile list) | Global Tilemap, Campaign Global Blendmap |
| `masks` | colour overlays, corruption, snow, event area, patch and tile masks, `lf_normal` | Color Overlay, Corruption / Snow / Event Area / Patch Visibility Mask |
| `devastation_pieces` | `pieces\event_*` for the main and devastated maps | Devastation pieces, without a fake campaign |
| `lookup` | `*_lookup.tga` / `.dds`, `_minimap.tga` | Convert lookup texture |
| `camera_heightmap` | `camera_heightmap.png` | Generate Camera Height Map (crashes in BOB) |
| `global_props` + `rivers` | `global_props.bin`, `global_props_sound.bin`, devastation-type BMDs, `models\river_*` | the props / Terry export action |
| `hlp_spd` | `hlp_data.esf`, `spd_data.esf` | the game's own generation (last) |

Until a step is native, the build runs that BOB action headless and handles the pack round trips itself.
- **The aim:** files that work correctly in game first, then byte-identical to BOB's output wherever that is
  practical.
- **Inputs, not outputs:** `map.hex`, `map_data.esf`, `pathfinding.ppd` and the lookup `.bmp` come from CAIME, as
  they did for Atlas3K. `startpos.esf` is out of scope.

## Documentation

- [`docs/atlaswh3_plan.md`](docs/atlaswh3_plan.md): the plan, with the decisions made, the phases, the test maps and
  the risks.
- [`docs/surveys/warhammer3.md`](docs/surveys/warhammer3.md): Atlas3K's survey of how far its 3K build carries over
  to WH3, format by format.
- The rest of `docs/` and `research/` is Atlas3K's 3K documentation. It is kept for its method (Ghidra, Frida
  instrumentation of BOB, byte-level parity diffs) and for the rules the two games share.
- Atlas3K's own README (3K features, install, build window, CLI):
  [at the fork point](https://github.com/Ironictw2st/Atlas3K/blob/cdd0a08fa0a07db3bc2e7faba6822a75d5512d91/README.md).

## Related tools

- [WH3_visual_map_decompiler](https://github.com/robert-d-schultz/WH3_visual_map_decompiler) does the reverse: it
  turns a compiled WH3 campaign map back into a Terry project. AtlasWH3 reuses its format readers, and the two together
  give a round-trip test.
- [CampaignMapToolkit (CAIME)](https://github.com/robert-d-schultz/CampaignMapToolkit) makes the campaign AI map files
  AtlasWH3 takes as input. Its code is under a non-commercial licence, so none of it is copied here.

## Building from source

```
dotnet build AtlasWH3.slnx -c Release
dotnet test src/AtlasWH3.Tests
```

The current `global.json` pins .NET SDK 9.0.312. The first phase of the plan moves the projects to .NET 10 and renames
them to AtlasWH3.

## Licence

[MIT](LICENSE), as Atlas3K. The libraries it uses keep their own licences; see
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Not affiliated with Creative Assembly or SEGA. Total War: WARHAMMER III and its Assembly Kit are their property.
AtlasWH3 ships none of the game's data.
