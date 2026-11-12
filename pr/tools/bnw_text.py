"""Parse Brave New World's description source (bnw 3.0/asm/descriptions.asm and
asm/banks/c4.asm) into plain English, in game order.

  sections()  -> {"spells": [...54], "items": [...], "blitz": [...], ...}
  esper_descriptions() -> [...27] (summon effect text, SNES esper order)
"""
import os
import re

import prdata as P

SOURCE = os.path.normpath(os.path.join(P.ROOT, "..", "mods", "brave-new-world", "source"))
DESC_ASM = os.path.join(SOURCE, "bnw 3.0", "asm", "descriptions.asm")
C4_ASM = os.path.join(SOURCE, "asm", "banks", "c4.asm")

# Byte codes from the legend at the top of descriptions.asm (bigrams + icons).
CODES = {
    0x07: "at", 0x08: "g ", 0x0E: " - ", 0xE0: "s ", 0xE1: "le", 0xE2: "al", 0xE4: "in",
    0xE5: "et", 0xE7: "e ", 0xEB: " d", 0xEC: "il", 0xF0: "ta", 0xF1: "re", 0xF3: "\"S",
    0xF4: "d ", 0xF5: "if", 0xF7: " s",
    # element icons
    0xF6: "{Holy}", 0xF8: "{Bolt}", 0xF9: "{Wind}", 0xFA: "{Earth}", 0xFB: "{Ice}",
    0xFC: "{Fire}", 0xFD: "{Water}", 0xEF: "{Dark}",
    # skill-type icons
    0xE3: "{lore}", 0xE8: "{white}", 0xE9: "{black}", 0xEA: "{gray}",
    0xC0: "/", 0xD2: "=", 0x01: "\n",
    0x03: " ", 0x11: "1/2", 0x12: "3/4", 0xD8: "X", 0xD9: "Y", 0xE6: "", 0xEE: "+",
}
# the menu font's text table; the generator bundle (wc_bundle.py) carries a copy in data/
VWF_TBL = next((p for p in (os.path.join(SOURCE, "asm", "banks", "vwf.tbl"), os.path.join(P.ROOT, "data", "vwf.tbl"))
                if os.path.exists(p)), os.path.join(SOURCE, "asm", "banks", "vwf.tbl"))


