"""bob-mcp: stdio MCP server that drives BOB, the Total War: Three Kingdoms Assembly Kit build tool.

How BOB is driven (see bobgui.py for details):
  * headless  - BOB started as `bob.modder.x64.exe /configuration:<name> /nosplashscreen /dont_stop_on_error`
                (the same switches Terry's "Process with BOB" uses) with binaries/BOB/<name>_configuration.xml
                holding <silent>1</silent> and <selected_actions><action>SHORT NAME</action></selected_actions>.
                No window is shown; BOB runs the matching actions and exits.
  * gui       - BOB's Qt window is read with UI Automation (MSAA proxy) and driven with PostMessage to BOB's
                own HWNDs only (never SendInput / foreground tricks), so input can never reach CAIME or any
                other application.  Used for listing actions, and as an alternative way to run one.

Only one BOB runs at a time (the server refuses to start a second one); every run selects exactly one action
and is verified from bob.log ("N action(s) were selected for execution." must be 1).
"""
from __future__ import annotations

import concurrent.futures
import datetime as _dt
import os
import re
import shutil
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from mcp.server.fastmcp import FastMCP  # noqa: E402

PROJECT = r"Z:\Claude\TerryClone"
RUNS_DIR = os.path.join(PROJECT, "output", "bob_runs")
GUI_CONFIG = "_bobmcp_gui"
RUN_CONFIG = "_bobmcp_run"

mcp = FastMCP("bob")


# All BOB work happens on one COM-initialised worker thread: serialises runs and keeps UIA happy.
def _init_worker():
    import comtypes
    comtypes.CoInitializeEx(comtypes.COINIT_APARTMENTTHREADED)


_POOL = concurrent.futures.ThreadPoolExecutor(max_workers=1, initializer=_init_worker)
_STATE = {"gui_pid": None, "gui_key": None}


def _on_worker(fn, *a, **kw):
    return _POOL.submit(fn, *a, **kw).result()


def _g():
    import bobgui
    return bobgui


# ---------------------------------------------------------------------------------------------- helpers
def _auto_dirs(tree: str, parts: list[str]) -> list[str]:
    low = [p.lower() for p in parts]
    if low[:2] == ["terrain", "campaigns"] and len(parts) >= 3:
        m = parts[2]
        return [f"<raw>/terrain/campaigns/{m}", f"<working>/terrain/campaigns/{m}", "<working>/"]
    if low[:1] == ["campaign_maps"] and len(parts) >= 2:
        m = parts[1]
        return [f"<working>/campaign_maps/{m}", f"<raw>/terrain/campaigns/{m}",
                f"<working>/terrain/campaigns/{m}", "<working>/"]
    return [f"<{tree}>/" + "/".join(parts), "<working>/"]


def _auto_processors(parts: list[str]) -> list[str]:
    low = [p.lower() for p in parts]
    if low[:1] == ["campaign_maps"]:
        return ["Terrain", "Texture"]
    return ["Terrain"]


def _short_name(action_name: str) -> str:
    """'Terrain / Global Mesh (c:/...)' -> 'Global Mesh' (ACTION_INTERFACE::get_name(false))."""
    n = action_name.strip()
    if " / " in n:
        n = n.split(" / ", 1)[1]
    n = re.sub(r"\s*\(.*$", "", n).strip()
    return n


def _norm(s: str) -> str:
    return re.sub(r"\s+", " ", s.replace("\\", "/").lower()).strip()


def _read(path: str) -> str:
    try:
        with open(path, "rb") as f:
            return f.read().decode("utf-8", "replace")
    except FileNotFoundError:
        return ""


def _log_path(name: str) -> str:
    return os.path.join(_g().BIN, name)


