"""Port equipment special effects: relic/armor passives and command swaps
(wear_function_group), armor spell triggers, weapon special effects, species
bonuses, back-row flag and element-proc field.

Translations are learned from vanilla (SNES bits/bytes -> PR values) and only
applied where BNW's bytes differ from vanilla. BNW values vanilla never used are
reported (they need the plugin), not guessed.
"""
from collections import Counter

import ff6snes as S
import groups as G
import idmap as M
import learn
import port_specials
import prdata as P

EFFECT_BYTES = (5, 9, 10, 11, 12, 13)  # field effects + battle effect flags


def effect_mask(raw):
    m = 0
    for n, b in enumerate(EFFECT_BYTES):
        m |= raw[b] << (8 * n)
    return m


def item_record(raw):
    rec = {f"b{i}": raw[i] for i in range(3, 28)}
    rec["b19_bit5"] = (raw[19] >> 5) & 1
    return rec


# Weapon special effects: byte 27's high nibble picks one (the low nibble is only the
# parry graphic). How vanilla PR expresses each, checked against every vanilla weapon:
# program (process_prog), extra wear functions, on-hit condition group, species bonus.
# 2 Atma, 7 MP crit, A Valiant Knife, C Heal Rod and E Ogre Nix are keyed on weapon ids
# inside the game (the plugin rewrites those lists); B is Tempest's change_ability entry.
VANILLA_SPECIALS = {
    1: {"prog": "THIEFS_KNIFE", "funcs": {(23, 0, 0, 0)}},
    3: {"prog": "ASSASIN_WEAPON", "funcs": {(23, 0, 0, 0)}, "hit_condition": 100},
    4: {"species": 2, "funcs": {(23, 0, 0, 0)}},
    5: {"funcs": {(13, 1, 100, 1)}},
    6: {"funcs": {(77, 2, 0, 0), (13, 2, 100, 1)}},
    8: {"prog": "HAWK_EYE", "funcs": {(23, 0, 0, 0)}},
    9: {"prog": "DICE"},
    11: {"prog": "ADD_MAGIC"},
    13: {"prog": "SWORD_THAT_CUTS_IRON", "funcs": {(23, 0, 0, 0)}},
}
# weapon byte 26: high nibble = MBlock +10/20/30/50 (an equip ability), low nibble = evade/10
WEAPON_MBLOCK = {1: (62, 256, 0, 0), 2: (62, 257, 0, 0), 3: (62, 258, 0, 0), 5: (62, 259, 0, 0)}
# relic bits the PR expresses as wear conditions (armor and weapons): Sniper Sight, Black
# Belt counter, True Knight cover, SOS Shell, SOS Safe (checked against vanilla armor)
CONDITION_BITS = {(11, 4): 49, (12, 1): 48, (12, 6): 29, (13, 0): 46, (13, 1): 47}
TRUSTED_COLS = ("wear_function_group_id", "wear_condition_group_id", "process_prog",
                "additional_condition_group_id", "effective_species", "evasion_rate")


