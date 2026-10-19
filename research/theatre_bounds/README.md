# Theatre bounds (campaign_map_playable_areas minx) — 2026-10-19

Question: can the logical map be offset from the visual one by giving the theatre a non-zero min x?

## Where min x lives

`map_data.esf` stores the bounds three times, (min x, min z) then (max x, max z), all written by CAIME from the same
playable-areas row:

| Record | Next to |
|---|---|
| `MAP_DATA > THEATRES_AND_REGIONS_FOR_UI > THEATRE` | the playable-areas `index` string, REGION_KEYS |
| `MAP_DATA > theatres > CAMPAIGN_THEATRE` | the `index` string, HEX_MAP_DATA |
| `MAP_DATA > REGIONS_DATA > MASKED_REGIONS_DATA` | REGIONS_BLOCK, REGION_AREA_INDEX_OVERRIDE |

Every vanilla row has minx 0, and so does every shipped map_data.esf (vanilla combi/chaos, IEE, Old World). The
playable-areas row has minx/maxx only, no z.

Warhammer3.exe: `map_data.esf` loader 0x1414706d8; its CAMPAIGN_THEATRE reader 0x141479eb0 stores min at +0x24,
max at +0x2c, hex width/height at +0x34/+0x38 and the hex array at +0x40 of the map-data object. MASKED_REGIONS_DATA
(0x141479c0c) goes to 0x50-byte region-set elements (`[obj+0x58]`, min at +0x20, max at +0x28). The UI copy is read
by 0x14147a290 from 0x142924c2c.

## The freeze with min x = 200 (Old World, map_data.esf patched, DB untouched)

`sample.py` on the hung game: one thread at 100% in 0x1429368dc, a scanline polygon fill, called once at campaign
load from 0x1429436a4 (thunk 0x1429368cc) with the shape `[owner+0x78]`. The owner is the campaign model (its
constructor 0x142926fc4 also reads `start_pos_regions_rebel_faction_name_`, `REGION_SANITATION`). The shape is a
map_data.esf region: its rectangle and the u16 988 at +0xb8 are REGIONS_BLOCK[988], `cr_oldworld_region_sea_maw`
(REGION_DATA's two vec2 values; not in the startpos, which has none of those floats).

0x1427c5068 clamps the shape's rectangle to a grid (min +0x64, max +0x6c, cols/rows +0x74/+0x78): the near edge to
>= 0, the far edge to <= cols − 1, but the far edge is not clamped below 0. The fill's loops end on `jne` against
those counts. Read live (`live.py`): the shape's rectangle is x 31.6–55.9, z 487.9–511.4; the snapped rectangle
starts at exactly 200.0 (the grid starts at the theatre's min x); cols = −285, rows = 54; row 5's column counter was
at 695,565,953, counting to the u32 wrap. So: a shape wholly below the grid's min x (or z) hangs the game instead of
being skipped. With min 0 that needs a shape at negative coordinates.

What that shows: the game does take the theatre's min as the grid origin, and the patch was incomplete. map_data.esf
also holds world-unit vec2 values that CAIME computes from the hexes with the theatre min (sea_maw's box is hexes
(48, 633)–(83, 662) at min 0), and the patch left them at min 0:

| Record | vec2 values (Old World) | |
|---|---|---|
| REGION_AREAS > REGION_AREA_DATA | 16,258 | each area's bounding box |
| REGIONS_BLOCK > REGION_DATA | 2,930 | each region's bounding box (1,465 regions) |
| THEATRE > REGION_KEYS | 1,317 | one position per UI region key |
| the three theatre bounds | 6 | |

A pure shift moves all of them and max x by the same amount (min alone also changes the hex size, which scales with
max − min). Startpos character positions are world units too (`hlp_spd/sp_chars.py`).

## Tools

- `probe.py` / `probe.js`: Frida watchpoints on min x. Hangs the game (traps in a per-hex loop starve the agent);
  not usable as is.
- `sample.py`: suspends each thread briefly and reads rip and the stack's code addresses; no debugger, no patches.

## Moving the terrain instead (2026-10-19)

The display coordinates the min-x shift would move include hardcoded campaign cameras (vanilla
`main_warhammer_faction_intro.lua`, 220 `cam_*` tables) and quest battle markers (`missions.location_x/y`, integer
display coordinates), so the logical map stays at min 0. Moving the terrain west of 0 instead: tile_list.bin's
`origin_offset` (−100, 0) changed nothing in game. In campaign mode TERRAIN_DIMENSIONS::init (modder DLL 0x1805b9fe0)
fixes the world origin at (0, 0), stretches the tile map to the world width passed in and sets the height map's
bounds to (0, 0)–(width, H·width/W); the header's origin fields are never read (see `TileList`'s format comment).
