"""Read-only PFH4/PFH5 pack index reader (same layout as src/AtlasWH3.Formats/Packs/PackFile.cs)."""
import os, struct, sys


def index(path):
    out = []
    with open(path, 'rb') as f:
        magic = f.read(4)
        mask, dep_n, dep_sz, n, idx_sz = struct.unpack('<5I', f.read(20))
        buf = 24 if mask & 0x100 else 4
        f.read(buf)
        f.read(dep_sz)
        ts = bool(mask & 0x40)
        pf5 = magic == b'PFH5'
        off = 24 + buf + dep_sz + idx_sz
        blob = f.read(idx_sz)
    p = 0
    for _ in range(n):
        size = struct.unpack_from('<I', blob, p)[0]; p += 4
        if ts: p += 4
        comp = False
        if pf5: comp = blob[p] != 0; p += 1
        e = blob.index(b'\0', p); name = blob[p:e].decode('utf-8', 'replace'); p = e + 1
        out.append((name, off, size, comp)); off += size
    return out


def read(path, entry):
    name, off, size, comp = entry
    if comp:
        raise ValueError('compressed entry')
    with open(path, 'rb') as f:
        f.seek(off); return f.read(size)


if __name__ == '__main__':
    for pk in sys.argv[2:]:
        try:
            for e in index(pk):
                if sys.argv[1].lower() in e[0].lower():
                    print(os.path.basename(pk), e)
        except Exception as ex:
            print(pk, 'ERR', ex)
