# Battle map editor: plan (draft, 2026-10-05)

Source survey: `Z:\Claude\BattleMaps` (docs, BOB decompiles, `bobsrc` scripts, the `BattleLocationsMap` tool, 190E outputs)
and Atlas3K's existing experimental battle window (`docs/battle_map_editor.md`).

## Where we are

There are two kinds of "battle map". Their state is very different.

| | Campaign-battle terrain (`terrain/battles/3k_main_map`) | Standalone battle maps (`terrain/battles/<folder>`) |
|---|---|---|
| What it is | The 892×703 cell grid that every campaign battle is cut from, plus the catchment areas (ambush, settlement, encampment, gate) | Hand-made maps: sieges, cities, ports, custom battles (`test_map`) |
| Formats | Decoded byte-exact: `battle_locations_map.bin`, `tile_map.tiles/.index`, the BOB raw sources, the tile database. `tile_map.bmd` is partly decoded | Mostly not decoded: `tile_list.bin`, `tile_map.bmd`, per-tile meshes, `hf`, grass and tree lists, `bmd_nogo_data`. Read: the Terry `ProjectTileWithVista` project, `map_info.xml` |
| Tools | `blm` / `blm-ui` (catchments, redirects, 190E coverage) and Atlas3K's Battle window (tile paint, heights, catchments, BOB build: catchments and climate byte-identical, ~92% of tiles match) | None. Built once through the kit + BOB (`test_map`) |
| Terry components | Catchment fields known from the decompiles | ECCaptureLocation, ECDeploymentZone(+Region), ECBuilding and ECWall partly known from prefabs. 31 components (PlayableArea, BattlefieldZone, Camera*, …) have no fields |

## Phases

### Phase 1: Campaign battles for 190E (one Battle workspace)
The quickest win. Everything it needs is decoded.
- Move `blm-ui`'s features into Atlas3K's Battle window so there is one tool:
  - the catchment editor;
  - coverage gaps;
  - the Regions panel, with each settlement's battle kind (city a–h, port, resource);
  - redirect to a battle-map folder;
  - writing the `battles_tables` row.
- Make it a mode of the Scene editor shell: layers panel, info cards, walkthrough, unsaved `*` prompt.
- Check every 190E settlement has a catchment of the right type; one click fixes a gap.
- Output goes to the kit or an output pack, never to the original files.
- Open item to settle in game: whether `battle_redirection` works on settlement catchments (it is proven on gates only).

### Phase 2: Standalone battle map projects (Terry-like)
- **Project:** create or open a `QTU::ProjectTileWithVista` project:
  - 8×8 tile; TerrainMaps 1280² (Height, Blend8, GroundType, ColorOverlay, mesh mask);
  - vista, environment, climate;
  - a `user_created_map` block (name, author, players, battle type).
- **Editing:** a 3D/2D viewport with the same height and texture brushes as the campaign Terrain tab, plus prop placement and clamp-to-ground from the Props tab (battle prefabs from `art/prefabs/battle`, 1,302 of them).
- **Battle entities:** deployment zones (polygons per alliance), capture points, playable area, buildings, walls, siege AI nodes.
  - These need the missing component fields first. Get them by decompiling `tweak_terrainmetadataeditor.modder.x64.dll` / `tweakshared.modder.x64.dll` in Ghidra, the same method as BOB.
- **Build:** through BOB at first (the `test_map` route: kit → BOB → `map_info.xml`, `tile_list.bin`, …), then a pack in the output folder and a `battles_tables` row.

### Phase 3: Show battles as they look in game
Draw what the scene view skips today: battle tile terrain meshes (material 96), SpeedTree trees (74/75) and `.cs2.parsed` buildings. Reuse the D3D11 viewport.

### Phase 4: Native battle build (off BOB)
Use the campaign parity method: decompile, Frida, field diffs, first divergence. Order of work:
1. `tile_list.bin` for battles (check it against the campaign `TileListWriter`);
2. finish the campaign-battle tile matching (the 8% of cells that differ);
3. per-tile `hf`, `ground_types`, `blend`;
4. grass and tree lists, `bmd_nogo_data`;
5. meshes.

`tile_map.bmd` is not produced by BOB: copy it until its format is known. This is the long research track. Keep it separate from the editor so the editor ships first.

## Risks
- Unknown component fields block deployment zones and capture points in standalone maps. Do the decompile early in Phase 2.
- `battles_tables` and redirects are tied to the battle type. A wrong type means the battle won't load.
- 14 GB of extracted data lives in `BattleMaps`. Atlas3K should read from the packs and kit through the linked-pack settings, not that folder.