def _log_summary(text: str | None = None, tail: int = 40) -> dict:
    g = _g()
    text = _read(_log_path("bob.log")) if text is None else text
    err = _read(_log_path("bob_error.log"))
    warn = _read(_log_path("bob_warnings.log"))
    startup = _read(_log_path("bob_startup_error.log"))
    m = re.search(r"(\d+) action\(s\) were selected for execution", text)
    lines = text.splitlines()
    return {
        "actions_selected": int(m.group(1)) if m else None,
        "headers": re.findall(r"^=== (.*) ===\s*$", text, re.M),
        "finished": "All actions took" in text,
        "duration_line": next((l for l in lines if l.startswith("All actions took")), None),
        "failed_to_find_tile": text.count("Failed to find tile"),
        "error_lines_in_bob_log": sum(1 for l in lines if re.search(r"\berror", l, re.I)),
        "bob_error_log_lines": len([l for l in err.splitlines() if l.strip()]),
        "bob_warnings_log_lines": len([l for l in warn.splitlines() if l.strip()]),
        "bob_startup_error_log_lines": len([l for l in startup.splitlines() if l.strip()]),
        "bob_error_log_head": err[:3000],
        "bob_log_tail": "\n".join(lines[-tail:]),
        "bob_log_mtime": _dt.datetime.fromtimestamp(os.path.getmtime(_log_path("bob.log"))).isoformat(" ", "seconds")
        if os.path.exists(_log_path("bob.log")) else None,
        "logs_dir": g.BIN,
    }


def _capture(label: str) -> str:
    ts = _dt.datetime.now().strftime("%Y%m%d_%H%M%S")
    safe = re.sub(r"[^A-Za-z0-9_.-]+", "_", label).strip("_") or "run"
    d = os.path.join(RUNS_DIR, f"{ts}_{safe}")
    os.makedirs(d, exist_ok=True)
    for n in _g().LOGS:
        p = _log_path(n)
        if os.path.exists(p):
            shutil.copy2(p, d)
    return d


def _other_bob_running() -> list[int]:
    return [p for p in _g().bob_pids() if p != _STATE["gui_pid"]]


def _ensure_gui(tree: str, parts: list[str], directories, processors) -> "object":
    g = _g()
    dirs = directories or _auto_dirs(tree, parts)
    procs = processors or _auto_processors(parts)
    key = (tuple(dirs), tuple(procs))
    pid = _STATE["gui_pid"]
    if pid and pid in g.bob_pids() and _STATE["gui_key"] == key and "Select Data Files" in g.main_title(pid):
        gui = g.BobGui(pid)
        gui.popup_cancel()
        return gui
    if g.bob_pids():
        raise RuntimeError(f"BOB is already running (pids {g.bob_pids()}); call bob_close first "
                           "(only one BOB may run at a time).")
    g.write_config(GUI_CONFIG, directories=dirs, processors=procs, silent=False)
    proc = g.launch(GUI_CONFIG)
    _STATE.update(gui_pid=proc.pid, gui_key=key)
    t0 = time.time()
    while time.time() - t0 < 180:
        if proc.poll() is not None:
            raise RuntimeError(f"BOB exited during start-up (code {proc.returncode}); "
                               f"startup log: {_read(_log_path('bob_startup_error.log'))[:1500]}")
        if "Select Data Files" in g.main_title(proc.pid):
            break
        time.sleep(1)
    else:
        raise RuntimeError("BOB file-selection window did not appear within 180 s")
    gui = g.BobGui(proc.pid)
    for _ in range(30):  # wait for the trees to be populated
        try:
            if gui.rows(tree)[0]:
                break
        except Exception:
            pass
        time.sleep(1)
    return gui


