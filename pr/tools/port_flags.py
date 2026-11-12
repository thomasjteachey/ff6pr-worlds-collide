"""Spell flags -> the PR's content flags: "ignore defense" (SNES spell data byte 2 bit 5) ->
flag 5, "Runic can absorb it" (byte 3 bit 3) -> flag 3, "can't miss" (byte 4 bit 5) -> flag 1,
"no split damage" (byte 2 bit 6) -> flag 2 (profile.runic_flags / hit_flags / split_flags,
same modes as defense_flags).

The PR keeps per-ability battle flags in content_flag_group (group_id -> content_flag_id
rows), referenced by ability.content_flag_group_id. The formula probe (2026-11-05) showed
flag 5 is "ignore defense": every ability that ignored Defense/MDef in the game had it,
none of the others did (flag 1 = can't miss, flag 2 = no multi-target split, as on the
SNES). The PR's own flags differ from the SNES in places (PR Meteor/Merton ignore MDef,
Quake didn't), so:

  profile.defense_flags = "mod"      every spell takes the mod's bit (BNW: Holy, Maduin,
                                     Avalanche, Blackjack ignore defense; Meteor... don't)
  profile.defense_flags = "changed"  only spells whose bit the mod changed (default)
"""
import ff6snes as S
import idmap as M
import prdata as P

IGNORE_DEFENSE = "5"
# SNES spell bits -> PR content flags. Flag 3 = Runic can absorb it (decompiled
# MagicSealSwordConditionFunction.ForciblyTarget: the act's ability has content flag 3; it
# matches the SNES "allow Runic" bit, byte 3 bit 3, on 247 of vanilla's 249 spells: the PR
# leaves X-Zone and Imp out). (flag, attribute, mask, profile setting)
# Flag 1 = can't miss (byte 4 bit 5, "unblockable": 243 of 249 agree), flag 2 = no
# multi-target split (byte 2 bit 6: 246 of 249), checked on vanilla 2026-11-05.
FLAG_BITS = [(IGNORE_DEFENSE, "flags1", 0x20, "defense_flags"), ("3", "flags2", 0x08, "runic_flags"),
             ("1", "flags3", 0x20, "hit_flags"), ("2", "flags1", 0x40, "split_flags")]


class FlagGroups:
    def __init__(self, tables):
        self.t = tables.setdefault("content_flag_group", P.Table("content_flag_group"))
        self.groups = {}
        for r in self.t:
            self.groups.setdefault(r["group_id"], set()).add(r["content_flag_id"])
        self.index = {frozenset(v): k for k, v in sorted(self.groups.items(), key=lambda kv: int(kv[0]))}
        self.changed = set()

    def flags(self, group_id):
        return set(self.groups.get(group_id, set())) if group_id not in ("", "0") else set()

    def get(self, flags):
        """group id holding exactly these flags (a new group if none does)."""
        flags = frozenset(flags)
        if not flags:
            return "0"
        if flags in self.index:
            return self.index[flags]
        gid = str(max(int(g) for g in self.groups) + 1)
        next_id = max(self.t.rows) + 1
        for f in sorted(flags, key=int):
            self.t.rows[next_id] = {"id": str(next_id), "group_id": gid, "content_flag_id": f}
            self.changed.add(next_id)
            next_id += 1
        self.groups[gid] = set(flags)
        self.index[flags] = gid
        return gid


def port(van, mod, tables, log, profile=None):
    ab = tables.setdefault("ability", P.Table("ability"))
    groups = FlagGroups(tables)
    vs, ms = S.spells(van), S.spells(mod)
    changed = set()
    for flag, attr, mask, setting in FLAG_BITS:
        mode = getattr(profile, setting, "changed")
        seen, added, removed = set(), [], []
        for v, m in zip(vs, ms):
            aid = M.spell(m.id)
            if aid is None or aid not in ab.rows or aid in seen:
                continue
            seen.add(aid)
            want = bool(getattr(m, attr) & mask)
            if mode != "mod" and want == bool(getattr(v, attr) & mask):
                continue
            row = ab[aid]
            flags = groups.flags(row["content_flag_group_id"])
            if (flag in flags) == want:
                continue
            flags = flags | {flag} if want else flags - {flag}
            row = dict(row)
            row["content_flag_group_id"] = groups.get(flags)
            ab.rows[aid] = row
            changed.add(aid)
            (added if want else removed).append(m.name.strip() or str(aid))
        log(f"{setting} (flag {flag}, {mode}): {len(added)} set ({', '.join(added[:12])}{'...' if len(added) > 12 else ''}), "
            f"{len(removed)} cleared ({', '.join(removed[:12])}{'...' if len(removed) > 12 else ''})")
    log(f"content flags: {len(groups.changed)} new flag rows")
    return {"ability": changed, "content_flag_group": groups.changed}
