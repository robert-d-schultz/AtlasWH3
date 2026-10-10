"""Compares a 3K-layout spd (ours: 16 values = 8 landmarks x out/in) with WH3's global values (cells 0..7)."""
import sys, numpy as np
import spd as S, spd2

ours, ref = sys.argv[1], sys.argv[2]
t = S.toks(ours)
x0, y0, x1, y1, n = [v[1] for v in t[:5]]
vals = np.array([v[1] for v in t[5:5 + n * 17]], dtype=np.int64).reshape(n, 17)[:, 1:]
d = spd2.load(ref)
assert d["box"] == (x0, y0, x1, y1), (d["box"], (x0, y0, x1, y1))
rv = d["v"]
for k in range(8):
    out_, in_ = vals[:, 2 * k], vals[:, 2 * k + 1]
    r = rv[:, k]
    print(k, "out", int((out_ == r).sum()), "in", int((in_ == r).sum()), "of", n)
