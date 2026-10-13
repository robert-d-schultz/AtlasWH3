# Campaign tile-map editor

This is validated hand-editing of a campaign `tile_map.png`, the source BOB's Terrain / Tilemap turns into `tile_list.bin`. You can add tiles (paint a tile set) and remove them (erase back to the surrounding land or sea) hex by hex. Each edit is checked before it is written, so you can adjust the map quickly without a BOB round trip per try.

There are three front ends, all on the same core (`src/Atlas3K.Core/Campaign/TileMapCheck/`):

| Front end | Where |
|---|---|
| GUI | Atlas3K app, **File > Edit campaign tile map...** (`CampaignTileWindow.cs`, `CampaignTileView.cs`) |
| CLI | `Atlas3K.Cli tiles-*` (`src/Atlas3K.Cli/TileCommands.cs`) |
| terry MCP | `list_tile_sets`, `get_tiles`, `edit_tiles`, `paint_tiles`, `erase_tiles`, `draw_tile_line`, `fill_tiles`, `validate_tiles`, `preview_tiles`, `tile_checkpoint` / `tile_rollback` / `tile_undo` / `tile_history`, `replay_tiles`, `check_tile_holes` |

There is also a **web editor** for the iPad and Apple Pencil (`src/Atlas3K.Web`), covering the tile map and the ground textures. See [Web editor](#web-editor-ipad--apple-pencil) below.

All of them share one undo journal per tile map: `output/tile_edits/<map>/` (snapshots, `journal.jsonl`, `checkpoints.json` and `ops.jsonl`). A GUI save and an MCP edit therefore show up in the same history.

## Tile map source

By default the tile map is the kit's `raw_data/terrain/campaigns/<map>/tile_map.png`, edited in place. **File > Tile map source…** in the tile map window, **Settings > Tile map source** (the default) and the `.atlas3k` project's **Tile map source** (Build window, Project settings tab; JSON field `tileMap`) can read it instead from:

| Kind | Reads | Saves edits to |
|---|---|---|
| Kit (default) | the kit's map folder | the same file |
| File | a `tile_map.png`, or a folder holding one | the same file, or `tile_map.png` in **Save edits to** |
| Pack | an entry in a `.pack` (default `terrain/campaigns/{map}/tile_map.png`) | `tile_map.png` in **Save edits to** (default: the kit's map folder) |

The pack is never written. When the save target is not the source, the editor first copies the source there, as one journaled step that *Undo last saved batch* reverts, and records that in `source.json` beside that target's journal. Each target has its own journal (`output/tile_edits/<map>/custom_<hash>/` for anything other than the kit file).

The build's `tile_list` step reads the same setting. It uses the editor's copy once the editor has seeded it from this source. Otherwise it reads the source, extracting a pack entry to `<build output>/_atlas3k_inputs/<map>/tile_map.png` first. The build log names the file it used.

```json
"tileMap": { "kind": "Pack", "path": "{game}\\my_map.pack", "internalPath": "", "saveFolder": "{project}\\tile_map" }
```

## Coordinates

- Hexes are `[col, row]` and row 0 is **south**. The image is `2W × (2H+1)` pixels at 2×2 px per hex, and odd columns sit half a hex north (`HexTileMap`).
- `get_tiles` / `tiles-get --world x,z` converts campaign world coordinates to the hex under them (`TileMapEditor.WorldToHex`; tile size is 595.1/1784 world units per pixel).
- Tile sets are named as in the tile database (`list_tile_sets`): `generic`, `generic_sea`, `sea_coast`, `mountains_*`, `river`, `river_start`, `river_mouth`, `roads_tracks` / `roads_paved` / `roads_imperial`, `blockout_cliff`, `canal`, ... A raw `"#rrggbb"` colour also works.

## Ops (tiles-edit / edit_tiles; the GUI records the same)

| Op | Fields | Effect |
|---|---|---|
| `paint` | `set`, area | Add tiles: every hex in the area becomes the set |
| `erase` | area, optional `to` | Remove tiles: hexes take the most common neighbouring **area** set (land or mountains, not coast or lines), or sea if they only touch sea. The area is resolved from its edge inwards |
| `line` | `set`, `points`, optional `width` | A connected one-hex-wide path through the waypoints (rivers, roads, cliffs, canals) |
| `fill` | `set`, `at`, optional `max` (20000) | Flood fill of the connected same-colour area |
| `replace` | `from`, `to`, area or `"all": true` | Recolour one set to another |

An area is one of `"hexes": [[c,r],...]`, `"circle": [c, r, radius]`, `"rect": [c0, r0, c1, r1]` or `"polygon": [[c,r],...]`.

## Validation

After the ops run in memory, `TileMapValidator.CheckMap` (owned by the validator work: palette, line width, coast ring, cliff ends, river ends and crossings, 7-hex patterns CA's map never uses) checks the changed hexes plus a ring around them. It does so on the map **before and after** the edit, and only issues the edit **introduces** are reported (`new_issues`). Issues already on the map are counted (`existing_issue_hexes_in_area`) but don't block.

The validator's severities are tuned for whole maps: CA's own map has thick lines and unseen patterns, so those are warnings there. A hand edit that newly causes one is still a likely see-through hole, so the editor blocks on:

- any new **error**;
- any new **warning** except `palette.unused_set`.

`allow_warnings` / `--allow-warnings` / the GUI's "Save despite warnings" write past warnings. `force` writes regardless. `dry_run` never writes. Nothing is written when an op fails.

Rivers through open land get flagged only at their two loose ends. Give the source a `river_start` hex and end the river in the sea (`river_mouth`) or in another river.

### BOB tile-matching simulation (explicit, ~1-3 min)

The per-edit check works on hex patterns. For an exact answer, `simulate_tiles` / `tiles-simulate [--since N]` / the window's **Check > Simulate BOB Tilemap** runs `TileMatchSimulator`, the port of BOB's tile placement with the tile database's links and masks. It runs on the whole map, so it is too slow for every edit: about 70 s on vanilla and 2.6 min on main190.

It lists the hexes that would get **no tile** (see-through holes) and splits them into holes in edited hexes and holes that were already there. The window counts unsaved strokes as edited, marks those holes red and the older ones orange.

Accuracy against BOB:
- vanilla: 175 of BOB's 176 uncovered points;
- main190: all 54, plus 4 extra.

Check run 2026-10-02: a 7-hex river blob forced onto main190 gave 6 no-tile hexes, all 6 attributed to the edit.

## After editing

0. Optional, before BOB: `simulate_tiles` to catch holes in the edits without a BOB run.
1. Run BOB **Terrain / Tilemap** (bob MCP `bob_run_action`). `tile_list.bin` only changes then.
2. `check_tile_holes` / `tiles-holes`. This is a C# port of `research/main190/tile_holes.py` and matches it exactly on main190 (54 cells). It lists uncovered clusters (edge or suspect) and flags the ones in hexes edited since `since_seq`. Settlement classification still needs `tile_holes.py` with map.hex.
3. `build_step global_map, global_mesh`, then pack. The pack must contain the new `tile_list.bin`.

## Regenerated tile maps

`research/main190/caime_tilemap.py` rewrites the 190E kit tile map from map.hex, and that wipes hand edits. Every written batch is kept with its ops in `ops.jsonl`. After a rebuild, `replay_tiles` / `tiles-replay [--from N --to M]` re-applies them as one new validated batch. Undo and rollback refuse to restore over a file that changed outside the editor, unless forced.

## Web editor (iPad + Apple Pencil)

Start it with `tools\web_editor.cmd`, which edits the scratch copies in `output\scratch\web_edit`. Use `tools\web_editor.cmd kit` to edit the real 190E kit files.

It serves `http://127.0.0.1:5180`. `tailscale serve --bg 5180` publishes that address to the tailnet as **https://arsal-desktop.dab-caiman.ts.net**, and `tailscale serve reset` undoes it. Open that address in Safari on any tailnet device; "Add to Home Screen" gives a full-screen app.

**Controls**
- **Pencil or mouse:** draws with the active tool.
- **One finger:** pans.
- **Two fingers:** pinch to zoom.
- **Two-finger tap:** undo. Touches are ignored while the Pencil is down (palm rejection).
- **Keyboard:** Ctrl/Cmd+Z, Ctrl+Y, Ctrl+S, `[` and `]` for brush size, Enter to finish a line.

**Tile map mode** uses the same ops, validation, save and journal as the CLI and MCP tools, through `TileEditSession` held on the server.
- Tools: paint, remove, line, fill and pick.
- The panel lists new issues, and tapping one jumps to it.
- **Simulate BOB Tilemap** runs `TileMatchSimulator` on the unsaved state.

**Textures mode** paints the kit's ground-texture source, `<map>.blend.<layer>.tif`: 8-bit, lf resolution, one pixel per texture group in texture_arrays.xml order. `global_map` turns it into global_blend.dds channel 0. The work is done by `Core/Editing/BlendEditSession.cs`.
- Strokes run through `BlendBrush` on the server. Pencil pressure scales the radius from 35 % to 100 %.
- Softness, strength and "only over <texture>" filters apply to strokes.
- Undo and redo work in memory.
- Save writes the TIF (palette kept, LZW) through `FileJournal` (`output\blend_edits\<map>`). It refuses if the file changed on disk since it was loaded, unless you confirm.
- The map streams as 256 px tiles of raw group indices: gzip, a level pyramid, and nearest sampling (`/api/blend/tile/{level}/{x}/{y}`). The client colours them either by each texture's average colour ("Natural colours") or by the TIF's editor palette.
- After saving, rebuild with `build_step rasters, global_map`, then pack.

Climate stays as it is: it lives in climate_map.png.

## Files

- `TileMapOps.cs`: ops, hex geometry (odd-q cube coordinates), lines, circles, polygons, erase.
- `TileMapEditor.cs`: load, apply, before/after validation, journaled write, ops log, undo/rollback, replay, hex ↔ world.
- `TileMapPreview.cs`: PNG of a hex area (`preview_tiles`, `--preview`).
- `TileHoles.cs`: post-Tilemap coverage check.
- `src/Atlas3K.Core/Editing/FileJournal.cs`: the undo journal, shared with `PropEditor`.
- Tests: `src/Atlas3K.Tests/TileMapEditTests.cs`.
