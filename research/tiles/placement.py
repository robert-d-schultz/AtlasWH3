"""Prototype of BOB's campaign tile placement (WARSCAPE::EDITOR_TILE_MAP::build_tile_map).
See docs/atlas3k/bob_re_tile_placement.md. Usage: python placement.py [out.pkl]
"""
import os, sys, time, pickle
import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(__file__))
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..'))
import tiledb
import msvc_sort
import compressed_map as CM

ROOT = 'Z:/Claude/TerryClone'
AK = 'C:/Program Files (x86)/Steam/steamapps/common/Total War THREE KINGDOMS/assembly_kit/raw_data/terrain/campaigns/3k_dlc07_main_map/'
INVALID = -1
ROTS = (0x10, 0x20, 0x40, 0x80)
M64 = (1 << 64) - 1


def rotl(v, r):
    return ((v << r) | (v >> (64 - r))) & M64


class Rng:
    def __init__(self, a=0x12344332, b=0x12344332):
        t = a ^ b
        self.s1 = rotl(t, 36)
        self.s0 = rotl(a, 55) ^ t ^ ((t << 14) & M64)
        self.draws = 0

    def _next16(self):
        r = ((self.s0 + self.s1) & M64) >> 48
        t = self.s1 ^ self.s0
        self.s0 = rotl(self.s0, 55) ^ t ^ ((t << 14) & M64)
        self.s1 = rotl(t, 36)
        self.draws += 1
        return r

    def draw(self, n):
        thr = 0xFFFF % n
        r = self._next16()
        while r <= thr:
            r = self._next16()
        return r % n


def rotate(w, h, rot, px, py):
    """TILE_MAP::rotate_in_tile_space(width, height, rot, x, y)."""
    if rot == 0x10:
        return px, py
    if rot == 0x20:
        return py, w - px - 1
    if rot == 0x40:
        return w - px - 1, h - py - 1
    return h - py - 1, px


# ---------------------------------------------------------------- database
class Db:
    def __init__(self, dbdir):
        s, tiles = tiledb.load_all(dbdir)
        self.sets = s['tile_sets']
        self.set_by_name = {ts['name']: i for i, ts in enumerate(self.sets)}
        self.climates = s['climates']
        lst = [tiles[k] for k in sorted(tiles)]          # load order: file listing (assumed alphabetical)
        for t in lst:
            t['area'] = t['width'] * t['height']
            t['ltc'] = len(t['link_targets'])
            t['set'] = self.set_by_name[t['tile_set']]
            m = t['mask']
            t['valid'] = [[(not m) or m[t['width'] * j + i] == '1' for i in range(t['width'])] for j in range(t['height'])]
            t['masked'] = bool(m)
            t['also_place'] = bool(self.sets[t['set']]['also_place_tile_set'])
            t['location'] = t['variations'][0]['location']
            t['valid_count'] = sum(sum(r) for r in t['valid'])

        def less(a, b):
            if a['area'] != b['area']:
                return a['area'] > b['area']
            if a['ltc'] != b['ltc']:
                return a['ltc'] > b['ltc']
            return a['name'].encode() < b['name'].encode()
        self.tiles = msvc_sort.sort(lst, less)
        for i, t in enumerate(self.tiles):
            t['idx'] = i
        self.n_sets = len(self.sets)

    def link_as(self, set_idx):
        ts = self.sets[set_idx]
        return ts['link_as_set'] or ts['name']

    def set_from_name(self, name):
        return self.set_by_name.get(name)


