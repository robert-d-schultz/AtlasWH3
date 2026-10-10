"""WH3 river bake, float32 prototype: bob_terrain -> TOOLDATABUILDER::process_river_spline -> WARSCAPE::tessellate_spline
(warscape.modder.x64.dll; RVAs of the 2026-09 kit). Checked against the meshes BOB left in a kit's working_data
(AK = the kit root; default the Steam kit). Ported to src/AtlasWH3.Core/Campaign/Rivers/Wh3River.cs.
 - heights relative to the stored first point's y (REBASE=y, BOB's; the alternatives tested: local = all of x, y, z,
   world = through the entity position, none); reverse_direction walks the points backwards, tangents swapped
 - 0x7109a0: one 4-D Bezier segment (x, y, z, width) per point pair: p_i, p_i + tangent_out, p_i+1 + tangent_in, p_i+1;
   widths clamped to >= 0.01, inner controls 0.75 w0 + 0.25 w1 and 0.25 w0 + 0.75 w1
 - 0x736b60: a segment whose end equals its start is dropped; degenerate ends -> midpoints, straight length; else
   length = 1001-step float32 rectangle sum of |B'(u)| (xyz), as 3K
 - 0x741410: weights from the power basis, w0 = (d - 3c) + (-a + 3b); each channel (P0 w0 + P1 w1) + (P2 w2 + P3 w3)
 - 0x7111b0: columns = ceil(clamp(width(0) / col step, 1, 10)) + 1; n = ceil(max(L / row step, 1)) steps, row k at t = k (1/n)
 - 0x733340: per row P = eval(t), D = deriv(t), xz direction normalised; off = (j / (cols - 1)) w - w/2;
   vertex (dz off + Px, Py, -(off dx) + Pz), with off and t L for the uv
usage: wh3_tessellate.py <map> [entity id ...]"""
import glob, math, os, struct, sys
import xml.etree.ElementTree as ET
import numpy as np

F = np.float32
AK = os.environ.get("AK", r"D:\SteamLibrary\steamapps\common\Total War WARHAMMER III\assembly_kit")
REBASE = os.environ.get("REBASE", "y")
COL_STEP, ROW_STEP = F(1), F(0.2)


def v3(s): return [float(x) for x in s.replace(',', ' ').split()]


def rivers(m):
    out = {}
    for f in glob.glob(os.path.join(AK, 'raw_data', 'terrain', 'campaigns', m, '*.layer')):
        txt = open(f, encoding='utf-8').read()
        if 'ECRiverSpline' not in txt: continue
        for e in ET.fromstring(txt).iter('entity'):
            rs = e.find('ECRiverSpline')
            if rs is None: continue
            pts = [dict(pos=v3(p.get('position')), tin=v3(p.get('tangent_in')), tout=v3(p.get('tangent_out')), w=float(p.get('width')))
                   for p in rs.iter('point')]
            t = e.find('ECTransform')
            out[e.get('id')] = dict(rev=rs.get('reverse_direction') == 'true', pts=pts, mat=rs.get('material'),
                                    pos=v3(t.get('position')) if t is not None else [0, 0, 0])
    return out


def model(path):
    d = open(path, 'rb').read()
    m = 0xa8
    _, _, _, voff, vc, ioff, ic = struct.unpack_from('<HHIIIII', d, m)
    v = np.frombuffer(d, np.uint8, vc * 48, m + voff).reshape(vc, 48)
    f = v[:, :32].copy().view('<f4')
    return dict(raw=d, pos=f[:, :3], w=f[:, 3], uv=f[:, 4:6], uv2=f[:, 6:8], nb=v[:, 32:48], idx=np.frombuffer(d, '<u2', ic, m + ioff),
                bounds=struct.unpack_from('<6f', d, m + 0x18), pivot=np.frombuffer(d, '<f4', 3, 0xf8 + 0x224))


def weights(a, b, c, d):
    """0x741410's basis weights for (a, b, c, d) = (u^3, u^2, u, 1) or (3u^2, 2u, 1, 0)."""
    w0 = F(F(F(d * F(1)) + F(c * F(-3))) + F(F(a * F(-1)) + F(b * F(3))))
    w1 = F(F(F(d * F(0)) + F(c * F(3))) + F(F(a * F(3)) + F(b * F(-6))))
    w2 = F(F(F(d * F(0)) + F(c * F(0))) + F(F(a * F(-3)) + F(b * F(3))))
    w3 = F(F(F(a * F(1)) + F(b * F(0))) + F(F(c * F(0)) + F(F(0) * d)))
    return w0, w1, w2, w3


