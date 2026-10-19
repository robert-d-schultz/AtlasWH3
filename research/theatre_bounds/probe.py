"""probe.py: every instruction in Warhammer3.exe that reads or writes map_data.esf's theatre min x.

Start this, then start the game and load the campaign. The probe waits for Warhammer3.exe, attaches Frida and puts
hardware breakpoints on the CAMPAIGN_THEATRE and MASKED_REGIONS_DATA readers; each arms a read/write watchpoint on
its copy of min x. Accesses are counted per pc (with the first backtraces) into <out>/stats.json, rewritten every
3 s, so it is complete whenever the game is closed.

  probe.py [--out DIR]
"""
import argparse, json, os, time
from pathlib import Path
import frida

HERE = Path(__file__).parent
EXE = "Warhammer3.exe"


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default=os.path.join(os.environ["LOCALAPPDATA"], "AtlasWH3", "theatre_probe", time.strftime("%Y%m%d_%H%M%S")))
    a = ap.parse_args()
    out = Path(a.out); out.mkdir(parents=True, exist_ok=True)
    print(f"output: {out}\nwaiting for {EXE} ...", flush=True)
    dev = frida.get_local_device()
    while not (procs := [p for p in dev.enumerate_processes() if p.name.lower() == EXE.lower()]):
        time.sleep(0.2)
    session = dev.attach(procs[0].pid)
    log = open(out / "probe_log.jsonl", "a", encoding="utf-8")
    ended = []

    def on_msg(msg, data):
        p = msg.get("payload") if msg["type"] == "send" else msg
        if isinstance(p, dict) and p.get("kind") == "stats":
            (out / "stats.json").write_text(json.dumps(p["stats"], indent=1))
            return
        log.write(json.dumps(dict(t=time.time(), msg=p)) + "\n"); log.flush()
        print(p, flush=True)

    session.on("detached", lambda reason, crash: (print(f"detached: {reason}", flush=True), ended.append(1)))
    script = session.create_script((HERE / "probe.js").read_text(encoding="utf-8"))
    script.on("message", on_msg)
    script.load()
    while not ended: time.sleep(0.5)
    print("game exited", flush=True)


if __name__ == "__main__":
    main()