# ---------------------------------------------------------------- placement groups
class Groups:
    def __init__(self, db):
        self.db = db
        self.groups = []      # dict(rgb, sets, tiles(explicit tile idx list), link_as)
        for i, ts in enumerate(db.sets):
            self.groups.append(dict(rgb=tuple(ts['rgb']), sets=[i], tiles=[], link_as=''))
        for t in db.tiles:
            if tuple(t['rgb']) != (0, 0, 0):
                self.groups.append(dict(rgb=tuple(t['rgb']), sets=[], tiles=[t['idx']], link_as=''))
        self.by_rgb = {}
        for gi, g in enumerate(self.groups):
            self.by_rgb.setdefault(g['rgb'], gi)
        # matches(group, tile variation) table
        self.match = np.zeros((len(self.groups) + 1, len(db.tiles)), bool)   # last row = INVALID
        for gi, g in enumerate(self.groups):
            for t in db.tiles:
                self.match[gi, t['idx']] = t['idx'] in g['tiles'] or t['set'] in g['sets']

    def get_tiles(self, gi):
        g = self.groups[gi]
        out = list(dict.fromkeys(g['tiles']))
        for s in g['sets']:
            out += [t['idx'] for t in self.db.tiles if t['set'] == s]
        return out

    def matches_link_as(self, gi, set_idx):
        if set_idx is None:
            return False
        g = self.groups[gi]
        las = self.db.link_as(set_idx)
        if g['link_as'] and g['link_as'] == las:
            return True
        for ti in g['tiles']:
            if self.db.link_as(self.db.tiles[ti]['set']) == las:
                return True
        for s in g['sets']:
            if self.db.link_as(s) == las:
                return True
        return False


