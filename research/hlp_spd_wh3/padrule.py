"""Tests rules for which uleb sizes CA pads to 5 bytes, over every size field of the given files."""
import sys, glob
sys.path.insert(0, r"C:\Users\rob\CascadeProjects\AtlasWH3\research\hlp_spd")
from esf_tree import read, SZ

def uleb(b, p):
    v = 0; n = 0
    while True:
        c = b[p + n]; v = v << 7 | (c & 0x7F); n += 1
        if not c & 0x80: return v, n

def walk(b, p, out, root=False):
    t = b[p]
    if t & 0x80:
        q = p + (4 if (t & 0x20) or root else 2)
        size, n = uleb(b, q); szpos = q; q += n
        if t & 0x40:
            cnt, n2 = uleb(b, q); q += n2; end = q + size
            out.append(("rec", p, szpos, n, size, q, end))
            for gi in range(cnt):
                gs, n3 = uleb(b, q); gp = q; q += n3; ge = q + gs
                out.append(("grp", gp, gp, n3, gs, q, ge))
                while q < ge: q = walk(b, q, out)
            return end
        end = q + size
        out.append(("rec", p, szpos, n, size, q, end))
        while q < end: q = walk(b, q, out)
        return end
    if t >= 0x40:
        l, n = uleb(b, p + 1); out.append(("arr", p, p + 1, n, l, p + 1 + n, p + 1 + n + l)); return p + 1 + n + l
    return p + 1 + SZ[t]

def shortest(v):
    n = 1
    while v >= 1 << (7 * n): n += 1
    return n

rows = []
for f in sys.argv[1:]:
    b = read(f)[0]
    out = []
    walk(b, 16, out, True)
    for kind, hdr, szpos, n, size, body, end in out:
        if size < 1000 and n == shortest(size): continue
        rows.append((f.split("\\")[-1] if "\\" in f else f[-60:], kind, hdr, szpos, n, size, body, end))
B = 1 << 20
rules = {
    "size>=2^20": lambda r: r[5] >= B,
    "body crosses 1MiB (szpos..end)": lambda r: r[3] // B != r[7] // B,
    "crosses 1MiB (body..end)": lambda r: r[6] // B != r[7] // B,
    "crosses 1MiB (hdr..end)": lambda r: r[2] // B != r[7] // B,
}
for name, rule in rules.items():
    bad = [r for r in rows if rule(r) != (r[4] == 5 and r[4] != shortest(r[5]) or (r[4] == 5 and r[5] >= 1 << 28))]
    print(f"{name}: {len(rows) - len(bad)}/{len(rows)}", bad[:4])