def _open_popup(gui, tree: str, parts: list[str]):
    # the action popup opens next to the clicked row; if it falls below the screen edge its rows capture black
    # and read as "checked" - keep the main window at the top-left of the primary screen
    try:
        import win32api, win32con, win32gui
        h = gui.main_hwnd(); l, t, r, b = win32gui.GetWindowRect(h)
        sh = win32api.GetSystemMetrics(win32con.SM_CYSCREEN)
        if l != 50 or t != 30 or b > sh - 40:
            win32gui.SetWindowPos(h, 0, 50, 30, r - l, min(b - t, sh - 300), win32con.SWP_NOZORDER)
            time.sleep(1)
        win32api.SetCursorPos((300, 200))   # the Qt popup opens at the mouse cursor
    except Exception:
        pass
    row = gui.navigate(tree, parts)
    st = gui.row_state(row)
    if st != "unchecked":
        gui.click_button("unselectAllButton")
        time.sleep(1)
        row = gui.navigate(tree, parts)
    gui.click_checkbox(row)
    if gui.popup() is None:
        raise RuntimeError("the action popup did not open (is Preferences > 'Show action selection popup' on?)")
    time.sleep(0.8)
    return gui.popup_items()


# ------------------------------------------------------------------------------------------------- tools
@mcp.tool()
def bob_list_actions(path: str, directories: list[str] | None = None, processors: list[str] | None = None,
                     keep_open: bool = True) -> dict:
    """List the Provider and Consumer actions BOB offers for a file or folder (GUI popup, nothing is run).

    path: kit path such as 'raw_data/terrain/campaigns/3k_guandu_map',
          'working_data/terrain/campaigns/3k_guandu_map/global_map/tile_list.bin' or an absolute kit path.
    directories / processors: BOB scan scope; default is derived from the path (for a campaign map:
          <raw>/terrain/campaigns/<map>, <working>/terrain/campaigns/<map>, <working>/ with the Terrain processor).
    keep_open: leave the BOB GUI open for further listings (it locks packs; call bob_close when done).
    Returns full action names ('Processor / Name (path)'), usable as action_name in bob_run_action.
    Note: 'provider' actions produce the ticked file; 'consumer' actions read it.
    """
    def work():
        g = _g()
        tree, parts = g.split_ak_path(path)
        gui = _ensure_gui(tree, parts, directories, processors)
        try:
            items = _open_popup(gui, tree, parts)
        finally:
            gui.popup_cancel()  # Escape = cancel, unticks the file again
        out = {k: [i["name"] for i in v if i["name"] != "<All>"] for k, v in items.items()}
        out["path"] = g.logical(tree, parts)
        out["bob_pid"] = gui.pid
        if not keep_open:
            g.close_gracefully(gui.pid)
            _STATE.update(gui_pid=None, gui_key=None)
        return out
    return _on_worker(work)


@mcp.tool()
def bob_list_children(path: str, directories: list[str] | None = None,
                      processors: list[str] | None = None) -> dict:
    """List the entries BOB shows under a folder in its Raw/Working/Retail tree (including virtual files that
    actions would produce). Useful to discover what can be ticked. Opens the BOB GUI if needed."""
    def work():
        g = _g()
        tree, parts = g.split_ak_path(path)
        gui = _ensure_gui(tree, parts, directories, processors)
        return {"path": g.logical(tree, parts), "children": gui.children_of(tree, parts)}
    return _on_worker(work)


def _map_of(parts: list[str]) -> str | None:
    low = [p.lower() for p in parts]
    if low[:2] == ["terrain", "campaigns"] and len(parts) >= 3:
        return parts[2]
    if low[:1] == ["campaign_maps"] and len(parts) >= 2:
        return parts[1]
    return None


def _validate_scope(dirs: list[str], parts: list[str]) -> str | None:
    """Headless runs must never scan another map's raw folder (that is how the 3k_dlc07_main_map Global Mesh
    got selected). Allowed: directories inside the target map's own folders, plus exactly '<working>/'
    (needed for BOB's working-data view). Returns an error string or None."""
    m = _map_of(parts)
    for d in dirs:
        dn = d.replace("\\", "/").rstrip("/").lower()
        if dn == "<working>":
            continue
        if m is None:
            return f"headless runs are only supported for map paths (terrain/campaigns/<map>, campaign_maps/<map>)"
        segs = dn.split("/")
        if m.lower() not in segs:
            return (f"directory {d!r} is outside map {m!r}; parent folders such as '<raw>/terrain/campaigns' "
                    "would create actions for other maps and are refused")
    return None


