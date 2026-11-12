"""The data the ROM-free Worlds Collide generator ships instead of the SNES ROM.

WC makes its decisions from the ROM's data tables only (PORTING.md "WC without an SNES ROM";
tools/wc_trace.py measures it), so the generator carries just those, at their ROM offsets:
characters, items, spells and skills, espers, enemies and their items and AI scripts,
formations, packs, zones, chests, shops, the Coliseum, natural magic, metamorph sets, map
properties, AP per formation, HP/MP growth, and the names WC and our porter use (94,656 bytes,
44 KB compressed). No graphics, dialogue, descriptions, maps, music or game code: the rest of
WC's input is tools/wc_romfree.py's stand-ins (tools/wc_parity.py: the same seed folders).

  python tools/wc_basedata.py build [OUT]        (dev, with the ROM) data/wc_base.bin
  python tools/wc_basedata.py regions WORK...    (dev) the regions WC (stand-ins on) and the
                                                 porter read in wc_trace/port_trace runs
  python tools/wc_basedata.py check              (dev) the file holds the ROM's bytes there

At seed time load() gives the input as the ROM's 3 MB with the regions filled, zeros elsewhere.
"""
import json
import os
import struct
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
PATH = os.path.join(ROOT, "data", "wc_base.bin")
ROM_SIZE = 3 * 2 ** 20
MAGIC = b"FF6WCB1\0"

# (start, end) offsets in the unheadered US v1.0 ROM: what WC (with the stand-ins) and our
# porter read across both presets and an option-heavy flag set (wc_basedata.py regions), less
# graphics and game code (whose porter reads fall back to vanilla when WC doesn't write them)
REGIONS = []   # filled from REGIONS_JSON below


def _regions_file():
    return os.path.join(HERE, "wc_basedata_regions.json")


def load_regions():
    with open(_regions_file(), encoding="utf-8") as f:
        return [(int(a, 16), int(b, 16)) for a, b in json.load(f)["regions"]]


def build(out=PATH):
    sys.path.insert(0, HERE)
    import prdata as P
    rom = open(P.VANILLA_ROM, "rb").read()
    if len(rom) % 0x8000 == 0x200:
        rom = rom[0x200:]
    regions = load_regions()
    body = bytearray()
    body += struct.pack("<I", len(regions))
    for s, e in regions:
        body += struct.pack("<II", s, e - s) + rom[s:e]
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "wb") as f:
        f.write(MAGIC + zlib.compress(bytes(body), 9))
    total = sum(e - s for s, e in regions)
    print(f"wc_base: {len(regions)} regions, {total} bytes ({os.path.getsize(out)} compressed) -> {out}")


def regions_of(path=PATH):
    with open(path, "rb") as f:
        raw = f.read()
    if not raw.startswith(MAGIC):
        raise ValueError(f"not a WC base data file: {path}")
    body = zlib.decompress(raw[len(MAGIC):])
    n = struct.unpack_from("<I", body, 0)[0]
    at, out = 4, []
    for _ in range(n):
        s, size = struct.unpack_from("<II", body, at)
        at += 8
        out.append((s, body[at:at + size]))
        at += size
    return out


def load(path=PATH):
    """The generator's input: the ROM's size, the shipped regions in place, zeros elsewhere."""
    buf = bytearray(ROM_SIZE)
    for s, data in regions_of(path):
        buf[s:s + len(data)] = data
    return bytes(buf)


def collect(works):
    """The regions to ship, from wc_trace.py (WC_ROMFREE=1) and port_trace.py runs."""
    GRAPHICS = ("data/character_sprites.py", "data/character_palettes.py", "data/menu_character_sprites.py")
    # porter reads of graphics (monster shapes) and of code WC writes when it changes it
    # (natural magic learners, the start level, BNW's name check): vanilla when absent
    SKIP_PORT = ("port_sprites.py:shape", "port_spells.py:natural_learners")
    # game code (banks C0-C3: WC's data modules glance at a few bytes of it, only to move or
    # patch it) and item/lore descriptions: the seed folders are the same without them
    CODE = [(0, 0x40000), (0x2D6400, 0x2D7CA0), (0x2FFC00, 0x2FFC08)]
    marks = bytearray(ROM_SIZE)

    def add(regions):
        for a, b in regions:
            s, e = int(a, 16), min(int(b, 16), ROM_SIZE)
            for cs, ce in CODE:                     # cut out wherever they overlap
                if s < ce and e > cs:
                    if s >= cs:
                        s = min(e, ce)
                    else:
                        if e > ce:
                            add([[hex(ce), hex(e)]])
                        e = cs
            if e > s:
                marks[s:e] = b"\1" * (e - s)
    for w in works:
        for name in sorted(os.listdir(w)):
            d = os.path.join(w, name)
            reads = os.path.join(d, "reads.json")
            if os.path.exists(reads):
                for mod, v in json.load(open(reads, encoding="utf-8")).items():
                    if mod.startswith("data/") and not mod.startswith(GRAPHICS):
                        add(v["regions"])
            port = os.path.join(d, "port_reads.json")
            if os.path.exists(port):
                for side in json.load(open(port, encoding="utf-8")).values():
                    for who, v in side.items():
                        if who not in SKIP_PORT:
                            add(v["regions"])
    out, s = [], None
    for i in range(ROM_SIZE + 1):
        on = i < ROM_SIZE and marks[i]
        if on and s is None:
            s = i
        elif not on and s is not None:
            if out and s - out[-1][1] <= 16:
                out[-1][1] = i                      # small gaps (table padding) join
            else:
                out.append([s, i])
            s = None
    return out


def check():
    sys.path.insert(0, HERE)
    import prdata as P
    rom = open(P.VANILLA_ROM, "rb").read()
    bad = [hex(s) for s, data in regions_of() if rom[s:s + len(data)] != data]
    print("wc_base matches the ROM" if not bad else f"wc_base differs from the ROM at {bad}")
    return 1 if bad else 0


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else ""
    if cmd == "build":
        build(sys.argv[2] if len(sys.argv) > 2 else PATH)
    elif cmd == "regions":
        regs = collect(sys.argv[2:])
        with open(_regions_file(), "w", encoding="utf-8") as f:
            json.dump({"note": "tools/wc_basedata.py regions: what WC (stand-ins on) and the porter read",
                       "regions": [[hex(a), hex(b)] for a, b in regs]}, f, indent=0)
        print(f"{len(regs)} regions, {sum(b - a for a, b in regs)} bytes -> {_regions_file()}")
    elif cmd == "check":
        raise SystemExit(check())
    else:
        print(__doc__)
