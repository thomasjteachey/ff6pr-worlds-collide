"""Sketch abilities: the mod's per-monster table onto the PR's ability groups.

SNES: rages at F4600 (2 bytes per rage = monster 0-254), sketches at F4300 (2 bytes per monster):
spell ids, EE = Fight, EF = the monster's special. PR: monster.rage_ability_random_group_id /
release_ability_random_group_id -> ability_random_group rows (ability_id, invocation_rate); the
rage groups add a Fight row of their own (25%). A monster whose table the mod changed gets its
group's rows rewritten in place (same rows, same odds): each vanilla entry is matched to the
row that holds it (Fight = ability 1, the special = the row no spell explains), then takes the
mod's entry. Monsters whose rows don't match vanilla's entries are left alone (logged).
Control (F3D00) isn't here: the PR keeps it elsewhere.
"""
from collections import defaultdict

import idmap as M
import port_specials
import prdata as P

# Rages are port_effects.port_rages' (its own odds per profile), not here.
TABLES = (("sketch", "release_ability_random_group_id", 0xF4300, 384, False),)
FIGHT, EE, EF, NONE = 1, 0xEE, 0xEF, 0xFF


def entries(rom, at, e):
    return list(rom.data[at + 2 * e:at + 2 * e + 2])


def port(van, mod, tables, log):
    mon = tables.setdefault("monster", P.Table("monster"))
    abilities = P.Table("ability")
    arg = tables.setdefault("ability_random_group", P.Table("ability_random_group"))
    rows = defaultdict(list)
    for r in arg:
        rows[int(r["group_id"])].append(int(r["id"]))
    changed, notes = set(), []
    for kind, col, at, count, strict in TABLES:
        done, skipped = 0, 0
        for e in range(count):
            vs, ms = entries(van, at, e), entries(mod, at, e)
            if vs == ms or e + 1 not in mon.rows:
                continue
            group = int(mon[e + 1][col])
            ids = sorted(rows.get(group, []))
            spells = {M.spell(x) for x in vs if x not in (EE, EF, NONE)}
            special = [i for i in ids if int(arg[i]["ability_id"]) not in spells | {FIGHT}]

            def pr_of(x):
                if x == EE:
                    return FIGHT
                if x == EF:
                    # the monster's special: the one its BNW special uses (port_specials: the
                    # PR's own, or a copy where BNW changed a shared one), else the group's row
                    own = port_specials.special_of(van, mod, e)
                    if own is not None:
                        return own
                    return int(arg[special[0]]["ability_id"]) if special else None
                if x == NONE:
                    return None
                return M.spell(x)

            used, slots = set(), []
            for v in vs:
                want = pr_of(v)
                row = next((i for i in ids if i not in used and want is not None and int(arg[i]["ability_id"]) == want), None)
                if row is None:
                    break
                used.add(row)
                slots.append(row)
            if len(slots) != len(vs) or (strict and len(slots) != len(ids)):
                skipped += 1
                continue
            for row, v, m in zip(slots, vs, ms):
                new = pr_of(m)
                if new is None or v == m or str(new) == arg[row]["ability_id"]:
                    continue
                r = dict(arg[row])
                r["ability_id"] = str(new)
                arg.rows[row] = r
                changed.add(row)
            done += 1
        notes.append(f"{kind} {done} monsters" + (f" ({skipped} left alone: rows unlike vanilla's)" if skipped else ""))
    log(f"rages/sketches: {', '.join(notes)}; {len(changed)} ability group rows changed")
    return {"ability_random_group": changed}
