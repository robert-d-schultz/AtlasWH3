"""Low-level driver for BOB (Total War: Three Kingdoms Assembly Kit build tool).

Two routes:

* **Headless ("silent") route** - BOB reads ``binaries/BOB/<name>_configuration.xml`` when started with
  ``/configuration:<name>``.  With ``<silent>1</silent>`` it never shows the file-selection window; it
  selects every action whose short name (``ACTION_INTERFACE::get_name(false)``, e.g. ``Global Mesh``) is
  listed in ``<selected_actions><action>..</action></selected_actions>`` (case-insensitive) AND, if any
  ``<selected_consumers>/<selected_providers>`` entries are given, whose input (consumer) / output
  (provider) files match those filters.  It then runs them and exits.

* **GUI route** - Qt 5.11 widgets, exposed through MSAA (read via UI Automation).  UIA is used only to
  *read* (find rows, rectangles, popup items).  All input is sent with PostMessage to BOB's own
  top-level HWNDs (Qt widgets are "alien", so the top-level window dispatches by coordinates).  Posted
  messages go to that exact window and cannot reach any other application, regardless of which window
  is in the foreground.  No SendInput / SetCursorPos / SetForegroundWindow is ever used.
"""
from __future__ import annotations

import ctypes
import os
import re
import subprocess
import time
from dataclasses import dataclass

import win32api
import win32con
import win32gui
import win32process

try:
    ctypes.windll.user32.SetProcessDPIAware()
except Exception:
    pass

# BOB_AK selects another kit; default: the Steam kit
AK = os.environ.get("BOB_AK") or r"C:\Program Files (x86)\Steam\steamapps\common\Total War WARHAMMER III\assembly_kit"
BIN = os.path.join(AK, "binaries")
EXE = os.path.join(BIN, "bob.modder.x64.exe")
EXE_NAME = "bob.modder.x64.exe"
LOGS = ["bob.log", "bob_error.log", "bob_warnings.log", "bob_startup_error.log", "bob_db.log"]

TREE_IDS = {"raw": "raw_data_files", "working": "working_data_files", "retail": "retail_build_files"}
INDENT = 20  # px per tree level in BOB's QTreeView


# ----------------------------------------------------------------------------------------------- paths
def split_ak_path(path: str) -> tuple[str, list[str]]:
    """'raw_data\\terrain\\campaigns\\m' or an absolute kit path -> ('raw', ['terrain','campaigns','m'])."""
    p = path.replace("\\", "/").strip().strip("/")
    low = p.lower()
    ak = AK.replace("\\", "/").lower() + "/"
    if low.startswith(ak):
        p = p[len(ak):]
        low = p.lower()
    for prefix, tree in (("raw_data/", "raw"), ("<raw>/", "raw"), ("working_data/", "working"),
                         ("<working>/", "working"), ("retail/", "retail"), ("<retail>/", "retail")):
        if low.startswith(prefix):
            return tree, [c for c in p[len(prefix):].split("/") if c]
    raise ValueError(f"path must start with raw_data/, working_data/ or retail/ (got {path!r})")


def logical(tree: str, parts: list[str]) -> str:
    return f"<{tree}>/" + "/".join(parts)


# ------------------------------------------------------------------------------------------- process
def bob_pids() -> list[int]:
    out = subprocess.run(["tasklist", "/FI", f"IMAGENAME eq {EXE_NAME}", "/FO", "CSV", "/NH"],
                         capture_output=True, text=True).stdout
    return [int(m.group(1)) for m in re.finditer(r'^"[^"]+","(\d+)"', out, re.M)]


def windows(pid: int) -> list[tuple[int, str, str]]:
    res = []

    def cb(h, _):
        if win32gui.IsWindowVisible(h) and win32process.GetWindowThreadProcessId(h)[1] == pid:
            res.append((h, win32gui.GetClassName(h), win32gui.GetWindowText(h)))
    win32gui.EnumWindows(cb, None)
    return res


def main_title(pid: int) -> str:
    for h, cls, title in windows(pid):
        if title.startswith("BOB -"):
            return title
    return ""