def combine(P, w):
    return [F(F(F(P[0][k] * w[0]) + F(P[1][k] * w[1])) + F(F(P[2][k] * w[2]) + F(P[3][k] * w[3]))) for k in range(4)]


def eval_u(P, u):
    u = F(u); u2 = F(u * u); return combine(P, weights(F(u2 * u), u2, u, F(1)))


def deriv_u(P, u):
    u = F(u); return combine(P, weights(F(F(u * F(3)) * u), F(u + u), F(1), F(0)))


class Spline4:
    def __init__(self):
        self.segs, self.lens, self.starts, self.total = [], [], [], F(0)

    def add(self, p0, p1, p2, p3):
        P = [list(map(F, p)) for p in (p0, p1, p2, p3)]
        if P[3][:3] == P[0][:3]: return                                    # closed segment: dropped
        h = F(0.5)
        e01, e23 = P[0][:3] == P[1][:3], P[2][:3] == P[3][:3]
        if e01 and not e23: P[1] = [F(F(P[2][k] + P[0][k]) * h) for k in range(4)]
        elif e23 and not e01: P[2] = [F(F(P[1][k] + P[3][k]) * h) for k in range(4)]
        elif e01 and e23:
            P[1] = [F(F(P[3][k] + P[0][k]) * h) for k in range(4)]; P[2] = list(P[1])
        self.segs.append(P)
        if e01 or e23:
            dx, dy, dz = (F(P[0][k] - P[3][k]) for k in range(3))
            L = F(math.sqrt(F(F(F(dx * dx) + F(dy * dy)) + F(dz * dz))))
        else:
            du = F(F(1) / F(1000)); u = F(0); acc = F(0)
            while True:
                d = deriv_u(P, u)
                u = F(u + du)
                acc = F(acc + F(math.sqrt(F(F(F(d[0] * d[0]) + F(d[1] * d[1])) + F(d[2] * d[2])))))
                if not u < F(1): break
            L = F(acc * du)
        self.lens.append(L); self.starts.append(self.total); self.total = F(self.total + L)

    def locate(self, t):
        t = F(t); acc = F(0); i = len(self.segs) - 1
        inv = F(F(1) / self.total)
        for k, L in enumerate(self.lens):
            ok = t >= acc; acc = F(F(inv * L) + acc)
            if ok and t <= acc: i = k; break
        return i, F(F(F(self.total * t) - self.starts[i]) / self.lens[i])

    def eval(self, t): i, u = self.locate(t); return eval_u(self.segs[i], u)

    def deriv(self, t): i, u = self.locate(t); return deriv_u(self.segs[i], u)


def spline(r):
    s = Spline4()
    pts = r['pts']
    # heights relative to the stored first point's (Old World's river 19261f91ac8e913 has its first point at y -0.757);
    # x and z stay: 15 IEE rivers with first points 1e-6 off in x/z match only unshifted
    o = list(map(F, pts[0]['pos']))
    if REBASE == 'world':
        e = list(map(F, r['pos'])); eo = [F(e[k] + o[k]) for k in range(3)]
        pts = [dict(p, pos=[F(F(e[k] + F(p['pos'][k])) - eo[k]) for k in range(3)]) for p in pts]
    elif REBASE == 'none':
        pass
    elif REBASE == 'y':
        pts = [dict(p, pos=[F(p['pos'][0]), F(F(p['pos'][1]) - o[1]), F(p['pos'][2])]) for p in pts]
    else:
        pts = [dict(p, pos=[F(F(p['pos'][k]) - o[k]) for k in range(3)]) for p in pts]
    if r['rev']:   # reverse_direction: the points walked backwards, each point's tangents swapped
        pts = [dict(p, tin=p['tout'], tout=p['tin']) for p in reversed(pts)]
    def clamp(w): w = F(w); return w if w > F(0.01) else F(0.01)
    for a, b in zip(pts, pts[1:]):
        p0, p3 = list(map(F, a['pos'])), list(map(F, b['pos']))
        w0, w1 = clamp(a['w']), clamp(b['w'])
        p1 = [F(p0[k] + F(a['tout'][k])) for k in range(3)]
        p2 = [F(p3[k] + F(b['tin'][k])) for k in range(3)]
        s.add(p0 + [w0], p1 + [F(F(w0 * F(0.75)) + F(w1 * F(0.25)))], p2 + [F(F(w0 * F(0.25)) + F(w1 * F(0.75)))], p3 + [w1])
    return s


