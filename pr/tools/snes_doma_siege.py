"""Doma's siege in a WC seed's SNES game, played in the windowless harness (snes_harness.py), for
side-by-side checks with the port (the user, 2026-11-11: "play through the doma siege event on both
the WC snes and pr WC port and tell me of any behavior/number/timing discrepancies").

    python tools/snes_doma_siege.py <PR seed folder or its wc_seed.json> [--out DIR] [--stop-at-free]

Makes the seed's ROM with WC's own wc.py and the seed's exact flags in a temp folder (deleted at
the end: the ROM is never kept), New Game, then patches a one-off event into the temp ROM's free
space that puts the party on the World of Balance one tile below Doma (156,85) and walks up onto
Doma's tile: WC's own way into the siege. Once the party is free it walks down to the commander
and talks to him; in battle the party's first command is Fight and A is tapped, its HP kept full
after the monsters' numbers are recorded. Times are from the world map going black after the step.
Writes a per-frame log (map, tile, dialog, event, battle, espers, gil, items) and screenshots of
the main moments into --out (default: a new temp folder, printed).
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import prdata as P  # noqa: E402
from snes_harness import Snes  # noqa: E402

WC = os.path.normpath(os.path.join(P.ROOT, "..", "mods", "worlds-collide"))
FPS = 60.0988


def rom_tables(d):
    def events(m):
        a = 0x40000 + int.from_bytes(d[0x40000 + 2 * m:0x40000 + 2 * m + 2], "little")
        b = 0x40000 + int.from_bytes(d[0x40000 + 2 * m + 2:0x40000 + 2 * m + 4], "little")
        return [(d[i], d[i + 1], int.from_bytes(d[i + 2:i + 5], "little")) for i in range(a, b, 5)]

    def npcs(m):
        p0 = d[0x41A10 + 2 * m] | d[0x41A10 + 2 * m + 1] << 8
        p1 = d[0x41A10 + 2 * m + 2] | d[0x41A10 + 2 * m + 3] << 8
        out = []
        for a in range(0x41A10 + p0, 0x41A10 + p1, 9):
            r = d[a:a + 9]
            ev = r[0] | r[1] << 8 | (r[2] & 3) << 16
            bit = ((r[2] & 0xC0) >> 6) | ((r[3] & 1) << 2)
            out.append(dict(x=r[4] & 0x7F, y=r[5] & 0x3F, sprite=r[6], ev=0xCA0000 + ev,
                            npc_bit=((r[3] & 0xFE) >> 1) * 8 + bit, move=r[7] & 0x0F,
                            code=d[0xA0000 + ev:0xA0000 + ev + 12].hex(" ")))
        return out
    return events, npcs


def formation(d, f):
    r = d[0xF6200 + 15 * f:0xF6200 + 15 * f + 15]
    ids = []
    for i in range(6):
        if r[1] >> i & 1:
            ids.append(r[2 + i] | ((r[14] >> i) & 1) << 8)
    return ids


def monster_name(d, i):
    raw = d[0xFC050 + 10 * i:0xFC050 + 10 * i + 10]
    s = ""
    for c in raw:
        if 0x80 <= c <= 0x99:
            s += chr(ord("A") + c - 0x80)
        elif 0x9A <= c <= 0xB3:
            s += chr(ord("a") + c - 0x9A)
        elif 0xB4 <= c <= 0xBD:
            s += chr(ord("0") + c - 0xB4)
        elif c == 0xFF:
            s += " "
        else:
            s += "?"
    return s.strip()


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("seed", help="a PR seed folder or its wc_seed.json (its seed and exact flags are used)")
    ap.add_argument("--out", help="folder for the screenshots and the log (default: a new temp folder)")
    ap.add_argument("--stop-at-free", action="store_true", help="stop once the party is free on the siege map")
    a = ap.parse_args()
    meta = a.seed if a.seed.endswith(".json") else os.path.join(a.seed, "wc_seed.json")
    info = json.load(open(meta, encoding="utf-8"))
    SEED, FLAGS = info["seed"], info["flags"].split()
    SHOTS = a.out or tempfile.mkdtemp(prefix="snes_doma_siege_")
    os.makedirs(SHOTS, exist_ok=True)
    STOP_AT_FREE = a.stop_at_free
    print(f"seed {SEED} ({info.get('hash')}); screenshots and log: {SHOTS}")
    tmp = tempfile.mkdtemp(prefix="snes_doma_rom_")
    try:
        rom = os.path.join(tmp, "wc.sfc")
        p = subprocess.run([sys.executable, "wc.py", "-i", P.VANILLA_ROM, "-o", rom, "-s", SEED] + FLAGS,
                           cwd=WC, capture_output=True, text=True, encoding="utf-8", errors="replace")
        if p.returncode != 0 or not os.path.exists(rom):
            print("WC failed:", (p.stderr or p.stdout)[-800:])
            return 1
        spoiler = [l for l in open(os.path.join(tmp, "wc.txt"), encoding="utf-8", errors="replace")] \
            if os.path.exists(os.path.join(tmp, "wc.txt")) else []
        for i, l in enumerate(spoiler):
            if "Doma Siege" in l or "Doma WOB" in l:
                print("spoiler:", " | ".join(x.strip() for x in spoiler[i:i + 2]))
        d = open(rom, "rb").read()
        hdr = 0x200 if len(d) % 0x400 == 0x200 else 0
        d = d[hdr:]
        events, npcs = rom_tables(d)
        # the world map start: an event in the ROM's free space (its end) that loads the World of
        # Balance one tile below Doma's tile (156,85, facing up); the runner walks up onto it (156,84)
        free = next(a for a in range(len(d) - 0x100, 0x300000, -16) if d[a:a + 64] in (bytes([0xFF]) * 64, bytes(64))) + 16
        WORLD_EV = 0xC00000 + free if free < 0x400000 else free
        code = bytes([0x6B, 0x00, 0x00, 156, 85, 0x00, 0xFE])
        raw = bytearray(open(rom, "rb").read())
        raw[hdr + free:hdr + free + len(code)] = code
        open(rom, "wb").write(raw)
        d = bytes(raw[hdr:])
        print(f"world start event at ${WORLD_EV:06X}: {code.hex(' ')}")
        ev = [e for e in events(0x78) if e[:2] == (33, 42)]
        EV = 0xCA0000 + ev[0][2]
        print(f"siege event: map 0x78 (33,42) -> ${EV:06X}")
        print("map 0x78 tile events:", [(x, y, f"${0xCA0000 + a:06X}") for x, y, a in events(0x78)])
        print("map 0x78 NPCs:")
        for i, n in enumerate(npcs(0x78)):
            print(f"  {i:2} ({n['x']},{n['y']}) sprite {n['sprite']} npc bit {n['npc_bit']:#x} move {n['move']} event ${n['ev']:06X}: {n['code']}")

        s = Snes(rom)
        print("core:", s.core_name)
        s.track_black = True
        rd, rd16, wr = s.rd, s.rd16, s.wr
        log = []
        last = {}
        state = "boot"
        deck = started = None
        noev = field_nmi = None
        tile_since = 0
        last_tile = None
        battle_at = None
        blocked_at = None
        idle_since = None
        shots = []
        inv0 = None
        world_since = step_at = None
        seen_running = False
        gil0 = 0

        def mp():
            return rd16(0x1F64) & 0x1FF

        def t():
            return (s.frame - started) / FPS if started is not None else -1

        def note(k, v):
            if last.get(k) != v:
                last[k] = v
                log.append((s.frame, k, v))

        def shot(name):
            path = os.path.join(SHOTS, f"{len(shots):02}_{t():06.2f}s_{name}.png")
            s.screenshot(path)
            shots.append(path)
            log.append((s.frame, "shot", os.path.basename(path)))

        def inventory():
            return {rd(0x1869 + i): rd(0x1969 + i) for i in range(256) if rd(0x1869 + i) != 0xFF}

        t_start = time.time()
        pending_shots = []   # (frame, name)
        while True:
            f = s.frame
            if state == "boot":
                s.press("a") if f % 16 < 2 else s.release()
                if deck is None and mp() == 6:
                    deck = f
                if deck is not None and f - deck > 180:
                    s.release()
                    noev = (rd(0xE5), rd(0xE6), rd(0xE7))
                    field_nmi = bytes(s.ram[0x1500:0x1508])
                    print(f"deck at frame {deck}; idle event pc {noev}; field $1500: {field_nmi.hex(' ')}")
                    party = []
                    for c in range(14):
                        b = 0x1600 + 37 * c
                        if rd(0x1850 + c) & 7:
                            party.append(f"char {c} Lv {rd(b + 8)} HP {rd16(b + 9)}/{rd16(b + 0xB)} cmds {[rd(b + 0x16 + k) for k in range(4)]}")
                            wr(b + 0x16, 0)     # first command: Fight (A-tapping attacks)
                    print("party:", "; ".join(party))
                    inv0 = inventory()
                    gil0 = rd(0x1860) | rd(0x1861) << 8 | rd(0x1862) << 16
                    lo, hi, bk = WORLD_EV & 0xFF, WORLD_EV >> 8 & 0xFF, WORLD_EV >> 16
                    for a, v in ((0xE5, lo), (0xE6, hi), (0xE7, bk), (0x05F4, lo), (0x05F5, hi), (0x05F6, bk),
                                 (0x0594, noev[0]), (0x0595, noev[1]), (0x0596, noev[2]), (0x05C7, 1), (0xE8, 3), (0xE9, 0)):
                        wr(a, v)
                    y = rd16(0x0803)
                    wr(0x087D + y, rd(0x087C + y))
                    wr(0x087C + y, 4)
                    started = f
                    state = "world"
                    log.append((f, "start", f"world start ${WORLD_EV:06X} from map {mp()}"))
            else:
                nmi = bytes(s.ram[0x1500:0x1508])
                in_battle = nmi[3] == 0xC1          # the battle module's NMI (field C0, world map elsewhere)
                note("nmi", nmi.hex(" "))
                idle = (rd(0xE5), rd(0xE6), rd(0xE7)) == noev
                note("black", s.black)
                if not idle and mp() == 0x78:
                    seen_running = True
                if not in_battle:
                    note("map", mp())
                    tile = (rd(0xAF), rd(0xB0))
                    note("tile", f"{tile[0]},{tile[1]}")
                    note("dialog", rd16(0xD0) & 0x1FFF if rd(0xBA) == 1 else None)
                    note("event", "idle" if idle else "running")
                    note("formation", rd16(0x11E0))
                    note("espers", bytes(s.ram[0x1A69:0x1A6D]).hex())
                    if last.get("dialog") is not None and ("dshot", last["dialog"]) not in last:
                        last[("dshot", last["dialog"])] = 1
                        pending_shots.append((f + 30, f"dialog{last['dialog']}"))
                    if battle_at is not None:
                        log.append((f, "battle end", f"{(f - battle_at) / FPS:.2f} s"))
                        pending_shots.append((f + 1, "after_battle"))
                        battle_at = None
                else:
                    if battle_at is None:
                        battle_at = f
                        log.append((f, "battle", f"formation {rd16(0x11E0)}: {[monster_name(d, m) for m in formation(d, rd16(0x11E0))]}"))
                        for k in (30, 90, 180, 300, 480):
                            pending_shots.append((f + k, f"battle+{k}"))
                    if f - battle_at == 240:
                        ms = []
                        for slot in range(4, 10):
                            yy = slot * 2
                            if rd16(0x3C1C + yy):
                                ms.append(f"slot {slot}: Lv {rd(0x3B18 + yy)} HP {rd16(0x3BF4 + yy)}/{rd16(0x3C1C + yy)} MP {rd16(0x3C08 + yy)}")
                        log.append((f, "monsters", "; ".join(ms)))
                        cs = []
                        for slot in range(4):
                            yy = slot * 2
                            if rd16(0x3C1C + yy):
                                cs.append(f"slot {slot}: Lv {rd(0x3B18 + yy)} HP {rd16(0x3BF4 + yy)}/{rd16(0x3C1C + yy)}")
                        log.append((f, "party", "; ".join(cs)))
                    if f - battle_at > 240:          # the party kept alive
                        for slot in range(4):
                            yy = slot * 2
                            if rd16(0x3C1C + yy):
                                wr(0x3C1C + yy, 0x0F); wr(0x3C1D + yy, 0x27)
                                wr(0x3BF4 + yy, 0x0F); wr(0x3BF5 + yy, 0x27)
                if state == "world":
                    # on the world map (its NMI isn't the field's or battle's) and past the first load's
                    # animation (Figaro Castle rising, ~4 s without input): up onto Doma's tile, held
                    # until the screen goes black
                    if world_since is None and nmi[3] not in (0xC0, 0xC1) and not s.black:
                        world_since = f
                        log.append((f, "world", f"on the world map, $1500 {nmi.hex(' ')}"))
                        pending_shots.append((f + 1, "world"))
                    if world_since is not None and f - world_since >= 480:   # past the world map's first-load animation
                        if step_at is None:
                            step_at = f
                            log.append((f, "step", "up onto Doma's tile (156,84)"))
                        s.press("up")
                        if (f - step_at) % 8 == 0 and f - step_at <= 120:
                            pending_shots.append((f, f"step+{f - step_at}"))
                        if s.black:
                            s.release()
                            log.append((f, "fade", "the world map went black"))
                            state = "opening"
                            started = f      # times from here: the screen going black after the step
                elif state == "opening":
                    s.press("a") if f % 16 < 2 else s.release()
                    if f - started == 2:
                        pending_shots.append((f + 1, "start"))
                    if (f - started) % 60 == 0 and f - started <= 900:
                        pending_shots.append((f, "opening"))
                    if idle and mp() == 0x78 and not in_battle and nmi[3] == 0xC0 and f - started > 60 and last.get("event") == "idle" and seen_running:
                        state = "walk"
                        log.append((f, "free", f"party at {last.get('tile')}"))
                        pending_shots.append((f + 1, "free"))
                elif state == "walk":
                    s.press("down")
                    tile = (rd(0xAF), rd(0xB0))
                    if tile != last_tile:
                        last_tile, tile_since = tile, f
                    if not idle:
                        state = "scene"
                        log.append((f, "walk", f"an event at {tile}"))
                    elif f - tile_since > 30:
                        blocked_at = f
                        state = "talk"
                        s.release()
                        log.append((f, "blocked", f"{tile[0]},{tile[1]}: talking"))
                        pending_shots.append((f + 1, "blocked"))
                elif state == "talk":
                    s.press("a") if (f - blocked_at) % 16 in (4, 5) else s.release()
                    if not idle:
                        state = "scene"
                        log.append((f, "talked", "the commander's event runs"))
                        pending_shots.append((f + 8, "talked"))
                    elif f - blocked_at > 300:
                        log.append((f, "stuck", "no event after talking"))
                        break
                elif state == "scene":
                    s.press("a") if f % 16 < 2 else s.release()
                    if mp() == 0x11D and idle and not in_battle:
                        if idle_since is None:
                            idle_since = f
                            log.append((f, "done", f"idle on map 0x11D at {last.get('tile')}"))
                            pending_shots.append((f + 1, "end"))
                        if f - idle_since > 120:
                            break
                    else:
                        idle_since = None
                if STOP_AT_FREE and state == "walk":
                    break
                if f - started > 60 * 900:
                    log.append((f, "timeout", "15 minutes of game time"))
                    break
            while pending_shots and pending_shots[0][0] <= s.frame:
                shot(pending_shots.pop(0)[1])
                pending_shots.sort()
            s.run(1)
        inv1 = inventory()
        gained = {k: inv1.get(k, 0) - inv0.get(k, 0) for k in set(inv1) | set(inv0) if inv1.get(k, 0) != inv0.get(k, 0)}
        gil1 = rd(0x1860) | rd(0x1861) << 8 | rd(0x1862) << 16
        print(f"ran {s.frame} frames in {time.time() - t_start:.1f} s; items changed {gained}; espers {bytes(s.ram[0x1A69:0x1A6D]).hex()}; gil {gil0} -> {gil1}")
        if step_at is not None:
            print(f"the step onto Doma's tile began at {(step_at - started) / FPS:.2f} s (times are from the world map going black)")
        for c in range(14):
            b = 0x1600 + 37 * c
            if rd(0x1850 + c) & 7:
                print(f"  char {c} Lv {rd(b + 8)} HP {rd16(b + 9)}/{rd16(b + 0xB)} exp {rd(b + 0x11) | rd(b + 0x12) << 8 | rd(b + 0x13) << 16}")
        s.close()
        out = [(fr, k, v) for fr, k, v in log]
        json.dump(out, open(os.path.join(SHOTS, "log.json"), "w"), indent=0, default=str)
        for fr, k, v in out:
            print(f"  {(fr - started) / FPS:8.2f} s  {k:10} {v}")
        return 0
    finally:
        shutil.rmtree(tmp, ignore_errors=True)   # the ROM is never kept


if __name__ == "__main__":
    sys.exit(main())
