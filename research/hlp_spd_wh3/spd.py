import sys
sys.path.insert(0, r"C:\Users\rob\CascadeProjects\AtlasWH3\research\hlp_spd")
from esf_tree import read, node

def toks(path):
    b, names, s8, s16 = read(path)
    root, _ = node(b, 16, names, True)
    out = []
    def walk(n, d):
        for g in n[3]:
            for c in g:
                if c[0] == "REC":
                    out.append(("<", c[1])); walk(c, d + 1); out.append((">", c[1]))
                elif c[0] == "ARR": out.append(("ARR", c[1], len(c[2])))
                else: out.append((c[1], c[2]))
    walk(root, 0)
    return out

if __name__ == "__main__":
    t = toks(sys.argv[1])
    print(len(t), t[:8])
    # find REFERENCE_POINTS
    i = next(k for k, x in enumerate(t) if x == ("<", "REFERENCE_POINTS"))
    print("REFERENCE_POINTS at", i, "of", len(t), t[i-40:i], t[i:i+20], t[-20:])
    # show token type sequence run-length of first 300 tokens after header
    seq = [x[0] for x in t[7:400]]
    runs = []
    for s in seq:
        if runs and runs[-1][0] == s: runs[-1][1] += 1
        else: runs.append([s, 1])
    print(runs[:60])
    # first cells with a non-sentinel u32
    for k in range(7, i):
        x = t[k]
        if x[0] == 8 and x[1] != 0xFFFFFFFF or x[0] in (0x14,0x15,0x16,0x17,0x18) and k > 7:
            print("first non-sentinel at", k, t[k-25:k+40]); break
