"""Traces the cheapest transition path between two regions (CA's transitions) and CA's table cost to each region on it."""
import sys, os, heapq
import arrays
path_, src, dst = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
nodes, u32, u8, oc, _ = arrays.load(path_)
areas = {a["r"]: a for _, ar in nodes for a in ar}
by_inside = {}
for k, a in areas.items():
    for i, t in enumerate(a["tr"]): by_inside.setdefault((k, t[0], t[1]), []).append(i)
def matrix(a, i, j):
    m = len(a["tr"]); idx = [t[6] for t in a["tr"]]; ii, jj = idx[i], idx[j]
    return a["costs"][ii * (m - 1) + (jj if jj < ii else jj - 1)]
def ca(r, s):
    if r == s: return 0
    return int(u32[min(r, s), max(r, s)]) if max(r, s) < 1024 else None
dist = {}; par = {}; pq = []
for k, a in areas.items():
    if k[0] == src:
        for i in range(len(a["tr"])): heapq.heappush(pq, (0, k, i, None))
best = {}
while pq:
    c, k, i, p = heapq.heappop(pq)
    if (k, i) in dist: continue
    dist[(k, i)] = c; par[(k, i)] = p
    t = areas[k]["tr"][i]; tgt = t[5]; nc = c + t[4]
    if tgt[0] not in best or nc < best[tgt[0]][0]: best[tgt[0]] = (nc, (k, i))
    b = areas.get(tgt)
    if not b: continue
    for j0 in by_inside.get((tgt, t[2], t[3]), []):
        for j in range(len(b["tr"])):
            if (tgt, j) not in dist: heapq.heappush(pq, (nc + (0 if j == j0 else matrix(b, j0, j)), tgt, j, (k, i)))
c, s = best[dst]
chain = []
while s: chain.append(s); s = par[s]
chain.reverse()
print(f"mine {src}->{dst} = {c}, CA = {ca(src, dst)}")
for k, i in chain:
    t = areas[k]["tr"][i]
    arr = dist[(k, i)] + t[4]
    print(f"  {k} tr{i} ({t[0]},{t[1]})->({t[2]},{t[3]}) cost {t[4]} into {t[5]}: mine {arr}  CA to region {t[5][0]}: {ca(src, t[5][0])}")

if len(sys.argv) > 4 and sys.argv[4] != "row":
    target = int(sys.argv[4])
    print(f"into region {target}: mine best {best.get(target, (None,))[0]}, CA {ca(src, target)}")
    for k, a in areas.items():
        for i, t in enumerate(a["tr"]):
            if t[5][0] == target and k[0] != target:
                d = dist.get((k, i))
                print(f"  from {k} tr{i} ({t[0]},{t[1]})->({t[2]},{t[3]}) cost {t[4]}: mine at tr {d}, mine arrive {None if d is None else d + t[4]}; CA to region {k[0]}: {ca(src, k[0])}")

if len(sys.argv) > 4 and sys.argv[4] == "row":
    rows = []
    for s, (c, _) in best.items():
        if s < 1024 and s != src:
            v = ca(src, s)
            if v != c: rows.append((v, s, c))
    rows.sort()
    print("row mismatches", len(rows))
    for v, s, c in rows[:15]: print(f"  region {s}: CA {v} mine {c} diff {c - v}")
