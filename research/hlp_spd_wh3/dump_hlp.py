"""Flat token dump of a WH3 hlp_data.esf's TRANSITION_DATA and OTHER_CONSTANTS (REGION_AREA_INDEX shown as R(r,i))."""
import sys
sys.path.insert(0, r"C:\Users\rob\CascadeProjects\AtlasWH3\research\hlp_spd")
from esf_tree import read, node

def flat(n):
    out = []
    for g in n[3]:
        for c in g:
            if c[0] == "REC" and c[1] == "REGION_AREA_INDEX":
                out.append(("R", tuple(k[2] for k in c[3][0])))
            elif c[0] == "REC":
                out.append(("REC", c[1], flat(c)))
            elif c[0] == "ARR":
                out.append(("ARR", c[1], len(c[2])))
            else:
                out.append((f"{c[1]:#x}", c[2]))
    return out

b, names, s8, s16 = read(sys.argv[1])
root, _ = node(b, 16, names, True)
kids = root[3][0]
print("names", names)
for c in kids:
    if c[0] == "REC":
        f = flat(c)
        print(c[1], len(f))
        print("  ", f[:int(sys.argv[2]) if len(sys.argv) > 2 else 200])