def _font():
    table = {}
    with open(VWF_TBL, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = re.match(r"\s*'(.+)'\s*=\s*\$([0-9A-Fa-f]{2})", line)
            if m:
                table[int(m.group(2), 16)] = m.group(1)
    return table


FONT = _font()
SECTIONS = {  # name: (text org, pointer org)
    "items": ("ED6400", "ED7AA0"),
    "spells": ("D8C9A0", "D8CF80"),
    "blitz": ("CFFC00", "CFFF9E"),
    "bushido": ("CFFD00", "CFFFAE"),
    "lores": ("ED77A0", "ED7A70"),
    "equip_bonus": ("CF3940", "CFFE40"),
    "esper_bonus": ("EDFE00", "EDFFD0"),
}

# entries per table (from BNW's descriptions.asm), for when the asm isn't there (generator bundle)
COUNTS = {"items": 255, "spells": 54, "blitz": 8, "bushido": 8, "lores": 12, "equip_bonus": 27, "esper_bonus": 16}

_TOKEN = re.compile(r'"((?:[^"\\]|\\.)*)"|\$([0-9A-Fa-f]{2})')


def decode_db(operands):
    out = []
    for m in _TOKEN.finditer(operands):
        if m.group(1) is not None:
            s = m.group(1).replace("|", "\n")
            s = s.replace("[", "\"").replace("]", "\"").replace("`", "\"").replace("^", "\"")
            out.append(s)
        else:
            b = int(m.group(2), 16)
            if b == 0x00:
                break
            out.append(CODES.get(b, FONT.get(b, "")))
    return "".join(out)


def _blocks(path):
    """Yield (org, [lines]) for each org block in an asm file."""
    org, lines = None, []
    with open(path, encoding="utf-8", errors="replace") as f:
        for raw in f:
            line = raw.rstrip("\n")
            m = re.match(r"\s*org\s+\$([0-9A-Fa-f]+)", line)
            if m:
                if org is not None:
                    yield org, lines
                org, lines = m.group(1).upper(), []
            elif org is not None:
                lines.append(line)
    if org is not None:
        yield org, lines


def _labels_and_pointers(path):
    labels, pointers = {}, {}
    for org, lines in _blocks(path):
        ptrs = []
        pending = None  # label on its own line, db on the next
        for line in lines:
            code = line.split(";", 1)[0] if '"' not in line.split(";", 1)[0] or line.count('"') % 2 == 0 else line
            m = re.match(r"\s*([A-Za-z_][A-Za-z_0-9]*)\s*:\s*db\s+(.*)$", line)
            if m:
                labels[m.group(1)] = decode_db(m.group(2))
                pending = None
                continue
            m = re.match(r"\s*([A-Za-z_][A-Za-z_0-9]*)\s*:\s*(;.*)?$", line)
            if m:
                pending = m.group(1)
                continue
            m = re.match(r"\s*db\s+(.*)$", line)
            if m and pending:
                labels[pending] = decode_db(m.group(1))
                pending = None
                continue
            m = re.match(r"\s*dw\s+([A-Za-z_][A-Za-z_0-9]*)", code)
            if m:
                ptrs.append(m.group(1))
        if ptrs:
            pointers[org] = ptrs
    return labels, pointers


def tidy(text):
    """Turn icons into words: '{Fire}{Fire}' -> 'Fire damage'."""
    t = text.strip()
    only_icons = re.fullmatch(r"(\{[A-Za-z]+\})+", t)
    if only_icons:
        elems = re.findall(r"\{([A-Za-z]+)\}", t)
        return f"{elems[0]} damage"
    t = re.sub(r"\{(white|black|gray|lore)\}", "", t).strip()
    lead = re.match(r"((?:\{[A-Za-z]+\}/?)+)\s*,?\s*", t)
    if lead:
        elems = "/".join(dict.fromkeys(re.findall(r"\{([A-Za-z]+)\}", lead.group(1))))
        rest = t[lead.end():]
        t = f"{elems} damage" + (f". {rest[0].upper()}{rest[1:]}" if rest else "")
    t = re.sub(r"\{([A-Za-z]+)\}", r"\1", t)
    return re.sub(r"\s*\n\s*", "\n", t).strip()


BNW_ROM = os.path.normpath(os.path.join(P.ROOT, "..", "build", "brave-new-world", "Brave New World 3.0 FRC.sfc"))


def _decode_rom(data):
    import ff6snes
    out = []
    for x in data:
        if x == 0:
            break
        out.append(CODES.get(x, FONT.get(x, ff6snes._NAME.get(x, "*" if x == 0xCF else ""))))
    s = "".join(out)
    return s.replace("`", "\"").replace("^", "\"").replace("[", "\"").replace("]", "\"")


def sections(rom_path=BNW_ROM, only=None):
    """Descriptions read from the built BNW ROM via its pointer tables (ground truth);
    the asm only supplies how many entries each table has."""
    pointers = _labels_and_pointers(DESC_ASM)[1] if os.path.exists(DESC_ASM) else None
    with open(rom_path, "rb") as f:
        rom = f.read()
    if len(rom) % 0x8000 == 0x200:
        rom = rom[0x200:]
    out = {}
    for name, (text_org, ptr_org) in SECTIONS.items():
        if only and name not in only:
            continue
        base, ptr = int(text_org, 16) - 0xC00000, int(ptr_org, 16) - 0xC00000
        n = len(pointers.get(ptr_org, [])) if pointers is not None else COUNTS[name]
        out[name] = [tidy(_decode_rom(rom[base + int.from_bytes(rom[ptr + 2 * i:ptr + 2 * i + 2], "little"):][:200]))
                     for i in range(n)]
    return out


def esper_descriptions():
    labels, pointers = _labels_and_pointers(C4_ASM)
    order = next((p for p in pointers.values() if p and p[0] == "Ramuh"), [])
    return [tidy(labels.get(lbl, "")) for lbl in order]
