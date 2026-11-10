"""disasm_named.py <dll> <function rva hex> <size hex>: disassemble a range, showing the string literal of each
RIP-relative target and naming call targets by the DLL's exports (with xref_strings.py: a serialiser's field order
and version gates). KIT_BINARIES overrides the WH3 kit's binaries folder."""
import os, sys, re, capstone, pefile
B = os.environ.get("KIT_BINARIES", r"D:\SteamLibrary\steamapps\common\Total War WARHAMMER III\assembly_kit\binaries")
pe = pefile.PE(os.path.join(B, sys.argv[1]), max_symbol_exports=0x100000)
base = pe.OPTIONAL_HEADER.ImageBase
names = {e.address: (e.name or b"").decode("latin1") for e in pe.DIRECTORY_ENTRY_EXPORT.symbols}
rva, n = int(sys.argv[2], 16), int(sys.argv[3], 16)
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
def s_at(r):
    try: b = pe.get_data(r, 64)
    except Exception: return None
    z = b.split(b"\0")[0]
    return z.decode() if len(z) >= 2 and all(32 <= c < 127 for c in z) else None
for i in md.disasm(pe.get_data(rva, n), base + rva):
    t = f"{i.address - base:x}  {i.mnemonic:8} {i.op_str}"
    m = re.search(r"\[rip ([+-]) 0x([0-9a-f]+)\]", i.op_str)
    if m:
        tgt = i.address + i.size + (int(m.group(2), 16) * (1 if m.group(1) == "+" else -1)) - base
        s = s_at(tgt); t += f"   ; {hex(tgt)} " + (repr(s) if s else names.get(tgt, ""))
    if i.mnemonic == "call" and i.op_str.startswith("0x"):
        tgt = int(i.op_str, 16) - base; t += f"   ; {names.get(tgt, '')[:90]}"
    print(t)
