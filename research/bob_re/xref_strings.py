"""xref_strings.py <dll> <string>...: RVAs of the RIP-relative lea instructions that reference each NUL-terminated
ASCII string (the kit's serialisers name every field, so this finds a record's serialiser from a field name).
KIT_BINARIES overrides the WH3 kit's binaries folder."""
import os, re, struct, sys, bisect
import pefile
B = os.environ.get("KIT_BINARIES", r"D:\SteamLibrary\steamapps\common\Total War WARHAMMER III\assembly_kit\binaries")
pe = pefile.PE(os.path.join(B, sys.argv[1]), fast_load=True)
data = pe.__data__
text = next(s for s in pe.sections if s.Name.startswith(b".text"))
tb = data[text.PointerToRawData:text.PointerToRawData + text.SizeOfRawData]
def off2rva(o):
    for s in pe.sections:
        if s.PointerToRawData <= o < s.PointerToRawData + s.SizeOfRawData: return o - s.PointerToRawData + s.VirtualAddress
# index all RIP-relative lea targets once
targets = {}
for m in re.finditer(rb"[\x48\x4c]\x8d[\x05\x0d\x15\x1d\x25\x2d\x35\x3d]", tb):
    i = m.start()
    disp = struct.unpack_from("<i", tb, i + 3)[0]
    t = text.VirtualAddress + i + 7 + disp
    targets.setdefault(t, []).append(text.VirtualAddress + i)
for s in sys.argv[2:]:
    for m in re.finditer(b"\x00" + re.escape(s.encode()) + b"\x00", data):
        rva = off2rva(m.start() + 1)
        print(s, hex(rva), [hex(x) for x in targets.get(rva, [])])