def _kill_tree(pid: int) -> bool:
    import subprocess
    subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], capture_output=True)
    for _ in range(50):
        if pid not in _g().bob_pids():
            return True
        time.sleep(0.1)
    return False


def _run_headless(tree, parts, action_name, directories, processors, timeout, capture_label, file_filter=True):
    g = _g()
    short = _short_name(action_name)
    dirs = directories or _auto_dirs(tree, parts)
    procs = processors or _auto_processors(parts)
    err = _validate_scope(dirs, parts)
    if err:
        return {"mode": "headless", "result": "refused", "reason": err, "directories": dirs}
    # File filter: only actions that consume or produce the given path (for a folder, anything under it)
    # may be selected on top of the short-name match. It can only narrow the selection; if BOB's
    # filter semantics don't match, the run ends as not_found and nothing is built.
    lp = g.logical(tree, parts)
    is_dir = os.path.isdir(os.path.join(g.AK, {"raw": "raw_data", "working": "working_data",
                                               "retail": "retail"}[tree], *parts))
    filt = [lp + "/..." if is_dir else lp] if file_filter else []
    g.write_config(RUN_CONFIG, directories=dirs, processors=procs, actions=[short], silent=True,
                   consumers=filt, providers=filt)
    log = _log_path("bob.log")
    before = os.path.getmtime(log) if os.path.exists(log) else 0
    t0 = time.time()
    proc = g.launch(RUN_CONFIG)
    verdict, guard = None, None
    # Backstop guard (primary protection is the scope validation + file filter above). BOB may only flush
    # bob.log late, so this is best-effort: poll every 0.2 s and kill the whole process tree on mismatch.
    while True:
        rc = proc.poll()
        text = _read(log) if os.path.exists(log) and os.path.getmtime(log) > before else ""
        if text and guard is None:
            m = re.search(r"(\d+) action\(s\) were selected for execution", text)
            hdr = re.search(r"^=== (.*) ===\s*$", text, re.M)
            if m and (hdr or int(m.group(1)) != 1):
                n = int(m.group(1))
                guard = "ok"
                if n != 1:
                    guard = f"{n} actions were selected (expected exactly 1)"
                elif not _header_matches(hdr.group(1), action_name, short, parts):
                    guard = f"unexpected action started: {hdr.group(1)!r}"
                if guard != "ok":
                    killed = _kill_tree(proc.pid)
                    guard += (f" - BOB killed after {round(time.time() - t0, 1)} s" if killed
                              else " - KILL FAILED, BOB still running")
                    verdict = "aborted"
                    break
        if rc is not None:
            break
        if time.time() - t0 > timeout:
            verdict = "timeout (BOB still running; use bob_status / bob_close)"
            break
        time.sleep(0.2)
    rewritten = os.path.exists(log) and os.path.getmtime(log) > before
    s = _log_summary() if rewritten else {"note": "bob.log was not rewritten: no action matched "
                                                  f"(short name {short!r}, file filter {filt}) in {dirs}"}
    if verdict is None and not rewritten and proc.poll() is not None:
        verdict = "not_found"
    if verdict is None:
        verdict = "success" if rewritten and s.get("finished") and s.get("actions_selected") == 1 \
            and not s.get("bob_error_log_lines") else "failed"
    res = {"mode": "headless", "result": verdict, "guard": guard, "exit_code": proc.poll(),
           "short_name": short, "config": os.path.join(g.BIN, "BOB", f"{RUN_CONFIG}_configuration.xml"),
           "directories": dirs, "processors": procs, "file_filter": filt, "seconds": round(time.time() - t0, 1), **s}
    if capture_label and rewritten:
        res["captured_to"] = _capture(capture_label)
    return res


