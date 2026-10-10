import sys, struct
import numpy as np
sys.path.insert(0, r"C:\Users\rob\CascadeProjects\AtlasWH3\research\hlp_spd")
from esf_tree import read, node

def load(path):
    b, names, s8, s16 = read(path)
    root, _ = node(b, 16, names, True)
    kids = root[3][0]
    td = kids[0]
    u32 = np.frombuffer(kids[1][2], dtype="<u4").reshape(1024, 1024)
    u8 = np.frombuffer(kids[2][2], dtype="u1").reshape(1024, 1024)
    oc = kids[3]
    # parse transition data
    toks = []
    def walk(n):
        for g in n[3]:
            for c in g:
                if c[0] == "REC" and c[1] == "REGION_AREA_INDEX": toks.append(("R", tuple(k[2] for k in c[3][0])))
                elif c[0] == "VAL": toks.append(c[2])
    walk(td)
    it = iter(toks)
    nodes = []
    nn = next(it)
    for _ in range(nn):
        nid = next(it); na = next(it); areas = []
        for _ in range(na):
            r = next(it)[1]; cx = next(it); cy = next(it); a = next(it); bb = next(it); m = next(it); tr = []
            for _ in range(m):
                x, y, ox, oy, cost = next(it), next(it), next(it), next(it), next(it)
                tgt = next(it)[1]; idx = next(it); f1 = next(it); f2 = next(it)
                tr.append((x, y, ox, oy, cost, tgt, idx, f1, f2))
            costs = [next(it) for _ in range(m * (m - 1))]
            areas.append(dict(r=r, c=(cx, cy), a=a, b=bb, tr=tr, costs=costs))
        nodes.append((nid, areas))
    rest = list(it)
    return nodes, u32, u8, [k[2] for k in oc[3][0]], rest

if __name__ == "__main__":
    nodes, u32, u8, oc, rest = load(sys.argv[1])
    areas = [a for _, ar in nodes for a in ar]
    print("nodes", len(nodes), "areas", len(areas), "transitions", sum(len(a["tr"]) for a in areas), "rest", rest, "oc", oc)
    print("node ids", [n for n, _ in nodes][:40])
    print("area keys", [a["r"] for a in areas][:60])
    print("a values", sorted(set(a["a"] for a in areas))[:20])
    nz = np.argwhere(u32 != 0xFFFFFFFF)
    print("u32 distinct sentinel counts", {hex(v): int((u32 == v).sum()) for v in (0, 0xFFFFFFFF, 0xFFFFFFFE)})
    print("u32 non-FFFFFFFF bbox", nz.min(0) if len(nz) else None, nz.max(0) if len(nz) else None)
    nz8 = np.argwhere(u8 != 0)
    print("u8 nonzero bbox", nz8.min(0), nz8.max(0), "u8 max", u8.max())
    print("u32 row0", u32[0, :30])
    print("u8 row0", u8[0, :30])
    print("u32 [0..8,0..8]\n", u32[:8, :8])

def check(path):
    nodes, u32, u8, oc, rest = load(path)
    fin = u32[u32 != 0xFFFFFFFE]
    print("max finite", fin.max(), "oc", oc, "u8 sym", bool((u8 == u8.T).all()), "u8 diag max", u8.diagonal().max())
    lower = np.tril(u32, -1)
    print("lower all FE", bool((u32[np.tril_indices(1024, 0)] == 0xFFFFFFFE).all()))
    print("u8 upper vs lower", int((np.triu(u8,1) != 0).sum()), int((np.tril(u8,-1) != 0).sum()))
