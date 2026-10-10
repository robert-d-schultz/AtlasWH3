# Known issues (0.1.0-alpha.2)

- **Battle maps:** the Campaign battles window (catchment areas and redirects) is phase 1. The battle terrain editor
  is experimental: it still builds through BOB and needs the battle tile database, which first-run setup does not
  extract yet.
- **Start positions are not built.** `startpos.esf` still comes from the game (RPFM's start-position build, for
  example). Add it as a custom step after Pack if you need it.
- **Tile list:**
  - The native `tile_list` is byte-identical to BOB's on the vanilla map. Heavily edited maps are less tested.
  - A map whose hex width is not a multiple of 4 reports `layout.mesh_columns`. Accept it in the profile to build
    anyway; BOB would also leave notches beside road tiles there.
- **Lookup textures:** the game reads only the first 1024 colours of a region lookup's palette, and the `lookup` step
  writes BOB's palette order (first appearance). On a map with more than 1024 regions some regions get no colour.
  Reorder the palette afterwards (Old World uses a script) until the step does it.
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
