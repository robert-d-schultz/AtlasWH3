import sys, numpy as np
import spd as S

def load(path):
    t = S.toks(path)
    x0, y0, x1, y1, n, h1, h2 = [v[1] for v in t[:7]]
    W, H = x1 - x0 + 1, y1 - y0 + 1
    cells = t[7:7 + n * 19]
    vals = np.array([c[1] for c in cells], dtype=np.int64).reshape(n, 19)
    rp = [v[1] for v in t[7 + n * 19 + 1:-1]]
    glob = [(rp[2*k], rp[2*k+1]) for k in range(8)]
    areas = {}
    k = 16
    while k < len(rp):
        r, a = rp[k], rp[k+1]; pts = [(rp[k+2+2*j], rp[k+3+2*j]) for j in range(8)]
        areas[(r, a)] = pts; k += 18
    return dict(box=(x0, y0, x1, y1), W=W, H=H, h=(h1, h2), v=vals, glob=glob, areas=areas)

def cell(d, x, y):
    x0, y0, _, _ = d["box"]
    return d["v"][(y - y0) * d["W"] + (x - x0)]

if __name__ == "__main__":
    d = load(sys.argv[1])
    print("header", d["box"], d["h"], "areas", len(d["areas"]))
    print("glob", d["glob"])
    for (r, a), pts in list(d["areas"].items())[:4]:
        print((r, a), pts)
    tr = d["v"][:, 16:]
    print("trailer u32 distinct", np.unique(tr[:, 0])[:20], "u16 a", np.unique(tr[:, 1])[:30], "u16 b", np.unique(tr[:, 2]))
    for name, pts in [("glob", d["glob"])] + [(str(k), v) for k, v in list(d["areas"].items())[:3]]:
        for j, (x, y) in enumerate(pts):
            if x == 65535: continue
            c = cell(d, x, y)
            print(name, j, (x, y), "zeros at", list(np.where(c[:16] == 0)[0]), "trailer", list(c[16:]))
