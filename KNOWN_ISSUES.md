# Known issues

- **Battle maps:** the Campaign battles window (catchment areas and redirects) is phase 1, and does not read WH3's
  `battle_locations_map.bin` yet. The battle terrain editor is cut.
- **Start positions are not built.** `startpos.esf` still comes from the game (RPFM's start-position build, for
  example). Add it as a custom step after Pack if you need it.
- **Tile list:**
  - The native `tile_list` is byte-identical to BOB's on the vanilla map. Heavily edited maps are less tested.
  - A map whose hex width is not a multiple of 4 reports `layout.mesh_columns`. Accept it in the profile to build
    anyway; BOB would also leave notches beside road tiles there.
- **Lookup textures:** the game reads only the first 1024 colours of a region lookup's palette. On a map with more
  regions than that (Old World), list the regions to move past entry 1024 in the build profile ("Lookup: regions past
  1024", CLI `--lookup-last`); the step names any region still past 1024.
- **AI pathfinding:** `hlp_data.esf` and `spd_data.esf` are built from the kit's `map_data.esf` and `pathfinding.ppd`
  (CAIME's), which the build does not write. Keep them the same as the pack's. On Old World one hlp value differs from
  the game's own build, where the game's A* has a bug; AtlasWH3 keeps the correct value.
- **Editors:** the scene, tile map and terrain editors are being moved to WH3 and are not finished (the tile map
  validator's rules, tile props and some textures are still 3K-shaped).
- **Camera height map:** the `camera_heightmap` step samples the logic heights and the layers' height patches the way
  BOB's action does, but not BOB's tile terrain, and it ignores `cam_hmap_apply_blur`. It is not byte-identical to
  anything (BOB's own action fails without the `cam_hmap_*` rules).
- **Global props:** `global_props.bin` matches BOB's content, not its bytes: BOB's own runs differ in record order
  and in the numbering of the mountain bodies. One difference remains, on purpose: props are put in quadtree cells by
  their models' real bounds. BOB treats any model whose `.wsmodel` isn't loose in the kit as a 2 × 2 × 2 cube, so a
  big prop can land in a cell smaller than itself.
- **Prepare game data** extracts one map at a time. Run it again for another map.
- Folder changes in Settings apply to windows opened after saving.
- The build runs while editors stay open. Saving an edit during a build can make the output mix old and new data;
  rebuild afterwards.
- The download is unsigned, so Windows SmartScreen may warn on the first start (More info › Run anyway).
- Windows 10/11 x64 only. The 3D view needs Direct3D 11.
