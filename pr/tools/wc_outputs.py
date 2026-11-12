"""Compare what two Worlds Collide runs leave for our port: the data tables of their output ROMs
and wc_run.py's dumps (dev tool for the ROM-free generator, with tools/wc_trace.py).

  python tools/wc_outputs.py RUN_A RUN_B      (each a folder with wc.sfc and wc_events*.json)

The tables are the ones the porter reads (ff6snes.py / the port_* modules); SNES content WC only
rewrites (dialogue, sprites, maps, AI, game code) isn't compared: the port doesn't read it.
"""
import glob
import json
import os
import sys

# (start, end) of each table in the output ROM (unheadered offsets, WC's data modules)
TABLES = {
    "characters (init)": (0x2D7CA0, 0x2D8220), "character names": (0x0478C0, 0x047A40),
    "items": (0x185000, 0x186E00), "item names": (0x12B300, 0x12C000),
    "espers": (0x186E00, 0x186FFA), "spells": (0x046AC0, 0x0478C0),
    "shops": (0x047AC0, 0x047F40), "metamorph": (0x047F40, 0x047FA8), "initial rages": (0x047AA0, 0x047AC0),
    "chests": (0x2D82F4, 0x2D8E59),
    "enemies": (0x0F0000, 0x0F3000), "enemy items": (0x0F3000, 0x0F3600), "enemy names": (0x0FC050, 0x0FD0D0),
    "controls/sketches": (0x0F3D00, 0x0F4600), "rages": (0x0F4600, 0x0F4800),
    "packs": (0x0F4800, 0x0F5400), "zones": (0x0F5400, 0x0F5900), "formations": (0x0F5900, 0x0F83C0),
    "coliseum": (0x1FB600, 0x1FBA00), "natural magic": (0x2CE3C0, 0x2CE400),
    "swdtech": (0x0F3C40, 0x0F3CA0), "growth (HP/MP)": (0x26F4A0, 0x26F564),
    "AP per formation": (0x1FB400, 0x1FB600), "dances": (0x0FFE80, 0x0FFEA0),
    "special attack names": (0x0FD0D0, 0x0FDFD0), "map properties": (0x2D8F00, 0x2DC4A0),
}


def output(d):
    """A run's output: wc.sfc, or a ROM-free run's wc_out.bin (ff6snes.rom_bytes reads both)."""
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import ff6snes
    for name in ("wc.sfc", "wc_out.bin"):
        if os.path.exists(os.path.join(d, name)):
            return ff6snes.rom_bytes(os.path.join(d, name))
    raise FileNotFoundError(f"no WC output in {d}")


def compare(a, b):
    ra, rb = output(a), output(b)
    same, differ = [], []
    for name, (s, e) in TABLES.items():
        n = sum(1 for x, y in zip(ra[s:e], rb[s:e]) if x != y)
        (differ if n else same).append(f"{name} ({n} bytes)" if n else name)
    dumps = []
    for p in sorted(glob.glob(os.path.join(a, "wc_events*.json"))):
        q = os.path.join(b, os.path.basename(p))
        if not os.path.exists(q):
            dumps.append(f"{os.path.basename(p)}: missing in B")
            continue
        ja, jb = (json.load(open(x, encoding="utf-8")) for x in (p, q))
        if ja != jb:
            dumps.append(f"{os.path.basename(p)}: differs")
    print(f"tables the same: {len(same)}; different: {', '.join(differ) or 'none'}")
    print(f"dumps: {', '.join(dumps) or 'all the same'}")
    return 1 if differ or dumps else 0


if __name__ == "__main__":
    raise SystemExit(compare(sys.argv[1], sys.argv[2]))
