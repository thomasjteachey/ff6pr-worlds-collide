"""The SNES's Regen/Sap/Poison ticks measured in the windowless harness (snes_harness.py): WC keeps
vanilla FF6's status clock, so the vanilla ROM is the yardstick for WcStatusTicks (the user,
2026-11-12: "is hot/dot numbers ticking at correct times ... for WC?" and "ensure it works the same
with, say, jump/stop/etc").

    python tools/snes_status_ticks.py [cadence|jump-regen|jump-poison|stop] ... [--json]

New Game on the vanilla ROM (read only, nothing written), A mashed and up held until the first
battle (Narshe); then in battle RAM: monsters held with Stop and full HP, statuses set on party
slots, and every HP change and status step logged by frame.
  cadence      Poison on slot 0, Regen on slot 1 (HP 1), Sap (Seizure) on slot 2
  jump-regen   Regen on slot 0, whose first command becomes Jump (A mashed; the others Defend)
  jump-poison  the same with Poison
  stop         Regen on slot 0, Stopped (timer 18) at frame 900
  timers       Sleep on slot 0 (timer 18), Reflect on slot 1 (26), Freeze on slot 2 (34), as their
               on-set handlers set them (C2/4634, 4687, 4691): frames until each goes
  condemned    Condemned on slot 0 with $3B05 = 30 (C2/09B4's count): frames to the death
  jump-air     slot 0 jumps (the others' gauges held at 0, so no other menus or actions): frames
               from the jump up to the landing, with its Speed and $3AC8
Results 2026-11-12: steps every 128-131 frames (128 + the engine's lag frames); Regen and Sap every
515-516 frames, Poison 1031 (and its damage grows each tick); a jumping character's Regen and Poison
land in the air on time; nothing while stopped (18 steps, 2292 frames) and the step index waits;
Jump (Speed 33, $3AC8 318): up ~25 frames after the input, down 729-743 frames later (the rule: 722).
"""
import argparse
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import prdata as P  # noqa: E402
from snes_harness import Snes  # noqa: E402

FRAMES = 6000