def write_config(name: str, *, directories: list[str], consumers: list[str] = (), providers: list[str] = (),
                 actions: list[str] = (), silent: bool, processors: list[str] = ("Terrain",)) -> str:
    """Write binaries/BOB/<name>_configuration.xml and return its path."""
    esc = lambda s: s.replace("&", "&amp;").replace("<", "&lt;")
    lines = ["<bob_configuration>", "    <processors>"]
    lines += [f"        <processor>{esc(p)}</processor>" for p in processors]
    lines += ["    </processors>", "    <directories>"]
    lines += [f"        <directory>{esc(d)}</directory>" for d in directories]
    lines += ["    </directories>", "    <global_rules/>", "    <retail>1</retail>",
              f"    <silent>{int(silent)}</silent>", f"    <show_errors>{int(not silent)}</show_errors>",
              f"    <no_progress>{int(silent)}</no_progress>", "    <fail_on_assert>0</fail_on_assert>",
              "    <scan_perforce>0</scan_perforce>", "    <merge_for_checkin_mode>3</merge_for_checkin_mode>",
              "    <keep_output>1</keep_output>", "    <load_asset_graph>0</load_asset_graph>",
              "    <clean_asset_graph>0</clean_asset_graph>", "    <get_latest>0</get_latest>"]

    def block(tag, child, items):
        if not items:
            return [f"    <{tag}/>"]
        return [f"    <{tag}>"] + [f"        <{child}>{esc(i)}</{child}>" for i in items] + [f"    </{tag}>"]
    lines += block("selected_providers", "entry", providers)
    lines += block("selected_consumers", "entry", consumers)
    lines += block("selected_actions", "action", actions)
    lines.append("</bob_configuration>")
    path = os.path.join(BIN, "BOB", f"{name}_configuration.xml")
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    return path


def launch(config_name: str) -> subprocess.Popen:
    """Start BOB with /configuration:<name>, cwd = binaries (as Terry's 'Process with BOB' does)."""
    return subprocess.Popen([EXE, f"/configuration:{config_name}", "/nosplashscreen", "/dont_stop_on_error"],
                            cwd=BIN)


def kill_all():
    for pid in bob_pids():
        subprocess.run(["taskkill", "/PID", str(pid), "/F"], capture_output=True)


def close_gracefully(pid: int, timeout: float = 20) -> bool:
    for h, cls, title in windows(pid):
        win32gui.PostMessage(h, win32con.WM_CLOSE, 0, 0)
    t0 = time.time()
    while time.time() - t0 < timeout:
        if pid not in bob_pids():
            return True
        time.sleep(0.5)
    return False


# ------------------------------------------------------------------------------------------ GUI input
def _assert_bob(hwnd: int):
    """Refuse to send input to any window that is not owned by a running bob.modder.x64.exe."""
    pid = win32process.GetWindowThreadProcessId(hwnd)[1]
    if not hwnd or pid not in bob_pids():
        raise RuntimeError(f"refusing to send input: hwnd {hwnd} (pid {pid}) is not a BOB window")


def _post_click(hwnd: int, sx: int, sy: int, double: bool = False):
    _assert_bob(hwnd)
    x, y = win32gui.ScreenToClient(hwnd, (sx, sy))
    lp = win32api.MAKELONG(x & 0xFFFF, y & 0xFFFF)
    win32gui.PostMessage(hwnd, win32con.WM_MOUSEMOVE, 0, lp)
    win32gui.PostMessage(hwnd, win32con.WM_LBUTTONDOWN, win32con.MK_LBUTTON, lp)
    win32gui.PostMessage(hwnd, win32con.WM_LBUTTONUP, 0, lp)
    if double:
        win32gui.PostMessage(hwnd, win32con.WM_LBUTTONDBLCLK, win32con.MK_LBUTTON, lp)
        win32gui.PostMessage(hwnd, win32con.WM_LBUTTONUP, 0, lp)


def _post_wheel(hwnd: int, sx: int, sy: int, notches: int):
    _assert_bob(hwnd)
    wp = win32api.MAKELONG(0, (-120 * notches) & 0xFFFF)
    lp = win32api.MAKELONG(sx & 0xFFFF, sy & 0xFFFF)  # WM_MOUSEWHEEL uses screen coordinates
    win32gui.PostMessage(hwnd, win32con.WM_MOUSEWHEEL, wp, lp)


def _post_key(hwnd: int, vk: int):
    _assert_bob(hwnd)
    win32gui.PostMessage(hwnd, win32con.WM_KEYDOWN, vk, 0x00000001)
    win32gui.PostMessage(hwnd, win32con.WM_KEYUP, vk, 0xC0000001)