def ceil_f(x):
    i = int(x)
    return i if F(i) == x else i + (1 if x > 0 else 0)


def tessellate(s):
    """the vertices (x, y, z, w, off, dist) of 0x7111b0 / 0x733340, rows in order, and the column count."""
    w0 = s.eval(F(0))[3]
    cols = ceil_f(min(max(F(w0 / COL_STEP), F(1)), F(10))) + 1
    n = ceil_f(max(F(s.total / ROW_STEP), F(1)))
    inv = F(F(1) / F(n))
    out = []
    for k in range(n + 1):
        t = F(0) if k == 0 else F(F(k) * inv)
        P = s.eval(t); D = s.deriv(t)
        dx, dz = D[0], D[2]
        l2 = F(F(dz * dz) + F(dx * dx))
        if l2 > 0:
            r = F(F(1) / F(math.sqrt(l2))); dz = F(r * dz); dx = F(r * dx)
        w = P[3]; half = F(w * F(0.5))
        for j in range(cols):
            off = F(F(F(F(j) / F(cols - 1)) * w) - half)
            out.append((F(F(dz * off) + P[0]), P[1], F(F(-F(off * dx)) + P[2]), w, off, F(t * s.total)))
    return out, cols


def indices(rows, cols):
    """0x733980 per new row: (A, B, C), (C, B, D) for each column pair."""
    out = []
    for k in range(1, rows):
        base = (k - 1) * cols
        for j in range(cols - 1):
            a, b, c, d = base + j, base + j + 1, base + cols + j, base + cols + j + 1
            out += [a, b, c, c, b, d]
    return out


def normals(vs, idx):
    """0x710be0: normalised face normals summed per vertex, normalised; tangent n x X (fallback (ny, -nx, 0)), bitangent n x t."""
    acc = [[F(0), F(0), F(0)] for _ in vs]
    one = F(1)
    for q in range(0, len(idx), 3):
        i0, i1, i2 = idx[q:q + 3]
        p0, p1, p2 = vs[i0], vs[i1], vs[i2]
        e1 = [F(p1[k] - p0[k]) for k in range(3)]; e2 = [F(p2[k] - p0[k]) for k in range(3)]
        l1 = F(F(F(e1[1] * e1[1]) + F(e1[0] * e1[0])) + F(e1[2] * e1[2]))
        if l1 > 0:
            l2 = F(F(F(e2[1] * e2[1]) + F(e2[0] * e2[0])) + F(e2[2] * e2[2]))
        if l1 > 0 and l2 > 0:
            r2 = F(one / F(math.sqrt(l2))); r1 = F(one / F(math.sqrt(l1)))
            bx, by, bz = F(r2 * e2[0]), F(r2 * e2[1]), F(r2 * e2[2])
            ax, ay, az = F(r1 * e1[0]), F(r1 * e1[1]), F(r1 * e1[2])
            cx = F(F(az * by) - F(ay * bz))
            cy = F(F(ax * bz) - F(az * bx))
            cz = F(F(ay * bx) - F(ax * by))
            r = F(one / F(math.sqrt(F(F(F(cy * cy) + F(cx * cx)) + F(cz * cz)))))
            n = (F(r * cx), F(r * cy), F(r * cz))
        else:
            n = (F(0), F(1), F(0))
        for i in (i0, i1, i2):
            acc[i] = [F(n[k] + acc[i][k]) for k in range(3)]
    out = []
    for a in acc:
        l = F(math.sqrt(F(F(F(a[0] * a[0]) + F(a[1] * a[1])) + F(a[2] * a[2]))))
        if l != 0:
            r = F(one / l); n = (F(a[0] * r), F(a[1] * r), F(a[2] * r))
        else:
            n = (F(0), F(1), F(0))
        t = (F(0), n[2], F(-n[1]))
        if not F(math.sqrt(F(F(n[2] * n[2]) + F(t[2] * t[2])))) >= F(0.001):
            t = (n[1], F(-n[0]), F(0))
        b = (F(F(t[2] * n[1]) - F(t[1] * n[2])), F(F(t[0] * n[2]) - F(t[2] * n[0])), F(F(t[1] * n[0]) - F(t[0] * n[1])))
        out.append((n, t, b))
    return out


