"""Which ROM bytes does our porter read for a WC seed? (dev tool for the ROM-free generator)

  python tools/port_trace.py WC_ROM OUT.json      (WC_ROM: a WC output ROM; the vanilla ROM is P's)

Runs the worlds-collide port in this process with every ff6snes.Rom's bytes traced, and writes
the regions each porter module read, for the vanilla ROM and for WC's.
"""
import collections
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import ff6snes as S  # noqa: E402
import prdata as P  # noqa: E402


class TracedBytes(bytes):
    """bytes that note which offsets are read (indexing and slicing), and by which tools module."""
    log = None
    label = ""

    def _who(self):
        f = sys._getframe(2)
        while f is not None:
            fn = f.f_code.co_filename
            if fn.startswith(HERE) and not fn.endswith(("ff6snes.py", "port_trace.py")):
                return os.path.basename(fn) + ":" + f.f_code.co_name
            f = f.f_back
        return "?"

    def __getitem__(self, i):
        who = self._who()
        if isinstance(i, slice):
            start, stop, step = i.indices(len(self))
            TracedBytes.log[(self.label, who)].update(range(start, stop, step))
        else:
            TracedBytes.log[(self.label, who)].add(i if i >= 0 else len(self) + i)
        return bytes.__getitem__(self, i)


def main(wc_rom, out):
    TracedBytes.log = collections.defaultdict(set)
    original = S.Rom.__init__

    def init(self, path):
        original(self, path)
        label = "vanilla" if os.path.abspath(path) == os.path.abspath(P.VANILLA_ROM) else "wc"
        tb = TracedBytes(self.data)
        tb.label = label
        self.data = tb

    S.Rom.__init__ = init
    os.environ["WC_FLAGS"] = os.environ.get("WC_FLAGS", "")
    import wc_seed
    import io
    wc_seed.port_in_process(wc_rom, "WCTrace", io.StringIO())
    res = {}
    for (label, who), addrs in sorted(TracedBytes.log.items()):
        a = sorted(addrs)
        regs, s = [], None
        for x in a:
            if s is None:
                s = e = x
            elif x <= e + 16:
                e = x
            else:
                regs.append([hex(s), hex(e + 1)])
                s = e = x
        if s is not None:
            regs.append([hex(s), hex(e + 1)])
        res.setdefault(label, {})[who] = {"bytes": len(a), "regions": regs}
    json.dump(res, open(out, "w"), indent=1)
    for label, mods in res.items():
        print(label, sum(v["bytes"] for v in mods.values()), "bytes by", len(mods), "functions")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