def capture(hwnd: int):
    """PrintWindow capture of one window (works while covered by other windows). Returns (PIL image, rect)."""
    import win32ui
    from PIL import Image
    l, t, r, b = win32gui.GetWindowRect(hwnd)
    w, h = r - l, b - t
    hdc = win32gui.GetWindowDC(hwnd)
    mdc = win32ui.CreateDCFromHandle(hdc)
    sdc = mdc.CreateCompatibleDC()
    bm = win32ui.CreateBitmap()
    bm.CreateCompatibleBitmap(mdc, w, h)
    sdc.SelectObject(bm)
    ctypes.windll.user32.PrintWindow(hwnd, sdc.GetSafeHdc(), 2)
    bi = bm.GetInfo()
    im = Image.frombuffer("RGB", (bi["bmWidth"], bi["bmHeight"]), bm.GetBitmapBits(True), "raw", "BGRX", 0, 1)
    win32gui.DeleteObject(bm.GetHandle())
    sdc.DeleteDC()
    mdc.DeleteDC()
    win32gui.ReleaseDC(hwnd, hdc)
    return im.convert("L"), (l, t, r, b)


def check_state(img, win_rect, box_left: int, cy: int) -> str:
    """Classify a Qt check box whose left edge is ~box_left (screen x) on row centre cy (screen y).
    Calibrated on BOB's popup: checked = black tick (min < 30); partial = grey fill; else unchecked."""
    l, t = win_rect[0], win_rect[1]
    px = []
    for x in range(box_left - l + 2, box_left - l + 16):
        for y in range(cy - t - 6, cy - t + 7):
            if 0 <= x < img.width and 0 <= y < img.height:
                px.append(img.getpixel((x, y)))
    if not px:
        return "unknown"
    if min(px) < 30:
        return "checked"
    if sum(60 <= p < 160 for p in px) > 40:
        return "partial"
    return "unchecked"


@dataclass
class Row:
    name: str
    left: int
    top: int
    right: int
    bottom: int

    @property
    def cy(self):
        return (self.top + self.bottom) // 2


