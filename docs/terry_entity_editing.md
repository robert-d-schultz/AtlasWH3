# Generic Terry entity editing

This is step 1 of the 1:1 Terry plan. Atlas3K can now read and edit **any** entity, component and field in **any** Terry project: the campaign map, battle prefabs (`raw_data/art/prefabs/**.terry`) and battle tile maps. Before this, only props could be edited.

## Where the schema comes from

Almost nothing here needed reverse engineering.

| Source | Gives | Code |
|---|---|---|
| `assembly_kit/working_data/Terry/entity_configuration.xml`, embedded as `Formats/Terry/Data/entity_configuration.xml` | The 55 entity types and their 86 component types, conditional components (`parameter="shape"` groups), `readonly` flags, and where each type is allowed (`allow_if`) | `EntityConfiguration` |
| Every `.layer` / `.terry` in the kit's `raw_data`: 3,864 files and 317,813 entities | Each component's fields in Terry's order, an inferred type (bool, int, float, vec2/3/4, colour, enum, id, path, string), defaults and known values, and the component layout of each entity type | `ComponentSchema`, generated into `Formats/Terry/Data/component_schema.json` by `Atlas3K.Cli terry-schema` |

Defaults:
- When a field has only a few distinct values in the kit, the default is the most common one (e.g. campaign lights use `colour_scale` 100000).
- Fields that vary per instance get neutral values instead.
- A small known-defaults table covers the cases where neutral would be wrong: `ECTransform.scale` is `1 1 1`, decal `tiling` is 1.

**Not covered yet:** 31 configured components never appear in the kit, so their fields are unknown. They are mostly battle components: catchment areas, cameras, splines, units, spot lights and water meshes. A Ghidra pass on `tweak_terrainmetadataeditor.modder.x64.dll` and `tweakshared.modder.x64.dll` would fill them in, along with enum lists and defaults.

Built-in types not listed in the configuration:

| Type | Components |
|---|---|
| `LayerFile` | ECLayerFile or the older ECFileLayer |
| `TagLayer` | ECLayer + ECLayerExportTags |
| `Layer` | ECLayer only: a plain folder |
| `Group` | ECGroup, with nested entities |
| `Signature` | ECSignature |

## File fidelity

`TerryXml` writes Terry's exact layout. All 3,869 parseable `.terry`, `.terry.user` and `.layer` files in the kit round-trip byte for byte. Two things vary by Terry version and are kept per file:
- the line ending (LF or CRLF);
- the form of an empty element: `<x/>`, `<x></x>`, or `<x>` and `</x>` on two lines.

One CA prefab layer, `ea_villa_s_04.161fff49c5220a2.layer`, is malformed XML. Edits keep the rest of a file byte-identical:
- New components copy the attribute set and order already used for that component in the same file, because Terry versions differ in both.
- Empty association blocks that were already in the file are left alone.

## Model