# ---------------------------------------------------------------- the editor tile map
class EditorTileMap:
    def __init__(self, db, groups, group_map, climate_map):
        self.db, self.groups = db, groups
        self.H, self.W = group_map.shape
        self.group = group_map            # [y, x], y = 0 south
        self.climate = climate_map
        self.layer = {1: np.zeros((self.H, self.W), np.int32), 2: np.zeros((self.H, self.W), np.int32)}
        self.layer_set = np.full((self.H, self.W), -1, np.int32)   # tile set of the layer-1 tile at a cell
        self.instances = []               # (layer, x, y, tile idx, rot, climate, first cell)
        self.rng = Rng()
        self.state_flags = {}             # cell -> set(ids)
        self.state_pts = {}               # (cell, id) -> [pts] (max 4)
        self.link_entries = self._build_link_entries()
        self.set_bounds = self._scan_tile_areas()
        self.log = []

    def _build_link_entries(self):
        db = self.db
        out = {}
        for t in db.tiles:
            w, h = t['width'], t['height']
            for L in t['links']:
                if L['test'] != 'TLT_EQUALS':
                    continue
                lx = min(max(L['x'], 0), w - 1)
                ly = min(max(L['y'], 0), h - 1)
                for T in t['link_targets']:
                    if (T['x'], T['y']) != (lx, ly):
                        continue
                    lset = db.set_from_name(L['link_set'])
                    tset = db.set_from_name(T['tile_set'])
                    if db.link_as(tset) != db.link_as(lset):
                        continue
                    s = db.set_from_name(db.link_as(tset))
                    if s is None:
                        continue
                    out.setdefault(t['idx'], []).append((s, (L['x'], h - L['y'] - 1), (T['x'], h - T['y'] - 1)))
        return out

    def _scan_tile_areas(self):
        # ASSUMPTION: bounds of cells whose group matches the tile set
        b = {}
        for s in range(self.db.n_sets):
            ok = np.zeros(len(self.groups.groups) + 1, bool)
            for gi, g in enumerate(self.groups.groups):
                ok[gi] = s in g['sets'] or any(self.db.tiles[ti]['set'] == s for ti in g['tiles'])
            m = ok[self.group]
            ys, xs = np.nonzero(m)
            b[s] = (xs.min(), ys.min(), xs.max(), ys.max()) if len(xs) else (1, 1, 0, 0)
        return b

    def g(self, x, y):
        if 0 <= x < self.W and 0 <= y < self.H:
            return int(self.group[y, x])
        return -999

    def occupied(self, x, y, t):
        lay = 2 if t['also_place'] else 1
        return self.layer[lay][y, x] != 0

    def space_free(self, t, x, y, r):
        w, h = t['width'], t['height']
        rot = ROTS[r]
        grp = INVALID
        corner = None
        for j in range(h):
            for i in range(w):
                if t['masked'] and not t['valid'][j][i]:
                    continue
                dx, dy = rotate(w, h, rot, i, h - j - 1)
                px, py = x + dx, y + dy
                if px >= self.W or py >= self.H:
                    return False, None, None
                if corner is None:
                    corner = (px, py)
                gi = int(self.group[py, px])
                if gi == INVALID:
                    return False, None, None
                if not self.groups.match[gi, t['idx']]:
                    return False, None, None
                if grp != INVALID and grp != gi:
                    return False, None, None
                if self.occupied(px, py, t):
                    return False, None, None
                grp = gi
        return True, corner, grp

    def links_match(self, t, x, y, r):
        db = self.db
        if not t['links']:
            return True
        w, h = t['width'], t['height']
        rot = ROTS[r]
        for L in t['links']:
            dx, dy = rotate(w, h, rot, L['x'], h - L['y'] - 1)
            px, py = x + dx, y + dy
            if px < 0 or py < 0 or px >= self.W or py >= self.H:
                continue                      # transition_tile_set is always null
            gi = int(self.group[py, px])
            S = db.set_from_name(db.link_as(db.set_from_name(L['link_set'])))
            if gi == INVALID or S is None:
                return False
            occ = int(self.layer_set[py, px])
            if occ < 0:
                m = self.groups.matches_link_as(gi, S)
            else:
                m = db.link_as(occ) == db.link_as(S)
            flags = self.state_flags.get((px, py))
            if L['test'] == 'TLT_EQUALS':
                if (not m) or flags:
                    if not flags or S not in flags:
                        return False
                    pts = self.state_pts.get(((px, py), S), [])
                    if any(p != (0, 0) for p in pts):
                        found = False
                        for P in pts:
                            if P == (0, 0):
                                continue
                            for T in t['link_targets']:
                                tx, ty = rotate(w, h, rot, T['x'], h - T['y'] - 1)
                                if (x + tx, y + ty) == P:
                                    found = True
                                    break
                            if found:
                                break
                        if not found:
                            return False
            else:
                if m:
                    return False
        return True

    def test_final(self, t, x, y):
        ok = [False] * 4
        corners = [None] * 4
        grp = INVALID
        for r in range(4):
            f, c, gi = self.space_free(t, x, y, r)
            corners[r] = c
            if f:
                grp = gi
            if f and self.links_match(t, x, y, r):
                ok[r] = True
        if not any(ok):
            return None
        self.rng.draw(len(t['variations']))
        clim = int(self.climate[y, x])
        if t['random_rotatable']:
            s = self.rng.draw(4)
            for r in list(range(s, 4)) + list(range(0, s)):
                if ok[r]:
                    if t['masked']:
                        cx, cy = corners[r]
                        clim = int(self.climate[cy, cx])
                    return ROTS[r], clim, grp
            return None
        return (0x10, clim, grp) if ok[0] else None

    def collect_matching(self, t, gi):
        out = []
        for ti in self.groups.get_tiles(gi):
            u = self.db.tiles[ti]
            if u['width'] == t['width'] and u['height'] == t['height'] and len(u['links']) == len(t['links']) \
                    and u['mask'] == t['mask'] and all(L in t['links'] for L in u['links']):
                out.append(ti)
        return out

    def place(self, x, y, t, rot, clim, layer):
        w, h = t['width'], t['height']
        L = self.layer[layer]
        first = None
        cells = []
        for j in range(h):
            for i in range(w):
                if t['masked'] and not t['valid'][j][i]:
                    continue
                dx, dy = rotate(w, h, rot, i, h - j - 1)
                px, py = x + dx, y + dy
                if py >= self.H:
                    continue
                if 0 <= px < self.W and L[py, px] != 0:
                    return False
                cells.append((px, py))
        inst = len(self.instances) + 1
        for (px, py) in cells:
            if first is None:
                first = (px, py)
                L[py, px] = inst
            elif px < self.W:
                L[py, px] = -inst
            if layer == 1 and px < self.W:
                self.layer_set[py, px] = t['set']
        self.instances.append((layer, x, y, t['idx'], rot, clim, first))
        return True

    def place_tile(self, x, y, t, rot, clim):
        if not self.place(x, y, t, rot, clim, 1):
            return False
        if t['also_place']:
            target = self.db.set_from_name(self.db.sets[t['set']]['also_place_tile_set'])
            for u in self.db.tiles:
                if u['set'] != target:
                    continue
                if u['width'] == t['width'] and u['height'] == t['height'] and u['mask'] == t['mask']:
                    return self.place(x, y, u, rot, clim, 2)
                if u['width'] == 1 and u['height'] == 1:
                    w, h = t['width'], t['height']
                    for j in range(h):
                        for i in range(w):
                            if not t['valid'][j][i]:
                                continue
                            dx, dy = rotate(w, h, rot, i, h - j - 1)
                            if not self.place(x + dx, y + dy, u, 0x10, clim, 2):
                                return False
                    return True
        return True

    def add_to_link_map(self, x, y, t, rot, gi):
        db = self.db
        w, h = t['width'], t['height']
        if not t['link_targets']:
            las = self.groups.groups[gi]['link_as'] if gi != INVALID else ''
            S = db.set_from_name(las or db.link_as(t['set']))
            if S is None:
                return
            for j in range(h):
                for i in range(w):
                    dx, dy = rotate(w, h, rot, i, h - j - 1)
                    px, py = x + dx, y + dy
                    if t['valid'][j][i]:
                        g2 = self.g(px, py)
                        if 0 <= g2 and self.groups.match[g2, t['idx']]:
                            self.state_flags.setdefault((px, py), set()).add(S)
        else:
            for j in range(h):
                for i in range(w):
                    if t['valid'][j][i]:
                        dx, dy = rotate(w, h, rot, i, h - j - 1)
                        self.state_flags.setdefault((x + dx, y + dy), set()).add(db.n_sets)
            for (S, lp, tp) in self.link_entries.get(t['idx'], []):
                cx, cy = rotate(w, h, rot, *tp)
                px, py = rotate(w, h, rot, *lp)
                cell = (x + cx, y + cy)
                self.state_flags.setdefault(cell, set()).add(S)
                pts = self.state_pts.setdefault((cell, S), [])
                if len(pts) < 4:
                    pts.append((x + px, y + py))

    # ------------------------------------------------------------ passes
    def select(self, typ):
        db = self.db
        sel = []
        for t in db.tiles:
            if typ == 4:
                for L in t['links']:
                    ls = db.set_from_name(L['link_set'])
                    for u in db.tiles:
                        if not u['links'] and u['set'] == ls and u not in sel:
                            sel.append(u)
                continue
            if typ == 5 and not t['links']:
                continue
            if typ == 3:
                own = db.link_as(t['set'])
                hit = any(db.link_as(db.set_from_name(T['tile_set'])) == own for T in t['link_targets'])
                las = bool(db.sets[t['set']]['link_as_set']) and t['ltc'] > 0
                if not hit and t['ltc'] < 3 and not las:
                    continue
            if typ == 2:
                if not any(T['tile_set'] != t['link_targets'][0]['tile_set'] for T in t['link_targets']):
                    continue
            if typ == 1:
                if not (t['ltc'] == 0 and not t['masked'] and t['width'] > 7 and t['height'] > 7):
                    continue
            sel.append(t)
        if typ == 3:
            msvc_sort.sort(sel, lambda a, b: a['ltc'] < b['ltc'])
        else:
            msvc_sort.sort(sel, lambda a, b: (a['valid_count'], a['ltc']) > (b['valid_count'], b['ltc']))
        return sel

    def candidate_mask(self, t):
        """Superset of anchors where the tile can possibly be placed (prefilter only)."""
        okg = np.append(self.groups.match[:-1, t['idx']], False)
        m = okg[self.group]
        x0, y0, x1, y1 = self.set_bounds[t['set']]
        span = max(t['width'], t['height'])
        if span > 1:
            # anchor (x, y) covers cells in [x, x+span) x [y, y+span) for any rotation
            from numpy.lib.stride_tricks import sliding_window_view
            pad = np.pad(m, ((0, span - 1), (0, span - 1)))
            m = sliding_window_view(pad, (span, span)).any(axis=(2, 3))
        bm = np.zeros_like(m)
        bm[y0:y1 + 1, x0:x1 + 1] = True
        return m & bm

    def try_at(self, t, x, y, typ):
        res = self.test_final(t, x, y)
        if res is None:
            return False
        rot, clim, gi = res
        if typ == 3 and t['width'] == 2 and t['height'] == 2:
            g0 = self.g(x, y)
            if self.g(x - 1, y) == g0 and self.g(x - 1, y + 1) == g0 and self.g(x - 1, y - 1) != g0 and self.g(x - 1, y + 2) != g0:
                return False
            if self.g(x + 2, y) == g0 and self.g(x + 2, y + 1) == g0 and self.g(x + 2, y - 1) != g0 and self.g(x + 2, y + 2) != g0:
                return False
        cands = self.collect_matching(t, gi)
        pick = self.db.tiles[cands[self.rng.draw(len(cands))]]
        self.place_tile(x, y, pick, rot, clim)
        self.add_to_link_map(x, y, t, rot, gi)
        return True

    def scan(self, typ):
        sel = self.select(typ)
        placed = 0
        if typ == 0:
            for t in sel:
                ys, xs = np.nonzero(self.candidate_mask(t))
                for y, x in zip(ys.tolist(), xs.tolist()):
                    if not t['masked'] and self.layer[1][y, x] != 0 and not t['also_place']:
                        continue
                    if self.try_at(t, x, y, typ):
                        placed += 1
        else:
            points = {}
            for k, t in enumerate(sel):
                ys, xs = np.nonzero(self.candidate_mask(t))
                for y, x in zip(ys.tolist(), xs.tolist()):
                    points.setdefault((y, x), []).append(k)
            for (y, x) in sorted(points):
                for k in points[(y, x)]:
                    t = sel[k]
                    occ = self.occupied(x, y, t)
                    if not (typ == 3 or t['masked'] or not occ):
                        continue
                    if self.try_at(t, x, y, typ):
                        placed += 1
                        if typ == 5:
                            break
        return placed

    def build(self):
        for typ in (1, 2, 3, 4, 5, 0):
            t0 = time.time()
            n = self.scan(typ)
            print(f'pass {typ}: placed {n}, draws {self.rng.draws}, {time.time() - t0:.0f}s', flush=True)


