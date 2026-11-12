"""The Veldt for a mod that changes it (BNW: 74 Veldt formations, 72 with new monsters).

The SNES's Veldt offers every formation the party has met that its flags allow (formation flags
$F5900 + 4f, byte 3 bit 1: not on the Veldt), cycling through blocks of 8 formations. The PR keeps
that as a table (the misc asset's beast_field_encount_table): group N = SNES block N - 1, one entry
per formation, each with its own unlock flag (flag_index), the party it fights (monster_party_id)
and the parties whose battle opens it (encount_target_party_id, encount_target_other_ids). Its
table is vanilla's formations with vanilla's monsters.

For the mod: one entry per formation its flags allow on the Veldt, fighting the mod's PR party for
it (port_encounters' party for a changed formation, made here where no encounter set needed one)
and opened by every PR party of that formation (port_encounters' per-look copies too: the PR
matches the battle's party id). Written as the mod's copy of the misc asset (group "misc").
Worlds Collide keeps vanilla's formations, and its plugin counts copies as their formation's
party there (WcVeldt): not for a WC seed.
"""
import json
import os

import idmap as M
import port_encounters as E
import prdata as P

GROUP, KEY = "misc", "serial_misc_assets"
ASSET = "Assets/GameAssets/Serial/Data/Misc/serial_misc_assets"
FLAGS = 0xF5900    # SNES formation flags, 4 bytes each; byte 3 bit 1: not on the Veldt


def monsters(rom, f):
    return sorted(e for e, _, _ in E.formation(rom, f))


def veldt_formations(rom):
    """The formations a ROM's Veldt can offer (in ID order)."""
    return [f for f in range(512) if not (rom.data[FLAGS + 4 * f + 3] & 0x02) and monsters(rom, f)]


def port(van, mod, tables, encounters, out_dir, log):
    """Write the mod's Veldt table; returns {"monster_party": parties made here}."""
    if "WC_FLAGS" in os.environ:
        return {}
    vf, mf = veldt_formations(van), veldt_formations(mod)
    if vf == mf and all(monsters(van, f) == monsters(mod, f) for f in mf):
        log("veldt: the mod's Veldt is vanilla's")
        return {}
    mp = tables.setdefault("monster_party", P.Table("monster_party"))
    src = os.path.join(P.VANILLA, GROUP, *ASSET.split("/")) + ".json"
    doc = json.load(open(src, encoding="utf-8"))
    # vanilla's own extra unlockers (the PR's copies of a party, e.g. 596 of 5), by formation
    pf = M.party_formation()
    extra = {}
    for g in doc["beast_field_encount_table"]:
        for e in g["party_table"]:
            f = pf.get(e["encount_target_party_id"])
            if f is not None:
                extra.setdefault(f, set()).update([e["encount_target_party_id"]] + list(e.get("encount_target_other_ids") or []))
    made, groups, n, kept = set(), [], 0, 0
    for block in range(64):
        fs = [f for f in mf if f // 8 == block]
        if not fs:
            continue
        table = []
        for f in fs:
            ids = E.party_ids(f, mp.rows)
            own = mp.rows.get(M.party(f))
            if monsters(mod, f) == monsters(van, f) and own and sorted(m - 1 for m in E.party_members(own)) == monsters(mod, f):
                party, openers = M.party(f), set(ids) | extra.get(f, set())
                kept += 1
            else:
                # (vanilla's party for an unchanged formation can be missing: formation + 1 is idmap's
                # guess, empty for formation 502)
                if not ids:
                    template = mp.rows.get(M.party(f)) or mp.rows[1]
                    ids = [encounters.party_for(f, template)]
                    made.add(ids[0])
                party, openers = ids[0], set(ids)
            openers.add(party)
            entry = {"id": n + 1, "flag_index": n, "monster_party_id": party, "encount_target_party_id": party}
            others = sorted(openers - {party})
            if others:
                entry["encount_target_other_ids"] = others
            table.append(entry)
            n += 1
        groups.append({"group_id": block + 1, "party_table": table})
    doc["beast_field_encount_table"] = groups
    dest = os.path.join(out_dir, GROUP, *ASSET.split("/")) + ".json"
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    with open(dest, "w", encoding="utf-8") as fh:
        fh.write(json.dumps(doc, ensure_ascii=False, separators=(",", ":")))
    keys = os.path.join(out_dir, GROUP, "keys")
    os.makedirs(keys, exist_ok=True)
    with open(os.path.join(keys, "mod_misc.json"), "w", encoding="utf-8") as fh:
        json.dump({"keys": [KEY], "values": [ASSET]}, fh, indent=2)
    log(f"veldt: {n} formations in {len(groups)} groups (vanilla {len(vf)}); {kept} as vanilla, "
        f"{n - kept} the mod's, {len(made)} parties made for them")
    return {"monster_party": made}