def _header_matches(header: str, action_name: str, short: str, parts: list[str]) -> bool:
    h = _norm(header)
    if " / " in action_name and "(" in action_name:
        return _norm(action_name) == h or _norm(action_name).replace("  ", " ") == h
    return _norm(short) in h and parts[-1].lower() in h


def _run_gui(tree, parts, action_name, directories, processors, timeout, capture_label, close_after,
             allow_helpers=()):
    g = _g()
    gui = _ensure_gui(tree, parts, directories, processors)
    items = _open_popup(gui, tree, parts)
    allitems = [(k, i) for k, v in items.items() for i in v if i["name"] != "<All>"]
    want = _norm(action_name)
    exact = [(k, i) for k, i in allitems if _norm(i["name"]) == want]
    if not exact:
        short = _norm(_short_name(action_name))
        exact = [(k, i) for k, i in allitems if _norm(_short_name(i["name"])) == short]
    if len(exact) != 1:
        gui.popup_cancel()
        return {"mode": "gui", "result": "not_found" if not exact else "ambiguous",
                "offered": {k: [i["name"] for i in v] for k, v in items.items()}}
    kind, target = exact[0]
    already = [i["name"] for _, i in allitems if i["state"] != "unchecked"]
    if already and os.environ.get("BOB_UNTICK_OTHERS") == "1":
        # opt-in: a freshly installed project comes up with every action pre-ticked - untick them (click toggles)
        for _, i in allitems:
            if i["state"] != "unchecked":
                gui.popup_check(i)
        time.sleep(0.8)
        items = gui.popup_items()
        allitems = [(k, i) for k, v in items.items() for i in v if i["name"] != "<All>"]
        target = next((i for _, i in allitems if i["name"] == target["name"]), target)
        already = [i["name"] for _, i in allitems if i["state"] != "unchecked"]
    if already:
        gui.popup_cancel()
        return {"mode": "gui", "result": "refused", "reason": f"actions pre-ticked: {already}"}
    gui.popup_check(target)
    time.sleep(0.8)
    items = gui.popup_items()
    ticked = [i["name"] for v in items.values() for i in v if i["name"] != "<All>" and i["state"] != "unchecked"]
    if ticked != [target["name"]]:
        gui.popup_cancel()
        return {"mode": "gui", "result": "refused",
                "reason": "ticking the action also ticked other actions (dependencies) or did not tick it",
                "ticked": ticked}
    if not gui.popup_commit():
        gui.popup_cancel()
        return {"mode": "gui", "result": "failed", "reason": "could not close the action popup"}
    log = _log_path("bob.log")
    before = os.path.getmtime(log) if os.path.exists(log) else 0
    if not gui.click_button("start_button"):
        return {"mode": "gui", "result": "failed", "reason": "Start button not enabled"}
    t0 = time.time()
    verdict, guard = None, None
    while True:
        title = g.main_title(gui.pid)
        alive = gui.pid in g.bob_pids()
        if os.path.exists(log) and os.path.getmtime(log) > before and guard is None:
            m = re.search(r"(\d+) action\(s\) were selected for execution", _read(log))
            if m:
                guard = "ok" if int(m.group(1)) == 1 else f"{m.group(1)} actions selected - BOB killed"
                if guard != "ok" and allow_helpers:
                    guard = "helpers"                       # opt-in: BOB adds helper actions (Initialise warscape ...)
                if guard != "ok" and guard != "helpers":
                    g.kill_all()
                    verdict = "aborted"
                    break
        if guard == "helpers" and os.path.exists(log):     # every action BOB starts must be the target or an allowed helper
            want = _norm(action_name)
            for hdr in re.findall(r"^=== (.+?) ===$", _read(log), re.M):
                if _norm(hdr) != want and not any(h.lower() in hdr.lower() for h in allow_helpers):
                    g.kill_all(); verdict = f"aborted: unexpected action {hdr!r}"; break
            if verdict: break
        if "Done" in title or not alive:
            break
        if time.time() - t0 > timeout:
            verdict = "timeout (BOB still running; use bob_status / bob_close)"
            break
        time.sleep(1)
    title = g.main_title(gui.pid)
    s = _log_summary()
    if verdict is None:
        verdict = "success" if s.get("finished") and s.get("actions_selected") == 1 \
            and not s.get("bob_error_log_lines") else "failed"
    res = {"mode": "gui", "result": verdict, "guard": guard, "action": target["name"], "list": kind,
           "final_title": title, "seconds": round(time.time() - t0, 1), **s}
    if capture_label:
        res["captured_to"] = _capture(capture_label)
    if close_after and "timeout" not in verdict:
        g.close_gracefully(gui.pid) or g.kill_all()
        _STATE.update(gui_pid=None, gui_key=None)
    return res


