"""Weapon spell procs: SNES item byte 18 (bit 6 = cast randomly, bits 0-5 = spell).

The PR keeps them in battle_extra_data.json `add_ability_weapon`
({weapon_id, ability_id, ability_per}): the weapon casts a proc copy of the spell
ability_per % of the time. A proc copy is the spell's ability row with type 629, no
reflect, no wait and no content flags (vanilla 263-276). BNW keeps vanilla's 1-in-4
(c2.asm RandomCast; a "High Proc Rate" relic doubles it, which needs the plugin).

Vanilla's procs give each proc copy's spell. BNW's procs reuse those copies, carrying
over whatever BNW changed in the spell itself, and get new copies for spells vanilla
never procced. Weapons whose proc BNW removed lose their entry.
"""
import copy
import json
import os

import idmap as M
import prdata as P

PROC_TYPE = "629"
PROC_RATE = 25  # percent, BNW's 1-in-4
PROC_OWN = {"type_id": PROC_TYPE, "content_flag_group_id": "0", "invalid_reflection": "1", "ability_wait": "0"}
GROUP = "battleextradata"
LAST = {}  # the latest port's {"copies": {PR spell: proc copy}, "rates": {PR weapon: [chance, with High Proc Rate]}}
# special effects (byte 27 high nibble) that cast a spell: ("add", SNES spell, %) = a proc
# beside the swing, ("change", ...) = the spell replaces the swing. Vanilla: Tempest.
VANILLA_SPECIAL_PROCS = {11: ("change", 0x98, 25)}
ASSET = "Assets/GameAssets/Serial/Data/BattleExtraData/battle_extra_data"


def proc_spell(raw):
    """SNES spell id a weapon casts at random, or None."""
    b = raw[18]
    return b & 0x3F if b & 0x40 else None


def vanilla_extra():
    with open(os.path.join(P.VANILLA, GROUP, *ASSET.split("/")) + ".json", encoding="utf-8") as f:
        return json.load(f)