def enc(v): return int(F(F(v + F(1)) * F(127.5)))


def reenc(b):
    """the RMV2 writer decodes b/255*2-1 and re-encodes trunc((v+1)*127.5), as 3K's MODEL_PROCESSOR."""
    t = F(F(b) * F(F(1) / F(255))); return enc(F(F(t + t) - F(1)))


def full_vertices(vs, idx, uv_scale=F(0.1)):
    """0x71fc60: STANDARD_RIGID_MESH_VERTEX_FULL bytes per vertex (48): xyz 1, u v, uv2, n t b (z y x 0xff), colour."""
    nb = normals(vs, idx)
    out = []
    for v, (n, t, b) in zip(vs, nb):
        o = struct.pack('<4f', v[0], v[1], v[2], 1.0) + struct.pack('<4f', F(uv_scale * v[4]), F(uv_scale * v[5]), 0, 0)
        o += bytes([enc(n[2]), enc(n[1]), enc(n[0]), 255, enc(t[2]), enc(t[1]), enc(t[0]), 255, enc(b[2]), enc(b[1]), enc(b[0]), 255, 255, 255, 255, 255])
        out.append(o)
    return out


if __name__ == '__main__':
    m = sys.argv[1]
    rs = rivers(m)
    md = os.path.join(AK, 'working_data', 'terrain', 'campaigns', m, 'models')
    ids = sys.argv[2:] or sorted(i for i in rs if os.path.exists(os.path.join(md, f'river_{i}.wsmodel.rigid_model_v2')))
    tot = ok = bad_count = 0
    for rid in ids:
        r = rs[rid]
        mo = model(os.path.join(md, f'river_{rid}.wsmodel.rigid_model_v2'))
        vs, cols = tessellate(spline(r))
        if len(vs) != len(mo['pos']):
            bad_count += 1
            print(rid, 'vertices', len(vs), 'BOB', len(mo['pos']), 'rev' if r['rev'] else ''); continue
        idx = indices(len(vs) // cols, cols)
        full = full_vertices(vs, idx)
        # first-use renumbering (from the unflipped list), positions relative to the box centre
        order, remap = [], {}
        for i in idx:
            if i not in remap: remap[i] = len(order); order.append(i)
        a = np.array([[vs[i][0], vs[i][1], vs[i][2]] for i in order], np.float32)
        lo, hi = a.min(0), a.max(0)
        piv = F(F(lo + hi) * F(0.5))
        rel = (a - piv).astype(np.float32)
        same = (rel == mo['pos']).all(1)
        mine = np.array([list(full[i]) for i in order], np.uint8)
        for g in range(3):
            for c in range(3): mine[:, 32 + 4 * g + c] = [reenc(x) for x in mine[:, 32 + 4 * g + c]]
        mine[:, [35, 39, 43, 44, 45, 46, 47]] = 0
        nbs = (mine[:, 32:48] == mo['nb'])
        uvs = (mine[:, 16:32].copy().view('<f4') == np.concatenate([mo['uv'], mo['uv2']], 1)).all(1)
        fidx = []
        for q in range(0, len(idx), 3): fidx += [remap[idx[q]], remap[idx[q + 2]], remap[idx[q + 1]]]
        tot += len(vs); ok += same.sum()
        print(rid, 'pos', same.sum(), '/', len(vs), 'uv', uvs.sum(), 'n/t/b/c bytes by column', nbs.sum(0), 'idx', list(mo['idx']) == fidx,
              'rev' if r['rev'] else '')
        if len(sys.argv) > 2:
            print(' mine', mine[:3, 32:48].tolist()); print(' bob ', mo['nb'][:3].tolist())
    print('rivers', len(ids), 'count mismatches', bad_count, 'vertices', tot, 'bit-exact', ok)
