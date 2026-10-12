# Terry battle components: fields from the decompile

This is the prerequisite for the Phase 2 battle map editor (`docs/atlas3k/battle_editor_plan.md`): what each Terry battle
entity component stores, so Atlas3K can write deployment zones, capture points and playable areas the way Terry
does.

**Result:**
- Every battle component's field list, type, default, range and enum values now come from Terry's own constructors.
- Wherever the kit has data, the corpus confirms them attribute for attribute.
- `ECPlayableArea`, `ECBattlefieldZone`, `ECCameraZone` and the other "marker" components store **no fields at all**. Their shape is the entity's ECPolyline / ECRectangle / ECCircle.

## How Terry stores a component's fields

Terry's components (`QTU::EC*` in `qttoolutility.modder.x64.dll`, a few `Terry::EC*` in
`tweak_terrainmetadataeditor.modder.x64.dll`) are QObjects, but their fields are **not** Qt properties: the
meta-objects carry no `Q_PROPERTY` entries (`qmeta_extract.py`).

Instead, each default constructor builds `UTILITYDLL` property objects (`PROPERTY_BOOL`, `PROPERTY_RANGED_INT`, `PROPERTY_RANGED_FLOAT`, `PROPERTY_STRING`, `PROPERTY_VECTOR2`, `PropertyEnum`, `PropertyEnumFlags`, ...). Each takes:
- a display name ("Flag Facing Direction");
- a default;
- for ranged types, a min and max;
- for enums, an `IEnumReflection`.

The constructor then registers them with `EntityComponent::add_property` in a fixed order. `EntityComponent::serialize` calls `serialize_properties` on that set.

**Attribute rule** (checked on every component with data, below): the XML attribute is the display name in lower case
with spaces turned into `_`, in `add_property` order. A name that would start with a digit gets a leading `_`
(`3D View Render` → `_3d_view_render`, ECTapeMeasure).

**Enums:** the reflection object's vftable slot 2 lists the written names in index order (`QString::fromAscii_helper("CLIT_MAJOR")` ...). Slot 3 lists the UI labels and the enum type name. `PropertyEnum`'s second argument is the default index.

**String fields** may carry a `CUSTOM_TYPE_ATTRIBUTE`. That is the name of the value list Terry offers, usually a DB table:

| Custom type | Values |
|---|---|
| CaptureLocationType | `capture_point_types` keys |
| CivilianType | `battlefield_civilian_groups` |
| CivilianAmount | `battlefield_civilian_amounts` |
| BuildingSlotGroup | `building_slot_groups` |
| BuildingKey | `battlefield_buildings` |
| CaptureLocationRef | an entity id |

**Legacy attributes:** two attributes in the corpus are no longer registered by any constructor, so current Terry neither reads nor writes them:
- `ECBattleProperties.allow_in_outfield` (1,398 of 4,062 instances);
- `ECBuilding.allow_in_outfield_as_prop`.

## Components

Sources:
- **ctor:** the qttoolutility default constructor (address).
- **tweak:** tweak_terrainmetadataeditor.
- **corpus:** where the attribute list was seen in Terry-written files: the vanilla kit's `raw_data` (3,864 files) plus the user battle projects in the 190E, map, `__MAP` and defunc kits.

### Battle objects