@mcp.tool()
def bob_run_action(path: str, action_name: str, timeout: int = 3600, mode: str = "headless",
                   directories: list[str] | None = None, processors: list[str] | None = None,
                   capture_label: str | None = None, close_after: bool = True,
                   file_filter: bool = True, allow_helpers: list[str] | None = None) -> dict:
    """Run exactly ONE BOB action and wait for it to finish.

    path: the file/folder the action belongs to (as used in bob_list_actions), e.g.
          'raw_data/terrain/campaigns/3k_guandu_map'.
    action_name: full name from bob_list_actions ('Terrain / Tilemap (c:/.../3k_guandu_map/)') or the short
          name ('Tilemap', 'Global Mesh').
    mode: 'headless' (default) - silent BOB configuration, no window; the scan scope is restricted to the
          map's folders and <selected_actions> to the short name. A guard reads bob.log as soon as BOB starts
          and kills BOB if it selected anything other than exactly one matching action.
          'gui' - ticks the path and the single action in BOB's window (verifying no dependent action got
          ticked), presses Start and waits for 'BOB - Done'.
    Headless safety: directories outside the target map's folders (other than '<working>/') are refused, and
          by default (file_filter=True) <selected_consumers>/<selected_providers> restrict the selection to
          actions reading or writing `path` (folder: '<path>/...'). If that yields not_found for an action
          you know exists, retry with file_filter=False (scope validation still applies).
    capture_label: if given, bob*.log are copied to output/bob_runs/<timestamp>_<label>/.
    allow_helpers (gui mode): action-name substrings BOB may run alongside the target (it adds e.g.
          'Initialise warscape' to Generate Camera Height Map); any other action started still kills BOB.
    Returns result (success/failed/aborted/timeout/not_found/refused), the bob.log tail, the number of
    'Failed to find tile' lines, error-line counts and the contents of bob_error.log.
    """
    def work():
        g = _g()
        tree, parts = g.split_ak_path(path)
        if mode == "gui":
            if _other_bob_running():
                return {"result": "refused", "reason": f"another BOB is running: {_other_bob_running()}"}
            return _run_gui(tree, parts, action_name, directories, processors, timeout, capture_label, close_after,
                            tuple(allow_helpers or ()))
        if g.bob_pids():
            return {"result": "refused", "reason": f"BOB is already running (pids {g.bob_pids()}); call bob_close "
                                                   "first - only one BOB may run at a time"}
        return _run_headless(tree, parts, action_name, directories, processors, timeout, capture_label,
                             file_filter)
    return _on_worker(work)


