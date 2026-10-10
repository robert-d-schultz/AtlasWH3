"""disasm_fn.py <dll name> <rva hex> [max bytes]: disassemble one function of a kit DLL (following a leading jmp thunk),
naming call/jmp targets by the DLL's exports and imports, and RIP-relative float constants by their value.
Stops at the first int3 padding after a ret/jmp. KIT_BINARIES overrides the WH3 kit's binaries folder."""
import os, struct, sys

import capstone
import pefile

MAX_EXPORTS = 0x100000   # kit DLLs export more than pefile's default 8192 names

B = os.environ.get("KIT_BINARIES", r"D:\SteamLibrary\steamapps\common\Total War WARHAMMER III\assembly_kit\binaries")
dll = os.path.join(B, sys.argv[1])
rva = int(sys.argv[2], 16)
limit = int(sys.argv[3], 16) if len(sys.argv) > 3 else 0x4000
pe = pefile.PE(dll, max_symbol_exports=MAX_EXPORTS)
base = pe.OPTIONAL_HEADER.ImageBase
names = {}
for e in getattr(getattr(pe, "DIRECTORY_ENTRY_EXPORT", None), "symbols", []) or []:
    names[e.address] = (e.name or b"").decode("latin1")
for imp in getattr(pe, "DIRECTORY_ENTRY_IMPORT", []):
    for i in imp.imports:
        names[i.address - base] = f"[{imp.dll.decode()}] " + (i.name or b"?").decode("latin1")
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
md.detail = True


def code(at, n): return pe.get_data(at, n)


# follow jmp thunks
while True:
    ins = next(md.disasm(code(rva, 16), base + rva))
    if ins.mnemonic == "jmp" and ins.op_str.startswith("0x"):
        print(f"; thunk {rva:#x} -> {int(ins.op_str, 16) - base:#x}")
        rva = int(ins.op_str, 16) - base
        continue
    break

prev = None
for ins in md.disasm(code(rva, limit), base + rva):
    note = ""
    ops = ins.op_str
    if ins.mnemonic in ("call", "jmp") or ins.mnemonic.startswith("j"):
        if ops.startswith("0x"):
            t = int(ops, 16) - base
            if t in names: note = names[t]
        elif "rip +" in ops or "rip -" in ops:
            disp = ins.disp if hasattr(ins, "disp") else 0
            for op in ins.operands:
                if op.type == capstone.x86.X86_OP_MEM:
                    t = ins.address + ins.size + op.mem.disp - base
                    if t in names: note = names[t]
    elif "rip" in ops:
        for op in ins.operands:
            if op.type == capstone.x86.X86_OP_MEM and op.mem.base == capstone.x86.X86_REG_RIP:
                t = ins.address + ins.size + op.mem.disp - base
                try:
                    raw = pe.get_data(t, 16)
                    f = struct.unpack_from("<4f", raw); d = struct.unpack_from("<d", raw)[0]
                    note = f"[{t:#x}] f32 {f[0]:.9g} ({', '.join(f'{x:.6g}' for x in f[1:])}) f64 {d:.9g}"
                except Exception:
                    note = f"[{t:#x}]"
    print(f"{ins.address - base:8x}  {ins.mnemonic:8s} {ops}" + (f"   ; {note}" if note else ""))
    if ins.mnemonic == "int3" and prev in ("ret", "jmp"):
        break
    prev = ins.mnemonic
