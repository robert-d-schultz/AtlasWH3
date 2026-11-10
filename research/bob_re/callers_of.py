"""callers_of.py <dll> <target rva hex>...: every direct call (E8 rel32) or jmp (E9) to each target, with the .pdata
function holding it. Calls through the import table are found by target = the IAT slot's rva (FF 15 disp32).
KIT_BINARIES overrides the WH3 kit's binaries folder."""
import bisect, os, re, struct, sys
import pefile

B = os.environ.get("KIT_BINARIES", r"D:\SteamLibrary\steamapps\common\Total War WARHAMMER III\assembly_kit\binaries")
pe = pefile.PE(os.path.join(B, sys.argv[1]), fast_load=True)
text = next(s for s in pe.sections if s.Name.startswith(b".text"))
tb = text.get_data()
d = pe.OPTIONAL_HEADER.DATA_DIRECTORY[3]
raw = pe.get_data(d.VirtualAddress, d.Size)
fns = sorted(struct.unpack_from("<II", raw, i) for i in range(0, len(raw) - 11, 12))
starts = [f[0] for f in fns]


def fn(r):
    k = bisect.bisect_right(starts, r) - 1
    return f"{fns[k][0]:x}" if k >= 0 and fns[k][0] <= r < fns[k][1] else "?"


targets = {int(a, 16) for a in sys.argv[2:]}
for m in re.finditer(rb"[\xe8\xe9]", tb):
    i = m.start()
    if i + 5 > len(tb): break
    t = text.VirtualAddress + i + 5 + struct.unpack_from("<i", tb, i + 1)[0]
    if t in targets: print(f"{t:x} <- {text.VirtualAddress + i:x} ({'call' if tb[i] == 0xe8 else 'jmp'}) in fn {fn(text.VirtualAddress + i)}")
for m in re.finditer(rb"\xff\x15", tb):
    i = m.start()
    t = text.VirtualAddress + i + 6 + struct.unpack_from("<i", tb, i + 2)[0]
    if t in targets: print(f"{t:x} <- {text.VirtualAddress + i:x} (call [iat]) in fn {fn(text.VirtualAddress + i)}")
