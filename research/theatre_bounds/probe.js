// Frida agent for probe.py: who reads map_data.esf's theatre bounds in Warhammer3.exe. No code is patched.
//  - execute breakpoint on the CAMPAIGN_THEATRE reader 0x141479eb0 (rcx = the map-data object: min x/z at +0x24,
//    max x/z at +0x2c, hex width/height at +0x34/+0x38, hex array at +0x40)
//  - execute breakpoint after the MASKED_REGIONS_DATA reader returns (0x141470898; elements of 0x50 bytes at
//    [obj+0x58], count [obj+0x54], min x/z at +0x20, max at +0x28)
// Each arms a read/write watchpoint on that copy's min x (4 bytes). Every access is counted per pc, with the first
// backtraces of each pc.
'use strict';
const STATIC_BASE = ptr('0x140000000');
const exe = Process.getModuleByName('Warhammer3.exe');
const at = va => exe.base.add(ptr(va).sub(STATIC_BASE));
function hex(p, n) { return Array.from(new Uint8Array(p.readByteArray(n)), b => b.toString(16).padStart(2, '0')).join(''); }
function toStatic(a) {
    const o = a.sub(exe.base);
    return o.compare(ptr(exe.size)) < 0 ? STATIC_BASE.add(o).toString() : a.toString();
}

const BPS = [
    { name: 'theatre', at: at('0x141479eb0'), bytes: '48895c2408', run: c => c.rsp.add(8).writePointer(c.rbx) },
    { name: 'masked_done', at: at('0x141470898'), bytes: '488d55c0', run: c => { c.rdx = c.rbp.sub(0x40); } },
];
for (const b of BPS) {
    const got = hex(b.at, b.bytes.length / 2);
    if (got !== b.bytes) throw new Error(`${b.name}: expected ${b.bytes}, found ${got} (game updated?)`);
    b.len = b.bytes.length / 2;
}

const watches = [null, null];   // slot 2: theatre min x, slot 3: masked regions min x
const stats = {};               // "watch|pc" -> { n, bts: [] }
const MAX_BT = 4;
const HOT = 20000;
let total = 0;

function armThread(t) {
    BPS.forEach((b, i) => { try { t.setHardwareBreakpoint(i, b.at); } catch (err) { send({ kind: 'warn', msg: `bp ${t.id}: ${err}` }); } });
    watches.forEach((w, i) => { if (w) try { t.setHardwareWatchpoint(2 + i, w.at, 4, 'rw'); } catch (err) { send({ kind: 'warn', msg: `watch ${t.id}: ${err}` }); } });
}
// Armed from a timer, not from inside the exception handler: setting every thread's debug registers while the
// trapping thread is held in the handler deadlocked the game's load.
function watch(i, name, addr) {
    setTimeout(() => {
        watches[i] = { name, at: addr };
        Process.enumerateThreads().forEach(t => { try { t.setHardwareWatchpoint(2 + i, addr, 4, 'rw'); } catch (err) { send({ kind: 'warn', msg: `watch ${t.id}: ${err}` }); } });
        send({ kind: 'armed', name, at: addr.toString() });
    }, 0);
}

Process.setExceptionHandler(d => {
    const b = BPS.find(x => d.address.equals(x.at));
    const c = d.context;
    if (b) {
        if (b.name === 'theatre') {
            const obj = c.rcx;
            watch(0, 'theatre_min_x', obj.add(0x24));
            send({ kind: 'hit', name: b.name, obj: obj.toString(), tid: Process.getCurrentThreadId(),
                   bt: Thread.backtrace(c, Backtracer.ACCURATE).slice(0, 8).map(toStatic) });
        } else {
            const obj = c.rsi, n = obj.add(0x54).readU32(), arr = obj.add(0x58).readPointer();
            const first = n ? hex(arr.add(0x20), 16) : null;   // read before the watch is armed
            if (n > 0) watch(1, 'masked_min_x', arr.add(0x20));
            send({ kind: 'hit', name: b.name, obj: obj.toString(), elements: n, first });
        }
        b.run(c);
        c.pc = b.at.add(b.len);
        return true;
    }
    if (d.type !== 'single-step') return false;
    // Never read a watched address here: the agent's own read would trap again. Which watch fired is not exposed
    // (no DR6), so a hit is keyed by the watches armed at the time.
    if (!watches.some(w => w)) return false;
    const key = `${watches.filter(w => w).map(w => w.name).join('+')}|${toStatic(d.address)}`;
    const s = stats[key] || (stats[key] = { n: 0, bts: [] });
    s.n++;
    if (s.bts.length < MAX_BT) s.bts.push(Thread.backtrace(c, Backtracer.FUZZY).slice(0, 12).map(toStatic));
    // A freed object's memory reused by a hot loop would trap without end: drop the watches.
    if (s.n === HOT || ++total % (5 * HOT) === 0) {
        watches.forEach((w, i) => { if (w) Process.enumerateThreads().forEach(t => { try { t.unsetHardwareWatchpoint(2 + i); } catch (err) {} }); });
        send({ kind: 'hot', key, watches: watches.filter(w => w).map(w => `${w.name}@${w.at}`) });
        watches[0] = watches[1] = null;
    }
    return true;   // a data breakpoint traps after the access: nothing to step over
});

setInterval(() => send({ kind: 'stats', stats }), 3000);
Process.enumerateThreads().forEach(armThread);
Process.attachThreadObserver({ onAdded: armThread });
send({ kind: 'ready', base: exe.base.toString(), threads: Process.enumerateThreads().length });
