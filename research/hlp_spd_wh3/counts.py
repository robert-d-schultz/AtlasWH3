"""Every nested record's group count field: width, and where the record / its groups sit relative to 1 MiB blocks."""
import sys
sys.path.insert(0, r"C:\Users\rob\CascadeProjects\AtlasWH3\research\hlp_spd")
from esf_tree import read, SZ
B = 1 << 20
def uleb(b, p):
    v = 0; n = 0
    while True:
        c = b[p + n]; v = v << 7 | (c & 0x7F); n += 1
        if not c & 0x80: return v, n
def walk(b, p, names, out, root=False):
    t = b[p]
    if t & 0x80:
        if (t & 0x20) or root: ni = int.from_bytes(b[p+1:p+3], "little"); q = p + 4
        else: ni = ((t & 1) << 8) | b[p+1]; q = p + 2
        szpos = q
        size, n = uleb(b, q); q += n
        if t & 0x40:
            cpos = q
            cnt, n2 = uleb(b, q); q += n2; end = q + size
            gpos = []
            first_end = None
            for gi in range(cnt):
                gs, n3 = uleb(b, q); gp = q; q += n3; ge = q + gs
                if first_end is None: first_end = ge
                while q < ge: q = walk(b, q, names, out)
            if size > 1000 or n2 > 1:
                out.append((names[ni], "cnt", cnt, "w", n2, "szw", n, "szpos", szpos, "end", end, "blocks", szpos // B, end // B, "firstgroupend", first_end, first_end // B))
            return end
        end = q + size
        while q < end: q = walk(b, q, names, out)
        return end
    if t >= 0x40:
        l, n = uleb(b, p + 1); return p + 1 + n + l
    return p + 1 + SZ[t]
for f in sys.argv[1:]:
    b, names, _, _ = read(f)
    out = []
    walk(b, 16, names, out, True)
    for o in out: print(f[:40], o)
