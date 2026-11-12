"""Readers for FF6 (SNES, US v1.0 layout) data tables.

Offsets are unheadered file offsets. Layouts follow Worlds Collide's data
modules (mods/worlds-collide/data) and are checked against the Pixel
Remaster's vanilla tables by tools/check_vanilla.py.
"""
from dataclasses import dataclass, field

# --- name text (menu font) -------------------------------------------------
_NAME = {0xbe: "!", 0xbf: "?", 0xc0: "/", 0xc1: ":", 0xc2: '"', 0xc3: "'", 0xc4: "-",
         0xc5: ".", 0xc6: ",", 0xc7: "…", 0xc8: ";", 0xc9: "#", 0xca: "+", 0xcb: "(",
         0xcc: ")", 0xcd: "%", 0xce: "~", 0xd2: "=", 0xfe: " "}
_NAME.update({c + 0x3f: chr(c) for c in range(ord("A"), ord("Z") + 1)})
_NAME.update({c + 0x39: chr(c) for c in range(ord("a"), ord("z") + 1)})
_NAME.update({c + 0x84: chr(c) for c in range(ord("0"), ord("9") + 1)})
ICONS = range(0xd8, 0xfe)  # weapon/armor/magic icons (BNW adds more above 0xEA)


def decode_name(raw: bytes) -> str:
    """Fixed-length menu names: 0xFF pads (and fills the empty icon slot)."""
    out = []
    for b in raw:
        if b in ICONS or (b < 0x80 and b != 0):  # icons; BNW also uses low tile ids as icons
            continue
        out.append(" " if b == 0xff else _NAME.get(b, f"<{b:02x}>"))
    return "".join(out).strip()


ELEMENTS = ["fire", "ice", "bolt", "poison", "wind", "pearl", "earth", "water"]
STATUS1 = ["dark", "zombie", "poison", "magitek", "clear", "imp", "petrify", "death"]
STATUS2 = ["condemned", "near_fatal", "image", "mute", "berserk", "muddle", "seizure", "sleep"]
STATUS3 = ["dance", "regen", "slow", "haste", "stop", "shell", "safe", "reflect"]
STATUS4 = ["rage", "frozen", "reraise", "morph", "casting", "removed", "interceptor", "float"]


def bits(value, names):
    return {n for i, n in enumerate(names) if value & (1 << i)}


def nibble_stat(n):
    """Item stat boosts: 0-7 = +n, 8-15 = -(n-8)."""
    return n if n < 8 else -(n - 8)


EVADE_STEPS = [0, 10, 20, 30, 40, 50, -10, -20, -30, -40, -50]


# ROM bytes held in memory, by absolute path (the ROM-free WC generator: its input is
# tools/wc_basedata.py's shipped data, its output never a ROM file)
MEMORY = {}
OUT_MAGIC = b"FF6WCO1\0"   # a ROM-free WC run's output (tools/wc_romfree.py): zlib'd bytes


def rom_bytes(path):
    """A ROM's bytes, unheadered: registered in MEMORY, a ROM-free WC output file, or the file."""
    import os
    key = os.path.abspath(path)
    if key in MEMORY:
        return MEMORY[key]
    with open(path, "rb") as f:
        data = f.read()
    if data.startswith(OUT_MAGIC):
        import zlib
        data = zlib.decompress(data[len(OUT_MAGIC):])
    if len(data) % 0x8000 == 0x200:
        data = data[0x200:]
    return data


class Rom:
    def __init__(self, path):
        self.data = rom_bytes(path)

    def b(self, start, size):
        return self.data[start:start + size]

    def table(self, start, size, count):
        return [self.data[start + i * size:start + (i + 1) * size] for i in range(count)]


# --- characters -------------------------------------------------------------
CHAR_INIT = 0x2D7CA0, 22, 64
CHAR_NAMES = 0x0478C0, 6, 64


@dataclass
class Character:
    id: int
    name: str
    hp: int
    mp: int
    commands: list
    vigor: int
    speed: int
    stamina: int
    magic: int
    attack: int
    defense: int
    mdef: int
    evade: int
    mblock: int
    equip: list  # rhand, lhand, head, body, relic1, relic2 (item ids, 0xff = none)
    level_factor: int


def characters(rom):
    names = rom.table(*CHAR_NAMES)
    out = []
    for i, d in enumerate(rom.table(*CHAR_INIT)):
        out.append(Character(i, decode_name(names[i]), d[0], d[1], list(d[2:6]), d[6], d[7], d[8], d[9],
                             d[10], d[11], d[12], d[13], d[14], [d[15], d[16], d[17], d[18], d[19], d[20]],
                             (d[21] >> 2) & 3))
    return out


# --- items ------------------------------------------------------------------
ITEMS = 0x185000, 30, 256
ITEM_NAMES = 0x12B300, 13, 256
ITEM_TYPES = {0: "tool", 1: "weapon", 2: "armor", 3: "shield", 4: "helmet", 5: "relic", 6: "item"}


