#!/usr/bin/env python3
"""Observe (and optionally force) WH3 BOB's action selection with selection.js.

  probe_frida_selection.py <kit> <config name> [--force CLASS,CLASS] [--run]

Without --run BOB is killed as soon as bob.log lists the selected actions; with --run it runs to the end.
"""
import os, re, sys, time
import frida

kit, config = sys.argv[1], sys.argv[2]
force = sys.argv[sys.argv.index("--force") + 1].split(",") if "--force" in sys.argv else None
run = "--run" in sys.argv
binaries = os.path.join(kit, "binaries")
log = os.path.join(binaries, "bob.log")
try: os.remove(log)
except OSError: pass
device = frida.get_local_device()
pid = device.spawn([os.path.join(binaries, "bob.modder.x64.exe"), f"/configuration:{config}", "/nosplashscreen", "/dont_stop_on_error"], cwd=binaries)
session = device.attach(pid)
done = {"exit": False}
session.on("detached", lambda *a: done.update(exit=True))
script = session.create_script(open(os.path.join(os.path.dirname(__file__), "selection.js"), encoding="utf-8").read())
script.on("message", lambda m, d: print(m["payload"] if m["type"] == "send" else m, flush=True))
# bob_shared loads at start-up: wait for it before loading the script
device.resume(pid)
t0 = time.time()
while time.time() - t0 < 30:
    try:
        session.create_script("Process.getModuleByName('bob_shared.modder.x64.dll')").load(); break
    except Exception: time.sleep(0.05)
script.load()
if force: script.post({"type": "force", "classes": force})
text = ""
while not done["exit"] and time.time() - t0 < 7200:
    time.sleep(0.5)
    try: text = open(log, encoding="utf-8", errors="replace").read()
    except OSError: continue
    m = re.search(r"(\d+) action\(s\) were selected", text)
    if not run and m and text.count("=== Running following sub-tree") >= int(m.group(1)):
        break
if not done["exit"]:
    device.kill(pid)
m = re.search(r"(\d+) action\(s\) were selected", text)
print("selected:", m.group(1) if m else None)
for h in re.findall(r"=== Running following sub-tree in thread \d+: ===\n(.+)\n", text):
    print("   ", h.split("(d:")[0].strip())