| Component | Fields (type, default, range / values) | Source | Corpus |
|---|---|---|---|
| ECCaptureLocation | `importance` enum CLIT_MAJOR / CLIT_MINOR (default MAJOR); `type` string, CaptureLocationType (capture_point, gate, tower, tower_neutral, tower_neutral_hidden, victory, victory_neutral); `flag_position` vec2; `min_players` int 2 [0, 100]; `max_players` int 8 [0, 100]; `flag_facing_direction` float 0 [0, 360]; `destroy_building_on_capture` bool false | ctor 1800a7350 | 52 in 17 files, same 7 attributes in the same order |
| ECDeploymentZone | `facing_direction` float 0 [0, 360]; `deployment_type` enum DZT_1V1 ... DZT_4V4 (16, `DZT_<a>V<b>`); `deployment_category` enum DZC_DEFAULT, AMBUSH, RIVER, COAST, ENCAMPMENT; `deployment_zone_id` int 0 [0, 3]; `alliance_id` int 0 [0, 3]; `configuration` int 0 | ctor 1800a8c40 | 34 in 18 files, exact match |
| ECDeploymentZoneRegion | `region_type` enum DZRT_ADDITIVE, SUBTRACTIVE, GUERRILLA_EXCLUSION_ADDITIVE, GUERRILLA_EXCLUSION_SUBTRACTIVE | ctor 1800a9480 | 36, exact |
| ECPlayableArea | none (marker) | ctor 1800ca4d0 | 2: `<ECPlayableArea/>` |
| ECPlayerDeploymentLocations | `deployment_locations` enum flags North, South, East, West (comma list, all by default) | ctor 1800932d0 | 2, exact |
| ECBuilding | `key` string (BuildingKey); `damage` float 0 [0, 100] %; `indestructible`, `toggleable` bool false; `capture_location` string (entity id); `export_as_prop`, `visible_beyond_outfield` bool false; `height_map_modification` enum Null, Minimum, Maximum, Additive, Subtractive, Absolute (default Maximum) | ctor 180094ae0 | 24,879, plus the legacy `allow_in_outfield_as_prop` |
| ECWall | `weak_wall` false, `ai_breachable` false, `dockable` true | ctor 18010b120 | 11, exact |
| ECSiegeAINode | `siege_ai_node_type` enum SANT_AREA, ENTRY, FIRING_AREA, INTERSECTION, WALL_AREA | ctor 1800ff950 | 5, exact |
| ECSiegeAIEdge | `siege_ai_edge_type` enum SAET_AREA_CONNECTION, FIRING_AREA_LINK, PALISADE, STREET, WALL (default STREET) | ctor 1800ffad0 | 2, exact |
| ECSiegeAIBoundary, ECBattlefieldZone, ECCameraZone, ECCameraBounds, ECLiteBuildingOutline, ECGoRegion, ECNoGoRegion, ECCivilianShelter, ECBuildingProjectileEmitter | none (markers; the shape is the entity's ECPolyline / ECRectangle) | copy constructors only, no properties | NoGo 192, Go 12, Boundary 9, Emitter 106, Shelter 16: all attribute-free |
| ECTerryBattlefieldZone | `locked` bool false; `rank_distance` float 3 [0, 100]; `zone_skirt_distance` float 3 [0, 100] | tweak 180052390 | 1, exact |
| ECBattleProperties | `visible_beyond_outfield` bool false | ctor 18009fc00 | 4,074, plus the legacy `allow_in_outfield` |
| ECProceduralExclusionZone2 | `exclude_vegetation`, `exclude_grass` bool true | ctor 1800927c0 | 216, exact |
| ECCivilianDeployment | `type` string (CivilianType); `amount` string (CivilianAmount) | ctor 180092370 | 16, exact |
| ECBuildingSlot | `slot_id` int 0; `group` string (BuildingSlotGroup) | ctor 180095850 | 2 (190E / map kit test maps), exact |
| ECBuildingSlotPreview | `level` string (BuildingSlotLevel) | tweak 180051f30 | none |
| ECBuildingDestructionLevel | `damage_threshold` float 0 [0, 100] | ctor 180095c70 | none |
| ECLineOfSight | `active` bool true; `range` float 0 [0, 5000] | tweak 180051d20 | none |
| ECCamera | `vertical_fov` float 60 [0, 179]; `aspect_ratio` 1; `near_clip` 1; `far_clip` 30000 | ctor 1800a4ae0 | none |
| ECUnit | `unit` string (UnitName); `width` float 0 [0, 1000]; `count` int 0 [0, 1000]; `script_name` string. A `Category` enum is built but not registered | ctor 180108520 | none |
| ECAIHint / ECAISeparator / ECEFLine | `type` AIH_* (20) / `type` PALT_NONE, GENERIC, FORT / `formation_purpose` EFP_* (28) | ctors 180092070, 1800921f0, 180092640 | AIHint 4, exact |

### Campaign-battle catchments

| Component | Fields |
|---|---|
| ECBattleCatchmentArea | `type` string (CampaignBattleTypeMask); `redirect_to` string (BattleAndCatchment); `ambient_light_environments` string (EnvironmentFileList); `culture` string (Culture); `procedural` bool false |
| ECBattleCatchmentAreaInTile | `type`, `culture` (same custom types) |
| ECBattleCatchmentAreaBoundary | none |

These match `Z:\Claude\BattleMaps\docs\bob-battle-terrain-sources.md`. ECBattleCatchmentArea has its own exported `deserialize`, which also reads an older `catchment_type` attribute for layer versions below 0x17.

The full table (every component, with UI labels, enum type names and custom types) is written by `build_schema.py` to `battle_components.json`. It stays local: research keeps scripts and notes only. The field lists, defaults and enum values are in Atlas3K's `component_schema.json` and in the tables above.

## The entities these components make

The component layouts come from `component_schema.json` EntityTemplates. Associations come from the default deployment prefabs.

- **PlayableArea:**
  - Components: `ECPlayableArea`, `ECPlayerDeploymentLocations`, `ECTransform`, `ECTransform2D`, `ECRectangle width height`.
  - The rectangle is the area. `deployment_locations` is the set of edges the player may deploy on.
- **DeploymentZone:**
  - The entity holds only `ECDeploymentZone`. Its regions are separate **DeploymentZoneRegion** entities:
    - components: `ECDeploymentZoneRegion`, `ECTransform`, `ECTransform2D`, and a closed `ECPolyline` (or `ECRectangle`);
    - linked from the zone by a `<Logical>` association (`<from id=zone><to id=region/>`).
  - The layer entity is associated to the zones the same way.
- **CaptureLocation:** `ECCaptureLocation`, `ECTransform`, `ECTransform2D`, plus `ECCircle radius is_semicircle` (30 of 52) or a closed `ECPolyline` (22).
  - Buildings point at it through `ECBuilding.capture_location` (the capture location's entity id). In prefabs this goes through `LayerDocument.ExpandPrefab`'s id remap.
- **BattlefieldZone:** `ECBattlefieldZone`, `ECTerryBattlefieldZone`, `ECTransform`, `ECTransform2D`, `ECPolyline`.

## Compiled side

- **BMD playable area** (`bmd_data.bin` / `global_props.bin` BMD_OBJECTS):
  - It is the 23 bytes after the projectile-emitter list (`BmdBody.EmittersAndPlayableArea`, 6 + 23): u16 version 2, AABB_2 (min x, min y, max x, max y as f32), u8 `has_been_set`, u32 `valid_locations`.
  - Evidence:
    - The empireutility `PLAYABLE_AREA` API: `aabb`, `has_been_set`, `valid_player_locations`, `set_valid_locations(VALID_LOCATIONS_FLAGS)`.
    - All 8,891 campaign bodies carry `0200 | 64 64 1920 1920 | 00 | 00000000`, an unset default.
  - Not yet checked against a battle tile with a set area.
- **BMD battle containers:** empireutility has BMD containers for `CAPTURE_LOCATION` (position VECTOR_2, radius, CAPTURE_LOCATION_TYPE, building indices), `CAPTURE_LOCATION_LIST` / `SET` and `DEPLOYMENT_ZONE` → `DEPLOYMENT_ZONE_REGION` (boundaries, facing VECTOR_2, id, snap_facing). `BATTLE_MAP_DEFINITION` exposes `playable_area()`, `capture_location_list()` and `deployment_area(alliance, zone, …)`.
  - Their byte layouts are not decoded yet: next step for the Phase 2 build.
- **`terrain/deployment/{north,south,east,west}.xml`** (`BATTLE_DEPLOYMENT_AREA_HASH_TABLE`, read by `EMPIREUTILITY::DEPRECATED::BATTLE_DEPLOYMENT_AREA_MANAGER::load_from_xml`):

  | XML | Terry field |
  |---|---|
  | `ALLIANCE id` | `alliance_id` |
  | `deployment_area id` | `deployment_zone_id` |
  | `category DAC_*` | `deployment_category DZC_*` (same order) |
  | `boundary type` "standard additive" / "standard subtractive" / "guerrilla exclusion additive" / "guerrilla exclusion subtractive" | `region_type DZRT_*` (same order) |
  | `position x y` | the region polyline in world space |

  These four files have the same structure as the default deployment prefabs (`art/prefabs/battle/logic/default_deployment/deploy_land_normal_1024x1024_<dir>`): alliances 0 and 1, one additive region each. Whether the coordinates are an exact export of those prefabs is not checked.

  The Terry exporter strings in tweak_terrainmetadataeditor show the prefix renames it applies when writing battle XML: `DZT_` → `EDZT_`, `DZC_` → `DAC_`, `DZRT_` → `DZPT_`, and the typo `SUBTRACTIVE` → `SUBSTRACTIVE`. Capture locations are written as `CaptureLocationPolygonInstance` with `polygon_shape` PS_CIRCLE / PS_SEMICIRCLE / PS_POLYGON / PS_RECTANGLE, `database_type`, `min_num_players`, `max_num_players`, `flag_facing`.

## What this unblocks (Phase 2)

- **Deployment zones:** create a zone (alliance, zone id, type, category, facing) and its additive or subtractive region polygons, linked by Logical associations, with every enum value Terry offers.
- **Capture points:** a circle, semicircle or polygon capture location with importance, type (from `capture_point_types`), player range, flag position and facing; buildings link to it with `capture_location`.
- **Playable area:** a rectangle entity with `ECPlayableArea` + `ECPlayerDeploymentLocations`. Its compiled form (23 bytes) is known.
- **Also covered:** siege AI nodes, edges and boundaries, walls, civilians, battlefield zones and building slots.

## Still unknown

- The byte layouts of the BMD capture-location and deployment-zone containers, which the native build needs: decompile `BMD_OBJECTS` serialise in empireutility. The editor can write `.layer` files without them, and BOB compiles those.
- The ECMarker `type` enum. Its reflection is a static table object (0x1808ba3b0) whose names are not in literal strings. It is editor-only.
- ECTileAlignment / ECTileCellAlignment (Terry-side): no constructor properties were found by the string search. They are probably markers, but this is not confirmed.
- Which deployment/capture fields BOB validates when it exports a battle (e.g. whether `deployment_zone_id` must be unique per alliance).

## Files

| File | What |
|---|---|
| `qmeta_extract.py` | decodes Qt meta-objects from a DLL; showed that components have no Q_PROPERTY fields |
| `enum_reflections.py` | resolves an `IEnumReflection` getter or static object to its value names and labels |
| `ctor_fields.py` | parses the Ghidra constructor decompiles into fields (display name, kind, default, range, enum, custom type) |
| `check_corpus.py` | compares the decompiled fields with Terry-written .layer/.terry files (order, values) |
| `build_schema.py` | writes `battle_components.json` and the additive `component_schema.json` entries (`Source: "decompile"`) |
| `battle_components.json` (generated, not committed) | the derived field table (86 components, 152 fields) |

To reproduce:
1. Decompile the constructors with `DecompileMatching.java` (`Z:\Claude\Tools\ghidra_scripts`) on the analysed `qttoolutility.modder.x64.dll`, name regex `QTU::EC[A-Za-z0-9]+::EC[A-Za-z0-9]+$`.
2. Run `ctor_fields.py <dir> --out ctor_fields.json`, then `check_corpus.py`, then `build_schema.py --write-schema`.

The decompiles are CA's code and stay outside this repo (`Z:\Claude\BattleMaps\research\bob_re\qttoolutility_battle_components`, `…\tweak_terrainmetadataeditor_battle_components`).