@mcp.tool()
def bob_get_log(log: str = "bob", tail: int = 200, grep: str | None = None) -> dict:
    """Read a BOB log from assembly_kit/binaries: log = bob | error | warnings | startup_error | db.
    Returns the last `tail` lines (optionally only lines matching the regex `grep`) plus a summary
    (actions selected, action headers, finished flag, 'Failed to find tile' count, error counts)."""
    name = {"bob": "bob.log", "error": "bob_error.log", "warnings": "bob_warnings.log",
            "startup_error": "bob_startup_error.log", "db": "bob_db.log"}.get(log, log)
    text = _read(_log_path(name))
    lines = text.splitlines()
    if grep:
        rx = re.compile(grep, re.I)
        lines = [l for l in lines if rx.search(l)]
    s = _log_summary()
    s.pop("bob_log_tail", None)
    return {"log": name, "total_lines": len(text.splitlines()), "lines": "\n".join(lines[-tail:]), "summary": s}


@mcp.tool()
def bob_capture_logs(label: str) -> dict:
    """Copy binaries/bob*.log to Z:/Claude/TerryClone/output/bob_runs/<timestamp>_<label>/."""
    return {"captured_to": _capture(label)}


@mcp.tool()
def bob_status() -> dict:
    """Running BOB processes with their window titles ('BOB - Select Data Files To Build', 'BOB - Done - ...')."""
    g = _g()
    return {"bob_processes": [{"pid": p, "windows": [t for _, _, t in g.windows(p)]} for p in g.bob_pids()],
            "gui_pid_managed_by_server": _STATE["gui_pid"], "bob_log": _log_summary(tail=5)}


@mcp.tool()
def bob_close(force: bool = False) -> dict:
    """Close every running BOB (WM_CLOSE to BOB's own windows; kill if still alive after 20 s or force=True).
    BOB locks the mod pack while open, so close it when done."""
    def work():
        g = _g()
        pids = g.bob_pids()
        res = {}
        for p in pids:
            ok = False if force else g.close_gracefully(p)
            if not ok:
                g.kill_all()
            res[p] = "closed" if ok else "killed"
        _STATE.update(gui_pid=None, gui_key=None)
        return {"closed": res, "still_running": g.bob_pids()}
    return _on_worker(work)


@mcp.tool()
def bob_diagnose(map_name: str) -> dict:
    """Check the prerequisites for building a campaign map's terrain (esp. the Global Mesh action):
    rules.bob flags, and whether BOB's database (raw_data/EmpireDesignData, NOT raw_data/db) has the
    campaign_map_playable_areas / campaign_maps records. BOB only creates 'Terrain / Global Mesh' when
    generate_global_mesh = true AND campaign_map_playable_areas has a row with mapname == map_name;
    otherwise it silently creates an error action 'Failed to find <map> in the database'."""
    g = _g()
    raw = os.path.join(g.AK, "raw_data")
    rules = _read(os.path.join(raw, "terrain", "campaigns", "rules.bob"))
    out = {"rules.bob": rules or "MISSING",
           "generate_global_mesh": bool(re.search(r"generate_global_mesh\s*=\s*true", rules, re.I))}
    for table in ("campaign_map_playable_areas", "campaign_maps"):
        for folder in ("EmpireDesignData", "db"):
            t = _read(os.path.join(raw, folder, f"{table}.xml"))
            out[f"{folder}/{table}.xml has {map_name}"] = bool(
                re.search(rf"<mapname>\s*{re.escape(map_name)}\s*</mapname>", t)) if t else "file missing"
    w = os.path.join(g.AK, "working_data", "terrain", "campaigns", map_name)
    for f in ("tile_list.bin", os.path.join("global_map", "tile_list.bin")):
        p = os.path.join(w, f)
        out[f"working {f}"] = (f"{os.path.getsize(p)} bytes, "
                               f"{_dt.datetime.fromtimestamp(os.path.getmtime(p)).isoformat(' ', 'seconds')}"
                               if os.path.exists(p) else "missing")
    out["global_mesh_available"] = out["generate_global_mesh"] and \
        out.get(f"EmpireDesignData/campaign_map_playable_areas.xml has {map_name}") is True
    return out


if __name__ == "__main__":
    mcp.run()
