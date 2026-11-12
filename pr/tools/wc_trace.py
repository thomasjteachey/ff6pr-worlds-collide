"""Record a Worlds Collide run: every random draw and every input-ROM byte it reads (dev tool
for the ROM-free generator, PORTING.md "WC without an SNES ROM").

  (cwd = the WC folder) python tools/wc_trace.py OUT_DIR DUMP.json <wc.py arguments...>
  python tools/wc_trace.py --diff OUT_A OUT_B

A run is wc_run.py's (WC's own main, unchanged, with our event recording) plus:
  OUT_DIR/rng.txt        one line per draw from Python's random (all of WC's randomness):
                         "n kind arg result site" (site = WC's file:function:line)
  OUT_DIR/rng_sites.json draws per WC file, in the order the files first draw
  OUT_DIR/reads.json     the input bytes WC read before writing them, by the WC module that
                         read them (past the generic readers: memory/, data/structures.py),
                         as merged regions
--diff compares two runs' draws: the first draw that differs and where each run was.

Two runs with the same draws, start to end, made the same decisions; with the same outputs the
porter reads, the same seed (the ROM-free generator is held to this against the real ROM).
"""
import collections
import json
import os
import random
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SIZE = 4 * 2 ** 20
GENERIC = ("memory" + os.sep, os.path.join("data", "structures.py"), "random.py", "wc_trace.py", "wc_run.py")


def site(depth=2):
    f = sys._getframe(depth)
    while f is not None:
        fn = f.f_code.co_filename
        if not fn.endswith(("random.py", "wc_trace.py")):
            rel = os.path.relpath(fn, os.getcwd()) if fn.startswith(os.getcwd()) else os.path.basename(fn)
            return f"{rel.replace(os.sep, '/')}:{f.f_code.co_name}:{f.f_lineno}"
        f = f.f_back
    return "?"


def reader(depth=3):
    """The WC module that asked for a ROM byte (past the ROM and generic table classes)."""
    f = sys._getframe(depth)
    while f is not None:
        fn = f.f_code.co_filename
        rel = os.path.relpath(fn, os.getcwd()) if fn.startswith(os.getcwd()) else fn
        if not any(g in rel for g in GENERIC):
            return rel.replace(os.sep, "/") + ":" + f.f_code.co_name
        f = f.f_back
    return "?"


def record(out_dir, wc_run_argv):
    os.makedirs(out_dir, exist_ok=True)
    draws = []
    inst = random._inst
    c_random, c_bits = inst.random, inst.getrandbits

    def traced_random():
        r = c_random()
        draws.append(("r", 0, repr(r), site()))
        return r

    def traced_bits(k):
        r = c_bits(k)
        draws.append(("b", k, r, site()))
        return r

    inst.random, inst.getrandbits = traced_random, traced_bits
    random.random, random.getrandbits = traced_random, traced_bits

    read_in, written = bytearray(SIZE), bytearray(SIZE)
    by_mod = collections.defaultdict(list)

    class TracedList(list):
        def _note(self, a):
            if a < SIZE and not written[a] and not read_in[a]:
                read_in[a] = 1
                by_mod[reader()].append(a)

        def __getitem__(self, i):
            if isinstance(i, slice):
                for a in range(*i.indices(len(self))):
                    self._note(a)
            elif i >= 0:
                self._note(i)
            return list.__getitem__(self, i)

        def __setitem__(self, i, v):
            if isinstance(i, slice):
                start, stop, step = i.indices(len(self))
                n = len(v) if hasattr(v, "__len__") else 0
                for a in range(start, min(SIZE, start + n) if step == 1 else stop, 1):
                    written[a] = 1
            elif 0 <= i < SIZE:
                written[i] = 1
            return list.__setitem__(self, i, v)

    sys.path.insert(0, os.getcwd())
    sys.path.insert(0, HERE)
    import wc_run

    def patch_memory(module):
        # once WC's Memory holds its ROM (a file's, or the ROM-free input: wc_romfree.py)
        init = module.Memory.__init__

        def traced_init(self):
            init(self)
            self.rom.data = TracedList(self.rom.data)

        module.Memory.__init__ = traced_init

    wc_run.EXTRA_PATCHES["memory.memory"] = patch_memory
    sys.argv = ["wc_run.py"] + wc_run_argv
    try:
        wc_run.main()
    finally:
        inst.random, inst.getrandbits = c_random, c_bits
        random.random, random.getrandbits = c_random, c_bits
        with open(os.path.join(out_dir, "rng.txt"), "w", encoding="utf-8") as f:
            for n, (kind, arg, res, where) in enumerate(draws):
                f.write(f"{n} {kind} {arg} {res} {where}\n")
        per_file = collections.OrderedDict()
        for kind, arg, res, where in draws:
            per_file[where.split(":")[0]] = per_file.get(where.split(":")[0], 0) + 1
        with open(os.path.join(out_dir, "rng_sites.json"), "w", encoding="utf-8") as f:
            json.dump({"draws": len(draws), "by_file": per_file}, f, indent=1)
        regions = {}
        for mod, addrs in by_mod.items():
            addrs.sort()
            out, s = [], None
            for a in addrs:
                if s is None:
                    s = e = a
                elif a <= e + 16:
                    e = a
                else:
                    out.append([hex(s), hex(e + 1)])
                    s = e = a
            if s is not None:
                out.append([hex(s), hex(e + 1)])
            regions[mod] = {"bytes": len(addrs), "regions": out}
        with open(os.path.join(out_dir, "reads.json"), "w", encoding="utf-8") as f:
            json.dump(dict(sorted(regions.items(), key=lambda kv: -kv[1]["bytes"])), f, indent=1)
        print(f"[trace] {len(draws)} draws, {sum(read_in)} input bytes read by {len(by_mod)} WC functions -> {out_dir}")


def diff(a, b):
    def lines(d):
        with open(os.path.join(d, "rng.txt"), encoding="utf-8") as f:
            return [line.rstrip("\n").split(" ", 4) for line in f]
    la, lb = lines(a), lines(b)
    moved = []        # same value, another line (a stand-in drawing for WC's code: wc_romfree.py)
    for i, (x, y) in enumerate(zip(la, lb)):
        if x[1:4] != y[1:4]:
            print(f"draw {i} differs:\n  A: {' '.join(x[1:])}\n  B: {' '.join(y[1:])}")
            print("  before it:", " | ".join(" ".join(z[1:]) for z in la[max(0, i - 3):i]))
            return 1
        if x[4] != y[4]:
            moved.append(f"{x[4]} -> {y[4]}")
    note = f" ({len(moved)} drawn from another line: {', '.join(sorted(set(moved)))})" if moved else ""
    if len(la) != len(lb):
        print(f"same first {min(len(la), len(lb))} draws{note}; A made {len(la)}, B {len(lb)}")
        return 1
    print(f"the same {len(la)} draws{note}")
    return 0


if __name__ == "__main__":
    if sys.argv[1] == "--diff":
        raise SystemExit(diff(sys.argv[2], sys.argv[3]))
    record(sys.argv[1], sys.argv[2:])
