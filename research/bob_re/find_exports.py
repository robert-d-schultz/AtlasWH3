"""Search every DLL in the kit's binaries folder for exports containing all of the given substrings.
usage: find_exports.py <substring> [substring ...] (KIT_BINARIES overrides the WH3 kit's binaries folder)"""
import glob, os, sys
import pefile

MAX_EXPORTS = 0x100000   # kit DLLs export more than pefile's default 8192 names
B = os.environ.get("KIT_BINARIES", r"D:\SteamLibrary\steamapps\common\Total War WARHAMMER III\assembly_kit\binaries")
keys = sys.argv[1:]
for dll in sorted(glob.glob(os.path.join(B, "*.dll"))):
    try:
        pe = pefile.PE(dll, fast_load=True, max_symbol_exports=MAX_EXPORTS)
        pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_EXPORT"]])
    except Exception:
        continue
    for e in getattr(getattr(pe, "DIRECTORY_ENTRY_EXPORT", None), "symbols", []) or []:
        n = (e.name or b"").decode("latin1")
        if all(k in n for k in keys):
            print(os.path.basename(dll), hex(e.address), n[:200])
