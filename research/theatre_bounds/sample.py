"""sample.py: where a hung Warhammer3.exe is spinning. No debugger, no Frida, no breakpoints.

Run it while the game is frozen. For each round it suspends every thread for a moment, reads rip and rsp
(GetThreadContext), copies the top of the stack and keeps the values that point into the exe's .text (fuzzy return
addresses), then resumes the thread. The threads that used CPU during the run are reported with their rip histogram
and their most common stack addresses.

  sample.py [--rounds 200] [--interval 0.02] [--stack 0x4000] [--out stats.json]
"""
import argparse, collections, ctypes, ctypes.wintypes as wt, json, struct, time

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
TH32CS_SNAPTHREAD, TH32CS_SNAPPROCESS = 0x4, 0x2
THREAD_ALL = 0x0002 | 0x0008 | 0x0040   # SUSPEND_RESUME | GET_CONTEXT | QUERY_INFORMATION
CONTEXT_AMD64, CONTEXT_CONTROL, CONTEXT_INTEGER = 0x100000, 0x1, 0x2
TEXT = (0x140001000, 0x1433a7000)   # Warhammer3.exe's .text (loaded at its image base, 0x140000000)


class THREADENTRY32(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ThreadID", wt.DWORD), ("th32OwnerProcessID", wt.DWORD),
                ("tpBasePri", wt.LONG), ("tpDeltaPri", wt.LONG), ("dwFlags", wt.DWORD)]


class PROCESSENTRY32(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD), ("th32DefaultHeapID", ctypes.c_size_t),
                ("th32ModuleID", wt.DWORD), ("cntThreads", wt.DWORD), ("th32ParentProcessID", wt.DWORD), ("pcPriClassBase", wt.LONG),
                ("dwFlags", wt.DWORD), ("szExeFile", ctypes.c_char * 260)]


k32.CreateToolhelp32Snapshot.restype = wt.HANDLE
k32.OpenThread.restype = wt.HANDLE
k32.OpenProcess.restype = wt.HANDLE
k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_uint64, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
k32.GetThreadContext.argtypes = [wt.HANDLE, ctypes.c_void_p]
k32.GetThreadTimes.argtypes = [wt.HANDLE] + [ctypes.POINTER(ctypes.c_uint64)] * 4


def find_pid(name="Warhammer3.exe"):
    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    e = PROCESSENTRY32(); e.dwSize = ctypes.sizeof(e)
    ok = k32.Process32First(snap, ctypes.byref(e))
    while ok:
        if e.szExeFile.decode(errors="replace").lower() == name.lower(): k32.CloseHandle(snap); return e.th32ProcessID
        ok = k32.Process32Next(snap, ctypes.byref(e))
    k32.CloseHandle(snap)
    raise SystemExit(f"{name} is not running")


def threads(pid):
    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0)
    e = THREADENTRY32(); e.dwSize = ctypes.sizeof(e)
    out, ok = [], k32.Thread32First(snap, ctypes.byref(e))
    while ok:
        if e.th32OwnerProcessID == pid: out.append(e.th32ThreadID)
        ok = k32.Thread32Next(snap, ctypes.byref(e))
    k32.CloseHandle(snap)
    return out


def cpu(h):
    c, x, k, u = (ctypes.c_uint64() for _ in range(4))
    k32.GetThreadTimes(h, ctypes.byref(c), ctypes.byref(x), ctypes.byref(k), ctypes.byref(u))
    return k.value + u.value


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--rounds", type=int, default=200)
    ap.add_argument("--interval", type=float, default=0.02)
    ap.add_argument("--stack", type=lambda s: int(s, 0), default=0x4000)
    ap.add_argument("--out", default="sample_stats.json")
    a = ap.parse_args()
    pid = find_pid()
    proc = k32.OpenProcess(0x0010 | 0x0400, False, pid)
    ctxbuf = ctypes.create_string_buffer(1232 + 16)
    ctx = (ctypes.addressof(ctxbuf) + 15) & ~15
    handles = {tid: k32.OpenThread(THREAD_ALL, False, tid) for tid in threads(pid)}
    handles = {t: h for t, h in handles.items() if h}
    cpu0 = {t: cpu(h) for t, h in handles.items()}
    rips = collections.defaultdict(collections.Counter)
    stacks = collections.defaultdict(collections.Counter)
    ordered = {}   # tid -> the last sample's stack values in stack order
    stack = ctypes.create_string_buffer(a.stack)
    for _ in range(a.rounds):
        for tid, h in handles.items():
            if k32.SuspendThread(h) == 0xFFFFFFFF: continue
            try:
                ctypes.memset(ctx, 0, 1232)
                struct.pack_into("<I", (ctypes.c_char * 1232).from_address(ctx), 0x30, CONTEXT_AMD64 | CONTEXT_CONTROL | CONTEXT_INTEGER)
                if not k32.GetThreadContext(h, ctx): continue
                raw = ctypes.string_at(ctx, 1232)
                rsp, rip = struct.unpack_from("<Q", raw, 0x98)[0], struct.unpack_from("<Q", raw, 0xF8)[0]
                rips[tid][rip] += 1
                got = ctypes.c_size_t()
                if k32.ReadProcessMemory(proc, rsp, stack, a.stack, ctypes.byref(got)) or got.value:
                    seen, order = set(), []
                    for (v,) in struct.iter_unpack("<Q", stack.raw[:got.value - got.value % 8]):
                        if TEXT[0] <= v < TEXT[1]:
                            order.append(v)
                            if v not in seen: seen.add(v); stacks[tid][v] += 1
                    ordered[tid] = order
            finally:
                k32.ResumeThread(h)
        time.sleep(a.interval)
    busy = sorted(((cpu(h) - cpu0[t]) / 1e7, t) for t, h in handles.items())[::-1]
    report = []
    for secs, tid in busy[:6]:
        report.append(dict(tid=tid, cpu_s=round(secs, 3), samples=sum(rips[tid].values()),
                           rip=[(hex(r), n) for r, n in rips[tid].most_common(25)],
                           stack=[(hex(r), n) for r, n in stacks[tid].most_common(40)],
                           ordered=[hex(v) for v in ordered.get(tid, [])[:80]]))
        print(f"thread {tid}: {secs:.2f} s CPU; rips {[(hex(r), n) for r, n in rips[tid].most_common(8)]}")
    json.dump(report, open(a.out, "w"), indent=1)
    print(f"written {a.out}")


if __name__ == "__main__":
    main()
