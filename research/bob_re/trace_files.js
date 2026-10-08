// File I/O of a BOB run: every CreateFileW (path, access, disposition, handle), and the offsets read from .pack files
// (ReadFile with the handle's position, or an OVERLAPPED offset; mapped views of packs are reported too).
// Messages: {kind: "open"|"read"|"map"|"close"|"move"}; batched to keep the run fast.
const k32 = Process.getModuleByName("kernelbase.dll");
const handles = new Map();      // handle (string) -> {path, pack}
const pos = new Map();          // pack handle -> current position (BigInt-free Number)
let batch = [];
function emit(m) { batch.push(m); if (batch.length >= 200) flush(); }
function flush() { if (batch.length) { send({ kind: "batch", items: batch }); batch = []; } }
setInterval(flush, 500);
recv("flush", function () { flush(); });

Interceptor.attach(k32.getExportByName("CreateFileW"), {
    onEnter(args) {
        this.path = args[0].isNull() ? "" : args[0].readUtf16String();
        this.access = args[1].toUInt32();
        this.disp = args[4].toUInt32();
    },
    onLeave(ret) {
        const h = ret.toString();
        const ok = !ret.equals(ptr("0xffffffffffffffff"));
        const pack = /\.pack$/i.test(this.path);
        if (ok) { handles.set(h, { path: this.path, pack }); if (pack) pos.set(h, 0); }
        emit({ kind: "open", path: this.path, access: this.access, disp: this.disp, ok, h, t: Date.now() });
    }
});

Interceptor.attach(k32.getExportByName("SetFilePointerEx"), {
    onEnter(args) {
        const h = args[0].toString();
        if (!pos.has(h)) return;
        const dist = args[1].toInt32() + 0x100000000 * args[1].shr(32).toInt32();   // LARGE_INTEGER by value
        const method = args[3].toUInt32();
        let p = method === 0 ? dist : method === 1 ? pos.get(h) + dist : -1;
        pos.set(h, p);
    }
});

Interceptor.attach(k32.getExportByName("ReadFile"), {
    onEnter(args) {
        const h = args[0].toString();
        if (!pos.has(h)) return;
        const n = args[2].toUInt32();
        const ov = args[4];
        let off = pos.get(h);
        if (!ov.isNull()) off = ov.add(8).readU32() + 0x100000000 * ov.add(12).readU32();
        else pos.set(h, off + n);
        emit({ kind: "read", h, path: handles.get(h).path, off, n });
    }
});

Interceptor.attach(k32.getExportByName("CloseHandle"), {
    onEnter(args) {
        const h = args[0].toString();
        if (handles.has(h)) { emit({ kind: "close", h }); handles.delete(h); pos.delete(h); }
    }
});

const mapping = new Map();   // mapping handle -> file path
Interceptor.attach(k32.getExportByName("CreateFileMappingW"), {
    onEnter(args) { this.file = handles.get(args[0].toString()); },
    onLeave(ret) { if (this.file && this.file.pack) mapping.set(ret.toString(), this.file.path); }
});
Interceptor.attach(k32.getExportByName("MapViewOfFile"), {
    onEnter(args) {
        const p = mapping.get(args[0].toString());
        if (p) emit({ kind: "map", path: p, off: args[3].toUInt32() + 0x100000000 * args[2].toUInt32(), n: args[4].toUInt32() });
    }
});

// MoveFileExW / ReplaceFileW: how BOB saves (write a temp file, then rename?)
for (const name of ["MoveFileExW", "MoveFileWithProgressW", "ReplaceFileW", "DeleteFileW"]) {
    let f = null;
    try { f = k32.getExportByName(name); } catch (e) { }
    if (f) Interceptor.attach(f, {
        onEnter(args) {
            emit({ kind: "move", fn: name, a: args[0].isNull() ? "" : args[0].readUtf16String(),
                   b: name === "DeleteFileW" || args[1].isNull() ? "" : args[1].readUtf16String() });
        }
    });
}
