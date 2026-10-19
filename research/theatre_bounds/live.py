"""live.py <tid>: in a game hung in the scanline fill 0x1429368dc, read the counts, the snapped rectangle and the shape (see README.md)."""
import ctypes, ctypes.wintypes as wt, struct, sys
sys.path.insert(0, r"C:\Users\rob\CascadeProjects\AtlasWH3\research\theatre_bounds")
import sample as S
k32 = S.k32
pid = S.find_pid(); tid = int(sys.argv[1])
proc = k32.OpenProcess(0x0010 | 0x0400, False, pid)
h = k32.OpenThread(S.THREAD_ALL, False, tid)
buf = ctypes.create_string_buffer(1232 + 16); ctx = (ctypes.addressof(buf) + 15) & ~15
def rd(a, n):
    b = ctypes.create_string_buffer(n); got = ctypes.c_size_t()
    k32.ReadProcessMemory(proc, a, b, n, ctypes.byref(got)); return b.raw[:got.value]
k32.SuspendThread(h)
try:
    ctypes.memset(ctx, 0, 1232)
    struct.pack_into("<I", (ctypes.c_char * 1232).from_address(ctx), 0x30, 0x100003)
    assert k32.GetThreadContext(h, ctx)
    raw = ctypes.string_at(ctx, 1232)
    R = {n: struct.unpack_from("<Q", raw, o)[0] for n, o in [("rbx",0x90),("rsp",0x98),("rbp",0xA0),("rsi",0xA8),("rdi",0xB0),("r9",0xC0),("r12",0xD8),("r13",0xE0),("r15",0xF0),("rip",0xF8)]}
    rbp = R["rbp"]
    f = lambda a, n=1: struct.unpack(f"<{n}f", rd(a, 4*n))
    i32 = lambda a: struct.unpack("<i", rd(a, 4))[0]
    print("rip", hex(R["rip"]), "row ebx", R["rbx"] & 0xffffffff, "col r9d", R["r9"] & 0xffffffff)
    print("cols [rbp+0x67]", i32(rbp+0x67), "rows [rbp+0x77]", i32(rbp+0x77))
    print("input rect (minx,minz,maxx,maxz) [rsp+0x48]..:", f(R["rsp"]+0x48, 1), f(rbp-0x7d, 1), f(rbp-0x79, 1), f(rbp-0x75, 1))
    print("snapped rect [rbp-0x71]:", f(rbp-0x71, 4))
    g = R["r13"]
    print("grid r13", hex(g), "min", f(g+0x64, 2), "max", f(g+0x6c, 2), "dims", struct.unpack("<ii", rd(g+0x74, 8)))
    shape = struct.unpack("<Q", rd(R["r12"]+0x18, 8))[0]
    print("shape [r12+0x18]", hex(shape), "rect +0x14", f(shape+0x14, 4), "u16 +0xb8", struct.unpack("<H", rd(shape+0xb8, 2))[0])
    m = struct.unpack("<Q", rd(g+0x28, 8))[0]; m = struct.unpack("<Q", rd(m, 8))[0]
    print("margin src [[[r13+0x28]]+0x80]", f(m+0x80, 1))
finally:
    k32.ResumeThread(h)

def rtti(obj):
    b = rd(obj, 8)
    if len(b) < 8: return hex(obj), "unreadable"
    vt = struct.unpack("<Q", b)[0]
    if not (0x140000000 <= vt < 0x146000000): return hex(vt), "not a vtable"
    col = struct.unpack("<Q", rd(vt - 8, 8))[0]
    if not col: return hex(vt), None
    sig, off, cdoff, td_rva, chd_rva, self_rva = struct.unpack("<IIIiii", rd(col, 24))
    name = rd(0x140000000 + td_rva + 16, 120).split(b"\0")[0].decode(errors="replace")
    return hex(vt), name
k32.SuspendThread(h)
try:
    print("shape first qwords", [hex(v) for v in struct.unpack("<4Q", rd(shape, 32))])
    print("shape class", rtti(shape))
    print("owner (r12-0x60) class", rtti(R["r12"] - 0x60))
    print("r12 class", rtti(R["r12"]))
    print("owner list object [owner+0x128] class", rtti(struct.unpack("<Q", rd(R["r12"] - 0x60 + 0x128, 8))[0]))
finally:
    k32.ResumeThread(h)
pts = struct.unpack("<Q", rd(shape + 8, 8))[0]
raw = rd(pts, 26 * 8)
print("points (as vec2):", [tuple(round(v, 2) for v in struct.unpack_from("<2f", raw, i * 8)) for i in range(26)])
