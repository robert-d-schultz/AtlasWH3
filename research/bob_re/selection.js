// How WH3's BOB selects actions: logs ACTION_INTERFACE::select / deselect / is_selected calls (bob_shared exports)
// with each action's class name from MSVC RTTI. With FORCE set (a list of class names, posted as {type:"force",
// classes:[...]}), is_selected returns true for those classes and false for every other action.
const shared = Process.getModuleByName("bob_shared.modder.x64.dll");
const exp = n => shared.getExportByName(n);
const names = new Map();
let force = null;
recv("force", function onForce(m) { force = new Set(m.classes); send({ kind: "forcing", classes: m.classes }); recv("force", onForce); });

function className(obj) {
    try {
        const vt = obj.readPointer();
        if (names.has(vt.toString())) return names.get(vt.toString());
        const col = vt.sub(8).readPointer();
        const tdRva = col.add(12).readU32();
        const selfRva = col.add(20).readU32();
        const base = col.sub(selfRva);
        const name = base.add(tdRva).add(16).readCString();
        names.set(vt.toString(), name);
        return name;
    } catch (e) { return "?"; }
}

const seen = new Map();
function note(fn, self, extra) {
    const c = className(self);
    const key = fn + " " + c + " " + (extra ?? "");
    if (!seen.has(key)) { seen.set(key, 1); send({ kind: "call", fn, cls: c, extra, obj: self.toString() }); }
}

Interceptor.attach(exp("?select@ACTION_INTERFACE@@QEAAXAEAV?$QSet@PEAVACTION_INTERFACE@@@@@Z"), {
    onEnter(args) { note("select", args[0]); }
});
Interceptor.attach(exp("?deselect@ACTION_INTERFACE@@QEAAXAEAV?$QSet@PEAVACTION_INTERFACE@@@@@Z"), {
    onEnter(args) { note("deselect", args[0]); }
});
Interceptor.attach(exp("?is_selected@ACTION_INTERFACE@@QEBA_NXZ"), {
    onEnter(args) { this.self = args[0]; },
    onLeave(ret) {
        const c = className(this.self);
        if (force !== null) ret.replace(force.has(c) ? 1 : 0);
        note("is_selected", this.self, ret.toInt32());
    }
});

// every action object constructed (ACTION_INTERFACE's constructor), reported with its final class at the first select
const created = [];
Interceptor.attach(exp("??0ACTION_INTERFACE@@QEAA@AEAVPROCESSOR_INTERFACE@@@Z"), {
    onEnter(args) { created.push(args[0]); }
});
let listed = false;
Interceptor.attach(exp("?select@ACTION_INTERFACE@@QEAAXAEAV?$QSet@PEAVACTION_INTERFACE@@@@@Z"), {
    onEnter(args) {
        if (listed) return;
        listed = true;
        const counts = {};
        for (const o of created) { const c = className(o); counts[c] = (counts[c] ?? 0) + 1; }
        send({ kind: "created", count: created.length, classes: counts });
    }
});