class BobGui:
    """Drives one running BOB GUI instance (identified by pid)."""

    def __init__(self, pid: int):
        from pywinauto import Desktop
        from pywinauto.uia_defines import IUIA
        self.pid = pid
        self.desktop = Desktop(backend="uia")
        self.uia = IUIA().UIA_dll

    # -- windows
    def hwnd(self, cls_part: str = "", title_part: str = "") -> int | None:
        for h, cls, title in windows(self.pid):
            if cls_part in cls and title_part in title:
                return h
        return None

    def main_hwnd(self) -> int:
        h = self.hwnd(title_part="BOB - ")
        if not h:
            raise RuntimeError("BOB main window not found")
        return h

    def wait_title(self, part: str, timeout: float) -> bool:
        t0 = time.time()
        while time.time() - t0 < timeout:
            if part in main_title(self.pid):
                return True
            if self.pid not in bob_pids():
                return False
            time.sleep(1)
        return False

    def _uia_top(self, cls_name: str):
        for w in self.desktop.windows(process=self.pid):
            if w.element_info.class_name == cls_name:
                return w
        return None

    # -- tree
    def tree(self, which: str):
        top = self._uia_top("DATA_SELECTION_DIALOG")
        if top is None:
            raise RuntimeError("BOB file-selection window not found (is BOB showing 'Select Data Files To Build'?)")
        aid = TREE_IDS[which]
        for e in top.descendants(control_type="Tree"):
            if e.element_info.automation_id.endswith(aid):
                return e
        raise RuntimeError(f"tree {aid} not found")

    def rows(self, which: str) -> tuple[list[Row], tuple[int, int, int, int]]:
        t = self.tree(which)
        r = t.rectangle()
        rows = []
        for c in t.children():
            if c.element_info.control_type == "TreeItem":
                rr = c.rectangle()
                rows.append(Row(c.window_text(), rr.left, rr.top, rr.right, rr.bottom))
        return rows, (r.left, r.top, r.right, r.bottom)

    def _visible(self, row: Row, box) -> bool:
        return row.top >= box[1] + 18 and row.bottom <= box[3] - 2  # header is ~18px

    def _scroll_into_view(self, which: str, pred, tries: int = 60) -> Row:
        h = self.main_hwnd()
        for _ in range(tries):
            rows, box = self.rows(which)
            match = [r for r in rows if pred(r, rows)]
            if match:
                r = match[0]
                if self._visible(r, box):
                    return r
                notches = 3 if r.top > box[3] else -3
                _post_wheel(h, (box[0] + box[2]) // 2, (box[1] + box[3]) // 2, notches)
                time.sleep(0.4)
                continue
            return None
        return None

    def navigate(self, which: str, parts: list[str], expand_last: bool = False) -> Row:
        """Expand the tree down to parts[-1] and return that row (visible)."""
        h = self.main_hwnd()
        # Depth = indent relative to the top-level rows. Recomputed from every snapshot, because the tree can
        # scroll horizontally while navigating (absolute x positions then shift).
        self._base = None

        def at_depth(name, depth):
            def pred(r, rows):
                base = min(x.left for x in rows)
                return r.name.lower() == name.lower() and abs(r.left - (base + depth * INDENT)) <= 4
            return pred
        row = None
        for depth, name in enumerate(parts):
            row = self._scroll_into_view(which, at_depth(name, depth))
            if row is None:
                rows, _ = self.rows(which)
                base = min(x.left for x in rows)
                sib = [r.name for r in rows if abs(r.left - (base + depth * INDENT)) <= 4]
                raise RuntimeError(f"'{name}' not found in {which} tree at depth {depth}; rows there: {sib[:60]}")
            if depth < len(parts) - 1 or expand_last:
                if not self._is_expanded(which, row):
                    _post_click(h, row.left - 11, row.cy)  # expand arrow
                    time.sleep(0.8)
                    row = self._scroll_into_view(which, at_depth(name, depth))
        return row

    def _is_expanded(self, which: str, row: Row) -> bool:
        rows, _ = self.rows(which)
        for i, r in enumerate(rows):
            if r.name == row.name and r.left == row.left and r.top == row.top:
                return i + 1 < len(rows) and rows[i + 1].left > r.left
        return False

    def children_of(self, which: str, parts: list[str]) -> list[str]:
        row = self.navigate(which, parts, expand_last=True)
        rows, _ = self.rows(which)
        out, inside = [], False
        for r in rows:
            if inside:
                if r.left <= row.left:
                    break
                if r.left == row.left + INDENT:
                    out.append(r.name)
            elif r.name == row.name and r.left == row.left and r.top == row.top:
                inside = True
        return out

    def click_checkbox(self, row: Row):
        _post_click(self.main_hwnd(), row.left + 9, row.cy)

    def row_state(self, row: Row) -> str:
        img, wr = capture(self.main_hwnd())
        return check_state(img, wr, row.left, row.cy)

    # -- action popup
    def popup(self, timeout: float = 15):
        t0 = time.time()
        while time.time() - t0 < timeout:
            w = self._uia_top("ACTION_LIST_DIALOG")
            if w is not None:
                return w
            time.sleep(0.3)
        return None

    def popup_items(self) -> dict[str, list[dict]]:
        w = self._uia_top("ACTION_LIST_DIALOG")
        if w is None:
            return {}
        res = {"provider": [], "consumer": []}
        for lst in w.descendants(control_type="List"):
            kind = "provider" if lst.element_info.automation_id.endswith("provider_list") else "consumer"
            for it in lst.children():
                if it.element_info.control_type != "ListItem":
                    continue
                st = 0
                try:
                    p = it.element_info.element.GetCurrentPattern(10018).QueryInterface(
                        self.uia.IUIAutomationLegacyIAccessiblePattern)
                    st = p.CurrentState
                except Exception:
                    pass
                rr = it.rectangle()
                res[kind].append({"name": it.window_text(), "rect": (rr.left, rr.top, rr.right, rr.bottom)})
        h = self.popup_hwnd()
        if h:
            img, wr = capture(h)
            for v in res.values():
                for i in v:
                    l, t, r, b = i["rect"]
                    i["state"] = check_state(img, wr, l, (t + b) // 2)
        return res

    def popup_hwnd(self) -> int | None:
        return self.hwnd(cls_part="Popup")

    def popup_cancel(self):
        h = self.popup_hwnd()
        if h:
            _post_key(h, win32con.VK_ESCAPE)
            time.sleep(0.8)

    def popup_commit(self) -> bool:
        """Close the popup KEEPING the ticked actions: click an empty spot of the Retail Data tree
        (a click outside a Qt popup closes it; Escape would cancel and untick)."""
        rows, box = self.rows("retail")
        y = max([r.bottom for r in rows] + [box[1] + 20]) + 20
        y = max(y, box[3] - 15)
        _post_click(self.main_hwnd(), (box[0] + box[2]) // 2, y)
        time.sleep(1)
        return self.popup_hwnd() is None

    def popup_check(self, item: dict):
        h = self.popup_hwnd()
        l, t, r, b = item["rect"]
        _post_click(h, l + 9, (t + b) // 2)
        time.sleep(0.5)

    # -- buttons
    def button(self, aid_suffix: str):
        top = self._uia_top("DATA_SELECTION_DIALOG")
        for e in top.descendants(control_type="Button"):
            if e.element_info.automation_id.endswith(aid_suffix):
                return e
        return None

    def click_button(self, aid_suffix: str) -> bool:
        b = self.button(aid_suffix)
        if b is None or not b.is_enabled():
            return False
        r = b.rectangle()
        _post_click(self.main_hwnd(), (r.left + r.right) // 2, (r.top + r.bottom) // 2)
        return True
