"""Parse FF6 SNES monster AI scripts (pointers at 0x0F8400, data at 0x0F8700)."""
AI_PTRS, AI_DATA = 0x0F8400, 0x0F8700

SIZES = {0xF0: 4, 0xF1: 2, 0xF2: 4, 0xF3: 3, 0xF4: 4, 0xF5: 4, 0xF6: 4, 0xF7: 2,
         0xF8: 3, 0xF9: 4, 0xFA: 4, 0xFB: 3, 0xFC: 4, 0xFD: 1, 0xFE: 1, 0xFF: 1}
NAMES = {0xF0: "random", 0xF1: "target", 0xF2: "formation", 0xF3: "message", 0xF4: "command",
         0xF5: "enemies", 0xF6: "item", 0xF7: "event", 0xF8: "var", 0xF9: "bits",
         0xFA: "animate", 0xFB: "misc", 0xFC: "if", 0xFD: "wait", 0xFE: "endif", 0xFF: "end"}


def parse(data, start):
    """Return (main, counter): lists of (name, args) with args a tuple of ints."""
    sections, i = [], start
    for _ in range(2):
        ops = []
        while i < len(data):
            op = data[i]
            size = SIZES.get(op, 1)
            args = tuple(data[i + 1:i + size])
            ops.append(("attack", (op,)) if op < 0xF0 else (NAMES[op], args))
            i += size
            if op == 0xFF:
                break
        sections.append(ops)
    return sections[0], sections[1]


def script(rom, enemy_id):
    d = rom.data
    off = int.from_bytes(d[AI_PTRS + 2 * enemy_id:AI_PTRS + 2 * enemy_id + 2], "little")
    return parse(d, AI_DATA + off)


def fmt(ops):
    return " ; ".join(name if not args else f"{name} {' '.join(f'{a:02x}' for a in args)}" for name, args in ops)