def load_inputs(db, groups, map_png=None):
    img = np.array(Image.open(map_png or AK + 'tile_map.png').convert('RGB'))[::-1]   # row 0 = south
    H, W, _ = img.shape
    key = img[..., 0].astype(np.int64) << 16 | img[..., 1].astype(np.int64) << 8 | img[..., 2]
    gmap = np.full((H, W), INVALID, np.int32)
    for k in np.unique(key):
        rgb = (int(k >> 16), int(k >> 8 & 255), int(k & 255))
        gi = groups.by_rgb.get(rgb, INVALID)
        gmap[key == k] = gi
    cm, _ = CM.decode(ROOT + '/Vanilla/Map/terrain/campaigns/3k_dlc07_main_map/climate_map.cm')
    clim = cm[::-1].astype(np.int32)
    return gmap, clim


if __name__ == '__main__':
    db = Db(ROOT + '/Vanilla/terrain/tiles/campaign/_tile_database')
    groups = Groups(db)
    gmap, clim = load_inputs(db, groups, os.environ.get('TILE_MAP_PNG'))
    m = EditorTileMap(db, groups, gmap, clim)
    m.build()
    out = sys.argv[1] if len(sys.argv) > 1 else ROOT + '/output/tiles_proto.pkl'
    pickle.dump(dict(instances=m.instances, tiles=[(t['tile_set'], t['name'], t['location']) for t in db.tiles],
                     climates=[c['name'] for c in db.climates]), open(out, 'wb'))
    print('instances', len(m.instances))