def to_battle(s):
    rd = s.rd
    while rd(0x1503) != 0xC1:              # the NMI's jump: bank C1 = battle
        k = s.frame % 40
        if k < 2:
            s.press("a")
        elif 10 <= k < 30 and (s.frame // 2000) % 2:
            s.press("up")
        else:
            s.release()
        s.run(1)
        if s.frame > 200000:
            raise RuntimeError("no battle by frame 200000")
    s.release()
    while not rd(0x3AA0) & 1:
        s.run(1)
    s.run(240)


def run(scenario, frames=FRAMES):
    s = Snes(P.VANILLA_ROM)
    rd, rd16, wr = s.rd, s.rd16, s.wr

    def wr16(a, v):
        wr(a, v & 0xFF)
        wr(a + 1, v >> 8 & 0xFF)
    try:
        to_battle(s)
        monsters = [x for x in range(8, 20, 2) if rd(0x3AA0 + x) & 1]

        def hold_monsters():
            for x in monsters:
                wr(0x3EF8 + x, rd(0x3EF8 + x) | 0x10)      # Stop
                wr(0x3AF1 + x, 0x12)
                wr16(0x3BF4 + x, 20000)
                wr16(0x3C1C + x, 20000)
        slots = {"cadence": {0: ("poison", 900), 2: ("regen", 1), 4: ("sap", 900)}}.get(
            scenario, {0: ("poison" if scenario == "jump-poison" else "regen", 400)})
        for x, (what, hp) in slots.items():
            wr16(0x3C1C + x, 999)
            wr16(0x3BF4 + x, hp)
            if what == "poison":
                wr(0x3EE4 + x, rd(0x3EE4 + x) | 0x04)
            elif what == "regen":
                wr(0x3EF8 + x, rd(0x3EF8 + x) | 0x02)
            else:
                wr(0x3EE5 + x, rd(0x3EE5 + x) | 0x40)
        if scenario.startswith("jump"):
            wr(0x202E, 0x16)                               # slot 0's first command: Jump
            wr(0x202E + 12, 0x15)                          # the others: Defend
            wr(0x202E + 24, 0x15)
        t0 = s.frame
        hp = {x: rd16(0x3BF4 + x) for x in slots}
        idx = {x: rd(0x3AF0 + x) for x in slots}
        out = {"scenario": scenario, "ticks": [], "steps": [], "air": [], "stop": []}
        air = stopped = False
        for _ in range(frames):
            hold_monsters()
            if scenario.startswith("jump"):
                s.press("a") if s.frame % 12 < 2 else s.release()
            if scenario == "stop" and s.frame - t0 == 900:
                wr(0x3EF8, rd(0x3EF8) | 0x10)
                wr(0x3AF1, 0x12)
            s.run(1)
            f = s.frame - t0
            for x, (what, _) in slots.items():
                if rd(0x3AF0 + x) != idx[x]:
                    idx[x] = rd(0x3AF0 + x)
                    out["steps"].append([f, x // 2, idx[x] & 7])
                h = rd16(0x3BF4 + x)
                if h != hp[x]:
                    out["ticks"].append([f, x // 2, what, h - hp[x]])
                    hp[x] = h
            a = bool(rd16(0x3F2C) & rd16(0x3018))
            if a != air:
                air = a
                out["air"].append([f, a])
            st = bool(rd(0x3EF8) & 0x10)
            if st != stopped:
                stopped = st
                out["stop"].append([f, st])
        return out
    finally:
        s.close()


def run_timers(scenario, frames=12000):
    """timers / condemned / jump-air: frames until each status goes (or the landing)."""
    s = Snes(P.VANILLA_ROM)
    rd, rd16, wr = s.rd, s.rd16, s.wr

    def wr16(a, v):
        wr(a, v & 0xFF)
        wr(a + 1, v >> 8 & 0xFF)
    try:
        to_battle(s)
        monsters = [x for x in range(8, 20, 2) if rd(0x3AA0 + x) & 1]
        out = {"scenario": scenario, "events": [], "speed": rd(0x3B19), "atb": rd16(0x3AC8)}
        # the monsters held first: an attack already on its way would wake a sleeper (not for the Jump:
        # its menu must open after the command list has Jump in it)
        for _ in range(300 if scenario != "jump-air" else 0):
            for x in monsters:
                wr(0x3EF8 + x, rd(0x3EF8 + x) | 0x10)
                wr(0x3AF1 + x, 0x12)
            for x in (0, 2, 4, 6):
                wr16(0x3218 + x, 0)
            s.run(1)
        # (status byte, bit, timer address, timer value) per slot
        watch = {}
        if scenario == "timers":
            watch = {0: (0x3EE5, 0x80, 0x3CF9, 18, "Sleep"), 2: (0x3EF8, 0x80, 0x3F0C, 26, "Reflect"),
                     4: (0x3EF9, 0x02, 0x3F0D, 34, "Freeze")}
        elif scenario == "condemned":
            watch = {0: (0x3EE5, 0x01, 0x3B05, 30, "Condemned")}
        for x, (sb, bit, timer, v, _) in watch.items():
            wr(sb + x, rd(sb + x) | bit)
            wr(timer + x, v)
            wr16(0x3C1C + x, 999)
            wr16(0x3BF4 + x, 999)
        if scenario == "jump-air":
            wr(0x202E, 0x16)
        t0 = s.frame
        on = {x: True for x in watch}
        air = False
        for _ in range(frames):
            for x in monsters:
                wr(0x3EF8 + x, rd(0x3EF8 + x) | 0x10)
                wr(0x3AF1 + x, 0x12)
                wr16(0x3BF4 + x, 20000)
            if scenario == "jump-air":
                for x in (2, 4):
                    wr16(0x3218 + x, 0)
                s.press("a") if not air and s.frame % 8 < 2 else s.release()
            else:
                for x in (0, 2, 4, 6):               # nobody acts
                    wr16(0x3218 + x, 0)
            s.run(1)
            f = s.frame - t0
            for x, (sb, bit, timer, v, name) in watch.items():
                now = bool(rd(sb + x) & bit) if name != "Condemned" else not rd(0x3EE4 + x) & 0x80
                if on[x] and not now:
                    on[x] = False
                    out["events"].append([f, x // 2, name + " off" if name != "Condemned" else "dead"])
            if scenario == "jump-air":
                a = bool(rd16(0x3F2C) & rd16(0x3018))
                if a != air:
                    air = a
                    out["events"].append([f, 0, "up" if a else "down"])
        return out
    finally:
        s.close()


def run_wait(command, frames=1400):
    """command-wait: slot 0's first command set to `command` (SNES id), the others held: per turn, the
    frame its wait is set ($322C from the C2/067B table) and the frame it's ready to act ($3AA0 bit
    20, C2/11B2), with $3AC8. Measured 2026-11-12 (Speed 33, $3AC8 318): Fight and Steal $10 ready
    51-57 frames on (the rule: 52), Magic $20 136 frames on."""
    s = Snes(P.VANILLA_ROM)
    rd, rd16, wr = s.rd, s.rd16, s.wr

    def wr16(a, v):
        wr(a, v & 0xFF)
        wr(a + 1, v >> 8 & 0xFF)
    try:
        to_battle(s)
        monsters = [x for x in range(8, 20, 2) if rd(0x3AA0 + x) & 1]
        wr(0x202E, command)
        out = {"scenario": f"wait-{command:02x}", "speed": rd(0x3B19), "atb": rd16(0x3AC8), "turns": []}
        t0 = s.frame
        set_at = None
        last = rd(0x322C)
        for _ in range(frames):
            for x in monsters:
                wr(0x3EF8 + x, rd(0x3EF8 + x) | 0x10)
                wr(0x3AF1 + x, 0x12)
                wr16(0x3BF4 + x, 20000)
            for x in (2, 4):
                wr16(0x3218 + x, 0)
            s.press("a") if s.frame % 8 < 2 else s.release()
            s.run(1)
            f = s.frame - t0
            v = rd(0x322C)
            if v != last:
                if v not in (0xFF, 0xFE):
                    set_at = (f, v)
                elif set_at is not None:
                    out["turns"].append([set_at[1], set_at[0], f])     # wait, set, ready
                    set_at = None
                last = v
        return out
    finally:
        s.close()


def run_turn(frames=1500):
    """turn: slot 0's Fight turns in frames and in battle-time moves ($3A3E, C2/1136), the others held:
    per turn the frame/move it's entered, ready ($3AA0 bit 20), done (bit 20 and 40 clear: the gauge
    refills). Measured 2026-11-12 (Speed 33): entered to ready 39 moves (the A presses' 13 + the wait's
    26), the attack 37 moves over 86 frames (time runs through it: 1.43 s real), the refill 206 moves."""
    s = Snes(P.VANILLA_ROM)
    rd, rd16, wr = s.rd, s.rd16, s.wr

    def wr16(a, v):
        wr(a, v & 0xFF)
        wr(a + 1, v >> 8 & 0xFF)
    try:
        to_battle(s)
        monsters = [x for x in range(8, 20, 2) if rd(0x3AA0 + x) & 1]
        wr(0x202E, 0x00)
        out = {"scenario": "turn", "speed": rd(0x3B19), "atb": rd16(0x3AC8), "turns": []}
        t0, k0 = s.frame, rd16(0x3A3E)
        cur = None
        last = rd(0x3AA0)
        for _ in range(frames):
            for x in monsters:
                wr(0x3EF8 + x, rd(0x3EF8 + x) | 0x10)
                wr(0x3AF1 + x, 0x12)
                wr16(0x3BF4 + x, 20000)
            for x in (2, 4):
                wr16(0x3218 + x, 0)
            s.press("a") if s.frame % 4 < 2 else s.release()
            s.run(1)
            f, k = s.frame - t0, (rd16(0x3A3E) - k0) & 0xFFFF
            a = rd(0x3AA0)
            if a != last:
                if a & 0x08 and not last & 0x08:
                    cur = {"entered": (f, k)}
                elif cur is not None and a & 0x40 and not last & 0x40:
                    cur["ready"] = (f, k)
                elif cur is not None and "ready" in cur and not a & 0x60:
                    cur["done"] = (f, k)
                    out["turns"].append(cur)
                    cur = None
                last = a
        return out
    finally:
        s.close()


def summary(r):
    if r.get("scenario") == "turn":
        return "== turn: " + "; ".join(
            f"wait {t['ready'][1] - t['entered'][1]} moves, attack {t['done'][1] - t['ready'][1]} moves / {t['done'][0] - t['ready'][0]} frames"
            for t in r["turns"])
    if "turns" in r:
        return f"== {r['scenario']} (slot 0 Speed {r['speed']}, $3AC8 {r['atb']}): " + \
            ", ".join(f"wait ${w:02X}: {b - a} frames" for w, a, b in r["turns"])
    if "events" in r:
        return f"== {r['scenario']} (slot 0 Speed {r['speed']}, $3AC8 {r['atb']}): {r['events']}"
    lines = [f"== {r['scenario']}"]
    for slot in sorted({t[1] for t in r["ticks"]}):
        ts = [t for t in r["ticks"] if t[1] == slot]
        fr = [t[0] for t in ts]
        gaps = [b - a for a, b in zip(fr, fr[1:])]
        lines.append(f"slot {slot} {ts[0][2]}: frames {fr[:14]} gaps {gaps[:14]} amounts {[t[3] for t in ts][:14]}")
    if r["air"]:
        lines.append(f"in the air: {r['air']}")
    if r["stop"]:
        lines.append(f"stopped: {r['stop']}")
    steps = [st[0] for st in r["steps"] if st[1] == 0]
    gaps = sorted({b - a for a, b in zip(steps, steps[1:])})
    lines.append(f"slot 0 status steps: {len(steps)}, gaps {gaps}")
    return "\n".join(lines)


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("scenarios", nargs="*", default=["cadence", "jump-regen", "jump-poison", "stop", "timers", "condemned", "jump-air"])
    ap.add_argument("--json", action="store_true")
    a = ap.parse_args()
    results = [run_timers(sc) if sc in ("timers", "condemned", "jump-air")
               else run_wait(int(sc[5:], 16)) if sc.startswith("wait-")
               else run_turn() if sc == "turn"
               else run(sc, 9000 if sc != "cadence" else FRAMES)
               for sc in a.scenarios]
    if a.json:
        print(json.dumps(results))
    else:
        for r in results:
            print(summary(r))


if __name__ == "__main__":
    main()
