"""fn_of.py <dll> <rva>...: the .pdata function (begin, end, size) containing each rva. KIT_BINARIES overrides the
WH3 kit's binaries folder."""
import os, sys, struct, pefile
B = os.environ.get("KIT_BINARIES", r"D:\SteamLibrary\steamapps\common\Total War WARHAMMER III\assembly_kit\binaries")
pe = pefile.PE(os.path.join(B, sys.argv[1]), fast_load=True)
d = pe.OPTIONAL_HEADER.DATA_DIRECTORY[3]
raw = pe.get_data(d.VirtualAddress, d.Size)
fns = [struct.unpack_from("<III", raw, i)[:2] for i in range(0, len(raw), 12)]
for a in sys.argv[2:]:
    r = int(a, 16)
    hit = [f for f in fns if f[0] <= r < f[1]]
    print(a, [(hex(b), hex(e), hex(e - b)) for b, e in hit])
