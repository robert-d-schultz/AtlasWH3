"""xref.py: tiny x64 PE helper for the WH3 kit DLLs (global_props research, 2026-10-09): string xrefs, containing
functions (.pdata), disassembly with import/export names and RIP-relative constants.
  xref.py str <dll> <text>           -> string VAs and the functions referencing them
  xref.py dis <dll> <va> [n]         -> disassemble the function containing va (or n bytes)
  xref.py refs <dll> <va>            -> functions with rip-relative refs or calls to va
"""
import sys, struct, bisect, re, os, pickle
import pefile, capstone, numpy as np
B = r"D:\SteamLibrary\steamapps\common\Total War WARHAMMER III\assembly_kit\binaries"
_cache = {}
class Img:
    def __init__(s, dll):
        path = dll if os.path.isabs(dll) else os.path.join(B, dll)
        s.pe = pe = pefile.PE(path)
        s.base = pe.OPTIONAL_HEADER.ImageBase
        s.mem = pe.get_memory_mapped_image()
        s.names = {}
        if hasattr(pe, 'DIRECTORY_ENTRY_IMPORT'):
            for d in pe.DIRECTORY_ENTRY_IMPORT:
                for i in d.imports:
                    s.names[i.address] = 'imp:' + d.dll.decode() + '!' + (i.name.decode() if i.name else str(i.ordinal))
        if hasattr(pe, 'DIRECTORY_ENTRY_EXPORT'):
            for e in pe.DIRECTORY_ENTRY_EXPORT.symbols:
                if e.name: s.names[s.base + e.address] = e.name.decode()
        s.funcs = []
        for f in getattr(pe, 'DIRECTORY_ENTRY_EXCEPTION', []):
            s.funcs.append((s.base + f.struct.BeginAddress, s.base + f.struct.EndAddress))
        s.funcs.sort(); s.starts = [f[0] for f in s.funcs]
        s.text = [sec for sec in pe.sections if sec.Name.startswith(b'.text')][0]
    def func_of(s, va):
        i = bisect.bisect_right(s.starts, va) - 1
        if i >= 0 and s.funcs[i][0] <= va < s.funcs[i][1]: return s.funcs[i]
        return None
    def rip_refs(s, target):
        t0 = s.text.VirtualAddress; data = np.frombuffer(s.mem[t0:t0 + s.text.Misc_VirtualSize], dtype=np.uint8)
        n = len(data) - 4
        d = data[:n].astype(np.int64) | (data[1:n+1].astype(np.int64) << 8) | (data[2:n+2].astype(np.int64) << 16) | (data[3:n+3].astype(np.int64) << 24)
        d = np.where(d >= 2**31, d - 2**32, d)
        pos = np.arange(n, dtype=np.int64) + t0 + s.base
        out = []
        for extra in (4, 5, 8):   # disp32 followed by 0, imm8 or imm32
            hit = np.nonzero(pos + extra + d == target)[0]
            out += [int(pos[h]) for h in hit]
        return sorted(set(out))
    def strings(s, text):
        out = []
        for enc in (text.encode(), text.encode('utf-16-le')):
            for m in re.finditer(re.escape(enc), s.mem):
                out.append(s.base + m.start())
        return out
    def dis(s, start, end):
        md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        code = s.mem[start - s.base:end - s.base]
        lines = []
        for ins in md.disasm(code, start):
            t = f'{ins.mnemonic} {ins.op_str}'
            m = re.search(r'\[rip ([+-]) (0x[0-9a-f]+)\]', ins.op_str)
            note = ''
            if m:
                tgt = ins.address + ins.size + (int(m.group(2), 16) * (1 if m.group(1) == '+' else -1))
                note = s.describe(tgt)
            elif ins.mnemonic in ('call', 'jmp') and ins.op_str.startswith('0x'):
                tgt = int(ins.op_str, 16); note = s.describe(tgt)
            lines.append(f'{ins.address:#x}  {t}' + (f'   ; {note}' if note else ''))
        return lines
    def describe(s, va):
        if va in s.names: return s.names[va]
        off = va - s.base
        if 0 <= off < len(s.mem):
            raw = s.mem[off:off + 64]
            z = raw.find(b'\0')
            if z > 3 and all(32 <= c < 127 for c in raw[:z]): return repr(raw[:z].decode())
            if len(raw) > 8 and raw[1] == 0 and raw[3] == 0 and 32 <= raw[0] < 127:
                try:
                    w = raw.decode('utf-16-le', 'ignore').split('\0')[0]
                    if len(w) > 3: return 'L' + repr(w)
                except Exception: pass
            f = s.func_of(va)
            if f and f[0] == va: return f'sub_{va:x}'
            if len(raw) >= 4: return f'[{va:#x}] = {struct.unpack("<f", raw[:4])[0]!r} / {struct.unpack("<I", raw[:4])[0]:#x}'
        return ''
def img(dll):
    if dll not in _cache: _cache[dll] = Img(dll)
    return _cache[dll]
if __name__ == '__main__':
    cmd, dll = sys.argv[1], sys.argv[2]
    I = img(dll)
    if cmd == 'str':
        for sva in I.strings(sys.argv[3]):
            print(f'string at {sva:#x}')
            for r in I.rip_refs(sva):
                f = I.func_of(r); print(f'   ref {r:#x} in {f[0]:#x}..{f[1]:#x}' if f else f'   ref {r:#x}')
    elif cmd == 'dis':
        va = int(sys.argv[3], 16)
        if len(sys.argv) > 4: a, b = va, va + int(sys.argv[4], 16)
        else: a, b = I.func_of(va)
        print('\n'.join(I.dis(a, b)))
    elif cmd == 'refs':
        va = int(sys.argv[3], 16)
        for r in I.rip_refs(va):
            f = I.func_of(r); print(f'ref {r:#x} in {f[0]:#x}' if f else f'ref {r:#x}')
        # rel32 calls/jmps
        t0 = I.text.VirtualAddress; mem = I.mem
        for m in re.finditer(b'[\xe8\xe9]', mem[t0:t0 + I.text.Misc_VirtualSize]):
            p = t0 + m.start(); d = struct.unpack_from('<i', mem, p + 1)[0]
            if I.base + p + 5 + d == va:
                f = I.func_of(I.base + p); print(f'call {I.base+p:#x} in {f[0]:#x}' if f else f'call {I.base+p:#x}')