| Class | What it does |
|---|---|
| `LayerDocument` (generic part in `LayerDocument.Generic.cs`) | `ReadEntities` / `Read`, `SetField` (validated against the schema; unknown fields are refused, never-seen enum values give a warning), `SetName`, `AddComponent` / `RemoveComponent`, `CreateEntity(type)`, `CreateLayer` (folder or tag layer), `SetParent` (Logical associations), `DeleteEntity` (a layer's members move up, or are deleted with `withMembers`), `DetachEntity` / `ImportEntity` (move between files, keeping ids). |
| `TerryProject` | Project type (`tile_map`, `prefab`, `tile`), database (`campaign`, `battle`), the scene's layer list, and adding, renaming, removing and setting export on file layers. |
| `TerryUserFile` | Per-user state in `.terry.user`: hidden and frozen entities, and the active layer. The kit has no non-empty id list, so the separator is unconfirmed; reads accept `,` `;` and space, writes use `,`. |
| `Core/Editing/EntityEditor` | Op batches across layers, the `.terry` and the `.terry.user`, with one `FileJournal` undo step per batch. The journal now also records files a batch creates or deletes. For the campaign map the journal is `output/prop_edits/<map>`, shared with `PropEditor`; other projects use `output/entity_edits/<project>_<hash>`. |

## CLI and MCP

CLI commands, all with optional `--project <.terry>`:
- reading: `entity-types`, `entity-type <T>`, `entity-project`, `entity-layers [--all]`, `entity-query`, `entity-get`
- editing and history: `entity-edit --ops`, `entity-undo`, `entity-checkpoint`, `entity-rollback`, `entity-history`

MCP tools in `tools/terry_mcp/server.py`:
- reading: `list_entity_types`, `describe_entity_type`, `terry_project_info`, `list_layers`, `query_entities`, `get_entities`
- editing: `edit_entities`, `set_entity_fields`, `bulk_set_entities`, `create_entity`, `delete_entities`, `move_entities_to_layer`
- layers: `create_layer`, `set_layer_state`
- history: `entity_checkpoint`, `entity_rollback`, `entity_undo`, `entity_history`

Op list, also documented in `edit_entities`: `set`, `add_component`, `remove_component`, `create`, `delete`, `move_to_layer`, `set_parent`, `create_layer`, `rename`, `layer_state`, `delete_layer`. Ops target `id`, `ids` or a `query`; a query makes the op a bulk find-and-replace.

Terry's rules are enforced. For example, `create Building` in a campaign tile_map fails because `allow_if` doesn't permit it.

## Verified

- On a copy of the 3k_dlc07 project, a mixed batch changed exactly the five expected files with minimal diffs. The batch set fields, created a light, created a file layer plus a prop, moved an entity across files, toggled layer visibility and lock, and bulk-set a query.
- A second batch renamed a layer, deleted a file layer, added and removed components, reparented an entity, and deleted a tag layer with its members.
- Undoing either batch restored all 267 files byte for byte, including deleting the created layer file and restoring the deleted one.
- The same works on a battle prefab, with its own journal.
- Tests: `TerrySchemaTests`, `EntityEditingTests` (kit round-trip and editor undo); the full suite passes 101/101.

## Scene editor (step 2)

The scene editor is a Terry-style window for any `.terry` project. It is in `src/Atlas3K.App/Scene/`.

To open it:
- In the main window: **File → Open Terry scene editor (campaign map)** or **File → Open Terry project (.terry)…**
- From the command line: `Atlas3K.App --scene [project.terry]`

### Layout

| Part | What it does |
|---|---|
| **Layer tree** (`LayerTreePanel`) | Lists file layers with entity counts. Folder and tag layers, groups and entities inside them load when you expand a node, at most 400 children per node, so the campaign map's ~70k entities stay fast. Layers have a visibility checkbox and a lock toggle. A filter bar on top narrows the tree by name (layer or entity name, label, id), region (part of the layer name), entity type and saved visibility; parents of matches stay, and filtering never changes visibility. **Show all**, **Hide all** and **Show only filtered** set many layers at once as one undo step and leave locked layers alone. |
| **Scene view** (`SceneView`) | A top-down view: x to the right, z up. Campaign projects get the shaded height TIF behind them; battle projects get a world grid. Entities show as markers coloured by type. Polylines, river splines, rectangles and circles are drawn through each entity's transform. Hidden layers are not drawn; locked entities are dimmed and cannot be picked. |
| **Inspector** (`InspectorPanel`) | Generated from the component schema (see the field editors below). |
| **Find box** | Free text (name, asset, id), `type:Prop`, `layer:<name>`, or field filters such as `ECMesh.model_path~metasequoia`. **Select all results** turns any query into a bulk edit in the inspector. |

Layer tree context menu:
- on layers: new folder layer, new tag layer, add entity here, rename, set active, toggle export, select all, delete (members move up, or are deleted with them);
- on entities: rename, duplicate, frame in view, delete.

Dragging entities onto a layer moves them there: within the same file the entity is reparented, otherwise it moves to the other layer file.

Scene view mouse controls:

| Input | Action |
|---|---|
| Click | Pick an entity |
| Ctrl+click / Shift+click | Add to / toggle in the selection |
| Drag on empty ground | Box-select |
| Drag a selected entity | Move the whole selection |
| Right or middle drag | Pan |
| Wheel | Zoom |

Inspector field editors:

| Field type | Editor |
|---|---|
| bool | Checkbox |
| enum | Dropdown of the values the kit uses |
| vector | One box per component |
| colour | RGBA boxes with a swatch |
| path / string | Editable dropdown of the values used in this project |

- Fields the component lacks but the schema knows can be set; they are added when written.
- With several entities selected, only shared components are shown. Mixed values show blank, and a blank vector component keeps each entity's own value, so "set every y to 0" works.
- Components can be added (from the type's configuration and the layouts seen in the kit) and removed. Read-only components, and components fixed by the project type, cannot be removed.

### Keys

| Key | Action |
|---|---|
| Ctrl+Z | Undo the last edit batch |
| Ctrl+D | Duplicate |
| Del | Delete |
| Ctrl+N | Add entity at the view centre, on the ground |
| F | Frame the selection |
| Home | Fit all |
| F5 | Reload files changed by the CLI or MCP tools |
| Ctrl+O | Open another project |

The Edit menu also has checkpoint, roll back and history.

### How edits are saved

Every change is an `EntityEditor` op batch. It is validated, written to disk at once, and journalled, so the app, the `entity-*` CLI and the MCP tools share one history. The window re-reads only the layers a batch touched. Values Terry could not read (for example text in a number field) are refused, the error is shown in red, and the inspector reverts.

`duplicate` is now also an entity op, in the CLI, MCP and app.

### Self-test

`Atlas3K.App --scene <copy>.terry --selftest <out dir>` drives the window in-process. It does not send mouse or keyboard input to the desktop.

It runs search, select, a scalar edit, a single-component vector edit, a refused invalid value, drag-move, duplicate, create, rename, a new folder layer plus dropping entities into it, multi-select, hiding a layer, and selecting a whole layer. It then undoes everything and checks that the project files are byte-identical. It writes screenshots and `selftest.json`.

It passed on a copy of 3k_dlc07 (9 edits undone, 268 files identical) and on a battle prefab. **Only run it on a copy of a project.**

### Terrain & trees tab

The right column has a second tab, **Terrain & trees**. It edits the kit sources of a campaign project directly: the `.terry`'s `LowFrequencyHeight` (land), `LowFrequencyHeightSea` (sea surface, half resolution) and `CampaignTree` TIFs. Because these are the sources, edits survive every rebuild.

- **Tool:** Off (select / move entities as usual), Land height, Sea height, or Trees. While a tool is chosen, left-drag in the 2D view paints. Right or middle drag still pans.
- **Height brushes:**
  - Modes: Raise, Lower, Smooth, Flatten (towards the value where the stroke started), Set to value, Noise.
  - Settings: radius in world units (`[` / `]` resize it), strength and softness (cosine falloff).
  - *Sea level* sets the value to 14219, the vanilla open-sea surface.
  - The sea target paints at the sea raster's own resolution. *Show water* tints where the sea surface is above the land, coloured by water level.
- **Trees:** one tree colour per hex, 2×2 px per hex, the palette being the sorted `campaign_tree_ids` colours (index 19 = no tree).
  - Modes:
    - Paint species and Erase.
    - Fill connected: every hex reachable through the clicked hex's species.
    - Fill region: every hex of the clicked map.hex region.
  - Optional fill limit (world units).
  - Restrict: any hex, only empty hexes, or only hexes that already have trees.
  - The species list shows each colour's tree ids and its hex count.
  - *Show tree map* draws the map over the 2D view.
- **Alt+click** picks the height value or the species under the cursor. The panel reads out the land and sea raw values, their world heights, and the hex and species under the cursor.
- **Live view:** the 2D view redraws while you paint. When a stroke ends:
  - the trees of the touched hexes are regenerated with BOB's placement (`CampaignTreeGenerator`);
  - height strokes also move the trees on them;
  - the 3D view re-meshes the terrain and water. It keeps the ground textures and re-uploads into the same height texture.
- **Undo / redo:** each stroke or fill is one step (the Undo / Redo buttons, or Ctrl+Z / Ctrl+Y while the tab is shown). These steps are in memory, separate from the entity journal.
- **Save:**
  - Writes only the changed TIFs.
  - Land also updates `lf_heights.tif` and sea updates `lf_sea_heights.tif` when they exist at the same size.
  - Writing is through a `FileJournal` in `output\terrain_edits\<map>` (a copied project gets `custom_<hash>`), which backs up the originals first.
  - Uncompressed TIFs (the height maps) are patched in place, so only pixel bytes change and the Photoshop header and metadata stay byte for byte. Compressed ones (the LZW tree map) are rewritten with the same compression, rows per strip and palette.
  - An unchanged save is byte-identical for the height maps and for tree maps written by Atlas3K. CA's original LZW tree map comes back pixel-identical but not byte-identical, because the encoder differs.
  - Save refuses files changed on disk since they were loaded, unless you confirm.
  - Switching project or closing with unsaved edits asks first.
- **Stale build warning:** the tab warns when the default build output (`output\compiled\<map>`) is older than the saved sources. It names the steps to re-run: rasters, tile_list, global_mesh and camera_heightmap for heights, and trees. *Build…* opens the Build window.

Code: `Core/Editing/KitTerrainEditSession.cs` (session, `KitHeightBrush`, hex helpers), `TiffMap.SaveGray16Like` / `SavePalette8Like`, `App/Scene/TerrainToolsPanel.cs`, and the `SceneView.ITool` hook. Tests: `KitTerrainEditTests` cover TIF round trips (hand-made Photoshop-like files in both byte orders, and the vanilla and 190E kit TIFs), brush math, hex↔pixel mapping, fills, save, dirty tracking and mirrors.

Self-test: `Atlas3K.App --scene <copy>.terry --terrain-selftest <out dir>`. It runs land raise, undo and redo, trees following the ground, sea set-to-value, tree paint, fill and erase, then save. It checks that the TIFs hold the edited rasters and that only pixel bytes changed. It passed on a copy of vanilla 3k_dlc07. **It saves into the project, so only run it on a copy.**

### Not done yet

- Editing child elements: polyline and spline points, prefab overrides.
- Moving whole nested layers by drag.
- Redo. The journal only undoes.
- Rendering models: footprints come in step 5, the 3D view after it.

## Prefabs (step 3)

### What a prefab is

- A prefab is a Terry project, `<key>.terry`, plus its layers.
- An instance is a `Prefab` entity whose `ECPrefab key` names that file.
- `<override id=…>` blocks inside `ECPrefab` change component fields of one inner entity, the one whose id matches.
- Libraries come from Terry's `configuration.xml`:

| Database | Library folder | In the kit |
|---|---|---|
| battle | `raw_data/art/prefabs/battle` | 1,302 prefabs; `ea_villa_m_01` and `wo_l_tea_field_07` each exist twice, and the first path in ordinal order wins |
| campaign | `raw_data/art/prefabs/campaign` | 33 prefabs (WH3's kit; the folder configuration.xml names is used only when it exists) |

### Code (`Formats/Terry/`)

| Class | What it does |
|---|---|
| `TerryTransform` | ECTransform maths in the convention the build uses (Blender XYZ Euler, `R = Rz·Ry·Rx`, `world = p + R·S·local`, the same as `CameraHeightmapStep.Matrix`). `Compose` places a child under a parent: yaw-only pairs add angles exactly, anything else goes through the matrix product and back to Euler angles and scale. Tests check it against the matrix product. |
| `PrefabLibrary` | The key → `.terry` index, plus each prefab's entities and their meta tags. |
| `PrefabExpander` | Turns an instance into world-space copies of its entities: transforms composed, overrides applied, nested prefabs expanded (`recursive`), meta tags carried. Missing keys are reported, not thrown. |
| `LayerDocument.ExpandPrefab` | Replaces an instance with its copies. Copies get fresh ids; references between them, such as a building's `capture_location`, are remapped. They go into a folder layer named after the prefab, or into tag layers when tags apply. |

### Editor ops (CLI, MCP and app)

| Op | What it does |
|---|---|
| `place_prefab {key, layer, position, rotation, scale}` | Places an instance. |
| `expand_prefab {ids, recursive, into_folder}` | Breaks instances apart. |
| `make_prefab {ids, key, folder, replace}` | Writes `<library>/<folder>/<key>.terry` and a Default layer, centred on the selection's x/z middle at its lowest y. With `replace`, the originals become one instance of it. The new files are part of the undo batch. |

CLI: `prefab-list [--filter]` and `prefab-get <key>` (counts, nested keys, bounds, assets).
MCP: `list_prefabs`, `describe_prefab`, `place_prefab`, `expand_prefabs`, `make_prefab`.

### Build

The native `global_props` step flattens campaign prefab instances into `global_props.bin`, because the game has no campaign prefab files for an instance to reference. Before this, instances were silently dropped. A test checks that a layer using a prefab builds byte-identical `global_props` bodies to the same layer expanded by hand. `camera_heightmap` reads `global_props.bin`, so it follows along.

### App

- Instances show what is inside them: the prefab's entity positions, its outlines, and its footprint box.
- **Create → Prefab…** (Ctrl+P) opens a browser with a filter, entity counts and the footprint size; the prefab is placed at the view centre.
- **Edit → Expand selected prefabs** (Ctrl+E) and **Edit → Make prefab from selection…**
- The inspector shows a prefab block with **Open prefab** (opens it in a new scene window) and **Expand**, plus an override count.
- The tree context menu has **Place prefab here** on layers, and **Open / Expand prefab** on instances.

### Tests

- `PrefabTests` (10): transform convention, Compose against the matrix product, nesting / overrides / tags, fresh ids, place → expand → make → expand-back with undo on a sandbox kit, and build flattening.
- The scene self-test now places, expands and makes a prefab, then undoes all of it; the prefab library ends byte-identical. It only runs `make_prefab` against a non-default kit (`--ak <sandbox>`).

### Open

- Override editing in the inspector (overrides are listed and applied, but not edited there).
- `LivePrefab`: only 2 in the kit; read as a plain entity.
- No check that a prefab key is unique when two libraries are merged.

## Game assets (step 4)

The asset pipeline reads models, materials and textures straight from the game packs. It's used for previews, model footprints and, next, the 3D viewport.

### Sources (`Formats/Packs/AssetSource`)

- Lookup order, highest first: loose folders (`--asset-root`), then mod packs (`--mod-pack`), then the vanilla packs in manifest order.
- No vanilla 3K pack has compressed entries or an encrypted index; the game predates pack compression. So the existing PFH4/5 reader is enough.

### RMV2 (`Formats/Models/RigidModel`)

A full reader for versions 6–8: every LOD and mesh, positions/normals/uvs, triangles, and from each material its name, shader, textures, attachments and alpha mode. It is ported from `Z:\Claude\Animations\tw3k_animforge\crates\af-formats\src\rmv2.rs` and extended with:

| Case | Handling |
|---|---|
| River meshes (vertex format 13) | Decoded using the layout our own `RiverBuilder` writes. |
| Decal stubs (material 100) and material 49 | These carry a 256-byte path and no vertices. The game builds the quad itself (unit box, textures at `<path>_base_colour.dds`), and `ModelLibrary` draws exactly that. |
| Battle vegetation LOD counts with flags in the high half | The flag bits are ignored. |
| Unknown vertex layouts | Positions only, accepted only if every vertex lies inside the mesh header's bounding box. |
| Anything unreadable | `HeaderBounds` still returns the mesh headers' bounding box, so a placeholder can be drawn. |

### Census and campaign coverage

- `asset-census --ext .rigid_model_v2`: 9,480 of 12,222 vanilla RMV2 files read. The remaining failures are battle-only:
  - tile terrain meshes (material 96): `terrain/tiles/battle` and `terrain/tiles/campaign` tile pieces;
  - battle SpeedTree-style trees (materials 74/75);
  - 2 UI models and 1 test file.
- **On the campaign map** (`asset-check`): 452 of the 453 models used by 63,765 entities load, with no missing materials or textures. The exception is `water_lily_2` (vertex format 12, material 97, 35 uses); its layout is still unknown, so it gets a header-bounds box.

### Models, materials, textures

- `WsModelFile` and `MaterialFile` parse `.wsmodel` (geometry plus a material per part and LOD, falling back to lower LODs) and `.material` (shader, textures by slot, params). Base colour comes from `s_xml_base_colour` or `s_xml_diffuse`; alpha test from an alpha shader or `alpha_on` in the name.
- `DdsTexture.DecodeMip` decodes the largest mip within a size limit. BC1 from DX10 headers now keeps its punch-through alpha (leaf cards).

### Core

- `ModelLibrary` (`Core/Assets/ModelLibrary.cs`) gives cached, thread-safe `RenderModel`s: LODs of meshes with their colour texture and alpha flag, bounds, and a list of problems (missing material or texture). Textures are cached per mip size.
- `ModelPreview` (`Core/Rendering/ModelPreview.cs`) is a CPU renderer: orthographic camera at yaw/pitch, z-buffer, textured, alpha-tested, Lambert-lit, PNG output.

### CLI and MCP

CLI:

| Command | What it does |
|---|---|
| `asset-info <path>` | Model, `.material` or `.dds`: source, LODs, meshes, textures, problems. |
| `asset-find <filter> [--ext]` | Finds asset paths. |
| `asset-preview <model> [--out --yaw --pitch --size --lod --untextured]` | Renders a model to PNG. |
| `asset-census --ext …` | Parses every asset of a kind and reports failures. |
| `asset-check [--project]` | Loads every model a project uses. |

MCP: `find_assets`, `inspect_asset`, `preview_asset` (returns the image), `check_project_assets`.

### App

- The scene view draws every prop's model footprint (LOD 0 bounds placed by its transform) once zoomed in. Bounds load in the background after the project opens.
- The inspector shows a rendered thumbnail of the selected entity's model, with triangle count, LODs, size and any problems.
- The self-test now checks footprints and the thumbnail (`09_model.png`).

### Tests

`AssetTests` (7): wsmodel and material parsing; RMV2 positions agree with the older geometry reader; library resolution for a tree, a hall and a decal; DDS mips and BC1 alpha; header bounds; preview coverage; loose-file overrides. Full suite: 118/118.

### Open

- Battle `.cs2.parsed` buildings (`ECBuilding` keys go through `models_building_tables`).
- Battle tile meshes (material 96) and SpeedTree trees (materials 74/75).
- Vertex format 12.
- Composite scenes (`.csc`).
- Model handedness (whether RMV2 x matches world x) is still to be confirmed against the game in the 3D viewport.

## 3D viewport (step 5)

The scene editor's centre is now two tabs: **Top (2D)** and **3D** (Ctrl+1 and Ctrl+2). Both share the selection and the edit ops. The code is in `src/Atlas3K.App/Viewport3D/`, using Vortice.Windows 3.8.3 (Direct3D11, DXGI, D3DCompiler).

### Code

| Class | What it does |
|---|---|
| `D3DHost` | A child Win32 window (`HwndHost`) for the swap chain. Its window procedure forwards mouse, wheel and keys in device pixels, since WPF does not route input to hosted windows. |
| `Renderer3D` | Device (hardware, WARP fallback), runtime-compiled HLSL, states, swap chain, offscreen render plus readback. Shaders: instanced lit and textured meshes with alpha test and a selection/frozen tint, height-tinted terrain, coloured lines, and translucent fills (water). Mesh and texture creation is free-threaded; texture uploads and mip generation run on the render thread. |
| `Camera3D` | Orbit camera, left-handed with world axes as they are (x east, y up, z north): no axis flip. Picking rays and screen projection come from the camera basis in double precision. Inverting the float view-projection was off by about 0.025 units at campaign coordinates, which skewed the rotate gizmo by about 5°. |
| `Viewport3DControl` | Draws the scene (see below) and handles picking, gizmos and input. |

What the viewport draws:
- Campaign terrain, as a mesh from the height TIF at most 2048 vertices across, plus a sea plane at y = 0. Battle projects get a grid instead.
- Every entity's model, through `ModelLibrary`. Models load on worker threads (6 at a time) and are instanced per (model, LOD); each instance is frustum-culled and its LOD picked by distance using the RMV2 LOD distances.
- Entities without a model as markers, which are hidden more than 120 units away unless selected.
- Undrawable models as their bounding box.
- Shapes as outlines.
- Selected models with a yellow tint and box.

### Picking

Ray against each model box in model space, refined to the nearest LOD 0 triangle (Möller–Trumbore). Markers are picked as spheres.

### Gizmos

| Key | Gizmo | Snap with Ctrl |
|---|---|---|
| W | Move: axis arrows, or the ground-plane square | 0.5 |
| E | Rotate about the vertical axis; positions orbit the selection's pivot, yaw changes to match | 15° |
| R | Uniform scale by dragging up/down; multi-selections scale about the pivot | 0.1 |

A drag previews live and commits one `set ECTransform` batch on mouse-up (undoable).

### Controls

| Input | Action |
|---|---|
| Click / Ctrl+click / Shift+click | Pick / add / toggle |
| Drag on empty | Box-select |
| Right-drag (or Alt+left) | Orbit |
| W A S D Q E while orbiting | Fly; Shift is faster |
| Middle-drag | Pan |
| Wheel | Zoom |
| F / Home | Frame the selection / everything |

Edit keys (Ctrl+Z, Ctrl+D, Del, Ctrl+N, Ctrl+P, Ctrl+E, Esc, F5) work inside the 3D view too. New entities and prefabs go to the 3D camera's target when the 3D tab is active.

### Verified (self-test, RTX 4070 SUPER)

- The campaign map renders: terrain, river meshes, lakes, forests, mountain props, and settlement models with textures.
- **Wall segments and towers join end to end.** That supports the transform convention and model handedness from step 4 (no mirroring).
- The self-test checks:
  - picking an isolated tree at its box centre;
  - the move gizmo moving along the ground plane with y unchanged;
  - the rotate gizmo turning exactly −90.0°;
  - undo of all edits back to byte-identical files.
- Screenshots: `output/previews/scene_selftest_campaign/10_3d_selected.png`, `11_3d_overview.png`, `12_3d_close.png`.
- The battle prefab project opens in 3D (grid, markers, outlines) and passes.

### Open

- Battle buildings (`.cs2.parsed`) are not drawn as meshes yet (markers only).
- No shadows, fog or seasons.
- The rotate gizmo only does yaw; there are no per-axis scale handles.
- No undo of camera moves.

## Textured terrain (3D)

The 3D terrain now uses the campaign's own ground textures, like the game and the 2D campaign editor.

### How it works

| Piece | Source |
|---|---|
| Ground group per lf pixel | The project's `BlendCampaign` TIF (8-bit palette; pixel value = group, `texture_arrays.xml` order; 7136×5620 on 3k_dlc07), uploaded as an `R8_UInt` texture. |
| Ground textures | `texture_arrays.xml` (searched in kit `working_data`, then the vanilla folder, then the packs): each group's base colour decoded at 512 px through `ModelLibrary`, resampled if needed, uploaded as one `Texture2DArray` with GPU-generated mips. Missing groups get the 2D renderer's flat colour. |

The terrain pixel shader:
- finds the lf pixel under each fragment (row 0 = north);
- loads the four neighbouring group indices;
- samples each group's texture with world-space UVs, repeating every 48 lf pixels (about 4 world units, as in the 2D view);
- blends the four bilinearly, so group borders are soft;
- applies the Lambert light.

Projects without a blend map, and battle projects, keep the height tint. The sea plane is darker and more opaque, so it still reads as sea over the textured seabed.

### Verified

The self-test step `3d terrain` loads 32 groups on the 7136×5620 map. Screenshots: `11_3d_overview.png` (deserts, steppe, farmland and forest mosaics, beaches) and `12_3d_close.png` (grass, flowers, sand patches under the props).

### Open

- No normal or roughness maps (`texture_arrays` has them).
- The compiled `global_blend.dds` weights are not used.
- The 2D top view is still height-tinted.

## Prefab contents in 3D

Prefab instances in the 3D view draw what is inside them, as the game shows them.

- **Parts:** `SceneModel.PartsOf(key)` expands a prefab once per key, nested prefabs included, into parts: each part's type, model path, and transform in prefab space. Model bounds for those parts are loaded in the background with the scene's own.
- **Drawing:** each frame, every part is drawn at `instance transform ∘ part transform` (`TerryTransform.Compose`). Gizmo drags and edits carry the whole prefab live. The prefab is frustum-culled once as a whole; its parts are LOD-picked and instanced together with ordinary props. Parts without a model (buildings, capture locations, markers) show as small markers near the camera, and shapes inside the prefab as outlines.
- **One entity:** parts belong to the instance. Clicking a part's triangles picks the instance, and the selection tint and box cover everything inside. The box is the union of the part boxes, which framing (F) and box picking also use.

**Verified** with the sandbox-kit self-test. It runs `--ak <sandbox kit>`, because `make_prefab` writes into the kit's prefab folder.
1. 8 real props (walls, platform, rocks, a decal) become a campaign prefab.
2. The instance draws all 8 parts, and a click at its centre picks the instance.
3. The move gizmo moves every part together. Screenshots: `13_3d_prefab.png`, `14_3d_prefab_moved.png`.
4. Undo removes the new prefab files and restores all 269 project files byte for byte.

**Open:**
- Instance overrides (field changes on inner entities) are applied on expand and in the build, but do not change what the 3D view draws.
- Battle prefabs, whose buildings are `.cs2.parsed`, draw only their non-building parts.

## Campaign map, complete (forests, water, rivers, composite scenes, seasons)

The goal is to load the campaign map 1:1: everything the project contains, drawn in the 3D view.

| Content | Source | How it is drawn |
|---|---|---|
| Props (63,713) | layer entities, `ModelLibrary` | instanced models, LOD by distance |
| Decals (52) | decal stubs | textured quads (flat at the entity's height, not projected) |
| Forests (205,767 trees, 50 types) | `CampaignTree` TIF | `CampaignTreeGenerator` (BOB's byte-exact placement: one tree per hex, seeded jitter, 60° rotations) with heights from the lf raster; the model for each tree id and season comes from `campaign_tree_variants` |
| Sea and lakes | `LowFrequencyHeightSea` TIF | translucent surface at the sea height wherever it is above the land (every 4th sea pixel) |
| Rivers (24) | River entity splines | the native `RiverBuilder` mesh, the same one the build writes, on our terrain; rebuilt when the scene changes |
| Composite scenes (2,573 entities, 41 `.csc` files) | `.csc` model references | the models drawn at the entity transform (cows, pigs, deer, farmers, lookouts). All 41 resolve. Offsets and animation inside multi-element scenes are not decoded |
| Terrain | height and blend TIFs | textured with the 32 ground groups |
| VFX, lights, sounds, probes | layer entities | markers |
| Shapes (no-go regions, splines, …) | layer entities | outlines |

**Season preview** in the 3D toolbar (All, spring, summer, harvest, autumn, winter; summer by default). The campaign layers hold every season's version of props, such as harvest and summer trees, as separate props at the same spot. Like Terry's season setting, props whose `season_mask` excludes the season are hidden in both views, and trees switch to that season's models. **Trees** and **Water** can be toggled.

**Performance.** Each entity caches its resolved model, instance rows and bounding sphere. Tree instances are precomputed per season. Each model keeps per-LOD instance lists that are reused every frame, and all instances go up in one buffer per frame with ranged draws. The worst case (whole map in view, about 250k instances) went from 138 to 44 ms of CPU per frame; close views are much cheaper.

**Verified** (self-test on a sandbox copy of 3k_dlc07):

| Check | Result |
|---|---|
| `3d campaign world` | 205,767 trees, sea map 3568×2810, rivers built, 41/41 composite scenes with models |
| `3d frame time` | 44 ms CPU for the whole map |
| Undo of all edits | still byte-identical |

Screenshots: `11_3d_overview.png` (forests and coastline), `15_3d_forest.png`, `16_3d_scene.png`, `17_3d_river.png`.

**Still different from the game:**
- VFX, lights and sounds are markers: no particles and no dynamic lights.
- Decals are not projected onto the terrain.
- Multi-element `.csc` scenes are drawn without their internal offsets or animation.
- `water_lily_2` (35 uses) is drawn as a box.
- There are no shadows or normal maps.

## Fauna scale, tile overlay, viewport input

**Fauna scale.** Fauna composite scenes (cows, pigs, deer, cranes, pandas, horses) use real-world-size models; a cow is 3.5 units long. Each `.csc` scales its model: the root element's first tagged float pair (tag 0x0A plus f32) reads `(1, s)`, with s = 0.1 for fauna and 1 for the living-campaign figures, which are modelled at campaign scale. `SceneModel.CompositeScale` reads that pair. Scenes without it (the horses) use 0.1 when their model is under `/fauna/`.

**Tile overlay** (the "Tiles" selector above the views: Off / Features / All):
- **Source:** the map's compiled `tile_list.bin` (kit `working_data`, then the vanilla folder, then the packs) read against the campaign tile database (`TileMapValidator.LoadDatabase`).
- **Placement:** each record's tile-map rectangle has y = 0 at the south row, with w and h swapped for the 0x20 and 0x80 orientations. Cell size = world size / the grid in the tile_list header.
- **Display:** coloured by tile set (roads brown, imperial roads gold, rivers blue, crossings purple, canals cyan, coast sand, mountains grey, cliffs red, lakes light blue, farms green). In 2D as fills; in 3D as translucent quads draped on the lf terrain. Features hide the base tiles (generic, sea, coast, mountains). Hovering shows the tile's name, set, rotation, size and climate.
- **Verified:** 195,490 tiles (13,560 features) on vanilla 3k_dlc07. Cliff tiles trace the coastline and river tiles follow the valleys. Screenshots: `18_3d_tiles.png`, `19_2d_tiles.png`.
- **Caveat:** `tile_list.bin` is a build output, so after editing `tile_map.png` the overlay shows the last build until the map is rebuilt.

**Viewport input fix.** The 3D view's child window is a Win32 static control, which answers `WM_NCHITTEST` with `HTTRANSPARENT`, so every mouse message went to the parent and the 3D view could not be moved. It now answers `HTCLIENT` (and `MA_ACTIVATE`). The self-test step `3d input` sends real window messages to the viewport (hit test, right-drag orbit, wheel), so this is now covered.

## Tile content in 3D (cliffs, mountains, bridges, rivers)

The campaign's game look mostly comes from the placed tiles, not the Terry layers. The 3D view now draws every tile's own content from `tile_list.bin` (toolbar toggle **Tile meshes**). On vanilla 3k_dlc07 that is 121,550 instances of 105 models, plus the tile rivers.

**Tile transform.** This comes from warscape `TERRAIN_RENDER_SETUP::get_tile_transform` (decompiled in `research/bob_re/tile_instance/180364c80_get_tile_transform.c`; callers in `research/bob_re/tile_transform_callers/`).
- Tile space is 128 units per tile-map cell. The tile's south-west corner is at (X·128, Y·128), with x east and z north.
- Orientation (& 0xF0; the 0x04 bit is ignored, as the engine masks it) maps the tile point (a, b) to:
  - 0x10: (a, b)
  - 0x20: (b, W−a)
  - 0x40: (W−a, H−b)
  - 0x80: (H−b, a)
  - W×H is the tile size before the turn.
- World x and z = tile space × cell size / 128, with non-square cells on the campaign. y uses the x scale.

**Frames per file.** Each was checked against the data, not guessed.

| file | frame | evidence |
| --- | --- | --- |
| `mesh` / `river_mesh` | 128 per cell, z 0..H | land mesh spans exactly 0..W·128 × 0..H·128 |
| `custom_mesh` (cliffs) | 128 per cell, z −H..0 → b = z + H | cliff-tile water-splash VFX sit 4–10 units from a cliff-foot vertex (20–50 for the 180° alternative); mask test 77% vs 1–52% for the mirrors |
| `bmd_data.bin` props | 32 per cell, b = z | 94% of props on valid subtiles (85% mirrored, ≤1% shifted) |

**Heights.**
- *Cliffs:* the cliff top is at the record's high lf height. high − low equals the mesh depth (770 units × scale ≈ 1.98 world).
- *Props:* y = 0 is the tile's own ground. The tile `hf_height_map.compressed_map` is flat on mountain tiles and spans at most −2..0.5 bmd units on rivers and roads (≤ 0.02 world). So each prop sits on the lf at its own position, plus its y, whatever its height mode (`BHM_ABSOLUTE`, `BHM_CUSTOM_VERTEX_OFFSETTING`, `BHM_TERRAIN`).
- *Rivers:* the tile river meshes are baked into one coloured mesh with every vertex snapped to the lf, because the lf has no carved channels to hold one water level per tile.

**Reader.** `GlobalProps.ReadBody(bytes)` reads a single FASTBIN0 body; tile bmds share the global_props layout. `PropRecord.HeightMode` is now exposed. On the river tiles' `SST_RIVER` sound emitters (an unknown layout) the read stops but keeps the props read before them, which adds the bridges and crossing props of 95 tiles. Season variants (`_summer`, `_winter`…) follow the previewed season, summer when "all".

**Verified** (self-test screenshots 20–25):
- cliffs inside their footprints at every orientation;
- karst pillars with their trees (subtropical) and rock walls (temperate);
- bridges on the crossing tiles;
- rivers joining from tile to tile and passing under the bridges.

Overview frame: 63 ms CPU, up from 44 ms. Test `TileBmd_ReadsMountainAndCrossingProps`.

**Open:**
- Tile blend0/normal textures (the per-tile ground detail) are not drawn.
- The tile `hf` relief is not added to the lf.
- Tile VFX (water splashes) and the river material (a flat water tint is used) are not drawn.
- Cliff side UVs look streaked.

## Tile ground textures, forest scale (mountains)

**Tile ground.** The game doesn't bake tile ground into `global_map/global_blend.dds`: byte 0 equals the BlendCampaign group raster everywhere and byte 1 is 0–3. Each tile's `blend0.dds` (512² DXT5) is applied on top at render time.
- *Texture set:* each tile variation has eight u16 strings after a u32 2: `TileVariation.TextureLayers`, captured as `TextureSetBytes` by the tile reader.
- *Slot → channel:* slot k drives blend0 channel k (R, G, B, A). Over all 1,015 vanilla tiles, the channels a blend0 uses are the named slots. Slots 4–7 are unused on the campaign.
- *Slot meaning:* `climate` (or empty) is the global ground there; other names are texture_arrays groups (`arid_1`, `imperial_road`…). For example, imperial road = climate / imperial_road (surface) / arid_1 (verges), and river = climate / arid_1 (bed and banks).
- *Image orientation:* the image's u runs along tile a and row 0 is the tile's south edge (v = b). Sampled at the river tiles' river-mesh vertices, the non-climate weight averages 124/255 that way against 77–92 for the other seven flips and transposes.

**Drawing.** `BuildTileGround` bakes a lf-draped grid per placement: two quads per cell side, through `TileMatrix`, normals from the lf. Grids are grouped per tile folder, each with its blend0 at 256 px. `Renderer3D.DrawTileGround` (`TilePS`) mixes the non-climate layers from the ground texture array and alpha-blends by their share, so the climate share stays the terrain below. There is no depth write and a 0.004 lift. On vanilla 3k_dlc07 this covers 15,077 tiles. Roads run continuously across tiles and over the bridges; river banks follow the river meshes.

**Forest scale (why the mountains were hidden).** Campaign tree instances carry no scale. The tree models are real-world size (castanopsis 2.4 units tall); the same models placed by the mountain tiles' bmds come out at 10.7 × 4 × cell/128 ≈ 0.11, and fauna scenes use 0.1. At scale 1 the canopies covered several cells and buried every hill, so forest trees are now drawn at `TreeScale` = 0.1. That reveals the lf relief and the tile mountain models. Screenshots: `26_3d_mountains.png`, `27_3d_mountains_close.png`, `11_3d_overview.png`.

**Fly speed.** The frame-time cap went from 0.1 s to 0.25 s, so slow frames (big campaign scenes while loading) no longer slow the camera down.

**Open:**
- The tile `normal.dds` is not used.
- Ground textures are always the summer set.
- Mountain tiles' `custom_alpha_blend_texture` (an edge fade mask) is not used.

## Tile normal maps and seasons

**Tile normal maps.** Each tile's `normal.dds` is 256² DXT5nm: x in alpha and y in green; R is 255; rows south-first like blend0.
- *Axes:* x runs along the tile's a axis and y along b. Against the gradients of the tile's own `hf_height_map`, x ~ −dh/da and y ~ −dh/db (net sign agreement 22 and 38 over 56 river and road tiles; the north-first readings disagree).
- *Vertex data:* the tile-ground vertices now carry the world xz of the tile's a and b axes (12 floats). `TilePS` tilts the lf normal by the map's x and y along them, so the shading follows the tile's turn.
- *Climate ground:* `TilePS` now computes the climate ground itself (`ClimateColour`, the same bilinear group blend as the terrain). It therefore draws opaque, and also covers tiles whose texture set is all "climate", so their normal maps show.

**Seasons** (the season selector, 3D):
- *Ground textures:* `SeasonalTexture` swaps the texture_arrays path's season token (`temperate_1_autumn_base_colour` → `_winter_`…) when that file exists. Harvest falls back to autumn. Groups with one texture for every season (arid, steppe, roads, farms…) and "all seasons" keep the listed file. The array rebuilds in the background on change (`EnsureSeason`).
- *Snow:* `terrain/textures/campaign/default/snow/3k_campaign_map_[season_]snowmask.dds` is L8 at one texel per tile-map cell, row 0 north (the DDS reader now decodes 8-bit luminance). It blends `campaign_snow_base_colour` over terrain and tile ground (strength 1.5). The mask is only used when its size matches the map's tile grid, so maps of another size get no snow rather than misplaced snow. Winter: 49% of the mask is above 20/255, summer and autumn 4%.
- *Trees and tile props:* these already follow the season.

**Verified:** the self-test step `3d seasons` switches to winter and back. Screenshots: `28_3d_winter.png`, `29_3d_winter_overview.png` (northern China under snow, the south green), `30_3d_summer_overview.png`.

## Zoom toward the cursor

**The problem.** The wheel scaled the orbit distance (×1/1.2 per notch, minimum 0.05), so zooming crept toward a fixed pivot. After flying with WASD the pivot keeps its old height, so it is often underground or off screen, and zooming "stopped" short of what was under the cursor.

**The fix.** `ZoomAt` moves the eye a sixth of the way to the lf ground under the cursor per notch. `GroundAlong` ray-marches against `GroundY` and bisects; without terrain it uses the y = 0 plane. The view direction is kept, and the pivot is re-set to where the view centre meets the ground, so orbiting afterwards turns around what you are looking at. With no ground hit (sky), the wheel dollies along the view; zooming out mirrors zooming in.

**Verified.** Self-test step `3d zoom to cursor`: 60 real `WM_MOUSEWHEEL` messages at the viewport centre bring the eye from 381 to 0.017 above the campaign ground, and from 12 to 0.014 on a prefab.

## Mod packs (`--pack`)

`--pack <file.pack>` (repeatable, highest priority first; `ProjectPaths.ModPacks`) adds mod packs to the asset source, searched before the vanilla packs. For the map's compiled files (`tile_list.bin`, `global_map/texture_arrays.xml`) these packs win over the kit's working_data and the vanilla folder. The tile overlay's hover note names the pack the tiles came from.

190E example: `--ak "...\assembly_kit_190E" --map 3k_190e_expanded_map --pack "...\data\!!190_expanded_region_test_main190_native.pack" --scene "...\3k_190e_expanded_map.terry"`. That loads 383,451 tiles, 238,806 tile meshes/props and 26,363 tiles with their own ground. The vanilla snow masks are skipped there because the map size differs.

Self-test: the pick/gizmo/prefab steps now report a failure instead of aborting, so the world, tile and season steps still run on projects laid out unlike dlc07.

## Hidden layers, region mask and cities

**Show hidden layers.** On 190E the "missing mountains" were in hidden layers: the project's `.terry.user` lists 266 of its 404 layers as invisible, including the main mountain and rock prop layers (one has 621 mountain/rock props). Editors skip hidden layers, but the game still exports them. The **Show hidden layers** checkbox (`SceneModel.ShowHidden`, `IsDrawn`) draws them in 2D and 3D without touching the saved visibility.

Also checked on 190E:
- All 481 prop models resolve (vanilla plus the native pack) and load, except one empty water lily.
- The ~90% of mountain tile placements with no bmd (1×1–4×4, 11×11 cold/temperate/tea) are genuinely empty: flat hf, flat mesh, no props. Their relief is in the lf.

**Region mask.** Turned on with the **Regions** checkbox.
- *Source:* the kit's `EmpireDesignData/campaign_maps/<map>/map.hex` plus the map's `campaign_map_playable_areas` bounds, through `HexRegionLookup` (the hex maths the native build uses).
- *Image:* `SceneModel.Regions` builds a world image (3 px per hex column, ≤ 4096 wide, row 0 north). It has one golden-ratio hue per land region at 35% opacity and dark borders at 90%; sea is left clear.
- *3D:* the terrain and tile-ground shaders blend it in (`Overlaid`, t7).
- *2D:* drawn over the map.
- *Hover* (both views) adds "region <key>".

**Cities.** `MapHexFile.SlotAt` reads byte 2 bits 4–7 as slot + 1. A region's city is the centre of its slot-0 hexes (`HexRegionLookup.HexCentre`, the exact inverse of `HexAt`: 17,261 of 17,261 round-trip). On 190E: 340 land regions, 339 cities, every centroid inside its own region. Drawn as gold poles in 3D, and in 2D as dots with the region key (game prefix dropped) once 120 or fewer are in view.

**Verified.** Self-test step `region mask` checks that a city reads back as its own region. Screenshots: `31_3d_regions.png`, `32_3d_region_city.png`, `33_2d_regions.png`.

## Region names in 3D; buried mountains on 190E

**Region names in 3D.** The 3D view is a native D3D window, so WPF can't draw over it. `EnsureLabelAtlas` renders every city's region name once with WPF (Segoe UI 13 semibold, white on a translucent dark box) into a 1024-wide atlas, converted from premultiplied BGRA. Each frame, `DrawCityLabels` places them as screen-space sprites (`Renderer3D.DrawSprites`, `SpriteVS/PS`) beside the city poles: nearest first, at most 150, skipping any that would overlap a nearer label. Screenshot: `31_3d_regions.png`.

**Mountains draped on the terrain (the "missing" mountain at 0ea79483739c373 on 190E).**

*Shader.* Campaign mountain models use `shaders/rigid_detail_map_terrain_blend_with_LF_offset` with `adjust_model_to_terrain = 1`. These are the cold peaks and ridges and the subtropical mountains, including the tiles' `BHM_CUSTOM_VERTEX_OFFSETTING` rocks. Its vertex shader (`..._vs_vs40_main.fxc`, disassembled with `Z:\Claude\Shaders	ools	wfxc.py disasm`) reads `t_low_res_height_map` at each vertex's world x/z (bilinear, `g_lf_height_range` → × `g_height_map_lf_scale` − `g_height_map_vertical_offset`). It then adds `sp_adjust_model_to_terrain` × that height to the vertex's world y. So a mountain's stored y is an offset above the ground and the mesh drapes over it.

*Evidence on vanilla.* Rock y tracks the lf (correlation 0.965) while mountain y barely does (0.167). On 190E the ridge `0ea3ba16cb6301a` is stored at y 1.23 with the ground at 9.1 (the game's global land mesh agrees: 8.98). It shows in game because the shader adds the 9.1.

*Viewer.*
- `MaterialFile.TerrainOffset` → `RenderMesh.TerrainOffset` → `GpuMesh.TerrainOffset`.
- `MeshVS` adds `flags.z` × the lf height from `Renderer3D.SetTerrainHeight` (R16, ≤ 4096 px, t8).
- Culling centres, selection boxes and picking add the same lift at the model origin (`DrapeLift`).
- Tile props with these models no longer get the ground added on the CPU.
- Self-test shot: `34_3d_mountain_drape.png`.

*Retraction.* An earlier version of this section reported 590 "buried" mountains on 190E and proposed lifting them. That was wrong; nothing was changed in the kit, and the prepared ops were deleted.

**Open:** a few models and tree specks render black in some views (texture issue, not yet diagnosed).

**Floating mountains on 190E.** With the drape rule, a mountain floats when its stored offset plus its scaled lowest vertex is above zero. In vanilla, 95% of the 502 LF-offset mountains sit at least 0.13 under the terrain and only 6 are more than 0.3 above. 190E has 58 more than 0.3 above (list: `output/floating_mountains.csv`, with the offset that would sink each base 15% of its height):
- 8 are ridges sitting on another mountain (likely intentional);
- 26 are cold peaks in the dense north-west ranges (x < 100, z 530–585), floating 1.5–5.1;
- 24 are elsewhere. These include `0e657e9f7621a92` (+3.18) and `0ea3ba16cb6301a` (+0.93), both reported floating in game and both visibly floating in the 3D view.

**Shots mode.** `Atlas3K.App ... --shots <csv with id,x,z[,footprint_radius]> <outdir>` saves a 3D close-up per row and exits without editing. Close-ups are in `output/previews/floating`, with contact sheets `_sheet1.png` and `_sheet2.png`.
