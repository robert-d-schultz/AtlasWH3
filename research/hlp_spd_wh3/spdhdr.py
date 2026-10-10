"""spd header (box, cell count, two u32s) and REFERENCE_POINTS group sizes, read directly from the bytes."""
import sys
sys.path.insert(0, r"C:\Users\rob\CascadeProjects\AtlasWH3\research\hlp_spd")
from esf_tree import read, node, SZ
from esf_flat import cauleb
for f in sys.argv[1:]:
    b = open(f, "rb").read()
    p = 20
    size, p = cauleb(b, p)
    vals = []
    for _ in range(7):
        t = b[p]; n = SZ[t]; raw = b[p+1:p+1+n]
        v = int.from_bytes(raw, "big") if t == 0x18 else (0 if t == 0x14 else 1 if t == 0x15 else int.from_bytes(raw, "little"))
        vals.append(v); p += 1 + n
    x0, y0, x1, y1, cells, a, c = vals
    # walk to REFERENCE_POINTS: end of root minus... use tree reader on the tail: find c0 01 record after cells
    names_off = int.from_bytes(b[12:16], "little")
    # cells: scan forward cell by cell is slow; instead search backwards for the REFERENCE_POINTS header via tree
    _, names, _, _ = read(f)
    root, _ = node(b, 16, names, True)
    rp = root[3][0][-1]
    g0, g1 = len(rp[3][0]) // 2, len(rp[3][1]) // 18
    print(f"{f.split('campaign_maps/')[1].split('/')[0]:32} box {x0},{y0}-{x1},{y1} cells {cells} = {(x1-x0+1)*(y1-y0+1)}  u32s {a} {c}  global points {g0}  area entries {g1}")
