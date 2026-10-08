#!/usr/bin/env python3
"""Trace BOB's file I/O for one headless run (Phase 2: which files each action reads and writes, and which pack
entries it reads). BOB is spawned suspended under Frida, so nothing is missed.

  trace_files.py <kit root> <config name> <label> [--timeout s]

The kit should be an AtlasWH3 scratch kit with binaries/BOB/<config name>_configuration.xml already written
(AtlasWH3.Cli bob-run ... --config-only). Writes research/bob_re/trace_out/<label>.json: files opened for reading and
for writing (outside the pack folder), and per pack the entries read (from the pack index and the read offsets).
"""
import json, os, struct, sys, time
from pathlib import Path
from collections import defaultdict
import frida

HERE = Path(__file__).parent
KIT, CONFIG, LABEL = sys.argv[1:4]
TIMEOUT = int(sys.argv[sys.argv.index("--timeout") + 1]) if "--timeout" in sys.argv else 7200
BIN = os.path.join(KIT, "binaries")
EXE = os.path.join(BIN, "bob.modder.x64.exe")
OUT = HERE / "trace_out"; OUT.mkdir(exist_ok=True)

GENERIC_WRITE, FILE_WRITE_DATA = 0x40000000, 0x2
events = []
done = {"exit": False}

def on_msg(msg, data):
    if msg["type"] == "send" and msg["payload"]["kind"] == "batch":
        events.extend(msg["payload"]["items"])
    elif msg["type"] != "send":
        print("frida:", msg, flush=True)

device = frida.get_local_device()
pid = device.spawn([EXE, f"/configuration:{CONFIG}", "/nosplashscreen", "/dont_stop_on_error"], cwd=BIN)
session = device.attach(pid)
session.on("detached", lambda reason, crash: done.update(exit=True))
script = session.create_script((HERE / "trace_files.js").read_text(encoding="utf-8"))
script.on("message", on_msg)
script.load()
t0 = time.time()
device.resume(pid)
print(f"BOB pid {pid} running under Frida", flush=True)
while not done["exit"] and time.time() - t0 < TIMEOUT:
    time.sleep(1)
if not done["exit"]:
    print("timeout: killing BOB", flush=True)
    try: script.post({"type": "flush"}); time.sleep(1)
    except Exception: pass
    device.kill(pid)
secs = time.time() - t0

# ---------------------------------------------------------------- summarise
def pack_index(path):
    out = []
    with open(path, "rb") as f:
        magic = f.read(4)
        mask, dep_n, dep_sz, n, idx_sz = struct.unpack("<5I", f.read(20))
        buf = 24 if mask & 0x100 else 4
        f.read(buf); f.read(dep_sz)
        off = 24 + buf + dep_sz + idx_sz
        blob = f.read(idx_sz)
    p = 0
    for _ in range(n):
        size = struct.unpack_from("<I", blob, p)[0]; p += 4
        if mask & 0x40: p += 4
        if magic == b"PFH5": p += 1
        e = blob.index(b"\0", p); name = blob[p:e].decode("utf-8", "replace"); p = e + 1
        out.append((off, size, name)); off += size
    return out

reads, writes, failed = set(), set(), set()
pack_reads = defaultdict(list)
for e in events:
    if e["kind"] == "open":
        p = e["path"]
        if not e["ok"]: failed.add(p); continue
        if p.lower().endswith(".pack"): continue
        (writes if e["access"] & (GENERIC_WRITE | FILE_WRITE_DATA) or e["disp"] in (1, 2, 4, 5) else reads).add(p)
    elif e["kind"] in ("read", "map") and e["off"] >= 0:
        pack_reads[e["path"]].append((e["off"], e["n"]))

packs = {}
for path, spans in pack_reads.items():
    try: idx = pack_index(path)
    except OSError: continue
    starts = [o for o, _, _ in idx]
    import bisect
    hit = set()
    for off, n in spans:
        i = bisect.bisect_right(starts, off) - 1
        while 0 <= i < len(idx) and idx[i][0] < off + n:
            if idx[i][0] + idx[i][1] > off: hit.add(idx[i][2])
            i += 1
    packs[path] = sorted(hit)

result = {"label": LABEL, "seconds": round(secs, 1), "events": len(events),
          "writes": sorted(writes), "reads": sorted(reads), "failed_opens": sorted(failed)[:500],
          "packs_opened": sorted({e["path"] for e in events if e["kind"] == "open" and e["path"].lower().endswith(".pack")}),
          "pack_entries_read": packs,
          "moves": [e for e in events if e["kind"] == "move"][:2000]}
(OUT / f"{LABEL}.json").write_text(json.dumps(result, indent=1), encoding="utf-8")
print(json.dumps({k: (len(v) if isinstance(v, (list, dict)) else v) for k, v in result.items()}, indent=1))
