# AtlasWH3

**Build Total War: WARHAMMER III campaign maps without BOB.**

AtlasWH3 compiles a campaign map from your Assembly Kit sources (the Terry project, its layers and maps) in one run,
then packs it into your mod and installs it. It does everything BOB's campaign actions do, plus the AI pathfinding
data the game would otherwise have to build, and it runs headless from the command line as well as from a window.

- **One run instead of ten actions.** A full IEE build takes about 5 minutes on a 4-core machine. A BOB reprocess of
  the same map takes about 20, with a pack round trip between some of its actions.
- **No workarounds.** The devastated map needs no fake second campaign. The camera height map builds (BOB's action
  fails). Nothing has to be packed and installed halfway through.
- **Checked against BOB.** Most outputs are byte-identical to BOB's on the test maps, and the full IEE build has been
  played in game.

## What it builds

| Step | Writes | Replaces |
|---|---|---|
| `heightmaps` | `full_height_map.dds`, `full_logic_map.compressed_map`, `shroud_heights.dds`, `lf_normal.dds` | Campaign Heightmap, Campaign Shroud Heights |
| `tile_list` | `tile_list.bin`, `tile_mask.dds`, `patch_mask.dds` | Tilemap |
| `trees` | `trees.campaign_tree_list` | Campaign Trees |
| `global_map` | `global_map\` (blend, texture arrays, tile list) | Global Tilemap, Campaign Global Blendmap |
| `masks` | colour overlays, corruption, snow and event area masks | Color Overlay, Corruption / Snow / Event Area Mask |
| `rivers` | `models\river_*` | Terry file (river export) |
| `global_props` | `global_props.bin`, `global_props_sound.bin` | Terry file (props export) |
| `environment` | `environment_collection.xml` | Terry file |
| `devastation_pieces` | `pieces\event_*`, `event_tiles`, `event_trees`, `event_vfx`, for the map and its devastated version | Devastation pieces |
| `lookup` | `*_lookup.tga` / `.dds`, `_minimap.tga` | Convert lookup texture |
| `camera_heightmap` | `camera_heightmap.png` | Generate Camera Height Map |
| `hlp_spd` | `hlp_data.esf`, `spd_data.esf` | the game's own generation during a startpos build |

Independent steps run in parallel. Every step reads the kit's sources and the game and mod packs directly, so
nothing has to be installed between steps.

**How close to BOB:** the logic map, shroud heights, tile list, masks, piece textures and river models (but for two
bytes BOB leaves uninitialised) are byte-identical to BOB's on IEE and The Old World. Trees are byte-identical on Old World; on IEE every tree has the
same id, position and rotation, and all but 23 of 253,903 heights agree within 1e-3. `global_props.bin` has the same
objects in the same bodies; BOB's own record order changes from run to run, so it is compared by content.
`hlp_data.esf` and `spd_data.esf` match the game's own files byte for byte on IEE (on Old World one value differs,
where the game's pathfinder has a bug). Each step's record, with every remaining difference explained, is in
[`docs/native_campaign_build.md`](docs/native_campaign_build.md).

**Where it differs on purpose:** `patch_mask.dds` covers the whole map (BOB's misses a band in the north). Props go
into quadtree cells by their models' real bounds (BOB uses a 2 × 2 × 2 cube for any model not loose in the kit).
Region lookups can move regions past the 1024 palette entries the game reads. `full_height_map.dds` uses AtlasWH3's
own BC6H encoder, with about half the error of BOB's.

## What you need

- Windows 10 or 11, x64.
- Total War: WARHAMMER III and its Assembly Kit, with your map's Terry project in `raw_data\terrain\campaigns\<map>`.
- CAIME's files for the map in the kit's `working_data\campaign_maps\<map>`: `map.hex`, `map_data.esf`,
  `pathfinding.ppd` and the region lookup `.bmp`, as for BOB.
- Optional: your mod's `.pack`. Linked, it supplies the mod's DB tables, models and textures, and the build is merged
  into a copy of it.
- Optional: the map's devastated project, `<map without _1>_devastate_1` beside it (or named in the project), for the
  devastated pieces.

## Install

Download the zip from the [Releases](https://github.com/robert-d-schultz/AtlasWH3/releases) page and unzip it
anywhere. It needs no .NET install. Run `AtlasWH3.exe` for the app, or `AtlasWH3.Cli.exe` from a terminal.

The download is unsigned, so Windows SmartScreen may warn on the first start (More info › Run anyway).

## Quick start

**Back up first.** A new project builds into the kit's `working_data`, as BOB does, and Install replaces your mod's
pack in the game's `data` folder. Back up the pack, or give the project its own output folder (below).

### In the app

1. **Settings:** check the game and Assembly Kit folders (found automatically on a Steam install), and link your
   mod's pack.
2. **Start page:** pick the kit and the map.
3. **Build** (Ctrl+B): *New project for &lt;map&gt;…*, save it, then *Build all* (F7).

The Build window shows each step's progress, time and notes, and keeps a log of every build in
`%LocalAppData%\AtlasWH3\output\build_logs`.

### From the command line

```
AtlasWH3.Cli new-project iee.atlaswh3 --map cr_combi_expanded_map_1 --pack "D:\...\data\!cr_immortal_empires_expanded.pack"
AtlasWH3.Cli build --project iee.atlaswh3
```

- `--segments validate,compile,pack` builds and packs without installing.
- `--steps tile_list,global_map` compiles only those steps.
- `--json` prints the report as JSON.
- `AtlasWH3.Cli help` lists every command.

### The project file

A project (`.atlaswh3`, JSON) holds one map's build. This one, based on IEE's, compiles into a folder beside the
project, merges the result into a copy of the mod's pack there, and installs that copy. Settings left out keep their
defaults.

```json
{
  "name": "IEE",
  "map": "cr_combi_expanded_map_1",
  "modPacks": ["{game}\\!cr_immortal_empires_expanded.pack"],
  "build": {
    "output": "{project}\\out",
    "packs": [
      {
        "mode": "merge",
        "base": "{game}\\!cr_immortal_empires_expanded.pack",
        "output": "{project}\\!cr_immortal_empires_expanded.pack",
        "contents": [{ "source": "{compiled}" }]
      }
    ],
    "install": { "enabled": true }
  }
}
```

- **Segments:** a build runs Validate, Compile, Pack and Install, in that order, and stops at the first failure.
  `customSteps` adds your own commands before or after any of them (a startpos build, a script).
- **Tokens:** `{project}`, `{game}` (the data folder), `{ak}`, `{map}`, `{devastated}`, `{out}` and `{pack}` expand
  in paths. Relative paths are relative to the project file.
- **Pack:** `{compiled}` means the files this build wrote, nothing else from `working_data`. A merge keeps everything
  else in the base pack (DB, scripts, battle maps) and drops river models and pieces the build no longer makes. A map
  can ship in several packs; `exclude` and a content `path` split it (see
  [`docs/native_campaign_build.md`](docs/native_campaign_build.md#project-build-pack-and-install)).
- **Install** refuses while the game runs, swaps the pack in through a temporary file, and keeps one backup of the
  pack it replaces in `%LocalAppData%\AtlasWH3\output\pack_backups` (`"backup": false` turns that off).
- **Map settings:** `acceptTileMap` (tile-map check codes to build through), `patchMask` (`fitted`, or `vanilla` for
  BOB's), `lookupLast` (regions to move past palette entry 1024), `devastatedMap` (a name, or `none`).

## Limits

- `startpos.esf` is not built: it still comes from the game (an RPFM startpos build works as a custom step).
- CAIME's files are inputs, not outputs.
- The full build has been played in game on IEE. Other maps, Old World included, are checked against BOB's output.
- The Scene editor, Tile map and Campaign battles windows are a **preview**: they come from Atlas3K and are only
  partly moved to WH3.

The full list is in [`KNOWN_ISSUES.md`](KNOWN_ISSUES.md).

## Documentation

- [`docs/native_campaign_build.md`](docs/native_campaign_build.md): each step, what it reads and writes, and its
  parity with BOB.
- [`docs/end_to_end.md`](docs/end_to_end.md): the IEE build, from the kit into the game, and the in-game checks.
- [`docs/hlp_spd.md`](docs/hlp_spd.md): the AI pathfinding files.
- [`docs/bob_wh3.md`](docs/bob_wh3.md): what WH3's BOB runs headless, and what it reads and writes.
- [`docs/atlaswh3_plan.md`](docs/atlaswh3_plan.md): the plan, its decisions and phases.
- [`docs/atlas3k/`](docs/atlas3k/): Atlas3K's Three Kingdoms notes, kept for their method.
- [`research/`](research/): the scripts behind the parity work (Ghidra, Frida traces of BOB, byte-level diffs).

## Building from source

```
dotnet build AtlasWH3.slnx -c Release
dotnet test src/AtlasWH3.Tests --filter "FullyQualifiedName~BuildProjectTests"
powershell -ExecutionPolicy Bypass -File tools\publish.ps1
```

- **SDK:** .NET 10 (`global.json` takes any 10.0 feature band from 10.0.100).
- **Publish:** `publish.ps1` writes the self-contained download to `dist\AtlasWH3-<version>\` and zips it.
- **Tests:** the full suite takes about 16 minutes, so filter to the classes you touch. Tests that need the game or
  kit data skip when it is missing.

| Project | Contents |
|---|---|
| `AtlasWH3.Formats` | Readers and writers: packs, DB, Terry, map.hex, tiles, BMD v27, ESF, DDS / BC6H, RMV2 |
| `AtlasWH3.Core` | The build steps, projects, pack and install, the editors' model |
| `AtlasWH3.Cli` | `AtlasWH3.Cli.exe` |
| `AtlasWH3.App` | `AtlasWH3.exe` (WPF, Direct3D 11) |
| `AtlasWH3.Tests` | xUnit |

**Parity fixtures:** the data tests run on two maps, IEE (`cr_combi_expanded_map_1`) and Old World
(`cr_oldworld_map_1`). Freeze them right after a BOB run, so the tests keep comparing those sources with those BOB
outputs while the maps change:

```
AtlasWH3.Cli fixture-freeze iee --maps cr_combi_expanded_map_1,cr_combi_expanded_map_devastate_1 --packs !cr_immortal_empires_expanded.pack
AtlasWH3.Cli fixture-freeze oldworld --maps cr_oldworld_map_1,cr_oldworld_map_devastate_1 --packs !cr_oldworld_campaign.pack,!cr_oldworld_campaign_devastate.pack
AtlasWH3.Cli fixture-check
```

A fixture lives in `atlaswh3_fixtures` beside the kit, as hard links (no extra disk space). Without one, the tests
read the live kit. Add `--replace` to freeze a fixture again.

## Related tools

- [WH3_visual_map_decompiler](https://github.com/robert-d-schultz/WH3_visual_map_decompiler) does the reverse: it
  turns a compiled WH3 campaign map back into a Terry project. AtlasWH3 reuses its format readers, and the two
  together give a round trip.
- [CampaignMapToolkit (CAIME)](https://github.com/robert-d-schultz/CampaignMapToolkit) makes the campaign AI map files
  AtlasWH3 takes as input. Its code is under a non-commercial licence, so none of it is copied here.
- [RPFM](https://github.com/Frodo45127/rpfm): AtlasWH3 reads its `schema_wh3.ron` for DB layouts when it is
  installed.

## Credits and licence

AtlasWH3 is a fork of [Atlas3K](https://github.com/Ironictw2st/Atlas3K), the same tooling for Total War: THREE
KINGDOMS. The two games share an engine, and Atlas3K's pack, PNG, hex grid, tree and Terry code, and its method of
matching BOB byte for byte, are the base this was built on. Atlas3K's own README is
[at the fork point](https://github.com/Ironictw2st/Atlas3K/blob/cdd0a08fa0a07db3bc2e7faba6822a75d5512d91/README.md).

[MIT](LICENSE), as Atlas3K. The libraries it uses keep their own licences; see
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Not affiliated with Creative Assembly or SEGA. Total War: WARHAMMER III and its Assembly Kit are their property.
AtlasWH3 ships none of the game's data.