class ProcPorter:
    def __init__(self, van, bnw, tables, sp, log, profile=None):
        self.van, self.bnw, self.tables, self.sp, self.log = van, bnw, tables, sp, log
        self.special_procs = dict(VANILLA_SPECIAL_PROCS)
        self.special_procs.update(getattr(profile, "special_procs", None) or {})
        self.odds = getattr(profile, "proc_odds", None)
        self.ab = tables["ability"]
        self.vab = P.Table("ability")
        self.extra = vanilla_extra()
        self.changed = {"ability": set(), "content": set(), "weapon": set()}

    def weapons(self, rom):
        """PR weapon id -> SNES spell it procs, for one ROM."""
        import ff6snes as S
        out = {}
        for it in S.items(rom):
            m = M.item(it.id)
            if m and m[0] == "weapon" and proc_spell(it.raw) is not None:
                out[m[1]] = proc_spell(it.raw)
        return out

    def vanilla_copies(self, vprocs):
        """PR spell id -> its vanilla proc copy, read off vanilla's add_ability_weapon."""
        copies, odd = {}, []
        listed = {e["weapon_id"]: e["ability_id"] for e in self.extra["add_ability_weapon"]}
        for wid, spell in vprocs.items():
            pid = M.spell(spell)
            if wid in listed:
                copies.setdefault(pid, listed[wid])
            else:
                odd.append(wid)
        missing = sorted(set(listed) - set(vprocs))
        if odd or missing:
            self.log(f"  procs: vanilla data and ROM disagree (ROM only {odd}, data only {missing})")
        return copies

    def sync(self, proc_id, spell_id):
        """Carry BNW's changes to the spell over to its existing proc copy."""
        proc, vspell, bspell = self.ab[proc_id], self.vab[spell_id], self.ab[spell_id]
        new = dict(proc)
        for col in proc:
            if col not in ("id", "sort_id") and col not in PROC_OWN and bspell[col] != vspell[col]:
                new[col] = bspell[col]
        if new != proc:
            self.ab.rows[proc_id] = new
            self.changed["ability"].add(proc_id)

    def new_copy(self, spell_id):
        k = max(self.ab.rows) + 1
        self.ab.rows[k] = dict(self.ab[spell_id], id=str(k), sort_id=str(k), **PROC_OWN)
        self.changed["ability"].add(k)
        content = self.sp.content
        cid = max(content.rows) + 1
        content.rows[cid] = {"id": str(cid), "mes_id_name": "None", "mes_id_battle": "None",
                             "mes_id_description": "None", "icon_id": "0", "type_id": "4", "type_value": str(k)}
        self.changed["content"].add(cid)
        return k

    def usage(self):
        """Rods: byte 18 bit 7 = break it from the Item menu to cast its spell (PR
        destroy_rate 100); byte 0 bit 4 = throwable (PR throw_flag). Checked on vanilla,
        applied where BNW's bits differ."""
        import ff6snes as S
        wt, odd, changed = self.tables["weapon"], [], []
        for iv, ib in zip(S.items(self.van), S.items(self.bnw)):
            m = M.item(iv.id)
            if not m or m[0] != "weapon" or m[1] not in wt.rows:
                continue
            row = wt[m[1]]
            want = lambda raw: {"destroy_rate": "100" if raw[18] & 0x80 else "0", "throw_flag": str(raw[0] >> 4 & 1)}
            vwant, bwant = want(iv.raw), want(ib.raw)
            if any(row[c] != v for c, v in vwant.items()):
                odd.append(iv.name.strip())
                continue
            for col, val in bwant.items():
                if val != vwant[col] and row[col] != val:
                    row[col] = val
                    self.changed["weapon"].add(m[1])
                    changed.append(f"{ib.name.strip()} {col}={val}")
        if odd:
            self.log(f"  rods: vanilla PR disagrees with the ROM for {odd} (left alone)")
        self.log(f"rods and throwing: {', '.join(changed) or 'no changes'}")

    def rates(self, raw):
        """(chance, chance with a High Proc Rate relic) for a weapon's spell proc."""
        odds = self.odds
        if not odds:
            return PROC_RATE, PROC_RATE
        brush = raw[odds["brush"][0]] >> odds["brush"][1] & 1
        high = raw[odds["high"][0]] >> odds["high"][1] & 1
        lo = odds["brush_rate"] if brush else PROC_RATE
        hi = odds["brush_high_rate"] if brush else odds["high_rate"]
        return (hi if high else lo), hi

    def copy_for(self, spell_id, copies, new):
        if spell_id not in copies:
            copies[spell_id] = self.new_copy(spell_id)
            new.append(spell_id)
        return copies[spell_id]

    def own_rules(self):
        """True if the mod clears "cast randomly" on most vanilla proc weapons yet keeps
        their spells (Beyond Chaos): it decides procs some other way, so keep vanilla's."""
        import ff6snes as S
        procs = cleared = 0
        for iv, ib in zip(S.items(self.van), S.items(self.bnw)):
            m = M.item(iv.id)
            if m and m[0] == "weapon" and iv.raw[18] & 0x40:
                procs += 1
                cleared += not ib.raw[18] & 0x40 and (ib.raw[18] & 0x3F) == (iv.raw[18] & 0x3F)
        return procs and cleared * 2 >= procs, cleared, procs

    # SNES metamorph item sets: 26 sets of 4 item ids (C4/7F40); each monster's metamorph byte
    # picks one. The PR copies each monster's set into battle_extra_data's monster_exchange_item
    # (all 273 entries equal a vanilla set's items, mapped with idmap.item_content)
    MORPH_SETS, MORPH_SET_COUNT = 0x047F40, 26

    def metamorph(self, extra):
        """The mod's changed metamorph sets onto every PR monster list that held the vanilla set
        (WC's -pd: no Fenix Down from Metamorph, -nee: no Rename Card...); returns the count."""
        import idmap as M
        lists = extra.get("monster_exchange_item") or []
        done, sets = 0, []
        for g in range(self.MORPH_SET_COUNT):
            at = self.MORPH_SETS + 4 * g
            a, b = list(self.van.data[at:at + 4]), list(self.bnw.data[at:at + 4])
            if a == b:
                continue
            pa, pb = [M.item_content(i) for i in a], [M.item_content(i) for i in b]
            if None in pb or None in pa:
                self.log(f"  metamorph set {g}: an item without a PR content id ({b}); kept")
                continue
            n = 0
            for e in lists:
                if list(e["exchange_content_ids"]) == pa:
                    e["exchange_content_ids"] = pb
                    n += 1
            done += n
            sets.append(f"{g}: {pa} -> {pb} ({n} monsters)")
        if sets:
            self.log(f"metamorph: {'; '.join(sets)}")
        return done

    def write_extra(self, out_dir, extra):
        group = os.path.join(out_dir, GROUP)
        path = os.path.join(group, *ASSET.split("/")) + ".json"
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(json.dumps(extra, ensure_ascii=False, indent=1))
        os.makedirs(os.path.join(group, "keys"), exist_ok=True)
        with open(os.path.join(group, "keys", "mod_battle_extra.json"), "w", encoding="utf-8") as f:
            json.dump({"keys": ["battle_extra_data"], "values": [ASSET]}, f, indent=2)

    def port(self, out_dir):
        import ff6snes as S
        own, cleared, procs = self.own_rules()
        if own:
            self.log(f"weapon procs: the mod clears 'cast randomly' on {cleared}/{procs} vanilla proc weapons but keeps "
                     f"their spells, so its proc rules differ: vanilla procs and rods kept")
            extra = copy.deepcopy(self.extra)
            if self.metamorph(extra):
                self.write_extra(out_dir, extra)
            return self.changed
        self.usage()
        vprocs, bprocs = self.weapons(self.van), self.weapons(self.bnw)
        copies = self.vanilla_copies(vprocs)
        # vanilla's change-ability copies (Tempest -> Wind Slash) count as copies too
        for e in self.extra["change_ability_by_weapon"]:
            raw = next((it.raw for it in S.items(self.van) if M.item(it.id) == ("weapon", e["weapon_id"])), None)
            special = self.special_procs.get(raw[27] >> 4) if raw else None
            if special and special[0] == "change":
                copies.setdefault(M.spell(special[1]), e["change_ability_id"])
        for spell_id, proc_id in copies.items():
            self.sync(proc_id, spell_id)
        entries, changes, new, rates, skipped = [], [], [], {}, []
        for it in S.items(self.bnw):
            m = M.item(it.id)
            if not m or m[0] != "weapon":
                continue
            wid, raw = m[1], it.raw
            special = self.special_procs.get(raw[27] >> 4)
            if wid in bprocs:
                spell_id = M.spell(bprocs[wid])
                if spell_id is None or spell_id not in self.ab.rows:
                    skipped.append(f"{it.name.strip()} (SNES spell {bprocs[wid]})")
                else:
                    lo, hi = self.rates(raw)
                    entries.append({"weapon_id": wid, "ability_id": self.copy_for(spell_id, copies, new), "ability_per": lo})
                    rates[wid] = [lo, hi]
                if special and special[0] == "add":
                    skipped.append(f"{it.name.strip()} (special proc under its spell proc)")
                special = special if special and special[0] == "change" else None
            if special:
                kind, snes_spell, rate = special
                spell_id = M.spell(snes_spell)
                if spell_id is None or spell_id not in self.ab.rows:
                    skipped.append(f"{it.name.strip()} (special SNES spell {snes_spell})")
                    continue
                pid = self.copy_for(spell_id, copies, new)
                if kind == "add":
                    entries.append({"weapon_id": wid, "ability_id": pid, "ability_per": rate})
                    rates[wid] = [rate, rate]
                else:
                    changes.append({"weapon_id": wid, "change_ability_id": pid, "ability_per": rate})
        entries.sort(key=lambda e: e["weapon_id"])
        changes.sort(key=lambda e: e["weapon_id"])
        if skipped:
            self.log(f"  procs skipped: {skipped}")
        LAST.clear()
        LAST.update(copies=dict(copies), rates=rates)
        extra = copy.deepcopy(self.extra)
        old = {e["weapon_id"]: e["ability_id"] for e in extra["add_ability_weapon"]}
        old_changes = extra["change_ability_by_weapon"]
        extra["add_ability_weapon"] = entries
        extra["change_ability_by_weapon"] = changes
        now = {e["weapon_id"]: e["ability_id"] for e in entries}
        gained = sorted(set(now) - set(old))
        lost = sorted(set(old) - set(now))
        swapped = sorted(w for w in set(now) & set(old) if now[w] != old[w])
        def same(a, b):  # entry order doesn't matter to the game
            key = lambda e: json.dumps(e, sort_keys=True)
            return sorted(map(key, a)) == sorted(map(key, b))
        morphed = self.metamorph(extra)
        if morphed or not (same(entries, self.extra["add_ability_weapon"]) and same(changes, old_changes)):
            self.write_extra(out_dir, extra)
        odd = sorted({e["ability_per"] for e in entries})
        self.log(f"weapon procs: {len(entries)} weapons ({len(gained)} new, {len(swapped)} other spell, "
                 f"{len(lost)} removed; chances {odd}%), {len(changes)} attack swaps (vanilla {len(old_changes)}); "
                 f"{len(new)} new proc copies; {len(self.changed['ability']) - len(new)} copies synced to BNW's spell")
        return self.changed
