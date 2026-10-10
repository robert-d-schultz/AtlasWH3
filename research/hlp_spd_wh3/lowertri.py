"""Hypotheses for the lower triangle of hlp's u8 region table (u8[s, r], s > r)."""
import sys, os, heapq
import numpy as np
import arrays
nodes, u32, u8, oc, rest = arrays.load(sys.argv[1])
areas = {a["r"]: a for _, ar in nodes for a in ar}
by_inside = {}
for k, a in areas.items():
    for i, t in enumerate(a["tr"]): by_inside.setdefault((k, t[0], t[1]), []).append(i)
def matrix(a, i, j):
    m = len(a["tr"]); idx = [t[6] for t in a["tr"]]; ii, jj = idx[i], idx[j]
    return a["costs"][ii * (m - 1) + (jj if jj < ii else jj - 1)]
VAR = os.environ.get("VAR", "noflag1")
def run(src):
    best = {}; pq = []; seen = set()
    for k, a in areas.items():
        if k[0] == src:
            for i in range(len(a["tr"])): heapq.heappush(pq, (0, 0, k, i))
    while pq:
        c, h, k, i = heapq.heappop(pq)
        if (k, i) in seen: continue
        seen.add((k, i))
        t = areas[k]["tr"][i]
        if VAR == "noflag1" and t[7]: continue
        if VAR == "noflag2" and t[8]: continue
        tgt = t[5]; nc = c + t[4]; nh = h + (1 if tgt[0] != k[0] else 0)
        if VAR == "minhops": nc, nh = nh, nc
        if tgt[0] != src and (tgt[0] not in best or (nc, nh) < best[tgt[0]]): best[tgt[0]] = (nc, nh)
        b = areas.get(tgt)
        if b is None: continue
        for j0 in by_inside.get((tgt, t[2], t[3]), []):
            for j in range(len(b["tr"])):
                step = 0 if j == j0 else matrix(b, j0, j)
                if VAR == "minhops": heapq.heappush(pq, (nc, nh + step, tgt, j))
                elif (tgt, j) not in seen: heapq.heappush(pq, (nc + step, nh, tgt, j))
    return best
regs = sorted({k[0] for k in areas})
SRC = int(os.environ.get("SRC", "40"))
ok = tot = 0; bad = []
for r in regs[:SRC]:
    best = run(r)
    for s in regs:
        if s <= r: continue
        v = best.get(s)
        hops = (v[0] if VAR == "minhops" else v[1]) if v else 255
        tot += 1
        if hops == u8[s, r]: ok += 1
        elif len(bad) < 8: bad.append((r, s, hops, int(u8[s, r]), int(u8[r, s])))
print(VAR, "lower", ok, "/", tot, bad)
