"""Top-level layout of a WH3 hlp/spd ESF: each child of the root, with value runs summarised."""
import sys
sys.path.insert(0, r"C:\Users\rob\CascadeProjects\AtlasWH3\research\hlp_spd")
from esf_tree import read, node

def summ(n, ind="", depth=0, maxd=1):
    if n[0] == "REC":
        g = n[3]
        print(f"{ind}<{n[1]} v{n[2]}{' nested' if n[4] else ''}> groups={len(g)} sizes={[len(x) for x in g[:6]]}{'...' if len(g)>6 else ''}")
        if depth < maxd:
            for grp in g[:2]:
                kids = grp
                i = 0
                while i < len(kids):
                    c = kids[i]
                    if c[0] == "REC":
                        summ(c, ind + "  ", depth + 1, maxd); i += 1
                    elif c[0] == "ARR":
                        print(f"{ind}  ARR {c[1]:#x} len={len(c[2])} {c[2][:32].hex(' ')}"); i += 1
                    else:
                        j = i
                        while j < len(kids) and kids[j][0] == "VAL" and j - i < 12: j += 1
                        print(f"{ind}  " + " ".join(f"{k[1]:#x}:{k[2]}" for k in kids[i:j])); i = j
                if len(g) > 2: print(f"{ind}  ...")
b, names, s8, s16 = read(sys.argv[1])
root, _ = node(b, 16, names, True)
print("names", names)
summ(root, "", 0, int(sys.argv[2]) if len(sys.argv) > 2 else 1)
