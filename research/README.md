# Moving a Total War campaign build off BOB

This folder is the research behind Atlas3K's native campaign build, kept in AtlasWH3 for its method. AtlasWH3 removed
the 3K-only folders (`gmesh/`, `camera/`, `derived_maps/` and the global-mesh Frida scripts): WH3 has no global meshes,
and its camera heightmap is built from other inputs. They are in git history before the cut. The native build
reproduces BOB's output for Total War: THREE KINGDOMS:

| File | Status |
|---|---|
| `tile_list.bin`, `global_map\` | byte-identical to BOB |
| `lookup` textures | byte-identical to BOB |
| `camera_heightmap.png` | byte-identical to BOB |
| `global_props.bin` | byte-identical to BOB |
| river models, `height_patches\` | byte-identical to BOB, except bytes BOB leaves uninitialised |
| `global_meshes\` (land / sea meshes, `.compressed_map`) | byte-identical to BOB, except uninitialised bytes |
| `trees.campaign_tree_list` | byte-identical to BOB's own Campaign Trees output (all 205,767 vanilla trees) |

The *uninitialised bytes* are bytes BOB fills with leftover memory: they differ between two BOB runs of the same
input. The parity tools mask them.

It is written so modders of other Warscape games (Warhammer, Troy, Pharaoh, Three Kingdoms mods) can reuse the method:
most of BOB's campaign code lives in shared Warscape DLLs, so the same functions, hooks and file formats come back.

Rules per step, formats and current numbers: [`docs/native_campaign_build.md`](../docs/native_campaign_build.md).
Deep dives: [`docs/bob_tile_matching.md`](../docs/bob_tile_matching.md),
[`docs/bob_re_tile_placement.md`](../docs/bob_re_tile_placement.md),
[`docs/bob_re_global_mesh.md`](../docs/bob_re_global_mesh.md),
[`docs/bob_campaign_build.md`](../docs/bob_campaign_build.md).

## The method

The aim is byte-identical output. A tool that is only "close" leaves you guessing about any visual bug in game, so each
step was pushed until its bytes matched BOB's, with three tools.

1. **Decompile** the BOB action with Ghidra. The code is in the kit's `binaries\` DLLs. The ones that matter here:

   | DLL | What it holds |
   |---|---|
   | `warscape.modder.x64.dll` | tile matching, terrain height, global mesh |
   | `bob_terrain.modder.x64.dll` | rivers, props export |
   | `qttoolutility.modder.x64.dll` | Terry-side helpers, campaign trees |
   | `empireutility.modder.x64.dll` | tree list writer |
   | `calibs.modder.x64.dll` | strings, hash maps |

   Most functions keep their C++ names as exports. Use `bob_re/find_exports.py <substring>` to find a function in any
   kit DLL. The decompiles themselves are not in this repo, because they are CA's code: re-create them with Ghidra
   (headless analyse plus a decompile script).

2. **Instrument BOB with Frida.** Decompiles show *what* a function does. A dump of BOB's live state at the function
   boundary shows *which* inputs and order BOB actually uses. That settled every case where the decompile looked right
   but the output didn't match.
   - **Harness:** `bob_re/frida_bob.py <script.js> <kit root> <kit path> <action> <label> [--gui]` starts one BOB
     action (through `tools/bob_mcp`), attaches as soon as `bob.retail.x64.exe` appears, and writes every message to
     `bob_re/frida_out/<label>.jsonl`.
   - **Example scripts:** `frida_tilemap.js` (tile database and pass sorts), `frida_trees*.js` (tree height lookups)
     and `frida_rivers*.js` (river meshes).
   - **Gotchas:**
     - The tile code DLL loads a few seconds into a run. Poll `Process.findModuleByName`, because
       `attachModuleObserver` did not fire.
     - Frida 17 has no `Module.getExportByName`; call `getExportByName` on the module itself.
     - Strings are CA's own `String` type; read them with calibs' `?data@String@CA@@QEBAPEBDXZ` and
       `?length@String@CA@@QEBAIXZ` exports.
     - Keep hot hooks cheap, or attach them only while the object you study is being built: a hook on a function called
       10 million times slows BOB to a crawl.
     - Some actions (Terry file) only run in BOB's GUI mode (`--gui`).

3. **Diff field by field and find the first divergence.** A byte diff of two files says *that* they differ. These say
   *where* and *why*:
   - `tilelist_fields.py`, `tilelist_area.py`, `sim_compare.py` and `sim_first_divergence.py` for tile lists;
   - `props/run_parity.sh` for global props;
   - `rivers/river_cmp.py` for rivers;
   - `trees/lf_exact.py` for tree heights.

   Walking placements in BOB's own order and stopping at the first difference is what exposed every root cause below.
   After the first wrong choice, the random-number stream shifts and everything after it looks broken.

Typical loop: decompile, port, diff, find the first divergence, Frida-dump that spot, fix, repeat. Run the
float-precision experiments in numpy `float32`: each operation is correctly rounded, so a Python prototype behaves
exactly like the C++.

## What made the difference (the non-obvious rules)

Each of these looked fine in the decompile and only showed up when the output was compared.

| Step | Rule |
|---|---|
| Tile list | `TILE_DATABASE::sort` runs **before** link targets are loaded (`link_target_count()` is 0 for every tile), so the database order is area, then name. Using the real counts reorders everything and shifts BOB's random-number stream. |
| Tile list | The pass sorts are MSVC `std::sort` (introsort, unstable). Port it exactly (`MsvcSort`), including how ties come out. |
| Tile list | `calculate_flow` neither flows nor queues a tile that has no `TLT_EQUALS` entry link (a river crossing). |
| Trees | The tree list comes from BOB's *Campaign Trees* action. The height scale is `(l·1100)·f′ − f′·240` with f′ = tile/25.6, the terrain setup's own constants. The algebraically equal `(l·5500)·f − f·1200` rounds differently in float32. |
| Trees | Per tree, the height provider (qttoolutility FUN_18011f1d0) asks only the tiles registered on the point's cell, (int)(x/T), (int)(z/(T·1.15476)), each tile on cells [X, X+w) × [Y, Y+h), and keeps the **highest** answering height (0 if none answers). Not the camera quadtree, and not the first tile. |
| Global props | Quadtree cell rule, record order by entity id, BOB's own quaternion and sine routines for matrices, `-0` written as `+0`, region order from CA's hash map, map bounds from `map_data.esf`. |
| Camera heightmap | BOB samples its scene (height patches, global mesh, a tile fallback reached through its own quadtree), not "terrain plus props". The PNG needs classic zlib's compressor to match byte for byte. |
| Rivers | Adaptive spline sampling (`optimise_spline`, including its in-place unique bug), BOB's own float-to-half, a snap pass of vertices onto triangle corners, triangles dropped by normal y, and height patches rasterised from the river models already on disk. |
| Rivers | Entity yaw turns points and tangents in float (cos/sin rounded to float first), `reverse_direction` walks the points backwards with tangents swapped, `terrain_relative` has no effect on the mesh. |
| Rivers | The river_N numbering rule (regions by largest entity id) fits main190 but not vanilla (11 of 24 renumbered): still open. |
| Global meshes | When the pack is set to Movie, BOB reads lf and the tile list from the game VFS, not the kit. The grid step is in double precision. The triangle merger runs 50 passes of 1.28 with an MSVC-sorted candidate list. |
| Global meshes | The VFS rule holds on vanilla too: BOB reads CA's `tile_list.bin` and `lf_sea_height_map` from `data/terrain.pack`, not the kit's (`gmesh/find_pack_inputs.py`, in git history). Compare against a fresh BOB run only with the inputs BOB read. |
| Global meshes | Land byte 0xA6 is character 165 of the mesh's own `.compressed_map` output path (stale string memory): it depends on the kit folder and map name. |
| Global meshes | MESH_SPLITTER closes a chunk after the triangle that brings it to 65,000 vertices; the chunks become extra meshes of the same LOD, not extra files. |

## Folder index

| Folder | What's in it |
|---|---|
| `bob_re/` | Frida harness and scripts, export finder, sort/database checks |
| `tiles/` | tile database format (`TILEDB_FORMAT.md`), tile-list comparisons, MSVC sort prototype |
| `trees/` | tree list decode/encode, lf and per-tile hf height prototypes |
| `props/` | `global_props.bin` parity tools (field diff, hash-map order, `run_parity.sh`) |
| `rivers/` | BOB river spline and mesh prototype, comparisons |
| `guandu/` (4 files) | hex grid and `map.hex` field references used by the program |
| top-level scripts | format readers (`compressed_map.py`, `hexmap.py`), comparisons (`bob_compare.py`, `native_vs_bob.py`) |

Scripts expect a local copy of the game data. No game files are in this repo: point the scripts at your own Assembly Kit
and packs.
