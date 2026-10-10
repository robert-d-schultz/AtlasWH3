"""Lists every record size / group size / array length in a CAAB file with its uleb byte count (padded or shortest)."""
import sys
sys.path.insert(0, r"C:\Users\rob\CascadeProjects\AtlasWH3\research\hlp_spd")
from esf_tree import read, SZ

def uleb(b, p):
    v = 0; n = 0
    while True:
        c = b[p + n]; v = v << 7 | (c & 0x7F); n += 1
        if not c & 0x80: return v, n

def walk(b, p, names, out, root=False, depth=0):
    t = b[p]
    if t & 0x80:
        if (t & 0x20) or root: ni = int.from_bytes(b[p+1:p+3], "little"); q = p + 4
        else: ni = ((t & 1) << 8) | b[p+1]; q = p + 2
        size, n = uleb(b, q); out.append((names[ni], "size", size, n, depth)); q += n
        if t & 0x40:
            cnt, n2 = uleb(b, q); q += n2; end = q + size
            for gi in range(cnt):
                gs, n3 = uleb(b, q); out.append((names[ni], f"group{gi}", gs, n3, depth)); q += n3; ge = q + gs
                while q < ge: q = walk(b, q, names, out, depth=depth+1)
            return end
        end = q + size
        while q < end: q = walk(b, q, names, out, depth=depth+1)
        return end
    if t >= 0x40:
        l, n = uleb(b, p + 1); out.append(("array", hex(t), l, n, depth)); return p + 1 + n + l
    return p + 1 + SZ[t]

b, names, s8, s16 = read(sys.argv[1])
out = []
walk(b, 16, names, out, True)
lim = int(sys.argv[2]) if len(sys.argv) > 2 else 0
for o in out:
    if o[2] >= lim or o[3] > 2: print(o)