class EffectPorter:
    def __init__(self, van, bnw, tables, log, profile=None):
        self.van, self.bnw, self.tables, self.log = van, bnw, tables, log
        self.specials = dict(VANILLA_SPECIALS)
        self.specials.update(getattr(profile, "weapon_specials", None) or {})
        self.bit_overrides = getattr(profile, "relic_bit_funcs", None) or {}
        self.fg = tables.setdefault("function_group", P.Table("function_group"))
        self.cg = tables.setdefault("condition_group", P.Table("condition_group"))
        self.fg_index = {}
        for r in self.fg:
            self.fg_index.setdefault(self.funcs(r), int(r["id"]))
        self.changed = {"armor": set(), "weapon": set(), "function_group": set(), "condition_group": set()}
        self.unknown = Counter()
        self.vitems, self.bitems = S.items(van), S.items(bnw)
        va, vw = P.Table("armor"), P.Table("weapon")
        self.armor_pairs = [(it, va[M.item(it.id)[1]]) for it in self.vitems if M.item(it.id) and M.item(it.id)[0] == "armor"]
        self.weapon_pairs = [(it, vw[M.item(it.id)[1]]) for it in self.vitems if M.item(it.id) and M.item(it.id)[0] == "weapon"]
        self.vweapon = {M.item(it.id)[1]: r for it, r in self.weapon_pairs}
        self.vweapon_raw = {M.item(it.id)[1]: it.raw for it, r in self.weapon_pairs}
        self.wear_bits = G.learn_bits([(effect_mask(it.raw), self.funcs(self.fg.rows.get(int(r["wear_function_group_id"]))))
                                       for it, r in self.armor_pairs], 8 * len(EFFECT_BYTES), min_share=0.75, max_noise=0.1)
        self.armor_maps = learn.learn([(item_record(it.raw), r) for it, r in self.armor_pairs],
                                      ["b18"], ["trigger_ability_id"], min_purity=0.95)
        self.weapon_maps = learn.learn([(item_record(it.raw), r) for it, r in self.weapon_pairs],
                                       ["b19_bit5", "b15"], ["back_flag", "weak_attribute"], min_purity=0.95)
        self.cg_index = {}
        groups = {}
        for r in self.cg:
            groups.setdefault(int(r["group_id"]), []).append(r)
        for gid, rows in groups.items():
            if all(r["type_id"] == "1" and r["invocation_rate"] == "0" for r in rows):
                self.cg_index.setdefault(frozenset(int(r["condition_id"]) for r in rows), gid)
        self.check_conditions()

    @staticmethod
    def funcs(row):
        if not row:
            return frozenset()
        return frozenset((int(row[f"function{k}_id"]), int(row[f"function{k}_value1"]), int(row[f"function{k}_value2"]),
                          int(row[f"function{k}_value3"])) for k in (1, 2, 3) if row[f"function{k}_id"] != "0")

    def conditions_of(self, gid):
        return frozenset(int(r["condition_id"]) for r in self.cg if int(r["group_id"]) == int(gid)) if int(gid) else frozenset()

    def check_conditions(self):
        """CONDITION_BITS must rebuild vanilla armor's wear conditions."""
        bad = []
        for it, r in self.armor_pairs:
            want = self.bit_conditions(it.raw)
            have = self.conditions_of(r["wear_condition_group_id"])
            if want != have and want:
                bad.append(f"{it.name.strip()} {sorted(want)} vs {sorted(have)}")
        if bad:
            self.log(f"  wear conditions: vanilla disagrees for {bad}")

    @staticmethod
    def bit_conditions(raw):
        return frozenset(c for (byte, bit), c in CONDITION_BITS.items() if raw[byte] >> bit & 1)

    def relic_funcs(self, raw, count=False):
        """Wear functions of the relic bits (bytes 5, 9-13) an item sets."""
        mask, funcs = effect_mask(raw), set()
        for b in range(8 * len(EFFECT_BYTES)):
            if not mask >> b & 1:
                continue
            key = (EFFECT_BYTES[b // 8], b % 8)
            if key in self.bit_overrides:
                funcs |= set(self.bit_overrides[key])
            elif b in self.wear_bits:
                funcs |= set(self.wear_bits[b])
            elif key not in CONDITION_BITS and count:
                self.unknown[f"relic effect byte {key[0]} bit {key[1]}"] += 1
        return funcs

    def function_group(self, funcs):
        funcs = frozenset(funcs)
        if not funcs:
            return 0
        if funcs in self.fg_index:
            return self.fg_index[funcs]
        if len(funcs) > 3:
            raise ValueError("more than 3 functions")
        gid = max(self.fg.rows) + 1
        row = {"id": str(gid)}
        for k, f in enumerate(sorted(funcs), 1):
            row[f"function{k}_id"], row[f"function{k}_value1"], row[f"function{k}_value2"], row[f"function{k}_value3"] = map(str, f)
        for k in range(len(funcs) + 1, 4):
            for c in ("id", "value1", "value2", "value3"):
                row[f"function{k}_{c}"] = "0"
        self.fg.rows[gid] = row
        self.fg_index[funcs] = gid
        self.changed["function_group"].add(gid)
        return gid

    def condition_group(self, conds):
        conds = frozenset(conds)
        if not conds:
            return 0
        if conds in self.cg_index:
            return self.cg_index[conds]
        gid = max(int(r["group_id"]) for r in self.cg) + 1
        for c in sorted(conds):
            rid = max(self.cg.rows) + 1
            self.cg.rows[rid] = {"id": str(rid), "group_id": str(gid), "condition_id": str(c), "type_id": "1",
                                 "invocation_rate": "0"}
            self.changed["condition_group"].add(rid)
        self.cg_index[conds] = gid
        return gid

    def setv(self, table, rid, col, val):
        row = self.tables[table][rid]
        if row[col] != str(val):
            row[col] = str(val)
            self.changed[table].add(rid)

    def armor(self):
        for iv, ib in zip(self.vitems, self.bitems):
            m = M.item(ib.id)
            if not m or m[0] != "armor" or iv.type != ib.type:
                continue
            rid = m[1]
            if effect_mask(iv.raw) != effect_mask(ib.raw) or self.bit_overrides:
                vf, bf = self.relic_funcs(iv.raw), self.relic_funcs(ib.raw, count=True)
                if vf != bf or effect_mask(iv.raw) != effect_mask(ib.raw):
                    try:
                        self.setv("armor", rid, "wear_function_group_id", self.function_group(bf))
                    except ValueError:
                        self.unknown["relic with >3 effects"] += 1
            vc, bc = self.bit_conditions(iv.raw), self.bit_conditions(ib.raw)
            if vc != bc:
                self.setv("armor", rid, "wear_condition_group_id", self.condition_group(bc))
            if iv.raw[18] != ib.raw[18]:
                fm = self.armor_maps.get("trigger_ability_id")
                val = fm.convert(ib.raw[18]) if fm else None
                if val is None:
                    self.unknown[f"armor proc {ib.raw[18]:#x}"] += 1
                else:
                    self.setv("armor", rid, "trigger_ability_id", val)

    def weapon_target(self, raw, specials, count=False):
        """PR weapon columns for SNES weapon bytes, under the given special-effect meanings."""
        n = raw[27] >> 4
        sp = specials.get(n, {})
        funcs = set(sp.get("funcs", ())) | self.relic_funcs(raw, count)
        mblock = raw[26] >> 4
        if mblock:
            if mblock in WEAPON_MBLOCK:
                funcs.add(WEAPON_MBLOCK[mblock])
            else:
                self.unknown[f"weapon MBlock +{mblock * 10}"] += 1
        prog = sp.get("prog")
        if raw[18] & 0x40:
            if prog and prog != "ADD_MAGIC":
                self.unknown[f"weapon with both a spell proc and {prog}"] += 1
            prog = prog or "ADD_MAGIC"
        return {
            "wear_function_group_id": frozenset(funcs),
            "wear_condition_group_id": self.bit_conditions(raw),
            "process_prog": prog or "None",
            "additional_condition_group_id": str(sp.get("hit_condition", 0)),
            "effective_species": str(sp.get("species", 0)),
            "evasion_rate": str((raw[26] & 15) * 10),
        }

    def actual(self, row):
        return {
            "wear_function_group_id": self.funcs(self.fg.rows.get(int(row["wear_function_group_id"]))),
            "wear_condition_group_id": self.conditions_of(row["wear_condition_group_id"]),
            "process_prog": row["process_prog"],
            "additional_condition_group_id": row["additional_condition_group_id"],
            "effective_species": row["effective_species"],
            "evasion_rate": row["evasion_rate"],
        }

    def weapons(self):
        untrusted = Counter()
        for iv, ib in zip(self.vitems, self.bitems):
            m = M.item(ib.id)
            if not m or m[0] != "weapon" or iv.type != ib.type:
                continue
            wid = m[1]
            vt = self.weapon_target(iv.raw, VANILLA_SPECIALS)
            have = self.actual(self.vweapon[wid])
            bt = self.weapon_target(ib.raw, self.specials, count=True)
            # conditions no SNES bit explains (the PR's own, e.g. Murasame's condition 64)
            # stay with the same weapon; a different BNW weapon in that slot doesn't get them
            extra = have["wear_condition_group_id"] - vt["wear_condition_group_id"]
            if iv.name.strip() != ib.name.strip():
                extra = frozenset()
            if vt["wear_condition_group_id"] <= have["wear_condition_group_id"]:
                vt["wear_condition_group_id"] = have["wear_condition_group_id"]
                bt["wear_condition_group_id"] = bt["wear_condition_group_id"] | extra
            for col in TRUSTED_COLS:
                if bt[col] == vt[col]:
                    continue
                if vt[col] != have[col]:
                    untrusted[col] += 1  # these rules don't rebuild vanilla's value: leave it
                    self.unknown[f"weapon {col}: vanilla PR not rebuilt ({iv.name.strip()})"] += 1
                    continue
                try:
                    if col == "wear_function_group_id":
                        val = self.function_group(bt[col])
                    elif col == "wear_condition_group_id":
                        val = self.condition_group(bt[col])
                    else:
                        val = bt[col]
                    self.setv("weapon", wid, col, val)
                except ValueError:
                    self.unknown["weapon with >3 effects"] += 1
            vr, br = item_record(iv.raw), item_record(ib.raw)
            for col, field in {"back_flag": "b19_bit5", "weak_attribute": "b15"}.items():
                if vr[field] == br[field]:
                    continue
                fm = self.weapon_maps.get(col)
                val = fm.convert(br[field]) if fm and fm.snes_field == field else None
                if val is None:
                    self.unknown[f"weapon {col} from {field}={br[field]}"] += 1
                else:
                    self.setv("weapon", wid, col, val)
        # how well the rules rebuild vanilla, for the report
        total = Counter()
        for it, r in self.weapon_pairs:
            vt, have = self.weapon_target(it.raw, VANILLA_SPECIALS), self.actual(r)
            for col in TRUSTED_COLS:
                total[col] += vt[col] == have[col]
        self.log(f"  weapon rules rebuild vanilla: " + ", ".join(f"{c} {total[c]}/{len(self.weapon_pairs)}" for c in TRUSTED_COLS))

    def port(self):
        self.armor()
        self.weapons()
        self.log(f"equipment effects: armor {len(self.changed['armor'])}, weapons {len(self.changed['weapon'])}, "
                 f"new function groups {len(self.changed['function_group'])}, new condition rows {len(self.changed['condition_group'])}")
        self.log(f"  not portable as data (need plugin): {dict(self.unknown.most_common())}")
        return self.changed


RAGES = 0x0F4600  # 256 monsters x 2 attacks (BNW: first 2/3 of the time, second 1/3)


def port_rages(van, bnw, tables, log, odds=(67, 33)):
    """Gau's Rage attacks -> each monster's rage ability_random_group rows."""
    mo = tables.setdefault("monster", P.Table("monster"))
    arg = tables.setdefault("ability_random_group", P.Table("ability_random_group"))
    rows = {}
    for r in arg:
        rows.setdefault(int(r["group_id"]), []).append(int(r["id"]))
    ab = P.Table("ability")

    def ability(e, x):
        if x == 0xEE:
            return 1
        if x == 0xEF:
            return port_specials.special_of(van, bnw, e, 1)   # the monster's own special
        return M.spell(x)

    changed, skipped = set(), Counter()
    for e in range(256):
        v, b = van.data[RAGES + 2 * e:RAGES + 2 * e + 2], bnw.data[RAGES + 2 * e:RAGES + 2 * e + 2]
        if v == b:
            continue
        if 0xFF in (b[0], b[1]):
            skipped["not a Rage in BNW"] += 1
            continue
        g = int(mo[e + 1]["rage_ability_random_group_id"])
        ids = sorted(rows.get(g, []))
        a1, a2 = ability(e, b[0]), ability(e, b[1])
        if not ids or a1 not in ab.rows or a2 not in ab.rows:
            skipped["no group or ability"] += 1
            continue
        plan = [(a1, odds[0]), (a2, odds[1])] + [(a2, 0)] * (len(ids) - 2)
        for rid, (aid, rate) in zip(ids, plan):
            row = arg[rid]
            new = dict(row, ability_id=str(aid), invocation_rate=str(rate))
            if new != row:
                arg.rows[rid] = new
                changed.add(rid)
    log(f"rages: {len(changed)} rows changed; skipped {dict(skipped)}")
    return {"ability_random_group": changed}
