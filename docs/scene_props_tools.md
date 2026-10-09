# Scene editor: adding props, clamping to the ground, unsaved edits

The scene editor's **Props** tab (next to *Inspector* and *Terrain & trees*) has two tools:
- an asset browser for placing new props;
- clamp-to-ground commands for props that float or are buried.

Code:
- UI: `src/Atlas3K.App/Scene/PropToolsPanel.cs` and `SceneWindow.Props.cs`.
- Logic: `src/Atlas3K.Core/Editing/PropPlacement.cs`, `GroundClamp.cs` and `GroundHeight.cs`.
- Tests: `src/Atlas3K.Tests/PropToolsTests.cs`.

## Adding props

1. Search the model list. It covers the game packs plus linked mod packs.
   - Words separated by spaces must all appear in the path.
   - Only `rigidmodels/campaign/` is listed unless *All models* is ticked.
   - A `.wsmodel` hides the bare `.rigid_model_v2` of the same name.
2. Select a model. The preview is a quick flat-shaded render of the lowest LOD. Below it you see:
   - the size and base y;
   - the triangle and LOD counts;
   - the pack the model comes from;
   - whether it is an LF-offset mountain.
3. Click **Place** (or double-click the model), then left-click on the terrain in the 2D top view or the 3D view. In 3D the click point is ray-marched onto the terrain.
4. With **Repeat** ticked you can keep clicking to place more. Esc or the button ends placement mode.

Each placement is one `create` op of type `Prop` in the active file layer, or in the folder layer selected in the tree:
- **Components:** the type's Terry components, in Terry's order: `ECPropMesh`, `ECMesh`, `ECMeshRenderSettings`, `ECPropHeightPatch`, `ECCampaignProperties`, `ECDLCMask`, `ECTransform` and `ECTerrainClamp`.
- **Model path:** `ECMesh.model_path` is written as Terry writes it (`RigidModels/campaign/…`).
- **Transform:** rotation is `0 yaw 0`, and scale is uniform.

It is written to the layer file straight away and undone with Ctrl+Z, like every other entity edit.

The y of a new prop is the ground at the click point (see *Ground* below), plus the y offset. With *Seat model base* ticked, the model's lowest point goes on the ground instead of its origin. LF-offset mountains (`rigid_detail_map_terrain_blend_with_LF_offset`) store y relative to the terrain, so they get y = offset.

## Clamp to ground

*Edit › Clamp selected to ground* (Ctrl+G), *Clamp all in active layer*, *Clamp all in view*, and the same buttons on the Props tab.
- **Scope:** only top-level props are clamped: entities with `ECPropMesh` and a model, not inside a group, not prefab instances. *Layer* and *view* skip hidden and frozen props. *View* means the 3D camera's view, or the 2D view's area.
- **Undo:** all moves are one batch of `set ECTransform.position` ops (x and z kept as stored), so one Ctrl+Z undoes the whole clamp.
- **Report:** the status line says how many props were lowered and raised, and the largest move.

**Modes**

| Mode | Stored y |
|---|---|
| Origin on the ground (default, as Terry) | ground |
| Model base on the ground | ground − scale_y · bounds_min_y |
| Vanilla sink (mountains / rocks) | ground + sink · scale_y, where sink is the model's typical depth; models without one fall back to *base* |

- **Offset:** added to every mode.
- **Only lower:** leaves buried props alone.
- **LF-offset mountains:** the ground term is (1 − share) · ground, because the shader adds share · lf itself. This is the same rule `MapAudit` uses.

**Vanilla sink.** Vanilla buries big mountain meshes deep so only the top shows. For example, `cold_ridge_small_1` sits about 67 model units × scale under the ground. The sink table is the per-model median of (y − ground) / scale_y:
- By default it is learnt from the open map's own mountains and rocks, for models with at least 3 instances. On a map built from vanilla this is vanilla's depth, the same method as `research/main190/vanilla_sink.py`.
- *Load sink table…* reads a JSON file instead. Both `{"model": depth}` and `vanilla_sink.py`'s `{"model": {"n": …, "sink": depth}}` work.

**Find floating props** selects props whose lowest point is more than the threshold above the ground (default 0.3). The lowest point is the origin or the model base, whichever is lower, so a tree's roots below its origin do not hide a lifted tree.
- Settlement pieces are skipped by default, because walls and roofs stand on platforms and on each other.
- On vanilla with the scene ground, 591 of about 16,400 non-settlement props are reported.

## Ground

| Source | What it is |
|---|---|
| **Scene height** (default) | the ground BOB stands WH3's trees on (`TreeHeightField`): the nearest full_logic_map texel, raised by the height patches of the layers' props (`apply_height_patch`). On IEE it gives BOB's tree heights (99% within 1e-3). |
| Built terrain only | the full_logic_map texel alone |
| Kit lf map | the kit's `LowFrequencyHeight` TIF, bilinear, with unbuilt edits (3K-shaped: WH3 projects have none until the editors' port) |

- **Why scene height is the default:** trees on mountains stand on the mountain *props'* height patches, not on the bare terrain. That makes scene height the ground the game shows.
- **Own patch ignored:** when a prop is clamped, its own height patch is ignored (the patch with the same model at the same x/z). Otherwise a building with a patch would be seated on its own top.

**Build needed.** Both built sources need the map's `full_logic_map.compressed_map` (step `heightmaps`). They try these in order:
1. the native build output (`output\compiled\<map>`);
2. the kit's `working_data`;
3. the game packs (the map's mod packs first).

The patches' models come from the packs (vanilla plus the linked mod packs). With a reference height from the project, each candidate is checked against it: if its median difference over a 24 × 24 grid is more than 0.5 (0.25 for the bare terrain), it is skipped as another map or an old build. When nothing matches, the kit lf is used and the Props tab says why. A built ground shows the **last build**: rebuild after height edits, or switch to the kit lf.

## Unsaved edits

- **Entity edits are never pending.** Every entity edit (inspector, gizmo, create, place, clamp …) is an EntityEditor batch. It is written to its `.layer`, `.terry` and `.terry.user` files at once and journaled in `output\entity_edits`. A value typed into the inspector is committed (focus is cleared) before the window closes.
- **Terrain & trees edits are held in memory until saved:**
  - While they are, the window title starts with `*`.
  - *File › Save terrain and tree edits* (Ctrl+S) saves them.
  - Closing the window, *File › Open Terry project…* and *Open campaign map* ask **Save / Don't save / Cancel**.
  - The title updates after every stroke, undo, redo and save.

Self-test (developer mode, on a **copy** of a project):
```
Atlas3K --scene <copy>\3k_dlc07_main_map.terry --props-selftest <out dir>
```
It checks:
- the model list and preview;
- placing in 2D and 3D on the ground;
- placement mode ending;
- Find floating;
- that a clamp is exactly one undo step;
- the title `*` when terrain edits are unsaved.

It writes `props_selftest.json` and a 3D screenshot.
