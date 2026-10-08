#!/usr/bin/env python3
"""Summarise a trace_files.py result: reads / writes / failed opens grouped by folder, and the pack entries read.

  trace_summary.py <label> [depth] [--grep text]
"""
import collections, json, re, sys
from pathlib import Path

r = json.loads((Path(__file__).parent / "trace_out" / f"{sys.argv[1]}.json").read_text(encoding="utf-8"))
depth = int(sys.argv[2]) if len(sys.argv) > 2 and sys.argv[2].isdigit() else 5
grep = sys.argv[sys.argv.index("--grep") + 1].lower() if "--grep" in sys.argv else None
BS = chr(92)


def norm(p):
    p = p.lower().replace(BS, "/")
    p = re.sub(r".*assembly_kit_atlaswh3/", "<kit>/", p)
    p = re.sub(r".*assembly_kit/", "<userkit>/", p)
    p = re.sub(r".*total war warhammer iii/", "<game>/", p)
    return p


def bucket(p):
    return "/".join(norm(p).split("/")[:depth])


print(f"{r['label']}: {r['seconds']} s, {r['events']} events")
for k in ("writes", "reads", "failed_opens"):
    items = [p for p in r[k] if grep is None or grep in norm(p)]
    c = collections.Counter(bucket(p) for p in items)
    print(f"{k}: {len(items)}")
    for b, n in c.most_common(30):
        print(f"   {n:6d}  {b}")
print("moves:", dict(collections.Counter(m["fn"] for m in r["moves"])))
for pack, entries in r["pack_entries_read"].items():
    print(f"pack {norm(pack)}: {len(entries)} entries read")
    for e in entries[:20]:
        print("    ", e)
