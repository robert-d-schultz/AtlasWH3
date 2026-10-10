"""Hypothesis test: hlp's region-to-region cost / hop tables from the transition graph."""
import sys, heapq
import numpy as np
import arrays

nodes, u32, u8, oc, rest = arrays.load(sys.argv[1])
areas = {a["r"]: a for _, ar in nodes for a in ar}
# transition node id: (area key, index in file order)
def regions_of(k): return k[0]
by_inside = {}
for k, a in areas.items():
    for i, t in enumerate(a["tr"]):
        by_inside.setdefault((k, t[0], t[1]), []).append(i)
def matrix(a, i, j):
    m = len(a["tr"])
    # row-major without diagonal, rows/cols in creation (idx) order; map file order -> idx
    idx = [t[6] for t in a["tr"]]
    ii, jj = idx[i], idx[j]
    return a["costs"][ii * (m - 1) + (jj if jj < ii else jj - 1)]
def run(src_region, mode):
    # Dijkstra over (area, transition) states; cost to *arrive* in a region
    best = {}
    pq = []
    for k, a in areas.items():
        if k[0] != src_region: continue
        for i in range(len(a["tr"])):
            heapq.heappush(pq, (0, 0, k, i))
    seen = set()
    while pq:
        c, hops, k, i = heapq.heappop(pq)
        if (k, i) in seen: continue
        seen.add((k, i))
        a = areas[k]
        t = a["tr"][i]
        tgt = t[5]
        if VARIANT == "noflag1" and t[7]: continue
        if VARIANT == "noflag2" and t[8]: continue
        nc = c + t[4]
        r = tgt[0]
        nh = hops + (1 if r != k[0] else 0)
        if r != src_region and (r not in best or (nc, nh) < best[r]): best[r] = (nc, nh)
        b = areas.get(tgt)
        if b is None: continue
        ins = by_inside.get((tgt, t[2], t[3]), [])
        for j0 in ins:
            for j in range(len(b["tr"])):
                cc = nc + (0 if j == j0 else matrix(b, j0, j))
                if (tgt, j) not in seen: heapq.heappush(pq, (cc, nh, tgt, j))
        # also continue within the source area to other transitions
        if hops == 0:
            for j in range(len(a["tr"])):
                if j != i and (k, j) not in seen: heapq.heappush(pq, (c + matrix(a, i, j), 0, k, j))
    return best
regs = sorted({k[0] for k in areas})
import os
VARIANT = os.environ.get('VARIANT', '')
SRC = int(os.environ.get('SRC', '100000'))
ok = tot = okh = 0
bad = []
for r in (regs[:SRC] if len(sys.argv) < 3 else []):
    best = run(r, 0)
    for s in regs:
        if s <= r: continue
        tot += 1
        exp = u32[r, s]; eh = u8[r, s]
        got = best.get(s, (0xFFFFFFFF, 255))
        if got[0] == exp: ok += 1
        else: bad.append((r, s, got, exp, eh))
        if got[1] == eh: okh += 1
print("cost", ok, "/", tot, "hops", okh, "/", tot)
for b in bad[:12]: print(b)

if len(sys.argv) > 2:
    from collections import Counter
    c = Counter()
    for r in regs[:40]:
        best = run(r, 0)
        for s in regs:
            if s <= r: continue
            got = best.get(s, (0xFFFFFFFF, 255))
            if got[1] != u8[r, s]: c[("cost same" if got[0] == u32[r, s] else "cost differs", int(got[1]) - int(u8[r, s]))] += 1
    print(sorted(c.items(), key=lambda kv: -kv[1])[:40])
