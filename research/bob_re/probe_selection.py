#!/usr/bin/env python3
"""Which actions does WH3's BOB select for a configuration? Starts BOB headless in a scratch kit, waits for bob.log's
"N action(s) were selected" and the sub-tree headers, prints them and kills BOB before any action does real work.

  probe_selection.py <scratch kit> --consumer <kit path> [--consumer ...] [--provider <kit path> ...] [--processor Terrain]

Kit paths in BOB's notation: <raw>/terrain/campaigns/m/m.terry, <working>/terrain/campaigns/m/shroud_heights.dds.
"""
import os, re, subprocess, sys, time

kit = sys.argv[1]
args = sys.argv[2:]
def opts(name): return [args[i + 1] for i, a in enumerate(args) if a == name]
consumers, providers = opts("--consumer"), opts("--provider")
processors = opts("--processor") or ["Terrain"]
directories = opts("--directory") or sorted({c[:c.rfind("/") + 1] for c in consumers + providers})
binaries = os.path.join(kit, "binaries")
esc = lambda s: s.replace("&", "&amp;").replace("<", "&lt;")
def block(tag, items): return f"    <{tag}/>" if not items else "\n".join([f"    <{tag}>"] + [f"        <entry>{esc(i)}</entry>" for i in items] + [f"    </{tag}>"])
xml = "\n".join(["<bob_configuration>", "    <processors>"] + [f"        <processor>{p}</processor>" for p in processors] + ["    </processors>", "    <directories>"]
                + [f"        <directory>{esc(d)}</directory>" for d in directories] + ["    </directories>", "    <global_rules/>",
                "    <retail>1</retail>", "    <silent>1</silent>", "    <show_errors>0</show_errors>", "    <no_progress>1</no_progress>",
                "    <fail_on_assert>0</fail_on_assert>", "    <scan_perforce>0</scan_perforce>", "    <keep_output>1</keep_output>",
                "    <load_asset_graph>0</load_asset_graph>", "    <get_latest>0</get_latest>",
                block("selected_providers", providers), block("selected_consumers", consumers), "</bob_configuration>", ""])
open(os.path.join(binaries, "BOB", "atlaswh3_probe_configuration.xml"), "w", encoding="utf-8", newline="\n").write(xml)
log = os.path.join(binaries, "bob.log")
try: os.remove(log)
except OSError: pass
p = subprocess.Popen([os.path.join(binaries, "bob.modder.x64.exe"), "/configuration:atlaswh3_probe", "/nosplashscreen", "/dont_stop_on_error"], cwd=binaries)
t0, text = time.time(), ""
while time.time() - t0 < 300 and p.poll() is None:
    time.sleep(0.5)
    try: text = open(log, encoding="utf-8", errors="replace").read()
    except OSError: continue
    m = re.search(r"(\d+) action\(s\) were selected", text)
    if m and text.count("=== Running following sub-tree") >= int(m.group(1)):
        break
subprocess.run(["taskkill", "/PID", str(p.pid), "/F", "/T"], capture_output=True)
m = re.search(r"(\d+) action\(s\) were selected", text)
print("selected:", m.group(1) if m else None, f"({time.time() - t0:.1f} s)")
for h in re.findall(r"=== Running following sub-tree in thread \d+: ===\n(.+)\n", text):
    print("   ", h.split(" (")[0].split("(d:")[0].strip())