@dataclass
class Item:
    id: int
    name: str
    raw: bytes
    type: str = field(init=False)
    equip_chars: int = field(init=False)
    price: int = field(init=False)
    power: int = field(init=False)      # battle power (weapons) / defense (armor)
    hit_or_mdef: int = field(init=False)  # hit rate (weapons) / magic defense (armor)
    vigor: int = field(init=False)
    speed: int = field(init=False)
    stamina: int = field(init=False)
    magic: int = field(init=False)
    evade: int = field(init=False)
    mblock: int = field(init=False)

    def __post_init__(self):
        d = self.raw
        self.type = ITEM_TYPES.get(d[0] & 7, f"?{d[0] & 7}")
        self.equip_chars = d[1] | ((d[2] & 0x3f) << 8)
        self.price = d[28] | (d[29] << 8)
        self.power = d[20]
        self.hit_or_mdef = d[21]
        self.vigor, self.speed = nibble_stat(d[16] & 0x0f), nibble_stat(d[16] >> 4)
        self.stamina, self.magic = nibble_stat(d[17] & 0x0f), nibble_stat(d[17] >> 4)
        ev, mb = d[26] & 0x0f, d[26] >> 4
        self.evade = EVADE_STEPS[ev] if ev < len(EVADE_STEPS) else 0
        self.mblock = EVADE_STEPS[mb] if mb < len(EVADE_STEPS) else 0


def items(rom):
    names = rom.table(*ITEM_NAMES)
    return [Item(i, decode_name(names[i]), d) for i, d in enumerate(rom.table(*ITEMS))]


# --- spells -----------------------------------------------------------------
SPELLS = 0x046AC0, 14, 256
SPELL_NAMES = [(0x26F567, 7, 54), (0x26F6E1, 8, 27), (0x26F7B9, 10, 175)]  # magic, espers, skills
# Brave New World moved and lengthened them (see c1/c3 asm "spell name length 12").
SPELL_NAMES_BNW = [(0x2FFC00, 8, 54), (0x2FFDB0, 9, 27), (0x26F567, 11, 175)]


@dataclass
class Spell:
    id: int
    name: str
    targets: int
    elements: int
    flags1: int
    flags2: int
    flags3: int
    mp: int
    power: int
    flags4: int
    accuracy: int
    effect: int
    status: tuple


def is_bnw(rom):
    return decode_name(rom.b(0x2FFC00, 8)) == "Fire"


def spell_names(rom):
    out = []
    for start, size, count in (SPELL_NAMES_BNW if is_bnw(rom) else SPELL_NAMES):
        out += [decode_name(n) for n in rom.table(start, size, count)]
    return out


def spells(rom):
    names = spell_names(rom)
    out = []
    for i, d in enumerate(rom.table(*SPELLS)):
        out.append(Spell(i, names[i] if i < len(names) else "", d[0], d[1], d[2], d[3], d[4], d[5], d[6],
                         d[7], d[8], d[9], (d[10], d[11], d[12], d[13])))
    return out


# --- espers -----------------------------------------------------------------
ESPERS = 0x186E00, 11, 27
ESPER_NAMES = 0x26F6E1, 8, 27


@dataclass
class Esper:
    id: int
    name: str
    spells: list  # [(rate, spell_id)] for up to 5
    bonus: int


def espers(rom):
    names = rom.table(*(SPELL_NAMES_BNW[1] if is_bnw(rom) else ESPER_NAMES))
    out = []
    for i, d in enumerate(rom.table(*ESPERS)):
        learn = [(d[j], d[j + 1]) for j in range(0, 10, 2) if d[j + 1] != 0xff]
        out.append(Esper(i, decode_name(names[i]), learn, d[10]))
    return out


# --- enemies ----------------------------------------------------------------
ENEMIES = 0x0F0000, 32, 384
ENEMY_NAMES = 0x0FC050, 10, 384
ENEMY_ITEMS = 0x0F3000, 4, 384  # rare steal, common steal, rare drop, common drop


@dataclass
class Enemy:
    id: int
    name: str
    raw: bytes
    items: bytes
    speed: int = field(init=False)
    vigor: int = field(init=False)
    accuracy: int = field(init=False)
    evade: int = field(init=False)
    mblock: int = field(init=False)
    defense: int = field(init=False)
    mdef: int = field(init=False)
    magic: int = field(init=False)
    hp: int = field(init=False)
    mp: int = field(init=False)
    exp: int = field(init=False)
    gold: int = field(init=False)
    level: int = field(init=False)

    def __post_init__(self):
        d = self.raw
        (self.speed, self.vigor, self.accuracy, self.evade, self.mblock, self.defense,
         self.mdef, self.magic) = d[0:8]
        self.hp = d[8] | d[9] << 8
        self.mp = d[10] | d[11] << 8
        self.exp = d[12] | d[13] << 8
        self.gold = d[14] | d[15] << 8
        self.level = d[16]


def blank_enemy(rom, e):
    """A monster slot the mod emptied (BNW: no name, 0 HP, level 0; it moved or dropped the
    monster that was there). The PR's own event battles and kept encounter sets still fight the
    vanilla monster in that slot (by id), so the port keeps the PR's monster there."""
    d = rom.data[ENEMIES[0] + ENEMIES[1] * e: ENEMIES[0] + ENEMIES[1] * (e + 1)]
    hp, level = d[8] | d[9] << 8, d[16]
    return hp == 0 and level == 0 and not decode_name(rom.table(*ENEMY_NAMES)[e]).strip()


def enemies(rom):
    names = rom.table(*ENEMY_NAMES)
    itm = rom.table(*ENEMY_ITEMS)
    return [Enemy(i, decode_name(names[i]), d, itm[i]) for i, d in enumerate(rom.table(*ENEMIES))]
